using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// CPU-side geometry extracted from a rebuilt PartStudio, grouped by material for one VBO /
/// ~N draw calls. Built off the GL thread; uploaded once on the GL thread.
/// </summary>
public sealed class CpuMesh : IDisposable
{
	public const int FloatsPerVertex = 24; // pos(3) + nrm(3) + boneIndices(4) + boneWeights(4) + UV(2) + tangent/sign(4) + color(4)

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
		/// <summary>Wave-2 look wiring: the SlotLook spectrum for this batch, from its descriptor.
		/// Defaults are the old behaviour, so batches from name-based palette entries (which
		/// never set a look) shade exactly as before.</summary>
		public readonly float LookToon, LookToonRim, LookStylized, LookOutlineWidth, LookOpacity, LookIridescence;
		public readonly int LookToonBands;
		public readonly float LookOutlineR, LookOutlineG, LookOutlineB;
		public readonly float LookShadowR, LookShadowG, LookShadowB;
		public readonly float LookClearcoat, LookClearcoatRough, LookSubsurface;
		public readonly float LookSubsurfaceR, LookSubsurfaceG, LookSubsurfaceB;
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
			LookToon = descriptor.LookToon; LookToonBands = descriptor.LookToonBands; LookToonRim = descriptor.LookToonRim;
			LookStylized = descriptor.LookStylized; LookOutlineWidth = descriptor.LookOutlineWidth;
			LookOutlineR = descriptor.LookOutlineColor.x; LookOutlineG = descriptor.LookOutlineColor.y; LookOutlineB = descriptor.LookOutlineColor.z;
			LookOpacity = descriptor.LookOpacity;
			LookShadowR = descriptor.LookShadowTint.x; LookShadowG = descriptor.LookShadowTint.y; LookShadowB = descriptor.LookShadowTint.z;
			LookIridescence = descriptor.LookIridescence;
			LookClearcoat = descriptor.Clearcoat; LookClearcoatRough = descriptor.ClearcoatRoughness;
			LookSubsurface = descriptor.Subsurface;
			LookSubsurfaceR = descriptor.LookSubsurfaceColor.x; LookSubsurfaceG = descriptor.LookSubsurfaceColor.y; LookSubsurfaceB = descriptor.LookSubsurfaceColor.z;
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
	public CpuMesh Remesh( PolyMesh modified )
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

	/// <summary>Resolve/decode authored maps before upload, on the document worker thread.</summary>
	public void ApplyMaterials( MaterialResolver resolver )
	{
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

	public static CpuMesh FromStudio( PartStudio studio, double loadSeconds, string documentPath )
	{
		var sw = Stopwatch.StartNew();
		if ( LivePieces is { } pieces
			&& !System.IO.Path.GetFileName( documentPath ).Equals( "camhead.effigy", StringComparison.OrdinalIgnoreCase )
			&& FromStudioPieces( studio, loadSeconds, pieces ) is { } incremental )
			return incremental;
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

	/// <summary>Live panes: the extracted pieces of each body from earlier revisions, keyed by a hash of
	/// the body's geometry, so a revision rebuilds only the bodies it changed. One per follower.</summary>
	public sealed class PieceCache
	{
		internal sealed class Piece { public CpuMesh Part; public Vector3 PickMin, PickMax; public int Used; }
		internal readonly Dictionary<(ulong, int, int), Piece> Map = new();
		internal int Generation;
		/// <summary>Bodies reused / rebuilt by the last build.</summary>
		public int Hits { get; internal set; }
		public int Misses { get; internal set; }
	}

	/// <summary>Set by a live follower around its build (same thread) to take the incremental path.</summary>
	[ThreadStatic] public static PieceCache LivePieces;

	/// <summary>Per-body layout of <see cref="Vertices"/> (piece key and float offset), so an upload over the
	/// previous revision's buffers writes only the bodies that changed.</summary>
	public (ulong, int, int)[] LayoutKeys;
	public int[] LayoutOffsets, LayoutLengths;

	static unsafe ulong GeometryHash( PolyMesh m )
	{
		// FNV-1a over 8-byte words of the positions, then each face's indices, UVs and material.
		ulong h = 1469598103934665603UL;
		static ulong Mix( ulong h, ReadOnlySpan<byte> bytes )
		{
			var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>( bytes );
			foreach ( var w in words ) h = (h ^ w) * 1099511628211UL;
			for ( int i = words.Length * 8; i < bytes.Length; i++ ) h = (h ^ bytes[i]) * 1099511628211UL;
			return h;
		}
		h = Mix( h, System.Runtime.InteropServices.MemoryMarshal.AsBytes( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( m.Positions ) ) );
		foreach ( var f in m.Faces )
		{
			h = (h ^ (ulong)(uint)f.Material ^ ((ulong)(uint)f.Count << 32)) * 1099511628211UL;
			h = Mix( h, System.Runtime.InteropServices.MemoryMarshal.AsBytes( f.Indices.AsSpan() ) );
			if ( f.UVs is { } uv ) h = Mix( h, System.Runtime.InteropServices.MemoryMarshal.AsBytes( uv.AsSpan() ) );
			else h = (h ^ 0x9E3779B97F4A7C15UL) * 1099511628211UL;
		}
		return h;
	}

	/// <summary>The same mesh <see cref="FromStudio"/> builds for an unrigged, shape-key-free document, made
	/// from cached per-body pieces: only bodies whose geometry changed are re-extracted (in parallel), the
	/// rest are copied in. Vertices are laid out body by body; batches stay one per material slot.
	/// Null (take the full path) for anything rigged or with shape keys.</summary>
	static CpuMesh FromStudioPieces( PartStudio studio, double loadSeconds, PieceCache cache )
	{
		var sw = Stopwatch.StartNew();
		if ( studio.Rig is { Count: > 0 } ) return null;
		var render = new List<Body>();
		foreach ( var b in studio.Bodies )
		{
			if ( b.IsReference || !b.Visible || b.Mesh is null ) continue;
			if ( b.Mesh.Skin is not null || System.Linq.Enumerable.Any( b.Mesh.ValidMorphs ) ) return null;
			if ( b.Mesh.FaceCount > 0 && b.Mesh.VertexCount > 0 ) render.Add( b );
		}
		if ( render.Count == 0 ) return null;
		// the merged authored mesh, for the live modifier stack (SourceMesh), beside everything else
		var sourceTask = System.Threading.Tasks.Task.Run( () => studio.ToMeshWithBodies( b => b.Visible ).Mesh );

		int gen = ++cache.Generation, hits = 0, misses = 0;
		var keys = new (ulong, int, int)[render.Count];
		var parts = new PieceCache.Piece[render.Count];
		System.Threading.Tasks.Parallel.For( 0, render.Count, i =>
		{
			var m = render[i].Mesh;
			keys[i] = (GeometryHash( m ), m.VertexCount, m.FaceCount);
		} );
		var build = new List<int>();
		for ( int i = 0; i < render.Count; i++ )
		{
			if ( cache.Map.TryGetValue( keys[i], out var hit ) ) { parts[i] = hit; hits++; }
			else build.Add( i );
		}
		System.Threading.Tasks.Parallel.ForEach( build, i =>
		{
			var m = render[i].Mesh;
			var piece = new PieceCache.Piece { Part = Extract( m, _ => "", null, false, null, 1, 0, retained: false ) };
			var pmin = new Vector3( float.MaxValue ); var pmax = new Vector3( float.MinValue );
			foreach ( var p in m.Positions ) { var v = new Vector3( p.x, p.y, p.z ); pmin = Vector3.Min( pmin, v ); pmax = Vector3.Max( pmax, v ); }
			piece.PickMin = pmin; piece.PickMax = pmax;
			parts[i] = piece;
		} );
		misses = build.Count;
		for ( int i = 0; i < render.Count; i++ ) { parts[i].Used = gen; cache.Map[keys[i]] = parts[i]; }
		// keep pieces seen in the last few revisions (a scrub back and forth, an undo) and drop the rest
		foreach ( var stale in System.Linq.Enumerable.ToList( System.Linq.Enumerable.Where( cache.Map, kv => gen - kv.Value.Used > 3 ) ) )
			cache.Map.Remove( stale.Key );
		cache.Hits = hits; cache.Misses = misses;

		// merge: vertices body by body, indices grouped by material slot in first-seen order
		var offsets = new int[render.Count]; var lengths = new int[render.Count];
		int floats = 0, indexTotal = 0, tris = 0;
		for ( int i = 0; i < render.Count; i++ )
		{
			offsets[i] = floats; lengths[i] = parts[i].Part.Vertices.Length;
			floats += lengths[i]; indexTotal += parts[i].Part.Indices.Length; tris += parts[i].Part.TriangleCount;
		}
		var vertices = new float[floats];
		var indices = new uint[indexTotal];
		System.Threading.Tasks.Parallel.For( 0, render.Count, i =>
			Array.Copy( parts[i].Part.Vertices, 0, vertices, offsets[i], lengths[i] ) );
		var slots = new List<int>();
		foreach ( var piece in parts )
			foreach ( var batch in piece.Part.Batches )
				if ( !slots.Contains( batch.MaterialSlot ) ) slots.Add( batch.MaterialSlot );
		var batches = new List<DrawBatch>( slots.Count );
		int at = 0;
		var bmin = new Vector3( float.MaxValue ); var bmax = new Vector3( float.MinValue );
		foreach ( var slot in slots )
		{
			int start = at;
			for ( int i = 0; i < parts.Length; i++ )
			{
				var part = parts[i].Part;
				uint baseVertex = (uint)(offsets[i] / FloatsPerVertex);
				foreach ( var batch in part.Batches )
				{
					if ( batch.MaterialSlot != slot ) continue;
					int first = batch.IndexOffset / sizeof( uint );
					for ( int k = 0; k < batch.IndexCount; k++ ) indices[at++] = part.Indices[first + k] + baseVertex;
				}
			}
			string name = studio.NameForSlot( slot );
			batches.Add( new DrawBatch( name, MaterialPalette.ForName( name ), start * sizeof( uint ), at - start, slot ) );
		}
		foreach ( var piece in parts ) { bmin = Vector3.Min( bmin, piece.Part.BoundsMin ); bmax = Vector3.Max( bmax, piece.Part.BoundsMax ); }

		var bodyPicks = new List<BodyPickEntry>( studio.Bodies.Count );
		var picker = new MeshPicker();
		foreach ( var body in studio.Bodies )
		{
			if ( !body.Visible || body.Mesh is null || body.Mesh.VertexCount == 0 ) continue;
			int i = render.IndexOf( body );
			Vector3 pmin, pmax;
			if ( i >= 0 ) { pmin = parts[i].PickMin; pmax = parts[i].PickMax; }
			else
			{
				pmin = new Vector3( float.MaxValue ); pmax = new Vector3( float.MinValue );
				foreach ( var p in body.Mesh.Positions ) { var v = new Vector3( p.x, p.y, p.z ); pmin = Vector3.Min( pmin, v ); pmax = Vector3.Max( pmax, v ); }
			}
			studio.BodyBoneMap.TryGetValue( body.Id, out var boneName );
			picker.Add( body.Mesh );            // Add copies what it needs; no skin to bind on this path
			bodyPicks.Add( new BodyPickEntry( body.Id, body.Name ?? body.Id, boneName ?? "", pmin, pmax ) );
		}

		var extent = bmax - bmin;
		float radius = extent.Length() * 0.5f;
		if ( radius < 1e-3f ) radius = 1f;
		var result = new CpuMesh
		{
			Vertices = vertices, BaseVertices = vertices,   // no shape keys: nothing ever deforms a copy
			Indices = indices, Batches = batches.ToArray(), BodyPicks = bodyPicks.ToArray(),
			Rig = studio.Rig, HasSkin = false, BoundsMin = bmin, BoundsMax = bmax, Centre = (bmin + bmax) * 0.5f,
			Radius = radius, TriangleCount = tris, BodyCount = studio.Bodies.Count, LoadSeconds = loadSeconds,
		};
		result.Picker = picker;
		result.LayoutKeys = keys; result.LayoutOffsets = offsets; result.LayoutLengths = lengths;
		result.SourceMesh = sourceTask.GetAwaiter().GetResult();
		result._slotNames = slot => studio.NameForSlot( slot );
		result.Budget.RecordLoad( loadSeconds );
		result.ExtractSeconds = sw.Elapsed.TotalSeconds;
		return result;
	}

	/// <summary>Extract a standalone authored mesh using the same corner-vertex path as document loading.</summary>
	public static CpuMesh FromPolyMesh( PolyMesh mesh, Func<int, string> nameForSlot )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		ArgumentNullException.ThrowIfNull( nameForSlot );
		if ( mesh.FaceCount == 0 ) throw new ArgumentException( "Mesh has no faces.", nameof( mesh ) );
		return Extract( mesh, nameForSlot, null, false, Array.Empty<BodyPickEntry>(), 1, 0 );
	}


	public static CpuMesh FromFbx( Effigy.FbxImport.Result imported, double loadSeconds, string documentPath )
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


	static CpuMesh Extract( PolyMesh mesh, Func<int,string> nameForSlot, Skeleton rig, bool hasSkin, BodyPickEntry[] bodyPicks, int bodyCount, double loadSeconds , bool retained = true )
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
					// Wave-5 look fix: per-corner vertex colour (foliage, paint); white when the mesh carries none.
					// Merged meshes pad colourless bodies with Vec4.Zero (MeshTransform.Append) — an exact-zero
					// vector means "no paint", NOT black paint, so it resolves to white. Only the exact zero
					// vector is remapped: a wind-zero foliage trunk vertex (0.4, 0.3, 0.2, 0) keeps its brown.
					var vc = mesh.VertexColors is { Length: > 0 } && vi < mesh.VertexColors.Length ? mesh.VertexColors[vi] : new Vec4( 1f, 1f, 1f, 1f );
					if ( vc.x == 0f && vc.y == 0f && vc.z == 0f && vc.w == 0f ) vc = new Vec4( 1f, 1f, 1f, 1f );
					vertList.Add( vc.x ); vertList.Add( vc.y ); vertList.Add( vc.z ); vertList.Add( vc.w );

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
		var result = new CpuMesh
		{
			Vertices = extractedVertices,
			BaseVertices = retained ? (float[])extractedVertices.Clone() : null,   // a cached piece is only copied from
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

	// ---- CPU morph deformation (was ApplyMorphWeights(GL,...)); GPU wrapper re-uploads when this returns true ----
	readonly Dictionary<string, float> _appliedWeights = new( StringComparer.Ordinal );
	bool _morphApplied, _morphResident;
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

	/// <summary>Deform <see cref="Vertices"/> from <see cref="BaseVertices"/>. Returns true when Vertices changed and a
	/// resident GPU copy must be re-uploaded. <paramref name="gpuResident"/> is the caller's "an uploaded copy exists" flag.</summary>
	public bool ApplyMorphWeights( IReadOnlyDictionary<string, float> weights, bool gpuResident = false )
	{
		if ( MorphCornerMap == null || Morphs == null ) return false;
		if ( Vertices == null && BaseVertices != null ) Vertices = (float[])BaseVertices.Clone();
		if ( Vertices == null || BaseVertices == null ) return false;
		if ( _morphApplied && _morphResident == gpuResident && ReferenceEquals( _morphVertices, Vertices ) && SameWeights( weights ) ) return false;
		_appliedWeights.Clear();
		if ( weights != null ) foreach ( var kv in weights ) _appliedWeights[kv.Key] = kv.Value;
		_morphApplied = true; _morphResident = gpuResident; _morphVertices = Vertices;
		MorphRevision++;
		BaseVertices.CopyTo( Vertices, 0 );
		if ( weights != null && weights.Count > 0 )
			MorphEvaluator.DeformInterleaved( BaseVertices, MorphCornerMap, weights, Morphs, Vertices, FloatsPerVertex, 0, 3 );
		return true;
	}

	/// <summary>Desktop-only policy: after GL upload the CPU interleaved/index copies are released.</summary>
	public void ReleaseRenderBuffers() { Vertices = null; Indices = null; }

	public void Dispose() { Vertices = null; Indices = null; Batches = null; }
}