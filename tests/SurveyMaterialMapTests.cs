using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Effigy;
using Marionette.Desktop;

internal static class SurveyMaterialMapTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		static void Require( bool ok, string why ) { if ( !ok ) throw new Exception( why ); }
		string Fixture( string name )
		{
			string root = Path.Combine( settings.WorkDirectory, "survey-material-map", Guid.NewGuid().ToString( "N" ), name );
			Directory.CreateDirectory( root );
			return root;
		}

		suite.Add( "survey-material-map", "head and suit aliases remain source-scoped", () =>
		{
			string root = Fixture( "aliases" );
			var paths = new AssetPathResolver( root );
			var suit = new SurveyMaterialMap( paths, SurveyMaterialMap.SuitModel );
			var head = new SurveyMaterialMap( paths, SurveyMaterialMap.HeadModel );
			Require( suit.Remaps.Count == 16 && head.Remaps.Count == 11, "source remap counts" );
			Require( suit.Remaps.Single( x => x.Slot == "hab_mustard" ).VmatPath == "materials/hab/boot_rubber.vmat", "non-obvious boot remap" );
			Require( head.Remaps.Single( x => x.Slot == "collar_plate" ).VmatPath == "materials/hab/collar_plate.vmat", "head/neck remap" );
			foreach ( var (map, slot) in new[] { (suit, "suit_body"), (head, "face_skin") } )
			{
				var raw = map.ResolveSlot( 7, slot, validateTextures: false );
				var alias = map.ResolveSlot( 8, slot + ".vmat", validateTextures: false );
				Require( raw.SlotIndex == 7 && alias.SlotIndex == 8 && raw.SlotName == slot && alias.SlotName == slot + ".vmat", "slot identity retained" );
				Require( raw.Diagnostics.Any( d => d.AssetPath == map.Remaps.Single( x => x.Slot == slot ).VmatPath ), "authored path named even when missing" );
			}
			Require( !SurveyMaterialMap.IsSurveyModel( "models/other/player_suit.vmdl" ), "no basename guessing" );
			try { _ = new SurveyMaterialMap( paths, "models/other/player_suit.vmdl" ); throw new Exception( "non-Survey must reject map" ); }
			catch ( ArgumentException ) { }
		} );

		suite.Add( "survey-material-map", "read-only relative references survive project move and diagnose effects", () =>
		{
			string original = Fixture( "original" );
			string vmat = Path.Combine( original, "materials/hab/hab_glow_green.vmat" );
			Directory.CreateDirectory( Path.GetDirectoryName( vmat )! );
			File.WriteAllText( vmat, "shader \"shaders/ps1_lit.shader\"\nTextureColor \"materials/hab/glow.png\"\ng_flPs1Glow \"2\"\ng_flSignalPalette \"6\"\n" );
			string png = Path.Combine( original, "materials/hab/glow.png" );
			PngWriter.WriteFileRgba( png, new byte[] { 255, 32, 8, 255 }, 1, 1 );
			string moved = Path.Combine( Path.GetDirectoryName( original )!, "moved" );
			Directory.Move( original, moved );
			var map = new SurveyMaterialMap( new AssetPathResolver( moved ), SurveyMaterialMap.HeadModel );
			var result = map.ResolveSlot( 3, "hab_glow_green" );
			Require( result.Success, "relative image should resolve after moving tree" );
			Require( result.Descriptor.BaseColorMap?.Reference.Path == "materials/hab/glow.png", "reference remains relative" );
			Require( result.Descriptor.BaseColorMap?.ColorSpace == ColorSpaceInterpretation.Srgb, "color interpretation" );
			Require( result.Descriptor.EmissionMap is null && result.Descriptor.EmissionFactor.x == 0, "shader glow is not native emission" );
			Require( result.Diagnostics.Any( d => d.Message.Contains( "ps1_lit.shader" ) ), "custom shader warning" );
			Require( result.Diagnostics.Any( d => d.Message.Contains( "g_flPs1Glow" ) ), "glow warning" );
			Require( result.Diagnostics.Any( d => d.Message.Contains( "g_flSignalPalette" ) ), "palette warning" );
			Require( Directory.GetFiles( moved, "*", SearchOption.AllDirectories ).Length == 2, "no source writes" );
		} );

		suite.Add( "survey-material-map", "non-Survey Effigy slot retains ordinary fallback", () =>
		{
			string root = Fixture( "other-document" );
			var resolver = new MaterialResolver( new AssetPathResolver( root ), "other.effigy" );
			var plain = resolver.ResolveSlot( 4, "unrelated_cloth" );
			Require( plain.Success && plain.Descriptor.BaseColorMap is null && plain.SlotIndex == 4, "mapless material retains generic fallback" );
			var sameName = resolver.ResolveSlot( 5, "face_skin" );
			Require( sameName.Success && sameName.Descriptor.BaseColorMap is null, "Survey slot name alone cannot inject a texture" );
			Require( resolver.Diagnostics.Count == 0, "no missing Survey textures in other document" );
		} );

		string? surveyRoot = Path.GetDirectoryName( Path.GetDirectoryName( Path.GetDirectoryName( settings.SurveyPath ) ) );
		string? missing = surveyRoot is null || !File.Exists( Path.Combine( surveyRoot, SurveyMaterialMap.SuitModel ) ) ||
			!File.Exists( Path.Combine( surveyRoot, SurveyMaterialMap.HeadModel ) ) ? "Survey source ModelDocs not available" : null;
		suite.Add( "survey-material-map", "source ModelDoc remaps and texture paths inventory", () =>
		{
			var paths = new AssetPathResolver( surveyRoot! );
			foreach ( string model in new[] { SurveyMaterialMap.SuitModel, SurveyMaterialMap.HeadModel } )
			{
				var map = new SurveyMaterialMap( paths, model );
				string text = File.ReadAllText( Path.Combine( surveyRoot!, model ) );
				var source = Regex.Matches( text, "from = \"([^\"]+)\" to = \"([^\"]+)\"" )
					.Select( m => (Slot: m.Groups[1].Value, Vmat: m.Groups[2].Value) ).ToArray();
				Require( source.Length == map.Remaps.Count * 2, $"{model}: remap inventory incomplete" );
				foreach ( var remap in map.Remaps )
				{
					Require( source.Contains( (remap.Slot, remap.VmatPath) ) && source.Contains( (remap.Slot + ".vmat", remap.VmatPath) ), $"{model}: wrong remap for {remap.Slot}" );
					var sourceVmat = paths.ResolveRead( new AssetReference( remap.Slot, remap.VmatPath ) );
					var vmatText = File.ReadAllText( sourceVmat.CanonicalPath );
					foreach ( Match m in Regex.Matches( vmatText, "Texture(?:Color|Normal|Roughness|Metalness)\\s+\"([^\"]+)\"" ) )
						Require( paths.ResolveRead( new AssetReference( remap.Slot, m.Groups[1].Value ) ).Exists, $"missing {m.Groups[1].Value}" );
					var result = map.ResolveSlot( 0, remap.Slot );
					Require( result.Success && result.Descriptor.BaseColorMap is not null && result.Diagnostics.Any( d => d.Message.Contains( "ps1_lit.shader" ) ), $"{remap.Slot}: source resolution" );
				}
			}
		}, () => missing, required: settings.RequireLocalFixtures );
	}
}
