using System;
using System.IO;
using Effigy;

internal static class VmdlReaderTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string root = Path.Combine( settings.WorkDirectory, "vmdl-reader" );
		Directory.CreateDirectory( root );

		registry.Add( "vmdl", "ModelDoc reader finds the mesh and the material bindings", () =>
		{
			string path = Path.Combine( root, "model.vmdl" );
			File.WriteAllText( path, """
				<!-- kv3 encoding:text:version{...} format:modeldoc29 -->
				{
					rootNode = { _class = "RootNode" children = [
					{ _class = "MaterialGroupList" children = [ { _class = "DefaultMaterialGroup" remaps = [
					{ from = "arm_ring" to = "materials/hab/arm_ring.vmat" },
					{ from = "hab_steel" to = "materials/hab/hab_steel.vmat" }
					] use_global_default = false } ] },
					{ _class = "RenderMeshList" children = [ { _class = "RenderMeshFile"
					filename = "models/showcase/pull_arms.fbx" import_scale = 0.5
					import_translation = [0,0,0] import_rotation = [0,0,0]
					import_filter = { exclude_by_default = false exception_list = [] } } ] }
					] }
				}
				""" );
			var model = VmdlReader.Read( path );
			if ( model is null ) throw new Exception( "ModelDoc was not read" );
			if ( model.RenderMeshFile != "models/showcase/pull_arms.fbx" )
				throw new Exception( $"wrong render mesh: {model.RenderMeshFile}" );
			if ( Math.Abs( model.ImportScale - 0.5f ) > 1e-6f )
				throw new Exception( $"wrong import scale: {model.ImportScale}" );
			if ( model.Remaps.Count != 2 ) throw new Exception( $"wrong remap count: {model.Remaps.Count}" );
			if ( model.Remaps[0].From != "arm_ring" || model.Remaps[0].To != "materials/hab/arm_ring.vmat" )
				throw new Exception( "wrong first remap" );
			if ( model.Remaps[1].From != "hab_steel" ) throw new Exception( "wrong second remap" );
		} );

		registry.Add( "vmdl", "ModelDoc reader survives a file it cannot read", () =>
		{
			if ( VmdlReader.Read( Path.Combine( root, "missing.vmdl" ) ) is not null )
				throw new Exception( "a missing file produced a model" );
			string path = Path.Combine( root, "empty.vmdl" );
			File.WriteAllText( path, "not a model doc at all" );
			var model = VmdlReader.Read( path );
			if ( model is null ) throw new Exception( "an unreadable node produced no model" );
			if ( model.RenderMeshFile.Length != 0 || model.Remaps.Count != 0 )
				throw new Exception( "an unreadable node produced data" );
			if ( Math.Abs( model.ImportScale - 1f ) > 1e-6f )
				throw new Exception( "an unreadable scale did not stay at 1" );
		} );

		registry.Add( "vmdl", "material name spellings cover the importer's own rules", () =>
		{
			var spellings = new System.Collections.Generic.List<string>();
			foreach ( string spelling in VmdlReader.NameSpellings( "materials/hab/arm_ring.vmat" ) ) spellings.Add( spelling );
			foreach ( string expected in new[] { "materials/hab/arm_ring.vmat", "arm_ring", "arm_ring.vmat" } )
				if ( !spellings.Contains( expected ) ) throw new Exception( $"missing spelling {expected}" );
			// A plain name has no path segment, but the mesh file still carries the bare
			// stem: "hab_brass.vmat" in the ModelDoc is "hab_brass" in the mesh.
			var plain = new System.Collections.Generic.List<string>();
			foreach ( string spelling in VmdlReader.NameSpellings( "hab_brass.vmat" ) ) plain.Add( spelling );
			foreach ( string expected in new[] { "hab_brass.vmat", "hab_brass" } )
				if ( !plain.Contains( expected ) ) throw new Exception( $"missing plain spelling {expected}" );
			var empty = new System.Collections.Generic.List<string>();
			foreach ( string spelling in VmdlReader.NameSpellings( "" ) ) empty.Add( spelling );
			if ( empty.Count != 0 ) throw new Exception( "an empty name produced spellings" );
		} );

		registry.Add( "vmdl", "the ModelDoc that draws a mesh is found by path, not by file name", () =>
		{
			string project = Path.Combine( root, "project" );
			Directory.CreateDirectory( Path.Combine( project, "models", "a" ) );
			Directory.CreateDirectory( Path.Combine( project, "models", "b" ) );
			string a = Path.Combine( project, "models", "a", "one.vmdl" );
			string b = Path.Combine( project, "models", "b", "two.vmdl" );
			File.WriteAllText( a, "{ children = [ { _class = \"RenderMeshFile\" filename = \"models/shared/thing.fbx\" } ] }" );
			File.WriteAllText( b, "{ children = [ { _class = \"RenderMeshFile\" filename = \"models/other/thing.fbx\" } ] }" );
			var found = VmdlReader.FindForRenderMesh( project, "models/other/thing.fbx" );
			if ( found is null ) throw new Exception( "no ModelDoc found" );
			if ( found.Path != b ) throw new Exception( $"the wrong ModelDoc matched: {found.Path}" );
			var byName = VmdlReader.FindForRenderMesh( project, "thing.fbx" );
			if ( byName is null ) throw new Exception( "no ModelDoc found by name" );
		} );
	}
}
