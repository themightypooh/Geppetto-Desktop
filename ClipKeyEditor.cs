#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// The cabinet's dope sheet, server side: the keys of the clip the viewer is playing, edited in
/// place with undo, and saved back over the .riganim. Modelled on Geppetto's RigTimelineLanes -
/// a key is addressed by (track index, frame), a selection is a list of them, and a drag of many
/// keys is ONE edit that moves them all by the same delta.
///
/// Every edit is a snapshot of the tracks it touched, before and after, so undo restores exactly
/// what was there however the edit merged or dropped keys - no per-op inverse to get wrong.
/// </summary>
public sealed class ClipKeyEditor
{
	public RigAnimFile Clip { get; }
	public string? Path { get; }
	public EditHistory History { get; } = new( 200 );

	readonly string? _baseJson;
	readonly Action _changed;

	public ClipKeyEditor( RigAnimFile clip, string? path, Action? changed = null )
	{
		Clip = clip ?? throw new ArgumentNullException( nameof( clip ) );
		Path = path;
		_changed = changed ?? (() => { });
		if ( CanSave ) _baseJson = File.ReadAllText( path! );
	}

	public bool CanSave => Path is not null && Path.EndsWith( ".riganim", StringComparison.OrdinalIgnoreCase ) && File.Exists( Path );

	public readonly record struct KeyRef( int Track, int Frame );

	/// <summary>"t:f t:f ..." - the cabinet's selection on the wire.</summary>
	public static List<KeyRef> ParseSelection( IEnumerable<string> tokens )
	{
		var list = new List<KeyRef>();
		foreach ( var token in tokens )
		{
			if ( token.Length == 0 ) continue;
			var p = token.Split( ':' );
			if ( p.Length != 2 ) throw new FormatException( "bad key ref: " + token );
			list.Add( new KeyRef( int.Parse( p[0], CultureInfo.InvariantCulture ), int.Parse( p[1], CultureInfo.InvariantCulture ) ) );
		}
		return list;
	}

	/// <summary>Moves every listed key by the same delta, clamped so none goes below frame 0. A
	/// moved key landing on an unmoved one replaces it, as dropping a key on another does in the
	/// editor. Returns the delta actually applied.</summary>
	public int Move( IReadOnlyList<KeyRef> keys, int delta )
	{
		var picked = Resolve( keys );
		if ( picked.Count == 0 || delta == 0 ) return 0;
		delta = Math.Max( delta, -picked.Min( k => k.Frame ) );
		if ( delta == 0 ) return 0;
		int d = delta;
		Edit( $"Move {picked.Count} key(s) {d:+#;-#}", picked, ( track, frames, keysOut ) =>
		{
			var moving = keysOut.Where( k => frames.Contains( k.Frame ) ).ToList();
			keysOut.RemoveAll( k => frames.Contains( k.Frame ) );
			foreach ( var k in moving ) k.Frame += d;
			var landed = moving.Select( k => k.Frame ).ToHashSet();
			keysOut.RemoveAll( k => landed.Contains( k.Frame ) );
			keysOut.AddRange( moving );
		} );
		return delta;
	}

	public int Delete( IReadOnlyList<KeyRef> keys )
	{
		var picked = Resolve( keys );
		if ( picked.Count == 0 ) return 0;
		Edit( $"Delete {picked.Count} key(s)", picked, ( _, frames, keysOut ) => keysOut.RemoveAll( k => frames.Contains( k.Frame ) ) );
		return picked.Count;
	}

	public int SetEase( IReadOnlyList<KeyRef> keys, RigEase ease )
	{
		var picked = Resolve( keys ).Where( k => Clip.Tracks[k.Track].Keys.First( x => x.Frame == k.Frame ).Ease != ease ).ToList();
		if ( picked.Count == 0 ) return 0;
		Edit( $"{ease} on {picked.Count} key(s)", picked, ( _, frames, keysOut ) =>
		{
			foreach ( var k in keysOut ) if ( frames.Contains( k.Frame ) ) k.Ease = ease;
		} );
		return picked.Count;
	}

	/// <summary>Keys each track at the frame with the pose it already has there, so the motion is
	/// unchanged until the key is moved - the cabinet has no pose gizmos, so the value of a new key
	/// can only come from the curve itself. A track already keyed at that frame is left alone.</summary>
	public int Insert( int frame, IEnumerable<int> tracks )
	{
		if ( frame < 0 ) throw new ArgumentOutOfRangeException( nameof( frame ) );
		var picked = tracks.Distinct()
			.Where( t => t >= 0 && t < Clip.Tracks.Count && Clip.Tracks[t].Keys.All( k => k.Frame != frame ) )
			.Select( t => new KeyRef( t, frame ) ).ToList();
		if ( picked.Count == 0 ) return 0;
		var poses = picked.ToDictionary( k => k.Track, k => Clip.Tracks[k.Track].Evaluate( frame ) );
		var eases = picked.ToDictionary( k => k.Track, k => EaseBefore( Clip.Tracks[k.Track], frame ) );
		Edit( $"Key {picked.Count} track(s) at {frame}", picked, ( track, _, keysOut ) =>
			keysOut.Add( new RigKey { Frame = frame, Local = poses[track], Ease = eases[track] } ) );
		return picked.Count;
	}

	/// <summary>Keys one track at the frame with a given local pose, replacing a key already there:
	/// how a script (or the AI) poses a clip from outside, since the cabinet has no gizmos.</summary>
	public void Set( int track, int frame, RigPose pose )
	{
		if ( frame < 0 ) throw new ArgumentOutOfRangeException( nameof( frame ) );
		if ( track < 0 || track >= Clip.Tracks.Count ) throw new ArgumentOutOfRangeException( nameof( track ) );
		var ease = Clip.Tracks[track].Keys.FirstOrDefault( k => k.Frame == frame )?.Ease ?? EaseBefore( Clip.Tracks[track], frame );
		Edit( $"Set key {track}:{frame}", new List<KeyRef> { new( track, frame ) }, ( _, _, keysOut ) =>
		{
			keysOut.RemoveAll( k => k.Frame == frame );
			keysOut.Add( new RigKey { Frame = frame, Local = pose, Ease = ease } );
		} );
	}

	static RigEase EaseBefore( RigTrack track, int frame )
	{
		RigEase ease = RigEase.Smooth;
		foreach ( var k in track.Keys ) { if ( k.Frame > frame ) break; ease = k.Ease; }
		return ease;
	}

	public bool Undo() { if ( !History.CanUndo ) return false; History.Undo(); return true; }
	public bool Redo() { if ( !History.CanRedo ) return false; History.Redo(); return true; }

	/// <summary>Writes the clip over its file, through a temp file so a crash can't leave half a
	/// clip, after copying the file as it was to <paramref name="backupDir"/>. Only the tracks are
	/// rewritten - everything else the clip carries comes from the file it was loaded from.</summary>
	public string Save( string backupDir )
	{
		if ( !CanSave ) throw new InvalidOperationException( "Only a .riganim loaded from disk can be saved." );
		var json = RigAnimWriter.Write( Clip, _baseJson );
		Directory.CreateDirectory( backupDir );
		var stamp = DateTime.Now.ToString( "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture );
		var backup = System.IO.Path.Combine( backupDir, $"{System.IO.Path.GetFileNameWithoutExtension( Path )}.{stamp}.riganim" );
		File.Copy( Path!, backup, overwrite: true );
		var temp = Path + ".tmp";
		File.WriteAllText( temp, json );
		File.Move( temp, Path!, overwrite: true );
		History.MarkSaved();
		return backup;
	}

	/// <summary>The "keys" event: every track with its keys flattened to [frame, ease, ...].</summary>
	public JsonObject Snapshot( Skeleton? rig )
	{
		var tracks = new JsonArray();
		for ( int i = 0; i < Clip.Tracks.Count; i++ )
		{
			var t = Clip.Tracks[i];
			var keys = new JsonArray();
			foreach ( var k in t.Keys ) { keys.Add( k.Frame ); keys.Add( (int)k.Ease ); }
			tracks.Add( new JsonObject
			{
				["name"] = t.Name,
				["kind"] = t.Kind.ToString(),
				["bone"] = t.Kind == RigTrackKind.Bone && rig is not null ? rig.IndexOf( t.Name ) : -1,
				["keys"] = keys,
			} );
		}
		return new JsonObject
		{
			["ev"] = "keys",
			["tracks"] = tracks,
			["undo"] = History.UndoCount,
			["redo"] = History.RedoCount,
			["dirty"] = History.IsDirty,
			["saveable"] = CanSave,
			["frames"] = Clip.LastFrame,
		};
	}

	List<KeyRef> Resolve( IReadOnlyList<KeyRef> keys ) => keys.Distinct()
		.Where( k => k.Track >= 0 && k.Track < Clip.Tracks.Count && Clip.Tracks[k.Track].Keys.Any( x => x.Frame == k.Frame ) )
		.ToList();

	void Edit( string what, List<KeyRef> picked, Action<int, HashSet<int>, List<RigKey>> change )
	{
		var before = new Dictionary<int, List<RigKey>>();
		var after = new Dictionary<int, List<RigKey>>();
		foreach ( var group in picked.GroupBy( k => k.Track ) )
		{
			var track = Clip.Tracks[group.Key];
			before[group.Key] = track.Keys.Select( ClipEditService.CloneKey ).ToList();
			var next = track.Keys.Select( ClipEditService.CloneKey ).ToList();
			change( group.Key, group.Select( k => k.Frame ).ToHashSet(), next );
			next.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
			after[group.Key] = next;
		}
		History.Execute( new DelegateEditCommand( what, () => Apply( after ), () => Apply( before ) ) );
	}

	void Apply( Dictionary<int, List<RigKey>> state )
	{
		foreach ( var (track, keys) in state )
			Clip.Tracks[track].Keys = keys.Select( ClipEditService.CloneKey ).ToList();
		// A key moved past the end makes the clip longer rather than vanishing off it.
		Clip.FrameCount = Math.Max( Clip.FrameCount, Clip.LastKeyFrame + 1 );
		_changed();
	}
}
