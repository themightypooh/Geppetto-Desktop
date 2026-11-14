using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// Lane 6a: a KV3 physics placement validator / emitter for Source 2 ModelDoc.
///
/// This is the validator half of Lane 6. It does NOT replace VmdlPhysics and does NOT
/// change CollisionBuilder: it takes a shape list, checks it against the placement rules
/// VmdlPhysics's own comment records as engine-probed, and emits a corrected
/// PhysicsShapeList node. The coordinator wires it in front of VmdlPhysics (map each
/// CollisionShape onto a PhysicsPlacementShape, run Validate, emit) — that wiring is
/// owed, not done here, because this lane creates new files only.
///
/// THE RULES, EXACTLY AS PROBED (VmdlPhysics class comment):
/// - PhysicsShapeBox: placed by `origin`, `dimensions` is the FULL size (not
///   half-extents). `center`, `translation` and `position` compile happily and leave
///   the box at the model origin — they must never appear on a box node.
/// - PhysicsShapeSphere: placed by `center`, and by nothing else. No `origin`.
/// - PhysicsShapeCapsule: `point0`, `point1`, `radius`. No separate placement key,
///   and none is needed — the points are where it goes. (Same key shape as the
///   PhysicsShapeCylinder VmdlPhysics already emits, but a different class: a
///   cylinder must NOT be silently rewritten as a capsule, or vice versa.)
/// - PhysicsShapeHull: `hull_vertices` in MODEL SPACE, verbatim. Nothing recentres
///   them, and a hull keeps its `parent_bone` — the compiler does the bone
///   conversion itself, which is why a hull is the one shape that may carry a bone.
///
/// THE BONE RULE, AND THE ONE CORRECTION THIS EMITTER MAKES. A primitive's
/// placement keys are read in its parent bone's LOCAL space (rotation included),
/// so a bone-parented box/sphere/capsule written as a primitive lands wherever
/// that bone's bind transform puts it — not where its model-space position says.
/// A hull's vertices are model space on every shape class. So a bone-parented
/// primitive is converted here to the hull of its bounding box (exact for a box;
/// the box around a sphere/capsule, never smaller than what it replaces), keeping
/// its bone. An unboned primitive is emitted as itself. This mirrors VmdlPhysics'
/// CornerHull conversion; it is repeated here, not imported, so this file stays
/// independent of CollisionShape and compiles in an isolated runner.
///
/// THE [0,0,0] TRAP, NARROWLY DEFINED. A shape centred at the origin is NOT a
/// trap — VmdlPhysics' own TwoBoxes fixture puts a 4-cube at the origin, and it
/// is correct there. A shape is flagged WouldLandAtOrigin only when it has NO
/// placement and NO size of its own at all: every field zero (box centre zero
/// AND half-extents zero, sphere centre zero AND radius zero, capsule endpoints
/// zero AND radius zero, hull empty or all vertices zero). That is the shape a
/// forgotten position produces, and the one that silently sits at the origin.
/// Zero size with a NONZERO anchor is a different issue (DegenerateSize): it is
/// a point somewhere real, not an origin trap, and it is still an error — it is
/// omitted from the emitted list either way, never silently kept.
///
/// INVALID SHAPES ARE OMITTED, NEVER SILENTLY ZEROED. A non-finite or negative
/// size, a hull with fewer than 4 points, or a degenerate shape is reported as
/// an Error and left out of the emitted node. An empty result emits "" — the
/// same rule as VmdlPhysics.ShapeList: a PhysicsShapeList that declares physics
/// and has none is worse than saying nothing.
/// </summary>
public enum PhysicsPlacementKind
{
	Box,
	Sphere,
	Capsule,
	Hull,
}

/// <summary>One shape to validate / emit. Factories pin each kind to its own fields.</summary>
public sealed class PhysicsPlacementShape
{
	public PhysicsPlacementKind Kind;

	/// <summary>Box centre / sphere centre, in model space. Unused by capsule/hull.</summary>
	public Vec3 Center;

	/// <summary>Box only: half-extents. Emitted dimensions are exactly twice these.</summary>
	public Vec3 HalfExtents;

	/// <summary>Sphere / capsule only: radius. Must be &gt; 0 to emit.</summary>
	public float Radius;

	/// <summary>Capsule only: endpoints, in model space.</summary>
	public Vec3 Point0;
	public Vec3 Point1;

	/// <summary>Hull only: vertices, in model space, verbatim.</summary>
	public List<Vec3> Vertices;

	/// <summary>Bone this shape rides on, or null/"" for the model root.</summary>
	public string ParentBone;

	public static PhysicsPlacementShape Box( Vec3 center, Vec3 halfExtents, string parentBone = null ) =>
		new() { Kind = PhysicsPlacementKind.Box, Center = center, HalfExtents = halfExtents, ParentBone = parentBone };

	public static PhysicsPlacementShape Sphere( Vec3 center, float radius, string parentBone = null ) =>
		new() { Kind = PhysicsPlacementKind.Sphere, Center = center, Radius = radius, ParentBone = parentBone };

	public static PhysicsPlacementShape Capsule( Vec3 point0, Vec3 point1, float radius, string parentBone = null ) =>
		new() { Kind = PhysicsPlacementKind.Capsule, Point0 = point0, Point1 = point1, Radius = radius, ParentBone = parentBone };

	public static PhysicsPlacementShape Hull( List<Vec3> vertices, string parentBone = null ) =>
		new() { Kind = PhysicsPlacementKind.Hull, Vertices = vertices, ParentBone = parentBone };
}

/// <summary>Severity of a validation finding. Warnings still emit (possibly corrected).</summary>
public enum PhysicsPlacementSeverity
{
	Warning,
	Error,
}

/// <summary>One validation finding against one shape in the list.</summary>
public sealed class PhysicsPlacementIssue
{
	public int Index;
	public PhysicsPlacementKind Kind;
	public string Code;
	public string Message;
	public PhysicsPlacementSeverity Severity;

	public override string ToString() => $"[{Index}] {Kind} {Code}: {Message}";
}

/// <summary>Validator + corrected KV3 emitter for a PhysicsShapeList.</summary>
public static class VmdlPhysicsPlacement
{
	/// <summary>Every finding for every shape, in list order. Null/empty in: no findings.</summary>
	public static List<PhysicsPlacementIssue> Validate( IReadOnlyList<PhysicsPlacementShape> shapes )
	{
		var issues = new List<PhysicsPlacementIssue>();
		if ( shapes is null )
			return issues;
		for ( var i = 0; i < shapes.Count; i++ )
			issues.AddRange( ValidateShape( shapes[i], i ) );
		return issues;
	}

	/// <summary>
	/// Findings for one shape. A bone-parented primitive is a Warning, not an Error:
	/// it is corrected to a hull on emit, so it still ships — but the caller is told,
	/// because a hull around a sphere/capsule is bigger than the shape it replaces.
	/// </summary>
	public static List<PhysicsPlacementIssue> ValidateShape( PhysicsPlacementShape shape, int index )
	{
		var issues = new List<PhysicsPlacementIssue>();
		void Add( PhysicsPlacementKind kind, string code, string message, PhysicsPlacementSeverity severity ) =>
			issues.Add( new PhysicsPlacementIssue { Index = index, Kind = kind, Code = code, Message = message, Severity = severity } );

		if ( shape is null )
		{
			Add( PhysicsPlacementKind.Box, "NullShape", "shape is null and is omitted from the emitted list.", PhysicsPlacementSeverity.Error );
			return issues;
		}

		var kind = shape.Kind;
		var boned = !string.IsNullOrEmpty( shape.ParentBone );

		switch ( kind )
		{
			case PhysicsPlacementKind.Box:
				if ( !Finite( shape.Center ) || !Finite( shape.HalfExtents ) )
					Add( kind, "InvalidNumber", "box centre or half-extents are not finite and the shape is omitted.", PhysicsPlacementSeverity.Error );
				else if ( shape.HalfExtents.x < 0f || shape.HalfExtents.y < 0f || shape.HalfExtents.z < 0f )
					Add( kind, "NegativeSize", "box half-extents are negative and the shape is omitted (a size is a magnitude; it is not abs-valued silently).", PhysicsPlacementSeverity.Error );
				else if ( shape.HalfExtents.x == 0f && shape.HalfExtents.y == 0f && shape.HalfExtents.z == 0f )
					Add( kind, shape.Center.Equals( Vec3.Zero ) ? "ZeroShapeAtOrigin" : "DegenerateSize",
						shape.Center.Equals( Vec3.Zero )
							? "box has zero size at the origin and would land at [0,0,0]; it is omitted."
							: "box has zero size and is omitted (a point, not a shape).", PhysicsPlacementSeverity.Error );
				break;

			case PhysicsPlacementKind.Sphere:
				if ( !Finite( shape.Center ) || !float.IsFinite( shape.Radius ) )
					Add( kind, "InvalidNumber", "sphere centre or radius are not finite and the shape is omitted.", PhysicsPlacementSeverity.Error );
				else if ( shape.Radius < 0f )
					Add( kind, "NegativeSize", "sphere radius is negative and the shape is omitted.", PhysicsPlacementSeverity.Error );
				else if ( shape.Radius == 0f )
					Add( kind, shape.Center.Equals( Vec3.Zero ) ? "ZeroShapeAtOrigin" : "DegenerateSize",
						shape.Center.Equals( Vec3.Zero )
							? "sphere has zero radius at the origin and would land at [0,0,0]; it is omitted."
							: "sphere has zero radius and is omitted.", PhysicsPlacementSeverity.Error );
				break;

			case PhysicsPlacementKind.Capsule:
				if ( !Finite( shape.Point0 ) || !Finite( shape.Point1 ) || !float.IsFinite( shape.Radius ) )
					Add( kind, "InvalidNumber", "capsule endpoints or radius are not finite and the shape is omitted.", PhysicsPlacementSeverity.Error );
				else if ( shape.Radius < 0f )
					Add( kind, "NegativeSize", "capsule radius is negative and the shape is omitted.", PhysicsPlacementSeverity.Error );
				else if ( shape.Radius == 0f && shape.Point0.Equals( Vec3.Zero ) && shape.Point1.Equals( Vec3.Zero ) )
					Add( kind, "ZeroShapeAtOrigin", "capsule has zero radius at the origin and would land at [0,0,0]; it is omitted.", PhysicsPlacementSeverity.Error );
				else if ( shape.Radius == 0f )
					Add( kind, "DegenerateSize", "capsule has zero radius and is omitted (a segment, not a shape).", PhysicsPlacementSeverity.Error );
				else if ( shape.Point0.Equals( shape.Point1 ) )
					Add( kind, "DegenerateCapsule", "capsule endpoints coincide, so it is a sphere of its radius; it is still emitted as a capsule.", PhysicsPlacementSeverity.Warning );
				break;

			case PhysicsPlacementKind.Hull:
				if ( shape.Vertices is null || shape.Vertices.Count < 4 )
					Add( kind, "HullTooFewPoints", "hull has fewer than 4 vertices and is omitted (a hull needs a volume).", PhysicsPlacementSeverity.Error );
				else if ( !AllFinite( shape.Vertices ) )
					Add( kind, "InvalidNumber", "hull has a non-finite vertex and the shape is omitted.", PhysicsPlacementSeverity.Error );
				else if ( AllZero( shape.Vertices ) )
					Add( kind, "ZeroShapeAtOrigin", "hull vertices are all at the origin and would land at [0,0,0]; it is omitted.", PhysicsPlacementSeverity.Error );
				else if ( AllSame( shape.Vertices ) )
					Add( kind, "DegenerateSize", "hull vertices are all one point and the shape is omitted.", PhysicsPlacementSeverity.Error );
				break;

			default:
				Add( kind, "UnknownKind", "unknown shape kind and the shape is omitted.", PhysicsPlacementSeverity.Error );
				break;
		}

		if ( boned && kind != PhysicsPlacementKind.Hull && !HasError( issues ) )
			Add( kind, "BonePrimitiveConverted",
				$"bone-parented {kind.ToString().ToLowerInvariant()} is read in the bone's local space, so it is emitted as its bounding-box hull instead (model-space vertices, bone kept).",
				PhysicsPlacementSeverity.Warning );
		return issues;
	}

	/// <summary>
	/// True only for the narrow trap: a shape with no placement AND no size of its
	/// own. A nonzero shape centred at the origin is NOT a trap and returns false.
	/// </summary>
	public static bool WouldLandAtOrigin( PhysicsPlacementShape shape )
	{
		if ( shape is null )
			return false;
		return shape.Kind switch
		{
			PhysicsPlacementKind.Box => shape.Center.Equals( Vec3.Zero )
				&& shape.HalfExtents.x == 0f && shape.HalfExtents.y == 0f && shape.HalfExtents.z == 0f,
			PhysicsPlacementKind.Sphere => shape.Center.Equals( Vec3.Zero ) && shape.Radius == 0f,
			PhysicsPlacementKind.Capsule => shape.Point0.Equals( Vec3.Zero ) && shape.Point1.Equals( Vec3.Zero ) && shape.Radius == 0f,
			PhysicsPlacementKind.Hull => shape.Vertices is null || shape.Vertices.Count == 0 || AllZero( shape.Vertices ),
			_ => false,
		};
	}

	/// <summary>
	/// The bone-safe form of a shape: a hull of its bounding box, bone kept, or the
	/// shape itself when it is already a hull. Box corners are exact; a sphere's
	/// box is radius on every axis; a capsule's box spans its endpoints expanded
	/// by its radius on every axis — never smaller than what it replaces.
	/// </summary>
	public static PhysicsPlacementShape ToHull( PhysicsPlacementShape shape )
	{
		if ( shape is null || shape.Kind == PhysicsPlacementKind.Hull )
			return shape;
		Vec3 lo, hi;
		switch ( shape.Kind )
		{
			case PhysicsPlacementKind.Box:
				lo = shape.Center - shape.HalfExtents;
				hi = shape.Center + shape.HalfExtents;
				break;
			case PhysicsPlacementKind.Sphere:
				lo = shape.Center - new Vec3( shape.Radius, shape.Radius, shape.Radius );
				hi = shape.Center + new Vec3( shape.Radius, shape.Radius, shape.Radius );
				break;
			default: // Capsule
				lo = new Vec3(
					MathF.Min( shape.Point0.x, shape.Point1.x ) - shape.Radius,
					MathF.Min( shape.Point0.y, shape.Point1.y ) - shape.Radius,
					MathF.Min( shape.Point0.z, shape.Point1.z ) - shape.Radius );
				hi = new Vec3(
					MathF.Max( shape.Point0.x, shape.Point1.x ) + shape.Radius,
					MathF.Max( shape.Point0.y, shape.Point1.y ) + shape.Radius,
					MathF.Max( shape.Point0.z, shape.Point1.z ) + shape.Radius );
				break;
		}
		var points = new List<Vec3>( 8 );
		for ( var i = 0; i < 8; i++ )
			points.Add( new Vec3(
				(i & 1) == 0 ? lo.x : hi.x,
				(i & 2) == 0 ? lo.y : hi.y,
				(i & 4) == 0 ? lo.z : hi.z ) );
		return new PhysicsPlacementShape { Kind = PhysicsPlacementKind.Hull, Vertices = points, ParentBone = shape.ParentBone };
	}

	/// <summary>
	/// The whole PhysicsShapeList node, indented to sit among a RootNode's
	/// children exactly as VmdlPhysics.ShapeList indents it, or "" when nothing
	/// valid remains. Error shapes are omitted; warning shapes emit (bone
	/// primitives as their hulls). A shape's own surface/collision values are
	/// the list defaults — per-shape overrides are a follow-up, not guessed here.
	/// </summary>
	public static string EmitShapeList( IReadOnlyList<PhysicsPlacementShape> shapes,
		string surfaceProp = "default", string collisionTags = "solid" )
	{
		if ( shapes is null || shapes.Count == 0 )
			return "";
		var body = new StringBuilder();
		var written = 0;
		for ( var i = 0; i < shapes.Count; i++ )
		{
			var shape = shapes[i];
			if ( shape is null || HasError( ValidateShape( shape, i ) ) )
				continue;
			if ( !string.IsNullOrEmpty( shape.ParentBone ) && shape.Kind != PhysicsPlacementKind.Hull )
				shape = ToHull( shape );
			body.Append( EmitShape( shape, surfaceProp, collisionTags ) );
			written++;
		}
		if ( written == 0 )
			return "";
		var sb = new StringBuilder();
		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"PhysicsShapeList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		sb.Append( body );
		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );
		return sb.ToString();
	}

	/// <summary>One already-validated shape node. Callers should Validate first; a shape that fails validation here emits "" rather than a bad node.</summary>
	public static string EmitShape( PhysicsPlacementShape shape, string surfaceProp = "default", string collisionTags = "solid" )
	{
		if ( shape is null || HasError( ValidateShape( shape, -1 ) ) )
			return "";
		// EmitShape is also reached with a boned primitive by direct callers; the
		// list path converts first, so convert here too rather than trusting it.
		if ( !string.IsNullOrEmpty( shape.ParentBone ) && shape.Kind != PhysicsPlacementKind.Hull )
			shape = ToHull( shape );
		var sb = new StringBuilder();
		switch ( shape.Kind )
		{
			case PhysicsPlacementKind.Box:
				Open( sb, "PhysicsShapeBox", shape, surfaceProp, collisionTags );
				Line( sb, "dimensions", Vector( shape.HalfExtents * 2f ) );
				Line( sb, "origin", Vector( shape.Center ) );
				break;
			case PhysicsPlacementKind.Sphere:
				Open( sb, "PhysicsShapeSphere", shape, surfaceProp, collisionTags );
				Line( sb, "radius", Number( shape.Radius ) );
				Line( sb, "center", Vector( shape.Center ) );
				break;
			case PhysicsPlacementKind.Capsule:
				Open( sb, "PhysicsShapeCapsule", shape, surfaceProp, collisionTags );
				Line( sb, "point0", Vector( shape.Point0 ) );
				Line( sb, "point1", Vector( shape.Point1 ) );
				Line( sb, "radius", Number( shape.Radius ) );
				break;
			case PhysicsPlacementKind.Hull:
				Open( sb, "PhysicsShapeHull", shape, surfaceProp, collisionTags );
				sb.Append( "\t\t\t\t\t\thull_vertices = \n" );
				sb.Append( "\t\t\t\t\t\t[\n" );
				foreach ( var p in shape.Vertices )
					sb.Append( $"\t\t\t\t\t\t\t{Vector( p )},\n" );
				sb.Append( "\t\t\t\t\t\t]\n" );
				break;
			default:
				return "";
		}
		sb.Append( "\t\t\t\t\t},\n" );
		return sb.ToString();

		void Open( StringBuilder into, string cls, PhysicsPlacementShape s, string sp, string ct )
		{
			into.Append( "\t\t\t\t\t{\n" );
			into.Append( $"\t\t\t\t\t\t_class = \"{cls}\"\n" );
			into.Append( $"\t\t\t\t\t\tparent_bone = \"{Escape( s.ParentBone ?? "" )}\"\n" );
			into.Append( $"\t\t\t\t\t\tsurface_prop = \"{Escape( sp )}\"\n" );
			into.Append( $"\t\t\t\t\t\tcollision_tags = \"{Escape( ct )}\"\n" );
		}
		static void Line( StringBuilder into, string key, string value ) =>
			into.Append( $"\t\t\t\t\t\t{key} = {value}\n" );
	}

	static bool HasError( List<PhysicsPlacementIssue> issues )
	{
		foreach ( var issue in issues )
			if ( issue.Severity == PhysicsPlacementSeverity.Error )
				return true;
		return false;
	}

	static bool Finite( Vec3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	static bool AllFinite( List<Vec3> points )
	{
		foreach ( var p in points )
			if ( !Finite( p ) )
				return false;
		return true;
	}

	static bool AllZero( List<Vec3> points )
	{
		foreach ( var p in points )
			if ( !p.Equals( Vec3.Zero ) )
				return false;
		return true;
	}

	static bool AllSame( List<Vec3> points )
	{
		for ( var i = 1; i < points.Count; i++ )
			if ( !points[i].Equals( points[0] ) )
				return false;
		return true;
	}

	static string Vector( Vec3 v ) => $"[ {Number( v.x )}, {Number( v.y )}, {Number( v.z )} ]";

	/// <summary>KV3 float: always a decimal point, never an exponent, never a comma decimal — same rule as VmdlPhysics.Number.</summary>
	static string Number( float value )
	{
		if ( !float.IsFinite( value ) )
			value = 0f;
		var text = value.ToString( "0.0######", CultureInfo.InvariantCulture );
		return text == "-0.0" ? "0.0" : text;
	}

	static string Escape( string value ) => value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
}
