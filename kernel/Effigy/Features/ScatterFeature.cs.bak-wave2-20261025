using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Scatter library parts (or copies of an existing body) across a target body's surface —
/// rivets down a hull, barnacles on a rock, teeth along a jaw. The barracuda jaw benchmark
/// is StationRing with a ToothCone source; the harbour crane benchmark is Grid with Bolt
/// and Rivet sources.
///
/// Modes:
/// - Grid: Count stations along the target's longest axis, Rings angles around it.
/// - StationRing: Rings stations along the axis, Count instances around each ring.
/// - CurveIMM: ZBrush tri-part welded brushes along a named Spline: the first
///   instance is StartCap, the last is EndCap, the rest are Middle, all welded into
///   one body, each link rolled 90 degrees from its neighbours like a real chain.
///   An empty cap name falls back to Source. Target is not used in this mode —
///   the chain hangs on the curve, not on a surface.
/// - Poisson: Count area-weighted random points, Spacing as a minimum distance when set.
/// - Curve: Count samples along a named Spline, projected onto the target surface.
///
/// Domain is the patch of surface to cover, in the target's own station/angle frame:
/// s=0.04..0.22 along the longest axis, angle=-90..90 around it, slot=1 to keep only
/// faces in that material slot. ScaleCurve sizes instances along s, as s:multiplier
/// pairs (0:0.4,0.3:1.2,1:0.3). AlignToNormal points each instance's +z out of the
/// surface; Tilt then pitches, yaws and rolls it in its own local frame.
///
/// The target body is never touched: every instance is appended into one new body, so
/// the scatter can be re-run, moved or deleted without rebuilding the thing it sits on.
/// A library part's +z is its outward axis (every PartLibrary part stands on z = 0
/// pointing up), so aligning +z to the surface normal is what makes a tooth stick OUT
/// of a jaw instead of lying flat on it.
///
/// Placement is deterministic: one fixed-seed random stream, so a rebuild of the same
/// document scatters the same teeth in the same places.
/// </summary>
public sealed class ScatterFeature : Feature
{
	public override string TypeName => "Scatter";

	public readonly StringParam Source = new( "Source", "" );
	public readonly BodySelectionParam Target = new( "Target" );
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Grid", "Poisson", "Curve", "StationRing", "CurveIMM" } );
	public readonly StringParam Domain = new( "Domain", "" );
	public readonly StringParam ScaleCurve = new( "Scale curve", "" );
	public readonly StringParam Curve = new( "Curve", "" );
	public readonly IntParam Count = new( "Count", 24, 1, 100000 );
	public readonly IntParam Rings = new( "Rings", 8, 1, 1024 );
	public readonly FloatParam Spacing = new( "Spacing", 0f, 0f, 1000f, "u" );
	public readonly FloatParam Jitter = new( "Jitter", 0f, 0f, 1f );
	public readonly BoolParam AlignToNormal = new( "Align to normal", true );
	public readonly Vec3Param Tilt = new( "Tilt", Vec3.Zero );

	/// <summary>Dials for a library-part source, mirroring PartFeature.</summary>
	public readonly FloatParam Size = new( "Size", 4f, 0.01f, unit: "u" );
	public readonly FloatParam Length = new( "Length", 8f, 0.01f, unit: "u" );
	public readonly FloatParam Thickness = new( "Thickness", 1f, 0.01f, unit: "u" );
	public readonly IntParam Sides = new( "Sides", 12, 3, 128 );
	public readonly FloatParam PartAngle = new( "Part angle", 90f, 0f, 360f, unit: "deg" );

	/// <summary>CurveIMM only: library part or body name for the first link. Empty = Source.</summary>
	public readonly StringParam StartCap = new( "Start cap", "" );

	/// <summary>CurveIMM only: library part or body name for the links between the caps. Empty = Source.</summary>
	public readonly StringParam Middle = new( "Middle", "" );

	/// <summary>CurveIMM only: library part or body name for the last link. Empty = Source.</summary>
	public readonly StringParam EndCap = new( "End cap", "" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Source, Target, Mode, Domain, ScaleCurve, Curve, Count, Rings, Spacing, Jitter, AlignToNormal, Tilt, Size, Length, Thickness, Sides, PartAngle, StartCap, Middle, EndCap };

	protected override void Execute( FeatureContext ctx )
	{
		// CurveIMM hangs a chain on a curve, not on a surface, so it never looks
		// at Target — the shared path below would fail it for having no target.
		if ( Mode.Value == "CurveIMM" )
		{
			BuildCurveImm( ctx );
			return;
		}

		if ( Target.BodyIds.Count == 0 )
			Fail( "Scatter needs a target body.", "Target is empty, so there is no surface to scatter onto.",
				"Pick the body to scatter onto" );

		var targetBody = ctx.Bodies.FirstOrDefault( b => Target.BodyIds.Contains( b.Id ) );

		if ( targetBody is null )
			Fail( "Scatter's target body is not above it.",
				$"Target names '{string.Join( ",", Target.BodyIds )}', which is not a body that has run before this one.",
				"Put the target body first", "Check the target name" );

		var target = targetBody!.Mesh;

		if ( target is null || target.VertexCount == 0 )
			Fail( "Scatter's target body has no mesh.", "There is no surface to scatter onto.",
				"Give the target body geometry first" );

		var source = ResolveSource( ctx );

		var domain = ParseDomain( Domain.Value );
		var scaleCurve = ParseScaleCurve( ScaleCurve.Value );
		var axis = new SurfaceFrame( target );
		var rng = new Random( 12345 );
		var placements = new List<Placement>();

		switch ( Mode.Value )
		{
			case "Grid":
				GenerateGrid( axis, target, domain, scaleCurve, Count.Clamped, Rings.Clamped, Spacing.Clamped, Jitter.Clamped, rng, placements );
				break;
			case "StationRing":
				GenerateGrid( axis, target, domain, scaleCurve, Rings.Clamped, Count.Clamped, Spacing.Clamped, Jitter.Clamped, rng, placements );
				break;
			case "Poisson":
				GeneratePoisson( axis, target, domain, scaleCurve, Count.Clamped, Spacing.Clamped, rng, placements );
				break;
			case "Curve":
				GenerateCurve( ctx, axis, target, domain, scaleCurve, Count.Clamped, placements );
				break;
			default:
				Fail( $"Scatter mode '{Mode.Value}' is not a mode.", "Grid, Poisson, Curve, StationRing and CurveIMM are.",
					"Pick one of those five" );
				break;
		}

		if ( placements.Count == 0 )
			Fail( "Scatter placed nothing.",
				"The domain, spacing or curve left no point on the target surface.",
				"Widen the domain", "Lower Spacing", "Check the curve runs near the target" );

		var tilt = Tilt.Value * MathF.PI / 180f;
		var tiltXform = Xform.Rotate( new Vec3( 0, 0, 1 ), tilt.z )
			* Xform.Rotate( new Vec3( 0, 1, 0 ), tilt.y )
			* Xform.Rotate( new Vec3( 1, 0, 0 ), tilt.x );
		var result = new PolyMesh();

		foreach ( var p in placements )
		{
			var instance = source.Clone();

			if ( p.Scale != 1f )
				MeshTransform.Apply( instance, Xform.Scale( new Vec3( p.Scale, p.Scale, p.Scale ) ) );

			var z = AlignToNormal.Value ? p.Normal.Normal : new Vec3( 0, 0, 1 );
			var helper = MathF.Abs( z.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var x = Vec3.Cross( helper, z ).Normal;
			var y = Vec3.Cross( z, x );
			x = tiltXform.TransformDirection( x );
			y = tiltXform.TransformDirection( y );
			z = tiltXform.TransformDirection( z );

			MeshTransform.Apply( instance, new Xform( x, y, z, p.Point ) );
			MeshTransform.Append( result, instance );
		}

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? $"Scatter {Source.Value}", result ) );
	}

	PolyMesh ResolveSource( FeatureContext ctx )
	{
		var name = Source.Value;

		if ( string.IsNullOrWhiteSpace( name ) )
			Fail( "Scatter needs a source.", "Source is empty, so there is nothing to place.",
				"Name a library part (ToothCone, Rivet, Bolt)", "Or name an existing body to copy" );

		if ( PartLibrary.Names.Contains( name ) )
			return PartLibrary.Build( name, Size.Clamped, Length.Clamped, Thickness.Clamped, Sides.Clamped, PartAngle.Clamped, 0 );

		var body = ctx.Bodies.FirstOrDefault( b => b.Name == name )
			?? ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) );

		if ( body?.Mesh is null )
			Fail( $"Scatter's source '{name}' is neither a library part nor a body above it.",
				"Library parts are Cog, Bolt, Rivet, ToothCone and the rest of PartLibrary.Names; a body source must have run before this one.",
				"Check the source name", "Put the source body first" );

		return body!.Mesh;
	}

	readonly struct DomainRange
	{
		public readonly float SMin, SMax, AngleMin, AngleMax;
		public readonly int Slot;

		public DomainRange( float sMin, float sMax, float angleMin, float angleMax, int slot )
		{
			SMin = sMin;
			SMax = sMax;
			AngleMin = angleMin;
			AngleMax = angleMax;
			Slot = slot;
		}

		public bool Contains( float s, float angle ) => s >= SMin && s <= SMax && angle >= AngleMin && angle <= AngleMax;
	}

	DomainRange ParseDomain( string text )
	{
		var sMin = 0f;
		var sMax = 1f;
		var aMin = -180f;
		var aMax = 180f;
		var slot = -1;

		if ( string.IsNullOrWhiteSpace( text ) )
			return new DomainRange( sMin, sMax, aMin, aMax, slot );

		foreach ( var piece in text.Split( ',' ) )
		{
			var part = piece.Trim();

			if ( part.StartsWith( "s=", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !TryRange( part[2..], out sMin, out sMax ) )
					Fail( $"Scatter's domain '{text}' does not parse.", "s= wants a..b, as in s=0.04..0.22.",
						"Write s=0.04..0.22" );
			}
			else if ( part.StartsWith( "angle=", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !TryRange( part[6..], out aMin, out aMax ) )
					Fail( $"Scatter's domain '{text}' does not parse.", "angle= wants a..b in degrees, as in angle=-90..90.",
						"Write angle=-90..90" );
			}
			else if ( part.StartsWith( "slot=", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !int.TryParse( part[5..], out slot ) )
					Fail( $"Scatter's domain '{text}' does not parse.", "slot= wants a material slot number, as in slot=1.",
						"Write slot=1" );
			}
			else
			{
				Fail( $"Scatter's domain '{text}' does not parse.", "Domain pieces are s=.., angle=.. and slot=, separated by commas.",
					"Write s=0.04..0.22,angle=-90..90,slot=1" );
			}
		}

		return new DomainRange( sMin, sMax, aMin, aMax, slot );
	}

	static bool TryRange( string text, out float min, out float max )
	{
		min = 0f;
		max = 1f;
		var split = text.Split( "..", StringSplitOptions.None );

		if ( split.Length == 1 )
		{
			if ( !float.TryParse( split[0], out min ) )
				return false;

			max = min;
			return true;
		}

		return split.Length == 2
			&& float.TryParse( split[0], out min )
			&& float.TryParse( split[1], out max );
	}

	List<(float S, float Scale)> ParseScaleCurve( string text )
	{
		var curve = new List<(float, float)>();

		if ( string.IsNullOrWhiteSpace( text ) )
			return curve;

		foreach ( var piece in text.Split( ',' ) )
		{
			var kv = piece.Trim().Split( ':', StringSplitOptions.None );
			var s = 0f;
			var scale = 1f;

			if ( kv.Length != 2 || !float.TryParse( kv[0], out s ) || !float.TryParse( kv[1], out scale ) )
				Fail( $"Scatter's scale curve '{text}' does not parse.", "Scale curve wants s:multiplier pairs, as in 0:0.4,0.3:1.2,1:0.3.",
					"Write 0:0.4,0.3:1.2,1:0.3" );

			curve.Add( (s, scale) );
		}

		curve.Sort( (a, b) => a.Item1.CompareTo( b.Item1 ) );
		return curve;
	}

	float ScaleAt( List<(float S, float Scale)> curve, float s )
	{
		if ( curve.Count == 0 )
			return 1f;

		if ( s <= curve[0].S )
			return curve[0].Scale;

		if ( s >= curve[^1].S )
			return curve[^1].Scale;

		for ( var i = 1; i < curve.Count; i++ )
		{
			if ( s > curve[i].S )
				continue;

			var (s0, v0) = curve[i - 1];
			var (s1, v1) = curve[i];
			return s1 == s0 ? v1 : v0 + (v1 - v0) * (s - s0) / (s1 - s0);
		}

		return curve[^1].Scale;
	}

	readonly struct Placement
	{
		public readonly Vec3 Point, Normal;
		public readonly float Scale;

		public Placement( Vec3 point, Vec3 normal, float scale )
		{
			Point = point;
			Normal = normal;
			Scale = scale;
		}
	}

	/// <summary>
	/// The target's station frame: its longest bounding-box axis is s, and angle is the
	/// polar angle around that axis, measured from the bbox centre in the other two axes.
	/// A jaw is long, so s runs jaw-length and angle wraps around it; a crane mast is
	/// tall, so s runs height and angle wraps around that instead.
	/// </summary>
	sealed class SurfaceFrame
	{
		public readonly int Axis;
		public readonly Vec3 U, V, Centre;
		public readonly float Min, Extent, Reach;

		public SurfaceFrame( PolyMesh mesh )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}

			var extents = new[] { max.x - min.x, max.y - min.y, max.z - min.z };
			Axis = extents[0] >= extents[1] && extents[0] >= extents[2] ? 0 : extents[1] >= extents[2] ? 1 : 2;
			Min = Axis == 0 ? min.x : Axis == 1 ? min.y : min.z;
			Extent = MathF.Max( extents[Axis], 1e-6f );
			Centre = (min + max) * 0.5f;

			var axes = new[] { new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ) };
			U = axes[(Axis + 1) % 3];
			V = axes[(Axis + 2) % 3];

			var reach = 0f;

			foreach ( var p in mesh.Positions )
			{
				var d = p - Centre;
				var du = Vec3.Dot( d, U );
				var dv = Vec3.Dot( d, V );
				reach = MathF.Max( reach, MathF.Sqrt( du * du + dv * dv ) );
			}

			Reach = reach;
		}

		public float Coord( Vec3 p ) => Axis == 0 ? p.x : Axis == 1 ? p.y : p.z;

		public float S( Vec3 p ) => (Coord( p ) - Min) / Extent;

		public float Angle( Vec3 p )
		{
			var d = p - Centre;
			return MathF.Atan2( Vec3.Dot( d, V ), Vec3.Dot( d, U ) ) * 180f / MathF.PI;
		}

		public Vec3 AxisPoint( float s ) =>
			Axis == 0 ? new Vec3( Min + s * Extent, Centre.y, Centre.z )
			: Axis == 1 ? new Vec3( Centre.x, Min + s * Extent, Centre.z )
			: new Vec3( Centre.x, Centre.y, Min + s * Extent );

		public Vec3 Radial( float angleDegrees )
		{
			var a = angleDegrees * MathF.PI / 180f;
			return U * MathF.Cos( a ) + V * MathF.Sin( a );
		}
	}

	void GenerateGrid( SurfaceFrame axis, PolyMesh target, DomainRange domain, List<(float S, float Scale)> scaleCurve, int stations, int around, float spacing, float jitter, Random rng, List<Placement> outPlacements )
	{
		var nS = spacing > 0f ? Math.Max( 1, (int)MathF.Floor( axis.Extent / spacing ) + 1 ) : Math.Max( 1, stations );
		var nA = Math.Max( 1, around );
		var fullCircle = domain.AngleMax - domain.AngleMin >= 359f;
		var pitchS = (domain.SMax - domain.SMin) / Math.Max( 1, nS );
		var pitchA = (domain.AngleMax - domain.AngleMin) / Math.Max( 1, nA );

		for ( var i = 0; i < nS; i++ )
		{
			var s = domain.SMin + (domain.SMax - domain.SMin) * (nS == 1 ? 0.5f : i / (float)(nS - 1));
			s += (float)(rng.NextDouble() * 2 - 1) * jitter * pitchS * 0.5f;

			for ( var j = 0; j < nA; j++ )
			{
				var angle = fullCircle
					? domain.AngleMin + (domain.AngleMax - domain.AngleMin) * j / nA
					: domain.AngleMin + (domain.AngleMax - domain.AngleMin) * (nA == 1 ? 0.5f : j / (float)(nA - 1));
				angle += (float)(rng.NextDouble() * 2 - 1) * jitter * pitchA * 0.5f;

				TryPlaceFromAxis( axis, target, domain, scaleCurve, s, angle, outPlacements );
			}
		}
	}

	void TryPlaceFromAxis( SurfaceFrame axis, PolyMesh target, DomainRange domain, List<(float S, float Scale)> scaleCurve, float s, float angle, List<Placement> outPlacements )
	{
		if ( s < domain.SMin || s > domain.SMax || angle < domain.AngleMin || angle > domain.AngleMax )
			return;

		var radial = axis.Radial( angle );
		var origin = axis.AxisPoint( s ) + radial * (axis.Reach + 1f);
		var hit = MeshRaycast.Raycast( target, origin, radial * -1f );

		if ( hit is null )
			return;

		var p = hit.Value.Point;
		var face = target.Faces[hit.Value.FaceIndex];

		if ( domain.Slot >= 0 && face.Material != domain.Slot )
			return;

		// Re-check the domain against the hit itself, not the requested station: a ray
		// through a notch can land on a face whose station is outside the patch.
		if ( !domain.Contains( axis.S( p ), axis.Angle( p ) ) )
			return;

		outPlacements.Add( new Placement( p, hit.Value.Normal, ScaleAt( scaleCurve, axis.S( p ) ) ) );
	}

	void GeneratePoisson( SurfaceFrame axis, PolyMesh target, DomainRange domain, List<(float S, float Scale)> scaleCurve, int count, float spacing, Random rng, List<Placement> outPlacements )
	{
		// Area-weighted fan triangles, so a big flat panel gets proportionally more
		// points than a small chamfer — uniform over surface area, not over faces.
		var tris = new List<(int Face, Vec3 A, Vec3 B, Vec3 C, float Area)>();
		var total = 0f;

		foreach ( var fi in Enumerable.Range( 0, target.FaceCount ) )
		{
			var f = target.Faces[fi];

			for ( var k = 1; k + 1 < f.Count; k++ )
			{
				var a = target.Positions[f.Indices[0]];
				var b = target.Positions[f.Indices[k]];
				var c = target.Positions[f.Indices[k + 1]];
				var area = Vec3.Cross( b - a, c - a ).Length * 0.5f;

				if ( area <= 0f )
					continue;

				tris.Add( (fi, a, b, c, area) );
				total += area;
			}
		}

		if ( tris.Count == 0 || total <= 0f )
			return;

		var placed = new List<Vec3>();
		var attempts = 0;
		var maxAttempts = Math.Max( count * 40, 400 );

		while ( outPlacements.Count < count && attempts < maxAttempts )
		{
			attempts++;
			var pick = (float)rng.NextDouble() * total;
			var acc = 0f;
			var tri = tris[0];

			foreach ( var t in tris )
			{
				acc += t.Area;

				if ( acc >= pick )
				{
					tri = t;
					break;
				}
			}

			var r1 = (float)rng.NextDouble();
			var r2 = (float)rng.NextDouble();
			var sq = MathF.Sqrt( r1 );
			var point = tri.A * (1f - sq) + tri.B * (sq * (1f - r2)) + tri.C * (sq * r2);
			var face = target.Faces[tri.Face];

			if ( domain.Slot >= 0 && face.Material != domain.Slot )
				continue;

			if ( !domain.Contains( axis.S( point ), axis.Angle( point ) ) )
				continue;

			if ( spacing > 0f && placed.Any( q => (q - point).LengthSquared < spacing * spacing ) )
				continue;

			placed.Add( point );
			outPlacements.Add( new Placement( point, target.FaceNormal( face ), ScaleAt( scaleCurve, axis.S( point ) ) ) );
		}
	}

	void GenerateCurve( FeatureContext ctx, SurfaceFrame axis, PolyMesh target, DomainRange domain, List<(float S, float Scale)> scaleCurve, int count, List<Placement> outPlacements )
	{
		if ( string.IsNullOrWhiteSpace( Curve.Value ) )
			Fail( "Scatter's Curve mode needs a curve.", "Curve is empty, so there is no path to sample.",
				"Name a Spline feature that has run before this one" );

		if ( !ctx.Splines.TryGetValue( Curve.Value, out var spline ) )
			Fail( "Scatter's curve is not above it.",
				$"Curve names '{Curve.Value}', which is not a Spline feature that has run before this one.",
				"Add the Spline first", "Check the name in Curve" );

		var polyline = spline.Sample( 8 );

		if ( polyline.Count < 2 )
			Fail( "Scatter's curve has fewer than two points.", "A curve needs a path to lie on.",
				"Give the Spline two or more points" );

		var samples = ResamplePolyline( polyline, spline.Closed, Math.Max( 1, count ) );
		var tree = MeshBVH.Build( target );
		var reach = axis.Extent * 2f + 10f;

		foreach ( var sample in samples )
		{
			var hit = tree.NearestSurface( target, sample, reach );

			if ( hit is null )
				continue;

			var p = hit.Value.Point;
			var face = target.Faces[hit.Value.FaceIndex];

			if ( domain.Slot >= 0 && face.Material != domain.Slot )
				continue;

			if ( !domain.Contains( axis.S( p ), axis.Angle( p ) ) )
				continue;

			outPlacements.Add( new Placement( p, hit.Value.Normal, ScaleAt( scaleCurve, axis.S( p ) ) ) );
		}
	}

	/// <summary>
	/// CurveIMM: one welded chain body along a spline. The first sample is the start
	/// cap, the last is the end cap, everything between is the middle link. Each
	/// instance's +z (a library part's outward axis) runs along the curve tangent,
	/// the instance is centred on the curve point rather than hung from its base,
	/// and neighbouring links roll 90 degrees apart, the way real chain links
	/// interlock. "Welded" here means one merged body, as with every other Scatter
	/// mode — links interlock geometrically but do not share vertices, so a chain
	/// that must deform as one piece still wants a weld pass after it.
	/// </summary>
	void BuildCurveImm( FeatureContext ctx )
	{
		if ( Count.Clamped < 2 )
			Fail( "CurveIMM needs at least 2 instances.", "One cap alone is not a chain.", "Set Count to 2 or more" );

		if ( string.IsNullOrWhiteSpace( Curve.Value ) )
			Fail( "Scatter's CurveIMM mode needs a curve.", "Curve is empty, so there is no path to hang the chain on.", "Name a Spline feature that has run before this one" );

		if ( !ctx.Splines.TryGetValue( Curve.Value, out var spline ) )
			Fail( "Scatter's curve is not above it.",
				$"Curve names '{Curve.Value}', which is not a Spline feature that has run before this one.",
				"Add the Spline first", "Check the name in Curve" );

		var polyline = spline.Sample( 8 );

		if ( polyline.Count < 2 )
			Fail( "Scatter's curve has fewer than two points.", "A curve needs a path to lie on.", "Give the Spline two or more points" );

		var samples = ResamplePolyline( polyline, spline.Closed, Count.Clamped );

		if ( samples.Count < 2 )
			Fail( "CurveIMM placed nothing.", "The curve resampled to fewer than two points.", "Give the Spline two or more points" );

		PolyMesh fallback = null;

		PolyMesh ResolveCap( string name )
		{
			if ( string.IsNullOrWhiteSpace( name ) )
				return fallback ??= ResolveSource( ctx );

			if ( PartLibrary.Names.Contains( name ) )
				return PartLibrary.Build( name, Size.Clamped, Length.Clamped, Thickness.Clamped, Sides.Clamped, PartAngle.Clamped, 0 );

			var body = ctx.Bodies.FirstOrDefault( b => b.Name == name )
				?? ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) );

			if ( body?.Mesh is null )
				Fail( $"Scatter's CurveIMM cap '{name}' is neither a library part nor a body above it.",
					"Library parts are Cog, Bolt, Rivet, ToothCone and the rest of PartLibrary.Names; a body cap must have run before this one.",
					"Check the cap name", "Put the cap body first" );

			return body!.Mesh;
		}

		var start = ResolveCap( StartCap.Value );
		var middle = ResolveCap( Middle.Value );
		var end = ResolveCap( EndCap.Value );

		var result = new PolyMesh();
		var n = samples.Count;

		for ( var i = 0; i < n; i++ )
		{
			var prev = spline.Closed ? samples[(i - 1 + n) % n] : samples[Math.Max( 0, i - 1 )];
			var next = spline.Closed ? samples[(i + 1) % n] : samples[Math.Min( n - 1, i + 1 )];
			var tangent = next - prev;

			if ( tangent.LengthSquared < 1e-12f )
				tangent = new Vec3( 0, 0, 1 );
			else
				tangent = tangent.Normal;

			var helper = MathF.Abs( tangent.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var x = Vec3.Cross( helper, tangent ).Normal;
			var y = Vec3.Cross( tangent, x );

			if ( i % 2 == 1 )
			{
				var roll = Xform.Rotate( tangent, MathF.PI / 2f );
				x = roll.TransformDirection( x );
				y = roll.TransformDirection( y );
			}

			var instance = (i == 0 ? start : i == n - 1 ? end : middle).Clone();
			MeshTransform.Apply( instance, new Xform( x, y, tangent, new Vec3( 0, 0, 0 ) ) );

			var centroid = new Vec3( 0, 0, 0 );

			foreach ( var p in instance.Positions )
				centroid += p;

			if ( instance.Positions.Count > 0 )
				centroid *= 1f / instance.Positions.Count;

			MeshTransform.Apply( instance, Xform.Translate( samples[i] - centroid ) );
			MeshTransform.Append( result, instance );
		}

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? $"Chain {Curve.Value}", result ) );
	}

	static List<Vec3> ResamplePolyline( List<Vec3> polyline, bool closed, int count )
	{
		var lengths = new List<float> { 0f };
		var total = 0f;
		var n = polyline.Count;

		for ( var i = 0; i < n; i++ )
		{
			var next = closed ? polyline[(i + 1) % n] : i + 1 < n ? polyline[i + 1] : polyline[i];
			var step = (next - polyline[i]).Length;
			total += step;
			lengths.Add( total );
		}

		var result = new List<Vec3>();
		var stepLen = closed ? total / count : total / Math.Max( 1, count - 1 );

		for ( var k = 0; k < count; k++ )
		{
			var d = k * stepLen;
			var seg = 0;

			while ( seg < lengths.Count - 2 && lengths[seg + 1] < d )
				seg++;

			var segLen = lengths[seg + 1] - lengths[seg];
			var f = segLen <= 0f ? 0f : (d - lengths[seg]) / segLen;
			var a = polyline[seg % n];
			var b = closed ? polyline[(seg + 1) % n] : polyline[Math.Min( seg + 1, n - 1 )];
			result.Add( a + (b - a) * f );
		}

		return result;
	}
}
