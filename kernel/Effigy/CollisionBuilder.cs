using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

public enum CollisionKind
{
	Box,
	Sphere,
	Cylinder,
	Hull,
}

/// <summary>One convex piece of a part's physics representation.</summary>
public sealed class CollisionShape
{
	public CollisionKind Kind;

	/// <summary>Centre of the shape, in model space.</summary>
	public Vec3 Position;

	/// <summary>Box: half-extents. Sphere: radius in x. Cylinder: radius in x, half-height in z.</summary>
	public Vec3 Size;

	/// <summary>Hull only: the points on it.</summary>
	public List<Vec3> Points;

	/// <summary>Which body this came from, so a caller can group them.</summary>
	public string BodyId;

	/// <summary>
	/// The bone this shape rides on, or null to sit on the model root.
	///
	/// WITHOUT THIS A RIGGED MODEL'S COLLISION IS FURNITURE. The shapes are welded to the root, the
	/// mesh bends away from them, and the part is solid where it used to be rather than where it is.
	/// Null is still the right answer for a static part, which has no bones to hang off.
	///
	/// Only a <see cref="CollisionKind.Hull"/> can carry one and mean it — see VmdlPhysics for the
	/// measurement that says why.
	/// </summary>
	public string Bone;

	public override string ToString() => Kind switch
	{
		CollisionKind.Box => $"box {Size.x * 2:0.##} x {Size.y * 2:0.##} x {Size.z * 2:0.##}",
		CollisionKind.Sphere => $"sphere r{Size.x:0.##}",
		CollisionKind.Cylinder => $"cylinder r{Size.x:0.##} h{Size.z * 2:0.##}",
		_ => $"hull of {Points?.Count ?? 0} points",
	};
}

/// <summary>What a collision build produced, and how it got there.</summary>
public sealed class CollisionReport
{
	public readonly List<CollisionShape> Shapes;
	public readonly bool FromHistory;
	public readonly string Reason;

	public CollisionReport( List<CollisionShape> shapes, bool fromHistory, string reason )
	{
		Shapes = shapes;
		FromHistory = fromHistory;
		Reason = reason;
	}

	public override string ToString() =>
		$"{Shapes.Count} shape(s), {(FromHistory ? "from the feature history" : "as hulls")}"
		+ (Reason is null ? "" : $" - {Reason}");
}

/// <summary>
/// A part's physics representation, taken from the feature history rather than from the mesh.
///
/// A MODEL KNOWN TO BE A UNION OF N CONVEX PRIMITIVES IS ALREADY ITS OWN COLLISION, so this is
/// bookkeeping rather than geometry. Walking the finished mesh instead would throw away the one
/// thing that makes the answer exact: that somebody typed "box, 2 by 2 by 2" and nothing since has
/// been allowed to disturb it.
///
/// WHAT SPOILS IT is anything that changes a body's shape in a way a primitive cannot describe — an
/// extrude, a revolve, a boolean, a shell, a fillet, a subdivide. The moment one of those is in the
/// tree the history stops being a description of the shape and the fallback takes over: one convex
/// hull per body. Bigger than the part wherever the part is concave, never smaller, always convex,
/// and honest about being an approximation.
///
/// Deliberately all-or-nothing rather than per body. Working out WHICH bodies a boolean touched
/// means tracking body identity through operations that create and destroy them, and a physics
/// representation that is exact for three props and quietly wrong for the fourth is worse than one
/// that is approximate for all four and says so.
/// </summary>
public static class CollisionBuilder
{
	public static CollisionReport Build( PartStudio studio )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		// Collision chosen by hand wins over the history: the choice is what the modeller wants
		// to ship, and the history cannot express "this part has none".
		if ( studio.BodyCollision.Count > 0 )
			return new CollisionReport( Hulls( studio ), false, "collision was set per part" );

		if ( TryFromHistory( studio, out var shapes, out var spoiler ) )
			return new CollisionReport( shapes, true, null );

		return new CollisionReport( Hulls( studio ), false,
			spoiler is null ? "there is nothing in the history to read" : $"{spoiler} is in the tree" );
	}

	static bool TryFromHistory( PartStudio studio, out List<CollisionShape> shapes, out string spoiler )
	{
		shapes = new List<CollisionShape>();
		spoiler = null;

		var count = Math.Min( studio.RollbackIndex, studio.Features.Count );

		for ( var i = 0; i < count; i++ )
		{
			var feature = studio.Features[i];

			if ( feature.Suppressed )
				continue;

			switch ( feature )
			{
				case PrimitiveFeature primitive:
					if ( FromPrimitive( primitive ) is { } shape )
					{
						// H1.3: a history shape must name its body, or the split exporter
						// (BuildScript.StaticPhysics, collision=auto) cannot scope it: its
						// filter keeps every shape whose BodyId is null, so an unscoped
						// shape was duplicated onto EVERY split part. A primitive makes
						// exactly one body, whose FeatureId is this feature's Id.
						shape.BodyId = studio.Bodies.FirstOrDefault( b => b.FeatureId == primitive.Id )?.Id;
						shapes.Add( shape );
					}
					else
						spoiler = primitive.Shape.Value;
					break;

				// A sketch produces no solid, so it cannot spoil anything.
				case SketchFeature:
					continue;

				// These copy what is already there, which is exactly what a shape list can do too.
				case MirrorFeature mirror:
					Mirror( shapes, mirror );
					break;

				case TransformFeature transform:
					// H1.2: a turn or a stretch is PROPAGATED now, not spoiled. A
						// CollisionShape carries no orientation, so a shape that cannot
						// stay a primitive under the transform becomes the hull of its
						// transformed corners (box) or cap circles (cylinder) — the
						// same convex shape, in the right place and at the right
						// angle — instead of the whole model falling back to hulls.
						// Pure moves and uniform-scale-plus-move still stay primitives.
						// NOTE: this intentionally changes the old CollisionTests
						// expectation that a rotated Transform spoils the history;
						// that test was written for the old behaviour and needs
						// updating by whoever owns it (not edited here).
					TransformShapes( shapes, transform );
					break;

				default:
					spoiler ??= feature.TypeName;
					break;
			}

			if ( spoiler is not null )
				return false;
		}

		return shapes.Count > 0;
	}

	static CollisionShape FromPrimitive( PrimitiveFeature primitive )
	{
		var scale = primitive.Scale.Value;
		var position = primitive.Position.Value;

		// H1.1: the primitive's own Rotation, applied about its origin BEFORE
		// Position, exactly as PrimitiveFeature.Execute does (scale, then rotate,
		// then translate). A sphere is rotation-invariant, so it is untouched;
		// a rotated box or cylinder cannot stay a primitive — a CollisionShape
		// has no orientation — so it becomes the hull of its rotated corners
		// (box) or cap circles (cylinder), which is the same convex shape.
		var angle = primitive.RotationAngle.Value;
		var axis = primitive.RotationAxis.Value;
		var hasRotation = MathF.Abs( angle ) > 1e-4f && axis.LengthSquared > 1e-8f;
		var rotation = hasRotation
			? Xform.Rotate( axis.Normal, angle * MathF.PI / 180f )
			: Xform.Identity;

		switch ( primitive.Shape.Value )
		{
			case "Box":
			case "Plane":
				var half = new Vec3(
					primitive.SizeX.Clamped * 0.5f * scale.x,
					primitive.SizeY.Clamped * 0.5f * scale.y,
					primitive.SizeZ.Clamped * 0.5f * scale.z );
				if ( !hasRotation )
					return new CollisionShape
					{
						Kind = CollisionKind.Box,
						Position = position,
						Size = half,
					};
				return new CollisionShape
				{
					Kind = CollisionKind.Hull,
					Position = Vec3.Zero,
					Points = BoxCorners( half ).Select( c => rotation.TransformPoint( c ) + position ).ToList(),
				};

			case "Sphere":
				// A sphere scaled unevenly is an ellipsoid, and there is no ellipsoid collision
				// shape - so it falls back rather than pretending the smallest axis is the radius.
				if ( MathF.Abs( scale.x - scale.y ) > 1e-4f || MathF.Abs( scale.x - scale.z ) > 1e-4f )
					return null;

				return new CollisionShape
				{
					Kind = CollisionKind.Sphere,
					Position = position,
					Size = new Vec3( primitive.Radius.Clamped * scale.x, 0f, 0f ),
				};

			case "Cylinder":
				if ( MathF.Abs( scale.x - scale.y ) > 1e-4f )
					return null;

				var radius = primitive.Radius.Clamped * scale.x;
				var halfHeight = primitive.SizeZ.Clamped * 0.5f * scale.z;
				// A turn about the cylinder's own Z axis leaves it unchanged.
				if ( !hasRotation || Vec3.Cross( axis.Normal, new Vec3( 0, 0, 1 ) ).LengthSquared < 1e-8f )
					return new CollisionShape
					{
						Kind = CollisionKind.Cylinder,
						Position = position,
						Size = new Vec3( radius, 0f, halfHeight ),
						};
				return new CollisionShape
				{
					Kind = CollisionKind.Hull,
					Position = Vec3.Zero,
					Points = CylinderCapPoints( radius, halfHeight, rotation )
						.Select( c => c + position ).ToList(),
				};

			// A wedge is a prism and a tube has a hole down the middle. Neither is one convex
			// primitive, and a hull of a tube fills its bore in - so they go to the fallback, where
			// at least the approximation is stated.
			default:
				return null;
		}
	}

	static void Mirror( List<CollisionShape> shapes, MirrorFeature mirror )
	{
		var normal = mirror.PlaneNormal.Value;

		if ( normal.LengthSquared < 1e-12f )
			return;

		normal = normal.Normal;

		var origin = mirror.PlanePoint.Value;
		var copies = new List<CollisionShape>( shapes.Count );

		foreach ( var shape in shapes )
		{
			var offset = shape.Position - origin;
			var reflected = origin + offset - normal * (2f * Vec3.Dot( offset, normal ));

			// H1.2: a hull's POINTS must be reflected too — the old code copied
				// the list reference, so a mirrored hull sat exactly where the
				// original did. The copy is a new body made by the mirror, so it
				// is deliberately unscoped (BodyId null): scoping it to the
				// original body would hide it from its own split part.
				List<Vec3> points = null;
				if ( shape.Points is not null )
				{
					points = new List<Vec3>( shape.Points.Count );
					foreach ( var p in shape.Points )
					{
						var off = p - origin;
						points.Add( origin + off - normal * (2f * Vec3.Dot( off, normal )) );
					}
				}

				copies.Add( new CollisionShape
				{
					Kind = shape.Kind,
					Position = reflected,
					Size = shape.Size,
					Points = points,
					BodyId = null,
					Bone = shape.Bone,
				} );
			}

		// Keep original off means the mirror REPLACES what it reflected, so the shapes have to go
		// with it. Appending regardless would leave collision on a half of the part that is not there.
		if ( !mirror.KeepOriginal.Value )
			shapes.Clear();

		shapes.AddRange( copies );
	}

	/// <summary>
	/// The shapes that belong to one split part: every shape scoped to that body, plus every
	/// unscoped shape (BodyId null — mirror copies and shapes whose body could not be matched).
	/// Unscoped shapes pass every filter, exactly as BuildScript.StaticPhysics already treated
	/// them; the H1.3 fix is that history shapes are scoped now, so they no longer pass all of them.
	/// </summary>
	public static List<CollisionShape> ForBody( IReadOnlyList<CollisionShape> shapes, string bodyId )
	{
		var out_ = new List<CollisionShape>();
		if ( shapes is null )
			return out_;
		foreach ( var shape in shapes )
			if ( shape is not null && (shape.BodyId is null || shape.BodyId == bodyId) )
				out_.Add( shape );
		return out_;
	}

	static List<Vec3> BoxCorners( Vec3 half )
	{
		var corners = new List<Vec3>( 8 );
		for ( var i = 0; i < 8; i++ )
			corners.Add( new Vec3(
				(i & 1) == 0 ? -half.x : half.x,
				(i & 2) == 0 ? -half.y : half.y,
				(i & 4) == 0 ? -half.z : half.z ) );
		return corners;
	}

	/// <summary>16 points per cap of a cylinder: the polygon a tilted cylinder's hull is made of.</summary>
	static List<Vec3> CylinderCapPoints( float radius, float halfHeight, Xform rotation )
	{
		var points = new List<Vec3>( 32 );
		var xDir = rotation.TransformDirection( new Vec3( 1, 0, 0 ) );
		var yDir = rotation.TransformDirection( new Vec3( 0, 1, 0 ) );
		var zDir = rotation.TransformDirection( new Vec3( 0, 0, 1 ) );
		for ( var s = 0; s < 2; s++ )
		{
			var cap = zDir * (s == 0 ? -halfHeight : halfHeight);
			for ( var i = 0; i < 16; i++ )
			{
				var a = i * MathF.PI / 8f;
				points.Add( cap + xDir * (radius * MathF.Cos( a )) + yDir * (radius * MathF.Sin( a )) );
			}
		}
		return points;
	}

	/// <summary>Points on an ellipsoid surface, for a sphere stretched unevenly: a hull of these
	/// is the ellipsoid, sampled, rather than a sphere pretending to be it.</summary>
	static List<Vec3> EllipsoidPoints( Vec3 centre, Vec3 radii )
	{
		var points = new List<Vec3>();
		for ( var lat = -75f; lat <= 75f; lat += 30f )
		{
			var phi = lat * MathF.PI / 180f;
			for ( var lon = 0f; lon < 360f; lon += 30f )
			{
				var theta = lon * MathF.PI / 180f;
				points.Add( centre + new Vec3(
					radii.x * MathF.Cos( phi ) * MathF.Cos( theta ),
					radii.y * MathF.Cos( phi ) * MathF.Sin( theta ),
					radii.z * MathF.Sin( phi ) ) );
			}
		}
		points.Add( centre + new Vec3( 0, 0, radii.z ) );
		points.Add( centre - new Vec3( 0, 0, radii.z ) );
		return points;
	}

	/// <summary>
	/// Apply a TransformFeature's xform (scale, then rotate, then translate — the same order
	/// TransformFeature.Execute uses) to every history shape, in place. A shape that keeps its
	/// kind under the transform keeps it; anything else becomes the hull of its transformed
	/// corners / cap points / surface points, which is the same convex shape moved, not a
	/// square shape sitting where the turned part used to be.
	/// </summary>
	static void TransformShapes( List<CollisionShape> shapes, TransformFeature transform )
	{
		var xform = Xform.Translate( transform.Translate.Value )
			* Xform.Rotate( transform.RotationAxis.Value, transform.RotationAngle.Value * MathF.PI / 180f )
			* Xform.Scale( transform.Scale.Value );
		var scale = transform.Scale.Value;
		var rotated = MathF.Abs( transform.RotationAngle.Value ) > 1e-4f
			&& transform.RotationAxis.Value.LengthSquared > 1e-8f;
		var uniformScale = MathF.Abs( scale.x - scale.y ) < 1e-4f && MathF.Abs( scale.x - scale.z ) < 1e-4f;
		var mirrored = xform.Determinant < 0f;

		for ( var i = 0; i < shapes.Count; i++ )
		{
			var shape = shapes[i];
			var moved = xform.TransformPoint( shape.Position );
			switch ( shape.Kind )
			{
				case CollisionKind.Box:
					if ( !rotated && !mirrored )
					{
						shape.Position = moved;
						shape.Size = new Vec3( shape.Size.x * scale.x, shape.Size.y * scale.y, shape.Size.z * scale.z );
					}
					else
						shapes[i] = new CollisionShape
						{
							Kind = CollisionKind.Hull, Position = Vec3.Zero,
							Points = BoxCorners( shape.Size ).Select( c => xform.TransformPoint( shape.Position + c ) ).ToList(),
							BodyId = shape.BodyId, Bone = shape.Bone,
							};
					break;
				case CollisionKind.Sphere:
					if ( uniformScale && !mirrored )
					{
						shape.Position = moved;
						shape.Size = new Vec3( shape.Size.x * scale.x, 0f, 0f );
					}
					else if ( !rotated )
						shapes[i] = new CollisionShape
						{
							Kind = CollisionKind.Hull, Position = Vec3.Zero,
							Points = EllipsoidPoints( moved, new Vec3( shape.Size.x * scale.x, shape.Size.x * scale.y, shape.Size.x * scale.z ) ),
							BodyId = shape.BodyId, Bone = shape.Bone,
							};
					else
						shapes[i] = new CollisionShape
						{
							Kind = CollisionKind.Hull, Position = Vec3.Zero,
							Points = EllipsoidPoints( moved, new Vec3( shape.Size.x * scale.x, shape.Size.x * scale.x, shape.Size.x * scale.x ) ),
							BodyId = shape.BodyId, Bone = shape.Bone,
							};
					break;
				case CollisionKind.Cylinder:
					if ( !rotated && !mirrored && MathF.Abs( scale.x - scale.y ) < 1e-4f )
					{
						shape.Position = moved;
						shape.Size = new Vec3( shape.Size.x * scale.x, 0f, shape.Size.z * scale.z );
					}
					else
					{
						var rot = Xform.Rotate( transform.RotationAxis.Value, transform.RotationAngle.Value * MathF.PI / 180f );
						var xDir = rot.TransformDirection( new Vec3( 1, 0, 0 ) );
						var yDir = rot.TransformDirection( new Vec3( 0, 1, 0 ) );
						var zDir = rot.TransformDirection( new Vec3( 0, 0, 1 ) );
						var r = shape.Size.x * scale.x;
						var h = shape.Size.z * scale.z;
						var points = new List<Vec3>( 32 );
						for ( var s = 0; s < 2; s++ )
						{
							var capCentre = moved + zDir * (s == 0 ? -h : h);
							for ( var k = 0; k < 16; k++ )
							{
								var a = k * MathF.PI / 8f;
								points.Add( capCentre + xDir * (r * MathF.Cos( a )) + yDir * (r * MathF.Sin( a )) );
							}
						}
						shapes[i] = new CollisionShape
						{
							Kind = CollisionKind.Hull, Position = Vec3.Zero, Points = points,
							BodyId = shape.BodyId, Bone = shape.Bone,
							};
					}
					break;
				case CollisionKind.Hull:
					if ( shape.Points is not null )
						shape.Points = shape.Points.Select( p => xform.TransformPoint( p ) ).ToList();
					shape.Position = Vec3.Zero;
					break;
			}
		}
	}

	/// <summary>
	/// One hull per body, which is what an undecomposable model gets — and what a RIGGED model gets
	/// whether or not its history could be decomposed.
	///
	/// WHY A RIG THROWS THE DECOMPOSITION AWAY. Bones are assigned to BODIES: the rig panel's map is
	/// body id to bone name, and it is the only statement anywhere about which part of the model
	/// moves with which bone. A history decomposition does not respect that boundary — a mirror
	/// duplicates shapes, a move slides them, and the box that comes out the far end belongs to no
	/// body in particular. One hull per body is coarser and it is ADDRESSABLE, which is worth more
	/// than exactness the moment a shape has to name the bone it rides on.
	/// </summary>
	public static List<CollisionShape> HullsPerBody( PartStudio studio )
	{
		if ( studio is null )
			throw new ArgumentNullException( nameof( studio ) );

		return Hulls( studio );
	}

	static List<CollisionShape> Hulls( PartStudio studio )
	{
		var shapes = new List<CollisionShape>();

		foreach ( var body in studio.Bodies )
		{
			if ( body.Mesh is not { VertexCount: >= 4 } mesh )
				continue;

			// A choice made per part: none, a box, or the hull below.
			studio.BodyCollision.TryGetValue( body.Id, out var chosen );
			if ( chosen == "none" )
				continue;

			if ( chosen == "box" )
			{
				shapes.Add( BoxAround( mesh, body.Id ) );
				continue;
			}

			var hull = ConvexHull.Build( mesh.Positions );

			if ( hull is not { } built )
			{
				// Flat, collinear, or a single point: no volume to enclose. A box round it is the
				// only convex answer left and it is at least never smaller than the part.
				shapes.Add( BoxAround( mesh, body.Id ) );
				continue;
			}

			shapes.Add( new CollisionShape
			{
				Kind = CollisionKind.Hull,
				Position = Vec3.Zero,
				Points = built.Points,
				BodyId = body.Id,
			} );
		}

		return shapes;
	}

	static CollisionShape BoxAround( PolyMesh mesh, string bodyId )
	{
		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		return new CollisionShape
		{
			Kind = CollisionKind.Box,
			Position = (min + max) * 0.5f,
			Size = (max - min) * 0.5f,
			BodyId = bodyId,
		};
	}
}
