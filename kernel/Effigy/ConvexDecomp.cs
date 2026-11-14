using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>One convex piece of a decomposed visual mesh, in model space.</summary>
public sealed class ConvexDecompHull
{
	/// <summary>Hull vertices, in the same model space as the input points. Never re-centred:
	/// a hull for a part offset along x stays offset along x, which is what the KV3 placement
	/// lane needs — <c>PhysicsShapeHull.hull_vertices</c> are read in model space (see
	/// VmdlPhysics), so a hull that was moved to its own origin would land at [0,0,0].</summary>
	public List<Vec3> Points;

	/// <summary>Hull faces, as indices into <see cref="Points"/>, wound outward by ConvexHull.</summary>
	public List<ConvexHull.HullFace> Faces;

	/// <summary>The Source 2 surface property for this hull: "flesh", "metal" or "wood".</summary>
	public string SurfaceProp;

	/// <summary>Average of the hull vertices. A label for the hull, not a volume centroid.</summary>
	public Vec3 Centre;

	/// <summary>Axis-aligned bounds of the hull vertices, in model space.</summary>
	public Vec3 Min, Max;

	/// <summary>Volume enclosed by the hull faces.</summary>
	public float Volume;

	/// <summary>Indices into the caller's input point list that this hull was built from, so a
	/// consumer (or a test) can check which input region a hull covers.</summary>
	public IReadOnlyList<int> SourceIndices;
}

/// <summary>What a decomposition produced.</summary>
public sealed class ConvexDecompResult
{
	public List<ConvexDecompHull> Hulls = new();

	/// <summary>Null on success. Set when no hull could be produced at all (empty input, or an
	/// input that encloses no volume) so the caller can tell "nothing to collide with" apart
	/// from "one hull".</summary>
	public string Reason;
}

/// <summary>
/// Volumetric convex decomposition: a visual mesh in, 4–12 tight convex hulls out.
///
/// WHY NOT ONE HULL PER MESH. CollisionBuilder already does that as its fallback, and it is
/// the honest answer for a convex part — but for a concave one (a body with limbs, a tool
/// with a handle) one hull fills every notch with thin air, and the model bumps into things
/// it visibly does not touch. Splitting the point cloud into compact spatial clusters and
/// hulling each one keeps the empty space down to the gaps between neighbouring hulls.
///
/// WHY BISECTING K-MEANS, NOT PLANE SLABS. A visual mesh is a surface: its vertices sit on
/// faces, not through the volume. Cutting such a cloud with a plane leaves one side holding
/// a single face layer — flat, no hull at all. Bisecting k-means instead seeds two centres
/// at the two furthest points, assigns every point to its nearer centre, and repeats; each
/// cluster holds points from several faces of its neighbourhood, so it still encloses a
/// volume. The split is deterministic: furthest-point seeds with first-wins ties, Lloyd
/// iterations in input order, a fixed iteration cap.
///
/// WHAT "4–12" MEANS. It is the target band for a real mesh, enforced by clamping
/// <see cref="Options.TargetHulls"/> into [<see cref="Options.MinHulls"/>,
/// <see cref="Options.MaxHulls"/>] (defaults 4 and 12). It is NOT a promise for degenerate
/// inputs: a tetrahedron cannot be split into four volumetric hulls, so it comes back as
/// one hull, and a flat cloud comes back as no hulls at all. Fabricating hulls to hit the
/// band would be worse than missing it — a hull with no points behind it is collision
/// where nothing is.
///
/// WHAT THIS DOES NOT DO. It does not write KV3. Each hull carries exactly what the KV3
/// placement lane needs to emit one <c>PhysicsShapeHull</c> node — <see
/// cref="ConvexDecompHull.Points"/> as <c>hull_vertices</c> and <see
/// cref="ConvexDecompHull.SurfaceProp"/> as <c>surface_prop</c> — but the text emission
/// is that lane's job, because VmdlPhysics.ShapeList takes one surface property for its
/// whole list and cannot express a per-hull property. Do not route these hulls through
/// it and lose the per-hull assignment.
///
/// CollisionBuilder and ConvexHull are used read-only here: ConvexHull.Build is the only
/// hull construction, and nothing in either file is modified by this lane.
/// </summary>
public static class ConvexDecomp
{
	/// <summary>Allowed surface properties, per the Lane 6 spec.</summary>
	public static readonly string[] KnownSurfaceProps = { "flesh", "metal", "wood" };

	public sealed class Options
	{
		/// <summary>Desired hull count before clamping. Default 6.</summary>
		public int TargetHulls = 6;

		/// <summary>Lower end of the target band. Default 4.</summary>
		public int MinHulls = 4;

		/// <summary>Upper end of the target band. Default 12.</summary>
		public int MaxHulls = 12;

		/// <summary>Surface property for every hull unless a selector or per-point list says otherwise.</summary>
		public string SurfaceProp = "flesh";

		/// <summary>Optional per-hull override, given the hull centre. Wins over
		/// <see cref="SurfaceProp"/>; a per-point list (see the other Decompose overload)
		/// wins over this.</summary>
		public Func<Vec3, string> SurfacePropForCentre;
	}

	/// <summary>Decompose <paramref name="points"/> (model space) into convex hulls.</summary>
	public static ConvexDecompResult Decompose( IReadOnlyList<Vec3> points, Options options = null ) =>
		Decompose( points, null, options );

	/// <summary>
	/// Decompose <paramref name="points"/> (model space), assigning each hull the surface
	/// property carried by the majority of its own points. A null or empty per-point entry
	/// falls back to <see cref="Options.SurfaceProp"/> for that point; any other value
	/// outside "flesh"/"metal"/"wood" throws, because a mistyped property silently baked
	/// into a .vmdl is a physics bug that only shows up in-game.
	/// </summary>
	public static ConvexDecompResult Decompose( IReadOnlyList<Vec3> points,
		IReadOnlyList<string> surfacePropsPerPoint, Options options = null )
	{
		if ( points is null )
			throw new ArgumentNullException( nameof( points ) );

		options ??= new Options();
		ValidateOptions( options );
		ValidateProps( options.SurfaceProp, nameof( options.SurfaceProp ) );

		if ( surfacePropsPerPoint is not null && surfacePropsPerPoint.Count != points.Count )
			throw new ArgumentException(
				$"per-point surface properties ({surfacePropsPerPoint.Count}) do not match point count ({points.Count})",
				nameof( surfacePropsPerPoint ) );

		var result = new ConvexDecompResult();

		if ( points.Count < 4 )
		{
			result.Reason = $"only {points.Count} point(s); a hull needs at least 4";
			return result;
		}

		var target = Math.Clamp( options.TargetHulls, options.MinHulls, options.MaxHulls );
		var clusters = new List<List<int>> { AllIndices( points.Count ) };
		var failed = new HashSet<string>();

		while ( clusters.Count < target )
		{
			var candidate = LargestSplittable( clusters, points, failed );

			if ( candidate < 0 )
				break;

			var key = ClusterKey( clusters[candidate] );
			var children = Bisect( clusters[candidate], points );

			if ( children is null )
			{
				failed.Add( key );
				continue;
			}

			// A child that encloses no volume is not a hull — reject the split rather
			// than emit a flat piece or silently drop its points.
			if ( ConvexHull.Build( PointsOf( children[0], points ) ) is null
				|| ConvexHull.Build( PointsOf( children[1], points ) ) is null )
			{
				failed.Add( key );
				continue;
			}

			clusters.RemoveAt( candidate );
			clusters.Add( children[0] );
			clusters.Add( children[1] );
		}

		foreach ( var cluster in clusters )
		{
			var hull = ConvexHull.Build( PointsOf( cluster, points ) );

			if ( hull is null )
				continue;

			var built = BuildHull( hull.Value, cluster, points, surfacePropsPerPoint, options );
			result.Hulls.Add( built );
		}

		if ( result.Hulls.Count == 0 )
			result.Reason = "input encloses no volume (flat, collinear, or a single point)";

		return result;
	}

	static ConvexDecompHull BuildHull( (List<Vec3> Points, List<ConvexHull.HullFace> Faces) hull,
		List<int> cluster, IReadOnlyList<Vec3> points,
		IReadOnlyList<string> surfacePropsPerPoint, Options options )
	{
		var min = hull.Points[0];
		var max = hull.Points[0];
		var sum = Vec3.Zero;

		foreach ( var p in hull.Points )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			sum += p;
		}

		var centre = sum / hull.Points.Count;
		var prop = ResolveProp( cluster, points, surfacePropsPerPoint, options, centre );

		return new ConvexDecompHull
		{
			Points = hull.Points,
			Faces = hull.Faces,
			SurfaceProp = prop,
			Centre = centre,
			Min = min,
			Max = max,
			Volume = HullVolume( hull.Points, hull.Faces ),
			SourceIndices = cluster.AsReadOnly(),
		};
	}

	static string ResolveProp( List<int> cluster, IReadOnlyList<Vec3> points,
		IReadOnlyList<string> surfacePropsPerPoint, Options options, Vec3 centre )
	{
		if ( surfacePropsPerPoint is not null )
		{
			// Majority of the hull's own points; ties keep the first-seen property, so
			// the answer is deterministic for a given input order.
			var counts = new Dictionary<string, int>();
			var firstSeen = new List<string>();

			foreach ( var i in cluster )
			{
				var prop = surfacePropsPerPoint[i];

				if ( string.IsNullOrEmpty( prop ) )
					prop = options.SurfaceProp;

				ValidateProps( prop, nameof( surfacePropsPerPoint ) );

				if ( !counts.TryGetValue( prop, out var count ) )
					firstSeen.Add( prop );

				counts[prop] = count + 1;
			}

			var best = firstSeen[0];

			foreach ( var prop in firstSeen )
				if ( counts[prop] > counts[best] )
					best = prop;

			return best;
		}

		if ( options.SurfacePropForCentre is not null )
		{
			var prop = options.SurfacePropForCentre( centre );
			ValidateProps( prop, nameof( options.SurfacePropForCentre ) );
			return prop;
		}

		return options.SurfaceProp;
	}

	static void ValidateOptions( Options options )
	{
		if ( options.MinHulls < 1 )
			throw new ArgumentException( "MinHulls must be at least 1", nameof( options ) );

		if ( options.MaxHulls < options.MinHulls )
			throw new ArgumentException( "MaxHulls must be at least MinHulls", nameof( options ) );

		if ( options.TargetHulls < 1 )
			throw new ArgumentException( "TargetHulls must be at least 1", nameof( options ) );
	}

	static void ValidateProps( string prop, string param )
	{
		if ( string.IsNullOrEmpty( prop ) )
			throw new ArgumentException( "surface property must not be empty", param );

		foreach ( var known in KnownSurfaceProps )
			if ( prop == known )
				return;

		throw new ArgumentException(
			$"unknown surface property '{prop}' (expected flesh, metal, or wood)", param );
	}

	static List<int> AllIndices( int count )
	{
		var indices = new List<int>( count );

		for ( var i = 0; i < count; i++ )
			indices.Add( i );

		return indices;
	}

	static List<Vec3> PointsOf( List<int> cluster, IReadOnlyList<Vec3> points )
	{
		var list = new List<Vec3>( cluster.Count );

		foreach ( var i in cluster )
			list.Add( points[i] );

		return list;
	}

	static string ClusterKey( List<int> cluster )
	{
		var sorted = new List<int>( cluster );
		sorted.Sort();
		return string.Join( ",", sorted );
	}

	/// <summary>Largest-by-bounds cluster that still has points to split and has not already
	/// failed a split, or -1 when splitting is done.</summary>
	static int LargestSplittable( List<List<int>> clusters, IReadOnlyList<Vec3> points,
		HashSet<string> failed )
	{
		var best = -1;
		var bestVolume = -1f;

		for ( var i = 0; i < clusters.Count; i++ )
		{
			var cluster = clusters[i];

			if ( cluster.Count < 8 || failed.Contains( ClusterKey( cluster ) ) )
				continue;

			var min = points[cluster[0]];
			var max = min;

			foreach ( var index in cluster )
			{
				var p = points[index];
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}

			var volume = (max.x - min.x) * (max.y - min.y) * (max.z - min.z);

			if ( volume > bestVolume )
			{
				bestVolume = volume;
				best = i;
			}
		}

		return best;
	}

	/// <summary>
	/// Bisect <paramref name="cluster"/> with 2-means, seeded at its two furthest points.
	/// Returns null when one side would hold fewer than 4 points (too small to hull).
	/// </summary>
	static List<List<int>> Bisect( List<int> cluster, IReadOnlyList<Vec3> points )
	{
		var a = cluster[0];
		var b = cluster[0];
		var best = -1f;

		for ( var i = 0; i < cluster.Count; i++ )
			for ( var j = i + 1; j < cluster.Count; j++ )
			{
				var d = (points[cluster[i]] - points[cluster[j]]).LengthSquared;

				if ( d > best )
				{
					best = d;
					a = cluster[i];
					b = cluster[j];
				}
			}

		if ( best <= 0f )
			return null;

		var centreA = points[a];
		var centreB = points[b];
		List<int> left = null, right = null;

		for ( var iter = 0; iter < 20; iter++ )
		{
			left = new List<int>();
			right = new List<int>();

			foreach ( var index in cluster )
			{
				var p = points[index];
				// Ties go left, so the assignment is deterministic.
				if ( (p - centreA).LengthSquared <= (p - centreB).LengthSquared )
					left.Add( index );
				else
					right.Add( index );
			}

			if ( left.Count == 0 || right.Count == 0 )
				return null;

			var nextA = Mean( left, points );
			var nextB = Mean( right, points );

			if ( nextA.Equals( centreA ) && nextB.Equals( centreB ) )
				break;

			centreA = nextA;
			centreB = nextB;
		}

		if ( left.Count < 4 || right.Count < 4 )
			return null;

		return new List<List<int>> { left, right };
	}

	static Vec3 Mean( List<int> cluster, IReadOnlyList<Vec3> points )
	{
		var sum = Vec3.Zero;

		foreach ( var index in cluster )
			sum += points[index];

		return sum / cluster.Count;
	}

	/// <summary>Volume enclosed by outward-wound faces: sum of signed tetrahedra from the origin.</summary>
	static float HullVolume( List<Vec3> hullPoints, List<ConvexHull.HullFace> faces )
	{
		var total = 0f;

		foreach ( var face in faces )
			total += Vec3.Dot( hullPoints[face.A],
				Vec3.Cross( hullPoints[face.B], hullPoints[face.C] ) ) / 6f;

		return MathF.Abs( total );
	}
}
