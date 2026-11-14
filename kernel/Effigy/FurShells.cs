using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fur the way s&amp;box draws it: stacked shells for <c>shaders/fur.shader</c>.
///
/// THE SHADER GROWS NOTHING. It is a shell shader (sbox/core/shaders/fur.shader): the mesh must
/// already hold the copies of the surface, stepped outward, and each copy's mesh vertex colour RED
/// says how high it is - 0 at the root layer, 1 at the tips. The pixel shader keeps a pixel where the
/// strand noise is above that height, so low shells are nearly solid and high ones are only the
/// strand tips; the vertex shader sways a vertex by red times the wind, so the roots stay put. This
/// is what Facepunch's own cardigan, Santa and teddy-bear clothing ships.
///
/// RED RIDES THE EXPORT ALREADY. <see cref="PolyMesh.VertexColors"/> goes out through
/// <see cref="Vec4.Tint"/>, which maps (h, 1, 1, 1) to a colour of (h, 1, 1) - red exactly h, as bytes
/// (see DmxText.Color for why bytes).
///
/// FLOW-COMBED SHELLS (spec 3.1). With <see cref="Options.AnatomicalFlow"/> (the default) the
/// shells no longer extrude strictly along the vertex normals (pin-cushion). Grow computes the
/// anatomical flow field — the geodesic gradient away from the nose/head root toward the tail
/// and extremities, projected onto each vertex tangent plane — and displaces each shell layer
/// along a curved cantilever arc (normal lift + tangent lay + droop), so a wolf-body pelt lies
/// nose-to-tail instead of standing straight out. Vertex colours now carry the guard-hair /
/// undercoat / agouti encoding: R = shell height h, G = undercoat mask (1 in dense undercoat,
/// tapering to 0 at the guard-hair tips), B = agouti band (sin(pi*h), the warm mid-shaft band).
/// </summary>
public static class FurShells
{
	public sealed class Options
	{
		public float Length = 1f;
		public int Layers = 12;

		/// <summary>0..1 - how much gravity bends the tips down.</summary>
		public float Droop = 0.3f;

		public int MaterialSlot;

		/// <summary>Per-vertex flow direction (one per mesh vertex), or null to compute it.
		/// Each vector is projected onto its vertex tangent plane and normalised; a zero
		/// vector at a vertex falls back to the anatomical field for that vertex.</summary>
		public Vec3[]? TangentFlow;

		/// <summary>When true (the default), Grow computes the anatomical flow field: the
		/// geodesic gradient away from the nose/head root toward the tail and extremities.
		/// When false and no <see cref="TangentFlow"/> is given, shells extrude strictly
		/// along the vertex normals (the legacy pin-cushion path).</summary>
		public bool AnatomicalFlow = true;

		/// <summary>Lay angle in degrees, 0..75. How flat the pelt lies against the body:
		/// 65 for a sleek panther/seal/horse coat, 20 for a fluffy bear/wolf mane. Only
		/// applies while a flow field is active; with no flow the shells stay normal.</summary>
		public float LayAngleDegrees = 65f;

		/// <summary>Nose/head root the anatomical flow runs away from. When unset, the
		/// vertex with the greatest +X is used: Effigy heads face +X (the FaceFeatures nose
		/// bump is at x &gt; 0, RigTemplates measure "nose to rump" from high +X), so +X is
		/// the nose end of a quadruped body and -X is the tail.</summary>
		public Vec3 FlowRoot;
		public bool HasFlowRoot;

		/// <summary>Dihedral angle in degrees above which <see cref="GrowWithFins"/> adds a
		/// silhouette fin card on an interior edge (a crease, e.g. spine or jaw). Boundary
		/// edges always get fins from <see cref="GrowFins"/>.</summary>
		public float DihedralFinDegrees = 35f;
	}

	/// <summary>
	/// Add shells over <paramref name="faces"/> of <paramref name="mesh"/>, in place. Returns how many
	/// faces were added.
	/// </summary>
	public static int Grow( PolyMesh mesh, IReadOnlyCollection<int> faces, Options o )
	{
		if ( faces.Count == 0 )
			return 0;

		var layers = Math.Clamp( o.Layers, 2, 64 );
		var normals = mesh.ComputeVertexNormals();
		var baseCount = mesh.VertexCount;

		// Everything already there keeps whatever colour it had; untouched vertices read as "no colour"
		// (w = 0), which Tint turns into white, which the Complex shader ignores anyway.
		var colors = new List<Vec4>( mesh.HasVertexColors ? mesh.VertexColors : new Vec4[baseCount] );

		var used = new SortedSet<int>();

		foreach ( var fi in faces )
		{
			foreach ( var vi in mesh.Faces[fi].Indices )
				used.Add( vi );
		}

		// The flow field is computed once, before any shell vertex exists, so the gradient is the
		// body's own anatomy and not the shells'. A vertex with no flow (Zero) extrudes along its
		// normal exactly like the legacy path.
		var flow = FlowField( mesh, normals, o );
		var lay = Math.Clamp( o.LayAngleDegrees, 0f, 75f ) * MathF.PI / 180f;

		var sourceFaces = faces.Select( fi => mesh.Faces[fi] ).ToList();
		var added = 0;

		for ( var layer = 0; layer < layers; layer++ )
		{
			var h = layer / (float)(layers - 1);

			var map = new Dictionary<int, int>( used.Count );

			foreach ( var vi in used )
			{
				// Curved cantilever arc (spec 3.1.A.1): the shell rises along the normal while
				// laying over along the flow tangent, so the pelt lies nose-to-tail instead of
				// standing straight out. At h = 0 the lift is the legacy root lift (0.03 * L)
				// and the tangent term is 0, so the root layer still sits on the skin.
				// With no flow at this vertex, cos = 1 and sin = 0 and this is exactly the
				// old displacement: normal lift + (0, 0, -sag).
				var t = flow[vi];
				var hasFlow = t.LengthSquared > 1e-12f;
				var cosLay = hasFlow ? MathF.Cos( lay * h ) : 1f;
				var sinLay = hasFlow ? MathF.Sin( lay * h ) : 0f;
				var lift = o.Length * (0.03f + 0.97f * h * cosLay);
				var layOut = o.Length * MathF.Pow( h, 1.4f ) * sinLay;
				var sag = o.Length * Math.Clamp( o.Droop, 0f, 1f ) * h * h;
				var p = mesh.Positions[vi] + normals[vi] * lift + t * layOut + new Vec3( 0, 0, -sag );
				map[vi] = mesh.AddVertex( p );
				colors.Add( ShellColor( h ) );

				if ( mesh.Skin is not null && vi < mesh.Skin.Count )
					mesh.Skin.Vertices.Add( (BoneWeight[])mesh.Skin[vi].Clone() );
			}

			foreach ( var f in sourceFaces )
			{
				var indices = f.Indices.Select( vi => map[vi] ).ToArray();
				mesh.AddFace( indices, (Vec2[])f.UVs?.Clone(), o.MaterialSlot );
				added++;
			}
		}

		mesh.VertexColors = colors.ToArray();
		return added;
	}

	/// <summary>Guard-hair + undercoat + agouti encoding for a shell at height
	/// <paramref name="h"/> (0 at the root, 1 at the tips). R = h for the fur shader's height
	/// clip; G = undercoat mask (1 in dense undercoat below h = 0.65, tapering to 0 at the
	/// guard-hair tips); B = agouti band, sin(pi*h), the warm mid-shaft band of a
	/// wolf/rodent/wildcat pelt.</summary>
	public static Vec4 ShellColor( float h )
	{
		h = Math.Clamp( h, 0f, 1f );
		var undercoat = h < 0.65f ? 1f : MathF.Max( 0f, 1f - (h - 0.65f) / 0.35f );
		var agouti = MathF.Sin( MathF.PI * h );
		return new Vec4( h, undercoat, agouti, 1f );
	}

	/// <summary>Anatomical flow field, one vector per mesh vertex (Zero where there is no
	/// flow). The field is the gradient of the geodesic distance from the nose/head root:
	/// distances come from Dijkstra over mesh edges (edge length = weight), and the gradient
	/// at each vertex is the least-squares estimate from its incident edges,
	/// sum (d_u - d_v) * (p_u - p_v) / |p_u - p_v|^2, projected onto the vertex tangent plane.
	/// It points away from the nose toward the tail and extremities. An explicit
	/// <see cref="Options.TangentFlow"/> overrides the computed field per vertex; with
	/// neither, every vector is Zero and Grow falls back to pure normal extrusion.</summary>
	public static Vec3[] FlowField( PolyMesh mesh, Vec3[] normals, Options o )
	{
		var flow = new Vec3[mesh.VertexCount];
		if ( mesh.VertexCount == 0 )
			return flow;

		var hasProvided = o.TangentFlow is not null;
		if ( !o.AnatomicalFlow && !hasProvided )
			return flow;

		// Adjacency over every face, so the field crosses the whole body, not just the
		// selected (furred) faces.
		var adj = new List<(int to, float w)>[mesh.VertexCount];
		for ( var i = 0; i < adj.Length; i++ )
			adj[i] = new List<(int, float)>();
		foreach ( var f in mesh.Faces )
		{
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				var a = f.Indices[k];
				var b = f.Indices[(k + 1) % f.Indices.Length];
				if ( a == b || a < 0 || b < 0 || a >= mesh.VertexCount || b >= mesh.VertexCount )
					continue;
				var w = (mesh.Positions[b] - mesh.Positions[a]).Length;
				if ( w < 1e-9f )
					continue;
				adj[a].Add( (b, w) );
				adj[b].Add( (a, w) );
			}
		}

		// Geodesic distances from the seed, if the anatomical field is wanted.
		var dist = new float[mesh.VertexCount];
		for ( var i = 0; i < dist.Length; i++ )
			dist[i] = float.PositiveInfinity;
		if ( o.AnatomicalFlow )
		{
			var seed = -1;
			if ( o.HasFlowRoot )
			{
				var best = float.PositiveInfinity;
				for ( var i = 0; i < mesh.VertexCount; i++ )
				{
					var d = (mesh.Positions[i] - o.FlowRoot).LengthSquared;
					if ( d < best ) { best = d; seed = i; }
				}
			}
			else
			{
				// Nose end: greatest +X (Effigy heads face +X).
				var best = float.NegativeInfinity;
				for ( var i = 0; i < mesh.VertexCount; i++ )
				{
					if ( mesh.Positions[i].x > best ) { best = mesh.Positions[i].x; seed = i; }
				}
			}

			if ( seed >= 0 )
			{
				dist[seed] = 0f;
				var pq = new PriorityQueue<int, float>();
				pq.Enqueue( seed, 0f );
				while ( pq.TryDequeue( out var v, out var dv ) )
				{
					if ( dv > dist[v] )
						continue;
					foreach ( var (to, w) in adj[v] )
					{
						var nd = dv + w;
						if ( nd < dist[to] )
						{
							dist[to] = nd;
							pq.Enqueue( to, nd );
						}
					}
				}
			}
		}

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			Vec3 t;
			if ( hasProvided && vi < o.TangentFlow!.Length && o.TangentFlow[vi].LengthSquared > 1e-12f )
			{
				t = o.TangentFlow[vi];
			}
			else if ( o.AnatomicalFlow && float.IsFinite( dist[vi] ) )
			{
				var g = Vec3.Zero;
				foreach ( var (to, w) in adj[vi] )
				{
					if ( !float.IsFinite( dist[to] ) )
						continue;
					var edge = mesh.Positions[to] - mesh.Positions[vi];
					var len2 = edge.LengthSquared;
					if ( len2 < 1e-12f )
						continue;
					g += edge * ((dist[to] - dist[vi]) / len2);
				}
				t = g;
			}
			else
			{
				flow[vi] = Vec3.Zero;
				continue;
			}

			// Project onto the tangent plane and normalise; a degenerate projection means
			// the flow runs along the normal here, i.e. no lay at this vertex.
			var n = normals[vi];
			t -= n * Vec3.Dot( t, n );
			flow[vi] = t.LengthSquared > 1e-12f ? t.Normal : Vec3.Zero;
		}

		return flow;
	}

	/// <summary>Fin cards at the silhouette: one vertical card per boundary edge
	/// (an edge used by exactly one selected face), rising Length along the edge's
	/// average vertex normal with droop sag at the tip. Shells alone read as a
	/// hard cut-out edge in profile; fins break that edge. Returns faces added.</summary>
	public static int GrowFins( PolyMesh mesh, IReadOnlyCollection<int> faces, float length, float droop, int materialSlot )
	{
		if ( faces.Count == 0 || length <= 0f ) return 0;
		var normals = mesh.ComputeVertexNormals();
		var edgeUse = new Dictionary<(int, int), int>();
		foreach ( var fi in faces )
		{
			var f = mesh.Faces[fi];
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				var a = f.Indices[k]; var b = f.Indices[(k + 1) % f.Indices.Length];
				var key = a < b ? (a, b) : (b, a);
				edgeUse[key] = edgeUse.TryGetValue( key, out var n ) ? n + 1 : 1;
			}
		}
		var hadColors = mesh.HasVertexColors;
		var colors = hadColors ? new List<Vec4>( mesh.VertexColors ) : null;
		var added = 0;
		foreach ( var (edge, use) in edgeUse )
		{
			if ( use != 1 ) continue;
			var (a, b) = edge;
			var na = normals[a]; var nb = normals[b];
			var sag = length * Math.Clamp( droop, 0f, 1f );
			var topA = mesh.Positions[a] + na * length + new Vec3( 0, 0, -sag );
			var topB = mesh.Positions[b] + nb * length + new Vec3( 0, 0, -sag );
			var ia = mesh.AddVertex( mesh.Positions[a] ); var ib = mesh.AddVertex( mesh.Positions[b] );
			var ita = mesh.AddVertex( topA ); var itb = mesh.AddVertex( topB );
			if ( colors is not null ) { colors.Add( ShellColor( 0f ) ); colors.Add( ShellColor( 0f ) ); colors.Add( ShellColor( 1f ) ); colors.Add( ShellColor( 1f ) ); }
			mesh.AddFace( new[] { ia, ib, itb, ita }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, materialSlot );
			added++;
		}
		if ( colors is not null ) mesh.VertexColors = colors.ToArray();
		return added;
	}

	/// <summary>Hybrid shells + silhouette fins (spec 3.1.A.3): stacked flow-combed shells
	/// from <see cref="Grow"/>, boundary fin cards from <see cref="GrowFins"/>, and extra fin
	/// ribbons on interior edges with a high dihedral angle (creases such as the spine or
	/// jaw, where shells alone show staircase gaps at glancing silhouette edges). 16 shells
	/// + fins read as a continuous pelt at the silhouette. Returns total faces added.</summary>
	public static int GrowWithFins( PolyMesh mesh, IReadOnlyCollection<int> faces, Options o )
	{
		var added = Grow( mesh, faces, o );
		added += GrowFins( mesh, faces, o.Length, o.Droop, o.MaterialSlot );
		added += GrowDihedralFins( mesh, faces, o );
		return added;
	}

	/// <summary>Fin ribbons on interior crease edges: an edge shared by exactly two selected
	/// faces whose face normals differ by more than <see cref="Options.DihedralFinDegrees"/>.
	/// Boundary edges are skipped here — <see cref="GrowFins"/> already covers them.</summary>
	static int GrowDihedralFins( PolyMesh mesh, IReadOnlyCollection<int> faces, Options o )
	{
		if ( faces.Count == 0 || o.Length <= 0f ) return 0;
		var threshold = Math.Clamp( o.DihedralFinDegrees, 5f, 170f );
		var edgeFaces = new Dictionary<(int, int), List<int>>();
		foreach ( var fi in faces )
		{
			var f = mesh.Faces[fi];
			for ( var k = 0; k < f.Indices.Length; k++ )
			{
				var a = f.Indices[k]; var b = f.Indices[(k + 1) % f.Indices.Length];
				var key = a < b ? (a, b) : (b, a);
				if ( !edgeFaces.TryGetValue( key, out var list ) )
					edgeFaces[key] = list = new List<int>();
				list.Add( fi );
			}
		}
		var normals = mesh.ComputeVertexNormals();
		var colors = mesh.HasVertexColors ? new List<Vec4>( mesh.VertexColors ) : null;
		var added = 0;
		foreach ( var (edge, fl) in edgeFaces )
		{
			if ( fl.Count != 2 ) continue;
			var n0 = mesh.FaceNormal( mesh.Faces[fl[0]] );
			var n1 = mesh.FaceNormal( mesh.Faces[fl[1]] );
			var cos = Math.Clamp( Vec3.Dot( n0, n1 ), -1f, 1f );
			var angle = MathF.Acos( cos ) * 180f / MathF.PI;
			if ( angle < threshold ) continue;
			var (a, b) = edge;
			var na = normals[a]; var nb = normals[b];
			var sag = o.Length * Math.Clamp( o.Droop, 0f, 1f );
			var topA = mesh.Positions[a] + na * o.Length + new Vec3( 0, 0, -sag );
			var topB = mesh.Positions[b] + nb * o.Length + new Vec3( 0, 0, -sag );
			var ia = mesh.AddVertex( mesh.Positions[a] ); var ib = mesh.AddVertex( mesh.Positions[b] );
			var ita = mesh.AddVertex( topA ); var itb = mesh.AddVertex( topB );
			if ( colors is not null ) { colors.Add( ShellColor( 0f ) ); colors.Add( ShellColor( 0f ) ); colors.Add( ShellColor( 1f ) ); colors.Add( ShellColor( 1f ) ); }
			mesh.AddFace( new[] { ia, ib, itb, ita }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, o.MaterialSlot );
			added++;
		}
		if ( colors is not null ) mesh.VertexColors = colors.ToArray();
		return added;
	}

	/// <summary>Faces within <paramref name="width"/> of an opening - a collar, a cuff, a hem.</summary>
	public static List<int> TrimFaces( PolyMesh mesh, float width )
	{
		var weights = OpeningWeights( mesh, width );
		var faces = new List<int>();

		for ( var fi = 0; fi < mesh.FaceCount; fi++ )
		{
			if ( mesh.Faces[fi].Indices.Any( vi => weights[vi] > 0f ) )
				faces.Add( fi );
		}

		return faces;
	}

	/// <summary>
	/// Per vertex, how near an opening it is. A solidified garment's openings are closed by its rim,
	/// so they are measured on its outer wall and read back onto both walls - see
	/// MeshSolidify.OuterSheet. Vertices past the walls (shells already grown) are never near one.
	/// </summary>
	static float[] OpeningWeights( PolyMesh mesh, float width )
	{
		var sheet = MeshSolidify.OuterSheet( mesh, out var offset );

		if ( sheet is null )
			return GarmentFit.FlareWeights( mesh, GarmentFit.BoundaryVertices( mesh ), width );

		var onSheet = GarmentFit.FlareWeights( sheet, GarmentFit.BoundaryVertices( sheet ), width );
		var weights = new float[mesh.VertexCount];

		for ( var vi = 0; vi < weights.Length && vi < 2 * offset; vi++ )
			weights[vi] = onSheet[vi % offset];

		return weights;
	}

	/// <summary>
	/// The outward-facing faces of a solidified garment: the ones whose normal points away from the
	/// garment's middle. Fur on the inside wall of a sleeve would be invisible and double the cost.
	/// </summary>
	public static List<int> OutwardFaces( PolyMesh mesh, IEnumerable<int> faces )
	{
		var centre = Vec3.Zero;

		foreach ( var p in mesh.Positions )
			centre += p;

		centre /= Math.Max( 1, mesh.VertexCount );

		return faces.Where( fi =>
		{
			var f = mesh.Faces[fi];
			var c = mesh.FaceCentroid( f );
			var axis = new Vec3( c.x - centre.x, c.y - centre.y, 0 );
			return Vec3.Dot( mesh.FaceNormal( f ), axis ) >= 0 || axis.LengthSquared < 1e-6f;
		} ).ToList();
	}
}

/// <summary>
/// The textures and .vmat for a fur feature. Text and bytes only, so the headless suite can judge it;
/// the editor writes the files and binds the slot.
/// </summary>
public static class FurMaterial
{
	public const int NoiseSize = 256;

	/// <summary>Procedural 3D solid-texture coat patterns (spec 3.2). Evaluated in object
	/// space, so a pattern has zero UV seam distortion: the same 3D point always maps to the
	/// same colour whichever shell, fin or card samples it.</summary>
	public enum CoatPatternKind
	{
		/// <summary>Uniform pelt, dark roots and sun-bleached tips (subtle mottling only).</summary>
		Solid,
		/// <summary>Banded wolf/coyote/husky pelt: dark slate base, warm buff mid-shaft, black
		/// guard-hair tips, pale belly counter-shading.</summary>
		Agouti,
		/// <summary>Anisotropic domain-warped vertical sine bands with a pale ventral belly.</summary>
		TigerStripe,
		/// <summary>2-scale Voronoi cell rings: a dark broken outer ring enclosing a warm
		/// golden centre spot over a tawny base.</summary>
		LeopardRosette,
		/// <summary>Poisson-distributed solid oval spots.</summary>
		CheetahSpot,
		/// <summary>High-frequency directional FBM streaks along the anatomical flow (+X).</summary>
		Brindle,
		/// <summary>Diluted multi-threshold patches (reaction-diffusion style).</summary>
		Merle,
		/// <summary>Large white patches over the base coat.</summary>
		Piebald,
		/// <summary>White, ginger and base-colour patches.</summary>
		Calico,
	}

	/// <summary>Coat colour at object-space point <paramref name="p"/> (inches, Z-up, +X
	/// forward, -Z ventral). <paramref name="scale"/> is the pattern frequency in
	/// 1/inches: 0.25 means one pattern repeat per 4 inches. Deterministic for a given
	/// seed; <paramref name="baseColor"/> is the main pelt colour, <paramref name="darkColor"/>
	/// the markings, <paramref name="lightColor"/> the pale/belly colour.</summary>
	public static Vec3 CoatColor( CoatPatternKind kind, Vec3 p, Vec3 baseColor, Vec3 darkColor, Vec3 lightColor, int seed, float scale = 0.25f )
	{
		var q = p * Math.Max( 1e-6f, scale );
		// Ventral (belly) mask: -Z faces down in Z-up space.
		var belly = Math.Clamp( -q.z, 0f, 1f );
		switch ( kind )
		{
			case CoatPatternKind.Solid:
				return baseColor * (0.92f + 0.16f * Value3( q * 2f, seed ));
			case CoatPatternKind.Agouti:
			{
				// Banded along a shaft diagonal, warped so the bands are not ruler-straight.
				var phase = 0.5f + 0.5f * MathF.Sin( (q.x + q.y + q.z) * MathF.PI * 2f + 2.5f * Fbm3( q * 2f, seed ) );
				var c = phase < 0.5f
					? Vec3.Lerp( darkColor, lightColor, phase * 2f )
					: Vec3.Lerp( lightColor, darkColor, (phase - 0.5f) * 2f );
				return Vec3.Lerp( c, lightColor, belly * 0.6f );
			}
			case CoatPatternKind.TigerStripe:
			{
				// Bands vary along body length (+X) and run vertically; the domain warp keeps
				// them from reading as printed stripes.
				var warp = Fbm3( q * 1.5f, seed );
				var s = MathF.Sin( (q.x * 9f + warp * 4f) * MathF.PI );
				var c = s > 0.55f ? darkColor : baseColor;
				return Vec3.Lerp( c, lightColor, belly * 0.7f );
			}
			case CoatPatternKind.LeopardRosette:
			{
				Voronoi( q * 3f, seed, out var f1, out var f2 );
				var ring = f2 - f1;
				var broken = Value3( q * 7f, seed + 5 ) > 0.45f;
				Vec3 c = baseColor;
				if ( f1 < 0.25f ) c = lightColor;
				if ( ring < 0.10f && broken ) c = darkColor;
				return c;
			}
			case CoatPatternKind.CheetahSpot:
			{
				// One Poisson point per cell; an oval spot when the sample is near it.
				var cx = MathF.Floor( q.x * 4f ); var cy = MathF.Floor( q.y * 4f ); var cz = MathF.Floor( q.z * 4f );
				var px = cx + 0.15f + 0.7f * Hash3( (int)cx, (int)cy, (int)cz, seed );
				var py = cy + 0.15f + 0.7f * Hash3( (int)cx, (int)cy, (int)cz, seed + 11 );
				var pz = cz + 0.15f + 0.7f * Hash3( (int)cx, (int)cy, (int)cz, seed + 23 );
				var dx = (q.x * 4f - px); var dy = (q.y * 4f - py) * 1.6f; var dz = (q.z * 4f - pz);
				var d = MathF.Sqrt( dx * dx + dy * dy + dz * dz );
				return d < 0.32f ? darkColor : baseColor;
			}
			case CoatPatternKind.Brindle:
			{
				// High frequency across the flow, low frequency along it: streaks run +X.
				var n = Fbm3( new Vec3( q.x * 1.5f, q.y * 10f, q.z * 10f ), seed );
				return Vec3.Lerp( baseColor, darkColor, Smooth( 0.45f, 0.75f, n ) );
			}
			case CoatPatternKind.Merle:
			{
				var m = Fbm3( q * 2.2f, seed );
				if ( m > 0.62f ) return lightColor;
				if ( m < 0.38f ) return Vec3.Lerp( baseColor, lightColor, 0.5f );
				return baseColor;
			}
			case CoatPatternKind.Piebald:
			{
				var m = Fbm3( q * 2.2f, seed );
				return m > 0.55f ? lightColor : baseColor;
			}
			case CoatPatternKind.Calico:
			{
				var m1 = Fbm3( q * 2.2f, seed );
				if ( m1 > 0.60f ) return lightColor;
				var m2 = Fbm3( q * 2.2f, seed + 31 );
				if ( m2 > 0.62f ) return new Vec3( 0.82f, 0.45f, 0.18f );
				return baseColor;
			}
			default:
				return baseColor;
		}
	}

	/// <summary>A 2D RGBA slice of the 3D coat pattern, for the .vmat / preview texture: the
	/// slice spans 1/<paramref name="scale"/> inches in X and Y at z = 0.5/scale. The
	/// seam-free evaluation is <see cref="CoatColor"/>; this is only its flat rendering.</summary>
	public static byte[] CoatPatternRgba( CoatPatternKind kind, Vec3 baseColor, Vec3 darkColor, Vec3 lightColor, int seed, int size = 256, float scale = 0.25f )
	{
		size = Math.Clamp( size, 1, 1024 );
		var rgba = new byte[size * size * 4];
		var span = 1f / Math.Max( 1e-6f, scale );
		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var p = new Vec3( x / (float)size * span, y / (float)size * span, 0.5f * span );
				var c = CoatColor( kind, p, baseColor, darkColor, lightColor, seed, scale );
				var o = (y * size + x) * 4;
				rgba[o] = Byte( c.x );
				rgba[o + 1] = Byte( c.y );
				rgba[o + 2] = Byte( c.z );
				rgba[o + 3] = 255;
			}
		}
		return rgba;
	}

	/// <summary>
	/// Strand noise: mostly dark with bright specks, a few pixels across, so the tip shells keep thin
	/// strands. <paramref name="clump"/> 0..1 groups neighbouring strands into tufts.
	/// </summary>
	public static byte[] NoiseRgba( int seed, float clump ) => NoiseRgba( seed, clump, 0 );

	/// <summary>Per-layer noise: <paramref name="layer"/> shifts the hash seed, so
	/// stacked shells do not read as photocopies of one noise texture.</summary>
	public static byte[] NoiseRgba( int seed, float clump, int layer )
	{
		var size = NoiseSize;
		var rgba = new byte[size * size * 4];
		clump = Math.Clamp( clump, 0f, 1f );

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var strand = Hash( x, y, seed + layer * 7919 );
				var tuft = Value( x / 16f, y / 16f, seed + layer * 7919 + 7 );
				var v = strand * (1f - clump) + strand * tuft * clump * 1.6f;
				var b = (byte)Math.Clamp( (int)(v * 255f), 0, 255 );
				var o = (y * size + x) * 4;
				rgba[o] = b;
				rgba[o + 1] = b;
				rgba[o + 2] = b;
				rgba[o + 3] = 255;
			}
		}

		return rgba;
	}

	/// <summary>A flat colour swatch for the fur's base colour.</summary>
	public static byte[] ColorRgba( Vec3 color, int size = 16 )
	{
		var rgba = new byte[size * size * 4];

		for ( var i = 0; i < size * size; i++ )
		{
			rgba[i * 4] = Byte( color.x );
			rgba[i * 4 + 1] = Byte( color.y );
			rgba[i * 4 + 2] = Byte( color.z );
			rgba[i * 4 + 3] = 255;
		}

		return rgba;
	}

	public static string VmatSource( string colorPng, string noisePng, float density, float darkRoots,
		Vec3 rim, float wind )
	{
		static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

		return
			"// THIS FILE IS AUTO-GENERATED by an Effigy Fur feature. Edit the feature, not this file.\n" +
			"Layer0\n" +
			"{\n" +
			"\tshader \"shaders/fur.shader\"\n" +
			"\tF_NEW_TEXTURE_PACKING 1\n" +
			$"\tTextureBaseColor \"{colorPng}\"\n" +
			$"\tTextureFurNoise \"{noisePng}\"\n" +
			$"\tg_flNoiseTiling \"{F( density )}\"\n" +
			$"\tg_flNoiseAlbedoMultiply \"{F( darkRoots )}\"\n" +
			$"\tg_flNoiseAOAmount \"{F( darkRoots * 0.6f )}\"\n" +
			"\tg_flMinClipFudge \"0.010\"\n" +
			$"\tg_vRimColour \"[{F( rim.x )} {F( rim.y )} {F( rim.z )}]\"\n" +
			"\tg_flRimPower \"2.000\"\n" +
			"\tg_flRimFudge \"0.300\"\n" +
			$"\tg_flWind \"{F( wind )}\"\n" +
			"\tg_flWindFreq \"0.360\"\n" +
			"\tg_flWindNoise \"0.360\"\n" +
			"}\n";
	}

	/// <summary>"#rrggbb" to 0..1 colour, or the fallback when it does not parse.</summary>
	public static Vec3 ParseHex( string hex, Vec3 fallback )
	{
		hex = (hex ?? "").Trim().TrimStart( '#' );

		if ( hex.Length != 6 || !int.TryParse( hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v ) )
			return fallback;

		return new Vec3( ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f );
	}

	static byte Byte( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );

	static float Smooth( float a, float b, float v )
	{
		var t = Math.Clamp( (v - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	static float Hash( int x, int y, int seed )
	{
		unchecked
		{
			var h = (uint)(x * 374761393 + y * 668265263 + seed * 144665);
			h = (h ^ (h >> 13)) * 1274126177u;
			h ^= h >> 16;
			return (h & 0xFFFFFF) / (float)0xFFFFFF;
		}
	}

	static float Hash3( int x, int y, int z, int seed )
	{
		unchecked
		{
			var h = (uint)(x * 374761393 ^ y * 668265263 ^ z * 974634211 ^ seed * 144665);
			h = (h ^ (h >> 13)) * 1274126177u;
			h ^= h >> 16;
			return (h & 0xFFFFFF) / (float)0xFFFFFF;
		}
	}

	static float Value( float x, float y, int seed )
	{
		var ix = (int)MathF.Floor( x );
		var iy = (int)MathF.Floor( y );
		var fx = x - ix;
		var fy = y - iy;
		fx = fx * fx * (3 - 2 * fx);
		fy = fy * fy * (3 - 2 * fy);
		var a = Hash( ix & 15, iy & 15, seed );
		var b = Hash( (ix + 1) & 15, iy & 15, seed );
		var c = Hash( ix & 15, (iy + 1) & 15, seed );
		var d = Hash( (ix + 1) & 15, (iy + 1) & 15, seed );
		return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
	}

	/// <summary>Trilinear value noise in 3D: the 3D counterpart of <see cref="Value"/>.</summary>
	static float Value3( Vec3 p, int seed ) => Value3( p.x, p.y, p.z, seed );

	static float Value3( float x, float y, float z, int seed )
	{
		var ix = (int)MathF.Floor( x );
		var iy = (int)MathF.Floor( y );
		var iz = (int)MathF.Floor( z );
		var fx = x - ix; var fy = y - iy; var fz = z - iz;
		fx = fx * fx * (3 - 2 * fx);
		fy = fy * fy * (3 - 2 * fy);
		fz = fz * fz * (3 - 2 * fz);
		float C( int dx, int dy, int dz ) => Hash3( ix + dx, iy + dy, iz + dz, seed );
		var c000 = C( 0, 0, 0 ); var c100 = C( 1, 0, 0 ); var c010 = C( 0, 1, 0 ); var c110 = C( 1, 1, 0 );
		var c001 = C( 0, 0, 1 ); var c101 = C( 1, 0, 1 ); var c011 = C( 0, 1, 1 ); var c111 = C( 1, 1, 1 );
		var x00 = c000 + (c100 - c000) * fx; var x10 = c010 + (c110 - c010) * fx;
		var x01 = c001 + (c101 - c001) * fx; var x11 = c011 + (c111 - c011) * fx;
		var y0 = x00 + (x10 - x00) * fy; var y1 = x01 + (x11 - x01) * fy;
		return y0 + (y1 - y0) * fz;
	}

	/// <summary>Value-noise FBM in 3D, 4 octaves by default.</summary>
	static float Fbm3( Vec3 p, int seed, int octaves = 4 )
	{
		var sum = 0f; var amp = 0.5f; var total = 0f;
		var q = p;
		for ( var i = 0; i < octaves; i++ )
		{
			sum += amp * Value3( q.x, q.y, q.z, seed + i * 101 );
			total += amp;
			amp *= 0.5f;
			q *= 2.03f;
		}
		return total > 0f ? sum / total : 0f;
	}

	/// <summary>3D Voronoi: <paramref name="f1"/> is the distance to the nearest cell point,
	/// <paramref name="f2"/> to the second nearest. The ring measure f2 - f1 is ~0 on cell
	/// borders and large near cell centres, which is what rosettes key off.</summary>
	static void Voronoi( Vec3 p, int seed, out float f1, out float f2 )
	{
		f1 = float.PositiveInfinity; f2 = float.PositiveInfinity;
		var ix = (int)MathF.Floor( p.x ); var iy = (int)MathF.Floor( p.y ); var iz = (int)MathF.Floor( p.z );
		for ( var dz = -1; dz <= 1; dz++ )
		for ( var dy = -1; dy <= 1; dy++ )
		for ( var dx = -1; dx <= 1; dx++ )
		{
			var cx = ix + dx; var cy = iy + dy; var cz = iz + dz;
			var px = cx + 0.15f + 0.7f * Hash3( cx, cy, cz, seed );
			var py = cy + 0.15f + 0.7f * Hash3( cx, cy, cz, seed + 11 );
			var pz = cz + 0.15f + 0.7f * Hash3( cx, cy, cz, seed + 23 );
			var ddx = p.x - px; var ddy = p.y - py; var ddz = p.z - pz;
			var d = MathF.Sqrt( ddx * ddx + ddy * ddy + ddz * ddz );
			if ( d < f1 ) { f2 = f1; f1 = d; }
			else if ( d < f2 ) { f2 = d; }
		}
	}
}
