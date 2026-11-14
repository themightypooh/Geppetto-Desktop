#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

/// <summary>
/// Material bridge tests (hammer-material-bridge-pbr-vmt-gltf): Geppetto PBR
/// (the shared PbrMaterial record) &lt;-&gt; Source VMT &lt;-&gt; glTF PBR, over the
/// representative set concrete / metal / glass / nodraw / skybox / water.
/// Fixtures live in tests/fixtures/hammer/hammer-material-bridge-pbr-vmt-gltf/.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is coordinator-owned;
/// the final integrator adds the one MaterialBridgeTests.Register(registry, settings);
/// line, as with MaterialXTests.
/// </summary>
internal static class MaterialBridgeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "materialbridge", name, test );
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

		string Fixture( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "hammer-material-bridge-pbr-vmt-gltf", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", "hammer-material-bridge-pbr-vmt-gltf", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", "hammer-material-bridge-pbr-vmt-gltf", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
			throw new Exception( "material-bridge fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}

		PbrMaterial ConcretePbr() => new()
		{
			Name = "concrete",
			BaseColor = new Vec3( 0.62f, 0.60f, 0.57f ),
			BaseColorMap = "concrete/concretefloor001a",
			NormalMap = "concrete/concretefloor001a_normal",
			Roughness = 0.9f,
			Metallic = 0f,
		};
		PbrMaterial MetalPbr() => new()
		{
			Name = "metal",
			BaseColor = new Vec3( 0.75f, 0.76f, 0.78f ),
			BaseColorMap = "metal/metalwall001a",
			NormalMap = "metal/metalwall001a_normal",
			Roughness = 0.35f,
			Metallic = 1f,
		};
		PbrMaterial GlassPbr() => new()
		{
			Name = "glass",
			BaseColor = new Vec3( 0.9f, 0.95f, 1f ),
			BaseColorMap = "glass/glasswindow001a",
			Roughness = 0.05f,
			Metallic = 0f,
			Opacity = 0.35f,
		};
		PbrMaterial WaterPbr() => new()
		{
			Name = "water",
			BaseColor = new Vec3( 0.1f, 0.25f, 0.4f ),
			BaseColorMap = "water/water001a",
			Roughness = 0.1f,
			Metallic = 0f,
			Opacity = 0.8f,
		};

		Check( "concrete fixture parses with shader, surfaceprop and unknown key", () =>
		{
			var vmt = VmtMaterial.Parse( Fixture( "concrete.vmt" ) );
			Require( vmt.Shader == "LightmappedGeneric", "shader must be LightmappedGeneric, got " + vmt.Shader );
			Require( vmt.BaseTexture == "concrete/concretefloor001a", "basetexture mismatch: " + vmt.BaseTexture );
			Require( vmt.SurfaceProp == "concrete", "surfaceprop must be concrete, got " + vmt.SurfaceProp );
			Require( vmt.Get( "$roughness" ) == "0.900", "roughness must be 0.900, got " + vmt.Get( "$roughness" ) );
			Require( vmt.Get( "$custom_vendor_key" ) == "keepme", "unknown key $custom_vendor_key must survive parsing" );
		} );

		Check( "metal fixture parses; metalness and roughness survive FromVmt", () =>
		{
			var pbr = VmtMaterial.Parse( Fixture( "metal.vmt" ) ).ToPbr();
			Near( pbr.Metallic, 1f, 0.001f, "metallic" );
			Near( pbr.Roughness, 0.35f, 0.001f, "roughness" );
			Require( pbr.BaseColorMap == "metal/metalwall001a", "base colour map mismatch: " + pbr.BaseColorMap );
			Require( pbr.NormalMap == "metal/metalwall001a_normal", "normal map mismatch: " + pbr.NormalMap );
		} );

		Check( "glass fixture parses; translucency and envmap survive", () =>
		{
			var vmt = VmtMaterial.Parse( Fixture( "glass.vmt" ) );
			var pbr = vmt.ToPbr();
			Near( pbr.Opacity, 0.35f, 0.001f, "opacity" );
			Near( pbr.Roughness, 0.05f, 0.001f, "roughness" );
			Require( vmt.Get( "$translucent" ) == "1", "$translucent must be 1" );
			Require( vmt.Get( "$envmap" ) == "env_cubemap", "$envmap must survive as an unknown key" );
			Require( vmt.Get( "$custom_tint" ) == "[0.9 0.95 1.0]", "unknown $custom_tint must survive parsing" );
		} );

		Check( "PBR->VMT->PBR concrete keeps base colour and roughness", () =>
		{
			var vmt = MaterialBridge.ToVmt( ConcretePbr(), surfaceProp: "concrete" );
			var back = VmtMaterial.Parse( vmt.ToText() ).ToPbr();
			NearColor( back.BaseColor, new Vec3( 0.62f, 0.60f, 0.57f ), 0.002f, "base colour" );
			Near( back.Roughness, 0.9f, 0.002f, "roughness" );
			Near( back.Metallic, 0f, 0.001f, "metallic" );
			Require( back.BaseColorMap == "concrete/concretefloor001a", "base colour map must round-trip" );
			Require( back.NormalMap == "concrete/concretefloor001a_normal", "normal map must round-trip" );
			Require( VmtMaterial.Parse( vmt.ToText() ).SurfaceProp == "concrete", "surfaceprop must round-trip on the VMT side" );
		} );

		Check( "PBR->VMT->PBR metal keeps metalness", () =>
		{
			var back = VmtMaterial.Parse( MaterialBridge.ToVmt( MetalPbr(), surfaceProp: "metal" ).ToText() ).ToPbr();
			Near( back.Metallic, 1f, 0.002f, "metallic" );
			Near( back.Roughness, 0.35f, 0.002f, "roughness" );
			NearColor( back.BaseColor, new Vec3( 0.75f, 0.76f, 0.78f ), 0.002f, "base colour" );
		} );

		Check( "PBR->VMT->PBR glass keeps opacity and translucent flag", () =>
		{
			var vmt = MaterialBridge.ToVmt( GlassPbr(), surfaceProp: "glass" );
			Require( vmt.Get( "$translucent" ) == "1", "glass VMT must carry $translucent" );
			var back = VmtMaterial.Parse( vmt.ToText() ).ToPbr();
			Near( back.Opacity, 0.35f, 0.002f, "opacity" );
			Near( back.Roughness, 0.05f, 0.002f, "roughness" );
		} );

		Check( "water uses the Water shader and keeps opacity", () =>
		{
			var fixture = VmtMaterial.Parse( Fixture( "water.vmt" ) );
			Require( fixture.Shader == "Water", "water fixture shader must be Water, got " + fixture.Shader );
			Near( fixture.ToPbr().Opacity, 0.8f, 0.001f, "fixture opacity" );
			var vmt = MaterialBridge.ToVmt( WaterPbr(), shader: MaterialBridge.WaterShader, surfaceProp: "water" );
			Require( vmt.Shader == "Water", "ToVmt shader override must be Water" );
			var back = VmtMaterial.Parse( vmt.ToText() ).ToPbr();
			Near( back.Opacity, 0.8f, 0.002f, "opacity" );
			Near( back.Roughness, 0.1f, 0.002f, "roughness" );
		} );

		Check( "unknown VMT keys survive a parse-emit round-trip and a PBR round-trip with extras", () =>
		{
			var parsed = VmtMaterial.Parse( Fixture( "concrete.vmt" ) );
			var reprinted = VmtMaterial.Parse( parsed.ToText() );
			Require( reprinted.Get( "$custom_vendor_key" ) == "keepme", "unknown key must survive parse->emit->parse" );
			var extra = new[]
			{
				new KeyValuePair<string, string>( "$mysteryknob", "7" ),
				new KeyValuePair<string, string>( "$custom_foo", "bar" ),
			};
			var vmt = MaterialBridge.ToVmt( ConcretePbr(), surfaceProp: "concrete", extra: extra );
			var text = vmt.ToText();
			Require( text.Contains( "$mysteryknob", StringComparison.Ordinal ), "extra key $mysteryknob must be emitted" );
			Require( text.Contains( "$custom_foo", StringComparison.Ordinal ), "extra key $custom_foo must be emitted" );
			var back = VmtMaterial.Parse( text );
			Require( back.Get( "$mysteryknob" ) == "7", "extra key must survive re-parse" );
			Require( back.Get( "$custom_foo" ) == "bar", "extra key must survive re-parse" );
		} );

		Check( "nested Proxies block survives parse and re-emit", () =>
		{
			var vmt = VmtMaterial.Parse( Fixture( "concrete.vmt" ) );
			Require( vmt.Blocks.ContainsKey( "Proxies" ), "Proxies block must be captured" );
			string text = vmt.ToText();
			Require( text.Contains( "animatedtextureframerate", StringComparison.Ordinal ), "Proxies content must survive re-emit" );
			Require( text.Contains( "30", StringComparison.Ordinal ), "Proxies framerate value must survive re-emit" );
			var back = VmtMaterial.Parse( text );
			Require( back.Blocks.ContainsKey( "Proxies" ), "Proxies block must survive a second parse" );
		} );

		Check( "nodraw tool carries compile flags and is detected", () =>
		{
			var tool = MaterialBridge.ToolTexture( "nodraw" );
			Require( tool != null, "nodraw must resolve as a tool texture" );
			string text = tool!.ToVmt().ToText();
			Require( text.Contains( "%compileNoDraw", StringComparison.Ordinal ), "nodraw VMT must carry %compileNoDraw" );
			Require( text.Contains( "%tooltexture", StringComparison.Ordinal ), "nodraw VMT must carry %tooltexture" );
			var fixture = VmtMaterial.Parse( Fixture( "nodraw.vmt" ) );
			Require( fixture.IsToolTexture, "nodraw fixture must detect as a tool texture" );
			Require( fixture.ToolName == "toolsnodraw", "tool name must be toolsnodraw, got " + fixture.ToolName );
			var gltf = MaterialBridge.ToGltf( ConcretePbr(), tool );
			Require( gltf.Extras["tool"] == "tools/toolsnodraw", "glTF tool marker must name tools/toolsnodraw" );
			Require( gltf.Extras["compileFlags"].Contains( "%compileNoDraw", StringComparison.Ordinal ), "glTF tool marker must carry the compile flag" );
		} );

		Check( "skybox tool keeps the Sky shader and sky name", () =>
		{
			var fixture = VmtMaterial.Parse( Fixture( "skybox.vmt" ) );
			Require( fixture.Shader == "Sky", "skybox fixture shader must be Sky, got " + fixture.Shader );
			Require( fixture.BaseTexture == "sky_day01_01", "skybox basetexture must be the sky name" );
			Require( fixture.IsToolTexture || fixture.ToolName == null, "skybox basetexture is a sky name, not a tools/ material" );
			var tool = MaterialBridge.ToolTexture( "skybox" );
			Require( tool != null && tool.Shader == "Sky", "skybox tool must resolve with the Sky shader" );
			Require( tool!.Kind == ToolTextureKind.Skybox, "skybox kind must be Skybox" );
			var vmt = tool.ToVmt( "sky_day01_01" );
			Require( vmt.Shader == "Sky" && vmt.BaseTexture == "sky_day01_01", "skybox ToVmt must keep shader and sky name" );
			Require( vmt.Get( "%compileNonSolid" ) == "1", "skybox is non-solid per the canonical registry" );
			var gltf = MaterialBridge.ToGltf( ConcretePbr(), tool );
			Require( gltf.Extras["shader"] == "Sky", "glTF skybox marker must carry the Sky shader" );
			Require( gltf.Extras["toolKind"] == "Skybox", "glTF skybox marker must carry the kind" );
		} );

		Check( "all eleven tool textures resolve and agree with the canonical registry", () =>
		{
			string[] names = { "nodraw", "skybox", "hint", "skip", "trigger", "clip", "areaportal", "occluder", "blocklight", "blocklos", "origin" };
			Require( MaterialBridge.ToolTextures.Count == ToolTexture.All.Count, "bridge table must mirror the canonical registry count" );
			Require( MaterialBridge.ToolTextures.Count == 15, "canonical registry must hold 15 entries, got " + MaterialBridge.ToolTextures.Count );
			foreach ( string name in names )
			{
				var tool = MaterialBridge.ToolTexture( name );
				Require( tool != null, "tool texture must resolve: " + name );
				Require( MaterialBridge.ToolTexture( "tools/tools" + name ) == tool || name == "blocklos",
					"tools/ spelling must resolve: " + name );
				var canonical = ToolTexture.Lookup( tool!.MaterialName );
				Require( canonical != null, "bridge material name must exist in the canonical registry: " + tool.MaterialName );
				bool noDraw = Array.IndexOf( tool.CompileFlags, "%compileNoDraw" ) >= 0;
				bool nonSolid = Array.IndexOf( tool.CompileFlags, "%compileNonSolid" ) >= 0;
				Require( noDraw == canonical!.DropsFromBakedMesh, "%compileNoDraw must match DropsFromBakedMesh: " + name );
				Require( nonSolid == !canonical.IsSolid, "%compileNonSolid must match solidity: " + name );
				string text = tool.ToVmt().ToText();
				Require( text.Contains( "%tooltexture", StringComparison.Ordinal ), "tool VMT must carry %tooltexture: " + name );
			}
			Require( MaterialBridge.ToolTexture( "blocklos" ) == MaterialBridge.ToolTexture( "tools/toolsblock_los" ),
				"blocklos must alias the canonical toolsblock_los" );
			Require( MaterialBridge.ToolTexture( "blocklos" )!.MaterialName == "tools/toolsblock_los",
				"blocklos material name must be the canonical tools/toolsblock_los" );
			foreach ( string extra in new[] { "playerclip", "npcclip", "ladder", "fog" } )
				Require( MaterialBridge.ToolTexture( extra ) != null, "extra registry tool must resolve: " + extra );
		} );

		Check( "FaceMaterial seam: nodraw by name resolves through the bridge", () =>
		{
			var face = FaceMaterial.NoDraw;
			Require( face.IsNoDraw, "FaceMaterial.NoDraw must report IsNoDraw" );
			var tool = MaterialBridge.ToolTexture( face.MaterialName );
			Require( tool != null, "FaceMaterial material name must resolve through the bridge: " + face.MaterialName );
			Require( tool!.Kind == ToolTextureKind.NoDraw, "FaceMaterial.NoDraw kind must be NoDraw" );
			Require( !face.ContributesToBakedMesh, "nodraw face must not contribute to the baked mesh" );
		} );

		Check( "glTF concrete round-trips colour, metalness, roughness and maps", () =>
		{
			var material = ConcretePbr();
			var json = MaterialBridge.ToGltfJson( material );
			Require( json.Contains( "baseColorFactor", StringComparison.Ordinal ), "glTF JSON must carry baseColorFactor" );
			var back = MaterialBridge.FromGltfJson( json );
			NearColor( back.BaseColor, new Vec3( 0.62f, 0.60f, 0.57f ), 0.002f, "base colour" );
			Near( back.Roughness, 0.9f, 0.002f, "roughness" );
			Near( back.Metallic, 0f, 0.001f, "metallic" );
			Require( back.BaseColorMap == "concrete/concretefloor001a", "base colour map must survive glTF extras, got " + back.BaseColorMap );
			Require( back.NormalMap == "concrete/concretefloor001a_normal", "normal map must survive glTF extras" );
			var indices = new Dictionary<string, int> { ["concrete/concretefloor001a"] = 0, ["concrete/concretefloor001a_normal"] = 1 };
			string indexed = MaterialBridge.ToGltfJson( material, indices );
			Require( indexed.Contains( "\"index\": 0", StringComparison.Ordinal ), "indexed glTF must carry baseColorTexture index 0" );
			Require( indexed.Contains( "\"index\": 1", StringComparison.Ordinal ), "indexed glTF must carry normalTexture index 1" );
			var backIndexed = MaterialBridge.FromGltfJson( indexed );
			Near( backIndexed.Roughness, 0.9f, 0.002f, "indexed roughness" );
		} );

		Check( "glTF glass keeps opacity and blend mode", () =>
		{
			var gltf = MaterialBridge.ToGltf( GlassPbr() );
			Require( gltf.AlphaMode == "BLEND", "glass glTF alphaMode must be BLEND, got " + gltf.AlphaMode );
			var back = MaterialBridge.FromGltfJson( gltf.ToJson() );
			Near( back.Opacity, 0.35f, 0.002f, "opacity" );
			Require( MaterialBridge.FromGltfJson( gltf.ToJson() ).Opacity < 1f, "glass must stay translucent after JSON round-trip" );
		} );

		Check( "glTF clearcoat and IOR extensions round-trip", () =>
		{
			var material = MetalPbr();
			material.Clearcoat = 0.5f;
			material.ClearcoatRoughness = 0.2f;
			material.Ior = 1.45f;
			string json = MaterialBridge.ToGltfJson( material );
			Require( json.Contains( "KHR_materials_clearcoat", StringComparison.Ordinal ), "glTF JSON must carry the clearcoat extension" );
			Require( json.Contains( "KHR_materials_ior", StringComparison.Ordinal ), "glTF JSON must carry the IOR extension" );
			var back = MaterialBridge.FromGltfJson( json );
			Near( back.Clearcoat, 0.5f, 0.002f, "clearcoat" );
			Near( back.ClearcoatRoughness, 0.2f, 0.002f, "clearcoat roughness" );
			Near( back.Ior, 1.45f, 0.002f, "ior" );
		} );

		Check( "defaults and invalid input fail safe, not silent", () =>
		{
			var empty = VmtMaterial.Parse( "\"LightmappedGeneric\"\n{\n}\n" );
			var pbr = empty.ToPbr();
			Near( pbr.Roughness, 0.6f, 0.001f, "missing $roughness must fall back to the PbrMaterial default" );
			Near( pbr.Metallic, 0f, 0.001f, "missing $metalness must be 0" );
			Near( pbr.Opacity, 1f, 0.001f, "missing $alpha must be opaque" );
			var bad = VmtMaterial.Parse( "\"LightmappedGeneric\"\n{\n\t\"$roughness\" \"banana\"\n}\n" );
			Near( bad.ToPbr().Roughness, 0.6f, 0.001f, "unparseable $roughness must fall back, not throw" );
			bool threw = false;
			try { MaterialBridge.ToVmt( null! ); } catch ( ArgumentNullException ) { threw = true; }
			Require( threw, "ToVmt(null) must throw ArgumentNullException" );
			threw = false;
			try { VmtMaterial.Parse( "" ); } catch ( FormatException ) { threw = true; }
			Require( threw, "Parse(\"\") must throw FormatException" );
			threw = false;
			try { VmtMaterial.Parse( "\"LightmappedGeneric\"\n{\n\t\"$roughness\" \"0.5\"\n" ); } catch ( FormatException ) { threw = true; }
			Require( threw, "unbalanced braces must throw FormatException" );
			threw = false;
			try { MaterialBridge.FromGltfJson( "{not json" ); } catch ( FormatException ) { threw = true; }
			Require( threw, "malformed glTF JSON must throw FormatException" );
		} );
	}
}
