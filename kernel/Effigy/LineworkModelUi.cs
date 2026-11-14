using System;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Lane J — 3D-in-2D model UI: a live-model button and a head/bust portrait frame,
/// s&amp;box Geppetto only, as DATA. This file is kernel-portable (no Sandbox types):
/// it says which model, which camera, which framing, which background and border.
/// It never renders a pixel itself.
///
/// Rendering is delegated, not re-implemented:
/// - Live s&amp;box path: the editor/runtime turns a <see cref="LineworkModelRenderRequest"/>
///   into the &lt;scene&gt; element <see cref="LineworkUiBaker"/> already emits for
///   <see cref="LineworkUiTag.ModelButton"/> / <see cref="LineworkUiTag.ModelView"/>
///   (model, pitch, yaw, fov), with the scene camera at
///   <see cref="LineworkModelFraming.CameraPosition"/>.
/// - Headless/offline path: <see cref="ModelRender.Render"/> and
///   <see cref="ShadedRender.Render"/> take a <see cref="RenderView"/> from
///   <see cref="LineworkModelCamera.ResolveView"/>; the framing numbers here
///   (target, distance) are the framing the runtime honours via
///   <see cref="RenderOptions"/> (FocusBodyId + Margin) or the scene camera.
///   Headless tests therefore verify framing math, rect computation and
///   parameter validation — never pixels, and never a fake renderer.
///
/// Up is +Z, as in <see cref="RenderView"/> (Front's up is (0, 0, 1), the
/// model's front is +X). A portrait's "bust" is the top fraction of that
/// Z range: head plus shoulders, never a re-measure of the mesh.
/// </summary>
public readonly struct LineworkModelRect : IEquatable<LineworkModelRect>
{
	public readonly float X;
	public readonly float Y;
	public readonly float Width;
	public readonly float Height;

	public LineworkModelRect( float x, float y, float width, float height )
	{
		X = x; Y = y; Width = width; Height = height;
	}

	public static readonly LineworkModelRect Empty = new( 0f, 0f, 0f, 0f );

	public float Right => X + Width;
	public float Bottom => Y + Height;
	public bool IsEmpty => Width <= 0f || Height <= 0f;
	public float Aspect => Height > 0f ? Width / Height : 0f;

	public bool Contains( float x, float y ) =>
		x >= X && x <= Right && y >= Y && y <= Bottom;

	/// <summary>Rect shrunk by <paramref name="inset"/> on every side; a too-large
	/// inset yields a zero-size rect, never a negative one.</summary>
	public LineworkModelRect Inset( float inset )
	{
		if ( inset <= 0f )
			return this;
		return new LineworkModelRect(
			X + inset,
			Y + inset,
			MathF.Max( 0f, Width - 2f * inset ),
			MathF.Max( 0f, Height - 2f * inset ) );
	}

	/// <summary>The largest rect of the given aspect that fits inside
	/// <paramref name="container"/>, centred (letterboxed). An invalid container
	/// or aspect yields <see cref="Empty"/> rather than an exception: layout
	/// code must survive a zero-size parent.</summary>
	public static LineworkModelRect FitInside( LineworkModelRect container, float aspect )
	{
		if ( container.IsEmpty || aspect <= 0f || !float.IsFinite( aspect ) )
			return Empty;
		var containerAspect = container.Aspect;
		float w, h;
		if ( containerAspect > aspect )
		{
			h = container.Height;
			w = h * aspect;
		}
		else
		{
			w = container.Width;
			h = w / aspect;
		}
		return new LineworkModelRect(
			container.X + ( container.Width - w ) / 2f,
			container.Y + ( container.Height - h ) / 2f,
			w, h );
	}

	public bool Equals( LineworkModelRect o ) =>
		X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
	public override bool Equals( object obj ) => obj is LineworkModelRect o && Equals( o );
	public override int GetHashCode() => HashCode.Combine( X, Y, Width, Height );
	public override string ToString() => $"({X:0.##}, {Y:0.##}, {Width:0.##}x{Height:0.##})";
}

/// <summary>
/// Colour spellings this data layer accepts, mirroring what the tree already
/// uses: "" (no colour), "#hex" (3/4/6/8 digits, as in LineworkUiNode), or
/// rgb()/rgba() (as in the LineworkWindow RPG template's portrait background).
/// A named colour ("blue") is rejected: it is not a spelling the UI layer
/// already produces, and silently accepting it would let a typo through.
/// </summary>
public static class LineworkModelColours
{
	public static bool IsValid( string colour )
	{
		if ( string.IsNullOrEmpty( colour ) )
			return true;
		if ( colour[0] == '#' )
		{
			if ( colour.Length != 4 && colour.Length != 5 && colour.Length != 7 && colour.Length != 9 )
				return false;
			for ( var i = 1; i < colour.Length; i++ )
			{
				var c = colour[i];
				var hex = c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
				if ( !hex )
					return false;
			}
			return true;
		}
		if ( ( colour.StartsWith( "rgb(", StringComparison.Ordinal ) || colour.StartsWith( "rgba(", StringComparison.Ordinal ) )
			&& colour.EndsWith( ")", StringComparison.Ordinal ) )
		{
			foreach ( var c in colour )
			{
				if ( !( char.IsDigit( c ) || c == ',' || c == '.' || c == '%' || c == ' ' || c == '(' || c == ')' || c == 'r' || c == 'g' || c == 'b' || c == 'a' ) )
					return false;
			}
			return true;
		}
		return false;
	}
}

/// <summary>
/// The camera for one model preview, in the existing vocabulary:
/// <see cref="LineworkCameraMode"/> for the fixed ortho views,
/// <see cref="RenderView.Turn"/> yaw/elevation for perspective.
/// Defaults mirror the existing node defaults where they exist
/// (Fov 45, Yaw 35) and RenderView.Turn's elevation default (18).
/// Yaw/elevation are NOT range-clamped: a turntable wraps, so a
/// yaw of 370 is the same view as 10 and validation must not reject it.
/// </summary>
public sealed class LineworkModelCamera
{
	public LineworkCameraMode CameraMode { get; set; } = LineworkCameraMode.Perspective;
	public float YawDegrees { get; set; } = 35f;
	public float ElevationDegrees { get; set; } = 18f;
	public float FovDegrees { get; set; } = 45f;
	public bool AutoRotate { get; set; } = false;
	public float AutoRotateSpeedDegPerSec { get; set; } = 30f;

	/// <summary>The existing renderer view for this camera. Ortho modes map
	/// to the fixed views; Perspective maps to a turntable view. Note the
	/// mapping is by RenderView's own definition: Turn yaw 0 is the front,
	/// growing toward the model's left.</summary>
	public RenderView ResolveView()
	{
		switch ( CameraMode )
		{
			case LineworkCameraMode.OrthoIso: return RenderView.Iso;
			case LineworkCameraMode.OrthoFront: return RenderView.Front;
			case LineworkCameraMode.OrthoSide: return RenderView.Side;
			default: return RenderView.Turn( YawDegrees, ElevationDegrees );
		}
	}

	public bool Validate( out string error )
	{
		if ( !float.IsFinite( YawDegrees ) || !float.IsFinite( ElevationDegrees ) )
		{
			error = "camera yaw/elevation must be finite";
			return false;
		}
		if ( FovDegrees <= 0f || FovDegrees >= 180f || !float.IsFinite( FovDegrees ) )
		{
			error = "camera fov must be between 0 and 180 degrees";
			return false;
		}
		if ( AutoRotateSpeedDegPerSec < 0f || !float.IsFinite( AutoRotateSpeedDegPerSec ) )
		{
			error = "auto-rotate speed must be 0 or more";
			return false;
		}
		error = null;
		return true;
	}
}

/// <summary>
/// Pure framing math. No renderer, no mesh, no engine: bounds in, numbers out.
/// A bounding sphere is used for distance because a button/portrait must
/// fit the whole bust whatever the view direction — a per-axis fit would
/// clip the model on the diagonal views (Iso, Turn).
/// </summary>
public static class LineworkModelFraming
{
	public static bool BoundsValid( Vec3 min, Vec3 max, out string error )
	{
		if ( !float.IsFinite( min.x ) || !float.IsFinite( min.y ) || !float.IsFinite( min.z )
			|| !float.IsFinite( max.x ) || !float.IsFinite( max.y ) || !float.IsFinite( max.z ) )
		{
			error = "model bounds must be finite";
			return false;
		}
		if ( max.x < min.x || max.y < min.y || max.z < min.z )
		{
			error = "model bounds max must not be below min on any axis";
			return false;
		}
		error = null;
		return true;
	}

	public static Vec3 Centre( Vec3 min, Vec3 max ) => ( min + max ) / 2f;

	public static float BoundingRadius( Vec3 min, Vec3 max ) => ( max - min ).Length / 2f;

	/// <summary>Camera distance that fits a sphere of <paramref name="radius"/>
	/// in a <paramref name="fovDegrees"/> vertical field of view, pulled back
	/// by <paramref name="marginFraction"/> beyond the tight fit (0.10 matches
	/// RenderOptions.Margin's default breathing room). Degenerate inputs
	/// (zero radius, out-of-range fov) yield 0 — the camera sits at the
	/// target — rather than NaN or infinity; rejection of bad parameters
	/// belongs to Validate, not to this arithmetic.</summary>
	public static float DistanceForFov( float radius, float fovDegrees, float marginFraction )
	{
		if ( radius <= 0f || fovDegrees <= 0f || fovDegrees >= 180f || marginFraction < 0f )
			return 0f;
		var half = fovDegrees * MathF.PI / 360f;
		return radius / MathF.Tan( half ) * ( 1f + marginFraction );
	}

	/// <summary>The bust bounds: X/Y unchanged (shoulders keep their width),
	/// Z cut to the top <paramref name="bustHeightFraction"/> of the model.
	/// Up is +Z, so the bust's bottom is max.z minus that fraction of height.</summary>
	public static (Vec3 Min, Vec3 Max) BustBounds( Vec3 min, Vec3 max, float bustHeightFraction )
	{
		var height = max.z - min.z;
		var bustMin = new Vec3( min.x, min.y, max.z - height * bustHeightFraction );
		return ( bustMin, max );
	}

	/// <summary>Where the scene camera sits for a view looking at
	/// <paramref name="target"/> from <paramref name="distance"/> away:
	/// back along the view's forward direction. RenderView.Forward is the
	/// direction the camera looks, so the camera is target minus forward
	/// times distance — for the Front view (-1, 0, 0) that puts the camera
	/// on +X, in front of the model, as documented on RenderView.</summary>
	public static Vec3 CameraPosition( Vec3 target, RenderView view, float distance )
	{
		var forward = view.Forward.Normal;
		return target - forward * distance;
	}
}

/// <summary>
/// One render request: everything a runtime needs to draw one preview,
/// and nothing it must guess. IsButton distinguishes the clickable
/// button (hover/click behaviour lives in the .razor, as in
/// LineworkUiBaker's ModelButton styling) from a plain portrait view.
/// </summary>
public sealed class LineworkModelRenderRequest
{
	public string ModelPath { get; set; } = "";
	public RenderView View { get; set; }
	public float FovDegrees { get; set; }
	public Vec3 Target { get; set; }
	public float Distance { get; set; }
	public Vec3 CameraPosition { get; set; }
	public string BackgroundColor { get; set; } = "";
	public float BorderWidth { get; set; }
	public string BorderColor { get; set; } = "";
	public float BorderRadius { get; set; }
	public string Label { get; set; } = "";
	public string OnClickExpr { get; set; } = "";
	public bool IsButton { get; set; }
	public LineworkModelRect Frame { get; set; }
}

/// <summary>
/// A UI button that renders a live 3D model preview. The button's own
/// rect is Width x Height; FrameRect fits that aspect inside a parent
/// container, and PreviewRect is the frame minus the border — the rect
/// the model itself occupies.
/// </summary>
public sealed class LineworkModelButton
{
	public string ModelPath { get; set; } = "";
	public LineworkModelCamera Camera { get; set; } = new();
	public float Width { get; set; } = 96f;
	public float Height { get; set; } = 96f;
	public string Label { get; set; } = "";
	public string OnClickExpr { get; set; } = "";
	public string BackgroundColor { get; set; } = "";
	public float BorderWidth { get; set; } = 0f;
	public string BorderColor { get; set; } = "";
	public float BorderRadius { get; set; } = 0f;
	public float MarginFraction { get; set; } = 0.10f;

	public bool Validate( out string error )
	{
		if ( string.IsNullOrWhiteSpace( ModelPath ) )
		{
			error = "model button needs a model path";
			return false;
		}
		if ( Width <= 0f || Height <= 0f || !float.IsFinite( Width ) || !float.IsFinite( Height ) )
		{
			error = "model button width/height must be above 0";
			return false;
		}
		if ( Camera is null )
		{
			error = "model button needs a camera";
			return false;
		}
		if ( !Camera.Validate( out error ) )
			return false;
		if ( !LineworkModelColours.IsValid( BackgroundColor ) )
		{
			error = "model button background colour is not a #hex or rgb()/rgba() colour";
			return false;
		}
		if ( !LineworkModelColours.IsValid( BorderColor ) )
		{
			error = "model button border colour is not a #hex or rgb()/rgba() colour";
			return false;
		}
		if ( BorderWidth < 0f || BorderRadius < 0f || MarginFraction < 0f )
		{
			error = "model button border width, border radius and margin must be 0 or more";
			return false;
		}
		error = null;
		return true;
	}

	public LineworkModelRect OuterRect( LineworkModelRect container ) =>
		LineworkModelRect.FitInside( container, Width / Height );

	public LineworkModelRect PreviewRect( LineworkModelRect container ) =>
		OuterRect( container ).Inset( BorderWidth );

	public LineworkModelRect OwnFrame() =>
		new LineworkModelRect( 0f, 0f, Width, Height ).Inset( BorderWidth );

	/// <summary>Full-model framing: target is the model's centre, distance
	/// fits its bounding sphere. Bounds come from the caller (the model
	/// loader / measure step) — this class never loads a model.</summary>
	public LineworkModelRenderRequest BuildRequest( Vec3 boundsMin, Vec3 boundsMax )
	{
		var view = Camera.ResolveView();
		var target = LineworkModelFraming.Centre( boundsMin, boundsMax );
		var distance = LineworkModelFraming.DistanceForFov(
			LineworkModelFraming.BoundingRadius( boundsMin, boundsMax ),
			Camera.FovDegrees, MarginFraction );
		return new LineworkModelRenderRequest
		{
			ModelPath = ModelPath,
			View = view,
			FovDegrees = Camera.FovDegrees,
			Target = target,
			Distance = distance,
			CameraPosition = LineworkModelFraming.CameraPosition( target, view, distance ),
			BackgroundColor = BackgroundColor,
			BorderWidth = BorderWidth,
			BorderColor = BorderColor,
			BorderRadius = BorderRadius,
			Label = Label,
			OnClickExpr = OnClickExpr,
			IsButton = true,
			Frame = OwnFrame(),
		};
	}

	public LineworkUiNode ToUiNode() => new()
	{
		Name = string.IsNullOrWhiteSpace( Label ) ? "ModelButton" : Label,
		Tag = LineworkUiTag.ModelButton,
		ModelPath = ModelPath,
		CameraMode = Camera.CameraMode,
		Yaw = Camera.YawDegrees,
		// Copied verbatim: the s&box <scene> element interprets pitch itself
		// (see LineworkUiBaker's <scene pitch=...> output). This data layer
		// does not re-sign it, so a round trip preserves it exactly.
		Pitch = Camera.ElevationDegrees,
		Fov = Camera.FovDegrees,
		AutoRotate = Camera.AutoRotate,
		AutoRotateSpeed = Camera.AutoRotateSpeedDegPerSec,
		Width = Width,
		Height = Height,
		Text = Label,
		OnClickExpr = OnClickExpr,
		BackgroundColor = BackgroundColor,
		BorderColor = BorderColor,
		BorderWidth = BorderWidth,
		BorderRadius = BorderRadius,
	};

	public static LineworkModelButton FromUiNode( LineworkUiNode node )
	{
		if ( node is null || ( node.Tag != LineworkUiTag.ModelButton && node.Tag != LineworkUiTag.ModelView ) )
			return null;
		return new LineworkModelButton
		{
			ModelPath = node.ModelPath,
			Camera = new LineworkModelCamera
			{
				CameraMode = node.CameraMode,
				YawDegrees = node.Yaw,
				ElevationDegrees = node.Pitch,
				FovDegrees = node.Fov,
				AutoRotate = node.AutoRotate,
				AutoRotateSpeedDegPerSec = node.AutoRotateSpeed,
			},
			Width = node.Width,
			Height = node.Height,
			Label = node.Text,
			OnClickExpr = node.OnClickExpr,
			BackgroundColor = node.BackgroundColor,
			BorderWidth = node.BorderWidth,
			BorderColor = node.BorderColor,
			BorderRadius = node.BorderRadius,
		};
	}
}

/// <summary>
/// A portrait frame: a model head/bust, not the whole model. The bust is
/// the top <see cref="BustHeightFraction"/> of the model's height
/// (default 0.45: head plus shoulders); <see cref="VerticalShift"/> nudges
/// the framing target up (+) or down (-) by that fraction of bust height,
/// so a tall hat or a low collar can be centred without changing the crop.
/// A portrait is a view, not a button: its node tag is ModelView, and a
/// clickable portrait is a <see cref="LineworkModelButton"/> instead.
/// </summary>
public sealed class LineworkModelPortrait
{
	public string ModelPath { get; set; } = "";
	public LineworkModelCamera Camera { get; set; } = new();
	public float Width { get; set; } = 96f;
	public float Height { get; set; } = 96f;
	public float BustHeightFraction { get; set; } = 0.45f;
	public float VerticalShift { get; set; } = 0f;
	public string BackgroundColor { get; set; } = "";
	public float BorderWidth { get; set; } = 0f;
	public string BorderColor { get; set; } = "";
	public float BorderRadius { get; set; } = 0f;
	public float MarginFraction { get; set; } = 0.10f;

	public bool Validate( out string error )
	{
		if ( string.IsNullOrWhiteSpace( ModelPath ) )
		{
			error = "portrait needs a model path";
			return false;
		}
		if ( Width <= 0f || Height <= 0f || !float.IsFinite( Width ) || !float.IsFinite( Height ) )
		{
			error = "portrait width/height must be above 0";
			return false;
		}
		if ( Camera is null )
		{
			error = "portrait needs a camera";
			return false;
		}
		if ( !Camera.Validate( out error ) )
			return false;
		if ( BustHeightFraction <= 0f || BustHeightFraction > 1f )
		{
			error = "portrait bust height fraction must be above 0 and at most 1";
			return false;
		}
		if ( VerticalShift < -0.5f || VerticalShift > 0.5f )
		{
			error = "portrait vertical shift must be between -0.5 and 0.5";
			return false;
		}
		if ( !LineworkModelColours.IsValid( BackgroundColor ) )
		{
			error = "portrait background colour is not a #hex or rgb()/rgba() colour";
			return false;
		}
		if ( !LineworkModelColours.IsValid( BorderColor ) )
		{
			error = "portrait border colour is not a #hex or rgb()/rgba() colour";
			return false;
		}
		if ( BorderWidth < 0f || BorderRadius < 0f || MarginFraction < 0f )
		{
			error = "portrait border width, border radius and margin must be 0 or more";
			return false;
		}
		error = null;
		return true;
	}

	public (Vec3 Min, Vec3 Max) BustBounds( Vec3 min, Vec3 max ) =>
		LineworkModelFraming.BustBounds( min, max, BustHeightFraction );

	public Vec3 Target( Vec3 min, Vec3 max )
	{
		var ( bustMin, bustMax ) = BustBounds( min, max );
		var centre = LineworkModelFraming.Centre( bustMin, bustMax );
		var bustHeight = bustMax.z - bustMin.z;
		return new Vec3( centre.x, centre.y, centre.z + VerticalShift * bustHeight );
	}

	public float Distance( Vec3 min, Vec3 max )
	{
		var ( bustMin, bustMax ) = BustBounds( min, max );
		return LineworkModelFraming.DistanceForFov(
			LineworkModelFraming.BoundingRadius( bustMin, bustMax ),
			Camera.FovDegrees, MarginFraction );
	}

	public LineworkModelRect OuterRect( LineworkModelRect container ) =>
		LineworkModelRect.FitInside( container, Width / Height );

	public LineworkModelRect PreviewRect( LineworkModelRect container ) =>
		OuterRect( container ).Inset( BorderWidth );

	/// <summary>Bust framing: target is the bust centre (plus vertical
	/// shift), distance fits the bust's bounding sphere — so the head
	/// fills the frame instead of the whole body shrinking into it.</summary>
	public LineworkModelRenderRequest BuildRequest( Vec3 boundsMin, Vec3 boundsMax )
	{
		var view = Camera.ResolveView();
		var target = Target( boundsMin, boundsMax );
		var distance = Distance( boundsMin, boundsMax );
		return new LineworkModelRenderRequest
		{
			ModelPath = ModelPath,
			View = view,
			FovDegrees = Camera.FovDegrees,
			Target = target,
			Distance = distance,
			CameraPosition = LineworkModelFraming.CameraPosition( target, view, distance ),
			BackgroundColor = BackgroundColor,
			BorderWidth = BorderWidth,
			BorderColor = BorderColor,
			BorderRadius = BorderRadius,
			IsButton = false,
			Frame = new LineworkModelRect( 0f, 0f, Width, Height ).Inset( BorderWidth ),
		};
	}

	public LineworkUiNode ToUiNode() => new()
	{
		Name = "ModelPortrait",
		Tag = LineworkUiTag.ModelView,
		ModelPath = ModelPath,
		CameraMode = Camera.CameraMode,
		Yaw = Camera.YawDegrees,
		Pitch = Camera.ElevationDegrees,
		Fov = Camera.FovDegrees,
		AutoRotate = Camera.AutoRotate,
		AutoRotateSpeed = Camera.AutoRotateSpeedDegPerSec,
		Width = Width,
		Height = Height,
		BackgroundColor = BackgroundColor,
		BorderColor = BorderColor,
		BorderWidth = BorderWidth,
		BorderRadius = BorderRadius,
	};

	public static LineworkModelPortrait FromUiNode( LineworkUiNode node )
	{
		if ( node is null || node.Tag != LineworkUiTag.ModelView )
			return null;
		return new LineworkModelPortrait
		{
			ModelPath = node.ModelPath,
			Camera = new LineworkModelCamera
			{
				CameraMode = node.CameraMode,
				YawDegrees = node.Yaw,
				ElevationDegrees = node.Pitch,
				FovDegrees = node.Fov,
				AutoRotate = node.AutoRotate,
				AutoRotateSpeedDegPerSec = node.AutoRotateSpeed,
			},
			Width = node.Width,
			Height = node.Height,
			BackgroundColor = node.BackgroundColor,
			BorderWidth = node.BorderWidth,
			BorderColor = node.BorderColor,
			BorderRadius = node.BorderRadius,
			// A node carries no bust fraction: a portrait rebuilt from a
			// node takes the default bust, and the caller re-sets it.
		};
	}
}
