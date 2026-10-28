#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Headless test of the Model workspace, driven by tools/model_test.gd. On a COPY of g1-spike: selects
/// elements programmatically, runs every dial op through <see cref="GeppettoModelEdit"/> (the same path
/// the dial uses), checks the mesh changed and that the document's undo puts it back exactly, checks the
/// typed-number preview (cancel leaves nothing, Done is one step), then saves through the document's
/// <see cref="EffigySaveBridge"/>, re-reads the file and checks the edit survived.
/// </summary>
public partial class ModelTest : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	public Godot.Collections.Dictionary Run( string fixture, string workDir )
	{
		try { RunAll( fixture, workDir ); }
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	void Add( string name, bool ok, string value )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } } );
	}

	GeppettoDocument _doc = null!;
	GeppettoModelEdit _edit = null!;
	string _bodyId = "";

	void RunAll( string fixture, string workDir )
	{
		string dir = ProjectSettings.GlobalizePath( workDir );
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		Directory.CreateDirectory( dir );
		string copy = Path.Combine( dir, "g1-spike-model.effigy" );
		File.Copy( ProjectSettings.GlobalizePath( fixture ), copy );

		_doc = GeppettoDocument.Get( copy );
		_doc.Refresh();
		_edit = GeppettoModelEdit.For( _doc );
		var body = _doc.Studio.Bodies.Where( b => b.Visible && b.Mesh != null ).OrderByDescending( b => b.Mesh.FaceCount ).First();
		_bodyId = body.Id;
		Add( "begin on a body", _edit.Begin( _bodyId ), $"{body.Name} ({body.Mesh.FaceCount} faces)" );

		// ---- selection-only ops: change the selection, add no undo step
		foreach ( var name in new[] { "Grow", "Shrink", "Linked", "Invert" } ) SelectionOp( name );

		// ---- every op that changes the mesh
		Op( "Move", Faces( 1 ) );
		Op( "Rotate", Faces( 2 ) );
		Op( "Scale", Faces( 2 ) );
		Op( "Edge slide", Edges( 1 ), 0.25f );
		Op( "Shrink/Fatten", Faces( 3 ) );
		Op( "Bend", Faces( 6 ) );
		Op( "Extrude", Faces( 1 ) );
		Op( "Inset", Faces( 1 ), 0.1f );
		Op( "Duplicate", Faces( 1 ) );
		Op( "Bridge", s => { HoleSetup( s, 2 ); } );
		Op( "Spin", Edges( 1 ), 90f );
		Op( "Fill", s => { HoleSetup( s, 1 ); } );
		Op( "Pipe", Edges( 1 ) );
		Op( "Merge", Verts( 2 ) );
		Op( "Dissolve", Edges( 1 ) );
		Op( "Delete", Faces( 1 ) );
		Op( "Smooth", Faces( 6 ) );
		Op( "Relax", Faces( 6 ) );
		Op( "Flip", Faces( 1 ) );
		Op( "Triangulate", Faces( 2 ) );
		Op( "Crease", Edges( 2 ) );
		Op( "Clear crease", s => { Edges( 2 )( s ); Must( "Crease", 1f ); Edges( 2 )( s ); } );
		Op( "Mark seam", Edges( 2 ) );
		Op( "Project UVs", Faces( 4 ), 8f );
		Op( "Cylinder projection", Faces( 4 ) );
		Op( "Sphere projection", Faces( 4 ) );

		Refusals();
		Preview();
		SaveAndReload( copy );
	}

	// =========================================================================================
	// Selections
	// =========================================================================================

	MeshEditSession S => _edit.Session!;

	/// <summary>Side faces of the body (quads, not caps): the first <paramref name="n"/> in a connected run.</summary>
	List<int> QuadRun( int n )
	{
		var m = S.Mesh;
		int seed = Enumerable.Range( 0, m.FaceCount ).First( f => m.Faces[f].Indices.Length == 4 );
		_edit.SetMode( EditElement.Face );
		S.SelectFace( seed );
		while ( S.SelectedFaces.Count < n )
		{
			int c = S.SelectedFaces.Count;
			S.GrowSelection();
			if ( S.SelectedFaces.Count == c ) break;
		}
		return S.SelectedFaces.Take( n ).ToList();
	}

	Action<MeshEditSession> Faces( int n ) => s =>
	{
		var faces = QuadRun( n );
		_edit.SetMode( EditElement.Face );
		s.ClearSelection();
		foreach ( var f in faces ) s.SelectFace( f, MeshEditSession.Combine.Add );
	};

	/// <summary>Interior edges (two faces each) of the first quad, along its ring.</summary>
	Action<MeshEditSession> Edges( int n ) => s =>
	{
		var faces = QuadRun( 1 );
		var idx = s.Mesh.Faces[faces[0]].Indices;
		var shared = s.Mesh.BuildEdgeFaces();
		_edit.SetMode( EditElement.Edge );
		s.ClearSelection();
		for ( int i = 0; i < idx.Length && s.SelectedEdges.Count < n; i++ )
		{
			var e = new EdgeKey( idx[i], idx[( i + 1 ) % idx.Length] );
			if ( shared.TryGetValue( e, out var fs ) && fs.Count == 2 ) s.SelectEdge( e, MeshEditSession.Combine.Add );
		}
	};

	Action<MeshEditSession> Verts( int n ) => s =>
	{
		var faces = QuadRun( 1 );
		var idx = s.Mesh.Faces[faces[0]].Indices;
		_edit.SetMode( EditElement.Vertex );
		s.ClearSelection();
		foreach ( var v in idx.Take( n ) ) s.SelectVertex( v, MeshEditSession.Combine.Add );
	};

	/// <summary>Deletes <paramref name="holes"/> separate side faces (as their own undo steps, undone
	/// with the op) and selects the open edges around them, ready for Fill or Bridge.</summary>
	void HoleSetup( MeshEditSession s, int holes )
	{
		var m = S.Mesh;
		var quads = Enumerable.Range( 0, m.FaceCount ).Where( f => m.Faces[f].Indices.Length == 4 ).ToList();
		var pick = new List<int> { quads[0] };
		if ( holes > 1 )
		{
			// The quad furthest from the first: its own hole, not touching the first one.
			var c0 = m.FaceCentroid( m.Faces[quads[0]] );
			pick.Add( quads.OrderByDescending( f => ( m.FaceCentroid( m.Faces[f] ) - c0 ).Length ).First() );
		}
		_edit.SetMode( EditElement.Face );
		S.ClearSelection();
		foreach ( var f in pick ) S.SelectFace( f, MeshEditSession.Combine.Add );
		Must( "Delete" );
		var open = S.Mesh.BuildEdgeFaces().Where( kv => kv.Value.Count == 1 ).Select( kv => kv.Key ).ToList();
		_edit.SetMode( EditElement.Edge );
		S.ClearSelection();
		foreach ( var e in open ) S.SelectEdge( e, MeshEditSession.Combine.Add );
	}

	void Must( string tool, float? value = null )
	{
		var op = GeppettoModelEdit.Ops[tool];
		bool ok = value is { } v ? _edit.Run( op, v ) : _edit.Run( op );
		if ( !ok ) throw new InvalidOperationException( $"setup {tool} failed: {_edit.LastMessage}" );
	}

	// =========================================================================================
	// Checks
	// =========================================================================================

	/// <summary>Everything an op can change: every body's mesh (positions, faces, per-corner UVs,
	/// seams) and the Mesh edit feature's saved mesh (creases), plus the feature list.</summary>
	string Signature()
	{
		var h = new HashCode();
		foreach ( var b in _doc.Studio.Bodies )
		{
			h.Add( b.Id );
			var m = b.Mesh;
			if ( m == null ) continue;
			h.Add( m.VertexCount ); h.Add( m.FaceCount );
			foreach ( var p in m.Positions ) { h.Add( MathF.Round( p.x, 4 ) ); h.Add( MathF.Round( p.y, 4 ) ); h.Add( MathF.Round( p.z, 4 ) ); }
			foreach ( var f in m.Faces )
			{
				foreach ( var i in f.Indices ) h.Add( i );
				if ( f.UVs != null ) foreach ( var uv in f.UVs ) { h.Add( MathF.Round( uv.x, 4 ) ); h.Add( MathF.Round( uv.y, 4 ) ); }
			}
			foreach ( var e in m.Seams.OrderBy( e => e.A ).ThenBy( e => e.B ) ) { h.Add( e.A ); h.Add( e.B ); }
		}
		foreach ( var f in _doc.Studio.Features )
		{
			h.Add( f.Id );
			if ( f is MeshEditFeature me && me.SaveMesh() is { } blob ) h.Add( Convert.ToBase64String( blob ) );
		}
		return h.ToHashCode().ToString( "x8" );
	}

	string Topo()
	{
		var m = _doc.Studio.Bodies.First( b => b.Id == _bodyId ).Mesh;
		return $"{m.VertexCount}v {m.FaceCount}f";
	}

	void UndoTo( int count )
	{
		var h = _doc.Session.History;
		while ( h.UndoCount > count && h.CanUndo ) { h.Undo(); _doc.Session.BumpRevision(); _doc.Refresh(); }
	}

	void SelectionOp( string tool )
	{
		Faces( 2 )( S );
		int before = S.SelectedFaces.Count, undo = _doc.Session.History.UndoCount;
		string sig = Signature();
		bool ok = _edit.Run( GeppettoModelEdit.Ops[tool] );
		int after = S.SelectedFaces.Count;
		Add( $"{tool}: changes the selection only", ok && after != before && _doc.Session.History.UndoCount == undo && Signature() == sig,
			$"{before} -> {after} faces, steps +{_doc.Session.History.UndoCount - undo}" );
	}

	void Op( string tool, Action<MeshEditSession> setup, float? value = null )
	{
		var history = _doc.Session.History;
		int start = history.UndoCount;
		string sig0 = Signature(), topo0 = Topo();
		try
		{
			setup( S );
			int beforeOp = history.UndoCount;
			string sig1 = Signature();
			var op = GeppettoModelEdit.Ops[tool];
			bool ok = value is { } v ? _edit.Run( op, v, op.DefaultAxis ) : _edit.Run( op );
			string sig2 = Signature(), topo2 = Topo();
			Add( $"{tool}: changes the mesh as one step", ok && sig2 != sig1 && history.UndoCount == beforeOp + 1,
				ok ? $"{topo0} -> {topo2}" : $"refused: {_edit.LastMessage}" );
			UndoTo( start );
			string sig3 = Signature();
			Add( $"{tool}: undo restores", sig3 == sig0 && Topo() == topo0 && _edit.Session != null, $"{Topo()}" );
		}
		catch ( Exception e )
		{
			Add( $"{tool}: runs", false, e.Message );
			UndoTo( start );
		}
	}

	void Refusals()
	{
		var s = S;
		_edit.SetMode( EditElement.Face );
		s.ClearSelection();
		int undo = _doc.Session.History.UndoCount;
		string sig = Signature();
		bool ok = _edit.Run( GeppettoModelEdit.Ops["Extrude"] );
		Add( "nothing selected: refused with a reason, no step", !ok && _edit.LastRefused && _doc.Session.History.UndoCount == undo && Signature() == sig,
			_edit.LastMessage ?? "(no message)" );
		Faces( 1 )( s );
		ok = _edit.Run( GeppettoModelEdit.Ops["Scale"], 0f );
		Add( "scale 0: refused with a reason, no step", !ok && _edit.LastRefused && !( _edit.LastMessage ?? "" ).Contains( "(Parameter" ) && _doc.Session.History.UndoCount == undo,
			_edit.LastMessage ?? "(no message)" );
	}

	void Preview()
	{
		var history = _doc.Session.History;
		var op = GeppettoModelEdit.Ops["Extrude"];
		Faces( 1 )( S );
		int undo = history.UndoCount;
		string sig0 = Signature();
		_edit.BeginNumeric( op );
		_edit.PreviewNumeric( 1f, 1 );
		string sigA = Signature();
		_edit.PreviewNumeric( 2f, 1 );
		string sigB = Signature();
		Add( "preview: shows each value live", sigA != sig0 && sigB != sigA && history.UndoCount == undo, $"{sig0} / {sigA} / {sigB}" );
		_edit.CancelNumeric();
		Add( "preview: cancel puts it back, no step", Signature() == sig0 && history.UndoCount == undo, Topo() );

		Faces( 1 )( S );
		_edit.BeginNumeric( op );
		_edit.PreviewNumeric( 0.5f, 1 );
		_edit.PreviewNumeric( 1.5f, 1 );
		_edit.AcceptNumeric();
		Add( "preview: Done is one step", history.UndoCount == undo + 1 && Signature() != sig0, Topo() );
		UndoTo( undo );
		Add( "preview: undo after Done restores", Signature() == sig0, Topo() );
	}

	void SaveAndReload( string copy )
	{
		// Two steps that stay: an extrude, then a crease (the crease only lives in the feature's mesh blob).
		Faces( 1 )( S );
		Must( "Extrude", 2f );
		Edges( 1 )( S );
		Must( "Crease", 2f );
		var edited = _doc.Studio.Bodies.First( b => b.Id == _bodyId ).Mesh;
		var feature = _doc.Studio.Features.OfType<MeshEditFeature>().LastOrDefault();
		Add( "a Mesh edit feature is in the tree", feature != null && feature.Bodies.BodyIds.Contains( _bodyId ), feature?.Id ?? "none" );
		int creases = feature?.Creases.Count ?? 0;

		var results = _doc.Bridge.SaveAll( force: true );
		Add( "saved through the bridge", results.All( r => r.Outcome is EffigySaveOutcome.Written or EffigySaveOutcome.Unchanged ),
			string.Join( "; ", results.Select( r => $"{Path.GetFileName( r.Path )} {r.Outcome} {r.Message}" ) ) );
		string sidecar = Path.Combine( Path.GetDirectoryName( copy )!, "g1-spike-model.meshedit" );
		Add( "mesh edit written beside the file", Directory.Exists( sidecar ) && Directory.GetFiles( sidecar ).Length > 0, sidecar );

		var reread = StudioDocument.ReadFile( copy );
		reread.Rebuild();
		var back = reread.Bodies.FirstOrDefault( b => b.Id == _bodyId )?.Mesh;
		var rf = reread.Features.OfType<MeshEditFeature>().LastOrDefault();
		bool same = back != null && back.VertexCount == edited.VertexCount && back.FaceCount == edited.FaceCount
			&& back.Positions.Zip( edited.Positions ).All( p => ( p.First - p.Second ).Length < 1e-4f );
		Add( "reloaded: the edit survived", same, back == null ? "body missing" : $"{back.VertexCount}v {back.FaceCount}f vs {edited.VertexCount}v {edited.FaceCount}f, error={rf?.Error}" );
		Add( "reloaded: creases survived", rf != null && rf.Creases.Count == creases && creases > 0, $"{rf?.Creases.Count} of {creases}" );
	}
}
#endif
