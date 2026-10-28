#if TOOLS
#nullable enable
using Godot;

namespace Geppetto;

/// <summary>
/// The small settings boxes the Animate tools open (Marionette's event / limit inspectors as a popup):
/// a title, label + editor rows, an OK button. Labels wrap and the box sizes to its contents, so nothing
/// is ever cut.
/// </summary>
public static class AnimateUi
{
	public static ConfirmationDialog Box( string title, string ok )
	{
		var d = new ConfirmationDialog { Title = title, OkButtonText = ok, Exclusive = false };
		var col = new VBoxContainer { Name = "Col", CustomMinimumSize = new Vector2( 420, 0 ) };
		col.AddThemeConstantOverride( "separation", 6 );
		d.AddChild( col );
		d.VisibilityChanged += () => { if ( !d.Visible ) d.QueueFree(); };
		return d;
	}

	static VBoxContainer Col( ConfirmationDialog d ) => d.GetNode<VBoxContainer>( "Col" );

	public static GridContainer Grid( ConfirmationDialog d )
	{
		var g = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		g.AddThemeConstantOverride( "h_separation", 10 );
		Col( d ).AddChild( g );
		return g;
	}

	/// <summary>One "label | editor" row. The label column is as wide as its longest label (no wrap needed
	/// for short labels, wraps at 160 px for long ones).</summary>
	public static T Row<T>( GridContainer g, string label, T editor ) where T : Control
	{
		g.AddChild( new Label
		{
			Text = label, TooltipText = label, VerticalAlignment = VerticalAlignment.Center,
			AutowrapMode = label.Length > 22 ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
			CustomMinimumSize = label.Length > 22 ? new Vector2( 160, 0 ) : Vector2.Zero,
		} );
		editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		g.AddChild( editor );
		return editor;
	}

	/// <summary>A wrapped, dimmed note under the rows.</summary>
	public static Label Note( ConfirmationDialog d, string text, bool warn = false )
	{
		var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		l.Modulate = warn ? new Color( "f0b860" ) : new Color( 1, 1, 1, 0.7f );
		Col( d ).AddChild( l );
		return l;
	}

	public static SpinBox Spin( double value, double min, double max, double step = 1, string suffix = "" ) =>
		new() { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, AllowGreater = false, AllowLesser = false, SelectAllOnFocus = true };

	/// <summary>Bone picker, longest name decides its width.</summary>
	public static OptionButton Pick( System.Collections.Generic.IEnumerable<string> items, string? select = null )
	{
		var o = new OptionButton { FitToLongestItem = true };
		foreach ( var i in items ) { o.AddItem( i ); if ( i == select ) o.Select( o.ItemCount - 1 ); }
		return o;
	}

	public static string Text( OptionButton o ) => o.Selected < 0 ? "" : o.GetItemText( o.Selected );

	public static void Show( ConfirmationDialog d )
	{
		EditorInterface.Singleton.GetBaseControl().AddChild( d );
		d.PopupCentered();
	}
}
#endif
