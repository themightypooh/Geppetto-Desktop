using System;
using System.IO;
using System.Linq;
using Effigy;

internal static class FrameAnimTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "frame-anim", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		// A 4x2 drawing filled with one opaque colour, so a pixel read
		// distinguishes drawings by channel instead of by id string.
		byte[] Solid( byte r, byte g, byte b )
		{
			var rgba = new byte[4 * 2 * 4];
			for ( int i = 0; i < rgba.Length; i += 4 ) { rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255; }
			return rgba;
		}

		FrameDrawingLibrary Library()
		{
			var library = new FrameDrawingLibrary();
			library.Add( new FrameDrawing( "a", "A", 4, 2, Solid( 255, 0, 0 ) ) );
			library.Add( new FrameDrawing( "b", "B", 4, 2, Solid( 0, 255, 0 ) ) );
			library.Add( new FrameDrawing( "c", "C", 4, 2, Solid( 0, 0, 255 ) ) );
			return library;
		}

		string WorkDir()
		{
			var dir = Path.Combine( Path.GetTempPath(), "frameanim-tests-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}

		Check( "on-twos at 24fps holds each drawing two frames", () =>
		{
			var clip = new FrameAnimClip( "twos", 4, 2 ) { Fps = 24 };
			clip.AddTrack( "main" ).ExposeOnTwos( new[] { "a", "b", "c" }, 0, FrameRole.Key );
			Require( clip.FrameCount == 6, $"frame count {clip.FrameCount}" );
			Require( Math.Abs( clip.DurationSeconds - 0.25 ) < 1e-9, $"duration {clip.DurationSeconds}" );
			Require( clip.DrawingAt( "main", 0 ) == "a" && clip.DrawingAt( "main", 1 ) == "a" );
			Require( clip.DrawingAt( "main", 2 ) == "b" && clip.DrawingAt( "main", 3 ) == "b" );
			Require( clip.DrawingAt( "main", 4 ) == "c" && clip.DrawingAt( "main", 5 ) == "c" );
		} );

		Check( "one drawing instanced across non-adjacent exposures", () =>
		{
			var clip = new FrameAnimClip( "reuse", 4, 2 );
			var track = clip.AddTrack( "main" );
			track.Expose( "a", 0, 2, FrameRole.Key );
			track.Expose( "b", 2, 2, FrameRole.Breakdown );
			track.Expose( "a", 4, 2, FrameRole.Key );
			Require( clip.DrawingAt( "main", 0 ) == "a" && clip.DrawingAt( "main", 4 ) == "a" );
			Require( clip.DistinctDrawings().SequenceEqual( new[] { "a", "b" } ) );
			Require( track.Exposures.Count == 3, "three exposures, two drawings" );
		} );

		Check( "keys breakdowns and in-betweens are first-class roles", () =>
		{
			var clip = new FrameAnimClip( "roles", 4, 2 );
			var track = clip.AddTrack( "main" );
			track.Expose( "a", 0, 1, FrameRole.Key );
			track.Expose( "b", 1, 1, FrameRole.Breakdown );
			track.Expose( "c", 2, 1, FrameRole.InBetween );
			Require( track.Exposures.Select( e => e.Role ).SequenceEqual( new[] { FrameRole.Key, FrameRole.Breakdown, FrameRole.InBetween } ) );
		} );

		Check( "overlapping exposures on one layer are rejected", () =>
		{
			var track = new DrawingTrack( "main" );
			track.Expose( "a", 0, 4, FrameRole.Key );
			bool threw = false;
			try { track.Expose( "b", 3, 2, FrameRole.Key ); } catch ( ArgumentException ) { threw = true; }
			Require( threw, "overlap at frame 3 must throw" );
			Require( track.Exposures.Count == 1, "rejected exposure must not land" );
		} );

		Check( "a gap is a blank frame, not an error", () =>
		{
			var clip = new FrameAnimClip( "gap", 4, 2 );
			var track = clip.AddTrack( "main" );
			track.Expose( "a", 0, 1, FrameRole.Key );
			track.Expose( "b", 4, 1, FrameRole.Key );
			Require( clip.DrawingAt( "main", 2 ) == null );
			Require( clip.CompositionAt( 2 ).Count == 0 );
			Require( clip.Validate( Library() ).Count == 0 );
		} );

		Check( "redraw keeps exposures and changes the next export", () =>
		{
			var library = Library();
			var clip = new FrameAnimClip( "redraw", 4, 2 );
			clip.AddTrack( "main" ).Expose( "a", 0, 2, FrameRole.Key );
			var dir = WorkDir();
			FrameAnimExporter.Export( clip, library, Path.Combine( dir, "v1" ) );
			var before = PngReader.Read( File.ReadAllBytes( Path.Combine( dir, "v1", "frame_0000.png" ) ) );
			Require( before.Rgba[0] == 255 && before.Rgba[1] == 0, "v1 frame is drawing a (red)" );
			library.Redraw( "a", Solid( 0, 0, 255 ) );
			FrameAnimExporter.Export( clip, library, Path.Combine( dir, "v2" ) );
			var after = PngReader.Read( File.ReadAllBytes( Path.Combine( dir, "v2", "frame_0000.png" ) ) );
			Require( after.Rgba[0] == 0 && after.Rgba[2] == 255, "v2 frame is the redrawn drawing (blue)" );
			Require( clip.DrawingAt( "main", 0 ) == "a", "exposure still names drawing a" );
		} );

		Check( "patch region edits only that rectangle", () =>
		{
			var library = Library();
			var patch = new byte[4]; // one opaque green pixel
			patch[1] = 255; patch[3] = 255;
			library.PatchRegion( "a", 1, 0, 1, 1, patch );
			var drawing = library.Get( "a" );
			Require( drawing.Rgba[4] == 0 && drawing.Rgba[5] == 255, "patched pixel is green" );
			Require( drawing.Rgba[0] == 255 && drawing.Rgba[1] == 0, "neighbour pixel still red" );
			bool threw = false;
			try { library.PatchRegion( "a", 3, 0, 2, 1, new byte[8] ); } catch ( ArgumentException ) { threw = true; }
			Require( threw, "a patch spilling off the drawing must throw, not clip" );
		} );

		Check( "reference layer is never composited or baked", () =>
		{
			var library = Library();
			var clip = new FrameAnimClip( "roto", 4, 2 );
			clip.AddTrack( "main" ).Expose( "a", 0, 1, FrameRole.Key );
			clip.Reference = new RotoscopeReference( "shot.mp4", "video", 24, 0, 0.5f );
			Require( clip.CompositionAt( 0 ).All( p => p.Value != "shot.mp4" ) );
			var dir = WorkDir();
			var result = FrameAnimExporter.Export( clip, library, dir );
			var pixels = PngReader.Read( File.ReadAllBytes( result.FrameFiles[0] ) );
			Require( pixels.Rgba[0] == 255, "exported frame holds the drawing, not the reference" );
			var manifest = File.ReadAllText( result.ManifestPath );
			Require( manifest.Contains( "\"bakedIntoDrawings\": false" ), "manifest must say the reference is not baked" );
			Require( !manifest.Contains( "shot.mp4\" ]" ), "reference id must not appear as a frame drawing" );
		} );

		Check( "PNG sequence plus manifest round-trips timing", () =>
		{
			var library = Library();
			var clip = new FrameAnimClip( "seq", 4, 2 ) { Fps = 24, Loop = false };
			var track = clip.AddTrack( "main" );
			track.Expose( "a", 0, 3, FrameRole.Key );
			track.Expose( "b", 3, 3, FrameRole.Breakdown );
			var dir = WorkDir();
			var result = FrameAnimExporter.Export( clip, library, dir );
			Require( result.FrameCount == 6 );
			Require( result.FrameFiles.Count == 6 );
			for ( int f = 0; f < 6; f++ )
				Require( File.Exists( Path.Combine( dir, $"frame_{f:D4}.png" ) ), $"frame_{f:D4}.png missing" );
			var reloaded = FrameAnimExporter.FromManifest( result.ManifestPath );
			Require( reloaded.Fps == 24 && reloaded.Loop == false );
			Require( reloaded.DrawingAt( "main", 0 ) == "a" && reloaded.DrawingAt( "main", 2 ) == "a" );
			Require( reloaded.DrawingAt( "main", 3 ) == "b" && reloaded.DrawingAt( "main", 5 ) == "b" );
			Require( reloaded.Tracks[0].Exposures[0].Hold == 3 );
		} );

		Check( "unknown drawing id fails validation before export", () =>
		{
			var clip = new FrameAnimClip( "bad", 4, 2 );
			clip.AddTrack( "main" ).Expose( "missing", 0, 1, FrameRole.Key );
			var errors = clip.Validate( Library() );
			Require( errors.Count == 1 && errors[0].Contains( "missing" ), string.Join( "; ", errors ) );
			bool threw = false;
			try { FrameAnimExporter.Export( clip, Library(), WorkDir() ); } catch ( InvalidOperationException ) { threw = true; }
			Require( threw, "export must refuse a clip naming a drawing that does not exist" );
		} );
	}
}
