#nullable enable
using System;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class PoseEditTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "pose-edit", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Skeleton CreateTestSkeleton()
		{
			var rig = new Skeleton();
			// Bone 0: root
			rig.AddBone( "root", -1, Xform.Identity, 5f );

			// Bone 1: parent under root, rotated 90 degrees around Z, scaled 2x, translated (10, 0, 0)
			var rotZ90 = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2f );
			var scale2 = Xform.Scale( new Vec3( 2f, 2f, 2f ) );
			var parentLocal = Xform.Translate( new Vec3( 10, 0, 0 ) ) * rotZ90 * scale2;
			rig.AddBone( "parent", 0, parentLocal, 4f );

			// Bone 2: child under parent, translated (0, 5, 0)
			var childLocal = Xform.Translate( new Vec3( 0, 5, 0 ) );
			rig.AddBone( "child", 1, childLocal, 3f );

			// Bone 3: grandchild under child, translated (0, 3, 0)
			var grandchildLocal = Xform.Translate( new Vec3( 0, 3, 0 ) );
			rig.AddBone( "grandchild", 2, grandchildLocal, 2f );

			return rig;
		}

		(DocumentSession Session, MarionettePlayer Player, Skeleton Rig) CreateFixture( bool startPlaying = false )
		{
			var studio = new PartStudio();
			studio.Add( new PrimitiveFeature { Name = "base_box" } );
			studio.Rebuild();

			var rig = CreateTestSkeleton();
			studio.Rig = rig;

			var session = DocumentSession.CreateEffigy( "test.effigy", studio );
			var player = new MarionettePlayer( rig, startPlaying: startPlaying );
			return (session, player, rig);
		}

		Check( "rotated/scaled parent: child transforms correctly", () =>
		{
			var (session, player, rig) = CreateFixture();
			var startChildWorld = player.WorldPose[2];
			var parentWorld = player.WorldPose[1];

			using var drag = DragSession.BeginPoseDrag( session, "child", player );

			// Desired world translation of child by (5, 10, 15)
			var deltaWorld = new Vec3( 5, 10, 15 );
			drag.UpdatePreviewTranslation( deltaWorld );

			var expectedWorldOrigin = startChildWorld.Origin + deltaWorld;
			Require( (player.WorldPose[2].Origin - expectedWorldOrigin).Length < 1e-4f,
				$"Child world position ({player.WorldPose[2].Origin}) did not match expected ({expectedWorldOrigin}) with scaled/rotated parent." );

			// Desired world rotation of child about its head by 45 degrees around X
			var expectedRot = Xform.RotateAbout( expectedWorldOrigin, new Vec3( 1, 0, 0 ), MathF.PI / 4f );
			var expectedRotWorld = expectedRot * new Xform( startChildWorld.X, startChildWorld.Y, startChildWorld.Z, expectedWorldOrigin );
			drag.UpdatePreviewRotation( new Vec3( 1, 0, 0 ), MathF.PI / 4f );

			// Verify rotation about head keeps origin intact
			Require( (player.WorldPose[2].Origin - startChildWorld.Origin).Length < 1e-4f,
				"Rotation about head must keep child head origin unchanged." );

			// Verify basis matches expected rotation
			var expectedHeadRot = Xform.RotateAbout( startChildWorld.Origin, new Vec3( 1, 0, 0 ), MathF.PI / 4f ) * startChildWorld;
			Require( (player.WorldPose[2].Y - expectedHeadRot.Y).Length < 1e-4f,
				"Child basis must rotate about head in world space." );

			drag.Commit();

			Require( session.History.UndoCount == 1 );
			Require( (player.WorldPose[2].Origin - startChildWorld.Origin).Length < 1e-4f );
		} );

		Check( "root bone edit", () =>
		{
			var (session, player, rig) = CreateFixture();
			var startRootWorld = player.WorldPose[0];
			var startChildWorld = player.WorldPose[1];

			using var drag = DragSession.BeginPoseDrag( session, "root", player );

			var delta = new Vec3( 12, -4, 8 );
			drag.UpdatePreviewTranslation( delta );

			// Root parent is -1, so local == world
			Require( (drag.CurrentLocalPose.Origin - (startRootWorld.Origin + delta)).Length < 1e-4f,
				"Root local transform must equal world transform." );
			Require( (player.WorldPose[0].Origin - (startRootWorld.Origin + delta)).Length < 1e-4f,
				"Root world position must update." );

			// Descendant parent should have moved in world space by delta
			Require( (player.WorldPose[1].Origin - (startChildWorld.Origin + delta)).Length < 1e-4f,
				"Descendant bones must follow root translation in world space." );

			drag.Commit();
			Require( session.History.UndoCount == 1 );
		} );

		Check( "child bones follow parent edit", () =>
		{
			var (session, player, rig) = CreateFixture();
			var startParentWorld = player.WorldPose[1];
			var startChildLocal = dragLocalPose( player, 2 );
			var startGrandchildLocal = dragLocalPose( player, 3 );
			var startChildWorld = player.WorldPose[2];
			var startGrandchildWorld = player.WorldPose[3];

			using var drag = DragSession.BeginPoseDrag( session, "parent", player );

			var delta = new Vec3( 4, 2, -1 );
			drag.UpdatePreviewTranslation( delta );

			// Children local poses must be untouched
			Require( PoseEditService.AreApproximatelyEqual( dragLocalPose( player, 2 ), startChildLocal ),
				"Child local pose must remain untouched when parent is edited." );
			Require( PoseEditService.AreApproximatelyEqual( dragLocalPose( player, 3 ), startGrandchildLocal ),
				"Grandchild local pose must remain untouched when parent is edited." );

			// Children world poses must have updated
			Require( (player.WorldPose[2].Origin - startChildWorld.Origin).Length > 0.1f,
				"Child world pose must follow parent edit." );
			Require( (player.WorldPose[3].Origin - startGrandchildWorld.Origin).Length > 0.1f,
				"Grandchild world pose must follow parent edit." );

			// Verify exact forward kinematics
			var expectedChildWorld = player.WorldPose[1] * startChildLocal;
			Require( (player.WorldPose[2].Origin - expectedChildWorld.Origin).Length < 1e-4f,
				"Child world pose must strictly match Parent.World * Child.Local." );
			var expectedGrandchildWorld = player.WorldPose[2] * startGrandchildLocal;
			Require( (player.WorldPose[3].Origin - expectedGrandchildWorld.Origin).Length < 1e-4f,
				"Grandchild world pose must strictly match Child.World * Grandchild.Local." );

			drag.Commit();
		} );

		Check( "bind pose and inverse-bind matrices remain untouched", () =>
		{
			var (session, player, rig) = CreateFixture();

			// Capture original bind poses
			var origBindLocals = new Xform[rig.Count];
			for ( int b = 0; b < rig.Count; b++ )
				origBindLocals[b] = rig.Bones[b].Local;

			var origInvBinds = PoseEditService.GetInvBindWorld( player ).ToArray();

			using ( var drag = DragSession.BeginPoseDrag( session, "parent", player ) )
			{
				for ( int i = 1; i <= 20; i++ )
				{
					drag.UpdatePreview( new Vec3( i, 0, 0 ), new Vec3( 0, 1, 0 ), i * 0.1f );
				}
				drag.Commit();
			}

			// Verify bind data untouched after commit
			for ( int b = 0; b < rig.Count; b++ )
			{
				Require( PoseEditService.AreApproximatelyEqual( rig.Bones[b].Local, origBindLocals[b] ),
					$"Skeleton.Bones[{b}].Local bind pose was modified by posing!" );
				Require( PoseEditService.AreApproximatelyEqual( PoseEditService.GetInvBindWorld( player )[b], origInvBinds[b] ),
					$"Player inverse-bind matrix for bone {b} was modified!" );
			}

			// Undo and verify again
			session.History.Undo();
			for ( int b = 0; b < rig.Count; b++ )
			{
				Require( PoseEditService.AreApproximatelyEqual( rig.Bones[b].Local, origBindLocals[b] ) );
				Require( PoseEditService.AreApproximatelyEqual( PoseEditService.GetInvBindWorld( player )[b], origInvBinds[b] ) );
			}

			// Redo and verify again
			session.History.Redo();
			for ( int b = 0; b < rig.Count; b++ )
			{
				Require( PoseEditService.AreApproximatelyEqual( rig.Bones[b].Local, origBindLocals[b] ) );
				Require( PoseEditService.AreApproximatelyEqual( PoseEditService.GetInvBindWorld( player )[b], origInvBinds[b] ) );
			}
		} );

		Check( "100 preview updates produce exactly one undo entry", () =>
		{
			var (session, player, rig) = CreateFixture();
			var initialPos = player.WorldPose[1].Origin;

			using ( var drag = DragSession.BeginPoseDrag( session, "parent", player ) )
			{
				for ( int i = 1; i <= 100; i++ )
				{
					drag.UpdatePreviewTranslation( new Vec3( i * 0.1f, 0, 0 ) );
				}
				drag.Commit();
			}

			Require( session.History.UndoCount == 1,
				$"Expected exactly 1 undo entry after 100 preview updates, got {session.History.UndoCount}." );
			Require( session.History.RedoCount == 0 );

			var expectedPos = initialPos + new Vec3( 10f, 0, 0 );
			Require( (player.WorldPose[1].Origin - expectedPos).Length < 1e-4f,
				"Committed position must reflect final preview update." );
		} );

		Check( "cancel restores exact pre-drag pose", () =>
		{
			var (session, player, rig) = CreateFixture( startPlaying: true );
			Require( player.Playing, "player should start playing" );

			var startLocal = dragLocalPose( player, 1 );
			var startWorld = player.WorldPose[1];
			var startChildWorld = player.WorldPose[2];

			using ( var drag = DragSession.BeginPoseDrag( session, "parent", player ) )
			{
				Require( !player.Playing, "drag session must pause playback" );

				drag.UpdatePreview( new Vec3( 10, 20, 30 ), new Vec3( 1, 0, 0 ), 1.2f );

				Require( (player.WorldPose[1].Origin - startWorld.Origin).Length > 1f, "Preview must translate origin." );
				Require( (player.WorldPose[1].X - startWorld.X).Length > 0.5f, "Preview must rotate orientation." );

				drag.Cancel();
			}

			Require( PoseEditService.AreApproximatelyEqual( dragLocalPose( player, 1 ), startLocal ),
				"Cancel must restore exact pre-drag local pose." );
			Require( (player.WorldPose[1].Origin - startWorld.Origin).Length < 1e-5f,
				"Cancel must restore exact pre-drag world pose." );
			Require( (player.WorldPose[2].Origin - startChildWorld.Origin).Length < 1e-5f,
				"Cancel must restore descendant world poses." );

			Require( player.Playing, "Cancel must restore playback state." );
			Require( session.History.UndoCount == 0, "Cancel must not create any history entry." );
			Require( !session.IsDirty, "Cancel must leave session clean with zero dirtying." );
		} );

		Check( "undo and redo restore exact poses", () =>
		{
			var (session, player, rig) = CreateFixture();
			var startLocal = dragLocalPose( player, 1 );
			var startWorld = player.WorldPose[1];

			using ( var drag = DragSession.BeginPoseDrag( session, "parent", player ) )
			{
				drag.UpdatePreview( new Vec3( 3, -2, 5 ), new Vec3( 0, 0, 1 ), 0.5f );
				drag.Commit();
			}

			var committedLocal = dragLocalPose( player, 1 );
			var committedWorld = player.WorldPose[1];

			Require( session.History.UndoCount == 1 );

			// Undo
			session.History.Undo();
			Require( PoseEditService.AreApproximatelyEqual( dragLocalPose( player, 1 ), startLocal ),
				"Undo must restore pre-drag local pose." );
			Require( (player.WorldPose[1].Origin - startWorld.Origin).Length < 1e-5f,
				"Undo must restore pre-drag world pose." );
			Require( (player.WorldPose[1].X - startWorld.X).Length < 1e-5f,
				"Undo must restore pre-drag world orientation." );

			// Redo
			session.History.Redo();
			Require( PoseEditService.AreApproximatelyEqual( dragLocalPose( player, 1 ), committedLocal ),
				"Redo must restore committed local pose." );
			Require( (player.WorldPose[1].Origin - committedWorld.Origin).Length < 1e-5f,
				"Redo must restore committed world pose." );
			Require( (player.WorldPose[1].X - committedWorld.X).Length < 1e-5f,
				"Redo must restore committed world orientation." );
		} );

		Check( "deleted/missing selection handled safely", () =>
		{
			var (session, player, rig) = CreateFixture();

			// Nonexistent bone name
			bool threw = false;
			try
			{
				DragSession.BeginPoseDrag( session, "nonexistent_bone", player );
			}
			catch ( ArgumentException )
			{
				threw = true;
			}
			Require( threw, "BeginPoseDrag on nonexistent bone must throw ArgumentException." );
			Require( session.History.CurrentTransaction == null, "Transaction must not leak on failure." );
			Require( !session.IsDirty, "Session must not be dirty." );

			// TryBeginPoseDrag safe version
			bool ok = PoseEditService.TryBeginPoseDrag( session, "unknown", out var drag, player );
			Require( !ok && drag == null, "TryBeginPoseDrag must return false for unknown bone." );

			// Empty string with no selection
			session.Selection = SessionSelection.Empty;
			threw = false;
			try
			{
				DragSession.BeginPoseDrag( session, "", player );
			}
			catch ( ArgumentException )
			{
				threw = true;
			}
			Require( threw, "BeginPoseDrag with empty string and no selection must throw ArgumentException." );
		} );

		Check( "invalid/NaN transform leaves state intact", () =>
		{
			var (session, player, rig) = CreateFixture();
			var startLocal = dragLocalPose( player, 1 );
			var startWorld = player.WorldPose[1];

			using var drag = DragSession.BeginPoseDrag( session, "parent", player );

			// 1. NaN translation
			bool threw = false;
			try
			{
				drag.UpdatePreviewTranslation( new Vec3( float.NaN, 0, 0 ) );
			}
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "NaN translation must be rejected." );
			Require( PoseEditService.AreApproximatelyEqual( drag.CurrentLocalPose, startLocal ),
				"State must remain intact after rejected NaN translation." );

			// 2. NaN rotation
			threw = false;
			try
			{
				drag.UpdatePreviewRotation( new Vec3( 0, 1, 0 ), float.PositiveInfinity );
			}
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "Infinite rotation angle must be rejected." );
			Require( PoseEditService.AreApproximatelyEqual( drag.CurrentLocalPose, startLocal ) );

			// 3. Zero-length rotation axis
			threw = false;
			try
			{
				drag.UpdatePreviewRotation( Vec3.Zero, 0.5f );
			}
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "Zero-length rotation axis must be rejected." );

			// 4. Singular matrix
			threw = false;
			try
			{
				drag.UpdatePreviewWorldTransform( new Xform( Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero ) );
			}
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "Singular world transform must be rejected." );

			// Clean commit with valid transform works normally afterwards
			drag.UpdatePreviewTranslation( new Vec3( 1, 2, 3 ) );
			drag.Commit();
			Require( session.History.UndoCount == 1 );
		} );

		Check( "no-op edit creates no history entry and leaves dirty state intact", () =>
		{
			var (session, player, rig) = CreateFixture();
			Require( !session.IsDirty );

			// 1. No-op on clean session
			using ( var drag = DragSession.BeginPoseDrag( session, "parent", player ) )
			{
				drag.UpdatePreviewTranslation( Vec3.Zero );
				drag.Commit();
			}

			Require( session.History.UndoCount == 0, "No-op drag must not create history entry." );
			Require( !session.IsDirty, "No-op drag must leave clean session clean." );

			// 2. Make session dirty with another command
			int dummy = 0;
			session.History.Execute( new ValueChangeCommand<int>( "dirty", v => dummy = v, 0, 1 ) );
			Require( session.History.UndoCount == 1 );
			Require( session.IsDirty, "Session should be dirty." );

			// No-op drag on dirty session
			using ( var drag = DragSession.BeginPoseDrag( session, "parent", player ) )
			{
				drag.Commit();
			}

			Require( session.History.UndoCount == 1, "No-op drag must not add another entry." );
			Require( session.IsDirty, "No-op drag must leave dirty state intact." );
		} );
	}

	static Xform dragLocalPose( MarionettePlayer player, int boneIndex )
	{
		return PoseEditService.GetLocalPoses( player )[boneIndex];
	}
}
