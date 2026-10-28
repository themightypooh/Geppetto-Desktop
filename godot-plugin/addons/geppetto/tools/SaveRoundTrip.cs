#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// G12 round-trip test for <see cref="EffigySaveBridge"/>. Headless, writes only into a work folder.
///
///   env -u DISPLAY -u WAYLAND_DISPLAY godot --headless --path godot-plugin \
///     -s res://addons/geppetto/tools/SaveRoundTrip.cs -- --work /tmp/dir [--out report.json] a.effigy b.riganim ...
///
/// Every input is COPIED into the work folder first; the originals are only read.
/// .effigy: open through EffigyLiveDocumentService, forced save with no edit, reopen and compare;
///   then toggle the first feature's Suppressed through the session history, save, reopen, compare;
///   then undo, save, and check the file is back to the first save.
/// .riganim: open next to a model (--model, default res://spike/g1-spike.effigy), forced save, reopen
///   and compare; then move one key through ClipEditService, save, reopen, compare, and check every
///   non-track JSON field survived.
/// "Document equal" means StudioDocument.Write / RigAnimWriter.Write of both sides give the same text.
/// Exits 1 if any check fails.
/// </summary>
[Tool]
public partial class SaveRoundTrip : SceneTree
{
	public override void _Initialize()
	{
		int failed = 0;
		var results = new JsonArray();
		string outPath = "";
		try
		{
			var args = OS.GetCmdlineUserArgs();
			string work = Path.Combine( Path.GetTempPath(), "geppetto-save-roundtrip" );
			string model = ProjectSettings.GlobalizePath( "res://spike/g1-spike.effigy" );
			var files = new List<string>();
			for ( int i = 0; i < args.Length; i++ )
			{
				if ( args[i] == "--work" && i + 1 < args.Length ) work = args[++i];
				else if ( args[i] == "--out" && i + 1 < args.Length ) outPath = args[++i];
				else if ( args[i] == "--model" && i + 1 < args.Length ) model = args[++i];
				else files.Add( args[i] );
			}
			if ( files.Count == 0 ) files.Add( model );
			Directory.CreateDirectory( work );

			foreach ( var f in files )
			{
				JsonObject r;
				try
				{
					string src = Path.GetFullPath( f.StartsWith( "res://" ) ? ProjectSettings.GlobalizePath( f ) : f );
					r = src.EndsWith( ".riganim", StringComparison.OrdinalIgnoreCase )
						? Clip( src, work, model )
						: Effigy( src, work );
				}
				catch ( Exception e )
				{
					r = new JsonObject { ["file"] = f, ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message };
				}
				if ( r["ok"]?.GetValue<bool>() != true ) failed++;
				results.Add( r );
			}
		}
		catch ( Exception e )
		{
			GD.PushError( $"[G12] {e}" );
			failed++;
		}

		var report = new JsonObject { ["files"] = results, ["failed"] = failed };
		string text = report.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
		GD.Print( text );
		if ( outPath != "" ) File.WriteAllText( outPath, text );
		Quit( failed > 0 ? 1 : 0 );
	}

	static string CopyIn( string src, string work )
	{
		string dst = Path.Combine( work, Path.GetFileName( src ) );
		if ( Path.GetFullPath( dst ) == Path.GetFullPath( src ) ) throw new InvalidOperationException( "work folder holds the original" );
		File.Copy( src, dst, overwrite: true );
		return dst;
	}

	static bool SameBytes( string a, string b ) => File.ReadAllBytes( a ).AsSpan().SequenceEqual( File.ReadAllBytes( b ) );

	static JsonObject Effigy( string src, string work )
	{
		var r = new JsonObject { ["file"] = src, ["kind"] = "effigy" };
		bool ok = true;
		void Check( string name, bool pass ) { r[name] = pass; ok &= pass; }

		string copy = CopyIn( src, work );
		using var live = EffigyLiveDocumentService.Open( copy );
		using var bridge = new EffigySaveBridge( live );
		var session = bridge.Model;
		string canon0 = StudioDocument.Write( session.Studio! );

		// Does the writer reproduce the original file byte for byte?
		r["writer_byte_identical"] = canon0 == File.ReadAllText( src ) && SameBytes( src, copy );
		r["header_in"] = File.ReadLines( src ).First();
		r["header_out"] = canon0[..canon0.IndexOf( '\n' )];

		// 1. Unmutated: an editor save (not dirty) must not touch the file at all.
		var clean = bridge.SaveOne( session );
		Check( "unmutated_editor_save_skipped", clean.Outcome == EffigySaveOutcome.Clean && SameBytes( src, copy ) );

		// 2. Unmutated forced save through DocumentSaveService.
		var forced = bridge.SaveOne( session, force: true );
		r["unmutated_forced_save"] = forced.Outcome.ToString();
		r["unmutated_file_byte_identical"] = SameBytes( src, copy );
		Check( "unmutated_reopen_equal", forced.Outcome != EffigySaveOutcome.Failed
			&& StudioDocument.Write( StudioDocument.ReadFile( copy ) ) == canon0 );
		r["features"] = session.Studio!.Features.Count;

		// 3. Mutate through the session history (the path the live service's undo adapter records).
		if ( session.Studio.Features.Count > 0 )
		{
			var feature = session.Studio.Features[0];
			bool was = feature.Suppressed;
			ParametricEditService.SetFeatureSuppressed( session, feature.Id, !was );
			Check( "mutated_dirty", session.IsDirty );
			string canonM = StudioDocument.Write( session.Studio );

			var saved = bridge.SaveAll();
			r["mutated_save"] = saved[0].Outcome.ToString();
			var reopened = StudioDocument.ReadFile( copy );
			Check( "mutated_reopen_equal", saved[0].Outcome == EffigySaveOutcome.Written
				&& StudioDocument.Write( reopened ) == canonM
				&& canonM != canon0
				&& reopened.Features[0].Suppressed == !was );
			Check( "mutated_clean_after_save", !session.IsDirty );

			// 4. Undo and save: back to exactly what the unmutated save wrote.
			session.History.Undo();
			var undone = bridge.SaveAll();
			Check( "undo_save_restores", undone[0].Outcome == EffigySaveOutcome.Written
				&& StudioDocument.Write( StudioDocument.ReadFile( copy ) ) == canon0 );
		}

		Check( "no_temp_files_left", !Directory.EnumerateFiles( work, "*.tmp" ).Any() );
		r["ok"] = ok;
		return r;
	}

	static JsonObject Clip( string src, string work, string model )
	{
		var r = new JsonObject { ["file"] = src, ["kind"] = "riganim" };
		bool ok = true;
		void Check( string name, bool pass ) { r[name] = pass; ok &= pass; }

		string copy = CopyIn( src, work );
		var studio = StudioDocument.ReadFile( model );
		var modelSession = DocumentSession.CreateEffigy( model, studio );
		// register: false, and the model is never saved here: the model path may be an original.
		using var bridge = new EffigySaveBridge( modelSession, register: false );
		var session = bridge.OpenClip( copy );
		var clip = session.Clip!;
		string canon0 = RigAnimWriter.Write( clip );
		var origJson = JsonNode.Parse( File.ReadAllText( src ) )!.AsObject();

		var clean = bridge.SaveOne( session );
		Check( "unmutated_editor_save_skipped", clean.Outcome == EffigySaveOutcome.Clean && SameBytes( src, copy ) );

		var forced = bridge.SaveOne( session, force: true );
		r["unmutated_forced_save"] = forced.Outcome.ToString();
		r["unmutated_file_byte_identical"] = SameBytes( src, copy );
		var savedJson = JsonNode.Parse( File.ReadAllText( copy ) )!.AsObject();
		r["unmutated_json_deep_equal"] = JsonNode.DeepEquals( origJson, savedJson );
		Check( "unmutated_reopen_equal", forced.Outcome != EffigySaveOutcome.Failed
			&& RigAnimWriter.Write( RigAnimFile.ReadFile( copy ) ) == canon0 );
		Check( "unmutated_other_fields_kept", OtherFieldsEqual( origJson, savedJson ) );
		r["tracks"] = clip.Tracks.Count;

		var track = clip.Tracks.FirstOrDefault( t => t.Keys.Count > 0 );
		if ( track != null )
		{
			var key = track.Keys[^1];
			var moved = new RigPose( key.Local.Position + new Vec3( 0.5f, 0f, 0f ), key.Local.Rotation, key.Local.Scale );
			ClipEditService.InsertOrReplaceKey( session, track.Kind, track.Name, key.Frame, moved, key.Ease );
			Check( "mutated_dirty", session.IsDirty );
			string canonM = RigAnimWriter.Write( clip );

			var saved = bridge.SaveAll();
			r["mutated_save"] = saved[1].Outcome.ToString();
			var reopened = RigAnimFile.ReadFile( copy );
			var mutatedJson = JsonNode.Parse( File.ReadAllText( copy ) )!.AsObject();
			Check( "mutated_reopen_equal", saved[1].Outcome == EffigySaveOutcome.Written
				&& RigAnimWriter.Write( reopened ) == canonM && canonM != canon0 );
			Check( "mutated_other_fields_kept", OtherFieldsEqual( origJson, mutatedJson ) );
			Check( "model_not_written", saved[0].Outcome == EffigySaveOutcome.Clean );
			Check( "mutated_clean_after_save", !session.IsDirty );
		}

		Check( "no_temp_files_left", !Directory.EnumerateFiles( work, "*.tmp" ).Any() );
		r["ok"] = ok;
		return r;
	}

	// Everything except the keys the kernel owns must come back exactly as it was read.
	static readonly HashSet<string> KernelOwned = new() { "BoneTracks", "FrameCount", "AnimationSpeed", "SourceModel", "RigAsset", "RootMotion" };

	static bool OtherFieldsEqual( JsonObject a, JsonObject b )
	{
		var keys = a.Select( p => p.Key ).Concat( b.Select( p => p.Key ) ).Distinct().Where( k => !KernelOwned.Contains( k ) );
		if ( !keys.All( k => JsonNode.DeepEquals( a[k], b[k] ) ) ) return false;

		// Per track, everything but the keys (DisplayName and the like) must survive too.
		static Dictionary<string, JsonObject> Extras( JsonObject o ) =>
			(o["BoneTracks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToDictionary(
				t => $"{t["BoneName"]}/{t["Target"]}",
				t => new JsonObject( t.Where( p => p.Key != "Keyframes" ).Select( p => KeyValuePair.Create( p.Key, p.Value?.DeepClone() ) ) ) );
		var ea = Extras( a );
		var eb = Extras( b );
		return ea.Count == eb.Count && ea.All( p => eb.TryGetValue( p.Key, out var o ) && JsonNode.DeepEquals( p.Value, o ) );
	}
}
