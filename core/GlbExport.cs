#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>A texture image ready to embed in a GLB: PNG bytes plus the dimensions the
/// glTF <c>images</c> entry reports. PNG is the only image format this writer embeds —
/// it is what <see cref="PngWriter"/> produces, and it round-trips losslessly.</summary>
public sealed record GlbImage( string Name, byte[] PngBytes, int Width, int Height );

/// <summary>One material for a <see cref="GlbPart"/>. Factors are glTF-linear, clamped to
/// [0,1] on write. A channel with an embedded image uses factor 1 for that channel (the
/// image carries the value), mirroring the kernel's snapshot rule that a paint texture
/// replaces its factor. <see cref="Emissive"/> is the paint emit image (albedo × mask);
/// when present the emissive factor is forced to (1,1,1). <see cref="AlphaModeBlend"/>
/// writes <c>alphaMode: BLEND</c> for a paint opacity image stamped into the base
/// colour's alpha.</summary>
public sealed record GlbMaterial(
	string Name,
	Vec4 BaseColorFactor,
	float MetallicFactor,
	float RoughnessFactor,
	Vec3 EmissionFactor,
	GlbImage? BaseColor,
	GlbImage? MetallicRoughness,
	GlbImage? Normal,
	GlbImage? Occlusion,
	GlbImage? Emissive = null,
	bool AlphaModeBlend = false );

/// <summary>One body to export: a name, a <see cref="PolyMesh"/>, and its material.
/// The mesh is read, never mutated. <see cref="GlbExport.Write"/> writes it static-only
/// (skinning, morphs and animation are dropped); <see cref="GlbExport.WriteAnimated"/>
/// writes them when a skeleton and clips are supplied.</summary>
public sealed record GlbPart( string Name, PolyMesh Mesh, GlbMaterial Material );

/// <summary>One animation clip to export: a name and its <see cref="RigAnimFile"/>.
/// Blank names become "clip"; duplicate names gain " 2", " 3" suffixes, mirroring
/// Mobile's <c>AnimExport.ClipsToWrite</c>.</summary>
public sealed record GlbClip( string Name, RigAnimFile Clip );

/// <summary>
/// Engine-independent glTF 2.0 writer (<c>.glb</c>) for static textured geometry.
/// Mirrors Mobile's <c>AnimExport.Glb</c> conventions so a file written here reads the
/// same way there, and so <see cref="GlbImport"/> round-trips it exactly:
///
/// <list type="bullet">
/// <item>Units: kernel inches × 0.0254 → metres. Axes: Effigy Z-up → glTF Y-up via
/// G(v) = (x, z, -y), a proper rotation, so winding is preserved.</item>
/// <item>Vertices are emitted per corner (position + corner normal + tangent + UV +
/// colour), because a corner can carry a different normal or UV than its vertex.</item>
/// <item>UVs pass through unchanged: the kernel's V convention is glTF's V convention
/// (FBX is the odd one — its V is flipped on import, glTF's is not).</item>
/// <item>Normal maps are expected in OpenGL form (green up). The kernel's
/// <see cref="PaintTextureSet"/> writes DirectX green, so whoever assembles a
/// <see cref="GlbMaterial"/> flips the normal map's G channel first.</item>
/// <item>Metallic-roughness is one image: R = 255 (occlusion-free), G = roughness,
/// B = metalness, matching the kernel's packing.</item>
/// </list>
///
/// The GLB container is the standard layout: 12-byte header (magic 0x46546C67, version 2,
/// total length), a JSON chunk (0x4E4F534A, space-padded to 4 bytes) and a BIN chunk
/// (0x004E4942, zero-padded to 4 bytes). No extensions are written. A part with no faces
/// is refused, as is an empty part list: a silent empty export is not an export.
///
/// <para><see cref="WriteAnimated"/> adds what <see cref="Write"/> drops: bone nodes
/// (TRS from the skeleton's bind pose), one shared skin (top 4 weights per vertex,
/// renormalised; an unweighted vertex binds to joint 0 at 1.0), morph targets (POSITION
/// and NORMAL deltas per valid morph, with <c>weights</c> and <c>extras.targetNames</c>),
/// and one glTF animation per clip (LINEAR translation/rotation/scale per bone, morph
/// <c>weights</c> per morph node, sampled at the clip's frame rate). Part, camera and
/// IK-goal tracks have no glTF representation and are not written — <c>SampleLocal</c>
/// already folds the bone, IK-goal and limit tracks into the sampled bone locals.</para>
/// </summary>
public static class GlbExport
{
	/// <summary>Metres per kernel inch.</summary>
	public const float InchesToMetres = 0.0254f;

	static Vec3 G( Vec3 v ) => new( v.x, v.z, -v.y );
	static Quat G( Quat q ) => new( q.x, q.z, -q.y, q.w );
	static Vec3 GScale( Vec3 s ) => new( s.x, s.z, s.y );
	static Xform Gx( Xform x, float unit ) => new( G( x.X ), G( x.Z ), G( x.Y ) * -1f, G( x.Origin ) * unit );
	static float[] Mat4( Xform x ) => new[] { x.X.x, x.X.y, x.X.z, 0f, x.Y.x, x.Y.y, x.Y.z, 0f, x.Z.x, x.Z.y, x.Z.z, 0f, x.Origin.x, x.Origin.y, x.Origin.z, 1f };

	/// <summary>Write <paramref name="parts"/> as a single static GLB document: no
	/// skeleton, no skinning, no morphs, no animation. Identical to what this method
	/// wrote before <see cref="WriteAnimated"/> existed.</summary>
	/// <exception cref="InvalidOperationException">No parts, or a part with no faces.</exception>
	public static byte[] Write( IReadOnlyList<GlbPart> parts, string generator = "Geppetto Desktop" )
		=> WriteCore( parts, skeleton: null, clips: null, generator );

	/// <summary>Write <paramref name="parts"/> as a GLB document with a skeleton,
	/// skinning, morph targets and one animation per clip (see the class remarks).
	/// A rigged part with no skeleton is refused rather than silently unskinned;
	/// a part whose skin row count does not match its vertex count is refused too.
	/// A null skeleton with morph-only parts is fine: morph targets and morph
	/// weight animations need no skeleton. Clips are ignored when there is neither
	/// a skeleton nor a morph node to animate.</summary>
	/// <exception cref="InvalidOperationException">No parts, a part with no faces, a
	/// rigged part with no skeleton, or a skin row count that does not match.</exception>
	public static byte[] WriteAnimated( IReadOnlyList<GlbPart> parts, Skeleton? skeleton, IReadOnlyList<GlbClip>? clips, string generator = "Geppetto Desktop" )
	{
		if ( parts is null ) throw new ArgumentNullException( nameof( parts ) );
		bool hasSkeleton = skeleton is { Count: > 0 };
		foreach ( var part in parts )
		{
			if ( part.Mesh is null || part.Mesh.FaceCount == 0 ) continue;
			if ( part.Mesh.Skin is not null && part.Mesh.Skin.Count != part.Mesh.Positions.Count )
				throw new InvalidOperationException( $"GLB part '{part.Name}' has a skin row count ({part.Mesh.Skin.Count}) that does not match its vertex count ({part.Mesh.Positions.Count})." );
			if ( !hasSkeleton && part.Mesh.IsRigged )
				throw new InvalidOperationException( $"GLB part '{part.Name}' carries skin weights but no skeleton was supplied; a skinned export needs the skeleton." );
		}
		return WriteCore( parts, skeleton, DedupeClips( clips ), generator );
	}

	static byte[] WriteCore( IReadOnlyList<GlbPart> parts, Skeleton? skeleton, IReadOnlyList<GlbClip>? clips, string generator )
	{
		if ( parts is null ) throw new ArgumentNullException( nameof( parts ) );
		if ( parts.Count == 0 ) throw new InvalidOperationException( "Cannot export a GLB with no parts." );
		foreach ( var part in parts )
		{
			if ( part.Mesh is null ) throw new ArgumentException( $"GLB part '{part.Name}' has no mesh.", nameof( parts ) );
			if ( part.Mesh.FaceCount == 0 ) throw new InvalidOperationException( $"GLB part '{part.Name}' has no faces." );
		}
		bool hasSkeleton = skeleton is { Count: > 0 };
		var clipsToWrite = clips ?? new List<GlbClip>();
		bool anyRigged = hasSkeleton && parts.Any( p => p.Mesh.IsRigged );

		var bin = new MemoryStream();
		var binWriter = new BinaryWriter( bin );
		var views = new List<JsonObject>();
		var accessors = new List<JsonObject>();
		var images = new List<JsonObject>();
		var textures = new List<JsonObject>();
		var materials = new List<JsonObject>();
		var meshes = new List<JsonObject>();
		var nodes = new List<JsonObject>();

		int View( Action write )
		{
			while ( bin.Position % 4 != 0 ) binWriter.Write( (byte)0 );
			int offset = (int)bin.Position;
			write();
			int length = (int)bin.Position - offset;
			views.Add( new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length } );
			return views.Count - 1;
		}

		int Accessor( int viewIndex, int componentType, int count, string type, bool bounds = false, float[]? min = null, float[]? max = null )
		{
			var accessor = new JsonObject
			{
				["bufferView"] = viewIndex,
				["componentType"] = componentType,
				["count"] = count,
				["type"] = type,
			};
			if ( bounds && min is not null && max is not null )
			{
				accessor["min"] = new JsonArray( min.Select( v => (JsonNode?)v ).ToArray() );
				accessor["max"] = new JsonArray( max.Select( v => (JsonNode?)v ).ToArray() );
			}
			accessors.Add( accessor );
			return accessors.Count - 1;
		}

		void Floats( IEnumerable<float> values )
		{
			foreach ( float value in values ) binWriter.Write( value );
		}

		bool hasAnyTexture = parts.Any( p => p.Material.BaseColor is not null || p.Material.MetallicRoughness is not null
			|| p.Material.Normal is not null || p.Material.Occlusion is not null || p.Material.Emissive is not null );

		// Images are deduplicated by CONTENT, not by name: two bodies can share a
		// material slot name (and hence an image name like "material_0_basecolor")
		// while carrying different paint. Deduping by name would silently point the
		// second body at the first body's pixels.
		var imageIndexByHash = new Dictionary<string, int>( StringComparer.Ordinal );
		int EmbedImage( GlbImage image )
		{
			string hash = Convert.ToHexString( SHA256.HashData( image.PngBytes ) );
			if ( imageIndexByHash.TryGetValue( hash, out int existing ) ) return existing;
			int bufferView = View( () => binWriter.Write( image.PngBytes ) );
			images.Add( new JsonObject
			{
				["name"] = image.Name,
				["mimeType"] = "image/png",
				["bufferView"] = bufferView,
			} );
			int index = images.Count - 1;
			imageIndexByHash[hash] = index;
			return index;
		}

		int TextureFor( GlbImage? image )
		{
			if ( image is null ) throw new ArgumentNullException( nameof( image ) );
			int imageIndex = EmbedImage( image );
			// One texture per image; a second channel reusing the same image gets its own
			// texture entry pointing at the same image (glTF texture objects are per-use).
			textures.Add( new JsonObject
			{
				["sampler"] = 0,
				["source"] = imageIndex,
			} );
			return textures.Count - 1;
		}

		// Bone nodes come first (indices 0..skeleton.Count-1); mesh nodes follow.
		// One shared skin; its inverse bind matrices are the bind-pose world
		// transforms converted to glTF metres/Y-up, then inverted.
		var skins = new List<JsonObject>();
		var morphNodes = new List<(int Node, IReadOnlyList<string> Names)>();
		var sceneNodes = new List<int>();
		var boneRoots = new List<int>();
		if ( hasSkeleton )
		{
			for ( int b = 0; b < skeleton!.Count; b++ )
			{
				var bone = skeleton.Bones[b];
				var pose = RigPose.FromXform( bone.Local );
				var tp = G( pose.Position ) * InchesToMetres;
				var rq = G( pose.Rotation.Normalized );
				var sc = GScale( pose.Scale );
				var node = new JsonObject
				{
					["name"] = bone.Name,
					["translation"] = new JsonArray( tp.x, tp.y, tp.z ),
					["rotation"] = new JsonArray( rq.x, rq.y, rq.z, rq.w ),
					["scale"] = new JsonArray( sc.x, sc.y, sc.z ),
				};
				var kids = skeleton.Children( b ).ToList();
				if ( kids.Count > 0 )
					node["children"] = new JsonArray( kids.Select( k => (JsonNode?)k ).ToArray() );
				nodes.Add( node );
				if ( bone.Parent < 0 ) boneRoots.Add( b );
			}
			sceneNodes.AddRange( boneRoots );
			if ( anyRigged )
			{
				var ibm = new List<float>();
				for ( int b = 0; b < skeleton.Count; b++ )
					ibm.AddRange( Mat4( Gx( skeleton.WorldBind( b ), InchesToMetres ).Inverse ) );
				var skin = new JsonObject
				{
					["joints"] = new JsonArray( Enumerable.Range( 0, skeleton.Count ).Select( i => (JsonNode?)i ).ToArray() ),
					["inverseBindMatrices"] = Accessor( View( () => Floats( ibm ) ), 5126, skeleton.Count, "MAT4" ),
				};
				if ( boneRoots.Count > 0 ) skin["skeleton"] = boneRoots[0];
				skins.Add( skin );
			}
		}

		for ( int pi = 0; pi < parts.Count; pi++ )
		{
			var part = parts[pi];
			var mesh = part.Mesh;
			var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
			var tangents = NormalBake.ExportTangents( mesh, cornerNormals, normals );
			bool rigged = hasSkeleton && mesh.IsRigged;
			var morphs = mesh.ValidMorphs.ToList();
			// Shaped-mesh corner normals per morph, for the NORMAL target deltas.
			// The shaped mesh shares the base faces (read-only here) and carries
			// base positions plus this morph's deltas.
			var shapedNormals = new List<(int[][] CornerNormals, List<Vec3> Normals)>( morphs.Count );
			foreach ( var morph in morphs )
			{
				var shaped = new PolyMesh
				{
					Positions = mesh.Positions.Zip( morph.Deltas, ( p, d ) => p + d ).ToList(),
					Faces = mesh.Faces,
				};
				shapedNormals.Add( MeshNormals.ComputeCornerNormals( shaped, MeshNormals.DefaultSmoothingAngleDegrees ) );
			}

			bool hasUv = part.Material.BaseColor is not null || part.Material.MetallicRoughness is not null
				|| part.Material.Normal is not null || part.Material.Occlusion is not null || part.Material.Emissive is not null
				|| mesh.Faces.Any( f => f.UVs is not null && f.UVs.Any( uv => uv.x != 0f || uv.y != 0f ) );
			bool hasColors = mesh.HasVertexColors;

			var positions = new List<float>();
			var normalData = new List<float>();
			var tangentData = new List<float>();
			var uvData = new List<float>();
			var colorData = new List<float>();
			var indexData = new List<uint>();
			var joints = new List<ushort>();
			var weights = new List<float>();
			var targetPos = morphs.Select( _ => new List<float>() ).ToList();
			var targetNrm = morphs.Select( _ => new List<float>() ).ToList();
			var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
			var max = new float[] { float.MinValue, float.MinValue, float.MinValue };

			for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
			{
				var face = mesh.Faces[fi];
				// FaceTriangles returns CORNER indices (0..face.Count-1), not vertex
				// indices: the vertex is face.Indices[cornerIndex]. Treating the corner
				// as a vertex index only works by coincidence when a face's vertices
				// happen to be numbered 0, 1, 2 — a box face breaks it.
				foreach ( var (a, b, c) in NormalBake.FaceTriangles( mesh, fi ) )
				{
					foreach ( int cornerIndex in new[] { a, b, c } )
					{
						int vertexIndex = face.Indices[cornerIndex];
						var p = G( mesh.Positions[vertexIndex] ) * InchesToMetres;
						positions.Add( p.x ); positions.Add( p.y ); positions.Add( p.z );
						for ( int axis = 0; axis < 3; axis++ )
						{
							min[axis] = MathF.Min( min[axis], axis == 0 ? p.x : axis == 1 ? p.y : p.z );
							max[axis] = MathF.Max( max[axis], axis == 0 ? p.x : axis == 1 ? p.y : p.z );
						}
						var n = G( normals[cornerNormals[fi][cornerIndex]] );
						normalData.Add( n.x ); normalData.Add( n.y ); normalData.Add( n.z );
						if ( hasUv )
						{
							var t = tangents[fi][cornerIndex];
							var tv = G( new Vec3( t.x, t.y, t.z ) );
							tangentData.Add( tv.x ); tangentData.Add( tv.y ); tangentData.Add( tv.z ); tangentData.Add( t.w );
							// A face's UVs can be null or short (the ctor defaults to a zero
							// array, but nothing enforces it): emit (0,0) rather than crash.
							var uv = face.UVs is { Length: > 0 } && cornerIndex < face.UVs.Length ? face.UVs[cornerIndex] : default;
							uvData.Add( uv.x ); uvData.Add( uv.y );
						}
						if ( hasColors )
						{
							var col = mesh.VertexColors[vertexIndex];
							colorData.Add( col.x ); colorData.Add( col.y ); colorData.Add( col.z ); colorData.Add( col.w );
						}
						if ( rigged )
						{
							// Top 4 weights by size, renormalised; a vertex with no
							// positive weight binds to joint 0 at 1.0 (Mobile's rule
							// for an empty row; a row of only zero weights is empty
							// here rather than written all-zero, which no reader
							// can skin). A bone index outside the skeleton is a
							// corrupt skin, not a silent drop.
							var row = mesh.Skin![vertexIndex] ?? Array.Empty<BoneWeight>();
							var top = row.Where( w => w.Weight > 0f ).OrderByDescending( w => w.Weight ).Take( 4 ).ToArray();
							float sum = top.Sum( w => w.Weight );
							for ( int k = 0; k < 4; k++ )
							{
								if ( k < top.Length )
								{
									if ( top[k].Bone < 0 || top[k].Bone >= skeleton!.Count )
										throw new InvalidOperationException( $"GLB part '{part.Name}' vertex {vertexIndex} weights bone {top[k].Bone}, outside the skeleton (0..{skeleton.Count - 1})." );
									joints.Add( (ushort)top[k].Bone );
									weights.Add( top[k].Weight / sum );
								}
								else
								{
									joints.Add( 0 );
									weights.Add( k == 0 && top.Length == 0 ? 1f : 0f );
								}
							}
						}
						for ( int k = 0; k < morphs.Count; k++ )
						{
							var d = G( morphs[k].Deltas[vertexIndex] ) * InchesToMetres;
							targetPos[k].Add( d.x ); targetPos[k].Add( d.y ); targetPos[k].Add( d.z );
							var (shapedCorners, shapedNormalsList) = shapedNormals[k];
							var dn = G( shapedNormalsList[shapedCorners[fi][cornerIndex]] ) - n;
							targetNrm[k].Add( dn.x ); targetNrm[k].Add( dn.y ); targetNrm[k].Add( dn.z );
						}
						indexData.Add( (uint)indexData.Count );
					}
				}
			}

			int positionView = View( () => Floats( positions ) );
			int positionAccessor = Accessor( positionView, 5126, positions.Count / 3, "VEC3", bounds: true, min: min, max: max );
			int normalView = View( () => Floats( normalData ) );
			int normalAccessor = Accessor( normalView, 5126, normalData.Count / 3, "VEC3" );

			var attributes = new JsonObject
			{
				["POSITION"] = positionAccessor,
				["NORMAL"] = normalAccessor,
			};
			if ( hasUv )
			{
				int tangentView = View( () => Floats( tangentData ) );
				attributes["TANGENT"] = Accessor( tangentView, 5126, tangentData.Count / 4, "VEC4" );
				int uvView = View( () => Floats( uvData ) );
				attributes["TEXCOORD_0"] = Accessor( uvView, 5126, uvData.Count / 2, "VEC2" );
			}
			if ( hasColors )
			{
				int colorView = View( () => Floats( colorData ) );
				attributes["COLOR_0"] = Accessor( colorView, 5126, colorData.Count / 4, "VEC4" );
			}
			if ( rigged )
			{
				int jointsView = View( () => { foreach ( ushort j in joints ) binWriter.Write( j ); } );
				attributes["JOINTS_0"] = Accessor( jointsView, 5123, joints.Count / 4, "VEC4" );
				int weightsView = View( () => Floats( weights ) );
				attributes["WEIGHTS_0"] = Accessor( weightsView, 5126, weights.Count / 4, "VEC4" );
			}
			int indexView = View( () => { foreach ( uint i in indexData ) binWriter.Write( i ); } );
			int indexAccessor = Accessor( indexView, 5125, indexData.Count, "SCALAR" );

			var primitive = new JsonObject
			{
				["attributes"] = attributes,
				["indices"] = indexAccessor,
				["material"] = pi,
				["mode"] = 4,
			};
			if ( morphs.Count > 0 )
			{
				var targets = new JsonArray();
				for ( int k = 0; k < morphs.Count; k++ )
				{
					var tmin = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
					var tmax = new float[] { float.MinValue, float.MinValue, float.MinValue };
					for ( int i = 0; i < targetPos[k].Count; i += 3 )
						for ( int axis = 0; axis < 3; axis++ )
						{
							tmin[axis] = MathF.Min( tmin[axis], targetPos[k][i + axis] );
							tmax[axis] = MathF.Max( tmax[axis], targetPos[k][i + axis] );
						}
					targets.Add( new JsonObject
					{
						["POSITION"] = Accessor( View( () => Floats( targetPos[k] ) ), 5126, targetPos[k].Count / 3, "VEC3", bounds: true, min: tmin, max: tmax ),
						["NORMAL"] = Accessor( View( () => Floats( targetNrm[k] ) ), 5126, targetNrm[k].Count / 3, "VEC3" ),
					} );
				}
				primitive["targets"] = targets;
			}
			var gltfMesh = new JsonObject
			{
				["name"] = part.Name,
				["primitives"] = new JsonArray( primitive ),
			};
			if ( morphs.Count > 0 )
			{
				gltfMesh["weights"] = new JsonArray( morphs.Select( _ => (JsonNode?)0f ).ToArray() );
				gltfMesh["extras"] = new JsonObject { ["targetNames"] = new JsonArray( morphs.Select( m => (JsonNode?)m.Name ).ToArray() ) };
			}
			meshes.Add( gltfMesh );
			var meshNode = new JsonObject
			{
				["name"] = part.Name,
				["mesh"] = pi,
			};
			if ( rigged ) meshNode["skin"] = 0;
			nodes.Add( meshNode );
			sceneNodes.Add( nodes.Count - 1 );
			if ( morphs.Count > 0 )
				morphNodes.Add( (nodes.Count - 1, morphs.Select( m => m.Name ).ToList()) );

			var material = new JsonObject { ["name"] = part.Material.Name };
			var pbr = new JsonObject
			{
				["baseColorFactor"] = new JsonArray( Clamp01( part.Material.BaseColorFactor.x ), Clamp01( part.Material.BaseColorFactor.y ), Clamp01( part.Material.BaseColorFactor.z ), Clamp01( part.Material.BaseColorFactor.w ) ),
				["metallicFactor"] = Clamp01( part.Material.MetallicFactor ),
				["roughnessFactor"] = Clamp01( part.Material.RoughnessFactor ),
			};
			if ( part.Material.BaseColor is not null )
				pbr["baseColorTexture"] = new JsonObject { ["index"] = TextureFor( part.Material.BaseColor ) };
			if ( part.Material.MetallicRoughness is not null )
				pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = TextureFor( part.Material.MetallicRoughness ) };
			material["pbrMetallicRoughness"] = pbr;
			if ( part.Material.EmissionFactor.x != 0f || part.Material.EmissionFactor.y != 0f || part.Material.EmissionFactor.z != 0f )
				material["emissiveFactor"] = new JsonArray( Clamp01( part.Material.EmissionFactor.x ), Clamp01( part.Material.EmissionFactor.y ), Clamp01( part.Material.EmissionFactor.z ) );
			if ( part.Material.Normal is not null )
				material["normalTexture"] = new JsonObject { ["index"] = TextureFor( part.Material.Normal ) };
			if ( part.Material.Occlusion is not null )
				material["occlusionTexture"] = new JsonObject { ["index"] = TextureFor( part.Material.Occlusion ) };
			if ( part.Material.Emissive is not null )
				material["emissiveTexture"] = new JsonObject { ["index"] = TextureFor( part.Material.Emissive ) };
			if ( part.Material.AlphaModeBlend )
				material["alphaMode"] = "BLEND";
			materials.Add( material );
		}

		// One glTF animation per clip, sampled at the clip's frame rate from
		// frame 0 to LastFrame. Bone channels are LINEAR TRS; morph channels
		// are weights per morph node. Quaternion hemisphere is fixed per bone
		// so a reader never interpolates the long way round.
		var animations = new JsonArray();
		if ( clipsToWrite.Count > 0 && ( hasSkeleton || morphNodes.Count > 0 ) )
		{
			foreach ( var clip in clipsToWrite )
			{
				int fps = Math.Max( clip.Clip.FrameRate, 1 );
				int last = Math.Max( clip.Clip.LastFrame, 0 );
				var frames = new List<Xform[]>( last + 1 );
				for ( int f = 0; f <= last; f++ )
					frames.Add( hasSkeleton ? clip.Clip.SampleLocal( skeleton!, f ) : Array.Empty<Xform>() );
				float[] times = Enumerable.Range( 0, frames.Count ).Select( f => (float)f / fps ).ToArray();
				int input = Accessor( View( () => Floats( times ) ), 5126, frames.Count, "SCALAR", bounds: true, min: new[] { 0f }, max: new[] { times[^1] } );
				var samplers = new JsonArray();
				var channels = new JsonArray();
				if ( hasSkeleton )
				{
					for ( int b = 0; b < skeleton!.Count; b++ )
					{
						var t = new List<float>(); var r = new List<float>(); var s = new List<float>();
						Quat prev = default; bool first = true;
						for ( int f = 0; f < frames.Count; f++ )
						{
							var pose = RigPose.FromXform( frames[f][b] );
							var tp = G( pose.Position ) * InchesToMetres;
							t.Add( tp.x ); t.Add( tp.y ); t.Add( tp.z );
							var rq = G( pose.Rotation.Normalized );
							if ( !first && rq.x * prev.x + rq.y * prev.y + rq.z * prev.z + rq.w * prev.w < 0f )
								rq = new Quat( -rq.x, -rq.y, -rq.z, -rq.w );
							prev = rq; first = false;
							r.Add( rq.x ); r.Add( rq.y ); r.Add( rq.z ); r.Add( rq.w );
							var sc = GScale( pose.Scale );
							s.Add( sc.x ); s.Add( sc.y ); s.Add( sc.z );
						}
						int ti = Accessor( View( () => Floats( t ) ), 5126, t.Count / 3, "VEC3" );
						int ri = Accessor( View( () => Floats( r ) ), 5126, r.Count / 4, "VEC4" );
						int si = Accessor( View( () => Floats( s ) ), 5126, s.Count / 3, "VEC3" );
						samplers.Add( new JsonObject { ["input"] = input, ["interpolation"] = "LINEAR", ["output"] = ti } );
						samplers.Add( new JsonObject { ["input"] = input, ["interpolation"] = "LINEAR", ["output"] = ri } );
						samplers.Add( new JsonObject { ["input"] = input, ["interpolation"] = "LINEAR", ["output"] = si } );
						channels.Add( new JsonObject { ["sampler"] = samplers.Count - 3, ["target"] = new JsonObject { ["node"] = b, ["path"] = "translation" } } );
						channels.Add( new JsonObject { ["sampler"] = samplers.Count - 2, ["target"] = new JsonObject { ["node"] = b, ["path"] = "rotation" } } );
						channels.Add( new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = b, ["path"] = "scale" } } );
					}
				}
				foreach ( var (node, names) in morphNodes )
				{
					var w = new List<float>();
					for ( int f = 0; f < frames.Count; f++ )
					{
						var mw = hasSkeleton ? clip.Clip.MorphWeightsAt( f, skeleton!, frames[f] ) : clip.Clip.MorphWeightsAt( f );
						foreach ( var name in names )
							w.Add( mw.TryGetValue( name, out float v ) ? v : 0f );
					}
					int wi = Accessor( View( () => Floats( w ) ), 5126, w.Count / names.Count, "SCALAR" );
					samplers.Add( new JsonObject { ["input"] = input, ["interpolation"] = "LINEAR", ["output"] = wi } );
					channels.Add( new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = node, ["path"] = "weights" } } );
				}
				animations.Add( new JsonObject { ["name"] = clip.Name, ["samplers"] = samplers, ["channels"] = channels } );
			}
		}

		var root = new JsonObject
		{
			["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = generator },
			["scene"] = 0,
			["scenes"] = new JsonArray( new JsonObject { ["nodes"] = new JsonArray( sceneNodes.Select( i => (JsonNode?)i ).ToArray() ) } ),
			["nodes"] = new JsonArray( nodes.Select( n => (JsonNode?)n ).ToArray() ),
			["meshes"] = new JsonArray( meshes.Select( m => (JsonNode?)m ).ToArray() ),
			["materials"] = new JsonArray( materials.Select( m => (JsonNode?)m ).ToArray() ),
			["accessors"] = new JsonArray( accessors.Select( a => (JsonNode?)a ).ToArray() ),
			["bufferViews"] = new JsonArray( views.Select( v => (JsonNode?)v ).ToArray() ),
			["buffers"] = new JsonArray( new JsonObject { ["byteLength"] = (int)bin.Length } ),
		};
		if ( skins.Count > 0 )
			root["skins"] = new JsonArray( skins.Select( s => (JsonNode?)s ).ToArray() );
		if ( animations.Count > 0 )
			root["animations"] = animations;
		if ( hasAnyTexture )
		{
			root["samplers"] = new JsonArray( new JsonObject
			{
				["magFilter"] = 9729,
				["minFilter"] = 9987,
				["wrapS"] = 33071,
				["wrapT"] = 33071,
			} );
			root["textures"] = new JsonArray( textures.Select( t => (JsonNode?)t ).ToArray() );
			root["images"] = new JsonArray( images.Select( i => (JsonNode?)i ).ToArray() );
		}

		byte[] jsonBytes = Encoding.UTF8.GetBytes( root.ToJsonString() );
		byte[] binBytes = bin.ToArray();
		using var output = new MemoryStream();
		using ( var writer = new BinaryWriter( output, Encoding.UTF8, leaveOpen: true ) )
		{
			int jsonPadded = ( jsonBytes.Length + 3 ) & ~3;
			int binPadded = ( binBytes.Length + 3 ) & ~3;
			writer.Write( 0x46546C67u );
			writer.Write( 2u );
			writer.Write( (uint)( 12 + 8 + jsonPadded + ( binBytes.Length > 0 ? 8 + binPadded : 0 ) ) );
			writer.Write( (uint)jsonPadded );
			writer.Write( 0x4E4F534Au );
			writer.Write( jsonBytes );
			for ( int i = jsonBytes.Length; i < jsonPadded; i++ ) writer.Write( (byte)' ' );
			if ( binBytes.Length > 0 )
			{
				writer.Write( (uint)binPadded );
				writer.Write( 0x004E4942u );
				writer.Write( binBytes );
				for ( int i = binBytes.Length; i < binPadded; i++ ) writer.Write( (byte)0 );
			}
		}
		return output.ToArray();
	}

	static List<GlbClip> DedupeClips( IReadOnlyList<GlbClip>? clips )
	{
		var result = new List<GlbClip>();
		if ( clips is null ) return result;
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var clip in clips )
		{
			if ( clip.Clip is null ) throw new ArgumentException( "A GLB clip has no RigAnimFile.", nameof( clips ) );
			string name = string.IsNullOrWhiteSpace( clip.Name ) ? "clip" : clip.Name.Trim();
			if ( !seen.Add( name ) )
			{
				int k = 2;
				while ( !seen.Add( $"{name} {k}" ) ) k++;
				name = $"{name} {k}";
			}
			result.Add( new GlbClip( name, clip.Clip ) );
		}
		return result;
	}

	static float Clamp01( float value ) => Math.Clamp( value, 0f, 1f );
}
