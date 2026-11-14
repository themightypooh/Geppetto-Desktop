using System;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class BrepSolidTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "brep-solid", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}
		void Approx( double actual, double expected, double relTol, string what )
		{
			var tol = Math.Max( 1e-9, Math.Abs( expected ) * relTol );
			Require( Math.Abs( actual - expected ) <= tol, $"{what}: got {actual}, expected {expected} (tol {tol})" );
		}

		Check( "H7 bore: the analytic tube bore is exactly 50 mm and its measures are exact", () =>
		{
			var tube = BrepSolid.Tube( 30, 25, 20 );
			// H7 for a 50 mm bore is +0.025 / 0 mm: the bore must measure 50.000..50.025.
			var bore = tube.Faces.Where( f => f.Kind == BrepSurfaceKind.Cylinder ).OrderBy( f => f.Radius ).First();
			Approx( bore.Radius * 2, 50.0, 0, "bore diameter" );
			Require( bore.Radius * 2 >= 50.0 && bore.Radius * 2 <= 50.025, "bore outside H7" );
			Approx( tube.VolumeMm3, Math.PI * (30 * 30 - 25 * 25) * 20, 1e-9, "tube volume" );
			Approx( tube.AreaMm2, 2750 * Math.PI, 1e-9, "tube area" );
			Require( tube.IsClosed, "tube is not closed" );
			Require( tube.Validate().Count == 0, "tube failed validation: " + string.Join( "; ", tube.Validate() ) );
		} );

		Check( "a 16-sided faceted cylinder fails H7 by about 19x", () =>
		{
			// Sagitta of a 16-gon on a 25 mm bore: 25 * (1 - cos(pi/16)) = 0.480 mm.
			var error = BrepTessellation.ChordalError( 25, 16 );
			Require( error > 0.48 && error < 0.49, $"16-gon sagitta was {error} mm, expected ~0.48" );
			Require( error > 19 * 0.025, $"faceted error {error} mm is not ~19x the 0.025 mm H7 tolerance" );
		} );

		Check( "tessellation deflection control: 96 sides cut 0.013 mm, precision needs more", () =>
		{
			var sides = BrepTessellation.SidesForDeflection( 25, 0.005 );
			Require( BrepTessellation.ChordalError( 25, sides ) <= 0.005, "precision sides do not meet 0.005 mm" );
			Require( BrepTessellation.ChordalError( 25, sides - 1 ) > 0.005, "side count is not minimal" );
			var e96 = BrepTessellation.ChordalError( 25, 96 );
			Require( e96 > 0.013 && e96 < 0.014, $"96-sided error was {e96} mm, expected ~0.013" );
			Require( e96 <= 0.05, "96 sides do not meet the 0.05 mm print tolerance" );
			Require( BrepTessellation.SidesForDeflection( 25, 0.05 ) <= 96, "print tolerance should need at most 96 sides" );
		} );

		Check( "tube tessellation uses the deflection side counts exactly", () =>
		{
			var tube = BrepSolid.Tube( 30, 25, 20 );
			var tess = BrepTessellation.Tessellate( tube, 0.01 );
			var nOut = Math.Max( BrepTessellation.SidesForDeflection( 30, 0.01 ), 24 );
			var nIn = Math.Max( BrepTessellation.SidesForDeflection( 25, 0.01 ), 24 );
			// Outer lateral 2*nOut, inner lateral 2*nIn, two annulus rings of 2*32 each.
			Require( tess.TriangleCount == 2 * (nOut + nIn + 64), $"triangles {tess.TriangleCount}, expected {2 * (nOut + nIn + 64)}" );
			Require( tess.Vertices.Count == (nOut + 1) * 2 + (nIn + 1) * 2 + 128, $"vertices {tess.Vertices.Count}, expected {(nOut + 1) * 2 + (nIn + 1) * 2 + 128}" );
		} );

		Check( "STEP round-trip preserves the tube exactly", () =>
		{
			var tube = BrepSolid.Tube( 30, 25, 20 );
			var step = StepExchange.Write( tube, "tube" );
			Require( step.Contains( "CYLINDRICAL_SURFACE" ), "STEP has no cylindrical surface" );
			Require( step.Contains( "MANIFOLD_SOLID_BREP" ), "STEP has no manifold solid brep" );
			Require( step.Contains( "ADVANCED_BREP_SHAPE_REPRESENTATION" ), "STEP has no shape representation" );
			var read = StepExchange.Read( step );
			Require( read.Ok, "STEP read failed: " + string.Join( "; ", read.Errors ) );
			Require( read.Warnings.Count == 0, "STEP read warned: " + string.Join( "; ", read.Warnings ) );
			Require( read.Solid.Faces.Count == 4, $"round-tripped tube has {read.Solid.Faces.Count} faces, expected 4" );
			var radii = read.Solid.Faces.Where( f => f.Kind == BrepSurfaceKind.Cylinder ).Select( f => f.Radius ).OrderBy( r => r ).ToArray();
			Approx( radii[0], 25.0, 0, "bore radius after round-trip" );
			Approx( radii[1], 30.0, 0, "outer radius after round-trip" );
			Approx( read.Solid.VolumeMm3, tube.VolumeMm3, 1e-9, "volume after round-trip" );
			Require( read.Solid.IsClosed, "round-tripped tube is not closed" );
			Require( read.Solid.Validate().Count == 0, "round-tripped tube failed validation: " + string.Join( "; ", read.Solid.Validate() ) );
		} );

		Check( "STEP round-trip preserves a box", () =>
		{
			var box = BrepSolid.Box( 10, 20, 30 );
			var read = StepExchange.Read( StepExchange.Write( box, "box" ) );
			Require( read.Ok, "box STEP read failed: " + string.Join( "; ", read.Errors ) );
			Require( read.Solid.Faces.Count == 6, $"round-tripped box has {read.Solid.Faces.Count} faces, expected 6" );
			Approx( read.Solid.VolumeMm3, 6000.0, 1e-9, "box volume after round-trip" );
			Require( read.Solid.IsClosed, "round-tripped box is not closed" );
		} );

		Check( "box is closed and valid with exact measures", () =>
		{
			var box = BrepSolid.Box( 10, 20, 30 );
			Approx( box.VolumeMm3, 6000.0, 0, "box volume" );
			Approx( box.AreaMm2, 2200.0, 0, "box area" );
			Require( box.Faces.Count == 6, "box has wrong face count" );
			Require( box.Edges.Count == 12, "box has wrong edge count" );
			Require( box.IsClosed, "box is not closed" );
			Require( box.Validate().Count == 0, "box failed validation" );
		} );

		Check( "an open shell is invalid with a named problem", () =>
		{
			var box = BrepSolid.Box( 10, 20, 30 );
			box.Faces.RemoveAt( 0 );
			Require( !box.IsClosed, "box with a face removed still claims to be closed" );
			var problems = box.Validate();
			Require( problems.Any( p => p.StartsWith( "brep-open-edge" ) ), "open edge not named: " + string.Join( "; ", problems ) );
			Throws( () => StepExchange.Write( box, "open" ) );
		} );

		Check( "boolean and fillet fail with named recoverable errors, never silently", () =>
		{
			var tube = BrepSolid.Tube( 30, 25, 20 );
			var union = tube.Boolean( BrepBooleanOp.Union, BrepSolid.Box( 10, 10, 10 ) );
			Require( !union.Ok, "boolean claimed success without an engine" );
			Require( union.Error.Contains( "occt-boolean-unavailable" ), "boolean error is not named: " + union.Error );
			var fillet = tube.Fillet( 1.0 );
			Require( !fillet.Ok, "fillet claimed success without an engine" );
			Require( fillet.Error.Contains( "occt-fillet-unavailable" ), "fillet error is not named: " + fillet.Error );
		} );

		Check( "measure: distance, angle, area and volume on analytic faces", () =>
		{
			var sphere = BrepSolid.Sphere( 10 );
			Approx( sphere.VolumeMm3, 4.0 / 3.0 * Math.PI * 1000, 1e-9, "sphere volume" );
			Approx( sphere.AreaMm2, 4 * Math.PI * 100, 1e-9, "sphere area" );
			Require( sphere.Faces.Count == 2, "sphere should be two hemispheres" );
			Require( sphere.IsClosed, "sphere is not closed" );
			var cyl = BrepSolid.Cylinder( 10, 20 );
			var lateral = cyl.Faces[0];
			Approx( BrepMeasure.DistancePointToFace( new Vec3( 10, 0, 10 ), lateral ), 0.0, 1e-6, "distance on the cylinder" );
			Approx( BrepMeasure.DistancePointToFace( new Vec3( 15, 0, 10 ), lateral ), 5.0, 1e-6, "distance off the cylinder" );
			var box = BrepSolid.Box( 10, 20, 30 );
			Approx( BrepMeasure.AngleBetweenFaces( box.Faces[4], box.Faces[0] ), 90.0, 1e-6, "angle between box faces" );
		} );

		Check( "OCCT loader reports honestly either way", () =>
		{
			OcctNative.ResetLoader();
			var available = OcctNative.Available();
			Require( OcctNative.LastRoute.Length > 0, "loader reported nothing" );
			if ( available )
			{
				Require( OcctNative.Version.StartsWith( "7.", StringComparison.Ordinal ), $"loaded OCCT version is '{OcctNative.Version}', expected 7.x" );
				Require( OcctNative.LastRoute.Contains( "occt " ), "route does not name OCCT: " + OcctNative.LastRoute );
			}
			else
			{
				Require( OcctNative.LastRoute.Contains( "libTKernel" ), "unavailable route does not name the missing library: " + OcctNative.LastRoute );
			}
			_ = OcctNative.StepAvailable();
			OcctNative.ResetLoader();
		} );

		Check( "units convert exactly at the inch boundary", () =>
		{
			var inch = BrepSolid.Box( 1, 1, 1 );
			var mm = inch.Scaled( BrepSolid.MmPerInch );
			Approx( mm.VolumeMm3, 25.4 * 25.4 * 25.4, 1e-9, "one cubic inch in mm^3" );
			Approx( mm.Scaled( 1.0 / BrepSolid.MmPerInch ).VolumeMm3, 1.0, 1e-9, "round-trip back to inches" );
		} );

		_ = settings;
	}
}
