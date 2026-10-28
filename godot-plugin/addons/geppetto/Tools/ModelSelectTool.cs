#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// The Model workspace's viewport tool, as s&box's Model picking has it: vertex / edge / face mode
/// (1 / 2 / 3), click to select, Shift adds or toggles, Ctrl removes, Alt+click picks an edge loop
/// (Ctrl+Alt a ring), drag a box, click empty space to clear. Hovered and selected elements are drawn
/// over the viewport. Clicking another body starts editing that one.
///
/// Picking is against the rest mesh (what the .effigy holds); a posed skinned character will not line up.
/// </summary>
public sealed class GeppettoModelSelectTool : IGeppettoViewportTool
{
	readonly GeppettoCharacter _character;
	readonly GeppettoModelEdit _edit;

	public GeppettoModelSelectTool( GeppettoCharacter character, GeppettoModelEdit edit )
	{
		_character = character;
		_edit = edit;
		_edit.Changed += OnEditChanged;
	}

	public string Name => $"Model: {_edit.Mode}";

	public void Exit() => _edit.Changed -= OnEditChanged;

	void OnEditChanged() => GeppettoTools.Redraw();

	// =========================================================================================
	// Space: effigy inches Z-up inside the Body node's transform, Godot metres Y-up outside
	// =========================================================================================

	Transform3D BodyXform =>
		( _character.Root as Node )?.FindChild( "Body", true, false ) is Node3D n ? n.GlobalTransform
		: _character.Root is Node3D r ? r.GlobalTransform : Transform3D.Identity;

	static Vector3 ToGodot( Vec3 p ) => EffigyMeshBridge.TransformPosition( p.x, p.y, p.z );
	static Vec3 ToEffigy( Vector3 g ) => new Vec3( g.Z, g.X, g.Y ) / EffigyMeshBridge.ScaleToMetres;

	Vector3 World( Vec3 p ) => BodyXform * ToGodot( p );

	(Vec3 Origin, Vec3 Dir) Ray( Camera3D cam, Vector2 screen )
	{
		var inv = BodyXform.AffineInverse();
		var o = inv * cam.ProjectRayOrigin( screen );
		var d = inv.Basis * cam.ProjectRayNormal( screen );
		return (ToEffigy( o ), new Vec3( d.Z, d.X, d.Y ).Normal);
	}

	// =========================================================================================
	// Picking
	// =========================================================================================

	readonly Dictionary<PolyMesh, (int Rev, MeshBVH Bvh)> _bvh = new( ReferenceEqualityComparer.Instance );

	MeshBVH Bvh( PolyMesh m, int rev )
	{
		if ( _bvh.TryGetValue( m, out var c ) && c.Rev == rev ) return c.Bvh;
		if ( _bvh.Count > 32 ) _bvh.Clear();
		var b = MeshBVH.Build( m );
		_bvh[m] = (rev, b);
		return b;
	}

	/// <summary>The mesh to pick a body against: the live session mesh for the edited body (hidden faces
	/// left out), the rebuilt mesh for the others.</summary>
	(PolyMesh Mesh, int[]? Map, int Rev) PickMesh( Body b )
	{
		if ( b.Id == _edit.BodyId && _edit.Session is { } s ) return (s.VisibleMesh, s.VisibleFaceMap, s.Revision);
		return (b.Mesh, null, 0);
	}

	record struct Pick( Body Body, int Face, Vec3 Point, float Distance );

	Pick? Raycast( Vec3 origin, Vec3 dir )
	{
		Pick? best = null;
		var doc = _edit.Doc;
		foreach ( var b in doc.Studio.Bodies )
		{
			if ( !b.Visible || b.Mesh is not { FaceCount: > 0 } ) continue;
			var (m, map, rev) = PickMesh( b );
			if ( Bvh( m, rev ).Raycast( m, origin, dir ) is not { } hit ) continue;
			if ( best is { } cur && cur.Distance <= hit.Distance ) continue;
			int face = map != null && hit.FaceIndex < map.Length ? map[hit.FaceIndex] : hit.FaceIndex;
			best = new Pick( b, face, hit.Point, hit.Distance );
		}
		return best;
	}

	enum Kind { None, Vertex, Edge, Face }
	readonly record struct Element( Kind Kind, int Index, EdgeKey Edge );

	Element Resolve( Pick p )
	{
		var mesh = _edit.Session!.Mesh;
		if ( p.Face < 0 || p.Face >= mesh.FaceCount ) return default;
		var idx = mesh.Faces[p.Face].Indices;
		switch ( _edit.Mode )
		{
			case EditElement.Vertex:
			{
				int bestV = idx[0]; float bd = float.MaxValue;
				foreach ( var v in idx ) { float d = ( mesh.Positions[v] - p.Point ).Length; if ( d < bd ) { bd = d; bestV = v; } }
				return new Element( Kind.Vertex, bestV, default );
			}
			case EditElement.Edge:
			{
				EdgeKey bestE = default; float bd = float.MaxValue;
				for ( int i = 0; i < idx.Length; i++ )
				{
					int a = idx[i], c = idx[( i + 1 ) % idx.Length];
					float d = SegmentDistance( p.Point, mesh.Positions[a], mesh.Positions[c] );
					if ( d < bd ) { bd = d; bestE = new EdgeKey( a, c ); }
				}
				return new Element( Kind.Edge, -1, bestE );
			}
			default:
				return new Element( Kind.Face, p.Face, default );
		}
	}

	static float SegmentDistance( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		float len2 = Vec3.Dot( ab, ab );
		float t = len2 > 0f ? Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f ) : 0f;
		return ( p - ( a + ab * t ) ).Length;
	}

	/// <summary>What is under the cursor on the edited body; switches body when <paramref name="switchBody"/>.</summary>
	Element Under( Camera3D cam, Vector2 screen, bool switchBody, out bool hitAnything )
	{
		var (o, d) = Ray( cam, screen );
		var p = Raycast( o, d );
		hitAnything = p != null;
		if ( p is not { } pick ) return default;
		if ( pick.Body.Id != _edit.BodyId )
		{
			if ( !switchBody ) return default;
			if ( !_edit.Begin( pick.Body.Id ) ) return default;
		}
		return _edit.Session == null ? default : Resolve( pick );
	}

	// =========================================================================================
	// Input
	// =========================================================================================

	Element _hover;
	Vector2? _press;
	Vector2 _dragTo;
	bool _dragging;
	Camera3D? _camera;

	public bool Input( Camera3D camera, InputEvent e )
	{
		_camera = camera;
		switch ( e )
		{
			case InputEventKey { Pressed: true, Echo: false } k when !k.CtrlPressed && !k.AltPressed:
				var mode = k.Keycode switch { Key.Key1 => EditElement.Vertex, Key.Key2 => EditElement.Edge, Key.Key3 => EditElement.Face, _ => (EditElement?)null };
				if ( mode is not { } m ) return false;
				_edit.SetMode( m );
				_hover = default;
				return true;

			case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
				if ( mb.Pressed )
				{
					_press = mb.Position;
					_dragTo = mb.Position;
					_dragging = false;
					return true;
				}
				if ( _press is not { } start ) return false;
				_press = null;
				if ( _dragging ) { _dragging = false; BoxSelect( camera, start, mb.Position, mb.ShiftPressed, mb.CtrlPressed ); }
				else Click( camera, mb.Position, mb.ShiftPressed, mb.CtrlPressed, mb.AltPressed );
				return true;

			case InputEventMouseMotion mm:
				if ( _press is { } p )
				{
					_dragTo = mm.Position;
					if ( !_dragging && ( mm.Position - p ).Length() > 4f ) _dragging = true;
					GeppettoTools.Redraw();
					return true;
				}
				var h = _edit.Session == null && !_edit.EnsureSessionQuiet() ? default : Under( camera, mm.Position, false, out _ );
				if ( h != _hover ) { _hover = h; GeppettoTools.Redraw(); }
				return false;
		}
		return false;
	}

	void Click( Camera3D cam, Vector2 at, bool shift, bool ctrl, bool alt )
	{
		var el = Under( cam, at, switchBody: true, out _ );
		var s = _edit.Session;
		if ( s == null ) return;
		if ( el.Kind == Kind.None )
		{
			if ( !shift && !ctrl ) s.ClearSelection();
			_edit.SelectionChanged();
			return;
		}
		var how = ctrl ? MeshEditSession.Combine.Remove : shift ? MeshEditSession.Combine.Toggle : MeshEditSession.Combine.Replace;
		switch ( el.Kind )
		{
			case Kind.Vertex: s.SelectVertex( el.Index, how ); break;
			case Kind.Edge:
				if ( alt && ctrl ) s.SelectEdgeRing( el.Edge, shift ? MeshEditSession.Combine.Add : MeshEditSession.Combine.Replace );
				else if ( alt ) s.SelectEdgeLoop( el.Edge, shift ? MeshEditSession.Combine.Add : MeshEditSession.Combine.Replace );
				else s.SelectEdge( el.Edge, how );
				break;
			default: s.SelectFace( el.Index, how ); break;
		}
		_edit.SelectionChanged();
	}

	/// <summary>Box select: everything of the current mode whose screen position is inside the box and
	/// that the camera can actually see (s&box only picks what is in front).</summary>
	void BoxSelect( Camera3D cam, Vector2 a, Vector2 b, bool shift, bool ctrl )
	{
		if ( _edit.Session == null && !_edit.EnsureSession() ) return;
		var s = _edit.Session!;
		var rect = new Rect2( a, Vector2.Zero ).Expand( b );
		var mesh = s.Mesh;
		var camPos = cam.GlobalPosition;

		bool Inside( Vec3 p, out Vector2 sp )
		{
			var w = World( p );
			sp = default;
			if ( cam.IsPositionBehind( w ) ) return false;
			sp = cam.UnprojectPosition( w );
			return rect.HasPoint( sp );
		}

		bool Visible( Vec3 p, int face )
		{
			var (o, d) = Ray( cam, cam.UnprojectPosition( World( p ) ) );
			var hit = Raycast( o, d );
			if ( hit is not { } h ) return true;
			if ( h.Body.Id != _edit.BodyId ) return h.Distance >= ( p - o ).Length - 0.01f * Math.Max( 1f, mesh.BoundsDiagonal );
			if ( face >= 0 && h.Face == face ) return true;
			return h.Distance >= ( p - o ).Length - 0.01f * Math.Max( 1f, mesh.BoundsDiagonal );
		}

		if ( !shift && !ctrl ) s.ClearSelection();
		var how = ctrl ? MeshEditSession.Combine.Remove : MeshEditSession.Combine.Add;
		switch ( _edit.Mode )
		{
			case EditElement.Vertex:
				for ( int v = 0; v < mesh.VertexCount; v++ )
					if ( s.IsVertexVisible( v ) && Inside( mesh.Positions[v], out _ ) && Visible( mesh.Positions[v], -1 ) ) s.SelectVertex( v, how );
				break;
			case EditElement.Edge:
				foreach ( var e in mesh.BuildEdgeFaces().Keys )
					if ( s.IsVertexVisible( e.A ) && s.IsVertexVisible( e.B )
						&& Inside( mesh.Positions[e.A], out _ ) && Inside( mesh.Positions[e.B], out _ )
						&& Visible( mesh.Positions[e.A], -1 ) && Visible( mesh.Positions[e.B], -1 ) )
						s.SelectEdge( e, how );
				break;
			default:
				foreach ( var f in s.VisibleFaceMap )
				{
					var c = mesh.FaceCentroid( mesh.Faces[f] );
					if ( Inside( c, out _ ) && Visible( c, f ) ) s.SelectFace( f, how );
				}
				break;
		}
		_edit.SelectionChanged();
	}

	// =========================================================================================
	// Overlay
	// =========================================================================================

	static readonly Color SelectedColor = new( 1f, 0.62f, 0.15f );
	static readonly Color HoverColor = new( 0.4f, 0.85f, 1f );
	const int MaxDrawn = 4000;

	public void Draw( Control overlay )
	{
		var cam = _camera ?? overlay.GetViewport()?.GetCamera3D();
		if ( cam != null && _edit.Session is { } s )
		{
			var mesh = s.Mesh;
			int drawn = 0;
			switch ( s.Mode )
			{
				case EditElement.Vertex:
					foreach ( var v in s.SelectedVertices ) { if ( drawn++ > MaxDrawn ) break; DrawVertex( overlay, cam, mesh, v, SelectedColor ); }
					break;
				case EditElement.Edge:
					foreach ( var e in s.SelectedEdges ) { if ( drawn++ > MaxDrawn ) break; DrawEdge( overlay, cam, mesh, e, SelectedColor, 3f ); }
					break;
				default:
					foreach ( var f in s.SelectedFaces ) { if ( drawn++ > MaxDrawn ) break; DrawFace( overlay, cam, mesh, f, SelectedColor ); }
					break;
			}
			switch ( _hover.Kind )
			{
				case Kind.Vertex: DrawVertex( overlay, cam, mesh, _hover.Index, HoverColor ); break;
				case Kind.Edge: DrawEdge( overlay, cam, mesh, _hover.Edge, HoverColor, 3f ); break;
				case Kind.Face: DrawFace( overlay, cam, mesh, _hover.Index, HoverColor ); break;
			}
		}

		if ( _dragging && _press is { } p )
		{
			var r = new Rect2( p, Vector2.Zero ).Expand( _dragTo );
			overlay.DrawRect( r, new Color( HoverColor, 0.12f ), true );
			overlay.DrawRect( r, HoverColor, false, 1f );
		}

		DrawStatus( overlay );
	}

	bool Screen( Camera3D cam, PolyMesh mesh, int v, out Vector2 sp )
	{
		sp = default;
		if ( v < 0 || v >= mesh.VertexCount ) return false;
		var w = World( mesh.Positions[v] );
		if ( cam.IsPositionBehind( w ) ) return false;
		sp = cam.UnprojectPosition( w );
		return true;
	}

	void DrawVertex( Control o, Camera3D cam, PolyMesh mesh, int v, Color c )
	{
		if ( Screen( cam, mesh, v, out var sp ) ) o.DrawRect( new Rect2( sp - new Vector2( 3, 3 ), new Vector2( 6, 6 ) ), c, true );
	}

	void DrawEdge( Control o, Camera3D cam, PolyMesh mesh, EdgeKey e, Color c, float width )
	{
		if ( Screen( cam, mesh, e.A, out var a ) && Screen( cam, mesh, e.B, out var b ) ) o.DrawLine( a, b, c, width, true );
	}

	void DrawFace( Control o, Camera3D cam, PolyMesh mesh, int f, Color c )
	{
		if ( f < 0 || f >= mesh.FaceCount ) return;
		var idx = mesh.Faces[f].Indices;
		var pts = new Vector2[idx.Length + 1];
		for ( int i = 0; i < idx.Length; i++ ) if ( !Screen( cam, mesh, idx[i], out pts[i] ) ) return;
		pts[^1] = pts[0];
		if ( idx.Length >= 3 )
		{
			try { o.DrawColoredPolygon( pts[..^1], new Color( c, 0.25f ) ); }
			catch { /* a self-crossing projected face: the outline still shows it */ }
		}
		o.DrawPolyline( pts, c, 2f, true );
	}

	/// <summary>Mode, count and the controls, wrapped to the overlay's width (never clipped).</summary>
	void DrawStatus( Control overlay )
	{
		string mode = _edit.Mode switch { EditElement.Vertex => "Vertex", EditElement.Edge => "Edge", _ => "Face" };
		string loop = _edit.Mode == EditElement.Edge ? " · Alt+click: loop · Ctrl+Alt+click: ring" : "";
		string text = $"{mode} mode · {_edit.SelectionText()}\nClick: select · Shift: add · Ctrl: remove · Drag: box{loop} · 1/2/3: mode · Esc: stop";
		if ( _edit.LastMessage is { Length: > 0 } msg ) text += "\n" + msg;

		var font = overlay.GetThemeDefaultFont() ?? ThemeDB.FallbackFont;
		int size = overlay.GetThemeDefaultFontSize() > 0 ? overlay.GetThemeDefaultFontSize() : 14;
		const float margin = 10f, pad = 6f;
		float width = Math.Max( 120f, overlay.Size.X - 2 * margin - 2 * pad );
		var extent = font.GetMultilineStringSize( text, HorizontalAlignment.Left, width, size, -1,
			TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive );
		var origin = new Vector2( margin, overlay.Size.Y - margin - extent.Y - 2 * pad );
		overlay.DrawRect( new Rect2( origin, extent + new Vector2( 2 * pad, 2 * pad ) ), new Color( 0, 0, 0, 0.6f ), true );
		var color = _edit.LastRefused ? new Color( 1f, 0.55f, 0.5f ) : Colors.White;
		overlay.DrawMultilineString( font, origin + new Vector2( pad, pad + font.GetAscent( size ) ), text, HorizontalAlignment.Left, width, size, -1, color,
			TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive );
	}
}
#endif
