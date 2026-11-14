using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;

// Godlike parametric sketcher: solver status, driven dimensions, headless inference,
// variable rename propagation, reorder/suppress, and the bracket acceptance workflow.
//
// NOT YET REGISTERED in tests/TestRegistry.cs (coordinator-owned; the final integrator
// adds the one SketchParametricTests.Register line). Written for the final build/test gate,
// not run here: this ticket is edits-only, no full compile (PO workflow override 2026-11-13).
//
// What is NOT covered here, and why:
// - The Driven flag does not survive save/reopen yet. The serializer still writes the old
//   8-field constraint line; the 9th field ships as
//   handoffs/sketch-driven-dimension-serialization.patch (dry-run clean), which the integrator
//   applies to kernel/Effigy/Features/StudioDocument.cs, then adds the round-trip check.
// - Exact B-rep solids are the separate godlike-occt-brep-core-step-export task. The solver and
//   tree here drive the existing mesh features (extrude, fillet); nothing below claims a solid
//   is exact, only that the mesh it produces has the right bounds, hole and face counts.
//
// Fixture note: the solver pins point 0 to give the sketch a frame, and a Fixed on point 0 is
// absorbed into that pin (see SketchSolver). So a "fully constrained" rectangle needs Horizontal
// x2, Vertical x2, width, height and Fixed on point 0 — 6 live rows over 6 free coordinates,
// 0 degrees of freedom — and the Fixed does NOT show up as redundant.
internal static class SketchParametricTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "sketch-parametric", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tol = 1e-5f )
		{
			Require( MathF.Abs( actual - expected ) <= tol, $"expected {expected}, got {actual}" );
		}

		// Rectangle 0..w x 0..h with its four corner points 0..3 and lines bottom, right, top,
		// left. Returns the sketch and its lines in that order.
		(Sketch Sketch, List<SketchLine> Lines) Rect( float w, float h )
		{
			var sketch = new Sketch();
			var lines = sketch.AddRectangle( new Vec2( 0, 0 ), new Vec2( w, h ) );
			return (sketch, lines);
		}

		// Horizontal on the bottom/top lines, Vertical on the right/left lines, Fixed on point 0.
		void ConstrainFrame( Sketch sketch, List<SketchLine> lines )
		{
			sketch.AddConstraint( lines[0], SketchConstraintKind.Horizontal );
			sketch.AddConstraint( lines[2], SketchConstraintKind.Horizontal );
			sketch.AddConstraint( lines[1], SketchConstraintKind.Vertical );
			sketch.AddConstraint( lines[3], SketchConstraintKind.Vertical );
			sketch.AddConstraint( SketchConstraintKind.Fixed, 0, -1, 0f );
		}

		Check( "fully-constrained-rectangle-reports-zero-dof", () =>
		{
			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 3, 40f );

			var solve = sketch.Solve();

			Require( solve.Converged, "a satisfiable rectangle must converge" );
			Require( solve.DegreesOfFreedom == 0, $"expected 0 DOF, got {solve.DegreesOfFreedom}" );
			Require( solve.Status == SketchStatus.FullyDefined, $"expected FullyDefined, got {solve.Status}" );
			Require( solve.RedundantConstraints == 0, $"expected no redundancy, got {solve.RedundantConstraints}" );
			Require( solve.ViolatedConstraints.Count == 0, "nothing should be violated" );
			Require( SketchDiagnosis.Describe( sketch, solve ) == "Fully defined." );
		} );

		Check( "under-constrained-rectangle-reports-one-dof", () =>
		{
			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );

			var solve = sketch.Solve();

			Require( solve.Converged, "an under-constrained sketch still converges" );
			Require( solve.DegreesOfFreedom == 1, $"expected 1 DOF (the height), got {solve.DegreesOfFreedom}" );
			Require( solve.Status == SketchStatus.UnderDefined, $"expected UnderDefined, got {solve.Status}" );
			Require( SketchDiagnosis.Describe( sketch, solve ) == "Under-defined by 1: the sketch can still be moved." );
		} );

		Check( "conflicting-distances-are-named", () =>
		{
			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			var first = sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 3, 40f );
			var second = sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 100f );

			var solve = sketch.Solve();
			var firstIndex = sketch.Constraints.IndexOf( first );
			var secondIndex = sketch.Constraints.IndexOf( second );

			Require( !solve.Converged, "80 vs 100 on the same segment cannot both hold" );
			Require( solve.Status == SketchStatus.Conflicting, $"expected Conflicting, got {solve.Status}" );
			Require( solve.ViolatedConstraints.Contains( firstIndex ), "the 80 rule must be named violated" );
			Require( solve.ViolatedConstraints.Contains( secondIndex ), "the 100 rule must be named violated" );
			Require( !solve.RedundantConstraintIndices.Contains( firstIndex ), "a violated rule is not redundant" );
			Require( !solve.RedundantConstraintIndices.Contains( secondIndex ), "a violated rule is not redundant" );
			var text = SketchDiagnosis.Describe( sketch, solve );
			Require( text.StartsWith( "Conflicting: Distance #" ), $"diagnosis must name the rules, got: {text}" );
			Require( text.Contains( $"Distance #{firstIndex}" ) && text.Contains( $"Distance #{secondIndex}" ) );
		} );

		Check( "duplicate-distance-is-over-defined", () =>
		{
			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 3, 40f );
			var duplicate = sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );

			var solve = sketch.Solve();
			var duplicateIndex = sketch.Constraints.IndexOf( duplicate );

			Require( solve.Converged, "a duplicate rule is satisfiable" );
			Require( solve.DegreesOfFreedom == 0, "the sketch is still fully defined" );
			Require( solve.Status == SketchStatus.OverDefined, $"expected OverDefined, got {solve.Status}" );
			Require( solve.RedundantConstraints > 0, "the duplicate must count as redundant" );
			Require( solve.RedundantConstraintIndices.Contains( duplicateIndex ), "the duplicate rule must be named" );
			var text = SketchDiagnosis.Describe( sketch, solve );
			Require( text.StartsWith( "Over-defined: Distance #" ), $"diagnosis must name the rule, got: {text}" );
		} );

		Check( "driven-dimension-measures-without-driving", () =>
		{
			var studio = new PartStudio();
			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 10f );
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 3, 40f );
			var driven = sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 99f );
			driven.Driven = true;
			// A driven expression is never evaluated: a missing variable here must not fail
			// the feature, because the dimension reports instead of driving.
			driven.Expr = "#missing_var";

			var sketchFeature = new SketchFeature { Name = "Plate sketch", Sketch = sketch };
			studio.Features.Add( sketchFeature );
			var report = studio.Rebuild();

			Require( sketchFeature.Error is null, $"driven Expr must not fail the feature, got: {sketchFeature.Error}" );
			Require( report.Errors.Count == 0, "rebuild must be clean" );
			Require( sketchFeature.Status == SketchStatus.FullyDefined, $"expected FullyDefined, got {sketchFeature.Status}" );
			Near( sketch.Points[1].x, 10f, 0.01f );
			Near( driven.Value, 10f, 0.01f );
		} );

		Check( "inference-adds-horizontal-vertical-and-coincident", () =>
		{
			var sketch = new Sketch();
			sketch.AddLine( new Vec2( 0, 0 ), new Vec2( 10, 0 ) );
			// Distinct points at the same location (10, 0): the second line does NOT share the
			// first line's endpoint, so nothing says they meet until inference does.
			sketch.AddLine( new Vec2( 10, 0 ), new Vec2( 10, 10 ) );
			// Near-horizontal, 0.06 degrees off axis: inside the 1 degree tolerance.
			sketch.AddLine( new Vec2( 20, 0 ), new Vec2( 30, 0.01f ) );

			var added = ConstraintTools.InferConstraints( sketch );

			Require( added.Any( c => c.Kind == SketchConstraintKind.Horizontal ), "a horizontal rule must be inferred" );
			Require( added.Any( c => c.Kind == SketchConstraintKind.Vertical ), "a vertical rule must be inferred" );
			Require( added.Any( c => c.Kind == SketchConstraintKind.Coincident ), "the shared endpoint must be made coincident" );
			var secondPass = ConstraintTools.InferConstraints( sketch );
			Require( secondPass.Count == 0, $"second pass must add nothing, added {secondPass.Count}" );
		} );

		Check( "variable-rename-propagates-everywhere", () =>
		{
			var studio = new PartStudio();
			studio.SetVariable( "plate_width", "80" );
			studio.SetVariable( "plate_width2", "5" );
			studio.SetVariable( "other", "#plate_width2 + 1" );

			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			var width = sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );
			width.Expr = "#plate_width";
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 3, 40f );

			var sketchFeature = new SketchFeature { Name = "Plate sketch", Sketch = sketch };
			var extrude = new ExtrudeFeature { Name = "Plate" };
			extrude.Distance.Expr = "#plate_width";
			studio.Features.Add( sketchFeature );
			studio.Features.Add( extrude );

			var ok = ParametricTree.RenameVariable( studio, "plate_width", "width", out var error, out var changed );

			Require( ok, $"rename must succeed, got: {error}" );
			Require( error is null, $"no error expected, got: {error}" );
			Require( changed >= 2, $"at least the extrude and the sketch must change, got {changed}" );
			Require( extrude.Distance.Expr == "#width", $"extrude must read #width, got {extrude.Distance.Expr}" );
			Require( width.Expr == "#width", $"sketch must read #width, got {width.Expr}" );
			var other = studio.Variables.First( v => v.Name == "other" );
			Require( other.Expr == "#plate_width2 + 1", $"#plate_width2 must not be rewritten, got {other.Expr}" );

			var report = studio.Rebuild();

			Require( report.Errors.Count == 0, "rebuild after rename must be clean" );
			Require( extrude.Error is null, $"extrude must build, got: {extrude.Error}" );
			Near( extrude.Distance.Value, 80f, 0.01f );
			Near( sketch.Points[1].x, 80f, 0.01f );

			Require( !ParametricTree.RenameVariable( studio, "nope", "x", out _, out _ ), "renaming a missing variable must fail" );
			Require( !ParametricTree.RenameVariable( studio, "width", "1bad", out _, out _ ), "an illegal new name must fail" );
			Require( !ParametricTree.RenameVariable( studio, "width", "plate_width2", out _, out _ ), "renaming onto an existing variable must fail" );
		} );

		Check( "rename-feature-and-suppress", () =>
		{
			var studio = new PartStudio();
			var (sketch, _) = Rect( 80, 40 );
			var sketchFeature = new SketchFeature { Name = "Plate sketch", Sketch = sketch };
			var extrude = new ExtrudeFeature { Name = "Plate" };
			extrude.Distance.Value = 10f;
			studio.Features.Add( sketchFeature );
			studio.Features.Add( extrude );
			studio.Rebuild();

			Require( ParametricTree.RenameFeature( studio, extrude, "Bracket plate", out var error ), $"rename must succeed, got: {error}" );
			Require( extrude.Name == "Bracket plate" );
			Require( !ParametricTree.RenameFeature( studio, extrude, "Plate sketch", out _ ), "renaming onto an existing feature must fail" );
			Require( ParametricTree.SetSuppressed( studio, extrude, true ) );
			Require( extrude.Suppressed );
			var report = studio.Rebuild();
			Require( report.FeaturesSuppressed == 1, "the suppressed extrude must be counted, not run" );
			Require( studio.Bodies.Count == 0, "a suppressed extrude leaves no body behind" );
			Require( ParametricTree.SetSuppressed( studio, extrude, false ) );
			studio.Rebuild();
			Require( studio.Bodies.Count == 1, "unsuppressing brings the body back" );
		} );

		// The bracket workflow, shared by the reorder/suppress checks and the acceptance check:
		// plate sketch (width driven by #plate_width, hole centred by a midpoint rule) +
		// extrude 10 + fillet radius 1.
		(PartStudio Studio, Sketch Sketch, int Centre, SketchFeature SketchFeature, ExtrudeFeature Extrude, FilletFeature Fillet) Bracket()
		{
			var studio = new PartStudio();
			studio.SetVariable( "plate_width", "80" );
			var (sketch, lines) = Rect( 80, 40 );
			ConstrainFrame( sketch, lines );
			var width = sketch.AddConstraint( SketchConstraintKind.Distance, 0, 1, 80f );
			width.Expr = "#plate_width";
			sketch.AddConstraint( SketchConstraintKind.Distance, 0, 3, 40f );
			var centre = sketch.AddPoint( new Vec2( 40, 20 ) );
			var midpoint = sketch.AddConstraint( SketchConstraintKind.Midpoint, centre, 0 );
			midpoint.PointC = 2;
			sketch.Add( new SketchCircle( centre, 5f ) );

			var sketchFeature = new SketchFeature { Name = "Plate sketch", Sketch = sketch };
			var extrude = new ExtrudeFeature { Name = "Plate" };
			extrude.Distance.Value = 10f;
			var fillet = new FilletFeature { Name = "Edge fillet" };
			fillet.Radius.Value = 1f;
			studio.Features.Add( sketchFeature );
			studio.Features.Add( extrude );
			studio.Features.Add( fillet );
			return (studio, sketch, centre, sketchFeature, extrude, fillet);
		}

		Check( "reorder-fillet-before-extrude-errors-without-crashing", () =>
		{
			var (studio, _, _, _, extrude, fillet) = Bracket();
			studio.Rebuild();
			Require( fillet.Error is null, $"fillet must build after the extrude, got: {fillet.Error}" );

			studio.Move( 2, 1 );
			var report = studio.Rebuild();

			Require( fillet.Error is not null, "a fillet with no bodies yet must report an error, not silently pass" );
			Require( extrude.Error is null, "the extrude after it must still build" );
			Require( report.Errors.Count == 1, $"exactly the fillet should error, got {report.Errors.Count}" );
			Require( studio.ToMesh().FaceCount > 0, "the plate must still be there" );
		} );

		Check( "suppress-fillet-removes-its-faces", () =>
		{
			var (studio, _, _, _, _, fillet) = Bracket();
			studio.Rebuild();
			Require( fillet.Error is null, $"fillet must build, got: {fillet.Error}" );
			var filleted = studio.ToMesh().FaceCount;

			Require( ParametricTree.SetSuppressed( studio, fillet, true ) );
			studio.Rebuild();
			var suppressed = studio.ToMesh().FaceCount;
			Require( suppressed < filleted, $"suppressing the fillet must remove faces ({suppressed} vs {filleted})" );

			Require( ParametricTree.SetSuppressed( studio, fillet, false ) );
			studio.Rebuild();
			Require( studio.ToMesh().FaceCount == filleted, "unsuppressing must bring the fillet back" );
		} );

		Check( "bracket-workflow-width-80-to-100", () =>
		{
			var (studio, sketch, centre, _, extrude, fillet) = Bracket();
			studio.Rebuild();

			void CheckBracket( float width, float centreX )
			{
				var mesh = studio.ToMesh();
				Require( mesh.Positions.Count > 0, "the plate must have vertices" );
				Near( mesh.Positions.Min( p => p.x ), 0f, 0.05f );
				Near( mesh.Positions.Max( p => p.x ), width, 0.05f );
				Near( sketch.Points[centre].x, centreX, 0.05f );
				Require( extrude.Error is null, $"extrude must build, got: {extrude.Error}" );
				Require( fillet.Error is null, $"fillet must rebuild, got: {fillet.Error}" );
				var profiles = ProfileFinder.Find( sketch ).Profiles;
				Require( profiles.Count == 1, $"one plate profile expected, got {profiles.Count}" );
				Require( profiles[0].HasHoles, "the centred circle must be a hole, not a second profile" );
				Require( profiles[0].Holes.Count == 1, "exactly one hole expected" );
				// width*40 minus the r=5 hole, polygon-tessellated: 3921.5 exact for a true
				// circle at width 100 (3121.5 at width 80), a little above it for the
				// inscribed polygon. width*40 would mean the hole never happened. The
				// old bounds (3900..3950) only fit width 100, so the width-80 call
				// could never pass even though its area (3121.667) was already right.
				Require( profiles[0].Area > width * 40f - 100f && profiles[0].Area < width * 40f - 50f, $"profile area must be plate minus hole, got {profiles[0].Area}" );
			}

			CheckBracket( 80f, 40f );

			Require( studio.SetVariable( "plate_width", "100" ), "the variable change must be accepted" );
			studio.Rebuild();
			CheckBracket( 100f, 50f );
		} );
	}
}
