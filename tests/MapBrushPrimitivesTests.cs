#nullable enable
using System;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Tests for hammer-brush-primitives-block-tool: block-tool primitives as plane brushes.
/// Every check goes through the MapBrush API specified by
/// hammer-brush-core-plane-based-brushes (FromPlanes, Faces, Volume, Centroid, IsValid,
/// Validate) — no mesh primitive path is used for map geometry anywhere here.
///
/// Analytic references (kernel inches, Z-up; tangent-plane brushes circumscribe the
/// analytic solid, so their volume is ABOVE the analytic value and converges from above):
///   box 128x128x64        -> volume exactly 1,048,576; 6 faces.
///   wedge 128x64x32       -> volume exactly 131,072 (half the box); 5 faces;
///                            centroid (-sizeX/6, 0, -sizeZ/6): mass sits toward the high -X side.
///   cylinder r32 h64, 8   -> 10 faces; volume = 8*R^2*tan(pi/8)*H = 1.0548x analytic.
///   sphere r16, div4 seg16 -> 50 faces; volume 1.1000x analytic (measured independently
///                            with a plane-intersection model; see the fixture README).
///   sphere r16, div8 seg32 -> 226 faces; volume 1.0231x analytic.
///   arch R64 h32 180deg/8 -> 11 faces; volume 1.018x the half-cylinder sector.
///   arch R64 h32 90deg/4  -> 8 faces; volume 1.0235x the quarter sector.
///
/// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
/// final integrator adds the one MapBrushPrimitivesTests.Register(registry, settings); line.
/// </summary>
internal static class MapBrushPrimitivesTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "mapbrushprimitives", "box 128x128x64 has 6 faces and exact volume", () =>
		{
			var box = MapBrushPrimitives.Box( 128f, 128f, 64f );
			Assert( box.IsValid, $"box must be valid: {Defects( box )}" );
			Assert( box.Faces.Count == 6, $"box must have 6 faces, got {box.Faces.Count}" );
			Assert( MathF.Abs( box.Volume - 128f * 128f * 64f ) < 0.001f * 128f * 128f * 64f,
				$"box volume must be 1048576, got {box.Volume}" );
			Assert( box.Centroid.AlmostEquals( Vec3.Zero, 1e-3f ),
				$"centred box centroid must be the origin, got {box.Centroid}" );
		} );

		registry.Add( "mapbrushprimitives", "box volume and centroid follow size and position", () =>
		{
			var box = MapBrushPrimitives.Box( 2f, 3f, 4f, position: new Vec3( 10f, 20f, 30f ) );
			Assert( box.IsValid, $"box must be valid: {Defects( box )}" );
			Assert( MathF.Abs( box.Volume - 24f ) < 1e-3f, $"2x3x4 box volume must be 24, got {box.Volume}" );
			Assert( box.Centroid.AlmostEquals( new Vec3( 10f, 20f, 30f ), 1e-3f ),
				$"box centroid must follow its position, got {box.Centroid}" );
		} );

		registry.Add( "mapbrushprimitives", "box scale stretches the volume", () =>
		{
			var box = MapBrushPrimitives.Box( 2f, 2f, 2f, scale: new Vec3( 2f, 1f, 1f ) );
			Assert( box.IsValid, $"scaled box must be valid: {Defects( box )}" );
			Assert( MathF.Abs( box.Volume - 16f ) < 1e-2f,
				$"2x2x2 box scaled x2 on X must have volume 16, got {box.Volume}" );
		} );

		registry.Add( "mapbrushprimitives", "wedge mirrors PrimitiveFeature wedge", () =>
		{
			var wedge = MapBrushPrimitives.Wedge( 128f, 64f, 32f );
			Assert( wedge.IsValid, $"wedge must be valid: {Defects( wedge )}" );
			Assert( wedge.Faces.Count == 5, $"wedge must have 5 faces, got {wedge.Faces.Count}" );
			Assert( MathF.Abs( wedge.Volume - 0.5f * 128f * 64f * 32f ) < 0.001f * 0.5f * 128f * 64f * 32f,
				$"wedge volume must be 131072, got {wedge.Volume}" );
			// High edge at -X (as in Primitives.Wedge): centroid sits at (-sizeX/6, 0, -sizeZ/6).
			Assert( wedge.Centroid.AlmostEquals( new Vec3( -128f / 6f, 0f, -32f / 6f ), 1e-3f ),
				$"wedge centroid must be (-21.333, 0, -5.333), got {wedge.Centroid}" );
		} );

		registry.Add( "mapbrushprimitives", "cylinder 8-sided volume and face count", () =>
		{
			var cyl = MapBrushPrimitives.Cylinder( 32f, 64f, 8 );
			Assert( cyl.IsValid, $"cylinder must be valid: {Defects( cyl )}" );
			Assert( cyl.Faces.Count == 10, $"8-sided cylinder must have 10 faces, got {cyl.Faces.Count}" );
			var analytic = MathF.PI * 32f * 32f * 64f;
			Assert( cyl.Volume > analytic,
				$"tangent-plane cylinder must circumscribe the analytic cylinder, got {cyl.Volume} vs {analytic}" );
			Assert( cyl.Volume < 1.10f * analytic,
				$"8-sided cylinder volume must be within 10% of analytic {analytic}, got {cyl.Volume}" );
		} );

		registry.Add( "mapbrushprimitives", "cylinder segment count is respected exactly", () =>
		{
			Assert( MapBrushPrimitives.Cylinder( 16f, 32f, 3 ).Faces.Count == 5,
				"3-sided cylinder must have 5 faces" );
			Assert( MapBrushPrimitives.Cylinder( 16f, 32f, 5 ).Faces.Count == 7,
				"5-sided cylinder must have 7 faces (no rounding to a multiple of 8)" );
			Assert( MapBrushPrimitives.Cylinder( 16f, 32f, 24 ).Faces.Count == 26,
				"24-sided cylinder must have 26 faces" );
		} );

		registry.Add( "mapbrushprimitives", "cylinder volume scales with radius squared", () =>
		{
			var small = MapBrushPrimitives.Cylinder( 16f, 64f, 16 );
			var big = MapBrushPrimitives.Cylinder( 32f, 64f, 16 );
			Assert( MathF.Abs( big.Volume / small.Volume - 4f ) < 0.005f * 4f,
				$"doubling the radius must quadruple the volume, got ratio {big.Volume / small.Volume}" );
		} );

		registry.Add( "mapbrushprimitives", "sphere default faceting is valid and within 15 percent", () =>
		{
			var sphere = MapBrushPrimitives.Sphere( 16f, 4, 16 );
			Assert( sphere.IsValid, $"sphere must be valid: {Defects( sphere )}" );
			Assert( sphere.Faces.Count == 50,
				$"div4/seg16 sphere must have 2 + 3*16 = 50 faces, got {sphere.Faces.Count}" );
			var analytic = 4f / 3f * MathF.PI * 16f * 16f * 16f;
			Assert( sphere.Volume > analytic,
				$"tangent-plane sphere must circumscribe the analytic sphere, got {sphere.Volume} vs {analytic}" );
			Assert( sphere.Volume < 1.15f * analytic,
				$"div4/seg16 sphere volume must be within 15% of analytic {analytic}, got {sphere.Volume}" );
		} );

		registry.Add( "mapbrushprimitives", "sphere converges to analytic volume", () =>
		{
			var sphere = MapBrushPrimitives.Sphere( 16f, 8, 32 );
			Assert( sphere.IsValid, $"fine sphere must be valid: {Defects( sphere )}" );
			Assert( sphere.Faces.Count == 226,
				$"div8/seg32 sphere must have 2 + 7*32 = 226 faces, got {sphere.Faces.Count}" );
			var analytic = 4f / 3f * MathF.PI * 16f * 16f * 16f;
			Assert( sphere.Volume < 1.05f * analytic,
				$"div8/seg32 sphere volume must be within 5% of analytic {analytic}, got {sphere.Volume}" );
		} );

		registry.Add( "mapbrushprimitives", "arch half cylinder has 11 faces and sector volume", () =>
		{
			var arch = MapBrushPrimitives.Arch( 64f, 32f, 180f, 8 );
			Assert( arch.IsValid, $"arch must be valid: {Defects( arch )}" );
			Assert( arch.Faces.Count == 11,
				$"180-degree arch must have 8 outer + 1 diameter + 2 caps = 11 faces, got {arch.Faces.Count}" );
			var analytic = 0.5f * MathF.PI * 64f * 64f * 32f;
			Assert( arch.Volume < 1.05f * analytic,
				$"half-cylinder arch volume must be within 5% of analytic {analytic}, got {arch.Volume}" );
			Assert( arch.Volume > analytic,
				$"arch must circumscribe the sector, got {arch.Volume} vs {analytic}" );
		} );

		registry.Add( "mapbrushprimitives", "arch quarter sweep has 8 faces and sector volume", () =>
		{
			var arch = MapBrushPrimitives.Arch( 64f, 32f, 90f, 4 );
			Assert( arch.IsValid, $"quarter arch must be valid: {Defects( arch )}" );
			Assert( arch.Faces.Count == 8,
				$"90-degree arch must have 4 outer + 2 ends + 2 caps = 8 faces, got {arch.Faces.Count}" );
			var analytic = 0.25f * MathF.PI * 64f * 64f * 32f;
			Assert( arch.Volume < 1.05f * analytic,
				$"quarter arch volume must be within 5% of analytic {analytic}, got {arch.Volume}" );
		} );

		registry.Add( "mapbrushprimitives", "arch over 180 degrees is rejected", () =>
		{
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Arch( 64f, 32f, 270f, 8 ),
				"a 270-degree arch sector is concave and cannot be one convex brush" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Arch( 64f, 32f, 0f, 8 ),
				"a zero-degree arch is not a solid" );
		} );

		registry.Add( "mapbrushprimitives", "invalid dimensions are rejected with named arguments", () =>
		{
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Box( 0f, 1f, 1f ), "zero box width" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Box( -1f, 1f, 1f ), "negative box width" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Wedge( 1f, 1f, 0f ), "zero wedge height" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Cylinder( 0f, 1f ), "zero cylinder radius" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Cylinder( 1f, 1f, 2 ), "2-sided cylinder" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Sphere( 0f, 4, 16 ), "zero sphere radius" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Sphere( 16f, 1, 16 ), "1-band sphere is a slab" );
			AssertThrows<ArgumentOutOfRangeException>( () => MapBrushPrimitives.Arch( 64f, 32f, 180f, 1 ), "1-facet arch" );
			AssertThrows<ArgumentOutOfRangeException>(
				() => MapBrushPrimitives.Box( 1f, 1f, 1f, scale: new Vec3( 1f, 0f, 1f ) ), "zero scale axis" );
		} );

		registry.Add( "mapbrushprimitives", "material slot is carried on every face", () =>
		{
			var box = MapBrushPrimitives.Box( 4f, 4f, 4f, material: 3 );
			Assert( box.Faces.All( f => f.MaterialId == "material_3" ),
				$"every box face must carry material slot 3, got {string.Join( ",", box.Faces.Select( f => f.Material ) )}" );
			var plain = MapBrushPrimitives.Box( 4f, 4f, 4f );
			Assert( plain.Faces.All( f => f.MaterialId == "material_0" ), "default material slot must be 0" );
		} );

		registry.Add( "mapbrushprimitives", "position moves the cylinder centroid", () =>
		{
			var cyl = MapBrushPrimitives.Cylinder( 8f, 16f, 8, position: new Vec3( 5f, -7f, 3f ) );
			Assert( cyl.Centroid.AlmostEquals( new Vec3( 5f, -7f, 3f ), 1e-3f ),
				$"cylinder centroid must follow its position, got {cyl.Centroid}" );
		} );
	}

	static string Defects( MapBrush brush ) => string.Join( "; ", brush.Validate() );

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	static void AssertThrows<T>( Action body, string what ) where T : Exception
	{
		try { body(); }
		catch ( T ) { return; }
		catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name} for {what}, got {ex.GetType().Name}: {ex.Message}" ); }
		throw new Exception( $"expected {typeof( T ).Name} for {what}, but nothing was thrown" );
	}
}
