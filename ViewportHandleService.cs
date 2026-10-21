#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Logical-pixel rectangle. The framebuffer/DPI scale never enters handle input.</summary>
public readonly record struct HandleViewport(float X, float Y, float Width, float Height)
{
	public bool Contains(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y)
		&& Width > 0 && Height > 0 && point.X >= X && point.Y >= Y
		&& point.X < X + Width && point.Y < Y + Height;
}

public enum HandleShape { AxisSegment, PlaneQuad, RotationRing, CenterSphere }
public enum HandleEditMode { Fk, Ik }
public enum HandleCancelReason { Escape, FocusLost, SourceReload, WindowClose, Error }

/// <summary>Draw specification, also used verbatim by hit testing. Axis segments start at A;
/// quads have corners A,B,C,D; rings use A as centre and Normal as axis.</summary>
public readonly record struct HandleGeometry(GizmoHandleId Id, HandleShape Shape,
	Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 Normal, float Radius, float Thickness,
	bool Hovered, bool Active);

/// <summary>
/// CPU-only G4 phase-one interaction seam. The host draws Geometry, supplies logical pointer
/// coordinates and OrbitCamera.ScreenRay (with coordinates relative to the viewport), and
/// dispatches only when an event returns true. It must capture/release the OS pointer according
/// to WantsPointerCapture; no window or GL state is touched here.
/// </summary>
public sealed class ViewportHandleService : IDisposable
{
	readonly DocumentSession _session;
	readonly MarionettePlayer? _player;
	readonly string _boneName;
	readonly HandleEditMode _mode;
	readonly GizmoConfig _config;
	readonly List<HandleGeometry> _geometry = new();
	DragSession? _pose;
	IkDragSession? _ik;
	Vector3 _startPoint;
	Vector3 _startDirection;
	float _angle;
	long _startRevision;
	RigAnimFile? _startClip;
	bool _moved;

	public ViewportHandleService(DocumentSession session, string boneName, MarionettePlayer? player = null,
		HandleEditMode mode = HandleEditMode.Fk, GizmoConfig? config = null)
	{
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_boneName = !string.IsNullOrWhiteSpace(boneName) ? boneName : throw new ArgumentException("Bone required.", nameof(boneName));
		_player = player;
		_mode = mode;
		_config = config ?? new GizmoConfig();
	}

	public GizmoFrame Frame { get; private set; } = GizmoFrame.World;
	public float Scale { get; private set; }
	public GizmoHandleId Hovered { get; private set; } = GizmoHandleId.None;
	public GizmoHandleId Active { get; private set; } = GizmoHandleId.None;
	public bool WantsPointerCapture => Active.IsValid;
	public IReadOnlyList<HandleGeometry> Geometry => _geometry;
	public string? LastDiagnostic { get; private set; }
	/// <summary>Opt-in keying. Off (default) commits an unkeyed pose override; on commits the
	/// override plus an E3 key for the dragged value in one history entry.</summary>
	public bool AutoKey { get; set; }
	public int KeyFrame { get; set; }
	public bool SnapEnabled { get; set; }
	public float TranslationSnap { get; set; } = 1f;
	public float RotationSnapDegrees { get; set; } = 15f;

	/// <summary>Rebuild on camera/pose/selection changes, not framebuffer resize. The returned
	/// primitives are the sole draw contract; callers must not draw old fixed-size bone axes.</summary>
	public void Rebuild(GizmoFrame frame, float scale, GizmoHandleFilter filter)
	{
		if (WantsPointerCapture) throw new InvalidOperationException("Cannot change handle geometry during capture.");
		if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
		Frame = frame;
		Scale = scale;
		if (_mode == HandleEditMode.Ik) filter &= GizmoHandleFilter.Translate;
		// G2's drag service currently supports translation/rotation, not scale.
		filter &= GizmoHandleFilter.Translate | GizmoHandleFilter.Rotate;
		_geometry.Clear();
		void Axis(GizmoHandleId id, Vector3 direction) => _geometry.Add(new(id, HandleShape.AxisSegment,
			frame.Origin, frame.Origin + direction * scale * _config.AxisLength, default, default,
			direction, scale * _config.AxisHitRadius, 0, id == Hovered, false));
		void Plane(GizmoHandleId id, GizmoPlane plane)
		{
			var (u, v) = frame.GetPlaneAxes(plane);
			float lo = scale * _config.PlaneOffsetMin, hi = scale * _config.PlaneOffsetMax;
			_geometry.Add(new(id, HandleShape.PlaneQuad, frame.Origin + u * lo + v * lo,
				frame.Origin + u * hi + v * lo, frame.Origin + u * hi + v * hi,
				frame.Origin + u * lo + v * hi, frame.GetPlaneNormal(plane), 0, 0, id == Hovered, false));
		}
		void Ring(GizmoHandleId id, Vector3 normal) => _geometry.Add(new(id, HandleShape.RotationRing,
			frame.Origin, default, default, default, normal, scale * _config.RingRadius,
			scale * _config.RingHitThickness, id == Hovered, false));
		foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
		{
			if ((filter & GizmoHandleFilter.TranslateAxes) != 0) Axis(new(GizmoHandleKind.TranslateAxis, axis), frame.GetAxis(axis));
			if ((filter & GizmoHandleFilter.RotateRings) != 0) Ring(new(GizmoHandleKind.RotateRing, axis), frame.GetAxis(axis));
		}
		if ((filter & GizmoHandleFilter.TranslatePlanes) != 0)
			foreach (var plane in new[] { GizmoPlane.XY, GizmoPlane.YZ, GizmoPlane.XZ })
				Plane(new(GizmoHandleKind.TranslatePlane, GizmoAxis.None, plane), plane);
		// Scale is deliberately absent: IK has no scale target and G2 does not expose scale-delta semantics.
	}

	public GizmoHitResult HitTest(Ray ray)
	{
		var best = GizmoHitResult.Miss;
		foreach (var g in _geometry)
		{
			float distance = 0; Vector3 point = default;
			bool hit = g.Shape switch
			{
				HandleShape.AxisSegment => GizmoMath.TryHitAxis(ray, Frame, g.Id.Axis, Scale,
					_config.AxisLength, _config.AxisHitRadius, out distance, out point, _config.ParallelDotThreshold),
				HandleShape.PlaneQuad => GizmoMath.TryHitPlane(ray, Frame, g.Id.Plane, Scale,
					_config.PlaneOffsetMin, _config.PlaneOffsetMax, out distance, out point, _config.ParallelDotThreshold),
				HandleShape.RotationRing => GizmoMath.TryHitRing(ray, Frame, g.Id.Axis, Scale,
					_config.RingRadius, _config.RingHitThickness, out distance, out point, _config.ParallelDotThreshold),
				_ => false
			};
			if (hit && distance > 0 && distance < best.RayDistance)
				best = new(true, g.Id, distance, point);
		}
		return best;
	}

	public bool Hover(Vector2 logicalPoint, HandleViewport viewport, Ray ray)
	{
		if (WantsPointerCapture) return true;
		Hovered = viewport.Contains(logicalPoint) ? HitTest(ray).HandleId : GizmoHandleId.None;
		RefreshStates();
		return Hovered.IsValid;
	}

	/// <summary>Return true only when this press takes precedence over orbit/body selection.</summary>
	public bool Press(Vector2 logicalPoint, HandleViewport viewport, Ray ray)
	{
		if (WantsPointerCapture) return true;
		LastDiagnostic = null;
		if (!viewport.Contains(logicalPoint)) return false;
		var hit = HitTest(ray);
		if (!hit.IsHit) return false;
		// G4 item 4: auto-key is opt-in. With it off the drag is a pure unkeyed pose override;
		// with it on the commit also writes a key through E3, atomically in one undo entry.
		// Fail closed before capture when the key cannot be written.
		if (AutoKey)
		{
			if (KeyFrame < 0) { LastDiagnostic = "Auto-key needs a non-negative key frame."; return true; }
			if (_session.Clip is null) { LastDiagnostic = "Auto-key needs a document animation clip."; return true; }
		}
		if (SnapEnabled && (hit.HandleId.Kind == GizmoHandleKind.RotateRing
			? !float.IsFinite(RotationSnapDegrees) || RotationSnapDegrees <= 0
			: !float.IsFinite(TranslationSnap) || TranslationSnap <= 0))
		{
			LastDiagnostic = "Snap step must be positive and finite."; return true;
		}
		if (!Project(ray, hit.HandleId, out _startPoint)) { LastDiagnostic = "No stable drag projection."; return true; }
		try
		{
			if (_mode == HandleEditMode.Ik)
			{
				if (hit.HandleId.Kind == GizmoHandleKind.RotateRing) { LastDiagnostic = "IK rotation target dragging is not supported."; return true; }
				if (!IkTargetService.TryBeginIkDrag(_session, _boneName, out _ik, out var error, _player))
				{ LastDiagnostic = error; return true; }
			}
			else if (!PoseEditService.TryBeginPoseDrag(_session, _boneName, out _pose, _player))
			{ LastDiagnostic = "Cannot start pose drag for the selected bone."; return true; }
			_startRevision = _session.Revision;
			_startClip = _session.Clip;
			_startDirection = Vector3.Normalize(_startPoint - Frame.Origin);
			_angle = 0;
			_moved = false;
			Active = Hovered = hit.HandleId;
			RefreshStates();
			return true;
		}
		catch (Exception ex)
		{
			Cancel(HandleCancelReason.Error);
			LastDiagnostic = ex.Message;
			return true;
		}
	}

	public bool Move(Vector2 logicalPoint, HandleViewport viewport, Ray ray)
	{
		if (!WantsPointerCapture) return Hover(logicalPoint, viewport, ray);
		if (!SourceUnchanged()) { Cancel(HandleCancelReason.SourceReload); return true; }
		if (!Project(ray, Active, out var point)) return true; // keep last finite preview
		try
		{
			if (Active.Kind == GizmoHandleKind.RotateRing)
			{
				float raw = GizmoMath.ComputeSignedAngleOnPlane(_startDirection, point - Frame.Origin, Frame.GetAxis(Active.Axis));
				float next = GizmoMath.UnwrapAngle(raw, _angle);
				_angle = next;
				float radians = GizmoMath.SnapRotation(next, RotationSnapDegrees, SnapEnabled);
				_pose!.UpdatePreviewRotation(Frame.GetAxis(Active.Axis), radians);
			}
			else
			{
				var delta = point - _startPoint;
				delta = GizmoMath.SnapTranslation(Frame.Origin + delta, Frame.Origin,
					TranslationSnap, Frame, SnapEnabled) - Frame.Origin;
				if (_ik != null && !_ik.UpdatePreviewTranslationDelta(delta)) return true;
				_pose?.UpdatePreviewTranslation(delta);
			}
			_moved = true;
		}
		catch (Exception ex) { Cancel(HandleCancelReason.Error); LastDiagnostic = ex.Message; }
		return true;
	}

	public bool Release(Vector2 logicalPoint, HandleViewport viewport, Ray ray)
	{
		if (!WantsPointerCapture) return false;
		if (!SourceUnchanged()) { Cancel(HandleCancelReason.SourceReload); return true; }
		Move(logicalPoint, viewport, ray);
		if (!WantsPointerCapture) return true;
		try
		{
			if (_moved)
			{
				JoinAutoKey();
				_pose?.Commit();
				_ik?.Commit();
			}
			else { _pose?.Cancel(); _ik?.Cancel(); }
		}
		finally { ClearCapture(); }
		return true;
	}

	/// <summary>G4 item 4: with auto-key deliberately enabled, the commit also writes keys through
	/// E3 so the dragged value survives clip evaluation. FK writes a local bone key for the posed
	/// bone; live IK writes the end bone's goal and weight keys. Both join the drag's own history
	/// transaction, so the pose and the key undo together as one entry.</summary>
	void JoinAutoKey()
	{
		if (!AutoKey || _session.Clip is null) return;

		if (_ik is not null)
		{
			var rotInModel = _ik.CurrentTargetRotationInModel ?? Quat.FromXform(_ik.CurrentEndBoneWorldPose);
			var targets = new[]
			{
				new ClipKeys.Target(KeyFrame, _ik.CurrentTargetInGoalSpace, ToGoalSpace(rotInModel), RigEase.Smooth, 1f)
			};
			_ik.JoinCommand(ClipEditService.BuildSetIkGoals(_session, _ik.BoneName, targets, _player));
			return;
		}

		if (_pose is not null)
		{
			_pose.JoinCommand(ClipEditService.BuildInsertOrReplaceKey(_session, RigTrackKind.Bone,
				_pose.BoneName, KeyFrame, RigPose.FromXform(_pose.CurrentLocalPose), RigEase.Smooth,
				null, null, _player));
		}
	}

	/// <summary>The IK drag reports its goal rotation in model space; the goal key is written in the
	/// constraint's declared goal space, exactly as the position already is.</summary>
	Quat ToGoalSpace(Quat rotationInModel)
	{
		if (_ik is null || _ik.GoalSpace == RigGoalSpace.Model) return rotationInModel;
		var asTransform = IkTargetService.ModelToGoalSpace(rotationInModel.ToXform(Vec3.Zero, Vec3.One),
			_ik.GoalSpace, _ik.AnchorTransform, _ik.ModelInWorld);
		return Quat.FromXform(asTransform);
	}

	public void Cancel(HandleCancelReason reason)
	{
		try { _pose?.Cancel(); _ik?.Cancel(); }
		finally { ClearCapture(); }
		LastDiagnostic = reason.ToString();
	}

	public void Dispose() => Cancel(HandleCancelReason.WindowClose);

	bool SourceUnchanged() => _session.Revision == _startRevision && ReferenceEquals(_session.Clip, _startClip)
		&& PoseEditService.ResolveSkeleton(_session, _player)?.IndexOf(_boneName) >= 0;

	bool Project(Ray ray, GizmoHandleId id, out Vector3 point)
	{
		point = default;
		bool projected = id.Kind switch
		{
			GizmoHandleKind.TranslateAxis => GizmoMath.TryProjectRayOnAxis(ray, Frame.Origin,
				Frame.GetAxis(id.Axis), out point, out _, _config.ParallelDotThreshold),
			GizmoHandleKind.TranslatePlane => GizmoMath.TryProjectRayOnPlane(ray, Frame.Origin,
				Frame.GetPlaneNormal(id.Plane), out point, _config.ParallelDotThreshold),
			GizmoHandleKind.RotateRing => GizmoMath.TryProjectRayOnPlane(ray, Frame.Origin,
				Frame.GetAxis(id.Axis), out point, _config.ParallelDotThreshold)
				&& (point - Frame.Origin).LengthSquared() > 1e-8f,
			_ => false
		};
		return projected && ray.IsFinite && Vector3.Dot(point - ray.Origin, ray.Direction) >= 0
			&& float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
	}

	void ClearCapture()
	{
		_pose?.Dispose(); _ik?.Dispose();
		_pose = null; _ik = null;
		Active = GizmoHandleId.None;
		RefreshStates();
	}

	void RefreshStates()
	{
		for (int i = 0; i < _geometry.Count; i++)
			_geometry[i] = _geometry[i] with { Hovered = _geometry[i].Id == Hovered,
				Active = _geometry[i].Id == Active };
	}
}
