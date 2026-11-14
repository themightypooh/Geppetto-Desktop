using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Body: the old whole-part pick. Face: the s&amp;box idle selection — a surface, or the rim
/// edge when the click lands within a few pixels of one. Edge: always the nearest rim edge.
/// Vertex: the nearest corner of the surface (for edit ops; s&amp;box has no idle vertex pick).</summary>
public enum SubMode { Body, Face, Edge, Vertex }

/// <summary>A vertex pick: the body and where the corner was. Re-found as the nearest used vertex.</summary>
public readonly record struct VertexRef( string BodyId, Vec3 Point );

/// <summary>Something under the cursor or in the selection, resolved against the current meshes.
/// Mesh is the mesh it was resolved on; a body rebuilt in place since then is skipped, not misdrawn.</summary>
public readonly record struct SubTarget( Body Body, PolyMesh Mesh, SubMode Kind, int Face, EdgeKey Edge, int Vertex, Vec3 Point );

/// <summary>
/// Face / edge / vertex selection on the displayed bodies, ported from the s&amp;box editor's idle
/// selection (EffigyViewport.Selection.cs) so the two behave the same:
/// - A face is its coplanar SURFACE (FaceSurface): a wall a boolean left as fragments lights and
///   selects as one wall, and only its silhouette is outlined.
/// - Edges are the surface's boundary edges, never the seams inside it. In Face mode a click within
///   EdgePickPixels of the rim takes the edge instead of the face.
/// - The selection is kernel references (FaceRef via FacePlane.Capture, EdgeRef), resolved again
///   against every rebuild. Only refs whose body is gone are pruned.
/// - Faces and edges never share a selection; adding one kind drops the other, and says so.
/// - Shift toggles, a plain click replaces, a click on nothing clears. Ctrl removes. Alt takes the
///   face and the one across the wall (MeshRaycast.TryOpposite).
/// - Hidden bodies cannot be picked.
/// Tools take it the way s&amp;box's Feature.ApplyGeometrySelection does: faces= for FaceRef lists,
/// edges= for EdgeRef lists, body names for body lists, plus a MeshSelector select=/element= for
/// edit ops.
/// </summary>
public sealed class SubSelection
{
	/// <summary>How close to a rim, in screen pixels, a Face-mode click counts as the edge (s&amp;box: 10).</summary>
	public const float EdgePickPixels = 10f;

	public SubMode Mode = SubMode.Body;
	public readonly List<Body> Bodies = new();
	public readonly List<FaceRef> Faces = new();
	public readonly List<EdgeRef> Edges = new();
	public readonly List<VertexRef> Vertices = new();
	public SubTarget? Hover;

	/// <summary>The selection resolved against the current meshes, rebuilt on every change.</summary>
	public IReadOnlyList<SubTarget> Resolved => _resolved;

	/// <summary>Picks that no longer resolve (lost after an edit), as s&amp;box's "re-pick it" callout counts them.</summary>
	public int Lost { get; private set; }

	/// <summary>A refusal from the last click, e.g. edges dropped by a shift-clicked face. Cleared by the next click.</summary>
	public string Notice { get; private set; }

	/// <summary>Bumped on every change so the highlight layers can tell.</summary>
	public int Version { get; private set; }

	public bool IsEmpty => Faces.Count == 0 && Edges.Count == 0 && Vertices.Count == 0 && BodyIds.Count == 0;

	/// <summary>The rig workspace (s&amp;box RigMode): a pick takes the WHOLE body under the cursor,
	/// never one of its faces, whatever the element mode. Rigging assigns whole parts.</summary>
	public bool Rig { get; private set; }

	/// <summary>Bodies picked in the rig workspace, by id.</summary>
	public readonly List<string> BodyIds = new();

	/// <summary>The brush or mesh tool in hand (paint, sculpt, meshedit, materialbrush, weight), or
	/// null. While one is, the highlight is not drawn — a wash over the part you are painting hides
	/// the colour you lay down — but the selection itself is kept (s&amp;box DrawIdleSelection).</summary>
	public string Tool { get; private set; }

	public bool HighlightHidden => Tool is not null;

	readonly List<SubTarget> _resolved = new();
	readonly Dictionary<PolyMesh, SurfaceIndex> _indices = new();
	readonly Dictionary<(PolyMesh, int), FaceSurface> _surfaces = new();

	/// <summary>A new document: new bodies and nothing selected.</summary>
	public void Load( IEnumerable<Body> bodies )
	{
		Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear();
		Reload( bodies );
	}

	/// <summary>A rebuild of the same document: keep the references, drop those whose body is gone,
	/// re-resolve the rest (s&amp;box PruneIdleSelection).</summary>
	public void Reload( IEnumerable<Body> bodies )
	{
		Bodies.Clear();
		Bodies.AddRange( bodies.Where( b => b?.Mesh is not null ) );
		_indices.Clear(); _surfaces.Clear(); _bvhs.Clear(); _edgeTables.Clear(); _rims.Clear();
		Hover = null;
		var ids = Bodies.Select( b => b.Id ).ToHashSet();
		Faces.RemoveAll( f => !ids.Contains( f.BodyId ) );
		Edges.RemoveAll( e => !ids.Contains( e.BodyId ) );
		Vertices.RemoveAll( v => !ids.Contains( v.BodyId ) );
		BodyIds.RemoveAll( id => !ids.Contains( id ) );
		Changed();
	}

	// --- surfaces ------------------------------------------------------------------------------

	/// <summary>The coplanar surface a face belongs to; the adjacency is built once per mesh.</summary>
	public FaceSurface SurfaceOf( PolyMesh mesh, int face )
	{
		if ( _surfaces.TryGetValue( (mesh, face), out var cached ) ) return cached;
		if ( !_indices.TryGetValue( mesh, out var index ) ) _indices[mesh] = index = SurfaceIndex.Build( mesh );
		return _surfaces[(mesh, face)] = FaceSurface.FromFace( index, face );
	}

	// --- picking -------------------------------------------------------------------------------

	static Vec3 E( Vector3 p ) => new( p.X, p.Y, p.Z );
	static Vector3 V( Vec3 p ) => new( p.x, p.y, p.z );

	/// <summary>The face a ray hits, visible bodies only.</summary>
	public bool TryPickFace( Vector3 origin, Vector3 direction, out Body body, out MeshHit hit )
	{
		body = null; hit = default;
		var visible = Bodies.Where( b => b.Visible && b.Mesh is { FaceCount: > 0 } ).ToList();
		if ( visible.Count == 0 ) return false;
		if ( MeshRaycast.Raycast( visible, E( origin ), E( Vector3.Normalize( direction ) ) ) is not { } result ) return false;
		if ( result.Hit.FaceIndex < 0 || result.Hit.FaceIndex >= result.Body.Mesh.FaceCount ) return false;
		(body, hit) = (result.Body, result.Hit);
		return true;
	}

	/// <summary>What a click under this ray would take in the current mode. <paramref name="edgeRadius"/>
	/// is EdgePickPixels in world units at the hit (only Face mode uses it).</summary>
	public SubTarget? Pick( Vector3 origin, Vector3 direction, Func<Vector3, float> edgeRadius = null ) =>
		Rig ? PickBody( origin, direction ) : Pick( origin, direction, Mode, edgeRadius );

	/// <summary>The rig workspace's pick: the body under the cursor, whole.</summary>
	public SubTarget? PickBody( Vector3 origin, Vector3 direction ) =>
		TryPickFace( origin, direction, out var body, out var hit )
			? new SubTarget( body, body.Mesh, SubMode.Body, hit.FaceIndex, default, -1, hit.Point ) : null;

	public SubTarget? Pick( Vector3 origin, Vector3 direction, SubMode mode, Func<Vector3, float> edgeRadius = null )
	{
		if ( mode == SubMode.Body || !TryPickFace( origin, direction, out var body, out var hit ) ) return null;
		var mesh = body.Mesh;
		var surface = SurfaceOf( mesh, hit.FaceIndex );
		switch ( mode )
		{
			case SubMode.Face:
				if ( edgeRadius is not null && !surface.IsEmpty && surface.TryClosestEdge( hit.Point, out var rim, out var d )
					&& d <= edgeRadius( V( hit.Point ) ) )
					return new SubTarget( body, mesh, SubMode.Edge, hit.FaceIndex, rim, -1, hit.Point );
				return new SubTarget( body, mesh, SubMode.Face, hit.FaceIndex, default, -1, hit.Point );
			case SubMode.Edge:
				// No radius: an edge pick always offers the nearest rim (s&box EdgePickFrame).
				if ( surface.IsEmpty || !surface.TryClosestEdge( hit.Point, out var key, out _ ) ) return null;
				return new SubTarget( body, mesh, SubMode.Edge, hit.FaceIndex, key, -1, hit.Point );
			default:
			{
				// Mesh-edit rule (s&box Sculpting.cs): the hit face's corner nearest the hit point.
				int best = -1; float bestD = float.MaxValue;
				foreach ( int v in mesh.Faces[hit.FaceIndex].Indices )
				{
					float dv = (mesh.Positions[v] - hit.Point).Length;
					if ( dv < bestD ) { bestD = dv; best = v; }
				}
				return new SubTarget( body, mesh, SubMode.Vertex, hit.FaceIndex, default, best, mesh.Positions[best] );
			}
		}
	}

	// --- editing -------------------------------------------------------------------------------

	/// <summary>A click. Plain replaces (a miss clears); add (shift) toggles; remove (ctrl) takes it out;
	/// pair (alt, on a face) takes the face and the one across the wall.</summary>
	public void Click( SubTarget? target, bool add = false, bool remove = false, bool pair = false )
	{
		Notice = null;
		if ( target is not { } t )
		{
			if ( !add && !remove ) { Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear(); }
			Changed();
			return;
		}

		// Mesh-edit rule (s&box Sculpting.cs): Alt on an edge is its loop, Ctrl+Alt its ring;
		// Shift with either adds to what is there.
		if ( pair && t.Kind == SubMode.Edge ) { SelectLoop( t, ring: remove, add ); return; }

		if ( t.Kind == SubMode.Body ) { ClickBody( t.Body, add, remove ); return; }

		if ( remove ) { RemoveSame( t ); Changed(); return; }

		if ( pair && t.Kind == SubMode.Face && !add )
		{
			Faces.Clear(); Edges.Clear(); Vertices.Clear();
			Faces.Add( FacePlane.Capture( t.Body, t.Face, t.Point ) );
			if ( MeshRaycast.TryOpposite( t.Mesh, t.Face, out var other, out var across ) )
				Faces.Add( FacePlane.Capture( t.Body, other, across ) );
			else
				Notice = "Nothing faces this one across the part, so just this face is selected. Alt+click a wall with a parallel face behind it to get the pair.";
			Changed();
			return;
		}

		if ( add )
		{
			if ( RemoveSame( t ) ) { Changed(); return; }
			// A selection is one kind of thing — every command that reads it takes faces OR edges.
			DropOtherKinds( t.Kind );
		}
		else { Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear(); }

		switch ( t.Kind )
		{
			case SubMode.Face: Faces.Add( FacePlane.Capture( t.Body, t.Face, t.Point ) ); break;
			case SubMode.Edge: Edges.Add( FacePlane.Capture( t.Body, t.Edge ) ); break;
			case SubMode.Vertex: Vertices.Add( new VertexRef( t.Body.Id, t.Mesh.Positions[t.Vertex] ) ); break;
		}
		Changed();
	}

	void DropOtherKinds( SubMode kind )
	{
		static string Said( int n, string one, string many ) => n == 1 ? $"1 selected {one} was" : $"{n} selected {many} were";
		var dropped = new List<string>();
		if ( kind != SubMode.Face && Faces.Count > 0 ) { dropped.Add( Said( Faces.Count, "face", "faces" ) ); Faces.Clear(); }
		if ( kind != SubMode.Edge && Edges.Count > 0 ) { dropped.Add( Said( Edges.Count, "edge", "edges" ) ); Edges.Clear(); }
		if ( kind != SubMode.Vertex && Vertices.Count > 0 ) { dropped.Add( Said( Vertices.Count, "vertex", "vertices" ) ); Vertices.Clear(); }
		BodyIds.Clear();
		if ( dropped.Count > 0 )
			Notice = $"Faces, edges and vertices can't share a selection, so the {string.Join( " and the ", dropped )} dropped. Shift-click more {ElementWord( kind )} to keep adding.";
	}

	/// <summary>Removes the selected element the target resolves to — matched by resolved surface /
	/// edge / vertex, not by stored ref equality: two clicks on one face give two different points.</summary>
	bool RemoveSame( SubTarget t )
	{
		switch ( t.Kind )
		{
			case SubMode.Face:
				var surface = SurfaceOf( t.Mesh, t.Face );
				return Faces.RemoveAll( f => f.BodyId == t.Body.Id && FacePlane.TryResolveFace( Bodies, f, out _, out var i )
					&& (i == t.Face || surface.Contains( i )) ) > 0;
			case SubMode.Edge:
				return Edges.RemoveAll( e => e.BodyId == t.Body.Id && FacePlane.TryResolveEdge( Bodies, e, out _, out var k ) && k.Equals( t.Edge ) ) > 0;
			default:
				return Vertices.RemoveAll( v => v.BodyId == t.Body.Id && ResolveVertex( t.Body, v.Point ) == t.Vertex ) > 0;
		}
	}

	public void SetHover( SubTarget? target )
	{
		if ( Same( Hover, target ) ) return;
		Hover = target;
		// Only the hover layers change: re-resolving every pick on each cursor move is what a
		// long loop or a box of edges can't afford.
		Version++;
		ShowLayers();
	}

	bool Same( SubTarget? a, SubTarget? b )
	{
		if ( a is not { } x || b is not { } y ) return a is null && b is null;
		if ( x.Body != y.Body || x.Mesh != y.Mesh || x.Kind != y.Kind ) return false;
		return x.Kind switch
		{
			SubMode.Face => x.Face == y.Face || SurfaceOf( x.Mesh, x.Face ).Contains( y.Face ),
			SubMode.Edge => x.Edge.Equals( y.Edge ),
			SubMode.Body => true,
			_ => x.Vertex == y.Vertex,
		};
	}

	public void Clear() { Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear(); Hover = null; Notice = null; Changed(); }

	public void SetMode( SubMode mode )
	{
		if ( mode == Mode ) return;
		Mode = mode;
		Faces.Clear(); Edges.Clear(); Vertices.Clear();
		Hover = null; Notice = null;
		Changed();
	}

	/// <summary>The used vertex nearest a point, within a hair of it (an edit can leave orphans where corners were).</summary>
	static int ResolveVertex( Body body, Vec3 p )
	{
		var mesh = body.Mesh;
		float tol = Math.Max( 1e-4f, mesh.BoundsDiagonal * 1e-3f );
		var used = new bool[mesh.VertexCount];
		foreach ( var f in mesh.Faces ) foreach ( int v in f.Indices ) used[v] = true;
		int best = -1; float bestD = tol;
		for ( int v = 0; v < mesh.VertexCount; v++ )
		{
			if ( !used[v] ) continue;
			float d = (mesh.Positions[v] - p).Length;
			if ( d <= bestD ) { bestD = d; best = v; }
		}
		return best;
	}

	void Changed()
	{
		Version++;
		_resolved.Clear();
		Lost = 0;
		foreach ( var f in Faces )
			if ( FacePlane.TryResolveFace( Bodies, f, out var body, out var index ) )
				_resolved.Add( new SubTarget( body, body.Mesh, SubMode.Face, index, default, -1, f.Point ) );
			else Lost++;
		foreach ( var e in Edges )
			if ( ResolveEdge( e, out var body, out var key ) )
				_resolved.Add( new SubTarget( body, body.Mesh, SubMode.Edge, -1, key, -1, e.Point ) );
			else Lost++;
		foreach ( var v in Vertices )
		{
			var body = Bodies.FirstOrDefault( b => b.Id == v.BodyId );
			int index = body is null ? -1 : ResolveVertex( body, v.Point );
			if ( index >= 0 ) _resolved.Add( new SubTarget( body, body.Mesh, SubMode.Vertex, -1, default, index, body.Mesh.Positions[index] ) );
			else Lost++;
		}
		foreach ( var id in BodyIds )
			if ( Bodies.FirstOrDefault( b => b.Id == id ) is { } body )
				_resolved.Add( new SubTarget( body, body.Mesh, SubMode.Body, -1, default, -1, default ) );
			else Lost++;
		ShowLayers();
	}

	void ShowLayers()
	{
		foreach ( var layer in _layers )
			layer.Visible = !HighlightHidden && (layer.IsPoints ? !Rig && Mode is SubMode.Vertex
				: layer.IsWire ? !Rig && Mode is SubMode.Edge or SubMode.Vertex
				: layer.Hover ? Hover is not null && !HoverIsSelected() : _resolved.Count > 0);
	}

	bool HoverIsSelected()
	{
		if ( Hover is not { } h ) return false;
		foreach ( var r in _resolved )
			if ( Same( r, h ) ) return true;
		return false;
	}

	readonly List<SelectionLayer> _layers = new();

	// --- mesh-edit extras (s&box EffigyViewport.Sculpting.cs) --------------------------------------

	readonly Dictionary<PolyMesh, MeshBVH> _bvhs = new();
	readonly Dictionary<PolyMesh, List<(EdgeKey Key, Vec3 Mid, Vec3 Dir)>> _edgeTables = new();
	readonly Dictionary<PolyMesh, EdgeKey[]> _rims = new();

	/// <summary>Past this many edges a body gets no idle wire: it is re-emitted every frame.</summary>
	public const int WireEdgeBudget = 30000;

	/// <summary>The edges an edge pick can take — seams inside a coplanar surface left out — cached
	/// per mesh for the idle wire.</summary>
	public EdgeKey[] RimEdges( PolyMesh mesh )
	{
		if ( _rims.TryGetValue( mesh, out var rim ) ) return rim;
		var list = new List<EdgeKey>();
		foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
			if ( !(faces.Count == 2 && SurfaceOf( mesh, faces[0] ).Contains( faces[1] )) ) list.Add( key );
		return _rims[mesh] = list.Count > WireEdgeBudget ? Array.Empty<EdgeKey>() : list.ToArray();
	}

	MeshBVH Bvh( PolyMesh mesh )
	{
		if ( !_bvhs.TryGetValue( mesh, out var tree ) ) _bvhs[mesh] = tree = MeshBVH.Build( mesh );
		return tree;
	}

	/// <summary>FacePlane.TryResolveEdge's rule (same direction within 0.02, nearest midpoint) over an
	/// edge table built once per mesh, rather than the mesh's edge map rebuilt for every reference.</summary>
	bool ResolveEdge( EdgeRef reference, out Body body, out EdgeKey key )
	{
		key = default;
		body = Bodies.FirstOrDefault( b => b.Id == reference.BodyId );
		if ( body is null ) return false;
		var mesh = body.Mesh;
		if ( !_edgeTables.TryGetValue( mesh, out var table ) )
		{
			table = new();
			foreach ( var (edge, _) in mesh.BuildEdgeFaces() )
			{
				var a = mesh.Positions[edge.A]; var b = mesh.Positions[edge.B];
				table.Add( (edge, (a + b) * 0.5f, FacePlane.CanonicalDirection( b - a )) );
			}
			_edgeTables[mesh] = table;
		}
		var want = FacePlane.CanonicalDirection( reference.Direction );
		float best = float.MaxValue; bool found = false;
		foreach ( var (k, mid, dir) in table )
		{
			if ( Vec3.Dot( dir, want ) < 1f - 0.02f ) continue;
			float d = (mid - reference.Point).Length;
			if ( d >= best ) continue;
			best = d; key = k; found = true;
		}
		return found;
	}

	/// <summary>Alt+click an edge: its loop (across each four-edge vertex, both ways, until it closes,
	/// hits a pole or a boundary). Ctrl+Alt: its ring (the edges a loop cut through it would cross).
	/// Shift adds to the edges already picked; otherwise it replaces.</summary>
	public void SelectLoop( SubTarget t, bool ring, bool add )
	{
		Notice = null;
		IEnumerable<EdgeKey> keys;
		if ( ring )
		{
			var session = new MeshEditSession( t.Mesh );
			session.SetMode( EditElement.Edge );
			session.SelectEdgeRing( t.Edge );
			keys = session.SelectedEdges.ToList();
		}
		else keys = MeshEditSession.EdgeLoop( t.Mesh, t.Edge );

		if ( add ) DropOtherKinds( SubMode.Edge );
		else { Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear(); }
		var have = new HashSet<EdgeKey>();
		foreach ( var e in Edges )
			if ( e.BodyId == t.Body.Id && ResolveEdge( e, out _, out var k ) ) have.Add( k );
		int added = 0;
		foreach ( var k in keys.Append( t.Edge ) )
			if ( have.Add( k ) ) { Edges.Add( FacePlane.Capture( t.Body, k ) ); added++; }
		if ( added <= 1 && !add )
			Notice = ring
				? "That edge has no ring — a ring crosses quads, and the faces either side of this edge aren't. Just the edge is selected."
				: "That edge has no loop — a loop runs on through vertices with exactly four edges, and this one ends at a pole or a boundary. Just the edge is selected.";
		Changed();
	}

	void ClickBody( Body body, bool add, bool remove )
	{
		Faces.Clear(); Edges.Clear(); Vertices.Clear();
		if ( remove ) BodyIds.Remove( body.Id );
		else if ( add ) { if ( !BodyIds.Remove( body.Id ) ) BodyIds.Add( body.Id ); }
		else { BodyIds.Clear(); BodyIds.Add( body.Id ); }
		Changed();
	}

	/// <summary>The rig workspace on or off. Its picks are whole bodies and an element pick isn't, so
	/// switching drops whatever was picked.</summary>
	public void SetRig( bool on )
	{
		if ( on == Rig ) return;
		Rig = on;
		Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear();
		Hover = null; Notice = null;
		Changed();
	}

	/// <summary>Tools during which the highlight is not drawn (s&amp;box: IsPainting, IsSculpting,
	/// IsMeshEditing, IsMaterialBrushing, IsWeightPainting).</summary>
	public static readonly string[] HidingTools = { "paint", "sculpt", "meshedit", "materialbrush", "weight" };

	/// <summary>A brush or mesh tool picked up (null or "none" puts it down). Only the drawing stops.</summary>
	public void SetTool( string tool )
	{
		tool = string.IsNullOrWhiteSpace( tool ) || tool.Trim().ToLowerInvariant() is "none" or "off" ? null : tool.Trim().ToLowerInvariant();
		if ( tool is not null && !HidingTools.Contains( tool ) )
			throw new ArgumentException( "seltool: " + string.Join( ", ", HidingTools ) + " or none" );
		if ( tool == Tool ) return;
		Tool = tool;
		Version++;
		ShowLayers();
	}

	/// <summary>
	/// Box / lasso select (s&amp;box MeshBoxFrame + SelectInRegion): every element of the current mode
	/// whose screen position <paramref name="inside"/> accepts, and that the eye can see — the first
	/// thing a ray from the eye meets on the way to it is it — unless <paramref name="xray"/>.
	/// Faces by centroid (one pick per surface), edges when both ends are inside (rim edges only, the
	/// ones an edge click can take), vertices by position. In the rig workspace, bodies with any
	/// visible vertex inside. Plain replaces, add (shift) adds, remove (ctrl) takes out.
	/// <paramref name="toScreen"/> maps a world point to pixels, null when it is behind the eye.
	/// </summary>
	public int SelectRegion( Vector3 eye, Func<Vector3, Vector2?> toScreen, Func<Vector2, bool> inside, bool add = false, bool remove = false, bool xray = false )
	{
		Notice = null;
		var visible = Bodies.Where( b => b.Visible && b.Mesh is { FaceCount: > 0 } ).ToList();
		float eps = Math.Max( 1e-3f, visible.Count == 0 ? 1f : visible.Max( b => b.Mesh.BoundsDiagonal ) ) * 1e-3f;
		var from = E( eye );

		bool In( Vec3 p ) => toScreen( V( p ) ) is { } s && inside( s );
		bool Seen( Vec3 p )
		{
			if ( xray ) return true;
			var to = p - from;
			float d = to.Length;
			if ( d < 1e-6f ) return true;
			return MeshRaycast.Raycast( visible, from, to / d, Bvh ) is not { } hit || hit.Hit.Distance >= d - eps;
		}

		if ( !add && !remove ) { Faces.Clear(); Edges.Clear(); Vertices.Clear(); BodyIds.Clear(); }
		var kind = Rig ? SubMode.Body : Mode;
		if ( !remove && kind != SubMode.Body ) DropOtherKinds( kind );
		int touched = 0;

		foreach ( var body in visible )
		{
			var mesh = body.Mesh;
			switch ( kind )
			{
				case SubMode.Body:
				{
					bool any = false;
					foreach ( var f in mesh.Faces )
					{
						foreach ( int v in f.Indices )
							if ( In( mesh.Positions[v] ) && Seen( mesh.Positions[v] + mesh.FaceNormal( f ) * eps ) ) { any = true; break; }
						if ( any ) break;
					}
					if ( !any ) break;
					touched++;
					if ( remove ) BodyIds.Remove( body.Id );
					else if ( !BodyIds.Contains( body.Id ) ) BodyIds.Add( body.Id );
					break;
				}
				case SubMode.Face:
				{
					var surfaces = new HashSet<int>();     // seed face of each surface taken
					var already = new HashSet<int>();
					foreach ( var r in Faces.Count == 0 ? Array.Empty<SubTarget>() : (IEnumerable<SubTarget>)_resolved )   // _resolved is stale after a replace-clear
						if ( r.Body == body && r.Kind == SubMode.Face ) foreach ( int f in SurfaceFaces( r ) ) already.Add( f );
					for ( int f = 0; f < mesh.FaceCount; f++ )
					{
						var face = mesh.Faces[f];
						if ( face.Count < 3 ) continue;
						var c = mesh.FaceCentroid( face );
						if ( !In( c ) || !Seen( c + mesh.FaceNormal( face ) * eps ) ) continue;
						var surface = SurfaceOf( mesh, f );
						int seed = surface.IsEmpty ? f : surface.Faces.Min();
						if ( !surfaces.Add( seed ) ) continue;
						touched++;
						if ( remove )
							Faces.RemoveAll( x => x.BodyId == body.Id && FacePlane.TryResolveFace( Bodies, x, out _, out var i ) && (i == f || surface.Contains( i )) );
						else if ( !already.Contains( f ) )
							Faces.Add( FacePlane.Capture( body, f, c ) );
					}
					break;
				}
				case SubMode.Edge:
				{
					var have = new HashSet<EdgeKey>();
					foreach ( var e in Edges )
						if ( e.BodyId == body.Id && ResolveEdge( e, out _, out var k ) ) have.Add( k );
					foreach ( var (key, faces) in mesh.BuildEdgeFaces() )
					{
						if ( faces.Count == 2 && SurfaceOf( mesh, faces[0] ).Contains( faces[1] ) ) continue;  // a seam inside a surface
						var a = mesh.Positions[key.A]; var b = mesh.Positions[key.B];
						if ( !In( a ) || !In( b ) || !Seen( (a + b) * 0.5f ) ) continue;
						touched++;
						if ( remove )
						{
							if ( have.Remove( key ) ) Edges.RemoveAll( e => e.BodyId == body.Id && ResolveEdge( e, out _, out var k ) && k.Equals( key ) );
						}
						else if ( have.Add( key ) ) Edges.Add( FacePlane.Capture( body, key ) );
					}
					break;
				}
				default:
				{
					var used = new bool[mesh.VertexCount];
					foreach ( var f in mesh.Faces ) foreach ( int v in f.Indices ) used[v] = true;
					var have = new HashSet<int>();
					foreach ( var v in Vertices ) if ( v.BodyId == body.Id ) have.Add( ResolveVertex( body, v.Point ) );
					for ( int v = 0; v < mesh.VertexCount; v++ )
					{
						if ( !used[v] || !In( mesh.Positions[v] ) || !Seen( mesh.Positions[v] ) ) continue;
						touched++;
						if ( remove )
						{
							if ( have.Remove( v ) ) Vertices.RemoveAll( x => x.BodyId == body.Id && ResolveVertex( body, x.Point ) == v );
						}
						else if ( have.Add( v ) ) Vertices.Add( new VertexRef( body.Id, mesh.Positions[v] ) );
					}
					break;
				}
			}
		}
		Changed();
		return touched;
	}

	/// <summary>Even-odd point in polygon (s&amp;box InsideLasso).</summary>
	public static bool InsideLasso( IReadOnlyList<Vector2> lasso, Vector2 p )
	{
		bool inside = false;
		for ( int i = 0, j = lasso.Count - 1; i < lasso.Count; j = i++ )
		{
			var a = lasso[i]; var b = lasso[j];
			if ( (a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X ) inside = !inside;
		}
		return inside;
	}

	public enum Topology { Grow, Shrink, Linked }

	/// <summary>
	/// Select More / Less / Linked on each picked body, by the kernel's own MeshEditSession rules
	/// (GrowSelection, ShrinkSelection, SelectLinked): the picks become the session's selection, the
	/// kernel widens or narrows it by shared vertices, and the result comes back as references.
	/// Faces come back one per surface.
	/// </summary>
	public void Apply( Topology op )
	{
		Notice = null;
		var kind = Faces.Count > 0 ? SubMode.Face : Edges.Count > 0 ? SubMode.Edge : Vertices.Count > 0 ? SubMode.Vertex : SubMode.Body;
		if ( kind == SubMode.Body )
		{
			Notice = op == Topology.Shrink ? "There is no selection to shrink." : $"Select something first — {(op == Topology.Linked ? "Linked" : "Grow")} works from what is already picked.";
			Changed();
			return;
		}
		var groups = _resolved.Where( r => r.Kind == kind && r.Body.Mesh == r.Mesh ).GroupBy( r => r.Body ).ToList();
		Faces.Clear(); Edges.Clear(); Vertices.Clear();
		foreach ( var group in groups )
		{
			var body = group.Key; var mesh = body.Mesh;
			var session = new MeshEditSession( mesh );
			session.SetMode( kind switch { SubMode.Face => EditElement.Face, SubMode.Edge => EditElement.Edge, _ => EditElement.Vertex } );
			foreach ( var r in group )
				switch ( kind )
				{
					case SubMode.Face: foreach ( int f in SurfaceFaces( r ) ) session.SelectFace( f, MeshEditSession.Combine.Add ); break;
					case SubMode.Edge: session.SelectEdge( r.Edge, MeshEditSession.Combine.Add ); break;
					default: session.SelectVertex( r.Vertex, MeshEditSession.Combine.Add ); break;
				}
			switch ( op )
			{
				case Topology.Grow: session.GrowSelection(); break;
				case Topology.Shrink: session.ShrinkSelection(); break;
				default: session.SelectLinked(); break;
			}
			var seeds = new HashSet<int>();
			foreach ( int f in session.SelectedFaces.OrderBy( x => x ) )
			{
				var surface = SurfaceOf( mesh, f );
				if ( seeds.Add( surface.IsEmpty ? f : surface.Faces.Min() ) )
					Faces.Add( FacePlane.Capture( body, f, mesh.FaceCentroid( mesh.Faces[f] ) ) );
			}
			foreach ( var e in session.SelectedEdges ) Edges.Add( FacePlane.Capture( body, e ) );
			foreach ( int v in session.SelectedVertices ) Vertices.Add( new VertexRef( body.Id, mesh.Positions[v] ) );
		}
		if ( op == Topology.Shrink && IsEmpty )
			Notice = $"Shrinking took every {ElementWord( kind ).TrimEnd( 's' )} — all of them were on the selection's border.";
		Changed();
	}

	/// <summary>A material dropped on the viewport is put on the face at the DROP pixel, picked again
	/// there rather than trusted from the last hover (s&amp;box OnDragDrop). Whole surface, any mode;
	/// the selection is left alone.</summary>
	public SubTarget? PickDrop( Vector3 origin, Vector3 direction ) => Pick( origin, direction, SubMode.Face );

	/// <summary>`Body:x,y,z:nx,ny,nz` for a face target, the faces= a drop hands to facematerial.</summary>
	public string FaceTokenOf( SubTarget t ) => FaceToken( FacePlane.Capture( t.Body, t.Face, t.Point ) );

	// --- kernel references ---------------------------------------------------------------------

	static string F( float v ) => Math.Round( v, 5 ).ToString( "0.#####", CultureInfo.InvariantCulture );
	static string P( Vec3 v ) => $"{F( v.x )},{F( v.y )},{F( v.z )}";
	static JsonArray A( Vec3 v ) => new( v.x, v.y, v.z );

	string NameOf( string bodyId ) => Bodies.FirstOrDefault( b => b.Id == bodyId ) is { } b ? b.Name ?? b.Id : bodyId;

	/// <summary>`Body:x,y,z:nx,ny,nz` — the script form of a FaceRef (faces= on FaceMaterial, Draft, Hole, Extrude…).</summary>
	public string FaceToken( FaceRef f ) => $"{NameOf( f.BodyId )}:{P( f.Point )}:{P( f.Normal )}";

	/// <summary>`Body:x,y,z:dx,dy,dz` — the script form of an EdgeRef (edges= on Fillet, Chamfer).</summary>
	public string EdgeToken( EdgeRef e ) => $"{NameOf( e.BodyId )}:{P( e.Point )}:{P( e.Direction )}";

	public static string ElementWord( SubMode m ) => m switch { SubMode.Face => "faces", SubMode.Edge => "edges", SubMode.Vertex => "vertices", _ => "auto" };

	/// <summary>Every mesh face a resolved face pick stands for: all of its surface.</summary>
	public IEnumerable<int> SurfaceFaces( SubTarget t )
	{
		var surface = SurfaceOf( t.Mesh, t.Face );
		return surface.IsEmpty ? new[] { t.Face } : surface.Faces;
	}

	/// <summary>A MeshSelector rule that selects exactly the picked elements of one body (whole
	/// surfaces for faces), for an edit line's select=: a near: term per element, judged as
	/// MeshSelector judges (centroid / midpoint / position), checked against the mesh; index: as the
	/// fallback when no radius isolates them.</summary>
	public string Rule( Body body, SubMode kind )
	{
		var items = _resolved.Where( r => r.Body == body && r.Kind == kind ).ToList();
		if ( items.Count == 0 ) return null;
		var mesh = body.Mesh;
		var faces = kind == SubMode.Face ? items.SelectMany( SurfaceFaces ).Distinct().ToList() : new List<int>();
		var edges = items.Select( r => r.Edge ).Distinct().ToList();
		var verts = items.Select( r => r.Vertex ).Distinct().ToList();
		float diag = Math.Max( 1e-3f, mesh.BoundsDiagonal );
		foreach ( float scale in new[] { 1e-2f, 1e-3f, 1e-4f } )
		{
			float r = MathF.Max( 1e-4f, diag * scale );
			var terms = kind switch
			{
				SubMode.Face => faces.Select( f => $"near:{P( mesh.FaceCentroid( mesh.Faces[f] ) )}~{F( r )} & normal:{P( mesh.FaceNormal( mesh.Faces[f] ) )}~5" ),
				SubMode.Edge => edges.Select( k => $"near:{P( (mesh.Positions[k.A] + mesh.Positions[k.B]) * 0.5f )}~{F( r )}" ),
				_ => verts.Select( v => $"near:{P( mesh.Positions[v] )}~{F( r )}" ),
			};
			var rule = string.Join( " | ", terms );
			if ( Matches( mesh, rule, kind, faces, edges, verts ) ) return rule;
		}
		return kind switch
		{
			SubMode.Face => "index:" + string.Join( ",", faces ),
			SubMode.Edge => "index:" + string.Join( ",", edges.Select( k => $"{k.A}/{k.B}" ) ),
			_ => "index:" + string.Join( ",", verts ),
		};
	}

	static bool Matches( PolyMesh mesh, string rule, SubMode kind, List<int> faces, List<EdgeKey> edges, List<int> verts )
	{
		if ( !MeshSelector.TryParse( rule, out var selector, out _ ) ) return false;
		return kind switch
		{
			SubMode.Face => selector.Faces( mesh ).ToHashSet().SetEquals( faces ),
			SubMode.Vertex => selector.Vertices( mesh ).ToHashSet().SetEquals( verts ),
			_ => selector.Edges( mesh ).Select( k => (k.Key.A, k.Key.B) ).ToHashSet().SetEquals( edges.Select( k => (k.A, k.B) ) ),
		};
	}

	/// <summary>
	/// The `selection` event. Top level carries what tools take, the way ApplyGeometrySelection hands
	/// it out: faces= (FaceRef tokens), edges= (EdgeRef tokens), the body names the picks belong to.
	/// Per body: the select=/element= an edit line takes. Items list every pick with its reference.
	/// </summary>
	public JsonObject ToJson()
	{
		var items = new JsonArray();
		foreach ( var r in _resolved ) items.Add( Item( r ) );
		var bodies = new JsonObject();
		foreach ( var group in _resolved.Where( r => r.Kind != SubMode.Body ).GroupBy( r => r.Body ) )
		{
			var kind = group.First().Kind;
			bodies[group.Key.Name ?? group.Key.Id] = new JsonObject
			{
				["select"] = Rule( group.Key, kind ),
				["element"] = ElementWord( kind ),
				["count"] = group.Count(),
			};
		}
		var kindWord = Faces.Count > 0 ? "faces" : Edges.Count > 0 ? "edges" : Vertices.Count > 0 ? "vertices" : BodyIds.Count > 0 ? "bodies" : "";
		var json = new JsonObject
		{
			["ev"] = "selection",
			["mode"] = Mode.ToString().ToLowerInvariant(),
			["kind"] = kindWord,
			["count"] = Faces.Count + Edges.Count + Vertices.Count + BodyIds.Count,
			["faces"] = string.Join( ";", Faces.Select( FaceToken ) ),
			["edges"] = string.Join( ";", Edges.Select( EdgeToken ) ),
			["bodyNames"] = string.Join( ",", _resolved.Select( r => r.Body.Name ?? r.Body.Id ).Distinct() ),
			["items"] = items,
			["bodies"] = bodies,
			["lost"] = Lost,
			["rig"] = Rig,
			["hidden"] = HighlightHidden,
		};
		if ( Tool is not null ) json["tool"] = Tool;
		if ( Notice is not null ) json["notice"] = Notice;
		if ( Hover is { } h ) json["hover"] = Item( h );
		return json;
	}

	JsonObject Item( SubTarget t )
	{
		var mesh = t.Mesh;
		var o = new JsonObject { ["kind"] = t.Kind.ToString().ToLowerInvariant(), ["body"] = t.Body.Name ?? t.Body.Id, ["bodyId"] = t.Body.Id };
		switch ( t.Kind )
		{
			case SubMode.Face:
				var reference = FacePlane.Capture( t.Body, t.Face, t.Point );
				o["index"] = t.Face;
				o["faces"] = new JsonArray( SurfaceFaces( t ).Select( f => (JsonNode)f ).ToArray() );
				o["point"] = A( reference.Point );
				o["normal"] = A( reference.Normal );
				o["ref"] = FaceToken( reference );
				break;
			case SubMode.Edge:
				o["index"] = $"{t.Edge.A}/{t.Edge.B}";
				o["a"] = A( mesh.Positions[t.Edge.A] );
				o["b"] = A( mesh.Positions[t.Edge.B] );
				o["ref"] = EdgeToken( FacePlane.Capture( t.Body, t.Edge ) );
				break;
			case SubMode.Body:
				o["faceCount"] = mesh.FaceCount;
				break;
			default:
				o["index"] = t.Vertex;
				o["point"] = A( mesh.Positions[t.Vertex] );
				break;
		}
		return o;
	}

	// --- highlight -----------------------------------------------------------------------------

	/// <summary>s&amp;box FaceSelectedColor (a committed pick) and FaceHighlightColor (hover).</summary>
	public static readonly Vector3 Amber = new( 1f, .66f, .2f );
	public static readonly Vector3 HoverBlue = new( .35f, .75f, 1f );
	/// <summary>The idle wire and vertex dots: unpicked edges and points, dark so they read on a white
	/// clay model as structure, not selection (a light grey wire vanished on it).</summary>
	public static readonly Vector3 WireTint = new( .06f, .08f, .11f );

	/// <summary>The highlight layers (selection fill, lines, dots; hover fill, lines, dots), drawn by
	/// SceneRenderer.Live through the shared lit shader. Blend layers: depth-tested, no shadows.
	/// Fills are emissive so the lit surface under them can't wash the hue out.</summary>
	public IReadOnlyList<SelectionLayer> Layers()
	{
		if ( _layers.Count == 0 )
		{
			// first, so it sits under the picks: every pickable edge, faint, in Edge and Vertex mode
			_layers.Add( new SelectionLayer( this, false, SelectionLayer.Shape.Wire ) { Material = new LiveMaterial( WireTint, Roughness: .9f, Emission: Vector3.Zero, Alpha: .8f, Blend: true ) } );
			// Vertex mode: a dot on every point, so there's something to aim the cursor at
			_layers.Add( new SelectionLayer( this, false, SelectionLayer.Shape.Points ) { Material = new LiveMaterial( WireTint, Roughness: .9f, Emission: Vector3.Zero, Alpha: .9f, Blend: true ) { Sprite = true } } );
			var deep = new Vector3( 1f, .3f, 0f );
			_layers.Add( new SelectionLayer( this, false, SelectionLayer.Shape.Fill ) { Material = new LiveMaterial( deep, Roughness: .9f, Emission: new Vector3( .3f, .08f, 0f ), Alpha: .8f, Blend: true ) } );
			_layers.Add( new SelectionLayer( this, false, SelectionLayer.Shape.Lines ) { Material = new LiveMaterial( Amber, Roughness: .8f, Emission: Amber * 2.5f, Alpha: 1f, Blend: true ) } );
			_layers.Add( new SelectionLayer( this, false, SelectionLayer.Shape.Dots ) { Material = new LiveMaterial( Amber, Roughness: .8f, Emission: Amber * 3f, Alpha: 1f, Blend: true ) { Sprite = true } } );
			_layers.Add( new SelectionLayer( this, true, SelectionLayer.Shape.Fill ) { Material = new LiveMaterial( HoverBlue, Roughness: .9f, Emission: new Vector3( .35f, .9f, 1.8f ), Alpha: .6f, Blend: true ) } );
			_layers.Add( new SelectionLayer( this, true, SelectionLayer.Shape.Lines ) { Material = new LiveMaterial( HoverBlue, Roughness: .8f, Emission: HoverBlue * 2.5f, Alpha: 1f, Blend: true ) } );
			_layers.Add( new SelectionLayer( this, true, SelectionLayer.Shape.Dots ) { Material = new LiveMaterial( HoverBlue, Roughness: .8f, Emission: HoverBlue * 3f, Alpha: 1f, Blend: true ) { Sprite = true } } );
			Changed();
		}
		return _layers;
	}
}

/// <summary>One part of the sub-selection highlight, as s&amp;box draws it: a translucent fill over a
/// picked face's whole surface with only its silhouette stroked; an edge as a thick line (thicker
/// than the outline); a vertex as a dot. Nudged toward the eye in proportion to distance, so it
/// wins against the surface it lies on but anything genuinely in front still hides it.</summary>
public sealed class SelectionLayer : LiveLayer
{
	public enum Shape { Fill, Lines, Dots, Wire, Points }

	readonly SubSelection _sel;
	readonly bool _hover;
	readonly Shape _shape;

	public SelectionLayer( SubSelection selection, bool hover, Shape shape ) { _sel = selection; _hover = hover; _shape = shape; }

	public bool Hover => _hover;
	public bool IsWire => _shape == Shape.Wire;
	public bool IsPoints => _shape == Shape.Points;

	/// <summary>Fixed in the world; only its camera-facing strips follow the view.</summary>
	public override bool Dynamic => false;

	/// <summary>Line width and dot size as fractions of the eye distance (about 4 and 13 px at 60° on a 900 px view).</summary>
	public float LineWidth { get; set; } = .008f;
	public float DotSize { get; set; } = .02f;

	IEnumerable<SubTarget> Items()
	{
		if ( _hover ) { if ( _sel.Hover is { } h ) yield return h; }
		else foreach ( var r in _sel.Resolved ) yield return r;
	}

	static Vector3 V( Vec3 p ) => new( p.x, p.y, p.z );

	public override void Emit( LiveBuilder b, in LiveView view )
	{
		if ( _shape == Shape.Wire )
		{
			foreach ( var body in _sel.Bodies )
			{
				if ( !body.Visible || body.Mesh is not { FaceCount: > 0 } mesh ) continue;
				foreach ( var e in _sel.RimEdges( mesh ) )
					Line( b, view, V( mesh.Positions[e.A] ), V( mesh.Positions[e.B] ), .5f );
			}
			return;
		}
		if ( _shape == Shape.Points )
		{
			foreach ( var body in _sel.Bodies )
			{
				if ( !body.Visible || body.Mesh is not { FaceCount: > 0 } mesh || mesh.Positions.Count > SubSelection.WireEdgeBudget ) continue;
				foreach ( var p in mesh.Positions ) Dot( b, view, V( p ), 1.2f );
			}
			return;
		}
		foreach ( var t in Items() )
		{
			// Rebuilt in place since it was resolved: the next Reload re-resolves it.
			if ( t.Body.Mesh != t.Mesh ) continue;
			var mesh = t.Mesh;
			switch ( t.Kind )
			{
				case SubMode.Face:
				{
					var surface = _sel.SurfaceOf( mesh, t.Face );
					if ( _shape == Shape.Fill )
					{
						var flat = new List<Vec3>();
						foreach ( int f in _sel.SurfaceFaces( t ) )
						{
							var face = mesh.Faces[f];
							if ( face.Count < 3 ) continue;
							var n = V( mesh.FaceNormal( face ) );
							flat.Clear();
							foreach ( int v in face.Indices ) flat.Add( mesh.Positions[v] );
							foreach ( var (i, j, k) in Triangulate.Face( flat ) )
								b.Triangle( Nudge( view, V( flat[i] ) ), Nudge( view, V( flat[j] ) ), Nudge( view, V( flat[k] ) ), n, n, n );
						}
					}
					else if ( _shape == Shape.Lines )
					{
						// Only the silhouette: stroking every member face would draw the seams between fragments.
						for ( int i = 0; i < surface.Boundary.Count; i++ )
						{
							var (p0, p1) = surface.Segment( i );
							Line( b, view, V( p0 ), V( p1 ), .8f );
						}
					}
					break;
				}
				case SubMode.Edge:
					if ( _shape == Shape.Lines ) Line( b, view, V( mesh.Positions[t.Edge.A] ), V( mesh.Positions[t.Edge.B] ), 1f );
					break;
				case SubMode.Vertex:
					if ( _shape == Shape.Dots ) Dot( b, view, V( mesh.Positions[t.Vertex] ), 2f );
					break;
				case SubMode.Body:
				{
					// The rig workspace's part pick: the whole body washed, no outline.
					if ( _shape != Shape.Fill ) break;
					var flat = new List<Vec3>();
					foreach ( var face in mesh.Faces )
					{
						if ( face.Count < 3 ) continue;
						var n = V( mesh.FaceNormal( face ) );
						flat.Clear();
						foreach ( int v in face.Indices ) flat.Add( mesh.Positions[v] );
						foreach ( var (i, j, k) in Triangulate.Face( flat ) )
							b.Triangle( Nudge( view, V( flat[i] ) ), Nudge( view, V( flat[j] ) ), Nudge( view, V( flat[k] ) ), n, n, n );
					}
					break;
				}
			}
		}
	}

	static Vector3 Nudge( in LiveView view, Vector3 p ) => p + Vector3.Normalize( view.Eye - p ) * Vector3.Distance( view.Eye, p ) * .002f;

	void Line( LiveBuilder b, in LiveView view, Vector3 p0, Vector3 p1, float scale ) => Stroke( b, view, p0, p1, LineWidth * scale );

	/// <summary>One stroked segment as a camera-facing strip, <paramref name="width"/> a fraction of the eye distance.
	/// The step highlight's outline draws through this too, so it is the selection's own line.</summary>
	internal static void Stroke( LiveBuilder b, in LiveView view, Vector3 p0, Vector3 p1, float width )
	{
		p0 = Nudge( view, p0 ); p1 = Nudge( view, p1 );
		var mid = ( p0 + p1 ) * .5f;
		var dir = p1 - p0;
		if ( dir.LengthSquared() < 1e-12f ) return;
		var toEye = view.Eye - mid;
		var side = LiveBuilder.SafeNormalize( Vector3.Cross( dir, toEye ), view.Right );
		float w0 = Vector3.Distance( view.Eye, p0 ) * width * .5f, w1 = Vector3.Distance( view.Eye, p1 ) * width * .5f;
		var n = LiveBuilder.SafeNormalize( toEye, -view.Forward );
		b.Quad( p0 - side * w0, p1 - side * w1, p1 + side * w1, p0 + side * w0, n );
	}

	void Dot( LiveBuilder b, in LiveView view, Vector3 p, float scale )
	{
		p = Nudge( view, p );
		float s = Vector3.Distance( view.Eye, p ) * DotSize * scale * .5f;
		var r = view.Right * s; var u = view.Up * s;
		b.Quad( p - r - u, p + r - u, p + r + u, p - r + u, -view.Forward );
	}
}
