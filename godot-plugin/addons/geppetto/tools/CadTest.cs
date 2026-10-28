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
/// Headless test for the CAD parametric tools (tools/cad_test.gd). On a copy of g1-spike it builds each
/// tool's feature exactly as the dial does (<see cref="GeppettoCad.NewFeature"/> + <see cref="GeppettoCad.Prepare"/>),
/// adds it through <see cref="GeppettoDocument.AddFeature"/> (no dialog), and checks the rebuild has no
/// error, the mesh changed, and undo puts the feature count and mesh back. Then the feature tree ops.
/// </summary>
public partial class CadTest : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	public Godot.Collections.Dictionary Run( string fixtureRes )
	{
		try { Body( fixtureRes ); }
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	void Add( string name, bool ok, string value )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } } );
	}

	static string Signature( PartStudio s )
	{
		double sum = 0; int verts = 0;
		foreach ( var b in s.Bodies )
		{
			if ( b?.Mesh == null ) continue;
			verts += b.Mesh.VertexCount;
			int i = 0;
			foreach ( var p in b.Mesh.Positions ) { i++; sum += ( p.x * 1.3 + p.y * 2.7 + p.z * 4.1 ) * ( 1 + i % 7 ); }
		}
		return $"{s.Bodies.Count}b/{verts}v/{sum:F3}";
	}

	void Body( string fixtureRes )
	{
		// ---- a scratch copy (never the fixture itself) ----
		string dir = ProjectSettings.GlobalizePath( "user://cad_test" );
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, true );
		Directory.CreateDirectory( dir );
		string copy = Path.Combine( dir, "g1-spike.effigy" );
		File.Copy( ProjectSettings.GlobalizePath( fixtureRes ), copy );
		string cube = Path.Combine( dir, "cube.obj" );
		File.WriteAllText( cube, "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\nv -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\n" +
			"f 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n" );

		var doc = GeppettoDocument.Get( copy );
		var studio = doc.Studio;
		int baselineErrors = doc.LastReport?.Errors.Count ?? 0;
		Add( "fixture loads", studio.Bodies.Count > 0, $"{studio.Features.Count} features, {studio.Bodies.Count} bodies, {baselineErrors} errors" );

		// ---- every dial tool is registered ----
		foreach ( var t in GeppettoCad.Tools ) Add( $"registered CAD/{t}", GeppettoTools.Has( "CAD", t ), "" );

		// ---- inputs some tools need: profiles, a curve, two solids to combine ----
		var lathe = new SketchFeature { Id = "t_lathe", Name = "Lathe profile" };
		lathe.Plane.Index = 1; // Front (XZ)
		lathe.Sketch.AddRectangle( new Vec2( 20, 0 ), new Vec2( 24, 6 ) );
		var ringA = new SketchFeature { Id = "t_ringA", Name = "Ring A" };
		ringA.PlaneOffset.Value = 40f;
		ringA.Sketch.AddCircle( new Vec2( 30, 0 ), 3f );
		var ringB = new SketchFeature { Id = "t_ringB", Name = "Ring B" };
		ringB.PlaneOffset.Value = 50f;
		ringB.Sketch.AddCircle( new Vec2( 30, 0 ), 2f );
		var path = new SplineFeature { Id = "t_path", Name = "Path" };
		path.Tube.Value = false;
		path.Points.AddRange( new[] { new Vec3( 22, 0, 3 ), new Vec3( 22, 10, 8 ), new Vec3( 22, 20, 3 ) } );
		var boxA = new PrimitiveFeature { Id = "t_boxA", Name = "Box A" };
		boxA.Position.Value = new Vec3( -30, 0, 0 );
		boxA.SizeX.Value = boxA.SizeY.Value = boxA.SizeZ.Value = 6f;
		var boxB = new PrimitiveFeature { Id = "t_boxB", Name = "Box B" };
		boxB.Position.Value = new Vec3( -28, 2, 2 );
		boxB.SizeX.Value = boxB.SizeY.Value = boxB.SizeZ.Value = 4f;
		foreach ( var f in new Feature[] { lathe, ringA, ringB, path, boxA, boxB } )
			Add( $"setup {f.Name}", doc.AddFeature( f ) && !HasError( doc, f.Id ), ErrorOf( doc, f.Id ) );
		string? BodyOf( Feature f ) => studio.Bodies.FirstOrDefault( b => b.FeatureId == f.Id )?.Id;
		string? a = BodyOf( boxA ), b = BodyOf( boxB );
		Add( "setup boxes made bodies", a != null && b != null, $"{a} {b}" );

		// ---- each tool ----
		void Tool( string tool, int choice, Action<Feature>? tweak = null, IEnumerable<string>? bodies = null, string? selectedFeature = null )
		{
			GeppettoCad.SelectedBodyIds.Clear();
			if ( bodies != null ) GeppettoCad.SelectedBodyIds.AddRange( bodies );
			GeppettoCad.SelectedFeatureId = selectedFeature;
			var feature = GeppettoCad.NewFeature( tool, choice );
			if ( feature == null ) { Add( $"{tool}: makes a feature", false, "null" ); return; }
			tweak?.Invoke( feature );
			var refusal = GeppettoCad.Prepare( doc, feature );
			if ( refusal != null ) { Add( $"{tool}: ready", false, refusal ); return; }

			int count = studio.Features.Count;
			string before = Signature( studio );
			bool added = doc.AddFeature( feature );
			string after = Signature( studio );
			Add( $"{tool}: added", added && studio.Features.Count == count + 1, feature.TypeName );
			Add( $"{tool}: rebuilds clean", !HasError( doc, feature.Id ) && ( doc.LastReport?.Errors.Count ?? 0 ) <= baselineErrors, ErrorOf( doc, feature.Id ) );
			Add( $"{tool}: mesh changed", after != before, $"{before} -> {after}" );
			doc.Session.History.Undo(); doc.Session.BumpRevision(); doc.Refresh();
			Add( $"{tool}: undo restores", studio.Features.Count == count && Signature( studio ) == before && doc.FeatureById( feature.Id ) == null,
				$"{studio.Features.Count}/{count} {Signature( studio )}" );
		}

		Tool( "Primitive", 1, f => ( (PrimitiveFeature)f ).Position.Value = new Vec3( 0, 40, 0 ) );
		Tool( "Import", -1, f => GeppettoCad.BindImport( doc, (ImportFeature)f, cube ) );
		Tool( "Profile", -1 );
		Tool( "Part", -1 );
		Tool( "Revolve", -1, f => ( (RevolveFeature)f ).SketchFeatureId = lathe.Id );
		Tool( "Sweep", -1, f => ( (SweepFeature)f ).PathSplineId = path.Id, selectedFeature: lathe.Id );
		Tool( "Loft", -1, f => ( (LoftFeature)f ).Sections.AddRange( new[] { ringA.Id, ringB.Id } ) );
		Tool( "Spline", -1, f => ( (SplineFeature)f ).Points.AddRange( new[] { new Vec3( 0, -40, 0 ), new Vec3( 0, -40, 20 ) } ) );
		Tool( "Boolean", 1, bodies: new[] { a!, b! } ); // Subtract: B (last picked) cut from A
		Tool( "Mirror", -1, bodies: new[] { a! } );
		Tool( "Linear Pattern", -1, bodies: new[] { a! } );
		Tool( "Circular Pattern", -1, bodies: new[] { a! } );
		Tool( "Transform", -1, f => ( (TransformFeature)f ).Translate.Value = new Vec3( 0, 0, 5 ), bodies: new[] { a! } );
		Tool( "Bend Along Curve", -1, bodies: new[] { a! }, selectedFeature: path.Id );
		Tool( "Simple Deform", -1, bodies: new[] { a! } );
		Tool( "Lattice", -1, f => ( (LatticeFeature)f ).Offsets.Value = "2,2,2: 0,0,3", bodies: new[] { a! } );

		// ---- refusals say why instead of adding a feature that can only fail ----
		GeppettoCad.SelectedBodyIds.Clear();
		Add( "Boolean refuses with nothing picked", GeppettoCad.Prepare( doc, GeppettoCad.NewFeature( "Boolean", 0 )! ) is { Length: > 0 }, "" );

		// ---- feature tree ops, each one undo step ----
		int n = studio.Features.Count;
		Undoable( doc, "rename", () => GeppettoFeatureOps.Rename( doc, boxB, "Cutter" ), () => boxB.Name == "Cutter", () => boxB.Name == "Box B" );
		string sig = Signature( studio );
		Undoable( doc, "suppress", () => GeppettoFeatureOps.ToggleSuppressed( doc, boxB ),
			() => boxB.Suppressed && Signature( studio ) != sig, () => !boxB.Suppressed && Signature( studio ) == sig );
		Undoable( doc, "hide", () => GeppettoFeatureOps.ToggleVisible( doc, boxB ), () => !boxB.Visible, () => boxB.Visible );
		int at = studio.Features.IndexOf( boxB );
		Undoable( doc, "move up", () => GeppettoFeatureOps.Move( doc, boxB, -1 ),
			() => studio.Features.IndexOf( boxB ) == at - 1, () => studio.Features.IndexOf( boxB ) == at );
		Add( "move down refuses at the end", !GeppettoFeatureOps.Move( doc, boxB, +1 ), "" );
		Undoable( doc, "delete", () => GeppettoFeatureOps.Delete( doc, boxB ),
			() => studio.Features.Count == n - 1 && doc.FeatureById( boxB.Id ) == null,
			() => studio.Features.Count == n && doc.FeatureById( boxB.Id ) == boxB && Signature( studio ) == sig );

		GeppettoCad.SelectedBodyIds.Clear();
		GeppettoCad.SelectedFeatureId = null;
		GeppettoDocument.CloseAll();
	}

	void Undoable( GeppettoDocument doc, string name, Func<bool> act, Func<bool> done, Func<bool> undone )
	{
		int steps = doc.Session.History.UndoCount;
		bool ok = act();
		Add( $"tree {name}: one undo step", ok && doc.Session.History.UndoCount == steps + 1 && done(), "" );
		doc.Session.History.Undo(); doc.Session.BumpRevision(); doc.Refresh();
		Add( $"tree {name}: undo restores", undone(), "" );
	}

	static bool HasError( GeppettoDocument doc, string id ) => doc.LastReport?.Errors.Any( e => e.FeatureId == id ) == true;
	static string ErrorOf( GeppettoDocument doc, string id ) =>
		string.Join( "; ", doc.LastReport?.Errors.Where( e => e.FeatureId == id ).Select( e => e.Message ) ?? Array.Empty<string>() );
}
#endif
