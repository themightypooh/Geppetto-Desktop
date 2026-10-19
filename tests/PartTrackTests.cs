#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class PartTrackTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "part_track", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void AssertNear( float actual, float expected, float eps = 1e-3f, string? msg = null )
		{
			if ( MathF.Abs( actual - expected ) > eps )
				throw new Exception( $"{msg ?? "Value"} expected {expected}, but got {actual} (diff: {MathF.Abs( actual - expected )})" );
		}

		void AssertVecNear( Vec3 actual, Vec3 expected, float eps = 1e-3f, string? msg = null )
		{
			if ( (actual - expected).Length > eps )
				throw new Exception( $"{msg ?? "Vec3"} expected ({expected.x}, {expected.y}, {expected.z}), but got ({actual.x}, {actual.y}, {actual.z})" );
		}

		// 1. Nested part composition (child part follows parent part)
		Check( "nested part composition (child part follows parent part)", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var desk = new RigPlacement
			{
				Name = "desk",
				Pose = new RigPose( new Vec3( 100, 0, 0 ), Quat.Identity, Vec3.One ),
				Parts = new List<RigPlacement>
				{
					new()
					{
						Name = "drawer_unit",
						Pose = new RigPose( new Vec3( 10, 0, 0 ), Quat.Identity, Vec3.One )
					},
					new()
					{
						Name = "drawer_tray",
						ParentPart = "drawer_unit",
						Pose = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One )
					},
					new()
					{
						Name = "handle",
						ParentPart = "drawer_tray",
						Pose = new RigPose( new Vec3( 0, 0, 2 ), Quat.Identity, Vec3.One )
					}
				}
			};
			clip.Objects.Add( desk );

			// Add animated part track for drawer_tray (sliding forward on Y by 8 units at frame 15)
			var trayTrack = new RigTrack { Name = "desk/drawer_tray", Kind = RigTrackKind.Part };
			trayTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			trayTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 21, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( trayTrack );

			// Frame 0 evaluation:
			// desk: (100, 0, 0)
			// drawer_unit: 100 + 10 = (110, 0, 0)
			// drawer_tray: 110 + (0, 5, 0) = (110, 5, 0)
			// handle: 110 + (0, 5, 0) + (0, 0, 2) = (110, 5, 2)
			var res0 = PartTrackEvaluator.Evaluate( clip, 0f );
			var unit0 = res0.GetPart( "desk/drawer_unit" );
			var tray0 = res0.GetPart( "desk/drawer_tray" );
			var handle0 = res0.GetPart( "desk/handle" );

			Assert( unit0 != null && tray0 != null && handle0 != null, "All parts must be evaluated" );
			AssertVecNear( unit0!.Transform.Origin, new Vec3( 110, 0, 0 ), 1e-3f, "Unit at frame 0" );
			AssertVecNear( tray0!.Transform.Origin, new Vec3( 110, 5, 0 ), 1e-3f, "Tray at frame 0" );
			AssertVecNear( handle0!.Transform.Origin, new Vec3( 110, 5, 2 ), 1e-3f, "Handle at frame 0" );

			// Frame 15 evaluation:
			// tray track local evaluates to (0, 13, 0)
			// handle should automatically follow parent drawer_tray to (110, 13, 2)
			var res15 = PartTrackEvaluator.Evaluate( clip, 15f );
			var tray15 = res15.GetPart( "desk/drawer_tray" );
			var handle15 = res15.GetPart( "desk/handle" );

			AssertVecNear( tray15!.Transform.Origin, new Vec3( 110, 13, 0 ), 1e-3f, "Tray at frame 15" );
			AssertVecNear( handle15!.Transform.Origin, new Vec3( 110, 13, 2 ), 1e-3f, "Handle at frame 15 following animated tray" );
		} );

		// 2. Follow-bone part follows animated bone
		Check( "follow-bone part follows animated bone", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Identity );
			rig.AddBone( "hand_R", 0, Xform.Translate( new Vec3( 20, 0, 10 ) ) );

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var boneTrack = new RigTrack { Name = "hand_R", Kind = RigTrackKind.Bone };
			boneTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 20, 0, 10 ), Quat.Identity, Vec3.One ) } );
			boneTrack.Keys.Add( new RigKey { Frame = 30, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 20, 30, 20 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( boneTrack );

			// Object with a part that follows hand_R
			var weaponObj = new RigPlacement
			{
				Name = "equipment",
				Parts = new List<RigPlacement>
				{
					new()
					{
						Name = "sword",
						FollowBone = "hand_R",
						Pose = new RigPose( new Vec3( 0, 0, 3 ), Quat.Identity, Vec3.One )
					}
				}
			};
			clip.Objects.Add( weaponObj );

			// Standalone reference prop that also follows hand_R
			var shieldProp = new RigPlacement
			{
				Name = "buckler",
				FollowBone = "hand_R",
				Pose = new RigPose( new Vec3( 0, -2, 0 ), Quat.Identity, Vec3.One )
			};
			clip.Props.Add( shieldProp );

			// Frame 0:
			// hand_R at (20, 0, 10)
			// sword at (20, 0, 13)
			// buckler at (20, -2, 10)
			var eval0 = PartTrackEvaluator.Evaluate( clip, 0f, skeleton: rig );
			var sword0 = eval0.GetPart( "equipment/sword" );
			var buckler0 = eval0.GetPart( "buckler" );

			Assert( sword0 != null && buckler0 != null );
			AssertVecNear( sword0!.Transform.Origin, new Vec3( 20, 0, 13 ), 1e-3f, "Sword at frame 0" );
			AssertVecNear( buckler0!.Transform.Origin, new Vec3( 20, -2, 10 ), 1e-3f, "Buckler at frame 0" );

			// Frame 15:
			// hand_R at (20, 15, 15)
			// sword at (20, 15, 18)
			// buckler at (20, 13, 15)
			var eval15 = PartTrackEvaluator.Evaluate( clip, 15f, skeleton: rig );
			var sword15 = eval15.GetPart( "equipment/sword" );
			var buckler15 = eval15.GetPart( "buckler" );

			AssertVecNear( sword15!.Transform.Origin, new Vec3( 20, 15, 18 ), 1e-3f, "Sword at frame 15" );
			AssertVecNear( buckler15!.Transform.Origin, new Vec3( 20, 13, 15 ), 1e-3f, "Buckler at frame 15" );
		} );

		// 3. Duplicate part names on different objects remain distinct
		Check( "duplicate part names on different objects remain distinct", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var carLeft = new RigPlacement
			{
				Name = "car_left",
				Pose = new RigPose( new Vec3( -50, 0, 0 ), Quat.Identity, Vec3.One ),
				Parts = new List<RigPlacement>
				{
					new() { Name = "wheel", Pose = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) }
				}
			};
			var carRight = new RigPlacement
			{
				Name = "car_right",
				Pose = new RigPose( new Vec3( 50, 0, 0 ), Quat.Identity, Vec3.One ),
				Parts = new List<RigPlacement>
				{
					new() { Name = "wheel", Pose = new RigPose( new Vec3( 0, -5, 0 ), Quat.Identity, Vec3.One ) }
				}
			};
			clip.Objects.Add( carLeft );
			clip.Objects.Add( carRight );

			// Track for car_left/wheel animating to (0, 15, 0)
			var trackL = new RigTrack { Name = "car_left/wheel", Kind = RigTrackKind.Part };
			trackL.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 5, 0 ), Quat.Identity, Vec3.One ) } );
			trackL.Keys.Add( new RigKey { Frame = 10, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, 15, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( trackL );

			// Track for car_right/wheel animating to (0, -25, 0)
			var trackR = new RigTrack { Name = "car_right/wheel", Kind = RigTrackKind.Part };
			trackR.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, -5, 0 ), Quat.Identity, Vec3.One ) } );
			trackR.Keys.Add( new RigKey { Frame = 10, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 0, -25, 0 ), Quat.Identity, Vec3.One ) } );
			clip.Tracks.Add( trackR );

			var res = PartTrackEvaluator.Evaluate( clip, 10f );

			var wheelL = res.GetPart( "car_left/wheel" );
			var wheelR = res.GetPart( "car_right/wheel" );

			Assert( wheelL != null && wheelR != null, "Both duplicate-named parts must be present" );
			Assert( wheelL!.Key == "car_left/wheel" );
			Assert( wheelR!.Key == "car_right/wheel" );

			// car_left at -50, wheel at 15 -> origin is (-50, 15, 0)
			// car_right at 50, wheel at -25 -> origin is (50, -25, 0)
			AssertVecNear( wheelL.Transform.Origin, new Vec3( -50, 15, 0 ), 1e-3f, "Car left wheel" );
			AssertVecNear( wheelR.Transform.Origin, new Vec3( 50, -25, 0 ), 1e-3f, "Car right wheel" );
			Assert( (wheelL.Transform.Origin - wheelR.Transform.Origin).Length > 1e-3f, "Transforms must not collide" );
		} );

		// 4. Circular dependency detection returns safe diagnostic
		Check( "circular dependency detection returns safe diagnostic", () =>
		{
			var clip = new RigAnimFile();
			var cycleObj = new RigPlacement
			{
				Name = "contraption",
				Parts = new List<RigPlacement>
				{
					new() { Name = "gear_A", ParentPart = "gear_B", Pose = new RigPose( new Vec3( 1, 0, 0 ), Quat.Identity, Vec3.One ) },
					new() { Name = "gear_B", ParentPart = "gear_C", Pose = new RigPose( new Vec3( 2, 0, 0 ), Quat.Identity, Vec3.One ) },
					new() { Name = "gear_C", ParentPart = "gear_A", Pose = new RigPose( new Vec3( 3, 0, 0 ), Quat.Identity, Vec3.One ) }
				}
			};
			clip.Objects.Add( cycleObj );

			var res = PartTrackEvaluator.Evaluate( clip, 0f );

			Assert( res.Diagnostics.Count > 0, "Diagnostics must report the circular dependency" );
			Assert( res.Diagnostics.Any( d => d.Contains( "Circular dependency", StringComparison.OrdinalIgnoreCase ) ), "Must mention circular dependency" );

			var gearA = res.GetPart( "contraption/gear_A" );
			var gearB = res.GetPart( "contraption/gear_B" );
			var gearC = res.GetPart( "contraption/gear_C" );

			Assert( gearA != null && gearA.HasCycle, "Gear A must be flagged with HasCycle" );
			Assert( gearB != null && gearB.HasCycle, "Gear B must be flagged with HasCycle" );
			Assert( gearC != null && gearC.HasCycle, "Gear C must be flagged with HasCycle" );

			// Safe fallback without NaN/inf
			Assert( float.IsFinite( gearA!.Transform.Origin.x ), "Transform must remain finite" );
		} );

		// 5. Object-space IK goal aligns with evaluated part transform
		Check( "object-space IK goal aligns with evaluated part transform", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var obj = new RigPlacement
			{
				Name = "switchboard",
				Pose = new RigPose( new Vec3( 50, 20, 10 ), Quat.Identity, Vec3.One ),
				Parts = new List<RigPlacement>
				{
					new() { Name = "lever", Pose = new RigPose( new Vec3( 5, 2, 1 ), Quat.Identity, Vec3.One ) }
				}
			};
			clip.Objects.Add( obj );

			// Anchor IK goal for hand_R to switchboard/lever in Object space
			clip.GoalSpaces["hand_R"] = (RigGoalSpace.Object, "switchboard/lever");

			// Goal track with identity local pose (no offset from the lever part)
			var goalTrack = new RigTrack { Name = "hand_R", Kind = RigTrackKind.IkGoal };
			goalTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = RigPose.Identity } );
			clip.Tracks.Add( goalTrack );

			// Evaluated lever transform: (50 + 5, 20 + 2, 10 + 1) = (55, 22, 11)
			var evaluatedPlacements = PartTrackEvaluator.EvaluatePlacements( clip, 0f );
			var evaluatedLever = evaluatedPlacements["switchboard/lever"];

			var evaluatedGoal = PartTrackEvaluator.EvaluateIkGoal( clip, "hand_R", 0f, precomputedPlacements: evaluatedPlacements );

			Assert( evaluatedGoal != null, "IK goal must evaluate" );
			AssertVecNear( evaluatedGoal!.Value.Position, evaluatedLever.Transform.Origin, 1e-4f, "IK goal must align with evaluated part" );
			AssertVecNear( evaluatedGoal.Value.Position, new Vec3( 55, 22, 11 ), 1e-4f );

			// Also verify that when goal has an offset (e.g. +2 on Z), it composes onto the part transform
			goalTrack.Keys[0].Local = new RigPose( new Vec3( 0, 0, 2 ), Quat.Identity, Vec3.One );
			var evaluatedOffsetGoal = PartTrackEvaluator.EvaluateIkGoal( clip, "hand_R", 0f, precomputedPlacements: evaluatedPlacements );
			AssertVecNear( evaluatedOffsetGoal!.Value.Position, new Vec3( 55, 22, 13 ), 1e-4f, "Offset goal aligns with part + offset" );
		} );

		// 6. Camera pose and FOV interpolation
		Check( "camera pose and FOV interpolation", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var camTrack = new RigTrack { Name = "DirectorCam", Kind = RigTrackKind.Camera };
			// Key 0: Position (10, 20, 30), FOV 60
			camTrack.Keys.Add( new RigKey
			{
				Frame = 0,
				Ease = RigEase.Linear,
				Local = new RigPose( new Vec3( 10, 20, 30 ), Quat.Identity, new Vec3( 60, 60, 60 ) )
			} );
			// Key 30: Position (40, 80, 90), FOV 90
			camTrack.Keys.Add( new RigKey
			{
				Frame = 30,
				Ease = RigEase.Linear,
				Local = new RigPose( new Vec3( 40, 80, 90 ), Quat.FromAngles( 0, 90, 0 ), new Vec3( 90, 90, 90 ) )
			} );
			clip.Tracks.Add( camTrack );

			// Evaluate at mid-frame 15:
			// Position should be (25, 50, 60)
			// FOV should be 75
			var cam15 = PartTrackEvaluator.EvaluateCamera( clip, "DirectorCam", 15f );

			Assert( cam15 != null, "Camera must evaluate" );
			AssertVecNear( cam15!.Position, new Vec3( 25, 50, 60 ), 1e-3f, "Camera position at frame 15" );
			AssertNear( cam15.FieldOfView, 75f, 1e-3f, "Camera FOV at frame 15" );

			// Evaluate at frame 0 and 30
			var cam0 = PartTrackEvaluator.EvaluateCamera( clip, "DirectorCam", 0f );
			AssertNear( cam0!.FieldOfView, 60f, 1e-3f, "Camera FOV at frame 0" );

			var cam30 = PartTrackEvaluator.EvaluateCamera( clip, "DirectorCam", 30f );
			AssertNear( cam30!.FieldOfView, 90f, 1e-3f, "Camera FOV at frame 30" );
		} );

		// 7. Camera track playback does not modify orbit camera state
		Check( "camera track playback does not modify orbit camera state", () =>
		{
			var orbit = new OrbitCamera
			{
				Target = new Vector3( 15, -25, 40 ),
				Distance = 275f,
				YawDegrees = 48f,
				PitchDegrees = -22f,
				FovDegrees = 55f
			};

			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			var camTrack = new RigTrack { Name = "DramaticShot", Kind = RigTrackKind.Camera };
			camTrack.Keys.Add( new RigKey
			{
				Frame = 0,
				Ease = RigEase.Linear,
				Local = new RigPose( new Vec3( 999, 888, 777 ), Quat.FromAngles( 45, 45, 45 ), new Vec3( 120, 120, 120 ) )
			} );
			camTrack.Keys.Add( new RigKey
			{
				Frame = 30,
				Ease = RigEase.Linear,
				Local = new RigPose( new Vec3( -500, 300, 100 ), Quat.Identity, new Vec3( 25, 25, 25 ) )
			} );
			clip.Tracks.Add( camTrack );

			// Evaluate multiple frames
			for ( float f = 0; f <= 30; f += 2.5f )
			{
				var eval = PartTrackEvaluator.Evaluate( clip, f );
				Assert( eval.Cameras.ContainsKey( "DramaticShot" ) );

				// OrbitCamera must remain 100% unchanged!
				Assert( orbit.Target == new Vector3( 15, -25, 40 ), "Orbit Target must not be modified" );
				Assert( orbit.Distance == 275f, "Orbit Distance must not be modified" );
				Assert( orbit.YawDegrees == 48f, "Orbit Yaw must not be modified" );
				Assert( orbit.PitchDegrees == -22f, "Orbit Pitch must not be modified" );
				Assert( orbit.FovDegrees == 55f, "Orbit FOV must not be modified" );
			}
		} );

		// 8. Missing bone or part target handled safely
		Check( "missing bone or part target handled safely", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			var brokenObj = new RigPlacement
			{
				Name = "broken_actor",
				FollowBone = "ghost_bone_404",
				Pose = new RigPose( new Vec3( 10, 20, 30 ), Quat.Identity, Vec3.One ),
				Parts = new List<RigPlacement>
				{
					new()
					{
						Name = "child_part",
						ParentPart = "missing_parent_xyz",
						Pose = new RigPose( new Vec3( 1, 2, 3 ), Quat.Identity, Vec3.One )
					},
					new()
					{
						Name = "bone_part",
						FollowBone = "missing_limb",
						Pose = new RigPose( new Vec3( 4, 5, 6 ), Quat.Identity, Vec3.One )
					}
				}
			};
			clip.Objects.Add( brokenObj );

			clip.Props.Add( new RigPlacement
			{
				Name = "lost_prop",
				FollowBone = "phantom_anchor",
				Pose = new RigPose( new Vec3( 7, 8, 9 ), Quat.Identity, Vec3.One )
			} );

			var emptyRig = new Skeleton(); // No bones defined

			var res = PartTrackEvaluator.Evaluate( clip, 0f, skeleton: emptyRig );

			// Must not throw, and diagnostics must capture all 4 missing targets
			Assert( res.Diagnostics.Count >= 4, $"Expected at least 4 diagnostics, got {res.Diagnostics.Count}" );
			Assert( res.Diagnostics.Any( d => d.Contains( "ghost_bone_404" ) ) );
			Assert( res.Diagnostics.Any( d => d.Contains( "missing_parent_xyz" ) ) );
			Assert( res.Diagnostics.Any( d => d.Contains( "missing_limb" ) ) );
			Assert( res.Diagnostics.Any( d => d.Contains( "phantom_anchor" ) ) );

			// Placements must still evaluate to safe fallback transforms
			var actor = res.GetPart( "broken_actor" );
			var child = res.GetPart( "broken_actor/child_part" );
			var bonePart = res.GetPart( "broken_actor/bone_part" );
			var prop = res.GetPart( "lost_prop" );

			Assert( actor != null && !actor.IsValid );
			Assert( child != null && !child.IsValid );
			Assert( bonePart != null && !bonePart.IsValid );
			Assert( prop != null && !prop.IsValid );

			AssertVecNear( actor!.Transform.Origin, new Vec3( 10, 20, 30 ), 1e-3f );
			AssertVecNear( prop!.Transform.Origin, new Vec3( 7, 8, 9 ), 1e-3f );
		} );

		// 9. Camera Hermite cubic FOV curve and default fallback
		Check( "camera hermite cubic FOV curve and default fallback", () =>
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };

			// Camera with cubic ease
			var camTrack = new RigTrack { Name = "SmoothZoom", Kind = RigTrackKind.Camera };
			camTrack.Keys.Add( new RigKey
			{
				Frame = 0,
				Ease = RigEase.Cubic,
				Local = new RigPose( new Vec3( 0, 0, 0 ), Quat.Identity, new Vec3( 45, 45, 45 ) )
			} );
			camTrack.Keys.Add( new RigKey
			{
				Frame = 30,
				Ease = RigEase.Cubic,
				Local = new RigPose( new Vec3( 0, 0, 100 ), Quat.Identity, new Vec3( 90, 90, 90 ) )
			} );
			clip.Tracks.Add( camTrack );

			// Default unkeyed camera definition
			var defaultDef = new CameraDefinition { Name = "DefaultCam", FieldOfView = 70f, Position = new Vec3( 5, 5, 5 ) };

			var eval = PartTrackEvaluator.Evaluate( clip, 15f, cameras: new[] { defaultDef } );
			var smooth = eval.GetCamera( "SmoothZoom" );
			var defCam = eval.GetCamera( "DefaultCam" );

			Assert( smooth != null && defCam != null );
			Assert( smooth!.FieldOfView > 45f && smooth.FieldOfView < 90f, "Cubic FOV must interpolate" );
			AssertNear( defCam!.FieldOfView, 70f, 1e-3f, "Unkeyed camera preserves base FOV" );
			AssertVecNear( defCam.Position, new Vec3( 5, 5, 5 ), 1e-3f, "Unkeyed camera preserves base position" );
		} );

		// 10. JSON camera definition parsing and opt-in orbit application
		Check( "json camera definition parsing and opt-in orbit application", () =>
		{
			string json = """
			{
				"Cameras": [
					{
						"Name": "ParsedShot",
						"Enabled": true,
						"Export": true,
						"Position": "12.5, 34.5, 56.5",
						"Rotation": "0, 90, 0",
						"FieldOfView": 65.0,
						"ZNear": 5.0,
						"ZFar": 2500.0
					}
				]
			}
			""";

			var parsed = PartTrackEvaluator.ParseCameras( json );
			Assert( parsed.Count == 1, "Must parse 1 camera definition" );
			Assert( parsed[0].Name == "ParsedShot" );
			Assert( parsed[0].Export );
			AssertNear( parsed[0].FieldOfView, 65f, 1e-3f );
			AssertVecNear( parsed[0].Position, new Vec3( 12.5f, 34.5f, 56.5f ), 1e-3f );

			var clip = new RigAnimFile();
			var eval = PartTrackEvaluator.Evaluate( clip, 0f, cameras: parsed );
			var shot = eval.GetCamera( "ParsedShot" );
			Assert( shot != null );
			AssertNear( shot!.FieldOfView, 65f, 1e-3f );

			// Opt-in application to orbit camera
			var orbit = new OrbitCamera { FovDegrees = 50f, Target = Vector3.Zero };
			PartTrackEvaluator.ApplyToOrbitCamera( shot, orbit );

			AssertNear( orbit.FovDegrees, 65f, 1e-3f, "Opt-in must apply FOV to OrbitCamera" );
			Assert( Vector3.Distance( orbit.Target, new Vector3( 12.5f, 34.5f, 56.5f ) ) < 1e-3f, "Opt-in must apply position to OrbitCamera" );
		} );
	}
}
