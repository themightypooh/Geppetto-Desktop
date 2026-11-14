using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Map;

// CadToMap — CAD feature tree <-> map brushes (Hammer Desktop).
// Board: hammer-interop-cad-feature-tree-brushes.
//
// A sketch/extrude/revolve/loft/sweep feature can produce MapBrushes, and a MapBrush is
// recorded as a feature in the CAD feature tree so it survives parametric rebuild:
//
//   * Extrude: a closed profile prismed along its sketch-plane normal. Exact: a convex
//     profile is ONE brush (one side plane per edge + two caps); a concave profile is
//     triangulated (ear clipping) into one triangular-prism brush per triangle, so the
//     union is exactly the prism. Volume is exact in both cases.
//   * Revolve: a profile revolved about an axis in its own plane. Faceted approximation:
//     the profile is triangulated and every triangle is revolved one angular segment at
//     a time; each triangle x segment is one convex-hull brush. Volume converges to the
//     analytic volume from below as Segments grows (chord planes cut the true surface).
//   * Loft: sections skinned into prismoids. Every section must have the same point
//     count and the same winding, with matching point order (index i is the same corner
//     in every section): each triangle of section 0, paired with the same triangle in
//     the next section, is one convex-hull brush. Ruled (linear) loft between matching
//     sections is exact. There is no start-angle realignment (LoftFeature has one;
//     this ticket does not): sections drawn with different start corners loft twisted,
//     and sections with different windings are refused rather than silently twisted.
//   * Sweep: a profile carried along explicit path stations (pure translation, no
//     banking/twist). One convex-hull brush per path segment (per triangle for a
//     concave profile). A sketch-path sweep is NOT in scope: the stations are the
//     feature's Path list, so a future ticket can fill them from a path sketch.
//
// A MapBrush is recorded in the tree two ways:
//   * Parametric: CadToMapFeature (below) holds the conversion parameters + the sketch
//     it consumes and regenerates its brushes on every Execute, so editing the sketch
//     rebuilds the brush. This is the rebuild-consistent recording.
//   * Snapshot: RecordBrushes appends one MapBrushFeature per brush (the feature
//     MapBrush.cs already provides). A snapshot does NOT follow sketch edits; it is
//     for brushes produced outside a sketch (a primitive, an import). The future
//     MapDocument owns the association by feature Id (stable, persisted).
//
// MAP DOCUMENT SEAM (dep hammer-map-document-model has LANDED: MapDocument.cs +
// MapDocumentIo.cs, worker 12, 2026-11-13). A MapDocument is identified by its
// DocumentId string (MapDocument.Create(name) assigns one); its brushes are
// MapBrushData, converted to/from the landed MapBrush by
// MapBrushData.FromMapBrush / ToMapBrush, and added by MapDocument.AddBrush (which
// records a history entry and bumps the revision). The seam this file provides:
// CadToMapFeature.MapId holds the target document's DocumentId (AttachToMap, with
// an overload taking the MapDocument itself), FeaturesForMap finds the conversions
// bound to one DocumentId, and PublishToMap copies converted brushes into the map
// document by value through FromMapBrush. The feature in the tree stays the
// parametric source of truth; the map document holds the published snapshot, and a
// re-publish after a sketch edit replaces it (the map document's own history makes
// that an undoable edit, not a silent overwrite). CadToMap itself never touches
// the filesystem and never shells out: NATIVE ONLY.
//
// NAMED REFUSALS, NEVER SILENT FIXES. A conversion that cannot be represented refuses
// with a CadToMapException carrying a machine-readable Reason: a profile with holes
// (a frame needs several brushes / CSG, not one prism), a revolve profile crossing its
// axis, a self-intersecting loop, loft sections with different point counts or
// windings, a zero distance, a path with fewer than two stations. The feature turns
// those into a feature Error (FailOn "Conversion") so the tree carries on and the
// dialog says what is wrong. Taper/draft is NOT supported: an extrude here is a blind
// prism, and a tapered extrude must not silently come out straight.
//
// TYPE NOTE. This file is in namespace Effigy.Map, where an unqualified MapBrushFace
// would resolve to the DIFFERENT MapBrushFace struct Displacement.cs defines in this
// namespace (corners + int material slot), not the landed brush face. Every reference
// to the landed types is therefore fully qualified (Effigy.MapBrush,
// Effigy.MapBrushFace, Effigy.MapBrushFeature). Do not "simplify" those qualifiers.
//
// MapBrushPrimitives.cs is NOT fixed by this ticket: it builds against a MapFace /
// MapPlane API the landed MapBrush does not provide (CS0246), and its owner / the
// integrator reconciles it. Nothing here references it.

/// <summary>A CAD-to-map conversion that cannot be represented, with a machine-readable
/// <see cref="Reason"/>: never a silent fix, never a wrong brush.</summary>
public sealed class CadToMapException : Exception
{
	/// <summary>Machine-readable refusal, e.g. "ProfileHasHoles".</summary>
	public string Reason { get; }

	public CadToMapException( string reason, string message ) : base( message ) => Reason = reason;
}

/// <summary>Which conversion a <see cref="CadToMapFeature"/> performs. The order matches
/// the feature's Conversion ChoiceParam options exactly: append, never reorder (a
/// ChoiceParam serialises its index).</summary>
public enum CadConversionKind
{
	Extrude,
	Revolve,
	Loft,
	Sweep,
}

/// <summary>One loft section: a closed loop in its own sketch plane's coordinates, plus
/// that plane. The loop's point order is the correspondence: index i is the same corner
/// in every section of a loft.</summary>
public readonly struct CadSection
{
	public readonly IReadOnlyList<Vec2> Loop;
	public readonly SketchPlane Plane;

	public CadSection( IReadOnlyList<Vec2> loop, SketchPlane plane )
	{
		Loop = loop;
		Plane = plane;
	}
}

/// <summary>Options for <see cref="CadToMap.ConvertSketch"/>. A plain holder, not a
/// persisted feature: the feature persists the same values as its own parameters.</summary>
public sealed class CadConversion
{
	public CadConversionKind Kind = CadConversionKind.Extrude;
	public float Distance = 4f;
	public float StartOffset = 0f;
	public float SecondDistance = 0f;
	public bool Symmetric = false;
	public bool Flip = false;
	public float AngleDegrees = 360f;
	public int Segments = 24;
	public Vec2 AxisPoint = new( 0f, 0f );
	public Vec2 AxisDirection = new( 1f, 0f );
	public string MaterialName = "";
	/// <summary>Sweep stations, in model space. Used only by Sweep.</summary>
	public List<Vec3> Path = new();
	/// <summary>Loft section sketches, in order. Used only by Loft.</summary>
	public List<Sketch> SectionSketches = new();
}

/// <summary>Profile -> brush conversion plus feature-tree recording. All methods are
/// pure functions of their inputs (no studio state, no IO) except the Record*/Attach*/
/// helpers, which append to a PartStudio.</summary>
public static class CadToMap
{
	/// <summary>The sketch has no closed region to build from.</summary>
	public const string ReasonNoClosedProfile = "NoClosedProfile";

	/// <summary>The profile has a hole: a frame, not one prism. Build it from several
	/// brushes instead.</summary>
	public const string ReasonProfileHasHoles = "ProfileHasHoles";

	/// <summary>The revolve profile straddles its axis (radii on both sides).</summary>
	public const string ReasonProfileCrossesAxis = "ProfileCrossesAxis";

	/// <summary>The loop crosses itself, or could not be triangulated.</summary>
	public const string ReasonSelfIntersectingProfile = "SelfIntersectingProfile";

	/// <summary>The loop encloses no area (fewer than 3 distinct points, or zero area).</summary>
	public const string ReasonZeroAreaProfile = "ZeroAreaProfile";

	/// <summary>A profile point is not finite.</summary>
	public const string ReasonInvalidProfile = "InvalidProfile";

	/// <summary>The extrude distance is not a finite number greater than zero.</summary>
	public const string ReasonZeroDistance = "ZeroDistance";

	/// <summary>A conversion number is out of its range (negative second distance,
	/// non-finite offset, revolve angle outside (0, 360], segments outside 3..512).</summary>
	public const string ReasonInvalidValue = "InvalidValue";

	/// <summary>The revolve axis direction is zero-length.</summary>
	public const string ReasonInvalidAxis = "InvalidAxis";

	/// <summary>Loft sections do not all have the same point count.</summary>
	public const string ReasonSectionPointCountMismatch = "SectionPointCountMismatch";

	/// <summary>Loft sections do not all wind the same way.</summary>
	public const string ReasonSectionWindingMismatch = "SectionWindingMismatch";

	/// <summary>Fewer than two loft sections.</summary>
	public const string ReasonTooFewSections = "TooFewSections";

	/// <summary>A loft section sketch has no closed region.</summary>
	public const string ReasonNoSectionProfile = "NoSectionProfile";

	/// <summary>The sweep path has fewer than two distinct stations.</summary>
	public const string ReasonDegeneratePath = "DegeneratePath";

	/// <summary>A conversion produced a brush that does not validate, or a hull
	/// degenerated to nothing. The defect codes are in the message.</summary>
	public const string ReasonInvalidBrush = "InvalidBrush";

	/// <summary>A hull needs at least 4 distinct points to enclose a volume.</summary>
	public const string ReasonDegenerateHull = "DegenerateHull";

	// --- extrude ---------------------------------------------------------------------

	/// <summary>Prism a closed loop along its sketch-plane normal. One brush when the loop
	/// is convex, one triangular-prism brush per ear-clipping triangle when it is concave;
	/// the union is exactly the prism either way. The solid runs from
	/// StartOffset - dir*SecondDistance to StartOffset + dir*Distance along the plane
	/// normal (dir is -1 when Flip, +1 otherwise); Symmetric splits Distance evenly
	/// about the plane and ignores SecondDistance, as Extrude does.</summary>
	public static IReadOnlyList<Effigy.MapBrush> ExtrudeLoop( IReadOnlyList<Vec2> loop, SketchPlane plane,
		float distance, string materialName = null,
		float startOffset = 0f, float secondDistance = 0f, bool symmetric = false, bool flip = false )
	{
		if ( plane is null ) throw new ArgumentNullException( nameof( plane ) );
		if ( !float.IsFinite( distance ) || distance <= 0f )
			throw new CadToMapException( ReasonZeroDistance,
				$"Extrude distance must be a finite number greater than zero, got {distance}." );
		if ( !float.IsFinite( startOffset ) )
			throw new CadToMapException( ReasonInvalidValue,
				$"Extrude start offset must be finite, got {startOffset}." );
		if ( !float.IsFinite( secondDistance ) || secondDistance < 0f )
			throw new CadToMapException( ReasonInvalidValue,
				$"Extrude second distance must be finite and at least zero, got {secondDistance}." );

		var clean = CleanLoop( loop );
		float lo, hi;
		if ( symmetric )
		{
			lo = -distance * 0.5f;
			hi = distance * 0.5f;
		}
		else
		{
			var dir = flip ? -1f : 1f;
			lo = MathF.Min( startOffset - dir * secondDistance, startOffset + dir * distance );
			hi = MathF.Max( startOffset - dir * secondDistance, startOffset + dir * distance );
		}

		if ( IsConvex( clean ) )
			return new[] { Prism( clean, plane, lo, hi, materialName ) };

		var brushes = new List<Effigy.MapBrush>();
		foreach ( var (a, b, c) in Triangulate( clean ) )
			brushes.Add( Prism( new[] { clean[a], clean[b], clean[c] }, plane, lo, hi, materialName ) );
		return brushes;
	}

	/// <summary>Prism one sketch profile. A profile with holes is refused: a frame is not
	/// one prism.</summary>
	public static IReadOnlyList<Effigy.MapBrush> ExtrudeProfile( Profile profile, SketchPlane plane,
		float distance, string materialName = null,
		float startOffset = 0f, float secondDistance = 0f, bool symmetric = false, bool flip = false )
	{
		if ( profile is null ) throw new ArgumentNullException( nameof( profile ) );
		if ( profile.HasHoles )
			throw new CadToMapException( ReasonProfileHasHoles,
				"Profile has a hole: an extruded frame is not one prism. Build the frame from several brushes instead." );
		return ExtrudeLoop( profile.Outer, plane, distance, materialName, startOffset, secondDistance, symmetric, flip );
	}

	/// <summary>Prism every closed region of a sketch (overlap lenses excluded, as
	/// SketchConsumingFeature does when no region is picked). No closed region is a
	/// refusal, not an empty list.</summary>
	public static IReadOnlyList<Effigy.MapBrush> ExtrudeSketch( Sketch sketch,
		float distance, string materialName = null,
		float startOffset = 0f, float secondDistance = 0f, bool symmetric = false, bool flip = false )
	{
		if ( sketch is null ) throw new ArgumentNullException( nameof( sketch ) );
		var profiles = ClosedProfiles( sketch );
		var brushes = new List<Effigy.MapBrush>();
		foreach ( var profile in profiles )
			brushes.AddRange( ExtrudeProfile( profile, sketch.Plane, distance, materialName, startOffset, secondDistance, symmetric, flip ) );
		return brushes;
	}

	// --- revolve ---------------------------------------------------------------------

	/// <summary>Revolve a closed loop about an axis in its own plane (axis point and
	/// direction in plane coordinates). Faceted: the loop is triangulated and every
	/// triangle is revolved one angular segment at a time; each triangle x segment is
	/// one convex-hull brush. The profile must sit on one side of the axis (points ON
	/// the axis are fine); straddling it is refused. Angle is (0, 360], segments 3..512.</summary>
	public static IReadOnlyList<Effigy.MapBrush> RevolveLoop( IReadOnlyList<Vec2> loop, SketchPlane plane,
		Vec2 axisPoint, Vec2 axisDirection, float angleDegrees = 360f, int segments = 24, string materialName = null )
	{
		if ( plane is null ) throw new ArgumentNullException( nameof( plane ) );
		if ( !float.IsFinite( angleDegrees ) || angleDegrees <= 0f || angleDegrees > 360f )
			throw new CadToMapException( ReasonInvalidValue,
				$"Revolve angle must be in (0, 360] degrees, got {angleDegrees}." );
		if ( segments < 3 || segments > 512 )
			throw new CadToMapException( ReasonInvalidValue,
				$"Revolve segments must be in 3..512, got {segments}." );
		if ( axisDirection.LengthSquared < 1e-12f )
			throw new CadToMapException( ReasonInvalidAxis, "Revolve axis direction is zero-length." );

		var clean = CleanLoop( loop );
		var dir = axisDirection.Normal;
		var perp = new Vec2( -dir.y, dir.x );

		// One side of the axis only: radii change sign across it.
		var sign = 0f;
		foreach ( var p in clean )
		{
			var radial = Vec2.Cross( dir, p - axisPoint );
			if ( MathF.Abs( radial ) <= 1e-6f ) continue;
			var s = MathF.Sign( radial );
			if ( sign == 0f ) sign = s;
			else if ( s != sign )
				throw new CadToMapException( ReasonProfileCrossesAxis,
					"Revolve profile straddles its axis: radii fall on both sides of it. Move the profile to one side, or put the axis on a profile edge." );
		}

		var axis3 = plane.XAxis * dir.x + plane.YAxis * dir.y;
		var perp3 = plane.XAxis * perp.x + plane.YAxis * perp.y;
		var normal3 = plane.Normal;
		var origin3 = plane.ToWorld( axisPoint );
		var step = angleDegrees * MathF.PI / 180f / segments;
		var brushes = new List<Effigy.MapBrush>();
		foreach ( var (a, b, c) in Triangulate( clean ) )
		{
			for ( var j = 0; j < segments; j++ )
			{
				var t0 = j * step;
				var t1 = ( j + 1 ) * step;
				var pts = new List<Vec3>( 6 );
				foreach ( var idx in new[] { a, b, c } )
				{
					var rel = clean[idx] - axisPoint;
					var along = Vec2.Dot( rel, dir );
					var radial = Vec2.Cross( dir, rel );
					foreach ( var t in new[] { t0, t1 } )
						pts.Add( origin3 + axis3 * along + ( perp3 * MathF.Cos( t ) + normal3 * MathF.Sin( t ) ) * radial );
				}
				brushes.Add( HullBrush( pts, materialName, "Revolve" ) );
			}
		}
		return brushes;
	}

	/// <summary>Revolve one sketch profile. A profile with holes is refused.</summary>
	public static IReadOnlyList<Effigy.MapBrush> RevolveProfile( Profile profile, SketchPlane plane,
		Vec2 axisPoint, Vec2 axisDirection, float angleDegrees = 360f, int segments = 24, string materialName = null )
	{
		if ( profile is null ) throw new ArgumentNullException( nameof( profile ) );
		if ( profile.HasHoles )
			throw new CadToMapException( ReasonProfileHasHoles,
				"Profile has a hole: a revolved frame is not one solid of revolution per segment. Build it from several brushes instead." );
		return RevolveLoop( profile.Outer, plane, axisPoint, axisDirection, angleDegrees, segments, materialName );
	}

	/// <summary>Revolve every closed region of a sketch about the same axis.</summary>
	public static IReadOnlyList<Effigy.MapBrush> RevolveSketch( Sketch sketch,
		Vec2 axisPoint, Vec2 axisDirection, float angleDegrees = 360f, int segments = 24, string materialName = null )
	{
		if ( sketch is null ) throw new ArgumentNullException( nameof( sketch ) );
		var profiles = ClosedProfiles( sketch );
		var brushes = new List<Effigy.MapBrush>();
		foreach ( var profile in profiles )
			brushes.AddRange( RevolveProfile( profile, sketch.Plane, axisPoint, axisDirection, angleDegrees, segments, materialName ) );
		return brushes;
	}

	// --- loft ------------------------------------------------------------------------

	/// <summary>Skin sections into prismoids. Every section must have the same point count
	/// and the same winding, with matching point order. Each triangle of section 0, paired
	/// with the same triangle in the next section, is one convex-hull brush; ruled loft
	/// between matching sections is exact. Caps are the end triangles of the first and
	/// last prismoids: no separate cap brushes are needed.</summary>
	public static IReadOnlyList<Effigy.MapBrush> LoftSections( IReadOnlyList<CadSection> sections, string materialName = null )
	{
		if ( sections is null ) throw new ArgumentNullException( nameof( sections ) );
		if ( sections.Count < 2 )
			throw new CadToMapException( ReasonTooFewSections,
				$"A loft needs at least two sections, got {sections.Count}." );

		var loops = new List<List<Vec2>>( sections.Count );
		var planes = new List<SketchPlane>( sections.Count );
		foreach ( var section in sections )
		{
			if ( section.Plane is null ) throw new ArgumentNullException( nameof( sections ), "A loft section has no plane." );
			// Winding is NOT normalised per section here: reversing one section would move
			// its corners relative to the others and twist the loft. All sections must
			// already wind the same way; if section 0 winds clockwise, ALL of them are
			// reversed together below, which preserves the correspondence.
			loops.Add( CleanLoop( section.Loop, normalizeWinding: false ) );
			planes.Add( section.Plane );
		}

		var count = loops[0].Count;
		for ( var i = 1; i < loops.Count; i++ )
			if ( loops[i].Count != count )
				throw new CadToMapException( ReasonSectionPointCountMismatch,
					$"Loft sections must all have the same point count: section 0 has {count}, section {i} has {loops[i].Count}. Resample the sections to the same count first." );

		var firstArea = SignedArea( loops[0] );
		for ( var i = 1; i < loops.Count; i++ )
			if ( MathF.Sign( SignedArea( loops[i] ) ) != MathF.Sign( firstArea ) )
				throw new CadToMapException( ReasonSectionWindingMismatch,
					$"Loft sections must all wind the same way: section {i} winds opposite to section 0. Redraw it with the same winding." );

		if ( firstArea < 0f )
			foreach ( var loop in loops )
				loop.Reverse();

		var brushes = new List<Effigy.MapBrush>();
		foreach ( var (a, b, c) in Triangulate( loops[0] ) )
		{
			for ( var s = 0; s < loops.Count - 1; s++ )
			{
				var pts = new List<Vec3>( 6 );
				foreach ( var idx in new[] { a, b, c } ) pts.Add( planes[s].ToWorld( loops[s][idx] ) );
				foreach ( var idx in new[] { a, b, c } ) pts.Add( planes[s + 1].ToWorld( loops[s + 1][idx] ) );
				brushes.Add( HullBrush( pts, materialName, "Loft" ) );
			}
		}
		return brushes;
	}

	/// <summary>Loft the first closed region of each sketch, in order. A section sketch
	/// with no closed region, or with a hole, is refused.</summary>
	public static IReadOnlyList<Effigy.MapBrush> LoftSketches( IReadOnlyList<Sketch> sketches, string materialName = null )
	{
		if ( sketches is null ) throw new ArgumentNullException( nameof( sketches ) );
		if ( sketches.Count < 2 )
			throw new CadToMapException( ReasonTooFewSections,
				$"A loft needs at least two section sketches, got {sketches.Count}." );
		var sections = new List<CadSection>( sketches.Count );
		for ( var i = 0; i < sketches.Count; i++ )
		{
			var sketch = sketches[i] ?? throw new ArgumentNullException( nameof( sketches ), $"Section sketch {i} is null." );
			var found = ProfileFinder.Find( sketch );
			var profile = found.Profiles.FirstOrDefault( p => !p.FromOverlap );
			if ( profile is null )
				throw new CadToMapException( ReasonNoSectionProfile,
					$"Loft section sketch {i} has no closed region in it. Close its curves into a loop." );
			if ( profile.HasHoles )
				throw new CadToMapException( ReasonProfileHasHoles,
					$"Loft section sketch {i} has a hole: a holed loft needs annular caps this conversion does not build." );
			sections.Add( new CadSection( profile.Outer, sketch.Plane ) );
		}
		return LoftSections( sections, materialName );
	}

	// --- sweep -----------------------------------------------------------------------

	/// <summary>Carry a closed loop along explicit path stations (model space), pure
	/// translation: the profile at station i is its sketch-plane shape translated by
	/// (station[i] - station[0]). One convex-hull brush per path segment for a convex
	/// profile, per triangle per segment for a concave one. No banking, twist or taper:
	/// a swept profile that needs a frame is out of scope, like the loft.</summary>
	public static IReadOnlyList<Effigy.MapBrush> SweepLoop( IReadOnlyList<Vec2> loop, SketchPlane plane,
		IReadOnlyList<Vec3> path, string materialName = null )
	{
		if ( plane is null ) throw new ArgumentNullException( nameof( plane ) );
		if ( path is null ) throw new ArgumentNullException( nameof( path ) );
		if ( path.Count < 2 )
			throw new CadToMapException( ReasonDegeneratePath,
				$"A sweep path needs at least two stations, got {path.Count}." );
		foreach ( var station in path )
			if ( !float.IsFinite( station.x ) || !float.IsFinite( station.y ) || !float.IsFinite( station.z ) )
				throw new CadToMapException( ReasonDegeneratePath, "A sweep path station is not finite." );
		for ( var i = 1; i < path.Count; i++ )
			if ( ( path[i] - path[i - 1] ).LengthSquared <= 1e-12f )
				throw new CadToMapException( ReasonDegeneratePath,
					$"Sweep path stations {i - 1} and {i} coincide: a zero-length segment sweeps nothing." );

		var clean = CleanLoop( loop );
		var start = path[0];
		var basePoints = clean.Select( p => plane.ToWorld( p ) ).ToList();
		var brushes = new List<Effigy.MapBrush>();

		void SweepTriangles( IReadOnlyList<Vec2> triangle )
		{
			var tri3 = triangle.Select( p => plane.ToWorld( p ) ).ToList();
			for ( var i = 0; i < path.Count - 1; i++ )
			{
				var pts = new List<Vec3>( 6 );
				foreach ( var p in tri3 ) pts.Add( p + ( path[i] - start ) );
				foreach ( var p in tri3 ) pts.Add( p + ( path[i + 1] - start ) );
				brushes.Add( HullBrush( pts, materialName, "Sweep" ) );
			}
		}

		if ( IsConvex( clean ) )
		{
			for ( var i = 0; i < path.Count - 1; i++ )
			{
				var pts = new List<Vec3>( basePoints.Count * 2 );
				foreach ( var p in basePoints ) pts.Add( p + ( path[i] - start ) );
				foreach ( var p in basePoints ) pts.Add( p + ( path[i + 1] - start ) );
				brushes.Add( HullBrush( pts, materialName, "Sweep" ) );
			}
			return brushes;
		}

		foreach ( var (a, b, c) in Triangulate( clean ) )
			SweepTriangles( new[] { clean[a], clean[b], clean[c] } );
		return brushes;
	}

	// --- dispatch --------------------------------------------------------------------

	/// <summary>Convert a sketch by a <see cref="CadConversion"/>. Loft reads
	/// <see cref="CadConversion.SectionSketches"/> (the sketch argument is ignored for
	/// Loft); Sweep reads <see cref="CadConversion.Path"/>; the rest read the sketch's
	/// own profiles.</summary>
	public static IReadOnlyList<Effigy.MapBrush> ConvertSketch( Sketch sketch, CadConversion conversion )
	{
		if ( sketch is null ) throw new ArgumentNullException( nameof( sketch ) );
		if ( conversion is null ) throw new ArgumentNullException( nameof( conversion ) );
		switch ( conversion.Kind )
		{
			case CadConversionKind.Extrude:
				return ExtrudeSketch( sketch, conversion.Distance, conversion.MaterialName,
					conversion.StartOffset, conversion.SecondDistance, conversion.Symmetric, conversion.Flip );
			case CadConversionKind.Revolve:
				return RevolveSketch( sketch, conversion.AxisPoint, conversion.AxisDirection,
					conversion.AngleDegrees, conversion.Segments, conversion.MaterialName );
			case CadConversionKind.Loft:
				return LoftSketches( conversion.SectionSketches, conversion.MaterialName );
			case CadConversionKind.Sweep:
				return SweepSketch( sketch, conversion.Path, conversion.MaterialName );
			default:
				throw new CadToMapException( ReasonInvalidValue, $"Unknown conversion kind {(int)conversion.Kind}." );
		}
	}

	/// <summary>Sweep every closed region of a sketch along explicit path stations.</summary>
	public static IReadOnlyList<Effigy.MapBrush> SweepSketch( Sketch sketch, IReadOnlyList<Vec3> path, string materialName = null )
	{
		if ( sketch is null ) throw new ArgumentNullException( nameof( sketch ) );
		var profiles = ClosedProfiles( sketch );
		var brushes = new List<Effigy.MapBrush>();
		foreach ( var profile in profiles )
			brushes.AddRange( SweepLoop( profile.Outer, sketch.Plane, path, materialName ) );
		return brushes;
	}

	// --- feature-tree recording ------------------------------------------------------

	/// <summary>Snapshot-record brushes in the tree: one <see cref="Effigy.MapBrushFeature"/>
	/// per brush, appended to the studio in order. The brushes are validated first: an
	/// invalid brush is refused here, not recorded to fail at rebuild. A snapshot does
	/// NOT follow sketch edits; for rebuild consistency use a
	/// <see cref="CadToMapFeature"/> instead. The caller (the future MapDocument) owns
	/// the map -> feature-id association: the returned features' Ids are the stable
	/// handles, persisted by StudioDocument.</summary>
	public static IReadOnlyList<Effigy.MapBrushFeature> RecordBrushes( PartStudio studio,
		IReadOnlyList<Effigy.MapBrush> brushes, string name )
	{
		if ( studio is null ) throw new ArgumentNullException( nameof( studio ) );
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Recorded brushes need a name.", nameof( name ) );
		foreach ( var brush in brushes )
		{
			if ( brush is null ) throw new ArgumentNullException( nameof( brushes ), "A brush to record is null." );
			var defects = brush.Validate();
			if ( defects.Count > 0 )
				throw new CadToMapException( ReasonInvalidBrush,
					$"Refusing to record an invalid brush: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
		}
		var recorded = new List<Effigy.MapBrushFeature>( brushes.Count );
		for ( var i = 0; i < brushes.Count; i++ )
		{
			var feature = new Effigy.MapBrushFeature();
			feature.Name = brushes.Count == 1 ? name : $"{name} {i + 1}";
			feature.SetBrush( brushes[i] );
			studio.Add( feature );
			recorded.Add( feature );
		}
		return recorded;
	}

	/// <summary>Bind a conversion feature to a map document, by the map's string id.
	/// Blank ids are refused: an unbound conversion keeps MapId empty instead.</summary>
	public static CadToMapFeature AttachToMap( CadToMapFeature feature, string mapId )
	{
		if ( feature is null ) throw new ArgumentNullException( nameof( feature ) );
		if ( string.IsNullOrWhiteSpace( mapId ) )
			throw new ArgumentException( "A map id must name a map document; blank binds nothing.", nameof( mapId ) );
		feature.MapId.Value = mapId.Trim();
		return feature;
	}

	/// <summary>Bind a conversion feature to a map document, by its DocumentId. This is
	/// the overload to use: the id comes from the document itself, so it cannot be a
	/// typo of the name.</summary>
	public static CadToMapFeature AttachToMap( CadToMapFeature feature, MapDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		return AttachToMap( feature, doc.DocumentId );
	}

	/// <summary>Publish converted brushes into a map document, by value: each brush
	/// becomes a MapBrushData through MapBrushData.FromMapBrush (planes and per-face
	/// material/UV preserved exactly) and is added by MapDocument.AddBrush, so the
	/// publish is a history entry the map can undo. Brushes are validated first, so a
	/// bad conversion publishes nothing rather than half a room. The returned data
	/// carry the map-side ids (MapDocument.NewId); the parametric source stays the
	/// CadToMapFeature in the tree. Re-publish after a sketch edit by removing the old
	/// brush ids and publishing again: the map document's own history makes that an
	/// undoable edit.</summary>
	public static IReadOnlyList<MapBrushData> PublishToMap( MapDocument doc,
		IReadOnlyList<Effigy.MapBrush> brushes, string name )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Published brushes need a name.", nameof( name ) );
		foreach ( var brush in brushes )
		{
			if ( brush is null ) throw new ArgumentNullException( nameof( brushes ), "A brush to publish is null." );
			var defects = brush.Validate();
			if ( defects.Count > 0 )
				throw new CadToMapException( ReasonInvalidBrush,
					$"Refusing to publish an invalid brush: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
		}
		var published = new List<MapBrushData>( brushes.Count );
		for ( var i = 0; i < brushes.Count; i++ )
		{
			var data = MapBrushData.FromMapBrush( brushes[i], MapDocument.NewId(),
				brushes.Count == 1 ? name : $"{name} {i + 1}" );
			doc.AddBrush( data );
			published.Add( data );
		}
		return published;
	}

	/// <summary>Conversions in the tree bound to one map id. Blank ids bind nothing, so
	/// they match nothing.</summary>
	public static IReadOnlyList<CadToMapFeature> FeaturesForMap( PartStudio studio, string mapId )
	{
		if ( studio is null ) throw new ArgumentNullException( nameof( studio ) );
		if ( string.IsNullOrWhiteSpace( mapId ) ) return Array.Empty<CadToMapFeature>();
		return studio.Features.OfType<CadToMapFeature>()
			.Where( f => string.Equals( f.MapId.Value, mapId, StringComparison.Ordinal ) )
			.ToList();
	}

	/// <summary>Snapshot brush features in the tree, for the map document to reference
	/// by feature Id.</summary>
	public static IReadOnlyList<Effigy.MapBrushFeature> MapBrushFeatures( PartStudio studio )
	{
		if ( studio is null ) throw new ArgumentNullException( nameof( studio ) );
		return studio.Features.OfType<Effigy.MapBrushFeature>().ToList();
	}

	// --- internals -------------------------------------------------------------------

	/// <summary>Closed, non-overlap regions of a sketch, or a refusal when there are none.</summary>
	static List<Profile> ClosedProfiles( Sketch sketch )
	{
		var found = ProfileFinder.Find( sketch );
		var profiles = found.Profiles.Where( p => !p.FromOverlap ).ToList();
		if ( profiles.Count == 0 )
			throw new CadToMapException( ReasonNoClosedProfile,
				"Sketch has no closed region to build from. Close its curves into a loop first." );
		return profiles;
	}

	/// <summary>One convex prism: side planes from the loop edges (outward by the centroid
	/// test, so winding does not matter) plus the two caps at lo/hi along the plane normal.</summary>
	static Effigy.MapBrush Prism( IReadOnlyList<Vec2> loop, SketchPlane plane, float lo, float hi, string materialName )
	{
		var normal = plane.Normal;
		var centroid = Centroid2( loop );
		var faces = new List<Effigy.MapBrushFace>( loop.Count + 2 );
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[( i + 1 ) % loop.Count];
			var e = b - a;
			var len = e.Length;
			var n2 = new Vec2( e.y / len, -e.x / len );
			var mid = ( a + b ) * 0.5f;
			if ( Vec2.Dot( n2, mid - centroid ) < 0f )
				n2 = new Vec2( -n2.x, -n2.y );
			var n3 = plane.XAxis * n2.x + plane.YAxis * n2.y;
			faces.Add( new Effigy.MapBrushFace( n3, Vec3.Dot( n3, plane.ToWorld( a ) ), materialName ) );
		}
		var nearPoint = plane.Origin + normal * lo;
		var farPoint = plane.Origin + normal * hi;
		faces.Add( new Effigy.MapBrushFace( -normal, -Vec3.Dot( normal, nearPoint ), materialName ) );
		faces.Add( new Effigy.MapBrushFace( normal, Vec3.Dot( normal, farPoint ), materialName ) );
		return RequireValid( Effigy.MapBrush.FromPlanes( faces ), "Extrude" );
	}

	/// <summary>One convex-hull brush from a point cloud, with the requested material on
	/// every face (blank = nodraw, the FromVertices default). A cloud with no volume is
	/// refused, not returned as an empty brush.</summary>
	static Effigy.MapBrush HullBrush( IReadOnlyList<Vec3> points, string materialName, string what )
	{
		var unique = new List<Vec3>( points.Count );
		foreach ( var p in points )
		{
			if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) || !float.IsFinite( p.z ) )
				throw new CadToMapException( ReasonInvalidProfile, $"{what}: a hull point is not finite." );
			var seen = false;
			foreach ( var q in unique )
				if ( ( q - p ).LengthSquared <= 1e-8f ) { seen = true; break; }
			if ( !seen ) unique.Add( p );
		}
		if ( unique.Count < 4 )
			throw new CadToMapException( ReasonDegenerateHull,
				$"{what}: only {unique.Count} distinct point(s); a hull brush needs at least 4." );
		if ( ConvexHull.Build( unique ) is null )
			throw new CadToMapException( ReasonDegenerateHull,
				$"{what}: the points enclose no volume (flat or collinear)." );
		var brush = Effigy.MapBrush.FromVertices( unique );
		// FromVertices leaves faces nodraw; re-wrap with the requested material. The
		// (normal, distance, name) constructor gives a blank name the nodraw default,
		// so this is a no-op for the blank case and exact otherwise.
		brush = Effigy.MapBrush.FromPlanes( brush.Faces
			.Select( f => new Effigy.MapBrushFace( f.Normal, f.Distance, materialName ) ) );
		return RequireValid( brush, what );
	}

	static Effigy.MapBrush RequireValid( Effigy.MapBrush brush, string what )
	{
		var defects = brush.Validate();
		if ( defects.Count > 0 )
			throw new CadToMapException( ReasonInvalidBrush,
				$"{what} produced an invalid brush: {string.Join( "; ", defects.Select( d => d.ToString() ) )}" );
		return brush;
	}

	/// <summary>Clean a loop for conversion: drop duplicate consecutive points and the
	/// closing duplicate, drop strictly-collinear middle points, refuse fewer than 3
	/// points / zero area / self-intersection. Normalises winding to counter-clockwise
	/// unless the caller keeps it (loft correspondence: see LoftSections).</summary>
	static List<Vec2> CleanLoop( IReadOnlyList<Vec2> loop, bool normalizeWinding = true )
	{
		if ( loop is null ) throw new ArgumentNullException( nameof( loop ) );
		var pts = new List<Vec2>( loop.Count );
		foreach ( var p in loop )
		{
			if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) )
				throw new CadToMapException( ReasonInvalidProfile, "Profile point is not finite." );
			if ( pts.Count > 0 && ( p - pts[pts.Count - 1] ).LengthSquared <= 1e-12f ) continue;
			pts.Add( p );
		}
		if ( pts.Count > 1 && ( pts[0] - pts[pts.Count - 1] ).LengthSquared <= 1e-12f )
			pts.RemoveAt( pts.Count - 1 );

		bool removed;
		do
		{
			removed = false;
			for ( var i = 0; i < pts.Count && pts.Count > 3; i++ )
			{
				var a = pts[( i + pts.Count - 1 ) % pts.Count];
				var b = pts[i];
				var c = pts[( i + 1 ) % pts.Count];
				var e1 = b - a;
				var e2 = c - b;
				if ( MathF.Abs( Vec2.Cross( e1, e2 ) ) <= 1e-9f && Vec2.Dot( e1, e2 ) > 0f )
				{
					pts.RemoveAt( i );
					removed = true;
					break;
				}
			}
		}
		while ( removed );

		if ( pts.Count < 3 )
			throw new CadToMapException( ReasonZeroAreaProfile,
				$"Profile has only {pts.Count} distinct point(s); a closed region needs at least 3." );
		var area = SignedArea( pts );
		if ( MathF.Abs( area ) <= 1e-9f )
			throw new CadToMapException( ReasonZeroAreaProfile, "Profile encloses no area." );
		if ( normalizeWinding && area < 0f )
			pts.Reverse();
		if ( !IsSimple( pts ) )
			throw new CadToMapException( ReasonSelfIntersectingProfile,
				"Profile loop crosses itself. A brush profile must be a simple polygon." );
		return pts;
	}

	static float SignedArea( IReadOnlyList<Vec2> loop )
	{
		var sum = 0f;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[( i + 1 ) % loop.Count];
			sum += a.x * b.y - b.x * a.y;
		}
		return sum * 0.5f;
	}

	static Vec2 Centroid2( IReadOnlyList<Vec2> loop )
	{
		var c = new Vec2( 0f, 0f );
		foreach ( var p in loop ) c += p;
		return c / loop.Count;
	}

	static bool IsConvex( IReadOnlyList<Vec2> loop )
	{
		var sign = 0f;
		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = loop[i];
			var b = loop[( i + 1 ) % loop.Count];
			var c = loop[( i + 2 ) % loop.Count];
			var cross = Vec2.Cross( b - a, c - b );
			if ( MathF.Abs( cross ) <= 1e-9f ) continue;
			var s = MathF.Sign( cross );
			if ( sign == 0f ) sign = s;
			else if ( s != sign ) return false;
		}
		return true;
	}

	/// <summary>Ear clipping on a counter-clockwise simple polygon. Returns index triples
	/// into the input loop, counter-clockwise.</summary>
	static List<(int A, int B, int C)> Triangulate( IReadOnlyList<Vec2> loop )
	{
		var remaining = Enumerable.Range( 0, loop.Count ).ToList();
		var tris = new List<(int, int, int)>();
		while ( remaining.Count > 3 )
		{
			var clipped = false;
			for ( var k = 0; k < remaining.Count; k++ )
			{
				var ia = remaining[( k + remaining.Count - 1 ) % remaining.Count];
				var ib = remaining[k];
				var ic = remaining[( k + 1 ) % remaining.Count];
				var a = loop[ia];
				var b = loop[ib];
				var c = loop[ic];
				if ( Vec2.Cross( b - a, c - b ) <= 1e-9f ) continue;
				var anyInside = false;
				foreach ( var j in remaining )
				{
					if ( j == ia || j == ib || j == ic ) continue;
					if ( PointInTriangleStrict( loop[j], a, b, c ) ) { anyInside = true; break; }
				}
				if ( anyInside ) continue;
				tris.Add( (ia, ib, ic) );
				remaining.RemoveAt( k );
				clipped = true;
				break;
			}
			if ( !clipped )
				throw new CadToMapException( ReasonSelfIntersectingProfile,
					"Profile could not be triangulated: no ear found. The loop is self-intersecting or degenerate." );
		}
		tris.Add( (remaining[0], remaining[1], remaining[2]) );
		return tris;
	}

	/// <summary>Strictly inside a counter-clockwise triangle (on an edge does not count:
	/// a vertex sitting on an ear's edge is not inside it).</summary>
	static bool PointInTriangleStrict( Vec2 p, Vec2 a, Vec2 b, Vec2 c )
	{
		const float eps = 1e-9f;
		return Vec2.Cross( b - a, p - a ) > eps
			&& Vec2.Cross( c - b, p - b ) > eps
			&& Vec2.Cross( a - c, p - c ) > eps;
	}

	static bool IsSimple( IReadOnlyList<Vec2> loop )
	{
		var n = loop.Count;
		for ( var i = 0; i < n; i++ )
		{
			var a1 = loop[i];
			var a2 = loop[( i + 1 ) % n];
			for ( var j = i + 1; j < n; j++ )
			{
				// Adjacent edges share a vertex by construction; the first and last
				// edges do too.
				if ( j == i + 1 ) continue;
				if ( i == 0 && j == n - 1 ) continue;
				if ( SegmentsIntersect( a1, a2, loop[j], loop[( j + 1 ) % n] ) ) return false;
			}
		}
		return true;
	}

	static bool SegmentsIntersect( Vec2 p1, Vec2 p2, Vec2 p3, Vec2 p4 )
	{
		var o1 = Orient( p1, p2, p3 );
		var o2 = Orient( p1, p2, p4 );
		var o3 = Orient( p3, p4, p1 );
		var o4 = Orient( p3, p4, p2 );
		if ( o1 * o2 < 0f && o3 * o4 < 0f ) return true;
		if ( o1 == 0f && OnSegment( p1, p3, p2 ) ) return true;
		if ( o2 == 0f && OnSegment( p1, p4, p2 ) ) return true;
		if ( o3 == 0f && OnSegment( p3, p1, p4 ) ) return true;
		if ( o4 == 0f && OnSegment( p3, p2, p4 ) ) return true;
		return false;
	}

	static float Orient( Vec2 a, Vec2 b, Vec2 c )
	{
		var cross = Vec2.Cross( b - a, c - a );
		if ( MathF.Abs( cross ) <= 1e-9f ) return 0f;
		return MathF.Sign( cross );
	}

	static bool OnSegment( Vec2 a, Vec2 b, Vec2 c ) =>
		b.x >= MathF.Min( a.x, c.x ) - 1e-9f && b.x <= MathF.Max( a.x, c.x ) + 1e-9f
		&& b.y >= MathF.Min( a.y, c.y ) - 1e-9f && b.y <= MathF.Max( a.y, c.y ) + 1e-9f;
}

/// <summary>A CAD conversion as a feature in the tree, so a MapBrush it produces survives
/// parametric rebuild: it holds the conversion parameters and the sketch it consumes,
/// and regenerates its brushes from the CURRENT sketch on every Execute. Editing the
/// sketch and rebuilding therefore rebuilds the brush; nothing here caches geometry.
///
/// Always emits NEW bodies, one per brush (named after the feature, numbered when
/// there are several). The inherited Result choice is intentionally unused: merging a
/// brush into a CAD part would destroy the brush's plane set, and a map brush is a
/// standalone solid. A suppressed feature emits nothing, like any other feature.
///
/// PERSISTENCE. Every public field is a StudioDocument-supported type (params, string,
/// List&lt;string&gt;, List&lt;Vec2&gt; inherited, List&lt;Vec3&gt;), so the feature
/// round-trips through .effigy with no writer change: StudioDocument.Create finds it
/// by class name in the kernel assembly. MapId is the map-document seam (see the file
/// header): blank means this conversion is studio-only.</summary>
[Feature( "CAD to Map", Category = "Map", Description = "CAD feature -> map brushes: extrude / revolve / loft / sweep a sketch profile into convex plane brushes, regenerated on every rebuild." )]
public sealed class CadToMapFeature : SketchConsumingFeature
{
	public override string TypeName => "CAD to Map";

	/// <summary>Conversion kind. Option order matches CadConversionKind: append, never reorder.</summary>
	public readonly ChoiceParam Conversion = new( "Conversion", new[] { "Extrude", "Revolve", "Loft", "Sweep" } );

	public readonly FloatParam Distance = new( "Distance", 4f, unit: "u" );
	public readonly FloatParam StartOffset = new( "Start offset", 0f, unit: "u" );
	public readonly FloatParam SecondDistance = new( "Second distance", 0f, 0f, unit: "u" );
	public readonly BoolParam Symmetric = new( "Symmetric", false );
	public readonly BoolParam Flip = new( "Flip direction", false );
	public readonly FloatParam Angle = new( "Angle", 360f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );
	public readonly Vec3Param AxisPoint = new( "Axis through (sketch coords)", Vec3.Zero );
	public readonly Vec3Param AxisDirection = new( "Axis direction (sketch coords)", new Vec3( 1, 0, 0 ) );

	/// <summary>Material name for every generated face. Blank = nodraw (the brush is still
	/// valid; its baked mesh drops the faces, as MapBrushFeature does).</summary>
	public readonly StringParam MaterialName = new( "Material name", "" );

	/// <summary>DocumentId of the map document this conversion feeds (see the file
	/// header for the MapDocument seam). Blank = studio-only, bound to no map.</summary>
	public readonly StringParam MapId = new( "Map id", "" );

	/// <summary>Loft section sketch feature ids, in order. Empty = every sketch available
	/// at this point in the tree, as LoftFeature does.</summary>
	public List<string> Sections = new();

	/// <summary>Sweep path stations, in model space. A future ticket may fill these from a
	/// path sketch; this ticket takes them as numbers so the sweep is testable now.</summary>
	public List<Vec3> Path = new();

	public override IReadOnlyList<IParam> Parameters
	{
		get
		{
			var list = new List<IParam> { Sketch, Conversion, MaterialName, MapId };
			switch ( (CadConversionKind)Conversion.Index )
			{
				case CadConversionKind.Extrude:
					list.AddRange( new IParam[] { Distance, StartOffset, SecondDistance, Symmetric, Flip } );
					break;
				case CadConversionKind.Revolve:
					list.AddRange( new IParam[] { AxisPoint, AxisDirection, Angle, Segments } );
					break;
				case CadConversionKind.Loft:
				case CadConversionKind.Sweep:
					break;
			}
			return list;
		}
	}

	protected override void Execute( FeatureContext ctx )
	{
		IReadOnlyList<Effigy.MapBrush> brushes = Array.Empty<Effigy.MapBrush>();
		try
		{
			brushes = ConvertFor( ctx );
		}
		catch ( CadToMapException ex )
		{
			// FailOn always throws (FeatureException); the empty list above only keeps the
			// compiler's definite-assignment analysis happy.
			FailOn( "Conversion", ex.Message,
				$"CAD to Map refused this conversion ({ex.Reason}).",
				"Change the sketch or the conversion settings",
				"Delete the conversion feature" );
		}

		foreach ( var brush in brushes )
		{
			var defects = brush.Validate();
			if ( defects.Count > 0 )
				FailOn( "Conversion", "CAD conversion produced an invalid brush",
					string.Join( "; ", defects.Select( d => d.ToString() ) ),
					"Change the sketch or the conversion settings",
					"Delete the conversion feature" );
		}

		for ( var i = 0; i < brushes.Count; i++ )
		{
			var name = brushes.Count == 1 ? Name : $"{Name} [{i + 1}]";
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), name, brushes[i].ToMesh( _ => 0 ) ) );
		}
		Info( $"CAD to Map ({Conversion.Value}): {brushes.Count} brush(es), {brushes.Sum( b => b.Volume ):0.###} cu in" );
	}

	IReadOnlyList<Effigy.MapBrush> ConvertFor( FeatureContext ctx )
	{
		var conversion = new CadConversion
		{
			Kind = (CadConversionKind)Conversion.Index,
			Distance = Distance.Value,
			StartOffset = StartOffset.Value,
			SecondDistance = SecondDistance.Value,
			Symmetric = Symmetric.Value,
			Flip = Flip.Value,
			AngleDegrees = Angle.Value,
			Segments = Segments.Clamped,
			AxisPoint = new Vec2( AxisPoint.Value.x, AxisPoint.Value.y ),
			AxisDirection = new Vec2( AxisDirection.Value.x, AxisDirection.Value.y ),
			MaterialName = MaterialName.Value,
		};
		conversion.Path.AddRange( Path );

		if ( conversion.Kind == CadConversionKind.Loft )
			return CadToMap.LoftSketches( ResolveSectionSketches( ctx ), conversion.MaterialName );

		var sketch = ResolveSketch( ctx );
		return CadToMap.ConvertSketch( sketch, conversion );
	}

	List<Sketch> ResolveSectionSketches( FeatureContext ctx )
	{
		if ( Sections.Count == 0 )
			return ctx.Sketches.Values.ToList();

		var sketches = new List<Sketch>( Sections.Count );
		foreach ( var id in Sections )
		{
			if ( !ctx.Sketches.TryGetValue( id, out var sketch ) )
				Fail(
					$"Loft section sketch '{id}' is not available at this point in the tree",
					"The sketch has been deleted, suppressed, or sits below this feature, so it has not run yet.",
					"Pick a sketch that sits above this feature",
					"Move this feature below the section sketches" );
			sketches.Add( sketch );
		}
		return sketches;
	}
}
