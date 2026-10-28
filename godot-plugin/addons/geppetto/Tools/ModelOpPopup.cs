#if TOOLS
#nullable enable
using System;
using Godot;

namespace Geppetto;

/// <summary>
/// s&box's mesh-op bar (EffigyMeshEditPanel: op name, amount, Done, Cancel) as a small box: the typed
/// amount (and axis for Move / Rotate), previewed live in the viewport on every change. Done makes it
/// one undo step; Cancel puts the mesh back.
/// </summary>
public partial class GeppettoModelOpPopup : ConfirmationDialog
{
	GeppettoModelEdit _edit = null!;
	GeppettoModelOp _op = null!;
	float _value;
	int _axis;
	bool _closed;
	Label _notice = null!;

	static GeppettoModelOpPopup? _open;

	public static void Open( GeppettoModelEdit edit, GeppettoModelOp op )
	{
		if ( _open != null && IsInstanceValid( _open ) ) _open.Finish( accept: true );
		if ( !edit.BeginNumeric( op ) ) return;

		var d = new GeppettoModelOpPopup { _edit = edit, _op = op, _axis = op.DefaultAxis };
		d._value = GeppettoModelEdit.Round( op.Default!( edit.Session! ) );
		d.Title = op.Label;
		d.OkButtonText = "Done";
		d.Exclusive = false;
		d.Build();
		EditorInterface.Singleton.GetBaseControl().AddChild( d );
		d.Confirmed += () => d.Finish( accept: true );
		d.Canceled += () => d.Finish( accept: false );
		d.CloseRequested += () => d.Finish( accept: false );
		_open = d;
		d.PopupCentered( new Vector2I( 380, 0 ) );
		d.Preview();
	}

	void Build()
	{
		var box = new VBoxContainer { CustomMinimumSize = new Vector2( 340, 0 ), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		AddChild( box );

		if ( _op.Axis )
		{
			var axis = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			axis.AddItem( "X" );
			axis.AddItem( "Y (up)" );
			axis.AddItem( "Z" );
			axis.Selected = _axis;
			axis.ItemSelected += i => { _axis = (int)i; Preview(); };
			box.AddChild( Row( "Axis", axis ) );
		}

		var amount = new EditorSpinSlider
		{
			Step = 0.001, AllowGreater = true, AllowLesser = true, MinValue = -1000, MaxValue = 1000,
			Value = _value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		amount.ValueChanged += v => { _value = (float)v; Preview(); };
		box.AddChild( Row( _op.ValueLabel, amount ) );

		_notice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		box.AddChild( _notice );
	}

	static Control Row( string label, Control editor )
	{
		var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		row.AddChild( new Label
		{
			Text = label, TooltipText = label, CustomMinimumSize = new Vector2( 140, 0 ),
			AutowrapMode = TextServer.AutowrapMode.WordSmart, VerticalAlignment = VerticalAlignment.Center,
		} );
		row.AddChild( editor );
		return row;
	}

	void Preview()
	{
		if ( _closed ) return;
		_edit.PreviewNumeric( _value, _axis );
		_notice.Text = _edit.LastRefused ? _edit.LastMessage ?? "" : "";
		_notice.TooltipText = _notice.Text;
		_notice.Visible = _notice.Text.Length > 0;
		ResetSize();
		GeppettoTools.Redraw();
	}

	void Finish( bool accept )
	{
		if ( _closed ) return;
		_closed = true;
		if ( accept ) _edit.AcceptNumeric(); else _edit.CancelNumeric();
		if ( _open == this ) _open = null;
		GeppettoTools.Redraw();
		QueueFree();
	}
}
#endif
