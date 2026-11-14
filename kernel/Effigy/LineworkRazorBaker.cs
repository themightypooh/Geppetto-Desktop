using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// Options for the strict offline .razor + .razor.scss bake.
/// </summary>
public sealed class LineworkRazorBakeOptions
{
	/// <summary>ScreenPanel (default) or WorldPanel, passed through to LineworkUiBaker.</summary>
	public LineworkUiTarget Target { get; set; } = LineworkUiTarget.ScreenPanel;

	/// <summary>Container size used for percent-sized nodes and for SolveLayout. Default 1920x1080.</summary>
	public float ContainerWidth { get; set; } = 1920f;
	public float ContainerHeight { get; set; } = 1080f;

	/// <summary>
	/// When true, LineworkLayoutSolver.Solve runs first and its ComputedW/ComputedH
	/// become the allocated rects (this mutates the tree's Computed fields).
	/// When false (default), allocated rects come from already-solved Computed
	/// values if present, else from the node's explicit Width/Height (percent
	/// sizes resolved against ContainerWidth/ContainerHeight).
	/// </summary>
	public bool SolveLayout { get; set; } = false;

	/// <summary>
	/// Default false = STRICT single-line mode: a text line wider than its inner
	/// rect fails even if the box is tall enough to wrap it. When true, text may
	/// wrap (white-space: normal in the baked scss) and only fails when the
	/// wrapped height exceeds the inner height.
	/// </summary>
	public bool WrapText { get; set; } = false;

	/// <summary>
	/// When true, a Label/Button whose rendered text is a runtime binding
	/// expression (@...) fails the bake instead of passing with a warning,
	/// because its length cannot be measured offline.
	/// </summary>
	public bool StrictBoundText { get; set; } = false;

	/// <summary>Line-height multiplier for required-height math. Matches the 1.5x used by LineworkLayoutSolver for labels.</summary>
	public float LineHeightFactor { get; set; } = 1.5f;
}

/// <summary>
/// One text element that failed the strict enforcement pass.
/// OverflowPx is the measured overflow in px (>= 0) for a sized failure, or -1
/// for the unmeasurable case (runtime-bound text under StrictBoundText).
/// </summary>
public sealed class LineworkRazorBakeFailure
{
	public string NodeName { get; set; } = "";
	public string Text { get; set; } = "";
	public float MeasuredWidthPx { get; set; }
	public float RequiredHeightPx { get; set; }
	public float AllocatedWidthPx { get; set; }
	public float AllocatedHeightPx { get; set; }
	public float WidthOverflowPx { get; set; }
	public float HeightOverflowPx { get; set; }
	public float OverflowPx { get; set; }
	public float SuggestedWidthPx { get; set; }
	public float SuggestedHeightPx { get; set; }
	public float SuggestedFontSizePx { get; set; }
	public string Suggestion { get; set; } = "";

	public override string ToString()
	{
		if ( OverflowPx < 0 )
			return $"{NodeName}: text '{Text}' cannot be measured offline (runtime binding) — {Suggestion}";
		return $"{NodeName}: text '{Text}' overflows by {OverflowPx:0.0}px " +
			$"(width overflow {WidthOverflowPx:0.0}px, height overflow {HeightOverflowPx:0.0}px; " +
			$"measured {MeasuredWidthPx:0.0}x{RequiredHeightPx:0.0}px in allocated {AllocatedWidthPx:0.0}x{AllocatedHeightPx:0.0}px) — {Suggestion}";
	}
}

/// <summary>Auto-size suggestion for a text node whose rect is auto in at least one dimension (it sizes to content, so it cannot overflow).</summary>
public sealed class LineworkRazorAutoSize
{
	public string NodeName { get; set; } = "";
	public float SuggestedWidthPx { get; set; }
	public float SuggestedHeightPx { get; set; }
}

/// <summary>Result of a strict bake. On failure Razor/Scss are null and nothing is written to disk.</summary>
public sealed class LineworkRazorBakeResult
{
	public bool Success { get; set; }
	public string ComponentName { get; set; } = "";
	public string Razor { get; set; }
	public string Scss { get; set; }
	public List<LineworkRazorBakeFailure> Failures { get; } = new();
	public List<string> Warnings { get; } = new();
	public List<LineworkRazorAutoSize> AutoSizeSuggestions { get; } = new();
	public string Error { get; set; }
}

/// <summary>
/// Offline, STRICT s&amp;box .razor + .razor.scss baker for Linework UI models (Lane K).
///
/// This is deliberately NOT a second copy of LineworkUiBaker: markup and styles
/// are delegated to LineworkUiBaker.GenerateCode, and this layer adds the hard
/// enforcement pass that baker lacks. The existing baker emits
/// "text-overflow: ellipsis" — silent clipping — and never measures text.
/// This baker fails the bake instead of clipping or spilling, and strips the
/// ellipsis line from the baked scss so a longer runtime string stays visible
/// rather than being silently truncated.
///
/// Enforcement, per text-bearing node, depth-first:
///   - Rendered text: Label/Button use BindingExpr when set, else Text.
///     A BindingExpr starting with '@' is a runtime expression and cannot be
///     measured offline: it passes with a warning, or fails under
///     StrictBoundText. ModelButton/ModelView labels, Checkbox labels and
///     Slot counts (11px bold in the baked styles) use their literal text.
///     TextEntry placeholder text is NOT checked: LineworkUiBaker emits no
///     TextEntry text, so there is nothing to police there (known gap).
///   - Allocated rect, per dimension: already-solved Computed value if &gt; 0,
///     else percent-of-container, else explicit px, else auto (0).
///   - Inner rect subtracts Padding and BorderWidth on both sides.
///   - Default (WrapText=false): the longest line must fit the inner width
///     AND the full text height (lines x FontSize x LineHeightFactor) must fit
///     the inner height. Any excess FAILS with the element name and the
///     overflow in px, plus widen / grow / shrink-font suggestions.
///   - WrapText=true: text may wrap; it fails only when the wrapped height
///     exceeds the inner height.
///   - A node auto in a dimension cannot overflow in that dimension; it gets
///     an AutoSizeSuggestions entry with the size it needs instead.
///
/// Measurement is a deterministic per-character estimator, NOT Poppins font
/// metrics (s&amp;box has no offline Poppins metrics available to this kernel):
/// narrow chars (i l 1 . , : ; ' ! | space) 0.35em, wide chars
/// (W M m @ % # &amp; w) 0.85em, digits 0.55em, everything else 0.60em,
/// bold (&gt;= 600) x1.08. It is deliberately on the wide side for common
/// letters, so a pass here is conservative, but it is NOT a guarantee against
/// a wider real font or a longer runtime-bound string — see Warnings.
/// A failed bake writes NOTHING, and never overwrites a previous good bake.
/// </summary>
public static class LineworkRazorBaker
{
	/// <summary>Deterministic text-width estimator in px. See the class doc for the factor table.</summary>
	public static float MeasureTextWidth( string text, float fontSize, int fontWeight = 400 )
	{
		if ( string.IsNullOrEmpty( text ) || fontSize <= 0f )
			return 0f;
		var total = 0f;
		foreach ( var c in text )
		{
			var factor = c switch
			{
				'i' or 'l' or '1' or '.' or ',' or ':' or ';' or '\'' or '!' or '|' or ' ' => 0.35f,
				'W' or 'M' or 'm' or '@' or '%' or '#' or '&' or 'w' => 0.85f,
				>= '0' and <= '9' => 0.55f,
				_ => 0.60f
			};
			total += factor;
		}
		if ( fontWeight >= 600 )
			total *= 1.08f;
		return total * fontSize;
	}

	/// <summary>Strict bake to strings. Returns Success=false with Failures/Error set on any overflow; Razor/Scss are null then.</summary>
	public static LineworkRazorBakeResult Bake( LineworkUiNode root, string componentName, LineworkRazorBakeOptions options = null )
	{
		options ??= new LineworkRazorBakeOptions();
		var result = new LineworkRazorBakeResult { ComponentName = SanitizeComponentName( componentName ) };

		if ( root == null )
		{
			result.Success = false;
			result.Error = "root is null: nothing to bake.";
			return result;
		}

		if ( options.SolveLayout )
			LineworkLayoutSolver.Solve( root, options.ContainerWidth, options.ContainerHeight );

		var visited = new HashSet<LineworkUiNode>();
		Walk( root, options, result, visited );

		if ( result.Failures.Count > 0 )
		{
			result.Success = false;
			var sb = new StringBuilder();
			foreach ( var f in result.Failures )
				sb.AppendLine( f.ToString() );
			result.Error = sb.ToString().TrimEnd();
			return result;
		}

		var (razor, scss) = LineworkUiBaker.GenerateCode( root, result.ComponentName, options.Target );
		result.Razor = "<!-- LineworkRazorBaker strict bake: overflow fails at bake time, never silently clipped or spilled. -->\n" + razor;
		result.Scss = "// LineworkRazorBaker strict bake: silent-truncation declarations removed; overflow fails at bake time.\n" + StripEllipsis( scss );
		result.Success = true;
		return result;
	}

	/// <summary>Strict bake to files. Mirrors LineworkUiBaker.BakeToFiles; writes NOTHING on failure and never overwrites a previous bake.</summary>
	public static bool BakeToFiles( LineworkUiNode root, string outputDirectory, string componentName, LineworkUiTarget target, out string error )
	{
		var options = new LineworkRazorBakeOptions { Target = target };
		return BakeToFiles( root, outputDirectory, componentName, options, out error );
	}

	/// <summary>Strict bake to files with full options. On success the files are {ComponentName}.razor and {ComponentName}.razor.scss.</summary>
	public static bool BakeToFiles( LineworkUiNode root, string outputDirectory, string componentName, LineworkRazorBakeOptions options, out string error )
	{
		error = null;
		var result = Bake( root, componentName, options );
		if ( !result.Success )
		{
			error = result.Error;
			return false;
		}
		try
		{
			if ( !Directory.Exists( outputDirectory ) )
				Directory.CreateDirectory( outputDirectory );
			File.WriteAllText( Path.Combine( outputDirectory, $"{result.ComponentName}.razor" ), result.Razor );
			File.WriteAllText( Path.Combine( outputDirectory, $"{result.ComponentName}.razor.scss" ), result.Scss );
			return true;
		}
		catch ( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}

	/// <summary>Component/file-safe name: letters, digits and '_' only; leading digit gets a '_' prefix; blank becomes CustomUi.</summary>
	public static string SanitizeComponentName( string componentName )
	{
		if ( string.IsNullOrWhiteSpace( componentName ) )
			return "CustomUi";
		var sb = new StringBuilder();
		foreach ( var c in componentName )
			if ( ( c >= 'a' && c <= 'z' ) || ( c >= 'A' && c <= 'Z' ) || ( c >= '0' && c <= '9' ) || c == '_' )
				sb.Append( c );
		var clean = sb.ToString();
		if ( clean.Length == 0 )
			return "CustomUi";
		if ( clean[0] >= '0' && clean[0] <= '9' )
			clean = "_" + clean;
		return clean;
	}

	private static void Walk( LineworkUiNode node, LineworkRazorBakeOptions options, LineworkRazorBakeResult result, HashSet<LineworkUiNode> visited )
	{
		if ( node == null || !visited.Add( node ) )
			return;
		CheckNode( node, options, result );
		foreach ( var child in node.Children )
			Walk( child, options, result, visited );
	}

	private static void CheckNode( LineworkUiNode node, LineworkRazorBakeOptions options, LineworkRazorBakeResult result )
	{
		// One entry per rendered text span: (text, fontSize, fontWeight, isBoundExpression).
		var spans = new List<(string Text, float FontSize, int FontWeight, bool IsBound)>();
		switch ( node.Tag )
		{
			case LineworkUiTag.Label:
			case LineworkUiTag.Button:
				if ( !string.IsNullOrEmpty( node.BindingExpr ) )
					spans.Add( (node.BindingExpr, node.FontSize, node.FontWeight, node.BindingExpr.StartsWith( "@" )) );
				else if ( !string.IsNullOrEmpty( node.Text ) )
					spans.Add( (node.Text, node.FontSize, node.FontWeight, false) );
				break;
			case LineworkUiTag.ModelButton:
			case LineworkUiTag.ModelView:
			case LineworkUiTag.Checkbox:
				if ( !string.IsNullOrEmpty( node.Text ) )
					spans.Add( (node.Text, node.FontSize, node.FontWeight, false) );
				break;
			case LineworkUiTag.Slot:
				if ( node.SlotCount > 1 )
					spans.Add( (node.SlotCount.ToString(), 11f, 700, false) );
				break;
			case LineworkUiTag.TextEntry:
				if ( !string.IsNullOrEmpty( node.Text ) )
					spans.Add( (node.Text, node.FontSize, node.FontWeight, false) );
				else if ( !string.IsNullOrEmpty( node.Placeholder ) )
					spans.Add( (node.Placeholder, node.FontSize, node.FontWeight, false) );
				break;
			case LineworkUiTag.Dropdown:
				if ( !string.IsNullOrEmpty( node.Text ) )
					spans.Add( (node.Text, node.FontSize, node.FontWeight, false) );
				spans.Add( ("v", 12f, 400, false) );
				break;
		}
		if ( spans.Count == 0 )
			return;

		var (allocW, allocH, hasW, hasH) = AllocatedRect( node, options );
		var padX = node.Padding.Left + node.Padding.Right + node.BorderWidth * 2f;
		var padY = node.Padding.Top + node.Padding.Bottom + node.BorderWidth * 2f;
		var innerW = hasW ? Math.Max( 0f, allocW - padX ) : 0f;
		var innerH = hasH ? Math.Max( 0f, allocH - padY ) : 0f;

		foreach ( var span in spans )
		{
			if ( span.IsBound )
			{
				if ( options.StrictBoundText )
				{
					result.Failures.Add( new LineworkRazorBakeFailure
					{
						NodeName = node.Name,
						Text = span.Text,
						AllocatedWidthPx = allocW,
						AllocatedHeightPx = allocH,
						WidthOverflowPx = -1f,
						HeightOverflowPx = -1f,
						OverflowPx = -1f,
						Suggestion = "give the bound value a fixed-size guarantee (wider rect, smaller font, or an auto-sized container) or bake with StrictBoundText=false and check the runtime string by hand"
					} );
				}
				else
				{
					result.Warnings.Add( $"{node.Name}: bound text '{span.Text}' cannot be measured offline; baked scss carries no ellipsis, so an over-long runtime string will be visible, not silently clipped" );
				}
				continue;
			}

			var fontSize = span.FontSize > 0f ? span.FontSize : 16f;
			var lineHeight = fontSize * ( options.LineHeightFactor > 0f ? options.LineHeightFactor : 1.5f );
			var lines = SplitLines( span.Text );
			var longest = 0f;
			foreach ( var line in lines )
				longest = Math.Max( longest, MeasureTextWidth( line, fontSize, span.FontWeight ) );

			float requiredHeight;
			if ( options.WrapText && hasW && innerW > 0f )
			{
				var wrapped = 0f;
				foreach ( var line in lines )
					wrapped += Math.Max( 1f, MathF.Ceiling( MeasureTextWidth( line, fontSize, span.FontWeight ) / innerW ) );
				requiredHeight = wrapped * lineHeight;
			}
			else
			{
				requiredHeight = lines.Count * lineHeight;
			}

			var widthOverflow = hasW ? Math.Max( 0f, longest - innerW ) : 0f;
			var heightOverflow = hasH ? Math.Max( 0f, requiredHeight - innerH ) : 0f;
			// In wrap mode a too-wide single line is not an overflow on its own: it wraps.
			if ( options.WrapText )
				widthOverflow = 0f;

			if ( widthOverflow <= 0f && heightOverflow <= 0f )
			{
				if ( !hasW || !hasH )
				{
					result.AutoSizeSuggestions.Add( new LineworkRazorAutoSize
					{
						NodeName = node.Name,
						SuggestedWidthPx = Ceil1( longest + padX ),
						SuggestedHeightPx = Ceil1( requiredHeight + padY )
					} );
				}
				continue;
			}

			var suggestedFont = fontSize;
			if ( hasW && longest > 0f && innerW > 0f )
				suggestedFont = Math.Min( suggestedFont, fontSize * ( innerW / longest ) );
			if ( hasH && requiredHeight > 0f && innerH > 0f )
				suggestedFont = Math.Min( suggestedFont, fontSize * ( innerH / requiredHeight ) );
			suggestedFont = Math.Max( 1f, MathF.Floor( suggestedFont * 2f ) / 2f );

			result.Failures.Add( new LineworkRazorBakeFailure
			{
				NodeName = node.Name,
				Text = span.Text,
				MeasuredWidthPx = longest,
				RequiredHeightPx = requiredHeight,
				AllocatedWidthPx = allocW,
				AllocatedHeightPx = allocH,
				WidthOverflowPx = widthOverflow,
				HeightOverflowPx = heightOverflow,
				OverflowPx = Math.Max( widthOverflow, heightOverflow ),
				SuggestedWidthPx = Ceil1( longest + padX ),
				SuggestedHeightPx = Ceil1( requiredHeight + padY ),
				SuggestedFontSizePx = suggestedFont,
				Suggestion = $"widen to {Ceil1( longest + padX ):0.0}px, grow height to {Ceil1( requiredHeight + padY ):0.0}px, or shrink font to {suggestedFont:0.0}px"
			} );
		}
	}

	private static (float W, float H, bool HasW, bool HasH) AllocatedRect( LineworkUiNode node, LineworkRazorBakeOptions options )
	{
		float w, h;
		if ( node.ComputedW > 0f )
			w = node.ComputedW;
		else if ( node.WidthPercent )
			w = options.ContainerWidth * ( node.Width / 100f );
		else
			w = node.Width;
		if ( node.ComputedH > 0f )
			h = node.ComputedH;
		else if ( node.HeightPercent )
			h = options.ContainerHeight * ( node.Height / 100f );
		else
			h = node.Height;
		return (w, h, w > 0f, h > 0f);
	}

	private static List<string> SplitLines( string text )
	{
		var lines = new List<string>();
		foreach ( var raw in text.Split( '\n' ) )
			lines.Add( raw.TrimEnd( '\r' ) );
		return lines.Count == 0 ? new List<string> { "" } : lines;
	}

	private static float Ceil1( float v ) => MathF.Ceiling( v * 10f ) / 10f;

	private static string StripEllipsis( string scss )
	{
		var sb = new StringBuilder();
		foreach ( var line in scss.Split( '\n' ) )
		{
			var trimmed = line.Trim();
			if ( trimmed.StartsWith( "text-overflow:" ) && trimmed.Contains( "ellipsis" ) )
				continue;
			sb.AppendLine( line.TrimEnd( '\r' ) );
		}
		return sb.ToString();
	}
}
