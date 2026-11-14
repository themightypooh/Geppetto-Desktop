// Hammer displacement data model — Geppetto Desktop, backend only (no UI).
//
// WHAT A DISPLACEMENT IS (Source / Hammer, Report §1.2 #13 + §5.2):
// A displacement is NOT a free mesh. It is a subdivided quad bound to one 4-sided
// brush face: a grid of (2^power + 1) x (2^power + 1) vertices (power 2/3/4, so
// 5x5 / 9x9 / 17x17), per-vertex offsets along the face normal, per-vertex blend
// alpha between two materials, sew edges that match shared edges between adjacent
// displacements, and a subdivide step. Displacements are solid and walkable but do
// NOT seal the map — that is recorded here as the checkable rule `SealsMap == false`
// (see below), not as a compile-time assumption: the map validation engine must read
// it when deciding whether a leaf is sealed.
//
// MAPBRUSH STATUS (dependency hammer-brush-core-plane-based-brushes):
// MapBrush has NOT landed yet (board status todo, no owner, no kernel/Effigy/Map/
// directory as of 2026-11-13). This file therefore defines `MapBrushFace` itself:
// exactly the face contract that task's desc specifies (plane normal + distance,
// 4 ordered corners, material id, UV offset/scale/rotation, lightmap scale,
// smoothing group). When MapBrush lands it can reuse this struct as-is, or the
// integrator adds a one-line `Displacement.Create(MapBrush.Face, power)` adapter
// overload. Do NOT invent a second face type with the same responsibility.
//
// SEW SCOPE (scalar model, per the ticket spec):
// Offsets are scalars along the face normal, so two grids can share an edge
// position-for-position only when their faces are coplanar (same normal within
// tolerance). Sew() returns false for non-coplanar faces instead of silently
// averaging meaningless numbers; Source's non-coplanar sew needs per-vertex 3D
// offset vectors (`offsets` in dispinfo), which is a later ticket, not this one.
//
// SUBDIVIDE:
// `Subdivide()` runs one real Catmull-Clark step (CatmullClark.SubdivideWithMap)
// over the grid as a PolyMesh, then projects every output vertex back onto the
// heightfield (offset = dot(ccPosition - basePosition, normal)) and pins the outer
// ring to the original boundary loop, so the displacement stays sewn to its
// neighbours and on its brush face. Power rises by 1; power 4 cannot subdivide
// (Source maximum) and throws. Sew records are NOT carried across a subdivide:
// re-sew afterwards; the boundary geometry is unchanged, so Sew() re-matches.
//
// ROUND-TRIP:
// StudioDocument / VMF wiring belongs to the map-document ticket (shared files,
// not owned here). This file provides the lossless primitive for it:
// `Snapshot()` / `FromSnapshot()` (power + face + offsets + alphas). Sew topology
// is map-level state and is re-established by Sew() when a map loads.

using System;
using System.Collections.Generic;

namespace Effigy.Map;

/// <summary>
/// One quad face of a MapBrush, as the brush-core spec defines it: a plane
/// (normal + distance) with 4 ordered corners, a material id, a UV transform,
/// a lightmap scale and a smoothing group. Corner UVs are resolved from the UV
/// transform at construction, so a displacement can interpolate them across its
/// grid without knowing how the brush derived them.
/// </summary>
public readonly struct MapBrushFace
{
	public readonly Vec3[] Corners;
	public readonly Vec3 Normal;
	public readonly float PlaneDistance;
	public readonly int MaterialId;
	public readonly Vec2 UvOffset;
	public readonly Vec2 UvScale;
	public readonly float UvRotationDegrees;
	public readonly float LightmapScale;
	public readonly int SmoothingGroup;
	public readonly Vec2[] CornerUVs;

	public MapBrushFace( Vec3[] corners, Vec3 normal, float planeDistance, int materialId,
		Vec2 uvOffset, Vec2 uvScale, float uvRotationDegrees, float lightmapScale, int smoothingGroup,
		Vec2[] cornerUVs )
	{
		if ( corners is null || corners.Length != 4 )
			throw new ArgumentException( "A brush face bound to a displacement must have exactly 4 corners.", nameof( corners ) );
		if ( cornerUVs is null || cornerUVs.Length != 4 )
			throw new ArgumentException( "A brush face bound to a displacement must have exactly 4 corner UVs.", nameof( cornerUVs ) );
		Corners = (Vec3[])corners.Clone();
		Normal = normal;
		PlaneDistance = planeDistance;
		MaterialId = materialId;
		UvOffset = uvOffset;
		UvScale = uvScale;
		UvRotationDegrees = uvRotationDegrees;
		LightmapScale = lightmapScale;
		SmoothingGroup = smoothingGroup;
		CornerUVs = (Vec2[])cornerUVs.Clone();
	}

	/// <summary>
	/// Build a face from 4 ordered corners. The plane normal comes from Newell's
	/// method, the plane distance from the first corner, and the corner UVs from
	/// projecting the corners onto the face's tangent frame (see ProjectUVs).
	/// A zero UV scale means 1 (the `default` of a Vec2 is (0,0), not (1,1)).
	/// </summary>
	public static MapBrushFace FromCorners( Vec3[] corners, int materialId = 0, Vec2 uvOffset = default,
		Vec2 uvScale = default, float uvRotationDegrees = 0f, float lightmapScale = 16f, int smoothingGroup = 0 )
	{
		if ( corners is null || corners.Length != 4 )
			throw new ArgumentException( "A brush face bound to a displacement must have exactly 4 corners.", nameof( corners ) );
		var normal = NewellNormal( corners );
		if ( normal.Length < 1e-6f )
			throw new ArgumentException( "Cannot build a brush face from degenerate corners (zero area).", nameof( corners ) );
		var distance = Vec3.Dot( normal, corners[0] );
		var scale = uvScale.x == 0f && uvScale.y == 0f ? new Vec2( 1f, 1f ) : uvScale;
		var uvs = ProjectUVs( corners, normal, uvOffset, scale, uvRotationDegrees );
		return new MapBrushFace( corners, normal, distance, materialId, uvOffset, scale,
			uvRotationDegrees, lightmapScale, smoothingGroup, uvs );
	}

	/// <summary>Newell's method: correct for non-planar quads, where a cross product of the first three corners is not.</summary>
	public static Vec3 NewellNormal( Vec3[] corners )
	{
		var n = NewellVector( corners );
		return n.Length < 1e-6f ? Vec3.Zero : n.Normal;
	}

	static Vec3 NewellVector( Vec3[] corners )
	{
		var n = Vec3.Zero;
		for ( var i = 0; i < corners.Length; i++ )
		{
			var a = corners[i];
			var b = corners[( i + 1 ) % corners.Length];
			n = new Vec3(
				n.x + ( a.y - b.y ) * ( a.z + b.z ),
				n.y + ( a.z - b.z ) * ( a.x + b.x ),
				n.z + ( a.x - b.x ) * ( a.y + b.y ) );
		}
		return n;
	}

	/// <summary>
	/// Corner UVs by projecting onto the face's tangent frame. U is the normal
	/// crossed with the world axis least aligned with it (so the frame never
	/// degenerates), V is normal x U (right-handed: U x V == normal). UV rotation
	/// rotates the result about the UV origin after offset/scale. For a unit
	/// square in the XY plane this gives (0,0), (0,-1), (1,-1), (1,0) — negative
	/// V is legal; the frame, not the sign, is the contract.
	/// </summary>
	static Vec2[] ProjectUVs( Vec3[] corners, Vec3 normal, Vec2 offset, Vec2 scale, float rotationDegrees )
	{
		var ax = MathF.Abs( normal.x );
		var ay = MathF.Abs( normal.y );
		var az = MathF.Abs( normal.z );
		var reference = ax <= ay && ax <= az ? new Vec3( 1f, 0f, 0f )
			: ay <= az ? new Vec3( 0f, 1f, 0f ) : new Vec3( 0f, 0f, 1f );
		var uAxis = Vec3.Cross( normal, reference ).Normal;
		var vAxis = Vec3.Cross( normal, uAxis );
		var radians = rotationDegrees * MathF.PI / 180f;
		var cos = MathF.Cos( radians );
		var sin = MathF.Sin( radians );
		var uvs = new Vec2[4];
		for ( var i = 0; i < 4; i++ )
		{
			var d = corners[i] - corners[0];
			var u = Vec3.Dot( d, uAxis ) * scale.x + offset.x;
			var v = Vec3.Dot( d, vAxis ) * scale.y + offset.y;
			uvs[i] = new Vec2( u * cos - v * sin, u * sin + v * cos );
		}
		return uvs;
	}

	/// <summary>Named defects, same convention as MapBrush.Validate(): empty means valid.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( Corners is null || Corners.Length != 4 )
		{
			defects.Add( "face-corner-count" );
			return defects;
		}
		if ( CornerUVs is null || CornerUVs.Length != 4 )
			defects.Add( "face-uv-count" );
		if ( Normal.Length < 1e-6f )
			defects.Add( "face-zero-normal" );
		var degenerate = false;
		for ( var i = 0; i < 4 && !degenerate; i++ )
			for ( var j = i + 1; j < 4; j++ )
				if ( ( Corners[i] - Corners[j] ).Length < 1e-6f )
					degenerate = true;
		if ( NewellVector( Corners ).Length * 0.5f < 1e-6f )
			degenerate = true;
		if ( degenerate )
			defects.Add( "face-degenerate" );
		for ( var i = 0; i < 4; i++ )
			if ( MathF.Abs( Vec3.Dot( Normal, Corners[i] ) - PlaneDistance ) > 1e-3f )
			{
				defects.Add( "face-non-planar" );
				break;
			}
		return defects;
	}

	public bool IsValid => Validate().Count == 0;

	/// <summary>Quad area from the Newell vector (half its length).</summary>
	public float Area => Corners is null || Corners.Length != 4 ? 0f : NewellVector( Corners ).Length * 0.5f;
}

/// <summary>
/// Lossless snapshot of a displacement's own state (power + face + offsets +
/// alphas). Sew topology is NOT in here: it is map-level state, re-established
/// by Sew() when a map loads its displacements.
/// </summary>
public sealed class DisplacementData
{
	public MapBrushFace Face;
	public int Power;
	public float[] Offsets;
	public float[] Alphas;
}

/// <summary>
/// A Hammer displacement: a (2^power + 1) x (2^power + 1) grid bound to one
/// 4-sided brush face, with per-vertex offsets along the face normal and
/// per-vertex blend alpha between two materials (alpha 0 = the face's material,
/// alpha 1 = the second material; the second material's identity is a
/// map/VMF-level concern, not stored here).
///
/// Grid layout is row-major: row 0 runs corner 0 -&gt; corner 1, row N-1 runs
/// corner 3 -&gt; corner 2, so (row, col) = (v, u) over the face. Edge indices:
/// 0 = row 0, 1 = col N-1, 2 = row N-1 (reversed), 3 = col 0 (reversed).
/// </summary>
public sealed class Displacement
{
	public const int MinPower = 2;
	public const int MaxPower = 4;

	/// <summary>How close two edge endpoints must be (inches) to count as the same shared edge.</summary>
	public const float SewTolerance = 1e-4f;

	/// <summary>Tolerance for Validate()'s sewn-edge agreement check (inches / alpha units).</summary>
	public const float ValidateTolerance = 1e-3f;

	public static int GridSizeForPower( int power )
	{
		if ( power < MinPower || power > MaxPower )
			throw new ArgumentOutOfRangeException( nameof( power ), power,
				$"Displacement power must be {MinPower}..{MaxPower} (Source power 2/3/4)." );
		return ( 1 << power ) + 1;
	}

	public MapBrushFace Face { get; }
	public int Power { get; }
	public int GridSize { get; }
	public int VertexCount => GridSize * GridSize;
	public Vec3 Normal => Face.Normal;

	/// <summary>Per-vertex offset along the face normal, row-major. Mutable in place: the sculpt ticket writes here.</summary>
	public float[] Offsets { get; }

	/// <summary>Per-vertex blend alpha 0..1, row-major. Mutable in place.</summary>
	public float[] Alphas { get; }

	/// <summary>
	/// VALIDATION RULE, not a compile assumption: a displacement never seals the
	/// map. It is solid and walkable, but a leaf closed only by displacements
	/// leaks. The map validation engine must read this when deciding whether a
	/// leaf is sealed; nothing in this type claims otherwise.
	/// </summary>
	public bool SealsMap => false;

	readonly List<(int Edge, Displacement Other, int OtherEdge, bool Reversed)> _sewn = new();

	Displacement( MapBrushFace face, int power, float[] offsets, float[] alphas )
	{
		Face = face;
		Power = power;
		GridSize = GridSizeForPower( power );
		Offsets = offsets;
		Alphas = alphas;
	}

	/// <summary>
	/// Bind a fresh displacement to a brush face: flat grid (all offsets and
	/// alphas zero) at the given power. Throws if the face is not a valid quad
	/// or the power is outside 2..4.
	/// </summary>
	public static Displacement Create( MapBrushFace face, int power )
	{
		var defects = face.Validate();
		if ( defects.Count > 0 )
			throw new ArgumentException(
				$"Cannot bind a displacement to an invalid brush face: {string.Join( ", ", defects )}.", nameof( face ) );
		var size = GridSizeForPower( power );
		return new Displacement( face, power, new float[size * size], new float[size * size] );
	}

	public bool IsValid => Validate().Count == 0;

	public int Index( int row, int col )
	{
		CheckGrid( row, col );
		return row * GridSize + col;
	}

	void CheckGrid( int row, int col )
	{
		if ( row < 0 || row >= GridSize || col < 0 || col >= GridSize )
			throw new ArgumentOutOfRangeException( $"Grid vertex ({row}, {col}) is outside a {GridSize}x{GridSize} grid." );
	}

	/// <summary>Undisplaced (bilinear) position of a grid vertex on the face.</summary>
	public Vec3 BasePosition( int row, int col )
	{
		CheckGrid( row, col );
		var u = (float)col / ( GridSize - 1 );
		var v = (float)row / ( GridSize - 1 );
		var c = Face.Corners;
		return c[0] * ( ( 1f - u ) * ( 1f - v ) )
			+ c[1] * ( u * ( 1f - v ) )
			+ c[2] * ( u * v )
			+ c[3] * ( ( 1f - u ) * v );
	}

	/// <summary>Displaced position: base + face normal * offset.</summary>
	public Vec3 VertexPosition( int row, int col ) => BasePosition( row, col ) + Normal * Offsets[Index( row, col )];

	/// <summary>All displaced vertex positions, row-major.</summary>
	public Vec3[] Positions()
	{
		var positions = new Vec3[VertexCount];
		for ( var r = 0; r < GridSize; r++ )
			for ( var c = 0; c < GridSize; c++ )
				positions[Index( r, c )] = VertexPosition( r, c );
		return positions;
	}

	/// <summary>UV at a grid vertex, bilinearly interpolated from the face's corner UVs.</summary>
	public Vec2 UvAt( int row, int col )
	{
		CheckGrid( row, col );
		var u = (float)col / ( GridSize - 1 );
		var v = (float)row / ( GridSize - 1 );
		var uv = Face.CornerUVs;
		return uv[0] * ( ( 1f - u ) * ( 1f - v ) )
			+ uv[1] * ( u * ( 1f - v ) )
			+ uv[2] * ( u * v )
			+ uv[3] * ( ( 1f - u ) * v );
	}

	public float GetOffset( int row, int col ) => Offsets[Index( row, col )];

	public void SetOffset( int row, int col, float value ) => Offsets[Index( row, col )] = value;

	public float GetAlpha( int row, int col ) => Alphas[Index( row, col )];

	public void SetAlpha( int row, int col, float value )
	{
		if ( value < 0f || value > 1f )
			throw new ArgumentOutOfRangeException( nameof( value ), value, "Blend alpha must be 0..1." );
		Alphas[Index( row, col )] = value;
	}

	/// <summary>The (row, col) of the i-th vertex along an edge, in edge order (see the class summary).</summary>
	public (int Row, int Col) EdgeVertex( int edge, int i )
	{
		if ( edge < 0 || edge > 3 )
			throw new ArgumentOutOfRangeException( nameof( edge ), edge, "A displacement has edges 0..3." );
		if ( i < 0 || i >= GridSize )
			throw new ArgumentOutOfRangeException( nameof( i ), i, $"Edge vertex {i} is outside a {GridSize}-vertex edge." );
		return edge switch
		{
			0 => ( 0, i ),
			1 => ( i, GridSize - 1 ),
			2 => ( GridSize - 1, GridSize - 1 - i ),
			_ => ( GridSize - 1 - i, 0 ),
		};
	}

	/// <summary>Undisplaced position of the i-th vertex along an edge.</summary>
	public Vec3 EdgeBasePosition( int edge, int i )
	{
		var (r, c) = EdgeVertex( edge, i );
		return BasePosition( r, c );
	}

	/// <summary>
	/// Sew this displacement to another along a shared edge: the two grids must
	/// share an edge (same endpoints within SewTolerance) and be coplanar (same
	/// face normal — scalar offsets along different normals cannot make edge
	/// positions coincide). Matching is by parameter along the edge, so grids of
	/// different powers can sew. Both sides' offsets and alphas along the shared
	/// edge become the average of the two old sides, so neither side silently
	/// wins and the shared edge has no gap. Returns true when an edge was sewn,
	/// false when there is no shared (coplanar) edge — which leaves both grids
	/// untouched. Idempotent: sewing again changes nothing.
	/// </summary>
	public bool Sew( Displacement other )
	{
		if ( other is null )
			throw new ArgumentNullException( nameof( other ) );
		if ( ReferenceEquals( this, other ) )
			return false;
		if ( Vec3.Dot( Normal, other.Normal ) < 0.999f )
			return false;
		for ( var ea = 0; ea < 4; ea++ )
			for ( var eb = 0; eb < 4; eb++ )
			{
				var a0 = EdgeBasePosition( ea, 0 );
				var a1 = EdgeBasePosition( ea, GridSize - 1 );
				var b0 = other.EdgeBasePosition( eb, 0 );
				var b1 = other.EdgeBasePosition( eb, other.GridSize - 1 );
				bool reversed;
				if ( Close( a0, b0 ) && Close( a1, b1 ) )
					reversed = false;
				else if ( Close( a0, b1 ) && Close( a1, b0 ) )
					reversed = true;
				else
					continue;
				SewEdges( other, ea, eb, reversed );
				return true;
			}
		return false;
	}

	static bool Close( Vec3 a, Vec3 b ) => ( a - b ).Length <= SewTolerance;

	void SewEdges( Displacement other, int myEdge, int otherEdge, bool reversed )
	{
		// Snapshot both edges FIRST: each side's new values average the two OLD
		// sides. Sampling a side after it was already written would double-count.
		var myOff = new float[GridSize];
		var myAl = new float[GridSize];
		var otOff = new float[other.GridSize];
		var otAl = new float[other.GridSize];
		for ( var i = 0; i < GridSize; i++ )
		{
			var (r, c) = EdgeVertex( myEdge, i );
			myOff[i] = Offsets[Index( r, c )];
			myAl[i] = Alphas[Index( r, c )];
		}
		for ( var j = 0; j < other.GridSize; j++ )
		{
			var (r, c) = other.EdgeVertex( otherEdge, j );
			otOff[j] = other.Offsets[other.Index( r, c )];
			otAl[j] = other.Alphas[other.Index( r, c )];
		}

		for ( var i = 0; i < GridSize; i++ )
		{
			var t = (float)i / ( GridSize - 1 );
			var (o, a) = SampleEdge( otOff, otAl, reversed ? 1f - t : t );
			var (r, c) = EdgeVertex( myEdge, i );
			Offsets[Index( r, c )] = ( myOff[i] + o ) * 0.5f;
			Alphas[Index( r, c )] = ( myAl[i] + a ) * 0.5f;
		}
		for ( var j = 0; j < other.GridSize; j++ )
		{
			var t = (float)j / ( other.GridSize - 1 );
			var (o, a) = SampleEdge( myOff, myAl, reversed ? 1f - t : t );
			var (r, c) = other.EdgeVertex( otherEdge, j );
			other.Offsets[other.Index( r, c )] = ( otOff[j] + o ) * 0.5f;
			other.Alphas[other.Index( r, c )] = ( otAl[j] + a ) * 0.5f;
		}

		RecordSew( myEdge, other, otherEdge, reversed );
		other.RecordSew( otherEdge, this, myEdge, reversed );
	}

	static (float Offset, float Alpha) SampleEdge( float[] offs, float[] als, float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		var pos = t * ( offs.Length - 1 );
		var i0 = (int)MathF.Floor( pos );
		var i1 = Math.Min( i0 + 1, offs.Length - 1 );
		var f = pos - i0;
		return ( offs[i0] + ( offs[i1] - offs[i0] ) * f, als[i0] + ( als[i1] - als[i0] ) * f );
	}

	void RecordSew( int edge, Displacement other, int otherEdge, bool reversed )
	{
		foreach ( var s in _sewn )
			if ( s.Edge == edge && ReferenceEquals( s.Other, other ) && s.OtherEdge == otherEdge )
				return;
		_sewn.Add( ( edge, other, otherEdge, reversed ) );
	}

	public bool IsSewnTo( Displacement other )
	{
		if ( other is null )
			return false;
		foreach ( var s in _sewn )
			if ( ReferenceEquals( s.Other, other ) )
				return true;
		return false;
	}

	/// <summary>Edge indices of this displacement that are currently sewn, ascending.</summary>
	public IReadOnlyList<int> SewnEdges
	{
		get
		{
			var edges = new List<int>();
			foreach ( var s in _sewn )
				if ( !edges.Contains( s.Edge ) )
					edges.Add( s.Edge );
			edges.Sort();
			return edges;
		}
	}

	/// <summary>
	/// Subdivide once via Catmull-Clark (CatmullClark.SubdivideWithMap) over the
	/// grid as a PolyMesh. Power rises by 1, so a power-2 grid becomes power-3
	/// (5x5 -&gt; 9x9). Every output vertex is projected back onto the heightfield
	/// (offset = dot(Catmull-Clark position - base position, normal)) and the
	/// outer ring is pinned to the original boundary loop, so the displacement
	/// stays on its brush face and stays sewn to its neighbours. Alphas are
	/// resampled the same way offsets are (carried for original vertices,
	/// averaged for edge/face points). Throws at power 4 (Source maximum).
	/// Sew records are NOT carried: re-sew afterwards.
	/// </summary>
	public Displacement Subdivide()
	{
		if ( Power >= MaxPower )
			throw new InvalidOperationException(
				$"Cannot subdivide a power-{Power} displacement: {MaxPower} is the Source maximum." );
		var n = GridSize;
		var mesh = new PolyMesh();
		for ( var r = 0; r < n; r++ )
			for ( var c = 0; c < n; c++ )
				mesh.AddVertex( VertexPosition( r, c ) );
		for ( var r = 0; r < n - 1; r++ )
			for ( var c = 0; c < n - 1; c++ )
			{
				var a = r * n + c;
				var b = r * n + c + 1;
				var d = ( r + 1 ) * n + c + 1;
				var e = ( r + 1 ) * n + c;
				mesh.AddFace( new[] { a, b, d, e },
					new[] { UvAt( r, c ), UvAt( r, c + 1 ), UvAt( r + 1, c + 1 ), UvAt( r + 1, c ) },
					Face.MaterialId );
			}
		var (sub, map) = CatmullClark.SubdivideWithMap( mesh );

		// The subdivided grid is (2N-1)x(2N-1): originals land on even rows/cols,
		// edge points between them, face points on odd/odd. The SubdivisionMap
		// names every output vertex by its source element, so the mapping is
		// exact rather than positional.
		var n2 = 2 * n - 1;
		var next = new Displacement( Face, Power + 1, new float[n2 * n2], new float[n2 * n2] );
		for ( var vi = 0; vi < map.OutputVertexCount; vi++ )
		{
			var sv = map.Vertices[vi];
			Vec3 ccPos;
			int nr, nc;
			float alpha;
			switch ( sv.Origin )
			{
				case SubdivisionOrigin.Original:
				{
					var r = sv.A / n;
					var c = sv.A % n;
					nr = 2 * r;
					nc = 2 * c;
					alpha = Alphas[sv.A];
					// Pin the outer ring: Catmull-Clark's boundary rule would move
					// it off the face edge.
					ccPos = IsBoundary( r, c, n ) ? mesh.Positions[sv.A] : sub.Positions[vi];
					break;
				}
				case SubdivisionOrigin.Edge:
				{
					var r1 = sv.A / n;
					var c1 = sv.A % n;
					var r2 = sv.B / n;
					var c2 = sv.B % n;
					if ( r1 == r2 )
					{
						nr = 2 * r1;
						nc = c1 + c2;
					}
					else
					{
						nr = r1 + r2;
						nc = 2 * c1;
					}
					alpha = ( Alphas[sv.A] + Alphas[sv.B] ) * 0.5f;
					ccPos = IsBoundaryEdge( r1, c1, r2, c2, n )
						? ( mesh.Positions[sv.A] + mesh.Positions[sv.B] ) * 0.5f
						: sub.Positions[vi];
					break;
				}
				default: // SubdivisionOrigin.Face
				{
					var fr = sv.A / ( n - 1 );
					var fc = sv.A % ( n - 1 );
					nr = 2 * fr + 1;
					nc = 2 * fc + 1;
					var a = fr * n + fc;
					var b = fr * n + fc + 1;
					var d = ( fr + 1 ) * n + fc + 1;
					var e = ( fr + 1 ) * n + fc;
					alpha = ( Alphas[a] + Alphas[b] + Alphas[d] + Alphas[e] ) * 0.25f;
					ccPos = sub.Positions[vi];
					break;
				}
			}
			next.Offsets[next.Index( nr, nc )] = Vec3.Dot( ccPos - next.BasePosition( nr, nc ), Normal );
			next.Alphas[next.Index( nr, nc )] = alpha;
		}
		return next;
	}

	static bool IsBoundary( int r, int c, int n ) => r == 0 || r == n - 1 || c == 0 || c == n - 1;

	static bool IsBoundaryEdge( int r1, int c1, int r2, int c2, int n ) =>
		( r1 == 0 && r2 == 0 ) || ( r1 == n - 1 && r2 == n - 1 )
		|| ( c1 == 0 && c2 == 0 ) || ( c1 == n - 1 && c2 == n - 1 );

	/// <summary>
	/// Bake/export mesh: the displaced grid as quads with the face's material
	/// and interpolated UVs. The two-material blend survives in VertexColors
	/// (grey = alpha, A = 1), because a PolyMesh face carries a single material
	/// slot — an exporter that splits on alpha reads it from there.
	/// </summary>
	public PolyMesh ToMesh()
	{
		var mesh = new PolyMesh();
		for ( var r = 0; r < GridSize; r++ )
			for ( var c = 0; c < GridSize; c++ )
				mesh.AddVertex( VertexPosition( r, c ) );
		for ( var r = 0; r < GridSize - 1; r++ )
			for ( var c = 0; c < GridSize - 1; c++ )
			{
				var a = Index( r, c );
				var b = Index( r, c + 1 );
				var d = Index( r + 1, c + 1 );
				var e = Index( r + 1, c );
				mesh.AddFace( new[] { a, b, d, e },
					new[] { UvAt( r, c ), UvAt( r, c + 1 ), UvAt( r + 1, c + 1 ), UvAt( r + 1, c ) },
					Face.MaterialId );
			}
		var colors = new Vec4[VertexCount];
		for ( var i = 0; i < VertexCount; i++ )
			colors[i] = new Vec4( Alphas[i], Alphas[i], Alphas[i], 1f );
		mesh.VertexColors = colors;
		return mesh;
	}

	/// <summary>
	/// Named defects, same convention as MapBrush.Validate(): empty means valid.
	/// Checks: power in 2..4, grid size matches power, alphas in 0..1, the bound
	/// face is valid, and every sewn edge still agrees (sampled at 17 parameters
	/// along the edge, so grids of different powers compare correctly).
	/// The no-seal rule is NOT a defect here — it is the `SealsMap` property the
	/// map validation engine reads.
	/// </summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( Power < MinPower || Power > MaxPower )
			defects.Add( "power-out-of-range" );
		if ( Offsets is null || Offsets.Length != VertexCount
			|| Alphas is null || Alphas.Length != VertexCount )
			defects.Add( "grid-size-mismatch" );
		else
		{
			for ( var i = 0; i < Alphas.Length; i++ )
				if ( Alphas[i] < -1e-6f || Alphas[i] > 1f + 1e-6f )
				{
					defects.Add( "alpha-out-of-range" );
					break;
				}
		}
		defects.AddRange( Face.Validate() );
		foreach ( var s in _sewn )
		{
			var bad = false;
			for ( var k = 0; k <= 16 && !bad; k++ )
			{
				var t = k / 16f;
				var (mo, ma) = SampleEdgeAt( this, s.Edge, t );
				var (oo, oa) = SampleEdgeAt( s.Other, s.OtherEdge, s.Reversed ? 1f - t : t );
				if ( MathF.Abs( mo - oo ) > ValidateTolerance || MathF.Abs( ma - oa ) > ValidateTolerance )
					bad = true;
			}
			if ( bad )
				defects.Add( "sewn-edge-mismatch" );
		}
		return defects;
	}

	static (float Offset, float Alpha) SampleEdgeAt( Displacement d, int edge, float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		var pos = t * ( d.GridSize - 1 );
		var i0 = (int)MathF.Floor( pos );
		var i1 = Math.Min( i0 + 1, d.GridSize - 1 );
		var f = pos - i0;
		var (r0, c0) = d.EdgeVertex( edge, i0 );
		var (r1, c1) = d.EdgeVertex( edge, i1 );
		var o0 = d.Offsets[d.Index( r0, c0 )];
		var o1 = d.Offsets[d.Index( r1, c1 )];
		var a0 = d.Alphas[d.Index( r0, c0 )];
		var a1 = d.Alphas[d.Index( r1, c1 )];
		return ( o0 + ( o1 - o0 ) * f, a0 + ( a1 - a0 ) * f );
	}

	/// <summary>Deep copy of the grid state. Sew records are NOT copied: the clone is unsewn.</summary>
	public Displacement Clone()
	{
		var offsets = new float[VertexCount];
		var alphas = new float[VertexCount];
		Array.Copy( Offsets, offsets, VertexCount );
		Array.Copy( Alphas, alphas, VertexCount );
		return new Displacement( Face, Power, offsets, alphas );
	}

	/// <summary>Lossless snapshot for the map-document round-trip (see the file header).</summary>
	public DisplacementData Snapshot()
	{
		var data = new DisplacementData
		{
			Face = Face,
			Power = Power,
			Offsets = new float[VertexCount],
			Alphas = new float[VertexCount],
		};
		Array.Copy( Offsets, data.Offsets, VertexCount );
		Array.Copy( Alphas, data.Alphas, VertexCount );
		return data;
	}

	/// <summary>Restore a snapshot. Throws ArgumentException naming the defect (e.g. grid-size-mismatch) when the snapshot is invalid.</summary>
	public static Displacement FromSnapshot( DisplacementData data )
	{
		if ( data is null )
			throw new ArgumentNullException( nameof( data ) );
		var faceDefects = data.Face.Validate();
		if ( faceDefects.Count > 0 )
			throw new ArgumentException(
				$"Snapshot face is invalid: {string.Join( ", ", faceDefects )}.", nameof( data ) );
		if ( data.Power < MinPower || data.Power > MaxPower )
			throw new ArgumentException(
				$"Snapshot power {data.Power} is out of range {MinPower}..{MaxPower}.", nameof( data ) );
		var size = GridSizeForPower( data.Power );
		if ( data.Offsets is null || data.Offsets.Length != size * size
			|| data.Alphas is null || data.Alphas.Length != size * size )
			throw new ArgumentException(
				"Snapshot grid-size-mismatch: offsets/alphas length does not match power.", nameof( data ) );
		var offsets = new float[size * size];
		var alphas = new float[size * size];
		Array.Copy( data.Offsets, offsets, size * size );
		Array.Copy( data.Alphas, alphas, size * size );
		return new Displacement( data.Face, data.Power, offsets, alphas );
	}
}
