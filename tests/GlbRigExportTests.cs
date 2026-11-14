#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

/// <summary>Animated GLB export: bones, skin, morphs and multiple clips, via
/// <see cref="GlbExport.WriteAnimated"/> and through <see cref="ExportService"/>.
/// GlbImport refuses skins by design, so these assertions use the test's own
/// independent GLB reader (JsonDocument + raw BIN slice), never the writer's
/// own structures. See <see cref="GlbInterchangeTests"/> for the static contract.</summary>
internal static class GlbRigExportTests
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

		// ---- Independent GLB parse for assertions (JsonDocument + raw BIN slice) -----
		(JsonDocument Json, byte[] Bin) ParseGlb( byte[] glb )
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
			return (json, bin);
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

		(byte[] Bytes, int Count, int Components) AccessorBytes( JsonElement root, byte[] bin, int accessorIndex )
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
			return (bytes, count, components);
		}

		float[] ReadFloats( JsonElement root, byte[] bin, int accessorIndex )
		{
			var (bytes, count, components) = AccessorBytes( root, bin, accessorIndex );
			var values = new float[count * components];
			Buffer.BlockCopy( bytes, 0, values, 0, values.Length * 4 );
			return values;
		}

		ushort[] ReadUshorts( JsonElement root, byte[] bin, int accessorIndex )
		{
			var (bytes, count, components) = AccessorBytes( root, bin, accessorIndex );
			var values = new ushort[count * components];
			Buffer.BlockCopy( bytes, 0, values, 0, values.Length * 2 );
			return values;
		}

		// ---- Rigged triangle fixture -------------------------------------------------
		// Root bone at the origin, arm bone 5 in up the Y axis; a triangle whose first
		// vertex rides the root and the other two ride the arm. Two clips: "wave" moves
		// the arm +10 in X over 30 frames @30fps, "nod" is a second named clip; the
		// mesh also carries a "Smile" morph (+2 in Y on vertex 2) keyed 0→1 by a morph
		// track (its weight lives in the key's RigPose.Scale.x).
		(Skeleton Rig, PolyMesh Mesh, RigAnimFile Wave, RigAnimFile Nod) RiggedTriangle()
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
			mesh.Morphs.Add( new MeshMorph { Name = "Smile", Deltas = new[] { new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 0 ), new Vec3( 0, 2, 0 ) } } );

			var wave = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var armTrack = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			armTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			armTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 10, 5, 0 ), Quat.Identity, Vec3.One ) } );
			wave.Tracks.Add( armTrack );
			var morphTrack = new RigTrack { Name = "Smile", Kind = RigTrackKind.Morph };
			morphTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( Vec3.Zero, Quat.Identity, new Vec3( 0f, 1f, 1f ) ) } );
			morphTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( Vec3.Zero, Quat.Identity, new Vec3( 1f, 1f, 1f ) ) } );
			wave.Tracks.Add( morphTrack );

			var nod = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var nodTrack = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			nodTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( Vec3.Zero, Quat.Identity, Vec3.One ) } );
			nodTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 0, 2 ), Quat.Identity, Vec3.One ) } );
			nod.Tracks.Add( nodTrack );

			return (rig, mesh, wave, nod);
		}

		GlbMaterial PlainMaterial( string name ) =>
			new( name, new Vec4( 1f, 1f, 1f, 1f ), 0f, 1f, new Vec3( 0f, 0f, 0f ), null, null, null, null );

		registry.Add( "glb", "animated GLB writes bone nodes, one skin, joints/weights, morph targets and two named animations", () =>
		{
			var (rig, mesh, wave, nod) = RiggedTriangle();
			byte[] glb = GlbExport.WriteAnimated(
				new[] { new GlbPart( "tri", mesh, PlainMaterial( "mat" ) ) },
				rig,
				new[] { new GlbClip( "wave", wave ), new GlbClip( "nod", nod ) } );
			var (json, bin) = ParseGlb( glb );
			var root = json.RootElement;

			// Bone nodes first: root (with arm as its child), then arm, then the mesh node.
			var nodes = root.GetProperty( "nodes" );
			Assert( nodes.GetArrayLength() == 3, $"Expected 3 nodes, got {nodes.GetArrayLength()}" );
			Assert( nodes[0].GetProperty( "name" ).GetString() == "root", "Node 0 must be the root bone" );
			Assert( nodes[1].GetProperty( "name" ).GetString() == "arm", "Node 1 must be the arm bone" );
			Assert( nodes[2].GetProperty( "name" ).GetString() == "tri", "Node 2 must be the mesh node" );
			Assert( nodes[0].GetProperty( "children" )[0].GetInt32() == 1, "Root bone must list the arm as its child" );
			// Arm bind pose: kernel (0,5,0) → glTF G (0,0,-5) × 0.0254 = (0,0,-0.127).
			var armTranslation = nodes[1].GetProperty( "translation" );
			AssertNear( armTranslation[0].GetSingle(), 0f, 1e-6f, "Arm translation x" );
			AssertNear( armTranslation[1].GetSingle(), 0f, 1e-6f, "Arm translation y" );
			AssertNear( armTranslation[2].GetSingle(), -0.127f, 1e-6f, "Arm translation z" );
			Assert( nodes[2].GetProperty( "skin" ).GetInt32() == 0, "Mesh node must reference skin 0" );
			// Scene roots are the bone roots, then the mesh node — the mesh is not a root.
			var sceneNodes = root.GetProperty( "scenes" )[0].GetProperty( "nodes" );
			Assert( sceneNodes.GetArrayLength() == 2, $"Scene must list 2 root nodes, got {sceneNodes.GetArrayLength()}" );
			Assert( sceneNodes[0].GetInt32() == 0 && sceneNodes[1].GetInt32() == 2, "Scene roots must be the root bone and the mesh node" );

			// One shared skin: joints [0,1], skeleton = root, IBM count 2.
			var skins = root.GetProperty( "skins" );
			Assert( skins.GetArrayLength() == 1, "Animated GLB must carry exactly one skin" );
			var skin = skins[0];
			Assert( skin.GetProperty( "joints" ).GetArrayLength() == 2, "Skin must list 2 joints" );
			Assert( skin.GetProperty( "joints" )[0].GetInt32() == 0 && skin.GetProperty( "joints" )[1].GetInt32() == 1, "Skin joints must be 0 and 1" );
			Assert( skin.GetProperty( "skeleton" ).GetInt32() == 0, "Skin skeleton must be the root bone" );
			var ibm = ReadFloats( root, bin, skin.GetProperty( "inverseBindMatrices" ).GetInt32() );
			Assert( ibm.Length == 32, $"IBM must hold 2 MAT4s (32 floats), got {ibm.Length}" );
			// Root bind is identity, so IBM[0] is identity; the arm's world bind is a
			// (0,0,-0.127) translation in glTF, so IBM[1] translates back +0.127 in Z.
			AssertNear( ibm[0], 1f, 1e-6f, "IBM[0] m00" );
			AssertNear( ibm[5], 1f, 1e-6f, "IBM[0] m11" );
			AssertNear( ibm[10], 1f, 1e-6f, "IBM[0] m22" );
			AssertNear( ibm[15], 1f, 1e-6f, "IBM[0] m33" );
			AssertNear( ibm[16 + 12], 0f, 1e-6f, "IBM[1] translation x" );
			AssertNear( ibm[16 + 13], 0f, 1e-6f, "IBM[1] translation y" );
			AssertNear( ibm[16 + 14], 0.127f, 1e-6f, "IBM[1] translation z" );

			// Skin attributes: one VEC4 per corner (3 corners, one per vertex here).
			var primitive = root.GetProperty( "meshes" )[0].GetProperty( "primitives" )[0];
			var attributes = primitive.GetProperty( "attributes" );
			var joints = ReadUshorts( root, bin, attributes.GetProperty( "JOINTS_0" ).GetInt32() );
			var weights = ReadFloats( root, bin, attributes.GetProperty( "WEIGHTS_0" ).GetInt32() );
			Assert( joints.Length == 12 && weights.Length == 12, $"JOINTS_0/WEIGHTS_0 must hold 3 VEC4s, got {joints.Length}/{weights.Length}" );
			Assert( joints[0] == 0 && joints[4] == 1 && joints[8] == 1, "Joints must be root, arm, arm" );
			AssertNear( weights[0], 1f, 1e-6f, "Root vertex weight" );
			AssertNear( weights[4], 1f, 1e-6f, "Arm vertex weight" );
			AssertNear( weights[8], 1f, 1e-6f, "Arm vertex weight" );
			for ( int i = 1; i < 4; i++ ) AssertNear( weights[i], 0f, 1e-6f, $"Root vertex padding weight {i}" );

			// Morph targets: POSITION delta for the Smile key, targetNames, weights [0].
			var targets = primitive.GetProperty( "targets" );
			Assert( targets.GetArrayLength() == 1, "Mesh must carry exactly one morph target" );
			var targetPos = ReadFloats( root, bin, targets[0].GetProperty( "POSITION" ).GetInt32() );
			Assert( targetPos.Length == 9, $"Target POSITION must hold 3 VEC3s, got {targetPos.Length}" );
			// Vertex 2 moves +2 in kernel Y → glTF G (0,0,-2) × 0.0254 = (0,0,-0.0508).
			AssertNear( targetPos[6], 0f, 1e-6f, "Smile delta v2 x" );
			AssertNear( targetPos[7], 0f, 1e-6f, "Smile delta v2 y" );
			AssertNear( targetPos[8], -0.0508f, 1e-6f, "Smile delta v2 z" );
			var meshJson = root.GetProperty( "meshes" )[0];
			Assert( meshJson.GetProperty( "weights" )[0].GetSingle() == 0f, "Mesh weights must start at 0" );
			Assert( meshJson.GetProperty( "extras" ).GetProperty( "targetNames" )[0].GetString() == "Smile", "extras.targetNames must name the Smile morph" );

			// Two animations, in clip order, with input times 0..1 s @30fps.
			var animations = root.GetProperty( "animations" );
			Assert( animations.GetArrayLength() == 2, $"Expected 2 animations, got {animations.GetArrayLength()}" );
			Assert( animations[0].GetProperty( "name" ).GetString() == "wave", "First animation must be 'wave'" );
			Assert( animations[1].GetProperty( "name" ).GetString() == "nod", "Second animation must be 'nod'" );
			var waveAnim = animations[0];
			var samplers = waveAnim.GetProperty( "samplers" );
			var channels = waveAnim.GetProperty( "channels" );
			// 2 bones × 3 TRS channels + 1 morph weights channel = 7.
			Assert( samplers.GetArrayLength() == 7, $"Wave must have 7 samplers, got {samplers.GetArrayLength()}" );
			Assert( channels.GetArrayLength() == 7, $"Wave must have 7 channels, got {channels.GetArrayLength()}" );
			int inputAccessor = samplers[0].GetProperty( "input" ).GetInt32();
			var times = ReadFloats( root, bin, inputAccessor );
			Assert( times.Length == 31, $"Input must hold 31 times (frames 0..30), got {times.Length}" );
			AssertNear( times[0], 0f, 1e-6f, "First input time" );
			AssertNear( times[15], 0.5f, 1e-6f, "Middle input time" );
			AssertNear( times[30], 1f, 1e-6f, "Last input time" );

			// Arm translation channel vs the kernel sampler: first, middle, last frames.
			// Node 1 (arm), path translation — find its channel.
			int armTranslationSampler = -1;
			foreach ( var channel in channels.EnumerateArray() )
			{
				var target = channel.GetProperty( "target" );
				if ( target.GetProperty( "node" ).GetInt32() == 1 && target.GetProperty( "path" ).GetString() == "translation" )
					armTranslationSampler = channel.GetProperty( "sampler" ).GetInt32();
			}
			Assert( armTranslationSampler >= 0, "Wave must animate the arm's translation" );
			int armTranslationOutput = samplers[armTranslationSampler].GetProperty( "output" ).GetInt32();
			var armTranslations = ReadFloats( root, bin, armTranslationOutput );
			Assert( armTranslations.Length == 93, $"Arm translation output must hold 31 VEC3s, got {armTranslations.Length}" );
			for ( int f = 0; f <= 30; f += 15 )
			{
				var expected = RigPose.FromXform( wave.SampleLocal( rig, f )[1] );
				var tp = new Vec3( expected.Position.x, expected.Position.z, -expected.Position.y ) * 0.0254f;
				AssertNear( armTranslations[f * 3], tp.x, 1e-5f, $"Arm translation frame {f} x" );
				AssertNear( armTranslations[f * 3 + 1], tp.y, 1e-5f, $"Arm translation frame {f} y" );
				AssertNear( armTranslations[f * 3 + 2], tp.z, 1e-5f, $"Arm translation frame {f} z" );
			}
			// Literal ends: frame 0 is the bind (0,0,-0.127); frame 30 moved +10 in X.
			AssertNear( armTranslations[0], 0f, 1e-6f, "Arm translation frame 0 x" );
			AssertNear( armTranslations[90], 0.254f, 1e-6f, "Arm translation frame 30 x" );
			AssertNear( armTranslations[92], -0.127f, 1e-6f, "Arm translation frame 30 z" );

			// Morph weights channel: 0 at frame 0, 0.5 mid-way, 1 at frame 30.
			int morphSampler = -1;
			foreach ( var channel in channels.EnumerateArray() )
			{
				var target = channel.GetProperty( "target" );
				if ( target.GetProperty( "node" ).GetInt32() == 2 && target.GetProperty( "path" ).GetString() == "weights" )
					morphSampler = channel.GetProperty( "sampler" ).GetInt32();
			}
			Assert( morphSampler >= 0, "Wave must animate the mesh node's morph weights" );
			int morphOutput = samplers[morphSampler].GetProperty( "output" ).GetInt32();
			var morphWeights = ReadFloats( root, bin, morphOutput );
			Assert( morphWeights.Length == 31, $"Morph weights output must hold 31 samples, got {morphWeights.Length}" );
			AssertNear( morphWeights[0], 0f, 1e-6f, "Morph weight frame 0" );
			AssertNear( morphWeights[15], 0.5f, 1e-5f, "Morph weight frame 15" );
			AssertNear( morphWeights[30], 1f, 1e-6f, "Morph weight frame 30" );
		} );

		registry.Add( "glb", "unweighted vertex binds to joint 0, top-4 weights renormalise, bad skin is refused", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "a", 0, Xform.Identity );
			rig.AddBone( "b", 0, Xform.Identity );
			rig.AddBone( "c", 0, Xform.Identity );
			rig.AddBone( "d", 0, Xform.Identity );
			rig.AddBone( "e", 0, Xform.Identity );

			// Quad: vertex 0 has no weights at all; vertex 1 has five (top 4 win);
			// vertices 2 and 3 ride single bones.
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
			} );
			mesh.Skin = new SkinWeights( 4 );
			mesh.Skin[0] = Array.Empty<BoneWeight>();
			mesh.Skin[1] = new[]
			{
				new BoneWeight( 1, 0.5f ), new BoneWeight( 2, 0.3f ), new BoneWeight( 3, 0.1f ),
				new BoneWeight( 4, 0.05f ), new BoneWeight( 5, 0.05f ),
			};
			mesh.Skin[2] = new[] { new BoneWeight( 2, 1f ) };
			mesh.Skin[3] = new[] { new BoneWeight( 3, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) }, material: 0 );
			mesh.AddFace( new[] { 0, 2, 3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, material: 0 );

			byte[] glb = GlbExport.WriteAnimated(
				new[] { new GlbPart( "quad", mesh, PlainMaterial( "mat" ) ) }, rig, null );
			var (json, bin) = ParseGlb( glb );
			var root = json.RootElement;
			var attributes = root.GetProperty( "meshes" )[0].GetProperty( "primitives" )[0].GetProperty( "attributes" );
			var joints = ReadUshorts( root, bin, attributes.GetProperty( "JOINTS_0" ).GetInt32() );
			var weights = ReadFloats( root, bin, attributes.GetProperty( "WEIGHTS_0" ).GetInt32() );
			// Six corners: (0,1,2) then (0,2,3). Corner 0 is the unweighted vertex.
			Assert( joints.Length == 24 && weights.Length == 24, $"Expected 6 VEC4 joints/weights, got {joints.Length}/{weights.Length}" );
			Assert( joints[0] == 0 && weights[0] == 1f, "Unweighted vertex must bind to joint 0 at weight 1" );
			// Corner 1 (vertex 1): top 4 of (0.5, 0.3, 0.1, 0.05, 0.05) renormalised
			// by their sum 0.95 — the fifth (bone 5) is dropped.
			Assert( joints[4] == 1 && joints[5] == 2 && joints[6] == 3 && joints[7] == 4, "Top-4 joints must be bones 1, 2, 3, 4" );
			AssertNear( weights[4], 0.5f / 0.95f, 1e-6f, "Top weight 0.5/0.95" );
			AssertNear( weights[5], 0.3f / 0.95f, 1e-6f, "Top weight 0.3/0.95" );
			AssertNear( weights[6], 0.1f / 0.95f, 1e-6f, "Top weight 0.1/0.95" );
			AssertNear( weights[7], 0.05f / 0.95f, 1e-6f, "Top weight 0.05/0.95" );
			AssertNear( weights[4] + weights[5] + weights[6] + weights[7], 1f, 1e-6f, "Renormalised weights must sum to 1" );

			// A rigged part with no skeleton is refused, not silently unskinned.
			AssertThrows<InvalidOperationException>( () =>
				GlbExport.WriteAnimated( new[] { new GlbPart( "quad", mesh, PlainMaterial( "mat" ) ) }, null, null ),
				"Rigged part with no skeleton must be refused" );
			// A bone index outside the skeleton is corrupt skin, not a silent drop.
			mesh.Skin[2] = new[] { new BoneWeight( 99, 1f ) };
			AssertThrows<InvalidOperationException>( () =>
				GlbExport.WriteAnimated( new[] { new GlbPart( "quad", mesh, PlainMaterial( "mat" ) ) }, rig, null ),
				"Out-of-range bone index must be refused" );
			// A skin row count that does not match the vertex count is refused too.
			mesh.Skin = new SkinWeights( 2 );
			AssertThrows<InvalidOperationException>( () =>
				GlbExport.WriteAnimated( new[] { new GlbPart( "quad", mesh, PlainMaterial( "mat" ) ) }, rig, null ),
				"Mismatched skin row count must be refused" );
		} );

		registry.Add( "glb", "clip names dedupe: blank becomes clip, duplicates gain suffixes", () =>
		{
			var (rig, mesh, wave, nod) = RiggedTriangle();
			var empty = new RigAnimFile { FrameRate = 30, FrameCount = 1 };
			byte[] glb = GlbExport.WriteAnimated(
				new[] { new GlbPart( "tri", mesh, PlainMaterial( "mat" ) ) },
				rig,
				new[] { new GlbClip( "", wave ), new GlbClip( "wave", nod ), new GlbClip( "wave", empty ) } );
			var (json, _) = ParseGlb( glb );
			var animations = json.RootElement.GetProperty( "animations" );
			Assert( animations.GetArrayLength() == 3, $"Expected 3 animations, got {animations.GetArrayLength()}" );
			Assert( animations[0].GetProperty( "name" ).GetString() == "clip", "Blank clip name must become 'clip'" );
			Assert( animations[1].GetProperty( "name" ).GetString() == "wave", "First 'wave' must stay 'wave'" );
			Assert( animations[2].GetProperty( "name" ).GetString() == "wave 2", "Second 'wave' must become 'wave 2'" );
		} );

		registry.Add( "glb", "static Write stays static: no skins or animations, morph targets carried, rigged mesh unskinned", () =>
		{
			var (_, mesh, _, _) = RiggedTriangle();
			byte[] glb = GlbExport.Write( new[] { new GlbPart( "tri", mesh, PlainMaterial( "mat" ) ) } );
			var (json, bin) = ParseGlb( glb );
			var root = json.RootElement;
			Assert( !root.TryGetProperty( "skins", out _ ), "Static Write must not write skins" );
			Assert( !root.TryGetProperty( "animations", out _ ), "Static Write must not write animations" );
			var primitive = root.GetProperty( "meshes" )[0].GetProperty( "primitives" )[0];
			Assert( !primitive.GetProperty( "attributes" ).TryGetProperty( "JOINTS_0", out _ ), "Static Write must not write JOINTS_0" );
			// Morph targets are static geometry data, so they ARE written (weights 0).
			Assert( primitive.TryGetProperty( "targets", out var targets ) && targets.GetArrayLength() == 1, "Static Write must still carry morph targets" );
			Assert( root.GetProperty( "meshes" )[0].GetProperty( "extras" ).GetProperty( "targetNames" )[0].GetString() == "Smile", "Static targetNames must name the Smile morph" );
			_ = bin;
		} );

		registry.Add( "glb", "ExportService end to end: Studio rig + BodyBoneMap + clip exports animated GLB with preserved rig, skin and animation", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_rig_export_e2e" );
			Directory.CreateDirectory( testDir );

			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 6f }, SizeZ = { Value = 4f } } );
			studio.Rebuild();
			Assert( studio.Bodies.Count == 1, $"Expected 1 body, got {studio.Bodies.Count}" );
			studio.Rig = rig;
			studio.BodyBoneMap[studio.Bodies[0].Id] = "root";

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( Vec3.Zero, Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 5, 0, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			string effigyPath = Path.Combine( testDir, "rigged.effigy" );
			var session = DocumentSession.CreateEffigy( effigyPath, studio, clip: clip );
			var service = new ExportService();

			var request = new ExportRequest( session, ExportFormat.Glb, Path.Combine( testDir, "rigged.glb" ) );
			var report = service.EvaluateCapabilities( request );
			Assert( report.CanExport, $"Capability report must allow the export: {report.ErrorMessage}" );
			Assert( report.PreservesRig, "Report must preserve the rig" );
			Assert( report.PreservesSkinning, "Report must preserve skinning" );
			Assert( report.PreservesAnimation, "Report must preserve animation" );
			Assert( !report.LostFeatures.Any( f => f.Contains( "skin", StringComparison.OrdinalIgnoreCase ) ), "Report must not list skinning as lost" );

			var result = service.Export( request );
			Assert( result.Success, $"Animated GLB export failed: {result.ErrorMessage}" );
			var (json, bin) = ParseGlb( File.ReadAllBytes( Path.Combine( testDir, "rigged.glb" ) ) );
			var root = json.RootElement;
			Assert( root.TryGetProperty( "skins", out var skins ) && skins.GetArrayLength() == 1, "Exported GLB must carry one skin" );
			Assert( root.TryGetProperty( "animations", out var animations ) && animations.GetArrayLength() == 1, "Exported GLB must carry one animation" );
			// The clip came from memory (no ClipPath), so its name is "clip".
			Assert( animations[0].GetProperty( "name" ).GetString() == "clip", "Animation must be named 'clip' when the session clip has no path" );
			// The merged export-grade skin binds the whole box to the root bone.
			var attributes = root.GetProperty( "meshes" )[0].GetProperty( "primitives" )[0].GetProperty( "attributes" );
			var joints = ReadUshorts( root, bin, attributes.GetProperty( "JOINTS_0" ).GetInt32() );
			var weights = ReadFloats( root, bin, attributes.GetProperty( "WEIGHTS_0" ).GetInt32() );
			Assert( joints.Length > 0 && joints.Length == weights.Length, "JOINTS_0 and WEIGHTS_0 must be present and equal length" );
			for ( int i = 0; i < joints.Length; i += 4 )
			{
				Assert( joints[i] == 0, $"Corner {i / 4} must bind to joint 0, got {joints[i]}" );
				AssertNear( weights[i], 1f, 1e-6f, $"Corner {i / 4} weight" );
			}
		} );

		registry.Add( "glb", "ExportService end to end: FBX-source weights resolve and export animated GLB", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_fbx_rig_export_e2e" );
			Directory.CreateDirectory( testDir );

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
			// No Skin on the mesh itself: the weights come from the FBX result and
			// must be resolved by the export.
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) }, material: 0 );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 10, 5, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			var fbxResult = new FbxImport.Result { Skeleton = rig };
			fbxResult.Pieces.Add( new ObjReader.ObjPiece( "piece0", mesh ) );
			fbxResult.Weights.Add( new List<(string Bone, float Weight)>[]
			{
				new List<(string, float)> { ("root", 1f) },
				new List<(string, float)> { ("arm", 1f) },
				new List<(string, float)> { ("arm", 1f) },
			} );
			string fbxPath = Path.Combine( testDir, "rigged_source.fbx" );
			var session = DocumentSession.CreateFbx( fbxPath, fbxResult, clip: clip );

			var service = new ExportService();
			string glbPath = Path.Combine( testDir, "rigged.glb" );
			var result = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( result.Success, $"FBX animated GLB export failed: {result.ErrorMessage}" );
			var (json, bin) = ParseGlb( File.ReadAllBytes( glbPath ) );
			var root = json.RootElement;
			Assert( root.TryGetProperty( "skins", out _ ), "FBX export must carry a skin" );
			Assert( root.TryGetProperty( "animations", out var animations ) && animations.GetArrayLength() == 1, "FBX export must carry one animation" );
			var attributes = root.GetProperty( "meshes" )[0].GetProperty( "primitives" )[0].GetProperty( "attributes" );
			var joints = ReadUshorts( root, bin, attributes.GetProperty( "JOINTS_0" ).GetInt32() );
			Assert( joints[0] == 0 && joints[4] == 1 && joints[8] == 1, "FBX joints must be root, arm, arm" );
		} );

		registry.Add( "glb", "multi-slot split keeps skin and morphs on each slot", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_split_skin_morph" );
			Directory.CreateDirectory( testDir );

			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 6f }, SizeZ = { Value = 4f } } );
			studio.Rebuild();
			Assert( studio.Bodies.Count == 1, $"Expected 1 body, got {studio.Bodies.Count}" );
			var bodyMesh = studio.Bodies[0].Mesh;
			// Two material slots on one body: the first half of the faces stay on
			// slot 0, the rest move to slot 1.
			for ( int fi = bodyMesh.Faces.Count / 2; fi < bodyMesh.Faces.Count; fi++ )
				bodyMesh.Faces[fi].Material = 1;
			// A morph that moves only vertex 0 (+2 in X).
			var target = bodyMesh.Positions.ToList();
			target[0] = target[0] + new Vec3( 2, 0, 0 );
			bodyMesh.AddMorph( "Nudge", target );
			studio.Rig = rig;
			studio.BodyBoneMap[studio.Bodies[0].Id] = "root";

			string effigyPath = Path.Combine( testDir, "split.effigy" );
			var session = DocumentSession.CreateEffigy( effigyPath, studio );
			var service = new ExportService();
			string glbPath = Path.Combine( testDir, "split.glb" );
			var result = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( result.Success, $"Split GLB export failed: {result.ErrorMessage}" );
			var (json, bin) = ParseGlb( File.ReadAllBytes( glbPath ) );
			var root = json.RootElement;
			var meshes = root.GetProperty( "meshes" );
			Assert( meshes.GetArrayLength() == 2, $"Expected 2 meshes (one per slot), got {meshes.GetArrayLength()}" );
			Assert( root.TryGetProperty( "skins", out _ ), "Split export must carry a skin" );
			// Both slots stay rigged, and the slot holding vertex 0 keeps the morph.
			bool anyMorph = false;
			for ( int mi = 0; mi < 2; mi++ )
			{
				var attributes = meshes[mi].GetProperty( "primitives" )[0].GetProperty( "attributes" );
				Assert( attributes.TryGetProperty( "JOINTS_0", out _ ), $"Mesh {mi} must carry JOINTS_0" );
				Assert( attributes.TryGetProperty( "WEIGHTS_0", out _ ), $"Mesh {mi} must carry WEIGHTS_0" );
				if ( meshes[mi].GetProperty( "primitives" )[0].TryGetProperty( "targets", out var targets ) && targets.GetArrayLength() == 1 )
				{
					anyMorph = true;
					Assert( meshes[mi].GetProperty( "extras" ).GetProperty( "targetNames" )[0].GetString() == "Nudge", "Split morph must keep its name" );
				}
			}
			Assert( anyMorph, "Exactly the slot holding vertex 0 must keep the Nudge morph target" );
			_ = bin;
		} );
	}
}
