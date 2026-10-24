#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Effigy;
using Marionette.Desktop;

internal static class FaceAnimCallersTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "face-anim", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action, string what )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( $"expected an error for {what}, got silent success" );
		}
		void Close( float actual, float expected, string what )
		{
			Require( MathF.Abs( actual - expected ) < 1e-3f, $"{what}: expected {expected}, got {actual}" );
		}
		// MorphWeightsAt only returns keyed morphs: a morph with no track (a
		// neutral-only clip) reads as 0, exactly as the player treats a
		// missing morph. Stereo controllers key BASE_L/_R, not BASE (see
		// FaceRig.Default: mouth_drop/part/puckerer are stereo, while
		// jaw_drop/presser/bite are not).
		static float W( Dictionary<string, float> weights, string morph ) =>
			weights.TryGetValue( morph, out var v ) ? v : 0;

		FaceRig Rig() => FaceRig.Default();

		Dictionary<string, FaceRig.Setting> Preset( FaceRig rig, string name ) =>
			new( rig.Presets[name], StringComparer.OrdinalIgnoreCase );

		Check( "face keys become Morph tracks, evaluated through MorphWeightsAt", () =>
		{
			var rig = Rig();
			var clip = FaceAnimCallers.BuildFaceClip( rig, new[]
			{
				new FaceAnimCallers.FaceKeyframe { Frame = 0, Settings = Preset( rig, "smile" ) },
				new FaceAnimCallers.FaceKeyframe { Frame = 30, Settings = new Dictionary<string, FaceRig.Setting>() },
			} );
			Require( clip.Tracks.Count > 0, "smile clip has no Morph tracks" );
			Require( clip.Tracks.TrueForAll( t => t.Kind == RigTrackKind.Morph ), "face clip must carry only Morph tracks" );
			var at0 = clip.MorphWeightsAt( 0 );
			Close( W( at0, "corner_puller_L" ), 0.8f, "smile corner_puller_L at frame 0" );
			Close( W( at0, "corner_puller_R" ), 0.8f, "smile corner_puller_R at frame 0" );
			Close( W( at0, "cheek_raiser_L" ), 0.45f, "smile cheek_raiser_L at frame 0" );
			var at15 = clip.MorphWeightsAt( 15 );
			Close( W( at15, "corner_puller_L" ), 0.4f, "smile easing to neutral at frame 15 (Smooth midpoint)" );
			var at30 = clip.MorphWeightsAt( 30 );
			Close( W( at30, "corner_puller_L" ), 0f, "smile returned to neutral at frame 30" );
		} );

		Check( "face scale scales every morph weight", () =>
		{
			var rig = Rig();
			var clip = FaceAnimCallers.BuildFaceClip( rig, new[]
			{
				new FaceAnimCallers.FaceKeyframe { Frame = 0, Settings = Preset( rig, "smile" ), Scale = 0.5f },
			} );
			Close( W( clip.MorphWeightsAt( 0 ), "corner_puller_L" ), 0.4f, "smile at scale 0.5" );
		} );

		Check( "preset clip returns to neutral instead of holding the last expression", () =>
		{
			var rig = Rig();
			var clip = FaceAnimCallers.BuildPresetClip( rig, new[]
			{
				new FaceAnimCallers.PresetStep { Frame = 0, Preset = "smile" },
				new FaceAnimCallers.PresetStep { Frame = 20, Preset = "neutral" },
			} );
			Close( W( clip.MorphWeightsAt( 20 ), "corner_puller_L" ), 0f, "neutral step zeroes corner_puller" );
			Close( W( clip.MorphWeightsAt( 20 ), "cheek_raiser_L" ), 0f, "neutral step zeroes cheek_raiser" );
		} );

		Check( "unknown preset fails loudly", () =>
		{
			var rig = Rig();
			Throws( () => FaceAnimCallers.BuildPresetClip( rig, new[]
			{
				new FaceAnimCallers.PresetStep { Frame = 0, Preset = "nope" },
			} ), "unknown preset" );
		} );

		Check( "duplicate face frame and empty keys fail loudly", () =>
		{
			var rig = Rig();
			Throws( () => FaceAnimCallers.BuildFaceClip( rig, Array.Empty<FaceAnimCallers.FaceKeyframe>() ), "empty keys" );
			Throws( () => FaceAnimCallers.BuildFaceClip( rig, new[]
			{
				new FaceAnimCallers.FaceKeyframe { Frame = 5, Settings = Preset( rig, "smile" ) },
				new FaceAnimCallers.FaceKeyframe { Frame = 5, Settings = Preset( rig, "frown" ) },
			} ), "duplicate frame" );
		} );

		Check( "viseme switch holds one shape until the next onset, with no bleed", () =>
		{
			var rig = Rig();
			var clip = FaceAnimCallers.BuildVisemeClip( rig, new[]
			{
				new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "A" },
				new FaceAnimCallers.VisemeKey { Frame = 10, Viseme = "U" },
			} );
			Require( clip.Tracks.TrueForAll( t => t.Kind == RigTrackKind.Morph ), "viseme clip must carry only Morph tracks" );
			var atHalf = clip.MorphWeightsAt( 0.5f );
			Close( W( atHalf, "jaw_drop" ), 0.8f, "viseme A jaw_drop" );
			Close( W( atHalf, "mouth_drop_L" ), 0.6f, "viseme A mouth_drop_L" );
			var held = clip.MorphWeightsAt( 9.5f );
			Close( W( held, "jaw_drop" ), 0.8f, "Stepped viseme A still held at 9.5" );
			// Sampled at 10.5, not exactly 10: RigTrack.Evaluate at the exact
			// integer Stepped key still returns the previous key (segment order).
			var switched = clip.MorphWeightsAt( 10.5f );
			Close( W( switched, "jaw_drop" ), 0.4f, "viseme U jaw_drop after switch" );
			Close( W( switched, "puckerer_L" ), 0.9f, "viseme U puckerer_L after switch" );
			Close( W( switched, "mouth_drop_L" ), 0f, "viseme U must zero viseme A's mouth_drop, not bleed it" );
		} );

		Check( "10-viseme aliases and Blair shapes resolve", () =>
		{
			var rig = Rig();
			Close( W( FaceAnimCallers.BuildVisemeClip( rig, new[] { new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "BMP" } } ).MorphWeightsAt( 0.5f ), "presser" ), 1f, "BMP -> presser" );
			Close( W( FaceAnimCallers.BuildVisemeClip( rig, new[] { new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "M" } } ).MorphWeightsAt( 0.5f ), "presser" ), 1f, "alias M -> BMP" );
			Close( W( FaceAnimCallers.BuildVisemeClip( rig, new[] { new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "FV" } } ).MorphWeightsAt( 0.5f ), "bite" ), 0.85f, "FV -> bite" );
			Close( W( FaceAnimCallers.BuildVisemeClip( rig, new[] { new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "MBP" } } ).MorphWeightsAt( 0.5f ), "presser" ), 1f, "Blair MBP -> presser" );
			Close( W( FaceAnimCallers.BuildVisemeClip( rig, new[] { new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "rest" } } ).MorphWeightsAt( 0.5f ), "jaw_drop" ), 0f, "Blair rest is neutral" );
			Throws( () => FaceAnimCallers.BuildVisemeClip( rig, new[] { new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "ZZZ" } } ), "unknown viseme" );
		} );

		Check( "viseme lead frames pull onsets earlier", () =>
		{
			var rig = Rig();
			var clip = FaceAnimCallers.BuildVisemeClip( rig, new[]
			{
				new FaceAnimCallers.VisemeKey { Frame = 0, Viseme = "rest" },
				new FaceAnimCallers.VisemeKey { Frame = 10, Viseme = "U" },
			}, leadFrames: 2 );
			Close( W( clip.MorphWeightsAt( 8.5f ), "puckerer_L" ), 0.9f, "U onset pulled from 10 to 8 by lead frames" );
		} );

		Check( "Rhubarb TSV import: docs 'Hi' example becomes Stepped viseme keys", () =>
		{
			var rig = Rig();
			var tsv = "0.00\tX\n0.05\tD\n0.27\tC\n0.31\tB\n0.43\tX\n0.47\tX\n";
			var cues = FaceAnimCallers.ParseRhubarbTsv( tsv, 30 );
			Require( cues.Count == 6, $"expected 6 TSV cues, got {cues.Count}" );
			Require( cues[1].Frame == 2 && cues[2].Frame == 8 && cues[3].Frame == 9 && cues[4].Frame == 13,
				$"TSV frames wrong: {cues[1].Frame},{cues[2].Frame},{cues[3].Frame},{cues[4].Frame} (want 2,8,9,13)" );
			var clip = FaceAnimCallers.BuildRhubarbClip( rig, tsv, 30 );
			Close( W( clip.MorphWeightsAt( 2.5f ), "jaw_drop" ), 0.8f, "Rhubarb D -> viseme_AA jaw_drop" );
			Close( W( clip.MorphWeightsAt( 8.5f ), "part_L" ), 1f, "Rhubarb C -> viseme_I part_L" );
			Close( W( clip.MorphWeightsAt( 9.5f ), "part_L" ), 0.25f, "Rhubarb B -> viseme_KK part_L, AA jaw_drop gone" );
			Close( W( clip.MorphWeightsAt( 9.5f ), "jaw_drop" ), 0.25f, "Rhubarb B -> viseme_KK jaw_drop" );
			Close( W( clip.MorphWeightsAt( 13.5f ), "jaw_drop" ), 0f, "trailing Rhubarb X closes the mouth" );
			Throws( () => FaceAnimCallers.ParseRhubarbTsv( "not-a-line\n", 30 ), "malformed TSV line" );
		} );

		Check( "Rhubarb .dat import: frame cues become Stepped viseme keys", () =>
		{
			var rig = Rig();
			var dat = "0 X\n2 D\n8 C\n13 X\n";
			var cues = FaceAnimCallers.ParseRhubarbDat( dat, 30 );
			Require( cues.Count == 4 && cues[1].Frame == 2 && cues[1].Code == "D", "dat cues parsed wrong" );
			var clip = FaceAnimCallers.BuildRhubarbClip( rig, dat, 30, fromDat: true );
			Close( W( clip.MorphWeightsAt( 2.5f ), "jaw_drop" ), 0.8f, "dat D -> viseme_AA jaw_drop" );
			Close( W( clip.MorphWeightsAt( 13.5f ), "jaw_drop" ), 0f, "dat trailing X closes the mouth" );
			Throws( () => FaceAnimCallers.ParseRhubarbDat( "0.05 D\n", 30 ), "dat line with seconds instead of frames" );
		} );

		Check( "Rhubarb custom map overrides the built-in code table", () =>
		{
			var rig = Rig();
			var map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ) { ["D"] = "viseme_U" };
			var clip = FaceAnimCallers.BuildRhubarbClip( rig, "0.00\tX\n0.05\tD\n", 30, customMap: map );
			Close( W( clip.MorphWeightsAt( 2.5f ), "puckerer_L" ), 0.9f, "custom map D -> viseme_U puckerer_L" );
			Throws( () => FaceAnimCallers.BuildRhubarbClip( rig, "0.00\tQ\n", 30 ), "unknown Rhubarb code" );
		} );

		Check( "face clip round-trips through RigAnimWriter and back", () =>
		{
			var rig = Rig();
			var clip = FaceAnimCallers.BuildPresetClip( rig, new[]
			{
				new FaceAnimCallers.PresetStep { Frame = 0, Preset = "smile" },
				new FaceAnimCallers.PresetStep { Frame = 20, Preset = "neutral" },
			} );
			var path = Path.Combine( Path.GetTempPath(), Guid.NewGuid() + ".riganim" );
			try
			{
				File.WriteAllText( path, RigAnimWriter.Write( clip ) );
				var back = RigAnimFile.ReadFile( path );
				Close( W( back.MorphWeightsAt( 0 ), "corner_puller_L" ), 0.8f, "round-tripped smile at frame 0" );
				Close( W( back.MorphWeightsAt( 20 ), "corner_puller_L" ), 0f, "round-tripped neutral at frame 20" );
			}
			finally { if ( File.Exists( path ) ) File.Delete( path ); }
		} );
	}
}
