#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// The feature settings box, like s&box's feature dialog: one row per <see cref="IParam"/> of a feature
/// (Advanced ones behind a toggle), every change previewed live in the viewport. OK commits one undo step
/// (adding the feature, or changing its settings); Cancel puts everything back.
/// </summary>
public partial class GeppettoFeatureDialog : ConfirmationDialog
{
	GeppettoDocument _doc = null!;
	Feature _feature = null!;
	bool _isNew;
	int? _index;
	ParamSnapshot _before = null!;
	bool _done;
	Action<bool>? _closed;

	/// <summary>Opens the box for a feature not yet in the tree. It is previewed at the end of the tree
	/// (or at <paramref name="index"/>) and added as one undo step on OK.</summary>
	public static GeppettoFeatureDialog ShowNew( GeppettoDocument doc, Feature feature, int? index = null, Action<bool>? closed = null )
	{
		if ( string.IsNullOrEmpty( feature.Id ) ) feature.Id = Guid.NewGuid().ToString( "N" )[..8];
		feature.Name ??= feature.TypeName;
		var studio = doc.Studio;
		if ( index is { } i && i < studio.Features.Count ) studio.Insert( i, feature ); else studio.Add( feature );
		return Open( doc, feature, true, index, closed );
	}

	/// <summary>Opens the box on a feature already in the tree.</summary>
	public static GeppettoFeatureDialog ShowEdit( GeppettoDocument doc, Feature feature, Action<bool>? closed = null ) =>
		Open( doc, feature, false, null, closed );

	static GeppettoFeatureDialog Open( GeppettoDocument doc, Feature feature, bool isNew, int? index, Action<bool>? closed )
	{
		var d = new GeppettoFeatureDialog { _doc = doc, _feature = feature, _isNew = isNew, _index = index, _closed = closed };
		d._before = ParamSnapshot.Take( feature );
		d.Title = ( isNew ? "New " : "Edit " ) + feature.TypeName;
		d.OkButtonText = isNew ? "Add" : "Apply";
		d.Exclusive = false;
		d.Build();
		EditorInterface.Singleton.GetBaseControl().AddChild( d );
		d.Confirmed += d.Commit;
		d.Canceled += d.Revert;
		d.CloseRequested += d.Revert;
		d.PopupCentered( new Vector2I( 420, 0 ) );
		doc.Refresh();
		return d;
	}

	public Feature Feature => _feature;

	ScrollContainer? _scroll;
	string _shown = "";

	/// <summary>The parameters showing, so a choice that swaps them (a Primitive's Shape) rebuilds the rows.</summary>
	string ParamKey() => string.Join( "|", _feature.Parameters.Concat( _feature.AdvancedParameters ).Select( p => p.Label ) );

	void Build()
	{
		_scroll?.QueueFree();
		_shown = ParamKey();
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2( 400, 0 ), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		scroll.AddChild( box );
		AddChild( scroll );
		_scroll = scroll;

		var nameEdit = new LineEdit { Text = _feature.Name ?? _feature.TypeName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		nameEdit.TextChanged += t => _feature.Name = t;
		box.AddChild( Row( "Name", nameEdit ) );

		var advanced = _feature.AdvancedParameters.ToHashSet();
		foreach ( var p in _feature.Parameters.Where( p => !advanced.Contains( p ) ) )
		{
			// A plane built from a picked face ignores its From choice, so showing "Top (XY)" there
			// said the face had been dropped. Say what it is on instead.
			if ( _feature is PlaneFeature { Face: not null } onFace && p == onFace.Base )
			{
				box.AddChild( Row( p.Label, new Label { Text = "The picked face", VerticalAlignment = VerticalAlignment.Center } ) );
				continue;
			}
			// The kernel's Sketch choice is a placeholder with one empty option; the real reference is
			// SketchFeatureId. Show the sketches instead (s&amp;box's EffigySketchSelector).
			if ( _feature is SketchConsumingFeature consumer && p == consumer.Sketch )
			{
				box.AddChild( Row( p.Label, SketchPicker( consumer ) ) );
				continue;
			}
			if ( Editor( p ) is { } c ) box.AddChild( Row( p, c ) );
		}

		if ( advanced.Count > 0 )
		{
			var adv = new VBoxContainer { Visible = false };
			var toggle = new CheckButton { Text = "Advanced" };
			toggle.Toggled += on => { adv.Visible = on; ResetSize(); };
			box.AddChild( toggle );
			box.AddChild( adv );
			foreach ( var p in _feature.AdvancedParameters )
				if ( Editor( p ) is { } c ) adv.AddChild( Row( p, c ) );
		}

		int rows = box.GetChildCount();
		scroll.CustomMinimumSize = new Vector2( 400, Math.Min( 560, 36 * rows + 8 ) );
		// A label that wraps to two lines makes its row taller than the 36px guess, and the last row
		// slid under the buttons (Fillet's "Angle threshold (deg)"). Size to the laid-out rows instead.
		box.Resized += () =>
		{
			float want = Math.Min( 560, box.Size.Y + 8 );
			if ( !IsInstanceValid( scroll ) || Math.Abs( scroll.CustomMinimumSize.Y - want ) < 1 ) return;
			scroll.CustomMinimumSize = new Vector2( 400, want );
			Callable.From( () => { if ( IsInstanceValid( this ) && !_done ) ResetSize(); } ).CallDeferred();
		};
	}

	Control SketchPicker( SketchConsumingFeature consumer )
	{
		var sketches = _doc.Studio.Features.OfType<SketchFeature>().Where( f => !f.Suppressed ).ToList();
		if ( sketches.Count == 0 )
			return new Label { Text = "None yet: draw a Sketch first", VerticalAlignment = VerticalAlignment.Center };
		var o = new OptionButton { FitToLongestItem = true };
		foreach ( var f in sketches ) o.AddItem( string.IsNullOrEmpty( f.Name ) ? f.TypeName : f.Name );
		o.Selected = sketches.FindIndex( f => f.Id == _doc.Studio.ResolveSketchFeatureId( consumer ) );
		o.ItemSelected += idx =>
		{
			if ( consumer.SketchFeatureId == sketches[(int)idx].Id ) return;
			consumer.SketchFeatureId = sketches[(int)idx].Id;
			// Picked regions are points in the old sketch's plane: meaningless in another one.
			consumer.RegionSeeds.Clear();
			if ( consumer is ExtrudeFeature ef ) ef.Faces.Clear();
			Preview();
		};
		return o;
	}

	static Control Row( IParam p, Control editor ) =>
		Row( p is FloatParam { Unit: { Length: > 0 } u } ? $"{p.Label} ({u})" : p.Label, editor );

	static Control Row( string label, Control editor )
	{
		var row = new HBoxContainer();
		// Labels wrap instead of clipping; the editor column takes what is left.
		var l = new Label { Text = label, CustomMinimumSize = new Vector2( 140, 0 ), AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.Fill, VerticalAlignment = VerticalAlignment.Center, TooltipText = label };
		row.AddChild( l );
		editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		row.AddChild( editor );
		return row;
	}

	Control? Editor( IParam p )
	{
		switch ( p )
		{
			case FloatParam f:
			{
				var s = new EditorSpinSlider { Step = 0.001, Value = f.Value, AllowGreater = true, AllowLesser = true, HideSlider = !( f.Min > float.MinValue / 2 && f.Max < float.MaxValue / 2 ) };
				if ( f.Min > float.MinValue / 2 ) s.MinValue = f.Min;
				if ( f.Max < float.MaxValue / 2 ) s.MaxValue = f.Max;
				s.Value = f.Value;
				s.ValueChanged += v => { f.Value = (float)v; f.Expr = null!; Preview(); };
				// Typed input: the internal LineEdit is created lazily, so hook TextSubmitted when it appears.
				Action hook = null!;
				hook = () =>
				{
					foreach ( var le in EnumerateLineEdits( s ) )
					{
						if ( le.HasMeta( "g_hooked" ) ) continue;
						le.SetMeta( "g_hooked", true );
						le.TextSubmitted += t =>
						{
							if ( float.TryParse( t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v ) )
							{ f.Value = v; f.Expr = null!; s.Value = v; Preview(); }
						};
					}
				};
				hook();
				s.ValueFocusEntered += () => hook();
				return s;
			}
			case IntParam i:
			{
				var s = new EditorSpinSlider { Step = 1, Rounded = true, HideSlider = !i.Slider };
				s.MinValue = i.Min > int.MinValue / 2 ? i.Min : -1e6;
				s.MaxValue = i.Max < int.MaxValue / 2 ? i.Max : 1e6;
				s.Value = i.Value;
				s.ValueChanged += v => { i.Value = (int)v; Preview(); };
				return s;
			}
			case BoolParam b:
			{
				var c = new CheckBox { ButtonPressed = b.Value };
				c.Toggled += on => { b.Value = on; Preview(); };
				return c;
			}
			case ChoiceParam ch:
			{
				var o = new OptionButton { FitToLongestItem = true };
				foreach ( var opt in ch.Options ) o.AddItem( opt );
				o.Selected = Math.Clamp( ch.Index, 0, ch.Options.Length - 1 );
				o.ItemSelected += idx => { ch.Index = (int)idx; Preview(); };
				return o;
			}
			case StringParam st:
			{
				var e = new LineEdit { Text = st.Value };
				e.TextSubmitted += t => { st.Value = t; Preview(); };
				e.FocusExited += () => { if ( st.Value != e.Text ) { st.Value = e.Text; Preview(); } };
				return e;
			}
			case Vec3Param v:
			{
				var row = new HBoxContainer();
				var axes = new[] { "X", "Y", "Z" };
				for ( int a = 0; a < 3; a++ )
				{
					int axis = a;
					var s = new EditorSpinSlider { Label = axes[a], Step = 0.001, AllowGreater = true, AllowLesser = true, HideSlider = true,
						MinValue = -1e6, MaxValue = 1e6, Value = Get( v.Value, a ), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
					s.ValueChanged += x => { v.Value = Set( v.Value, axis, (float)x ); Preview(); };
					row.AddChild( s );
				}
				return row;
			}
			case BodySelectionParam bs:
			{
				// Body ids mean nothing to anyone: show the part names the Parts list shows.
				string BodyName( string id ) => _doc.Studio.Bodies.FirstOrDefault( b => b.Id == id ) is { } b
					? ( string.IsNullOrEmpty( b.Name ) ? "Unnamed part" : b.Name ) : "A removed part";
				var l = new Label { Text = bs.BodyIds.Count == 0 ? "All bodies" : string.Join( ", ", bs.BodyIds.Select( BodyName ) ), AutowrapMode = TextServer.AutowrapMode.WordSmart,
					TooltipText = "Pick bodies in the viewport before opening the tool to limit it" };
				return l;
			}
		}
		return null;
	}

	static IEnumerable<LineEdit> EnumerateLineEdits( Node root )
	{
		foreach ( Node c in root.GetChildren() )
		{
			if ( c is LineEdit le ) yield return le;
			foreach ( var x in EnumerateLineEdits( c ) ) yield return x;
		}
	}

	static float Get( Vec3 v, int a ) => a == 0 ? v.x : a == 1 ? v.y : v.z;
	static Vec3 Set( Vec3 v, int a, float x ) => a == 0 ? new Vec3( x, v.y, v.z ) : a == 1 ? new Vec3( v.x, x, v.z ) : new Vec3( v.x, v.y, x );

	void Preview()
	{
		_doc.Studio.MarkDirty( _feature );
		_doc.Refresh();
		if ( ParamKey() != _shown ) Callable.From( () => { if ( IsInstanceValid( this ) && !_done ) { Build(); ResetSize(); } } ).CallDeferred();
	}

	void Commit()
	{
		if ( _done ) return;
		_done = true;
		var studio = _doc.Studio;
		if ( _isNew )
		{
			int at = studio.Features.IndexOf( _feature );
			studio.Remove( _feature );
			_doc.AddFeature( _feature, at >= 0 ? at : _index );
		}
		else
		{
			var after = ParamSnapshot.Take( _feature );
			var before = _before;
			var f = _feature;
			_doc.Execute( new DelegateEditCommand( $"Edit {f.TypeName} '{f.Name}'",
				execute: () => { after.Restore( f ); studio.MarkDirty( f ); },
				undo: () => { before.Restore( f ); studio.MarkDirty( f ); },
				isNoOp: () => after.SameAs( before ) ) );
		}
		_closed?.Invoke( true );
		QueueFree();
	}

	void Revert()
	{
		if ( _done ) return;
		_done = true;
		if ( _isNew ) _doc.Studio.Remove( _feature );
		else { _before.Restore( _feature ); _doc.Studio.MarkDirty( _feature ); }
		_doc.Refresh();
		_closed?.Invoke( false );
		QueueFree();
	}
}

/// <summary>A copy of every parameter value (and the name) of one feature, so it can be put back.</summary>
public sealed class ParamSnapshot
{
	readonly List<(IParam P, object V)> _values = new();
	string? _name;
	// What a sketch-consuming feature builds from: not parameters, but Cancel has to put them back too.
	string? _sketchId;
	List<Vec2>? _seeds;
	List<FaceRef>? _faces;

	public static ParamSnapshot Take( Feature f )
	{
		var s = new ParamSnapshot { _name = f.Name };
		if ( f is SketchConsumingFeature c ) { s._sketchId = c.SketchFeatureId; s._seeds = c.RegionSeeds.ToList(); }
		if ( f is ExtrudeFeature ef ) s._faces = ef.Faces.ToList();
		foreach ( var p in f.Parameters.Concat( f.AdvancedParameters ).Distinct() )
			s._values.Add( ( p, p switch
			{
				FloatParam x => ( x.Value, x.Expr ),
				IntParam x => x.Value,
				BoolParam x => x.Value,
				ChoiceParam x => x.Index,
				StringParam x => x.Value,
				Vec3Param x => x.Value,
				BodySelectionParam x => x.BodyIds.ToList(),
				_ => (object)0,
			} ) );
		return s;
	}

	public void Restore( Feature f )
	{
		f.Name = _name;
		if ( f is SketchConsumingFeature c && _seeds != null )
		{
			c.SketchFeatureId = _sketchId ?? "";
			c.RegionSeeds.Clear();
			c.RegionSeeds.AddRange( _seeds );
		}
		if ( f is ExtrudeFeature ef && _faces != null )
		{
			ef.Faces.Clear();
			ef.Faces.AddRange( _faces );
		}
		foreach ( var (p, v) in _values )
			switch ( p )
			{
				case FloatParam x: (x.Value, x.Expr) = ((float, string))v; break;
				case IntParam x: x.Value = (int)v; break;
				case BoolParam x: x.Value = (bool)v; break;
				case ChoiceParam x: x.Index = (int)v; break;
				case StringParam x: x.Value = (string)v; break;
				case Vec3Param x: x.Value = (Vec3)v; break;
				case BodySelectionParam x: x.BodyIds = ( (List<string>)v ).ToList(); break;
			}
	}

	public bool SameAs( ParamSnapshot o ) =>
		_name == o._name && _sketchId == o._sketchId && ( _seeds ?? new() ).SequenceEqual( o._seeds ?? new() ) && _values.Count == o._values.Count &&
		_values.Zip( o._values ).All( z => z.First.V is List<string> a && z.Second.V is List<string> b ? a.SequenceEqual( b ) : Equals( z.First.V, z.Second.V ) );
}
#endif
