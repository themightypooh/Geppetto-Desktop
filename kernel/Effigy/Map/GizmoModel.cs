// Hammer gizmo + pivot tool model (state + math only, no rendering, no UI).
// Board task: hammer-gizmo-pivot-tool-model (worker muse-3-desktop-hammer-32).
//
// What this file is: the tool model a future viewport/AI caller drives — mode
// (translate/rotate/scale, Ctrl+J cycles it), space (global/local), axis lock
// (X/Y/Z/plane), adjustable pivot (position + rotation centre, Ctrl+P story;
// Paste Special consumes the same pivot via the MapPaste sibling), snap-on-rotate
// and grid snap, and drag semantics as pure math: DragToTransform(drag, view)
// produces the transform for one drag. No rendering, no panels, no Desktop UI.
// Pure math on System.Numerics types; the kernel stays engine-free.
//
// GizmoMath reconciliation (same situation as MapTransform): the ticket says
// "on GizmoMath types", but GizmoMath lives in Marionette.Desktop (GizmoMath.cs,
// namespace Marionette.Desktop) and the kernel cannot reference it (RULES #8:
// never introduce desktop types into the engine-free kernel). GizmoMath.cs also
// defines NO GizmoState type — it has GizmoAxis/GizmoPlane/GizmoHandleId/
// GizmoFrame/Ray/GizmoMath only. So this file is built on System.Numerics — the
// exact types GizmoMath itself is built on (Vector3, Quaternion, Matrix4x4) —
// and mirrors GizmoMath's vocabulary 1:1 so a desktop caller maps across
// without guessing:
//   GizmoAxisLock.None/X/Y/Z/XY/YZ/XZ  <->  GizmoAxis.X/Y/Z + GizmoPlane.XY/YZ/XZ
//   GizmoModel.SelectionOrientation     <->  GizmoFrame(origin, orientation).AxisX/Y/Z
//   GizmoView.Right/Up/Forward          <->  the camera basis GizmoMath hit-tests use
// GizmoMath.cs itself is untouched (reuse, not edit).
//
// MapTransform reconciliation: the dependency (hammer-brush-transform-grid-snap-api,
// worker muse-3-desktop-hammer-04) HAS landed on disk as
// kernel/Effigy/Map/MapTransform.cs with exactly the API its desc specifies
// (SnapToGrid, SnapAngle, TranslationMatrix, RotationMatrix, ScaleMatrix,
// ComposeMatrix, ConstrainToAxis, ConstrainToPlane). DragToTransform returns a
// Matrix4x4 built through those helpers — "DragToTransform(drag, view) ->
// MapTransform" in the desc means the transform is produced via the MapTransform
// API (MapTransform is a static class, so it cannot itself be a return type).
// The caller applies the matrix with MapTransform.Transform / TransformSelection.
//
// Drag semantics (stateless pure function; one call = one drag gesture):
//   * A GizmoDrag carries a displacement in VIEW space: X along the view Right,
//     Y along the view Up, Z along the view Forward. GizmoDrag.FromScreen maps a
//     2D screen drag (Y down) to view space at a caller-supplied world-per-pixel
//     scale. The view converts it to a world-space drag vector.
//   * Translate: the world drag is constrained to the locked axis (MapTransform.
//     ConstrainToAxis) or plane (ConstrainToPlane) in the ACTIVE frame — global
//     axes are world axes, local axes are SelectionOrientation-rotated axes —
//     then, when snapping is on, each delta component is snapped to GridSize
//     (the drag is assumed to start on-grid, as a gizmo drag does; a caller
//     needing absolute position snapping applies MapTransform.SnapToGrid to
//     Pivot + delta). Translation ignores the pivot (MapTransform: a pure
//     translation does not need one).
//   * Rotate: the angle is the signed angle of the world drag projected onto
//     the plane perpendicular to the rotation axis, measured from that plane's
//     U axis toward V (right-hand rule about the axis). The rotation axis is
//     the locked axis in the active frame; a plane lock rotates about the
//     plane normal (XY -> Z, YZ -> X, XZ -> Y); with no lock the drag rotates
//     about the view Forward axis (screen-plane rotation). A drag with no
//     component in the rotation plane gives angle 0. When snapping is on the
//     angle is snapped to AngleSnapDegrees via MapTransform.SnapAngle.
//     Rotation happens about the pivot (MapTransform.RotationMatrix).
//   * Scale: one world unit of drag along an axis changes that axis by a factor
//     of 1.0 (drag +0.5 along X scales X by 1.5; drag -0.5 scales X by 0.5).
//     An axis lock scales only that axis, a plane lock scales the plane's two
//     axes, no lock scales uniformly by 1 + |drag|. Factors are clamped to a
//     minimum of 1e-4 (GizmoMath.ClampScale) so a drag through the pivot never
//     produces a zero or negative scale. Scaling happens about the pivot.
//   * A zero drag returns the identity matrix in every mode.
//
// Immutability: Set* methods mutate the model's state (it IS the tool state);
// DragToTransform is pure and never mutates state. GizmoState is an immutable
// snapshot of the model at one moment.
//
// Persistence (hammer-gizmo-state-persistence, worker muse-3-desktop-hammer-47):
// Save/Load live on GizmoModel. There is NO MapDocument class on disk yet
// (hammer-12's map-document file has not landed; grep finds no MapDocument
// type anywhere in kernel/tests), so the document API is defined against the
// map document's JSON object — the same JsonObject pattern FaceMaterial uses
// for its ToJson/FromJson round trip. SaveToDocument writes the "gizmo"
// section into the document object; LoadFromDocument reads it back. A future
// MapDocument embeds that section verbatim under the "gizmo" key. The section
// carries schemaVersion 1; loading is a migration, never a crash: a missing
// section is a fresh document (silent defaults); an unknown field, an unknown
// enum value, a newer schema version, or an invalid value falls back to the
// default for that field with a NAMED warning (GizmoLoadResult.Warnings) and
// the unknown field's raw value is preserved and re-emitted by SaveState, so
// a save/load round trip never silently drops a newer writer's data
// (CONTRACTS save contract: keep unrecognized fields on round trip).
// GizmoHealthCheck proves the always-available contract: a fresh model, and
// a model loaded from a corrupted section, both pass with a valid default
// state — the gizmo is never missing/null after an update.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>Gizmo tool mode: what a drag does. Ctrl+J cycles these.</summary>
public enum GizmoMode
{
	Translate = 0,
	Rotate,
	Scale,
}

/// <summary>
/// Gizmo coordinate space: Global uses world axes; Local uses the selection's
/// orientation (SelectionOrientation) rotated axes.
/// </summary>
public enum GizmoSpace
{
	Global = 0,
	Local,
}

/// <summary>
/// Axis/plane lock, mirroring GizmoMath's GizmoAxis (X/Y/Z) + GizmoPlane
/// (XY/YZ/XZ). A plane lock means "move/scale inside this plane" for translate
/// and scale, and "rotate about this plane's normal" for rotate.
/// </summary>
public enum GizmoAxisLock
{
	None = 0,
	X,
	Y,
	Z,
	XY,
	YZ,
	XZ,
}

/// <summary>
/// Immutable snapshot of a GizmoModel's state at one moment.
/// </summary>
public readonly struct GizmoState
{
	public GizmoMode Mode { get; }
	public GizmoSpace Space { get; }
	public GizmoAxisLock LockedAxis { get; }
	public Vector3 Pivot { get; }
	public Quaternion SelectionOrientation { get; }
	public bool SnapEnabled { get; }
	public double GridSize { get; }
	public double AngleSnapDegrees { get; }

	public GizmoState(
		GizmoMode mode,
		GizmoSpace space,
		GizmoAxisLock lockedAxis,
		Vector3 pivot,
		Quaternion selectionOrientation,
		bool snapEnabled,
		double gridSize,
		double angleSnapDegrees)
	{
		Mode = mode;
		Space = space;
		LockedAxis = lockedAxis;
		Pivot = pivot;
		SelectionOrientation = selectionOrientation;
		SnapEnabled = snapEnabled;
		GridSize = gridSize;
		AngleSnapDegrees = angleSnapDegrees;
	}
}

/// <summary>
/// One drag gesture, expressed as a displacement in view space: X along the
/// view Right, Y along the view Up, Z along the view Forward (all world units).
/// </summary>
public readonly struct GizmoDrag
{
	public Vector3 ViewDelta { get; }

	public GizmoDrag(Vector3 viewDelta)
	{
		if (!IsFinite(viewDelta))
			throw new ArgumentException("Drag delta must be finite.", nameof(viewDelta));
		ViewDelta = viewDelta;
	}

	/// <summary>
	/// Build a drag from a 2D screen displacement (pixels; screen Y grows
	/// downward, so a downward drag is negative view-Up) at a caller-supplied
	/// world-units-per-pixel scale (world size of one pixel at the pivot depth).
	/// </summary>
	public static GizmoDrag FromScreen(Vector2 screenDelta, float worldUnitsPerPixel)
	{
		if (!float.IsFinite(screenDelta.X) || !float.IsFinite(screenDelta.Y))
			throw new ArgumentException("Screen delta must be finite.", nameof(screenDelta));
		if (!float.IsFinite(worldUnitsPerPixel) || worldUnitsPerPixel <= 0f)
			throw new ArgumentOutOfRangeException(nameof(worldUnitsPerPixel), "World units per pixel must be positive and finite.");
		return new GizmoDrag(new Vector3(
			screenDelta.X * worldUnitsPerPixel,
			-screenDelta.Y * worldUnitsPerPixel,
			0f));
	}

	static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>
/// Camera basis in world space, orthonormalized on construction to a
/// right-handed basis with Up = Right x Forward. Presets are right-handed view bases.
/// </summary>
public readonly struct GizmoView
{
	public Vector3 Right { get; }
	public Vector3 Up { get; }
	public Vector3 Forward { get; }
	public Vector3 CameraPosition { get; }

	/// <summary>Front view: camera on +Z looking toward -Z.</summary>
	public static readonly GizmoView Front = new(Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, new Vector3(0f, 0f, 100f));

	/// <summary>Top view: camera above (+Y) looking down; screen-up is world -Z.</summary>
	public static readonly GizmoView Top = new(Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY, new Vector3(0f, 100f, 0f));

	/// <summary>Side view: camera on +X looking toward -X; screen-right is world -Z.</summary>
	public static readonly GizmoView Side = new(-Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX, new Vector3(100f, 0f, 0f));

	public GizmoView(Vector3 right, Vector3 up, Vector3 forward)
		: this(right, up, forward, Vector3.Zero)
	{
	}

	public GizmoView(Vector3 right, Vector3 up, Vector3 forward, Vector3 cameraPosition)
	{
		if (!IsFinite(forward) || forward.LengthSquared() < 1e-12f)
			throw new ArgumentException("View forward must be finite and non-zero.", nameof(forward));
		if (!IsFinite(right) || right.LengthSquared() < 1e-12f)
			throw new ArgumentException("View right must be finite and non-zero.", nameof(right));
		if (!IsFinite(up) || up.LengthSquared() < 1e-12f)
			throw new ArgumentException("View up must be finite and non-zero.", nameof(up));
		if (!IsFinite(cameraPosition))
			throw new ArgumentException("Camera position must be finite.", nameof(cameraPosition));

		// Orthonormalize: forward first, then right perpendicular to it, then
		// up rebuilt so the basis is right-handed (Up = Right x Forward).
		var f = Vector3.Normalize(forward);
		var r = right - f * Vector3.Dot(right, f);
		if (r.LengthSquared() < 1e-12f)
			throw new ArgumentException("View right must not be parallel to view forward.", nameof(right));
		r = Vector3.Normalize(r);
		var u = Vector3.Cross(r, f);
		Right = r;
		Up = u;
		Forward = f;
		CameraPosition = cameraPosition;
	}

	/// <summary>Build a view from a camera position, a look target and a world-up hint.</summary>
	public static GizmoView FromCamera(Vector3 eye, Vector3 target, Vector3 worldUp)
	{
		if (!IsFinite(eye) || !IsFinite(target) || !IsFinite(worldUp))
			throw new ArgumentException("Camera eye, target and up must be finite.");
		var forward = target - eye;
		if (forward.LengthSquared() < 1e-12f)
			throw new ArgumentException("Camera eye and target must differ.", nameof(target));
		if (worldUp.LengthSquared() < 1e-12f)
			throw new ArgumentException("World up must be non-zero.", nameof(worldUp));
		var f = Vector3.Normalize(forward);
		var r = Vector3.Cross(f, worldUp);
		if (r.LengthSquared() < 1e-12f)
			throw new ArgumentException("World up must not be parallel to the view direction.", nameof(worldUp));
		r = Vector3.Normalize(r);
		var u = Vector3.Cross(r, f);
		return new GizmoView(r, u, f, eye);
	}

	static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>
/// Gizmo + pivot tool model: state (mode/space/lock/pivot/snap) plus the pure
/// drag math that turns one drag gesture, seen through a camera view, into the
/// transform a caller applies (via MapTransform) to the selection. No rendering,
/// no UI. The model is the tool state; DragToTransform never mutates it.
/// </summary>
public sealed class GizmoModel
{
	/// <summary>Minimum scale factor: a drag through the pivot never mirrors or flattens (mirrors GizmoMath.ClampScale).</summary>
	public const float MinScale = 1e-4f;

	/// <summary>Default grid size for translate snap (Hammer grid).</summary>
	public const double DefaultGridSize = 16.0;

	/// <summary>Default angle snap for rotate (Hammer 15-degree snap).</summary>
	public const double DefaultAngleSnapDegrees = 15.0;

	/// <summary>Section key this gizmo state is stored under in a map document's JSON.</summary>
	public const string DocumentSectionKey = "gizmo";

	/// <summary>Schema version written by <see cref="SaveState"/>; newer versions load leniently (see <see cref="LoadState"/>).</summary>
	public const int SchemaVersion = 1;

	public GizmoMode Mode { get; private set; } = GizmoMode.Translate;
	public GizmoSpace Space { get; private set; } = GizmoSpace.Global;
	public GizmoAxisLock LockedAxis { get; private set; } = GizmoAxisLock.None;
	public Vector3 Pivot { get; private set; } = Vector3.Zero;
	public Quaternion SelectionOrientation { get; private set; } = Quaternion.Identity;
	public bool SnapEnabled { get; private set; }
	public double GridSize { get; private set; } = DefaultGridSize;
	public double AngleSnapDegrees { get; private set; } = DefaultAngleSnapDegrees;

	/// <summary>Warnings raised by the most recent load (<see cref="LoadState"/>, <see cref="LoadStateString"/> or <see cref="LoadFromDocument"/>); empty when the load was clean. A missing section is a fresh document and raises none.</summary>
	public IReadOnlyList<string> LastLoadWarnings { get; private set; } = Array.Empty<string>();

	/// <summary>Unknown fields seen by the most recent load, kept verbatim and re-emitted by <see cref="SaveState"/> so a round trip never drops a newer writer's data.</summary>
	public IReadOnlyDictionary<string, JsonNode> PreservedUnknownFields => _unknownFields;

	readonly Dictionary<string, JsonNode> _unknownFields = new( StringComparer.Ordinal );

	/// <summary>Immutable snapshot of the current state.</summary>
	public GizmoState State => new(
		Mode, Space, LockedAxis, Pivot, SelectionOrientation,
		SnapEnabled, GridSize, AngleSnapDegrees);

	/// <summary>Set the tool mode (translate/rotate/scale).</summary>
	public void SetMode(GizmoMode mode)
	{
		if (!Enum.IsDefined(mode))
			throw new ArgumentOutOfRangeException(nameof(mode), "Unknown gizmo mode.");
		Mode = mode;
	}

	/// <summary>Set the coordinate space (global/world or local/selection).</summary>
	public void SetSpace(GizmoSpace space)
	{
		if (!Enum.IsDefined(space))
			throw new ArgumentOutOfRangeException(nameof(space), "Unknown gizmo space.");
		Space = space;
	}

	/// <summary>Lock dragging to one axis (X/Y/Z) or one plane (XY/YZ/XZ).</summary>
	public void LockAxis(GizmoAxisLock axisLock)
	{
		if (!Enum.IsDefined(axisLock))
			throw new ArgumentOutOfRangeException(nameof(axisLock), "Unknown axis lock.");
		LockedAxis = axisLock;
	}

	/// <summary>Clear the axis/plane lock (free drag).</summary>
	public void UnlockAxis() => LockedAxis = GizmoAxisLock.None;

	/// <summary>Cycle translate -&gt; rotate -&gt; scale -&gt; translate (Hammer++ Ctrl+J).</summary>
	public void CycleMode() => Mode = Mode switch
	{
		GizmoMode.Translate => GizmoMode.Rotate,
		GizmoMode.Rotate => GizmoMode.Scale,
		_ => GizmoMode.Translate,
	};

	/// <summary>Toggle global &lt;-&gt; local space.</summary>
	public void ToggleSpace() => Space = Space == GizmoSpace.Global ? GizmoSpace.Local : GizmoSpace.Global;

	/// <summary>Move the pivot (rotation/scale centre) to a point. Ctrl+P story: the pivot is settable from a point.</summary>
	public void SetPivot(Vector3 pivot)
	{
		if (!IsFinite(pivot))
			throw new ArgumentException("Pivot must be finite.", nameof(pivot));
		Pivot = pivot;
	}

	/// <summary>
	/// Move the pivot to the centroid of a selection (the point a Paste Special
	/// / Ctrl+P pivot is taken from). At least one point is required.
	/// </summary>
	public void SetPivotFromSelection(IReadOnlyList<Vector3> points)
	{
		if (points == null) throw new ArgumentNullException(nameof(points));
		if (points.Count == 0)
			throw new ArgumentException("A selection needs at least one point to place the pivot.", nameof(points));
		var sum = Vector3.Zero;
		foreach (var p in points)
		{
			if (!IsFinite(p))
				throw new ArgumentException("Selection points must be finite.", nameof(points));
			sum += p;
		}
		Pivot = sum / points.Count;
	}

	/// <summary>
	/// Set the selection's orientation (the local frame: its X/Y/Z axes in world
	/// space). A desktop caller passes the GizmoFrame orientation, i.e. the
	/// quaternion whose rotation matrix has rows GizmoFrame.AxisX/AxisY/AxisZ.
	/// Must be finite; near-zero quaternions are rejected.
	/// </summary>
	public void SetSelectionOrientation(Quaternion orientation)
	{
		if (!IsFinite(orientation) || orientation.LengthSquared() < 1e-12f)
			throw new ArgumentException("Selection orientation must be a finite non-zero quaternion.", nameof(orientation));
		SelectionOrientation = Quaternion.Normalize(orientation);
	}

	/// <summary>
	/// Configure snapping: grid size for translate (Hammer sizes 1024..0.125;
	/// any positive finite size snaps, standard membership is
	/// MapTransform.IsValidGridSize) and angle snap in degrees for rotate.
	/// Both sizes must be positive and finite.
	/// </summary>
	public void SetSnap(bool enabled, double gridSize, double angleSnapDegrees)
	{
		if (!double.IsFinite(gridSize) || gridSize <= 0.0)
			throw new ArgumentOutOfRangeException(nameof(gridSize), "Grid size must be positive and finite.");
		if (!double.IsFinite(angleSnapDegrees) || angleSnapDegrees <= 0.0)
			throw new ArgumentOutOfRangeException(nameof(angleSnapDegrees), "Angle snap must be positive and finite.");
		SnapEnabled = enabled;
		GridSize = gridSize;
		AngleSnapDegrees = angleSnapDegrees;
	}

	/// <summary>Restore defaults: translate, global, no lock, pivot at origin, identity orientation, snapping off.</summary>
	public void Reset()
	{
		_unknownFields.Clear();
		Mode = GizmoMode.Translate;
		Space = GizmoSpace.Global;
		LockedAxis = GizmoAxisLock.None;
		Pivot = Vector3.Zero;
		SelectionOrientation = Quaternion.Identity;
		SnapEnabled = false;
		GridSize = DefaultGridSize;
		AngleSnapDegrees = DefaultAngleSnapDegrees;
	}

	/// <summary>
	/// Serialize the current state as the map document's "gizmo" section:
	/// schemaVersion, mode/space/lock as names, pivot and selection orientation
	/// as number arrays, snap settings, plus any unknown fields preserved by
	/// the last load, re-emitted verbatim. Never throws for a valid model.
	/// </summary>
	public JsonObject SaveState()
	{
		var section = new JsonObject
		{
			["schemaVersion"] = SchemaVersion,
			["mode"] = Mode.ToString(),
			["space"] = Space.ToString(),
			["lockedAxis"] = LockedAxis.ToString(),
			["pivot"] = new JsonArray( (double)Pivot.X, (double)Pivot.Y, (double)Pivot.Z ),
			["selectionOrientation"] = new JsonArray( (double)SelectionOrientation.X, (double)SelectionOrientation.Y, (double)SelectionOrientation.Z, (double)SelectionOrientation.W ),
			["snapEnabled"] = SnapEnabled,
			["gridSize"] = GridSize,
			["angleSnapDegrees"] = AngleSnapDegrees,
		};
		foreach ( var pair in _unknownFields )
		{
			if ( !section.ContainsKey( pair.Key ) )
				section[pair.Key] = pair.Value?.DeepClone();
		}
		return section;
	}

	/// <summary>Serialize the current state as a JSON string (see <see cref="SaveState"/>).</summary>
	public string SaveStateString() => SaveState().ToJsonString();

	/// <summary>
	/// Write the current state into a map document's JSON object under the
	/// "gizmo" key. Only that key is touched; every other document field is
	/// left exactly as it was.
	/// </summary>
	public void SaveToDocument( JsonObject document )
	{
		if ( document == null ) throw new ArgumentNullException( nameof( document ) );
		document[DocumentSectionKey] = SaveState();
	}

	/// <summary>
	/// Load gizmo state from a "gizmo" section (see <see cref="SaveState"/>).
	/// A migration, never a crash: a missing (null) section is a fresh
	/// document — silent defaults, no warnings. A present section loads per
	/// field: each valid field is applied, each invalid field falls back to
	/// its default with a named warning, each unknown field is warned about
	/// by name and preserved for re-emit by <see cref="SaveState"/>. A newer
	/// schemaVersion warns but still loads the known fields. The model is
	/// always left in a valid state: <see cref="GizmoHealthCheck"/> passes
	/// after any load. Returns the warnings raised.
	/// </summary>
	public GizmoLoadResult LoadState( JsonObject section )
	{
		var warnings = new List<string>();
		_unknownFields.Clear();
		if ( section == null )
		{
			Reset();
			LastLoadWarnings = Array.Empty<string>();
			return new GizmoLoadResult( LastLoadWarnings );
		}

		// Schema version: a section with no version predates versioning — assume 1.
		if ( section.TryGetPropertyValue( "schemaVersion", out var versionNode ) )
		{
			if ( versionNode is JsonValue versionValue && versionValue.TryGetValue<int>( out var version ) )
			{
				if ( version > SchemaVersion )
					warnings.Add( $"gizmo schema version {version} is newer than supported {SchemaVersion}; known fields loaded, unknown fields ignored" );
				else if ( version < 1 )
					warnings.Add( $"gizmo schema version {version} is not valid; known fields loaded as version {SchemaVersion}" );
			}
			else
				warnings.Add( "gizmo field 'schemaVersion' is not an integer; known fields loaded as version 1" );
		}

		// Resolve every field into locals first (invalid present field ->
		// default + named warning; missing field -> silent default), then
		// apply atomically so a load never leaves a half-applied state.
		var mode = GizmoMode.Translate;
		var space = GizmoSpace.Global;
		var lockedAxis = GizmoAxisLock.None;
		var pivot = Vector3.Zero;
		var orientation = Quaternion.Identity;
		var snapEnabled = false;
		var gridSize = DefaultGridSize;
		var angleSnapDegrees = DefaultAngleSnapDegrees;

		if ( section.ContainsKey( "mode" ) )
			TryReadEnum( section, "mode", warnings, GizmoMode.Translate, out mode );
		if ( section.ContainsKey( "space" ) )
			TryReadEnum( section, "space", warnings, GizmoSpace.Global, out space );
		if ( section.ContainsKey( "lockedAxis" ) )
			TryReadEnum( section, "lockedAxis", warnings, GizmoAxisLock.None, out lockedAxis );
		if ( section.ContainsKey( "pivot" ) )
		{
			if ( !TryReadVector3( section["pivot"], out pivot ) )
			{
				warnings.Add( "gizmo field 'pivot' is not an array of 3 finite numbers; falling back to default (0, 0, 0)" );
				pivot = Vector3.Zero;
			}
		}
		if ( section.ContainsKey( "selectionOrientation" ) )
		{
			if ( !TryReadQuaternion( section["selectionOrientation"], out orientation ) )
			{
				warnings.Add( "gizmo field 'selectionOrientation' is not an array of 4 finite numbers forming a non-zero quaternion; falling back to default identity" );
				orientation = Quaternion.Identity;
			}
		}
		if ( section.ContainsKey( "snapEnabled" ) )
		{
			if ( !TryReadBool( section["snapEnabled"], out snapEnabled ) )
			{
				warnings.Add( "gizmo field 'snapEnabled' is not a boolean; falling back to default false" );
				snapEnabled = false;
			}
		}
		if ( section.ContainsKey( "gridSize" ) )
		{
			if ( !TryReadNumber( section["gridSize"], out var grid ) || grid <= 0.0 )
			{
				warnings.Add( $"gizmo field 'gridSize' must be a positive finite number; falling back to default {DefaultGridSize}" );
				gridSize = DefaultGridSize;
			}
			else gridSize = grid;
		}
		if ( section.ContainsKey( "angleSnapDegrees" ) )
		{
			if ( !TryReadNumber( section["angleSnapDegrees"], out var angle ) || angle <= 0.0 )
			{
				warnings.Add( $"gizmo field 'angleSnapDegrees' must be a positive finite number; falling back to default {DefaultAngleSnapDegrees}" );
				angleSnapDegrees = DefaultAngleSnapDegrees;
			}
			else angleSnapDegrees = angle;
		}

		// Unknown fields: warn by name, keep the raw value for re-emit.
		foreach ( var pair in section )
		{
			if ( IsKnownKey( pair.Key ) ) continue;
			warnings.Add( $"unknown gizmo field '{pair.Key}' ignored; falling back to defaults for that field" );
			_unknownFields[pair.Key] = pair.Value?.DeepClone();
		}

		Mode = mode;
		Space = space;
		LockedAxis = lockedAxis;
		Pivot = pivot;
		SelectionOrientation = orientation;
		SnapEnabled = snapEnabled;
		GridSize = gridSize;
		AngleSnapDegrees = angleSnapDegrees;
		LastLoadWarnings = warnings;
		return new GizmoLoadResult( warnings );
	}

	/// <summary>
	/// Load gizmo state from a JSON string (see <see cref="LoadState"/>).
	/// Empty text is a fresh document (silent defaults); text that is not
	/// valid JSON, or JSON that is not an object, falls back to defaults
	/// with a named warning. Never throws for malformed input.
	/// </summary>
	public GizmoLoadResult LoadStateString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
		{
			Reset();
			LastLoadWarnings = Array.Empty<string>();
			return new GizmoLoadResult( LastLoadWarnings );
		}
		JsonNode node;
		try { node = JsonNode.Parse( json ); }
		catch ( System.Text.Json.JsonException ex )
		{
			Reset();
			LastLoadWarnings = new[] { $"gizmo state is not valid JSON ({ex.Message}); using defaults" };
			return new GizmoLoadResult( LastLoadWarnings );
		}
		if ( node is not JsonObject section )
		{
			Reset();
			LastLoadWarnings = new[] { "gizmo state is not a JSON object; using defaults" };
			return new GizmoLoadResult( LastLoadWarnings );
		}
		return LoadState( section );
	}

	/// <summary>
	/// Load gizmo state from a map document's JSON object: reads the
	/// "gizmo" section (see <see cref="LoadState"/>). A document with no
	/// gizmo section is a fresh document — silent defaults, no warnings. A
	/// gizmo section that is present but not an object falls back to
	/// defaults with a named warning. No other document field is touched.
	/// </summary>
	public GizmoLoadResult LoadFromDocument( JsonObject document )
	{
		if ( document == null ) throw new ArgumentNullException( nameof( document ) );
		if ( !document.TryGetPropertyValue( DocumentSectionKey, out var node ) || node == null )
		{
			Reset();
			LastLoadWarnings = Array.Empty<string>();
			return new GizmoLoadResult( LastLoadWarnings );
		}
		if ( node is not JsonObject section )
		{
			Reset();
			LastLoadWarnings = new[] { $"map document field '{DocumentSectionKey}' is not a JSON object; using default gizmo state" };
			return new GizmoLoadResult( LastLoadWarnings );
		}
		return LoadState( section );
	}

	/// <summary>
	/// Always-available contract: proves this model instantiates with a
	/// valid, usable gizmo state — never a missing/null gizmo after an
	/// update. Passes when mode, space and lock are defined values, the
	/// pivot is finite, the selection orientation is a finite unit
	/// quaternion, and both snap sizes are positive and finite. Fails with
	/// a reason naming the first invalid field. A fresh model and a model
	/// loaded from a corrupted section both pass (load falls back to
	/// defaults); a hand-built invalid <see cref="GizmoState"/> fails.
	/// </summary>
	public GizmoHealthResult GizmoHealthCheck() => GizmoHealthCheck( State );

	/// <summary>Health-check any gizmo state, not just this model's (see <see cref="GizmoHealthCheck()"/>).</summary>
	public static GizmoHealthResult GizmoHealthCheck( GizmoState state )
	{
		if ( !Enum.IsDefined( state.Mode ) )
			return new GizmoHealthResult( false, $"invalid gizmo state: mode value {(int)state.Mode} is not a defined GizmoMode" );
		if ( !Enum.IsDefined( state.Space ) )
			return new GizmoHealthResult( false, $"invalid gizmo state: space value {(int)state.Space} is not a defined GizmoSpace" );
			if ( !Enum.IsDefined( state.LockedAxis ) )
			return new GizmoHealthResult( false, $"invalid gizmo state: lockedAxis value {(int)state.LockedAxis} is not a defined GizmoAxisLock" );
		if ( !IsFinite( state.Pivot ) )
			return new GizmoHealthResult( false, "invalid gizmo state: pivot is not finite" );
		if ( !IsFinite( state.SelectionOrientation ) || state.SelectionOrientation.LengthSquared() < 1e-12f )
			return new GizmoHealthResult( false, "invalid gizmo state: selection orientation is not a finite non-zero quaternion" );
		if ( MathF.Abs( state.SelectionOrientation.Length() - 1f ) > 1e-4f )
			return new GizmoHealthResult( false, "invalid gizmo state: selection orientation is not a unit quaternion" );
		if ( !double.IsFinite( state.GridSize ) || state.GridSize <= 0.0 )
			return new GizmoHealthResult( false, "invalid gizmo state: gridSize must be positive and finite" );
		if ( !double.IsFinite( state.AngleSnapDegrees ) || state.AngleSnapDegrees <= 0.0 )
			return new GizmoHealthResult( false, "invalid gizmo state: angleSnapDegrees must be positive and finite" );
		return new GizmoHealthResult( true, $"gizmo available: mode={state.Mode}, space={state.Space}, lock={state.LockedAxis}, pivot=({state.Pivot.X}, {state.Pivot.Y}, {state.Pivot.Z})" );
	}

	static bool IsKnownKey( string key ) => key switch
	{
		"schemaVersion" or "mode" or "space" or "lockedAxis" or "pivot" or "selectionOrientation" or "snapEnabled" or "gridSize" or "angleSnapDegrees" => true,
		_ => false,
	};

	static bool TryReadEnum<T>( JsonObject section, string key, List<string> warnings, T fallback, out T value ) where T : struct, Enum
	{
		value = fallback;
		if ( !section.TryGetPropertyValue( key, out var node ) || node == null ) return false;
		if ( node is JsonValue textValue && textValue.TryGetValue<string>( out var text )
			&& Enum.TryParse<T>( text, ignoreCase: true, out var named ) && Enum.IsDefined( named ) )
		{
			value = named;
			return true;
		}
		if ( node is JsonValue intValue && intValue.TryGetValue<int>( out var number ) && Enum.IsDefined( typeof( T ), number ) )
		{
			value = (T)Enum.ToObject( typeof( T ), number );
			return true;
		}
		warnings.Add( $"gizmo field '{key}' has unknown value {node.ToJsonString()}; falling back to default {fallback}" );
		return false;
	}

	static bool TryReadNumber( JsonNode node, out double value )
	{
		value = 0.0;
		if ( node is JsonValue number && number.TryGetValue<double>( out var d ) && double.IsFinite( d ) )
		{
			value = d;
			return true;
		}
		return false;
	}

	static bool TryReadBool( JsonNode node, out bool value )
	{
		value = false;
		if ( node is JsonValue v )
		{
			if ( v.TryGetValue<bool>( out var b ) ) { value = b; return true; }
			if ( v.TryGetValue<int>( out var n ) && ( n == 0 || n == 1 ) ) { value = n == 1; return true; }
		}
		return false;
	}

	static bool TryReadVector3( JsonNode node, out Vector3 value )
	{
		value = Vector3.Zero;
		if ( node is not JsonArray array || array.Count != 3 ) return false;
		var components = new double[3];
		for ( int i = 0; i < 3; i++ )
		{
			if ( !TryReadNumber( array[i], out var d ) || Math.Abs( d ) > float.MaxValue ) return false;
			components[i] = d;
		}
		value = new Vector3( (float)components[0], (float)components[1], (float)components[2] );
		return true;
	}

	static bool TryReadQuaternion( JsonNode node, out Quaternion value )
	{
		value = Quaternion.Identity;
		if ( node is not JsonArray array || array.Count != 4 ) return false;
		var components = new float[4];
		for ( int i = 0; i < 4; i++ )
		{
			if ( !TryReadNumber( array[i], out var d ) || Math.Abs( d ) > float.MaxValue ) return false;
			components[i] = (float)d;
		}
		var q = new Quaternion( components[0], components[1], components[2], components[3] );
		if ( q.LengthSquared() < 1e-12f ) return false;
		value = Quaternion.Normalize( q );
		return true;
	}

	/// <summary>
	/// The locked axis in world space, in the active frame: a single-axis lock
	/// returns that axis; a plane lock returns the plane normal (XY -&gt; Z,
	/// YZ -&gt; X, XZ -&gt; Y); no lock returns Vector3.Zero. Global returns the
	/// world axis; local returns the SelectionOrientation-rotated axis.
	/// </summary>
	public Vector3 LockedAxisWorld()
	{
		var local = LockedAxis switch
		{
			GizmoAxisLock.X => Vector3.UnitX,
			GizmoAxisLock.Y => Vector3.UnitY,
			GizmoAxisLock.Z => Vector3.UnitZ,
			GizmoAxisLock.XY => Vector3.UnitZ,
			GizmoAxisLock.YZ => Vector3.UnitX,
			GizmoAxisLock.XZ => Vector3.UnitY,
			_ => Vector3.Zero,
		};
		if (local == Vector3.Zero) return Vector3.Zero;
		return Space == GizmoSpace.Local
			? Vector3.Transform(local, Matrix4x4.CreateFromQuaternion(SelectionOrientation))
			: local;
	}

	/// <summary>True when the lock is a plane lock (XY/YZ/XZ).</summary>
	public bool IsPlaneLock => LockedAxis is GizmoAxisLock.XY or GizmoAxisLock.YZ or GizmoAxisLock.XZ;

	/// <summary>
	/// The transform for one drag gesture, seen through a view, built through
	/// the MapTransform API. Never mutates state; a zero drag is identity.
	/// See the file header for the per-mode drag semantics.
	/// </summary>
	public Matrix4x4 DragToTransform(GizmoDrag drag, GizmoView view)
	{
		// GizmoDrag's ctor already validated finiteness; view is a struct whose
		// ctor orthonormalized it, but a default(GizmoView) has zero axes, so
		// guard here instead of trusting construction.
		if (view.Right.LengthSquared() < 1e-12f || view.Up.LengthSquared() < 1e-12f || view.Forward.LengthSquared() < 1e-12f)
			throw new ArgumentException("View axes must be non-zero; use a GizmoView preset or FromCamera, not default(GizmoView).", nameof(view));

		var worldDrag = view.Right * drag.ViewDelta.X
			+ view.Up * drag.ViewDelta.Y
			+ view.Forward * drag.ViewDelta.Z;

		return Mode switch
		{
			GizmoMode.Translate => TranslateDrag(worldDrag),
			GizmoMode.Rotate => RotateDrag(worldDrag, view),
			GizmoMode.Scale => ScaleDrag(worldDrag),
			_ => Matrix4x4.Identity,
		};
	}

	Matrix4x4 TranslateDrag(Vector3 worldDrag)
	{
		Vector3 delta = LockedAxis switch
		{
			GizmoAxisLock.None => worldDrag,
			GizmoAxisLock.XY or GizmoAxisLock.YZ or GizmoAxisLock.XZ => MapTransform.ConstrainToPlane(worldDrag, LockedAxisWorld()),
			_ => MapTransform.ConstrainToAxis(worldDrag, LockedAxisWorld()),
		};
		if (SnapEnabled)
			delta = MapTransform.SnapToGrid(delta, GridSize);
		return MapTransform.TranslationMatrix(delta);
	}

	Matrix4x4 RotateDrag(Vector3 worldDrag, GizmoView view)
	{
		// Rotation axis: locked axis (plane lock -> plane normal) in the active
		// frame; with no lock, the view Forward axis (screen-plane rotation).
		var axis = LockedAxis == GizmoAxisLock.None ? view.Forward : LockedAxisWorld();

		// In-plane basis (U, V) with V = axis x U, so atan2 measures the
		// right-hand-rule angle about the axis. U is the first world axis (+X,
		// then +Y, then +Z) with a substantial component perpendicular to the
		// rotation axis, so world-aligned cases read naturally: rotating about
		// +Z measures from +X toward +Y, and a front-view drag up-right is
		// +45 degrees (about the view Forward axis that is -Z, that is -45
		// degrees about -Z, i.e. the same screen-counterclockwise turn).
		var u = Vector3.UnitX;
		foreach (var candidate in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
		{
			var perpendicular = candidate - axis * Vector3.Dot(candidate, axis);
			if (perpendicular.LengthSquared() > 0.25f)
			{
				u = Vector3.Normalize(perpendicular);
				break;
			}
		}
		var v = Vector3.Cross(axis, u);

		var projected = worldDrag - axis * Vector3.Dot(worldDrag, axis);
		float angleDegrees = 0f;
		if (projected.LengthSquared() >= 1e-12f)
		{
			float radians = MathF.Atan2(Vector3.Dot(projected, v), Vector3.Dot(projected, u));
			angleDegrees = radians * 180f / MathF.PI;
		}
		if (SnapEnabled)
			angleDegrees = (float)MapTransform.SnapAngle(angleDegrees, AngleSnapDegrees);
		return MapTransform.RotationMatrix(axis, angleDegrees, Pivot);
	}

	Matrix4x4 ScaleDrag(Vector3 worldDrag)
	{
		// Factors are computed in the ACTIVE frame's axes (global: world axes;
		// local: selection axes), from the drag's component along each frame axis.
		var factors = Vector3.One;
		switch (LockedAxis)
		{
			case GizmoAxisLock.None:
				factors = new Vector3(1f + worldDrag.Length());
				break;
			case GizmoAxisLock.X:
				factors.X = 1f + Vector3.Dot(worldDrag, LockedAxisWorld());
				break;
			case GizmoAxisLock.Y:
				factors.Y = 1f + Vector3.Dot(worldDrag, LockedAxisWorld());
				break;
			case GizmoAxisLock.Z:
				factors.Z = 1f + Vector3.Dot(worldDrag, LockedAxisWorld());
				break;
			case GizmoAxisLock.XY:
				factors.X = 1f + Vector3.Dot(worldDrag, ActiveFrameAxes().Item1);
				factors.Y = 1f + Vector3.Dot(worldDrag, ActiveFrameAxes().Item2);
				break;
			case GizmoAxisLock.YZ:
				factors.Y = 1f + Vector3.Dot(worldDrag, ActiveFrameAxes().Item2);
				factors.Z = 1f + Vector3.Dot(worldDrag, ActiveFrameAxes().Item3);
				break;
			default: // XZ
				factors.X = 1f + Vector3.Dot(worldDrag, ActiveFrameAxes().Item1);
				factors.Z = 1f + Vector3.Dot(worldDrag, ActiveFrameAxes().Item3);
				break;
		}
		factors = new Vector3(
			MathF.Max(factors.X, MinScale),
			MathF.Max(factors.Y, MinScale),
			MathF.Max(factors.Z, MinScale));
		if (Space == GizmoSpace.Global)
			return MapTransform.ScaleMatrix(factors, Pivot);
		// Local space: the scale axes are the selection's rotated axes, so the
		// scale must be conjugated by the orientation (R * S * R^-1 about the
		// pivot). A world-axis-aligned MapTransform.ScaleMatrix cannot express
		// a rotated scale, so build it directly: T(-pivot) * R * S * R^-1 * T(pivot).
		var rotation = Matrix4x4.CreateFromQuaternion(SelectionOrientation);
		var inverseRotation = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(SelectionOrientation));
		return Matrix4x4.CreateTranslation(-Pivot)
			* rotation
			* Matrix4x4.CreateScale(factors)
			* inverseRotation
			* Matrix4x4.CreateTranslation(Pivot);
	}

	/// <summary>Active-frame X/Y/Z axes in world space (global: world axes; local: orientation-rotated).</summary>
	(Vector3, Vector3, Vector3) ActiveFrameAxes()
	{
		if (Space == GizmoSpace.Global) return (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
		var rotation = Matrix4x4.CreateFromQuaternion(SelectionOrientation);
		return (
			Vector3.Transform(Vector3.UnitX, rotation),
			Vector3.Transform(Vector3.UnitY, rotation),
			Vector3.Transform(Vector3.UnitZ, rotation));
	}

	static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	static bool IsFinite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}

/// <summary>
/// Result of <see cref="GizmoModel.GizmoHealthCheck"/>: whether the gizmo
/// is available with a valid state, plus the reason (what is available, or
/// which field is invalid).
/// </summary>
public readonly struct GizmoHealthResult
{
	public bool Passed { get; }
	public string Reason { get; }

	public GizmoHealthResult( bool passed, string reason )
	{
		Passed = passed;
		Reason = reason ?? throw new ArgumentNullException( nameof( reason ) );
	}
}

/// <summary>
/// Result of a gizmo state load (<see cref="GizmoModel.LoadState"/>,
/// <see cref="GizmoModel.LoadStateString"/>, <see cref="GizmoModel.LoadFromDocument"/>):
/// the named warnings raised while migrating the saved state (empty when the
/// load was clean). <see cref="Migrated"/> is true when any fallback or
/// warning occurred — i.e. the loaded state differs from what was saved.
/// </summary>
public readonly struct GizmoLoadResult
{
	public IReadOnlyList<string> Warnings { get; }
	public bool Migrated { get; }

	public GizmoLoadResult( IReadOnlyList<string> warnings )
	{
		Warnings = warnings ?? Array.Empty<string>();
		Migrated = Warnings.Count > 0;
	}
}
