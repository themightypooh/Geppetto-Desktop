// PropPlacement — prop placement helpers: Move-to-Floor/Ceiling, align-by-normal,
// drop-to-rest pose.
//
// Hammer Desktop P2: hammer-prop-placement-helpers (worker muse-3-desktop-hammer-35).
// Backend / data-model / API only — no Desktop UI. The pose-freeze without a physics
// engine: props are seated on the map's own collision/baked geometry with raycasts
// and a fixed-step drop, never a physics simulation.
//
// Dependencies, as landed on disk 2026-11-13 (read, not assumed):
//   * MapEntities (hammer-entity-place-edit-validate, LANDED on disk while this ticket
//     was being written): kernel/Effigy/Map/MapEntities.cs, namespace Effigy.
//     PropPlacement seats real MapEntity props through MapEntities.SetOrigin/SetAngles,
//     so Origin/Angles stay in sync with keyvalues["angles"]. A prop entity is a
//     prop_static / prop_dynamic / prop_physics point entity from the registry.
//   * Quick Play (hammer-quick-play-bsp-only-bake, LANDED on disk, worker in progress):
//     kernel/Effigy/Map/QuickPlay.cs, namespace Effigy. FromQuickPlayMap builds the
//     exact surface from the map's brushes (AABBs) + displacement heightfields;
//     FromQuickPlayBake builds it from the bake's CollisionHulls (one convex hull
//     per solid brush via ConvexHull.Build, displacement hulls via ConvexDecomp).
//     The Quick Play desc says "collision via CollisionBuilder/ConvexDecomp" — what
//     actually landed is ConvexHull.Build for brushes + ConvexDecomp for displacements
//     (CollisionBuilder itself builds PartStudio CAD collision, not map collision),
//     and this file consumes what landed, not the desc's shorthand.
//   * Displacement (hammer-displacement-sculpt-brush-modes / displacement data model,
//     LANDED): kernel/Effigy/Map/Displacement.cs + IDisplacementSurface in
//     DisplacementSculpt.cs. FromDisplacement / FromDisplacementSurface triangulate
//     the real displaced grid, so a prop lands on the sculpted slope, not the flat base.
//   * CollisionBuilder / ConvexDecomp / ConvexHull (landed, namespace Effigy):
//     ConvexDecompResult hulls are consumed directly (their faces are already
//     triangles); CollisionShape hulls are re-hulled with ConvexHull.Build because
//     a CollisionShape carries points only, no faces.
//   * Model/particle/sound registries (hammer-model-particle-sound-registries, NOT
//     landed — still todo at write time, no AssetRegistries.cs on disk): PropPlacement
//     is geometry-only and takes no model path, so there is nothing to validate here.
//     When that ticket lands, its "placing a prop_static validates the model path
//     exists" check belongs in the placement call that hands PropPlacement a
//     MapEntity, not in the seating math.
//
// Conventions (kernel inches, Z-up):
//   * A PropPose is an origin + an Up direction + yaw/roll. Up is what AlignToNormal
//     sets to the surface normal; yaw is preserved across every seating operation.
//     Angles (pitch/yaw/roll, degrees, Hammer order) round-trip through Up: pitch is
//     asin(Up . forward(yaw)), roll is carried as a field because no Up vector can
//     express it. FromAngles/ Angles are inverses for pitch and yaw.
//   * Triangles are expected wound outward (boxes, hull faces and displacement grids
//     built here are). Raycast returns the true geometric normal, never flipped: a
//     ray that meets the underside of a surface sees a downward normal, and the
//     floor search skips it instead of seating a prop on a ceiling's underside.
//   * A prop whose origin starts inside a solid is NOT pushed out: the floor search
//     skips the underside hit and seats it on the next walkable surface below.
//     A drop whose first solid contact is steeper than MaxSlopeDegrees stops with
//     too-steep instead of sliding — there is no physics engine here to slide with.
//   * Raycasts start RestGap above the pose origin (floor) / below it (ceiling) so
//     a prop already resting on a surface re-seats instead of reporting no surface.
//     Reported hit distances are measured from the pose origin, not the offset start.

using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Map;

/// <summary>Named error codes for prop placement. Kebab-case, like QuickPlayError.</summary>
public static class PropPlacementError
{
	public const string NoSurface = "no-surface";
	public const string NoFloorBelow = "no-floor-below";
	public const string NoCeilingAbove = "no-ceiling-above";
	public const string TooSteep = "too-steep";
	public const string NoRestFound = "no-rest-found";
	public const string InvalidOptions = "invalid-options";
}

/// <summary>Named failure from the prop placement layer.</summary>
public sealed class PropPlacementException : Exception
{
	public string Code { get; }

	public PropPlacementException( string code, string message ) : base( message )
	{
		Code = code;
	}
}

/// <summary>One raycast hit against the placement surface: the surface point, its true
/// geometric normal, the distance from the ray origin, and which triangle was hit.
/// SlopeDegrees is the angle between the normal and world up — 0 is flat floor,
/// 90 is a wall, 180 is a ceiling underside.</summary>
public readonly struct PropPlacementHit
{
	public readonly Vec3 Point;
	public readonly Vec3 Normal;
	public readonly float Distance;
	public readonly int TriangleIndex;

	public PropPlacementHit( Vec3 point, Vec3 normal, float distance, int triangleIndex )
	{
		Point = point;
		Normal = normal;
		Distance = distance;
		TriangleIndex = triangleIndex;
	}

	public float SlopeDegrees => MathF.Acos( Math.Clamp( Normal.z, -1f, 1f ) ) * 180f / MathF.PI;

	public override string ToString() =>
		$"hit {Point} normal {Normal} d={Distance:0.##} tri={TriangleIndex}";
}

/// <summary>A prop's placement pose: where its origin sits, which way is up for it, and
/// its yaw/roll. Up is a unit vector (Zero in means straight up); yaw is degrees
/// around Z, roll is degrees around the prop's forward axis.</summary>
public readonly struct PropPose
{
	public readonly Vec3 Origin;
	public readonly Vec3 Up;
	public readonly float YawDegrees;
	public readonly float RollDegrees;

	public PropPose( Vec3 origin, Vec3 up, float yawDegrees, float rollDegrees = 0f )
	{
		Origin = origin;
		Up = up.Length > 1e-12f ? up.Normal : new Vec3( 0f, 0f, 1f );
		YawDegrees = yawDegrees;
		RollDegrees = rollDegrees;
	}

	/// <summary>Upright pose: Up is world up.</summary>
	public static PropPose Upright( Vec3 origin, float yawDegrees = 0f, float rollDegrees = 0f ) =>
		new PropPose( origin, new Vec3( 0f, 0f, 1f ), yawDegrees, rollDegrees );

	/// <summary>Horizontal forward direction for this yaw: (cos yaw, sin yaw, 0).</summary>
	public Vec3 Forward
	{
		get
		{
			float r = YawDegrees * MathF.PI / 180f;
			return new Vec3( MathF.Cos( r ), MathF.Sin( r ), 0f );
		}
	}

	/// <summary>Hammer angles (pitch/yaw/roll, degrees). Pitch is asin(Up . forward):
	/// positive pitch tilts Up toward the prop's forward direction. Roll is carried
	/// verbatim — no Up vector can express it.</summary>
	public Vec3 Angles
	{
		get
		{
			float pitch = MathF.Asin( Math.Clamp( Vec3.Dot( Up, Forward ), -1f, 1f ) ) * 180f / MathF.PI;
			return new Vec3( pitch, YawDegrees, RollDegrees );
		}
	}

	/// <summary>Pose from Hammer angles: Up = Rz(yaw) * Ry(pitch) * (0,0,1), i.e.
	/// (sin pitch * cos yaw, sin pitch * sin yaw, cos pitch). The inverse of
	/// <see cref="Angles"/> for pitch and yaw; roll is stored as given.</summary>
	public static PropPose FromAngles( Vec3 origin, Vec3 angles )
	{
		float p = angles.x * MathF.PI / 180f;
		float y = angles.y * MathF.PI / 180f;
		var up = new Vec3( MathF.Sin( p ) * MathF.Cos( y ), MathF.Sin( p ) * MathF.Sin( y ), MathF.Cos( p ) );
		return new PropPose( origin, up, angles.y, angles.z );
	}

	/// <summary>Pose of a placed entity: its Origin and Angles.</summary>
	public static PropPose FromEntity( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return FromAngles( entity.Origin, entity.Angles );
	}

	public PropPose WithOrigin( Vec3 origin ) => new PropPose( origin, Up, YawDegrees, RollDegrees );
	public PropPose WithUp( Vec3 up ) => new PropPose( Origin, up, YawDegrees, RollDegrees );

	public override string ToString() => $"pose {Origin} up {Up} yaw {YawDegrees:0.##}";
}

/// <summary>Tuning for the seating operations. All distances are kernel inches.</summary>
public sealed class PropPlacementOptions
{
	/// <summary>How far MoveToFloor / MoveToCeiling raycast. Default 4096 (one big room).</summary>
	public float MaxDistance = 4096f;
	/// <summary>How far DropToRest may fall in total. Default 4096.</summary>
	public float MaxDrop = 4096f;
	/// <summary>Fixed drop step for DropToRest. Default 4: small enough that a prop never
	/// tunnels through a thin floor in one step, large enough that a long drop is cheap.</summary>
	public float StepSize = 4f;
	/// <summary>Steepest surface a prop may rest on, in degrees from horizontal.
	/// Default 45 — Hammer's walkable-slope neighbourhood.</summary>
	public float MaxSlopeDegrees = 45f;
	/// <summary>Height of the prop origin above its base contact point. 0 seats the
	/// origin on the surface; a prop whose origin is its centre passes half its height.</summary>
	public float OriginHeightAboveBase = 0f;
	/// <summary>When true (default), seating sets Up to the surface normal. When false,
	/// the prop keeps its incoming Up and only its origin moves.</summary>
	public bool AlignOnLand = true;
	/// <summary>A hit within this distance below the origin counts as already resting.
	/// Also the ray-start offset, so a resting prop re-seats. Default 0.5.</summary>
	public float RestGap = 0.5f;

	internal static PropPlacementOptions Validated( PropPlacementOptions options )
	{
		options ??= new PropPlacementOptions();
		var defects = new List<string>();
		if ( !( options.MaxDistance > 0f ) ) defects.Add( "MaxDistance must be > 0" );
		if ( !( options.MaxDrop >= 0f ) ) defects.Add( "MaxDrop must be >= 0" );
		if ( !( options.StepSize > 0f ) ) defects.Add( "StepSize must be > 0" );
		if ( !( options.MaxSlopeDegrees >= 0f && options.MaxSlopeDegrees <= 90f ) ) defects.Add( "MaxSlopeDegrees must be 0..90" );
		if ( !( options.OriginHeightAboveBase >= 0f ) ) defects.Add( "OriginHeightAboveBase must be >= 0" );
		if ( !( options.RestGap >= 0f ) ) defects.Add( "RestGap must be >= 0" );
		if ( defects.Count > 0 )
			throw new PropPlacementException( PropPlacementError.InvalidOptions,
				"invalid-options: " + string.Join( "; ", defects ) );
		return options;
	}

	/// <summary>Cosine of MaxSlopeDegrees: a floor hit needs Normal.z &gt;= this.</summary>
	internal float MinFloorNormalZ => MathF.Cos( MaxSlopeDegrees * MathF.PI / 180f );
}

/// <summary>What a seating operation did. Seated carries the new pose and the hit it
/// rests on; not seated carries the named Reason (a PropPlacementError code) and the
/// pose is the incoming one, unchanged. StepsTaken counts the drop for DropToRest
/// (0 for the instant operations); DistanceDropped is the signed vertical change
/// of the seated pose (negative when the prop rose to a ceiling), or how far it
/// fell before a failed drop gave up.</summary>
public sealed class PropPlacementResult
{
	public bool Seated { get; internal set; }
	public PropPose Pose { get; internal set; }
	public PropPlacementHit? Hit { get; internal set; }
	public string Reason { get; internal set; }
	public int StepsTaken { get; internal set; }
	public float DistanceDropped { get; internal set; }

	internal static PropPlacementResult SeatedResult( PropPose pose, PropPlacementHit hit, int steps, float dropped ) =>
		new PropPlacementResult { Seated = true, Pose = pose, Hit = hit, Reason = null, StepsTaken = steps, DistanceDropped = dropped };

	internal static PropPlacementResult NotSeated( PropPose pose, string reason, PropPlacementHit? hit = null, int steps = 0, float dropped = 0f ) =>
		new PropPlacementResult { Seated = false, Pose = pose, Hit = hit, Reason = reason, StepsTaken = steps, DistanceDropped = dropped };

	public override string ToString() => Seated
		? $"seated {Pose} on {Hit} steps={StepsTaken} dropped={DistanceDropped:0.##}"
		: $"not seated ({Reason}) pose {Pose} steps={StepsTaken} dropped={DistanceDropped:0.##}";
}

/// <summary>One triangle of the placement surface, with its precomputed geometric normal.</summary>
public readonly struct PropPlacementTriangle
{
	public readonly Vec3 A, B, C;
	public readonly Vec3 Normal;

	public PropPlacementTriangle( Vec3 a, Vec3 b, Vec3 c )
	{
		A = a; B = b; C = c;
		Normal = Vec3.Cross( b - a, c - a ).Normal;
	}
}

/// <summary>The collision surface props seat on: a triangle soup built from the map's
/// own geometry. Brute-force raycast (no BVH): placement is a handful of casts per
/// prop, not a per-frame query, and a BVH would be a second spatial index to keep in
/// sync with the map. TriangleCount in the low thousands is the expected case
/// (a room of brushes + a displacement grid); a whole-map soup is still fine for
/// one-off placement, and a caller that seats hundreds of props reuses one surface.</summary>
public sealed class PropPlacementSurface
{
	readonly List<PropPlacementTriangle> _triangles = new List<PropPlacementTriangle>();

	/// <summary>Triangles on the surface, in build order.</summary>
	public IReadOnlyList<PropPlacementTriangle> Triangles => _triangles;
	public int TriangleCount => _triangles.Count;
	/// <summary>Zero-area triangles dropped at build time (degenerate hull points,
	/// collapsed displacement cells, pole duplicates on spheres).</summary>
	public int DegenerateSkipped { get; private set; }
	/// <summary>Where the triangles came from, in build order ("quickplay-map: 6 brushes,
	/// 1 displacement", "collision-shapes: 3 hulls", ...). Diagnostics, not identity.</summary>
	public List<string> Sources { get; } = new List<string>();
	/// <summary>Build notes: shapes that could not be turned into triangles and why.</summary>
	public List<string> Notes { get; } = new List<string>();
	public Vec3 BoundsMin { get; private set; }
	public Vec3 BoundsMax { get; private set; }

	void AddTriangle( Vec3 a, Vec3 b, Vec3 c )
	{
		var cross = Vec3.Cross( b - a, c - a );
		if ( cross.Length < 1e-9f ) { DegenerateSkipped++; return; }
		_triangles.Add( new PropPlacementTriangle( a, b, c ) );
		if ( _triangles.Count == 1 ) { BoundsMin = a; BoundsMax = a; }
		ExpandBounds( a ); ExpandBounds( b ); ExpandBounds( c );
	}

	void ExpandBounds( Vec3 p )
	{
		BoundsMin = new Vec3( MathF.Min( BoundsMin.x, p.x ), MathF.Min( BoundsMin.y, p.y ), MathF.Min( BoundsMin.z, p.z ) );
		BoundsMax = new Vec3( MathF.Max( BoundsMax.x, p.x ), MathF.Max( BoundsMax.y, p.y ), MathF.Max( BoundsMax.z, p.z ) );
	}

	void AddQuad( Vec3 a, Vec3 b, Vec3 c, Vec3 d )
	{
		AddTriangle( a, b, c );
		AddTriangle( a, c, d );
	}

	/// <summary>Box from axis-aligned bounds, wound outward. The exact collision shape
	/// of a QuickPlay brush (AABB); also the fallback shape for a sphere/cylinder
	/// CollisionShape whose tessellation is unavailable — never used as one, see
	/// <see cref="FromCollisionShapes"/>.</summary>
	void AddBox( Vec3 min, Vec3 max )
	{
		var c = new[]
		{
			new Vec3( min.x, min.y, min.z ), new Vec3( max.x, min.y, min.z ),
			new Vec3( max.x, max.y, min.z ), new Vec3( min.x, max.y, min.z ),
			new Vec3( min.x, min.y, max.z ), new Vec3( max.x, min.y, max.z ),
			new Vec3( max.x, max.y, max.z ), new Vec3( min.x, max.y, max.z ),
		};
		AddQuad( c[0], c[2], c[1], c[3] ); // bottom, normal -Z
		AddQuad( c[4], c[5], c[6], c[7] ); // top, normal +Z
		AddQuad( c[0], c[1], c[5], c[4] ); // south, normal -Y
		AddQuad( c[2], c[3], c[7], c[6] ); // north, normal +Y
		AddQuad( c[3], c[0], c[4], c[7] ); // west, normal -X
		AddQuad( c[1], c[2], c[6], c[5] ); // east, normal +X
	}

	void AddSphere( Vec3 centre, float radius )
	{
		const int stacks = 6, slices = 8;
		var ring = new Vec3[stacks + 1, slices];
		for ( int i = 0; i <= stacks; i++ )
		{
			float phi = ( -90f + 180f * i / stacks ) * MathF.PI / 180f;
			for ( int j = 0; j < slices; j++ )
			{
				float theta = 360f * j / slices * MathF.PI / 180f;
				ring[i, j] = centre + new Vec3(
					radius * MathF.Cos( phi ) * MathF.Cos( theta ),
					radius * MathF.Cos( phi ) * MathF.Sin( theta ),
					radius * MathF.Sin( phi ) );
			}
		}
		for ( int i = 0; i < stacks; i++ )
			for ( int j = 0; j < slices; j++ )
				AddQuad( ring[i, j], ring[i, ( j + 1 ) % slices], ring[i + 1, ( j + 1 ) % slices], ring[i + 1, j] );
	}

	void AddCylinder( Vec3 centre, float radius, float halfHeight )
	{
		const int sides = 12;
		var bottom = new Vec3[sides];
		var top = new Vec3[sides];
		for ( int j = 0; j < sides; j++ )
		{
			float theta = 360f * j / sides * MathF.PI / 180f;
			var radial = new Vec3( radius * MathF.Cos( theta ), radius * MathF.Sin( theta ), 0f );
			bottom[j] = centre + radial - new Vec3( 0f, 0f, halfHeight );
			top[j] = centre + radial + new Vec3( 0f, 0f, halfHeight );
		}
		for ( int j = 0; j < sides; j++ )
		{
			int k = ( j + 1 ) % sides;
			AddQuad( bottom[j], bottom[k], top[k], top[j] );
			AddTriangle( centre + new Vec3( 0f, 0f, halfHeight ), top[j], top[k] );
			AddTriangle( centre - new Vec3( 0f, 0f, halfHeight ), bottom[k], bottom[j] );
		}
	}

	void AddHullPoints( IReadOnlyList<Vec3> points, string label )
	{
		if ( points is null || points.Count < 4 ) { Notes.Add( $"hull '{label}' has fewer than 4 points, skipped" ); return; }
		var built = ConvexHull.Build( points );
		if ( built is null ) { Notes.Add( $"hull '{label}' encloses no volume, skipped" ); return; }
		foreach ( var face in built.Value.Faces )
			AddTriangle( built.Value.Points[face.A], built.Value.Points[face.B], built.Value.Points[face.C] );
	}

	// ---- Factories ----

	/// <summary>Surface from an indexed triangle list. Indices must come in triples;
	/// a bad triple (out of range, or a count that is not a multiple of 3) throws
	/// ArgumentException — a placement surface built from corrupt indices must fail
	/// loudly at build time, not seat props on half a floor.</summary>
	public static PropPlacementSurface FromTriangles( IReadOnlyList<Vec3> vertices, IReadOnlyList<int> indices )
	{
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		if ( indices is null ) throw new ArgumentNullException( nameof( indices ) );
		if ( indices.Count % 3 != 0 )
			throw new ArgumentException( $"Triangle index count {indices.Count} is not a multiple of 3.", nameof( indices ) );
		var surface = new PropPlacementSurface();
		for ( int i = 0; i < indices.Count; i += 3 )
		{
			int a = indices[i], b = indices[i + 1], c = indices[i + 2];
			if ( a < 0 || a >= vertices.Count || b < 0 || b >= vertices.Count || c < 0 || c >= vertices.Count )
				throw new ArgumentException( $"Triangle index out of range at offset {i}: ({a}, {b}, {c}) for {vertices.Count} vertices.", nameof( indices ) );
			surface.AddTriangle( vertices[a], vertices[b], vertices[c] );
		}
		surface.Sources.Add( $"triangles: {surface.TriangleCount} from {vertices.Count} vertices" );
		return surface;
	}

	/// <summary>Empty surface: every seating operation reports no-surface. Useful for
	/// the void / no-geometry case without a null.</summary>
	public static PropPlacementSurface Empty()
	{
		var surface = new PropPlacementSurface();
		surface.Sources.Add( "empty" );
		return surface;
	}

	/// <summary>Exact surface from a Quick Play map: every brush as its AABB box and
	/// every displacement as its heightfield grid (two triangles per cell, bilinear
	/// heights from <see cref="QuickPlayDisplacement.HeightAt"/>). No hull
	/// approximation anywhere — this is the geometry the prop visibly sits on.</summary>
	public static PropPlacementSurface FromQuickPlayMap( QuickPlayMap map )
	{
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		var surface = new PropPlacementSurface();
		int brushes = 0;
		foreach ( var b in map.Brushes )
		{
			if ( b is null || !b.IsValid ) continue;
			surface.AddBox( b.Min, b.Max );
			brushes++;
		}
		int displacements = 0;
		foreach ( var d in map.Displacements )
		{
			if ( d is null || !d.IsValid ) continue;
			for ( int r = 0; r < d.Rows - 1; r++ )
				for ( int c = 0; c < d.Columns - 1; c++ )
				{
					float x0 = d.Origin.x + d.SizeX * c / ( d.Columns - 1 );
					float x1 = d.Origin.x + d.SizeX * ( c + 1 ) / ( d.Columns - 1 );
					float y0 = d.Origin.y + d.SizeY * r / ( d.Rows - 1 );
					float y1 = d.Origin.y + d.SizeY * ( r + 1 ) / ( d.Rows - 1 );
					var a = new Vec3( x0, y0, d.Heights[r, c] );
					var b = new Vec3( x1, y0, d.Heights[r, c + 1] );
					var cc = new Vec3( x1, y1, d.Heights[r + 1, c + 1] );
					var e = new Vec3( x0, y1, d.Heights[r + 1, c] );
					surface.AddQuad( a, b, cc, e );
				}
			displacements++;
		}
		surface.Sources.Add( $"quickplay-map: {brushes} brushes, {displacements} displacements, {surface.TriangleCount} triangles" );
		return surface;
	}

	/// <summary>Surface from a Quick Play bake's collision hulls: the baked collision,
	/// not the visible geometry. Brush hulls are convex hulls of the brush corners,
	/// displacement hulls are ConvexDecomp pieces; both arrive as
	/// <see cref="CollisionShape"/> points-only, so each is re-hulled with
	/// <see cref="ConvexHull.Build"/> to recover its faces. A displacement's baked
	/// hull is a solid down to MinHeight - 16, so a prop seats on the hull top —
	/// slightly above the visible slope wherever the decomp coarsened it. Prefer
	/// <see cref="FromQuickPlayMap"/> when the map is at hand.</summary>
	public static PropPlacementSurface FromQuickPlayBake( QuickPlayBakeResult bake )
	{
		if ( bake is null ) throw new ArgumentNullException( nameof( bake ) );
		return FromCollisionShapes( bake.CollisionHulls, "quickplay-bake" );
	}

	/// <summary>Surface from CollisionBuilder / Quick Play collision shapes. Box is
	/// exact; Hull is re-hulled from its points; Sphere and Cylinder are tessellated
	/// (8x6 sphere, 12-sided cylinder) — an approximation, recorded in Sources, not
	/// a silent bounding box.</summary>
	public static PropPlacementSurface FromCollisionShapes( IReadOnlyList<CollisionShape> shapes, string sourceLabel = "collision-shapes" )
	{
		if ( shapes is null ) throw new ArgumentNullException( nameof( shapes ) );
		var surface = new PropPlacementSurface();
		int boxes = 0, hulls = 0, spheres = 0, cylinders = 0;
		foreach ( var shape in shapes )
		{
			if ( shape is null ) continue;
			switch ( shape.Kind )
			{
				case CollisionKind.Box:
					surface.AddBox( shape.Position - shape.Size, shape.Position + shape.Size );
					boxes++;
					break;
				case CollisionKind.Hull:
					surface.AddHullPoints( shape.Points, shape.BodyId ?? "hull" );
					hulls++;
					break;
				case CollisionKind.Sphere:
					surface.AddSphere( shape.Position, shape.Size.x );
					spheres++;
					break;
				case CollisionKind.Cylinder:
					surface.AddCylinder( shape.Position, shape.Size.x, shape.Size.z );
					cylinders++;
					break;
			}
		}
		surface.Sources.Add( $"{sourceLabel}: {boxes} boxes, {hulls} hulls, {spheres} spheres, {cylinders} cylinders, {surface.TriangleCount} triangles" );
		return surface;
	}

	/// <summary>Surface from a ConvexDecomp result: each hull's faces are already
	/// triangles (HullFace A/B/C into the hull's points), so no re-hulling is needed.</summary>
	public static PropPlacementSurface FromConvexDecomp( ConvexDecompResult decomp )
	{
		if ( decomp is null ) throw new ArgumentNullException( nameof( decomp ) );
		var surface = new PropPlacementSurface();
		int hulls = 0;
		foreach ( var hull in decomp.Hulls )
		{
			if ( hull is null || hull.Points is null || hull.Faces is null ) continue;
			foreach ( var face in hull.Faces )
			{
				if ( face.A < 0 || face.A >= hull.Points.Count || face.B < 0 || face.B >= hull.Points.Count || face.C < 0 || face.C >= hull.Points.Count )
					continue;
				surface.AddTriangle( hull.Points[face.A], hull.Points[face.B], hull.Points[face.C] );
			}
			hulls++;
		}
		surface.Sources.Add( $"convex-decomp: {hulls} hulls, {surface.TriangleCount} triangles" );
		return surface;
	}

	/// <summary>Surface from a bound displacement's mesh: the real displaced grid
	/// (quads fanned to triangles), so a prop lands on the sculpted slope.</summary>
	public static PropPlacementSurface FromDisplacement( Displacement displacement )
	{
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		var mesh = displacement.ToMesh();
		var surface = new PropPlacementSurface();
		foreach ( var face in mesh.Faces )
		{
			if ( face is null || face.Indices is null || face.Count < 3 ) continue;
			for ( int i = 1; i < face.Count - 1; i++ )
				surface.AddTriangle(
					mesh.Positions[face.Indices[0]],
					mesh.Positions[face.Indices[i]],
					mesh.Positions[face.Indices[i + 1]] );
		}
		surface.Sources.Add( $"displacement: power {displacement.Power}, {surface.TriangleCount} triangles" );
		return surface;
	}

	/// <summary>Surface from a displacement sculpt surface: the displaced vertex grid,
	/// two triangles per cell.</summary>
	public static PropPlacementSurface FromDisplacementSurface( IDisplacementSurface target )
	{
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		var surface = new PropPlacementSurface();
		int side = target.Side;
		for ( int r = 0; r < side - 1; r++ )
			for ( int c = 0; c < side - 1; c++ )
				surface.AddQuad(
					target.VertexPosition( r, c ),
					target.VertexPosition( r, c + 1 ),
					target.VertexPosition( r + 1, c + 1 ),
					target.VertexPosition( r + 1, c ) );
		surface.Sources.Add( $"displacement-surface: {side}x{side} grid, {surface.TriangleCount} triangles" );
		return surface;
	}

	/// <summary>Combine surfaces (e.g. map geometry + a prop's own collision hulls
	/// excluded — callers combine only what the prop may rest on).</summary>
	public static PropPlacementSurface Combine( params PropPlacementSurface[] parts )
	{
		var surface = new PropPlacementSurface();
		if ( parts is null ) return surface;
		foreach ( var part in parts )
		{
			if ( part is null ) continue;
			foreach ( var tri in part._triangles ) surface.AddTriangle( tri.A, tri.B, tri.C );
			surface.Sources.AddRange( part.Sources );
			surface.Notes.AddRange( part.Notes );
		}
		return surface;
	}

	// ---- Raycast ----

	/// <summary>All hits along a ray, nearest first. Double-sided: a triangle is hit
	/// from either side, and the hit normal is the triangle's true geometric normal
	/// (never flipped toward the ray) — see the file header for why that matters.</summary>
	public IReadOnlyList<PropPlacementHit> RaycastAll( Vec3 origin, Vec3 direction, float maxDistance )
	{
		if ( direction.Length < 1e-12f ) throw new ArgumentException( "Ray direction must not be zero.", nameof( direction ) );
		if ( !( maxDistance > 0f ) ) throw new ArgumentException( "maxDistance must be > 0.", nameof( maxDistance ) );
		var dir = direction.Normal;
		var hits = new List<PropPlacementHit>();
		for ( int i = 0; i < _triangles.Count; i++ )
		{
			var tri = _triangles[i];
			if ( !RayTriangle( origin, dir, tri, out float t ) ) continue;
			if ( t > maxDistance ) continue;
			hits.Add( new PropPlacementHit( origin + dir * t, tri.Normal, t, i ) );
		}
		hits.Sort( ( a, b ) =>
		{
			int c = a.Distance.CompareTo( b.Distance );
			return c != 0 ? c : a.TriangleIndex.CompareTo( b.TriangleIndex );
		} );
		return hits;
	}

	/// <summary>Nearest hit along a ray, or false.</summary>
	public bool Raycast( Vec3 origin, Vec3 direction, float maxDistance, out PropPlacementHit hit )
	{
		var hits = RaycastAll( origin, direction, maxDistance );
		if ( hits.Count == 0 ) { hit = default; return false; }
		hit = hits[0];
		return true;
	}

	/// <summary>Nearest surface below a point (straight down, -Z).</summary>
	public bool RaycastDown( Vec3 origin, float maxDistance, out PropPlacementHit hit ) =>
		Raycast( origin, new Vec3( 0f, 0f, -1f ), maxDistance, out hit );

	/// <summary>Nearest surface above a point (straight up, +Z).</summary>
	public bool RaycastUp( Vec3 origin, float maxDistance, out PropPlacementHit hit ) =>
		Raycast( origin, new Vec3( 0f, 0f, 1f ), maxDistance, out hit );

	static bool RayTriangle( Vec3 origin, Vec3 dir, PropPlacementTriangle tri, out float t )
	{
		t = 0f;
		var e1 = tri.B - tri.A;
		var e2 = tri.C - tri.A;
		var p = Vec3.Cross( dir, e2 );
		float det = Vec3.Dot( e1, p );
		if ( MathF.Abs( det ) < 1e-9f ) return false;
		float inv = 1f / det;
		var tv = origin - tri.A;
		float u = Vec3.Dot( tv, p ) * inv;
		if ( u < -1e-6f || u > 1f + 1e-6f ) return false;
		var q = Vec3.Cross( tv, e1 );
		float v = Vec3.Dot( dir, q ) * inv;
		if ( v < -1e-6f || u + v > 1f + 1e-6f ) return false;
		t = Vec3.Dot( e2, q ) * inv;
		return t > 1e-4f;
	}
}

/// <summary>Prop placement helpers. Every operation takes a pose and returns a
/// <see cref="PropPlacementResult"/> — the input pose is never mutated, and a
/// failure is a named Reason on the result, not an exception, except for null
/// arguments and invalid options, which are caller bugs and throw.
/// <para/>
/// The entity overloads (SeatOnFloor / SeatOnCeiling / DropEntityToRest /
/// AlignEntityToNormal) are the same operations on a placed
/// <see cref="MapEntity"/>: on success they write the seated pose back through
/// <see cref="MapEntities"/> (SetOrigin/SetAngles, so keyvalues["angles"] stays in
/// sync); on failure the entity is left exactly where it was.</summary>
public static class PropPlacement
{
	static readonly Vec3 Down = new Vec3( 0f, 0f, -1f );
	static readonly Vec3 Up = new Vec3( 0f, 0f, 1f );

	// ---- Raycast helpers ----

	/// <summary>Nearest walkable-or-not surface below a point. False when nothing is below.</summary>
	public static bool TryRaycastDown( PropPlacementSurface surface, Vec3 origin, float maxDistance, out PropPlacementHit hit )
	{
		if ( surface is null ) throw new ArgumentNullException( nameof( surface ) );
		return surface.RaycastDown( origin, maxDistance, out hit );
	}

	/// <summary>Nearest surface above a point. False when nothing is above.</summary>
	public static bool TryRaycastUp( PropPlacementSurface surface, Vec3 origin, float maxDistance, out PropPlacementHit hit )
	{
		if ( surface is null ) throw new ArgumentNullException( nameof( surface ) );
		return surface.RaycastUp( origin, maxDistance, out hit );
	}

	// ---- Pose operations ----

	/// <summary>Move a prop to the nearest walkable floor below it: raycast down, take
	/// the first hit whose normal is within MaxSlopeDegrees of up (undersides are
	/// skipped, so a prop inside a solid seats on the floor below it, not on the
	/// solid's underside). The seated origin is the hit point plus Up *
	/// OriginHeightAboveBase; with AlignOnLand the prop's Up becomes the hit normal.
	/// No walkable hit below: not seated, no-floor-below when nothing was hit at all,
	/// too-steep when something was hit but every hit was too steep.</summary>
	public static PropPlacementResult MoveToFloor( PropPlacementSurface surface, PropPose pose, PropPlacementOptions options = null )
	{
		options = PropPlacementOptions.Validated( options );
		if ( surface is null ) throw new ArgumentNullException( nameof( surface ) );
		if ( surface.TriangleCount == 0 )
			return PropPlacementResult.NotSeated( pose, PropPlacementError.NoSurface );
		var hits = surface.RaycastAll( pose.Origin + Up * options.RestGap, Down, options.MaxDistance + options.RestGap );
		return SeatOnHit( pose, hits, options, PropPlacementError.NoFloorBelow, floor: true );
	}

	/// <summary>Move a prop to the nearest ceiling above it: raycast up, take the
	/// first hit whose normal is within MaxSlopeDegrees of straight down. With
	/// AlignOnLand the prop's Up becomes that (downward) normal — the prop hangs
	/// upside down from the ceiling, as Hammer's Move to Ceiling does. The seated
	/// origin is the hit point plus the seated Up * OriginHeightAboveBase, i.e. the
	/// prop hangs that far below the ceiling surface.</summary>
	public static PropPlacementResult MoveToCeiling( PropPlacementSurface surface, PropPose pose, PropPlacementOptions options = null )
	{
		options = PropPlacementOptions.Validated( options );
		if ( surface is null ) throw new ArgumentNullException( nameof( surface ) );
		if ( surface.TriangleCount == 0 )
			return PropPlacementResult.NotSeated( pose, PropPlacementError.NoSurface );
		var hits = surface.RaycastAll( pose.Origin - Up * options.RestGap, Up, options.MaxDistance + options.RestGap );
		return SeatOnHit( pose, hits, options, PropPlacementError.NoCeilingAbove, floor: false );
	}

	/// <summary>Orient a prop to the surface normal under it, without moving its
	/// origin: raycast down, and when a walkable floor is found set Up to its normal
	/// (yaw and roll are preserved). No floor below: not seated, pose unchanged.</summary>
	public static PropPlacementResult AlignToNormal( PropPlacementSurface surface, PropPose pose, PropPlacementOptions options = null )
	{
		options = PropPlacementOptions.Validated( options );
		if ( surface is null ) throw new ArgumentNullException( nameof( surface ) );
		if ( surface.TriangleCount == 0 )
			return PropPlacementResult.NotSeated( pose, PropPlacementError.NoSurface );
		var hits = surface.RaycastAll( pose.Origin + Up * options.RestGap, Down, options.MaxDistance + options.RestGap );
		foreach ( var hit in hits )
		{
			if ( hit.Normal.z < options.MinFloorNormalZ ) continue;
			var aligned = pose.WithUp( hit.Normal );
			return PropPlacementResult.SeatedResult( aligned, AdjustHit( hit, options.RestGap ), 0, 0f );
		}
		return hits.Count == 0
			? PropPlacementResult.NotSeated( pose, PropPlacementError.NoFloorBelow )
			: PropPlacementResult.NotSeated( pose, PropPlacementError.TooSteep, AdjustHit( hits[0], options.RestGap ) );
	}

	/// <summary>Drop a prop under gravity onto collision and settle its orientation —
	/// the pose-freeze without a physics engine. Falls in fixed StepSize steps (never
	/// more than MaxDrop total); after each step it raycasts down, and lands when a
	/// walkable surface is within one step plus RestGap. A prop already resting (a
	/// surface within RestGap) seats immediately with 0 steps. Landing aligns Up to
	/// the surface normal when AlignOnLand is set. Stops not seated with too-steep
	/// when the first contact is steeper than MaxSlopeDegrees, and with
	/// no-rest-found when MaxDrop runs out over the void. The prop falls straight
	/// down: it never slides, rolls, or tips over an edge.</summary>
	public static PropPlacementResult DropToRest( PropPlacementSurface surface, PropPose pose, PropPlacementOptions options = null )
	{
		options = PropPlacementOptions.Validated( options );
		if ( surface is null ) throw new ArgumentNullException( nameof( surface ) );
		if ( surface.TriangleCount == 0 )
			return PropPlacementResult.NotSeated( pose, PropPlacementError.NoSurface );
		float minUp = options.MinFloorNormalZ;
		var current = pose.Origin;
		float remaining = options.MaxDrop;
		int steps = 0;
		int maxSteps = (int)Math.Ceiling( options.MaxDrop / options.StepSize );
		for ( int step = 0; step <= maxSteps; step++ )
		{
			// Probe from RestGap above the current origin, like MoveToFloor: a prop
			// already resting exactly on the surface (hit distance 0) would otherwise
			// miss — RayTriangle rejects t == 0 — and fall straight through the floor.
			var hits = surface.RaycastAll( current + Up * options.RestGap, Down, remaining + options.StepSize + options.RestGap );
			if ( hits.Count > 0 )
			{
				var first = AdjustHit( hits[0], options.RestGap );
				if ( first.Normal.z < minUp )
					return PropPlacementResult.NotSeated( pose, PropPlacementError.TooSteep, first, steps, pose.Origin.z - current.z );
				if ( first.Distance <= options.RestGap + 1e-3f )
				{
					var up = options.AlignOnLand ? first.Normal : pose.Up;
					var seated = new PropPose( first.Point + up * options.OriginHeightAboveBase, up, pose.YawDegrees, pose.RollDegrees );
					return PropPlacementResult.SeatedResult( seated, first, steps, pose.Origin.z - seated.Origin.z );
				}
				if ( first.Distance <= options.StepSize + options.RestGap )
				{
					var up = options.AlignOnLand ? first.Normal : pose.Up;
					var seated = new PropPose( first.Point + up * options.OriginHeightAboveBase, up, pose.YawDegrees, pose.RollDegrees );
					return PropPlacementResult.SeatedResult( seated, first, steps + 1, pose.Origin.z - seated.Origin.z );
				}
			}
			float fall = MathF.Min( options.StepSize, remaining );
			if ( fall <= 0f ) break;
			current = new Vec3( current.x, current.y, current.z - fall );
			remaining -= fall;
			steps++;
		}
		return PropPlacementResult.NotSeated( pose, PropPlacementError.NoRestFound, null, steps, pose.Origin.z - current.z );
	}

	static PropPlacementResult SeatOnHit( PropPose pose, IReadOnlyList<PropPlacementHit> hits, PropPlacementOptions options, string emptyReason, bool floor )
	{
		float minUp = options.MinFloorNormalZ;
		foreach ( var hit in hits )
		{
			bool walkable = floor ? hit.Normal.z >= minUp : hit.Normal.z <= -minUp;
			if ( !walkable ) continue;
			var adjusted = AdjustHit( hit, options.RestGap );
			var up = options.AlignOnLand ? hit.Normal : pose.Up;
			var seated = new PropPose( hit.Point + up * options.OriginHeightAboveBase, up, pose.YawDegrees, pose.RollDegrees );
			return PropPlacementResult.SeatedResult( seated, adjusted, 0, pose.Origin.z - seated.Origin.z );
		}
		return hits.Count == 0
			? PropPlacementResult.NotSeated( pose, emptyReason )
			: PropPlacementResult.NotSeated( pose, PropPlacementError.TooSteep, AdjustHit( hits[0], options.RestGap ) );
	}

	/// <summary>The ray started RestGap beyond the pose origin, so a hit distance
	/// measured from the ray start is RestGap too long. Distances are reported from
	/// the pose origin, never negative.</summary>
	static PropPlacementHit AdjustHit( PropPlacementHit hit, float restGap ) =>
		new PropPlacementHit( hit.Point, hit.Normal, MathF.Max( 0f, hit.Distance - restGap ), hit.TriangleIndex );

	// ---- Entity operations (MapEntities-owned props) ----

	/// <summary>Seat a placed prop entity on the floor below it. On success the
	/// entity's Origin and Angles are written back through <paramref name="entities"/>
	/// (SetOrigin/SetAngles); on failure the entity is untouched.</summary>
	public static PropPlacementResult SeatOnFloor( MapEntities entities, MapEntity entity, PropPlacementSurface surface, PropPlacementOptions options = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var result = MoveToFloor( surface, PropPose.FromEntity( entity ), options );
		if ( result.Seated ) Apply( entities, entity, result.Pose );
		return result;
	}

	/// <summary>Seat a placed prop entity on the ceiling above it (hanging, Up flipped
	/// to the ceiling normal when AlignOnLand is set). On failure the entity is untouched.</summary>
	public static PropPlacementResult SeatOnCeiling( MapEntities entities, MapEntity entity, PropPlacementSurface surface, PropPlacementOptions options = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var result = MoveToCeiling( surface, PropPose.FromEntity( entity ), options );
		if ( result.Seated ) Apply( entities, entity, result.Pose );
		return result;
	}

	/// <summary>Drop a placed prop entity to rest. On failure the entity is untouched.</summary>
	public static PropPlacementResult DropEntityToRest( MapEntities entities, MapEntity entity, PropPlacementSurface surface, PropPlacementOptions options = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var result = DropToRest( surface, PropPose.FromEntity( entity ), options );
		if ( result.Seated ) Apply( entities, entity, result.Pose );
		return result;
	}

	/// <summary>Align a placed prop entity to the surface normal under it, without
	/// moving it. On failure the entity is untouched.</summary>
	public static PropPlacementResult AlignEntityToNormal( MapEntities entities, MapEntity entity, PropPlacementSurface surface, PropPlacementOptions options = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var result = AlignToNormal( surface, PropPose.FromEntity( entity ), options );
		if ( result.Seated ) Apply( entities, entity, result.Pose );
		return result;
	}

	static void Apply( MapEntities entities, MapEntity entity, PropPose pose )
	{
		entities.SetOrigin( entity, pose.Origin );
		entities.SetAngles( entity, pose.Angles );
	}
}
