using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;
using Marionette.Desktop;
using Silk.NET.OpenGL;

// M4 item 2: after the 20-float vertex layout change, skinning, picking and the shadow pass
// must all consume the one posed ViewportMesh and the one palette. Each assertion is a
// cross-over — the same probe pixel/ray is checked under two palettes — so a stale mesh or a
// palette read from a different source cannot satisfy all of them by accident.
internal static class PoseAgreementTests
{
	const int W = HeadlessImageChecks.Width, H = HeadlessImageChecks.Height;
	const float Stride = 0.6f;   // caster half-extent in its own plane
	const float CasterZ = 0f;
	const float ReceiverZ = -2f;
	const float ReceiverHalf = 3f;
	const float PoseX = 2f;

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }

		registry.Add( "pose-agreement", "skinning picking and shadow share one posed mesh after the layout change", () =>
		{
			// ---------------------------------------------------------------------------------
			// One rig: "mover" is posed, "anchor" never moves. The receiver rides the anchor so a
			// palette that leaked into the wrong consumer would move the ground plane too.
			// ---------------------------------------------------------------------------------
			var rig = new Skeleton();
			rig.AddBone( "mover", -1, Xform.Identity );
			rig.AddBone( "anchor", -1, Xform.Identity );

			var verts = new List<float>();
			var idx = new List<uint>();
			var batches = new List<ViewportMesh.DrawBatch>();
			var caster = new MaterialPalette.Entry( "caster", .82f, .10f, .10f, .85f );
			var receiver = new MaterialPalette.Entry( "receiver", .55f, .55f, .55f, .9f );

			// GPU side: two quads written straight into the production 20-float layout so the
			// batch path, the skinning path and the shadow pass all read this exact buffer.
			int AddQuad( Vector3 center, Vector3 right, Vector3 up, Vector3 normal, int bone )
			{
				int indexOffset = idx.Count;
				uint first = (uint)( verts.Count / 20 );
				var positions = new[] { center - right - up, center + right - up, center + right + up, center - right + up };
				var uvs = new[] { new Vector2( 0, 0 ), new Vector2( 1, 0 ), new Vector2( 1, 1 ), new Vector2( 0, 1 ) };
				var tangent = Vector3.Normalize( right );
				for ( int corner = 0; corner < 4; corner++ )
				{
					var p = positions[corner];
					verts.AddRange( new[] { p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z,
						bone, 0f, 0f, 0f, 1f, 0f, 0f, 0f,
						uvs[corner].X, uvs[corner].Y, tangent.X, tangent.Y, tangent.Z, 1f } );
				}
				idx.AddRange( new[] { first, first + 1, first + 2, first, first + 2, first + 3 } );
				return indexOffset;
			}

			int casterOffset = AddQuad( new Vector3( 0, 0, CasterZ ), new Vector3( Stride, 0, 0 ),
				new Vector3( 0, Stride, 0 ), Vector3.UnitZ, 0 );
			batches.Add( new ViewportMesh.DrawBatch( "caster", caster, casterOffset * sizeof( uint ), 6 ) );
			int receiverOffset = AddQuad( new Vector3( 0, 0, ReceiverZ ), new Vector3( ReceiverHalf, 0, 0 ),
				new Vector3( 0, ReceiverHalf, 0 ), Vector3.UnitZ, 1 );
			batches.Add( new ViewportMesh.DrawBatch( "receiver", receiver, receiverOffset * sizeof( uint ), 6 ) );

			// CPU side: the same two quads as independently triangulated picker parts carrying the
			// same per-vertex bone, so MeshPicker poses them with player.GpuBones exactly as the
			// vertex shader does.
			static PolyMesh QuadPoly( Vector3 center, Vector3 right, Vector3 up, int bone )
			{
				var poly = new PolyMesh();
				var positions = new[] { center - right - up, center + right - up, center + right + up, center - right + up };
				foreach ( var p in positions ) poly.Positions.Add( new Vec3( p.X, p.Y, p.Z ) );
				poly.AddFace( new[] { 0, 1, 2, 3 },
					new[] { Vec2.Zero, new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, 0 );
				poly.Skin = new SkinWeights( 4 );
				for ( int v = 0; v < 4; v++ ) poly.Skin[v] = new[] { new BoneWeight( bone, 1f ) };
				return poly;
			}

			var mesh = new ViewportMesh
			{
				Vertices = verts.ToArray(),
				Indices = idx.ToArray(),
				Batches = batches.ToArray(),
				Rig = rig,
				HasSkin = true,
				Centre = Vector3.Zero,
				Radius = 5f,
				TriangleCount = idx.Count / 3,
				BodyCount = 2,
			};
			mesh.Picker.Add( QuadPoly( new Vector3( 0, 0, CasterZ ), new Vector3( Stride, 0, 0 ),
				new Vector3( 0, Stride, 0 ), 0 ) );
			mesh.Picker.Add( QuadPoly( new Vector3( 0, 0, ReceiverZ ), new Vector3( ReceiverHalf, 0, 0 ),
				new Vector3( 0, ReceiverHalf, 0 ), 1 ) );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track = new RigTrack { Name = "mover", Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( PoseX, 0, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			var player = new MarionettePlayer( rig );
			player.LoadClip( clip );

			var camera = HeadlessImageChecks.Camera();
			var casterCenter = new Vector3( 0, 0, CasterZ );
			var posedCenter = new Vector3( PoseX, 0, CasterZ );

			// The key light is the only light that varies between probes: everything else is fixed,
			// so a luma cross-over on the receiver can only come from the caster's shadow moving.
			var light = KeyShadowMap.Direction;
			float travel = (CasterZ - ReceiverZ) / light.Z;
			var shadowBind = new Vector3( casterCenter.X - light.X * travel, casterCenter.Y - light.Y * travel, ReceiverZ );
			var shadowPosed = new Vector3( posedCenter.X - light.X * travel, posedCenter.Y - light.Y * travel, ReceiverZ );

			using var context = new HeadlessGlContext();
			var gl = context.Gl;
			using var renderer = new SceneRenderer( gl );
			using var target = new CaptureTarget( gl, W, H );

			byte[] Render( float time )
			{
				player.Time = time;
				player.Step( 0f );
				renderer.Draw( mesh, player, camera, W, H, 1f, target.Handle );
				gl.Finish();
				var image = new byte[W * H * 4];
				gl.BindFramebuffer( FramebufferTarget.ReadFramebuffer, target.Handle );
				gl.ReadBuffer( ReadBufferMode.ColorAttachment0 );
				gl.PixelStore( PixelStoreParameter.PackAlignment, 1 );
				gl.ReadPixels<byte>( 0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, image.AsSpan() );
				Check( gl.GetError() == GLEnum.NoError, "GL error while rendering the posed mesh" );
				return image;
			}

			static bool IsCaster( byte[] image, int x, int y )
			{
				int o = (y * W + x) * 4;
				return image[o] > image[o + 1] + 30 && image[o] > image[o + 2] + 30;
			}
			static double Mean( byte[] image, int x, int y )
			{
				double sum = 0; int count = 0;
				for ( int j = -3; j <= 3; j++ )
				for ( int i = -3; i <= 3; i++ )
				{
					int px = x + i, py = y + j;
					if ( (uint)px >= W || (uint)py >= H ) continue;
					int o = (py * W + px) * 4;
					sum += .2126 * image[o] + .7152 * image[o + 1] + .0722 * image[o + 2];
					count++;
				}
				return count == 0 ? 0 : sum / count;
			}

			// Upload once: Upload drops the CPU vertex copies once they are on the GPU, so the
			// two palettes share one buffer and one release. Picker keeps its own CPU parts and
			// survives the release, which is why picking can assert below this block.
			byte[] bindImage, posedImage;
			mesh.Upload( gl );
			try { bindImage = Render( 0f ); posedImage = Render( 1f ); }
			finally { mesh.ReleaseGpu( gl ); }

			// ---------------------------------------------------------------------------------
			// Skinning: the caster is red exactly where the palette puts it, and nowhere else.
			// Both probes are cross-checked against the opposite palette.
			// ---------------------------------------------------------------------------------
			var (bindX, bindY) = HeadlessImageChecks.Project( camera, casterCenter );
			var (posedX, posedY) = HeadlessImageChecks.Project( camera, posedCenter );
			Check( (uint)bindX < W && (uint)bindY < H && (uint)posedX < W && (uint)posedY < H,
				"projected probe fell outside the capture" );
			Check( Math.Abs( bindX - posedX ) + Math.Abs( bindY - posedY ) > 60,
				"the two caster probes are not far enough apart to distinguish" );

			Check( IsCaster( bindImage, bindX, bindY ), "skinning lost the caster at its bind location" );
			Check( !IsCaster( posedImage, bindX, bindY ), "caster did not leave its bind location under the posed palette" );
			Check( IsCaster( posedImage, posedX, posedY ), "skinning did not move the caster to the posed location" );
			Check( !IsCaster( bindImage, posedX, posedY ), "caster appeared at the posed location before the pose" );

			// ---------------------------------------------------------------------------------
			// Picking: the same player.GpuBones the renderer used, read back after both renders.
			// ---------------------------------------------------------------------------------
			player.Time = 1f;
			player.Step( 0f );
			var origin = camera.Eye;
			int hitPosed = mesh.Picker.Pick( origin, Vector3.Normalize( posedCenter - origin ), player.GpuBones );
			int hitAtBind = mesh.Picker.Pick( origin, Vector3.Normalize( casterCenter - origin ), player.GpuBones );
			Check( hitPosed == 0, $"picking missed the posed caster (hit part {hitPosed})" );
			Check( hitAtBind != 0, "picking still hit the caster at its pre-pose location; it is not sharing the posed mesh" );

			player.Time = 0f;
			player.Step( 0f );
			int hitBindPalette = mesh.Picker.Pick( origin, Vector3.Normalize( casterCenter - origin ), player.GpuBones );
			Check( hitBindPalette == 0, $"picking lost the caster under the bind palette (hit part {hitBindPalette})" );

			// ---------------------------------------------------------------------------------
			// Shadow pass: the key-light shadow lands under the posed caster, not the bind one.
			// ---------------------------------------------------------------------------------
			var (shadowBindX, shadowBindY) = HeadlessImageChecks.Project( camera, shadowBind );
			var (shadowPosedX, shadowPosedY) = HeadlessImageChecks.Project( camera, shadowPosed );
			double bindAtBind = Mean( bindImage, shadowBindX, shadowBindY );
			double bindAtPosed = Mean( bindImage, shadowPosedX, shadowPosedY );
			double posedAtBind = Mean( posedImage, shadowBindX, shadowBindY );
			double posedAtPosed = Mean( posedImage, shadowPosedX, shadowPosedY );

			Check( bindAtBind + 8 < bindAtPosed,
				$"bind render: expected the shadow at {shadowBind} ({bindAtBind:F1}) and light at {shadowPosed} ({bindAtPosed:F1})" );
			Check( posedAtPosed + 8 < posedAtBind,
				$"shadow pass ignored the posed palette: probe under the posed shadow is {posedAtPosed:F1}, unshadowed is {posedAtBind:F1}" );
			Check( Math.Abs( shadowBindX - shadowPosedX ) + Math.Abs( shadowBindY - shadowPosedY ) > 60,
				"the two shadow probes are not far enough apart to distinguish" );
		} );
	}
}
