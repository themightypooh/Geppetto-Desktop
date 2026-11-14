using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Effigy;

namespace Effigy.Export;

/// <summary>Thrown when a MaterialX document cannot be parsed; the message names the defect.</summary>
public sealed class MaterialXException : Exception
{
	public MaterialXException( string message ) : base( message ) { }
	public MaterialXException( string message, Exception inner ) : base( message, inner ) { }
}

/// <summary>One input on a MaterialX shader node: a literal value, a link to another node, or both (link wins).</summary>
public sealed class MaterialXInput
{
	public string Name { get; set; }
	public string Type { get; set; }
	public string Value { get; set; }
	public string NodeName { get; set; }

	public MaterialXInput( string name, string type, string value = null, string nodeName = null )
	{
		Name = name;
		Type = type;
		Value = value;
		NodeName = nodeName;
	}
}

/// <summary>One shader node in a MaterialX document: a standard_surface (OpenPBR 1.1) or a UsdPreviewSurface.</summary>
public sealed class MaterialXSurface
{
	public string Name { get; set; }
	public string ShaderKind { get; set; }
	public Dictionary<string, MaterialXInput> Inputs { get; } = new Dictionary<string, MaterialXInput>( StringComparer.Ordinal );

	public MaterialXSurface( string name, string shaderKind )
	{
		Name = name;
		ShaderKind = shaderKind;
	}

	public void Set( string name, string type, string value ) => Inputs[name] = new MaterialXInput( name, type, value );
	public void Link( string name, string type, string nodeName ) => Inputs[name] = new MaterialXInput( name, type, null, nodeName );

	public string GetValue( string name )
	{
		MaterialXInput input;
		return Inputs.TryGetValue( name, out input ) ? input.Value : null;
	}

	public float GetFloat( string name, float fallback )
	{
		string raw = GetValue( name );
		float value;
		return raw != null && float.TryParse( raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value ) ? value : fallback;
	}

	public Vec3 GetColor( string name, Vec3 fallback )
	{
		string raw = GetValue( name );
		if ( raw == null ) return fallback;
		string[] parts = raw.Split( new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length < 3 ) return fallback;
		float r, g, b;
		if ( !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r ) ) return fallback;
		if ( !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g ) ) return fallback;
		if ( !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b ) ) return fallback;
		return new Vec3( r, g, b );
	}
}

/// <summary>
/// MaterialX / OpenPBR material document (godlike-usd-stage-alembic-materialx).
///
/// Authoring form is OpenPBR 1.1 (<c>standard_surface</c>); UsdPreviewSurface
/// nodes are read as well, so a material authored for USD round-trips through
/// the same mapping. Texture inputs are <c>file</c> nodes in a nodegraph,
/// linked by nodename — no shader execution happens here: node graphs beyond
/// file-texture inputs are a named limitation, not a silent evaluation.
///
/// Conversion losses are returned by name on every mapping (see
/// <see cref="KnownLimitations"/>): transmission is a refraction model and
/// maps to opacity only approximately; OpenPBR sheen/fuzz and thin-film have
/// no UsdPreviewSurface equivalent at all.
/// </summary>
public sealed class MaterialXDocument
{
	public List<MaterialXSurface> Surfaces { get; } = new List<MaterialXSurface>();
	public Dictionary<string, string> TextureFiles { get; } = new Dictionary<string, string>( StringComparer.Ordinal );
	public Dictionary<string, string> MaterialBindings { get; } = new Dictionary<string, string>( StringComparer.Ordinal );

	public static IReadOnlyList<string> KnownLimitations => new List<string>
	{
		"MaterialX node graphs beyond file-texture inputs are not evaluated (no shader execution).",
		"OpenPBR sheen/fuzz and thin-film have no UsdPreviewSurface equivalent and are a named loss on conversion.",
		"Transmission maps to opacity only approximately (1 - transmission); refraction is not represented.",
		"Only standard_surface (OpenPBR 1.1) and UsdPreviewSurface shader nodes are authored.",
	};

	public MaterialXSurface AddSurface( string name, string shaderKind )
	{
		var surface = new MaterialXSurface( name, shaderKind );
		Surfaces.Add( surface );
		return surface;
	}

	public void AddTextureFile( string nodeName, string filePath )
	{
		if ( string.IsNullOrEmpty( nodeName ) ) throw new ArgumentException( "MaterialX texture node name is empty" );
		TextureFiles[nodeName] = filePath;
	}

	/// <summary>Build an OpenPBR document from a preview surface (the portable authoring form).</summary>
	public static MaterialXDocument FromPreviewSurface( UsdPreviewSurface surface, string name = null )
	{
		if ( surface == null ) throw new ArgumentNullException( nameof( surface ) );
		var doc = new MaterialXDocument();
		string surfaceName = string.IsNullOrEmpty( name ) ? surface.Name : name;
		var node = doc.AddSurface( surfaceName, "standard_surface" );
		node.Set( "base_color", "color3", ColorString( surface.DiffuseColor ) );
		node.Set( "base_metalness", "float", FloatString( surface.Metallic ) );
		node.Set( "specular_roughness", "float", FloatString( surface.Roughness ) );
		node.Set( "specular_IOR", "float", FloatString( surface.Ior ) );
		node.Set( "opacity", "float", FloatString( surface.Opacity ) );
		node.Set( "emission_color", "color3", ColorString( surface.EmissiveColor ) );
		node.Set( "coat", "float", FloatString( surface.Clearcoat ) );
		node.Set( "coat_roughness", "float", FloatString( surface.ClearcoatRoughness ) );
		LinkTexture( doc, node, "base_color", surface.DiffuseMap, "color3" );
		LinkTexture( doc, node, "specular_roughness", surface.RoughnessMap, "float" );
		LinkTexture( doc, node, "base_metalness", surface.MetallicMap, "float" );
		LinkTexture( doc, node, "emission_color", surface.EmissiveMap, "color3" );
		LinkTexture( doc, node, "normal", surface.NormalMap, "vector3" );
		doc.MaterialBindings[surfaceName + "_material"] = surfaceName;
		return doc;
	}

	static void LinkTexture( MaterialXDocument doc, MaterialXSurface node, string inputName, string filePath, string type )
	{
		if ( string.IsNullOrEmpty( filePath ) ) return;
		string nodeName = node.Name + "_" + inputName.Replace( "_", "" ) + "_tex";
		doc.AddTextureFile( nodeName, filePath );
		node.Link( inputName, type, nodeName );
	}

	/// <summary>Convert the first surface in the document to a preview surface. Every loss is named in <paramref name="losses"/>.</summary>
	public UsdPreviewSurface ToPreviewSurface( out List<string> losses )
	{
		if ( Surfaces.Count == 0 ) throw new MaterialXException( "MaterialX document has no shader surface to convert" );
		return ToPreviewSurface( Surfaces[0], out losses );
	}

	/// <summary>Convert one surface to a preview surface. Every loss is named in <paramref name="losses"/>.</summary>
	public UsdPreviewSurface ToPreviewSurface( MaterialXSurface surface, out List<string> losses )
	{
		if ( surface == null ) throw new ArgumentNullException( nameof( surface ) );
		losses = new List<string>();
		var preview = new UsdPreviewSurface { Name = surface.Name };
		bool isOpenPbr = surface.ShaderKind == "standard_surface";
		if ( isOpenPbr )
		{
			preview.DiffuseColor = surface.GetColor( "base_color", Vec3.One );
			preview.Metallic = surface.GetFloat( "base_metalness", 0f );
			preview.Roughness = surface.GetFloat( "specular_roughness", 0.5f );
			preview.Ior = surface.GetFloat( "specular_IOR", 1.5f );
			preview.EmissiveColor = surface.GetColor( "emission_color", Vec3.Zero );
			preview.Clearcoat = surface.GetFloat( "coat", 0f );
			preview.ClearcoatRoughness = surface.GetFloat( "coat_roughness", 0f );
			string opacity = surface.GetValue( "opacity" );
			if ( opacity != null ) preview.Opacity = surface.GetFloat( "opacity", 1f );
			else
			{
				string transmission = surface.GetValue( "transmission" );
				if ( transmission != null )
				{
					float t = surface.GetFloat( "transmission", 0f );
					preview.Opacity = 1f - t;
					losses.Add( "surface '" + surface.Name + "': transmission " + FloatString( t ) + " mapped to opacity " + FloatString( preview.Opacity ) + " (1 - transmission); refraction is not represented" );
				}
			}
			preview.DiffuseMap = ResolveTexture( surface, "base_color" );
			preview.RoughnessMap = ResolveTexture( surface, "specular_roughness" );
			preview.MetallicMap = ResolveTexture( surface, "base_metalness" );
			preview.EmissiveMap = ResolveTexture( surface, "emission_color" );
			preview.NormalMap = ResolveTexture( surface, "normal" );
			foreach ( string dropped in new[] { "sheen", "sheen_color", "sheen_roughness", "thin_film_thickness", "thin_film_IOR" } )
				if ( surface.Inputs.ContainsKey( dropped ) )
					losses.Add( "surface '" + surface.Name + "': OpenPBR '" + dropped + "' has no UsdPreviewSurface equivalent and was dropped" );
		}
		else
		{
			preview.DiffuseColor = surface.GetColor( "diffuseColor", Vec3.One );
			preview.Metallic = surface.GetFloat( "metallic", 0f );
			preview.Roughness = surface.GetFloat( "roughness", 0.5f );
			preview.Opacity = surface.GetFloat( "opacity", 1f );
			preview.OpacityThreshold = surface.GetFloat( "opacityThreshold", 0f );
			preview.EmissiveColor = surface.GetColor( "emissiveColor", Vec3.Zero );
			preview.Clearcoat = surface.GetFloat( "clearcoat", 0f );
			preview.ClearcoatRoughness = surface.GetFloat( "clearcoatRoughness", 0f );
			preview.Ior = surface.GetFloat( "ior", 1.5f );
			preview.DiffuseMap = ResolveTexture( surface, "diffuseColor" );
			preview.NormalMap = ResolveTexture( surface, "normal" );
			foreach ( var pair in surface.Inputs )
				if ( pair.Key != "diffuseColor" && pair.Key != "metallic" && pair.Key != "roughness" && pair.Key != "opacity" && pair.Key != "opacityThreshold" && pair.Key != "emissiveColor" && pair.Key != "clearcoat" && pair.Key != "clearcoatRoughness" && pair.Key != "ior" && pair.Key != "normal" )
					losses.Add( "surface '" + surface.Name + "': UsdPreviewSurface input '" + pair.Key + "' has no kernel mapping and was dropped" );
		}
		return preview;
	}

	string ResolveTexture( MaterialXSurface surface, string inputName )
	{
		MaterialXInput input;
		if ( !surface.Inputs.TryGetValue( inputName, out input ) ) return null;
		if ( string.IsNullOrEmpty( input.NodeName ) ) return null;
		string file;
		return TextureFiles.TryGetValue( input.NodeName, out file ) ? file : null;
	}

	static string FloatString( float value ) => value.ToString( "R", CultureInfo.InvariantCulture );
	static string ColorString( Vec3 color ) => FloatString( color.x ) + ", " + FloatString( color.y ) + ", " + FloatString( color.z );

	public string Write()
	{
		var root = new XElement( "materialx", new XAttribute( "version", "1.39" ) );
		foreach ( var surface in Surfaces )
		{
			var node = new XElement( surface.ShaderKind,
				new XAttribute( "name", surface.Name ),
				new XAttribute( "type", "surfaceshader" ) );
			foreach ( var pair in surface.Inputs )
			{
				var input = new XElement( "input",
					new XAttribute( "name", pair.Value.Name ),
					new XAttribute( "type", pair.Value.Type ) );
				if ( !string.IsNullOrEmpty( pair.Value.Value ) ) input.SetAttributeValue( "value", pair.Value.Value );
				if ( !string.IsNullOrEmpty( pair.Value.NodeName ) ) input.SetAttributeValue( "nodename", pair.Value.NodeName );
				node.Add( input );
			}
			root.Add( node );
		}
		if ( TextureFiles.Count > 0 )
		{
			var graph = new XElement( "nodegraph", new XAttribute( "name", "textures" ) );
			foreach ( var pair in TextureFiles )
			{
				var file = new XElement( "file",
					new XAttribute( "name", pair.Key ),
					new XAttribute( "type", "filename" ) );
				file.Add( new XElement( "input",
					new XAttribute( "name", "file" ),
					new XAttribute( "type", "filename" ),
					new XAttribute( "value", pair.Value ) ) );
				graph.Add( file );
			}
			root.Add( graph );
		}
		foreach ( var pair in MaterialBindings )
		{
			var material = new XElement( "material", new XAttribute( "name", pair.Key ) );
			material.Add( new XElement( "shaderref",
				new XAttribute( "name", "sr" ),
				new XAttribute( "node", pair.Value ) ) );
			root.Add( material );
		}
		var doc = new XDocument( new XDeclaration( "1.0", "UTF-8", null ), root );
		return doc.ToString();
	}

	public void WriteFile( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "MaterialX output path is empty" );
		var dir = Path.GetDirectoryName( filePath );
		if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
		File.WriteAllText( filePath, Write() );
	}

	public static MaterialXDocument Parse( string xml )
	{
		if ( string.IsNullOrWhiteSpace( xml ) ) throw new MaterialXException( "MaterialX document is empty" );
		XDocument doc;
		try { doc = XDocument.Parse( xml ); }
		catch ( Exception ex ) { throw new MaterialXException( "MaterialX document is not well-formed XML: " + ex.Message, ex ); }
		return ParseDocument( doc );
	}

	public static MaterialXDocument ParseFile( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "MaterialX input path is empty" );
		if ( !File.Exists( filePath ) ) throw new FileNotFoundException( "MaterialX file not found: " + filePath );
		return Parse( File.ReadAllText( filePath ) );
	}

	static MaterialXDocument ParseDocument( XDocument doc )
	{
		var root = doc.Root;
		if ( root == null || root.Name.LocalName != "materialx" )
			throw new MaterialXException( "MaterialX document root must be <materialx>" );
		var document = new MaterialXDocument();
		foreach ( var element in root.Descendants() )
		{
			string local = element.Name.LocalName;
			if ( local == "standard_surface" || local == "UsdPreviewSurface" )
			{
				var surface = document.AddSurface( Attr( element, "name" ) ?? "Material", local );
				foreach ( var input in element.Elements( "input" ) )
				{
					string name = Attr( input, "name" );
					if ( string.IsNullOrEmpty( name ) ) continue;
					surface.Inputs[name] = new MaterialXInput( name, Attr( input, "type" ) ?? "float", Attr( input, "value" ), Attr( input, "nodename" ) );
				}
			}
			else if ( local == "file" )
			{
				string nodeName = Attr( element, "name" );
				var fileInput = element.Elements( "input" ).FirstOrDefault( e => Attr( e, "name" ) == "file" );
				if ( !string.IsNullOrEmpty( nodeName ) && fileInput != null && !string.IsNullOrEmpty( Attr( fileInput, "value" ) ) )
					document.TextureFiles[nodeName] = Attr( fileInput, "value" );
			}
			else if ( local == "shaderref" )
			{
				var material = element.Parent;
				if ( material != null && material.Name.LocalName == "material" && !string.IsNullOrEmpty( Attr( material, "name" ) ) && !string.IsNullOrEmpty( Attr( element, "node" ) ) )
					document.MaterialBindings[Attr( material, "name" )] = Attr( element, "node" );
			}
		}
		if ( document.Surfaces.Count == 0 )
			throw new MaterialXException( "MaterialX document has no standard_surface or UsdPreviewSurface node" );
		return document;
	}

	static string Attr( XElement element, string name )
	{
		var attr = element.Attribute( name );
		return attr == null ? null : attr.Value;
	}
}
