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
/// One Model-workspace operation, as s&box's mesh-edit op table has it (EffigyWindow.Workspaces.cs,
/// BuildMeshEditStages): a label, and either a plain run or a run that takes a typed number with a
/// default. <see cref="Axis"/> ops also take an axis (Godot's X, Y up, Z).
/// </summary>
public sealed record GeppettoModelOp(
	string Tool,
	string Label,
	Action<MeshEditSession, float, int> Run,
	Func<MeshEditSession, float>? Default = null,
	string ValueLabel = "",
	bool Axis = false,
	int DefaultAxis = 1,
	bool SelectionOnly = false )
{
	public bool TakesNumber => Default != null;
}

public static partial class GeppettoTools
{
	static partial void RegisterModel()
	{
		foreach ( var mode in new[] { ("Vertex", EditElement.Vertex), ("Edge", EditElement.Edge), ("Face", EditElement.Face) } )
		{
			var (tool, element) = mode;
			Register( "Model", tool, ctx =>
			{
				if ( ctx.Doc == null || ctx.Character == null ) return ctx.Need();
				if ( !HasEditableBody( ctx.Doc ) ) return StartFromPrimitive( ctx, element );
				var edit = GeppettoModelEdit.For( ctx.Doc );
				edit.SetMode( element );
				Activate( new GeppettoModelSelectTool( ctx.Character, edit ) );
				return true;
			} );
		}

		// The CAD Primitive (shape menu, then its settings box), so a part can be added without leaving Model.
		Register( "Model", "Primitive", ctx =>
		{
			if ( ctx.Doc == null ) return ctx.Need();
			var edit = GeppettoModelEdit.For( ctx.Doc );
			if ( edit.IsPreviewing ) edit.AcceptNumeric();
			return GeppettoCad.Run( ctx.Doc, "Primitive", ctx.Character );
		} );

		foreach ( var op in GeppettoModelEdit.Ops.Values )
		{
			var o = op;
			Register( "Model", o.Tool, ctx =>
			{
				if ( ctx.Doc == null || ctx.Character == null ) return ctx.Need();
				if ( !HasEditableBody( ctx.Doc ) ) return StartFromPrimitive( ctx, null );
				var edit = GeppettoModelEdit.For( ctx.Doc );
				if ( !edit.EnsureSession() ) return true;
				if ( o.TakesNumber ) GeppettoModelOpPopup.Open( edit, o );
				else edit.Run( o );
				// Keep picking available: the selection is what every op works on.
				if ( Active is not GeppettoModelSelectTool ) Activate( new GeppettoModelSelectTool( ctx.Character, edit ) );
				Redraw();
				return true;
			} );
		}
	}

	static bool HasEditableBody( GeppettoDocument doc ) =>
		doc.Studio.Bodies.Any( b => b.Visible && !b.IsReference && b.Mesh is { FaceCount: > 0 } );

	static Action? _afterPrimitive;

	/// <summary>
	/// A Model tool on a model with nothing in it yet (a new model made in an empty scene): there is no
	/// vertex, edge or face to work on, so offer the Add &gt; Primitive shape menu instead of a dead end.
	/// Once the shape is in, the picking tool the user asked for (Vertex, Edge or Face) starts on it.
	/// </summary>
	static bool StartFromPrimitive( GeppettoToolContext ctx, EditElement? mode )
	{
		var doc = ctx.Doc!;
		var character = ctx.Character!;
		if ( _afterPrimitive != null ) { GeppettoDocument.AnyChanged -= OnAnyChangedAfterPrimitive; _afterPrimitive = null; }
		if ( mode is { } m )
		{
			_afterPrimitive = () =>
			{
				if ( !HasEditableBody( doc ) || Active is GeppettoModelSelectTool ) return;
				var edit = GeppettoModelEdit.For( doc );
				edit.SetMode( m );
				Activate( new GeppettoModelSelectTool( character, edit ) );
			};
			GeppettoDocument.AnyChanged += OnAnyChangedAfterPrimitive;
		}
		GeppettoToolContext.Say( "This model is empty: pick a shape to start from, then edit its vertices, edges and faces." );
		return GeppettoCad.Run( doc, "Primitive", character );
	}

	static void OnAnyChangedAfterPrimitive( GeppettoDocument doc )
	{
		if ( _afterPrimitive == null || !HasEditableBody( doc ) ) return;
		// Wait for the shape's settings box to close (OK keeps it, Cancel takes it out again).
		if ( GeppettoFeatureDialog.AnyOpen ) return;
		var run = _afterPrimitive;
		_afterPrimitive = null;
		GeppettoDocument.AnyChanged -= OnAnyChangedAfterPrimitive;
		run();
	}
}

/// <summary>
/// The Model workspace's live mesh edit for one document: which body is being edited, the kernel
/// <see cref="MeshEditSession"/> holding its selection, and the <see cref="MeshEditFeature"/> in the
/// tree its edits land in - the same record s&box's Model workspace writes (AddMeshEdit / EnterMeshEdit,
/// committed with <see cref="MeshEditSession.CommitTo"/>). The feature's mesh is saved beside the
/// .effigy in <c>&lt;name&gt;.meshedit/&lt;id&gt;.bin</c> (<see cref="MeshEditSidecar"/>).
///
/// Every op that changes the mesh is ONE undo step on the document: the step swaps the feature's saved
/// mesh (and whether the feature is in the tree at all) between before and after, so Poohdot's Ctrl+Z
/// takes the edit back exactly, and the session is rebuilt from the feature afterwards.
/// </summary>
public sealed class GeppettoModelEdit
{
	static readonly Dictionary<GeppettoDocument, GeppettoModelEdit> Edits = new();

	public static GeppettoModelEdit For( GeppettoDocument doc )
	{
		if ( !Edits.TryGetValue( doc, out var e ) ) Edits[doc] = e = new GeppettoModelEdit( doc );
		return e;
	}

	public GeppettoDocument Doc { get; }
	public MeshEditSession? Session { get; private set; }
	public MeshEditFeature? Feature { get; private set; }
	public string? BodyId { get; private set; }
	public EditElement Mode { get; private set; } = EditElement.Face;

	/// <summary>The last thing an op had to say (refusal, notice or outcome), for the overlay and tests.</summary>
	public string? LastMessage { get; private set; }
	/// <summary>True when <see cref="LastMessage"/> is a refusal.</summary>
	public bool LastRefused { get; private set; }

	/// <summary>Raised whenever the selection, mode or mesh changes (the overlay repaints).</summary>
	public event Action? Changed;

	int _knownRevision = -1;
	bool _inOp;

	GeppettoModelEdit( GeppettoDocument doc )
	{
		Doc = doc;
		doc.Changed += OnDocChanged;
	}

	public Body? Body => BodyId == null ? null : Doc.Studio.Bodies.FirstOrDefault( b => b.Id == BodyId );

	// =========================================================================================
	// Which body is edited
	// =========================================================================================

	/// <summary>Starts editing <paramref name="bodyId"/> (keeps the session when it is already that body).
	/// Continues the mesh edit at the end of the tree when it is on this body, as s&box's EnterMeshEdit
	/// does; otherwise the first change adds a new Mesh edit feature.</summary>
	public bool Begin( string bodyId )
	{
		if ( BodyId == bodyId && Session != null ) return true;
		var studio = Doc.Studio;
		var body = studio.Bodies.FirstOrDefault( b => b.Id == bodyId );
		if ( body?.Mesh == null ) return false;

		if ( Session is { IsPreviewing: true } ) Session.Cancel();
		Session = null;
		Feature = null;

		if ( LastEffective() is MeshEditFeature me && me.LastBodyId == bodyId && me.LastInput != null && me.Error == null )
		{
			Feature = me;
			Session = new MeshEditSession( me.LastInput, me.Edited ?? me.LastInput );
			Session.LoadCreases( me.Creases );
			_knownRevision = me.Revision;
		}
		else
		{
			Session = new MeshEditSession( body.Mesh );
		}

		BodyId = bodyId;
		Session.SetMode( Mode );
		Changed?.Invoke();
		return true;
	}

	Feature? LastEffective()
	{
		var studio = Doc.Studio;
		for ( int i = studio.EffectiveCount - 1; i >= 0; i-- )
			if ( !studio.Features[i].Suppressed ) return studio.Features[i];
		return null;
	}

	/// <summary>A session to run an op on: the current one, or the only visible body's. Warns and
	/// returns false when there is nothing to edit yet.</summary>
	public bool EnsureSession()
	{
		if ( Session != null && Body != null ) return true;
		var bodies = Doc.Studio.Bodies.Where( b => b.Visible && b.Mesh is { FaceCount: > 0 } ).ToList();
		if ( bodies.Count == 1 && Begin( bodies[0].Id ) ) return true;
		Refuse( bodies.Count == 0
			? "This model has no visible body to edit."
			: "Pick Vertex, Edge or Face on the dial, then click the body to edit." );
		return false;
	}

	/// <summary>Like <see cref="EnsureSession"/>, without a warning (hover over a one-body model).</summary>
	public bool EnsureSessionQuiet()
	{
		if ( Session != null && Body != null ) return true;
		var bodies = Doc.Studio.Bodies.Where( b => b.Visible && b.Mesh is { FaceCount: > 0 } ).ToList();
		return bodies.Count == 1 && Begin( bodies[0].Id );
	}

	public void SetMode( EditElement mode )
	{
		Mode = mode;
		Session?.SetMode( mode );
		Changed?.Invoke();
	}

	/// <summary>Selection changes made from outside (the viewport tool) end here, so the overlay repaints.</summary>
	public void SelectionChanged() => Changed?.Invoke();

	// =========================================================================================
	// Running ops
	// =========================================================================================

	/// <summary>Runs an op with no number (or with its default number) as one step.</summary>
	public bool Run( GeppettoModelOp op ) =>
		Run( op, op.Default is { } d && Session != null ? Round( d( Session ) ) : 0f, op.DefaultAxis );

	/// <summary>Runs an op at <paramref name="value"/> as one step (the popup's Done, or a test).</summary>
	public bool Run( GeppettoModelOp op, float value, int axis = 1 ) =>
		Run( op.Label, s => op.Run( s, value, axis ) );

	/// <summary>
	/// s&box's TryRunMeshOp: run the op on the session; a kernel refusal becomes a sentence; a change is
	/// committed to the Mesh edit feature as one undo step; a no-op says why and records nothing.
	/// </summary>
	public bool Run( string label, Action<MeshEditSession> run )
	{
		if ( !EnsureSession() ) return false;
		var s = Session!;
		if ( _numeric != null ) AcceptNumeric();

		s.ClearNotice();
		int before = s.UndoCount;
		var beforeState = Capture();
		try { run( s ); }
		catch ( Exception e ) when ( e is InvalidOperationException or ArgumentException )
		{
			Refuse( $"{label}: {Readable( e )}" );
			Changed?.Invoke();
			return false;
		}

		if ( s.UndoCount == before )
		{
			// Selection ops (Grow, Linked …) change no mesh and record no step; a no-op says why.
			if ( s.LastChangedNothing ) { Refuse( $"{label}: {s.Notice ?? "nothing changed."}" ); Changed?.Invoke(); return false; }
			Say( s.Notice ?? s.Outcome ?? $"{label}: {SelectionText()}" );
			Changed?.Invoke();
			return true;
		}

		Commit( label, beforeState );
		Say( s.Notice != null ? $"{label}: {s.Notice}" : s.Outcome ?? $"{label}: done" );
		return true;
	}

	// ---- a typed number with a live preview (s&box's StartMeshOp / TryPreview / Accept / Cancel)

	sealed class Numeric
	{
		public required string Label;
		public required Action<MeshEditSession, float, int> Run;
		public required State Before;
		public required int UndoBefore;
	}

	Numeric? _numeric;

	public bool IsPreviewing => _numeric != null;

	/// <summary>Opens a previewed op. False (and nothing open) when there is no session.</summary>
	public bool BeginNumeric( GeppettoModelOp op )
	{
		if ( !EnsureSession() ) return false;
		if ( _numeric != null ) AcceptNumeric();
		_numeric = new Numeric { Label = op.Label, Run = op.Run, Before = Capture(), UndoBefore = Session!.UndoCount };
		return true;
	}

	/// <summary>Shows the open op at <paramref name="value"/>: the session previews it and the feature
	/// holds the preview, so the viewport shows it; nothing reaches undo yet.</summary>
	public bool PreviewNumeric( float value, int axis )
	{
		if ( _numeric is not { } n || Session is not { } s ) return false;
		_inOp = true;
		try
		{
			s.Preview( n.Label, x => n.Run( x, value, axis ) );
			var f = EnsureFeature();
			f.Commit( s.Mesh, s.Base, s.Separated, s.Creases, s.Seams );
			_knownRevision = f.Revision;
			Doc.Refresh();
			if ( s.Notice != null ) Refuse( $"{n.Label}: {s.Notice}" ); else Say( s.Outcome ?? $"{n.Label}: {value:0.###}", quiet: true );
			Changed?.Invoke();
			return true;
		}
		catch ( Exception e ) when ( e is InvalidOperationException or ArgumentException )
		{
			// The session put the mesh back; show it that way too.
			if ( Feature != null ) { Apply( Feature, n.Before ); _knownRevision = Feature.Revision; Doc.Refresh(); }
			Refuse( $"{n.Label}: {Readable( e )}" );
			Changed?.Invoke();
			return false;
		}
		finally { _inOp = false; }
	}

	/// <summary>Done: the previewed op becomes one undo step (or is put back if it changed nothing).</summary>
	public bool AcceptNumeric()
	{
		if ( _numeric is not { } n || Session is not { } s ) { _numeric = null; return false; }
		_numeric = null;
		s.Accept();
		if ( s.UndoCount > n.UndoBefore )
		{
			Commit( n.Label, n.Before );
			return true;
		}
		RestoreBefore( n.Before );
		return false;
	}

	/// <summary>Cancel: the mesh goes back to how it was before the op opened.</summary>
	public void CancelNumeric()
	{
		if ( _numeric is not { } n ) return;
		_numeric = null;
		Session?.Cancel();
		RestoreBefore( n.Before );
		Say( $"{n.Label}: cancelled" );
	}

	void RestoreBefore( State before )
	{
		if ( Feature == null ) return;
		_inOp = true;
		try
		{
			Apply( Feature, before );
			_knownRevision = Feature.Revision;
			Doc.Refresh();
		}
		finally { _inOp = false; }
		if ( !Doc.Studio.Features.Contains( Feature ) ) Feature = null;
		Changed?.Invoke();
	}

	// =========================================================================================
	// The undo step
	// =========================================================================================

	/// <summary>What one undo step swaps: whether the feature is in the tree (and where), and its mesh.</summary>
	public readonly record struct State( bool InTree, int Index, byte[]? Blob );

	State Capture()
	{
		if ( Feature == null ) return new State( false, -1, null );
		int i = Doc.Studio.Features.IndexOf( Feature );
		return new State( i >= 0, i, Feature.SaveMesh() );
	}

	MeshEditFeature EnsureFeature()
	{
		var studio = Doc.Studio;
		if ( Feature != null && studio.Features.Contains( Feature ) ) return Feature;
		var f = Feature ?? new MeshEditFeature();
		if ( string.IsNullOrEmpty( f.Id ) ) f.Id = Guid.NewGuid().ToString( "N" )[..8];
		f.Name ??= "Mesh edit";
		f.Bodies.BodyIds.Clear();
		if ( BodyId != null ) f.Bodies.BodyIds.Add( BodyId );
		// At the rollback bar, like every new feature: the edit is made on the body as it is there.
		int at = studio.EffectiveCount;
		if ( at < studio.Features.Count ) studio.Insert( at, f ); else studio.Add( f );
		Feature = f;
		return f;
	}

	void Commit( string label, State before )
	{
		var s = Session!;
		_inOp = true;
		try
		{
			var f = EnsureFeature();
			s.CommitTo( f );
			var after = Capture();
			// The feature as it was before this op: not in the tree when this op added it.
			bool first = true;
			var cmd = new DelegateEditCommand(
				$"Geppetto: {label}",
				() => { if ( first ) { first = false; return; } Apply( f, after ); },
				() => Apply( f, before ) );
			_knownRevision = f.Revision;
			Doc.Execute( cmd );
			_knownRevision = f.Revision;
		}
		finally { _inOp = false; }
		Changed?.Invoke();
	}

	void Apply( MeshEditFeature f, State state )
	{
		var studio = Doc.Studio;
		bool inTree = studio.Features.Contains( f );
		if ( state.InTree && !inTree )
		{
			if ( state.Index >= 0 && state.Index < studio.Features.Count ) studio.Insert( state.Index, f ); else studio.Add( f );
		}
		else if ( !state.InTree && inTree ) studio.Remove( f );
		if ( state.Blob != null ) f.LoadMesh( state.Blob ); else f.Clear();
	}

	/// <summary>After an undo / redo (or any other tool's edit) the session no longer matches the tree:
	/// rebuild it from the feature, keeping the mode and whatever selection still exists.</summary>
	void OnDocChanged()
	{
		if ( _inOp || BodyId == null ) return;
		if ( Feature != null && Doc.Studio.Features.Contains( Feature ) && Feature.Revision == _knownRevision && LastEffective() == Feature )
			return;
		Resync();
	}

	void Resync()
	{
		var id = BodyId!;
		var old = Session;
		_numeric = null;
		Session = null;
		Feature = null;
		BodyId = null;
		if ( !Begin( id ) ) { Changed?.Invoke(); return; }
		if ( old == null || Session == null ) return;
		var s = Session;
		int nv = s.Mesh.VertexCount, nf = s.Mesh.FaceCount;
		switch ( Mode )
		{
			case EditElement.Vertex:
				foreach ( var v in old.SelectedVertices ) if ( v < nv ) s.SelectVertex( v, MeshEditSession.Combine.Add );
				break;
			case EditElement.Edge:
				var edges = s.Mesh.BuildEdgeFaces();
				foreach ( var e in old.SelectedEdges ) if ( edges.ContainsKey( e ) ) s.SelectEdge( e, MeshEditSession.Combine.Add );
				break;
			default:
				foreach ( var f in old.SelectedFaces ) if ( f < nf ) s.SelectFace( f, MeshEditSession.Combine.Add );
				break;
		}
		Changed?.Invoke();
	}

	// =========================================================================================
	// Words
	// =========================================================================================

	public string SelectionText()
	{
		if ( Session is not { } s ) return "nothing selected";
		var (what, count) = s.Mode switch
		{
			EditElement.Vertex => ("vertices", s.SelectedVertices.Count),
			EditElement.Edge => ("edges", s.SelectedEdges.Count),
			_ => ("faces", s.SelectedFaces.Count),
		};
		return count == 0 ? $"Nothing selected - click {what} to pick them" : $"{count} {what} selected";
	}

	void Say( string message, bool quiet = false )
	{
		LastMessage = message;
		LastRefused = false;
		if ( !quiet ) GeppettoToolContext.Say( message );
	}

	void Refuse( string message )
	{
		LastMessage = message;
		LastRefused = true;
		GeppettoToolContext.Warn( $"{message}" );
	}

	/// <summary>A kernel refusal as a sentence: ArgumentException's "(Parameter 'x')" means nothing to the reader.</summary>
	static string Readable( Exception e )
	{
		var m = e.Message;
		if ( e is ArgumentException { ParamName: { } p } )
		{
			int at = m.IndexOf( $" (Parameter '{p}')", StringComparison.Ordinal );
			if ( at >= 0 ) m = m[..at];
		}
		return m.Trim();
	}

	/// <summary>Rounded to something typeable, as s&box does: 1 decimal at 1 or more, else 3.</summary>
	public static float Round( float v ) => MathF.Round( v, MathF.Abs( v ) >= 1f ? 1 : 3 );

	// =========================================================================================
	// The op table (s&box EffigyWindow.Workspaces.cs BuildMeshEditStages), keyed by dial name
	// =========================================================================================

	static float Size( MeshEditSession s ) { float d = s.Mesh.BoundsDiagonal; return d > 0f ? d : 1f; }

	/// <summary>Godot's axis (0 X, 1 Y up, 2 Z) as an Effigy direction (inches, Z up): godot = (y, z, x).</summary>
	public static Vec3 AxisVec( int axis ) => axis switch { 0 => new Vec3( 0, 1, 0 ), 2 => new Vec3( 1, 0, 0 ), _ => new Vec3( 0, 0, 1 ) };

	static float NonZero( string what, float v ) =>
		MathF.Abs( v ) < 1e-6f ? throw new ArgumentException( $"{what} of 0 would do nothing - type a distance." ) : v;

	static float Positive( string what, float v ) =>
		v <= 0f ? throw new ArgumentException( $"{what} has to be more than 0." ) : v;

	static float Within( string what, float v, float min, float max, string unit = "" ) =>
		v < min || v > max ? throw new ArgumentException( $"{what} goes from {min:0.###} to {max:0.###}{unit}." ) : v;

	static float ScaleFactor( float v ) =>
		MathF.Abs( v ) < 1e-6f ? throw new ArgumentException( "A scale of 0 would flatten the selection to a point - use 1 for no change." ) : v;

	static GeppettoModelOp Plain( string tool, string label, Action<MeshEditSession> run, bool selectionOnly = false ) =>
		new( tool, label, ( s, _, _ ) => run( s ), SelectionOnly: selectionOnly );

	static GeppettoModelOp Typed( string tool, string label, Func<MeshEditSession, float> def, string valueLabel, Action<MeshEditSession, float> run ) =>
		new( tool, label, ( s, v, _ ) => run( s, v ), def, valueLabel );

	static float _unitsPerTile = 32f;

	public static readonly IReadOnlyDictionary<string, GeppettoModelOp> Ops = new[]
	{
		// Select
		Plain( "Grow", "Grow", s => s.GrowSelection(), true ),
		Plain( "Shrink", "Shrink", s => s.ShrinkSelection(), true ),
		Plain( "Linked", "Linked", s => s.SelectLinked(), true ),
		Plain( "Invert", "Invert", s => s.InvertSelection(), true ),

		// Transform. s&box moves these with a drag handle; the typed amounts are its Transform menu's.
		new GeppettoModelOp( "Move", "Move", ( s, v, a ) => s.Move( AxisVec( a ) * v ), s => Size( s ) * 0.05f, "Distance (inches)", Axis: true ),
		new GeppettoModelOp( "Rotate", "Rotate", ( s, v, a ) => s.Rotate( AxisVec( a ), v ), _ => 15f, "Angle (degrees)", Axis: true ),
		Typed( "Scale", "Scale", _ => 1.5f, "Factor", ( s, v ) => { var f = ScaleFactor( v ); s.Scale( new Vec3( f, f, f ) ); } ),
		Typed( "Edge slide", "Edge slide", _ => 0.25f, "Amount (-1 to 1)", ( s, v ) => s.EdgeSlide( v ) ),
		Typed( "Shrink/Fatten", "Shrink/Fatten", s => Size( s ) * 0.02f, "Distance (inches)", ( s, v ) => s.ShrinkFatten( v ) ),
		Typed( "Bend", "Bend", _ => 90f, "Angle (degrees)", ( s, v ) => s.Bend( v ) ),

		// Add
		Typed( "Extrude", "Extrude", s => Size( s ) * 0.1f, "Distance (inches)", ( s, v ) => s.Extrude( NonZero( "An extrude", v ) ) ),
		Typed( "Inset", "Inset", s => Size( s ) * 0.03f, "Distance (inches)", ( s, v ) => s.Inset( v ) ),
		// s&box StartBevel: four segments round the edge off; one makes a flat chamfer.
		new GeppettoModelOp( "Bevel", "Bevel", ( s, v, _ ) => s.Bevel( Positive( "The bevel width", v ), 4 ), s => Size( s ) * 0.02f, "Width (inches)" ),
		Plain( "Duplicate", "Duplicate", s => s.Duplicate() ),
		Plain( "Bridge", "Bridge", s => s.BridgeSelectedLoops() ),
		Typed( "Spin", "Spin", _ => 360f, "Angle (degrees)", ( s, v ) => s.Spin( v, Math.Max( 4, (int)MathF.Round( MathF.Abs( v ) / 15f ) ) ) ),
		Plain( "Fill", "Fill", s => s.Fill() ),
		Typed( "Pipe", "Pipe", s => Size( s ) * 0.01f, "Radius (inches)", ( s, v ) => s.Pipe( Positive( "The pipe radius", v ), 8 ) ),

		// Clean up
		Plain( "Merge", "Merge", s => s.MergeAtCentre() ),
		Plain( "Dissolve", "Dissolve", s => s.Dissolve() ),
		Plain( "Delete", "Delete", s => s.Delete() ),
		Typed( "Smooth", "Smooth", _ => 0.5f, "Strength", ( s, v ) => s.Smooth( v ) ),
		Typed( "Relax", "Relax", _ => 0.5f, "Strength", ( s, v ) => s.Relax( v ) ),
		Plain( "Flip", "Flip normals", s => s.FlipNormals() ),
		Plain( "Triangulate", "Triangulate", s => s.TriangulateFaces() ),

		// Modifiers
		Typed( "Crease", "Crease", _ => 1f, "Levels", ( s, v ) => s.Crease( Within( "Crease", v, 0f, float.MaxValue, " levels" ) ) ),
		Plain( "Clear crease", "Clear crease", s => s.ClearCreases() ),

		// UV
		Plain( "Mark seam", "Mark seam", s => s.MarkSeam() ),
		Typed( "Project UVs", "Project UVs", _ => _unitsPerTile, "Units per tile (inches)", ( s, v ) => { _unitsPerTile = Positive( "Units per tile", v ); s.ProjectUVs( _unitsPerTile ); } ),
		Plain( "Cylinder projection", "Cylinder projection", s => s.ProjectCylinder( null ) ),
		Plain( "Sphere projection", "Sphere projection", s => s.ProjectSphere( null ) ),
	}.ToDictionary( o => o.Tool );
}
#endif
