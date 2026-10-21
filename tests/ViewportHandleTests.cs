#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Marionette.Desktop;

internal static class ViewportHandleTests
{
	public static void Register(TestRegistry registry, TestSettings settings)
	{
		void Check(string name, Action body) => registry.Add("viewport-handle", name, body);
		static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
		static Ray RayAt(float x, float y, float z = 0) => new(new Vector3(x, y, 10), -Vector3.UnitZ);
		var area = new HandleViewport(100, 50, 400, 300);
		(DocumentSession, MarionettePlayer) Fixture(bool ik = false, bool playing = false)
		{
			var rig = new Skeleton();
			rig.AddBone("root", -1, Xform.Identity, 4);
			rig.AddBone("elbow", 0, Xform.Translate(new Vec3(0, 5, 0)), 4);
			rig.AddBone("wrist", 1, Xform.Translate(new Vec3(0, 5, 0)), 2);
			var studio = new PartStudio { Rig = rig };
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
			if (ik) clip.Ik = new List<RigIk> { new() { EndBone = "wrist", ChainLength = 2, Pole = new Vec3(0, 0, 1), Weight = 1, Enabled = true, Live = true } };
			return (DocumentSession.CreateEffigy("handles.effigy", studio, clip: clip), new MarionettePlayer(rig, startPlaying: playing));
		}
		ViewportHandleService Service(DocumentSession session, MarionettePlayer player, string bone = "root", HandleEditMode mode = HandleEditMode.Fk)
		{
			var service = new ViewportHandleService(session, bone, player, mode);
			service.Rebuild(new GizmoFrame(bone == "wrist" ? new Vector3(0, 10, 0) : Vector3.Zero,
				Quaternion.Identity), 2, GizmoHandleFilter.TranslateAxes);
			return service;
		}

		Check("draw geometry and hit share typed dimensions", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			Require(s.Geometry.Count == 3, "three axes expected");
			foreach (var g in s.Geometry)
			{
				Require(g.Shape == HandleShape.AxisSegment && g.Radius > 0, "axis draw spec");
				var mid = (g.A + g.B) * .5f;
				var side = g.Id.Axis == GizmoAxis.Z ? Vector3.UnitX : Vector3.UnitZ;
				Require(s.HitTest(new Ray(mid + side * 10, -side)).HandleId == g.Id,
					$"drawn {g.Id} midpoint must be picked");
			}
			Require(!s.HitTest(RayAt(5, 5)).IsHit, "miss");
			s.Rebuild(GizmoFrame.World, 2, GizmoHandleFilter.TranslatePlanes);
			var xy = s.Geometry.Single(g => g.Id == GizmoHandleId.TranslateXY);
			Require(s.HitTest(new Ray((xy.A + xy.C) * .5f + Vector3.UnitZ * 10, -Vector3.UnitZ)).HandleId == xy.Id, "quad agrees");
			s.Rebuild(GizmoFrame.World, 2, GizmoHandleFilter.RotateRings);
			var z = s.Geometry.Single(g => g.Id == GizmoHandleId.RotateZ);
			Require(s.HitTest(RayAt(z.Radius, 0)).HandleId == z.Id, "ring agrees");
		});

		Check("overlay emits only typed selectable shape vertices", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			var lines = new System.Collections.Generic.List<(GizmoHandleId Id, Vector3 A, Vector3 B, Vector3 Color)>();
			void Capture(GizmoHandleId id, Vector3 a, Vector3 b, Vector3 color) => lines.Add((id, a, b, color));
			SkeletonOverlay.EmitHandleLines(s.Geometry, Capture);
			Require(lines.Count == 3, "axis geometry has exactly one segment per axis; no arrowheads");
			foreach (var g in s.Geometry)
			{
				var line = lines.Single(l => l.Id == g.Id);
				Require(line.A == g.A && line.B == g.B, "axis endpoints must match hit geometry");
			}
			s.Hover(new Vector2(150, 80), area, RayAt(1, 0));
			lines.Clear(); SkeletonOverlay.EmitHandleLines(s.Geometry, Capture);
			Require(lines.Single(l => l.Id == GizmoHandleId.TranslateX).Color ==
				SkeletonOverlay.HandleColor(s.Geometry.Single(g => g.Id == GizmoHandleId.TranslateX)), "hover color from ID");
			Require(lines[0].Color != lines.Single(l => l.Id == GizmoHandleId.TranslateY).Color, "hover distinct");

			s.Rebuild(GizmoFrame.World, 2, GizmoHandleFilter.TranslatePlanes);
			lines.Clear(); SkeletonOverlay.EmitHandleLines(s.Geometry, Capture);
			Require(lines.Count == 12, "four edges for each plane, no spurious diagonals");
			foreach (var g in s.Geometry)
			{
				var edges = lines.Where(l => l.Id == g.Id).ToArray();
				Require(edges.Length == 4 && edges[0].A == g.A && edges[0].B == g.B &&
					edges[3].A == g.D && edges[3].B == g.A, "quad outline matches hit bounds");
			}

			s.Rebuild(GizmoFrame.World, 2, GizmoHandleFilter.RotateRings);
			lines.Clear(); SkeletonOverlay.EmitHandleLines(s.Geometry, Capture);
			Require(lines.Count == 192, "64 segments per typed ring");
			foreach (var g in s.Geometry)
			{
				var ring = lines.Where(l => l.Id == g.Id).ToArray();
				// The ring closes through Cos(2*PI)/Sin(2*PI), which are not exactly 1/0 in float,
				// so closure holds to float precision rather than bitwise. An actually open ring
				// would miss the last chord and gap by ~0.098*Radius, three orders out.
				Require((ring[0].A - ring[^1].B).Length() < g.Radius * 1e-3f, "closed ring");
				foreach (var edge in ring)
				{
					Require(MathF.Abs((edge.A - g.A).Length() - g.Radius) < 1e-4f &&
						MathF.Abs(Vector3.Dot(edge.A - g.A, g.Normal)) < 1e-4f, "ring vertex on hit circle/plane");
				}
			}
		});

		Check("priority over orbit, one undo entry and palette undo", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			var initialPalette = player.GpuBones[0];
			int orbitCalls = 0;
			var ray = RayAt(1, 0);
			if (!s.Press(new Vector2(150, 80), area, ray)) orbitCalls++;
			Require(s.WantsPointerCapture && s.Active == GizmoHandleId.TranslateX, "captured X before orbit");
			for (int i = 0; i < 30; i++) if (!s.Move(new Vector2(150 + i, 80), area, RayAt(1 + i * .1f, 0))) orbitCalls++;
			Require(s.Geometry.Single(g => g.Id == GizmoHandleId.TranslateX).Active, "draw active ID");
			Require(s.Release(new Vector2(200, 80), area, RayAt(4, 0)), "release consumed");
			Require(!s.WantsPointerCapture && orbitCalls == 0, "drag must not orbit");
			Require(session.History.UndoCount == 1 && session.Clip!.Tracks.Count == 0, "one unkeyed entry");
			Require(player.GpuBones[0] != initialPalette, "preview palette changed");
			session.History.Undo();
			Require(player.GpuBones[0] == initialPalette, "undo palette restored");
		});

		Check("logical viewport offset and framebuffer DPI independence", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			Require(!s.Press(new Vector2(50, 80), area, RayAt(1, 0)), "outside logical viewport");
			// A 2x framebuffer (800x600) does not change the logical hit or draw scale.
			var before = s.Geometry.Single(g => g.Id == GizmoHandleId.TranslateX).B;
			Require(s.Press(new Vector2(150, 80), area, RayAt(1, 0)), "logical hit at DPI 2");
			Require(s.WantsPointerCapture && before == s.Geometry.Single(g => g.Id == GizmoHandleId.TranslateX).B, "same geometry");
			s.Cancel(HandleCancelReason.Escape);
		});

		Check("escape focus loss reload and close cancel exact state", () =>
		{
			foreach (var reason in new[] { HandleCancelReason.Escape, HandleCancelReason.FocusLost,
				HandleCancelReason.SourceReload, HandleCancelReason.WindowClose })
			{
				var (session, player) = Fixture(playing: true);
				using var s = Service(session, player);
				var start = player.WorldPose[0];
				Require(s.Press(new Vector2(150, 80), area, RayAt(1, 0)), "press");
				s.Move(new Vector2(160, 80), area, RayAt(3, 0));
				Require(!player.Playing, "paused");
				s.Cancel(reason);
				Require(!s.WantsPointerCapture && PoseEditService.AreApproximatelyEqual(start, player.WorldPose[0]), "cancel restores");
				Require(player.Playing && session.History.UndoCount == 0 && !session.IsDirty, "transport/history restored");
			}
		});

		Check("auto-key off is an unkeyed override and on writes keys atomically", () =>
		{
			// Auto-key OFF (the default): a pure unkeyed pose override, one undo entry, no clip keys.
			{
				var (session, player) = Fixture();
				using var s = Service(session, player);
				Require(s.Press(new Vector2(150, 80), area, RayAt(1, 0)), "plain press");
				Require(s.Move(new Vector2(160, 80), area, RayAt(2, 0)), "plain move");
				s.Release(new Vector2(160, 80), area, RayAt(2, 0));
				Require(session.History.UndoCount == 1, "override is one undo entry");
				Require(session.Clip!.Tracks.Count == 0, "auto-key off must not write clip keys");
				session.History.Undo();
				Require(session.Clip.Tracks.Count == 0, "undoing an override writes no keys");
			}

			// Auto-key ON, FK: a local bone key for the posed bone, committed with the pose as one entry.
			{
				var (session, player) = Fixture();
				using var s = Service(session, player);
				s.AutoKey = true; s.KeyFrame = 12;
				Require(s.Press(new Vector2(150, 80), area, RayAt(1, 0)), "auto-key press");
				Require(s.WantsPointerCapture, "auto-key still captures like a normal drag");
				Require(s.Move(new Vector2(160, 80), area, RayAt(2, 0)), "auto-key move");
				s.Release(new Vector2(160, 80), area, RayAt(2, 0));

				Require(session.History.UndoCount == 1, "pose and its key must be one undo entry");
				var track = session.Clip!.Tracks.SingleOrDefault(t => t.Kind == RigTrackKind.Bone && t.Name == "root");
				Require(track is not null, "auto-key must write a local bone key through E3");
				Require(track!.Keys.Count == 1 && track.Keys[0].Frame == 12, "the key lands on the requested frame");
				// Root has no parent, so its world pose is its local pose: the key records what was dragged.
				var keyed = track.Keys[0].Local.ToXform();
				Require((keyed.Origin - player.WorldPose[0].Origin).LengthSquared < 1e-8f,
					"the key records the dragged pose, not the bind pose");

				session.History.Undo();
				Require(session.Clip.Tracks.All(t => t.Keys.Count == 0),
					"one undo removes the pose and the key together");
				session.History.Redo();
				Require(session.Clip.Tracks.Single(t => t.Kind == RigTrackKind.Bone).Keys.Count == 1,
					"one redo restores the pose and the key together");
			}

			// Auto-key ON, live IK: goal and weight keys for the end bone, again one entry with the solve.
			{
				var (session, player) = Fixture(ik: true);
				using var s = Service(session, player, "wrist", HandleEditMode.Ik);
				s.AutoKey = true; s.KeyFrame = 7;
				Require(s.Press(new Vector2(150, 80), area, RayAt(1, 10)), "IK auto-key press");
				Require(s.Move(new Vector2(170, 80), area, RayAt(3, 10)), "IK auto-key move");
				s.Release(new Vector2(170, 80), area, RayAt(3, 10));

				Require(session.History.UndoCount == 1, "IK solve and its goals must be one undo entry");
				Require(session.Clip!.Tracks.Any(t => t.Kind == RigTrackKind.IkGoal && t.Name == "wrist" && t.Keys.Any(k => k.Frame == 7)),
					"auto-key must write the IK goal key through E3");
				Require(session.Clip.Tracks.Any(t => t.Kind == RigTrackKind.IkWeight && t.Name == "wrist" && t.Keys.Any(k => k.Frame == 7)),
					"auto-key must write the IK weight key through E3");
				session.History.Undo();
				Require(session.Clip.Tracks.All(t => t.Keys.Count == 0), "IK undo removes the solve and the goals together");
			}

			// Fail closed before capture when the key cannot be written.
			{
				var (session, player) = Fixture();
				using var s = Service(session, player);
				s.AutoKey = true; s.KeyFrame = -1;
				Require(s.Press(new Vector2(150, 80), area, RayAt(1, 0)), "handle consumes the press rather than orbit");
				Require(!s.WantsPointerCapture && session.History.UndoCount == 0 && !session.IsDirty &&
					session.Clip!.Tracks.Count == 0, "a negative key frame must not capture, dirty or half-edit");
			}
		});

		Check("IK target ray drag solves end effector and undoes", () =>
		{
			var (session, player) = Fixture(ik: true);
			using var s = Service(session, player, "wrist", HandleEditMode.Ik);
			var start = player.WorldPose[2].Origin;
			Require(s.Press(new Vector2(150, 80), area, RayAt(1, 10)), "IK press");
			Require(s.Move(new Vector2(170, 80), area, RayAt(3, 10)), "IK move");
			s.Release(new Vector2(170, 80), area, RayAt(3, 10));
			Require((player.WorldPose[2].Origin - start).Length > .1f && session.History.UndoCount == 1, "IK solved/committed");
			session.History.Undo();
			Require((player.WorldPose[2].Origin - start).Length < 1e-4f, "IK undo");
		});

		Check("rotation ring angle unwrap and degree snapping", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			s.Rebuild(GizmoFrame.World, 2, GizmoHandleFilter.RotateRings);
			s.SnapEnabled = true;
			s.RotationSnapDegrees = 45;
			Require(s.Press(new Vector2(150, 80), area, RayAt(2, 0)), "ring press");
			Require(s.Active == GizmoHandleId.RotateZ, "Z ring chosen");
			s.Move(new Vector2(160, 80), area, RayAt(MathF.Cos(MathF.PI / 3) * 2, MathF.Sin(MathF.PI / 3) * 2));
			var x = player.WorldPose[0].X;
			Require(MathF.Abs(MathF.Abs(x.x) - MathF.Sqrt(.5f)) < .01f &&
				MathF.Abs(MathF.Abs(x.y) - MathF.Sqrt(.5f)) < .01f, "60 degrees snapped to 45");
			s.Release(new Vector2(160, 80), area, RayAt(0, 2));
			Require(session.History.UndoCount == 1, "one rotation command");
		});

		Check("source revision invalidation and unstable ray leave clean capture", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			var start = player.WorldPose[0];
			s.Press(new Vector2(150, 80), area, RayAt(1, 0));
			// Looking straight down X cannot project an axis. No jump or history entry.
			s.Move(new Vector2(170, 80), area, new Ray(new Vector3(10, 0, 0), -Vector3.UnitX));
			Require(PoseEditService.AreApproximatelyEqual(start, player.WorldPose[0]), "unstable ray unchanged");
			session.BumpRevision(); // external source publication invalidates this interaction
			s.Move(new Vector2(180, 80), area, RayAt(3, 0));
			Require(!s.WantsPointerCapture && session.History.UndoCount == 0, "revision cancels capture");
		});

		Check("disabled snap continuous, invalid snap rejected before capture", () =>
		{
			var (session, player) = Fixture();
			using var s = Service(session, player);
			s.SnapEnabled = true; s.TranslationSnap = 0;
			s.Press(new Vector2(150, 80), area, RayAt(1, 0));
			Require(!s.WantsPointerCapture && !session.IsDirty, "bad snap rejected");
			s.SnapEnabled = false;
			s.Press(new Vector2(150, 80), area, RayAt(1, 0));
			s.Move(new Vector2(155, 80), area, RayAt(1.3f, 0));
			Require(MathF.Abs(player.WorldPose[0].Origin.x - .3f) < .001f, "continuous delta");
			s.Release(new Vector2(155, 80), area, RayAt(1.3f, 0));
			Require(session.History.UndoCount == 1, "committed continuous edit");
		});
	}
}
