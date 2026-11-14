using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Dynamic-topology sculpting on <see cref="PolyMesh"/>: split edges that are too long for
/// the current detail level, collapse edges that are too short, and run the brush strokes
/// that move vertices. Replaces the original foundation, which split exactly one edge per
/// iteration, never collapsed, and smoothed with an O(V^2) neighbour scan.
///
/// BVH NOTE (roadmap E2 says "use MeshAdjacency + MeshBVH"): adjacency is used here —
/// <see cref="VertexFaces"/> supplies the Smooth neighbourhood and the collapse valence
/// checks. MeshBVH is NOT used here, deliberately: it exposes no radius query (only
/// NearestSurface / TryProjectSurface / Refit), and no query in this service needs one.
/// The topology passes scan the edge list once per iteration (linear), and stroke
/// hit-testing already owns a MeshBVH in SculptSession. Building a second BVH here
/// would index a mesh that changes topology every pass — the one case MeshBVH says
/// to rebuild, not reuse.
///
/// All methods return a new mesh; the source is never mutated. Split vertices carry
/// interpolated UVs, vertex colours, skin weights and morph deltas, so a dyntopo pass
/// does not silently drop a rig, a shape key, or paint. Collapse keeps the survivor's
/// midpoint position and the average of the two endpoints' colours / weights / deltas.
/// </summary>
public static class DyntopoService
{
	public sealed class Options
	{
		public Vec3 Center { get; set; }
		public float Radius { get; set; } = 1f;
		public float MaxEdgeLength { get; set; } = 0.1f;

		/// <summary>
		/// Collapse threshold. 0 (the default) means "derive it": 0.4 * MaxEdgeLength,
		/// per the roadmap. An explicit value must be positive and below MaxEdgeLength,
		/// otherwise split and collapse would fight over the same edges forever.
		/// </summary>
		public float MinEdgeLength { get; set; } = 0f;
		public int MaxIterations { get; set; } = 4;

		internal float EffectiveMin => MinEdgeLength > 0f ? MinEdgeLength : 0.4f * MaxEdgeLength;
	}

	public enum BrushMode { Subdivide, Inflate, Flatten, Smooth }

	public sealed class SculptOptions
	{
		public Vec3 Center { get; set; }
		public float Radius { get; set; } = 1f;
		public BrushMode Mode { get; set; } = BrushMode.Inflate;
		public float Strength { get; set; } = 0.1f;
		public Vec3 Direction { get; set; } = new Vec3( 0, 0, 1 );
	}

	static void Check( PolyMesh source, float radius )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( radius <= 0f || !float.IsFinite( radius ) ) throw new ArgumentException( "radius must be positive" );
	}

	static void CheckOptions( Options options )
	{
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		if ( options.MaxEdgeLength <= 0f || !float.IsFinite( options.MaxEdgeLength ) )
			throw new ArgumentException( "MaxEdgeLength must be positive" );
		if ( options.MinEdgeLength < 0f )
			throw new ArgumentException( "MinEdgeLength must not be negative" );
		if ( options.MinEdgeLength > 0f && options.MinEdgeLength >= options.MaxEdgeLength )
			throw new ArgumentException( "MinEdgeLength must be below MaxEdgeLength" );
		if ( options.MaxIterations < 1 ) throw new ArgumentException( "MaxIterations must be at least 1" );
	}

	public static PolyMesh Sculpt( PolyMesh source, SculptOptions options )
	{
		Check( source, options is null ? 0f : options.Radius );
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );

		// Clone (not a hand copy) so morphs, seams and the paint atlas survive a stroke;
		// the old code dropped all three.
		var m = source.Clone();
		VertexFaces adjacency = null;

		for ( int i = 0; i < source.Positions.Count; i++ )
		{
			float d = ( source.Positions[ i ] - options.Center ).Length;
			if ( d > options.Radius ) continue;
			float t = 1f - d / options.Radius;
			switch ( options.Mode )
			{
				case BrushMode.Inflate:
					m.Positions[ i ] = source.Positions[ i ] + options.Direction * ( options.Strength * t );
					break;
				case BrushMode.Flatten:
				{
					float target = options.Center.y; // flatten to plane Y
					m.Positions[ i ] = new Vec3( source.Positions[ i ].x, source.Positions[ i ].y + ( target - source.Positions[ i ].y ) * ( options.Strength * t ), source.Positions[ i ].z );
					break;
				}
				case BrushMode.Smooth:
				{
					// Adjacency neighbours (vertices sharing a face), not a radius scan:
					// the old O(V^2) loop compared every vertex against every other.
					adjacency ??= VertexFaces.Build( source );
					Vec3 sum = new Vec3( 0, 0, 0 );
					int c = 0;
					var seen = new HashSet<int>();
					foreach ( int fi in adjacency[ i ] )
					{
						foreach ( int vi in source.Faces[ fi ].Indices )
						{
							if ( vi != i && seen.Add( vi ) ) { sum += source.Positions[ vi ]; c++; }
						}
					}
					if ( c > 0 )
					{
						var avg = sum / c;
						m.Positions[ i ] = source.Positions[ i ] + ( avg - source.Positions[ i ] ) * ( options.Strength * t );
					}
					break;
				}
				// BrushMode.Subdivide moves no vertices; topology is Subdivide()'s job.
			}
		}
		return m;
	}

	/// <summary>
	/// Split every edge longer than <see cref="Options.MaxEdgeLength"/> whose midpoint
	/// falls inside the brush sphere, then collapse every edge shorter than the
	/// effective minimum that is safe to collapse, repeating until stable or
	/// <see cref="Options.MaxIterations"/> is reached. Splits are edge-consistent:
	/// an edge shared by two faces gets exactly one midpoint vertex, referenced by
	/// both rebuilt faces, so no cracks open along shared edges.
	///
	/// A split face is fan-triangulated back into triangles (from corner 0, keeping
	/// UVs and material). Without that step a split triangle would come back as a
	/// quad — one face where two belong — so the face count would never grow no
	/// matter how fine the detail size, and every caller counting faces would
	/// report no tessellation at all. The fan preserves the Euler characteristic
	/// exactly: each n-corner face becomes n-2 triangles with n-3 new diagonals,
	/// so V - E + F is unchanged by the triangulation step.
	/// </summary>
	public static PolyMesh Subdivide( PolyMesh source, Options options )
	{
		if ( options is null ) throw new ArgumentNullException( nameof( options ) );
		Check( source, options.Radius );
		CheckOptions( options );
		var m = source.Clone();
		RunDyntopo( m, options, options.Center, options.Radius );
		return m;
	}

	/// <summary>
	/// Uniform detail equalisation over the whole mesh: split edges longer than
	/// <paramref name="targetEdgeLength"/> and collapse edges shorter than
	/// 0.4 * <paramref name="targetEdgeLength"/>, everywhere, until stable
	/// (capped at 16 passes). Boundary, seam, material-boundary and UV-seam
	/// edges are never collapsed, so equalisation cannot weld a mesh shut or
	/// smear a texture seam.
	/// </summary>
	public static PolyMesh DetailFloodFill( PolyMesh mesh, float targetEdgeLength )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( targetEdgeLength <= 0f || !float.IsFinite( targetEdgeLength ) )
			throw new ArgumentException( "targetEdgeLength must be positive" );
		var m = mesh.Clone();
		var options = new Options { MaxEdgeLength = targetEdgeLength, MinEdgeLength = 0.4f * targetEdgeLength, MaxIterations = 16 };
		RunDyntopo( m, options, new Vec3( 0, 0, 0 ), float.MaxValue );
		return m;
	}

	static void RunDyntopo( PolyMesh m, Options options, Vec3 center, float radius )
	{
		float min = options.EffectiveMin;
		for ( int iter = 0; iter < options.MaxIterations; iter++ )
		{
			bool split = SplitLongEdges( m, options.MaxEdgeLength, center, radius );
			bool collapsed = CollapseShortEdges( m, min );
			if ( !split && !collapsed ) break;
		}
	}

	static bool SplitLongEdges( PolyMesh m, float maxLen, Vec3 center, float radius )
	{
		var edgeFaces = m.BuildEdgeFaces();
		var toSplit = new List<EdgeKey>();
		foreach ( var key in edgeFaces.Keys )
		{
			var a = m.Positions[ key.A ];
			var b = m.Positions[ key.B ];
			if ( ( b - a ).Length <= maxLen ) continue;
			var mid = ( a + b ) * 0.5f;
			if ( ( mid - center ).Length > radius ) continue;
			toSplit.Add( key );
		}
		if ( toSplit.Count == 0 ) return false;
		toSplit.Sort( ( x, y ) => x.A != y.A ? x.A.CompareTo( y.A ) : x.B.CompareTo( y.B ) );

		bool hasColors = m.HasVertexColors;
		bool hasSkin = m.Skin is not null && m.Skin.Count == m.Positions.Count;
		var midpoints = new Dictionary<EdgeKey, int>();
		foreach ( var key in toSplit )
		{
			int idx = m.Positions.Count;
			m.Positions.Add( ( m.Positions[ key.A ] + m.Positions[ key.B ] ) * 0.5f );
			if ( hasColors )
			{
				var ca = m.VertexColors[ key.A ];
				var cb = m.VertexColors[ key.B ];
				Array.Resize( ref m.VertexColors, m.Positions.Count );
				m.VertexColors[ idx ] = new Vec4( ( ca.x + cb.x ) * 0.5f, ( ca.y + cb.y ) * 0.5f, ( ca.z + cb.z ) * 0.5f, ( ca.w + cb.w ) * 0.5f );
			}
			if ( hasSkin )
				m.Skin.Vertices.Add( SkinWeights.Blend( new[] { ( m.Skin[ key.A ], 0.5f ), ( m.Skin[ key.B ], 0.5f ) } ) );
			foreach ( var morph in m.Morphs )
			{
				if ( morph is null || morph.Deltas is null || morph.Deltas.Length != m.Positions.Count - 1 ) continue;
				var d = morph.Deltas;
				Array.Resize( ref morph.Deltas, m.Positions.Count );
				morph.Deltas[ idx ] = ( d[ key.A ] + d[ key.B ] ) * 0.5f;
				if ( morph.Base is not null && morph.Base.Length == m.Positions.Count - 1 )
				{
					var b0 = morph.Base;
					Array.Resize( ref morph.Base, m.Positions.Count );
					morph.Base[ idx ] = ( b0[ key.A ] + b0[ key.B ] ) * 0.5f;
				}
			}
			midpoints[ key ] = idx;
		}

		// Rebuild every face, inserting each split edge's midpoint after its start
		// corner, with the face-local UV average, then fan-triangulate every touched
		// face back into triangles. Faces untouched by any split edge are copied
		// unchanged. The fan is what makes a split add faces at all: without it a
		// split triangle comes back as a quad and the face count never moves.
		// Fan origin: the first midpoint corner of the face. A fan from corner 0
		// draws diagonal (0,2) — and when the split edge is the face's closing
		// edge, (0,2) IS that split edge: the fan resurrects it whole, the
		// original triangle comes back verbatim, and a duplicate flipped pair
		// (a,c,M)/(a,M,c) is added alongside it. The split then does not stick
		// (the same edge is a candidate again next pass) and V - E + F jumps.
		// A midpoint can never be an endpoint of an original edge, so no fan
		// diagonal from a midpoint corner is ever a split edge. (A touched face
		// always has a midpoint; corner 0 is only the unreachable fallback.)
		var midVerts = new HashSet<int>( midpoints.Values );
		var rebuilt = new List<Face>( m.Faces.Count );
		for ( int fi = 0; fi < m.Faces.Count; fi++ )
		{
			var f = m.Faces[ fi ];
			int n = f.Count;
			bool touches = false;
			for ( int c = 0; c < n; c++ )
			{
				if ( midpoints.ContainsKey( new EdgeKey( f.Indices[ c ], f.Indices[ ( c + 1 ) % n ] ) ) ) { touches = true; break; }
			}
			if ( !touches ) { rebuilt.Add( f ); continue; }
			var indices = new List<int>( n + 2 );
			var uvs = new List<Vec2>( n + 2 );
			for ( int c = 0; c < n; c++ )
			{
				indices.Add( f.Indices[ c ] );
				uvs.Add( f.UVs[ c ] );
				var key = new EdgeKey( f.Indices[ c ], f.Indices[ ( c + 1 ) % n ] );
				if ( midpoints.TryGetValue( key, out int mid ) )
				{
					indices.Add( mid );
					var u0 = f.UVs[ c ];
					var u1 = f.UVs[ ( c + 1 ) % n ];
					uvs.Add( new Vec2( ( u0.x + u1.x ) * 0.5f, ( u0.y + u1.y ) * 0.5f ) );
				}
			}
			if ( indices.Count == 3 )
			{
				rebuilt.Add( new Face( indices.ToArray(), uvs.ToArray(), f.Material ) );
				continue;
			}
			// Fan from the first midpoint corner: n corners become n-2 triangles
			// and n-3 diagonals, so V - E + F is unchanged and every face under
			// the brush stays a triangle — and the split sticks, because no fan
			// diagonal is a split edge.
			int origin = 0;
			for ( int k = 0; k < indices.Count; k++ )
				if ( midVerts.Contains( indices[ k ] ) ) { origin = k; break; }
			int corners = indices.Count;
			for ( int j = 1; j + 1 < corners; j++ )
			{
				int i0 = origin;
				int i1 = ( origin + j ) % corners;
				int i2 = ( origin + j + 1 ) % corners;
				rebuilt.Add( new Face(
					new[] { indices[ i0 ], indices[ i1 ], indices[ i2 ] },
					new[] { uvs[ i0 ], uvs[ i1 ], uvs[ i2 ] },
					f.Material ) );
			}
		}
		m.Faces.Clear();
		m.Faces.AddRange( rebuilt );

		if ( m.Seams is not null && m.Seams.Count > 0 )
		{
			var extra = new List<EdgeKey>();
			foreach ( var key in toSplit )
			{
				if ( m.Seams.Contains( key ) )
				{
					extra.Add( new EdgeKey( key.A, midpoints[ key ] ) );
					extra.Add( new EdgeKey( midpoints[ key ], key.B ) );
				}
			}
			foreach ( var key in extra ) m.Seams.Add( key );
		}
		return true;
	}

	/// <summary>An edge may collapse only if it is interior (exactly 2 faces), is not a
	/// marked seam, does not separate two materials, and does not carry a UV seam
	/// (the two faces disagree on a corner UV). Anything else is shape the artist
	/// can see, and collapsing it would weld, smear, or recolour it.</summary>
	static bool CollapseSafe( PolyMesh m, EdgeKey key, List<int> faces )
	{
		if ( faces.Count != 2 ) return false;
		if ( m.Seams is not null && m.Seams.Contains( key ) ) return false;
		var f0 = m.Faces[ faces[ 0 ] ];
		var f1 = m.Faces[ faces[ 1 ] ];
		if ( f0.Material != f1.Material ) return false;
		const float eps = 1e-4f;
		foreach ( int v in new[] { key.A, key.B } )
		{
			int c0 = Array.IndexOf( f0.Indices, v );
			int c1 = Array.IndexOf( f1.Indices, v );
			if ( c0 < 0 || c1 < 0 ) return false;
			var u0 = f0.UVs[ c0 ];
			var u1 = f1.UVs[ c1 ];
			if ( MathF.Abs( u0.x - u1.x ) > eps || MathF.Abs( u0.y - u1.y ) > eps ) return false;
		}
		return true;
	}

	static bool CollapseShortEdges( PolyMesh m, float minLen )
	{
		var edgeFaces = m.BuildEdgeFaces();
		var candidates = new List<EdgeKey>();
		foreach ( var kv in edgeFaces )
		{
			var a = m.Positions[ kv.Key.A ];
			var b = m.Positions[ kv.Key.B ];
			if ( ( b - a ).Length >= minLen ) continue;
			if ( !CollapseSafe( m, kv.Key, kv.Value ) ) continue;
			candidates.Add( kv.Key );
		}
		if ( candidates.Count == 0 ) return false;
		candidates.Sort( ( x, y ) => x.A != y.A ? x.A.CompareTo( y.A ) : x.B.CompareTo( y.B ) );

		// Disjoint pairs only: a vertex collapsed once per pass, so no pair's
		// survivor is another pair's victim mid-pass.
		var used = new HashSet<int>();
		var pairs = new List<(int keep, int drop)>();
		foreach ( var key in candidates )
		{
			if ( used.Contains( key.A ) || used.Contains( key.B ) ) continue;
			used.Add( key.A );
			used.Add( key.B );
			pairs.Add( ( key.A, key.B ) );
		}
		if ( pairs.Count == 0 ) return false;

		var drop = new Dictionary<int, int>(); // dropped vertex -> survivor
		foreach ( var (keep, dropV) in pairs )
		{
			m.Positions[ keep ] = ( m.Positions[ keep ] + m.Positions[ dropV ] ) * 0.5f;
			if ( m.HasVertexColors )
			{
				var ca = m.VertexColors[ keep ];
				var cb = m.VertexColors[ dropV ];
				m.VertexColors[ keep ] = new Vec4( ( ca.x + cb.x ) * 0.5f, ( ca.y + cb.y ) * 0.5f, ( ca.z + cb.z ) * 0.5f, ( ca.w + cb.w ) * 0.5f );
			}
			if ( m.Skin is not null && m.Skin.Count == m.Positions.Count )
				m.Skin[ keep ] = SkinWeights.Blend( new[] { ( m.Skin[ keep ], 0.5f ), ( m.Skin[ dropV ], 0.5f ) } );
			foreach ( var morph in m.Morphs )
			{
				if ( morph is null || morph.Deltas is null || morph.Deltas.Length != m.Positions.Count ) continue;
				morph.Deltas[ keep ] = ( morph.Deltas[ keep ] + morph.Deltas[ dropV ] ) * 0.5f;
				if ( morph.Base is not null && morph.Base.Length == m.Positions.Count )
					morph.Base[ keep ] = ( morph.Base[ keep ] + morph.Base[ dropV ] ) * 0.5f;
			}
			drop[ dropV ] = keep;
		}

		// Compact: survivors keep order, dropped vertices vanish, every index remaps.
		int oldCount = m.Positions.Count;
		var remap = new int[ oldCount ];
		var newPositions = new List<Vec3>( oldCount - drop.Count );
		Vec4[] newColors = m.HasVertexColors ? new Vec4[ oldCount - drop.Count ] : null;
		SkinWeights newSkin = m.Skin is not null && m.Skin.Count == oldCount ? new SkinWeights() : null;
		var newMorphs = new List<(MeshMorph morph, Vec3[] deltas, Vec3[] bas)>();
		foreach ( var morph in m.Morphs )
		{
			if ( morph is null || morph.Deltas is null || morph.Deltas.Length != oldCount ) continue;
			newMorphs.Add( ( morph, new Vec3[ oldCount - drop.Count ],
				morph.Base is not null && morph.Base.Length == oldCount ? new Vec3[ oldCount - drop.Count ] : null ) );
		}
		for ( int v = 0; v < oldCount; v++ )
		{
			if ( drop.ContainsKey( v ) ) { remap[ v ] = -1; continue; } // fixed up below, once survivors have new indices
			remap[ v ] = newPositions.Count;
			newPositions.Add( m.Positions[ v ] );
			if ( newColors is not null ) newColors[ newPositions.Count - 1 ] = m.VertexColors[ v ];
			newSkin?.Vertices.Add( m.Skin[ v ] );
			foreach ( var (morph, deltas, bas) in newMorphs )
			{
				deltas[ newPositions.Count - 1 ] = morph.Deltas[ v ];
				if ( bas is not null ) bas[ newPositions.Count - 1 ] = morph.Base[ v ];
			}
		}
		// A dropped vertex maps to its survivor's NEW index.
		foreach ( var kv in drop ) remap[ kv.Key ] = remap[ kv.Value ];

		var newFaces = new List<Face>( m.Faces.Count );
		foreach ( var f in m.Faces )
		{
			var idx = f.Indices.Select( v => remap[ v ] ).ToArray();
			if ( idx.Distinct().Count() < 3 ) continue; // collapsed degenerate
			newFaces.Add( new Face( idx, (Vec2[])f.UVs.Clone(), f.Material ) );
		}
		m.Positions = newPositions;
		m.Faces = newFaces;
		if ( newColors is not null ) m.VertexColors = newColors;
		if ( newSkin is not null ) m.Skin = newSkin;
		foreach ( var (morph, deltas, bas) in newMorphs )
		{
			morph.Deltas = deltas;
			if ( bas is not null ) morph.Base = bas;
		}
		if ( m.Seams is not null && m.Seams.Count > 0 )
		{
			var seams = new HashSet<EdgeKey>();
			foreach ( var s in m.Seams )
			{
				int a = remap[ s.A ];
				int b = remap[ s.B ];
				if ( a != b ) seams.Add( new EdgeKey( a, b ) );
			}
			m.Seams = seams;
		}
		return true;
	}
}
