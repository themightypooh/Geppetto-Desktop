// ============================================================================
// Hammer Desktop P1: cordon / region compile + compile-visible-layers only.
// Board task hammer-compile-cordon-region.
//
// A cordon box restricts a compile to its interior with an automatic
// temporary seal; region compile is a cordon built from the current selection
// or a named layer/visgroup; compile-visible-only filters brushes by the
// layers flag. All as data + runner options, no UI.
//
// SOURCE: Report §1.2 #21 + §3.4 #3 — cordon: only the inside is shown /
// compiled, sealed by a temporary box; used for fast test compiles of one
// room and for bisecting a broken map; generalised to 'Compile selection /
// visible layers / this room' as presets.
//
// FILE PLACEMENT (2026-11-13): the native-pipeline ticket left a seam saying
// Cordon + ApplyCordon belong IN CompileRunner.cs, hooked into PrepareInput.
// That file is currently LOCKED on the Hub by hammer-compile-parallel-stages
// (daxter-hammer-compile-parallel), so this ticket implements the whole
// feature in this NEW file instead of colliding with that worker: Cordon,
// ApplyCordon, region presets and BisectLeak live here and work through
// CompileRunner's PUBLIC API (CompileInput / CompileBrush / CompileRunner.Run)
// plus its internal polygon builder (same assembly). Nothing here edits
// CompileRunner.cs, MapDocument.cs or any other worker's file. Callers use
// it as: var sub = CompileCordon.ApplyCordon( input, cordon );
// var result = CompileRunner.Run( sub, preset, options );
//
// DEFERRED INTEGRATOR PATCH (apply once CompileRunner.cs is free — the final
// integrator owns shared entry points; exact patch, no design left open):
//   1. In CompileOptions add:
//        public List<Cordon> Cordons { get; } = new();
//        public bool CompileVisibleOnly { get; set; }
//        public List<string> VisibleLayers { get; } = new();
//   2. In CompileBrush add:
//        // Layer id (MapBrushData.LayerId). The FromDocument adapter copies
//        // it from MapDocument; "" = unlayered, always compile-visible.
//        public string Layer { get; set; } = "";
//   3. In CompileRunner.PrepareInput, before validation:
//        if ( _options.CompileVisibleOnly )
//            input = CompileCordon.ApplyVisibleOnly( input, _options.VisibleLayers, b => b.Layer );
//        if ( _options.Cordons.Count > 0 )
//            input = CompileCordon.ApplyCordon( input, _options.Cordons );
//      (log the CordonReport counts the same way Run logs stage lines.)
//   4. Register the test group: CompileCordonTests.Register( registry, settings );
//      in tests/TestRegistry.cs (same convention as CompileRunnerTests, which
//      is also unregistered pending the integrator).
//
// DEPENDENCY STATUS (2026-11-13, verified against the live tree):
//   * hammer-compile-native-pipeline is DONE: CompileRunner / CompileInput /
//     CompileBrush / LeakFinder are real and reused here through their real
//     APIs. No stub types are referenced anywhere in this file.
//   * MapLayer / MapVisGroup HAVE landed in MapDocument.cs with CompileVisible
//     flags, and MapBrushData carries LayerId ("", = no layer) + VisGroupIds.
//     CompileInput.FromDocument(MapDocument) HAS also landed: it builds the
//     compile input from document.CompileVisibleBrushes() /
//     CompileVisibleEntities() — the visible-only filter at the document
//     level. It does NOT copy LayerId into CompileBrush (no Layer tag exists
//     yet), so this file still does NOT depend on MapDocument: layer identity
//     is supplied by the caller as a Func<CompileBrush, string> lookup
//     (ApplyVisibleOnly / FromLayer). When the integrator adds
//     CompileBrush.Layer (patch step 2), FromDocument should copy
//     brush.LayerId into it, and callers pass b => b.Layer. The visible-layer
//     set itself is the layers ticket's job: the ids whose MapLayer/MapVisGroup
//     CompileVisible is true. This file only consumes that set.
//   * VmfIo round-trips a VMF cordon section (HasCordon / CordonMins /
//     CordonMaxs / CordonActive). A VmfDocument -> Cordon mapping is a
//     future VMF-ticket seam, not built here.
//   * No UI. No external processes. Native only.
//
// HOW THE SEAL WORKS (read this before changing ApplyCordon):
//   A kept brush that straddles a cordon boundary is CLIPPED to the cordon by
//   adding the cordon faces it crosses as extra planes (a convex brush is an
//   intersection of half-spaces, so clipping is plane addition; the new faces
//   carry the seal material) and REMOVING the original faces that lie
//   entirely beyond a cut — their plane no longer touches the solid, so
//   keeping one would build a degenerate polygon and fail validation. A cut
//   face is added ONLY where the brush extends beyond that cordon face by
//   more than Eps — a redundant face would build a degenerate polygon and
//   fail validation, and a coincident face would be a duplicate plane. If
//   the clipped brush ever fails Validate(), the brush is kept WHOLE
//   instead: the seal boxes below still seal the interior, so correctness
//   is preserved and no invalid brush is ever produced.
//   A brush is KEPT when it has interior volume inside the cordon (its
//   overlap with the cordon is longer than Eps on every axis). A brush
//   merely touching the boundary from outside — e.g. a seal box from a
//   previous pass, flush against the face — has zero overlap and is dropped,
//   which is what makes ApplyCordon idempotent.
//   Clipping alone does NOT seal the map: a cordon boundary can cut through
//   EMPTY space (e.g. the doorway of a two-room map), and the void beyond
//   would leak in. So a cordon face gets ONE temporary seal box, sitting just
//   OUTSIDE the cordon (thickness SealThickness, extended past the corners
//   so the shell has no corner gaps), exactly when the cordon CUTS geometry
//   there: some brush straddles that face, or some brush lies beyond it
//   (was cut off by the cordon). A face where the boundary coincides with
//   the hull — no brush straddles it, none lies beyond it — gets NO seal:
//   the hull itself seals that face, and a real gap in that hull stays a
//   real leak (it opens to the void outside the hull). That is what makes
//   bisect work: the leaking half is the one whose hull gap faces an
//   unsealed cordon face.
//   KNOWN LIMIT: a seal covers its whole face, so a real gap in a wall that
//   the cordon boundary itself cuts through (the boundary runs through the
//   middle of a gapped wall) is sealed over — move the boundary off the wall
//   to bisect that gap.
//   Multiple cordons are a UNION: a brush is kept when it overlaps ANY
//   cordon. A brush overlapping exactly one cordon is clipped to it; a
//   brush overlapping two or more is kept whole (clipping to a union is not
//   a convex operation, and a straddling solid plugs its own crossing). A
//   face's seal box is skipped when it would land inside another cordon
//   (a union that covers the whole map seals itself; a seal inside the
//   compiled region would be a phantom wall).
//   Seal brushes are synthetic: negative ids, names cordon_seal_*,
//   material SealMaterial. They exist only in the sub-document, never in
//   the source.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Which axis a cordon bisects along.</summary>
public enum CordonAxis
{
	X,
	Y,
	Z,
}

/// <summary>Which half of a bisected cordon leaked.</summary>
public enum CordonHalf
{
	/// <summary>Neither half leaked (or neither half could be leak-checked).</summary>
	None,
	First,
	Second,
	Both,
}

/// <summary>
/// An axis-aligned cordon box: only the inside is shown / compiled.
//  Min must be &lt;= Max on every axis and the box must have positive volume;
//  both are enforced by the constructor (a bad box is a caller error, named
//  by the exception, never silently fixed). Units are the kernel's inches,
//  Z-up. A cordon is immutable value data: it can be stored in
//  CompileOptions, serialised, or passed to ApplyCordon as-is.
// </summary>
public readonly struct Cordon : IEquatable<Cordon>
{
	/// <summary>Material for the temporary seal faces and seal brushes. Not a real Source tool texture (Hammer's cordon has none): it marks what the cordon added so the .bsp / .lightmap output shows it, and a future VMF exporter must drop seal brushes instead of writing them.</summary>
	public const string SealMaterial = "tools/cordon_seal";

	/// <summary>Seal-box thickness in inches (one wall thickness in the compile fixtures). Seal boxes also extend this far past the cordon on their tangential axes so the shell has no corner gaps.</summary>
	public const float SealThickness = 16f;

	public Vec3 Min { get; }
	public Vec3 Max { get; }

	public Cordon( Vec3 min, Vec3 max )
	{
		if ( min.x > max.x ) throw new ArgumentException( $"Cordon min.x {min.x} is greater than max.x {max.x}: Min must be <= Max on every axis.", nameof( min ) );
		if ( min.y > max.y ) throw new ArgumentException( $"Cordon min.y {min.y} is greater than max.y {max.y}: Min must be <= Max on every axis.", nameof( min ) );
		if ( min.z > max.z ) throw new ArgumentException( $"Cordon min.z {min.z} is greater than max.z {max.z}: Min must be <= Max on every axis.", nameof( min ) );
		if ( min.x == max.x ) throw new ArgumentException( "Cordon has zero volume: min.x equals max.x.", nameof( min ) );
		if ( min.y == max.y ) throw new ArgumentException( "Cordon has zero volume: min.y equals max.y.", nameof( min ) );
		if ( min.z == max.z ) throw new ArgumentException( "Cordon has zero volume: min.z equals max.z.", nameof( min ) );
		Min = min;
		Max = max;
	}

	/// <summary>Centre of the box.</summary>
	public Vec3 Center => ( Min + Max ) * 0.5f;

	/// <summary>Size of the box per axis.</summary>
	public Vec3 Size => Max - Min;

	/// <summary>Longest axis of the box (ties break X, then Y).</summary>
	public CordonAxis LongestAxis
	{
		get
		{
			var size = Size;
			if ( size.x >= size.y && size.x >= size.z ) return CordonAxis.X;
			if ( size.y >= size.z ) return CordonAxis.Y;
			return CordonAxis.Z;
		}
	}

	/// <summary>True when the point is inside the box, boundary included.</summary>
	public bool Contains( Vec3 p ) =>
		p.x >= Min.x && p.x <= Max.x &&
		p.y >= Min.y && p.y <= Max.y &&
		p.z >= Min.z && p.z <= Max.z;

	/// <summary>True when the axis-aligned box [min, max] overlaps this cordon at all, boundary contact included.</summary>
	public bool Intersects( Vec3 min, Vec3 max ) =>
		min.x <= Max.x && max.x >= Min.x &&
		min.y <= Max.y && max.y >= Min.y &&
		min.z <= Max.z && max.z >= Min.z;

	/// <summary>Cordon grown by <paramref name="margin"/> on every side (margin 0 = unchanged). A negative margin shrinks; it throws when the result would have zero or negative volume.</summary>
	public Cordon Expanded( float margin )
	{
		if ( margin < 0f && ( Max.x - Min.x <= 2f * -margin || Max.y - Min.y <= 2f * -margin || Max.z - Min.z <= 2f * -margin ) )
			throw new ArgumentException( $"Cordon margin {margin} would shrink the box to zero or negative volume.", nameof( margin ) );
		return new Cordon(
			new Vec3( Min.x - margin, Min.y - margin, Min.z - margin ),
			new Vec3( Max.x + margin, Max.y + margin, Max.z + margin ) );
	}

	/// <summary>Split the cordon into two halves along <paramref name="axis"/> at the midpoint. The halves share the split face. Throws when the cordon is too thin on that axis to split.</summary>
	public (Cordon First, Cordon Second) Bisect( CordonAxis axis )
	{
		var min = Min;
		var max = Max;
		float mid;
		Vec3 firstMax, secondMin;
		switch ( axis )
		{
			case CordonAxis.X:
				mid = ( min.x + max.x ) * 0.5f;
				if ( !( mid > min.x && mid < max.x ) ) throw new ArgumentException( "Cordon is too thin on X to bisect.", nameof( axis ) );
				firstMax = new Vec3( mid, max.y, max.z );
				secondMin = new Vec3( mid, min.y, min.z );
				break;
			case CordonAxis.Y:
				mid = ( min.y + max.y ) * 0.5f;
				if ( !( mid > min.y && mid < max.y ) ) throw new ArgumentException( "Cordon is too thin on Y to bisect.", nameof( axis ) );
				firstMax = new Vec3( max.x, mid, max.z );
				secondMin = new Vec3( min.x, mid, min.z );
				break;
			default:
				mid = ( min.z + max.z ) * 0.5f;
				if ( !( mid > min.z && mid < max.z ) ) throw new ArgumentException( "Cordon is too thin on Z to bisect.", nameof( axis ) );
				firstMax = new Vec3( max.x, max.y, mid );
				secondMin = new Vec3( min.x, min.y, mid );
				break;
		}
		return ( new Cordon( min, firstMax ), new Cordon( secondMin, max ) );
	}

	/// <summary>Cordon around a set of points, grown by <paramref name="margin"/> (default 0). Throws when there are no points.</summary>
	public static Cordon FromBounds( IEnumerable<Vec3> points, float margin = 0f )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		if ( margin < 0f ) throw new ArgumentException( $"Cordon margin {margin} must be >= 0.", nameof( margin ) );
		var first = true;
		var min = Vec3.Zero;
		var max = Vec3.Zero;
		foreach ( var p in points )
		{
			if ( first )
			{
				min = p;
				max = p;
				first = false;
			}
			else
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
		}
		if ( first ) throw new ArgumentException( "Cannot build a cordon from no points.", nameof( points ) );
		return new Cordon( min, max ).Expanded( margin );
	}

	/// <summary>Cordon around a set of brushes (union of their bounds), grown by <paramref name="margin"/> (default 0). Brushes with no usable bounds (degenerate) are skipped; throws when no brush has usable bounds.</summary>
	public static Cordon FromBrushes( IEnumerable<CompileBrush> brushes, float margin = 0f )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		if ( margin < 0f ) throw new ArgumentException( $"Cordon margin {margin} must be >= 0.", nameof( margin ) );
		var bounds = new List<(Vec3 Min, Vec3 Max)>();
		foreach ( var brush in brushes )
		{
			if ( brush is null ) continue;
			var b = CompileCordon.BrushBounds( brush );
			if ( b is not null ) bounds.Add( b.Value );
		}
		if ( bounds.Count == 0 ) throw new ArgumentException( "Cannot build a cordon: no brush has usable bounds.", nameof( brushes ) );
		var min = bounds[0].Min;
		var max = bounds[0].Max;
		foreach ( var b in bounds )
		{
			min = new Vec3( MathF.Min( min.x, b.Min.x ), MathF.Min( min.y, b.Min.y ), MathF.Min( min.z, b.Min.z ) );
			max = new Vec3( MathF.Max( max.x, b.Max.x ), MathF.Max( max.y, b.Max.y ), MathF.Max( max.z, b.Max.z ) );
		}
		return new Cordon( min, max ).Expanded( margin );
	}

	/// <summary>
	/// Region preset: cordon around the brushes in <paramref name="brushIds"/>
	/// ('Compile selection' — the selection is brush ids, the data a caller or
	/// AI tool already has; there is no selection UI in this ticket). Grown by
	/// <paramref name="margin"/> (default 16, one wall thickness, so the seal
	/// sits clear of the selected geometry). Throws naming any unknown id, an
	/// empty selection, or a selection with no usable bounds.
	/// </summary>
	public static Cordon FromSelection( CompileInput input, IEnumerable<int> brushIds, float margin = 16f )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( brushIds is null ) throw new ArgumentNullException( nameof( brushIds ) );
		if ( margin < 0f ) throw new ArgumentException( $"Cordon margin {margin} must be >= 0.", nameof( margin ) );
		var ids = brushIds.ToList();
		if ( ids.Count == 0 ) throw new ArgumentException( "Cannot build a cordon from an empty selection.", nameof( brushIds ) );
		var byId = input.Brushes.Where( b => b is not null ).ToDictionary( b => b.Id, b => b );
		var missing = ids.Where( id => !byId.ContainsKey( id ) ).ToList();
		if ( missing.Count > 0 ) throw new ArgumentException( $"Selection names unknown brush ids: {string.Join( ", ", missing )}.", nameof( brushIds ) );
		return FromBrushes( ids.Select( id => byId[id] ), margin );
	}

	/// <summary>
	/// Region preset: cordon around every brush in layer <paramref name="layerId"/>
	/// ('Compile this layer / visgroup / this room' — a room is the layer or
	/// visgroup its brushes belong to). Layer identity comes from
	/// <paramref name="layerOf"/> because CompileBrush carries no layer tag
	/// yet (see the file header: the FromDocument adapter copies
	/// MapBrushData.LayerId into CompileBrush.Layer and then passes
	/// <c>b =&gt; b.Layer</c>). Grown by <paramref name="margin"/> (default 16).
	/// Throws when no brush is in that layer.
	/// </summary>
	public static Cordon FromLayer( CompileInput input, string layerId, Func<CompileBrush, string> layerOf, float margin = 16f )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( string.IsNullOrEmpty( layerId ) ) throw new ArgumentException( "Layer id must not be empty.", nameof( layerId ) );
		if ( layerOf is null ) throw new ArgumentNullException( nameof( layerOf ) );
		if ( margin < 0f ) throw new ArgumentException( $"Cordon margin {margin} must be >= 0.", nameof( margin ) );
		var inLayer = input.Brushes.Where( b => b is not null && string.Equals( layerOf( b ), layerId, StringComparison.Ordinal ) ).ToList();
		if ( inLayer.Count == 0 ) throw new ArgumentException( $"No brush is in layer '{layerId}'.", nameof( layerId ) );
		return FromBrushes( inLayer, margin );
	}

	public bool Equals( Cordon other ) => Min.Equals( other.Min ) && Max.Equals( other.Max );
	public override bool Equals( object obj ) => obj is Cordon other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Min, Max );
	public static bool operator ==( Cordon a, Cordon b ) => a.Equals( b );
	public static bool operator !=( Cordon a, Cordon b ) => !a.Equals( b );
	public override string ToString() => $"Cordon({Min}..{Max})";
}

/// <summary>What ApplyCordon did, so the runner log and tests can report it without re-deriving it from the brush lists.</summary>
public sealed class CordonReport
{
	/// <summary>Brushes kept (excluding seal brushes).</summary>
	public int KeptBrushes { get; set; }
	/// <summary>Brushes dropped (entirely outside every cordon).</summary>
	public int DroppedBrushes { get; set; }
	/// <summary>Names of the dropped brushes, in input order.</summary>
	public List<string> DroppedBrushNames { get; } = new();
	/// <summary>Kept brushes clipped to a cordon boundary.</summary>
	public int ClippedBrushes { get; set; }
	/// <summary>Names of the clipped brushes, in input order.</summary>
	public List<string> ClippedBrushNames { get; } = new();
	/// <summary>Temporary seal brushes added.</summary>
	public int SealBrushes { get; set; }
	/// <summary>Lights kept (inside a cordon).</summary>
	public int LightsKept { get; set; }
	/// <summary>Lights dropped (outside every cordon).</summary>
	public int LightsDropped { get; set; }
	/// <summary>True when the player start was inside a cordon and kept. False means it was outside (or absent) and the sub-document has no leak-check origin: the runner warns missing-player-start and skips the leak check.</summary>
	public bool PlayerStartKept { get; set; }
	/// <summary>The cordons that were applied (deduplicated, in caller order).</summary>
	public List<Cordon> Cordons { get; } = new();
}

/// <summary>Result of bisecting a leaking map: both half-compiles plus which half leaked.</summary>
public sealed class CordonBisectResult
{
	/// <summary>None = neither half leaked; Both = the leak spans the split (or both halves leak independently).</summary>
	public CordonHalf LeakingHalf { get; set; }
	public Cordon FirstCordon { get; set; }
	public Cordon SecondCordon { get; set; }
	/// <summary>QuickPlay compile of the first half (BSP + leak check, no outputs).</summary>
	public CompileResult First { get; set; }
	/// <summary>QuickPlay compile of the second half (BSP + leak check, no outputs).</summary>
	public CompileResult Second { get; set; }
}

/// <summary>
/// Cordon / region compile for the native Geppetto compile pipeline.
/// Pure functions over CompileInput: the source input is never mutated, and
/// the returned sub-document is a fresh CompileInput the caller hands to
/// CompileRunner.Run. See the file header for the seal design and the
/// deferred CompileRunner.PrepareInput wiring.
/// </summary>
public static class CompileCordon
{
	// Brush-vs-cordon classification tolerance (inches). Matches the runner's
	// plane tolerance: a brush within this of the boundary counts as touching
	// it, never as straddling it by a sliver that would clip a zero-width cut.
	const float Eps = 0.01f;

	/// <summary>Axis-aligned bounds of a brush from its face polygons, or null when the brush builds no polygons (degenerate: the runner's validation names it downstream, so ApplyCordon keeps it as-is instead of dropping it).</summary>
	internal static (Vec3 Min, Vec3 Max)? BrushBounds( CompileBrush brush )
	{
		var polygons = CompileRunner.BuildBrushPolygons( brush, out _ );
		if ( polygons.Count == 0 ) return null;
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		var any = false;
		foreach ( var polygon in polygons )
		{
			foreach ( var v in polygon.Vertices )
			{
				min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
				max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
				any = true;
			}
		}
		return any ? (min, max) : null;
	}

	/// <summary>Apply one cordon box to a compile input, producing a sealed sub-document. See <see cref="ApplyCordon(CompileInput, IEnumerable{Cordon}, out CordonReport)"/>.</summary>
	public static CompileInput ApplyCordon( CompileInput input, Cordon cordon ) =>
		ApplyCordon( input, new[] { cordon }, out _ );

	/// <summary>Apply one cordon box to a compile input, producing a sealed sub-document, with a report of what was kept, dropped, clipped and sealed.</summary>
	public static CompileInput ApplyCordon( CompileInput input, Cordon cordon, out CordonReport report ) =>
		ApplyCordon( input, new[] { cordon }, out report );

	/// <summary>Apply one or more cordon boxes (union) to a compile input, producing a sealed sub-document.</summary>
	public static CompileInput ApplyCordon( CompileInput input, IEnumerable<Cordon> cordons ) =>
		ApplyCordon( input, cordons, out _ );

	/// <summary>
	/// Apply cordon boxes to a compile input, producing a sealed sub-document.
	/// Keeps brushes with interior volume inside any cordon (clipped to the
	/// one cordon they overlap, when they overlap exactly one), drops the
	/// rest, drops lights outside every cordon, clears the player start when
	/// it is outside every cordon, and adds one temporary seal brush per
	/// cordon face where the cordon cuts or drops geometry (see the header
	/// note). The input is never mutated; the output name is the input name
	/// with " (cordon)" appended once. An empty cordon list is a no-op
	/// returning the input unchanged.
	/// </summary>
	public static CompileInput ApplyCordon( CompileInput input, IEnumerable<Cordon> cordons, out CordonReport report )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( cordons is null ) throw new ArgumentNullException( nameof( cordons ) );
		report = new CordonReport();
		var boxes = cordons.Distinct().ToList();
		if ( boxes.Count == 0 ) return input;
		report.Cordons.AddRange( boxes );

		var output = new CompileInput( input.Name.EndsWith( " (cordon)", StringComparison.Ordinal ) ? input.Name : input.Name + " (cordon)" );
		// sealFace[i, f]: cordon i needs a seal box on face f
		// (0 east +x, 1 west -x, 2 north +y, 3 south -y, 4 top +z, 5 bottom -z).
		var sealFace = new bool[boxes.Count, 6];

		foreach ( var brush in input.Brushes )
		{
			if ( brush is null )
			{
				// Preserved so the runner's validation names it (invalid-brush),
				// never silently dropped by the cordon.
				output.Brushes.Add( null );
				continue;
			}
			var bounds = BrushBounds( brush );
			if ( bounds is null )
			{
				// Degenerate: no meaningful bounds to test. Kept as-is; the
				// runner's validation names it downstream.
				output.Brushes.Add( brush );
				report.KeptBrushes++;
				continue;
			}
			var (bMin, bMax) = bounds.Value;
			var hits = new List<int>();
			for ( var i = 0; i < boxes.Count; i++ )
			{
				var c = boxes[i];
				var overlaps = Overlaps( c, bMin, bMax );
				if ( overlaps ) hits.Add( i );
				for ( var f = 0; f < 6; f++ )
					if ( SealFaceNeeded( c, f, bMin, bMax, overlaps ) ) sealFace[i, f] = true;
			}
			if ( hits.Count == 0 )
			{
				report.DroppedBrushes++;
				report.DroppedBrushNames.Add( brush.Name );
				continue;
			}
			if ( hits.Count == 1 )
			{
				var cordon = boxes[hits[0]];
				var straddles = bMin.x < cordon.Min.x - Eps || bMax.x > cordon.Max.x + Eps
					|| bMin.y < cordon.Min.y - Eps || bMax.y > cordon.Max.y + Eps
					|| bMin.z < cordon.Min.z - Eps || bMax.z > cordon.Max.z + Eps;
				var clipped = straddles ? ClipToCordon( brush, cordon, bMin, bMax ) : null;
				if ( clipped is not null )
				{
					output.Brushes.Add( clipped );
					report.KeptBrushes++;
					report.ClippedBrushes++;
					report.ClippedBrushNames.Add( brush.Name );
					continue;
				}
				// No cut needed, or the clip produced an invalid brush
				// (should not happen: cuts only fire past Eps, so every cut
				// section has positive area). Keep the brush whole: the seal
				// boxes still seal the interior.
			}
			output.Brushes.Add( brush );
			report.KeptBrushes++;
		}

		foreach ( var light in input.Lights )
		{
			if ( light is not null && boxes.Any( c => c.Contains( light.Position ) ) )
			{
				output.Lights.Add( light );
				report.LightsKept++;
			}
			else report.LightsDropped++;
		}

		if ( input.PlayerStart is not null && boxes.Any( c => c.Contains( input.PlayerStart.Value ) ) )
		{
			output.PlayerStart = input.PlayerStart;
			report.PlayerStartKept = true;
		}

		// One seal box per needed face, skipped when it would land inside
		// another cordon (a union seals itself; a seal inside the compiled
		// region would be a phantom wall).
		for ( var i = 0; i < boxes.Count; i++ )
			for ( var f = 0; f < 6; f++ )
			{
				if ( !sealFace[i, f] ) continue;
				var (sMin, sMax) = SealBox( boxes[i], f );
				var insideAnother = false;
				for ( var j = 0; j < boxes.Count; j++ )
					if ( j != i && Overlaps( boxes[j], sMin, sMax ) ) { insideAnother = true; break; }
				if ( insideAnother ) continue;
				var name = "cordon_seal" + SealNames[f] + ( boxes.Count > 1 ? "_" + i : "" );
				output.Brushes.Add( CompileBrush.Box( name, -( ( i + 1 ) * 100 + f + 1 ), sMin, sMax, Cordon.SealMaterial ) );
				report.SealBrushes++;
			}

		return output;
	}

	/// <summary>
	/// Compile-visible-only filter: keeps brushes whose layer (via
	/// <paramref name="layerOf"/>) is unlayered (null/empty) or in
	/// <paramref name="visibleLayers"/> — the set of layer/visgroup ids whose
	/// MapLayer/MapVisGroup CompileVisible flag is true, computed by the
	/// caller (the layers ticket owns that flag). Lights and the player start
	/// are not brushes and are kept. The input is never mutated; the name is
	/// preserved (this is a filter, not a region). Returns the filtered input.
	/// </summary>
	public static CompileInput ApplyVisibleOnly( CompileInput input, IEnumerable<string> visibleLayers, Func<CompileBrush, string> layerOf ) =>
		ApplyVisibleOnly( input, visibleLayers, layerOf, out _ );

	/// <summary>Compile-visible-only filter; <paramref name="dropped"/> counts the brushes removed.</summary>
	public static CompileInput ApplyVisibleOnly( CompileInput input, IEnumerable<string> visibleLayers, Func<CompileBrush, string> layerOf, out int dropped )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( visibleLayers is null ) throw new ArgumentNullException( nameof( visibleLayers ) );
		if ( layerOf is null ) throw new ArgumentNullException( nameof( layerOf ) );
		var visible = new HashSet<string>( visibleLayers, StringComparer.Ordinal );
		var output = new CompileInput( input.Name );
		dropped = 0;
		foreach ( var brush in input.Brushes )
		{
			if ( brush is null )
			{
				output.Brushes.Add( null );
				continue;
			}
			var layer = layerOf( brush );
			if ( string.IsNullOrEmpty( layer ) || visible.Contains( layer ) )
				output.Brushes.Add( brush );
			else
				dropped++;
		}
		output.Lights.AddRange( input.Lights );
		output.PlayerStart = input.PlayerStart;
		return output;
	}

	/// <summary>
	/// Bisect a leaking map: split <paramref name="cordon"/> along
	/// <paramref name="axis"/> and compile each half (QuickPlay: BSP + leak
	/// check only, no output files). Each half's leak check starts from its
	/// flood origin: an explicit start, else the input's player start when it
	/// lies inside that half, else the half cordon's centre. A half whose
	/// origin is outside it throws (caller error, named). The result says
	/// which half leaked, so the next bisect (or fix) targets that half.
	/// </summary>
	public static CordonBisectResult BisectLeak( CompileInput input, Cordon cordon, CordonAxis axis, Vec3? firstStart = null, Vec3? secondStart = null )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var (first, second) = cordon.Bisect( axis );
		// Resolve both origins first: a bad explicit origin throws before
		// either half compiles.
		var firstStartResolved = ResolveStart( input, first, firstStart, "first" );
		var secondStartResolved = ResolveStart( input, second, secondStart, "second" );
		var firstResult = RunHalf( input, first, firstStartResolved );
		var secondResult = RunHalf( input, second, secondStartResolved );
		var firstLeaked = Leaked( firstResult );
		var secondLeaked = Leaked( secondResult );
		var half = firstLeaked && secondLeaked ? CordonHalf.Both
			: firstLeaked ? CordonHalf.First
			: secondLeaked ? CordonHalf.Second
			: CordonHalf.None;
		return new CordonBisectResult
		{
			LeakingHalf = half,
			FirstCordon = first,
			SecondCordon = second,
			First = firstResult,
			Second = secondResult,
		};
	}

	static Vec3 ResolveStart( CompileInput input, Cordon half, Vec3? explicitStart, string which )
	{
		var start = explicitStart
			?? ( input.PlayerStart is not null && half.Contains( input.PlayerStart.Value ) ? input.PlayerStart.Value : half.Center );
		if ( !half.Contains( start ) )
			throw new ArgumentException( $"Bisect {which}-half flood origin {start} is outside the {which} half cordon {half}.", nameof( explicitStart ) );
		return start;
	}

	static CompileResult RunHalf( CompileInput input, Cordon half, Vec3 start )
	{
		var sub = ApplyCordon( input, half );
		sub.PlayerStart = start;
		return CompileRunner.Run( sub, CompilePreset.QuickPlay );
	}

	static bool Leaked( CompileResult result ) =>
		result.Leak is not null && result.Leak.IsLeaked;

	/// <summary>
	/// Clip a brush to a cordon by adding the cordon faces it crosses as extra
	/// planes (seal material) and removing the original faces that lie
	/// entirely beyond a cut (their plane no longer touches the solid).
	/// Returns null when no cut is needed (brush fully inside) or when the
	/// clipped brush would be invalid, so the caller keeps the brush whole
	/// instead.
	/// </summary>
	static CompileBrush ClipToCordon( CompileBrush brush, Cordon cordon, Vec3 bMin, Vec3 bMax )
	{
		var cutMinX = bMin.x < cordon.Min.x - Eps;
		var cutMaxX = bMax.x > cordon.Max.x + Eps;
		var cutMinY = bMin.y < cordon.Min.y - Eps;
		var cutMaxY = bMax.y > cordon.Max.y + Eps;
		var cutMinZ = bMin.z < cordon.Min.z - Eps;
		var cutMaxZ = bMax.z > cordon.Max.z + Eps;
		if ( !cutMinX && !cutMaxX && !cutMinY && !cutMaxY && !cutMinZ && !cutMaxZ ) return null;

		// A face is entirely beyond a cut when ALL its polygon vertices lie
		// past the cut plane. Only faces that built a polygon can be judged
		// this way; a face with no polygon is already degenerate, and the
		// clipped brush fails Validate() below, so the caller keeps the brush
		// whole and the runner names the original defect.
		var drop = new bool[brush.Faces.Count];
		foreach ( var polygon in CompileRunner.BuildBrushPolygons( brush, out _ ) )
		{
			var verts = polygon.Vertices;
			if ( cutMinX && verts.All( v => v.x < cordon.Min.x - Eps ) ) drop[polygon.FaceIndex] = true;
			if ( cutMaxX && verts.All( v => v.x > cordon.Max.x + Eps ) ) drop[polygon.FaceIndex] = true;
			if ( cutMinY && verts.All( v => v.y < cordon.Min.y - Eps ) ) drop[polygon.FaceIndex] = true;
			if ( cutMaxY && verts.All( v => v.y > cordon.Max.y + Eps ) ) drop[polygon.FaceIndex] = true;
			if ( cutMinZ && verts.All( v => v.z < cordon.Min.z - Eps ) ) drop[polygon.FaceIndex] = true;
			if ( cutMaxZ && verts.All( v => v.z > cordon.Max.z + Eps ) ) drop[polygon.FaceIndex] = true;
		}
		var faces = new List<CompileFace>();
		for ( var i = 0; i < brush.Faces.Count; i++ )
			if ( !drop[i] ) faces.Add( brush.Faces[i] );
		if ( cutMinX ) faces.Add( new CompileFace( new Vec3( -1, 0, 0 ), -cordon.Min.x, Cordon.SealMaterial ) );
		if ( cutMaxX ) faces.Add( new CompileFace( new Vec3( 1, 0, 0 ), cordon.Max.x, Cordon.SealMaterial ) );
		if ( cutMinY ) faces.Add( new CompileFace( new Vec3( 0, -1, 0 ), -cordon.Min.y, Cordon.SealMaterial ) );
		if ( cutMaxY ) faces.Add( new CompileFace( new Vec3( 0, 1, 0 ), cordon.Max.y, Cordon.SealMaterial ) );
		if ( cutMinZ ) faces.Add( new CompileFace( new Vec3( 0, 0, -1 ), -cordon.Min.z, Cordon.SealMaterial ) );
		if ( cutMaxZ ) faces.Add( new CompileFace( new Vec3( 0, 0, 1 ), cordon.Max.z, Cordon.SealMaterial ) );
		var clipped = new CompileBrush( brush.Name, brush.Id, faces, brush.IsDetail );
		return clipped.Validate().Count == 0 ? clipped : null;
	}

	/// <summary>True when a brush has interior volume inside a cordon: its overlap with the cordon is longer than Eps on every axis. A brush merely touching the boundary from outside (e.g. a seal box from a previous pass, flush against the face) has zero overlap and is dropped, which is what makes ApplyCordon idempotent.</summary>
	static bool Overlaps( Cordon c, Vec3 bMin, Vec3 bMax ) =>
		Math.Min( bMax.x, c.Max.x ) - Math.Max( bMin.x, c.Min.x ) > Eps &&
		Math.Min( bMax.y, c.Max.y ) - Math.Max( bMin.y, c.Min.y ) > Eps &&
		Math.Min( bMax.z, c.Max.z ) - Math.Max( bMin.z, c.Min.z ) > Eps;

	/// <summary>Seal-brush name suffixes, in face order (0 east, 1 west, 2 north, 3 south, 4 top, 5 bottom).</summary>
	static readonly string[] SealNames = { "_east", "_west", "_north", "_south", "_top", "_bottom" };

	/// <summary>True when brush bounds trigger a seal on face <paramref name="face"/> of cordon <paramref name="c"/>: the brush straddles the face (it overlaps the cordon and pokes past the plane), or it lies beyond the face and faces it (its bounds overlap the face's tangential span, so the cordon cut it off there). A face where the boundary coincides with the hull — nothing straddles it, nothing lies beyond it — needs no seal: the hull itself seals it, and a real gap in that hull stays a real leak.</summary>
	static bool SealFaceNeeded( Cordon c, int face, Vec3 bMin, Vec3 bMax, bool overlaps )
	{
		switch ( face )
		{
			case 0: return ( overlaps && bMax.x > c.Max.x + Eps )
				|| ( bMin.x > c.Max.x - Eps && bMax.y > c.Min.y - Eps && bMin.y < c.Max.y + Eps && bMax.z > c.Min.z - Eps && bMin.z < c.Max.z + Eps );
			case 1: return ( overlaps && bMin.x < c.Min.x - Eps )
				|| ( bMax.x < c.Min.x + Eps && bMax.y > c.Min.y - Eps && bMin.y < c.Max.y + Eps && bMax.z > c.Min.z - Eps && bMin.z < c.Max.z + Eps );
			case 2: return ( overlaps && bMax.y > c.Max.y + Eps )
				|| ( bMin.y > c.Max.y - Eps && bMax.x > c.Min.x - Eps && bMin.x < c.Max.x + Eps && bMax.z > c.Min.z - Eps && bMin.z < c.Max.z + Eps );
			case 3: return ( overlaps && bMin.y < c.Min.y - Eps )
				|| ( bMax.y < c.Min.y + Eps && bMax.x > c.Min.x - Eps && bMin.x < c.Max.x + Eps && bMax.z > c.Min.z - Eps && bMin.z < c.Max.z + Eps );
			case 4: return ( overlaps && bMax.z > c.Max.z + Eps )
				|| ( bMin.z > c.Max.z - Eps && bMax.x > c.Min.x - Eps && bMin.x < c.Max.x + Eps && bMax.y > c.Min.y - Eps && bMin.y < c.Max.y + Eps );
			default: return ( overlaps && bMin.z < c.Min.z - Eps )
				|| ( bMax.z < c.Min.z + Eps && bMax.x > c.Min.x - Eps && bMin.x < c.Max.x + Eps && bMax.y > c.Min.y - Eps && bMin.y < c.Max.y + Eps );
		}
	}

	/// <summary>Bounds of the temporary seal box for face <paramref name="face"/> of a cordon (0 east, 1 west, 2 north, 3 south, 4 top, 5 bottom): just outside the face, extended past the corners so the shell has no corner gaps. Synthetic ids are -( (cordonIndex + 1) * 100 + face + 1), so cordon 0 gets -101..-106, cordon 1 gets -201..-206, and multi-cordon seals never collide.</summary>
	static (Vec3 Min, Vec3 Max) SealBox( Cordon cordon, int face )
	{
		var t = Cordon.SealThickness;
		return face switch
		{
			0 => ( new Vec3( cordon.Max.x, cordon.Min.y - t, cordon.Min.z - t ), new Vec3( cordon.Max.x + t, cordon.Max.y + t, cordon.Max.z + t ) ),
			1 => ( new Vec3( cordon.Min.x - t, cordon.Min.y - t, cordon.Min.z - t ), new Vec3( cordon.Min.x, cordon.Max.y + t, cordon.Max.z + t ) ),
			2 => ( new Vec3( cordon.Min.x - t, cordon.Max.y, cordon.Min.z - t ), new Vec3( cordon.Max.x + t, cordon.Max.y + t, cordon.Max.z + t ) ),
			3 => ( new Vec3( cordon.Min.x - t, cordon.Min.y - t, cordon.Min.z - t ), new Vec3( cordon.Max.x + t, cordon.Min.y, cordon.Max.z + t ) ),
			4 => ( new Vec3( cordon.Min.x - t, cordon.Min.y - t, cordon.Max.z ), new Vec3( cordon.Max.x + t, cordon.Max.y + t, cordon.Max.z + t ) ),
			_ => ( new Vec3( cordon.Min.x - t, cordon.Min.y - t, cordon.Min.z - t ), new Vec3( cordon.Max.x + t, cordon.Max.y + t, cordon.Min.z ) ),
		};
	}
}
