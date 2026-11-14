using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS13: convert mechanical assemblies into editable rigs.
// Mirrors Mobile app/AssemblyToRigChecks.cs (non-UI assertions) in Desktop
// form. A robot arm (hinge + hinge + slider) plans, applies, pins, weights,
// saves/reopens and poses; fixed joints merge; loops and bad joints convert
// as an explicitly described safe subset; sliders follow their stored travel
// (clamped); hinges read their angle from the occurrence transforms; the
// converted rig, re-applied to a fresh studio, drives the same posed mesh as
// the assembly snapshot (production SkinBinder.Deform, not a second skinning
// implementation).
//
// Checks compare solved pivots, bone names, pins, weights and mesh
// coordinates, never only a success flag. Undo/redo is not involved here
// (Apply is a conversion, not an edit command), but records are re-fetched
// by stable Id wherever a history restore could replace them.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyToRigTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-to-rig", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		static bool Close( float a, float b, float tol ) => MathF.Abs( a - b ) <= tol;
		void Near( Vec3 got, Vec3 want, float tol, string what )
		{
			if ( ( got - want ).LengthSquared > tol * tol )
				throw new Exception( $"{what}: got ({got.x:0.###}, {got.y:0.###}, {got.z:0.###}), want ({want.x:0.###}, {want.y:0.###}, {want.z:0.###})" );
		}
		void NearF( float got, float want, float tol, string what )
		{
			if ( !Close( got, want, tol ) ) throw new Exception( $"{what}: got {got:0.###}, want {want:0.###}" );
		}
		static bool NearV( Vec3 a, Vec3 b, float eps = 1e-4f ) => ( a - b ).LengthSquared < eps * eps;

		// Centred box, 8 corners, 12 outward triangles.
		static PolyMesh BoxMesh( float hx, float hy, float hz )
		{
			var mesh = new PolyMesh();
			mesh.AddVertex( new Vec3( -hx, -hy, -hz ) );
			mesh.AddVertex( new Vec3( hx, -hy, -hz ) );
			mesh.AddVertex( new Vec3( hx, hy, -hz ) );
			mesh.AddVertex( new Vec3( -hx, hy, -hz ) );
			mesh.AddVertex( new Vec3( -hx, -hy, hz ) );
			mesh.AddVertex( new Vec3( hx, -hy, hz ) );
			mesh.AddVertex( new Vec3( hx, hy, hz ) );
			mesh.AddVertex( new Vec3( -hx, hy, hz ) );
			mesh.AddFace( new[] { 0, 2, 1 } ); mesh.AddFace( new[] { 0, 3, 2 } ); // bottom -Z
			mesh.AddFace( new[] { 4, 5, 6 } ); mesh.AddFace( new[] { 4, 6, 7 } ); // top +Z
			mesh.AddFace( new[] { 0, 1, 5 } ); mesh.AddFace( new[] { 0, 5, 4 } ); // front -Y
			mesh.AddFace( new[] { 3, 7, 6 } ); mesh.AddFace( new[] { 3, 6, 2 } ); // back +Y
			mesh.AddFace( new[] { 0, 4, 7 } ); mesh.AddFace( new[] { 0, 7, 3 } ); // left -X
			mesh.AddFace( new[] { 1, 2, 6 } ); mesh.AddFace( new[] { 1, 6, 5 } ); // right +X
			return mesh;
		}

		// A rigid connector frame: orthonormal basis with +Z along the given axis.
		static AssemblyTransform FrameAt( Vec3 origin, Vec3 zAxis )
		{
			var z = zAxis * ( 1f / MathF.Sqrt( zAxis.LengthSquared ) );
			var temp = MathF.Abs( z.x ) < 0.9f ? new Vec3( 1f, 0f, 0f ) : new Vec3( 0f, 1f, 0f );
			var x = temp - z * Vec3.Dot( temp, z );
			x *= 1f / MathF.Sqrt( x.LengthSquared );
			var y = Vec3.Cross( z, x );
			return new AssemblyTransform
			{
				X = new[] { x.x, x.y, x.z }, Y = new[] { y.x, y.y, y.z }, Z = new[] { z.x, z.y, z.z },
				Origin = new[] { origin.x, origin.y, origin.z },
			};
		}
		static AssemblyTransform At( Vec3 origin ) => new()
		{
			X = new[] { 1f, 0f, 0f }, Y = new[] { 0f, 1f, 0f }, Z = new[] { 0f, 0f, 1f },
			Origin = new[] { origin.x, origin.y, origin.z },
		};
		static AssemblyOccurrence Occ( string name, string definitionId, Xform transform ) => new()
		{
			Id = AssemblyDocument.NewId(), DefinitionId = definitionId, Name = name,
			Transform = AssemblyTransform.FromXform( transform ),
		};
		static AssemblyJoint Joint( string kind, string a, string b, AssemblyTransform fa, AssemblyTransform fb, float? min = null, float? max = null ) => new()
		{
			Id = AssemblyDocument.NewId(), Kind = kind, OccurrenceA = a, OccurrenceB = b,
			FrameA = fa, FrameB = fb, Minimum = min, Maximum = max,
		};

		// A studio with N direct bodies (box 8x8x8 each); definitions point at
		// them by convention ("studio-body#<bodyId>").
		static (PartStudio Studio, string[] BodyIds, AssemblyDocument Assembly) StudioWithParts( int count )
		{
			var studio = new PartStudio();
			var bodyIds = new string[count];
			var assembly = new AssemblyDocument();
			for ( var i = 0; i < count; i++ )
			{
				var bodyId = "body-" + ( i + 1 );
				bodyIds[i] = bodyId;
				studio.Bodies.Add( new Body( bodyId, "Part " + ( i + 1 ), BoxMesh( 4, 4, 4 ) ) );
				assembly.Definitions.Add( new AssemblyPartDefinition
				{
					Id = AssemblyDocument.NewId(), Name = "Part " + bodyId,
					SourceKind = "studio-body", SourceReference = "studio-body#" + bodyId,
				} );
			}
			return ( studio, bodyIds, assembly );
		}

		// Robot arm fixture: Base -hinge- Upper arm -hinge- Forearm -slider- Gripper.
		static (PartStudio Studio, AssemblyDocument Doc, string BaseId, string UpperId, string ForeId, string GripId, AssemblyJoint Hinge1, AssemblyJoint Slider) RobotFixture()
		{
			var (studio, _, assembly) = StudioWithParts( 4 );
			var defs = assembly.Definitions;
			var baseOcc = Occ( "Base", defs[0].Id, Xform.Identity );
			var upperOcc = Occ( "Upper arm", defs[1].Id, Xform.Translate( new Vec3( 0f, 0f, 2f ) ) );
			var foreOcc = Occ( "Forearm", defs[2].Id, Xform.Translate( new Vec3( 0f, 0f, 4f ) ) );
			var gripOcc = Occ( "Gripper", defs[3].Id, Xform.Translate( new Vec3( 0f, 0f, 6f ) ) );
			assembly.Occurrences.AddRange( new[] { baseOcc, upperOcc, foreOcc, gripOcc } );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var hinge1 = Joint( "hinge", baseOcc.Id, upperOcc.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -1.2f, 1.2f );
			var hinge2 = Joint( "hinge", upperOcc.Id, foreOcc.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -2.0f, 0.5f );
			var slider = Joint( "slider", foreOcc.Id, gripOcc.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), new Vec3( 0f, 0f, 1f ) ), FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ), 0f, 2f );
			assembly.Joints.AddRange( new[] { hinge1, hinge2, slider } );
			return ( studio, assembly, baseOcc.Id, upperOcc.Id, foreOcc.Id, gripOcc.Id, hinge1, slider );
		}

		AssemblySourceResolver ResolverFor( PartStudio studio ) => def =>
		{
			if ( def == null || def.SourceKind != "studio-body" ) return null;
			var body = studio.Bodies.FirstOrDefault( b => b != null && ( "studio-body#" + b.Id ) == def.SourceReference || b != null && b.Id == def.SourceReference );
			if ( body?.Mesh == null ) return null;
			return ( body.Mesh, 0, body.Id );
		};

		// ---- A. Robot arm: hinge + hinge + slider, plan + apply + weights
		//      + snapshot + save/reopen + pose + clamps.
		Check( "robot arm plans successfully with four bones", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Require( plan.Success, "plan must succeed: " + plan.Error );
			Require( plan.Bones.Count == 4, $"expected 4 bones, got {plan.Bones.Count}" );
			Require( plan.RootOccurrenceName == "Base", $"root must be Base, got {plan.RootOccurrenceName}" );
		} );
		Check( "bone names follow occurrence names, sanitized", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Require( plan.Bones.Select( b => b.Name ).SequenceEqual( new[] { "Base", "Upper_arm", "Forearm", "Gripper" } ),
				"bone names: " + string.Join( ",", plan.Bones.Select( b => b.Name ) ) );
		} );
		Check( "bone parenting follows the joint tree", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Require( plan.Bones[1].ParentName == "Base" && plan.Bones[2].ParentName == "Upper_arm" && plan.Bones[3].ParentName == "Forearm",
				"parents: " + string.Join( ",", plan.Bones.Select( b => b.ParentName ?? "-" ) ) );
		} );
		Check( "hinge child bones start exactly on the joint pivot", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Near( plan.Bones[1].Head, new Vec3( 0f, 0f, 2f ), 1e-4f, "upper bone head" );
			Near( plan.Bones[2].Head, new Vec3( 0f, 0f, 4f ), 1e-4f, "forearm bone head" );
		} );
		Check( "child bones point along the limb toward the next joint", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Near( plan.Bones[1].Tail, new Vec3( 0f, 0f, 4f ), 1e-4f, "upper bone tail" );
			Near( plan.Bones[2].Tail, new Vec3( 0f, 0f, 6f ), 1e-4f, "forearm bone tail" );
		} );
		Check( "all three joints map with limits and units recorded", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Require( plan.Joints.Count == 3 && plan.Joints.Count( j => j.Kind == "hinge" ) == 2 && plan.Joints.Count( j => j.Kind == "slider" ) == 1,
				$"joints={plan.Joints.Count} hinge={plan.Joints.Count( j => j.Kind == "hinge" )} slider={plan.Joints.Count( j => j.Kind == "slider" )}" );
			var hinge1 = plan.Joints.First( j => j.ChildBoneName == "Upper_arm" );
			var slider = plan.Joints.First( j => j.Kind == "slider" );
			Require( hinge1.Minimum == -1.2f && hinge1.Maximum == 1.2f && hinge1.Unit == "rad"
				&& slider.Minimum == 0f && slider.Maximum == 2f && slider.Unit == "in",
				"limits/units on the mappings" );
			Require( AssemblyToRig.AssemblyToRigPlan.ClampHingeAngle( hinge1, 1.5f ) == 1.2f
				&& AssemblyToRig.AssemblyToRigPlan.ClampHingeAngle( hinge1, -3f ) == -1.2f
				&& AssemblyToRig.AssemblyToRigPlan.ClampSliderTravel( slider, 3f ) == 2f
				&& AssemblyToRig.AssemblyToRigPlan.ClampSliderTravel( slider, -1f ) == 0f,
				"limit clamps hold at both ends" );
		} );
		Check( "rest pose reads hinge angle 0 and slider travel 0", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			var hinge1 = plan.Joints.First( j => j.ChildBoneName == "Upper_arm" );
			var slider = plan.Joints.First( j => j.Kind == "slider" );
			Require( hinge1.CurrentAngle.HasValue && MathF.Abs( hinge1.CurrentAngle.Value ) < 1e-4f
				&& slider.CurrentTravel.HasValue && slider.CurrentTravel.Value == 0f,
				$"angle={hinge1.CurrentAngle} travel={slider.CurrentTravel}" );
		} );
		Check( "planning is pure and repeatable", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			var plan2 = AssemblyToRig.Plan( assembly, studio, baseId );
			Require( AssemblyToRig.Describe( plan ) == AssemblyToRig.Describe( plan2 ), "two plans must describe identically" );
		} );
		Check( "apply creates the bones and pins every body, assembly untouched", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var bodyIds = studio.Bodies.Select( b => b.Id ).ToArray();
			var jsonBefore = assembly.ToJson();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			var result = AssemblyToRig.Apply( studio, plan );
			Require( studio.Rig.Count == 4 && result.AppliedBoneNames.Count == 4, $"rig={studio.Rig.Count} applied={result.AppliedBoneNames.Count}" );
			Require( result.BoundBodies.Count == 4 && bodyIds.All( id => result.BoundBodies.ContainsKey( id ) ),
				"bound: " + string.Join( ",", result.BoundBodies.Keys ) );
			Require( assembly.ToJson() == jsonBefore, "conversion must never mutate the assembly" );
			Require( result.SourceAssemblyJson == jsonBefore, "result must keep the source assembly JSON as its snapshot record" );
		} );
		Check( "rigid weights are exactly 1.0 to the pinned bone, no bleed", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			var result = AssemblyToRig.Apply( studio, plan );
			var (mesh, ranges) = studio.ToMeshWithBodies();
			var weights = SkinBinder.BindBodies( mesh, ranges, studio.BodyBoneMap, studio.Rig );
			var rigidOk = ranges.All( range =>
			{
				var boneIndex = studio.Rig.IndexOf( studio.BodyBoneMap[range.BodyId] );
				for ( var i = range.Start; i < range.Start + range.Count && i < weights.Count; i++ )
				{
					var w = weights[i];
					if ( w == null || w.Length != 1 || w[0].Bone != boneIndex || w[0].Weight != 1f ) return false;
				}
				return true;
			} );
			Require( rigidOk && result.WeightedVertexCount > 0, $"rigid weights wrong or nothing weighted (weighted={result.WeightedVertexCount})" );
		} );
		Check( "posing a hinge bone keeps its head fixed and moves its tail", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			AssemblyToRig.Apply( studio, plan );
			var upperIndex = studio.Rig.IndexOf( "Upper_arm" );
			var headBefore = studio.Rig.HeadWorld( upperIndex );
			var tailBefore = studio.Rig.TailWorld( upperIndex );
			var localBefore = studio.Rig.Bones[upperIndex].Local;
			var parentIndex = studio.Rig.Bones[upperIndex].Parent;
			var parentWorld = parentIndex >= 0 ? studio.Rig.WorldBind( parentIndex ) : Xform.Identity;
			var worldAfter = Xform.RotateAbout( headBefore, new Vec3( 1f, 0f, 0f ), 0.5f ) * studio.Rig.WorldBind( upperIndex );
			studio.Rig.Bones[upperIndex].Local = parentWorld.Inverse * worldAfter;
			Require( NearV( studio.Rig.HeadWorld( upperIndex ), headBefore ), "hinge pose must keep the head (pivot) fixed" );
			Require( !NearV( studio.Rig.TailWorld( upperIndex ), tailBefore, 0.1f ), "hinge pose must move the tail" );
			studio.Rig.Bones[upperIndex].Local = localBefore;
		} );
		Check( "convert plans and applies in one call", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var result = AssemblyToRig.Convert( assembly, studio, baseId );
			Require( studio.Rig.Count == 4 && result.BoundBodies.Count == 4, $"rig={studio.Rig.Count} bound={result.BoundBodies.Count}" );
			Require( result.Summary.Contains( "4 bone(s)" ), "summary must count the bones" );
		} );
		Check( "default root is the first occurrence in a joint", () =>
		{
			var (studio, assembly, _, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.RootOccurrenceName == "Base", $"root must be Base, got {plan.RootOccurrenceName}" );
		} );
		Check( "hinge limits are recorded, not enforced (warning)", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var plan = AssemblyToRig.Plan( assembly, studio, baseId );
			Require( plan.Warnings.Any( w => w.Contains( "recorded, not enforced" ) ),
				"warnings: " + string.Join( " | ", plan.Warnings ) );
		} );

		// ---- B. Fixed joints merge into one rigid bone; a misaligned
		//      rest pose warns instead of failing.
		Check( "fixed joint merges two parts into one bone", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var bA = Occ( "Plate A", assembly.Definitions[0].Id, Xform.Identity );
			var bB = Occ( "Plate B", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 1f ) ) );
			assembly.Occurrences.AddRange( new[] { bA, bB } );
			assembly.Joints.Add( Joint( "fixed", bA.Id, bB.Id,
				FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ), FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ) ) );
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.Success && plan.Bones.Count == 1, $"expected 1 bone, got {plan.Bones.Count}" );
			Require( plan.Warnings.Count > 0, "misaligned fixed frames must warn instead of failing" );
			var result = AssemblyToRig.Apply( studio, plan );
			Require( result.BoundBodies.Count == 2, "both fixed parts must pin to the one merged bone" );
		} );

		// ---- C. A loop converts as a safe subset: the closing joint is
		//      listed, the tree still converts.
		Check( "loop converts as a tree plus one skipped joint", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 3 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var cA = Occ( "Link A", assembly.Definitions[0].Id, Xform.Identity );
			var cB = Occ( "Link B", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 2f ) ) );
			var cC = Occ( "Link C", assembly.Definitions[2].Id, Xform.Translate( new Vec3( 0f, 0f, 4f ) ) );
			assembly.Occurrences.AddRange( new[] { cA, cB, cC } );
			assembly.Joints.Add( Joint( "hinge", cA.Id, cB.Id, FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			assembly.Joints.Add( Joint( "hinge", cB.Id, cC.Id, FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			assembly.Joints.Add( Joint( "hinge", cC.Id, cA.Id, FrameAt( new Vec3( 0f, 0f, 4f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			var plan = AssemblyToRig.Plan( assembly, studio, cA.Id );
			Require( plan.Success && plan.Bones.Count == 3 && plan.Joints.Count == 2 && plan.Skipped.Count == 1,
				$"bones={plan.Bones.Count} joints={plan.Joints.Count} skipped={plan.Skipped.Count}" );
			Require( plan.Skipped[0].Reason.Contains( "loop" ), "skipped loop joint must say why: " + plan.Skipped[0].Reason );
			AssemblyToRig.Apply( studio, plan );
			Require( studio.Rig.Count == 3, "loop subset must apply cleanly" );
		} );

		// ---- D. Unsupported kind, missing occurrence and inverted limits
		//      are each skipped with a reason; the good joint still converts.
		Check( "unsupported, missing and inverted joints are skipped, good joint converts", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var dA = Occ( "Arm A", assembly.Definitions[0].Id, Xform.Identity );
			var dB = Occ( "Arm B", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 2f ) ) );
			assembly.Occurrences.AddRange( new[] { dA, dB } );
			assembly.Joints.Add( Joint( "hinge", dA.Id, dB.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -1f, 1f ) );
			assembly.Joints.Add( Joint( "ball", dA.Id, dB.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			assembly.Joints.Add( Joint( "hinge", dA.Id, AssemblyDocument.NewId(),
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			assembly.Joints.Add( Joint( "hinge", dB.Id, dA.Id,
				FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), 2f, -2f ) );
			var plan = AssemblyToRig.Plan( assembly, studio, dA.Id );
			Require( plan.Success && plan.Bones.Count == 2 && plan.Joints.Count == 1 && plan.Skipped.Count == 3,
				$"bones={plan.Bones.Count} joints={plan.Joints.Count} skipped={plan.Skipped.Count}" );
			Require( plan.Skipped.Any( s => s.Kind == "ball" && s.Reason.Contains( "not supported" ) )
				&& plan.Skipped.Any( s => s.Reason.Contains( "does not exist" ) )
				&& plan.Skipped.Any( s => s.Reason.Contains( "exceeds" ) ),
				"each skipped joint must name its own reason" );
		} );
		Check( "hinge inside a fixed group is skipped, the group still converts", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var a = Occ( "Plate A", assembly.Definitions[0].Id, Xform.Identity );
			var b = Occ( "Plate B", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 1f ) ) );
			assembly.Occurrences.AddRange( new[] { a, b } );
			assembly.Joints.Add( Joint( "fixed", a.Id, b.Id, At( new Vec3( 0f, 0f, 0f ) ), At( new Vec3( 0f, 0f, 0f ) ) ) );
			assembly.Joints.Add( Joint( "hinge", a.Id, b.Id,
				FrameAt( new Vec3( 0f, 0f, 1f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -1f, 1f ) );
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.Success && plan.Bones.Count == 1 && plan.Joints.Count == 0 && plan.Skipped.Count == 1,
				$"bones={plan.Bones.Count} joints={plan.Joints.Count} skipped={plan.Skipped.Count}" );
			Require( plan.Skipped[0].Reason.Contains( "no motion" ), "skip reason: " + plan.Skipped[0].Reason );
		} );

		// ---- E. Slider-only assembly converts; a lone part becomes a
		//      standalone root bone; an empty assembly refuses.
		Check( "slider-only assembly converts with the slider mapped", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var eA = Occ( "Rail", assembly.Definitions[0].Id, Xform.Identity );
			var eB = Occ( "Carriage", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 1f ) ) );
			assembly.Occurrences.AddRange( new[] { eA, eB } );
			assembly.Joints.Add( Joint( "slider", eA.Id, eB.Id,
				FrameAt( new Vec3( 0f, 0f, 1f ), new Vec3( 0f, 0f, 1f ) ), FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ), -1f, 1f ) );
			var plan = AssemblyToRig.Plan( assembly, studio, eA.Id );
			Require( plan.Success && plan.Bones.Count == 2 && plan.Joints.Count == 1 && plan.Joints[0].Kind == "slider",
				$"bones={plan.Bones.Count} joints={plan.Joints.Count}" );
			Require( plan.Bones[1].Kind == "slider" && NearV( plan.Bones[1].Head, new Vec3( 0f, 0f, 1f ) ),
				"slider child bone must start on the slider pivot" );
		} );
		Check( "slider stored travel is read and drives the bind pose", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var e2A = Occ( "Rail 2", assembly.Definitions[0].Id, Xform.Identity );
			var e2B = Occ( "Carriage 2", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 1f ) ) );
			assembly.Occurrences.AddRange( new[] { e2A, e2B } );
			var e2Joint = Joint( "slider", e2A.Id, e2B.Id,
				FrameAt( new Vec3( 0f, 0f, 1f ), new Vec3( 0f, 0f, 1f ) ), FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ), -1f, 3f );
			Require( AssemblySlider.TrySetPosition( e2Joint, 1.5f, out var setErr ), "position must store: " + setErr );
			assembly.Joints.Add( e2Joint );
			var plan = AssemblyToRig.Plan( assembly, studio, e2A.Id );
			Require( plan.Success && plan.Joints.Count == 1 && plan.Joints[0].CurrentTravel == 1.5f,
				plan.Joints.Count > 0 ? $"got travel {plan.Joints[0].CurrentTravel}" : "no joint mapped" );
			Require( NearV( plan.Bones[1].Head, new Vec3( 0f, 0f, 1f ) ) && NearV( plan.Bones[1].Tail, new Vec3( 0f, 0f, 2.5f ) ),
				"slider bind pose must follow the stored travel (carriage moved 1.5 in along the axis)" );
		} );
		Check( "slider travel outside its limits is clamped with a warning", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var a = Occ( "Rail", assembly.Definitions[0].Id, Xform.Identity );
			var b = Occ( "Carriage", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 1f ) ) );
			assembly.Occurrences.AddRange( new[] { a, b } );
			var joint = Joint( "slider", a.Id, b.Id,
				FrameAt( new Vec3( 0f, 0f, 1f ), new Vec3( 0f, 0f, 1f ) ), FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ), 0f, 2f );
			// Write the stored value past the limit directly: TrySetPosition
			// refuses it, and the plan must clamp a stale out-of-range value.
			joint.ExtensionData = new Dictionary<string, JsonElement>
				{ ["position"] = JsonSerializer.SerializeToElement( 5f ) };
			assembly.Joints.Add( joint );
			var plan = AssemblyToRig.Plan( assembly, studio, a.Id );
			Require( plan.Success && plan.Joints.Count == 1 && plan.Joints[0].CurrentTravel == 2f,
				plan.Joints.Count > 0 ? $"got travel {plan.Joints[0].CurrentTravel}" : "no joint mapped" );
			Require( plan.Warnings.Any( w => w.Contains( "clamped" ) ), "clamping must be warned" );
		} );
		Check( "hinge current angle reads 0.5 rad from the occurrence transforms", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var gA = Occ( "Hinge base", assembly.Definitions[0].Id, Xform.Identity );
			var cos = MathF.Cos( 0.5f ); var sin = MathF.Sin( 0.5f );
			var gB = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = assembly.Definitions[1].Id, Name = "Hinge arm",
				Transform = new AssemblyTransform
				{
					X = new[] { 1f, 0f, 0f }, Y = new[] { 0f, cos, sin }, Z = new[] { 0f, -sin, cos },
					Origin = new[] { 0f, 0f, 2f },
				},
			};
			assembly.Occurrences.AddRange( new[] { gA, gB } );
			assembly.Joints.Add( Joint( "hinge", gA.Id, gB.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -1f, 1f ) );
			var plan = AssemblyToRig.Plan( assembly, studio, gA.Id );
			Require( plan.Success && plan.Joints.Count == 1
				&& plan.Joints[0].CurrentAngle.HasValue
				&& MathF.Abs( plan.Joints[0].CurrentAngle.Value - 0.5f ) < 1e-4f,
				plan.Joints.Count > 0 ? $"got angle {plan.Joints[0].CurrentAngle}" : "no joint mapped" );
		} );

		// ---- H. Grounded occurrence anchors the tree when no root is picked;
		//      an explicit pick wins; two grounded parts in one tree warn.
		Check( "grounded occurrence anchors the tree, explicit pick wins", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 3 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var hA = Occ( "Free A", assembly.Definitions[0].Id, Xform.Identity );
			var hB = Occ( "Fixed base", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 2f ) ) );
			var hC = Occ( "Free C", assembly.Definitions[2].Id, Xform.Translate( new Vec3( 0f, 0f, 4f ) ) );
			Require( AssemblyGrounding.SetGrounded( assembly, hB.Id, true ), "grounding B must stick" );
			assembly.Occurrences.AddRange( new[] { hA, hB, hC } );
			assembly.Joints.Add( Joint( "hinge", hA.Id, hB.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			assembly.Joints.Add( Joint( "hinge", hB.Id, hC.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			var planH = AssemblyToRig.Plan( assembly, studio );
			Require( planH.Success && planH.RootOccurrenceName == "Fixed base",
				$"grounded root must be Fixed base, got {planH.RootOccurrenceName}" );
			var planH2 = AssemblyToRig.Plan( assembly, studio, hA.Id );
			Require( planH2.RootOccurrenceName == "Free A", "an explicit root pick must win over the grounded flag" );
			Require( AssemblyGrounding.SetGrounded( assembly, hA.Id, true ), "grounding A must stick" );
			var planH3 = AssemblyToRig.Plan( assembly, studio );
			Require( planH3.Warnings.Any( w => w.Contains( "grounded" ) ),
				"two grounded parts in one tree must warn instead of failing" );
		} );

		// ---- F. A lone occurrence converts as a standalone root bone, at
		//      least MinBoneLength long; an empty assembly refuses.
		Check( "a lone occurrence converts as a standalone root bone", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 1 );
			assembly.Occurrences.Add( Occ( "Loose part", assembly.Definitions[0].Id, Xform.Identity ) );
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.Success && plan.Bones.Count == 1 && plan.Bones[0].Kind == "standalone",
				$"bones={plan.Bones.Count} kind={plan.Bones[0].Kind}" );
			NearF( ( plan.Bones[0].Tail - plan.Bones[0].Head ).Length, AssemblyToRig.MinBoneLength, 1e-4f,
				"a point part still gets a MinBoneLength bone" );
		} );
		Check( "plan refusals name the problem", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 1 );
			var nullDoc = AssemblyToRig.Plan( null, studio );
			Require( !nullDoc.Success && nullDoc.Error == "No assembly document.", "null doc: " + nullDoc.Error );
			var nullStudio = AssemblyToRig.Plan( assembly, null );
			Require( !nullStudio.Success && nullStudio.Error == "No studio to convert into.", "null studio: " + nullStudio.Error );
			var badUnits = new AssemblyDocument { LengthUnit = "mm" };
			badUnits.Occurrences.Add( Occ( "P", assembly.Definitions[0].Id, Xform.Identity ) );
			var badUnitsPlan = AssemblyToRig.Plan( badUnits, studio );
			Require( !badUnitsPlan.Success && badUnitsPlan.Error.Contains( "unsupported" ), "wrong units: " + badUnitsPlan.Error );
			var future = new AssemblyDocument { SchemaVersion = 2 };
			future.Occurrences.Add( Occ( "P", assembly.Definitions[0].Id, Xform.Identity ) );
			var futurePlan = AssemblyToRig.Plan( future, studio );
			Require( !futurePlan.Success && futurePlan.Error.Contains( "unsupported" ), "future schema: " + futurePlan.Error );
			var empty = AssemblyToRig.Plan( new AssemblyDocument(), new PartStudio() );
			Require( !empty.Success && empty.Error != null, "empty assembly must refuse" );
			Require( new PartStudio().Rig.Count == 0, "a refused plan must change nothing" );
		} );
		Check( "an occurrence with no valid transform is left out with a warning", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var a = Occ( "Good", assembly.Definitions[0].Id, Xform.Identity );
			var b = Occ( "Broken", assembly.Definitions[1].Id, Xform.Identity );
			b.Transform = new AssemblyTransform { Origin = new[] { float.NaN, 0f, 0f } };
			assembly.Occurrences.AddRange( new[] { a, b } );
			assembly.Joints.Add( Joint( "hinge", a.Id, b.Id,
				FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.Success && plan.Bones.Count == 1 && plan.Bones[0].Kind == "standalone",
				$"the good part must still convert alone: bones={plan.Bones.Count}" );
			Require( plan.Warnings.Any( w => w.Contains( "no valid transform" ) ), "the broken part must be warned" );
			Require( plan.Skipped.Count == 1 && plan.Skipped[0].Reason.Contains( "no valid transform" ),
				"the joint to the broken part must be skipped with the reason" );
		} );

		// ---- Disconnected components each get their own root bone.
		Check( "disconnected components each get their own root bone", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 4 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var a = Occ( "A", assembly.Definitions[0].Id, Xform.Identity );
			var b = Occ( "B", assembly.Definitions[1].Id, Xform.Translate( new Vec3( 0f, 0f, 2f ) ) );
			var c = Occ( "C", assembly.Definitions[2].Id, Xform.Translate( new Vec3( 0f, 0f, 10f ) ) );
			var d = Occ( "D", assembly.Definitions[3].Id, Xform.Translate( new Vec3( 0f, 0f, 12f ) ) );
			assembly.Occurrences.AddRange( new[] { a, b, c, d } );
			assembly.Joints.Add( Joint( "hinge", a.Id, b.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			assembly.Joints.Add( Joint( "hinge", c.Id, d.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ) ) );
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.Success && plan.Bones.Count == 4 && plan.Joints.Count == 2,
				$"bones={plan.Bones.Count} joints={plan.Joints.Count}" );
			Require( plan.Bones.Count( bone => bone.ParentName == null ) == 2, "two components need two roots" );
			Require( plan.Bones[0].Name == "A" && plan.Bones[2].Name == "C",
				"components convert in document order" );
		} );

		// ---- Unbound bodies still get a bone, but no pin and no weights.
		Check( "occurrence with no body in the studio still gets a bone but no pin", () =>
		{
			var studio = new PartStudio();
			var assembly = new AssemblyDocument();
			assembly.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Ghost",
				SourceKind = "studio-body", SourceReference = "studio-body#body-nope",
			} );
			assembly.Occurrences.Add( Occ( "Ghost part", assembly.Definitions[0].Id, Xform.Identity ) );
			var plan = AssemblyToRig.Plan( assembly, studio );
			Require( plan.Success && plan.Bones.Count == 1, "the ghost must still get a bone" );
			var result = AssemblyToRig.Apply( studio, plan );
			Require( result.BoundBodies.Count == 0 && result.UnboundBodies.ContainsKey( "body-nope" ),
				"the missing body must be reported, never silently skipped" );
			var noIdAssembly = new AssemblyDocument();
			noIdAssembly.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "NoRef",
				SourceKind = "studio-body", SourceReference = "",
			} );
			noIdAssembly.Occurrences.Add( Occ( "NoRef part", noIdAssembly.Definitions[0].Id, Xform.Identity ) );
			var noIdResult = AssemblyToRig.Convert( noIdAssembly, new PartStudio() );
			Require( noIdResult.BoundBodies.Count == 0 && noIdResult.UnboundBodies.Count == 1,
				"an occurrence with no body id must be reported unbound" );
		} );

		// ---- Convert to a new studio: the source studio and the assembly
		//      are untouched; the destination carries the rig and cloned bodies.
		Check( "convert to a new studio leaves the source untouched", () =>
		{
			var (studio, assembly, baseId, _, _, _, _, _) = RobotFixture();
			var jsonBefore = assembly.ToJson();
			var bodyMeshBefore = studio.Bodies[0].Mesh.Positions.ToArray();
			var (destination, result) = AssemblyToRig.ConvertToNewStudio( assembly, studio, baseId );
			Require( studio.Rig.Count == 0, "the source studio must gain no bones" );
			Require( assembly.ToJson() == jsonBefore, "the source assembly must be untouched" );
			Require( destination.Rig.Count == 4 && result.BoundBodies.Count == 4,
				$"destination rig={destination.Rig.Count} bound={result.BoundBodies.Count}" );
			Require( destination.Assembly.ToJson() == jsonBefore, "the destination must carry a copy of the assembly" );
			Require( !ReferenceEquals( destination.Bodies[0].Mesh, studio.Bodies[0].Mesh ),
				"destination bodies must be clones, not the source meshes" );
			Require( destination.Bodies[0].Mesh.Positions.Count == bodyMeshBefore.Length
				&& destination.Bodies[0].Mesh.Positions.Zip( bodyMeshBefore, ( got, want ) => ( got - want ).LengthSquared ).All( d => d < 1e-8f ),
				"cloned bodies must carry the same geometry" );
		} );

		// ---- Save / reopen keeps the assembly, the rig and the pins.
		//      Real feature bodies (PrimitiveFeature + Rebuild) so the bodies
		//      themselves survive the round-trip, like Mobile's checks.
		Check( "save and reopen keeps the assembly, the rig and the pins", () =>
		{
			var studio = new PartStudio();
			for ( var i = 0; i < 4; i++ )
				studio.Add( new PrimitiveFeature { SizeX = { Value = 8f }, SizeY = { Value = 8f }, SizeZ = { Value = 8f } } );
			studio.Rebuild();
			Require( studio.Bodies.Count == 4, $"expected 4 feature bodies, got {studio.Bodies.Count}" );
			var assembly = new AssemblyDocument();
			foreach ( var body in studio.Bodies )
				assembly.Definitions.Add( new AssemblyPartDefinition
				{
					Id = AssemblyDocument.NewId(), Name = "Part " + body.Id,
					SourceKind = "studio-body", SourceReference = "studio-body#" + body.Id,
				} );
			var defs = assembly.Definitions;
			var baseOcc = Occ( "Base", defs[0].Id, Xform.Identity );
			var upperOcc = Occ( "Upper arm", defs[1].Id, Xform.Translate( new Vec3( 0f, 0f, 2f ) ) );
			var foreOcc = Occ( "Forearm", defs[2].Id, Xform.Translate( new Vec3( 0f, 0f, 4f ) ) );
			var gripOcc = Occ( "Gripper", defs[3].Id, Xform.Translate( new Vec3( 0f, 0f, 6f ) ) );
			assembly.Occurrences.AddRange( new[] { baseOcc, upperOcc, foreOcc, gripOcc } );
			var hingeX = new Vec3( 1f, 0f, 0f );
			assembly.Joints.Add( Joint( "hinge", baseOcc.Id, upperOcc.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -1.2f, 1.2f ) );
			assembly.Joints.Add( Joint( "hinge", upperOcc.Id, foreOcc.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -2.0f, 0.5f ) );
			assembly.Joints.Add( Joint( "slider", foreOcc.Id, gripOcc.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), new Vec3( 0f, 0f, 1f ) ), FrameAt( new Vec3( 0f, 0f, 0f ), new Vec3( 0f, 0f, 1f ) ), 0f, 2f ) );
			var jsonBefore = assembly.ToJson();
			var plan = AssemblyToRig.Plan( assembly, studio, baseOcc.Id );
			AssemblyToRig.Apply( studio, plan );
			studio.Assembly = assembly;
			var reopened = StudioDocument.Read( StudioDocument.Write( studio ) );
			Require( reopened.Assembly.ToJson() == jsonBefore, "assembly must round-trip through save/reopen" );
			Require( reopened.Rig.Count == 4, $"rig bones must round-trip, got {reopened.Rig.Count}" );
			Require( reopened.BodyBoneMap.Count == 4 && studio.Bodies.All( b => reopened.BodyBoneMap.ContainsKey( b.Id ) ),
				"body pins must round-trip through save/reopen" );
		} );

		// ---- Round-trip: the converted rig, re-applied to a fresh studio
		//      holding the assembly-posed meshes, drives the same posed mesh
		//      as the assembly snapshot (production SkinBinder.Deform).
		Check( "converted rig re-applied to a fresh studio reproduces the assembly pose", () =>
		{
			var (studio, _, assembly) = StudioWithParts( 2 );
			var hingeX = new Vec3( 1f, 0f, 0f );
			var a = Occ( "Base", assembly.Definitions[0].Id, Xform.Identity );
			var cos = MathF.Cos( 0.5f ); var sin = MathF.Sin( 0.5f );
			var b = new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = assembly.Definitions[1].Id, Name = "Arm",
				Transform = new AssemblyTransform
				{
					X = new[] { 1f, 0f, 0f }, Y = new[] { 0f, cos, sin }, Z = new[] { 0f, -sin, cos },
					Origin = new[] { 0f, 0f, 2f },
				},
			};
			assembly.Occurrences.AddRange( new[] { a, b } );
			assembly.Joints.Add( Joint( "hinge", a.Id, b.Id,
				FrameAt( new Vec3( 0f, 0f, 2f ), hingeX ), FrameAt( new Vec3( 0f, 0f, 0f ), hingeX ), -1f, 1f ) );
			var plan = AssemblyToRig.Plan( assembly, studio, a.Id );
			Require( plan.Success, "plan must succeed: " + plan.Error );
			Require( plan.Joints.Count == 1 && plan.Joints[0].CurrentAngle.HasValue
				&& MathF.Abs( plan.Joints[0].CurrentAngle.Value - 0.5f ) < 1e-4f,
				"hinge must read 0.5 rad from the transforms" );
			AssemblyToRig.Apply( studio, plan );

			// Assembly snapshot: the posed meshes in assembly space.
			var snapshot = new AssemblySnapshotService().Build( assembly, ResolverFor( studio ) );
			Require( snapshot.Occurrences.Count == 2 && snapshot.Occurrences.All( s => !s.MissingSource ),
				"both occurrences must resolve in the snapshot" );
			var snapA = snapshot.Occurrences[0].Mesh.Positions;
			var snapB = snapshot.Occurrences[1].Mesh.Positions;

			// Fresh studio holding the assembly-posed meshes, same body ids;
			// re-apply the same plan onto it.
			var fresh = new PartStudio();
			fresh.Bodies.Add( new Body( studio.Bodies[0].Id, "Base", snapshot.Occurrences[0].Mesh.Clone() ) );
			fresh.Bodies.Add( new Body( studio.Bodies[1].Id, "Arm", snapshot.Occurrences[1].Mesh.Clone() ) );
			var reapplied = AssemblyToRig.Apply( fresh, plan );
			Require( fresh.Rig.Count == 2 && reapplied.BoundBodies.Count == 2, "the plan must re-apply cleanly to the fresh studio" );
			var (freshMesh, freshRanges) = fresh.ToMeshWithBodies();
			var freshWeights = SkinBinder.BindBodies( freshMesh, freshRanges, fresh.BodyBoneMap, fresh.Rig );
			var bindWorlds = Enumerable.Range( 0, fresh.Rig.Count ).Select( i => fresh.Rig.WorldBind( i ) ).ToList();

			// At bind the rig must reproduce the snapshot exactly.
			var atBind = SkinBinder.Deform( freshMesh.Positions, freshWeights, bindWorlds, bindWorlds );
			Require( PositionsMatch( atBind, freshRanges, snapA, snapB, 1e-4f ),
				"bind pose must reproduce the assembly snapshot" );

			// Pose the arm bone +0.3 rad about the hinge axis through the
			// pivot; the arm's vertices must follow exactly, the base's not at all.
			var armIndex = fresh.Rig.IndexOf( "Arm" );
			var pivot = plan.Joints[0].PivotWorld;
			var headBefore = fresh.Rig.HeadWorld( armIndex );
			Near( headBefore, pivot, 1e-4f, "arm bone head must sit on the joint pivot" );
			var parentWorld = fresh.Rig.WorldBind( fresh.Rig.Bones[armIndex].Parent );
			var worldAfter = Xform.RotateAbout( headBefore, hingeX, 0.3f ) * fresh.Rig.WorldBind( armIndex );
			fresh.Rig.Bones[armIndex].Local = parentWorld.Inverse * worldAfter;
			var poseWorlds = Enumerable.Range( 0, fresh.Rig.Count ).Select( i => fresh.Rig.WorldBind( i ) ).ToList();
			var posed = SkinBinder.Deform( freshMesh.Positions, freshWeights, bindWorlds, poseWorlds );
			var rotate = Xform.RotateAbout( pivot, hingeX, 0.3f );
			var wantB = snapB.Select( p => rotate.TransformPoint( p ) ).ToList();
			Require( PositionsMatch( posed, freshRanges, snapA, wantB, 1e-4f ),
				"posing the rig must move the arm exactly like the assembly joint" );
		} );

		// Per-range position comparison: each body range against its expected
		// positions, order-insensitive within the range.
		static bool PositionsMatch( Vec3[] got, IReadOnlyList<BodyRange> ranges, IReadOnlyList<Vec3> wantA, IReadOnlyList<Vec3> wantB, float tol )
		{
			if ( ranges.Count != 2 ) return false;
			return RangeMatch( got, ranges[0], wantA, tol ) && RangeMatch( got, ranges[1], wantB, tol );
		}
		static bool RangeMatch( Vec3[] got, BodyRange range, IReadOnlyList<Vec3> want, float tol )
		{
			if ( range.Count != want.Count ) return false;
			var gotSorted = got.Skip( range.Start ).Take( range.Count )
				.OrderBy( p => p.x ).ThenBy( p => p.y ).ThenBy( p => p.z ).ToList();
			var wantSorted = want.OrderBy( p => p.x ).ThenBy( p => p.y ).ThenBy( p => p.z ).ToList();
			for ( var i = 0; i < gotSorted.Count; i++ )
				if ( ( gotSorted[i] - wantSorted[i] ).LengthSquared > tol * tol ) return false;
			return true;
		}
	}
}
