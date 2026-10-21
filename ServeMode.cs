using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// --serve: the viewport for the cabinet UI (geppetto-desktop/cabinet/host.py). Renders headless
/// (EGL, no window) into an offscreen target and hands each frame to the host through a shared
/// memory file, top-down RGBA. Commands come in on stdin, one per line; events go out on stdout as
/// lines starting with '@' followed by JSON (anything else on stdout is log noise).
///
///   orbit DX DY | pan DX DY | zoom F | size W H | frame | play 0|1 | time T | bones 0|1 | grid 0|1
///   wire N | select BONE | pick X Y | clip PATH | loop 0|1 | ack | quit
///   keys | kmove D T:F.. | kdel T:F.. | kease E T:F.. | kins F T.. | undo | redo | save
///
/// A new frame is only written after the host acks the last one, so it never reads a half-written
/// frame and a slow host simply gets fewer frames.
/// </summary>
public static class ServeMode
{
	static readonly object OutLock = new();

	static void Emit( JsonObject ev )
	{
		lock ( OutLock ) { Console.Out.Write( "@" + ev.ToJsonString() + "\n" ); Console.Out.Flush(); }
	}

	public static int Run( DesktopOptions options )
	{
		int width = options.Width, height = options.Height;
		var commands = new BlockingCollection<string>();
		var reader = new Thread( () =>
		{
			string line;
			while ( (line = Console.In.ReadLine()) is not null ) commands.Add( line.Trim() );
			commands.Add( "quit" );
		} ) { IsBackground = true };
		reader.Start();

		Emit( new JsonObject { ["ev"] = "loading", ["model"] = options.Model } );
		var loader = new DocumentLoader();
		if ( options.WatchRun is null )
		{
			loader.Start( options.Model, options.Clip, options.Animate, options.Time );
			loader.Completion.GetAwaiter().GetResult();
			if ( loader.Status != DocumentLoader.State.Ready )
			{
				Emit( new JsonObject { ["ev"] = "error", ["msg"] = (loader.Error ?? "load failed").Split( '\n' )[0] } );
				return 1;
			}
		}
		var mesh = options.WatchRun is null ? loader.Mesh : AiLivePreview.EmptyMesh();
		var player = options.WatchRun is null ? loader.Player : null;
		AiLivePreview follow = options.WatchRun is null ? null : new AiLivePreview( options.WatchRun );
		long displayedRevision = 0, eventSequence = 0;
		string displayedRun = null;
		bool framedLive = false;
		string previewPath = options.WatchRun is null ? options.Model : "";
		string previewError = null, publishedAt = null;

		using var context = new HeadlessGlContext();
		var gl = context.Gl;
		using var renderer = new SceneRenderer( gl );
		renderer.View = options.View;
		// The clip's reference props: the same lit path, their own ModelDoc and materials.
		renderer.Props = loader.Snapshot?.Props ?? Array.Empty<PropScene>();
		using var overlay = new SkeletonOverlay( gl );
		var target = new CaptureTarget( gl, width, height );
		var camera = new OrbitCamera();
		bool fbx = options.Model.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase );
		void Reframe()
		{
			// Clips can move the arms far outside the FBX bind-pose bounds. Frame
			// the evaluated mesh so the cabinet does not crop the posed character.
			player?.Step(0);
			var bounds = mesh.Picker.Bounds(player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones);
			var centre = mesh.Centre;
			float radius = mesh.Radius;
			if (float.IsFinite(bounds.Min.Length()) && float.IsFinite(bounds.Max.Length()))
			{
				centre = (bounds.Min + bounds.Max) * 0.5f;
				radius = Math.Max((bounds.Max - bounds.Min).Length() * 0.5f, 0.1f);
			}
			camera.Frame( centre, radius );
			float vertical = camera.FovDegrees * MathF.PI / 360;
			float horizontal = MathF.Atan(MathF.Tan(vertical) * width / Math.Max(1f, height));
			camera.Distance = Math.Max(camera.Distance, radius * 1.15f / MathF.Sin(Math.Min(vertical, horizontal)));
			camera.YawDegrees = fbx ? -55 : 35;
			camera.PitchDegrees = 18;
			renderer.ResetTemporalHistory();
		}
		Reframe();
		if ( mesh.TriangleCount > 0 ) mesh.Upload( gl );
		renderer.WireframeMode = options.Wireframe ? 1 : 0;

		string shmPath = $"/dev/shm/geppetto-view-{Environment.ProcessId}.rgba";
		MemoryMappedFile map = null;
		MemoryMappedViewAccessor view = null;
		void Remap()
		{
			view?.Dispose(); map?.Dispose();
			using ( var fs = new FileStream( shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite ) )
				fs.SetLength( (long)width * height * 4 );
			map = MemoryMappedFile.CreateFromFile( shmPath, FileMode.Open, null, (long)width * height * 4, MemoryMappedFileAccess.ReadWrite );
			view = map.CreateViewAccessor();
		}
		Remap();
		var pixels = new byte[width * height * 4];
		var flipped = new byte[width * height * 4];

		Emit( Ready( options, mesh, player, shmPath, loader ) );
		ClipKeyEditor keys = null;
		void OpenKeys( string path )
		{
			keys = player?.Clip is { } clip ? new ClipKeyEditor( clip, path, () => player.InvalidateClipCache( preservePlayhead: true ) ) : null;
			EmitKeys();
		}
		void EmitKeys() => Emit( keys?.Snapshot( mesh.Rig ) ?? new JsonObject { ["ev"] = "keys", ["tracks"] = new JsonArray(), ["undo"] = 0, ["redo"] = 0, ["dirty"] = false, ["saveable"] = false, ["frames"] = 0 } );
		ClipKeyEditor Keys() => keys ?? throw new InvalidOperationException( "No clip to edit keys on." );
		OpenKeys( options.Clip );

		bool dirty = true, acked = true, running = true;
		int frameNo = 0;
		var clock = Stopwatch.StartNew();
		double last = clock.Elapsed.TotalSeconds;
		try
		{
			while ( running )
			{
				// wait for work: block when idle, poll at ~60 Hz while playing
				bool busy = dirty || player is { Playing: true };
				if ( commands.TryTake( out var cmd, busy && acked ? 0 : busy ? 4 : follow is not null ? 25 : 250 ) )
				{
					do
					{
						try { running = Apply( cmd ); }
						catch ( Exception ex ) { Emit( new JsonObject { ["ev"] = "error", ["msg"] = ex.Message } ); }
					} while ( running && commands.TryTake( out cmd ) );
				}
				if ( !running ) break;
				if ( follow is not null )
				{
					if ( follow.Error is { } error && error != previewError )
					{
						previewError = error;
						Emit( new JsonObject { ["ev"] = "ai-preview-error", ["msg"] = error } );
					}
					var activity = follow.State;
					if ( activity is not null && (activity["sequence"]?.GetValue<long>() ?? 0) > eventSequence )
					{
						eventSequence = activity["sequence"]!.GetValue<long>();
						activity["ev"] = "ai-activity";
						Emit( activity );
					}
					if ( follow.Take() is { } update )
					{
						if ( displayedRun is not null && (displayedRun != update.RunId || update.Revision <= displayedRevision) ) update.Mesh.Dispose();
						else
						{
							try
							{
								var upload = Stopwatch.StartNew();
								if ( update.Mesh.TriangleCount > 0 ) update.Mesh.Upload( gl );
								if ( gl.GetError() is var uploadError && uploadError != GLEnum.NoError ) throw new InvalidOperationException( $"Preview upload: {uploadError}" );
								var old = mesh; mesh = update.Mesh; player = null;
								foreach ( var prop in renderer.Props ) { prop.Mesh.ReleaseGpu( gl ); prop.Dispose(); }
								renderer.Props = Array.Empty<PropScene>();
								overlay.SelectedBone = overlay.SelectedBody = -1;
								previewPath = update.Path; publishedAt = update.PublishedAt; previewError = null;
								displayedRun = update.RunId; displayedRevision = update.Revision;
								old.ReleaseGpu( gl ); old.Dispose();
								if ( !framedLive && mesh.TriangleCount > 0 ) { fbx = false; Reframe(); framedLive = true; }
								renderer.ResetTemporalHistory(); dirty = true;
								Emit( new JsonObject { ["ev"] = "ai-preview", ["runId"] = update.RunId, ["revision"] = update.Revision,
									["path"] = update.Path, ["tris"] = mesh.TriangleCount, ["load_ms"] = update.LoadMs,
									["upload_ms"] = upload.Elapsed.TotalMilliseconds, ["cache"] = update.CacheNote } );
							}
							catch ( Exception ex )
							{
								update.Mesh.ReleaseGpu( gl ); update.Mesh.Dispose();
								Emit( new JsonObject { ["ev"] = "ai-preview-error", ["msg"] = ex.Message } );
							}
						}
					}
				}


				double now = clock.Elapsed.TotalSeconds;
				float dt = (float)Math.Min( now - last, 0.1 );
				if ( !acked || !(dirty || player is { Playing: true }) ) { last = now; continue; }
				if ( dt < 1f / 60 && !dirty ) { Thread.Sleep( 2 ); continue; }
				last = now;

				player?.Step( player.Playing ? dt : 0 );
				gl.BindFramebuffer( FramebufferTarget.Framebuffer, target.Handle );
				gl.Viewport( 0, 0, (uint)width, (uint)height );
				if ( mesh.TriangleCount > 0 ) renderer.Draw( mesh, player, camera, width, height, options.Exposure, target.Handle );
				else { gl.ClearColor( .14f, .145f, .16f, 1 ); gl.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit ); }
				(Vector3 Min, Vector3 Max)? selBounds = overlay.SelectedBody >= 0
					? mesh.Picker.Bounds( overlay.SelectedBody, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones ) : null;
				overlay.Draw( camera.ViewMatrix() * camera.ProjectionMatrix( width / (float)height ), mesh.Rig,
					player is null ? ReadOnlySpan<Xform>.Empty : player.WorldPose, mesh.BodyPicks, selBounds );
				ReadFrame();
				view.WriteArray( 0, flipped, 0, flipped.Length );
				view.Flush();
				frameNo++;
				dirty = false;
				acked = false;
				Emit( new JsonObject
				{
					["ev"] = "frame", ["n"] = frameNo, ["w"] = width, ["h"] = height,
					["time"] = player?.Time ?? 0, ["playing"] = player?.Playing ?? false, ["loop"] = player?.Loop ?? true,
					["frame"] = ClipFrame( player ),
					["tris"] = mesh.TriangleCount, ["revision"] = displayedRevision, ["runId"] = displayedRun, ["path"] = previewPath,
					["commit_to_frame_ms"] = publishedAt is null ? 0 : (DateTimeOffset.UtcNow - DateTimeOffset.Parse( publishedAt )).TotalMilliseconds,
					["render_target_objects"] = RenderTarget.LiveObjects, ["mesh_textures"] = MeshResourceBudget.LiveGpuTextures,
					["yaw"] = camera.YawDegrees, ["pitch"] = camera.PitchDegrees, ["distance"] = camera.Distance,
				} );
			}
		}
		finally
		{
			follow?.Dispose();
			view?.Dispose(); map?.Dispose();
			try { File.Delete( shmPath ); } catch { }
			target.Dispose();
			foreach ( var prop in renderer.Props ) { prop.Mesh.ReleaseGpu( gl ); prop.Dispose(); }
			mesh.ReleaseGpu( gl );
		}
		return 0;

		unsafe void ReadFrame()
		{
			gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, target.Handle );
			gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
			fixed ( byte* raw = pixels )
				gl.ReadPixels( 0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, raw );
			int row = width * 4;
			for ( int y = 0; y < height; y++ )
				System.Buffer.BlockCopy( pixels, (height - 1 - y) * row, flipped, y * row, row );
		}

		bool Apply( string line )
		{
			if ( line.Length == 0 ) return true;
			var a = line.Split( ' ', 2 );
			string arg = a.Length > 1 ? a[1] : "";
			float F( int i ) => float.Parse( arg.Split( ' ' )[i], System.Globalization.CultureInfo.InvariantCulture );
			switch ( a[0] )
			{
				case "follow":
					follow?.Dispose(); follow = null;
					displayedRevision = eventSequence = 0; displayedRun = null; framedLive = false; previewError = publishedAt = null;
					if ( arg.Length > 0 ) follow = new AiLivePreview( arg );
					Emit( new JsonObject { ["ev"] = "ai-follow", ["path"] = arg } );
					return true;
				case "quit": return false;
				case "ack": acked = true; return true;
				case "orbit": camera.Orbit( F( 0 ) * 0.35f, -F( 1 ) * 0.35f ); break;
				case "pan":
					var side = Vector3.Normalize( Vector3.Cross( camera.Forward, Vector3.UnitZ ) );
					camera.Target += (-side * F( 0 ) + Vector3.UnitZ * F( 1 )) * (camera.Distance * 0.0015f);
					break;
				case "zoom": camera.Zoom( F( 0 ) ); break;
				case "frame": Reframe(); break;
				case "size":
					int w = Math.Clamp( (int)F( 0 ), 16, 4096 ), h = Math.Clamp( (int)F( 1 ), 16, 4096 );
					if ( w == width && h == height ) return true;
					width = w; height = h;
					target.Dispose();
					target = new CaptureTarget( gl, width, height );
					pixels = new byte[width * height * 4];
					flipped = new byte[width * height * 4];
					Remap();
					acked = true;
					break;
				case "play": if ( player is not null ) SetPlaying( player, arg == "1" ); break;
				case "time": if ( player is not null ) { player.Playing = false; player.Time = Math.Clamp( F( 0 ), 0, ClipSeconds( player ) ); } break;
				case "loop": if ( player is not null ) { player.Loop = arg == "1"; if ( !player.Playing ) player.Time = Math.Clamp( player.Time, 0, ClipSeconds( player ) ); } break;
				case "bones": overlay.ShowBones = arg == "1"; break;
				case "grid": overlay.ShowGrid = arg == "1"; break;
				case "wire": renderer.WireframeMode = Math.Clamp( (int)F( 0 ), 0, 2 ); break;
				case "select":
					int bone = (int)F( 0 );
					overlay.SelectedBone = mesh.Rig is not null && bone >= 0 && bone < mesh.Rig.Count ? bone : -1;
					overlay.SelectedBody = -1;
					if ( overlay.SelectedBone >= 0 ) overlay.ShowBones = true;
					Emit( new JsonObject { ["ev"] = "selected", ["bone"] = overlay.SelectedBone, ["body"] = -1 } );
					break;
				case "pick":
				{
					var (ro, rd) = camera.ScreenRay( F( 0 ), F( 1 ), width, height );
					int boneHit = player is not null && overlay.ShowBones ? SkeletonOverlay.PickBone( ro, rd, mesh.Rig, player.WorldPose ) : -1;
					int bodyHit = -1;
					if ( boneHit < 0 )
					{
						bodyHit = mesh.Picker.Pick( ro, rd, player is null ? ReadOnlySpan<Matrix4x4>.Empty : player.GpuBones );
						if ( bodyHit >= 0 && !string.IsNullOrEmpty( mesh.BodyPicks[bodyHit].BoneName ) && mesh.Rig is not null )
							boneHit = mesh.Rig.IndexOf( mesh.BodyPicks[bodyHit].BoneName );
					}
					overlay.SelectedBone = boneHit;
					overlay.SelectedBody = bodyHit;
					Emit( new JsonObject { ["ev"] = "selected", ["bone"] = boneHit, ["body"] = bodyHit } );
					break;
				}
				case "clip":
					if ( player is null ) throw new InvalidOperationException( "This model has no skeleton to play a clip on." );
					player.LoadClip( arg );
					Reframe();
					player.Playing = true;
					Emit( ClipInfo( player, arg ) );
					OpenKeys( arg );
					break;
				// the dope sheet: keys are "track:frame" refs, track being the index in the keys event
				case "keys": EmitKeys(); break;
				case "kmove": { var p = arg.Split( ' ' ); Keys().Move( ClipKeyEditor.ParseSelection( p.Skip( 1 ) ), int.Parse( p[0] ) ); EmitKeys(); break; }
				case "kdel": Keys().Delete( ClipKeyEditor.ParseSelection( arg.Split( ' ' ) ) ); EmitKeys(); break;
				case "kease": { var p = arg.Split( ' ' ); Keys().SetEase( ClipKeyEditor.ParseSelection( p.Skip( 1 ) ), (RigEase)Math.Clamp( int.Parse( p[0] ), 0, 6 ) ); EmitKeys(); break; }
				case "kins": { var p = arg.Split( ' ' ); Keys().Insert( int.Parse( p[0] ), p.Skip( 1 ).Where( x => x.Length > 0 ).Select( int.Parse ) ); EmitKeys(); break; }
				case "undo": Keys().Undo(); EmitKeys(); break;
				case "redo": Keys().Redo(); EmitKeys(); break;
				case "save":
				{
					var backup = Keys().Save( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify ), "geppetto", "clip-backups" ) );
					Emit( new JsonObject { ["ev"] = "saved", ["path"] = keys.Path, ["backup"] = backup } );
					EmitKeys();
					break;
				}
				default: throw new ArgumentException( "unknown command: " + a[0] );
			}
			dirty = true;
			return true;
		}
	}

	/// <summary>
	/// The frame the player is showing, by the player's own rule: a looping clip that is playing
	/// wraps its ever-running time, a paused (or one-shot) clip clamps it, so the last frame can be held.
	/// </summary>
	static int ClipFrame( MarionettePlayer player )
	{
		var clip = player?.Clip;
		if ( clip is not { FrameRate: > 0 } || clip.LastFrame <= 0 ) return 0;
		float raw = player.Time * clip.FrameRate;
		float frame = player.Loop && player.Playing ? ((raw % clip.LastFrame) + clip.LastFrame) % clip.LastFrame : Math.Clamp( raw, 0, clip.LastFrame );
		return (int)MathF.Round( frame );
	}

	static float ClipSeconds( MarionettePlayer player )
		=> player.Clip is { FrameRate: > 0, LastFrame: > 0 } clip ? clip.LastFrame / (float)clip.FrameRate : float.MaxValue;

	/// <summary>
	/// Pausing a looping clip folds its running time back into the clip, or the paused pose (which
	/// clamps) would jump to the last frame; playing a one-shot clip that has ended starts it over.
	/// </summary>
	static void SetPlaying( MarionettePlayer player, bool playing )
	{
		float length = ClipSeconds( player );
		if ( playing == player.Playing || length == float.MaxValue ) { player.Playing = playing; return; }
		if ( !playing && player.Loop && player.Time > length ) player.Time %= length;
		if ( playing && !player.Loop && player.Time >= length ) player.Time = 0;
		player.Playing = playing;
	}

	static JsonObject ClipInfo( MarionettePlayer player, string path )
	{
		var clip = player?.Clip;
		return new JsonObject
		{
			["ev"] = "clip",
			["path"] = path ?? "",
			["name"] = path is null ? (player is null ? "" : "procedural") : Path.GetFileNameWithoutExtension( path ),
			["rate"] = clip?.FrameRate ?? 30,
			["frames"] = clip?.LastFrame ?? 0,
			["tracks"] = clip?.Tracks.Count ?? 0,
			["space"] = player?.ClipSpaceNote ?? "",
			["space_scale"] = player?.ClipPositionScale ?? 1f,
		};
	}

	static JsonObject Ready( DesktopOptions options, ViewportMesh mesh, MarionettePlayer player, string shm, DocumentLoader loader )
	{
		var bones = new JsonArray();
		if ( mesh.Rig is not null )
			foreach ( var b in mesh.Rig.Bones ) bones.Add( new JsonObject { ["name"] = b.Name, ["parent"] = b.Parent } );
		var bodies = new JsonArray();
		foreach ( var b in mesh.BodyPicks ?? Array.Empty<ViewportMesh.BodyPickEntry>() )
			bodies.Add( new JsonObject { ["name"] = b.Name, ["bone"] = b.BoneName ?? "" } );
		var materials = new JsonArray();
		foreach ( var b in mesh.Batches ) materials.Add( b.MaterialName );
		return new JsonObject
		{
			["ev"] = "ready",
			["shm"] = shm,
			["model"] = options.WatchRun ?? options.Model,
			["tris"] = mesh.TriangleCount,
			["bodies"] = bodies,
			["bones"] = bones,
			["materials"] = materials,
			["load_s"] = Math.Round( loader.TotalSeconds, 2 ),
			["cache"] = loader.CacheNote ?? "",
			["clip"] = ClipInfo( player, options.Clip ),
			["clip_space"] = player?.ClipSpaceNote ?? "",
			["clip_space_scale"] = player?.ClipPositionScale ?? 1f,
		};
	}
}
