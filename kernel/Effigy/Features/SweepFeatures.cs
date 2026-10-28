using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Shared machinery for the two features that build a solid by running a profile through a series
/// of rings and skinning between them: sweep along a path, and loft between sections.
///
/// Both come down to the same three steps — produce a list of rings with the same point count,
/// stitch quads between consecutive rings, cap the two ends — and the only interesting differences
/// are where the rings come from and how they are kept from twisting relative to each other.
/// </summary>
internal static class Skinner
{
	/// <summary>
	/// Stitch a stack of rings into a tube and cap both ends.
	///
	/// Every ring must have the same number of points, in the same winding, and be rotationally
	/// aligned with its neighbours — nothing here checks that, because both callers have already
	/// had to do the work of arranging it and re-deriving it would be guessing.
	/// </summary>
	/// <param name="wrap">
	/// Stitch the last ring back to the first, using the vertices that are already there.
	///
	/// A closed sweep cannot be closed by appending a copy of the first ring instead: the copy
	/// would be new vertices at the same positions, and this mesh is manifold by INDEX rather than
	/// by position, so the tube would still have two open ends that merely happen to touch. It has
	/// to be the same indices, which means it has to be a flag here.
	/// </param>
	public static PolyMesh Skin( IReadOnlyList<List<Vec3>> rings, bool capEnds, int material, bool wrap = false )
	{
		var mesh = new PolyMesh();
		var count = rings[0].Count;

		var starts = new int[rings.Count];

		for ( var r = 0; r < rings.Count; r++ )
		{
			starts[r] = mesh.Positions.Count;

			foreach ( var p in rings[r] )
				mesh.AddVertex( p );
		}

		// One span per gap between rings, plus the closing gap when the stack wraps.
		var spans = wrap ? rings.Count : rings.Count - 1;

		// V runs along the sweep and U around the ring, both by cumulative distance so a texture
		// does not bunch up where the rings happen to be close together.
		var along = new float[spans + 1];

		for ( var r = 1; r <= spans; r++ )
			along[r] = along[r - 1] + (rings[r % rings.Count][0] - rings[r - 1][0]).Length;

		var total = along[^1] > 0f ? along[^1] : 1f;

		var around = new float[count + 1];

		for ( var i = 0; i < count; i++ )
			around[i + 1] = around[i] + (rings[0][(i + 1) % count] - rings[0][i]).Length;

		var perimeter = around[^1] > 0f ? around[^1] : 1f;

		for ( var r = 0; r < spans; r++ )
		{
			var next = (r + 1) % rings.Count;

			for ( var i = 0; i < count; i++ )
			{
				var j = (i + 1) % count;

				var v0 = along[r] / total;
				var v1 = along[r + 1] / total;
				var u0 = around[i] / perimeter;
				var u1 = around[i + 1] / perimeter;

				mesh.AddFace(
					new[] { starts[r] + i, starts[r] + j, starts[next] + j, starts[next] + i },
					new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
					material );
			}
		}

		if ( !capEnds )
			return mesh;

		// The far cap keeps the ring's winding and the near one reverses it, so both normals point
		// out of the solid rather than both pointing the same way along the sweep.
		var last = rings.Count - 1;

		var farIndices = new int[count];
		var farUVs = new Vec2[count];
		var nearIndices = new int[count];
		var nearUVs = new Vec2[count];

		for ( var i = 0; i < count; i++ )
		{
			farIndices[i] = starts[last] + i;
			farUVs[i] = new Vec2( around[i] / perimeter, 0f );

			nearIndices[i] = starts[0] + count - 1 - i;
			nearUVs[i] = new Vec2( around[count - 1 - i] / perimeter, 0f );
		}

		mesh.AddFace( farIndices, farUVs, material );
		mesh.AddFace( nearIndices, nearUVs, material );

		return mesh;
	}

	/// <summary>
	/// Skin, then check the result is not inside out (Backlog B3). A profile wound the other
	/// way, or a loft whose sections all face backwards, skins into a negative-volume solid
	/// that renders black and breaks every boolean downstream. Flipping every ring and
	/// re-skinning is the whole fix: the caps are built from the rings, so they flip too.
	/// A degenerate (zero-volume) skin is left alone — its sign is noise, not a winding.
	/// </summary>
	public static PolyMesh SkinChecked( IReadOnlyList<List<Vec3>> rings, bool capEnds, int material, bool wrap = false )
	{
		var mesh = Skin( rings, capEnds, material, wrap );

		if ( mesh.SignedVolume() < -1e-9f )
		{
			foreach ( var ring in rings )
				ring.Reverse();

			mesh = Skin( rings, capEnds, material, wrap );
		}

		return mesh;
	}

	/// <summary>
	/// Skin a tube whose section has one hole (Backlog: profile holes, A5.3). The outer wall
	/// and the hole wall are skinned as two tubes in one mesh, and the two ends are closed
	/// with annular quad bands between the outer ring and the hole ring — not fan caps, which
	/// would pave over the hole. The hole wall's quads are wound backwards relative to the
	/// outer wall, so its normals point INTO the void (out of the material), and the hole
	/// ring's point order pairs index-for-index with the outer ring's, which is what makes
	/// the annular band tile without twist. Callers must prepare the hole ring that way:
	/// same traversal direction as the outer ring, same point count, start angles matched.
	/// When <paramref name="wrap"/> is set the tubes close on themselves and no caps are built.
	/// </summary>
	public static PolyMesh SkinWithHole( IReadOnlyList<List<Vec3>> outerRings, IReadOnlyList<List<Vec3>> holeRings, int material, bool wrap = false )
	{
		var mesh = BuildHoled( outerRings, holeRings, material, wrap );

		if ( mesh.SignedVolume() < -1e-9f )
		{
			foreach ( var ring in outerRings )
				ring.Reverse();

			foreach ( var ring in holeRings )
				ring.Reverse();

			mesh = BuildHoled( outerRings, holeRings, material, wrap );
		}

		return mesh;
	}

	static PolyMesh BuildHoled( IReadOnlyList<List<Vec3>> outerRings, IReadOnlyList<List<Vec3>> holeRings, int material, bool wrap )
	{
		var count = outerRings[0].Count;
		var mesh = new PolyMesh();

		var outerStarts = new int[outerRings.Count];
		var holeStarts = new int[holeRings.Count];

		for ( var r = 0; r < outerRings.Count; r++ )
		{
			outerStarts[r] = mesh.Positions.Count;
			foreach ( var p in outerRings[r] )
				mesh.AddVertex( p );

			holeStarts[r] = mesh.Positions.Count;
			foreach ( var p in holeRings[r] )
				mesh.AddVertex( p );
		}

		var spans = wrap ? outerRings.Count : outerRings.Count - 1;
		var along = new float[spans + 1];

		for ( var r = 1; r <= spans; r++ )
			along[r] = along[r - 1] + (outerRings[r % outerRings.Count][0] - outerRings[r - 1][0]).Length;

		var total = along[^1] > 0f ? along[^1] : 1f;
		var around = new float[count + 1];

		for ( var i = 0; i < count; i++ )
			around[i + 1] = around[i] + (outerRings[0][(i + 1) % count] - outerRings[0][i]).Length;

		var perimeter = around[^1] > 0f ? around[^1] : 1f;

		void Tube( int[] starts, bool flip )
		{
			for ( var r = 0; r < spans; r++ )
			{
				var next = (r + 1) % outerRings.Count;

				for ( var i = 0; i < count; i++ )
				{
					var j = (i + 1) % count;
					var v0 = along[r] / total;
					var v1 = along[r + 1] / total;
					var u0 = around[i] / perimeter;
					var u1 = around[i + 1] / perimeter;

					// The hole wall is the same quad with its corners walked backwards, so its
					// normal points into the tunnel instead of into the material.
					mesh.AddFace(
						flip
							? new[] { starts[r] + i, starts[next] + i, starts[next] + j, starts[r] + j }
							: new[] { starts[r] + i, starts[r] + j, starts[next] + j, starts[next] + i },
						flip
							? new[] { new Vec2( u0, v0 ), new Vec2( u0, v1 ), new Vec2( u1, v1 ), new Vec2( u1, v0 ) }
							: new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
						material );
				}
			}
		}

		Tube( outerStarts, flip: false );
		Tube( holeStarts, flip: true );

		if ( wrap )
			return mesh;

		// Annular caps: one quad band per end, outer ring to hole ring, index for index.
		// The near cap walks the band one way and the far cap the other, so both normals
		// point out of the solid, matching Skin's plain-cap convention.
		var last = outerRings.Count - 1;

		for ( var i = 0; i < count; i++ )
		{
			var j = (i + 1) % count;

			mesh.AddFace(
				new[] { outerStarts[last] + i, outerStarts[last] + j, holeStarts[last] + j, holeStarts[last] + i },
				new[] { new Vec2( around[i] / perimeter, 0f ), new Vec2( around[j] / perimeter, 0f ), new Vec2( around[j] / perimeter, 0f ), new Vec2( around[i] / perimeter, 0f ) },
				material );
			mesh.AddFace(
				new[] { outerStarts[0] + j, outerStarts[0] + i, holeStarts[0] + i, holeStarts[0] + j },
				new[] { new Vec2( around[j] / perimeter, 0f ), new Vec2( around[i] / perimeter, 0f ), new Vec2( around[i] / perimeter, 0f ), new Vec2( around[j] / perimeter, 0f ) },
				material );
		}

		return mesh;
	}

	/// <summary>
	/// Skin a tube whose section has TWO OR MORE holes (wave 2: multi-hole caps).
	///
	/// The single-hole case keeps its annular quad bands (see SkinWithHole); with two or more
	/// holes a band per hole cannot tile the cap, because the cap is one region with several
	/// boundaries, not several annuli. So the caps here are TRIANGULATED from the section's
	/// own loops, with the same contract the extrude caps use: <paramref name="nearTris"/>
	/// and <paramref name="farTris"/> index the concatenated loop list (outer first, then
	/// each hole in order, every loop with the outer's point count), exactly as
	/// Triangulate.WithHoles returns them; the far cap takes them as given and the near cap
	/// takes them reversed, the same far-as-is / near-reversed convention as the extrude.
	/// The tubes are the same as BuildHoled's (outer wall out, hole walls into the void).
	///
	/// WINDING IS NOT FIXED BY REVERSING RINGS. SkinChecked can reverse rings because its
	/// caps are built from ring order; a triangulated cap is indexed by profile position, so
	/// reversing a ring would scramble its triangles instead of flipping them. Here a
	/// negative-volume build is rebuilt with every winding flipped instead: tube quads swap
	/// sides and the two caps swap which one is reversed. The caller supplies a
	/// triangulation per end because loft sections differ per end; a sweep passes the same
	/// triangulation twice, since its profile is the same at both ends (taper is a uniform
	/// scale about the section centre, so it cannot change which bridge the triangulation
	/// needs). With <paramref name="wrap"/> set there are no caps and the triangulations
	/// are ignored.
	/// </summary>
	public static PolyMesh SkinWithHoles( IReadOnlyList<List<Vec3>> outerRings,
		IReadOnlyList<IReadOnlyList<List<Vec3>>> holeRings,
		IReadOnlyList<(int A, int B, int C)> nearTris,
		IReadOnlyList<(int A, int B, int C)> farTris,
		int material, bool wrap = false )
	{
		var mesh = BuildHoledMulti( outerRings, holeRings, nearTris, farTris, material, wrap, flip: false );

		if ( mesh.SignedVolume() < -1e-9f )
			mesh = BuildHoledMulti( outerRings, holeRings, nearTris, farTris, material, wrap, flip: true );

		return mesh;
	}

	static PolyMesh BuildHoledMulti( IReadOnlyList<List<Vec3>> outerRings,
		IReadOnlyList<IReadOnlyList<List<Vec3>>> holeRings,
		IReadOnlyList<(int A, int B, int C)> nearTris,
		IReadOnlyList<(int A, int B, int C)> farTris,
		int material, bool wrap, bool flip )
	{
		var count = outerRings[0].Count;
		var mesh = new PolyMesh();

		var outerStarts = new int[outerRings.Count];
		var holeStarts = new int[holeRings.Count][];

		for ( var r = 0; r < outerRings.Count; r++ )
		{
			outerStarts[r] = mesh.Positions.Count;
			foreach ( var p in outerRings[r] )
				mesh.AddVertex( p );

			for ( var h = 0; h < holeRings.Count; h++ )
			{
				holeStarts[h] ??= new int[outerRings.Count];
				holeStarts[h][r] = mesh.Positions.Count;
				foreach ( var p in holeRings[h][r] )
					mesh.AddVertex( p );
			}
		}

		var spans = wrap ? outerRings.Count : outerRings.Count - 1;
		var along = new float[spans + 1];

		for ( var r = 1; r <= spans; r++ )
			along[r] = along[r - 1] + (outerRings[r % outerRings.Count][0] - outerRings[r - 1][0]).Length;

		var total = along[^1] > 0f ? along[^1] : 1f;
		var around = new float[count + 1];

		for ( var i = 0; i < count; i++ )
			around[i + 1] = around[i] + (outerRings[0][(i + 1) % count] - outerRings[0][i]).Length;

		var perimeter = around[^1] > 0f ? around[^1] : 1f;

		void Tube( int[] starts, bool holeWall )
		{
			// Hole walls are wound backwards relative to the outer wall; the global flip
			// swaps both, so a flipped build still has hole normals into the void.
			var flipQuad = holeWall ? !flip : flip;

			for ( var r = 0; r < spans; r++ )
			{
				var next = (r + 1) % outerRings.Count;

				for ( var i = 0; i < count; i++ )
				{
					var j = (i + 1) % count;
					var v0 = along[r] / total;
					var v1 = along[r + 1] / total;
					var u0 = around[i] / perimeter;
					var u1 = around[i + 1] / perimeter;

					mesh.AddFace(
						flipQuad
							? new[] { starts[r] + i, starts[next] + i, starts[next] + j, starts[r] + j }
							: new[] { starts[r] + i, starts[r] + j, starts[next] + j, starts[next] + i },
						flipQuad
							? new[] { new Vec2( u0, v0 ), new Vec2( u0, v1 ), new Vec2( u1, v1 ), new Vec2( u0, v0 ) }
							: new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
						material );
				}
			}
		}

		Tube( outerStarts, holeWall: false );

		for ( var h = 0; h < holeRings.Count; h++ )
			Tube( holeStarts[h], holeWall: true );

		if ( wrap )
			return mesh;

		// Cap triangles index the concatenated loop list: outer first, then each hole in
		// order, every loop with the outer's point count. Cap UVs are flat zeros — a cap
		// is not a surface anyone textures by loop position, and inventing UVs here would
		// be worse than none.
		int Map( int flatIndex, int station )
		{
			if ( flatIndex < count )
				return outerStarts[station] + flatIndex;

			var h = (flatIndex - count) / count;
			return holeStarts[h][station] + (flatIndex - count - h * count);
		}

		var last = outerRings.Count - 1;

		void Cap( IReadOnlyList<(int A, int B, int C)> tris, int station, bool reverse )
		{
			foreach ( var (a, b, c) in tris )
			{
				mesh.AddFace(
					reverse
						? new[] { Map( c, station ), Map( b, station ), Map( a, station ) }
						: new[] { Map( a, station ), Map( b, station ), Map( c, station ) },
					new[] { Vec2.Zero, Vec2.Zero, Vec2.Zero },
					material );
			}
		}

		// Far cap as given, near cap reversed — the extrude convention. The global flip
		// swaps the two, which is the whole winding fix.
		Cap( farTris, last, reverse: flip );
		Cap( nearTris, 0, reverse: !flip );

		return mesh;
	}

	/// <summary>
	/// Resample a closed loop to exactly <paramref name="count"/> points, evenly spaced by arc
	/// length.
	///
	/// Loft needs this because two sections drawn by hand have no reason to share a point count,
	/// and skinning needs them to. Even spacing by LENGTH rather than by index is what stops a
	/// section with a dense corner dragging the whole surface toward it.
	/// </summary>
	public static List<Vec2> Resample( IReadOnlyList<Vec2> loop, int count )
	{
		var n = loop.Count;
		var lengths = new float[n + 1];

		for ( var i = 0; i < n; i++ )
			lengths[i + 1] = lengths[i] + (loop[(i + 1) % n] - loop[i]).Length;

		var perimeter = lengths[n];
		var result = new List<Vec2>( count );

		if ( perimeter < 1e-9f )
		{
			for ( var i = 0; i < count; i++ )
				result.Add( loop[0] );

			return result;
		}

		var at = 0;

		for ( var i = 0; i < count; i++ )
		{
			var target = perimeter * i / count;

			while ( at + 1 < n && lengths[at + 1] < target )
				at++;

			var span = lengths[at + 1] - lengths[at];
			var t = span > 1e-9f ? (target - lengths[at]) / span : 0f;

			var a = loop[at];
			var b = loop[(at + 1) % n];

			result.Add( a + (b - a) * t );
		}

		return result;
	}

	/// <summary>
	/// Rotate a ring's point order so it lines up with the ring before it, and reverse it if the two
	/// wind opposite ways.
	///
	/// WITHOUT THIS A LOFT TWISTS. Two circles resampled independently start wherever their first
	/// curve happened to start, so skinning point 0 to point 0 can put a half-turn of shear into a
	/// surface between two shapes that are the same. Trying every rotation and taking the one with
	/// the least total squared distance is O(n^2) in the ring size and completely dominated by
	/// everything else at these counts.
	/// </summary>
	public static List<Vec3> Align( List<Vec3> ring, IReadOnlyList<Vec3> previous )
	{
		var count = ring.Count;

		// Winding first: a reversed ring can never be brought into agreement by rotation alone, and
		// skinning one to its neighbour turns the surface inside out at that joint.
		var forward = Best( ring, previous, out var forwardScore );

		var flipped = new List<Vec3>( ring );
		flipped.Reverse();

		var backward = Best( flipped, previous, out var backwardScore );

		return backwardScore < forwardScore
			? Rotate( flipped, backward )
			: Rotate( ring, forward );
	}

	static int Best( IReadOnlyList<Vec3> ring, IReadOnlyList<Vec3> previous, out float score )
	{
		var count = ring.Count;
		var bestOffset = 0;

		score = float.MaxValue;

		for ( var offset = 0; offset < count; offset++ )
		{
			var total = 0f;

			for ( var i = 0; i < count; i++ )
				total += (ring[(i + offset) % count] - previous[i]).LengthSquared;

			if ( total >= score )
				continue;

			score = total;
			bestOffset = offset;
		}

		return bestOffset;
	}

	static List<Vec3> Rotate( IReadOnlyList<Vec3> ring, int offset )
	{
		var result = new List<Vec3>( ring.Count );

		for ( var i = 0; i < ring.Count; i++ )
			result.Add( ring[(i + offset) % ring.Count] );

		return result;
	}
}

/// <summary>
/// Run a profile along a path, building a solid out of the swept surface. Onshape's Sweep.
///
/// THE PROFILE IS TAKEN PERPENDICULAR TO THE PATH, not left lying on its own plane. A sweep whose
/// profile keeps its original orientation is an extrusion along a curve, which is a different and
/// much less useful operation — it collapses wherever the path turns toward the profile's normal.
///
/// FRAMES ARE PROPAGATED, NOT RECOMPUTED. The obvious way to orient the profile at each station is
/// to build a frame from the tangent and some fixed up-vector, and it is wrong: wherever the
/// tangent passes near that up-vector the frame spins wildly, and a sweep round a helix visibly
/// corkscrews. Instead each station's frame is the previous one turned by the smallest rotation
/// that carries the old tangent onto the new one. That is the rotation-minimising frame, it has no
/// preferred direction to go unstable near, and it costs one cross product per station.
/// </summary>
public sealed class SweepFeature : SketchConsumingFeature
{
	public override string TypeName => "Sweep";

	/// <summary>
	/// The sketch whose curves form the path. Empty means the sketch before the profile's, which is
	/// the order a person draws them in.
	/// </summary>
	public string PathSketchId = "";

	/// <summary>
	/// A 3D spline to sweep along instead of a sketch — a SplineFeature's id. Empty means: use the
	/// path sketch, or, when there is no second sketch but a spline has been drawn, the spline
	/// published last. A curve you can click out in space is the path most sweeps actually want.
	/// </summary>
	public string PathSplineId = "";

	public readonly FloatParam Twist = new( "Twist", 0f, unit: "deg" );

	/// <summary>Resample the path to this many stations, evenly by arc length, before sweeping.
	/// 0 means the path's own tessellation, exactly as before — so existing sweeps do not move.
	/// Set it to give a sparse path enough stations for a taper mid-point to appear.</summary>
	public readonly IntParam PathStations = new( "Path stations", 0, 0, 512 );

	/// <summary>Rotate the profile about its own centre before sweeping, in degrees. The
	/// connector-point override: when Align-style correspondence guesses the wrong start
	/// angle, turn the profile here instead of redrawing it.</summary>
	public readonly FloatParam StartAngle = new( "Start angle", 0f, unit: "deg" );

	/// <summary>How the profile frame is carried along the path. Parallel transport is the
	/// default and the right one for most sweeps; Fixed up keeps the profile upright to
	/// world up; Frenet follows the path's curvature (and is undefined at inflections, where
	/// it carries the last good normal forward).</summary>
	public readonly ChoiceParam FrameMode = new( "Frame mode", new[] { "Parallel transport", "Fixed up", "Frenet" } );

	public readonly BoolParam BendGuard = new( "Bend guard", true );

	/// <summary>Sample taper and twist by arc length along the path instead of by station
	/// index, matching SplineTube. On a path whose stations bunch on a bend, station-index
	/// taper bunches there too; arc length does not. Turn this off for the old behaviour.</summary>
	public readonly BoolParam ArcTaper = new( "Arc length taper", true );

	/// <summary>Feature id of a sketch whose open chain is a second rail for a two-rail sweep:
	/// the profile is scaled (and, with both rails, shifted) so it spans rail to rail at
	/// every station. Empty means a plain sweep.</summary>
	public string RailSketchId = "";

	/// <summary>Feature id of a sketch whose open chain is the second rail of a two-rail sweep.
	/// Only used together with RailSketchId.</summary>
	public string Rail2SketchId = "";

	/// <summary>Scale along the sweep as "t:scale,…" pairs, t in 0..1 along the path — e.g.
	/// "0:1,0.5:1.4,1:0.2" swells mid-sweep and tapers to a point — BUT ONLY IF THE PATH
	/// HAS STATIONS NEAR THOSE t VALUES. The taper is sampled at the path's stations, so a
	/// two-station path interpolates end to end and never shows a mid value: that is the
	/// contract, not a bug (wave 2), and a sweep with mid-taper points on a two-station path
	/// warns instead of silently drawing a straight taper. Empty means no taper.</summary>
	public readonly StringParam Taper = new( "Taper (t:scale,…)", "" );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Twist, Taper, PathStations, StartAngle, FrameMode, Result, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material, BendGuard, ArcTaper };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveProfileSketch( ctx );
		var profiles = ResolveProfiles( sketch, ctx );
		var path = ResolvePath( ctx, sketch );

		if ( path.Count < 2 )
		{
			Fail(
				"The path needs at least two points to sweep along.",
				$"The path resolved to {path.Count} point(s), which is not a curve.",
				"Draw a path sketch with at least one line or arc",
				"Pick a different sketch as the path" );
		}

		// A path that returns to where it started is a closed tube: no ends to cap, and the last
		// station is the first one again rather than a station of its own. The comparison is
		// against the path's own scale so a large model does not read as closed just because its
		// absolute gap is small.
		var closed = (path[^1] - path[0]).Length < PathScale( path ) * 1e-3f;

		if ( closed )
		{
			path.RemoveAt( path.Count - 1 );

			if ( path.Count < 3 )
				Fail(
					"A closed path needs at least three stations to sweep along.",
					"The path returns to its start, so the closing point was dropped, and fewer than three stations remain.",
					"Add another point to the path",
					"Leave the path open" );
		}

		// Path stations (wave 3): resample arc-length-even when asked. 0 keeps the path's
		// own tessellation, so existing sweeps do not move.
		if ( PathStations.Clamped > 0 )
		{
			if ( !closed && PathStations.Clamped < 2 )
				Fail(
					"Path stations needs at least two stations for an open path.",
					"Path stations was set to 1, which is a point, not a path.",
					"Set Path stations to 0 (as tessellated) or to 2 or more" );

			path = ResamplePath( path, PathStations.Clamped, closed );
		}

		var taper = ParseTaper( Taper.Value );

		// TAPER STATIONS ARE THE CONTRACT (wave 2 decision, with evidence in the tests):
		// the taper function is sampled at the path's stations, so a path with only its two
		// ends has nothing to sample a mid value at, and interpolating end to end is the
		// correct result for that path — NOT a missing feature. Auto-subdividing the path
		// would change every existing tapered sweep's rings, so the fix is a warning plus
		// the docstring above, not a geometry change.
		if ( taper is not null && !closed && path.Count == 2 && taper.Any( p => p.T > 0f && p.T < 1f ) )
			Warning = "Taper mid-points need path stations: this path has only its two ends, so the sweep interpolates end to end and the mid-taper values never appear. Add a mid-point to the path to see them.";

		foreach ( var profile in profiles )
		{
			// A twist cannot be carried round a closed path: the last ring has to be the first
			// ring, and a turned copy of it is not. Dropped rather than applied, and said out loud
			// rather than dropped quietly.
			if ( closed && Twist.Value != 0f )
				Warning = "Twist was ignored: a closed path has to meet itself, so it cannot end turned.";

			var frames = BuildFrames( sketch, path, closed, out var holonomy, FrameMode.Index );
			var centre = CentreOf( profile.Outer );
			var outerLoop = StartAngle.Value == 0f ? profile.Outer : RotateLoopGeom( profile.Outer, centre, StartAngle.Value * MathF.PI / 180f );
			var arcFracs = ArcFractions( path, closed );
			if ( BendGuard.Value )
				CheckBend( path, frames, closed, outerLoop, centre, taper, arcFracs, ArcTaper.Value, holonomy, closed ? 0f : Twist.Value, StartAngle.Value * MathF.PI / 180f );
			var (railScales, railShifts) = RailScalesAndShifts( ctx, path, frames, closed, outerLoop, centre, holonomy, closed ? 0f : Twist.Value, arcFracs, ArcTaper.Value, StartAngle.Value * MathF.PI / 180f, taper );
			var rings = RingsForLoop( profile.Outer, centre, path, frames, holonomy, closed ? 0f : Twist.Value, taper, closed, arcFracs, ArcTaper.Value, railScales, railShifts, StartAngle.Value * MathF.PI / 180f );

			PolyMesh mesh;

			if ( !profile.HasHoles )
			{
				mesh = Skinner.SkinChecked( rings, !closed, Material.Clamped, wrap: closed );
			}
			else if ( profile.Holes.Count == 1 )
			{
				// One hole keeps its annular quad bands — the path every existing holed sweep
				// already rebuilds on, left exactly alone.
				var holeLoop = PrepareHoleLoop( outerLoop, profile.Holes[0], profile.Outer.Count );
				var holeRings = RingsForLoop( holeLoop, centre, path, frames, holonomy, closed ? 0f : Twist.Value, taper, closed, arcFracs, ArcTaper.Value, railScales, railShifts, StartAngle.Value * MathF.PI / 180f );

				mesh = Skinner.SkinWithHole( rings, holeRings, Material.Clamped, wrap: closed );
			}
			else
			{
				// Two or more holes: every hole is carried through as its own tunnel, and the
				// caps are triangulated from the prepared loops (wave 2). The prepared loops
				// are what the rings are built from, so the triangulation's indices map
				// straight onto the ring vertices.
				var prepared = profile.Holes
					.Select( h => PrepareHoleLoop( outerLoop, h, profile.Outer.Count ) )
					.ToList();
				var allHoleRings = prepared
					.Select( loop => RingsForLoop( loop, centre, path, frames, holonomy, closed ? 0f : Twist.Value, taper, closed, arcFracs, ArcTaper.Value, railScales, railShifts, StartAngle.Value * MathF.PI / 180f ) )
					.ToList();
				var tris = Triangulate.WithHoles( profile.Outer, prepared.Cast<IReadOnlyList<Vec2>>().ToList() );

				if ( tris.Count == 0 )
					Fail(
						$"This profile's {profile.Holes.Count} holes could not be capped — the loops may cross each other.",
						"Ear clipping returned no triangles for a multi-hole profile.",
						"Check that every inner loop lies fully inside the outer one",
						"Redraw a hole so it does not cross the outer loop or another hole" );

				mesh = Skinner.SkinWithHoles( rings, allHoleRings, tris, tris, Material.Clamped, wrap: closed );
			}

			Emit( ctx, mesh );
		}
		var _profileId = ctx.Sketches.FirstOrDefault( kv => ReferenceEquals( kv.Value, sketch ) ).Key;
		var _pathSketch = ResolvePathSketch( ctx, sketch );
		var _pathName = !string.IsNullOrEmpty( PathSplineId ) && ctx.Splines.ContainsKey( PathSplineId ) ? $"spline {PathSplineId}" : _pathSketch is not null ? SketchNameFor( ctx, _pathSketch ) : "(last spline)";
		NoteChoice( $"Sweep: profile={SketchNameFor( ctx, sketch )}" + ( string.IsNullOrEmpty( SketchFeatureId ) ? " (auto-selected)" : "" ) + $", path={_pathName}" + ( string.IsNullOrEmpty( PathSketchId ) && string.IsNullOrEmpty( PathSplineId ) ? " (auto-selected)" : "" ) );
	}

	static float PathScale( IReadOnlyList<Vec3> path )
	{
		var total = 0f;

		for ( var i = 0; i + 1 < path.Count; i++ )
			total += (path[i + 1] - path[i]).Length;

		return MathF.Max( total, 1e-6f );
	}

	internal static Vec2 CentreOf( List<Vec2> loop )
	{
		var centre = Vec2.Zero;

		foreach ( var p in loop )
			centre += p;

		return centre / loop.Count;
	}

	/// <summary>
	/// The rotation-minimising frame at every path station, plus the closed-loop holonomy
	/// (Backlog B16): after the last station the frame is turned back onto the first
	/// station's tangent, and the angle between that closing frame and frame 0 is the twist
	/// a naive closed sweep would leave as a kink at the seam. It is 0 for a planar loop
	/// and non-zero for a loop that leaves its plane — exactly the case that used to kink.
	/// </summary>
	internal static List<(Vec3 U, Vec3 V, Vec3 Tangent)> BuildFrames( Sketch sketch, List<Vec3> path, bool closed, out float holonomy ) =>
		BuildFrames( sketch, path, closed, out holonomy, 0 );

	internal static List<(Vec3 U, Vec3 V, Vec3 Tangent)> BuildFrames( Sketch sketch, List<Vec3> path, bool closed, out float holonomy, int frameMode )
	{
		if ( frameMode == 1 )
			return BuildFixedUpFrames( path, closed, out holonomy );

		if ( frameMode == 2 )
			return BuildFrenetFrames( sketch, path, closed, out holonomy );

		var frames = new List<(Vec3 U, Vec3 V, Vec3 Tangent)>( path.Count );
		var tangent = (path[1] - path[0]).Normal;
		var u = sketch.Plane.XAxis;
		var v = sketch.Plane.YAxis;

		Rotate( ref u, ref v, sketch.Plane.Normal, tangent );

		for ( var i = 0; i < path.Count; i++ )
		{
			if ( i > 0 )
			{
				var next = i + 1 < path.Count
					? (path[i + 1] - path[i]).Normal
					: (path[i] - path[i - 1]).Normal;

				Rotate( ref u, ref v, tangent, next );
				tangent = next;
			}

			frames.Add( (u, v, tangent) );
		}

		holonomy = 0f;

		if ( closed )
		{
			var cu = u;
			var cv = v;

			Rotate( ref cu, ref cv, tangent, frames[0].Tangent );

			holonomy = MathF.Atan2( Vec3.Dot( frames[0].V, cu ), Vec3.Dot( frames[0].U, cu ) );
		}

		return frames;
	}

	static List<(Vec3 U, Vec3 V, Vec3 Tangent)> BuildFixedUpFrames( List<Vec3> path, bool closed, out float holonomy )
	{
		holonomy = 0f;
		var frames = new List<(Vec3 U, Vec3 V, Vec3 Tangent)>( path.Count );

		for ( var i = 0; i < path.Count; i++ )
		{
			var tangent = TangentAtPath( path, i, closed );
			var up = MathF.Abs( tangent.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
			var u = Vec3.Cross( up, tangent );
			if ( u.LengthSquared < 1e-12f )
				u = frames.Count > 0 ? frames[^1].U : new Vec3( 1, 0, 0 );
			else
				u = u.Normal;
			var v = Vec3.Cross( tangent, u );
			frames.Add( (u, v, tangent) );
		}

		return frames;
	}

	static List<(Vec3 U, Vec3 V, Vec3 Tangent)> BuildFrenetFrames( Sketch sketch, List<Vec3> path, bool closed, out float holonomy )
	{
		holonomy = 0f;
		var frames = new List<(Vec3 U, Vec3 V, Vec3 Tangent)>( path.Count );
		var n = path.Count;
		var prevNormal = Vec3.Zero;

		for ( var i = 0; i < n; i++ )
		{
			var tangent = TangentAtPath( path, i, closed );
			var tPrev = TangentAtPath( path, closed ? (i - 1 + n) % n : Math.Max( i - 1, 0 ), closed );
			var tNext = TangentAtPath( path, closed ? (i + 1) % n : Math.Min( i + 1, n - 1 ), closed );
			var curve = tNext - tPrev;
			Vec3 normal;
			if ( curve.LengthSquared > 1e-8f )
				normal = curve.Normal;
			else if ( prevNormal.LengthSquared > 1e-12f )
				normal = prevNormal;
			else
			{
				normal = sketch.Plane.XAxis - tangent * Vec3.Dot( sketch.Plane.XAxis, tangent );
				normal = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 1, 0, 0 );
			}
			// Keep the normal square to the tangent (central-difference curvature is only
			// approximately perpendicular on uneven stations).
			normal = (normal - tangent * Vec3.Dot( normal, tangent ));
			normal = normal.LengthSquared > 1e-12f ? normal.Normal : prevNormal;
			prevNormal = normal;
			var binormal = Vec3.Cross( tangent, normal );
			frames.Add( (normal, binormal, tangent) );
		}

		return frames;
	}

	static Vec3 TangentAtPath( List<Vec3> path, int i, bool closed )
	{
		var n = path.Count;
		if ( n < 2 )
			return new Vec3( 1, 0, 0 );
		if ( closed )
		{
			var d = path[(i + 1) % n] - path[((i - 1) % n + n) % n];
			return d.LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : d.Normal;
		}
		if ( i == 0 )
		{
			var d = path[1] - path[0];
			return d.LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : d.Normal;
		}
		if ( i == n - 1 )
		{
			var d = path[n - 1] - path[n - 2];
			return d.LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : d.Normal;
		}
		var dd = path[i + 1] - path[i - 1];
		return dd.LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : dd.Normal;
	}

	/// <summary>Resample a polyline to exactly <paramref name="count"/> stations, evenly by
	/// arc length. Open paths keep both ends; closed paths wrap (no duplicated end point).</summary>
	internal static List<Vec3> ResamplePath( List<Vec3> path, int count, bool closed )
	{
		var n = path.Count;
		var cum = new float[n + 1];
		for ( var i = 1; i < n; i++ )
			cum[i] = cum[i - 1] + (path[i] - path[i - 1]).Length;
		var total = cum[n - 1];
		if ( closed )
			total += (path[0] - path[n - 1]).Length;
		if ( total < 1e-9f )
			return new List<Vec3>( path );
		var result = new List<Vec3>( count );
		for ( var k = 0; k < count; k++ )
		{
			var target = closed ? total * k / count : total * k / (count - 1);
			if ( !closed && k == count - 1 )
			{
				result.Add( path[n - 1] );
				continue;
			}
			var seg = 0;
			while ( seg + 1 < n && cum[seg + 1] < target )
				seg++;
			// Closing segment for a closed path.
			if ( closed && target >= cum[n - 1] )
			{
				var span = total - cum[n - 1];
				var t = span > 1e-9f ? (target - cum[n - 1]) / span : 0f;
				result.Add( path[n - 1] + (path[0] - path[n - 1]) * t );
				continue;
			}
			var span2 = cum[seg + 1] - cum[seg];
			var tt = span2 > 1e-9f ? (target - cum[seg]) / span2 : 0f;
			result.Add( path[seg] + (path[seg + 1] - path[seg]) * tt );
		}
		return result;
	}

	/// <summary>Arc-length fraction of each path station, 0 at the start. Closed paths use
	/// the full loop length including the closing edge.</summary>
	internal static List<float> ArcFractions( List<Vec3> path, bool closed )
	{
		var n = path.Count;
		var cum = new float[n];
		for ( var i = 1; i < n; i++ )
			cum[i] = cum[i - 1] + (path[i] - path[i - 1]).Length;
		var total = cum[n - 1];
		if ( closed )
			total += (path[0] - path[n - 1]).Length;
		if ( total < 1e-9f )
			return path.Select( _ => 0f ).ToList();
		return cum.Select( c => c / total ).ToList();
	}

	/// <summary>Rotate a 2D loop geometrically about its centre by <paramref name="angleRad"/>.</summary>
	internal static List<Vec2> RotateLoopGeom( List<Vec2> loop, Vec2 centre, float angleRad )
	{
		var cos = MathF.Cos( angleRad );
		var sin = MathF.Sin( angleRad );
		var result = new List<Vec2>( loop.Count );
		foreach ( var p in loop )
		{
			var d = p - centre;
			result.Add( centre + new Vec2( d.x * cos - d.y * sin, d.x * sin + d.y * cos ) );
		}
		return result;
	}

	/// <summary>Bend-radius guard: at every interior station, the bend radius implied by the
	/// turn between neighbouring stations must exceed the profile's extent toward the
	/// centre of curvature, or the inner wall folds through itself. Fails loudly with the
	/// station, radius, and extent instead of building silent garbage.</summary>
	void CheckBend( List<Vec3> path, List<(Vec3 U, Vec3 V, Vec3 Tangent)> frames, bool closed,
		List<Vec2> loop, Vec2 centre, List<(float T, float Scale)> taper, List<float> arcFracs, bool useArc,
		float holonomy, float twistDegrees, float startAngleRad )
	{
		var n = path.Count;
		for ( var i = 0; i < n; i++ )
		{
			if ( !closed && (i == 0 || i == n - 1) )
				continue;
			var prev = path[(i - 1 + n) % n];
			var next = path[(i + 1) % n];
			if ( !closed && (i == 0 || i == n - 1) )
				continue;
			var tIn = (path[i] - prev).LengthSquared < 1e-12f ? frames[i].Tangent : (path[i] - prev).Normal;
			var tOut = (next - path[i]).LengthSquared < 1e-12f ? frames[i].Tangent : (next - path[i]).Normal;
			var cosTheta = Math.Clamp( Vec3.Dot( tIn, tOut ), -1f, 1f );
			var theta = MathF.Acos( cosTheta );
			if ( theta < 1e-4f )
				continue;
			var chord = (next - prev).Length;
			var radius = chord / (2f * MathF.Sin( theta ));
			var centreDir = tOut - tIn;
			if ( centreDir.LengthSquared < 1e-12f )
				continue;
			centreDir = centreDir.Normal;
			var frac = useArc ? arcFracs[i] : (closed ? i / (float)n : (n > 1 ? i / (float)(n - 1) : 0f));
			var scale = taper is null ? 1f : TaperAt( taper, frac );
			var (u, v, _) = frames[i];
			var angle = twistDegrees * MathF.PI / 180f * frac - holonomy * frac + startAngleRad;
			var cos = MathF.Cos( angle );
			var sin = MathF.Sin( angle );
			var tu = u * cos + v * sin;
			var tv = v * cos - u * sin;
			var extent = 0f;
			foreach ( var p in loop )
			{
				var local = p - centre;
				var off = tu * local.x + tv * local.y;
				extent = MathF.Max( extent, Vec3.Dot( off, centreDir ) * scale );
			}
			if ( extent > 0f && radius <= extent )
				Fail(
					$"This sweep folds at station {i}: the bend radius is {radius:0.##} but the profile reaches {extent:0.##} toward the centre of the bend.",
					"On the inside of a bend, rings converge; when the bend radius drops below the profile's extent in that direction, the inner wall passes through itself.",
					"Widen the bend in the path",
					"Shrink the profile, or turn Bend guard off to build it anyway" );
		}
	}

	/// <summary>
	/// Rings for one loop (outer or hole) from shared frames. A closed sweep distributes
	/// -holonomy * i/N across the rings, so the ring beside the seam meets ring 0 with no
	/// twist kink; an open sweep distributes the Twist parameter instead. The taper scale,
	/// when given, sizes each ring about its path station.
	/// </summary>
	internal static List<List<Vec3>> RingsForLoop( List<Vec2> loop, Vec2 centre, List<Vec3> path,
		List<(Vec3 U, Vec3 V, Vec3 Tangent)> frames, float holonomy, float twistDegrees,
		List<(float T, float Scale)> taper, bool closed ) =>
		RingsForLoop( loop, centre, path, frames, holonomy, twistDegrees, taper, closed,
			ArcFractions( path, closed ), false, null, null, 0f );

	internal static List<List<Vec3>> RingsForLoop( List<Vec2> loop, Vec2 centre, List<Vec3> path,
		List<(Vec3 U, Vec3 V, Vec3 Tangent)> frames, float holonomy, float twistDegrees,
		List<(float T, float Scale)> taper, bool closed, List<float> arcFracs, bool useArc,
		List<float> railScales, List<Vec3> railShifts, float startAngleRad )
	{
		var rings = new List<List<Vec3>>( path.Count );

		for ( var i = 0; i < path.Count; i++ )
		{
			// Twist accumulates along the path rather than being applied at the end, so a swept
			// square with 90 degrees of twist turns steadily instead of shearing at the last ring.
			var frac = useArc && arcFracs is not null ? arcFracs[i] : (closed ? i / (float)path.Count : (path.Count > 1 ? i / (float)(path.Count - 1) : 0f));
			var angle = twistDegrees * MathF.PI / 180f * frac - holonomy * frac + startAngleRad;
			var cos = MathF.Cos( angle );
			var sin = MathF.Sin( angle );

			var (u, v, _) = frames[i];
			var tu = u * cos + v * sin;
			var tv = v * cos - u * sin;
			var scale = taper is null ? 1f : TaperAt( taper, frac );
			if ( railScales is not null && i < railScales.Count )
				scale *= railScales[i];
			var origin = path[i];
			if ( railShifts is not null && i < railShifts.Count )
				origin = origin + railShifts[i];

			var ring = new List<Vec3>( loop.Count );

			foreach ( var p in loop )
			{
				var local = p - centre;
				ring.Add( origin + (tu * local.x + tv * local.y) * scale );
			}

			rings.Add( ring );
		}

		return rings;
	}

	/// <summary>Per-station rail scales/shifts for a two-rail (or single-rail) sweep. A rail
	/// is an open chain in another sketch; at each station the rail is intersected with the
	/// station's frame plane. One rail scales the profile so its extent in the rail
	/// direction reaches the rail; two rails scale and shift it so it spans rail to rail.
	/// A station whose frame plane a rail never crosses fails loudly, naming the station.</summary>
	(List<float>, List<Vec3>) RailScalesAndShifts( FeatureContext ctx, List<Vec3> path,
		List<(Vec3 U, Vec3 V, Vec3 Tangent)> frames, bool closed, List<Vec2> loop, Vec2 centre,
		float holonomy, float twistDegrees, List<float> arcFracs, bool useArc, float startAngleRad,
		List<(float T, float Scale)> taper )
	{
		if ( string.IsNullOrEmpty( RailSketchId ) )
			return (null, null);
		var (rail1, rail1Name) = ResolveRail( ctx, RailSketchId );
		List<Vec3> rail2 = null;
		var rail2Name = "";
		if ( !string.IsNullOrEmpty( Rail2SketchId ) )
			(rail2, rail2Name) = ResolveRail( ctx, Rail2SketchId );
		var scales = new List<float>( path.Count );
		var shifts = new List<Vec3>( path.Count );
		for ( var i = 0; i < path.Count; i++ )
		{
			var tangent = frames[i].Tangent;
			var p1 = IntersectPlane( rail1, path[i], tangent );
			if ( p1 is null )
				Fail(
					$"Rail '{rail1Name}' does not cross the sweep's frame plane at station {i}.",
					"A rail must cross every station plane to scale the profile there.",
					"Extend the rail past that station",
					"Remove the rail to sweep without it" );
			if ( rail2 is null )
			{
				var dir = p1.Value - path[i];
				var inPlane = dir - tangent * Vec3.Dot( dir, tangent );
				if ( inPlane.LengthSquared < 1e-12f )
				{
					scales.Add( 1f );
					shifts.Add( Vec3.Zero );
					continue;
				}
				var d = inPlane.Normal;
				var (tu, tv) = RailAxes( frames[i], i, path.Count, closed, holonomy, twistDegrees, arcFracs, useArc, startAngleRad );
				var extent = 0f;
				foreach ( var p in loop )
				{
					var local = p - centre;
					extent = MathF.Max( extent, Vec3.Dot( tu * local.x + tv * local.y, d ) );
				}
				if ( extent <= 1e-9f )
				{
					scales.Add( 1f );
					shifts.Add( Vec3.Zero );
					continue;
				}
				var frac1 = useArc && arcFracs is not null ? arcFracs[i] : (closed ? i / (float)path.Count : (path.Count > 1 ? i / (float)(path.Count - 1) : 0f));
				var taperScale1 = taper is null ? 1f : TaperAt( taper, frac1 );
				scales.Add( inPlane.Length / (extent * taperScale1) );
				shifts.Add( Vec3.Zero );
			}
			else
			{
				var p2 = IntersectPlane( rail2, path[i], tangent );
				if ( p2 is null )
					Fail(
						$"Rail '{rail2Name}' does not cross the sweep's frame plane at station {i}.",
						"A rail must cross every station plane to scale the profile there.",
						"Extend the rail past that station",
						"Remove the rail to sweep without it" );
				var spanVec = p2.Value - p1.Value;
				var span = spanVec.Length;
				if ( span < 1e-9f )
				{
					scales.Add( 1f );
					shifts.Add( Vec3.Zero );
					continue;
				}
				var d = spanVec.Normal;
				var (tu, tv) = RailAxes( frames[i], i, path.Count, closed, holonomy, twistDegrees, arcFracs, useArc, startAngleRad );
				var lo = float.MaxValue;
				var hi = float.MinValue;
				foreach ( var p in loop )
				{
					var local = p - centre;
					var dot = Vec3.Dot( tu * local.x + tv * local.y, d );
					lo = MathF.Min( lo, dot );
					hi = MathF.Max( hi, dot );
				}
				var extent = hi - lo;
				if ( extent <= 1e-9f )
				{
					scales.Add( 1f );
					shifts.Add( Vec3.Zero );
					continue;
				}
				var frac2 = useArc && arcFracs is not null ? arcFracs[i] : (closed ? i / (float)path.Count : (path.Count > 1 ? i / (float)(path.Count - 1) : 0f));
				var taperScale2 = taper is null ? 1f : TaperAt( taper, frac2 );
				var finalScale = span / extent;
				scales.Add( finalScale / taperScale2 );
				var mid = (p1.Value + p2.Value) * 0.5f;
				// Shift so the profile's mid-extent in the rail direction lands on the rails' midpoint.
				var profileMid = (lo + hi) * 0.5f;
				shifts.Add( mid - path[i] - d * (profileMid * finalScale) );
			}
		}
		return (scales, shifts);
	}

	(Vec3, Vec3) RailAxes( (Vec3 U, Vec3 V, Vec3 Tangent) frame, int i, int count, bool closed,
		float holonomy, float twistDegrees, List<float> arcFracs, bool useArc, float startAngleRad )
	{
		var frac = useArc && arcFracs is not null ? arcFracs[i] : (closed ? i / (float)count : (count > 1 ? i / (float)(count - 1) : 0f));
		var angle = twistDegrees * MathF.PI / 180f * frac - holonomy * frac + startAngleRad;
		var cos = MathF.Cos( angle );
		var sin = MathF.Sin( angle );
		return (frame.U * cos + frame.V * sin, frame.V * cos - frame.U * sin);
	}

	(List<Vec3>, string) ResolveRail( FeatureContext ctx, string id )
	{
		if ( !ctx.Sketches.TryGetValue( id, out var railSketch ) )
			Fail(
				$"No sketch named '{id}' in the tree (have: {SketchList( ctx )})",
				"The rail sketch this sweep was pointed at does not exist at this point in the tree.",
				"Check the spelling of the rail sketch id",
				"Remove the rail to sweep without it" );
		var chain = SketchChain.Walk( railSketch );
		if ( chain.Count < 2 )
			Fail(
				$"Rail sketch '{SketchNameFor( ctx, railSketch )}' has no connected chain to sweep along.",
				"The rail sketch's curves do not join into a walk of two or more points.",
				"Join the rail curves end to end",
				"Draw a single line or polyline as the rail" );
		return (chain.Select( railSketch.Plane.ToWorld ).ToList(), SketchNameFor( ctx, railSketch ));
	}

	static Vec3? IntersectPlane( List<Vec3> polyline, Vec3 point, Vec3 normal )
	{
		for ( var i = 0; i + 1 < polyline.Count; i++ )
		{
			var a = polyline[i];
			var b = polyline[i + 1];
			var da = Vec3.Dot( a - point, normal );
			var db = Vec3.Dot( b - point, normal );
			if ( MathF.Abs( da ) < 1e-9f )
				return a;
			if ( MathF.Abs( db ) < 1e-9f )
				return b;
			if ( da * db < 0f )
			{
				var t = da / (da - db);
				return a + (b - a) * t;
			}
		}
		return null;
	}

	/// <summary>
	/// Parse a taper string ("0:1,0.5:1.4,1:0.2") into sorted (t, scale) pairs. Empty is
	/// null (no taper). Anything else malformed fails loudly: a taper that silently
	/// ignored a typo would ship the wrong shape.
	/// </summary>
	internal static List<(float T, float Scale)> ParseTaper( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return null;

		var pairs = new List<(float, float)>();

		foreach ( var part in text.Split( ';', ',' ) )
		{
			var kv = part.Split( ':' );

			float t = 0f, scale = 0f;

			if ( kv.Length != 2
				|| !float.TryParse( kv[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t )
				|| !float.TryParse( kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale ) )
				FailOn( "Taper (t:scale,…)",
					$"Taper '{text}' is not a list of t:scale pairs.",
					"A taper is t:scale pairs in order along the sweep, e.g. 0:1,0.5:1.4,1:0.2.",
					"Fix the taper string", "Clear the taper to sweep at a constant size" );

			if ( t < 0f || t > 1f || scale <= 0f )
				FailOn( "Taper (t:scale,…)",
					$"Taper '{text}' has a t outside 0..1 or a non-positive scale.",
					"t runs 0 at the start of the sweep to 1 at the end, and scale must be positive.",
					"Fix the taper string", "Clear the taper to sweep at a constant size" );

			pairs.Add( (t, scale) );
		}

		pairs.Sort( (a, b) => a.Item1.CompareTo( b.Item1 ) );

		return pairs;
	}

	internal static float TaperAt( List<(float T, float Scale)> taper, float frac )
	{
		if ( frac <= taper[0].T )
			return taper[0].Scale;

		for ( var i = 1; i < taper.Count; i++ )
		{
			if ( frac <= taper[i].T )
			{
				var span = taper[i].T - taper[i - 1].T;
				var t = span > 1e-9f ? (frac - taper[i - 1].T) / span : 0f;
				return taper[i - 1].Scale + (taper[i].Scale - taper[i - 1].Scale) * t;
			}
		}

		return taper[^1].Scale;
	}

	/// <summary>
	/// Prepare a hole loop for skinning alongside its outer loop: same traversal direction
	/// as the outer (ProfileFinder stores holes wound opposite), resampled to the outer's
	/// point count, and rotated so it starts at the angle the outer loop starts at about
	/// the shared centre. That pairing is what lets the annular end caps tile index for
	/// index without twist. Only the first hole is ever prepared — see Execute.
	/// </summary>
	internal static List<Vec2> PrepareHoleLoop( List<Vec2> outer, List<Vec2> hole, int count )
	{
		var centre = CentreOf( outer );
		var oriented = new List<Vec2>( hole );

		if ( MathF.Sign( ProfileFinder.SignedArea( oriented ) ) != MathF.Sign( ProfileFinder.SignedArea( outer ) ) )
			oriented.Reverse();

		var target = MathF.Atan2( outer[0].y - centre.y, outer[0].x - centre.x );
		var best = 0;
		var bestD = float.MaxValue;

		for ( var i = 0; i < oriented.Count; i++ )
		{
			var a = MathF.Atan2( oriented[i].y - centre.y, oriented[i].x - centre.x );
			var d = MathF.Abs( a - target );

			if ( d > MathF.PI )
				d = 2f * MathF.PI - d;

			if ( d < bestD )
			{
				bestD = d;
				best = i;
			}
		}

		var rotated = new List<Vec2>( oriented.Count );

		for ( var i = 0; i < oriented.Count; i++ )
			rotated.Add( oriented[(best + i) % oriented.Count] );

		return Skinner.Resample( rotated, count );
	}

	/// <summary>
	/// Turn two axes by the smallest rotation carrying <paramref name="from"/> onto
	/// <paramref name="to"/>. Rodrigues, with the degenerate cases taken out first.
	/// </summary>
	static void Rotate( ref Vec3 u, ref Vec3 v, Vec3 from, Vec3 to )
	{
		var axis = Vec3.Cross( from, to );
		var sin = axis.Length;
		var cos = Vec3.Dot( from, to );

		// Already aligned: nothing to do. Exactly reversed: the rotation is a half turn about any
		// perpendicular axis, and picking one arbitrarily is the best that can be done — a path
		// that doubles straight back on itself has no continuous frame through the reversal.
		if ( sin < 1e-9f )
		{
			if ( cos > 0f )
				return;

			u = -u;
			v = -v;
			return;
		}

		axis = axis / sin;

		u = Turn( u, axis, cos, sin );
		v = Turn( v, axis, cos, sin );
	}

	static Vec3 Turn( Vec3 x, Vec3 axis, float cos, float sin ) =>
		x * cos + Vec3.Cross( axis, x ) * sin + axis * (Vec3.Dot( axis, x ) * (1f - cos));

	/// <summary>
	/// Rotate a 2D loop's point order so it starts at the given angle about its centre.
	/// Resample always starts at the loop's own first point, so without this two sections
	/// resampled independently start at unrelated angles and their skin shears.
	/// </summary>
	internal static List<Vec2> RotateLoopToAngle( List<Vec2> loop, Vec2 centre, float target )
	{
		var best = 0;
		var bestD = float.MaxValue;

		for ( var i = 0; i < loop.Count; i++ )
		{
			var a = MathF.Atan2( loop[i].y - centre.y, loop[i].x - centre.x );
			var d = MathF.Abs( a - target );

			if ( d > MathF.PI )
				d = 2f * MathF.PI - d;

			if ( d < bestD )
			{
				bestD = d;
				best = i;
			}
		}

		var rotated = new List<Vec2>( loop.Count );

		for ( var i = 0; i < loop.Count; i++ )
			rotated.Add( loop[(best + i) % loop.Count] );

		return rotated;
	}

	internal static Vec3 CentreOf3( List<Vec3> ring )
	{
		var centre = Vec3.Zero;

		foreach ( var p in ring )
			centre += p;

		return centre / ring.Count;
	}

	/// <summary>
	/// The path, as world points. Walked out of a sketch's curves the way a loop is, except that a
	/// path is normally open — so the walk starts at a free end rather than anywhere.
	/// </summary>
	List<Vec3> ResolvePath( FeatureContext ctx, Sketch profileSketch )
	{
		if ( !string.IsNullOrEmpty( PathSplineId ) && !ctx.Splines.TryGetValue( PathSplineId, out var _ ) )
			Fail(
				$"No spline named '{PathSplineId}' in the tree",
				"The path spline this sweep was pointed at does not exist at this point in the tree.",
				"Check the spelling of the path spline id",
				"Leave the path spline empty to use a path sketch instead" );

		if ( !string.IsNullOrEmpty( PathSplineId ) && ctx.Splines.TryGetValue( PathSplineId, out var named ) )
			return SplinePath( named );

		var pathSketch = ResolvePathSketch( ctx, profileSketch );

		// No second sketch, but a spline: that is the path. Drawn in space, it is the more natural
		// way to describe where a sweep goes than a flat sketch ever was.
		if ( pathSketch is null && ctx.LastSplineId is not null && ctx.Splines.TryGetValue( ctx.LastSplineId, out var last ) )
			return SplinePath( last );

		if ( pathSketch is null )
		{
			Fail(
				"A sweep needs a second sketch for its path.",
				"A sweep takes a closed profile and an open path, and only one sketch is in the tree.",
				"Add a second Sketch and draw the path",
				"Move this feature below both sketches" );
		}

		if ( ReferenceEquals( pathSketch, profileSketch ) )
		{
			Fail(
				"The path and the profile cannot be the same sketch.",
				"Both roles resolved to one sketch, so there is nothing to sweep along.",
				"Add a second Sketch for the path",
				"Draw the path as an open chain, not a closed region" );
		}

		var chain = SketchChain.Walk( pathSketch );

		if ( chain.Count < 2 )
		{
			Fail(
				"The path sketch has no connected chain of curves to sweep along.",
				"The path sketch's curves do not join into a walk of two or more points.",
				"Join the path curves end to end",
				"Draw a single line or polyline as the path" );
		}

		return chain.Select( pathSketch.Plane.ToWorld ).ToList();
	}

	/// <summary>A spline as a sweep path. A closed spline gets its first point repeated at the end,
	/// which is how Execute recognises a closed path.</summary>
	static List<Vec3> SplinePath( Spline3 spline )
	{
		var path = spline.Sample( 8 );

		if ( spline.Closed && path.Count > 2 )
			path.Add( path[0] );

		return path;
	}

	/// <summary>
	/// Which sketch holds the profile.
	///
	/// NOT "THE MOST RECENT", which is what every other sketch-consuming feature means by an unset
	/// reference. A sweep takes two sketches and there is no reliable order to them — a path is
	/// drawn after its profile about as often as before it, so "the last one" is a coin flip that
	/// picks the path half the time and then fails with "no closed region", which reads as the
	/// sketch being broken rather than as the two being the wrong way round.
	///
	/// The two roles are distinguishable by what is IN them: a profile is closed and a path is not.
	/// So the profile is the most recent sketch that actually has a closed region, and the path is
	/// whatever else is left. An explicit SketchFeatureId still wins over all of it.
	/// </summary>
	Sketch ResolveProfileSketch( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( SketchFeatureId ) )
			return ResolveSketch( ctx );

		if ( ctx.Sketches.Count == 0 )
		{
			Fail(
				"There is no sketch to use — add a Sketch feature first",
				"A sweep needs a profile sketch, and the tree has none at this point.",
				"Add a Sketch feature and draw a closed region",
				"Move this feature below an existing Sketch in the tree" );
		}

		var closed = ctx.Sketches.Values.LastOrDefault( s => ProfileFinder.Find( s ).Profiles.Count > 0 );

		return closed ?? ctx.Sketches.Values.Last();
	}

	Sketch ResolvePathSketch( FeatureContext ctx, Sketch profileSketch )
	{
		if ( !string.IsNullOrEmpty( PathSketchId ) )
		{
			if ( !ctx.Sketches.TryGetValue( PathSketchId, out var named ) )
			Fail(
				$"No sketch named '{PathSketchId}' in the tree (have: {SketchList( ctx )})",
				"The path sketch this sweep was pointed at does not exist at this point in the tree.",
				"Check the spelling of the path sketch id",
				"Pick a sketch that sits above this sweep" );
			return named;
		}

		// Whatever is not the profile. Preferring one with no closed region of its own keeps a
		// studio holding two closed sketches from sweeping one round the other by accident.
		var open = ctx.Sketches.Values.LastOrDefault( s =>
			!ReferenceEquals( s, profileSketch ) && ProfileFinder.Find( s ).Profiles.Count == 0 );

		return open ?? ctx.Sketches.Values.LastOrDefault( s => !ReferenceEquals( s, profileSketch ) );
	}
}

/// <summary>
/// Skin a surface between two or more section sketches. Onshape's Loft.
///
/// Sections are resampled to a common point count and rotationally aligned before anything is
/// stitched — see Skinner.Resample and Skinner.Align for why both are needed and what goes wrong
/// without them.
///
/// RULED BETWEEN SECTIONS BY DEFAULT, NOT SMOOTHLY THROUGH THEM. Each pair of neighbouring
/// sections is joined by a ruled surface, so a three-section loft has a crease at the middle one.
/// Turn Smooth loft on to insert Catmull-Rom interpolated rings between sections instead; the
/// ruled mode stays the default so existing lofts do not move.
/// </summary>
public sealed class LoftFeature : SketchConsumingFeature
{
	public override string TypeName => "Loft";

	/// <summary>
	/// The sketches to loft between, in order. Fewer than two means "every sketch available", which
	/// is what a loft with nothing configured should do rather than fail.
	/// </summary>
	public List<string> Sections = new();

	/// <summary>How many points each section is resampled to. More is smoother and heavier; the
	/// default is enough for a circle to read as round without subdivision.</summary>
	public readonly IntParam Segments = new( "Segments", 24, 3, 512 );

	public readonly BoolParam Closed = new( "Closed", false );

	/// <summary>Scale between sections as "t:scale,…" pairs, t in 0..1 from the first to the
	/// last section — same spelling as Sweep's taper. Empty means no taper.</summary>
	public readonly StringParam Taper = new( "Taper (t:scale,…)", "" );

	/// <summary>Smooth loft: insert interpolated rings between sections (Catmull-Rom through
	/// corresponding points) instead of ruled straight spans. Off keeps the old ruled loft,
	/// so existing lofts do not move.</summary>
	public readonly BoolParam SmoothLoft = new( "Smooth loft", false );

	/// <summary>Interpolated rings inserted between each pair of sections when Smooth loft is on.</summary>
	public readonly IntParam SmoothRings = new( "Smooth rings", 2, 0, 16 );

	/// <summary>Rotate every section about its own centroid, in degrees, before skinning.
	/// The connector-point override for a loft whose automatic alignment guessed wrong.</summary>
	public readonly FloatParam StartAngle = new( "Start angle", 0f, unit: "deg" );

	public readonly BoolParam BendGuard = new( "Bend guard", true );

	/// <summary>Feature id of a sketch whose open chain is a guide rail: at each section's
	/// plane the rail intersection scales the section so its perimeter reaches the rail.</summary>
	public string GuideSketchId = "";

	/// <summary>Feature id of a sketch whose open chain is a centreline: at each section's
	/// plane the centreline intersection becomes the section's new centroid.</summary>
	public string CentrelineSketchId = "";

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Segments, Closed, Taper, SmoothLoft, SmoothRings, StartAngle, Result, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material, BendGuard };

	protected override void Execute( FeatureContext ctx )
	{
		var sketches = ResolveSections( ctx );

		if ( sketches.Count < 2 )
		{
			Fail(
				"A loft needs at least two sections.",
				$"Only {sketches.Count} section sketch(es) are available at this point in the tree.",
				"Add a second Sketch for the other section",
				"Move this feature below both section sketches" );
		}

		var count = Segments.Clamped;
		var taper = SweepFeature.ParseTaper( Taper.Value );
		var rings = new List<List<Vec3>>( sketches.Count );
		// Per hole, per section — the single-hole path reads holeRingsByHole[0], so the
		// old single-ring list is gone rather than kept alongside as a second truth.
		var holeRingsByHole = new List<List<List<Vec3>>>();
		// The FINAL 2D loops per section (after start-angle rotation), for triangulating
		// the two end caps of a multi-hole loft.
		var sectionOuters = new List<List<Vec2>>();
		var sectionHoles = new List<List<List<Vec2>>>();
		var holeCount = -1;

		// Holes change the alignment strategy: Skinner.Align rotates each section to its
		// neighbour in 3D, which would break the index-for-index pairing the annular caps
		// need. A holed loft therefore aligns every section by start ANGLE about its own
		// centroid instead — the same correspondence, decided in 2D before any 3D mapping.
		// A loft with no holes keeps the old Align path exactly, so existing lofts do not move.
		var anyHoles = false;

		foreach ( var sketch in sketches )
		{
			var found = ProfileFinder.Find( sketch );

			if ( found.Profiles.Count == 0 )
				Fail(
					"One of the loft's sections has no closed region in it.",
					"Every loft section has to be a closed loop so it can be skinned to the next.",
					"Close that section's curves into a loop",
					"Remove that sketch from the loft" );

			if ( found.Profiles[0].HasHoles )
			{
				anyHoles = true;

				if ( holeCount < 0 )
					holeCount = found.Profiles[0].Holes.Count;
				else if ( found.Profiles[0].Holes.Count != holeCount )
					Fail(
						"Every loft section needs the same holes.",
						$"One section has {found.Profiles[0].Holes.Count} hole(s) and another has {holeCount}.",
						"Give every section the same holes", "Remove the holes from every section" );
			}
		}

		var firstAngle = 0f;

		foreach ( var sketch in sketches )
		{
			var found = ProfileFinder.Find( sketch );
			var profile = found.Profiles[0];
			var outer = Skinner.Resample( profile.Outer, count );
			var centre = SweepFeature.CentreOf( profile.Outer );

			if ( anyHoles )
			{
				if ( !profile.HasHoles )
					Fail(
						"Every loft section needs the same hole.",
						"One section has a hole and another does not, so there is no tunnel to skin between them.",
						"Give every section the same hole", "Remove the hole from every section" );

				if ( profile.Holes.Count != holeCount )
					Fail(
						"Every loft section needs the same holes.",
						$"One section has {profile.Holes.Count} hole(s) and another has {holeCount}.",
						"Give every section the same holes", "Remove the holes from every section" );

				if ( rings.Count == 0 )
				{
					firstAngle = MathF.Atan2( outer[0].y - centre.y, outer[0].x - centre.x );
				}
				else
				{
					outer = SweepFeature.RotateLoopToAngle( outer, centre, firstAngle );
				}

				// The FINAL 2D loops — after the start-angle rotation — are what the end
				// caps are triangulated from, so the triangulation's indices map straight
				// onto the ring vertices at that end.
				var finalHoles = new List<List<Vec2>>( profile.Holes.Count );

				for ( var h = 0; h < profile.Holes.Count; h++ )
				{
					var hole = SweepFeature.PrepareHoleLoop( profile.Outer, profile.Holes[h], count );

					// About the OUTER centre, not the hole's own: the pairing is by angle about
						// the shared centre, which is what the annular caps are tiled on.
					if ( rings.Count > 0 )
						hole = SweepFeature.RotateLoopToAngle( hole, centre, firstAngle );

					finalHoles.Add( hole );

					if ( holeRingsByHole.Count <= h )
						holeRingsByHole.Add( new List<List<Vec3>>( sketches.Count ) );

					var holeRing3 = hole.Select( sketch.Plane.ToWorld ).ToList();
					if ( StartAngle.Value != 0f )
					{
						var sc = SweepFeature.CentreOf3( outer.Select( sketch.Plane.ToWorld ).ToList() );
						holeRing3 = RotateRingGeom( holeRing3, sc, sketch.Plane.Normal, StartAngle.Value * MathF.PI / 180f );
					}
					holeRingsByHole[h].Add( holeRing3 );
				}

				sectionOuters.Add( outer );
				sectionHoles.Add( finalHoles );
			}

			var ring = outer.Select( sketch.Plane.ToWorld ).ToList();

			if ( !anyHoles && rings.Count > 0 )
				ring = Skinner.Align( ring, rings[^1] );

			if ( StartAngle.Value != 0f )
			{
				var c = SweepFeature.CentreOf3( ring );
				ring = RotateRingGeom( ring, c, sketch.Plane.Normal, StartAngle.Value * MathF.PI / 180f );
			}

			rings.Add( ring );
		}

		// Taper sizes each section about its own centroid, after alignment, so a tapered
		// loft keeps the correspondence the skinning was built on.
		if ( taper is not null )
		{
			for ( var i = 0; i < rings.Count; i++ )
			{
				var frac = Closed.Value ? i / (float)rings.Count : (rings.Count > 1 ? i / (float)(rings.Count - 1) : 0f);
				var scale = SweepFeature.TaperAt( taper, frac );
				var c = SweepFeature.CentreOf3( rings[i] );

				for ( var k = 0; k < rings[i].Count; k++ )
					rings[i][k] = c + (rings[i][k] - c) * scale;

				if ( anyHoles )
				{
					// About the section centroid, not the hole's own: tapering shrinks the
					// whole section toward its centre, hole position included. Taper is a
					// uniform scale, so it cannot change which bridge the cap triangulation
					// needs — the untapered loops' triangulation stays valid.
					foreach ( var holeRing in holeRingsByHole )
						for ( var k = 0; k < holeRing[i].Count; k++ )
							holeRing[i][k] = c + (holeRing[i][k] - c) * scale;
				}
			}
		}

		// Guide rail / centreline (wave 3), applied to the section rings before skinning.
		if ( !string.IsNullOrEmpty( GuideSketchId ) || !string.IsNullOrEmpty( CentrelineSketchId ) )
		{
			List<Vec3> guide = null;
			List<Vec3> centreline = null;
			if ( !string.IsNullOrEmpty( GuideSketchId ) )
				guide = ResolveLoftRail( ctx, GuideSketchId );
			if ( !string.IsNullOrEmpty( CentrelineSketchId ) )
				centreline = ResolveLoftRail( ctx, CentrelineSketchId );
			for ( var i = 0; i < rings.Count; i++ )
			{
				var centroid = SweepFeature.CentreOf3( rings[i] );
				var normal = sketches[i].Plane.Normal;
				if ( guide is not null )
				{
					var hit = IntersectPlaneLoft( guide, centroid, normal );
					if ( hit is null )
						Fail(
							$"Guide rail '{GuideSketchId}' does not cross section {i}'s plane.",
							"A guide rail must cross every section plane to scale that section.",
							"Extend the guide rail past that section",
							"Remove the guide rail to loft without it" );
					var dir = hit.Value - centroid;
					if ( dir.LengthSquared > 1e-12f )
					{
						var d = dir.Normal;
						var radiusInDir = 0f;
						foreach ( var p in rings[i] )
							radiusInDir = MathF.Max( radiusInDir, Vec3.Dot( p - centroid, d ) );
						if ( radiusInDir > 1e-9f )
						{
							var scale = dir.Length / radiusInDir;
							for ( var k = 0; k < rings[i].Count; k++ )
								rings[i][k] = centroid + (rings[i][k] - centroid) * scale;
							foreach ( var holeRing in holeRingsByHole )
								for ( var k = 0; k < holeRing[i].Count; k++ )
									holeRing[i][k] = centroid + (holeRing[i][k] - centroid) * scale;
						}
					}
				}
				if ( centreline is not null )
				{
					var hit = IntersectPlaneLoft( centreline, centroid, normal );
					if ( hit is null )
						Fail(
							$"Centreline '{CentrelineSketchId}' does not cross section {i}'s plane.",
							"A centreline must cross every section plane to move that section.",
							"Extend the centreline past that section",
							"Remove the centreline to loft without it" );
					var shift = hit.Value - centroid;
					for ( var k = 0; k < rings[i].Count; k++ )
						rings[i][k] = rings[i][k] + shift;
					foreach ( var holeRing in holeRingsByHole )
						for ( var k = 0; k < holeRing[i].Count; k++ )
							holeRing[i][k] = holeRing[i][k] + shift;
				}
			}
		}

		// Bend guard for lofts: the centroid polyline's bend radius must exceed each
		// section's extent toward the centre of curvature, or the loft folds there.
		if ( BendGuard.Value )
		{
			var n = rings.Count;
			for ( var i = 0; i < n; i++ )
			{
				if ( !Closed.Value && (i == 0 || i == n - 1) )
					continue;
				var prev = SweepFeature.CentreOf3( rings[(i - 1 + n) % n] );
				var next = SweepFeature.CentreOf3( rings[(i + 1) % n] );
				var cur = SweepFeature.CentreOf3( rings[i] );
				var tIn = (cur - prev).LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : (cur - prev).Normal;
				var tOut = (next - cur).LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : (next - cur).Normal;
				var theta = MathF.Acos( Math.Clamp( Vec3.Dot( tIn, tOut ), -1f, 1f ) );
				if ( theta < 1e-4f )
					continue;
				var radius = (next - prev).Length / (2f * MathF.Sin( theta ));
				var centreDir = tOut - tIn;
				if ( centreDir.LengthSquared < 1e-12f )
					continue;
				centreDir = centreDir.Normal;
				var extent = 0f;
				foreach ( var p in rings[i] )
					extent = MathF.Max( extent, Vec3.Dot( p - cur, centreDir ) );
				if ( extent > 0f && radius <= extent )
					Fail(
						$"This loft folds at section {i}: the bend radius is {radius:0.##} but the section reaches {extent:0.##} toward the centre of the bend.",
						"On the inside of a bend, sections converge; when the bend radius drops below the section's extent in that direction, the surface passes through itself.",
						"Straighten the section path",
						"Shrink the sections, or turn Bend guard off to build it anyway" );
			}
		}

		// Smooth loft (wave 3): Catmull-Rom interpolated rings between sections, after
		// taper/guide/centreline, so interpolation is between the final sections.
		if ( SmoothLoft.Value && SmoothRings.Clamped > 0 && rings.Count >= 2 )
		{
			rings = ExpandSmoothRings( rings, Closed.Value, SmoothRings.Clamped );
			for ( var h = 0; h < holeRingsByHole.Count; h++ )
				holeRingsByHole[h] = ExpandSmoothRings( holeRingsByHole[h], Closed.Value, SmoothRings.Clamped );
		}

		// Closed skins the last section back to the FIRST ONE'S vertices rather than to a copy
		// of them, which is the only way the seam is genuinely closed — see Skinner.Skin.
		if ( !anyHoles )
			Emit( ctx, Skinner.SkinChecked( rings, !Closed.Value, Material.Clamped, wrap: Closed.Value ) );
		else if ( holeCount == 1 )
			Emit( ctx, Skinner.SkinWithHole( rings, holeRingsByHole[0], Material.Clamped, wrap: Closed.Value ) );
		else
		{
			// Two or more holes: each end cap is triangulated from ITS OWN section's
			// loops (sections differ, so one triangulation cannot serve both ends),
			// exactly as the extrude caps triangulate top and bottom separately.
			var nearTris = Triangulate.WithHoles( sectionOuters[0], sectionHoles[0].Cast<IReadOnlyList<Vec2>>().ToList() );
			var farTris = Triangulate.WithHoles( sectionOuters[^1], sectionHoles[^1].Cast<IReadOnlyList<Vec2>>().ToList() );

			if ( nearTris.Count == 0 || farTris.Count == 0 )
				Fail(
					$"This loft's {holeCount} holes could not be capped — the loops may cross each other.",
					"Ear clipping returned no triangles for a multi-hole section.",
					"Check that every inner loop lies fully inside the outer one",
					"Redraw a hole so it does not cross the outer loop or another hole" );

			Emit( ctx, Skinner.SkinWithHoles( rings, holeRingsByHole, nearTris, farTris, Material.Clamped, wrap: Closed.Value ) );
		}
		NoteChoice( "Loft: sections=" + string.Join( ", ", sketches.Select( s => SketchNameFor( ctx, s ) ) ) + ( Sections.Count < 2 ? " (auto-selected: all sketches)" : "" ) + ( SmoothLoft.Value ? $" (smooth, {SmoothRings.Clamped} between)" : "" ) );
	}

	static List<Vec3> RotateRingGeom( List<Vec3> ring, Vec3 centre, Vec3 axis, float angleRad )
	{
		var cos = MathF.Cos( angleRad );
		var sin = MathF.Sin( angleRad );
		axis = axis.LengthSquared > 1e-12f ? axis.Normal : new Vec3( 0, 0, 1 );
		var result = new List<Vec3>( ring.Count );
		foreach ( var p in ring )
		{
			var d = p - centre;
			// Rodrigues rotation about the section plane normal.
			var rotated = d * cos + Vec3.Cross( axis, d ) * sin + axis * (Vec3.Dot( axis, d ) * (1f - cos));
			result.Add( centre + rotated );
		}
		return result;
	}

	static List<List<Vec3>> ExpandSmoothRings( List<List<Vec3>> rings, bool closed, int between )
	{
		var n = rings.Count;
		var expanded = new List<List<Vec3>>();
		for ( var i = 0; i < n; i++ )
		{
			expanded.Add( rings[i] );
			if ( !closed && i == n - 1 )
				continue;
			var next = (i + 1) % n;
			var prev = closed ? (i - 1 + n) % n : Math.Max( i - 1, 0 );
			var nextNext = closed ? (i + 2) % n : Math.Min( i + 2, n - 1 );
			for ( var j = 1; j <= between; j++ )
			{
				var t = j / (float)(between + 1);
				var interp = new List<Vec3>( rings[i].Count );
				for ( var k = 0; k < rings[i].Count; k++ )
				{
					var p0 = rings[prev][k];
					var p1 = rings[i][k];
					var p2 = rings[next][k];
					var p3 = rings[nextNext][k];
					var point = 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (t * t) + (-p0 + 3f * p1 - 3f * p2 + p3) * (t * t * t));
					interp.Add( point );
				}
				expanded.Add( interp );
			}
		}
		return expanded;
	}

	List<Vec3> ResolveLoftRail( FeatureContext ctx, string id )
	{
		if ( !ctx.Sketches.TryGetValue( id, out var railSketch ) )
			Fail(
				$"No sketch named '{id}' in the tree (have: {SketchList( ctx )})",
				"The rail sketch this loft was pointed at does not exist at this point in the tree.",
				"Check the spelling of the rail sketch id",
				"Remove the rail to loft without it" );
		var chain = SketchChain.Walk( railSketch );
		if ( chain.Count < 2 )
			Fail(
				$"Rail sketch '{id}' has no connected chain to loft along.",
				"The rail sketch's curves do not join into a walk of two or more points.",
				"Join the rail curves end to end",
				"Draw a single line or polyline as the rail" );
		return chain.Select( railSketch.Plane.ToWorld ).ToList();
	}

	static Vec3? IntersectPlaneLoft( List<Vec3> polyline, Vec3 point, Vec3 normal )
	{
		for ( var i = 0; i + 1 < polyline.Count; i++ )
		{
			var a = polyline[i];
			var b = polyline[i + 1];
			var da = Vec3.Dot( a - point, normal );
			var db = Vec3.Dot( b - point, normal );
			if ( MathF.Abs( da ) < 1e-9f )
				return a;
			if ( MathF.Abs( db ) < 1e-9f )
				return b;
			if ( da * db < 0f )
			{
				var t = da / (da - db);
				return a + (b - a) * t;
			}
		}
		return null;
	}

	List<Sketch> ResolveSections( FeatureContext ctx )
	{
		if ( Sections.Count < 2 )
			return ctx.Sketches.Values.ToList();

		var result = new List<Sketch>( Sections.Count );

		foreach ( var id in Sections )
		{
			if ( !ctx.Sketches.TryGetValue( id, out var sketch ) )
			{
				Fail(
					$"Sketch '{id}' is not available at this point in the tree",
					"A section this loft was pointed at has been deleted, suppressed, or sits below this feature. Sketches available at this point: " + SketchList( ctx ) + ".",
					"Pick a sketch that sits above this feature",
					"Move this feature below that sketch" );
			}

			result.Add( sketch );
		}

		return result;
	}
}

/// <summary>
/// Walking a sketch's curves into a single open or closed chain of points.
///
/// ProfileFinder does something similar and deliberately does not do this: it looks for CLOSED
/// regions and reports open chains as a count it ignores. A sweep path is the opposite — it is
/// normally open, and the one thing it must not do is close.
/// </summary>
public static class SketchChain
{
	/// <summary>
	/// The longest chain of connected curves in a sketch, as points on its plane.
	///
	/// Starts from a free end where there is one, so an open path is walked from its beginning
	/// rather than from somewhere in the middle. A ring with no free end anywhere is walked from an
	/// arbitrary curve, which is the right answer for a closed path.
	/// </summary>
	public static List<Vec2> Walk( Sketch sketch )
	{
		var curves = sketch.Curves.Where( c => !c.Construction && !c.IsClosed ).ToList();

		// A single closed curve IS the path — a circle swept round is a torus.
		if ( curves.Count == 0 )
		{
			var ring = sketch.Curves.FirstOrDefault( c => !c.Construction && c.IsClosed );

			return ring is null ? new List<Vec2>() : ring.Tessellate( sketch, sketch.Tolerance );
		}

		var touching = new Dictionary<int, List<SketchCurve>>();

		foreach ( var curve in curves )
		{
			var (a, b) = curve.Endpoints;

			Link( touching, a, curve );
			Link( touching, b, curve );
		}

		var start = curves[0];
		var startAt = start.Endpoints.A;

		foreach ( var curve in curves )
		{
			var (a, b) = curve.Endpoints;

			if ( touching[a].Count == 1 )
			{
				start = curve;
				startAt = a;
				break;
			}

			if ( touching[b].Count == 1 )
			{
				start = curve;
				startAt = b;
				break;
			}
		}

		var points = new List<Vec2>();
		var used = new HashSet<SketchCurve>();
		var current = start;
		var from = startAt;

		while ( current is not null && used.Add( current ) )
		{
			var samples = current.Tessellate( sketch, sketch.Tolerance );
			var (a, _) = current.Endpoints;

			// Every curve is walked in the direction the chain is travelling, so the samples of one
			// continue the samples of the last rather than doubling back.
			if ( from != a )
				samples.Reverse();

			// The first sample of every curve after the first repeats the last of the one before.
			if ( points.Count > 0 )
				samples.RemoveAt( 0 );

			points.AddRange( samples );

			var (ca, cb) = current.Endpoints;
			var to = from == ca ? cb : ca;

			current = touching[to].FirstOrDefault( c => !used.Contains( c ) );
			from = to;
		}

		return points;
	}

	static void Link( Dictionary<int, List<SketchCurve>> map, int point, SketchCurve curve )
	{
		if ( !map.TryGetValue( point, out var list ) )
			map[point] = list = new List<SketchCurve>();

		list.Add( curve );
	}
}
