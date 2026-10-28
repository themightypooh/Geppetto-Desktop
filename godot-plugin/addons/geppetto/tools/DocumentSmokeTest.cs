#nullable enable
using System;
using System.IO;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Headless smoke test for the tools foundation (tools/document_smoke_test.gd): one edit through
/// <see cref="GeppettoDocument"/> on a scratch copy of g1-spike changes the build, undo/redo walk it,
/// and SaveAll writes it so a fresh read has the new feature.
/// </summary>
public partial class DocumentSmokeTest : RefCounted
{
	int _failed, _total;

	void Check( string name, bool ok, string detail = "" )
	{
		_total++;
		if ( !ok ) _failed++;
		GD.Print( $"{( ok ? "PASS" : "FAIL" )}  {name}  {detail}" );
	}

	public int Run( string fixtureRes )
	{
		try
		{
			string src = ProjectSettings.GlobalizePath( fixtureRes );
			string dir = Path.Combine( Path.GetTempPath(), "geppetto-doc-smoke-" + Guid.NewGuid().ToString( "N" )[..6] );
			Directory.CreateDirectory( dir );
			string copy = Path.Combine( dir, "smoke.effigy" );
			File.Copy( src, copy );

			var doc = GeppettoDocument.Get( copy );
			Check( "same document on second Get", ReferenceEquals( doc, GeppettoDocument.Get( copy ) ) );
			int features = doc.Studio.Features.Count;
			int verts = CpuMesh.FromStudio( doc.Studio, 0, copy ).Vertices.Length;

			var box = new PrimitiveFeature { Id = "smokebox", Name = "Smoke box" };
			box.Position.Value = new Vec3( 0, 0, 40 );
			box.SizeX.Value = box.SizeY.Value = box.SizeZ.Value = 6f;
			Check( "AddFeature records a step", doc.AddFeature( box ) );
			Check( "feature count +1", doc.Studio.Features.Count == features + 1 );
			Check( "rebuild has no errors", doc.LastReport?.Errors.Count == 0, string.Join( "; ", doc.LastReport?.Errors ?? new() ) );
			int verts2 = CpuMesh.FromStudio( doc.Studio, 0, copy ).Vertices.Length;
			Check( "mesh grew", verts2 > verts, $"{verts} -> {verts2}" );
			Check( "document dirty", doc.Session.IsDirty );

			doc.Session.History.Undo(); doc.Refresh();
			Check( "undo removes it", doc.Studio.Features.Count == features );
			doc.Session.History.Redo(); doc.Refresh();
			Check( "redo puts it back", doc.Studio.Features.Count == features + 1 );

			var saved = doc.Bridge.SaveAll();
			Check( "SaveAll wrote", saved.Count > 0, string.Join( ", ", saved ) );
			var reread = StudioDocument.ReadFile( copy );
			Check( "reread has the feature", reread.Features.Exists( f => f.Id == "smokebox" ) );

			GeppettoDocument.CloseAll();
			Directory.Delete( dir, true );
		}
		catch ( Exception e ) { Check( "exception", false, e.ToString() ); }
		GD.Print( $"[DocSmoke] {_total - _failed}/{_total} checks passed" );
		return _failed;
	}
}
