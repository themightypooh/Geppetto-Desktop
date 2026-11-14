using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// Supported UI element tags for s&box Razor components.
/// </summary>
public enum LineworkUiTag
{
	Panel,
	Button,
	Label,
	Image,
	ModelButton, // 3D object rendered as interactive button (offline 2D baked or live ScenePanel)
	ModelView,   // 3D object view (rotatable/inspectable item preview)
	ProgressBar,
	RadialGauge,
	Slot,        // Inventory / Hotbar item slot with count and rarity border
	TextEntry,
	Slider,
	Checkbox,
	Dropdown,
	ScrollPanel,
	Grid,
	Crosshair,
	ModalDialog
}

public enum LineworkFlexDirection
{
	Column,
	Row
}

public enum LineworkJustify
{
	FlexStart,
	Center,
	FlexEnd,
	SpaceBetween,
	SpaceAround
}

public enum LineworkAlign
{
	FlexStart,
	Center,
	FlexEnd,
	Stretch
}

public enum LineworkUiTarget
{
	ScreenPanel,
	WorldPanel
}

public enum LineworkCameraMode
{
	OrthoIso,
	OrthoFront,
	OrthoSide,
	Perspective
}

/// <summary>
/// Inset margins / padding / slice cuts (L, T, R, B).
/// </summary>
public struct LineworkInsets
{
	public float Left;
	public float Top;
	public float Right;
	public float Bottom;

	public LineworkInsets( float left, float top, float right, float bottom )
	{
		Left = left; Top = top; Right = right; Bottom = bottom;
	}

	public LineworkInsets( float uniform )
	{
		Left = uniform; Top = uniform; Right = uniform; Bottom = uniform;
	}
}

/// <summary>
/// A single UI node in the Linework UI Designer tree.
/// </summary>
public sealed class LineworkUiNode
{
	public string Id { get; set; } = Guid.NewGuid().ToString( "N" )[..8];
	public string Name { get; set; } = "panel";
	public string ClassName { get; set; } = "";
	public LineworkUiTag Tag { get; set; } = LineworkUiTag.Panel;

	// Hierarchy
	public LineworkUiNode Parent { get; set; }
	public List<LineworkUiNode> Children { get; } = new();

	// Layout (Flexbox & Grid)
	public LineworkFlexDirection Direction { get; set; } = LineworkFlexDirection.Column;
	public LineworkJustify JustifyContent { get; set; } = LineworkJustify.FlexStart;
	public LineworkAlign AlignItems { get; set; } = LineworkAlign.Stretch;
	public float Gap { get; set; } = 0f;
	public float FlexGrow { get; set; } = 0f;
	public float FlexShrink { get; set; } = 1f;
	public int GridColumns { get; set; } = 4;

	// Box Model
	public float Width { get; set; } = 0f; // 0 = auto / stretch
	public bool WidthPercent { get; set; } = false;
	public float Height { get; set; } = 0f; // 0 = auto / stretch
	public bool HeightPercent { get; set; } = false;
	public LineworkInsets Padding { get; set; } = new( 0f );
	public LineworkInsets Margin { get; set; } = new( 0f );

	// Styling
	public string BackgroundColor { get; set; } = "";
	public string BorderColor { get; set; } = "";
	public float BorderWidth { get; set; } = 0f;
	public float BorderRadius { get; set; } = 0f;
	public float Opacity { get; set; } = 1f;

	// 9-Slice / Border-Image
	public string BorderImageSource { get; set; } = "";
	public LineworkInsets SliceInsets { get; set; } = new( 16f );
	public bool SliceFill { get; set; } = true;
	public float BorderImageRenderWidth { get; set; } = 16f;
	public string BorderImageTint { get; set; } = "#ffffff";

	// 3D Object Rendering in 2D UI (ModelButton / ModelView)
	public string ModelPath { get; set; } = ""; // Path to .vmdl or .effigy
	public LineworkCameraMode CameraMode { get; set; } = LineworkCameraMode.OrthoIso;
	public float Pitch { get; set; } = -20f;
	public float Yaw { get; set; } = 35f;
	public float Roll { get; set; } = 0f;
	public float Fov { get; set; } = 45f;
	public bool AutoRotate { get; set; } = true;
	public float AutoRotateSpeed { get; set; } = 30f; // deg/s
	public bool Bake2DIcon { get; set; } = false; // When true, baked offline to 2D icon PNG

	// Typography & Content
	public string Text { get; set; } = "";
	public float FontSize { get; set; } = 16f;
	public string TextColor { get; set; } = "#ffffff";
	public string TextAlign { get; set; } = "left";
	public int FontWeight { get; set; } = 400;

	// Dynamic Data Binding & Events
	public string BindingExpr { get; set; } = "";
	public string OnClickExpr { get; set; } = "";

	// Specific Component Properties
	public float ProgressValue { get; set; } = 1f;
	public string ProgressColor { get; set; } = "#4cd964";
	public float MinValue { get; set; } = 0f;
	public float MaxValue { get; set; } = 100f;
	public float StepValue { get; set; } = 1f;
	public string Placeholder { get; set; } = "Enter text...";
	public string SlotItemIcon { get; set; } = "";
	public int SlotCount { get; set; } = 1;
	public string SlotRarityColor { get; set; } = "#a0aec0";

	// Computed Layout Bounds
	public float ComputedX { get; set; }
	public float ComputedY { get; set; }
	public float ComputedW { get; set; }
	public float ComputedH { get; set; }

	public void AddChild( LineworkUiNode child )
	{
		child.Parent = this;
		Children.Add( child );
	}

	public void RemoveChild( LineworkUiNode child )
	{
		if ( Children.Remove( child ) )
			child.Parent = null;
	}

	public LineworkUiNode Clone()
	{
		var clone = new LineworkUiNode
		{
			Id = Guid.NewGuid().ToString( "N" )[..8],
			Name = Name + "_copy",
			ClassName = ClassName,
			Tag = Tag,
			Direction = Direction,
			JustifyContent = JustifyContent,
			AlignItems = AlignItems,
			Gap = Gap,
			FlexGrow = FlexGrow,
			FlexShrink = FlexShrink,
			GridColumns = GridColumns,
			Width = Width,
			WidthPercent = WidthPercent,
			Height = Height,
			HeightPercent = HeightPercent,
			Padding = Padding,
			Margin = Margin,
			BackgroundColor = BackgroundColor,
			BorderColor = BorderColor,
			BorderWidth = BorderWidth,
			BorderRadius = BorderRadius,
			Opacity = Opacity,
			BorderImageSource = BorderImageSource,
			SliceInsets = SliceInsets,
			SliceFill = SliceFill,
			BorderImageRenderWidth = BorderImageRenderWidth,
			BorderImageTint = BorderImageTint,
			ModelPath = ModelPath,
			CameraMode = CameraMode,
			Pitch = Pitch,
			Yaw = Yaw,
			Roll = Roll,
			Fov = Fov,
			AutoRotate = AutoRotate,
			AutoRotateSpeed = AutoRotateSpeed,
			Bake2DIcon = Bake2DIcon,
			Text = Text,
			FontSize = FontSize,
			TextColor = TextColor,
			TextAlign = TextAlign,
			FontWeight = FontWeight,
			BindingExpr = BindingExpr,
			OnClickExpr = OnClickExpr,
			ProgressValue = ProgressValue,
			ProgressColor = ProgressColor,
			MinValue = MinValue,
			MaxValue = MaxValue,
			StepValue = StepValue,
			Placeholder = Placeholder,
			SlotItemIcon = SlotItemIcon,
			SlotCount = SlotCount,
			SlotRarityColor = SlotRarityColor
		};
		foreach ( var c in Children )
			clone.AddChild( c.Clone() );
		return clone;
	}

	public JsonObject ToJson()
	{
		var obj = new JsonObject
		{
			["id"] = Id,
			["name"] = Name,
			["class"] = ClassName,
			["tag"] = Tag.ToString(),
			["dir"] = Direction.ToString(),
			["justify"] = JustifyContent.ToString(),
			["align"] = AlignItems.ToString(),
			["gap"] = Gap,
			["grow"] = FlexGrow,
			["shrink"] = FlexShrink,
			["grid_cols"] = GridColumns,
			["w"] = Width,
			["w_pct"] = WidthPercent,
			["h"] = Height,
			["h_pct"] = HeightPercent,
			["pad"] = new JsonArray { Padding.Left, Padding.Top, Padding.Right, Padding.Bottom },
			["mar"] = new JsonArray { Margin.Left, Margin.Top, Margin.Right, Margin.Bottom },
			["bg"] = BackgroundColor,
			["border_col"] = BorderColor,
			["border_w"] = BorderWidth,
			["radius"] = BorderRadius,
			["opacity"] = Opacity,
			["border_img"] = BorderImageSource,
			["slice"] = new JsonArray { SliceInsets.Left, SliceInsets.Top, SliceInsets.Right, SliceInsets.Bottom },
			["slice_fill"] = SliceFill,
			["slice_w"] = BorderImageRenderWidth,
			["slice_tint"] = BorderImageTint,
			["model_path"] = ModelPath,
			["cam_mode"] = CameraMode.ToString(),
			["pitch"] = Pitch,
			["yaw"] = Yaw,
			["roll"] = Roll,
			["fov"] = Fov,
			["autorotate"] = AutoRotate,
			["autorotate_speed"] = AutoRotateSpeed,
			["bake_2d"] = Bake2DIcon,
			["text"] = Text,
			["font_size"] = FontSize,
			["text_col"] = TextColor,
			["text_align"] = TextAlign,
			["font_weight"] = FontWeight,
			["bind"] = BindingExpr,
			["click"] = OnClickExpr,
			["prog_val"] = ProgressValue,
			["prog_col"] = ProgressColor,
			["min_val"] = MinValue,
			["max_val"] = MaxValue,
			["step_val"] = StepValue,
			["placeholder"] = Placeholder,
			["slot_icon"] = SlotItemIcon,
			["slot_count"] = SlotCount,
			["slot_rarity"] = SlotRarityColor
		};

		var chArray = new JsonArray();
		foreach ( var c in Children )
			chArray.Add( c.ToJson() );
		obj["children"] = chArray;

		return obj;
	}

	public static LineworkUiNode FromJson( JsonNode node )
	{
		if ( node is not JsonObject obj ) return null;
		var item = new LineworkUiNode
		{
			Id = obj["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString( "N" )[..8],
			Name = obj["name"]?.GetValue<string>() ?? "node",
			ClassName = obj["class"]?.GetValue<string>() ?? "",
			Tag = Enum.TryParse<LineworkUiTag>( obj["tag"]?.GetValue<string>(), out var tag ) ? tag : LineworkUiTag.Panel,
			Direction = Enum.TryParse<LineworkFlexDirection>( obj["dir"]?.GetValue<string>(), out var dir ) ? dir : LineworkFlexDirection.Column,
			JustifyContent = Enum.TryParse<LineworkJustify>( obj["justify"]?.GetValue<string>(), out var just ) ? just : LineworkJustify.FlexStart,
			AlignItems = Enum.TryParse<LineworkAlign>( obj["align"]?.GetValue<string>(), out var al ) ? al : LineworkAlign.Stretch,
			Gap = (float)( obj["gap"]?.GetValue<double>() ?? 0.0 ),
			FlexGrow = (float)( obj["grow"]?.GetValue<double>() ?? 0.0 ),
			FlexShrink = (float)( obj["shrink"]?.GetValue<double>() ?? 1.0 ),
			GridColumns = obj["grid_cols"]?.GetValue<int>() ?? 4,
			Width = (float)( obj["w"]?.GetValue<double>() ?? 0.0 ),
			WidthPercent = obj["w_pct"]?.GetValue<bool>() ?? false,
			Height = (float)( obj["h"]?.GetValue<double>() ?? 0.0 ),
			HeightPercent = obj["h_pct"]?.GetValue<bool>() ?? false,
			BackgroundColor = obj["bg"]?.GetValue<string>() ?? "",
			BorderColor = obj["border_col"]?.GetValue<string>() ?? "",
			BorderWidth = (float)( obj["border_w"]?.GetValue<double>() ?? 0.0 ),
			BorderRadius = (float)( obj["radius"]?.GetValue<double>() ?? 0.0 ),
			Opacity = (float)( obj["opacity"]?.GetValue<double>() ?? 1.0 ),
			BorderImageSource = obj["border_img"]?.GetValue<string>() ?? "",
			SliceFill = obj["slice_fill"]?.GetValue<bool>() ?? true,
			BorderImageRenderWidth = (float)( obj["slice_w"]?.GetValue<double>() ?? 16.0 ),
			BorderImageTint = obj["slice_tint"]?.GetValue<string>() ?? "#ffffff",
			ModelPath = obj["model_path"]?.GetValue<string>() ?? "",
			CameraMode = Enum.TryParse<LineworkCameraMode>( obj["cam_mode"]?.GetValue<string>(), out var cm ) ? cm : LineworkCameraMode.OrthoIso,
			Pitch = (float)( obj["pitch"]?.GetValue<double>() ?? -20.0 ),
			Yaw = (float)( obj["yaw"]?.GetValue<double>() ?? 35.0 ),
			Roll = (float)( obj["roll"]?.GetValue<double>() ?? 0.0 ),
			Fov = (float)( obj["fov"]?.GetValue<double>() ?? 45.0 ),
			AutoRotate = obj["autorotate"]?.GetValue<bool>() ?? true,
			AutoRotateSpeed = (float)( obj["autorotate_speed"]?.GetValue<double>() ?? 30.0 ),
			Bake2DIcon = obj["bake_2d"]?.GetValue<bool>() ?? false,
			Text = obj["text"]?.GetValue<string>() ?? "",
			FontSize = (float)( obj["font_size"]?.GetValue<double>() ?? 16.0 ),
			TextColor = obj["text_col"]?.GetValue<string>() ?? "#ffffff",
			TextAlign = obj["text_align"]?.GetValue<string>() ?? "left",
			FontWeight = obj["font_weight"]?.GetValue<int>() ?? 400,
			BindingExpr = obj["bind"]?.GetValue<string>() ?? "",
			OnClickExpr = obj["click"]?.GetValue<string>() ?? "",
			ProgressValue = (float)( obj["prog_val"]?.GetValue<double>() ?? 1.0 ),
			ProgressColor = obj["prog_col"]?.GetValue<string>() ?? "#4cd964",
			MinValue = (float)( obj["min_val"]?.GetValue<double>() ?? 0.0 ),
			MaxValue = (float)( obj["max_val"]?.GetValue<double>() ?? 100.0 ),
			StepValue = (float)( obj["step_val"]?.GetValue<double>() ?? 1.0 ),
			Placeholder = obj["placeholder"]?.GetValue<string>() ?? "Enter text...",
			SlotItemIcon = obj["slot_icon"]?.GetValue<string>() ?? "",
			SlotCount = obj["slot_count"]?.GetValue<int>() ?? 1,
			SlotRarityColor = obj["slot_rarity"]?.GetValue<string>() ?? "#a0aec0"
		};

		if ( obj["pad"] is JsonArray padArr && padArr.Count >= 4 )
			item.Padding = new LineworkInsets( (float)( padArr[0]?.GetValue<double>() ?? 0 ), (float)( padArr[1]?.GetValue<double>() ?? 0 ), (float)( padArr[2]?.GetValue<double>() ?? 0 ), (float)( padArr[3]?.GetValue<double>() ?? 0 ) );
		if ( obj["mar"] is JsonArray marArr && marArr.Count >= 4 )
			item.Margin = new LineworkInsets( (float)( marArr[0]?.GetValue<double>() ?? 0 ), (float)( marArr[1]?.GetValue<double>() ?? 0 ), (float)( marArr[2]?.GetValue<double>() ?? 0 ), (float)( marArr[3]?.GetValue<double>() ?? 0 ) );
		if ( obj["slice"] is JsonArray slArr && slArr.Count >= 4 )
			item.SliceInsets = new LineworkInsets( (float)( slArr[0]?.GetValue<double>() ?? 16 ), (float)( slArr[1]?.GetValue<double>() ?? 16 ), (float)( slArr[2]?.GetValue<double>() ?? 16 ), (float)( slArr[3]?.GetValue<double>() ?? 16 ) );

		if ( obj["children"] is JsonArray chArr )
		{
			foreach ( var cNode in chArr )
			{
				if ( FromJson( cNode ) is { } child )
					item.AddChild( child );
			}
		}

		return item;
	}
}

/// <summary>
/// Layout solver for Linework UI trees: computes pixel coordinates and dimensions
/// for each element in the hierarchy matching standard flexbox behavior.
/// </summary>
public static class LineworkLayoutSolver
{
	public static void Solve( LineworkUiNode root, float containerWidth, float containerHeight )
	{
		if ( root == null ) return;

		root.ComputedX = root.Margin.Left;
		root.ComputedY = root.Margin.Top;
		root.ComputedW = root.Width > 0 ? ( root.WidthPercent ? containerWidth * ( root.Width / 100f ) : root.Width ) : containerWidth - root.Margin.Left - root.Margin.Right;
		root.ComputedH = root.Height > 0 ? ( root.HeightPercent ? containerHeight * ( root.Height / 100f ) : root.Height ) : containerHeight - root.Margin.Top - root.Margin.Bottom;

		LayoutChildren( root );
	}

	private static void LayoutChildren( LineworkUiNode parent )
	{
		if ( parent.Children.Count == 0 ) return;

		var contentX = parent.ComputedX + parent.Padding.Left;
		var contentY = parent.ComputedY + parent.Padding.Top;
		var contentW = Math.Max( 0, parent.ComputedW - parent.Padding.Left - parent.Padding.Right );
		var contentH = Math.Max( 0, parent.ComputedH - parent.Padding.Top - parent.Padding.Bottom );

		// Grid layout handling
		if ( parent.Tag == LineworkUiTag.Grid )
		{
			var cols = Math.Max( 1, parent.GridColumns );
			var cellW = ( contentW - ( cols - 1 ) * parent.Gap ) / cols;
			var cellH = parent.Height > 0 ? parent.Height : cellW;

			for ( var i = 0; i < parent.Children.Count; i++ )
			{
				var child = parent.Children[i];
				var col = i % cols;
				var row = i / cols;

				child.ComputedX = contentX + col * ( cellW + parent.Gap ) + child.Margin.Left;
				child.ComputedY = contentY + row * ( cellH + parent.Gap ) + child.Margin.Top;
				child.ComputedW = child.Width > 0 ? child.Width : cellW;
				child.ComputedH = child.Height > 0 ? child.Height : cellH;

				LayoutChildren( child );
			}
			return;
		}

		var isRow = parent.Direction == LineworkFlexDirection.Row;
		var totalFixedMain = 0f;
		var totalGrow = 0f;

		// First pass: measure sizes
		foreach ( var child in parent.Children )
		{
			float cw = child.Width > 0 ? ( child.WidthPercent ? contentW * ( child.Width / 100f ) : child.Width ) : ( isRow ? 64f : contentW );
			float ch = child.Height > 0 ? ( child.HeightPercent ? contentH * ( child.Height / 100f ) : child.Height ) : ( isRow ? contentH : 32f );

			if ( child.Tag == LineworkUiTag.Label && child.Height <= 0 )
				ch = Math.Max( 24f, child.FontSize * 1.5f );
			else if ( child.Tag == LineworkUiTag.Slot && child.Width <= 0 && child.Height <= 0 )
			{
				cw = 48f; ch = 48f;
			}
			else if ( ( child.Tag == LineworkUiTag.ModelButton || child.Tag == LineworkUiTag.ModelView ) && child.Width <= 0 && child.Height <= 0 )
			{
				cw = 80f; ch = 80f;
			}

			child.ComputedW = cw;
			child.ComputedH = ch;

			totalGrow += child.FlexGrow;
			totalFixedMain += ( isRow ? cw : ch ) + ( isRow ? ( child.Margin.Left + child.Margin.Right ) : ( child.Margin.Top + child.Margin.Bottom ) );
		}

		var gapsTotal = Math.Max( 0, parent.Children.Count - 1 ) * parent.Gap;
		var remainingMain = ( isRow ? contentW : contentH ) - totalFixedMain - gapsTotal;

		// Second pass: distribute flex-grow
		if ( remainingMain > 0 && totalGrow > 0 )
		{
			foreach ( var child in parent.Children )
			{
				if ( child.FlexGrow > 0 )
				{
					var extra = remainingMain * ( child.FlexGrow / totalGrow );
					if ( isRow ) child.ComputedW += extra;
					else child.ComputedH += extra;
				}
			}
		}

		// Third pass: position along main and cross axes
		var currentMain = isRow ? contentX : contentY;

		if ( remainingMain > 0 && totalGrow <= 0 )
		{
			if ( parent.JustifyContent == LineworkJustify.Center )
				currentMain += remainingMain * 0.5f;
			else if ( parent.JustifyContent == LineworkJustify.FlexEnd )
				currentMain += remainingMain;
		}

		foreach ( var child in parent.Children )
		{
			if ( isRow )
			{
				child.ComputedX = currentMain + child.Margin.Left;
				if ( parent.AlignItems == LineworkAlign.Center )
					child.ComputedY = contentY + ( contentH - child.ComputedH ) * 0.5f;
				else if ( parent.AlignItems == LineworkAlign.FlexEnd )
					child.ComputedY = contentY + contentH - child.ComputedH - child.Margin.Bottom;
				else
					child.ComputedY = contentY + child.Margin.Top;

				currentMain += child.ComputedW + child.Margin.Left + child.Margin.Right + parent.Gap;
			}
			else
			{
				child.ComputedY = currentMain + child.Margin.Top;
				if ( parent.AlignItems == LineworkAlign.Center )
					child.ComputedX = contentX + ( contentW - child.ComputedW ) * 0.5f;
				else if ( parent.AlignItems == LineworkAlign.FlexEnd )
					child.ComputedX = contentX + contentW - child.ComputedW - child.Margin.Right;
				else
					child.ComputedX = contentX + child.Margin.Left;

				currentMain += child.ComputedH + child.Margin.Top + child.Margin.Bottom + parent.Gap;
			}

			LayoutChildren( child );
		}
	}
}
