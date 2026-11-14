#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

/// <summary>
/// Shared material library tests (hammer-interop-shared-material-library): map
/// materials registered with the same library entries as sculpt/CAD materials,
/// searchable by name/tags, usable on a CAD body through a studio slot, and
/// exported through the VMT/glTF presets — the material identical in both
/// exports. Fixtures live in
/// tests/fixtures/hammer/hammer-interop-shared-material-library/.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is coordinator-owned;
/// the final integrator adds the one
/// SharedMaterialLibraryTests.Register(registry, settings); line, as with
/// MaterialBridgeTests.
///
/// OPEN ITEM (not a test gap): the FaceMaterial -&gt; PbrMaterial adapter is
/// unbuilt and unowned, so the map seam here is by name — ResolveFace returns
/// null for an unregistered material instead of inventing a look.
/// </summary>
internal static class SharedMaterialLibraryTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "sharedmateriallibrary", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string what )
		{
			if ( Math.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{what}: expected {expected}, got {actual}" );
		}
		void NearColor( Vec3 actual, Vec3 expected, float tolerance, string what )
		{
			Near( actual.x, expected.x, tolerance, what + ".r" );
			Near( actual.y, expected.y, tolerance, what + ".g" );
			Near( actual.z, expected.z, tolerance, what + ".b" );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, nothing was thrown." );
		}

		string Fixture( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "hammer-interop-shared-material-library", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", "hammer-interop-shared-material-library", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", "hammer-interop-shared-material-library", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
			throw new Exception( "shared-material-library fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}

		PbrMaterial ConcretePbr() => new()
		{
			Name = "brick/brickwall001",
			BaseColor = new Vec3( 0.62f, 0.60f, 0.57f ),
			BaseColorMap = "brick/brickwall001a.vtf",
			NormalMap = "brick/brickwall001a_normal.vtf",
			Roughness = 0.9f,
			Metallic = 0f,
		};
		PbrMaterial MetalPbr() => new()
		{
			Name = "metal/metalwall001a",
			BaseColor = new Vec3( 0.75f, 0.76f, 0.78f ),
			BaseColorMap = "metal/metalwall001a.vtf",
			NormalMap = "metal/metalwall001a_normal.vtf",
			MetallicMap = "metal/metalwall001a_orm",
			RoughnessMap = "metal/metalwall001a_orm",
			Roughness = 0.35f,
			Metallic = 1f,
		};
		PbrMaterial GlassPbr() => new()
		{
			Name = "glass/glasswindow001a",
			BaseColor = new Vec3( 0.9f, 0.95f, 1f ),
			BaseColorMap = "glass/glasswindow001a.vtf",
			Roughness = 0.05f,
			Metallic = 0f,
			Opacity = 0.35f,
		};
		PbrMaterial WaterPbr() => new()
		{
			Name = "water/water001a",
			BaseColor = new Vec3( 0.1f, 0.25f, 0.4f ),
			BaseColorMap = "water/water001a.vtf",
			Roughness = 0.05f,
			Metallic = 0f,
			Opacity = 0.8f,
		};

		SharedMaterialLibrary LibraryWithThree()
		{
			var library = new SharedMaterialLibrary();
			library.Add( ConcretePbr(), MaterialOrigin.Map, new[] { "brick", "concrete", "floor" }, surfaceProp: "concrete" );
			library.Add( MetalPbr(), MaterialOrigin.Cad, new[] { "metal", "wall" } );
			library.Add( GlassPbr(), MaterialOrigin.Sculpt, new[] { "glass", "window" } );
			return library;
		}

		// 1. a map material registers and resolves by name, case-insensitively, slashes normalised
		Check( "map material registers and resolves by name", () =>
		{
			var library = new SharedMaterialLibrary();
			var entry = library.Add( ConcretePbr(), MaterialOrigin.Map, new[] { "brick", "concrete", "floor" }, surfaceProp: "concrete" );
			Require( library.Count == 1, $"Expected 1 entry, got {library.Count}." );
			Require( ReferenceEquals( library.Get( "brick/brickwall001" ), entry ), "Get by name did not return the registered entry." );
			Require( ReferenceEquals( library.Get( "BRICK/BRICKWALL001" ), entry ), "Get is not case-insensitive." );
			Require( ReferenceEquals( library.Get( "brick\\brickwall001" ), entry ), "Get does not normalise backslashes." );
			Require( entry.Origin == MaterialOrigin.Map, $"Expected origin Map, got {entry.Origin}." );
			Require( entry.MapPath == "brick/brickwall001", $"Expected map path brick/brickwall001, got {entry.MapPath}." );
			Require( entry.Pbr.Name == entry.Name, "Entry did not keep Pbr.Name equal to the library name." );
			Require( library.Get( "no/such/material" ) is null, "Get returned an entry for an unknown name." );
			Require( library.Get( "" ) is null && library.Get( null ) is null, "Get did not return null for a blank name." );
		} );

		// 2. a duplicate name or a duplicate map path is refused, naming the collision
		Check( "duplicate name or map path is refused", () =>
		{
			var library = new SharedMaterialLibrary();
			library.Add( ConcretePbr(), MaterialOrigin.Map );
			Throws<ArgumentException>( () => library.Add( ConcretePbr(), MaterialOrigin.Cad ), "duplicate name" );
			var alias = ConcretePbr();
			alias.Name = "Brick Wall";
			Throws<ArgumentException>( () => library.Add( alias, MaterialOrigin.Sculpt, mapPath: "brick/brickwall001" ), "duplicate map path under another name" );
			Require( library.Count == 1, $"A refused add changed the library: count {library.Count}." );
		} );

		// 3. blank names and null looks are refused
		Check( "blank name and null pbr are refused", () =>
		{
			Throws<ArgumentException>( () => new SharedMaterialEntry( "", ConcretePbr() ), "blank entry name" );
			Throws<ArgumentException>( () => new SharedMaterialEntry( "   ", ConcretePbr() ), "whitespace entry name" );
			Throws<ArgumentNullException>( () => new SharedMaterialEntry( "brick/brickwall001", null ), "null pbr" );
			Throws<ArgumentNullException>( () => new SharedMaterialLibrary().Add( (PbrMaterial)null, MaterialOrigin.Map ), "null pbr on Add" );
			Throws<ArgumentException>( () => new SharedMaterialLibrary().Add( new PbrMaterial { Name = "   " }, MaterialOrigin.Map ), "blank pbr name on Add" );
			Throws<ArgumentNullException>( () => new SharedMaterialLibrary().Add( (SharedMaterialEntry)null ), "null entry on Add" );
		} );

		// 4. the library search finds map materials by name/tags
		Check( "search finds map materials by name and tags", () =>
		{
			var library = LibraryWithThree();
			Require( library.Search( "concrete" ).Count == 1, "Search by tag 'concrete' did not find the brick entry." );
			Require( library.Search( "BRICK" ).Count == 1, "Search by name is not case-insensitive." );
			Require( library.Search( "metal" ).Count == 1 && library.Search( "metal" )[0].Name == "metal/metalwall001a", "Search by tag 'metal' found the wrong entry." );
			Require( library.Search( "wall" ).Count == 2, $"Search by tag 'wall' should find metal + glass names, got {library.Search( "wall" ).Count}." );
			Require( library.Search( "zzz-no-such" ).Count == 0, "Search returned entries for a query that matches nothing." );
			Require( library.Search( "" ).Count == 3 && library.Search( null ).Count == 3, "A blank search did not return everything." );
		} );

		// 5. a map material binds to a CAD body slot, reused on a second bind
		Check( "map material binds to a cad body slot", () =>
		{
			var library = LibraryWithThree();
			var entry = library.Get( "brick/brickwall001" );
			var studio = new PartStudio();
			int slot = library.BindToStudio( studio, entry );
			Require( slot >= 1 && slot <= 63, $"Bind returned slot {slot}, outside 1..63." );
			Require( studio.NameForSlot( slot ) == "brick/brickwall001", $"NameForSlot({slot}) is '{studio.NameForSlot( slot )}', not the map path." );
			Require( MaterialDrop.SlotCarrying( studio, "brick/brickwall001" ) == slot, "SlotCarrying does not agree with the bind." );
			Require( studio.MaterialNames.Count == 1, $"Bind wrote {studio.MaterialNames.Count} slot names, expected 1." );
			int again = library.BindToStudio( studio, entry );
			Require( again == slot, $"A second bind took slot {again} instead of reusing {slot}." );
			Require( studio.MaterialNames.Count == 1, "A second bind added a second slot name." );
			Throws<ArgumentNullException>( () => library.BindToStudio( null, entry ), "null studio on Bind" );
			Throws<ArgumentNullException>( () => library.BindToStudio( studio, null ), "null entry on Bind" );
		} );

		// 6. binding fails loudly when every slot is spoken for
		Check( "bind returns -1 when all slots are full", () =>
		{
			var library = LibraryWithThree();
			var studio = new PartStudio();
			for ( int slot = 1; slot <= MaterialDrop.HighestSlot; slot++ )
				studio.MaterialNames[slot] = "filler/filler" + slot;
			Require( library.BindToStudio( studio, library.Get( "brick/brickwall001" ) ) == -1, "Bind did not return -1 with all 63 slots full." );
		} );

		// 7. unbind frees an unworn slot and refuses a worn one
		Check( "unbind frees an unworn slot and refuses a worn one", () =>
		{
			var library = LibraryWithThree();
			var entry = library.Get( "brick/brickwall001" );
			var studio = new PartStudio();
			int slot = library.BindToStudio( studio, entry );
			Require( library.UnbindFromStudio( studio, entry ), "Unbind refused a slot no face wears." );
			Require( !studio.MaterialNames.ContainsKey( slot ), "Unbind left the slot name behind." );
			Require( !library.UnbindFromStudio( studio, entry ), "Unbind reported success for an entry that is not bound." );
			slot = library.BindToStudio( studio, entry );
			var feature = new FaceMaterialFeature();
			feature.Material.Value = slot;
			studio.Add( feature );
			Require( !library.UnbindFromStudio( studio, entry ), "Unbind retired a slot a face still wears." );
			Require( studio.MaterialNames.ContainsKey( slot ), "Unbind removed a worn slot's name." );
		} );

		// 8. a brush face resolves to the shared entry by name; unknown names resolve to nothing
		Check( "face resolves to the shared entry by name", () =>
		{
			var library = LibraryWithThree();
			var entry = library.Get( "brick/brickwall001" );
			Require( ReferenceEquals( library.ResolveFace( new FaceMaterial( "brick/brickwall001" ) ), entry ), "ResolveFace did not return the registered entry." );
			Require( library.ResolveFace( new FaceMaterial( "no/such/material" ) ) is null, "ResolveFace invented an entry for an unknown material." );
			Require( library.ResolveFace( null ) is null, "ResolveFace did not return null for a null face." );
			Require( library.TryResolvePbr( new FaceMaterial( "brick/brickwall001" ), out var pbr ) && ReferenceEquals( pbr, entry.Pbr ), "TryResolvePbr did not return the entry's PBR." );
			Require( !library.TryResolvePbr( new FaceMaterial( "no/such/material" ), out var missing ) && missing is null, "TryResolvePbr returned a look for an unknown material." );
		} );

		// 9. a face built from an entry keeps the map path and the UV
		Check( "face for entry keeps the map path and uv", () =>
		{
			var entry = new SharedMaterialEntry( "Brick Wall", ConcretePbr(), MaterialOrigin.Map ) { MapPath = "brick/brickwall001" };
			var uv = new FaceUv( new Vec2( 0.25f, 0.5f ), new Vec2( 2f, 2f ), 90f, 16f, 1 );
			var face = entry.ToFaceMaterial( uv );
			Require( face.MaterialName == "brick/brickwall001", $"Face material is '{face.MaterialName}', not the map path." );
			Require( face.Uv.Equals( uv ), "Face UV did not survive ToFaceMaterial." );
			Require( entry.ToFaceMaterial().Uv.Equals( FaceUv.Default ), "ToFaceMaterial() did not use the default UV." );
			Require( !face.IsTool && face.ContributesToBakedMesh, "An ordinary material face was treated as a tool / non-baking face." );
		} );

		// 10. the VMT preset export matches the bridge exactly
		Check( "vmt preset export matches the bridge", () =>
		{
			var library = LibraryWithThree();
			var entry = library.Get( "brick/brickwall001" );
			var vmt = MaterialExportPreset.SourceVmt.ToVmt( entry );
			Require( vmt.Shader == MaterialBridge.DefaultShader, $"VMT shader is '{vmt.Shader}', not LightmappedGeneric." );
			Require( vmt.Get( "$basetexture" ) == "brick/brickwall001a", $"$basetexture is '{vmt.Get( "$basetexture" )}' — the .vtf extension was not stripped." );
			Require( vmt.Get( "$bumpmap" ) == "brick/brickwall001a_normal", $"$bumpmap is '{vmt.Get( "$bumpmap" )}'." );
			Require( vmt.Get( "$roughness" ) == "0.900", $"$roughness is '{vmt.Get( "$roughness" )}'." );
			Require( vmt.Get( "$surfaceprop" ) == "concrete", $"$surfaceprop is '{vmt.Get( "$surfaceprop" )}'." );
			string direct = MaterialBridge.ToVmt( entry.Pbr, entry.Shader, entry.SurfaceProp, null ).ToText();
			Require( vmt.ToText() == direct, "Preset VMT text differs from the direct bridge VMT text." );
			Require( MaterialExportPreset.SourceVmt.ExportText( entry ) == vmt.ToText(), "ExportText differs from ToVmt().ToText()." );
			Require( MaterialExportPreset.SourceVmt.ExportFileName( entry ) == "brick/brickwall001.vmt", $"VMT file name is '{MaterialExportPreset.SourceVmt.ExportFileName( entry )}'." );
		} );

		// 11. the water preset honours its shader and surfaceprop
		Check( "water preset honours shader and surfaceprop", () =>
		{
			var entry = new SharedMaterialEntry( "water/water001a", WaterPbr(), MaterialOrigin.Map );
			var vmt = MaterialExportPreset.SourceVmtWater.ToVmt( entry );
			Require( vmt.Shader == MaterialBridge.WaterShader, $"Water VMT shader is '{vmt.Shader}', not Water." );
			Require( vmt.Get( "$surfaceprop" ) == "water", $"Water $surfaceprop is '{vmt.Get( "$surfaceprop" )}'." );
			Require( vmt.Get( "$basetexture" ) == "water/water001a", $"Water $basetexture is '{vmt.Get( "$basetexture" )}'." );
			Require( vmt.Get( "$alpha" ) == "0.800", $"Water $alpha is '{vmt.Get( "$alpha" )}'." );
		} );

		// 12. the glTF preset export matches the bridge, round-tripping through JSON
		Check( "gltf preset export matches the bridge", () =>
		{
			var library = LibraryWithThree();
			var entry = library.Get( "metal/metalwall001a" );
			var gltf = MaterialExportPreset.GltfPbr.ToGltf( entry );
			Require( gltf.Name == entry.Name, $"glTF name is '{gltf.Name}', not the library name." );
			NearColor( gltf.BaseColor, entry.Pbr.BaseColor, 1e-6f, "glTF base colour" );
			Near( gltf.Metallic, 1f, 1e-6f, "glTF metallic" );
			Near( gltf.Roughness, 0.35f, 1e-6f, "glTF roughness" );
			Require( gltf.MetallicRoughnessTexture == "metal/metalwall001a_orm", "Equal metallic/roughness maps did not combine into one metallicRoughnessTexture." );
			var direct = MaterialBridge.ToGltf( entry.Pbr, null );
			Require( MaterialExportPreset.GltfPbr.ExportText( entry ) == MaterialBridge.ToGltfJson( entry.Pbr, null, null ), "Preset glTF JSON differs from the direct bridge JSON." );
			var parsed = GltfMaterial.ParseJson( MaterialExportPreset.GltfPbr.ExportText( entry ) );
			Require( parsed.Name == entry.Name, "Parsed glTF JSON lost the material name." );
			NearColor( parsed.BaseColor, entry.Pbr.BaseColor, 1e-6f, "parsed glTF base colour" );
			Require( parsed.MetallicRoughnessTexture == "metal/metalwall001a_orm", "Parsed glTF JSON lost the combined metallic-roughness texture." );
			Require( MaterialExportPreset.GltfPbr.ExportFileName( entry ) == "metal/metalwall001a.gltf-material.json", $"glTF file name is '{MaterialExportPreset.GltfPbr.ExportFileName( entry )}'." );
			Require( direct.MetallicRoughnessTexture == gltf.MetallicRoughnessTexture, "Preset glTF differs from the direct bridge glTF record." );
		} );

		// 13. a preset refuses the wrong target instead of guessing
		Check( "preset refuses the wrong target", () =>
		{
			var entry = new SharedMaterialEntry( "brick/brickwall001", ConcretePbr(), MaterialOrigin.Map );
			Throws<InvalidOperationException>( () => MaterialExportPreset.SourceVmt.ToGltf( entry ), "VMT preset exporting glTF" );
			Throws<InvalidOperationException>( () => MaterialExportPreset.GltfPbr.ToVmt( entry ), "glTF preset exporting VMT" );
			Throws<InvalidOperationException>( () => MaterialExportPreset.SourceVmtWater.ToGltf( entry ), "water VMT preset exporting glTF" );
			Throws<ArgumentNullException>( () => MaterialExportPreset.SourceVmt.ToVmt( null ), "null entry on ToVmt" );
			Throws<ArgumentNullException>( () => MaterialExportPreset.GltfPbr.ToGltf( null ), "null entry on ToGltf" );
		} );

		// 14. map and CAD exports are identical: one entry, two uses, same bytes
		Check( "map and cad exports are identical", () =>
		{
			var library = LibraryWithThree();
			var entry = library.Get( "brick/brickwall001" );
			var studio = new PartStudio();
			int slot = library.BindToStudio( studio, entry );
			Require( slot >= 1, "Bind failed before the export comparison." );
			var resolved = library.Get( studio.NameForSlot( slot ) );
			Require( ReferenceEquals( resolved, entry ), "Resolving the material through the studio slot did not return the same library entry." );
			string vmtMap = MaterialExportPreset.SourceVmt.ExportText( entry );
			string vmtCad = MaterialExportPreset.SourceVmt.ExportText( resolved );
			Require( vmtMap == vmtCad, "VMT export from the map entry differs from the VMT export from the CAD-resolved entry." );
			string gltfMap = MaterialExportPreset.GltfPbr.ExportText( entry );
			string gltfCad = MaterialExportPreset.GltfPbr.ExportText( resolved );
			Require( gltfMap == gltfCad, "glTF export from the map entry differs from the glTF export from the CAD-resolved entry." );
			Require( vmtMap.Contains( "\"LightmappedGeneric\"" ) && vmtMap.Contains( "brick/brickwall001a" ), "VMT export does not carry the bridge-mapped shader and texture." );
			Require( gltfMap.Contains( "\"metallicFactor\"" ) && gltfMap.Contains( "brick/brickwall001" ), "glTF export does not carry the bridge-mapped factors and name." );
		} );

		// 15. a tool material exports as a tool marker in both presets
		Check( "tool material exports as a tool marker", () =>
		{
			var library = new SharedMaterialLibrary();
			var nodraw = library.Add( new PbrMaterial { Name = "tools/toolsnodraw" }, MaterialOrigin.Map, new[] { "tool" } );
			Require( nodraw.IsTool, "The nodraw entry was not recognised as a tool." );
			var vmt = MaterialExportPreset.SourceVmt.ToVmt( nodraw );
			Require( vmt.Get( "$basetexture" ) == "tools/toolsnodraw", $"Tool VMT $basetexture is '{vmt.Get( "$basetexture" )}'." );
			Require( vmt.Get( MaterialBridge.ToolTextureMarker ) == "1", "Tool VMT is missing the %tooltexture marker." );
			Require( vmt.Get( MaterialBridge.CompileNoDraw ) == "1", "Tool VMT is missing %compileNoDraw." );
			var gltf = MaterialExportPreset.GltfPbr.ToGltf( nodraw );
			Require( gltf.Extras.TryGetValue( "tool", out var tool ) && tool == "tools/toolsnodraw", "glTF tool marker lost the tool name." );
			Require( gltf.Extras.ContainsKey( "toolKind" ) && gltf.Extras.ContainsKey( "compileFlags" ), "glTF tool marker lost the kind or compile flags." );
			var sky = library.Add( new PbrMaterial { Name = "tools/toolsskybox", BaseColorMap = "sky_day01_01" }, MaterialOrigin.Map, new[] { "tool", "sky" } );
			var skyVmt = MaterialExportPreset.SourceVmt.ToVmt( sky );
			Require( skyVmt.Get( "$basetexture" ) == "sky_day01_01", $"Skybox VMT $basetexture is '{skyVmt.Get( "$basetexture" )}' — the entry's sky name was not used." );
			Require( skyVmt.Shader == MaterialBridge.SkyShader, $"Skybox VMT shader is '{skyVmt.Shader}', not Sky." );
		} );

		// 16. library JSON round-trips with values intact
		Check( "library json round-trips", () =>
		{
			var library = LibraryWithThree();
			library.Add( new PbrMaterial { Name = "tools/toolsnodraw" }, MaterialOrigin.Map, new[] { "tool" } );
			var (imported, warnings) = SharedMaterialLibrary.ImportJson( library.ExportJson() );
			Require( warnings.Count == 0, $"Round-trip produced warnings: {string.Join( "; ", warnings )}." );
			Require( imported.Count == 4, $"Round-trip imported {imported.Count} entries, expected 4." );
			var concrete = imported.Get( "brick/brickwall001" );
			Require( concrete != null, "Round-trip lost the concrete entry." );
			Require( concrete.Origin == MaterialOrigin.Map, $"Round-trip origin is {concrete.Origin}, not Map." );
			Require( concrete.Tags.Contains( "floor" ), "Round-trip lost the 'floor' tag." );
			NearColor( concrete.Pbr.BaseColor, new Vec3( 0.62f, 0.60f, 0.57f ), 1e-6f, "round-trip base colour" );
			Near( concrete.Pbr.Roughness, 0.9f, 1e-6f, "round-trip roughness" );
			Require( concrete.Pbr.BaseColorMap == "brick/brickwall001a.vtf", $"Round-trip base colour map is '{concrete.Pbr.BaseColorMap}'." );
			Require( concrete.Pbr.Name == concrete.Name, "Round-trip did not keep Pbr.Name equal to the library name." );
			var metal = imported.Get( "metal/metalwall001a" );
			Require( metal.Origin == MaterialOrigin.Cad, $"Round-trip metal origin is {metal.Origin}, not Cad." );
			Require( imported.Get( "tools/toolsnodraw" ).IsTool, "Round-trip lost the tool identity of the nodraw entry." );
			Require( imported.Search( "glass" ).Count == 1, "Round-trip library search no longer finds glass." );
		} );

		// 17. the fixture library imports cleanly
		Check( "fixture library imports", () =>
		{
			var (library, warnings) = SharedMaterialLibrary.ImportJson( Fixture( "library.json" ) );
			Require( warnings.Count == 0, $"Fixture import produced warnings: {string.Join( "; ", warnings )}." );
			Require( library.Count == 3, $"Fixture library has {library.Count} entries, expected 3." );
			var concrete = library.Get( "brick/brickwall001" );
			Require( concrete != null && concrete.Origin == MaterialOrigin.Map, "Fixture concrete entry is missing or has the wrong origin." );
			Require( concrete.Tags.Contains( "floor" ) && concrete.Tags.Contains( "brick" ), "Fixture concrete entry lost its tags." );
			Require( concrete.SurfaceProp == "concrete", $"Fixture concrete surfaceprop is '{concrete.SurfaceProp}'." );
			var metal = library.Get( "metal/metalwall001a" );
			Require( metal != null && metal.Origin == MaterialOrigin.Cad, "Fixture metal entry is missing or has the wrong origin." );
			Near( metal.Pbr.Metallic, 1f, 1e-6f, "fixture metal metallic" );
			var nodraw = library.Get( "tools/toolsnodraw" );
			Require( nodraw != null && nodraw.IsTool, "Fixture nodraw entry is missing or is not a tool." );
			Require( library.Search( "brick" ).Count == 1, "Fixture library search by 'brick' found the wrong count." );
		} );

		// 18. a bad entry is skipped with a named warning; the rest still import
		Check( "library json import warns and skips bad entries", () =>
		{
			string json = "[{\"name\":\"brick/brickwall001\",\"origin\":\"Map\",\"mapPath\":\"brick/brickwall001\",\"shader\":\"LightmappedGeneric\",\"pbr\":{\"name\":\"brick/brickwall001\"}},"
				+ "{\"origin\":\"Map\",\"mapPath\":\"no/name\"},"
				+ "{\"name\":\"brick/brickwall001\",\"origin\":\"Cad\",\"mapPath\":\"brick/brickwall001\"},"
				+ "{\"name\":\"metal/metalwall001a\",\"origin\":\"NotAnOrigin\",\"mapPath\":\"metal/metalwall001a\"}]";
			var (library, warnings) = SharedMaterialLibrary.ImportJson( json );
			Require( library.Count == 1, $"Import kept {library.Count} entries, expected 1 (the first good one)." );
			Require( warnings.Count == 3, $"Import produced {warnings.Count} warnings, expected 3 (nameless, duplicate, unknown origin)." );
			Require( warnings.All( w => w.StartsWith( "Skipped a library entry:", StringComparison.Ordinal ) ), "A warning does not name the skipped entry." );
			Throws<ArgumentException>( () => SharedMaterialLibrary.ImportJson( "" ), "blank import" );
			Throws<ArgumentException>( () => SharedMaterialLibrary.ImportJson( "{\"not\":\"a list\"}" ), "non-list import" );
			Throws<ArgumentException>( () => SharedMaterialLibrary.ImportJson( "{malformed" ), "malformed import" );
		} );

		// 19. preset lookup is case-insensitive and every builtin validates
		Check( "preset lookup and validation", () =>
		{
			Require( MaterialExportPreset.All.Count == 4, $"Expected 4 builtin presets, got {MaterialExportPreset.All.Count}." );
			foreach ( var preset in MaterialExportPreset.All )
				Require( preset.Validate().Count == 0, $"Builtin preset '{preset.Id}' does not validate: {string.Join( "; ", preset.Validate() )}." );
			Require( ReferenceEquals( MaterialExportPreset.ById( "source-vmt" ), MaterialExportPreset.SourceVmt ), "ById did not return the source-vmt preset." );
			Require( ReferenceEquals( MaterialExportPreset.ById( "SOURCE-VMT" ), MaterialExportPreset.SourceVmt ), "ById is not case-insensitive." );
			Require( ReferenceEquals( MaterialExportPreset.ById( "gltf-pbr" ), MaterialExportPreset.GltfPbr ), "ById did not return the gltf-pbr preset." );
			Require( MaterialExportPreset.ById( "no-such-preset" ) is null, "ById returned a preset for an unknown id." );
			Require( MaterialExportPreset.ById( null ) is null && MaterialExportPreset.ById( "  " ) is null, "ById did not return null for a blank id." );
			Throws<ArgumentException>( () => new MaterialExportPreset( "", "x", MaterialExportTarget.Vmt ), "blank preset id" );
			Throws<ArgumentException>( () => new MaterialExportPreset( "x", "", MaterialExportTarget.Vmt ), "blank preset display name" );
		} );

		// 20. removing an entry drops it from every lookup
		Check( "remove drops the entry from every lookup", () =>
		{
			var library = LibraryWithThree();
			Require( library.Remove( "metal/metalwall001a" ), "Remove reported failure for a present entry." );
			Require( library.Count == 2, $"Remove left {library.Count} entries, expected 2." );
			Require( library.Get( "metal/metalwall001a" ) is null, "Get still finds a removed entry." );
			Require( library.Search( "metal" ).Count == 0, "Search still finds a removed entry." );
			Require( !library.Remove( "metal/metalwall001a" ), "Remove reported success for an absent entry." );
			Require( library.Remove( "BRICK/BRICKWALL001" ), "Remove by case-insensitive name failed." );
		} );
	}
}
