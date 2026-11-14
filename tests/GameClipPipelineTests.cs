#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

/// <summary>Game-clip pipeline: named library, loop seams, root-motion policy,
/// control-rig bake and per-clip export (<c>godlike-game-clip-pipeline</c>).</summary>
internal static class GameClipPipelineTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void AssertNear( float actual, float expected, float tolerance, string what )
		{
			Assert( MathF.Abs( actual - expected ) <= tolerance, $"{what}: expected {expected}, got {actual} (tolerance {tolerance})" );
		}

		void AssertThrows<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, no exception was thrown" );
		}

		RigPose At( Vec3 pos ) => new( pos, Quat.Identity, Vec3.One );
		RigPose AtRot( Vec3 pos, Quat rot ) => new( pos, rot, Vec3.One );

		Skeleton TwoBoneRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ) );
			return rig;
		}

		// A 1-second loop that does NOT close: the root walks +10 in X and turns
		// 30 degrees, the arm swings 20 degrees. The seam pops 30 degrees / 10 units.
		RigAnimFile WalkClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = AtRot( new Vec3( 10, 0, 0 ), Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), MathF.PI / 6f ) ), Ease = RigEase.Linear } } } );
			clip.Tracks.Add( new RigTrack { Name = "arm", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 5, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = AtRot( new Vec3( 0, 5, 0 ), Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), 20f * MathF.PI / 180f ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		// A 1-second loop that closes: the root walks out 5 and back, no turn.
		RigAnimFile CleanLoopClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 15, Local = At( new Vec3( 5, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		Skeleton HipsRig()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "hips", 0, Xform.Translate( new Vec3( 0, 10, 0 ) ) );
			return rig;
		}

		// Hips walk +10 in X over 30 frames; the root stands still.
		RigAnimFile HipsClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear } } } );
			clip.Tracks.Add( new RigTrack { Name = "hips", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 10, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 10, 10, 0 ) ), Ease = RigEase.Linear } } } );
			return clip;
		}

		Xform[] WorldAt( RigAnimFile clip, Skeleton rig, float frame ) =>
			RigAnimFile.ModelSpace( rig, clip.SampleLocal( rig, frame ) );

		void AssertWorldNear( RigAnimFile a, RigAnimFile b, Skeleton rig, int[] frames, float tolerance, string what, string skipBone = "" )
		{
			foreach ( var f in frames )
			{
				var wa = WorldAt( a, rig, f );
				var wb = WorldAt( b, rig, f );
				for ( var bone = 0; bone < rig.Count; bone++ )
				{
					if ( rig.Bones[bone].Name == skipBone )
						continue;
					Assert( ( wa[bone].Origin - wb[bone].Origin ).Length <= tolerance,
						$"{what}: bone '{rig.Bones[bone].Name}' frame {f} differs by {( wa[bone].Origin - wb[bone].Origin ).Length}" );
				}
			}
		}

		// ---- Independent GLB parse for assertions (JsonDocument + raw BIN slice) -----
		( JsonDocument Json, byte[] Bin ) ParseGlb( byte[] glb )
		{
			Assert( glb.Length >= 12, "GLB too short for a header" );
			Assert( BitConverter.ToUInt32( glb, 0 ) == 0x46546C67u, "GLB magic must be 'glTF'" );
			Assert( BitConverter.ToUInt32( glb, 4 ) == 2u, "GLB version must be 2" );
			uint jsonLength = BitConverter.ToUInt32( glb, 12 );
			Assert( BitConverter.ToUInt32( glb, 16 ) == 0x4E4F534Au, "First GLB chunk must be JSON" );
			var json = JsonDocument.Parse( Encoding.UTF8.GetString( glb, 20, (int)jsonLength ).TrimEnd( ' ', '\0' ) );
			int binOffset = 20 + (int)( ( jsonLength + 3 ) & ~3u );
			byte[] bin = Array.Empty<byte>();
			if ( binOffset + 8 <= glb.Length && BitConverter.ToUInt32( glb, binOffset + 4 ) == 0x004E4942u )
			{
				uint binLength = BitConverter.ToUInt32( glb, binOffset );
				bin = new byte[binLength];
				Array.Copy( glb, binOffset + 8, bin, 0, (int)binLength );
			}
			return ( json, bin );
		}

		int ComponentsPerElement( string type ) => type switch
		{
			"SCALAR" => 1,
			"VEC2" => 2,
			"VEC3" => 3,
			"VEC4" => 4,
			"MAT4" => 16,
			_ => throw new Exception( $"Unknown accessor type '{type}'" ),
		};

		float[] ReadFloats( JsonElement root, byte[] bin, int accessorIndex )
		{
			var accessor = root.GetProperty( "accessors" )[accessorIndex];
			var view = root.GetProperty( "bufferViews" )[accessor.GetProperty( "bufferView" ).GetInt32()];
			int offset = view.TryGetProperty( "byteOffset", out var off ) ? off.GetInt32() : 0;
			if ( accessor.TryGetProperty( "byteOffset", out var accOff ) ) offset += accOff.GetInt32();
			int length = view.GetProperty( "byteLength" ).GetInt32();
			int count = accessor.GetProperty( "count" ).GetInt32();
			int components = ComponentsPerElement( accessor.GetProperty( "type" ).GetString()! );
			var bytes = new byte[length];
			Array.Copy( bin, offset, bytes, 0, length );
			var values = new float[count * components];
			Buffer.BlockCopy( bytes, 0, values, 0, values.Length * 4 );
			return values;
		}

		GlbMaterial PlainMaterial( string name ) =>
			new( name, new Vec4( 1f, 1f, 1f, 1f ), 0f, 1f, new Vec3( 0f, 0f, 0f ), null, null, null, null );

		// Root bone at the origin, arm bone 5 in up the Y axis; a triangle whose first
		// vertex rides the root and the other two ride the arm. The "wave" clip moves
		// the arm +10 in X over 30 frames @30fps. (Same fixture as GlbRigExportTests.)
		( Skeleton Rig, PolyMesh Mesh, RigAnimFile Wave ) RiggedTriangle()
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ) );
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( 0, 5, 0 ),
				new Vec3( 2, 5, 0 ),
			} );
			mesh.Skin = new SkinWeights( 3 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 1, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 1, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) }, material: 0 );

			var wave = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var armTrack = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			armTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			armTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 10, 5, 0 ), Quat.Identity, Vec3.One ) } );
			wave.Tracks.Add( armTrack );
			return ( rig, mesh, wave );
		}

		registry.Add( "game-clip", "loop-seam checker flags a popping loop and passes a clean one", () =>
		{
			var rig = TwoBoneRig();
			var pop = LoopSeamChecker.Check( WalkClip(), rig );
			Assert( !pop.Pass, "a loop with a 30-degree turn and 10 units of travel passed the seam check" );
			AssertNear( pop.MaxRotationDegrees, 30f, 0.5f, "rotation pop" );
			AssertNear( pop.MaxTranslation, 10f, 0.01f, "translation pop" );
			Assert( pop.WorstBone == "root", $"worst bone is '{pop.WorstBone}', expected 'root'" );

			var clean = LoopSeamChecker.Check( CleanLoopClip(), rig );
			Assert( clean.Pass, "a loop that returns to its start failed the seam check: " + clean );
		} );

		registry.Add( "game-clip", "CloseLoop closes the seam and leaves the source untouched", () =>
		{
			var rig = TwoBoneRig();
			var walk = WalkClip();
			var closed = LoopSeamChecker.CloseLoop( walk, rig );
			Assert( closed.After.Pass, "the closed loop still pops: " + closed.After );

			var first = closed.Clip.SampleLocal( rig, 0 );
			var last = closed.Clip.SampleLocal( rig, closed.Clip.LastFrame );
			for ( var b = 0; b < rig.Count; b++ )
			{
				Assert( ( last[b].Origin - first[b].Origin ).Length < 1e-4f,
					$"closed loop bone '{rig.Bones[b].Name}' last frame does not match its first frame" );
				Assert( LoopSeamChecker.RotationBetween( first[b], last[b] ) < 0.01f,
					$"closed loop bone '{rig.Bones[b].Name}' last rotation does not match its first" );
			}

			var sourceFirst = walk.SampleLocal( rig, 0 );
			for ( var b = 0; b < rig.Count; b++ )
				Assert( ( first[b].Origin - sourceFirst[b].Origin ).Length < 1e-4f,
					"CloseLoop moved the first frame" );

			Assert( walk.Tracks[0].Keys.Count == 2, "CloseLoop rewrote the source clip" );
			Assert( !LoopSeamChecker.Check( walk, rig ).Pass, "the source clip stopped popping" );
		} );

		registry.Add( "game-clip", "root-motion extract keeps every pose and strip round-trips to in-place", () =>
		{
			var rig = HipsRig();
			var clip = HipsClip();
			var original = RigAnimFile.Read( RigAnimWriter.Write( clip ) );
			var settings = new RootMotionSettings { Bone = "root", Source = "hips", Mode = RootMotionMode.XY, Linear = true };

			var extracted = RootMotionPolicy.Extract( clip, rig, settings );
			Assert( extracted.Ok, "extract failed: " + extracted.Error );
			AssertNear( extracted.Travel.x, 10f, 0.05f, "extracted travel" );
			// The carrier bone is the exception: its world position moves by the travel
			// by design - that is what root motion IS (the root has no parent, so its
			// world position IS its local, and the next assert pins that local to the
			// travel). Every other bone's played pose must be unchanged.
			AssertWorldNear( clip, original, rig, new[] { 0, 15, 30 }, 0.005f, "extract changed the played poses", skipBone: settings.Bone );
			AssertNear( clip.SampleLocal( rig, 30 )[0].Origin.x, 10f, 0.05f, "root does not carry the travel after extract" );

			var extractedCopy = RigAnimFile.Read( RigAnimWriter.Write( clip ) );
			var stripped = RootMotionPolicy.Strip( clip, rig );
			Assert( stripped.Ok, "strip failed: " + stripped.Error );
			Assert( clip.RootMotion is null, "strip left the root-motion settings on the clip" );
			Assert( clip.SampleLocal( rig, 30 )[0].Origin.Length < 1e-3f, "the root still travels after strip" );

			// The stripped clip is the in-place version of the extracted clip.
			foreach ( var f in new[] { 0, 15, 30 } )
			{
				var strippedWorld = WorldAt( clip, rig, f );
				var inPlaceWorld = RigAnimFile.ModelSpace( rig, RootMotion.InPlace( extractedCopy, rig, extractedCopy.SampleLocal( rig, f ), f ) );
				for ( var b = 0; b < rig.Count; b++ )
					Assert( ( strippedWorld[b].Origin - inPlaceWorld[b].Origin ).Length <= 0.01f,
						$"stripped bone '{rig.Bones[b].Name}' frame {f} is not the in-place pose" );
			}

			var fresh = HipsClip();
			var refused = RootMotionPolicy.Extract( fresh, rig, new RootMotionSettings { Bone = "hips", Source = "" } );
			Assert( refused.Error is not null && refused.Error.Contains( "top bone" ),
				"extract onto a non-top bone succeeded: " + refused );
			Assert( fresh.Tracks[0].Keys.Count == 2, "a refused extract rewrote the clip" );
		} );

		registry.Add( "game-clip", "baked clip matches the control rig and holds sampled keys only", () =>
		{
			var rig = TwoBoneRig();
			var walk = WalkClip();
			var baked = ControlRigBaker.Bake( walk, rig );
			Assert( baked.FrameRate == 30, $"baked frame rate {baked.FrameRate}, expected 30" );
			Assert( baked.FrameCount == 31, $"baked frame count {baked.FrameCount}, expected 31" );

			var report = ControlRigBaker.Validate( baked, walk, rig );
			Assert( report.Pass, "baked clip drifts from the control rig: " + report );

			// Linear motion collapses to a handful of keys, and re-reducing removes none.
			foreach ( var track in baked.Tracks.Where( t => t.Kind == RigTrackKind.Bone ) )
			{
				Assert( track.Keys.Count <= 3, $"baked track '{track.Name}' kept {track.Keys.Count} keys for linear motion" );
				Assert( track.Keys.All( k => k.Frame >= 0 && k.Frame <= baked.LastFrame ), "baked key outside the clip" );
				Assert( ClipBake.Reduce( track, 0, baked.LastFrame, 0.02f, 0.25f, 0.005f ) == 0,
					$"baked track '{track.Name}' still had redundant keys" );
			}
		} );

		registry.Add( "game-clip", "per-clip export writes one 30fps animation and FBX refuses honestly", () =>
		{
			var (rig, mesh, wave) = RiggedTriangle();
			var parts = new[] { new GlbPart( "tri", mesh, PlainMaterial( "mat" ) ) };
			var entry = new ClipLibraryEntry( "walk", wave );

			byte[] glb = ClipExportService.ExportClip( entry, rig, parts );
			var (json, bin) = ParseGlb( glb );
			var root = json.RootElement;
			var animations = root.GetProperty( "animations" );
			Assert( animations.GetArrayLength() == 1, $"expected 1 animation, got {animations.GetArrayLength()}" );
			Assert( animations[0].GetProperty( "name" ).GetString() == "walk", "the exported animation is not named 'walk'" );
			var samplers = animations[0].GetProperty( "samplers" );
			var channels = animations[0].GetProperty( "channels" );
			int inputAccessor = samplers[0].GetProperty( "input" ).GetInt32();
			var times = ReadFloats( root, bin, inputAccessor );
			Assert( times.Length == 31, $"input must hold 31 times (frames 0..30), got {times.Length}" );
			AssertNear( times[0], 0f, 1e-6f, "first input time" );
			AssertNear( times[15], 0.5f, 1e-6f, "middle input time" );
			AssertNear( times[30], 1f, 1e-6f, "last input time" );

			int armTranslationSampler = -1;
			foreach ( var channel in channels.EnumerateArray() )
			{
				var target = channel.GetProperty( "target" );
				if ( target.GetProperty( "node" ).GetInt32() == 1 && target.GetProperty( "path" ).GetString() == "translation" )
					armTranslationSampler = channel.GetProperty( "sampler" ).GetInt32();
			}
			Assert( armTranslationSampler >= 0, "the export must animate the arm's translation" );
			int armTranslationOutput = samplers[armTranslationSampler].GetProperty( "output" ).GetInt32();
			var armTranslations = ReadFloats( root, bin, armTranslationOutput );
			Assert( armTranslations.Length == 93, $"arm translation output must hold 31 VEC3s, got {armTranslations.Length}" );
			AssertNear( armTranslations[0], 0f, 1e-6f, "arm translation frame 0 x" );
			AssertNear( armTranslations[90], 0.254f, 1e-6f, "arm translation frame 30 x" );
			AssertNear( armTranslations[92], -0.127f, 1e-6f, "arm translation frame 30 z" );

			AssertThrows<ArgumentException>( () => ClipExportService.ExportClip( entry, rig, parts, "fbx" ), "per-clip FBX export" );
		} );

		registry.Add( "game-clip", "clip library trims, scales and repeats", () =>
		{
			var source = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			source.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 10, Local = At( new Vec3( 10, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 20, Local = At( new Vec3( 20, 0, 0 ) ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( new Vec3( 30, 0, 0 ) ), Ease = RigEase.Linear } } } );
			source.MorphEvents.Add( new RigMorphEvent { Morph = "smile", Start = 10, End = 20, FadeIn = 0, FadeOut = 0, Value = 1f } );
			source.Marks = new() { new RigAnimMark { Name = "mid", Frame = 10, Note = "" } };

			var entry = new ClipLibraryEntry( "trimmed", source ) { TrimIn = 10, TrimOut = 20, Scale = 2f, Repeat = 2 };
			var played = entry.Resolve();
			Assert( played.FrameCount == 12, $"played frame count {played.FrameCount}, expected 12" );
			var frames = played.Find( "root" )!.Keys.Select( k => k.Frame ).ToArray();
			Assert( frames.SequenceEqual( new[] { 0, 5, 6, 11 } ), "played key frames: " + string.Join( ",", frames ) );
			var xs = played.Find( "root" )!.Keys.Select( k => k.Local.Position.x ).ToArray();
			Assert( xs.SequenceEqual( new[] { 10f, 20f, 10f, 20f } ), "played key positions: " + string.Join( ",", xs ) );
			Assert( played.MorphEvents.Count == 2, $"played morph events {played.MorphEvents.Count}, expected 2" );
			Assert( played.MorphEvents[0].Start == 0 && played.MorphEvents[0].End == 5, "first morph event did not retime" );
			Assert( played.MorphEvents[1].Start == 6 && played.MorphEvents[1].End == 11, "second morph event did not retime" );
			Assert( played.Marks is { Count: 2 } && played.Marks[0].Frame == 0 && played.Marks[1].Frame == 6, "marks did not retime" );
			Assert( source.Tracks[0].Keys.Count == 4, "Resolve rewrote the source clip" );

			var badScale = new ClipLibraryEntry( "bad", source ) { Scale = 0f };
			AssertThrows<ArgumentException>( () => badScale.Resolve(), "scale 0" );
			var emptyTrim = new ClipLibraryEntry( "empty", source ) { TrimIn = 25, TrimOut = 10 };
			AssertThrows<InvalidOperationException>( () => emptyTrim.Resolve(), "empty trim" );
		} );

		registry.Add( "game-clip", "clip library loads a folder lazily and skips bad files", () =>
		{
			var dir = Path.Combine( Path.GetTempPath(), "game-clip-test-" + Guid.NewGuid().ToString( "N" ) );
			try
			{
				Directory.CreateDirectory( dir );
				File.WriteAllText( Path.Combine( dir, "walk.riganim" ), RigAnimWriter.Write( WalkClip() ) );
				File.WriteAllText( Path.Combine( dir, "run.riganim" ), RigAnimWriter.Write( CleanLoopClip() ) );
				File.WriteAllText( Path.Combine( dir, "idle.riganim" ), RigAnimWriter.Write( HipsClip() ) );
				File.WriteAllText( Path.Combine( dir, "bad.riganim" ), "{not json" );

				var library = new ClipLibrary();
				var report = library.LoadFolder( dir );
				Assert( report.Ok, "folder load failed: " + report.Error );
				Assert( report.Added == 3, $"load added {report.Added}, expected 3" );
				Assert( report.Skipped == 1, $"load skipped {report.Skipped}, expected 1" );
				Assert( library.Count == 3, $"library holds {library.Count}, expected 3" );

				var walk = library.Find( "walk" );
				Assert( walk is not null, "walk not found after load" );
				Assert( !walk!.IsLoaded, "the folder load decoded the clip eagerly" );
				Assert( walk.HeaderFrameRate == 30, $"header frame rate {walk.HeaderFrameRate}, expected 30" );
				Assert( walk.HeaderFrameCount == 31, $"header frame count {walk.HeaderFrameCount}, expected 31" );
				Assert( library.Find( "WALK" ) is not null, "find must be case-insensitive" );

				var resolved = walk.Resolve();
				Assert( walk.IsLoaded, "resolve did not load the clip" );
				Assert( resolved.FrameCount == 31, $"resolved frame count {resolved.FrameCount}, expected 31" );

				AssertThrows<ArgumentException>( () => library.Add( new ClipLibraryEntry( "walk", WalkClip() ) ), "duplicate name" );
				Assert( !new ClipLibrary().LoadFolder( dir + "-missing" ).Ok, "a missing folder loaded fine" );
			}
			finally
			{
				try { if ( Directory.Exists( dir ) ) Directory.Delete( dir, true ); } catch { }
			}
		} );

		registry.Add( "game-clip", "loop-seam checker passes a single-frame clip", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 1 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( new Vec3( 0, 0, 0 ) ), Ease = RigEase.Linear } } } );
			var report = LoopSeamChecker.Check( clip, TwoBoneRig() );
			Assert( report.Pass, "a single-frame clip failed the seam check" );
			Assert( report.MaxRotationDegrees == 0f && report.MaxTranslation == 0f, "a single-frame clip reported a seam" );
		} );
	}
}
