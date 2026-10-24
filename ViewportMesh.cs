using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// CPU-side geometry extracted from a rebuilt PartStudio, grouped by material for one VBO /
/// ~N draw calls. Built off the GL thread; uploaded once on the GL thread.
/// </summary>
public sealed class ViewportMesh : IDisposable
{
	public const int FloatsPerVertex = 20; // pos(3) + nrm(3) + boneIndices(4) + boneWeights(4) + UV(2) + tangent/sign(4)

	public readonly struct DrawBatch
	{
		public readonly string MaterialName;
		public readonly int MaterialSlot;
		public readonly float R, G, B, A;
		public readonly float Roughness;
		public readonly float NormalStrength;
		public readonly float Metallic;
		public readonly float Er, Eg, Eb;
		public readonly float Ps1Occlusion, Ps1Wear, Ps1Grime, Ps1GrimeFilm, Ps1Glow;
		public readonly float Wr, Wg, Wb;
		public readonly float Ps1BootDirt, Ps1BootToe, Ps1BootCuff;
		public readonly int IndexOffset; // bytes into the element buffer
		public readonly int IndexCount;
		public readonly DecodedImage AlbedoImage;
		public readonly TextureBinding AlbedoBinding;
		public readonly bool DoubleSided;
		public readonly AlphaMode AlphaMode;
		public readonly float AlphaCutoff;
		public readonly DecodedImage NormalImage, RoughnessImage, MetallicImage, EmissionImage, OcclusionImage;
		public readonly TextureBinding NormalBinding, RoughnessBinding, MetallicBinding, EmissionBinding, OcclusionBinding;
		/// <summary>R6 extra lobes (transmission, subsurface, sheen, clearcoat, anisotropy).</summary>
		public readonly MaterialLobes Lobes;
		/// <summary>Drawn after opaque batches: blended or refracting what is behind it.</summary>
		public bool Translucent => AlphaMode == AlphaMode.Blend || Lobes.Transmission > 0f;

		public DrawBatch( string name, MaterialPalette.Entry mat, int indexOffset, int indexCount, int materialSlot = 0 )
		{
			MaterialName = name;
			MaterialSlot = materialSlot;
			R = mat.R; G = mat.G; B = mat.B;
			A = 1f;
			Roughness = mat.Roughness;
			NormalStrength = 1f;
			Metallic = mat.Metallic;
			Er = mat.Er; Eg = mat.Eg; Eb = mat.Eb;
			Ps1Occlusion = 1f; // No authored AO means unoccluded environment lighting.
			IndexOffset = indexOffset;
			IndexCount = indexCount;
			AlbedoImage = null;
			AlbedoBinding = null;
			DoubleSided = false;
			AlphaMode = AlphaMode.Opaque;
			AlphaCutoff = 0.5f;
			NormalImage = RoughnessImage = MetallicImage = EmissionImage = OcclusionImage = null;
			NormalBinding = RoughnessBinding = MetallicBinding = EmissionBinding = OcclusionBinding = null;
			Lobes = MaterialLobes.None;
		}

		/// <summary>R9: same resolved material, new index range (re-extracted modified geometry).</summary>
		internal DrawBatch( DrawBatch material, int indexOffset, int indexCount )
		{
			this = material;
			IndexOffset = indexOffset; IndexCount = indexCount;
		}

		internal DrawBatch( DrawBatch source, MaterialDescriptor descriptor, DecodedImage image,
			DecodedImage normal, DecodedImage roughness, DecodedImage metallic, DecodedImage emission, DecodedImage occlusion )
		{
			MaterialName = source.MaterialName;
			MaterialSlot = source.MaterialSlot;
			IndexOffset = source.IndexOffset; IndexCount = source.IndexCount;
			R = descriptor.BaseColorFactor.x; G = descriptor.BaseColorFactor.y; B = descriptor.BaseColorFactor.z;
			A = descriptor.BaseColorFactor.w;
			Roughness = descriptor.RoughnessMap is not null && descriptor.RoughnessFactor == 0.5f ? 1.0f : descriptor.RoughnessFactor;
			NormalStrength = descriptor.NormalStrength;
			Metallic = descriptor.MetallicMap is not null && descriptor.MetallicFactor == 0.0f ? 1.0f : descriptor.MetallicFactor;
			Er = descriptor.EmissionFactor.x; Eg = descriptor.EmissionFactor.y; Eb = descriptor.EmissionFactor.z;
			Ps1Occlusion = descriptor.Ps1Occlusion; Ps1Wear = descriptor.Ps1Wear; Ps1Grime = descriptor.Ps1Grime;
			Ps1GrimeFilm = descriptor.Ps1GrimeFilm; Ps1Glow = descriptor.Ps1Glow;
			Wr = descriptor.Ps1WearColor.x; Wg = descriptor.Ps1WearColor.y; Wb = descriptor.Ps1WearColor.z;
			Ps1BootDirt = descriptor.Ps1BootDirt; Ps1BootToe = descriptor.Ps1BootToe; Ps1BootCuff = descriptor.Ps1BootCuff;
			if ( descriptor.EmissionMap is not null && Er == 0 && Eg == 0 && Eb == 0 )
			{
				Er = Eg = Eb = 1.0f;
			}
			AlbedoImage = image; AlbedoBinding = descriptor.BaseColorMap;
			DoubleSided = descriptor.DoubleSided;
			AlphaMode = descriptor.AlphaMode;
			AlphaCutoff = descriptor.AlphaCutoff;
			NormalImage = normal; NormalBinding = descriptor.NormalMap;
			RoughnessImage = roughness; RoughnessBinding = descriptor.RoughnessMap;
			MetallicImage = metallic; MetallicBinding = descriptor.MetallicMap;
			EmissionImage = emission; EmissionBinding = descriptor.EmissionMap;
			OcclusionImage = occlusion; OcclusionBinding = descriptor.AmbientOcclusionMap;
			Lobes = MaterialLobes.From( descriptor );
		}
	}

	public readonly struct BodyPickEntry
	{
		public readonly string Id;
		public readonly string Name;
		public readonly string BoneName;
		public readonly Vector3 Min;
		public readonly Vector3 Max;

		public BodyPickEntry( string id, string name, string boneName, Vector3 min, Vector3 max )
		{
			Id = id;
			Name = name;
			BoneName = boneName;
			Min = min;
			Max = max;
		}
	}

	/// <summary>Interleaved position, normal, bone indices/weights and per-corner UV, ready for GL_FLOAT upload.</summary>
	public float[] Vertices;
	public uint[] Indices;
	public DrawBatch[] Batches;
	public BodyPickEntry[] BodyPicks = Array.Empty<BodyPickEntry>();
	public MeshPicker Picker { get; private set; } = new();
	public Skeleton Rig;
	public bool HasSkin;
	public Vector3 BoundsMin;
	public Vector3 BoundsMax;
	public Vector3 Centre;
	public float Radius;
	public int TriangleCount;
	public int BodyCount;
	public double LoadSeconds;
	public double ExtractSeconds;

	/// <summary>Base (undeformed) interleaved vertex buffer, retained for morph re-application after upload.</summary>
	public float[] BaseVertices;
	/// <summary>Corner map matching the material-bucketed VBO layout, for morph deformation.</summary>
	public CornerVertexMap MorphCornerMap;
	/// <summary>Retained morph targets for this mesh.</summary>
	public RetainedMeshMorphs Morphs;
	/// <summary>Current morph weights to apply during rendering (empty = base shape).</summary>
	public Dictionary<string, float> MorphWeights = new( StringComparer.Ordinal );

	/// <summary>R9: the authored mesh this was extracted from (input of the live modifier stack).</summary>
	public PolyMesh SourceMesh { get; private set; }
	Func<int, string> _slotNames;

	/// <summary>R9: extract <paramref name="modified"/> (derived from <see cref="SourceMesh"/>) with this
	/// mesh's rig, picks and already-resolved materials, so the modifier stack never re-decodes textures.
	/// Works before or after <see cref="Upload"/> (it reads only the batches, not the released arrays).</summary>
	public ViewportMesh Remesh( PolyMesh modified )
	{
		ArgumentNullException.ThrowIfNull( modified );
		if ( modified.FaceCount == 0 ) throw new ArgumentException( "Modified mesh has no faces.", nameof( modified ) );
		bool skin = HasSkin && modified.Skin is not null && modified.Skin.Count == modified.VertexCount;
		var result = Extract( modified, _slotNames, Rig, skin, BodyPicks, BodyCount, LoadSeconds );
		for ( int i = 0; i < result.Batches.Length; i++ )
			for ( int j = 0; j < Batches.Length; j++ )
				if ( Batches[j].MaterialSlot == result.Batches[i].MaterialSlot && Batches[j].MaterialName == result.Batches[i].MaterialName )
				{ result.Batches[i] = new DrawBatch( Batches[j], result.Batches[i].IndexOffset, result.Batches[i].IndexCount ); break; }
		result.Picker = Picker;
		result.MorphWeights = MorphWeights;
		return result;
	}

	/// <summary>M4: decoded/GPU memory, stage timings, batch count and reload resource counts.</summary>
	public MeshResourceBudget Budget { get; } = new();

	uint _vao, _vbo, _ebo;
	bool _uploaded;
	long _gpuTextureBytes;
	uint _whiteTexture, _flatNormalTexture, _unitDataTexture;
	uint[] _batchAlbedoTextures, _batchNormalTextures, _batchRoughnessTextures, _batchMetallicTextures, _batchEmissionTextures, _batchOcclusionTextures;
	readonly Dictionary<string, uint> _ownedTextures = new( StringComparer.Ordinal );

	/// <summary>Resolve/decode authored maps before upload, on the document worker thread.</summary>
	public void ApplyMaterials( MaterialResolver resolver )
	{
		if ( _uploaded ) throw new InvalidOperationException( "Material resolution must precede GL upload." );
		_batchUv = null;
		var decodeClock = Stopwatch.StartNew();
		var decoded = new HashSet<DecodedImage>();
		for ( int i = 0; i < Batches.Length; i++ )
		{
			var resolved = resolver.ResolveSlot( Batches[i].MaterialSlot, Batches[i].MaterialName );
			if ( !resolved.Success ) throw new InvalidOperationException( $"Material '{Batches[i].MaterialName}' could not be resolved." );
			var descriptor = resolved.Descriptor;
			foreach ( var (binding, space, channel) in new[] {
				(descriptor.BaseColorMap, ColorSpaceInterpretation.Srgb, "albedo"),
				(descriptor.NormalMap, ColorSpaceInterpretation.Linear, "normal"),
				(descriptor.RoughnessMap, ColorSpaceInterpretation.Linear, "roughness"),
				(descriptor.MetallicMap, ColorSpaceInterpretation.Linear, "metallic"),
				(descriptor.EmissionMap, ColorSpaceInterpretation.Srgb, "emission"),
				(descriptor.AmbientOcclusionMap, ColorSpaceInterpretation.Linear, "occlusion") } )
			{
				if ( binding is not null && binding.UvSetIndex != 0 )
					throw new NotSupportedException( $"Material '{descriptor.Id}' {channel} requests UV set {binding.UvSetIndex}; viewport supports only set 0." );
				if ( binding is not null && binding.ColorSpace != space )
					throw new NotSupportedException( $"Material '{descriptor.Id}' {channel} has {binding.ColorSpace} data; expected {space}." );
			}
			var image = descriptor.BaseColorMap is null ? null : resolver.LoadTexture( descriptor.BaseColorMap, descriptor.Id ).Image;
			DecodedImage Load( TextureBinding binding ) => binding is null ? null : resolver.LoadTexture( binding, descriptor.Id ).Image;
			Batches[i] = new DrawBatch( Batches[i], descriptor, image,
				Load( descriptor.NormalMap ), Load( descriptor.RoughnessMap ),
				Load( descriptor.MetallicMap ), Load( descriptor.EmissionMap ), Load( descriptor.AmbientOcclusionMap ) );
			foreach ( var candidate in new[] { Batches[i].AlbedoImage, Batches[i].NormalImage, Batches[i].RoughnessImage,
				Batches[i].MetallicImage, Batches[i].EmissionImage, Batches[i].OcclusionImage } )
				if ( candidate is not null ) decoded.Add( candidate );
		}
		// M4 budget: decoded memory and the one and only material-lookup pass for this mesh.
		// Draw() and DrawBatched() never touch the resolver again.
		long decodedBytes = 0;
		foreach ( var image in decoded ) decodedBytes += image.Rgba.Length;
		Budget.RecordDecode( decodeClock.Elapsed.TotalSeconds, Batches.Length, decodedBytes, decoded.Count, decoded.Count );
	}

	public static ViewportMesh FromStudio( PartStudio studio, double loadSeconds, string documentPath )
	{
		var sw = Stopwatch.StartNew();
		PolyMesh mesh;
		if ( System.IO.Path.GetFileName( documentPath ).Equals( "camhead.effigy", StringComparison.OrdinalIgnoreCase ) )
		{
			mesh = CamheadAssembly.Load( studio, documentPath );
		}
		else
		{
			var (m, ranges) = studio.ToMeshWithBodies( b => b.Visible );
			if ( studio.Rig is { Count: > 0 } )
				m.Skin = SkinBinder.BindBodies( m, ranges, studio.BodyBoneMap, studio.Rig );
			mesh = m;
		}

		if ( mesh is null || mesh.FaceCount == 0 || mesh.VertexCount == 0 )
			throw new InvalidOperationException( "document produced no visible mesh" );

		// Build per-body AABB pick table for 3D viewport inspection, off to the side of the merged extract.
		var picksTask = System.Threading.Tasks.Task.Run( () =>
		{
			var bodyPicks = new List<BodyPickEntry>( studio.Bodies.Count );
			var picker = new MeshPicker();
			foreach ( var body in studio.Bodies )
			{
				if ( !body.Visible || body.Mesh is null || body.Mesh.VertexCount == 0 ) continue;
				var pmin = new Vector3( float.MaxValue );
				var pmax = new Vector3( float.MinValue );
				foreach ( var p in body.Mesh.Positions )
				{
					var pv = new Vector3( p.x, p.y, p.z );
					pmin = Vector3.Min( pmin, pv );
					pmax = Vector3.Max( pmax, pv );
				}
				studio.BodyBoneMap.TryGetValue( body.Id, out var boneName );
				var pickMesh = body.Mesh.Clone();
				if ( studio.Rig is { Count: > 0 } )
					pickMesh.Skin = SkinBinder.BindBodies( pickMesh,
						new[] { new BodyRange( body.Id, body.Name, 0, pickMesh.VertexCount, pickMesh.IsRigged ) },
						studio.BodyBoneMap, studio.Rig );
				picker.Add( pickMesh );
				bodyPicks.Add( new BodyPickEntry( body.Id, body.Name ?? body.Id, boneName ?? "", pmin, pmax ) );
			}

			return (bodyPicks, picker);
		} );
		var hasSkin = mesh.Skin is not null && mesh.Skin.Count == mesh.VertexCount && studio.Rig is { Count: > 0 };
		var result = Extract( mesh, slot => studio.NameForSlot( slot ), studio.Rig, hasSkin, null, studio.Bodies.Count, loadSeconds );
		var (bodyPicks, picker) = picksTask.GetAwaiter().GetResult();
		result.BodyPicks = bodyPicks.ToArray();
		result.Picker = picker;
		result.ExtractSeconds = sw.Elapsed.TotalSeconds;
		return result;
	}

	/// <summary>Extract a standalone authored mesh using the same corner-vertex path as document loading.</summary>
	public static ViewportMesh FromPolyMesh( PolyMesh mesh, Func<int, string> nameForSlot )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( nameForSlot );
		if ( mesh.FaceCount == 0 ) throw new ArgumentException( "Mesh has no faces.", nameof( mesh ) );
		return Extract( mesh, nameForSlot, null, false, Array.Empty<BodyPickEntry>(), 1, 0 );
	}


	public static ViewportMesh FromFbx( Effigy.FbxImport.Result imported, double loadSeconds, string documentPath )
	{
		var mesh = new Effigy.PolyMesh();
		var rig = imported.Skeleton;

		var bodyPicks = new List<BodyPickEntry>( imported.Pieces.Count );
		var picker = new MeshPicker();
		var sw = Stopwatch.StartNew();

		for ( int pi = 0; pi < imported.Pieces.Count; pi++ )
		{
			var piece = imported.Pieces[pi];
			var weights = (pi < imported.Weights.Count) ? imported.Weights[pi] : null;

			if ( rig is { Count: > 0 } )
			{
				piece.Mesh.Skin = Effigy.FbxImport.Resolve( weights, rig, null, out int unweighted )
					?? throw new InvalidOperationException( $"FBX piece '{piece.Name}' has missing or unresolved skin weights ({unweighted} unweighted vertices). Refusing to invent replacement weights." );
			}
			picker.Add( piece.Mesh );

			// Per-piece AABB for 3D picking.
			var pmin = new Vector3( float.MaxValue );
			var pmax = new Vector3( float.MinValue );
			foreach ( var pos in piece.Mesh.Positions )
			{
				var pv = new Vector3( pos.x, pos.y, pos.z );
				pmin = Vector3.Min( pmin, pv );
				pmax = Vector3.Max( pmax, pv );
			}
			string boneName = "";
			// A skinned piece is not "bound to" the first vertex's first bone.
			bodyPicks.Add( new BodyPickEntry( $"fbx:{pi}", piece.Name, boneName, pmin, pmax ) );

			Effigy.MeshTransform.Append( mesh, piece.Mesh );
		}

		if ( mesh.FaceCount == 0 || mesh.VertexCount == 0 )
			throw new InvalidOperationException( "document produced no visible mesh" );

		var hasSkin = mesh.Skin is not null && mesh.Skin.Count == mesh.VertexCount && rig is { Count: > 0 };

		Func<int,string> nameForSlot = slot =>
			imported.MaterialSlots.TryGetValue( slot, out var n ) ? n : $"material_{slot}";

		var result = Extract( mesh, nameForSlot, rig, hasSkin, bodyPicks.ToArray(), imported.Pieces.Count, loadSeconds );
		result.Picker = picker;
		result.ExtractSeconds = sw.Elapsed.TotalSeconds;
		return result;
	}

	static void PackBoneWeights( BoneWeight[] weights,
		out float b0, out float b1, out float b2, out float b3,
		out float w0, out float w1, out float w2, out float w3 )
	{
		b0 = b1 = b2 = b3 = 0f;
		w0 = 1f; w1 = w2 = w3 = 0f;
		if ( weights is null || weights.Length == 0 )
			return;

		if ( weights.Length > 4 )
			weights = SkinWeights.Prune( weights, 4 );

		w0 = 0f;
		if ( weights.Length > 0 ) { b0 = weights[0].Bone; w0 = weights[0].Weight; }
		if ( weights.Length > 1 ) { b1 = weights[1].Bone; w1 = weights[1].Weight; }
		if ( weights.Length > 2 ) { b2 = weights[2].Bone; w2 = weights[2].Weight; }
		if ( weights.Length > 3 ) { b3 = weights[3].Bone; w3 = weights[3].Weight; }

		var sum = w0 + w1 + w2 + w3;
		if ( sum > 1e-6f )
		{
			var inv = 1f / sum;
			w0 *= inv; w1 *= inv; w2 *= inv; w3 *= inv;
		}
		else
		{
			w0 = 1f;
		}
	}


	static ViewportMesh Extract( PolyMesh mesh, Func<int,string> nameForSlot, Skeleton rig, bool hasSkin, BodyPickEntry[] bodyPicks, int bodyCount, double loadSeconds )
	{
		var sw = Stopwatch.StartNew();
		if ( hasSkin && rig.Count > MarionettePlayer.MaxGpuBones )
			throw new NotSupportedException( $"This viewport supports at most {MarionettePlayer.MaxGpuBones} bones; this rig has {rig.Count}." );
		var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals(
			mesh, MeshNormals.DefaultSmoothingAngleDegrees );
		// Tangents, the morph corner map and the morph deltas only read the mesh: build them side by side
		// (they were ~60% of a live revision's load on a 175k-tri model, one after another).
		var tangentsTask = System.Threading.Tasks.Task.Run( () => NormalBake.ExportTangents( mesh, cornerNormals, normals ) );
		// A mesh with no shape keys needs neither (ApplyMorphWeights and the Face Poser treat null as none).
		bool morphed = System.Linq.Enumerable.Any( mesh.ValidMorphs );
		var cornerMapTask = morphed ? System.Threading.Tasks.Task.Run( () => CornerVertexMap.FromMeshBucketed( mesh, slot => nameForSlot( slot ) ) ) : System.Threading.Tasks.Task.FromResult<CornerVertexMap>( null );
		var morphsTask = morphed ? System.Threading.Tasks.Task.Run( () => RetainedMeshMorphs.FromMesh( mesh, computeNormalDeltas: true ) ) : System.Threading.Tasks.Task.FromResult<RetainedMeshMorphs>( null );
		var cornerTangents = tangentsTask.GetAwaiter().GetResult();

		var buckets = new Dictionary<int, List<int>>();
		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			var face = mesh.Faces[fi];
			if ( face.Count < 3 ) continue;
			if ( !buckets.TryGetValue( face.Material, out var list ) )
				buckets[face.Material] = list = new List<int>();
			list.Add( fi );
		}

		var vertList = new List<float>( mesh.VertexCount * FloatsPerVertex );
		var indexList = new List<uint>( mesh.FaceCount * 6 );
		var batches = new List<DrawBatch>( buckets.Count );
		var bmin = new Vector3( float.MaxValue );
		var bmax = new Vector3( float.MinValue );
		var tris = 0;

		foreach ( var (slot, faces) in buckets )
		{
			string name = nameForSlot( slot );
			var indexStart = indexList.Count;

			foreach ( var fi in faces )
			{
				var face = mesh.Faces[fi];
				var corners = cornerNormals[fi];
				var baseCorner = (uint)(vertList.Count / FloatsPerVertex);

				for ( var c = 0; c < face.Count; c++ )
				{
					var vi = face.Indices[c];
					var p = mesh.Positions[vi];
					var n = normals[corners[c]];
					vertList.Add( p.x ); vertList.Add( p.y ); vertList.Add( p.z );
					vertList.Add( n.x ); vertList.Add( n.y ); vertList.Add( n.z );

					PackBoneWeights( hasSkin ? mesh.Skin[vi] : null,
						out float b0, out float b1, out float b2, out float b3,
						out float w0, out float w1, out float w2, out float w3 );

					vertList.Add( b0 ); vertList.Add( b1 ); vertList.Add( b2 ); vertList.Add( b3 );
					vertList.Add( w0 ); vertList.Add( w1 ); vertList.Add( w2 ); vertList.Add( w3 );
					// Face.UVs is per corner: a shared position can have independent seam UVs.
					var uv = face.UVs is { Length: > 0 } && c < face.UVs.Length ? face.UVs[c] : new Vec2( 0, 0 );
					vertList.Add( uv.x ); vertList.Add( uv.y );
					var tangent = cornerTangents[fi][c];
					vertList.Add( tangent.x ); vertList.Add( tangent.y );
					vertList.Add( tangent.z ); vertList.Add( tangent.w );

					var pv = new Vector3( p.x, p.y, p.z );
					bmin = Vector3.Min( bmin, pv );
					bmax = Vector3.Max( bmax, pv );
				}

				if ( face.Count == 3 )
				{
					indexList.Add( baseCorner );
					indexList.Add( baseCorner + 1 );
					indexList.Add( baseCorner + 2 );
					tris++;
				}
				else
				{
					var polygon = new List<Vec3>( face.Count );
					for ( var k = 0; k < face.Count; k++ )
						polygon.Add( mesh.Positions[face.Indices[k]] );
					foreach ( var (a, b, c) in Triangulate.Face( polygon ) )
					{
						indexList.Add( baseCorner + (uint)a );
						indexList.Add( baseCorner + (uint)b );
						indexList.Add( baseCorner + (uint)c );
						tris++;
					}
				}
			}

			var mat = MaterialPalette.ForName( name );
			batches.Add( new DrawBatch(
				name, mat,
				indexStart * sizeof( uint ),
				indexList.Count - indexStart, slot ) );
		}

		var centre = (bmin + bmax) * 0.5f;
		var extent = bmax - bmin;
		var radius = extent.Length() * 0.5f;
		if ( radius < 1e-3f ) radius = 1f;

		sw.Stop();
		var extractedVertices = vertList.ToArray();
		var result = new ViewportMesh
		{
			Vertices = extractedVertices,
			BaseVertices = (float[])extractedVertices.Clone(),
			Indices = indexList.ToArray(),
			Batches = batches.ToArray(),
			BodyPicks = bodyPicks ?? Array.Empty<BodyPickEntry>(),
			Rig = rig,
			HasSkin = hasSkin,
			BoundsMin = bmin,
			BoundsMax = bmax,
			Centre = centre,
			Radius = radius,
			TriangleCount = tris,
			BodyCount = bodyCount,
			LoadSeconds = loadSeconds,
			ExtractSeconds = sw.Elapsed.TotalSeconds,
			MorphCornerMap = cornerMapTask.GetAwaiter().GetResult(),
			Morphs = morphsTask.GetAwaiter().GetResult(),
		};
		result.SourceMesh = mesh; result._slotNames = nameForSlot;
		result.Budget.RecordLoad( loadSeconds );
		return result;
	}

	public unsafe void Upload( GL gl )
	{
		if ( _uploaded ) return;
		var uploadClock = Stopwatch.StartNew();

		_vao = gl.GenVertexArray();
		_vbo = gl.GenBuffer();
		_ebo = gl.GenBuffer();

		gl.BindVertexArray( _vao );

		gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		fixed ( float* raw = Vertices )
		{
			gl.BufferData( BufferTargetARB.ArrayBuffer,
				(nuint)(Vertices.Length * sizeof( float )), raw,
				BufferUsageARB.StaticDraw );
		}

		gl.BindBuffer( BufferTargetARB.ElementArrayBuffer, _ebo );
		fixed ( uint* raw = Indices )
		{
			gl.BufferData( BufferTargetARB.ElementArrayBuffer,
				(nuint)(Indices.Length * sizeof( uint )), raw,
				BufferUsageARB.StaticDraw );
		}

		const uint stride = FloatsPerVertex * sizeof( float );
		gl.EnableVertexAttribArray( 0 );
		gl.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, stride, (void*)0 );
		gl.EnableVertexAttribArray( 1 );
		gl.VertexAttribPointer( 1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof( float )) );
		gl.EnableVertexAttribArray( 2 );
		gl.VertexAttribPointer( 2, 4, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof( float )) );
		gl.EnableVertexAttribArray( 3 );
		gl.VertexAttribPointer( 3, 4, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof( float )) );
		gl.EnableVertexAttribArray( 4 );
		gl.VertexAttribPointer( 4, 2, VertexAttribPointerType.Float, false, stride, (void*)(14 * sizeof( float )) );
		gl.EnableVertexAttribArray( 5 );
		gl.VertexAttribPointer( 5, 4, VertexAttribPointerType.Float, false, stride, (void*)(16 * sizeof( float )) );
		_whiteTexture = CreateTexture( gl, DecodedImage.NeutralWhite, null, true );
		_flatNormalTexture = CreateTexture( gl, DecodedImage.NeutralFlatNormal, null, false );
		_unitDataTexture = CreateTexture( gl, DecodedImage.NeutralWhite, null, false );
		_batchAlbedoTextures = new uint[Batches.Length];
		_batchNormalTextures = new uint[Batches.Length];
		_batchRoughnessTextures = new uint[Batches.Length];
		_batchMetallicTextures = new uint[Batches.Length];
		_batchEmissionTextures = new uint[Batches.Length];
		_batchOcclusionTextures = new uint[Batches.Length];
		for ( int i = 0; i < Batches.Length; i++ )
		{
			var batch = Batches[i];
			_batchAlbedoTextures[i] = Texture( gl, batch.AlbedoImage, batch.AlbedoBinding, _whiteTexture, true );
			_batchNormalTextures[i] = Texture( gl, batch.NormalImage, batch.NormalBinding, _flatNormalTexture, false );
			_batchRoughnessTextures[i] = Texture( gl, batch.RoughnessImage, batch.RoughnessBinding, _unitDataTexture, false );
			_batchMetallicTextures[i] = Texture( gl, batch.MetallicImage, batch.MetallicBinding, _unitDataTexture, false );
			// Unauthored emission map multiplies by one, preserving the existing emission factor.
			_batchEmissionTextures[i] = Texture( gl, batch.EmissionImage, batch.EmissionBinding, _whiteTexture, true );
			_batchOcclusionTextures[i] = Texture( gl, batch.OcclusionImage, batch.OcclusionBinding, _unitDataTexture, false );
		}

		gl.BindVertexArray( 0 );
		_uploaded = true;

		// M4 budget: driver-side storage for the owned maps plus the three shared fallbacks
		// (white, flat normal, unit data — one texel each).
		Budget.RecordUpload( uploadClock.Elapsed.TotalSeconds,
			_gpuTextureBytes + 3 * 4, _ownedTextures.Count + 3 );

		// CPU copies are no longer needed after upload.
		Vertices = null;
		Indices = null;
	}

	uint Texture( GL gl, DecodedImage image, TextureBinding binding, uint fallback, bool srgb )
	{
		if ( image is null ) return fallback;
		if ( binding.UvSetIndex != 0 ) throw new NotSupportedException( "Only UV set 0 is available in the viewport." );
		// Format is part of the identity: the same bytes may be sampled as color and data.
		string key = $"{image.Width}x{image.Height}:{binding.Wrap}:{binding.Filter}:{srgb}:" +
			Convert.ToHexString( SHA256.HashData( image.Rgba ) );
		if ( !_ownedTextures.TryGetValue( key, out uint texture ) )
		{
			_ownedTextures[key] = texture = CreateTexture( gl, image, binding, srgb );
			_gpuTextureBytes += (long)image.Width * image.Height * 4; // RGBA8 or sRGB8: 4 bytes/texel
		}
		return texture;
	}

	static unsafe uint CreateTexture( GL gl, DecodedImage image, TextureBinding binding, bool srgb )
	{
		uint handle = gl.GenTexture();
		gl.BindTexture( TextureTarget.Texture2D, handle );
		bool nearest = binding?.Filter == TextureFilter.Nearest;
		bool hasMips = !nearest && image.Width > 1 && image.Height > 1;
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
			(int)(nearest ? GLEnum.Nearest : (hasMips ? GLEnum.LinearMipmapLinear : GLEnum.Linear)) );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
			(int)(nearest ? GLEnum.Nearest : GLEnum.Linear) );
		GLEnum wrap = GLEnum.Repeat;
		if ( binding?.Wrap == TextureWrap.Clamp ) wrap = GLEnum.ClampToEdge;
		else if ( binding?.Wrap == TextureWrap.Mirror ) wrap = GLEnum.MirroredRepeat;
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap );
		gl.PixelStore( PixelStoreParameter.UnpackAlignment, 1 );
		// Reader row 0 is top, GL upload row 0 is bottom. Flip once here; UV V remains upward.
		int stride = image.Width * 4;
		byte[] bottomFirst = new byte[image.Rgba.Length];
		for ( int row = 0; row < image.Height; row++ )
			Array.Copy( image.Rgba, row * stride, bottomFirst, (image.Height - 1 - row) * stride, stride );
		fixed ( byte* pixels = bottomFirst )
			gl.TexImage2D( TextureTarget.Texture2D, 0, srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8,
				(uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels );

		if ( hasMips )
		{
			gl.GenerateMipmap( TextureTarget.Texture2D );
			try
			{
				gl.GetFloat( (GLEnum)0x84FF /* GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT */, out float maxAniso );
				if ( maxAniso > 1f )
					gl.TexParameter( TextureTarget.Texture2D, (TextureParameterName)0x84FE /* GL_TEXTURE_MAX_ANISOTROPY_EXT */, Math.Min( 8f, maxAniso ) );
			}
			catch { }
		}
		gl.BindTexture( TextureTarget.Texture2D, 0 );
		return handle;
	}

	/// <summary>
	/// Applies <paramref name="weights"/> to the uploaded vertex buffer, re-uploading the affected
	/// positions and normals. Pass null or empty to restore the base shape. Safe to call every frame.
	/// </summary>
	readonly Dictionary<string, float> _appliedWeights = new( StringComparer.Ordinal );
	bool _morphApplied, _morphUploaded;
	float[] _morphVertices;
	/// <summary>Bumped whenever <see cref="ApplyMorphWeights"/> rewrites <see cref="Vertices"/>.</summary>
	public int MorphRevision { get; private set; }

	bool SameWeights( IReadOnlyDictionary<string, float> weights )
	{
		int count = weights?.Count ?? 0;
		if ( count != _appliedWeights.Count ) return false;
		if ( count == 0 ) return true;
		// Concrete-type enumeration avoids boxing the enumerator every frame.
		if ( weights is Dictionary<string, float> d )
		{
			foreach ( var kv in d )
				if ( !_appliedWeights.TryGetValue( kv.Key, out var v ) || v != kv.Value ) return false;
			return true;
		}
		foreach ( var kv in weights )
			if ( !_appliedWeights.TryGetValue( kv.Key, out var v ) || v != kv.Value ) return false;
		return true;
	}

	public unsafe void ApplyMorphWeights( GL gl, IReadOnlyDictionary<string, float> weights )
	{
		if ( MorphCornerMap == null || Morphs == null ) return;

		if ( Vertices == null && BaseVertices != null )
			Vertices = (float[])BaseVertices.Clone();
		if ( Vertices == null || BaseVertices == null ) return;
		// R10: re-deform and re-upload only when the weights actually changed.
		if ( _morphApplied && _morphUploaded == _uploaded && ReferenceEquals( _morphVertices, Vertices ) && SameWeights( weights ) ) return;
		_appliedWeights.Clear();
		if ( weights != null ) foreach ( var kv in weights ) _appliedWeights[kv.Key] = kv.Value;
		_morphApplied = true; _morphUploaded = _uploaded; _morphVertices = Vertices;
		MorphRevision++;

		BaseVertices.CopyTo( Vertices, 0 );

		if ( weights != null && weights.Count > 0 )
		{
			MorphEvaluator.DeformInterleaved( BaseVertices, MorphCornerMap, weights, Morphs, Vertices, FloatsPerVertex, 0, 3 );
		}

		if ( !_uploaded ) return;
		gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		fixed ( float* raw = Vertices )
		{
			gl.BufferSubData( BufferTargetARB.ArrayBuffer, 0, (nuint)(Vertices.Length * sizeof( float )), raw );
		}
	}

	public unsafe void Draw( GL gl )
	{
		if ( !_uploaded ) return;
		gl.BindVertexArray( _vao );
		foreach ( var batch in Batches )
		{
			gl.DrawElements( PrimitiveType.Triangles, (uint)batch.IndexCount,
				DrawElementsType.UnsignedInt, (void*)batch.IndexOffset );
		}
		gl.BindVertexArray( 0 );
	}

	/// <summary>Draw with per-batch PBR material uniforms (albedo, roughness, metallic, emission).</summary>
	public unsafe void DrawBatched( GL gl, GlShader shader, int albedoLoc, int roughLoc = -1, int metalLoc = -1, int emitLoc = -1, int normalYLoc = -1, int uvScaleLoc = -1, int uvOffsetLoc = -1, int uvRotationLoc = -1, int alphaModeLoc = -1, int alphaCutoffLoc = -1, int baseAlphaLoc = -1, int normalStrengthLoc = -1, int ps1OcclusionLoc = -1, int ps1WearLoc = -1, int ps1WearColorLoc = -1, int ps1GrimeLoc = -1, int ps1GrimeFilmLoc = -1, int ps1GlowLoc = -1, int doubleSidedLoc = -1, LobeUniforms lobes = null, Action beforeTranslucent = null )
	{
		if ( !_uploaded ) return;
		gl.BindVertexArray( _vao );
		// Opaque, mask and hashed batches first; translucent ones (blend, transmission) after,
		// so refraction can sample a copy of the finished opaque scene and blending composites
		// over it. beforeTranslucent runs once between the two groups when any translucent exists.
		bool anyTranslucent = false;
		foreach ( var b in Batches ) anyTranslucent |= b.Translucent;
		// ps1_lit boot mud lives only in the main lit shader, which is the one that takes ps1 glow.
		int bootDirtLoc = ps1GlowLoc >= 0 ? shader.Uniform( "uPs1BootDirt" ) : -1;
		int bootToeLoc = bootDirtLoc >= 0 ? shader.Uniform( "uPs1BootToe" ) : -1;
		int bootCuffLoc = bootDirtLoc >= 0 ? shader.Uniform( "uPs1BootCuff" ) : -1;
		Span<uint> bound = stackalloc uint[7];
		for ( int group = 0; group < 2; group++ )
		{
		bound.Clear();
		if ( group == 1 )
		{
			if ( !anyTranslucent ) break;
			beforeTranslucent?.Invoke();
			gl.BindVertexArray( _vao );
		}
		for ( int i = 0; i < Batches.Length; i++ )
		{
			var batch = Batches[i];
			if ( batch.Translucent != ( group == 1 ) ) continue;
			// R10: skip binds the previous batch already made (fallback maps are shared).
			Bind( gl, bound, 1, _batchAlbedoTextures[i] );
			Bind( gl, bound, 2, _batchNormalTextures[i] );
			Bind( gl, bound, 3, _batchRoughnessTextures[i] );
			Bind( gl, bound, 4, _batchMetallicTextures[i] );
			Bind( gl, bound, 5, _batchEmissionTextures[i] );
			Bind( gl, bound, 6, _batchOcclusionTextures[i] );
			shader.SetFloat( normalYLoc, batch.NormalBinding?.NormalConvention == NormalMapConvention.DirectX ? -1f : 1f );
			shader.SetVec3( albedoLoc, batch.R, batch.G, batch.B );
			shader.SetFloat( roughLoc, batch.Roughness );
			shader.SetFloat( metalLoc, batch.Metallic );
			shader.SetVec3( emitLoc, batch.Er, batch.Eg, batch.Eb );
			if ( uvScaleLoc >= 0 )
			{
				// R10: per-batch UV transforms are built once, not per draw.
				_batchUv ??= BuildBatchUv();
				var uv = _batchUv[i];
				shader.SetVec2Array( uvScaleLoc, uv.Scales );
				shader.SetVec2Array( uvOffsetLoc, uv.Offsets );
				if ( uvRotationLoc >= 0 ) shader.SetFloatArray( uvRotationLoc, uv.Rotations );
			}
			if ( alphaModeLoc >= 0 ) shader.SetInt( alphaModeLoc, (int)batch.AlphaMode );
			if ( alphaCutoffLoc >= 0 ) shader.SetFloat( alphaCutoffLoc, batch.AlphaCutoff );
			if ( baseAlphaLoc >= 0 ) shader.SetFloat( baseAlphaLoc, batch.A );
			if ( normalStrengthLoc >= 0 ) shader.SetFloat( normalStrengthLoc, batch.NormalStrength );
			if ( ps1OcclusionLoc >= 0 ) shader.SetFloat( ps1OcclusionLoc, batch.Ps1Occlusion );
			if ( ps1WearLoc >= 0 ) shader.SetFloat( ps1WearLoc, batch.Ps1Wear );
			if ( ps1WearColorLoc >= 0 ) shader.SetVec3( ps1WearColorLoc, batch.Wr, batch.Wg, batch.Wb );
			if ( ps1GrimeLoc >= 0 ) shader.SetFloat( ps1GrimeLoc, batch.Ps1Grime );
			if ( ps1GrimeFilmLoc >= 0 ) shader.SetFloat( ps1GrimeFilmLoc, batch.Ps1GrimeFilm );
			if ( ps1GlowLoc >= 0 ) shader.SetFloat( ps1GlowLoc, batch.Ps1Glow );
			if ( bootDirtLoc >= 0 )
			{
				shader.SetFloat( bootDirtLoc, batch.Ps1BootDirt );
				shader.SetFloat( bootToeLoc, batch.Ps1BootToe );
				shader.SetFloat( bootCuffLoc, batch.Ps1BootCuff );
			}
			if ( doubleSidedLoc >= 0 ) shader.SetInt( doubleSidedLoc, batch.DoubleSided ? 1 : 0 );
			lobes?.Apply( shader, batch.Lobes );
			bool blend = batch.AlphaMode == AlphaMode.Blend;
			// Blended surfaces test against depth but do not occlude each other.
			if ( blend ) { gl.Enable( EnableCap.Blend ); gl.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha ); gl.DepthMask( false ); }
			gl.DrawElements( PrimitiveType.Triangles, (uint)batch.IndexCount,
				DrawElementsType.UnsignedInt, (void*)batch.IndexOffset );
			if ( blend ) { gl.Disable( EnableCap.Blend ); gl.DepthMask( true ); }
		}
		}
		gl.BindVertexArray( 0 );
		for ( int unit = 1; unit <= 6; unit++ )
		{
			gl.ActiveTexture( (TextureUnit)((int)TextureUnit.Texture0 + unit) );
			gl.BindTexture( TextureTarget.Texture2D, 0 );
		}
	}

	static void Bind( GL gl, Span<uint> bound, int unit, uint texture )
	{
		if ( bound[unit] == texture && texture != 0 ) return;
		bound[unit] = texture;
		gl.ActiveTexture( TextureUnit.Texture0 + unit );
		gl.BindTexture( TextureTarget.Texture2D, texture );
	}

	sealed record BatchUv( System.Numerics.Vector2[] Scales, System.Numerics.Vector2[] Offsets, float[] Rotations );
	BatchUv[] _batchUv;

	BatchUv[] BuildBatchUv()
	{
		var result = new BatchUv[Batches.Length];
		for ( int i = 0; i < Batches.Length; i++ )
		{
			var batch = Batches[i];
			var scales = new System.Numerics.Vector2[6];
			var offsets = new System.Numerics.Vector2[6];
			var rotations = new float[6];
			TextureBinding[] bindings = { batch.AlbedoBinding, batch.NormalBinding, batch.RoughnessBinding,
				batch.MetallicBinding, batch.EmissionBinding, batch.OcclusionBinding };
			for ( int c = 0; c < 6; c++ )
			{
				var b = bindings[c];
				scales[c] = b is null ? new System.Numerics.Vector2( 1f, 1f ) : new System.Numerics.Vector2( b.UvScale.x, b.UvScale.y );
				offsets[c] = b is null ? System.Numerics.Vector2.Zero : new System.Numerics.Vector2( b.UvOffset.x, b.UvOffset.y );
				rotations[c] = b is null ? 0f : b.UvRotation;
			}
			result[i] = new BatchUv( scales, offsets, rotations );
		}
		return result;
	}

	public void Dispose()
	{
		// GL delete happens via Release on the GL thread; this is a soft clear.
		Vertices = null;
		Indices = null;
		Batches = null;
	}

	public void ReleaseGpu( GL gl )
	{
		if ( !_uploaded ) return;
		gl.DeleteVertexArray( _vao );
		gl.DeleteBuffer( _vbo );
		gl.DeleteBuffer( _ebo );
		gl.DeleteTexture( _whiteTexture );
		gl.DeleteTexture( _flatNormalTexture );
		gl.DeleteTexture( _unitDataTexture );
		foreach ( var texture in _ownedTextures.Values ) gl.DeleteTexture( texture );
		_ownedTextures.Clear();
		Budget.RecordRelease();
		_gpuTextureBytes = 0;
		_batchAlbedoTextures = null;
		_batchNormalTextures = _batchRoughnessTextures = _batchMetallicTextures = _batchEmissionTextures = null;
		_batchOcclusionTextures = null;
		_batchUv = null;
		_morphApplied = false;
		_uploaded = false;
	}
}
