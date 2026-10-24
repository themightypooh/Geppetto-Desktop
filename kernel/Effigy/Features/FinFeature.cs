using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>A rayed fin, wing, leaf or cape as one clean quad loft, instead of the 180 SDF cones
/// a barracuda's fins used to take inside Blob features. Two polylines — the root line where it
/// attaches and the tip line at its far margin — and a set of rays spanning between them.
/// The membrane between rays can dip, and each ray can stand proud as a rib, so a fin reads as
/// rays plus webbing rather than as one flat sheet.</summary>
public static class FinBody
{
	/// <param name="root">2+ points along the attachment line, leading edge first (either order
	/// works; the solid is flipped to face outward if the order comes in reversed).</param>
	/// <param name="tip">2+ points along the far margin, in the same order as the root.</param>
	/// <param name="rootThickness">Solid thickness at the root, in the same units as the points.</param>
	/// <param name="tipThickness">Solid thickness at the tip.</param>
	/// <param name="rays">Ribs spanning root to tip. Web columns sit halfway between rays.</param>
	/// <param name="ridge">How far a ray rib stands above the membrane. 0 is a smooth fin.</param>
	/// <param name="web">How much of the ridge the webbing between rays loses: 0 keeps the webbing
	/// as high as the rays, 1 drops it back to the bare membrane, so the rays scallop.</param>
	/// <param name="curve">Bow of each ray as a fraction of its length, in the fin's own plane,
	/// peaking mid-ray. Negative bows the other way.</param>
	/// <param name="subdiv">Segments along each ray: 1 is root straight to tip.</param>
	/// <returns>A closed, manifold, all-quad solid. U runs root to tip, V runs along the
	/// root/tip lines (leading to trailing), so paint layers and stripes land sensibly.</returns>
	public static PolyMesh Build( IReadOnlyList<Vec3> root, IReadOnlyList<Vec3> tip,
		float rootThickness, float tipThickness, int rays, float ridge, float web,
		float curve, int subdiv, int material )
	{
		if ( root is null || root.Count < 2 )
			throw new ArgumentException( "A fin needs at least two root points.", nameof( root ) );

		if ( tip is null || tip.Count < 2 )
			throw new ArgumentException( "A fin needs at least two tip points.", nameof( tip ) );

		if ( rays < 2 )
			throw new ArgumentException( $"A fin needs at least 2 rays, not {rays}.", nameof( rays ) );

		if ( subdiv < 1 )
			throw new ArgumentException( $"A fin needs at least 1 subdivision, not {subdiv}.", nameof( subdiv ) );

		if ( rootThickness <= 0f || tipThickness <= 0f )
			throw new ArgumentException( $"A fin's thickness must be positive, not root {rootThickness} tip {tipThickness}." );

		web = Math.Clamp( web, 0f, 1f );

		// The fin's normal, from the average ray direction and the average root direction.
		var rootDir = (Sample( root, 1f ) - Sample( root, 0f )).Normal;
		var rayDir = (Sample( tip, 0.5f ) - Sample( root, 0.5f )).Normal;
		var normal = Vec3.Cross( rayDir, rootDir );

		if ( normal.LengthSquared < 1e-12f )
			normal = Vec3.Cross( rayDir, new Vec3( 0, 0, 1 ) );

		if ( normal.LengthSquared < 1e-12f )
			normal = new Vec3( 0, 0, 1 );

		normal = normal.Normal;

		var cols = 2 * rays - 1;
		var stations = subdiv + 1;
		var top = new int[stations, cols];
		var bottom = new int[stations, cols];
		var mesh = new PolyMesh();

		Vec2 Uv( float t, float u ) => new( t, u );

		for ( var i = 0; i < stations; i++ )
		{
			var t = (float)i / subdiv;

			for ( var c = 0; c < cols; c++ )
			{
				// Even columns are rays, odd columns are the webbing halfway between them.
				var u = c % 2 == 0 ? (c / 2) / (float)(rays - 1) : (c / 2 + 0.5f) / (rays - 1);
				var isRay = c % 2 == 0;
				var rootP = Sample( root, u );
				var tipP = Sample( tip, u );
				var ray = tipP - rootP;
				var len = ray.Length;
				var dir = len > 1e-8f ? ray / len : rayDir;

				// In-plane bow, peaking mid-ray: sideways to the ray, inside the fin's plane.
				var perp = Vec3.Cross( normal, dir );

				if ( perp.LengthSquared < 1e-12f )
					perp = rootDir;

				perp = perp.Normal;
				var centre = rootP + ray * t + perp * (curve * len * MathF.Sin( MathF.PI * t ));
				var thickness = rootThickness + (tipThickness - rootThickness) * t;
				var bump = isRay ? ridge : ridge * (1f - web);
				var half = thickness * 0.5f + bump;

				top[i, c] = mesh.AddVertex( centre + normal * half );
				bottom[i, c] = mesh.AddVertex( centre - normal * half );
			}
		}

		void Quad( int a, int b, int c2, int d, Vec2 ua, Vec2 ub, Vec2 uc, Vec2 ud )
			=> mesh.AddFace( new[] { a, b, c2, d }, new[] { ua, ub, uc, ud }, material );

		var lastStation = stations - 1;
		var lastCol = cols - 1;

		for ( var i = 0; i < lastStation; i++ )
		{
			var t0 = (float)i / subdiv;
			var t1 = (float)(i + 1) / subdiv;

			for ( var c = 0; c < lastCol; c++ )
			{
				var u0 = ColumnU( c );
				var u1 = ColumnU( c + 1 );

				// Top winds ray-then-root so its normal is the fin normal; bottom is reversed.
				Quad( top[i, c], top[i + 1, c], top[i + 1, c + 1], top[i, c + 1],
					Uv( t0, u0 ), Uv( t1, u0 ), Uv( t1, u1 ), Uv( t0, u1 ) );
				Quad( bottom[i, c], bottom[i, c + 1], bottom[i + 1, c + 1], bottom[i + 1, c],
					Uv( t0, u0 ), Uv( t0, u1 ), Uv( t1, u1 ), Uv( t1, u0 ) );
			}

			// Leading rim (first column) faces away from the root direction; trailing rim
			// (last column) faces toward it. Each rim's winding was checked against the
			// right-hand rule for its outward direction, not copied from the top.
			Quad( top[i, 0], bottom[i, 0], bottom[i + 1, 0], top[i + 1, 0],
				Uv( t0, ColumnU( 0 ) ), Uv( t0, ColumnU( 0 ) ), Uv( t1, ColumnU( 0 ) ), Uv( t1, ColumnU( 0 ) ) );
			Quad( top[i, lastCol], top[i + 1, lastCol], bottom[i + 1, lastCol], bottom[i, lastCol],
				Uv( t0, ColumnU( lastCol ) ), Uv( t1, ColumnU( lastCol ) ), Uv( t1, ColumnU( lastCol ) ), Uv( t0, ColumnU( lastCol ) ) );
		}

		for ( var c = 0; c < lastCol; c++ )
		{
			var u0 = ColumnU( c );
			var u1 = ColumnU( c + 1 );

			// Root rim faces back along the rays; tip rim faces along them.
			Quad( top[0, c], top[0, c + 1], bottom[0, c + 1], bottom[0, c],
				Uv( 0, u0 ), Uv( 0, u1 ), Uv( 0, u1 ), Uv( 0, u0 ) );
			Quad( top[lastStation, c], bottom[lastStation, c], bottom[lastStation, c + 1], top[lastStation, c + 1],
				Uv( 1, u0 ), Uv( 1, u0 ), Uv( 1, u1 ), Uv( 1, u1 ) );
		}

		// Point order along the root or tip is the caller's choice, and it flips the frame's
		// normal. The rims are consistent with each other either way, so a negative volume
		// means the whole solid came out inside out — reverse every face, UVs with it.
		if ( mesh.SignedVolume() < 0f )
		{
			foreach ( var face in mesh.Faces )
			{
				Array.Reverse( face.Indices );
				Array.Reverse( face.UVs );
			}
		}

		return mesh;

		float ColumnU( int c ) => c % 2 == 0 ? (c / 2) / (float)(rays - 1) : (c / 2 + 0.5f) / (rays - 1);
	}

	/// <summary>A point on a polyline at fraction <paramref name="u"/> of its chord length, so a
	/// root of 3 points and a tip of 5 still line up by distance along the fin, not by index.</summary>
	static Vec3 Sample( IReadOnlyList<Vec3> points, float u )
	{
		u = Math.Clamp( u, 0f, 1f );

		var total = 0f;
		var lengths = new float[points.Count - 1];

		for ( var i = 0; i + 1 < points.Count; i++ )
		{
			lengths[i] = (points[i + 1] - points[i]).Length;
			total += lengths[i];
		}

		if ( total < 1e-12f )
			return points[0];

		var target = u * total;

		for ( var i = 0; i + 1 < points.Count; i++ )
		{
			if ( target <= lengths[i] || i + 2 == points.Count )
			{
				var seg = lengths[i] < 1e-12f ? 0f : target / lengths[i];
				return points[i] + (points[i + 1] - points[i]) * Math.Clamp( seg, 0f, 1f );
			}

			target -= lengths[i];
		}

		return points[^1];
	}
}

/// <summary>Script: <c>add Fin name=Caudal root=0,17.2,0;0,18.2,0.5;0,19.2,0
/// tip=0,24.2,3.7;0,21.0,0;0,24.2,-3.5 thickness=0.26,0.04 rays=14 ridge=0.06 web=0.72
/// curve=0.15 subdiv=1 material=4</c>. Root and tip are point lists, so landmarks work in
/// them the same way they work in any other point list.</summary>
public sealed class FinFeature : Feature
{
	public override string TypeName => "Fin";

	public List<Vec3> Root = new();
	public List<Vec3> Tip = new();

	public readonly StringParam Thickness = new( "Thickness (root,tip)", "0.26,0.04" );
	public readonly IntParam Rays = new( "Rays", 14, 2, 64 );
	public readonly FloatParam Ridge = new( "Ridge", 0.06f, 0f, 10f, unit: "u" );
	public readonly FloatParam Web = new( "Web dip", 0.72f, 0f, 1f );
	public readonly FloatParam Curve = new( "Curve", 0.15f, -1f, 1f );
	public readonly IntParam Subdiv = new( "Subdiv", 1, 1, 16 );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Thickness, Rays, Ridge, Web, Curve, Subdiv, Material };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Root.Count < 2 )
		{
			Fail( "A fin needs at least two root points.",
				"Root is the line the fin attaches along; one point is not a line.",
				"Give root as two or more points, e.g. root=0,17,0;0,18,0.5;0,19,0" );
		}

		if ( Tip.Count < 2 )
		{
			Fail( "A fin needs at least two tip points.",
				"Tip is the far margin of the fin; one point is not a margin.",
				"Give tip as two or more points, e.g. tip=0,24,3.7;0,21,0;0,24,-3.5" );
		}

		var parts = (Thickness.Value ?? "").Split( ',' ).Select( p => p.Trim() ).Where( p => p.Length > 0 ).ToArray();
		float rootThickness = 0f, tipThickness = 0f;

		if ( parts.Length == 1
			&& float.TryParse( parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var single ) )
		{
			rootThickness = tipThickness = single;
		}
		else if ( parts.Length == 2
			&& float.TryParse( parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rootThickness )
			&& float.TryParse( parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tipThickness ) )
		{
			// Parsed above.
		}
		else
		{
			FailOn( Thickness.Label,
				$"Thickness reads '{Thickness.Value}', which is not one or two numbers.",
				"Thickness is the solid thickness at the root and at the tip.",
				"Write it like thickness=0.26,0.04",
				"Write one number to use it at both ends" );
		}

		if ( rootThickness <= 0f || tipThickness <= 0f )
		{
			FailOn( Thickness.Label,
				$"Thickness must be positive, not root {rootThickness} tip {tipThickness}.",
				"A zero-thickness tip would collapse the fin's two sides onto each other.",
				"Give a small positive tip thickness, e.g. thickness=0.26,0.04" );
		}

		var mesh = FinBody.Build( Root, Tip, rootThickness, tipThickness, Rays.Clamped, Ridge.Value, Web.Clamped, Curve.Value, Subdiv.Clamped, Material.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}
}
