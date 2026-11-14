using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Hard-surface sculpt kit: Trim boolean / TrimDynamic / DamStandard brushes, a
/// searchable alpha library, pressure curves, stroke record/replay, and an LOD
/// display level picker for sculpting at high level.
///
/// What this file reuses, and does not duplicate:
///   - Stroke modes + lazy-mouse stabilisation: <see cref="StrokePath"/> (the
///     shared stroke engine core; <see cref="StrokeMode.Lazy"/> and
///     <see cref="StrokePath.Smooth"/> already exist). Kit strokes are expanded
///     through it; the Wave 2 stroke-engine ticket owns the core.
///   - Boolean backend: <see cref="MeshBoolean"/> (Manifold when installed,
///     else the built-in exact boolean). TrimBoolean is a cutter + a call
///     into it, never a second CSG implementation.
///   - Alphas: <see cref="BrushAlpha"/> procedural stamps. The library below
///     only names, tags, and searches them.
///   - Multires levels: <see cref="MultiresSculpt"/> (ViewLevel / Cost).
///
/// What this file deliberately does NOT do:
///   - IMM / VDM insert brushes: owned by godlike-imm-nanomesh-vdm-insert-repeat
///     (in progress). There is no insert API to wire to yet; wiring lands with it.
///   - Sculpt layers / polygroups: owned by godlike-sculpt-layers-morph-targets-
///     polygroups (in progress, owns SculptSession.cs — this file does not touch it).
///   - Voxel remesh / decimate: owned by the OpenVDB + decimation tickets. The
///     kit's acceptance is that Trim produces a manifold mesh a remesh can take.
///   - Session wiring: the kit works on <see cref="PolyMesh"/> directly. Wiring
///     the brushes into SculptSession.BrushKind is the Wave 2 engine's job, since
///     SculptSession.cs is owned by the sculpt-layers ticket.
///
/// The body every op here produces is a mesh solid, not a B-rep solid.
/// </summary>
public static class HardSurfaceKit
{
	// ------------------------------------------------------------------ pressure

	/// <summary>
	/// Piecewise-linear pressure curve: stylus pressure 0..1 maps to a brush
	/// multiplier. Points must be at least two, with strictly increasing inputs
	/// in 0..1. Evaluate clamps outside 0..1 to the end values.
	/// </summary>
	public sealed class PressureCurve
	{
		public (float Input, float Output)[] Points;

		public PressureCurve( params (float Input, float Output)[] points )
		{
			if ( points is null || points.Length < 2 )
				throw new ArgumentException( "A pressure curve needs at least two points." );
			for ( var i = 0; i < points.Length; i++ )
			{
				if ( points[i].Input < 0f || points[i].Input > 1f )
					throw new ArgumentException( "Pressure curve inputs must be in 0..1." );
				if ( i > 0 && points[i].Input <= points[i - 1].Input )
					throw new ArgumentException( "Pressure curve inputs must strictly increase." );
			}
			Points = points.ToArray();
		}

		public static PressureCurve Linear => new( (0f, 0f), (1f, 1f) );
		public static PressureCurve Soft => new( (0f, 0f), (0.5f, 0.35f), (1f, 1f) );
		public static PressureCurve Hard => new( (0f, 0f), (0.5f, 0.65f), (1f, 1f) );

		public float Evaluate( float pressure )
		{
			pressure = Math.Clamp( pressure, 0f, 1f );
			if ( pressure <= Points[0].Input ) return Points[0].Output;
			for ( var i = 1; i < Points.Length; i++ )
			{
				if ( pressure > Points[i].Input ) continue;
				var (x0, y0) = Points[i - 1];
				var (x1, y1) = Points[i];
				var t = (pressure - x0) / (x1 - x0);
				return y0 + t * (y1 - y0);
			}
			return Points[^1].Output;
		}
	}

	// ------------------------------------------------------------------ alphas

	/// <summary>One searchable alpha: a procedural stamp plus what it is for.</summary>
	public sealed class AlphaEntry
	{
		public string Name;
		public string[] Tags;
		public BrushAlpha Alpha;
	}

	/// <summary>
	/// Searchable library over the built-in procedural alphas
	/// (<see cref="BrushAlpha.ProceduralNames"/>). No PNGs, no disk: every entry
	/// is deterministic per name, so the same search on another machine finds the
	/// same stamp. Search matches a case-insensitive substring of the name or any
	/// tag; an empty query returns everything, name-sorted.
	/// </summary>
	public sealed class AlphaLibrary
	{
		static readonly Dictionary<string, string[]> TagsByName = new( StringComparer.Ordinal )
		{
			["rivets"] = new[] { "hard-surface", "metal", "detail", "fastener" },
			["cracks"] = new[] { "hard-surface", "damage", "detail" },
			["stitches"] = new[] { "hard-surface", "detail", "seam", "fabric" },
			["scales"] = new[] { "creature", "detail", "reptile" },
			["gill_slit"] = new[] { "creature", "detail" },
			["veins"] = new[] { "creature", "detail", "organic" },
			["pores"] = new[] { "surface", "skin", "detail" },
			["wrinkles"] = new[] { "surface", "skin", "detail" },
			["smoke"] = new[] { "surface", "soft", "noise" },
			["spark"] = new[] { "surface", "detail", "noise" },
			["ember"] = new[] { "surface", "detail", "noise" },
			["droplet"] = new[] { "surface", "detail" },
			["ring"] = new[] { "surface", "shape", "detail" },
			["streak"] = new[] { "surface", "shape", "detail" },
			["softdisc"] = new[] { "falloff", "soft", "shape" },
		};

		public List<AlphaEntry> Entries { get; } = new();

		public AlphaLibrary()
		{
			foreach ( var name in BrushAlpha.ProceduralNames )
			{
				var alpha = BrushAlpha.FromProcedural( name );
				if ( alpha is null ) continue;
				Entries.Add( new AlphaEntry
				{
					Name = name,
					Tags = TagsByName.TryGetValue( name, out var tags ) ? tags : new[] { "surface" },
					Alpha = alpha,
				} );
			}
			Entries.Sort( ( a, b ) => string.CompareOrdinal( a.Name, b.Name ) );
		}

		public AlphaEntry Get( string name )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) return null;
			return Entries.FirstOrDefault( e => string.Equals( e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase ) );
		}

		public List<AlphaEntry> Search( string query )
		{
			if ( string.IsNullOrWhiteSpace( query ) ) return Entries.ToList();
			var q = query.Trim().ToLowerInvariant();
			return Entries
				.Where( e => e.Name.ToLowerInvariant().Contains( q, StringComparison.Ordinal )
					|| e.Tags.Any( t => t.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ) )
				.ToList();
		}
	}

	// ------------------------------------------------------------------ DamStandard

	/// <summary>
	/// DamStandard: cuts a sharp crease along a stroke path. Vertices near the
	/// path are pulled toward the path line in the surface plane (pinch) and
	/// pushed down along their normal (depth), both with a squared falloff, so the
	/// centre of the stroke is the deepest and sharpest. Operates on the mesh in
	/// place; the path is in mesh space.
	/// </summary>
	public static void DamStandard( PolyMesh mesh, IReadOnlyList<Vec3> path, float radius, float depth, float pinch )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( path is null || path.Count == 0 || radius <= 0f ) return;
		depth = Math.Clamp( depth, -1e6f, 1e6f );
		pinch = Math.Clamp( pinch, 0f, 1f );

		var normals = VertexNormals( mesh );
		for ( var vi = 0; vi < mesh.Positions.Count; vi++ )
		{
			var v = mesh.Positions[vi];
			var n = normals[vi];
			if ( n.LengthSquared < 1e-12f ) continue;
			if ( !ClosestOnPath( path, v, out var closest, out var dist ) ) continue;
			if ( dist >= radius ) continue;
			var w = 1f - dist / radius;
			w *= w;
			var toPath = closest - v;
			var inPlane = toPath - n * Vec3.Dot( toPath, n );
			mesh.Positions[vi] = v + inPlane * (w * pinch) - n * (w * depth);
		}
		mesh.InvalidateVertexCache();
	}

	// ------------------------------------------------------------------ TrimDynamic

	/// <summary>
	/// TrimDynamic: flattens the surface onto the plane through the stroke path
	/// (centroid, <paramref name="planeNormal"/>). Vertices near the path are
	/// pulled onto the plane with a squared falloff; strength 1 flattens fully,
	/// 0 changes nothing. Bumps beside the stroke are untouched, so this planes
	/// a hard-surface face without touching the rest of the sculpt.
	/// </summary>
	public static void TrimDynamic( PolyMesh mesh, IReadOnlyList<Vec3> path, Vec3 planeNormal, float radius, float strength )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( path is null || path.Count == 0 || radius <= 0f ) return;
		if ( planeNormal.LengthSquared < 1e-12f )
			throw new ArgumentException( "TrimDynamic needs a non-zero plane normal." );
		var n = planeNormal.Normal;
		strength = Math.Clamp( strength, 0f, 1f );
		if ( strength <= 0f ) return;

		var centre = Vec3.Zero;
		foreach ( var p in path ) centre += p;
		centre *= 1f / path.Count;

		for ( var vi = 0; vi < mesh.Positions.Count; vi++ )
		{
			var v = mesh.Positions[vi];
			if ( !ClosestOnPath( path, v, out _, out var dist ) ) continue;
			if ( dist >= radius ) continue;
			var w = 1f - dist / radius;
			w *= w;
			var offPlane = Vec3.Dot( v - centre, n );
			mesh.Positions[vi] = v - n * (offPlane * w * strength);
		}
		mesh.InvalidateVertexCache();
	}

	// ------------------------------------------------------------------ Trim boolean

	/// <summary>Outcome of a Trim boolean cut. <see cref="Mesh"/> is null when the cut was refused.</summary>
	public sealed class TrimResult
	{
		public PolyMesh Mesh;
		/// <summary>Which boolean ran, e.g. "manifold" or "built-in (exact)".</summary>
		public string Route;
		/// <summary>Why the cut did not happen, or why the result is not a clean solid. Null on a clean cut.</summary>
		public string Error;
		public bool Closed;
	}

	/// <summary>
	/// Trim boolean: cuts the mesh with a plane, keeping the side
	/// <paramref name="keepAbove"/> names (above = Dot(v - planePoint, normal) &gt; 0).
	/// The cut is a real boolean, not a clip: a cutter box covering the unwanted
	/// half-space is subtracted through <see cref="MeshBoolean"/>, so the result
	/// stays a closed solid and the route (Manifold vs built-in exact) is reported
	/// honestly. A plane that misses the mesh leaves it unchanged; a cut that
	/// would remove the whole mesh is refused rather than returning an empty solid.
	/// </summary>
	public static TrimResult TrimBoolean( PolyMesh mesh, Vec3 planePoint, Vec3 planeNormal, bool keepAbove )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( planeNormal.LengthSquared < 1e-12f )
			throw new ArgumentException( "Trim boolean needs a non-zero plane normal." );
		var n = planeNormal.Normal;

		if ( mesh.Positions.Count == 0 )
			return new TrimResult { Error = "Trim boolean needs a mesh with vertices." };

		var min = float.MaxValue;
		var max = float.MinValue;
		foreach ( var v in mesh.Positions )
		{
			var d = Vec3.Dot( v - planePoint, n );
			min = Math.Min( min, d );
			max = Math.Max( max, d );
		}
		if ( keepAbove && min >= 0f || !keepAbove && max <= 0f )
			return new TrimResult { Mesh = mesh.Clone(), Route = "unchanged (plane misses mesh)", Closed = MeshValidator.Validate( mesh ).IsClosed };
		if ( keepAbove && max <= 0f || !keepAbove && min >= 0f )
			return new TrimResult { Error = "Trim boolean would remove the whole mesh." };

		var cutter = CutterBox( mesh, planePoint, n, keepAbove );
		var routeBefore = ManifoldBoolean.LastRoute;
		PolyMesh result;
		try
		{
			result = MeshBoolean.Apply( BooleanOp.Subtract, mesh, cutter );
		}
		catch ( Exception ex )
		{
			return new TrimResult { Error = $"Trim boolean failed: {ex.Message}" };
		}
		var route = ManifoldBoolean.LastRoute != routeBefore && ManifoldBoolean.LastRoute.Length > 0
			? ManifoldBoolean.LastRoute
			: "built-in (exact)";
		var closed = MeshValidator.Validate( result ).IsClosed;
		return new TrimResult
		{
			Mesh = result,
			Route = route,
			Closed = closed,
			Error = closed ? null : "Trim boolean result is not a closed solid.",
		};
	}

	/// <summary>
	/// A closed box covering the unwanted half-space: centred a half-diagonal
	/// past the plane on the cut side, big enough to swallow the whole mesh.
	/// Winding is self-checked: if the signed volume comes out negative the
	/// faces are flipped, so the cutter is always a positive solid.
	/// </summary>
	static PolyMesh CutterBox( PolyMesh mesh, Vec3 planePoint, Vec3 n, bool keepAbove )
	{
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var v in mesh.Positions )
		{
			lo = new Vec3( Math.Min( lo.x, v.x ), Math.Min( lo.y, v.y ), Math.Min( lo.z, v.z ) );
			hi = new Vec3( Math.Max( hi.x, v.x ), Math.Max( hi.y, v.y ), Math.Max( hi.z, v.z ) );
		}
		var centre = (lo + hi) * 0.5f;
		var halfDiag = (hi - lo).Length * 0.5f + 1f;

		// Orthonormal basis around the plane normal.
		var basis = Math.Abs( n.x ) < 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 1, 0 );
		var u = Vec3.Cross( n, basis ).Normal;
		var w = Vec3.Cross( n, u );
		var side = keepAbove ? -1f : 1f;
		// Centre projected onto the plane, then halfDiag to the unwanted side,
		// plus a hair past the plane into the kept side: the cutter's kept-side
		// face then sits 1e-4 past the plane instead of exactly on it, so the
		// boolean never meets a coplanar interface there. The kept side loses
		// a 1e-4 sliver for it — far inside any tolerance that matters.
		var c = centre - n * Vec3.Dot( centre - planePoint, n ) + n * (side * (halfDiag + 1e-4f));

		var m = new PolyMesh();
		for ( var i = 0; i < 8; i++ )
		{
			var su = (i & 1) == 0 ? -1f : 1f;
			var sw = (i & 2) == 0 ? -1f : 1f;
			var sn = (i & 4) == 0 ? -1f : 1f;
			m.AddVertex( c + u * (su * halfDiag) + w * (sw * halfDiag) + n * (sn * halfDiag) );
		}
		// Corner order: 0=(-,-,-) 1=(+,-,-) 2=(-,+,-) 3=(+,+,-) 4=(-,-,+) 5=(+,-,+) 6=(-,+,+) 7=(+,+,+)
		// in (u, w, n). Faces wound so Newell normals point out.
		m.AddFace( new[] { 0, 1, 3, 2 } ); // -n
		m.AddFace( new[] { 4, 6, 7, 5 } ); // +n
		m.AddFace( new[] { 0, 4, 5, 1 } ); // -w
		m.AddFace( new[] { 2, 3, 7, 6 } ); // +w
		m.AddFace( new[] { 0, 2, 6, 4 } ); // -u
		m.AddFace( new[] { 1, 5, 7, 3 } ); // +u
		if ( m.SignedVolume() < 0f )
		{
			foreach ( var f in m.Faces ) Array.Reverse( f.Indices );
			m.InvalidateVertexCache();
		}
		return m;
	}

	// ------------------------------------------------------------------ record / replay

	public enum KitStrokeKind { DamStandard, TrimDynamic, TrimBoolean }

	/// <summary>
	/// A recorded kit stroke: everything needed to replay it on a fresh mesh and
	/// get the same result. Replaying is how a trim/dam pass survives undo/redo
	/// and how a scripted hard-surface pass is re-applied after a remesh.
	/// <see cref="Replay"/> returns the resulting mesh (the same instance for
	/// DamStandard / TrimDynamic, the boolean result for TrimBoolean).
	/// </summary>
	public sealed class StrokeRecord
	{
		public KitStrokeKind Kind;
		public List<Vec3> Path = new();
		public float Radius;
		public float Depth;
		public float Pinch;
		public Vec3 PlaneNormal;
		public float Strength;
		public bool KeepAbove;
		public Vec3 PlanePoint;

		public PolyMesh Replay( PolyMesh mesh )
		{
			switch ( Kind )
			{
				case KitStrokeKind.DamStandard:
					DamStandard( mesh, Path, Radius, Depth, Pinch );
					return mesh;
				case KitStrokeKind.TrimDynamic:
					TrimDynamic( mesh, Path, PlaneNormal, Radius, Strength );
					return mesh;
				case KitStrokeKind.TrimBoolean:
				{
					var cut = TrimBoolean( mesh, PlanePoint, PlaneNormal, KeepAbove );
					if ( cut.Mesh is null )
						throw new InvalidOperationException( cut.Error ?? "Trim boolean replay failed." );
					return cut.Mesh;
				}
				default:
					throw new InvalidOperationException( $"Unknown kit stroke kind {Kind}." );
			}
		}
	}

	// ------------------------------------------------------------------ LOD display

	/// <summary>
	/// Highest multires level whose face cost fits <paramref name="faceBudget"/>,
	/// or 0 when even the cage is over budget. Pure: it does not move ViewLevel.
	/// Use <see cref="ApplyDisplayLevel"/> to actually switch the display while
	/// sculpting, then restore ViewLevel to TopLevel when the stroke ends.
	/// </summary>
	public static int PickDisplayLevel( MultiresSculpt sculpt, int faceBudget )
	{
		if ( sculpt is null ) throw new ArgumentNullException( nameof( sculpt ) );
		for ( var level = sculpt.TopLevel; level >= 0; level-- )
			if ( sculpt.Cost( level ).Faces <= faceBudget )
				return level;
		return 0;
	}

	public static int ApplyDisplayLevel( MultiresSculpt sculpt, int faceBudget )
	{
		var level = PickDisplayLevel( sculpt, faceBudget );
		sculpt.ViewLevel = level;
		return level;
	}

	// ------------------------------------------------------------------ helpers

	/// <summary>Area-weighted vertex normals from the mesh itself (no dependency on the mesh-normal sync).</summary>
	static Vec3[] VertexNormals( PolyMesh mesh )
	{
		var normals = new Vec3[mesh.Positions.Count];
		foreach ( var f in mesh.Faces )
		{
			if ( f.Count < 3 ) continue;
			var a = mesh.Positions[f.Indices[0]];
			var b = mesh.Positions[f.Indices[1]];
			var c = mesh.Positions[f.Indices[2]];
			var n = Vec3.Cross( b - a, c - a );
			foreach ( var i in f.Indices )
				normals[i] += n;
		}
		for ( var i = 0; i < normals.Length; i++ )
			normals[i] = normals[i].LengthSquared > 1e-12f ? normals[i].Normal : Vec3.Zero;
		return normals;
	}

	/// <summary>Closest point on a polyline and its distance. False when the path is empty.</summary>
	static bool ClosestOnPath( IReadOnlyList<Vec3> path, Vec3 p, out Vec3 closest, out float dist )
	{
		closest = Vec3.Zero;
		dist = float.MaxValue;
		if ( path is null || path.Count == 0 ) return false;
		if ( path.Count == 1 )
		{
			closest = path[0];
			dist = (p - closest).Length;
			return true;
		}
		for ( var i = 0; i + 1 < path.Count; i++ )
		{
			var a = path[i];
			var ab = path[i + 1] - a;
			var lenSq = ab.LengthSquared;
			var t = lenSq < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / lenSq, 0f, 1f );
			var q = a + ab * t;
			var d = (p - q).Length;
			if ( d < dist )
			{
				dist = d;
				closest = q;
			}
		}
		return true;
	}
}
