#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Effigy;
using Marionette.Desktop;

internal static class GlbInterchangeTests
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

		// ---- Minimal hand-built GLB fixture (independent of GlbExport) ----------------
		// One triangle, positions in metres, optional UVs, one node named "tri". The
		// `mutate` hook lets refusal cases twist one thing each; `version` and `indices`
		// cover the container-level cases. Nothing here calls GlbExport or GlbImport.
		byte[] MinimalGlb( Action<JsonObject>? mutate = null, uint version = 2, ushort[]? indices = null, bool withUv = false )
		{
			indices ??= new ushort[] { 0, 1, 2 };
			var positions = new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
			var uvs = new float[] { 0f, 0f, 1f, 0f, 0f, 1f };
			using var bin = new MemoryStream();
			using ( var writer = new BinaryWriter( bin, Encoding.UTF8, leaveOpen: true ) )
			{
				foreach ( float v in positions ) writer.Write( v );
				foreach ( ushort i in indices ) writer.Write( i );
				if ( withUv ) foreach ( float v in uvs ) writer.Write( v );
			}
			byte[] binBytes = bin.ToArray();
			int posBytes = positions.Length * 4;
			int idxBytes = indices.Length * 2;

			var attributes = new JsonObject { ["POSITION"] = 0 };
			if ( withUv ) attributes["TEXCOORD_0"] = 2;
			var accessors = new JsonArray(
				new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3" },
				new JsonObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = indices.Length, ["type"] = "SCALAR" } );
			var bufferViews = new JsonArray(
				new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = posBytes },
				new JsonObject { ["buffer"] = 0, ["byteOffset"] = posBytes, ["byteLength"] = idxBytes } );
			if ( withUv )
			{
				accessors.Add( new JsonObject { ["bufferView"] = 2, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC2" } );
				bufferViews.Add( new JsonObject { ["buffer"] = 0, ["byteOffset"] = posBytes + idxBytes, ["byteLength"] = uvs.Length * 4 } );
			}
			var root = new JsonObject
			{
				["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "GlbInterchangeTests" },
				["scene"] = 0,
				["scenes"] = new JsonArray( new JsonObject { ["nodes"] = new JsonArray( 0 ) } ),
				["nodes"] = new JsonArray( new JsonObject { ["name"] = "tri", ["mesh"] = 0 } ),
				["meshes"] = new JsonArray( new JsonObject
				{
					["name"] = "tri_mesh",
					["primitives"] = new JsonArray( new JsonObject
					{
						["attributes"] = attributes,
						["indices"] = 1,
						["mode"] = 4,
					} ),
				} ),
				["accessors"] = accessors,
				["bufferViews"] = bufferViews,
				["buffers"] = new JsonArray( new JsonObject { ["byteLength"] = binBytes.Length } ),
			};
			mutate?.Invoke( root );

			byte[] jsonBytes = Encoding.UTF8.GetBytes( root.ToJsonString() );
			using var output = new MemoryStream();
			using ( var writer = new BinaryWriter( output, Encoding.UTF8, leaveOpen: true ) )
			{
				int jsonPadded = ( jsonBytes.Length + 3 ) & ~3;
				int binPadded = ( binBytes.Length + 3 ) & ~3;
				writer.Write( 0x46546C67u );
				writer.Write( version );
				writer.Write( (uint)( 12 + 8 + jsonPadded + 8 + binPadded ) );
				writer.Write( (uint)jsonPadded );
				writer.Write( 0x4E4F534Au );
				writer.Write( jsonBytes );
				for ( int i = jsonBytes.Length; i < jsonPadded; i++ ) writer.Write( (byte)' ' );
				writer.Write( (uint)binPadded );
				writer.Write( 0x004E4942u );
				writer.Write( binBytes );
				for ( int i = binBytes.Length; i < binPadded; i++ ) writer.Write( (byte)0 );
			}
			return output.ToArray();
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

		byte[] ImageBytes( JsonElement glbRoot, byte[] bin, int imageIndex )
		{
			var image = glbRoot.GetProperty( "images" )[imageIndex];
			var view = glbRoot.GetProperty( "bufferViews" )[image.GetProperty( "bufferView" ).GetInt32()];
			int offset = view.GetProperty( "byteOffset" ).GetInt32();
			int length = view.GetProperty( "byteLength" ).GetInt32();
			var bytes = new byte[length];
			Array.Copy( bin, offset, bytes, 0, length );
			return bytes;
		}

		byte[] TextureImageBytes( JsonElement glbRoot, byte[] bin, JsonElement material, string textureProperty )
		{
			var pbr = material.GetProperty( "pbrMetallicRoughness" );
			int textureIndex = pbr.GetProperty( textureProperty ).GetProperty( "index" ).GetInt32();
			int imageIndex = glbRoot.GetProperty( "textures" )[textureIndex].GetProperty( "source" ).GetInt32();
			return ImageBytes( glbRoot, bin, imageIndex );
		}

		byte[] FlipGreen( byte[] rgba )
		{
			var flipped = (byte[])rgba.Clone();
			for ( int i = 0; i + 1 < flipped.Length; i += 4 ) flipped[i + 1] = (byte)( 255 - flipped[i + 1] );
			return flipped;
		}

		byte[] PackMetallicRoughness( byte[] roughRgba, byte[] metalRgba )
		{
			Assert( roughRgba.Length == metalRgba.Length, "rough/metal maps must share a size for the pack check" );
			var packed = new byte[roughRgba.Length];
			for ( int i = 0; i < roughRgba.Length; i += 4 )
			{
				packed[i] = 255;
				packed[i + 1] = roughRgba[i + 1];
				packed[i + 2] = metalRgba[i + 1];
				packed[i + 3] = 255;
			}
			return packed;
		}

		(Vec3 Min, Vec3 Max) BoundsOf( PolyMesh mesh )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			return (min, max);
		}

		PaintCanvas PaintedCanvas( byte r, byte g, byte b, byte rough, byte metal, byte height, byte heightHot )
		{
			var canvas = PaintLayers.Solid( 4, 4, r, g, b );
			canvas.RoughnessMap = PaintLayers.Solid( 4, 4, rough, rough, rough );
			canvas.MetalnessMap = PaintLayers.Solid( 4, 4, metal, metal, metal );
			canvas.HeightMap = PaintLayers.Solid( 4, 4, height, height, height );
			// One hot pixel so the height-to-normal map is not flat: the flip check
			// needs a green channel that is not 128.
			for ( int c = 0; c < 4; c++ ) canvas.HeightMap.Rgba[( 1 * 4 + 1 ) * 4 + c] = heightHot;
			return canvas;
		}

		(DocumentSession Session, PaintCanvas Red, PaintCanvas Blue) TwoPaintedBodies( string workDir )
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 6f }, SizeZ = { Value = 4f } } );
			studio.Add( new PrimitiveFeature { SizeX = { Value = 20f }, SizeY = { Value = 8f }, SizeZ = { Value = 2f } } );
			studio.Rebuild();
			Assert( studio.Bodies.Count == 2, $"Expected 2 bodies, got {studio.Bodies.Count}" );
			studio.Bodies[0].Name = "red_body";
			studio.Bodies[1].Name = "blue_body";
			// Distinct slots per body, so the round-trip exercises two materials that
			// stay distinct (same-named materials would correctly collapse to one
			// slot on import, per the slot rule).
			foreach ( var face in studio.Bodies[1].Mesh.Faces ) face.Material = 1;
			var red = PaintedCanvas( 255, 0, 0, 128, 64, 128, 255 );
			var blue = PaintedCanvas( 0, 0, 255, 200, 192, 64, 0 );
			studio.Bodies[0].Mesh.Paint = red;
			studio.Bodies[1].Mesh.Paint = blue;
			string effigyPath = Path.Combine( workDir, "two_bodies.effigy" );
			return (DocumentSession.CreateEffigy( effigyPath, studio ), red, blue);
		}

		registry.Add( "glb", "minimal hand-built GLB imports one triangle with exact metre-to-inch conversion", () =>
		{
			var result = GlbImport.Read( MinimalGlb() );
			Assert( result.Pieces.Count == 1, $"Expected 1 piece, got {result.Pieces.Count}" );
			var piece = result.Pieces[0];
			Assert( piece.Name == "tri", $"Piece name should be the node name 'tri', got '{piece.Name}'" );
			Assert( piece.Mesh.Positions.Count == 3, $"Expected 3 vertices, got {piece.Mesh.Positions.Count}" );
			const float inchesPerMetre = 39.3700787f;
			AssertNear( piece.Mesh.Positions[0].x, 0f, 1e-4f, "v0.x" );
			AssertNear( piece.Mesh.Positions[1].x, inchesPerMetre, 1e-4f, "1 m along glTF +X must be 39.3701 in along kernel +X" );
			AssertNear( piece.Mesh.Positions[1].y, 0f, 1e-4f, "v1.y" );
			AssertNear( piece.Mesh.Positions[1].z, 0f, 1e-4f, "v1.z" );
			// glTF +Y (up) lands on kernel +Z (up): (0, 1, 0) m → (0, 0, 39.3701) in.
			AssertNear( piece.Mesh.Positions[2].x, 0f, 1e-4f, "v2.x" );
			AssertNear( piece.Mesh.Positions[2].y, 0f, 1e-4f, "v2.y" );
			AssertNear( piece.Mesh.Positions[2].z, inchesPerMetre, 1e-4f, "1 m along glTF +Y must be 39.3701 in along kernel +Z" );
			Assert( result.MaterialSlots.Count == 1 && result.MaterialSlots[0] == "material_0", "Unmaterialed primitive takes the default material_0 slot" );
			Assert( result.Notes.Any( n => n.Contains( "glTF 2.0" ) ), "Import notes must record the glTF format line" );
		} );

		registry.Add( "glb", "two painted bodies export and re-import with identical names, bounds, materials and texture pixels", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_two_painted_bodies" );
			Directory.CreateDirectory( testDir );
			var (session, red, blue) = TwoPaintedBodies( testDir );
			var redBounds = BoundsOf( session.Studio!.Bodies[0].Mesh );
			var blueBounds = BoundsOf( session.Studio!.Bodies[1].Mesh );

			string glbPath = Path.Combine( testDir, "two_bodies.glb" );
			var service = new ExportService();
			var result = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( result.Success, $"GLB export failed: {result.ErrorMessage}" );
			Assert( File.Exists( glbPath ), "Exported .glb file must exist on disk." );

			// Independent parse: no GlbImport involved in these assertions.
			byte[] glbBytes = File.ReadAllBytes( glbPath );
			var (json, bin) = ParseGlb( glbBytes );
			var root = json.RootElement;
			Assert( root.GetProperty( "meshes" ).GetArrayLength() == 2, "GLB must carry 2 meshes" );
			Assert( root.GetProperty( "meshes" )[0].GetProperty( "name" ).GetString() == "red_body", "First mesh must be red_body" );
			Assert( root.GetProperty( "meshes" )[1].GetProperty( "name" ).GetString() == "blue_body", "Second mesh must be blue_body" );
			Assert( root.GetProperty( "nodes" )[0].GetProperty( "name" ).GetString() == "red_body", "First node must be red_body" );
			Assert( root.GetProperty( "nodes" )[1].GetProperty( "name" ).GetString() == "blue_body", "Second node must be blue_body" );

			var materials = root.GetProperty( "materials" );
			Assert( materials.GetArrayLength() == 2, "GLB must carry 2 materials" );
			for ( int mi = 0; mi < 2; mi++ )
			{
				var material = materials[mi];
				var pbr = material.GetProperty( "pbrMetallicRoughness" );
				Assert( pbr.TryGetProperty( "baseColorTexture", out _ ), $"Material {mi} must embed a base-colour texture" );
				Assert( pbr.TryGetProperty( "metallicRoughnessTexture", out _ ), $"Material {mi} must embed a metallic-roughness texture" );
				Assert( material.TryGetProperty( "normalTexture", out _ ), $"Material {mi} must embed a normal texture" );
				// Painted channels take factor 1: the image carries the value.
				var factors = pbr.GetProperty( "baseColorFactor" );
				AssertNear( factors[0].GetSingle(), 1f, 1e-6f, $"Material {mi} baseColorFactor r" );
				AssertNear( pbr.GetProperty( "metallicFactor" ).GetSingle(), 1f, 1e-6f, $"Material {mi} metallicFactor" );
				AssertNear( pbr.GetProperty( "roughnessFactor" ).GetSingle(), 1f, 1e-6f, $"Material {mi} roughnessFactor" );
			}

			// Embedded PNG pixels must equal the source canvases exactly.
			var canvases = new[] { red, blue };
			for ( int mi = 0; mi < 2; mi++ )
			{
				var material = materials[mi];
				var canvas = canvases[mi];
				var (colorRgba, cw, ch) = PngReader.Read( TextureImageBytes( root, bin, material, "baseColorTexture" ) );
				var expectedColor = PaintLayers.Opaque( canvas, PaintChannel.Color );
				Assert( cw == canvas.Width && ch == canvas.Height, $"Material {mi} base-colour size must match the canvas" );
				Assert( colorRgba.SequenceEqual( expectedColor ), $"Material {mi} base-colour pixels must equal the source canvas exactly" );

				var (mrRgba, mw, mh) = PngReader.Read( TextureImageBytes( root, bin, material, "metallicRoughnessTexture" ) );
				var expectedMr = PackMetallicRoughness(
					PaintLayers.Opaque( canvas, PaintChannel.Roughness ),
					PaintLayers.Opaque( canvas, PaintChannel.Metalness ) );
				Assert( mw == canvas.Width && mh == canvas.Height, $"Material {mi} metallic-roughness size must match the canvas" );
				Assert( mrRgba.SequenceEqual( expectedMr ), $"Material {mi} metallic-roughness pixels must be R=255, G=rough, B=metal" );

				var (normalRgba, nw, nh) = PngReader.Read( ImageBytes( root, bin, root.GetProperty( "textures" )[material.GetProperty( "normalTexture" ).GetProperty( "index" ).GetInt32()].GetProperty( "source" ).GetInt32() ) );
				var expectedNormal = FlipGreen( PaintTextureSet.HeightToNormal( canvas.HeightMap! ) );
				Assert( nw == canvas.Width && nh == canvas.Height, $"Material {mi} normal size must match the canvas" );
				Assert( normalRgba.SequenceEqual( expectedNormal ), $"Material {mi} normal pixels must be the G-flipped height-to-normal map" );
			}

			// POSITION bounds: source bounds × 0.0254, through the axis map
			// G(v) = (x, z, -y). glTF min/max are therefore NOT the source min/max
			// per axis: glTF Y comes from kernel Z, and glTF Z is negated kernel Y,
			// so its min comes from the source MAX y.
			var meshes = root.GetProperty( "meshes" );
			var accessors = root.GetProperty( "accessors" );
			var sourceBounds = new[] { redBounds, blueBounds };
			for ( int mi = 0; mi < 2; mi++ )
			{
				int posAccessor = meshes[mi].GetProperty( "primitives" )[0].GetProperty( "attributes" ).GetProperty( "POSITION" ).GetInt32();
				var acc = accessors[posAccessor];
				var min = acc.GetProperty( "min" );
				var max = acc.GetProperty( "max" );
				float[] expectedMin =
				{
					sourceBounds[mi].Min.x * 0.0254f,
					sourceBounds[mi].Min.z * 0.0254f,
					-sourceBounds[mi].Max.y * 0.0254f,
				};
				float[] expectedMax =
				{
					sourceBounds[mi].Max.x * 0.0254f,
					sourceBounds[mi].Max.z * 0.0254f,
					-sourceBounds[mi].Min.y * 0.0254f,
				};
				for ( int axis = 0; axis < 3; axis++ )
				{
					AssertNear( min[axis].GetSingle(), expectedMin[axis], 1e-6f, $"Mesh {mi} POSITION min axis {axis}" );
					AssertNear( max[axis].GetSingle(), expectedMax[axis], 1e-6f, $"Mesh {mi} POSITION max axis {axis}" );
				}
			}

			// Round-trip through the reader: names, bounds, slots, face counts.
			var roundTrip = GlbImport.Read( glbBytes, glbPath );
			Assert( roundTrip.Pieces.Count == 2, $"Round-trip must yield 2 pieces, got {roundTrip.Pieces.Count}" );
			Assert( roundTrip.Pieces[0].Name == "red_body" && roundTrip.Pieces[1].Name == "blue_body", "Round-trip piece names must match the body names" );
			Assert( roundTrip.MaterialSlots.Count == 2, $"Round-trip must yield 2 material slots, got {roundTrip.MaterialSlots.Count}" );
			for ( int pi = 0; pi < 2; pi++ )
			{
				var (min, max) = BoundsOf( roundTrip.Pieces[pi].Mesh );
				var (srcMin, srcMax) = sourceBounds[pi];
				AssertNear( min.x, srcMin.x, 1e-3f, $"Round-trip piece {pi} min.x" );
				AssertNear( min.y, srcMin.y, 1e-3f, $"Round-trip piece {pi} min.y" );
				AssertNear( min.z, srcMin.z, 1e-3f, $"Round-trip piece {pi} min.z" );
				AssertNear( max.x, srcMax.x, 1e-3f, $"Round-trip piece {pi} max.x" );
				AssertNear( max.y, srcMax.y, 1e-3f, $"Round-trip piece {pi} max.y" );
				AssertNear( max.z, srcMax.z, 1e-3f, $"Round-trip piece {pi} max.z" );
			}
		} );

		registry.Add( "glb", "posed GLB export without bake is refused; with bake it exports the posed shape", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_posed_bake" );
			Directory.CreateDirectory( testDir );

			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 0, 5, 0 ) ) );
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( 0, 5, 0 ),
				new Vec3( 2, 5, 0 )
			} );
			mesh.Skin = new SkinWeights( 3 );
			mesh.Skin[0] = new[] { new BoneWeight( 0, 1f ) };
			mesh.Skin[1] = new[] { new BoneWeight( 1, 1f ) };
			mesh.Skin[2] = new[] { new BoneWeight( 1, 1f ) };
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ) }, material: 0 );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var track = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			track.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			track.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 10, 5, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( track );

			var fbxResult = new FbxImport.Result { Skeleton = rig };
			fbxResult.Pieces.Add( new ObjReader.ObjPiece( "piece0", mesh ) );
			fbxResult.Weights.Add( null );
			string fbxPath = Path.Combine( testDir, "rigged_source.fbx" );
			var session = DocumentSession.CreateFbx( fbxPath, fbxResult, clip: clip );

			var service = new ExportService();
			string refusedPath = Path.Combine( testDir, "posed_no_bake.glb" );
			var refused = service.Export( new ExportRequest( session, ExportFormat.Glb, refusedPath, poseMode: PoseMode.Posed, playheadTime: 1.0f ) );
			Assert( !refused.Success, "Posed GLB export without BakePosedMesh must be refused" );
			Assert( ( refused.ErrorMessage ?? "" ).Contains( "BakePosedMesh" ), $"Refusal must name BakePosedMesh, got: {refused.ErrorMessage}" );
			Assert( !File.Exists( refusedPath ), "Refused export must not write a file" );

			string bakedPath = Path.Combine( testDir, "posed_baked.glb" );
			var baked = service.Export( new ExportRequest( session, ExportFormat.Glb, bakedPath, poseMode: PoseMode.Posed, playheadTime: 1.0f, bakePosedMesh: true ) );
			Assert( baked.Success, $"Posed GLB export with bake failed: {baked.ErrorMessage}" );
			var roundTrip = GlbImport.Read( File.ReadAllBytes( bakedPath ), bakedPath );
			Assert( roundTrip.Pieces.Count == 1, "Baked export must yield 1 piece" );
			// The arm-bound vertex (2, 5, 0) moves +10 in X at frame 30; the baked static
			// geometry must carry that, and no skin may survive the round-trip.
			bool moved = roundTrip.Pieces[0].Mesh.Positions.Any( p => MathF.Abs( p.x - 12f ) < 1e-3f && MathF.Abs( p.y - 5f ) < 1e-3f );
			Assert( moved, "Baked GLB must contain the posed vertex at (12, 5, 0)" );
			Assert( roundTrip.Weights.All( w => w is null ), "Baked GLB round-trip must carry no skinning weights" );
			Assert( roundTrip.Skeleton is null, "Baked GLB round-trip must carry no skeleton" );
		} );

		registry.Add( "glb", "import refuses Draco, skins, bad magic, truncated data, version 1, non-triangle mode, out-of-range index and sparse accessors", () =>
		{
			AssertThrows<NotSupportedException>( () => GlbImport.Read( MinimalGlb( root =>
			{
				root["extensionsRequired"] = new JsonArray( "KHR_draco_mesh_compression" );
				root["extensionsUsed"] = new JsonArray( "KHR_draco_mesh_compression" );
			} ) ), "Draco extension must be refused" );

			AssertThrows<NotSupportedException>( () => GlbImport.Read( MinimalGlb( root =>
			{
				root["skins"] = new JsonArray( new JsonObject { ["joints"] = new JsonArray( 0 ) } );
			} ) ), "Skinned file must be refused" );

			AssertThrows<InvalidDataException>( () => GlbImport.Read( Encoding.UTF8.GetBytes( "this is not a gltf file" ) ), "Non-JSON, non-GLB bytes must be refused" );

			byte[] valid = MinimalGlb();
			byte[] truncated = new byte[valid.Length / 2];
			Array.Copy( valid, truncated, truncated.Length );
			AssertThrows<InvalidDataException>( () => GlbImport.Read( truncated ), "Truncated GLB must be refused" );

			AssertThrows<NotSupportedException>( () => GlbImport.Read( MinimalGlb( version: 1 ) ), "glTF version 1 must be refused" );

			AssertThrows<NotSupportedException>( () => GlbImport.Read( MinimalGlb( root =>
			{
				root["meshes"]!.AsArray()[0]!.AsObject()["primitives"]!.AsArray()[0]!.AsObject()["mode"] = 1;
			} ) ), "Non-triangle primitive mode must be refused" );

			AssertThrows<InvalidDataException>( () => GlbImport.Read( MinimalGlb( indices: new ushort[] { 0, 1, 99 } ) ), "Out-of-range index must be refused" );

			AssertThrows<NotSupportedException>( () => GlbImport.Read( MinimalGlb( root =>
			{
				root["accessors"]!.AsArray()[0]!.AsObject()["sparse"] = new JsonObject
				{
					["count"] = 1,
					["indices"] = new JsonObject { ["bufferView"] = 0, ["componentType"] = 5123 },
					["values"] = new JsonObject { ["bufferView"] = 0 },
				};
			} ) ), "Sparse accessor must be refused" );
		} );

		registry.Add( "glb", "DocumentLoader loads a .glb end to end", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_loader_e2e" );
			Directory.CreateDirectory( testDir );
			var (session, _, _) = TwoPaintedBodies( testDir );
			string glbPath = Path.Combine( testDir, "loader_input.glb" );
			var service = new ExportService();
			var exported = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( exported.Success, $"Fixture export failed: {exported.ErrorMessage}" );

			var loader = new DocumentLoader();
			loader.Start( glbPath );
			bool finished = loader.Completion.Wait( TimeSpan.FromSeconds( 30 ) );
			Assert( finished, "DocumentLoader did not finish within 30 s" );
			Assert( loader.Status == DocumentLoader.State.Ready, $"Loader must be Ready, got {loader.Status}: {loader.Error}" );
			Assert( loader.Mesh is not null, "Loaded mesh must be non-null" );
			Assert( loader.Session is not null, "Loaded session must be non-null" );
			var pieces = loader.Session!.FbxSource?.Result?.Pieces;
			Assert( pieces is not null && pieces.Count == 2, $"Loaded session must carry 2 pieces, got {pieces?.Count}" );
			Assert( pieces![0].Name == "red_body" && pieces[1].Name == "blue_body", "Loaded session pieces must be named red_body and blue_body" );
		} );

		registry.Add( "glb", "GLB export refuses overwrite and leaves the session revision and source file untouched", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_overwrite_refusal" );
			Directory.CreateDirectory( testDir );
			var (session, _, _) = TwoPaintedBodies( testDir );
			string effigyPath = Path.Combine( testDir, "two_bodies.effigy" );
			File.WriteAllText( effigyPath, StudioDocument.Write( session.Studio! ) );
			string sourceHashBefore;
			using ( var sha = SHA256.Create() )
			using ( var stream = File.OpenRead( effigyPath ) )
				sourceHashBefore = Convert.ToHexString( sha.ComputeHash( stream ) );
			long revisionBefore = session.Revision;

			string glbPath = Path.Combine( testDir, "two_bodies.glb" );
			var service = new ExportService();
			var first = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( first.Success, $"First GLB export failed: {first.ErrorMessage}" );

			var second = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( !second.Success, "Second export to the same path must be refused" );
			Assert( ( second.ErrorMessage ?? "" ).Contains( "overwrite", StringComparison.OrdinalIgnoreCase ), $"Refusal must mention overwrite, got: {second.ErrorMessage}" );

			Assert( session.Revision == revisionBefore, "Export must not change the session revision" );
			string sourceHashAfter;
			using ( var sha = SHA256.Create() )
			using ( var stream = File.OpenRead( effigyPath ) )
				sourceHashAfter = Convert.ToHexString( sha.ComputeHash( stream ) );
			Assert( sourceHashAfter == sourceHashBefore, "Export must not modify the source file" );
		} );

		registry.Add( "glb", "asymmetric UVs pass through export and import exactly", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_uv_passthrough" );
			Directory.CreateDirectory( testDir );

			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( 0, 0, 0 ),
				new Vec3( 10, 0, 0 ),
				new Vec3( 10, 10, 0 ),
				new Vec3( 0, 10, 0 ),
			} );
			// Deliberately asymmetric: a V flip or U/V swap changes these values.
			var uvs = new[]
			{
				new Vec2( 0f, 0f ),
				new Vec2( 0.25f, 0f ),
				new Vec2( 0.25f, 0.75f ),
				new Vec2( 0f, 0.75f ),
			};
			mesh.AddFace( new[] { 0, 1, 2 }, new[] { uvs[0], uvs[1], uvs[2] }, material: 0 );
			mesh.AddFace( new[] { 0, 2, 3 }, new[] { uvs[0], uvs[2], uvs[3] }, material: 0 );

			var fbxResult = new FbxImport.Result();
			fbxResult.Pieces.Add( new ObjReader.ObjPiece( "uv_quad", mesh ) );
			fbxResult.Weights.Add( null );
			string fbxPath = Path.Combine( testDir, "uv_source.fbx" );
			var session = DocumentSession.CreateFbx( fbxPath, fbxResult );

			string glbPath = Path.Combine( testDir, "uv_quad.glb" );
			var service = new ExportService();
			var exported = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( exported.Success, $"UV fixture export failed: {exported.ErrorMessage}" );

			var roundTrip = GlbImport.Read( File.ReadAllBytes( glbPath ), glbPath );
			Assert( roundTrip.Pieces.Count == 1, "UV round-trip must yield 1 piece" );
			var back = roundTrip.Pieces[0].Mesh;
			Assert( back.Faces.Count == 2, $"UV round-trip must yield 2 faces, got {back.Faces.Count}" );
			for ( int fi = 0; fi < 2; fi++ )
			{
				var expected = fi == 0
					? new[] { uvs[0], uvs[1], uvs[2] }
					: new[] { uvs[0], uvs[2], uvs[3] };
				for ( int c = 0; c < 3; c++ )
				{
					AssertNear( back.Faces[fi].UVs[c].x, expected[c].x, 1e-6f, $"Face {fi} corner {c} U" );
					AssertNear( back.Faces[fi].UVs[c].y, expected[c].y, 1e-6f, $"Face {fi} corner {c} V" );
				}
			}
		} );

		registry.Add( "glb", "unpainted body exports factors with no textures", () =>
		{
			string testDir = Path.Combine( settings.WorkDirectory, "glb_unpainted_factors" );
			Directory.CreateDirectory( testDir );
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { SizeX = { Value = 10f }, SizeY = { Value = 10f }, SizeZ = { Value = 10f } } );
			studio.Rebuild();
			string effigyPath = Path.Combine( testDir, "cube_source.effigy" );
			var session = DocumentSession.CreateEffigy( effigyPath, studio );

			string glbPath = Path.Combine( testDir, "cube.glb" );
			var service = new ExportService();
			var exported = service.Export( new ExportRequest( session, ExportFormat.Glb, glbPath ) );
			Assert( exported.Success, $"Unpainted GLB export failed: {exported.ErrorMessage}" );

			var (json, _) = ParseGlb( File.ReadAllBytes( glbPath ) );
			var root = json.RootElement;
			Assert( !root.TryGetProperty( "images", out _ ), "Unpainted export must embed no images" );
			Assert( !root.TryGetProperty( "textures", out _ ), "Unpainted export must reference no textures" );
			Assert( !root.TryGetProperty( "samplers", out _ ), "Unpainted export must define no samplers" );
			var material = root.GetProperty( "materials" )[0];
			var pbr = material.GetProperty( "pbrMetallicRoughness" );
			Assert( !pbr.TryGetProperty( "baseColorTexture", out _ ), "Unpainted material must have no base-colour texture" );
			Assert( !pbr.TryGetProperty( "metallicRoughnessTexture", out _ ), "Unpainted material must have no metallic-roughness texture" );
			Assert( !material.TryGetProperty( "normalTexture", out _ ), "Unpainted material must have no normal texture" );
			// Expected factors are whatever the document resolver resolves for the
			// slot: the export carries the resolved descriptor, palette fallback
			// included — not a hardcoded neutral. (material_0's palette colour is
			// not white, and asserting white here would pin the wrong contract.)
			string sourceDir = Path.GetDirectoryName( Path.GetFullPath( effigyPath ) )!;
			var resolver = new MaterialResolver( sourceDir, session.DocumentId );
			string slotName = session.Studio!.NameForSlot( 0 );
			var descriptor = resolver.ResolveSlot( 0, slotName, validateTextures: true ).Descriptor;
			var factors = pbr.GetProperty( "baseColorFactor" );
			AssertNear( factors[0].GetSingle(), descriptor.BaseColorFactor.x, 1e-6f, "Unpainted baseColorFactor r" );
			AssertNear( factors[1].GetSingle(), descriptor.BaseColorFactor.y, 1e-6f, "Unpainted baseColorFactor g" );
			AssertNear( factors[2].GetSingle(), descriptor.BaseColorFactor.z, 1e-6f, "Unpainted baseColorFactor b" );
			AssertNear( factors[3].GetSingle(), descriptor.BaseColorFactor.w, 1e-6f, "Unpainted baseColorFactor a" );
			AssertNear( pbr.GetProperty( "metallicFactor" ).GetSingle(), descriptor.MetallicFactor, 1e-6f, "Unpainted metallicFactor" );
			AssertNear( pbr.GetProperty( "roughnessFactor" ).GetSingle(), descriptor.RoughnessFactor, 1e-6f, "Unpainted roughnessFactor" );

			var roundTrip = GlbImport.Read( File.ReadAllBytes( glbPath ), glbPath );
			Assert( roundTrip.Pieces.Count == 1, "Unpainted round-trip must yield 1 piece" );
			Assert( roundTrip.Pieces[0].Mesh.FaceCount > 0, "Unpainted round-trip piece must have faces" );
		} );
	}
}
