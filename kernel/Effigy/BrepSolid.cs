using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Managed analytic B-rep solid core (GODLIKE WAVE 1 P0, godlike-occt-brep-core-step-export).
///
/// A true B-rep solid layer ALONGSIDE the mesh sculpt layer, never a replacement for it.
/// Faces are analytic surfaces (plane / cylinder / cone / sphere / torus) with exact
/// parameters, so a 50 mm bore IS 50 mm — it is not a 16-sided facet pretending to be
/// one (a 16-gon cuts 0.48 mm and fails H7 by ~19x; see <see cref="BrepTessellation.ChordalError"/>).
///
/// UNITS: millimetres throughout, Z-up like the rest of the kernel. The Desktop kernel
/// is inches elsewhere; convert at the boundary with <see cref="BrepSolid.MmPerInch"/>
/// (see <see cref="BrepSolid.Scaled"/>). Document tolerance is 0.01 mm.
///
/// HONEST LIMITS (read before relying on this):
/// - Volume/Area are exact closed-form values from the primitive parameters, set by the
///   factories. They are NOT re-integrated from the face set; a face-set divergence
///   integration (and an OCCT cross-check of it) is the follow-on. Validity is structural
///   (closed shell, shared edges) plus the exact measures.
/// - Boolean / fillet are NOT implemented on the managed core: they return a NAMED
///   recoverable error (<see cref="BrepBooleanResult.Error"/>) instead of a silent
///   non-watertight solid. Routing them to OCCT needs the C++ shim described in
///   packaging/OCCT.md; <see cref="OcctNative"/> is the loader for it.
/// - STEP import/export (<see cref="StepExchange"/>) covers the analytic subset this
///   file can build; anything else is reported, never silently faceted.
/// </summary>
public enum BrepSurfaceKind
{
	Plane,
	Cylinder,
	Cone,
	Sphere,
	Torus
}

public enum BrepEdgeKind
{
	Line,
	Circle
}

public enum BrepBooleanOp
{
	Union,
	Subtract,
	Intersect
}

/// <summary>One analytic face: its surface, its trimming loop(s) and its exact area.</summary>
public sealed class BrepFace
{
	public BrepSurfaceKind Kind;
	/// <summary>Plane: a point on it. Cylinder/cone: a point on the axis. Sphere/torus: the centre.</summary>
	public Vec3 Origin;
	/// <summary>Plane: the unit normal. Cylinder/cone/torus: the unit axis. Sphere: unused (0,0,1).</summary>
	public Vec3 Axis = new( 0, 0, 1 );
	/// <summary>Cylinder/cone-base/sphere radius, torus major radius. Plane: unused.</summary>
	public double Radius;
	/// <summary>Torus minor radius. Cone: half-angle in radians (Radius is the base radius).</summary>
	public double Radius2;
	/// <summary>True when the surface normal points INTO the solid (the bore of a tube).</summary>
	public bool Reversed;
	/// <summary>Parameter spans. Cylinder/cone: U = angle (rad), V = axial (mm). Sphere: U = longitude,
	/// V = polar angle from +axis. Torus: U = angle about the axis, V = angle about the tube. Plane: unused.</summary>
	public double U0, U1, V0, V1;
	/// <summary>Trimming loop, 3D points in order. Plane rectangle: 4 corners. Disc/annulus: sampled circle points.</summary>
	public List<Vec3> Loop = new();
	/// <summary>Inner trimming loop (annulus holes). Null when the face has no hole.</summary>
	public List<Vec3> InnerLoop;
	/// <summary>Exact analytic area in mm^2, set by the factory.</summary>
	public double AreaMm2;
	public string Name = "";
}

/// <summary>One topological edge: shared by FaceA and FaceB (both &gt;= 0 on a closed solid).</summary>
public sealed class BrepEdge
{
	public BrepEdgeKind Kind;
	public Vec3 Start, End;
	/// <summary>Circle: centre, unit normal and radius. Line: unused.</summary>
	public Vec3 Center;
	public Vec3 Axis = new( 0, 0, 1 );
	public double Radius;
	public double Angle0, Angle1;
	public int FaceA = -1, FaceB = -1;
	/// <summary>True for a seam edge: the same face on both sides (a closed periodic face's
	/// seam, e.g. a cylinder lateral's seam line). A seam is closed, not an open edge.</summary>
	public bool Seam;
}

/// <summary>Result of a boolean / fillet: either a solid, or a NAMED recoverable error. Never a silent bad solid.</summary>
public sealed class BrepBooleanResult
{
	public BrepSolid Solid;
	public string Error;
	public bool Ok => Error is null && Solid is not null;
}

/// <summary>A closed shell of analytic faces with exact measures (mm).</summary>
public sealed class BrepSolid
{
	/// <summary>Millimetres per inch: the conversion at the Desktop kernel boundary (the kernel is inches).</summary>
	public const double MmPerInch = 25.4;
	/// <summary>Document tolerance, mm: the ticket's acceptance tolerance.</summary>
	public const double DocumentToleranceMm = 0.01;
	/// <summary>Precision tessellation chordal deflection, mm.</summary>
	public const double PrecisionDeflectionMm = 0.005;

	public string Name = "solid";
	public List<BrepFace> Faces = new();
	public List<BrepEdge> Edges = new();
	/// <summary>Exact analytic volume in mm^3, set by the factory (see the file header).</summary>
	public double VolumeMm3;
	/// <summary>Exact analytic area in mm^2: the sum of the faces' exact areas.</summary>
	public double AreaMm2 => Faces.Sum( f => f is null ? 0 : f.AreaMm2 );

	/// <summary>True when every edge is shared by exactly two distinct faces (or there are no edges, as on a closed periodic sphere/torus face).</summary>
	public bool IsClosed
	{
		get
		{
			foreach ( var e in Edges )
				// Face indices must name faces that exist: a removed face shifts the list, so a
				// stale index (>= Faces.Count) is an open edge, not a closed one.
				if ( e is null || e.FaceA < 0 || e.FaceB < 0 || e.FaceA >= Faces.Count || e.FaceB >= Faces.Count || (e.FaceA == e.FaceB && !e.Seam) ) return false;
			return Faces.Count > 0;
		}
	}

	/// <summary>Named problems with this solid; empty means valid. Nothing here is silent.</summary>
	public List<string> Validate()
	{
		var problems = new List<string>();
		if ( Faces.Count == 0 ) problems.Add( "brep-empty: solid has no faces" );
		for ( int i = 0; i < Faces.Count; i++ )
		{
			var f = Faces[i];
			if ( f is null ) { problems.Add( $"brep-null-face: face {i} is null" ); continue; }
			if ( !float.IsFinite( f.Origin.x ) || !float.IsFinite( f.Origin.y ) || !float.IsFinite( f.Origin.z ) )
				problems.Add( $"brep-degenerate-face: face {i} ({f.Kind}) has a non-finite origin" );
			if ( f.Kind != BrepSurfaceKind.Plane && f.Kind != BrepSurfaceKind.Sphere && Math.Abs( f.Axis.Length - 1.0 ) > 1e-4 )
				problems.Add( $"brep-degenerate-face: face {i} ({f.Kind}) axis is not a unit vector" );
			if ( f.Kind != BrepSurfaceKind.Plane && f.Radius <= 0 )
				problems.Add( $"brep-degenerate-face: face {i} ({f.Kind}) radius is {f.Radius} mm, must be above zero" );
			if ( f.Kind == BrepSurfaceKind.Torus && f.Radius2 <= 0 )
				problems.Add( $"brep-degenerate-face: face {i} (Torus) minor radius is {f.Radius2} mm, must be above zero" );
			if ( f.Kind == BrepSurfaceKind.Cone && (f.Radius2 <= 0 || f.Radius2 >= Math.PI / 2) )
				problems.Add( $"brep-degenerate-face: face {i} (Cone) half-angle is {f.Radius2} rad, must be in (0, pi/2)" );
			if ( f.AreaMm2 <= 0 || !double.IsFinite( f.AreaMm2 ) )
				problems.Add( $"brep-degenerate-face: face {i} ({f.Kind}) area is {f.AreaMm2} mm^2" );
		}
		for ( int i = 0; i < Edges.Count; i++ )
		{
			var e = Edges[i];
			if ( e is null ) { problems.Add( $"brep-null-edge: edge {i} is null" ); continue; }
			if ( e.FaceA < 0 || e.FaceB < 0 || e.FaceA >= Faces.Count || e.FaceB >= Faces.Count )
				problems.Add( $"brep-open-edge: edge {i} ({e.Kind}) is used by only one face or references a missing face" );
			else if ( e.FaceA == e.FaceB && !e.Seam )
				problems.Add( $"brep-degenerate-edge: edge {i} ({e.Kind}) has the same face on both sides and is not a seam" );
			if ( e.Kind == BrepEdgeKind.Circle && e.Radius <= 0 )
				problems.Add( $"brep-degenerate-edge: edge {i} (Circle) radius is {e.Radius} mm" );
		}
		if ( !double.IsFinite( VolumeMm3 ) || VolumeMm3 <= 0 )
			problems.Add( $"brep-bad-volume: volume is {VolumeMm3} mm^3, must be finite and above zero" );
		return problems;
	}

	/// <summary>A copy with every length scaled by <paramref name="factor"/> (volumes by factor^3, areas by factor^2).</summary>
	public BrepSolid Scaled( double factor )
	{
		if ( !double.IsFinite( factor ) || factor <= 0 ) throw new ArgumentOutOfRangeException( nameof( factor ), "Scale factor must be finite and above zero." );
		var copy = new BrepSolid { Name = Name, VolumeMm3 = VolumeMm3 * factor * factor * factor };
		foreach ( var f in Faces )
		{
			var c = new BrepFace
			{
				Kind = f.Kind, Origin = f.Origin * (float)factor, Axis = f.Axis,
				Radius = f.Radius * factor, Radius2 = f.Kind == BrepSurfaceKind.Cone ? f.Radius2 : f.Radius2 * factor,
				Reversed = f.Reversed, U0 = f.U0, U1 = f.U1,
				// V is axial (mm) on cylinder/cone and angular (rad) on sphere/torus: only scale the axial one.
				V0 = (f.Kind == BrepSurfaceKind.Cylinder || f.Kind == BrepSurfaceKind.Cone) ? f.V0 * factor : f.V0,
				V1 = (f.Kind == BrepSurfaceKind.Cylinder || f.Kind == BrepSurfaceKind.Cone) ? f.V1 * factor : f.V1,
				AreaMm2 = f.AreaMm2 * factor * factor, Name = f.Name,
				Loop = f.Loop.Select( p => p * (float)factor ).ToList(),
				InnerLoop = f.InnerLoop?.Select( p => p * (float)factor ).ToList(),
			};
			copy.Faces.Add( c );
		}
		foreach ( var e in Edges )
		{
			copy.Edges.Add( new BrepEdge
			{
				Kind = e.Kind, Start = e.Start * (float)factor, End = e.End * (float)factor,
				Center = e.Center * (float)factor, Axis = e.Axis, Radius = e.Radius * factor,
				Angle0 = e.Angle0, Angle1 = e.Angle1, FaceA = e.FaceA, FaceB = e.FaceB, Seam = e.Seam,
			} );
		}
		return copy;
	}

	/// <summary>Boolean with another solid. NOT implemented on the managed core: returns the named
	/// recoverable error, never a silent non-watertight solid. Routing it to OCCT needs the C++ shim
	/// in packaging/OCCT.md.</summary>
	public BrepBooleanResult Boolean( BrepBooleanOp op, BrepSolid other )
	{
		ArgumentNullException.ThrowIfNull( other );
		return new BrepBooleanResult { Error = $"occt-boolean-unavailable: {op} needs the OCCT native shim (packaging/OCCT.md); the managed analytic core has no boolean engine" };
	}

	/// <summary>Fillet of every convex edge. NOT implemented on the managed core: named error, as <see cref="Boolean"/>.</summary>
	public BrepBooleanResult Fillet( double radiusMm )
	{
		if ( !double.IsFinite( radiusMm ) || radiusMm <= 0 ) throw new ArgumentOutOfRangeException( nameof( radiusMm ), "Fillet radius must be finite and above zero." );
		return new BrepBooleanResult { Error = "occt-fillet-unavailable: fillet needs the OCCT native shim (packaging/OCCT.md); the managed analytic core has no fillet engine" };
	}

	// --- factories: exact analytic primitives. Origin is the primitive centre/axis point; +Z is up. ---

	static Vec3 V( double x, double y, double z ) => new( (float)x, (float)y, (float)z );

	static List<Vec3> CirclePoints( Vec3 centre, Vec3 axis, double radius, int count )
	{
		Basis( axis, out var e1, out var e2 );
		var pts = new List<Vec3>( count );
		for ( int i = 0; i < count; i++ )
		{
			var a = 2.0 * Math.PI * i / count;
			pts.Add( centre + (e1 * (float)(Math.Cos( a ) * radius) + e2 * (float)(Math.Sin( a ) * radius)) );
		}
		return pts;
	}

	/// <summary>Orthonormal basis perpendicular to <paramref name="axis"/> (unit).</summary>
	public static void Basis( Vec3 axis, out Vec3 e1, out Vec3 e2 )
	{
		var a = Math.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		e1 = Vec3.Cross( axis, a ).Normal;
		e2 = Vec3.Cross( axis, e1 );
	}

	/// <summary>Returns <paramref name="loop"/> wound counter-clockwise as seen from <paramref name="outwardNormal"/>
	/// (Newell normal aligned with the normal); reverses it when it is not. Planar loops must face outward
	/// or the tessellation and the STEP bounds come out inside-out.</summary>
	static List<Vec3> OrientLoop( List<Vec3> loop, Vec3 outwardNormal )
	{
		if ( loop is null || loop.Count < 3 ) return loop;
		double nx = 0, ny = 0, nz = 0;
		for ( int i = 0; i < loop.Count; i++ )
		{
			var a = loop[i]; var b = loop[(i + 1) % loop.Count];
			nx += (a.y - b.y) * (a.z + b.z);
			ny += (a.z - b.z) * (a.x + b.x);
			nz += (a.x - b.x) * (a.y + b.y);
		}
		var dot = nx * outwardNormal.x + ny * outwardNormal.y + nz * outwardNormal.z;
		if ( dot < 0 ) loop.Reverse();
		return loop;
	}

	static BrepEdge CircleEdge( Vec3 centre, Vec3 axis, double radius, int faceA, int faceB )
	{
		Basis( axis, out var e1, out _ );
		var seam = centre + e1 * (float)radius;
		return new BrepEdge { Kind = BrepEdgeKind.Circle, Center = centre, Axis = axis, Radius = radius, Angle0 = 0, Angle1 = 2 * Math.PI, Start = seam, End = seam, FaceA = faceA, FaceB = faceB };
	}

	static BrepEdge LineEdge( Vec3 a, Vec3 b, int faceA, int faceB ) =>
		new() { Kind = BrepEdgeKind.Line, Start = a, End = b, FaceA = faceA, FaceB = faceB };

	/// <summary>Axis-aligned box centred on the origin: 6 planar faces, 12 edges, exact volume/area.</summary>
	public static BrepSolid Box( double sx, double sy, double sz, string name = "box" )
	{
		if ( sx <= 0 || sy <= 0 || sz <= 0 ) throw new ArgumentOutOfRangeException( "Box sides must be above zero." );
		var hx = sx / 2; var hy = sy / 2; var hz = sz / 2;
		var solid = new BrepSolid { Name = name, VolumeMm3 = sx * sy * sz };
		// Faces: +X, -X, +Y, -Y, +Z, -Z. Each loop is its 4 corners, outward normal.
		var corners = new[]
		{
			V( hx, hy, hz ), V( hx, -hy, hz ), V( hx, -hy, -hz ), V( hx, hy, -hz ),   // +X
			V( -hx, -hy, hz ), V( -hx, hy, hz ), V( -hx, hy, -hz ), V( -hx, -hy, -hz ), // -X
			V( -hx, hy, hz ), V( hx, hy, hz ), V( hx, hy, -hz ), V( -hx, hy, -hz ),   // +Y
			V( hx, -hy, hz ), V( -hx, -hy, hz ), V( -hx, -hy, -hz ), V( hx, -hy, -hz ), // -Y
			V( -hx, -hy, hz ), V( hx, -hy, hz ), V( hx, hy, hz ), V( -hx, hy, hz ),   // +Z
			V( hx, -hy, -hz ), V( -hx, -hy, -hz ), V( -hx, hy, -hz ), V( hx, hy, -hz ), // -Z
		};
		var normals = new[] { V( 1, 0, 0 ), V( -1, 0, 0 ), V( 0, 1, 0 ), V( 0, -1, 0 ), V( 0, 0, 1 ), V( 0, 0, -1 ) };
		var areas = new[] { sy * sz, sy * sz, sx * sz, sx * sz, sx * sy, sx * sy };
		for ( int f = 0; f < 6; f++ )
			solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Plane, Origin = corners[f * 4], Axis = normals[f], AreaMm2 = areas[f], Name = $"face{f}", Loop = OrientLoop( corners.Skip( f * 4 ).Take( 4 ).ToList(), normals[f] ) } );
		// 12 edges: each corner-to-corner segment, shared by its two faces.
		var v = new[]
		{
			V( hx, hy, hz ), V( hx, -hy, hz ), V( hx, -hy, -hz ), V( hx, hy, -hz ),
			V( -hx, -hy, hz ), V( -hx, hy, hz ), V( -hx, hy, -hz ), V( -hx, -hy, -hz ),
		};
		// (vertexA, vertexB, faceA, faceB) on the 8 unique corners.
		var edges = new (int, int, int, int)[]
		{
			// (vertexA, vertexB, faceA, faceB): each edge joins the two faces that
			// geometrically share it — v0-v1 is +X/+Z, v1-v2 +X/-Y, v2-v3 +X/-Z,
			// v3-v0 +X/+Y, v4-v5 -X/+Z, v7-v4 -X/-Y. (The first four rows used to
			// name the wrong second face, which the structural closed check cannot
			// see — only the STEP round-trip exposes it, via the geometric loop match.)
			(0, 1, 0, 4), (1, 2, 0, 3), (2, 3, 0, 5), (3, 0, 0, 2),
			(4, 5, 1, 4), (5, 6, 1, 2), (6, 7, 1, 5), (7, 4, 1, 3),
			(0, 5, 2, 4), (3, 6, 2, 5), (1, 4, 3, 4), (2, 7, 3, 5),
		};
		foreach ( var (a, b, fa, fb) in edges )
			solid.Edges.Add( LineEdge( v[a], v[b], fa, fb ) );
		return solid;
	}

	/// <summary>Solid cylinder, axis +Z, base at z = 0, top at z = height. Exact volume/area.</summary>
	public static BrepSolid Cylinder( double radius, double height, string name = "cylinder" )
	{
		if ( radius <= 0 || height <= 0 ) throw new ArgumentOutOfRangeException( "Cylinder radius and height must be above zero." );
		var axis = new Vec3( 0, 0, 1 );
		var solid = new BrepSolid { Name = name, VolumeMm3 = Math.PI * radius * radius * height };
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Cylinder, Origin = V( 0, 0, 0 ), Axis = axis, Radius = radius, U0 = 0, U1 = 2 * Math.PI, V0 = 0, V1 = height, AreaMm2 = 2 * Math.PI * radius * height, Name = "lateral" } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Plane, Origin = V( 0, 0, height ), Axis = axis, AreaMm2 = Math.PI * radius * radius, Name = "top", Loop = OrientLoop( CirclePoints( V( 0, 0, height ), axis, radius, 32 ), axis ) } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Plane, Origin = V( 0, 0, 0 ), Axis = new Vec3( 0, 0, -1 ), AreaMm2 = Math.PI * radius * radius, Name = "bottom", Loop = OrientLoop( CirclePoints( V( 0, 0, 0 ), axis, radius, 32 ), new Vec3( 0, 0, -1 ) ) } );
		solid.Edges.Add( CircleEdge( V( 0, 0, height ), axis, radius, 0, 1 ) );
		solid.Edges.Add( CircleEdge( V( 0, 0, 0 ), axis, radius, 0, 2 ) );
		// Seam line of the closed lateral face: same face on both sides, at the circles' seam point.
		solid.Edges.Add( new BrepEdge { Kind = BrepEdgeKind.Line, Start = solid.Edges[1].Start, End = solid.Edges[0].Start, FaceA = 0, FaceB = 0, Seam = true } );
		return solid;
	}

	/// <summary>Hollow cylinder (a tube): outer radius, bore radius, height. The bore is the H7 test
	/// shape — its diameter is exact, never faceted. Exact volume/area.</summary>
	public static BrepSolid Tube( double outerRadius, double boreRadius, double height, string name = "tube" )
	{
		if ( outerRadius <= 0 || boreRadius <= 0 || height <= 0 ) throw new ArgumentOutOfRangeException( "Tube radii and height must be above zero." );
		if ( boreRadius >= outerRadius ) throw new ArgumentOutOfRangeException( nameof( boreRadius ), "Bore radius must be smaller than the outer radius." );
		var axis = new Vec3( 0, 0, 1 );
		var solid = new BrepSolid { Name = name, VolumeMm3 = Math.PI * (outerRadius * outerRadius - boreRadius * boreRadius) * height };
		var annulus = Math.PI * (outerRadius * outerRadius - boreRadius * boreRadius);
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Cylinder, Origin = V( 0, 0, 0 ), Axis = axis, Radius = outerRadius, U0 = 0, U1 = 2 * Math.PI, V0 = 0, V1 = height, AreaMm2 = 2 * Math.PI * outerRadius * height, Name = "outer" } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Cylinder, Origin = V( 0, 0, 0 ), Axis = axis, Radius = boreRadius, Reversed = true, U0 = 0, U1 = 2 * Math.PI, V0 = 0, V1 = height, AreaMm2 = 2 * Math.PI * boreRadius * height, Name = "bore" } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Plane, Origin = V( 0, 0, height ), Axis = axis, AreaMm2 = annulus, Name = "top", Loop = OrientLoop( CirclePoints( V( 0, 0, height ), axis, outerRadius, 32 ), axis ), InnerLoop = OrientLoop( CirclePoints( V( 0, 0, height ), axis, boreRadius, 32 ), axis ) } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Plane, Origin = V( 0, 0, 0 ), Axis = new Vec3( 0, 0, -1 ), AreaMm2 = annulus, Name = "bottom", Loop = OrientLoop( CirclePoints( V( 0, 0, 0 ), axis, outerRadius, 32 ), new Vec3( 0, 0, -1 ) ), InnerLoop = OrientLoop( CirclePoints( V( 0, 0, 0 ), axis, boreRadius, 32 ), new Vec3( 0, 0, -1 ) ) } );
		solid.Edges.Add( CircleEdge( V( 0, 0, height ), axis, outerRadius, 0, 2 ) );
		solid.Edges.Add( CircleEdge( V( 0, 0, 0 ), axis, outerRadius, 0, 3 ) );
		solid.Edges.Add( CircleEdge( V( 0, 0, height ), axis, boreRadius, 1, 2 ) );
		solid.Edges.Add( CircleEdge( V( 0, 0, 0 ), axis, boreRadius, 1, 3 ) );
		// Seam lines of the two closed lateral faces (same face on both sides, at the rims'
		// seam point). Without a seam a periodic face has no STEP edge loop: the writer
		// cannot bound it and the reader cannot link its rims, so the round-trip comes
		// back open. Same pattern as Cylinder's seam above.
		solid.Edges.Add( new BrepEdge { Kind = BrepEdgeKind.Line, Start = solid.Edges[1].Start, End = solid.Edges[0].Start, FaceA = 0, FaceB = 0, Seam = true } );
		solid.Edges.Add( new BrepEdge { Kind = BrepEdgeKind.Line, Start = solid.Edges[3].Start, End = solid.Edges[2].Start, FaceA = 1, FaceB = 1, Seam = true } );
		return solid;
	}

	/// <summary>Solid cone, apex at z = height, base disc at z = 0 with <paramref name="baseRadius"/>.</summary>
	public static BrepSolid Cone( double baseRadius, double height, string name = "cone" )
	{
		if ( baseRadius <= 0 || height <= 0 ) throw new ArgumentOutOfRangeException( "Cone radius and height must be above zero." );
		var axis = new Vec3( 0, 0, 1 );
		var halfAngle = Math.Atan2( baseRadius, height );
		var slant = Math.Sqrt( baseRadius * baseRadius + height * height );
		var solid = new BrepSolid { Name = name, VolumeMm3 = Math.PI * baseRadius * baseRadius * height / 3.0 };
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Cone, Origin = V( 0, 0, height ), Axis = new Vec3( 0, 0, -1 ), Radius = baseRadius, Radius2 = halfAngle, U0 = 0, U1 = 2 * Math.PI, V0 = 0, V1 = height, AreaMm2 = Math.PI * baseRadius * slant, Name = "lateral" } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Plane, Origin = V( 0, 0, 0 ), Axis = new Vec3( 0, 0, -1 ), AreaMm2 = Math.PI * baseRadius * baseRadius, Name = "base", Loop = OrientLoop( CirclePoints( V( 0, 0, 0 ), axis, baseRadius, 32 ), new Vec3( 0, 0, -1 ) ) } );
		solid.Edges.Add( CircleEdge( V( 0, 0, 0 ), axis, baseRadius, 0, 1 ) );
		solid.Edges.Add( new BrepEdge { Kind = BrepEdgeKind.Line, Start = solid.Edges[0].Start, End = V( 0, 0, height ), FaceA = 0, FaceB = 0, Seam = true } );
		return solid;
	}

	/// <summary>Solid sphere centred on the origin. Two hemispherical faces sharing the equator
	/// circle (a full sphere has no boundary; one face with no bounds is not valid STEP).</summary>
	public static BrepSolid Sphere( double radius, string name = "sphere" )
	{
		if ( radius <= 0 ) throw new ArgumentOutOfRangeException( nameof( radius ), "Sphere radius must be above zero." );
		var solid = new BrepSolid { Name = name, VolumeMm3 = 4.0 / 3.0 * Math.PI * radius * radius * radius };
		var axis = new Vec3( 0, 0, 1 );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Sphere, Origin = V( 0, 0, 0 ), Axis = axis, Radius = radius, U0 = 0, U1 = 2 * Math.PI, V0 = 0, V1 = Math.PI / 2, AreaMm2 = 2 * Math.PI * radius * radius, Name = "north" } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Sphere, Origin = V( 0, 0, 0 ), Axis = axis, Radius = radius, U0 = 0, U1 = 2 * Math.PI, V0 = Math.PI / 2, V1 = Math.PI, AreaMm2 = 2 * Math.PI * radius * radius, Name = "south" } );
		solid.Edges.Add( CircleEdge( V( 0, 0, 0 ), axis, radius, 0, 1 ) );
		return solid;
	}

	/// <summary>Solid torus centred on the origin, axis +Z. Two half faces sharing the two tube
	/// circles at U = 0 and U = pi (a full torus has no boundary; one face with no bounds is not valid STEP).</summary>
	public static BrepSolid Torus( double majorRadius, double minorRadius, string name = "torus" )
	{
		if ( majorRadius <= 0 || minorRadius <= 0 ) throw new ArgumentOutOfRangeException( "Torus radii must be above zero." );
		if ( minorRadius >= majorRadius ) throw new ArgumentOutOfRangeException( nameof( minorRadius ), "Torus minor radius must be smaller than the major radius (no self-intersection)." );
		var solid = new BrepSolid { Name = name, VolumeMm3 = 2 * Math.PI * Math.PI * majorRadius * minorRadius * minorRadius };
		var axis = new Vec3( 0, 0, 1 );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Torus, Origin = V( 0, 0, 0 ), Axis = axis, Radius = majorRadius, Radius2 = minorRadius, U0 = 0, U1 = Math.PI, V0 = 0, V1 = 2 * Math.PI, AreaMm2 = 2 * Math.PI * Math.PI * majorRadius * minorRadius, Name = "half-a" } );
		solid.Faces.Add( new BrepFace { Kind = BrepSurfaceKind.Torus, Origin = V( 0, 0, 0 ), Axis = axis, Radius = majorRadius, Radius2 = minorRadius, U0 = Math.PI, U1 = 2 * Math.PI, V0 = 0, V1 = 2 * Math.PI, AreaMm2 = 2 * Math.PI * Math.PI * majorRadius * minorRadius, Name = "half-b" } );
		BrepSolid.Basis( axis, out var e1, out var e2 );
		foreach ( var u in new[] { 0.0, Math.PI } )
		{
			var radial = e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u );
			var tangent = e1 * (float)-Math.Sin( u ) + e2 * (float)Math.Cos( u );
			var centre = V( 0, 0, 0 ) + radial * (float)majorRadius;
			var seam = centre + tangent * (float)minorRadius;
			solid.Edges.Add( new BrepEdge { Kind = BrepEdgeKind.Circle, Center = centre, Axis = tangent, Radius = minorRadius, Angle0 = 0, Angle1 = 2 * Math.PI, Start = seam, End = seam, FaceA = 0, FaceB = 1 } );
		}
		return solid;
	}
}

/// <summary>Exact measurement on analytic faces, in mm / degrees.</summary>
public static class BrepMeasure
{
	/// <summary>Surface normal at parameters (u, v) on <paramref name="face"/> (outward; negated when the face is Reversed).</summary>
	public static Vec3 NormalAt( BrepFace face, double u, double v )
	{
		ArgumentNullException.ThrowIfNull( face );
		Vec3 n;
		switch ( face.Kind )
		{
			case BrepSurfaceKind.Plane:
				n = face.Axis;
				break;
			case BrepSurfaceKind.Cylinder:
			{
				BrepSolid.Basis( face.Axis, out var e1, out var e2 );
				n = e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u );
				break;
			}
			case BrepSurfaceKind.Cone:
			{
				BrepSolid.Basis( face.Axis, out var e1, out var e2 );
				var radial = e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u );
				// Outward normal tilts from radial toward the base by the half-angle.
				n = radial * (float)Math.Cos( face.Radius2 ) - face.Axis * (float)Math.Sin( face.Radius2 );
				break;
			}
			case BrepSurfaceKind.Sphere:
			{
				BrepSolid.Basis( face.Axis, out var e1, out var e2 );
				n = e1 * (float)(Math.Sin( v ) * Math.Cos( u )) + e2 * (float)(Math.Sin( v ) * Math.Sin( u )) + face.Axis * (float)Math.Cos( v );
				break;
			}
			default: // Torus
			{
				BrepSolid.Basis( face.Axis, out var e1, out var e2 );
				var around = e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u );
				n = around * (float)Math.Cos( v ) + face.Axis * (float)Math.Sin( v );
				break;
			}
		}
		return face.Reversed ? -n : n;
	}

	/// <summary>Point on <paramref name="face"/> at parameters (u, v), in mm.</summary>
	public static Vec3 PointAt( BrepFace face, double u, double v )
	{
		ArgumentNullException.ThrowIfNull( face );
		BrepSolid.Basis( face.Axis, out var e1, out var e2 );
		switch ( face.Kind )
		{
			case BrepSurfaceKind.Plane:
				return face.Origin + e1 * (float)u + e2 * (float)v;
			case BrepSurfaceKind.Cylinder:
				return face.Origin + face.Axis * (float)v + (e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u )) * (float)face.Radius;
			case BrepSurfaceKind.Cone:
			{
				var r = v * Math.Tan( face.Radius2 );
				return face.Origin + face.Axis * (float)v + (e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u )) * (float)r;
			}
			case BrepSurfaceKind.Sphere:
			{
				var dir = e1 * (float)(Math.Sin( v ) * Math.Cos( u )) + e2 * (float)(Math.Sin( v ) * Math.Sin( u )) + face.Axis * (float)Math.Cos( v );
				return face.Origin + dir * (float)face.Radius;
			}
			default: // Torus
			{
				var around = e1 * (float)Math.Cos( u ) + e2 * (float)Math.Sin( u );
				return face.Origin + around * (float)(face.Radius + face.Radius2 * Math.Cos( v )) + face.Axis * (float)(face.Radius2 * Math.Sin( v ));
			}
		}
	}

	/// <summary>Shortest distance from <paramref name="p"/> to <paramref name="face"/>'s surface, in mm.
	/// Infinite-surface distance: the trimming spans are not applied, so a point past the rim measures to the
	/// extended surface. Callers that need the trimmed distance clamp to the face bounds first.</summary>
	public static double DistancePointToFace( Vec3 p, BrepFace face )
	{
		ArgumentNullException.ThrowIfNull( face );
		var d = p - face.Origin;
		switch ( face.Kind )
		{
			case BrepSurfaceKind.Plane:
				return Math.Abs( Vec3.Dot( d, face.Axis ) );
			case BrepSurfaceKind.Cylinder:
			{
				var axial = Vec3.Dot( d, face.Axis );
				var radial = (d - face.Axis * axial).Length;
				return Math.Abs( radial - face.Radius );
			}
			case BrepSurfaceKind.Cone:
			{
				var axial = Vec3.Dot( d, face.Axis );
				var radial = (d - face.Axis * axial).Length;
				// Perpendicular distance to the infinite cone, from the apex along the axis.
				return Math.Abs( radial * Math.Cos( face.Radius2 ) - axial * Math.Sin( face.Radius2 ) );
			}
			case BrepSurfaceKind.Sphere:
				return Math.Abs( d.Length - face.Radius );
			default: // Torus
			{
				var axial = Vec3.Dot( d, face.Axis );
				var radial = (d - face.Axis * axial).Length;
				var tube = Math.Sqrt( (radial - face.Radius) * (radial - face.Radius) + (double)axial * axial );
				return Math.Abs( tube - face.Radius2 );
			}
		}
	}

	/// <summary>Angle between two faces' outward normals at their parameter centres, in degrees.</summary>
	public static double AngleBetweenFaces( BrepFace a, BrepFace b )
	{
		ArgumentNullException.ThrowIfNull( a );
		ArgumentNullException.ThrowIfNull( b );
		var na = NormalAt( a, (a.U0 + a.U1) / 2, (a.V0 + a.V1) / 2 );
		var nb = NormalAt( b, (b.U0 + b.U1) / 2, (b.V0 + b.V1) / 2 );
		return Math.Acos( Math.Clamp( Vec3.Dot( na, nb ), -1.0, 1.0 ) ) * 180.0 / Math.PI;
	}
}

/// <summary>Tessellation with explicit deflection control. Display/export ONLY: the solid stays analytic.
/// Chordal error for a circle of radius R with n sides is R(1-cos(pi/n)); a 16-sided 25 mm bore cuts
/// 0.48 mm (fails H7 by ~19x) while 96 sides cut 0.013 mm.</summary>
public static class BrepTessellation
{
	/// <summary>Chordal (sagitta) error in mm of approximating a radius-<paramref name="radius"/> circle with <paramref name="sides"/> straight segments.</summary>
	public static double ChordalError( double radius, int sides )
	{
		if ( radius <= 0 ) throw new ArgumentOutOfRangeException( nameof( radius ), "Radius must be above zero." );
		if ( sides < 3 ) throw new ArgumentOutOfRangeException( nameof( sides ), "A circle needs at least 3 sides." );
		return radius * (1.0 - Math.Cos( Math.PI / sides ));
	}

	/// <summary>Smallest side count whose chordal error at <paramref name="radius"/> is at most <paramref name="deflectionMm"/>.</summary>
	public static int SidesForDeflection( double radius, double deflectionMm )
	{
		if ( radius <= 0 ) throw new ArgumentOutOfRangeException( nameof( radius ), "Radius must be above zero." );
		if ( deflectionMm <= 0 ) throw new ArgumentOutOfRangeException( nameof( deflectionMm ), "Deflection must be above zero." );
		if ( deflectionMm >= radius ) return 3;
		var n = (int)Math.Ceiling( Math.PI / Math.Acos( 1.0 - deflectionMm / radius ) );
		return Math.Max( 3, n );
	}

	public sealed class Result
	{
		public List<Vec3> Vertices = new();
		/// <summary>Index triples into <see cref="Vertices"/>.</summary>
		public List<int> Triangles = new();
		public int TriangleCount => Triangles.Count / 3;
	}

	/// <summary>Tessellate <paramref name="solid"/> for display/export. Chordal deflection bounds the
	/// sagitta on every curved face; angular deflection bounds the angle per segment (default 15 deg).
	/// The solid itself is unchanged and stays analytic.</summary>
	public static Result Tessellate( BrepSolid solid, double chordalDeflectionMm, double angularDeflectionDeg = 15.0 )
	{
		ArgumentNullException.ThrowIfNull( solid );
		if ( chordalDeflectionMm <= 0 ) throw new ArgumentOutOfRangeException( nameof( chordalDeflectionMm ), "Deflection must be above zero." );
		if ( angularDeflectionDeg <= 0 || angularDeflectionDeg > 180 ) throw new ArgumentOutOfRangeException( nameof( angularDeflectionDeg ), "Angular deflection must be in (0, 180]." );
		var result = new Result();
		foreach ( var face in solid.Faces )
		{
			if ( face is null ) continue;
			TessellateFace( face, chordalDeflectionMm, angularDeflectionDeg, result );
		}
		return result;
	}

	static int SidesFor( BrepFace face, double radius, double chordal, double angular )
	{
		var span = face.Kind switch
		{
			BrepSurfaceKind.Cylinder or BrepSurfaceKind.Cone => face.U1 - face.U0,
			BrepSurfaceKind.Sphere or BrepSurfaceKind.Torus => face.U1 - face.U0,
			_ => 2 * Math.PI,
		};
		var n = Math.Max( SidesForDeflection( radius, chordal ), (int)Math.Ceiling( 360.0 / angular ) );
		return Math.Max( 3, (int)Math.Ceiling( n * span / (2 * Math.PI) ) );
	}

	static void AddTri( Result r, int a, int b, int c ) { r.Triangles.Add( a ); r.Triangles.Add( b ); r.Triangles.Add( c ); }

	static void TessellateFace( BrepFace face, double chordal, double angular, Result r )
	{
		switch ( face.Kind )
		{
			case BrepSurfaceKind.Plane:
			{
				if ( face.InnerLoop is { Count: > 0 } && face.Loop.Count == face.InnerLoop.Count )
				{
					// Annulus ring: quads between the outer and inner loops.
					int n = face.Loop.Count;
					int baseIdx = r.Vertices.Count;
					r.Vertices.AddRange( face.Loop );
					r.Vertices.AddRange( face.InnerLoop );
					for ( int i = 0; i < n; i++ )
					{
						int j = (i + 1) % n;
						AddTri( r, baseIdx + i, baseIdx + j, baseIdx + n + j );
						AddTri( r, baseIdx + i, baseIdx + n + j, baseIdx + n + i );
					}
				}
				else if ( face.Loop.Count >= 3 )
				{
					// Convex polygon fan from its centroid (rectangles and sampled discs).
					var c = new Vec3( 0, 0, 0 );
					foreach ( var p in face.Loop ) c += p;
					c /= face.Loop.Count;
					int ci = r.Vertices.Count;
					r.Vertices.Add( c );
					int baseIdx = r.Vertices.Count;
					r.Vertices.AddRange( face.Loop );
					for ( int i = 0; i < face.Loop.Count; i++ )
					{
						int j = (i + 1) % face.Loop.Count;
						AddTri( r, ci, baseIdx + i, baseIdx + j );
					}
				}
				break;
			}
			case BrepSurfaceKind.Cylinder:
			case BrepSurfaceKind.Cone:
			{
				int n = SidesFor( face, face.Kind == BrepSurfaceKind.Cylinder ? face.Radius : Math.Max( face.Radius, 1e-9 ), chordal, angular );
				int baseIdx = r.Vertices.Count;
				for ( int i = 0; i <= n; i++ )
				{
					var u = face.U0 + (face.U1 - face.U0) * i / n;
					r.Vertices.Add( BrepMeasure.PointAt( face, u, face.V0 ) );
					r.Vertices.Add( BrepMeasure.PointAt( face, u, face.V1 ) );
				}
				bool apex = face.Kind == BrepSurfaceKind.Cone && face.V0 == 0;
				for ( int i = 0; i < n; i++ )
				{
					int a0 = baseIdx + i * 2, a1 = baseIdx + (i + 1) * 2;
					if ( apex && i == 0 )
					{
						// First ring is the apex point (a0 and a1 coincide); the quad is a fan triangle.
						AddTri( r, a0, a0 + 1, a1 + 1 );
					}
					else
					{
						AddTri( r, a0, a1, a1 + 1 );
						AddTri( r, a0, a1 + 1, a0 + 1 );
					}
				}
				break;
			}
			case BrepSurfaceKind.Sphere:
			{
				int nLon = SidesFor( face, face.Radius, chordal, angular );
				int nLat = Math.Max( 3, nLon / 2 );
				int baseIdx = r.Vertices.Count;
				for ( int j = 0; j <= nLat; j++ )
				{
					var v = face.V0 + (face.V1 - face.V0) * j / nLat;
					for ( int i = 0; i <= nLon; i++ )
					{
						var u = face.U0 + (face.U1 - face.U0) * i / nLon;
						r.Vertices.Add( BrepMeasure.PointAt( face, u, v ) );
					}
				}
				for ( int j = 0; j < nLat; j++ )
					for ( int i = 0; i < nLon; i++ )
					{
						int a = baseIdx + j * (nLon + 1) + i, b = a + 1, c = a + nLon + 1, d = c + 1;
						AddTri( r, a, c, d );
						AddTri( r, a, d, b );
					}
				break;
			}
			default: // Torus
			{
				int nMajor = SidesFor( face, face.Radius, chordal, angular );
				int nMinor = SidesFor( face, face.Radius2, chordal, angular );
				int baseIdx = r.Vertices.Count;
				for ( int j = 0; j <= nMinor; j++ )
				{
					var v = face.V0 + (face.V1 - face.V0) * j / nMinor;
					for ( int i = 0; i <= nMajor; i++ )
					{
						var u = face.U0 + (face.U1 - face.U0) * i / nMajor;
						r.Vertices.Add( BrepMeasure.PointAt( face, u, v ) );
					}
				}
				for ( int j = 0; j < nMinor; j++ )
					for ( int i = 0; i < nMajor; i++ )
					{
						int a = baseIdx + j * (nMajor + 1) + i, b = a + 1, c = a + nMajor + 1, d = c + 1;
						AddTri( r, a, c, d );
						AddTri( r, a, d, b );
					}
				break;
			}
		}
	}
}
