using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS14: numerical and performance regressions for the assembly system.
//
// The numerics half checks the solver and the joint setters against an
// INDEPENDENT oracle written in this file: OMat is a row-major 3x3 + origin
// matrix with its own adjugate inverse and its own rotation constructors.
// It shares no code with kernel Xform and never calls it on the oracle side
// (Xform is used only to build fixtures and to read the values under test).
// Identities, all in inches / Z-up / radians:
//   SolveFixed  = Ta * Fa * Fb^-1
//   SolveHinge  = Translate(axis * along) * Ta * Fa * RotZ(angle) * Fb^-1
//   SolveSlider = Ta * Fa * Trans(0, 0, d / |Ta*Fa.Z|) * Fb^-1
//   AlignFaces  = Ta * Fa * Trans(0, 0, offset) * (opposed ? RotX(pi) : I) * Fb^-1
//   AlignAxis   = Ta * Fa * Trans(0, 0, offset) * Fb^-1
// PosTol / AngTol are 1e-4; RandomSeed is 20261113.
// AlignFaces / AlignAxis are verified through the landed fixed solver with
// frames constructed to encode the alignment: AS07 (AssemblyMateService) had
// not landed when this file was written, so a direct AS07 API check belongs
// to AS07's own tests; the identities themselves are proven here.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyNumericsTests
{
	const float PosTol = 1e-4f;
	const float AngTol = 1e-4f;
	const int RandomSeed = 20261113;

	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-numerics", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float a, float b, float eps, string what )
		{
			if ( MathF.Abs( a - b ) > eps ) throw new Exception( $"{what}: expected {b}, got {a}" );
		}
		void NearVec( Vec3 a, Vec3 b, float eps, string what )
		{
			Near( a.x, b.x, eps, what + ".x" );
			Near( a.y, b.y, eps, what + ".y" );
			Near( a.z, b.z, eps, what + ".z" );
		}
		void NearXform( Xform a, Xform b, float eps, string what )
		{
			NearVec( a.X, b.X, eps, what + ".X" );
			NearVec( a.Y, b.Y, eps, what + ".Y" );
			NearVec( a.Z, b.Z, eps, what + ".Z" );
			NearVec( a.Origin, b.Origin, eps, what + ".Origin" );
		}
		void NearOVec( OVec a, OVec b, float eps, string what )
		{
			Near( a.X, b.X, eps, what + ".x" );
			Near( a.Y, b.Y, eps, what + ".y" );
			Near( a.Z, b.Z, eps, what + ".z" );
		}
		void NearOMat( OMat a, OMat b, float eps, string what )
		{
			Near( a.R00, b.R00, eps, what + ".r00" );
			Near( a.R01, b.R01, eps, what + ".r01" );
			Near( a.R02, b.R02, eps, what + ".r02" );
			Near( a.R10, b.R10, eps, what + ".r10" );
			Near( a.R11, b.R11, eps, what + ".r11" );
			Near( a.R12, b.R12, eps, what + ".r12" );
			Near( a.R20, b.R20, eps, what + ".r20" );
			Near( a.R21, b.R21, eps, what + ".r21" );
			Near( a.R22, b.R22, eps, what + ".r22" );
			NearOVec( a.O, b.O, eps, what + ".origin" );
		}
		bool HasError( AssemblySolveResult result, string code ) =>
			result.Diagnostics.Any( d => d.Kind == "error" && d.Code == code );
		bool HasIssue( IReadOnlyList<AssemblyValidationIssue> issues, string code ) =>
			issues.Any( i => i.Code == code );

		// ---- Fixture builders ----

		(AssemblyDocument Doc, string DefId) NewDoc()
		{
			var doc = new AssemblyDocument();
			var def = new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Link",
				SourceKind = "effigy-file", SourceReference = "parts/link.effigy#body-1",
			};
			doc.Definitions.Add( def );
			return (doc, def.Id);
		}
		string Occ( AssemblyDocument doc, string defId, string name, Xform transform, string? id = null )
		{
			var occ = new AssemblyOccurrence
			{
				Id = id ?? AssemblyDocument.NewId(), DefinitionId = defId, Name = name,
				Transform = AssemblyTransform.FromXform( transform ),
			};
			doc.Occurrences.Add( occ );
			return occ.Id;
		}
		AssemblyJoint Fixed( AssemblyDocument doc, string a, string b, Xform fa, Xform fb, string? id = null )
		{
			var joint = new AssemblyJoint
			{
				Id = id ?? AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a, OccurrenceB = b,
				FrameA = AssemblyTransform.FromXform( fa ), FrameB = AssemblyTransform.FromXform( fb ),
			};
			doc.Joints.Add( joint );
			return joint;
		}
		AssemblyJoint Hinge( AssemblyDocument doc, string a, string b, Xform fa, Xform fb, float? min = null, float? max = null )
		{
			var joint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = a, OccurrenceB = b,
				FrameA = AssemblyTransform.FromXform( fa ), FrameB = AssemblyTransform.FromXform( fb ),
				Minimum = min, Maximum = max,
			};
			doc.Joints.Add( joint );
			return joint;
		}
		AssemblyJoint Slider( AssemblyDocument doc, string a, string b, Xform fa, Xform fb, float? min = null, float? max = null )
		{
			var joint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "slider", OccurrenceA = a, OccurrenceB = b,
				FrameA = AssemblyTransform.FromXform( fa ), FrameB = AssemblyTransform.FromXform( fb ),
				Minimum = min, Maximum = max,
			};
			doc.Joints.Add( joint );
			return joint;
		}
		Vec3 RndDir( Random rng )
		{
			while ( true )
			{
				var v = new Vec3(
					(float)( rng.NextDouble() * 2.0 - 1.0 ),
					(float)( rng.NextDouble() * 2.0 - 1.0 ),
					(float)( rng.NextDouble() * 2.0 - 1.0 ) );
				if ( v.LengthSquared > 0.01f ) return v.Normal;
			}
		}
		Xform Rigid( Random rng, float tRange = 10f )
		{
			var axis = RndDir( rng );
			var angle = (float)( rng.NextDouble() * 2.0 * Math.PI - Math.PI );
			var t = new Vec3(
				(float)( rng.NextDouble() * 2.0 * tRange - tRange ),
				(float)( rng.NextDouble() * 2.0 * tRange - tRange ),
				(float)( rng.NextDouble() * 2.0 * tRange - tRange ) );
			return Xform.Translate( t ) * Xform.Rotate( axis, angle );
		}

		// Independent re-derivation of the fixed solve: BFS over the fixed
		// graph from each component's root (first grounded by id order, else
		// first by id order), composing OccB = OccA * Fa * Fb^-1 with the
		// oracle. Fixtures here are trees, so the result is path-independent.
		Dictionary<string, OMat> OracleFixedSolve( AssemblyDocument doc )
		{
			var occs = doc.Occurrences.Where( o => o != null ).ToList();
			var stored = occs.ToDictionary( o => o.Id, o => OMat.FromXform( o.Transform.ToXform() ), StringComparer.Ordinal );
			var links = new Dictionary<string, List<(string Neighbor, OMat FrameCurrent, OMat FrameNeighbor)>>( StringComparer.Ordinal );
			foreach ( var o in occs ) links[o.Id] = new List<(string, OMat, OMat)>();
			foreach ( var j in doc.Joints.Where( j => j != null && j.Kind == "fixed" ) )
			{
				var fa = OMat.FromXform( j.FrameA.ToXform() );
				var fb = OMat.FromXform( j.FrameB.ToXform() );
				links[j.OccurrenceA].Add( (j.OccurrenceB, fa, fb) );
				links[j.OccurrenceB].Add( (j.OccurrenceA, fb, fa) );
			}
			var solved = new Dictionary<string, OMat>( StringComparer.Ordinal );
			var seen = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var seed in occs.Select( o => o.Id ).OrderBy( id => id, StringComparer.Ordinal ) )
			{
				if ( seen.Contains( seed ) ) continue;
				var closure = new HashSet<string>( StringComparer.Ordinal );
				var walk = new Queue<string>();
				walk.Enqueue( seed );
				closure.Add( seed );
				while ( walk.Count > 0 )
				{
					var cur = walk.Dequeue();
					foreach ( var l in links[cur] )
						if ( closure.Add( l.Neighbor ) ) walk.Enqueue( l.Neighbor );
				}
				foreach ( var id in closure ) seen.Add( id );
				var grounded = closure.Where( id => AssemblyGrounding.IsGrounded( doc, id ) )
					.OrderBy( id => id, StringComparer.Ordinal ).ToList();
				var root = grounded.Count > 0
					? grounded[0]
					: closure.OrderBy( id => id, StringComparer.Ordinal ).First();
				solved[root] = stored[root];
				var bfs = new Queue<string>();
				bfs.Enqueue( root );
				while ( bfs.Count > 0 )
				{
					var cur = bfs.Dequeue();
					foreach ( var l in links[cur] )
					{
						if ( solved.ContainsKey( l.Neighbor ) ) continue;
						solved[l.Neighbor] = OMat.Compose( OMat.Compose( solved[cur], l.FrameCurrent ), l.FrameNeighbor.Inverse() );
						bfs.Enqueue( l.Neighbor );
					}
				}
			}
			return solved;
		}
		void ExpectSolved( AssemblySolveResult result, Dictionary<string, OMat> expected, float eps, string what )
		{
			Require( result.Success, $"{what}: solve failed: {string.Join( "; ", result.Diagnostics.Select( d => d.Code + " " + d.Message ) )}" );
			foreach ( var pair in expected )
			{
				Require( result.TryGetSolvedTransform( pair.Key, out var actual ), $"{what}: no solved transform for {pair.Key}" );
				NearOMat( OMat.FromXform( actual ), pair.Value, eps, $"{what}.{pair.Key}" );
			}
		}

		// ---- Oracle self-checks: hand-computed literals ----

		Check( "oracle-rotations-hand-literal", () =>
		{
			var x = new OVec( 1, 0, 0 );
			var y = new OVec( 0, 1, 0 );
			var z = new OVec( 0, 0, 1 );
			NearOVec( OMat.RotZ( MathF.PI / 2f ).ApplyDir( x ), y, 1e-6f, "RotZ(90) X" );
			NearOVec( OMat.RotZ( MathF.PI / 2f ).ApplyDir( y ), new OVec( -1, 0, 0 ), 1e-6f, "RotZ(90) Y" );
			NearOVec( OMat.RotX( MathF.PI ).ApplyDir( y ), new OVec( 0, -1, 0 ), 1e-6f, "RotX(180) Y" );
			NearOVec( OMat.RotX( MathF.PI ).ApplyDir( z ), new OVec( 0, 0, -1 ), 1e-6f, "RotX(180) Z" );
			NearOVec( OMat.RotY( MathF.PI / 2f ).ApplyDir( z ), x, 1e-6f, "RotY(90) Z" );
			NearOVec( OMat.RotY( MathF.PI / 2f ).ApplyDir( x ), new OVec( 0, 0, -1 ), 1e-6f, "RotY(90) X" );
		} );

		Check( "oracle-inverse-hand-literal", () =>
		{
			// Rotation + translation + uniform scale 2: det must be 8.
			var m = OMat.Compose( OMat.Trans( 3, -2, 5 ),
				OMat.Compose( OMat.RotZ( 0.7f ), new OMat( 2, 0, 0, 0, 2, 0, 0, 0, 2, OVec.Zero ) ) );
			Near( m.Determinant, 8f, 1e-5f, "det" );
			var inv = m.Inverse();
			NearOMat( OMat.Compose( m, inv ), OMat.Identity, 1e-5f, "M * M^-1" );
			NearOMat( OMat.Compose( inv, m ), OMat.Identity, 1e-5f, "M^-1 * M" );
		} );

		Check( "oracle-compose-order-applies-right-first", () =>
		{
			// Compose(a, b) applies b first, then a: RotZ(90) then Translate(1,0,0)
			// moves (1,0,0) to (0,1,0) and then to (1,1,0).
			var m = OMat.Compose( OMat.Trans( 1, 0, 0 ), OMat.RotZ( MathF.PI / 2f ) );
			NearOVec( m.ApplyPoint( new OVec( 1, 0, 0 ) ), new OVec( 1, 1, 0 ), 1e-6f, "order" );
			NearOVec( m.ApplyDir( new OVec( 1, 0, 0 ) ), new OVec( 0, 1, 0 ), 1e-6f, "order dir" );
		} );

		// ---- Fixed solve vs oracle ----

		Check( "fixed-identity-frames", () =>
		{
			var (doc, def) = NewDoc();
			var ta = Xform.Translate( new Vec3( 3, 1, 0 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Translate( new Vec3( 9, 9, 9 ) ) );
			Fixed( doc, a, b, Xform.Identity, Xform.Identity );
			ExpectSolved( AssemblyFixedJointSolver.Solve( doc ), OracleFixedSolve( doc ), PosTol, "identity-frames" );
		} );

		Check( "fixed-rotated-frames", () =>
		{
			var (doc, def) = NewDoc();
			var ta = Xform.Translate( new Vec3( 1, 2, 3 ) ) * Xform.Rotate( new Vec3( 1, 2, 3 ), 0.6f );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Identity );
			var fa = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f );
			Fixed( doc, a, b, fa, Xform.Identity );
			// Hand literal: solved B is exactly Ta * Fa.
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			var expected = OMat.Compose( OMat.FromXform( ta ), OMat.FromXform( fa ) );
			Require( result.TryGetSolvedTransform( b, out var actual ), "no solved B" );
			NearOMat( OMat.FromXform( actual ), expected, PosTol, "rotated-frames.B" );
			ExpectSolved( result, OracleFixedSolve( doc ), PosTol, "rotated-frames" );
		} );

		Check( "fixed-offset-frames", () =>
		{
			var (doc, def) = NewDoc();
			var ta = Xform.Translate( new Vec3( 1, 0, 0 ) );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Translate( new Vec3( 0, 0, 7 ) ) );
			Fixed( doc, a, b, Xform.Translate( new Vec3( 1, 0, 0 ) ), Xform.Translate( new Vec3( 0, 2, 0 ) ) );
			// Hand literal: Tb = Ta * Trans(1,0,0) * Trans(0,2,0)^-1 = Trans(2,-2,0).
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.TryGetSolvedTransform( b, out var actual ), "no solved B" );
			NearXform( actual, Xform.Translate( new Vec3( 2, -2, 0 ) ), PosTol, "offset-frames.B" );
			ExpectSolved( result, OracleFixedSolve( doc ), PosTol, "offset-frames" );
		} );

		Check( "fixed-chain-three-occurrences", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Rigid( rng ) );
			var b = Occ( doc, def, "B", Rigid( rng ) );
			var c = Occ( doc, def, "C", Rigid( rng ) );
			Fixed( doc, a, b, Rigid( rng ), Rigid( rng ) );
			Fixed( doc, b, c, Rigid( rng ), Rigid( rng ) );
			ExpectSolved( AssemblyFixedJointSolver.Solve( doc ), OracleFixedSolve( doc ), PosTol, "chain-three" );
		} );

		Check( "fixed-grounded-root-beats-id-order", () =>
		{
			// Deterministic 32-char ids: a < b < c by ordinal, but c is grounded,
			// so c is the root even though it sorts last.
			var (doc, def) = NewDoc();
			var rng = new Random( RandomSeed );
			var a = Occ( doc, def, "A", Rigid( rng ), "22222222222222222222222222222222" );
			var b = Occ( doc, def, "B", Rigid( rng ), "33333333333333333333333333333333" );
			var c = Occ( doc, def, "C", Rigid( rng ), "44444444444444444444444444444444" );
			Fixed( doc, a, b, Rigid( rng ), Rigid( rng ), "55555555555555555555555555555555" );
			Fixed( doc, b, c, Rigid( rng ), Rigid( rng ), "66666666666666666666666666666666" );
			Require( AssemblyGrounding.SetGrounded( doc, c, true ), "ground c" );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.Components.Count == 1, $"expected 1 component, got {result.Components.Count}" );
			Require( result.Components[0].RootOccurrenceId == c, $"root should be grounded c, got {result.Components[0].RootOccurrenceId}" );
			Require( result.Components[0].RootIsGrounded, "root should be grounded" );
			ExpectSolved( result, OracleFixedSolve( doc ), PosTol, "grounded-root" );
		} );

		Check( "fixed-random-200-seeded", () =>
		{
			var rng = new Random( RandomSeed );
			for ( var i = 0; i < 200; i++ )
			{
				var (doc, def) = NewDoc();
				var a = Occ( doc, def, "A", Rigid( rng ) );
				var b = Occ( doc, def, "B", Rigid( rng ) );
				Fixed( doc, a, b, Rigid( rng ), Rigid( rng ) );
				var result = AssemblyFixedJointSolver.Solve( doc );
				ExpectSolved( result, OracleFixedSolve( doc ), PosTol, $"random-200[{i}]" );
			}
		} );

		// ---- Hinge vs oracle ----
		//
		// TrySetHingeAngle keeps A fixed and rebuilds B as
		//   worldB' = Translate(axis * along) * worldA * RotZ(target),
		//   placementB' = worldB' * Fb^-1,
		// where axis is worldA +Z and along is the current along-axis offset
		// between the two world frames. The oracle below is that same
		// construction with independent matrix code.

		Check( "hinge-set-matches-oracle-hand-case", () =>
		{
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			// B at 30 degrees about Z, 2 in along the axis.
			var tb = Xform.Translate( new Vec3( 0, 0, 2 ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 6f );
			var b = Occ( doc, def, "B", tb );
			var joint = Hinge( doc, a, b, Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.TryGetHingeAngle( doc, joint, out var current, out _ ), "measure current" );
			Near( current, MathF.PI / 6f, AngTol, "current angle" );
			var target = 75f * MathF.PI / 180f;
			Require( AssemblyHinge.TrySetHingeAngle( doc, joint, target, id => AssemblyGrounding.IsGrounded( doc, id ), out var why ),
				$"set hinge: {why}" );
			var worldA = OMat.FromXform( Xform.Identity );
			var expectedWorldB = OMat.Compose( OMat.Trans( 0, 0, 2 ), OMat.Compose( worldA, OMat.RotZ( target ) ) );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occB != null, "B gone" );
			NearOMat( OMat.FromXform( occB.Transform.ToXform() ), expectedWorldB, PosTol, "hinge-hand.B" );
			Require( AssemblyHinge.TryGetHingeAngle( doc, joint, out var after, out _ ), "measure after" );
			Near( after, target, AngTol, "angle after set" );
			// A never moves.
			var occA = doc.Occurrences.Find( o => o.Id == a );
			Require( occA != null, "A gone" );
			NearXform( occA.Transform.ToXform(), Xform.Identity, 1e-6f, "hinge-hand.A unchanged" );
		} );

		Check( "hinge-random-60-seeded", () =>
		{
			var rng = new Random( RandomSeed );
			for ( var i = 0; i < 60; i++ )
			{
				var (doc, def) = NewDoc();
				var ta = Rigid( rng );
				var fa = Rigid( rng );
				var fb = Rigid( rng );
				var worldA = ta * fa;
				var axis = worldA.Z;
				var cur = (float)( rng.NextDouble() * 4.0 - 2.0 );
				var along = (float)( rng.NextDouble() * 6.0 - 3.0 );
				var worldB = Xform.Translate( axis * along ) * worldA * Xform.Rotate( new Vec3( 0, 0, 1 ), cur );
				var tb = worldB * fb.Inverse;
				var a = Occ( doc, def, "A", ta );
				var b = Occ( doc, def, "B", tb );
				var joint = Hinge( doc, a, b, fa, fb );
				Require( AssemblyHinge.TryGetHingeAngle( doc, joint, out var measured, out var measureWhy ),
					$"random-hinge[{i}] measure: {measureWhy}" );
				Near( AssemblyHinge.WrapAngle( measured - cur ), 0f, AngTol, $"random-hinge[{i}] current" );
				var target = cur + 0.7f;
				Require( AssemblyHinge.TrySetHingeAngle( doc, joint, target, id => AssemblyGrounding.IsGrounded( doc, id ), out var why ),
					$"random-hinge[{i}] set: {why}" );
				var oWorldA = OMat.FromXform( worldA );
				var oAxis = oWorldA.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
				var oAlong = OVec.Dot( OMat.FromXform( worldB ).O - oWorldA.O, oAxis );
				var expectedWorldB = OMat.Compose( OMat.Trans( oAxis * oAlong ),
					OMat.Compose( oWorldA, OMat.RotZ( target ) ) );
				var expected = OMat.Compose( expectedWorldB, OMat.FromXform( fb ).Inverse() );
				var occB = doc.Occurrences.Find( o => o.Id == b );
				Require( occB != null, $"random-hinge[{i}] B gone" );
				NearOMat( OMat.FromXform( occB.Transform.ToXform() ), expected, PosTol, $"random-hinge[{i}].B" );
				Require( AssemblyHinge.TryGetHingeAngle( doc, joint, out var after, out _ ), $"random-hinge[{i}] re-measure" );
				Near( AssemblyHinge.WrapAngle( after - target ), 0f, AngTol, $"random-hinge[{i}] angle after" );
			}
		} );

		Check( "hinge-preserves-along-axis-offset", () =>
		{
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var tb = Xform.Translate( new Vec3( 0, 0, 2.5f ) ) * Xform.Rotate( new Vec3( 0, 0, 1 ), 0.3f );
			var b = Occ( doc, def, "B", tb );
			var joint = Hinge( doc, a, b, Xform.Identity, Xform.Identity );
			Require( AssemblyHinge.TrySetHingeAngle( doc, joint, 1.2f, id => AssemblyGrounding.IsGrounded( doc, id ), out var why ),
				$"set hinge: {why}" );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occB != null, "B gone" );
			var worldB = occB.Transform.ToXform();
			Near( worldB.Origin.z, 2.5f, PosTol, "along-axis offset preserved" );
			Near( worldB.Origin.x, 0f, PosTol, "no off-axis x drift" );
			Near( worldB.Origin.y, 0f, PosTol, "no off-axis y drift" );
		} );

		// ---- Slider vs oracle ----

		Check( "slider-solve-matches-oracle", () =>
		{
			var (doc, def) = NewDoc();
			var ta = Xform.Translate( new Vec3( 1, 2, 3 ) ) * Xform.Rotate( new Vec3( 0, 1, 0 ), 0.4f );
			var fa = Xform.Rotate( new Vec3( 1, 0, 0 ), 0.9f );
			var fb = Xform.Rotate( new Vec3( 0, 0, 1 ), -0.5f ) * Xform.Translate( new Vec3( 0, 1, 0 ) );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Translate( new Vec3( 7, 7, 7 ) ) );
			var joint = Slider( doc, a, b, fa, fb );
			var occA = doc.Occurrences.Find( o => o.Id == a );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occA != null && occB != null, "occurrences gone" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, 2.5f, out var solvedB, out var error, out var clamped ),
				$"solve: {error}" );
			Require( !clamped, "unlimited travel must not clamp" );
			var expected = OMat.Compose( OMat.Compose( OMat.FromXform( ta ), OMat.FromXform( fa ) ),
				OMat.Compose( OMat.Trans( 0, 0, 2.5f ), OMat.FromXform( fb ).Inverse() ) );
			NearOMat( OMat.FromXform( solvedB ), expected, PosTol, "slider.B" );
			// TrySolve is pure: B's stored transform is untouched.
			NearXform( occB.Transform.ToXform(), Xform.Translate( new Vec3( 7, 7, 7 ) ), 1e-6f, "slider.B stored unchanged" );
			// Measured travel reads back as 2.5 in.
			Require( AssemblySlider.TryMeasure( joint, occA, new AssemblyOccurrence
			{
				Id = b, DefinitionId = def, Name = "B",
				Transform = AssemblyTransform.FromXform( solvedB ),
			}, out var travel, out var offAxis, out var measureError ), $"measure: {measureError}" );
			Near( travel, 2.5f, PosTol, "measured travel" );
			Near( offAxis, 0f, PosTol, "measured off-axis" );
		} );

		Check( "slider-random-60-seeded", () =>
		{
			var rng = new Random( RandomSeed );
			for ( var i = 0; i < 60; i++ )
			{
				var (doc, def) = NewDoc();
				var ta = Rigid( rng );
				var fa = Rigid( rng );
				var fb = Rigid( rng );
				var a = Occ( doc, def, "A", ta );
				var b = Occ( doc, def, "B", Rigid( rng ) );
				var joint = Slider( doc, a, b, fa, fb );
				var occA = doc.Occurrences.Find( o => o.Id == a );
				var occB = doc.Occurrences.Find( o => o.Id == b );
				Require( occA != null && occB != null, $"random-slider[{i}] occurrences gone" );
				var d = (float)( rng.NextDouble() * 10.0 - 5.0 );
				Require( AssemblySlider.TrySolve( joint, occA, occB, d, out var solvedB, out var error, out var clamped ),
					$"random-slider[{i}] solve: {error}" );
				Require( !clamped, $"random-slider[{i}] unlimited travel must not clamp" );
				var worldA = OMat.Compose( OMat.FromXform( ta ), OMat.FromXform( fa ) );
				var axisLen = worldA.ApplyDir( new OVec( 0, 0, 1 ) ).Length;
				var expected = OMat.Compose( worldA,
					OMat.Compose( OMat.Trans( 0, 0, d / axisLen ), OMat.FromXform( fb ).Inverse() ) );
				NearOMat( OMat.FromXform( solvedB ), expected, PosTol, $"random-slider[{i}].B" );
			}
		} );

		Check( "slider-scaled-anchor-travel-is-world-inches", () =>
		{
			// Uniform 2x scale on the anchor: the axis length divides out, so a
			// 3 in travel is 3 world inches, not 3 local units (1.5 along the
			// scaled axis). Solved B still carries det 8 from the anchor.
			var (doc, def) = NewDoc();
			var ta = Xform.Scale( new Vec3( 2, 2, 2 ) ) * Xform.Translate( new Vec3( 1, 0, 0 ) );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Identity );
			var joint = Slider( doc, a, b, Xform.Identity, Xform.Identity );
			var occA = doc.Occurrences.Find( o => o.Id == a );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occA != null && occB != null, "occurrences gone" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, 3f, out var solvedB, out var error, out _ ),
				$"solve: {error}" );
			Near( solvedB.Determinant, 8f, 1e-4f, "det 8 inherited" );
			var worldA = ta;
			var worldB = solvedB;
			var axis = worldA.Z.Normal;
			var alongWorld = Vec3.Dot( worldB.Origin - worldA.Origin, axis );
			Near( alongWorld, 3f, PosTol, "world travel is 3 in" );
			var expected = OMat.Compose( OMat.FromXform( ta ),
				OMat.Compose( OMat.Trans( 0, 0, 1.5f ), OMat.Identity ) );
			NearOMat( OMat.FromXform( solvedB ), expected, PosTol, "scaled-anchor.B" );
		} );

		Check( "slider-clamp-vs-set-reject", () =>
		{
			// The asymmetry is the contract: TrySolve CLAMPS out-of-limit
			// travel and reports it; TrySetPosition REJECTS it and keeps the
			// last valid stored position.
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Identity );
			var joint = Slider( doc, a, b, Xform.Identity, Xform.Identity, -2f, 4f );
			Require( AssemblySlider.TrySetPosition( joint, 1f, out var setError ), $"set 1: {setError}" );
			Near( AssemblySlider.GetPosition( joint ), 1f, 1e-6f, "stored 1" );
			var occA = doc.Occurrences.Find( o => o.Id == a );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occA != null && occB != null, "occurrences gone" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, 6f, out var solvedHigh, out _, out var clampedHigh ),
				"solve 6 should succeed by clamping" );
			Require( clampedHigh, "6 in must report clamped" );
			Near( solvedHigh.Origin.z, 4f, PosTol, "clamped to max 4" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, -3f, out var solvedLow, out _, out var clampedLow ),
				"solve -3 should succeed by clamping" );
			Require( clampedLow, "-3 in must report clamped" );
			Near( solvedLow.Origin.z, -2f, PosTol, "clamped to min -2" );
			Require( !AssemblySlider.TrySetPosition( joint, 6f, out _ ), "set 6 must be rejected" );
			Near( AssemblySlider.GetPosition( joint ), 1f, 1e-6f, "last valid 1 survives rejection" );
			Require( !AssemblySlider.TrySetPosition( joint, -3f, out _ ), "set -3 must be rejected" );
			Near( AssemblySlider.GetPosition( joint ), 1f, 1e-6f, "last valid 1 still survives" );
		} );

		// ---- Face / axis alignment identities ----
		//
		// AS07 had not landed when this file was written, so the identities
		// are proven through the landed fixed solver: the joint frames are
		// constructed so the fixed equation Tb = Ta * Fa * Fb'^-1 IS the
		// alignment equation, and the geometric meaning (opposed / same
		// normals, coaxial axes, exact gap) is asserted on the solved frames.

		Check( "align-faces-opposed", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var ta = Rigid( rng );
			var fa = Rigid( rng );
			var fb = Rigid( rng );
			const float off = 1.5f;
			// Fb' = Fb * RotX(pi)^-1 * Trans(0,0,-off), so
			// Tb = Ta*Fa*Fb'^-1 = Ta*Fa*Trans(off)*RotX(pi)*Fb^-1.
			var fbPrime = fb * Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI ).Inverse
				* Xform.Translate( new Vec3( 0, 0, -off ) );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Rigid( rng ) );
			Fixed( doc, a, b, fa, fbPrime );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.TryGetSolvedTransform( b, out var solvedB ), "no solved B" );
			var worldA = OMat.Compose( OMat.FromXform( ta ), OMat.FromXform( fa ) );
			var worldB = OMat.Compose( OMat.FromXform( solvedB ), OMat.FromXform( fb ) );
			var nA = worldA.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
			var nB = worldB.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
			Near( OVec.Dot( nA, nB ), -1f, PosTol, "opposed normals" );
			Near( OVec.Dot( worldB.O - worldA.O, nA ), off, PosTol, "gap along A normal" );
			var expected = OMat.Compose( OMat.Compose( OMat.Compose( worldA, OMat.Trans( 0, 0, off ) ),
				OMat.RotX( MathF.PI ) ), OMat.FromXform( fb ).Inverse() );
			NearOMat( OMat.FromXform( solvedB ), expected, PosTol, "opposed.B" );
		} );

		Check( "align-faces-same-normal", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var ta = Rigid( rng );
			var fa = Rigid( rng );
			var fb = Rigid( rng );
			const float off = 0.75f;
			var fbPrime = fb * Xform.Translate( new Vec3( 0, 0, -off ) );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Rigid( rng ) );
			Fixed( doc, a, b, fa, fbPrime );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.TryGetSolvedTransform( b, out var solvedB ), "no solved B" );
			var worldA = OMat.Compose( OMat.FromXform( ta ), OMat.FromXform( fa ) );
			var worldB = OMat.Compose( OMat.FromXform( solvedB ), OMat.FromXform( fb ) );
			var nA = worldA.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
			var nB = worldB.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
			Near( OVec.Dot( nA, nB ), 1f, PosTol, "same normals" );
			Near( OVec.Dot( worldB.O - worldA.O, nA ), off, PosTol, "gap along A normal" );
		} );

		Check( "align-axis-coaxial-offset", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var ta = Rigid( rng );
			var fa = Rigid( rng );
			var fb = Rigid( rng );
			const float off = 2.25f;
			var fbPrime = fb * Xform.Translate( new Vec3( 0, 0, -off ) );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Rigid( rng ) );
			Fixed( doc, a, b, fa, fbPrime );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.TryGetSolvedTransform( b, out var solvedB ), "no solved B" );
			var worldA = OMat.Compose( OMat.FromXform( ta ), OMat.FromXform( fa ) );
			var worldB = OMat.Compose( OMat.FromXform( solvedB ), OMat.FromXform( fb ) );
			var axisA = worldA.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
			var axisB = worldB.ApplyDir( new OVec( 0, 0, 1 ) ).Normalized;
			Near( OVec.Dot( axisA, axisB ), 1f, PosTol, "coaxial axes" );
			var delta = worldB.O - worldA.O;
			Near( OVec.Dot( delta, axisA ), off, PosTol, "axial offset" );
			Near( ( delta - axisA * off ).Length, 0f, PosTol, "no off-axis drift" );
			var expected = OMat.Compose( OMat.Compose( worldA, OMat.Trans( 0, 0, off ) ),
				OMat.FromXform( fb ).Inverse() );
			NearOMat( OMat.FromXform( solvedB ), expected, PosTol, "coaxial.B" );
		} );

		// ---- JSON round-trip ----

		Check( "json-round-trip-byte-identical-20", () =>
		{
			var rng = new Random( RandomSeed );
			for ( var i = 0; i < 20; i++ )
			{
				var (doc, def) = NewDoc();
				var count = 1 + rng.Next( 4 );
				var ids = new List<string>();
				for ( var k = 0; k < count; k++ )
					ids.Add( Occ( doc, def, "P" + k, Rigid( rng ) ) );
				var joints = rng.Next( 4 );
				for ( var k = 0; k < joints; k++ )
				{
					var x = ids[rng.Next( ids.Count )];
					var y = ids[rng.Next( ids.Count )];
					if ( x == y ) continue;
					switch ( rng.Next( 3 ) )
					{
						case 0:
							Fixed( doc, x, y, Rigid( rng ), Rigid( rng ) );
							break;
						case 1:
							Hinge( doc, x, y, Rigid( rng ), Rigid( rng ), -1f, 1f );
							break;
						default:
							var joint = Slider( doc, x, y, Rigid( rng ), Rigid( rng ), -5f, 5f );
							AssemblySlider.TrySetPosition( joint, (float)( rng.NextDouble() * 4.0 - 2.0 ), out _ );
							break;
					}
				}
				if ( rng.Next( 2 ) == 0 )
					AssemblyGrounding.SetGrounded( doc, ids[rng.Next( ids.Count )], true );
				var json = doc.ToJson();
				var back = AssemblyDocument.FromJson( json );
				Require( back.ToJson() == json, $"round-trip[{i}] not byte-identical" );
				var issues = back.Validate();
				Require( issues.Count == 0,
					$"round-trip[{i}] reloaded doc has issues: {string.Join( "; ", issues.Select( s => s.Code + " " + s.Message ) )}" );
			}
		} );

		// ---- Closed loops ----

		Check( "closed-loop-consistent-solves", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Identity );
			var c = Occ( doc, def, "C", Xform.Identity );
			var faAb = Rigid( rng );
			var fbAb = Rigid( rng );
			var faBc = Rigid( rng );
			var fbBc = Rigid( rng );
			var rab = OMat.Compose( OMat.FromXform( faAb ), OMat.FromXform( fbAb ).Inverse() );
			var rbc = OMat.Compose( OMat.FromXform( faBc ), OMat.FromXform( fbBc ).Inverse() );
			// Close the loop: Rca must be (Rab * Rbc)^-1, so the product
			// around the cycle is the identity.
			var faCa = OMat.Compose( rab, rbc ).Inverse();
			Fixed( doc, a, b, faAb, fbAb );
			Fixed( doc, b, c, faBc, fbBc );
			Fixed( doc, c, a, FromOMat( faCa ), Xform.Identity );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "consistent loop must solve" );
			Require( result.Components.Count == 1, "one component" );
			Require( result.Components[0].OccurrenceIds.Count == 3, "three occurrences in the component" );
			// The loop is present even though it is consistent: 3 joints over
			// 3 occurrences is one redundant constraint.
			Require( doc.Joints.Count > doc.Occurrences.Count - 1,
				"loop must be detectable: joints exceed occurrences - 1" );
			var residual = OMat.Compose( OMat.Compose( rab, rbc ), faCa );
			NearOMat( residual, OMat.Identity, PosTol, "loop residual" );
		} );

		Check( "closed-loop-contradictory-fails", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Identity );
			var c = Occ( doc, def, "C", Xform.Identity );
			var faAb = Rigid( rng );
			var fbAb = Rigid( rng );
			var faBc = Rigid( rng );
			var fbBc = Rigid( rng );
			var rab = OMat.Compose( OMat.FromXform( faAb ), OMat.FromXform( fbAb ).Inverse() );
			var rbc = OMat.Compose( OMat.FromXform( faBc ), OMat.FromXform( fbBc ).Inverse() );
			// 5 degrees off closing the loop: the two paths to one occurrence
			// now disagree by far more than the 1e-4 solver tolerance.
			var faCa = OMat.Compose( OMat.RotZ( 5f * MathF.PI / 180f ), OMat.Compose( rab, rbc ).Inverse() );
			Fixed( doc, a, b, faAb, fbAb );
			Fixed( doc, b, c, faBc, fbBc );
			Fixed( doc, c, a, FromOMat( faCa ), Xform.Identity );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "contradictory loop must fail" );
			Require( HasError( result, "contradictory-fixed-joint" ), "must report contradictory-fixed-joint" );
		} );

		// ---- No drift ----

		Check( "no-drift-300-repeated-solves", () =>
		{
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Rigid( rng ) );
			var b = Occ( doc, def, "B", Rigid( rng ) );
			var c = Occ( doc, def, "C", Rigid( rng ) );
			Fixed( doc, a, b, Rigid( rng ), Rigid( rng ) );
			Fixed( doc, b, c, Rigid( rng ), Rigid( rng ) );
			var first = AssemblyFixedJointSolver.Solve( doc );
			Require( first.Success, "first solve failed" );
			AssemblySolveResult last = first;
			for ( var i = 0; i < 300; i++ )
			{
				last = AssemblyFixedJointSolver.Solve( doc );
				Require( last.Success, $"solve {i} failed" );
			}
			foreach ( var pair in first.SolvedTransforms )
			{
				Require( last.TryGetSolvedTransform( pair.Key, out var actual ), $"no solved {pair.Key}" );
				NearXform( actual, pair.Value, 1e-6f, $"drift after 300 solves.{pair.Key}" );
			}
		} );

		Check( "no-drift-100-write-back-rounds", () =>
		{
			// Solve, write the solved transforms back into the occurrences,
			// solve again: the solver is a pure function of the document, so
			// the fixed point must not wander.
			var rng = new Random( RandomSeed );
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Rigid( rng ) );
			var b = Occ( doc, def, "B", Rigid( rng ) );
			var c = Occ( doc, def, "C", Rigid( rng ) );
			Fixed( doc, a, b, Rigid( rng ), Rigid( rng ) );
			Fixed( doc, b, c, Rigid( rng ), Rigid( rng ) );
			Require( AssemblyGrounding.SetGrounded( doc, a, true ), "ground a" );
			var first = AssemblyFixedJointSolver.Solve( doc );
			Require( first.Success, "first solve failed" );
			for ( var round = 0; round < 100; round++ )
			{
				var result = AssemblyFixedJointSolver.Solve( doc );
				Require( result.Success, $"round {round} failed" );
				foreach ( var occ in doc.Occurrences )
					occ.Transform = AssemblyTransform.FromXform( result.SolvedTransforms[occ.Id] );
			}
			var final = AssemblyFixedJointSolver.Solve( doc );
			Require( final.Success, "final solve failed" );
			foreach ( var pair in first.SolvedTransforms )
			{
				Require( final.TryGetSolvedTransform( pair.Key, out var actual ), $"no solved {pair.Key}" );
				NearXform( actual, pair.Value, 1e-6f, $"drift after 100 write-back rounds.{pair.Key}" );
			}
		} );

		// ---- Units traps: conversions happen at the caller, never silently ----

		Check( "units-millimetres-25-4-is-one-inch", () =>
		{
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Identity );
			var joint = Slider( doc, a, b, Xform.Identity, Xform.Identity );
			var occA = doc.Occurrences.Find( o => o.Id == a );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occA != null && occB != null, "occurrences gone" );
			var inches = 25.4f / 25.4f;
			Require( AssemblySlider.TrySolve( joint, occA, occB, inches, out var solvedMm, out var error, out _ ),
				$"solve mm-derived: {error}" );
			Near( solvedMm.Origin.z, 1f, PosTol, "25.4 mm converts to 1 in" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, 1f, out var solvedIn, out _, out _ ), "solve 1 in" );
			NearXform( solvedMm, solvedIn, 1e-6f, "mm-derived equals inch" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, 25.4f, out var solvedNaive, out _, out _ ),
				"solve naive 25.4" );
			Near( solvedNaive.Origin.z, 25.4f, PosTol, "25.4 read as inches is 25.4 in" );
			Near( MathF.Abs( solvedNaive.Origin.z - solvedMm.Origin.z ), 24.4f, PosTol, "naive reading is 24.4 in too far" );
		} );

		Check( "units-centimetres-2-54-is-one-inch", () =>
		{
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Identity );
			var joint = Slider( doc, a, b, Xform.Identity, Xform.Identity );
			var occA = doc.Occurrences.Find( o => o.Id == a );
			var occB = doc.Occurrences.Find( o => o.Id == b );
			Require( occA != null && occB != null, "occurrences gone" );
			var inches = 2.54f / 2.54f;
			Require( AssemblySlider.TrySolve( joint, occA, occB, inches, out var solvedCm, out var error, out _ ),
				$"solve cm-derived: {error}" );
			Near( solvedCm.Origin.z, 1f, PosTol, "2.54 cm converts to 1 in" );
			Require( AssemblySlider.TrySolve( joint, occA, occB, 2.54f, out var solvedNaive, out _, out _ ),
				"solve naive 2.54" );
			Near( MathF.Abs( solvedNaive.Origin.z - solvedCm.Origin.z ), 1.54f, PosTol, "naive reading is 1.54 in too far" );
		} );

		// ---- Scale / mirror inheritance ----

		Check( "scale-det-8-inherited", () =>
		{
			var (doc, def) = NewDoc();
			var ta = Xform.Scale( new Vec3( 2, 2, 2 ) ) * Xform.Translate( new Vec3( 1, 0, 0 ) );
			Near( ta.Determinant, 8f, 1e-6f, "parent det 8" );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Identity );
			Fixed( doc, a, b, Xform.Identity, Xform.Identity );
			Require( AssemblyGrounding.SetGrounded( doc, a, true ), "ground a" );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.TryGetSolvedTransform( b, out var solvedB ), "no solved B" );
			Near( solvedB.Determinant, 8f, 1e-4f, "child inherits det 8" );
		} );

		Check( "mirror-det-minus-1-inherited", () =>
		{
			var (doc, def) = NewDoc();
			var ta = Xform.Mirror( Vec3.Zero, new Vec3( 1, 0, 0 ) );
			Near( ta.Determinant, -1f, 1e-6f, "parent det -1" );
			var a = Occ( doc, def, "A", ta );
			var b = Occ( doc, def, "B", Xform.Identity );
			Fixed( doc, a, b, Xform.Identity, Xform.Identity );
			Require( AssemblyGrounding.SetGrounded( doc, a, true ), "ground a" );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( result.Success, "solve failed" );
			Require( result.TryGetSolvedTransform( b, out var solvedB ), "no solved B" );
			Near( solvedB.Determinant, -1f, 1e-4f, "child inherits det -1" );
			Require( solvedB.FlipsWinding, "mirrored child flips winding" );
		} );

		// ---- Invalid input is refused, never solved ----

		Check( "singular-transform-refused", () =>
		{
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Scale( new Vec3( 0, 1, 1 ) ) );
			Fixed( doc, a, b, Xform.Identity, Xform.Identity );
			Require( HasIssue( doc.Validate(), "invalid-transform" ), "zero scale flagged invalid-transform" );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "singular transform must fail the solve" );
			Require( HasError( result, "invalid-transform" ), "must report invalid-transform" );
		} );

		Check( "nan-transform-and-frame-refused", () =>
		{
			// NaN occurrence transform.
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var bad = new Xform( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), new Vec3( float.NaN, 0, 0 ) );
			var b = Occ( doc, def, "B", bad );
			Fixed( doc, a, b, Xform.Identity, Xform.Identity );
			Require( HasIssue( doc.Validate(), "invalid-transform" ), "NaN transform flagged invalid-transform" );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "NaN transform must fail the solve" );
			Require( HasError( result, "invalid-transform" ), "must report invalid-transform" );

			// NaN connector frame (occurrences are fine here).
			var (doc2, def2) = NewDoc();
			var a2 = Occ( doc2, def2, "A", Xform.Identity );
			var b2 = Occ( doc2, def2, "B", Xform.Identity );
			var nanFrame = new Xform( new Vec3( float.NaN, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), Vec3.Zero );
			var joint = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a2, OccurrenceB = b2,
				FrameA = AssemblyTransform.FromXform( nanFrame ), FrameB = new AssemblyTransform(),
			};
			doc2.Joints.Add( joint );
			Require( HasIssue( doc2.Validate(), "invalid-connector-frame" ), "NaN frame flagged invalid-connector-frame" );
			var result2 = AssemblyFixedJointSolver.Solve( doc2 );
			Require( !result2.Success, "NaN frame must fail the solve" );
			Require( HasError( result2, "invalid-connector-frame" ), "must report invalid-connector-frame" );
		} );

		Check( "non-rigid-frame-refused", () =>
		{
			var (doc, def) = NewDoc();
			var a = Occ( doc, def, "A", Xform.Identity );
			var b = Occ( doc, def, "B", Xform.Identity );
			// A scaled connector frame is not a rigid frame: the solver must
			// refuse it, not silently solve through the scale.
			Fixed( doc, a, b, Xform.Scale( new Vec3( 2, 1, 1 ) ), Xform.Identity );
			Require( HasIssue( doc.Validate(), "invalid-connector-frame" ), "scaled frame flagged invalid-connector-frame" );
			var result = AssemblyFixedJointSolver.Solve( doc );
			Require( !result.Success, "non-rigid frame must fail the solve" );
			Require( HasError( result, "invalid-connector-frame" ), "must report invalid-connector-frame" );
		} );

		// ---- Degrees of freedom: known values on small fixtures ----

		Check( "free-degrees-of-freedom-known-values", () =>
		{
			var (doc, def) = NewDoc();
			var g = Occ( doc, def, "G", Xform.Identity );
			Require( AssemblyGrounding.SetGrounded( doc, g, true ), "ground g" );
			var x = Occ( doc, def, "X", Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			var y = Occ( doc, def, "Y", Xform.Translate( new Vec3( 9, 0, 0 ) ) );
			var z = Occ( doc, def, "Z", Xform.Translate( new Vec3( 2, 2, 2 ) ) );
			Fixed( doc, g, x, Xform.Identity, Xform.Identity );
			Hinge( doc, g, y, Xform.Identity, Xform.Identity );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, g ) == 0, "grounded is 0" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, x ) == 0, "fixed to ground is 0" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, y ) == 1, "hinge to ground is 1" );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, z ) == 6, "floating is 6" );
			// A parallel fixed path to ground beats the hinge path: the cost
			// is minimised over ALL paths, not the first one found.
			Hinge( doc, g, x, Xform.Identity, Xform.Identity );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, x ) == 0, "parallel fixed path still 0" );
			var w = Occ( doc, def, "W", Xform.Translate( new Vec3( 1, 1, 1 ) ) );
			Hinge( doc, y, w, Xform.Identity, Xform.Identity );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, w ) == 1, "hinge chain to ground is 1" );
			var u = Occ( doc, def, "U", Xform.Translate( new Vec3( 3, 3, 3 ) ) );
			doc.Joints.Add( new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "cable", OccurrenceA = g, OccurrenceB = u,
				FrameA = new AssemblyTransform(), FrameB = new AssemblyTransform(),
			} );
			Require( AssemblyFixedJointSolver.FreeDegreesOfFreedom( doc, u ) == 1, "unknown kind costs 1" );
		} );
	}

	// Independent oracle: a row-major 3x3 rotation plus an origin.
	// Deliberately a different representation from kernel Xform (which stores
	// column basis vectors): the composition, rotation constructors and
	// inverse below were written from the matrix definitions, not ported.
	readonly struct OVec
	{
		public readonly float X, Y, Z;
		public OVec( float x, float y, float z ) { X = x; Y = y; Z = z; }
		public static readonly OVec Zero = new( 0, 0, 0 );
		public static OVec operator +( OVec a, OVec b ) => new( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
		public static OVec operator -( OVec a, OVec b ) => new( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
		public static OVec operator *( OVec a, float s ) => new( a.X * s, a.Y * s, a.Z * s );
		public static OVec operator *( float s, OVec a ) => a * s;
		public float LengthSquared => X * X + Y * Y + Z * Z;
		public float Length => MathF.Sqrt( LengthSquared );
		public OVec Normalized => Length > 1e-12f ? this * ( 1f / Length ) : Zero;
		public static float Dot( OVec a, OVec b ) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
		public static OVec Cross( OVec a, OVec b ) => new(
			a.Y * b.Z - a.Z * b.Y,
			a.Z * b.X - a.X * b.Z,
			a.X * b.Y - a.Y * b.X );
		public static OVec FromVec3( Vec3 v ) => new( v.x, v.y, v.z );
	}

	readonly struct OMat
	{
		public readonly float R00, R01, R02, R10, R11, R12, R20, R21, R22;
		public readonly OVec O;
		public OMat( float r00, float r01, float r02, float r10, float r11, float r12,
			float r20, float r21, float r22, OVec o )
		{
			R00 = r00; R01 = r01; R02 = r02;
			R10 = r10; R11 = r11; R12 = r12;
			R20 = r20; R21 = r21; R22 = r22;
			O = o;
		}
		public static readonly OMat Identity = new( 1, 0, 0, 0, 1, 0, 0, 0, 1, OVec.Zero );
		public OVec ApplyPoint( OVec p ) => new(
			R00 * p.X + R01 * p.Y + R02 * p.Z + O.X,
			R10 * p.X + R11 * p.Y + R12 * p.Z + O.Y,
			R20 * p.X + R21 * p.Y + R22 * p.Z + O.Z );
		public OVec ApplyDir( OVec d ) => new(
			R00 * d.X + R01 * d.Y + R02 * d.Z,
			R10 * d.X + R11 * d.Y + R12 * d.Z,
			R20 * d.X + R21 * d.Y + R22 * d.Z );
		/// <summary>Compose(a, b) applies b first, then a: R = a.R * b.R, o = a.R * b.o + a.o.</summary>
		public static OMat Compose( OMat a, OMat b ) => new(
			a.R00 * b.R00 + a.R01 * b.R10 + a.R02 * b.R20,
			a.R00 * b.R01 + a.R01 * b.R11 + a.R02 * b.R21,
			a.R00 * b.R02 + a.R01 * b.R12 + a.R02 * b.R22,
			a.R10 * b.R00 + a.R11 * b.R10 + a.R12 * b.R20,
			a.R10 * b.R01 + a.R11 * b.R11 + a.R12 * b.R21,
			a.R10 * b.R02 + a.R11 * b.R12 + a.R12 * b.R22,
			a.R20 * b.R00 + a.R21 * b.R10 + a.R22 * b.R20,
			a.R20 * b.R01 + a.R21 * b.R11 + a.R22 * b.R21,
			a.R20 * b.R02 + a.R21 * b.R12 + a.R22 * b.R22,
			a.ApplyPoint( b.O ) );
		public static OMat RotX( float t )
		{
			var c = MathF.Cos( t );
			var s = MathF.Sin( t );
			return new OMat( 1, 0, 0, 0, c, -s, 0, s, c, OVec.Zero );
		}
		public static OMat RotY( float t )
		{
			var c = MathF.Cos( t );
			var s = MathF.Sin( t );
			return new OMat( c, 0, s, 0, 1, 0, -s, 0, c, OVec.Zero );
		}
		public static OMat RotZ( float t )
		{
			var c = MathF.Cos( t );
			var s = MathF.Sin( t );
			return new OMat( c, -s, 0, s, c, 0, 0, 0, 1, OVec.Zero );
		}
		public static OMat Trans( OVec t ) => new( 1, 0, 0, 0, 1, 0, 0, 0, 1, t );
		public static OMat Trans( float x, float y, float z ) => Trans( new OVec( x, y, z ) );
		public float Determinant => R00 * ( R11 * R22 - R12 * R21 )
			- R01 * ( R10 * R22 - R12 * R20 )
			+ R02 * ( R10 * R21 - R11 * R20 );
		public OMat Inverse()
		{
			var det = Determinant;
			if ( MathF.Abs( det ) < 1e-12f )
				throw new InvalidOperationException( "Oracle matrix is singular." );
			// Rows of the inverse are the cross products of the rows, over det.
			var r0 = new OVec( R00, R01, R02 );
			var r1 = new OVec( R10, R11, R12 );
			var r2 = new OVec( R20, R21, R22 );
			var i0 = OVec.Cross( r1, r2 ) * ( 1f / det );
			var i1 = OVec.Cross( r2, r0 ) * ( 1f / det );
			var i2 = OVec.Cross( r0, r1 ) * ( 1f / det );
			var invO = new OVec( -OVec.Dot( i0, O ), -OVec.Dot( i1, O ), -OVec.Dot( i2, O ) );
			return new OMat( i0.X, i0.Y, i0.Z, i1.X, i1.Y, i1.Z, i2.X, i2.Y, i2.Z, invO );
		}
		/// <summary>Conversion only, no logic: Xform columns become matrix rows.</summary>
		public static OMat FromXform( Xform t ) => new(
			t.X.x, t.Y.x, t.Z.x,
			t.X.y, t.Y.y, t.Z.y,
			t.X.z, t.Y.z, t.Z.z,
			OVec.FromVec3( t.Origin ) );
	}

	/// <summary>Conversion only, no logic: matrix rows become Xform columns.</summary>
	static Xform FromOMat( OMat m ) => new(
		new Vec3( m.R00, m.R10, m.R20 ),
		new Vec3( m.R01, m.R11, m.R21 ),
		new Vec3( m.R02, m.R12, m.R22 ),
		new Vec3( m.O.X, m.O.Y, m.O.Z ) );
}
