using System;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// Bakes Linework UI hierarchies into production-ready s&box .razor and .razor.scss files.
/// Adheres strictly to the user's hard rule: NO TEXT CLIPPING OR OVERFLOW.
/// Includes support for 3D model buttons/views, inventory slots, HUD meters, and widgets.
/// </summary>
public static class LineworkUiBaker
{
	public static (string razor, string scss) GenerateCode( LineworkUiNode root, string componentName, LineworkUiTarget target = LineworkUiTarget.ScreenPanel )
	{
		if ( root == null ) throw new ArgumentNullException( nameof( root ) );
		if ( string.IsNullOrWhiteSpace( componentName ) ) componentName = "CustomUi";

		var razorBuilder = new StringBuilder();
		var scssBuilder = new StringBuilder();

		// Razor Header
		razorBuilder.AppendLine( "@using Sandbox;" );
		razorBuilder.AppendLine( "@using Sandbox.UI;" );
		razorBuilder.AppendLine( "@inherits PanelComponent" );
		razorBuilder.AppendLine( $"@namespace Sandbox" );
		razorBuilder.AppendLine();
		razorBuilder.AppendLine( $"<!-- Linework UI Generated Component: {componentName} -->" );
		razorBuilder.AppendLine( $"<!-- Target: {target} (Bake, never generate at runtime) -->" );
		razorBuilder.AppendLine();

		// Generate Razor Markup
		GenerateNodeMarkup( root, razorBuilder, 0, isRoot: true, componentName );

		// Generate Razor Code block for bindings if present
		razorBuilder.AppendLine();
		razorBuilder.AppendLine( "@code {" );
		razorBuilder.AppendLine( "\t// Data bindings and state" );
		GenerateCodeBindings( root, razorBuilder );
		razorBuilder.AppendLine( "}" );

		// SCSS Header
		scssBuilder.AppendLine( $"// Linework UI Stylesheet: {componentName}.razor.scss" );
		scssBuilder.AppendLine( $"// Zero-clipping flex & grid layout for s&box" );
		scssBuilder.AppendLine();

		scssBuilder.AppendLine( $"{componentName} {{" );
		scssBuilder.AppendLine( "\tposition: absolute;" );
		if ( target == LineworkUiTarget.ScreenPanel )
		{
			scssBuilder.AppendLine( "\tleft: 0;" );
			scssBuilder.AppendLine( "\ttop: 0;" );
			scssBuilder.AppendLine( "\twidth: 100%;" );
			scssBuilder.AppendLine( "\theight: 100%;" );
		}
		scssBuilder.AppendLine( "\tpointer-events: all;" );
		scssBuilder.AppendLine( "\tfont-family: Poppins, sans-serif;" );
		scssBuilder.AppendLine();

		// Generate CSS rules for all classes in hierarchy
		GenerateNodeStyles( root, scssBuilder, 1, isRoot: true );

		scssBuilder.AppendLine( "}" );

		return (razorBuilder.ToString(), scssBuilder.ToString());
	}

	private static void GenerateNodeMarkup( LineworkUiNode node, StringBuilder sb, int indent, bool isRoot, string componentName )
	{
		var ind = new string( '\t', indent );
		var tag = isRoot ? "root" : GetHtmlTag( node.Tag );
		var className = isRoot ? componentName.ToLowerInvariant() : ( string.IsNullOrWhiteSpace( node.ClassName ) ? node.Name.ToLowerInvariant() : node.ClassName );

		sb.Append( $"{ind}<{tag}" );

		if ( !string.IsNullOrWhiteSpace( className ) )
			sb.Append( $" class=\"{className}\"" );

		if ( !string.IsNullOrWhiteSpace( node.OnClickExpr ) )
			sb.Append( $" onclick=\"{node.OnClickExpr}\"" );

		if ( node.Tag == LineworkUiTag.TextEntry )
		{
			if ( !string.IsNullOrWhiteSpace( node.Placeholder ) )
				sb.Append( $" placeholder=\"{EscapeAttr( node.Placeholder )}\"" );
			if ( !string.IsNullOrWhiteSpace( node.BindingExpr ) )
				sb.Append( $" value=\"{node.BindingExpr}\"" );
			else if ( !string.IsNullOrWhiteSpace( node.Text ) )
				sb.Append( $" value=\"{EscapeAttr( node.Text )}\"" );
		}

		sb.AppendLine( ">" );

		// Node Content by Element Type
		if ( node.Tag == LineworkUiTag.Label || node.Tag == LineworkUiTag.Button )
		{
			var textContent = !string.IsNullOrWhiteSpace( node.BindingExpr ) ? node.BindingExpr : node.Text;
			if ( !string.IsNullOrWhiteSpace( textContent ) )
				sb.AppendLine( $"{ind}\t{textContent}" );
		}
		else if ( node.Tag == LineworkUiTag.ProgressBar )
		{
			sb.AppendLine( $"{ind}\t<div class=\"fill\" style=\"width: @({( string.IsNullOrWhiteSpace( node.BindingExpr ) ? $"{MathF.Round( node.ProgressValue * 100f, 1 )}f" : node.BindingExpr )})%;\"></div>" );
		}
		else if ( node.Tag == LineworkUiTag.ModelButton || node.Tag == LineworkUiTag.ModelView )
		{
			if ( node.Bake2DIcon )
			{
				var iconPath = string.IsNullOrWhiteSpace( node.ModelPath ) ? "ui/icons/model_icon.png" : Path.ChangeExtension( node.ModelPath, ".png" );
				sb.AppendLine( $"{ind}\t<image class=\"model-icon\" src=\"{iconPath}\" />" );
			}
			else
			{
				sb.AppendLine( $"{ind}\t<scene class=\"scene-viewport\" model=\"{node.ModelPath}\" pitch=\"{node.Pitch}\" yaw=\"{node.Yaw}\" fov=\"{node.Fov}\" />" );
			}
			if ( !string.IsNullOrWhiteSpace( node.Text ) )
				sb.AppendLine( $"{ind}\t<label class=\"model-label\">{node.Text}</label>" );
		}
		else if ( node.Tag == LineworkUiTag.Slot )
		{
			if ( !string.IsNullOrWhiteSpace( node.SlotItemIcon ) )
				sb.AppendLine( $"{ind}\t<image class=\"item-icon\" src=\"{node.SlotItemIcon}\" />" );
			if ( node.SlotCount > 1 )
				sb.AppendLine( $"{ind}\t<label class=\"count\">{node.SlotCount}</label>" );
		}
		else if ( node.Tag == LineworkUiTag.Checkbox )
		{
			var mark = node.ProgressValue >= 0.5f ? "<div class=\"check-mark\"></div>" : "";
			sb.AppendLine( $"{ind}\t<div class=\"checkbox-box\">{mark}</div>" );
			if ( !string.IsNullOrWhiteSpace( node.Text ) )
				sb.AppendLine( $"{ind}\t<label class=\"checkbox-label\">{node.Text}</label>" );
		}
		else if ( node.Tag == LineworkUiTag.Slider )
		{
			var pct = SliderPercentExpr( node );
			var fill = string.IsNullOrWhiteSpace( node.ProgressColor ) ? "#7eb6ff" : node.ProgressColor;
			sb.AppendLine( $"{ind}\t<div class=\"slider-track\"><div class=\"slider-fill\" style=\"width: @({pct})%; background-color: {fill};\"></div><div class=\"slider-thumb\" style=\"left: @({pct})%;\"></div></div>" );
			sb.AppendLine( $"{ind}\t<label class=\"slider-readout\">@({pct})</label>" );
		}
		else if ( node.Tag == LineworkUiTag.Dropdown )
		{
			if ( !string.IsNullOrWhiteSpace( node.Text ) )
				sb.AppendLine( $"{ind}\t<label class=\"dropdown-value\">{node.Text}</label>" );
			sb.AppendLine( $"{ind}\t<label class=\"dropdown-chevron\">v</label>" );
		}
		else if ( node.Tag == LineworkUiTag.Crosshair )
		{
			sb.AppendLine( $"{ind}\t<div class=\"reticle-dot\"></div>" );
			sb.AppendLine( $"{ind}\t<div class=\"bracket top\"></div>" );
			sb.AppendLine( $"{ind}\t<div class=\"bracket bottom\"></div>" );
			sb.AppendLine( $"{ind}\t<div class=\"bracket left\"></div>" );
			sb.AppendLine( $"{ind}\t<div class=\"bracket right\"></div>" );
		}

		// Children
		foreach ( var child in node.Children )
		{
			GenerateNodeMarkup( child, sb, indent + 1, false, componentName );
		}

		sb.AppendLine( $"{ind}</{tag}>" );
	}

	private static void GenerateNodeStyles( LineworkUiNode node, StringBuilder sb, int indent, bool isRoot )
	{
		var ind = new string( '\t', indent );
		var selector = isRoot ? "&" : $".{( string.IsNullOrWhiteSpace( node.ClassName ) ? node.Name.ToLowerInvariant() : node.ClassName )}";

		sb.AppendLine( $"{ind}{selector} {{" );

		// Layout Strategy: Grid vs Flex
		if ( node.Tag == LineworkUiTag.Grid )
		{
			sb.AppendLine( $"{ind}\tdisplay: grid;" );
			sb.AppendLine( $"{ind}\tgrid-template-columns: repeat({Math.Max( 1, node.GridColumns )}, 1fr);" );
			if ( node.Gap > 0 )
				sb.AppendLine( $"{ind}\tgap: {node.Gap}px;" );
		}
		else
		{
			sb.AppendLine( $"{ind}\tdisplay: flex;" );
			sb.AppendLine( $"{ind}\tflex-direction: {( node.Direction == LineworkFlexDirection.Row ? "row" : "column" )};" );
			sb.AppendLine( $"{ind}\tjustify-content: {GetJustifyCss( node.JustifyContent )};" );
			sb.AppendLine( $"{ind}\talign-items: {GetAlignCss( node.AlignItems )};" );

			if ( node.Gap > 0 )
				sb.AppendLine( $"{ind}\tgap: {node.Gap}px;" );
			if ( node.FlexGrow > 0 )
				sb.AppendLine( $"{ind}\tflex-grow: {node.FlexGrow};" );
			if ( node.FlexShrink != 1f )
				sb.AppendLine( $"{ind}\tflex-shrink: {node.FlexShrink};" );
		}

		// Scrollable Container
		if ( node.Tag == LineworkUiTag.ScrollPanel )
		{
			sb.AppendLine( $"{ind}\toverflow-y: scroll;" );
		}

		// Box Sizing
		if ( node.Width > 0 )
			sb.AppendLine( $"{ind}\twidth: {node.Width}{( node.WidthPercent ? "%" : "px" )};" );
		if ( node.Height > 0 )
			sb.AppendLine( $"{ind}\theight: {node.Height}{( node.HeightPercent ? "%" : "px" )};" );

		// Insets
		if ( node.Padding.Left > 0 || node.Padding.Top > 0 || node.Padding.Right > 0 || node.Padding.Bottom > 0 )
			sb.AppendLine( $"{ind}\tpadding: {node.Padding.Top}px {node.Padding.Right}px {node.Padding.Bottom}px {node.Padding.Left}px;" );
		if ( node.Margin.Left > 0 || node.Margin.Top > 0 || node.Margin.Right > 0 || node.Margin.Bottom > 0 )
			sb.AppendLine( $"{ind}\tmargin: {node.Margin.Top}px {node.Margin.Right}px {node.Margin.Bottom}px {node.Margin.Left}px;" );

		// Appearance
		if ( !string.IsNullOrWhiteSpace( node.BackgroundColor ) )
			sb.AppendLine( $"{ind}\tbackground-color: {node.BackgroundColor};" );
		if ( node.BorderWidth > 0 && !string.IsNullOrWhiteSpace( node.BorderColor ) )
			sb.AppendLine( $"{ind}\tborder: {node.BorderWidth}px solid {node.BorderColor};" );
		if ( node.BorderRadius > 0 )
			sb.AppendLine( $"{ind}\tborder-radius: {node.BorderRadius}px;" );
		if ( node.Opacity < 1f && node.Opacity >= 0f )
			sb.AppendLine( $"{ind}\topacity: {node.Opacity};" );

		// 9-Slice / Border-Image (Native s&box syntax)
		if ( !string.IsNullOrWhiteSpace( node.BorderImageSource ) )
		{
			var fillStr = node.SliceFill ? "fill" : "";
			sb.AppendLine( $"{ind}\tborder-image: url({node.BorderImageSource}) {node.SliceInsets.Top} {node.SliceInsets.Right} {node.SliceInsets.Bottom} {node.SliceInsets.Left} {fillStr} / {node.BorderImageRenderWidth}px stretch;" );
			if ( !string.IsNullOrWhiteSpace( node.BorderImageTint ) )
				sb.AppendLine( $"{ind}\tborder-image-tint: {node.BorderImageTint};" );
		}

		// Typography & Text Safety (po's HARD RULE: NO TEXT CLIPPING/SPILLING)
		if ( node.Tag == LineworkUiTag.Label || node.Tag == LineworkUiTag.Button || node.Tag == LineworkUiTag.ModelButton
			|| node.Tag == LineworkUiTag.TextEntry || node.Tag == LineworkUiTag.Dropdown || node.Tag == LineworkUiTag.Checkbox )
		{
			sb.AppendLine( $"{ind}\tfont-size: {node.FontSize}px;" );
			sb.AppendLine( $"{ind}\tfont-weight: {node.FontWeight};" );
			sb.AppendLine( $"{ind}\tcolor: {node.TextColor};" );
			sb.AppendLine( $"{ind}\ttext-align: {node.TextAlign};" );
			sb.AppendLine( $"{ind}\tword-break: break-word;" );
			sb.AppendLine( $"{ind}\ttext-overflow: ellipsis;" );
			sb.AppendLine( $"{ind}\twhite-space: normal;" );
		}

		// 3D Model Button / View styling
		if ( node.Tag == LineworkUiTag.ModelButton || node.Tag == LineworkUiTag.ModelView )
		{
			sb.AppendLine( $"{ind}\tposition: relative;" );
			sb.AppendLine( $"{ind}\tcursor: pointer;" );
			sb.AppendLine( $"{ind}\t.scene-viewport {{" );
			sb.AppendLine( $"{ind}\t\twidth: 100%;" );
			sb.AppendLine( $"{ind}\t\theight: 100%;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t.model-icon {{" );
			sb.AppendLine( $"{ind}\t\twidth: 100%;" );
			sb.AppendLine( $"{ind}\t\theight: 100%;" );
			sb.AppendLine( $"{ind}\t}}" );
			if ( node.Tag == LineworkUiTag.ModelButton )
			{
				sb.AppendLine( $"{ind}\t&:hover {{" );
				sb.AppendLine( $"{ind}\t\ttransform: scale(1.05);" );
				sb.AppendLine( $"{ind}\t\tfilter: brightness(1.2);" );
				sb.AppendLine( $"{ind}\t}}" );
			}
		}

		// Inventory Slot styling
		if ( node.Tag == LineworkUiTag.Slot )
		{
			sb.AppendLine( $"{ind}\tposition: relative;" );
			sb.AppendLine( $"{ind}\tborder: 2px solid {node.SlotRarityColor};" );
			sb.AppendLine( $"{ind}\tbackground-color: rgba(18, 22, 30, 0.85);" );
			sb.AppendLine( $"{ind}\tborder-radius: 4px;" );
			sb.AppendLine( $"{ind}\t.item-icon {{ width: 100%; height: 100%; }}" );
			sb.AppendLine( $"{ind}\t.count {{" );
			sb.AppendLine( $"{ind}\t\tposition: absolute;" );
			sb.AppendLine( $"{ind}\t\tright: 3px; bottom: 1px;" );
			sb.AppendLine( $"{ind}\t\tfont-size: 11px; font-weight: 700; color: #fff;" );
			sb.AppendLine( $"{ind}\t\ttext-shadow: 1px 1px 2px #000;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t&:hover {{" );
			sb.AppendLine( $"{ind}\t\tborder-color: #ffd700;" );
			sb.AppendLine( $"{ind}\t\tbackground-color: rgba(30, 38, 55, 0.95);" );
			sb.AppendLine( $"{ind}\t}}" );
		}

		// Progress bar styling
		if ( node.Tag == LineworkUiTag.ProgressBar )
		{
			sb.AppendLine( $"{ind}\t.fill {{" );
			sb.AppendLine( $"{ind}\t\theight: 100%;" );
			sb.AppendLine( $"{ind}\t\tbackground-color: {node.ProgressColor};" );
			sb.AppendLine( $"{ind}\t\tborder-radius: {Math.Max( 0, node.BorderRadius - 2 )}px;" );
			sb.AppendLine( $"{ind}\t\ttransition: width 0.15s ease-out;" );
			sb.AppendLine( $"{ind}\t}}" );
		}

		if ( node.Tag == LineworkUiTag.Slider )
		{
			sb.AppendLine( $"{ind}\t.slider-track {{" );
			sb.AppendLine( $"{ind}\t\tflex-grow: 1;" );
			sb.AppendLine( $"{ind}\t\theight: 6px;" );
			sb.AppendLine( $"{ind}\t\tbackground-color: #2a2e38;" );
			sb.AppendLine( $"{ind}\t\tborder-radius: 3px;" );
			sb.AppendLine( $"{ind}\t\tposition: relative;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t.slider-fill {{" );
			sb.AppendLine( $"{ind}\t\tposition: absolute;" );
			sb.AppendLine( $"{ind}\t\tleft: 0; top: 0; height: 100%;" );
			sb.AppendLine( $"{ind}\t\tborder-radius: 3px;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t.slider-thumb {{" );
			sb.AppendLine( $"{ind}\t\tposition: absolute;" );
			sb.AppendLine( $"{ind}\t\ttop: -5px;" );
			sb.AppendLine( $"{ind}\t\twidth: 16px; height: 16px;" );
			sb.AppendLine( $"{ind}\t\tmargin-left: -8px;" );
			sb.AppendLine( $"{ind}\t\tbackground-color: #ffffff;" );
			sb.AppendLine( $"{ind}\t\tborder-radius: 8px;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t.slider-readout {{" );
			sb.AppendLine( $"{ind}\t\twidth: 40px;" );
			sb.AppendLine( $"{ind}\t\tflex-shrink: 0;" );
			sb.AppendLine( $"{ind}\t\tfont-size: 13px;" );
			sb.AppendLine( $"{ind}\t\tcolor: #e8eaed;" );
			sb.AppendLine( $"{ind}\t\ttext-align: right;" );
			sb.AppendLine( $"{ind}\t}}" );
		}

		if ( node.Tag == LineworkUiTag.Checkbox )
		{
			sb.AppendLine( $"{ind}\t.checkbox-box {{" );
			sb.AppendLine( $"{ind}\t\twidth: 16px; height: 16px;" );
			sb.AppendLine( $"{ind}\t\tflex-shrink: 0;" );
			sb.AppendLine( $"{ind}\t\tborder: 1px solid #8b93a7;" );
			sb.AppendLine( $"{ind}\t\tbackground-color: #12141a;" );
			sb.AppendLine( $"{ind}\t\tborder-radius: 3px;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t.check-mark {{" );
			sb.AppendLine( $"{ind}\t\twidth: 8px; height: 8px;" );
			sb.AppendLine( $"{ind}\t\tmargin: 3px;" );
			sb.AppendLine( $"{ind}\t\tbackground-color: #ffffff;" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t.checkbox-label {{" );
			sb.AppendLine( $"{ind}\t\tfont-size: {node.FontSize}px;" );
			sb.AppendLine( $"{ind}\t\tcolor: {node.TextColor};" );
			sb.AppendLine( $"{ind}\t}}" );
		}

		if ( node.Tag == LineworkUiTag.Dropdown )
		{
			sb.AppendLine( $"{ind}\tposition: relative;" );
			sb.AppendLine( $"{ind}\t.dropdown-chevron {{" );
			sb.AppendLine( $"{ind}\t\tposition: absolute;" );
			sb.AppendLine( $"{ind}\t\tright: 12px;" );
			sb.AppendLine( $"{ind}\t\tfont-size: 12px;" );
			sb.AppendLine( $"{ind}\t\tcolor: #b8c0cc;" );
			sb.AppendLine( $"{ind}\t}}" );
		}

		// Button hover state
		if ( node.Tag == LineworkUiTag.Button )
		{
			sb.AppendLine( $"{ind}\t&:hover {{" );
			sb.AppendLine( $"{ind}\t\tfilter: brightness(1.2);" );
			sb.AppendLine( $"{ind}\t\ttransform: scale(1.02);" );
			sb.AppendLine( $"{ind}\t}}" );
			sb.AppendLine( $"{ind}\t&:active {{" );
			sb.AppendLine( $"{ind}\t\tfilter: brightness(0.9);" );
			sb.AppendLine( $"{ind}\t\ttransform: scale(0.98);" );
			sb.AppendLine( $"{ind}\t}}" );
		}

		// Recurse children
		foreach ( var child in node.Children )
		{
			GenerateNodeStyles( child, sb, indent, false );
		}

		sb.AppendLine( $"{ind}}}" );
	}

	private static void GenerateCodeBindings( LineworkUiNode node, StringBuilder sb )
	{
		if ( !string.IsNullOrWhiteSpace( node.BindingExpr ) && node.BindingExpr.StartsWith( "@" ) )
		{
			var propName = node.BindingExpr.TrimStart( '@' );
			if ( !propName.Contains( '(' ) && !propName.Contains( '.' ) && !propName.Contains( ' ' ) )
			{
				if ( node.Tag == LineworkUiTag.ProgressBar || node.Tag == LineworkUiTag.Slider )
					sb.AppendLine( $"\t[Property] public float {propName} {{ get; set; }} = {MathF.Round( node.ProgressValue * 100f, 1 )}f;" );
				else
					sb.AppendLine( $"\t[Property] public string {propName} {{ get; set; }} = \"{node.Text}\";" );
			}
		}

		foreach ( var child in node.Children )
			GenerateCodeBindings( child, sb );
	}

	/// <summary>0..1 progress as a percent expression. A binding <c>@Volume</c> becomes <c>Volume</c> so the razor is <c>@(Volume)</c>, not <c>@(@Volume)</c>.</summary>
	private static string SliderPercentExpr( LineworkUiNode node )
	{
		if ( !string.IsNullOrWhiteSpace( node.BindingExpr ) )
		{
			var expr = node.BindingExpr.Trim();
			if ( expr.StartsWith( "@", StringComparison.Ordinal ) )
				expr = expr[1..].Trim();
			return expr;
		}
		var pct = MathF.Round( Math.Clamp( node.ProgressValue, 0f, 1f ) * 100f, 1 );
		return pct.ToString( System.Globalization.CultureInfo.InvariantCulture );
	}

	private static string EscapeAttr( string value )
	{
		if ( string.IsNullOrEmpty( value ) ) return "";
		return value.Replace( "&", "&amp;" ).Replace( "\"", "&quot;" ).Replace( "<", "&lt;" );
	}

	private static string GetHtmlTag( LineworkUiTag tag ) => tag switch
	{
		LineworkUiTag.Panel => "div",
		LineworkUiTag.Button => "button",
		LineworkUiTag.Label => "label",
		LineworkUiTag.Image => "image",
		LineworkUiTag.ModelButton => "div",
		LineworkUiTag.ModelView => "div",
		LineworkUiTag.ProgressBar => "div",
		LineworkUiTag.Slot => "div",
		LineworkUiTag.TextEntry => "textentry",
		LineworkUiTag.Slider => "div",
		LineworkUiTag.Checkbox => "div",
		LineworkUiTag.ScrollPanel => "div",
		LineworkUiTag.Grid => "div",
		LineworkUiTag.Crosshair => "div",
		LineworkUiTag.ModalDialog => "div",
		_ => "div"
	};

	private static string GetJustifyCss( LineworkJustify justify ) => justify switch
	{
		LineworkJustify.FlexStart => "flex-start",
		LineworkJustify.Center => "center",
		LineworkJustify.FlexEnd => "flex-end",
		LineworkJustify.SpaceBetween => "space-between",
		LineworkJustify.SpaceAround => "space-around",
		_ => "flex-start"
	};

	private static string GetAlignCss( LineworkAlign align ) => align switch
	{
		LineworkAlign.FlexStart => "flex-start",
		LineworkAlign.Center => "center",
		LineworkAlign.FlexEnd => "flex-end",
		LineworkAlign.Stretch => "stretch",
		_ => "stretch"
	};

	public static bool BakeToFiles( LineworkUiNode root, string outputDirectory, string componentName, LineworkUiTarget target, out string error )
	{
		error = null;
		try
		{
			if ( !Directory.Exists( outputDirectory ) )
				Directory.CreateDirectory( outputDirectory );

			var (razor, scss) = GenerateCode( root, componentName, target );

			var razorPath = Path.Combine( outputDirectory, $"{componentName}.razor" );
			var scssPath = Path.Combine( outputDirectory, $"{componentName}.razor.scss" );

			File.WriteAllText( razorPath, razor );
			File.WriteAllText( scssPath, scss );
			return true;
		}
		catch ( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}
}
