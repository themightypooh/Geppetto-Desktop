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
/// Headless test for the CAD picking tools (Tools/GeppettoPicker.cs, CadPick.cs, CadSketch.cs).
/// Builds a 4-inch box document in user://, shoots Godot-space rays at it through the picker and
/// checks the face / edge / region that comes back, then runs Fillet, Shell, Face Material, Sketch
/// and Extrude on the picked geometry through GeppettoDocument and checks the rebuild and undo.
/// Driven by cadpick_test.gd.
/// </summary>
#pragma warning disable IDE1006
public partial class cadpick_test : RefCounted
#pragma warning restore IDE1006
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	void Add( string name, bool ok, object? value = null )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { ["name"] = name, ["ok"] = ok, ["value"] = value?.ToString() ?? "" } );
	}

	public Godot.Collections.Dictionary Run()
	{
		try { Body(); }
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		return new Godot.Collections.Dictionary { ["checks"] = _checks, ["total"] = _checks.Count, ["failed"] = _failed };
	}

	static Vector3 G( float x, float y, float z ) => EffigyMeshBridge.TransformPosition( x, y, z );
	static Vector3 GDir( float x, float y, float z ) => EffigyMeshBridge.TransformPosition( x, y, z ).Normalized();

	GeppettoDocument _doc = null!;
	Vec3 _min, _max, _c;

	int Verts => _doc.Studio.Bodies.Sum( b => b.Mesh?.Positions.Count ?? 0 );
	int FaceCount => _doc.Studio.Bodies.Sum( b => b.Mesh?.Faces.Count ?? 0 );
	bool NoErrors => _doc.LastReport is { } r && r.Errors.Count == 0;
	string Errors => _doc.LastReport is { } r ? string.Join( "; ", r.Errors.Select( e => e.Message ) ) : "no report";

	void Undo() { _doc.Session.History.Undo(); _doc.Refresh(); }
	void Redo() { _doc.Session.History.Redo(); _doc.Refresh(); }

	// Rays in Godot space (the picker's Body transform is identity here).
	(Vector3, Vector3) DownAt( float x, float y ) => ( G( x, y, _max.z + 10 ), GDir( 0, 0, -1 ) );
	(Vector3, Vector3) FromPlusX() => ( G( _max.x + 10, _c.y, _c.z ), GDir( -1, 0, 0 ) );

	GeppettoPicker Picker( PickKinds kinds ) => new( "test", _doc, kinds, "" );

	void Body()
	{
		// --- fixture: a primitive box, written fresh to user:// (never a checked-in fixture) ---
		var studio = new PartStudio();
		var box = new PrimitiveFeature();
		box.SizeX.Value = 4; box.SizeY.Value = 4; box.SizeZ.Value = 4;
		studio.Add( box );
		string path = ProjectSettings.GlobalizePath( "user://cadpick_box.effigy" );
		StudioDocument.WriteFile( studio, path );
		_doc = GeppettoDocument.Get( path );
		Add( "box document builds", NoErrors && _doc.Studio.Bodies.Count == 1, Errors );
		var pos = _doc.Studio.Bodies[0].Mesh.Positions;
		_min = new Vec3( pos.Min( p => p.x ), pos.Min( p => p.y ), pos.Min( p => p.z ) );
		_max = new Vec3( pos.Max( p => p.x ), pos.Max( p => p.y ), pos.Max( p => p.z ) );
		_c = ( _min + _max ) * 0.5f;

		foreach ( var t in new[] { "Fillet", "Chamfer", "Shell", "Move Face", "Delete Face", "Draft", "Hole", "Extrude", "Plane", "Face Material", "Sketch" } )
			Add( $"registered CAD/{t}", GeppettoTools.Has( "CAD", t ) );

		// --- picking ---
		var p = Picker( PickKinds.Faces | PickKinds.Edges );
		var (o, d) = DownAt( _c.x, _c.y );
		var top = p.PickRay( o, d );
		Add( "ray down hits a face", top is { Kind: SubMode.Face } );
		if ( top is { } tt )
		{
			var n = p.Selection.SurfaceOf( tt.Mesh, tt.Face ).Normal;
			Add( "top face normal is +Z", n.z > 0.99f, n );
			Add( "top hit point is on the top", MathF.Abs( tt.Point.z - _max.z ) < 1e-3f, tt.Point );
		}
		var (so, sd) = FromPlusX();
		var side = p.PickRay( so, sd );
		Add( "ray from +X hits the +X face", side is { } s && p.Selection.SurfaceOf( s.Mesh, s.Face ).Normal.x > 0.99f );

		p.ClickRay( o, d, shift: false, ctrl: false );
		Add( "click picks one face", p.Faces.Count == 1 && p.Edges.Count == 0, p.Summary() );
		p.ClickRay( so, sd, shift: true, ctrl: false );
		Add( "shift-click adds a second face", p.Faces.Count == 2, p.Summary() );
		p.ClickRay( so, sd, shift: false, ctrl: true );
		Add( "ctrl-click removes it", p.Faces.Count == 1, p.Summary() );
		p.ClickRay( o, d, shift: false, ctrl: false );
		p.ClickRay( so, sd, shift: false, ctrl: false );
		Add( "plain click replaces", p.Faces.Count == 1 && FaceNormal( p.Faces[0] ).x > 0.99f, p.Summary() );
		var (mo, md) = ( G( _max.x + 10, _max.y + 10, _max.z + 10 ), GDir( 1, 0, 0 ) );
		p.ClickRay( mo, md, shift: false, ctrl: false );
		Add( "click on nothing clears", p.IsEmpty, p.Summary() );

		// Near the +X rim of the top, with a 0.2" snap: the rim edge, not the face.
		var (eo, ed) = DownAt( _max.x - 0.05f, _c.y );
		var edge = p.PickRay( eo, ed, _ => 0.2f );
		Add( "ray near the rim takes the edge", edge is { Kind: SubMode.Edge } );
		p.ClickRay( eo, ed, false, false, _ => 0.2f );
		Add( "edge click records an EdgeRef", p.Edges.Count == 1 && p.Faces.Count == 0, p.Summary() );
		var faceOnly = Picker( PickKinds.Faces );
		Add( "face-only picker ignores the rim", faceOnly.PickRay( eo, ed, _ => 0.2f ) is { Kind: SubMode.Face } );

		// A body moved in the scene: rays still land on the right face.
		var moved = Picker( PickKinds.Faces );
		moved.BodyTransform = new Transform3D( Basis.Identity, new Vector3( 5, 0, 0 ) );
		var (mo2, md2) = DownAt( _c.x, _c.y );
		mo2 += new Vector3( 5, 0, 0 );
		Add( "picking follows the Body transform", moved.PickRay( mo2, md2 ) is { } mt && moved.Selection.SurfaceOf( mt.Mesh, mt.Face ).Normal.z > 0.99f );

		// --- Fillet on the top face's rim ---
		int v0 = Verts, f0 = _doc.Studio.Features.Count;
		var topPick = Picker( PickKinds.Faces | PickKinds.Edges );
		topPick.ClickRay( o, d, false, false );
		var fillet = GeppettoTools.BuildCadFeature( "Fillet", _doc.Studio, topPick.Faces.ToList(), topPick.Edges.ToList() ) as FilletFeature;
		Add( "Fillet built from a face pick", fillet != null );
		_doc.AddFeature( fillet! );
		Add( "Fillet rebuild OK", NoErrors && fillet!.Error == null, Errors + fillet?.Error );
		Add( "Fillet changed the mesh", Verts != v0, $"{v0} -> {Verts}" );
		Undo();
		Add( "Fillet undo restores", Verts == v0 && _doc.Studio.Features.Count == f0, $"{Verts} verts, {_doc.Studio.Features.Count} features" );

		// --- Fillet on one picked edge ---
		var edgePick = Picker( PickKinds.Faces | PickKinds.Edges );
		edgePick.ClickRay( eo, ed, false, false, _ => 0.2f );
		var fillet1 = GeppettoTools.BuildCadFeature( "Fillet", _doc.Studio, edgePick.Faces.ToList(), edgePick.Edges.ToList() )!;
		_doc.AddFeature( fillet1 );
		Add( "Fillet on one picked edge rebuilds OK", NoErrors && fillet1.Error == null && Verts != v0, Errors + fillet1.Error );
		Undo();

		// --- Shell opening the top ---
		int fc0 = FaceCount;
		var shell = GeppettoTools.BuildCadFeature( "Shell", _doc.Studio, topPick.Faces.ToList(), topPick.Edges.ToList() ) as ShellFeature;
		Add( "Shell gets the picked face as its opening", shell != null && shell.OpenFaces.Count == 1, shell?.OpenFaces.Count );
		_doc.AddFeature( shell! );
		Add( "Shell rebuild OK", NoErrors && shell!.Error == null, Errors + shell?.Error );
		Add( "Shell changed the mesh", FaceCount != fc0 || Verts != v0, $"{fc0} -> {FaceCount} faces" );
		Undo();
		Add( "Shell undo restores", Verts == v0 && FaceCount == fc0 && _doc.Studio.Features.Count == f0 );

		// --- Face Material on the top only ---
		var look = SlotLook.Presets.Values.First();
		int slot = GeppettoTools.ApplyFaceMaterial( _doc, topPick.Faces.ToList(), "Test", look );
		Add( "Face Material picks a slot from 1", slot >= 1, slot );
		Add( "Face Material rebuild OK", NoErrors, Errors );
		Add( "picked face takes the slot", MaterialOf( topPick.Faces[0] ) == slot, MaterialOf( topPick.Faces[0] ) );
		Add( "the side face is untouched", SideMaterial() == 0, SideMaterial() );
		Add( "the slot shows the look", _doc.Studio.MaterialLooks.TryGetValue( slot, out var got ) && got.Color.x == look.Color.x );
		int again = GeppettoTools.ApplyFaceMaterial( _doc, topPick.Faces.ToList(), "Test", look );
		Add( "same look reuses its slot, no extra feature", again == slot && _doc.Studio.Features.OfType<FaceMaterialFeature>().Count() == 1 );
		Undo();
		Undo();
		Add( "Face Material undo restores", MaterialOf( topPick.Faces[0] ) == 0 && !_doc.Studio.Features.OfType<FaceMaterialFeature>().Any() && !_doc.Studio.MaterialLooks.ContainsKey( slot ) );
		Redo();
		Add( "Face Material redo re-applies", MaterialOf( topPick.Faces[0] ) == slot && SideMaterial() == 0 );
		Undo();

		// --- Sketch on the top face, then Extrude its region ---
		var sk = new CadSketch( _doc );
		sk.ChoosePlane( o, d );
		Add( "Sketch takes the clicked face's plane", sk.Face != null && sk.Plane != null && MathF.Abs( sk.Plane.Normal.z ) > 0.99f );
		var at = sk.OnPlane( o, d );
		Add( "Sketch ray lands on the plane", at != null );
		var c2 = at ?? new Vec2( 0, 0 );
		foreach ( var q in new[] { new Vec2( -1, -1 ), new Vec2( 1, -1 ), new Vec2( 1, 1 ), new Vec2( -1, 1 ) } )
			sk.Points.Add( new Vec2( c2.x + q.x, c2.y + q.y ) );
		int nf = _doc.Studio.Features.Count;
		Add( "Sketch commits", sk.Commit() && _doc.Studio.Features.Count == nf + 1 && NoErrors, Errors );
		var sketch = _doc.Studio.Features.Last() as SketchFeature;
		Add( "Sketch is a closed 4-line outline", sketch != null && sketch.Sketch.Curves.Count == 4 && ProfileFinder.Find( sketch.Sketch ).Profiles.Count == 1 );

		var rp = Picker( PickKinds.Faces | PickKinds.Regions );
		rp.ClickRay( o, d, false, false );
		Add( "Extrude picker takes the sketch region over the face", rp.RegionSketchId == sketch?.Id && rp.RegionSeeds.Count == 1 && rp.Faces.Count == 0, rp.Summary() );
		var (oo, od) = DownAt( _min.x + 0.2f, _min.y + 0.2f );
		Add( "outside the region the face is picked", rp.PickRegion( oo, od ) == null && rp.PickRay( oo, od ) is { Kind: SubMode.Face } );
		int v1 = Verts;
		var ext = GeppettoTools.BuildCadFeature( "Extrude", _doc.Studio, rp.Faces.ToList(), rp.Edges.ToList(), rp.RegionSketchId, rp.RegionSeeds.ToList() ) as ExtrudeFeature;
		Add( "Extrude gets the sketch", ext != null && ext.SketchFeatureId == sketch?.Id && !ext.IsAwaitingPick );
		_doc.AddFeature( ext! );
		Add( "Extrude rebuild OK and the mesh grew", NoErrors && ext!.Error == null && Verts > v1, Errors + ext?.Error + $" {v1}->{Verts}" );
		Undo(); Undo();
		Add( "Sketch + Extrude undo restores", Verts == v0 && _doc.Studio.Features.Count == f0 );

		// --- the rest of the Detail tools build and take the pick ---
		// (Draft tapers walls along the pull, so it takes the +X side rather than the top.)
		var sidePick = Picker( PickKinds.Faces );
		sidePick.ClickRay( so, sd, false, false );
		foreach ( var tool in new[] { "Move Face", "Delete Face", "Draft", "Hole", "Plane", "Chamfer" } )
		{
			var pick = tool == "Draft" ? sidePick : topPick;
			var f = GeppettoTools.BuildCadFeature( tool, _doc.Studio, pick.Faces.ToList(), pick.Edges.ToList() )!;
			_doc.AddFeature( f );
			Add( $"{tool} on the {( tool == "Draft" ? "side" : "top" )} face rebuilds OK", NoErrors && f.Error == null, Errors + f.Error );
			Undo();
		}
		Add( "everything undone", Verts == v0 && _doc.Studio.Features.Count == f0 );
	}

	Vec3 FaceNormal( FaceRef f ) => FacePlane.TryResolveFace( _doc.Studio.Bodies, f, out var b, out int i )
		? FaceSurface.FromFace( b.Mesh, i ).Normal : Vec3.Zero;

	int MaterialOf( FaceRef f ) => FacePlane.TryResolveFace( _doc.Studio.Bodies, f, out var b, out int i ) ? b.Mesh.Faces[i].Material : -1;

	int SideMaterial()
	{
		var p = Picker( PickKinds.Faces );
		var (so, sd) = FromPlusX();
		return p.PickRay( so, sd ) is { } t ? t.Mesh.Faces[t.Face].Material : -1;
	}
}
#endif
