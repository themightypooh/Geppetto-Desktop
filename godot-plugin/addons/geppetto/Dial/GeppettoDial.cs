#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Geppetto;

/// <summary>
/// The Geppetto tool dial, drawn over one of Poohdot's 3D editor viewports (a child of the viewport's
/// input surface). It copies Linework's ToolWheel: one ring that fans between three levels
/// (workspaces → a workspace's stages → a stage's tools, each with a Back tile), a core that shows the
/// material ball and "Workspace > Tool", and a material ring that opens from the core.
///
/// Geometry is the approved mock's, in its units: core r50, tiles to r122, plate r132, material ring
/// r136–262 over -131°..25° (SVG angles: 0° is east, positive is clockwise/down). Everything is mapped
/// to the screen as centre + scale·(r cos a, r sin a).
///
/// Input: only left clicks and Ctrl+wheel over the dial (and plain wheel over the material ring) are
/// taken. Right and middle mouse are never accepted, and the filter is Pass, so they reach the
/// viewport's camera underneath.
/// </summary>
[Tool]
public partial class GeppettoDial : Control
{
	// ---- mock geometry ----
	const float RCore = 50, RRing = 122, RPlate = 132;
	const float MatR0 = RRing + 14, MatR1 = 262, MatA0 = -131, MatA1 = 25;
	const float BallsA0 = -128, BallsA1 = 22;          // the material arc proper
	const float RBalls = 196, BallR = 16, RRecent = 150, RecentR = 8.5f;
	const float TabR0 = 232, TabR1 = 254;
	const float GripR = RRing + 24, GripMoveA = 46, GripSizeA = 62, GripRadius = 12;
	const int BallsPerPage = 12;
	const float FanMs = 260, StaggerMs = 18;

	static readonly (string Mode, string Label, float A0, float A1)[] Tabs =
	{
		( "materials", "MATERIALS", -128, -96 ), ( "shades", "SHADES", -95, -73 ),
		( "hsl", "HSL", -72, -59 ), ( "pick", "PICK", -58, -44 ),
	};
	static readonly float[] SliderR = { 176, 200, 224 };

	// ---- mock colours ----
	static readonly Color Gep = new( "e8a03a" );
	static readonly (Color In, Color Out) GNormal = ( new( "24272d" ), new( "363a42" ) );
	static readonly (Color In, Color Out) GHover = ( new( "2d3139" ), new( "474c56" ) );
	static readonly (Color In, Color Out) GActive = ( new( "c27a1c" ), new( "f3b553" ) );
	static readonly (Color In, Color Out) GBack = ( new( "1b1d22" ), new( "2a2d33" ) );
	static readonly Color LabelCol = new( "aeb2ba" ), LabelActive = new( "2a1a03" ), IconCol = new( "c9ccd3" );
	static readonly Color CoreName = new( "7d828c" ), CoreText = new( "e6e7ea" );
	static readonly Color GripFill = new( "1d1f24" ), GripStroke = new( "3a3e46" );
	static readonly Color RingBody = new( 18 / 255f, 19 / 255f, 23 / 255f, 0.93f );

	/// <summary>The 3D viewport this dial sits on, for the PICK eyedropper.</summary>
	public SubViewport Viewport3D;
	/// <summary>The viewport's input surface: the dial's parent and the thing that holds keyboard focus.</summary>
	Control _surface;

	enum Hit { None, Plate, Tile, Core, GripMove, GripSize, RingBody, Tab, Ball, Shade, Slider, Recent }
	readonly record struct HitInfo( Hit Kind, int Index = -1, int Sub = -1, float Value = 0 );

	HitInfo _hover;
	bool _dragMove, _dragSize, _dragSlider;
	int _slider;
	Vector2 _dragStart, _offsetStart;
	float _scaleStart;
	static bool _picking;
	static float _h = 30, _sat = 0.6f, _lit = 0.5f;     // HSL ring, degrees / 0..1

	Vector2 _ctr;
	float _s = 1;
	Font _font, _bold;

	public override void _EnterTree()
	{
		_surface = GetParent() as Control;
		SetAnchorsAndOffsetsPreset( LayoutPreset.FullRect );
		MouseFilter = MouseFilterEnum.Pass;
		FocusMode = FocusModeEnum.None;
		var theme = EditorInterface.Singleton.GetEditorTheme();
		_font = theme.GetFont( "main", "EditorFonts" );
		_bold = theme.GetFont( "bold", "EditorFonts" );
		GeppettoDialState.Changed += QueueRedraw;
		SyncHsl();
	}

	public override void _ExitTree() => GeppettoDialState.Changed -= QueueRedraw;

	public override void _Process( double delta )
	{
		// Keep animating while a fan or the ring opening is in flight.
		var now = Time.GetTicksMsec();
		if ( now - GeppettoDialState.FanStart < FanMs + StaggerMs * 9 + 40 || now - GeppettoDialState.RingStart < FanMs + 40 )
			QueueRedraw();
	}

	// =========================================================================================
	// Layout
	// =========================================================================================

	void Layout()
	{
		_s = GeppettoDialState.Scale * EditorInterface.Singleton.GetEditorScale();
		float margin = 14 * EditorInterface.Singleton.GetEditorScale(), rr = RPlate * _s;
		// The part of this overlay actually inside the window: when the editor's docks need more height
		// than the window has, the viewport runs past the bottom edge and a corner dial would be hidden.
		var visible = GetGlobalTransformWithCanvas().AffineInverse() * (Vector2)GetWindow().Size;
		var view = new Vector2( Mathf.Min( Size.X, visible.X ), Mathf.Min( Size.Y, visible.Y ) );
		var home = new Vector2( margin + rr, view.Y - margin - rr );
		var c = home + GeppettoDialState.Offset;
		// Keep the whole plate on screen whatever the viewport size, and the material ring too while it
		// is open (it reaches MatR1 up and right, and MatR1·cos(MatA0) left), so no tab or label is cut.
		float left = rr, top = rr, right = rr;
		if ( GeppettoDialState.MatOpen )
		{
			left = Mathf.Max( rr, -MatR1 * Mathf.Cos( Mathf.DegToRad( MatA0 ) ) * _s );
			top = right = MatR1 * _s;
		}
		c.X = Mathf.Clamp( c.X, left, Mathf.Max( left, view.X - right ) );
		c.Y = Mathf.Clamp( c.Y, top, Mathf.Max( top, view.Y - rr ) );
		_ctr = c;
	}

	/// <summary>The plate's bounds in global canvas coordinates (for the window-run on-screen check).</summary>
	public Rect2 RingRectGlobal()
	{
		Layout();
		float rr = RPlate * _s;
		return new Rect2( GetGlobalTransformWithCanvas() * ( _ctr - new Vector2( rr, rr ) ), new Vector2( rr * 2, rr * 2 ) );
	}

	Vector2 P( float r, float deg ) => _ctr + _s * r * Dir( deg );
	static Vector2 Dir( float deg ) => new( Mathf.Cos( Mathf.DegToRad( deg ) ), Mathf.Sin( Mathf.DegToRad( deg ) ) );

	/// <summary>The tiles on the ring at the current level: name, icon path, active, is-Back.</summary>
	List<(string Name, string Icon, bool Active, bool Back)> Tiles()
	{
		var st = GeppettoDialState.Workspace;
		var list = new List<(string, string, bool, bool)>();
		switch ( GeppettoDialState.Level )
		{
			case 0:
				for ( int i = 0; i < GeppettoDialData.Workspaces.Length; i++ )
				{
					var w = GeppettoDialData.Workspaces[i];
					list.Add( ( w.Name, GeppettoDialData.IconPath( "ws:" + w.Name, null ), i == GeppettoDialState.Ws, false ) );
				}
				break;
			case 1:
				list.Add( ( "Back", GeppettoDialData.IconPath( "__back", null ), false, true ) );
				for ( int i = 0; i < st.Stages.Length; i++ )
					list.Add( ( st.Stages[i].Stage, GeppettoDialData.IconPath( st.Stages[i].Stage, null ), i == GeppettoDialState.LastStage[GeppettoDialState.Ws], false ) );
				break;
			default:
				var stage = st.Stages[GeppettoDialState.OpenStage];
				list.Add( ( "Back", GeppettoDialData.IconPath( "__back", null ), false, true ) );
				bool inStage = GeppettoDialState.LastStage[GeppettoDialState.Ws] == GeppettoDialState.OpenStage;
				foreach ( var t in stage.Tools )
					list.Add( ( t, GeppettoDialData.IconPath( t, stage.Stage ), inStage && GeppettoDialState.CurrentTool == t, false ) );
				break;
		}
		return list;
	}

	List<GeppettoDialMaterial> PageMaterials() =>
		GeppettoDialState.Materials.Skip( GeppettoDialState.MatPage * BallsPerPage ).Take( BallsPerPage ).ToList();

	static float BallAngle( int i ) => BallsA0 + ( BallsA1 - BallsA0 ) * ( i + 0.5f ) / BallsPerPage;
	static float RecentAngle( int i ) => BallsA0 + 2 + i * 12.5f;
	const float ShadeStep = ( BallsA1 - BallsA0 ) / 16f;
	static Color ShadeColor( int h, int k ) => FromHsl( h * 22.5f, ( 50 + 7 * k ) / 100f, ( 18 + 14 * k ) / 100f );

	// =========================================================================================
	// Hit testing
	// =========================================================================================

	static float NormDeg( float a ) { a %= 360; if ( a > 180 ) a -= 360; if ( a <= -180 ) a += 360; return a; }
	static bool InArc( float a, float a0, float a1 ) { a = NormDeg( a ); return a >= a0 && a <= a1; }

	HitInfo HitTest( Vector2 p )
	{
		if ( !Visible ) return default;
		Layout();
		var v = ( p - _ctr ) / _s;
		float r = v.Length(), a = Mathf.RadToDeg( Mathf.Atan2( v.Y, v.X ) );

		if ( ( p - P( GripR, GripMoveA ) ).Length() <= GripRadius * _s ) return new( Hit.GripMove );
		if ( ( p - P( GripR, GripSizeA ) ).Length() <= GripRadius * _s ) return new( Hit.GripSize );

		if ( GeppettoDialState.MatOpen && r >= MatR0 && r <= MatR1 && InArc( a, MatA0, MatA1 ) )
			return RingHit( p, r, a );

		if ( r <= RCore ) return new( Hit.Core );
		if ( r <= RPlate )
		{
			if ( r < RCore + 2 || r > RRing ) return new( Hit.Plate );
			int n = Tiles().Count;
			float step = 360f / n;
			int i = ( (int)Mathf.Round( NormDeg( a + 90 ) / step ) % n + n ) % n;
			return new( Hit.Tile, i );
		}
		return default;
	}

	HitInfo RingHit( Vector2 p, float r, float a )
	{
		a = NormDeg( a );
		for ( int t = 0; t < Tabs.Length; t++ )
			if ( r >= TabR0 && r <= TabR1 && a >= Tabs[t].A0 && a <= Tabs[t].A1 ) return new( Hit.Tab, t );
		for ( int i = 0; i < GeppettoDialState.Recent.Count; i++ )
			if ( ( p - P( RRecent, RecentAngle( i ) ) ).Length() <= ( RecentR + 2 ) * _s ) return new( Hit.Recent, i );

		switch ( GeppettoDialState.MatMode )
		{
			case "materials":
				var page = PageMaterials();
				for ( int i = 0; i < page.Count; i++ )
					if ( ( p - P( RBalls, BallAngle( i ) ) ).Length() <= BallR * _s ) return new( Hit.Ball, i );
				break;
			case "shades":
				if ( r >= 170 && r < 230 && a >= BallsA0 && a < BallsA1 )
					return new( Hit.Shade, Math.Clamp( (int)( ( a - BallsA0 ) / ShadeStep ), 0, 15 ), Math.Clamp( (int)( ( r - 170 ) / 12 ), 0, 4 ) );
				break;
			case "hsl":
				for ( int j = 0; j < SliderR.Length; j++ )
					if ( Mathf.Abs( r - SliderR[j] ) <= 8 && a >= BallsA0 - 3 && a <= BallsA1 + 3 )
						return new( Hit.Slider, j, -1, Mathf.Clamp( ( a - BallsA0 ) / ( BallsA1 - BallsA0 ), 0, 1 ) );
				break;
		}
		return new( Hit.RingBody );
	}

	public override bool _HasPoint( Vector2 point ) => HitTest( point ).Kind != Hit.None || _dragMove || _dragSize || _dragSlider;

	// =========================================================================================
	// Input
	// =========================================================================================

	public override void _GuiInput( InputEvent e )
	{
		switch ( e )
		{
			case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
				if ( mb.Pressed ) Press( mb.Position );
				else Release();
				AcceptEvent();
				break;

			case InputEventMouseButton mb when mb.Pressed && ( mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown ):
				int dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
				if ( mb.CtrlPressed )
				{
					GeppettoDialState.Scale = Mathf.Clamp( dir > 0 ? GeppettoDialState.Scale * 1.06f : GeppettoDialState.Scale / 1.06f, 0.6f, 1.5f );
					GeppettoDialState.Save(); GeppettoDialState.Touch();
					AcceptEvent();
				}
				else if ( GeppettoDialState.MatOpen && GeppettoDialState.MatMode == "materials" && HitTest( mb.Position ).Kind is Hit.Ball or Hit.RingBody )
				{
					GeppettoDialState.PageMaterials( -dir, BallsPerPage );
					AcceptEvent();
				}
				// Plain wheel elsewhere falls through to the viewport's zoom.
				break;

			case InputEventMouseMotion mm:
				if ( _dragMove )
				{
					GeppettoDialState.Offset = _offsetStart + ( mm.Position - _dragStart );
					GeppettoDialState.Touch();
					AcceptEvent();
				}
				else if ( _dragSize )
				{
					var d = mm.Position - _dragStart;
					GeppettoDialState.Scale = Mathf.Clamp( _scaleStart + ( d.X - d.Y ) / 300f, 0.6f, 1.5f );
					GeppettoDialState.Touch();
					AcceptEvent();
				}
				else if ( _dragSlider )
				{
					Layout();
					var v = ( mm.Position - _ctr ) / _s;
					float a = Mathf.RadToDeg( Mathf.Atan2( v.Y, v.X ) );
					SetSlider( _slider, Mathf.Clamp( ( a - BallsA0 ) / ( BallsA1 - BallsA0 ), 0, 1 ), false );
					AcceptEvent();
				}
				else
				{
					var h = HitTest( mm.Position );
					if ( h != _hover ) { _hover = h; QueueRedraw(); }
				}
				break;
			// Right-click on a ball or a recent opens its menu (swap, edit, rename, delete). Every other right
			// and middle click is never accepted: it reaches the camera underneath.
			case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Right && HitTest( mb.Position ) is { Kind: Hit.Ball or Hit.Recent } rh:
				if ( mb.Pressed )
				{
					var m = rh.Kind == Hit.Ball ? PageMaterials()[rh.Index] : rh.Index < GeppettoDialState.Recent.Count ? GeppettoDialState.Recent[rh.Index] : null;
					GeppettoDialState.AskMaterialMenu( m, GetScreenTransform() * mb.Position );
				}
				AcceptEvent();
				break;
		}
	}

	public override void _Notification( int what )
	{
		if ( what == NotificationMouseExit && !_dragMove && !_dragSize && !_dragSlider ) { _hover = default; QueueRedraw(); }
	}

	void Press( Vector2 p )
	{
		var h = HitTest( p );
		switch ( h.Kind )
		{
			case Hit.GripMove:
				_dragMove = true; _dragStart = p; _offsetStart = GeppettoDialState.Offset; break;
			case Hit.GripSize:
				_dragSize = true; _dragStart = p; _scaleStart = GeppettoDialState.Scale; break;
			case Hit.Core:
				GeppettoDialState.ToggleMaterials(); break;
			case Hit.Tile:
				ClickTile( h.Index ); break;
			case Hit.Tab:
				if ( Tabs[h.Index].Mode == "pick" )
				{
					_picking = !_picking;
					GD.Print( _picking ? "[Geppetto] Pick: click anywhere in the 3D view to take its colour." : "[Geppetto] Pick cancelled." );
					GeppettoDialState.Touch();
				}
				else { _picking = false; GeppettoDialState.SetMatMode( Tabs[h.Index].Mode ); }
				break;
			case Hit.Ball:
				var ball = PageMaterials()[h.Index];
				if ( ball.IsNew ) { GeppettoDialState.AskNewMaterial(); break; }
				GeppettoDialState.PickMaterial( ball ); SyncHsl(); break;
			case Hit.Recent:
				GeppettoDialState.PickRecent( h.Index ); SyncHsl(); break;
			case Hit.Shade:
				var c = ShadeColor( h.Index, h.Sub );
				GeppettoDialState.PickMaterial( GeppettoDialMaterial.FromColor( c ) ); SyncHsl(); break;
			case Hit.Slider:
				_dragSlider = true; _slider = h.Index; SetSlider( h.Index, h.Value, false ); break;
		}
	}

	void Release()
	{
		if ( _dragMove || _dragSize ) GeppettoDialState.Save();
		if ( _dragSlider ) GeppettoDialState.PickMaterial( GeppettoDialState.Current );   // remember the mixed colour
		_dragMove = _dragSize = _dragSlider = false;
	}

	void ClickTile( int i )
	{
		var tiles = Tiles();
		if ( i < 0 || i >= tiles.Count ) return;
		if ( tiles[i].Back ) { GeppettoDialState.Back(); return; }
		switch ( GeppettoDialState.Level )
		{
			case 0: GeppettoDialState.OpenWorkspace( i ); break;
			case 1: GeppettoDialState.OpenStageTools( i - 1 ); break;
			default: GeppettoDialState.PickTool( tiles[i].Name ); break;
		}
	}

	void SetSlider( int j, float t, bool remember )
	{
		if ( j == 0 ) _h = t * 360f; else if ( j == 1 ) _sat = t; else _lit = t;
		GeppettoDialState.PickMaterial( GeppettoDialMaterial.FromColor( FromHsl( _h, _sat, _lit ) ), remember );
	}

	static void SyncHsl()
	{
		var c = GeppettoDialState.Current?.Color ?? Colors.Gray;
		ToHsl( c, out _h, out _sat, out _lit );
	}

	public override void _Input( InputEvent e )
	{
		if ( !IsVisibleInTree() || _surface == null ) return;

		// 1–8 pick a recent material while this viewport has focus.
		if ( e is InputEventKey k && k.Pressed && !k.Echo && !k.CtrlPressed && !k.AltPressed && !k.ShiftPressed && !k.MetaPressed
			&& k.Keycode >= Key.Key1 && k.Keycode <= Key.Key8 && _surface.HasFocus() )
		{
			GeppettoDialState.PickRecent( (int)( k.Keycode - Key.Key1 ) );
			SyncHsl();
			GetViewport().SetInputAsHandled();
			return;
		}

		// PICK: the next left click on the 3D view takes the colour under it.
		if ( _picking && e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left && Viewport3D != null )
		{
			var local = GetGlobalTransformWithCanvas().AffineInverse() * mb.Position;
			if ( !GetRect().HasPoint( local + Position ) || HitTest( local ).Kind != Hit.None ) return;
			var img = Viewport3D.GetTexture().GetImage();
			var px = (Vector2I)( local * ( (Vector2)img.GetSize() / Size ) );
			px = px.Clamp( Vector2I.Zero, img.GetSize() - Vector2I.One );
			var c = img.GetPixelv( px ); c.A = 1;
			_picking = false;
			GeppettoDialState.PickMaterial( GeppettoDialMaterial.FromColor( c ) );
			SyncHsl();
			GetViewport().SetInputAsHandled();
		}
	}

	// =========================================================================================
	// Drawing
	// =========================================================================================

	static float EaseOut( float t ) { t = Mathf.Clamp( t, 0, 1 ); return 1 - ( 1 - t ) * ( 1 - t ) * ( 1 - t ); }

	public override void _Draw()
	{
		Layout();
		DrawShadow( 0, RPlate, 0, 360 );
		if ( GeppettoDialState.MatOpen ) DrawMaterialRing();
		DrawPlate();
		DrawTiles();
		DrawCore();
		DrawGrip( GripMoveA, "__move", _hover.Kind == Hit.GripMove || _dragMove );
		DrawGrip( GripSizeA, "__size", _hover.Kind == Hit.GripSize || _dragSize );
		if ( GeppettoDialState.MatOpen ) DrawNameTag();
	}

	/// <summary>A soft drop shadow under a disc or sector: stacked low-alpha sectors, offset down.</summary>
	void DrawShadow( float r0, float r1, float a0, float a1, float alpha = 1 )
	{
		var off = new Vector2( 0, 5 * _s );
		for ( int k = 0; k < 5; k++ )
			Sector( r0, r1 + 1.5f * k, a0 - 0.4f * k, a1 + 0.4f * k, _ => new Color( 0, 0, 0, 0.07f * alpha ), off );
	}

	void DrawPlate()
	{
		Color c0 = new( "121316" ), c1 = new( "1b1d22" ), c2 = new( "2a2d33" );
		Sector( 0, RPlate, 0, 360, r => r < RPlate * 0.6f ? c0.Lerp( c1, r / ( RPlate * 0.6f ) ) : c1.Lerp( c2, ( r - RPlate * 0.6f ) / ( RPlate * 0.4f ) ), default, 4 );
		DrawArc( _ctr, RPlate * _s, 0, Mathf.Tau, 96, new Color( 1, 1, 1, 0.07f ), 1.2f * _s, true );
		DrawArc( _ctr, ( RPlate - 1.5f ) * _s, Mathf.DegToRad( 200 ), Mathf.DegToRad( 340 ), 48, new Color( 1, 1, 1, 0.06f ), 1f * _s, true );
	}

	void DrawTiles()
	{
		var tiles = Tiles();
		int n = tiles.Count;
		float step = 360f / n;
		var now = Time.GetTicksMsec();
		for ( int i = 0; i < n; i++ )
		{
			var (name, icon, active, back) = tiles[i];
			float t = EaseOut( ( now - GeppettoDialState.FanStart - i * StaggerMs ) / FanMs );
			float rot = -24 * ( 1 - t ), k = Mathf.Lerp( 0.82f, 1, t ), alpha = t;
			if ( alpha <= 0.001f ) continue;

			float mid = -90 + i * step + rot;
			float a0 = mid - step / 2 + 1.1f, a1 = mid + step / 2 - 1.1f;
			float r0 = ( RCore + 4 ) * k, r1 = RRing * k;
			bool hover = _hover.Kind == Hit.Tile && _hover.Index == i;
			var g = active ? GActive : back ? GBack : hover ? GHover : GNormal;
			Color GradAt( float r )
			{
				float u = r / RRing;
				var col = u <= 0.42f ? g.In : g.In.Lerp( g.Out, ( u - 0.42f ) / 0.58f );
				col.A *= alpha;
				return col;
			}
			Sector( r0, r1, a0, a1, GradAt, default, 3 );
			// bevel: light outer lip, dark inner lip
			DrawArc( _ctr, ( r1 - 0.8f ) * _s, Mathf.DegToRad( a0 ), Mathf.DegToRad( a1 ), 24, new Color( 1, 1, 1, ( active ? 0.35f : 0.13f ) * alpha ), 1.4f * _s, true );
			DrawArc( _ctr, ( r0 + 0.8f ) * _s, Mathf.DegToRad( a0 ), Mathf.DegToRad( a1 ), 16, new Color( 0, 0, 0, 0.45f * alpha ), 1.4f * _s, true );

			// icon + small-caps label, centred on the tile
			float ir = ( ( RCore + RRing ) / 2 + 4 ) * k;
			var ic = _ctr + _s * ir * Dir( mid );
			var labelCol = active ? LabelActive : LabelCol; labelCol.A *= alpha;
			var iconCol = active ? LabelActive : back ? LabelCol : IconCol; iconCol.A *= alpha;
			int ipx = Mathf.Max( 8, Mathf.RoundToInt( 20 * _s * k ) );
			var tex = GeppettoDialData.Icon( icon, iconCol, ipx );
			DrawTextureRect( tex, new Rect2( ic + new Vector2( -ipx / 2f, -15 * _s * k ), new Vector2( ipx, ipx ) ), false );

			// Width available at the label: the tile's chord there, less a margin.
			float lr = ir + 10;
			float chord = 2 * lr * _s * Mathf.Sin( Mathf.DegToRad( Mathf.Min( step - 2.2f, 170 ) / 2 ) ) - 8 * _s;
			float baseSize = ( n > 6 ? 7.5f : 8.5f ) * _s * k;
			DrawFitted( _bold, GeppettoDialData.Clean( name ).ToUpperInvariant(), ic + new Vector2( 0, 15 * _s * k ), chord, baseSize, 6f * _s, labelCol );
		}
	}

	void DrawCore()
	{
		Color c0 = new( "25282e" ), c1 = new( "101114" );
		Sector( 0, RCore, 0, 360, r => c0.Lerp( c1, r / RCore ), default, 3 );
		bool hover = _hover.Kind == Hit.Core;
		DrawArc( _ctr, RCore * _s, 0, Mathf.Tau, 64, hover || GeppettoDialState.MatOpen ? Gep : new Color( "3a3e46" ), 1.3f * _s, true );

		var m = GeppettoDialState.Current;
		if ( m != null )
		{
			int bpx = Mathf.RoundToInt( 30 * _s );
			DrawTextureRect( GeppettoDialData.Ball( m, bpx ), new Rect2( _ctr + new Vector2( -bpx / 2f, -2 * _s - bpx / 2f ), new Vector2( bpx, bpx ) ), false );
			DrawFitted( _font, m.Name.ToUpperInvariant(), _ctr + new Vector2( 0, -26 * _s ), 66 * _s, 8 * _s, 6 * _s, CoreName, maxLines: 1 );
		}

		DrawFitted( _font, CoreReadout(), _ctr + new Vector2( 0, 30 * _s ), 70 * _s, 9 * _s, 6 * _s, CoreText, maxLines: 1 );
	}

	/// <summary>What the core says: the hovered item's name, else "Workspace > Tool".</summary>
	string CoreReadout()
	{
		switch ( _hover.Kind )
		{
			case Hit.Tile:
				var tiles = Tiles();
				if ( _hover.Index < tiles.Count ) return tiles[_hover.Index].Back ? "Back" : GeppettoDialData.Clean( tiles[_hover.Index].Name );
				break;
			case Hit.Core: return GeppettoDialState.MatOpen ? "Close materials" : "Materials";
			case Hit.GripMove: return "Move dial";
			case Hit.GripSize: return "Resize dial";
			case Hit.Tab: return Tabs[_hover.Index].Mode == "pick" ? "Pick from view" : Tabs[_hover.Index].Label.ToLowerInvariant().Capitalize();
			case Hit.Ball:
				var page = PageMaterials();
				if ( _hover.Index < page.Count ) return page[_hover.Index].Name;
				break;
			case Hit.Recent:
				if ( _hover.Index < GeppettoDialState.Recent.Count ) return $"{_hover.Index + 1}  {GeppettoDialState.Recent[_hover.Index].Name}";
				break;
			case Hit.Shade: return "#" + ShadeColor( _hover.Index, _hover.Sub ).ToHtml( false ).ToUpperInvariant();
			case Hit.Slider: return new[] { "Hue", "Saturation", "Lightness" }[_hover.Index];
		}
		if ( _picking ) return "Click the view";
		return $"{GeppettoDialState.Workspace.Name} > {GeppettoDialData.Clean( GeppettoDialState.CurrentTool )}";
	}

	void DrawGrip( float deg, string icon, bool hot )
	{
		var c = P( GripR, deg );
		DrawCircle( c + new Vector2( 0, 3 * _s ), ( GripRadius + 1.5f ) * _s, new Color( 0, 0, 0, 0.3f ), true, -1, true );
		DrawCircle( c, GripRadius * _s, GripFill, true, -1, true );
		DrawCircle( c, GripRadius * _s, hot ? Gep : GripStroke, false, 1.2f * _s, true );
		int px = Mathf.Max( 8, Mathf.RoundToInt( 14 * _s ) );
		DrawTextureRect( GeppettoDialData.Icon( GeppettoDialData.IconPath( icon, null ), hot ? Gep : LabelCol, px, 2.2f ),
			new Rect2( c - new Vector2( px, px ) / 2, new Vector2( px, px ) ), false );
	}

	// ---- material ring ----

	void DrawMaterialRing()
	{
		float t = EaseOut( ( Time.GetTicksMsec() - GeppettoDialState.RingStart ) / FanMs );
		float rot = -24 * ( 1 - t ), k = Mathf.Lerp( 0.82f, 1, t ), alpha = t;
		// The ring pops open around the core: scale and rotation are applied about the dial centre.
		_xf = new Transform2D( Mathf.DegToRad( rot ), new Vector2( k, k ), 0, _ctr );
		DrawSetTransformMatrix( _xf );
		var saved = _ctr; _ctr = Vector2.Zero;

		DrawShadow( MatR0, MatR1, MatA0, MatA1, alpha );
		var body = RingBody; body.A *= alpha;
		Sector( MatR0, MatR1, MatA0, MatA1, _ => body );
		DrawArc( _ctr, MatR1 * _s, Mathf.DegToRad( MatA0 ), Mathf.DegToRad( MatA1 ), 64, new Color( 1, 1, 1, 0.08f * alpha ), 1.2f * _s, true );
		DrawArc( _ctr, MatR0 * _s, Mathf.DegToRad( MatA0 ), Mathf.DegToRad( MatA1 ), 48, new Color( 0, 0, 0, 0.5f * alpha ), 1.2f * _s, true );

		DrawTabs( alpha );
		switch ( GeppettoDialState.MatMode )
		{
			case "materials": DrawBalls( alpha ); break;
			case "shades": DrawShades( alpha ); break;
			case "hsl": DrawSliders( alpha ); break;
		}
		DrawRecents( alpha );

		_ctr = saved;
		_xf = Transform2D.Identity;
		DrawSetTransform( Vector2.Zero, 0, Vector2.One );
	}

	void DrawTabs( float alpha )
	{
		for ( int i = 0; i < Tabs.Length; i++ )
		{
			var tab = Tabs[i];
			bool active = tab.Mode == "pick" ? _picking : !_picking && GeppettoDialState.MatMode == tab.Mode;
			bool hover = _hover.Kind == Hit.Tab && _hover.Index == i;
			Color a = active ? new Color( "f6bd5e" ) : hover ? new Color( "474c56" ) : new Color( "3a3e46" );
			Color b = active ? new Color( "d48a24" ) : hover ? new Color( "2d3139" ) : new Color( "26292f" );
			a.A *= alpha; b.A *= alpha;
			Sector( TabR0, TabR1, tab.A0, tab.A1, r => b.Lerp( a, ( r - TabR0 ) / ( TabR1 - TabR0 ) ), default, 2 );
			var col = active ? LabelActive : LabelCol; col.A *= alpha;
			ArcText( tab.Label, ( TabR0 + TabR1 ) / 2, ( tab.A0 + tab.A1 ) / 2, tab.A1 - tab.A0, 8 * _s, col );
		}
	}

	void DrawBalls( float alpha )
	{
		var page = PageMaterials();
		for ( int i = 0; i < page.Count; i++ )
		{
			var c = P( RBalls, BallAngle( i ) );
			bool hover = _hover.Kind == Hit.Ball && _hover.Index == i;
			bool current = GeppettoDialState.Current?.Name == page[i].Name;
			float br = BallR * ( hover ? 1.08f : 1f );
			int px = Mathf.RoundToInt( br * 2 * _s );
			if ( page[i].IsNew )
			{
				// the "+" ball: an outlined socket with a plus, makes a new material
				DrawCircle( c, br * _s, new Color( RingBody, alpha ), true, -1, true );
				DrawCircle( c, br * _s, new Color( hover ? Gep : CoreName, alpha ), false, 1.4f * _s, true );
				float k = br * 0.45f * _s;
				var pc = new Color( hover ? Gep : CoreText, alpha );
				DrawLine( c - new Vector2( k, 0 ), c + new Vector2( k, 0 ), pc, 1.8f * _s, true );
				DrawLine( c - new Vector2( 0, k ), c + new Vector2( 0, k ), pc, 1.8f * _s, true );
				continue;
			}
			DrawTextureRect( GeppettoDialData.Ball( page[i], px ), new Rect2( c - new Vector2( px, px ) / 2, new Vector2( px, px ) ), false, new Color( 1, 1, 1, alpha ) );
			if ( current ) DrawCircle( c, ( br + 2.5f ) * _s, new Color( Gep, alpha ), false, 1.6f * _s, true );
		}
		int pages = Math.Max( 1, ( GeppettoDialState.Materials.Count + BallsPerPage - 1 ) / BallsPerPage );
		if ( pages > 1 )
		{
			var col = CoreName; col.A *= alpha;
			// page dots along the inside of the ball track
			for ( int p = 0; p < pages; p++ )
			{
				var c = P( RBalls - BallR - 7, BallsA1 - 3 - ( pages - 1 - p ) * 3.2f );
				DrawCircle( c, 1.8f * _s, p == GeppettoDialState.MatPage ? new Color( Gep, alpha ) : col, true, -1, true );
			}
		}
	}

	void DrawShades( float alpha )
	{
		for ( int h = 0; h < 16; h++ )
			for ( int k = 0; k < 5; k++ )
			{
				var col = ShadeColor( h, k ); col.A = alpha;
				float a0 = BallsA0 + h * ShadeStep + 0.35f, a1 = BallsA0 + ( h + 1 ) * ShadeStep - 0.35f;
				float r0 = 170 + k * 12 + 0.6f, r1 = 170 + ( k + 1 ) * 12 - 0.6f;
				Sector( r0, r1, a0, a1, _ => col );
				if ( _hover.Kind == Hit.Shade && _hover.Index == h && _hover.Sub == k )
					SectorOutline( r0, r1, a0, a1, new Color( 1, 1, 1, 0.9f * alpha ) );
			}
	}

	void DrawSliders( float alpha )
	{
		for ( int j = 0; j < 3; j++ )
		{
			float r = SliderR[j];
			Color At( float u ) => j switch
			{
				0 => FromHsl( u * 360, _sat, _lit ),
				1 => FromHsl( _h, u, _lit ),
				_ => FromHsl( _h, _sat, u ),
			};
			const int seg = 48;
			for ( int s = 0; s < seg; s++ )
			{
				float u0 = s / (float)seg, u1 = ( s + 1 ) / (float)seg;
				var c0 = At( u0 ); c0.A = alpha;
				Sector( r - 5, r + 5, Mathf.Lerp( BallsA0, BallsA1, u0 ), Mathf.Lerp( BallsA0, BallsA1, u1 ) + 0.2f, _ => c0 );
			}
			float val = j == 0 ? _h / 360f : j == 1 ? _sat : _lit;
			var knob = P( r, Mathf.Lerp( BallsA0, BallsA1, val ) );
			DrawCircle( knob, 7 * _s, new Color( "101114", alpha ), true, -1, true );
			DrawCircle( knob, 7 * _s, new Color( 1, 1, 1, 0.9f * alpha ), false, 1.6f * _s, true );
		}
	}

	void DrawRecents( float alpha )
	{
		for ( int i = 0; i < GeppettoDialState.Recent.Count; i++ )
		{
			var m = GeppettoDialState.Recent[i];
			var c = P( RRecent, RecentAngle( i ) );
			int px = Mathf.RoundToInt( RecentR * 2 * _s );
			DrawTextureRect( GeppettoDialData.Ball( m, px ), new Rect2( c - new Vector2( px, px ) / 2, new Vector2( px, px ) ), false, new Color( 1, 1, 1, alpha ) );
			bool hot = _hover.Kind == Hit.Recent && _hover.Index == i;
			if ( hot || GeppettoDialState.Current?.Name == m.Name )
				DrawCircle( c, ( RecentR + 2 ) * _s, new Color( hot ? Colors.White : Gep, alpha ), false, 1.3f * _s, true );
			var nc = CoreName; nc.A *= alpha;
			DrawFitted( _bold, ( i + 1 ).ToString(), P( RRecent + 15, RecentAngle( i ) ), 10 * _s, 7.5f * _s, 6 * _s, nc, maxLines: 1 );
		}
	}

	/// <summary>Beside the open ring: the current material's ball, name and hex.</summary>
	void DrawNameTag()
	{
		var m = GeppettoDialState.Current;
		if ( m == null ) return;
		float alpha = EaseOut( ( Time.GetTicksMsec() - GeppettoDialState.RingStart ) / FanMs );
		var anchor = P( MatR1 + 10, 4 );
		float fs = 10 * _s, hs = 8.5f * _s;
		string name = m.Name, hex = m.Hex;
		float tw = Mathf.Max( _bold.GetStringSize( name, HorizontalAlignment.Left, -1, (int)fs ).X, _font.GetStringSize( hex, HorizontalAlignment.Left, -1, (int)hs ).X );
		// keep the tag on screen: the text area shrinks if the viewport is narrow
		float maxW = Mathf.Max( 40 * _s, Size.X - anchor.X - 40 * _s );
		tw = Mathf.Min( tw, maxW );
		var box = new Rect2( anchor + new Vector2( 0, -18 * _s ), new Vector2( 34 * _s + tw + 10 * _s, 36 * _s ) );
		var bg = RingBody; bg.A *= alpha;
		DrawStyleBox( new StyleBoxFlat { BgColor = bg, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, BorderColor = new Color( "3a3e46", alpha ), BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1 }, box );
		int px = Mathf.RoundToInt( 22 * _s );
		DrawTextureRect( GeppettoDialData.Ball( m, px ), new Rect2( box.Position + new Vector2( 6 * _s, ( box.Size.Y - px ) / 2 ), new Vector2( px, px ) ), false, new Color( 1, 1, 1, alpha ) );
		var tx = box.Position.X + 34 * _s;
		DrawFittedLeft( _bold, name, new Vector2( tx, box.Position.Y + 15 * _s ), tw, fs, new Color( CoreText, alpha ) );
		DrawFittedLeft( _font, hex, new Vector2( tx, box.Position.Y + 28 * _s ), tw, hs, new Color( CoreName, alpha ) );
	}

	// =========================================================================================
	// Primitives
	// =========================================================================================

	/// <summary>Fills an annular sector (r in mock units, angles in degrees) with a colour that varies by
	/// radius, as one triangle array. <paramref name="radial"/> rings let the gradient follow the radius.</summary>
	void Sector( float r0, float r1, float a0, float a1, Func<float, Color> colAt, Vector2 offset = default, int radial = 1 )
	{
		int seg = Math.Max( 3, (int)Mathf.Ceil( Mathf.Abs( a1 - a0 ) / 3f ) );
		int rows = radial + 1;
		var pts = new Vector2[( seg + 1 ) * rows];
		var cols = new Color[pts.Length];
		for ( int ri = 0; ri < rows; ri++ )
		{
			float r = Mathf.Lerp( r0, r1, ri / (float)radial );
			var col = colAt( r );
			for ( int s = 0; s <= seg; s++ )
			{
				pts[ri * ( seg + 1 ) + s] = _ctr + offset + _s * r * Dir( Mathf.Lerp( a0, a1, s / (float)seg ) );
				cols[ri * ( seg + 1 ) + s] = col;
			}
		}
		var idx = new int[seg * radial * 6];
		int n = 0;
		for ( int ri = 0; ri < radial; ri++ )
			for ( int s = 0; s < seg; s++ )
			{
				int a = ri * ( seg + 1 ) + s, b = a + 1, c = a + seg + 1, d = c + 1;
				idx[n++] = a; idx[n++] = b; idx[n++] = d;
				idx[n++] = a; idx[n++] = d; idx[n++] = c;
			}
		RenderingServer.CanvasItemAddTriangleArray( GetCanvasItem(), idx, pts, cols );
	}

	void SectorOutline( float r0, float r1, float a0, float a1, Color c )
	{
		DrawArc( _ctr, r0 * _s, Mathf.DegToRad( a0 ), Mathf.DegToRad( a1 ), 6, c, 1.2f * _s, true );
		DrawArc( _ctr, r1 * _s, Mathf.DegToRad( a0 ), Mathf.DegToRad( a1 ), 6, c, 1.2f * _s, true );
		DrawLine( P( r0, a0 ), P( r1, a0 ), c, 1.2f * _s, true );
		DrawLine( P( r0, a1 ), P( r1, a1 ), c, 1.2f * _s, true );
	}

	/// <summary>
	/// Draws text centred on <paramref name="centre"/> inside <paramref name="maxW"/>: shrink first, then
	/// wrap onto two lines, then elide with "…". Nothing is ever drawn wider than the box.
	/// </summary>
	void DrawFitted( Font font, string text, Vector2 centre, float maxW, float size, float minSize, Color col, int maxLines = 2 )
	{
		var (lines, fs) = Fit( font, text, maxW, size, minSize, maxLines );
		int isz = Mathf.Max( 1, Mathf.RoundToInt( fs ) );
		float lh = font.GetHeight( isz );
		float top = centre.Y - lh * lines.Length / 2f;
		for ( int i = 0; i < lines.Length; i++ )
		{
			float baseline = top + i * lh + font.GetAscent( isz );
			font.DrawString( GetCanvasItem(), new Vector2( centre.X - maxW / 2, baseline ), lines[i], HorizontalAlignment.Center, maxW, isz, col );
		}
	}

	void DrawFittedLeft( Font font, string text, Vector2 baselineLeft, float maxW, float size, Color col )
	{
		var (lines, fs) = Fit( font, text, maxW, size, size * 0.75f, 1 );
		font.DrawString( GetCanvasItem(), baselineLeft, lines[0], HorizontalAlignment.Left, maxW, Mathf.RoundToInt( fs ), col );
	}

	static (string[] Lines, float Size) Fit( Font font, string text, float maxW, float size, float minSize, int maxLines )
	{
		float W( string s, float fs ) => font.GetStringSize( s, HorizontalAlignment.Left, -1, Mathf.Max( 1, Mathf.RoundToInt( fs ) ) ).X;
		minSize = Mathf.Min( minSize, size );
		for ( float fs = size; fs >= minSize - 0.01f; fs -= 0.5f )
			if ( W( text, fs ) <= maxW ) return ( new[] { text }, fs );

		if ( maxLines >= 2 && text.Contains( ' ' ) )
		{
			// best two-line split: the one whose longer line is shortest
			var words = text.Split( ' ' );
			(string, string) best = default; float bestW = float.MaxValue;
			for ( int i = 1; i < words.Length; i++ )
			{
				string l1 = string.Join( ' ', words[..i] ), l2 = string.Join( ' ', words[i..] );
				float w = Mathf.Max( W( l1, size ), W( l2, size ) );
				if ( w < bestW ) { bestW = w; best = ( l1, l2 ); }
			}
			for ( float fs = size; fs >= minSize - 0.01f; fs -= 0.5f )
				if ( W( best.Item1, fs ) <= maxW && W( best.Item2, fs ) <= maxW ) return ( new[] { best.Item1, best.Item2 }, fs );
			return ( new[] { Elide( best.Item1, maxW, minSize, W ), Elide( best.Item2, maxW, minSize, W ) }, minSize );
		}
		return ( new[] { Elide( text, maxW, minSize, W ) }, minSize );
	}

	static string Elide( string s, float maxW, float fs, Func<string, float, float> w )
	{
		if ( w( s, fs ) <= maxW ) return s;
		for ( int n = s.Length - 1; n > 0; n-- )
		{
			var t = s[..n].TrimEnd() + "…";
			if ( w( t, fs ) <= maxW ) return t;
		}
		return "…";
	}

	/// <summary>Text set along an arc (the material tabs): letters rotated to the tangent, centred on
	/// <paramref name="midDeg"/>, shrunk until it fits the tab's span.</summary>
	void ArcText( string text, float r, float midDeg, float spanDeg, float size, Color col )
	{
		float avail = Mathf.DegToRad( spanDeg - 2 ) * r * _s;
		int isz = Mathf.RoundToInt( size );
		float Width( int sz ) => text.Sum( ch => _bold.GetCharSize( ch, sz ).X ) + ( text.Length - 1 ) * 0.6f * _s;
		while ( isz > 5 && Width( isz ) > avail ) isz--;
		float total = Width( isz );
		float ascent = _bold.GetAscent( isz ), descent = _bold.GetDescent( isz );
		float pos = -total / 2;
		foreach ( var ch in text )
		{
			float cw = _bold.GetCharSize( ch, isz ).X;
			float deg = midDeg + Mathf.RadToDeg( ( pos + cw / 2 ) / ( r * _s ) );
			var at = _ctr + _s * r * Dir( deg );
			DrawSetTransformMatrix( CurrentTransform() * new Transform2D( Mathf.DegToRad( deg + 90 ), at ) );
			_bold.DrawChar( GetCanvasItem(), new Vector2( -cw / 2, ( ascent - descent ) / 2 ), ch, isz, col );
			pos += cw + 0.6f * _s;
		}
		DrawSetTransformMatrix( CurrentTransform() );
	}

	// The transform the material ring is drawn under (pop-open about the dial centre); ArcText composes with it.
	Transform2D _xf = Transform2D.Identity;
	Transform2D CurrentTransform() => _xf;

	// ---- colour helpers ----

	static Color FromHsl( float hDeg, float s, float l )
	{
		float c = ( 1 - Mathf.Abs( 2 * l - 1 ) ) * s;
		float hp = ( ( hDeg % 360 + 360 ) % 360 ) / 60f;
		float x = c * ( 1 - Mathf.Abs( hp % 2 - 1 ) );
		(float r, float g, float b) = hp switch
		{
			< 1 => ( c, x, 0f ), < 2 => ( x, c, 0f ), < 3 => ( 0f, c, x ),
			< 4 => ( 0f, x, c ), < 5 => ( x, 0f, c ), _ => ( c, 0f, x ),
		};
		float m = l - c / 2;
		return new Color( r + m, g + m, b + m );
	}

	static void ToHsl( Color col, out float h, out float s, out float l )
	{
		float max = Mathf.Max( col.R, Mathf.Max( col.G, col.B ) ), min = Mathf.Min( col.R, Mathf.Min( col.G, col.B ) );
		l = ( max + min ) / 2;
		float d = max - min;
		s = d < 1e-5f ? 0 : d / ( 1 - Mathf.Abs( 2 * l - 1 ) );
		h = col.H * 360f;
	}
}
#endif
