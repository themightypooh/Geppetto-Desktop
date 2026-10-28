using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Where a vertex sits relative to the seams between a mesh's parts.</summary>
public sealed class SeamField
{
	/// <summary>Distance over the surface to the nearest seam (inches); <see cref="float.MaxValue"/> past the reach.</summary>
	public float[] Distance;
	/// <summary>The part on the far side of that seam, or -1.</summary>
	public int[] Other;
	/// <summary>True when this vertex's part lies above the other where they meet (its skin overhangs the seam).</summary>
	public bool[] Top;
}

/// <summary>Settings for <see cref="SurfaceDetail.GrownSeam"/>, in inches.</summary>
public sealed class SeamOptions
{
	/// <summary>Depth of the crease line at the seam.</summary>
	public float Depth = 0.6f;
	/// <summary>Half-width of the crease (keep it two edges or more wide, or it aliases).</summary>
	public float Width = 0.45f;
	/// <summary>Height of the roll of skin the upper part lays over the crease.</summary>
	public float Roll = 0.5f;
	/// <summary>How far up the upper part the roll's crest sits, and its half-width.</summary>
	public float RollOffset = 0.9f, RollWidth = 0.7f;
	/// <summary>How far the roll sags toward the crease, as a share of its height.</summary>
	public float Sag = 0.6f;
	/// <summary>Amplitude varies along the seam by up to this share, so it doesn't read machined.</summary>
	public float Variation = 0.4f;
	public Vec3 Up = new( 0, 0, 1 );
	public int Seed = 1;
}

/// <summary>Settings for <see cref="SurfaceDetail.SagFolds"/>, in inches.</summary>
public sealed class FoldOptions
{
	/// <summary>Distance between fold crests down the slope.</summary>
	public float Spacing = 2.2f;
	/// <summary>Height of a fold's roll; its crease is cut a third of that.</summary>
	public float Amplitude = 0.35f;
	/// <summary>How much the crests wander (share of spacing) and break along their length.</summary>
	public float Wander = 0.35f, Break = 0.35f;
	public Vec3 Up = new( 0, 0, 1 );
	public int Seed = 1;
}

/// <summary>Settings for <see cref="SurfaceDetail.AnchoredFolds"/>, in inches.</summary>
public sealed class AnchoredFoldOptions
{
	/// <summary>How many folds hang below the anchor.</summary>
	public int Count = 4;
	/// <summary>Distance from the anchor to the first fold, and the first fold's width; each later
	/// fold is wider by <see cref="Growth"/> and lower by <see cref="Decay"/>.</summary>
	public float First = 1.2f, Spacing = 1.6f, Growth = 0.35f, Decay = 0.72f;
	/// <summary>Height of the first fold's roll; its crease is cut 0.4 of that.</summary>
	public float Amplitude = 0.6f;
	/// <summary>How far the rolls hang toward gravity, as a share of their height.</summary>
	public float Sag = 0.5f;
	/// <summary>Variation along the anchor: amplitude and position both wander by this share.</summary>
	public float Variation = 0.45f;
	public Vec3 Up = new( 0, 0, 1 );
	public int Seed = 1;
}

/// <summary>Settings for <see cref="SurfaceDetail.DirectionFolds"/>, in inches.</summary>
public sealed class DirectionFoldOptions
{
	/// <summary>Direction of compression / muscle pull. Fold crests run perpendicular to it.</summary>
	public Vec3 Direction = new( 0, 1, 0 );
	/// <summary>Distance between fold crests along the direction.</summary>
	public float Spacing = 2.2f;
	/// <summary>Height of a fold's roll; its crease is cut a third of that.</summary>
	public float Amplitude = 0.35f;
	/// <summary>Height of the rolled lip on the compressed side of each crease, as a share of Amplitude.</summary>
	public float Lip = 0.5f;
	/// <summary>Extra amplitude where the surface faces against the direction (the bunched side), 0..1.</summary>
	public float Bunch = 0.5f;
	/// <summary>How much the crests wander (share of spacing) and break along their length.</summary>
	public float Wander = 0.35f, Break = 0.35f;
	public int Seed = 1;
}

/// <summary>Settings for rimmed pore pits pressed into a surface.</summary>
public sealed class PoreOptions
{
	/// <summary>Minimum spacing between pore centres.</summary>
	public float Spacing = 1f;
	/// <summary>Depth of each pit (positive number; pits go inward).</summary>
	public float Depth = 0.05f;
	public int Seed = 1;
}

/// <summary>A dome (pustule, cyst, lump) or a pit pressed into a surface.</summary>
public readonly record struct SurfaceStamp( Vec3 Center, float Radius, float Height );

/// <summary>
/// Secondary and tertiary surface detail for organic meshes, the passes a sculptor adds after the
/// big forms (after the recipe in the organic-modeling skill's tutorials-surface.md):
/// - <see cref="Seams"/> + <see cref="GrownSeam"/>: where two parts of a creature meet, a crease with
///   the upper part's skin rolled over it, instead of a bald smooth fillet (B1/B2: seams that look grown);
/// - <see cref="SagFolds"/>: hanging folds on loose skin, crests across the pull of gravity (A3, B2);
/// - <see cref="Stamps"/> with <see cref="PoissonOnSurface"/>: pustules and lumps that push the skin
///   around them aside before they rise (C1's warp-before-add, D5);
/// - <see cref="Grain"/>: a broken crease network and soft tissue lumps (A4, A6);
/// - <see cref="Cavity"/> and <see cref="AmbientOcclusion"/>: masks for the passes above and for
///   bakes (dirt and blood in crevices, roughness).
/// Every pass moves vertices along their normals in place; widths should be two edges or more.
/// </summary>
public static class SurfaceDetail
{
	/// <summary>
	/// The seams between a mesh's parts (<paramref name="part"/> per vertex, e.g. which leaf of the
	/// field made it): each vertex's distance over the surface to the nearest edge where parts change,
	/// out to <paramref name="reach"/>, which part is across it, and which side is on top.
	/// <paramref name="want"/> picks the pairs that get seams (both orders are asked).
	/// </summary>
	public static SeamField Seams( PolyMesh mesh, int[] part, float reach, Func<int, int, bool> want = null, Vec3? up = null )
	{
		var n = mesh.VertexCount;
		var nb = MeshLoops.Neighbours( n, mesh.Faces );
		var pos = mesh.Positions;
		var dist = Enumerable.Repeat( float.MaxValue, n ).ToArray();
		var other = Enumerable.Repeat( -1, n ).ToArray();
		var queue = new PriorityQueue<int, float>();
		// Sources: vertices on a part boundary, each half an edge from the seam.
		for ( var v = 0; v < n; v++ )
			foreach ( var u in nb[v] )
			{
				if ( part[u] == part[v] || part[u] < 0 || part[v] < 0 ) continue;
				if ( want is not null && !want( part[v], part[u] ) ) continue;
				var d = (pos[u] - pos[v]).Length * 0.5f;
				if ( d < dist[v] ) { dist[v] = d; other[v] = part[u]; queue.Enqueue( v, d ); }
			}
		// Spread over each part's own surface (never across into another part).
		while ( queue.TryDequeue( out var v, out var d ) )
		{
			if ( d > dist[v] ) continue;
			foreach ( var u in nb[v] )
			{
				if ( part[u] != part[v] ) continue;
				var nd = d + (pos[u] - pos[v]).Length;
				if ( nd >= dist[u] || nd > reach ) continue;
				dist[u] = nd; other[u] = other[v];
				queue.Enqueue( u, nd );
			}
		}
		// The boundary from a labelled mesh is stair-stepped: relax the distance along the surface.
		for ( var it = 0; it < 3; it++ )
		{
			var next = (float[])dist.Clone();
			for ( var v = 0; v < n; v++ )
			{
				if ( dist[v] > reach ) continue;
				var sum = dist[v]; var c = 1;
				foreach ( var u in nb[v] )
					if ( part[u] == part[v] && dist[u] <= reach ) { sum += dist[u]; c++; }
				next[v] = MathF.Max( sum / c, dist[v] * 0.5f );
			}
			dist = next;
		}
		// Which side is on top, per pair: the part whose seam-side vertices sit higher.
		var upv = (up ?? new Vec3( 0, 0, 1 )).Normal;
		var height = new Dictionary<(int, int), (double Sum, int Count)>();
		for ( var v = 0; v < n; v++ )
		{
			if ( other[v] < 0 || dist[v] > reach ) continue;
			var key = (part[v], other[v]);
			var (s, c) = height.GetValueOrDefault( key );
			height[key] = (s + Vec3.Dot( pos[v], upv ), c + 1);
		}
		var top = new bool[n];
		for ( var v = 0; v < n; v++ )
		{
			if ( other[v] < 0 ) continue;
			var mine = height.GetValueOrDefault( (part[v], other[v]) );
			var theirs = height.GetValueOrDefault( (other[v], part[v]) );
			var hm = mine.Count > 0 ? mine.Sum / mine.Count : 0;
			var ht = theirs.Count > 0 ? theirs.Sum / theirs.Count : double.MinValue;
			top[v] = hm >= ht;
		}
		return new SeamField { Distance = dist, Other = other, Top = top };
	}

	/// <summary>
	/// Seams that look grown, not merged: a crease along each seam, the upper part's skin rolled over
	/// it and sagging toward it, a small lip on the lower side, the amplitude varying along the seam.
	/// <paramref name="strength"/> scales the whole pass per vertex (null for everywhere).
	/// </summary>
	public static void GrownSeam( PolyMesh mesh, SeamField seams, SeamOptions o, Func<int, float> strength = null )
	{
		var n = mesh.VertexCount;
		var normals = VertexNormals( mesh );
		var noise = new Noise3( o.Seed );
		var up = o.Up.Normal;
		var reach = MathF.Max( 3f * o.Width, o.RollOffset + 2.5f * o.RollWidth );
		var delta = new Vec3[n];
		for ( var v = 0; v < n; v++ )
		{
			var l = seams.Distance[v];
			if ( seams.Other[v] < 0 || l > reach ) continue;
			var p = mesh.Positions[v];
			var k = (strength?.Invoke( v ) ?? 1f) * (1f + o.Variation * noise.Sample( p * 0.18f ));
			if ( k <= 0f ) continue;
			var g = l / o.Width;
			var h = -o.Depth * MathF.Exp( -g * g );
			var sag = Vec3.Zero;
			if ( seams.Top[v] )
			{
				var r = (l - o.RollOffset) / o.RollWidth;
				var roll = o.Roll * MathF.Exp( -r * r );
				h += roll;
				// The roll hangs: carried down toward the crease, the more the surface faces sideways.
				var side = 1f - MathF.Abs( Vec3.Dot( normals[v], up ) );
				sag = -up * (roll * o.Sag * side);
			}
			else
			{
				var r = (l - 0.6f * o.RollOffset) / (0.5f * o.RollWidth);
				h += 0.25f * o.Roll * MathF.Exp( -r * r );
			}
			delta[v] = (normals[v] * h + sag) * k;
		}
		for ( var v = 0; v < n; v++ ) mesh.Positions[v] += delta[v];
	}

	/// <summary>
	/// Hanging folds where skin is loose (<paramref name="looseness"/> 0..1 per vertex): rolls whose
	/// crests run across the pull of gravity, a sharp crease above each roll and the roll's belly
	/// hanging below it, wandering and broken along their length. Faces that look straight up or
	/// down get none (folds there are side-on to gravity).
	/// </summary>
	public static void SagFolds( PolyMesh mesh, float[] looseness, FoldOptions o )
	{
		var n = mesh.VertexCount;
		var normals = VertexNormals( mesh );
		var noise = new Noise3( o.Seed );
		var up = o.Up.Normal;
		var delta = new Vec3[n];
		for ( var v = 0; v < n; v++ )
		{
			var loose = looseness[v];
			if ( loose <= 0f ) continue;
			var p = mesh.Positions[v];
			var facing = Vec3.Dot( normals[v], up );
			var sideOn = 1f - Smooth( 0.75f, 0.95f, MathF.Abs( facing ) );
			if ( sideOn <= 0f ) continue;
			// Height down the slope, wandered so crests aren't contour lines.
			var t = Vec3.Dot( p, up ) / o.Spacing + o.Wander * noise.Sample( p * (0.35f / o.Spacing) ) * 2f;
			var f = t - MathF.Floor( t );
			// One fold per period: crease at f = 0, the roll's belly hanging below it (f near 1).
			var crease = -MathF.Exp( -(f / 0.08f) * (f / 0.08f) ) - MathF.Exp( -((1f - f) / 0.08f) * ((1f - f) / 0.08f) );
			var belly = MathF.Exp( -((f - 0.72f) / 0.22f) * ((f - 0.72f) / 0.22f) );
			var broken = Smooth( -0.3f, 0.3f, noise.Sample( p * (0.6f / o.Spacing) + new Vec3( 5.1f, 0, 0 ) ) + (1f - 2f * o.Break) );
			var h = o.Amplitude * (belly + 0.33f * crease) * loose * sideOn * broken;
			delta[v] = normals[v] * h;
		}
		for ( var v = 0; v < n; v++ ) mesh.Positions[v] += delta[v];
	}

	/// <summary>
	/// Compression folds: folds whose crests run PERPENDICULAR to a direction of compression or
	/// muscle pull (forehead lines across the forehead, elbow/knuckle folds across the joint),
	/// instead of gravity-only SagFolds. Each fold is a crease plus a rolled lip on the
	/// compressed side, with extra amplitude where the surface faces against the direction
	/// (the bunched side), wandering and broken along its length. <paramref name="looseness"/>
	/// 0..1 per vertex scales the pass, exactly like SagFolds.
	/// </summary>
	public static void DirectionFolds( PolyMesh mesh, float[] looseness, DirectionFoldOptions o )
	{
		var n = mesh.VertexCount;
		var normals = VertexNormals( mesh );
		var noise = new Noise3( o.Seed );
		var dir = o.Direction.LengthSquared > 1e-12f ? o.Direction.Normal : new Vec3( 0, 1, 0 );
		var delta = new Vec3[n];
		for ( var v = 0; v < n; v++ )
		{
			var loose = looseness[v];
			if ( loose <= 0f ) continue;
			var p = mesh.Positions[v];
			// Phase along the compression direction; crests are perpendicular to it.
			var t = Vec3.Dot( p, dir ) / o.Spacing + o.Wander * noise.Sample( p * (0.35f / o.Spacing) ) * 2f;
			var f = t - MathF.Floor( t );
			var crease = -MathF.Exp( -(f / 0.08f) * (f / 0.08f) ) - MathF.Exp( -((1f - f) / 0.08f) * ((1f - f) / 0.08f) );
			var belly = MathF.Exp( -((f - 0.72f) / 0.22f) * ((f - 0.72f) / 0.22f) );
			var lip = o.Lip * MathF.Exp( -((f - 0.15f) / 0.12f) * ((f - 0.15f) / 0.12f) );
			var broken = Smooth( -0.3f, 0.3f, noise.Sample( p * (0.6f / o.Spacing) + new Vec3( 5.1f, 0, 0 ) ) + (1f - 2f * o.Break) );
			// Bunched side: surface facing against the pull gets up to (1 + Bunch) amplitude.
			var bunch = 1f + o.Bunch * MathF.Max( 0f, -Vec3.Dot( normals[v], dir ) );
			var h = o.Amplitude * (belly + lip + 0.33f * crease) * loose * broken * bunch;
			delta[v] = normals[v] * h;
		}
		for ( var v = 0; v < n; v++ ) mesh.Positions[v] += delta[v];
	}

	/// <summary>
	/// Rimmed pore pits: pit centres spread at least <see cref="PoreOptions.Spacing"/> apart
	/// where <paramref name="density"/> allows, each pressed in as a pit. <see cref="Stamps"/>
	/// already gives every pit its faint rim, so pores read as pits-with-rims, not dots.
	/// Density is per vertex 0..1 (a region mask), so pore size/density can vary by region.
	/// </summary>
	public static void Pores( PolyMesh mesh, float[] density, PoreOptions o )
	{
		var picked = PoissonOnSurface( mesh, o.Spacing, v => v < density.Length && density[v] > 0.01f, o.Seed );
		var before = mesh.Positions.ToArray();
		Stamps( mesh, picked.Select( v => new SurfaceStamp( before[v], o.Spacing * 0.45f, -o.Depth * (v < density.Length ? density[v] : 1f) ) ) );
	}

	/// <summary>
	/// Folds hung from an anchor, the way skin bunches below a seam or under a belly: a few folds
	/// (not a field of them) following the anchor's own line, the first close and deep, each later
	/// one wider apart and shallower, wandering along the anchor. <paramref name="distance"/> is each
	/// vertex's surface distance from the anchor (e.g. <see cref="SeamField.Distance"/>);
	/// <paramref name="hanging"/> says which vertices are on the side that hangs (below it).
	/// </summary>
	public static void AnchoredFolds( PolyMesh mesh, float[] distance, Func<int, bool> hanging, AnchoredFoldOptions o )
	{
		var n = mesh.VertexCount;
		var normals = VertexNormals( mesh );
		var noise = new Noise3( o.Seed );
		var up = o.Up.Normal;
		var centres = new float[o.Count];
		var widths = new float[o.Count];
		var at = o.First;
		for ( var k = 0; k < o.Count; k++ )
		{
			widths[k] = o.Spacing * (1f + o.Growth * k);
			centres[k] = at;
			at += widths[k];
		}
		var reach = at + widths[^1];
		var delta = new Vec3[n];
		for ( var v = 0; v < n; v++ )
		{
			var d = distance[v];
			if ( d > reach || !hanging( v ) ) continue;
			var p = mesh.Positions[v];
			var wobble = noise.Sample( p * 0.12f );
			var gain = 1f + o.Variation * noise.Sample( p * 0.2f + new Vec3( 3.3f, 0, 0 ) );
			var h = 0f;
			for ( var k = 0; k < o.Count; k++ )
			{
				var w = widths[k];
				var l = d - (centres[k] + o.Variation * 0.5f * w * wobble);
				var a = o.Amplitude * MathF.Pow( o.Decay, k ) * gain;
				var roll = MathF.Exp( -(l / (0.38f * w)) * (l / (0.38f * w)) );
				var crease = MathF.Exp( -((l + 0.5f * w) / (0.11f * w)) * ((l + 0.5f * w) / (0.11f * w)) );
				h += a * (roll - 0.4f * crease);
			}
			var side = 1f - MathF.Abs( Vec3.Dot( normals[v], up ) );
			delta[v] = normals[v] * h - up * (MathF.Max( h, 0f ) * o.Sag * side);
		}
		for ( var v = 0; v < n; v++ ) mesh.Positions[v] += delta[v];
	}

	/// <summary>
	/// A quiet mask: 1 on roughly <paramref name="busy"/> of the surface in broad patches about
	/// 1/<paramref name="frequency"/> across, 0 on the rest, feathered between. Sculptors keep most
	/// skin calm so the detail that is there reads; multiply detail passes by this.
	/// </summary>
	public static float[] BusyMask( PolyMesh mesh, float busy, float frequency, int seed, float feather = 0.08f )
	{
		var noise = new Noise3( seed );
		var raw = mesh.Positions.Select( p => noise.Fbm( p * frequency, 2 ) ).ToArray();
		if ( raw.Length == 0 ) return raw;
		var sorted = (float[])raw.Clone();
		Array.Sort( sorted );
		var cut = sorted[Math.Clamp( (int)((1f - busy) * (sorted.Length - 1)), 0, sorted.Length - 1 )];
		return raw.Select( x => Smooth( cut - feather, cut + feather, x ) ).ToArray();
	}

	/// <summary>
	/// Stamps in clusters, not sprinkled: <paramref name="clusters"/> centres picked apart from each
	/// other among the vertices <paramref name="where"/> allows, then <paramref name="perCluster"/>
	/// stamps scattered within <paramref name="spread"/> of each, sizes from big at the centre to small
	/// at the edge.
	/// </summary>
	public static List<SurfaceStamp> Clusters( PolyMesh mesh, Func<int, bool> where, int clusters, int perCluster, float spread, float minRadius, float maxRadius, int seed )
	{
		var rng = new Random( seed );
		var centres = PoissonOnSurface( mesh, spread * 3f, where, seed ).Take( clusters ).ToList();
		var bvh = MeshBVH.Build( mesh );
		var near = new List<int>();
		var stamps = new List<SurfaceStamp>();
		foreach ( var c in centres )
		{
			near.Clear();
			bvh.VerticesInRadius( mesh, mesh.Positions[c], spread, near );
			if ( near.Count == 0 ) continue;
			for ( var i = 0; i < perCluster; i++ )
			{
				var v = near[rng.Next( near.Count )];
				var t = (mesh.Positions[v] - mesh.Positions[c]).Length / spread;
				var r = minRadius + (maxRadius - minRadius) * (1f - t) * (0.6f + 0.4f * (float)rng.NextDouble());
				stamps.Add( new SurfaceStamp( mesh.Positions[v], r, r * (0.45f + 0.25f * (float)rng.NextDouble()) ) );
			}
		}
		return stamps;
	}

	/// <summary>
	/// Domes and pits pressed into the surface. Each first pushes the skin within twice its radius
	/// outward from its centre along the surface (the skin makes room), then rises (or sinks) as a
	/// smooth cap with a faint rim, so it reads grown rather than stuck on.
	/// </summary>
	public static void Stamps( PolyMesh mesh, IEnumerable<SurfaceStamp> stamps )
	{
		var normals = VertexNormals( mesh );
		var bvh = MeshBVH.Build( mesh );
		var near = new List<int>();
		var delta = new Vec3[mesh.VertexCount];
		foreach ( var s in stamps )
		{
			near.Clear();
			bvh.VerticesInRadius( mesh, s.Center, s.Radius * 2f, near );
			foreach ( var v in near )
			{
				var d = mesh.Positions[v] - s.Center;
				var r = d.Length / s.Radius;
				var nrm = normals[v];
				var along = d - nrm * Vec3.Dot( d, nrm );
				// Make room: a ring of skin shoved outward around the base.
				var push = 0.25f * MathF.Abs( s.Height ) * MathF.Exp( -((r - 1.1f) / 0.45f) * ((r - 1.1f) / 0.45f) );
				if ( along.LengthSquared > 1e-10f ) delta[v] += along.Normal * push;
				var cap = r < 1f ? (1f - r * r) * (1f - r * r) : 0f;
				var rim = 0.12f * MathF.Exp( -((r - 1.15f) / 0.2f) * ((r - 1.15f) / 0.2f) );
				delta[v] += nrm * (s.Height * cap + MathF.Abs( s.Height ) * rim);
			}
		}
		for ( var v = 0; v < mesh.VertexCount; v++ ) mesh.Positions[v] += delta[v];
	}

	/// <summary>
	/// Points spread over the surface at least <paramref name="spacing"/> apart (dart throwing over
	/// the vertices, in random order), only where <paramref name="mask"/> allows. Clustered fields
	/// come from calling it per cluster region.
	/// </summary>
	public static List<int> PoissonOnSurface( PolyMesh mesh, float spacing, Func<int, bool> mask, int seed )
	{
		var order = Enumerable.Range( 0, mesh.VertexCount ).Where( v => mask is null || mask( v ) ).ToArray();
		var rng = new Random( seed );
		for ( var i = order.Length - 1; i > 0; i-- ) { var j = rng.Next( i + 1 ); (order[i], order[j]) = (order[j], order[i]); }
		var cell = spacing / MathF.Sqrt( 3f );
		var grid = new Dictionary<(int, int, int), int>();
		(int, int, int) Key( Vec3 p ) => ((int)MathF.Floor( p.x / cell ), (int)MathF.Floor( p.y / cell ), (int)MathF.Floor( p.z / cell ));
		var picked = new List<int>();
		var r2 = spacing * spacing;
		foreach ( var v in order )
		{
			var p = mesh.Positions[v];
			var (kx, ky, kz) = Key( p );
			var ok = true;
			for ( var dx = -2; dx <= 2 && ok; dx++ )
				for ( var dy = -2; dy <= 2 && ok; dy++ )
					for ( var dz = -2; dz <= 2 && ok; dz++ )
						if ( grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var w ) && (mesh.Positions[w] - p).LengthSquared < r2 )
							ok = false;
			if ( !ok ) continue;
			grid[(kx, ky, kz)] = v;
			picked.Add( v );
		}
		return picked;
	}

	/// <summary>
	/// Skin grain: soft tissue lumps (fbm, <paramref name="lumps"/> high at about 1/<paramref name="lumpFrequency"/>
	/// across) plus a broken network of fine creases (Worley cell edges, cells <paramref name="cell"/>
	/// across, <paramref name="crease"/> deep). <paramref name="mask"/> 0..1 per vertex (null for all).
	/// </summary>
	public static void Grain( PolyMesh mesh, float lumps, float lumpFrequency, float cell, float crease, int seed, Func<int, float> mask = null )
	{
		var normals = VertexNormals( mesh );
		var noise = new Noise3( seed );
		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var k = mask?.Invoke( v ) ?? 1f;
			if ( k <= 0f ) continue;
			var p = mesh.Positions[v];
			var tissue = noise.Fbm( p * lumpFrequency, 3 ) * lumps;
			var q = p / cell + noise.Vector( p * (0.6f / cell) ) * 0.35f;
			var (f1, f2) = Worley( q, seed );
			var edge = 1f - Smooth( 0f, 0.22f, f2 - f1 );
			var broken = Smooth( -0.25f, 0.25f, noise.Sample( p * (1.5f / cell) + new Vec3( 0, 7.7f, 0 ) ) );
			mesh.Positions[v] = p + normals[v] * ((tissue - crease * edge * (0.35f + 0.65f * broken)) * k);
		}
	}

	/// <summary>
	/// Cavity per vertex (positive in creases, negative on ridges), in inches: how far each vertex
	/// sits below the surface smoothed <paramref name="iterations"/> times (feature scale about
	/// edge length times the square root of that).
	/// </summary>
	public static float[] Cavity( PolyMesh mesh, int iterations = 8 )
	{
		var n = mesh.VertexCount;
		var nb = MeshLoops.Neighbours( n, mesh.Faces );
		var normals = VertexNormals( mesh );
		var s = mesh.Positions.ToArray();
		for ( var it = 0; it < iterations; it++ )
		{
			var next = new Vec3[n];
			for ( var v = 0; v < n; v++ )
			{
				if ( nb[v].Length == 0 ) { next[v] = s[v]; continue; }
				var avg = Vec3.Zero;
				foreach ( var u in nb[v] ) avg += s[u];
				next[v] = s[v] * 0.5f + avg / nb[v].Length * 0.5f;
			}
			s = next;
		}
		var cavity = new float[n];
		for ( var v = 0; v < n; v++ ) cavity[v] = Vec3.Dot( s[v] - mesh.Positions[v], normals[v] );
		return cavity;
	}

	/// <summary>
	/// Ambient occlusion per vertex, 0 (open) to 1 (shut in): the share of <paramref name="rays"/>
	/// cosine-weighted rays over the normal's hemisphere that hit the mesh within <paramref name="distance"/>.
	/// </summary>
	public static float[] AmbientOcclusion( PolyMesh mesh, int rays = 24, float distance = 6f, int seed = 1 )
	{
		var normals = VertexNormals( mesh );
		var bvh = MeshBVH.Build( mesh );
		var ao = new float[mesh.VertexCount];
		System.Threading.Tasks.Parallel.For( 0, mesh.VertexCount, v =>
		{
			var nrm = normals[v];
			if ( nrm.LengthSquared == 0f ) return;
			var rng = new Random( seed * 7919 + v );
			Ortho( nrm, out var t, out var b );
			var origin = mesh.Positions[v] + nrm * 0.02f;
			var hits = 0;
			for ( var i = 0; i < rays; i++ )
			{
				var u1 = (float)rng.NextDouble(); var u2 = (float)rng.NextDouble();
				var r = MathF.Sqrt( u1 ); var a = 2f * MathF.PI * u2;
				var dir = (t * (r * MathF.Cos( a )) + b * (r * MathF.Sin( a )) + nrm * MathF.Sqrt( MathF.Max( 0f, 1f - u1 ) )).Normal;
				var hit = bvh.Raycast( mesh, origin, dir );
				if ( hit is { } h && h.Distance <= distance ) hits++;
			}
			ao[v] = hits / (float)rays;
		} );
		return ao;
	}

	/// <summary>
	/// Lane C: <see cref="Grain"/> with a per-vertex weight array instead of a function.
	/// <paramref name="mask"/> is 0..1 per vertex (short arrays read as 0 past their end, null
	/// means everywhere). Same pass as the Func form; the array form is what a script hands over
	/// after <see cref="RegionMask"/> builds it.
	/// </summary>
	public static void Grain( PolyMesh mesh, float lumps, float lumpFrequency, float cell, float crease, int seed, float[] mask )
	{
		if ( mask is null ) { Grain( mesh, lumps, lumpFrequency, cell, crease, seed, (Func<int, float>)null ); return; }
		Grain( mesh, lumps, lumpFrequency, cell, crease, seed, v => v < mask.Length ? Math.Clamp( mask[v], 0f, 1f ) : 0f );
	}

	/// <summary>
	/// Lane C: <see cref="SagFolds"/> with a per-vertex weight array multiplying
	/// <paramref name="looseness"/> (a masked fold field is a fold field that only hangs where
	/// the mask says). Null mask means the unmasked pass.
	/// </summary>
	public static void SagFolds( PolyMesh mesh, float[] looseness, FoldOptions o, float[] mask )
	{
		if ( mask is null ) { SagFolds( mesh, looseness, o ); return; }
		var eff = new float[mesh.VertexCount];
		for ( var v = 0; v < eff.Length; v++ )
			eff[v] = (v < looseness.Length ? looseness[v] : 0f) * (v < mask.Length ? Math.Clamp( mask[v], 0f, 1f ) : 0f);
		SagFolds( mesh, eff, o );
	}

	/// <summary>
	/// Lane C: <see cref="AnchoredFolds"/> with a per-vertex weight array. Implemented as
	/// run-then-blend (new position = old + delta * mask), which is exactly scaling the
	/// fold height per vertex, since the pass only adds a displacement per vertex.
	/// </summary>
	public static void AnchoredFolds( PolyMesh mesh, float[] distance, Func<int, bool> hanging, AnchoredFoldOptions o, float[] mask )
	{
		if ( mask is null ) { AnchoredFolds( mesh, distance, hanging, o ); return; }
		var before = mesh.Positions.ToArray();
		AnchoredFolds( mesh, distance, hanging, o );
		for ( var v = 0; v < mesh.VertexCount; v++ )
			mesh.Positions[v] = before[v] + (mesh.Positions[v] - before[v]) * (v < mask.Length ? Math.Clamp( mask[v], 0f, 1f ) : 0f);
	}

	/// <summary>
	/// Lane C: <see cref="Stamps"/> with a per-vertex weight array, same run-then-blend
	/// reasoning as <see cref="AnchoredFolds(PolyMesh, float[], Func{int, bool}, AnchoredFoldOptions, float[])"/>.
	/// </summary>
	public static void Stamps( PolyMesh mesh, IEnumerable<SurfaceStamp> stamps, float[] mask )
	{
		if ( mask is null ) { Stamps( mesh, stamps ); return; }
		var before = mesh.Positions.ToArray();
		Stamps( mesh, stamps );
		for ( var v = 0; v < mesh.VertexCount; v++ )
			mesh.Positions[v] = before[v] + (mesh.Positions[v] - before[v]) * (v < mask.Length ? Math.Clamp( mask[v], 0f, 1f ) : 0f);
	}

	/// <summary>
	/// Lane C: a per-vertex 0..1 weight that keeps detail to one region of a mesh, so a script
	/// can say `mask_z=-0.5..2.2` (a zone along an axis) or a side or a slot instead of
	/// detailing the whole body. <paramref name="axis"/> is the direction the zone is measured
	/// along (normalised here; a zero axis reads as Z). Inside [min, max] the weight is 1,
	/// feathered to 0 over <paramref name="falloff"/> on either side (0 = a hard step).
	/// <paramref name="cavityWeight"/> (0..1) blends the weight toward crevices: at 1, only
	/// vertices <see cref="Cavity"/> calls creases keep their weight. <paramref name="materialSlot"/>
	/// (-1 = any) keeps only vertices on a face wearing that slot.
	/// </summary>
	public static float[] RegionMask( PolyMesh mesh, Vec3 axis, float min, float max, float falloff, float cavityWeight = 0f, int materialSlot = -1 )
	{
		var n = mesh.VertexCount;
		var outp = new float[n];
		if ( n == 0 ) return outp;
		if ( min > max ) (min, max) = (max, min);
		var ax = axis.LengthSquared > 1e-12f ? axis.Normal : new Vec3( 0, 0, 1 );
		var f = Math.Max( falloff, 0f );
		float[] cav = null;
		float cavScale = 1f;
		if ( cavityWeight > 0f )
		{
			cav = Cavity( mesh, 4 );
			foreach ( var c in cav ) cavScale = Math.Max( cavScale, MathF.Abs( c ) );
		}
		HashSet<int> inSlot = null;
		if ( materialSlot >= 0 )
		{
			inSlot = new HashSet<int>();
			foreach ( var face in mesh.Faces )
				if ( face.Material == materialSlot )
					foreach ( var v in face.Indices ) inSlot.Add( v );
		}
		for ( var v = 0; v < n; v++ )
		{
			var t = Vec3.Dot( mesh.Positions[v], ax );
			float w;
			if ( f <= 0f ) w = t >= min && t <= max ? 1f : 0f;
			else if ( t < min ) w = Smooth( min - f, min, t );
			else if ( t > max ) w = 1f - Smooth( max, max + f, t );
			else w = 1f;
			if ( cav is not null )
			{
				var cn = Math.Clamp( cav[v] / cavScale, 0f, 1f );
				w *= 1f - Math.Clamp( cavityWeight, 0f, 1f ) * (1f - cn);
			}
			if ( inSlot is not null && !inSlot.Contains( v ) ) w = 0f;
			outp[v] = Math.Clamp( w, 0f, 1f );
		}
		return outp;
	}

	// --- helpers ---------------------------------------------------------------------------------

	public static Vec3[] VertexNormals( PolyMesh mesh ) => SkinStrain.VertexNormals( mesh.Positions, mesh.Faces );

	static float Smooth( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	static void Ortho( Vec3 n, out Vec3 t, out Vec3 b )
	{
		var a = MathF.Abs( n.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		t = Vec3.Cross( n, a ).Normal;
		b = Vec3.Cross( n, t );
	}

	/// <summary>Distances to the nearest and second-nearest jittered cell point (Worley F1, F2).</summary>
	public static (float F1, float F2) Worley( Vec3 q, int seed )
	{
		int cx = (int)MathF.Floor( q.x ), cy = (int)MathF.Floor( q.y ), cz = (int)MathF.Floor( q.z );
		float f1 = float.MaxValue, f2 = float.MaxValue;
		for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
				for ( var dz = -1; dz <= 1; dz++ )
				{
					int x = cx + dx, y = cy + dy, z = cz + dz;
					var h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ seed * 2654435761u);
					float R() { h ^= h << 13; h ^= h >> 17; h ^= h << 5; return (h & 0xFFFFFF) / (float)0x1000000; }
					var pt = new Vec3( x + 0.08f + 0.84f * R(), y + 0.08f + 0.84f * R(), z + 0.08f + 0.84f * R() );
					var d = (pt - q).Length;
					if ( d < f1 ) { f2 = f1; f1 = d; }
					else if ( d < f2 ) f2 = d;
				}
		return (f1, f2);
	}
}
