using System;
using System.IO;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class ClipKeyEditorTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "clip-keys", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		RigPose At( float y ) => new( new Vec3( 0, y, 0 ), Quat.Identity, Vec3.One );

		RigAnimFile MakeClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( 0 ) }, new RigKey { Frame = 10, Local = At( 10 ) }, new RigKey { Frame = 30, Local = At( 30 ), Ease = RigEase.Linear } } } );
			clip.Tracks.Add( new RigTrack { Name = "arm_L", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( 1 ) }, new RigKey { Frame = 20, Local = At( 2 ) } } } );
			return clip;
		}

		int[] Frames( RigAnimFile clip, int track ) => clip.Tracks[track].Keys.Select( k => k.Frame ).ToArray();
		ClipKeyEditor.KeyRef K( int t, int f ) => new( t, f );

		Check( "a multi-track move is one undo step", () =>
		{
			var clip = MakeClip();
			int changed = 0;
			var editor = new ClipKeyEditor( clip, null, () => changed++ );
			Require( editor.Move( new[] { K( 0, 10 ), K( 1, 20 ) }, 5 ) == 5 );
			Require( Frames( clip, 0 ).SequenceEqual( new[] { 0, 15, 30 } ) && Frames( clip, 1 ).SequenceEqual( new[] { 0, 25 } ) );
			Require( editor.History.UndoCount == 1 && changed == 1 );
			editor.Undo();
			Require( Frames( clip, 0 ).SequenceEqual( new[] { 0, 10, 30 } ) && Frames( clip, 1 ).SequenceEqual( new[] { 0, 20 } ) );
			editor.Redo();
			Require( Frames( clip, 0 ).SequenceEqual( new[] { 0, 15, 30 } ) );
		} );

		Check( "a move clamps at frame zero and keeps the group's spacing", () =>
		{
			var clip = MakeClip();
			var editor = new ClipKeyEditor( clip, null );
			Require( editor.Move( new[] { K( 0, 10 ), K( 0, 30 ) }, -50 ) == -10 );
			// 10 lands on 0 and replaces the unmoved key there; 30 keeps its 20 frame gap
			Require( Frames( clip, 0 ).SequenceEqual( new[] { 0, 20 } ), string.Join( ",", Frames( clip, 0 ) ) );
			Require( clip.Tracks[0].Keys[0].Local.Position.y == 10, "the moved key wins the collision" );
			editor.Undo();
			Require( Frames( clip, 0 ).SequenceEqual( new[] { 0, 10, 30 } ) && clip.Tracks[0].Keys[0].Local.Position.y == 0 );
		} );

		Check( "moving keys past the end lengthens the clip", () =>
		{
			var clip = MakeClip();
			var editor = new ClipKeyEditor( clip, null );
			editor.Move( new[] { K( 0, 30 ) }, 12 );
			Require( clip.LastFrame == 42, "last frame " + clip.LastFrame );
		} );

		Check( "delete, ease and stale refs", () =>
		{
			var clip = MakeClip();
			var editor = new ClipKeyEditor( clip, null );
			Require( editor.Delete( new[] { K( 1, 20 ), K( 1, 99 ), K( 7, 0 ) } ) == 1 );
			Require( Frames( clip, 1 ).SequenceEqual( new[] { 0 } ) );
			Require( editor.SetEase( new[] { K( 0, 0 ), K( 0, 30 ) }, RigEase.Linear ) == 1, "30 is already linear" );
			Require( clip.Tracks[0].Keys[0].Ease == RigEase.Linear );
			Require( editor.SetEase( new[] { K( 0, 0 ) }, RigEase.Linear ) == 0 && editor.History.UndoCount == 2, "a no-op is not an undo step" );
			editor.Undo(); editor.Undo();
			Require( Frames( clip, 1 ).SequenceEqual( new[] { 0, 20 } ) && clip.Tracks[0].Keys[0].Ease == RigEase.Smooth );
		} );

		Check( "inserting a key keeps the motion it lands in", () =>
		{
			var clip = MakeClip();
			var editor = new ClipKeyEditor( clip, null );
			var before = clip.Tracks[1].Evaluate( 7 ).Position.y;
			Require( editor.Insert( 7, new[] { 0, 1, 1, 5 } ) == 2 );
			Require( Frames( clip, 1 ).SequenceEqual( new[] { 0, 7, 20 } ) );
			Require( MathF.Abs( clip.Tracks[1].Keys[1].Local.Position.y - before ) < 1e-5f );
			Require( editor.Insert( 7, new[] { 0 } ) == 0, "already keyed there" );
		} );

		Check( "save writes the edit back and keeps a backup", () =>
		{
			var dir = Path.Combine( settings.WorkDirectory, "clip-keys-save" );
			if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
			Directory.CreateDirectory( dir );
			var path = Path.Combine( dir, "walk.riganim" );
			var node = System.Text.Json.Nodes.JsonNode.Parse( RigAnimWriter.Write( MakeClip() ) )!;
			node["BoneTracks"]![0]!["DisplayName"] = "Root (pretty)";
			node["Marks"] = new System.Text.Json.Nodes.JsonArray( "kept" );
			var original = node.ToJsonString();
			File.WriteAllText( path, original );

			var clip = RigAnimFile.ReadFile( path );
			var editor = new ClipKeyEditor( clip, path );
			Require( editor.CanSave && !editor.History.IsDirty );
			editor.Move( new[] { K( 0, 10 ) }, 3 );
			editor.SetEase( new[] { K( 1, 20 ) }, RigEase.Stepped );
			Require( editor.History.IsDirty );
			var backup = editor.Save( Path.Combine( dir, "backups" ) );
			Require( !editor.History.IsDirty && File.ReadAllText( backup ) == original );
			Require( !File.Exists( path + ".tmp" ) );

			var back = RigAnimFile.ReadFile( path );
			Require( back.Tracks[0].Keys.Select( k => k.Frame ).SequenceEqual( new[] { 0, 13, 30 } ) );
			Require( back.Tracks[1].Keys[1].Ease == RigEase.Stepped );
			Require( MathF.Abs( back.Tracks[0].Keys[1].Local.Position.y - 10 ) < 1e-4f );
			var saved = System.Text.Json.Nodes.JsonNode.Parse( File.ReadAllText( path ) )!;
			Require( (string)saved["BoneTracks"]![0]!["DisplayName"]! == "Root (pretty)", "a track's DisplayName survives the save" );
			Require( (string)saved["Marks"]![0]! == "kept", "what isn't keys survives the save" );
		} );

		Check( "an unsaved clip refuses to save", () =>
		{
			var editor = new ClipKeyEditor( MakeClip(), null );
			Require( !editor.CanSave );
			try { editor.Save( settings.WorkDirectory ); }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( "saved a clip with no file" );
		} );

		Check( "the keys event lists tracks as frame/ease pairs", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			var snap = new ClipKeyEditor( MakeClip(), null ).Snapshot( rig );
			var tracks = snap["tracks"]!.AsArray();
			Require( tracks.Count == 2 && (int)tracks[0]!["bone"]! == 0 && (int)tracks[1]!["bone"]! == -1 );
			Require( string.Join( ",", tracks[0]!["keys"]!.AsArray().Select( n => (int)n! ) ) == "0,0,10,0,30,1" );
		} );
	}
}
