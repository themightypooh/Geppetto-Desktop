using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Effigy;

/// <summary>
/// MaterialX / OpenPBR material interchange (godlike-usd-stage-alembic-materialx, Platform P0).
///
/// Bounded subset, pure C# (System.Xml.Linq, no native MaterialX library): a
/// MaterialX document holding standard_surface / open_pbr_surface / UsdPreviewSurface
/// shader nodes, image + normalmap texture nodes, nodegraphs and surfacematerials.
/// PbrMaterial is the shared record both MaterialX and UsdPreviewSurface map through,
/// so a look survives USD -&gt; MaterialX -&gt; USD with its values intact.
///
/// LOSSES: every node type or input the subset does not model lands in
/// Document.Losses by name — no silent drops. A nodegraph that only feeds an
/// unsupported node still parses; only its unmapped inputs are losses.
/// </summary>
public sealed class PbrMaterial
{
	public string Name = "Material";
	/// <summary>Linear base colour (MaterialX color3 is linear, same as USD).</summary>
	public Vec3 BaseColor = new( 0.18f, 0.18f, 0.18f );
	public string BaseColorMap;
	public float Metallic;
	public string MetallicMap;
	public float Roughness = 0.6f;
	public string RoughnessMap;
	public string NormalMap;
	public float Opacity = 1f;
	public string OpacityMap;
	public Vec3 Emissive = new( 0f, 0f, 0f );
	public string EmissiveMap;
	public float Clearcoat;
	public float ClearcoatRoughness = 0.1f;
	public float Ior = 1.5f;

	public static PbrMaterial FromUsd( UsdPreviewSurface surface, string name = "Material" ) => new()
	{
		Name = name,
		BaseColor = surface.DiffuseColor, BaseColorMap = surface.DiffuseMap,
		Metallic = surface.Metallic, MetallicMap = surface.MetallicMap,
		Roughness = surface.Roughness, RoughnessMap = surface.RoughnessMap,
		NormalMap = surface.NormalMap,
		Opacity = surface.Opacity, OpacityMap = surface.OpacityMap,
		Emissive = surface.EmissiveColor, EmissiveMap = surface.EmissiveMap,
		Clearcoat = surface.Clearcoat, ClearcoatRoughness = surface.ClearcoatRoughness,
		Ior = surface.Ior,
	};

	public UsdPreviewSurface ToUsd() => new()
	{
		DiffuseColor = BaseColor, DiffuseMap = BaseColorMap,
		Metallic = Metallic, MetallicMap = MetallicMap,
		Roughness = Roughness, RoughnessMap = RoughnessMap,
		NormalMap = NormalMap,
		Opacity = Opacity, OpacityMap = OpacityMap,
		EmissiveColor = Emissive, EmissiveMap = EmissiveMap,
		Clearcoat = Clearcoat, ClearcoatRoughness = ClearcoatRoughness,
		Ior = Ior,
	};
}

/// <summary>One input (or parameter) on a MaterialX node: a literal value, a connection, or both.</summary>
public sealed class MxInput
{
	public string Name;
	public string Type;
	public object Value;
	/// <summary>Connected node name, or null for a literal.</summary>
	public string NodeName;
	public string Output = "out";

	public MxInput Clone() => new() { Name = Name, Type = Type, Value = Value, NodeName = NodeName, Output = Output };
}

/// <summary>One node in a MaterialX document (shader, image, normalmap, surfacematerial, ...).</summary>
public sealed class MxNode
{
	public string Name;
	public string Type;
	public Dictionary<string, MxInput> Inputs = new( StringComparer.Ordinal );
	public Dictionary<string, string> Outputs = new( StringComparer.Ordinal );

	public MxInput Input( string name ) => Inputs.TryGetValue( name, out var input ) ? input : null;

	public MxNode Clone()
	{
		var copy = new MxNode { Name = Name, Type = Type, Outputs = new Dictionary<string, string>( Outputs ) };
		foreach ( var pair in Inputs ) copy.Inputs[pair.Key] = pair.Value.Clone();
		return copy;
	}
}

public sealed class MxNodeGraph
{
	public string Name;
	public List<MxNode> Nodes = new();
}

public sealed class MxMaterial
{
	public string Name;
	/// <summary>Node the surfacematerial's surfaceshader points at.</summary>
	public string ShaderNode;
}

/// <summary>
/// A MaterialX document: the bounded subset above. Parse never throws on an
/// unknown node type or input — it records a loss and keeps the rest.
/// </summary>
public sealed class MaterialXDocument
{
	public const string Version = "1.39";

	public List<MxMaterial> Materials = new();
	public List<MxNodeGraph> NodeGraphs = new();
	/// <summary>Nodes at document level (shader nodes, surfacematerials, image nodes).</summary>
	public List<MxNode> Nodes = new();
	/// <summary>Named loss list: unsupported node types and unmapped inputs.</summary>
	public List<string> Losses = new();

	public MxNode FindNode( string name )
	{
		foreach ( var node in Nodes )
			if ( node.Name == name ) return node;
		foreach ( var graph in NodeGraphs )
			foreach ( var node in graph.Nodes )
				if ( node.Name == name ) return node;
		return null;
	}

	public static MaterialXDocument Parse( string xml )
	{
		if ( xml is null ) throw new ArgumentNullException( nameof( xml ) );
		var doc = new MaterialXDocument();
		XDocument parsed;
		try { parsed = XDocument.Parse( xml ); }
		catch ( Exception e ) { throw new FormatException( "Not a MaterialX document: " + e.Message, e ); }
		var root = parsed.Root;
		if ( root is null || root.Name.LocalName != "materialx" )
			throw new FormatException( "Not a MaterialX document: root element is not <materialx>." );
		var version = root.Attribute( "version" )?.Value ?? "";
		if ( version.Length > 0 && !version.StartsWith( "1.", StringComparison.Ordinal ) )
			doc.Losses.Add( $"materialx version {version} is not a 1.x document; parsed as 1.x" );
		foreach ( var element in root.Elements() )
		{
			switch ( element.Name.LocalName )
			{
				case "nodegraph":
				{
					var graph = new MxNodeGraph { Name = (string)element.Attribute( "name" ) ?? "NodeGraph" };
					foreach ( var child in element.Elements() ) ParseNode( doc, graph.Nodes, child );
					doc.NodeGraphs.Add( graph );
					break;
				}
				case "surfacematerial":
				{
					var material = ParseNode( doc, doc.Nodes, element );
					var shader = material?.Input( "surfaceshader" )?.NodeName;
					if ( material is not null ) doc.Materials.Add( new MxMaterial { Name = material.Name, ShaderNode = shader } );
					break;
				}
				default:
					ParseNode( doc, doc.Nodes, element );
					break;
			}
		}
		return doc;
	}

	static MxNode ParseNode( MaterialXDocument doc, List<MxNode> into, XElement element )
	{
		var type = element.Name.LocalName;
		var name = (string)element.Attribute( "name" ) ?? type;
		switch ( type )
		{
			case "standard_surface":
			case "open_pbr_surface":
			case "UsdPreviewSurface":
			case "image":
			case "normalmap":
			case "surfacematerial":
			case "UsdPrimvarReader_float2":
			case "UsdUVTexture":
				break;
			default:
				doc.Losses.Add( $"unsupported node type '{type}' (node '{name}') skipped" );
				return null;
		}
		var node = new MxNode { Name = name, Type = type };
		foreach ( var child in element.Elements() )
		{
			if ( child.Name.LocalName == "input" || child.Name.LocalName == "parameter" )
			{
				var input = new MxInput
				{
					Name = (string)child.Attribute( "name" ) ?? "",
					Type = (string)child.Attribute( "type" ) ?? "float",
					NodeName = (string)child.Attribute( "nodename" ),
					Output = (string)child.Attribute( "output" ) ?? "out",
				};
				var value = (string)child.Attribute( "value" );
				if ( value is not null ) input.Value = ParseTypedValue( input.Type, value );
				if ( input.Name.Length > 0 ) node.Inputs[input.Name] = input;
			}
			else if ( child.Name.LocalName == "output" )
			{
				var outputName = (string)child.Attribute( "name" ) ?? "out";
				node.Outputs[outputName] = (string)child.Attribute( "type" ) ?? "";
			}
			else
			{
				doc.Losses.Add( $"unsupported child element '{child.Name.LocalName}' on node '{name}' skipped" );
			}
		}
		into.Add( node );
		return node;
	}

	static object ParseTypedValue( string type, string value )
	{
		switch ( type )
		{
			case "float": return float.Parse( value, CultureInfo.InvariantCulture );
			case "integer": return int.Parse( value, CultureInfo.InvariantCulture );
			case "boolean": return value == "true" || value == "1";
			case "color3": case "vector3": case "normal3":
			{
				var parts = value.Split( ',' );
				return parts.Length >= 3
					? new Vec3( ParseFloat( parts[0] ), ParseFloat( parts[1] ), ParseFloat( parts[2] ) )
					: new Vec3( 0f, 0f, 0f );
			}
			case "vector2": case "float2":
			{
				var parts = value.Split( ',' );
				return parts.Length >= 2 ? new Vec2( ParseFloat( parts[0] ), ParseFloat( parts[1] ) ) : new Vec2( 0f, 0f );
			}
			case "color4":
			{
				var parts = value.Split( ',' );
				var floats = new List<float>();
				foreach ( var part in parts ) floats.Add( ParseFloat( part ) );
				return floats;
			}
			default: return value; // string, filename, token, and anything else stay literal
		}
	}

	static float ParseFloat( string text ) => float.Parse( text.Trim(), CultureInfo.InvariantCulture );

	static string FormatTypedValue( object value )
	{
		switch ( value )
		{
			case null: return "";
			case float f: return f.ToString( "0.######", CultureInfo.InvariantCulture );
			case int i: return i.ToString( CultureInfo.InvariantCulture );
			case bool b: return b ? "true" : "false";
			case string s: return s;
			case Vec2 v2: return $"{v2.x.ToString( "0.######", CultureInfo.InvariantCulture )}, {v2.y.ToString( "0.######", CultureInfo.InvariantCulture )}";
			case Vec3 v3: return $"{v3.x.ToString( "0.######", CultureInfo.InvariantCulture )}, {v3.y.ToString( "0.######", CultureInfo.InvariantCulture )}, {v3.z.ToString( "0.######", CultureInfo.InvariantCulture )}";
			case List<float> floats: return string.Join( ", ", floats.Select( f => f.ToString( "0.######", CultureInfo.InvariantCulture ) ) );
			default: return value.ToString();
		}
	}

	/// <summary>Write the document back to .mtlx text. Round-trips Parse for the modelled subset.</summary>
	public string Write()
	{
		var root = new XElement( "materialx", new XAttribute( "version", Version ) );
		var doc = new XDocument( new XDeclaration( "1.0", "utf-8", null ), root );
		foreach ( var graph in NodeGraphs )
		{
			var graphElement = new XElement( "nodegraph", new XAttribute( "name", graph.Name ) );
			foreach ( var node in graph.Nodes ) graphElement.Add( WriteNode( node ) );
			root.Add( graphElement );
		}
		foreach ( var node in Nodes ) root.Add( WriteNode( node ) );
		if ( Losses.Count > 0 )
			root.Add( new XComment( " Geppetto loss list: " + string.Join( "; ", Losses ) + " " ) );
		return doc.ToString();
	}

	static XElement WriteNode( MxNode node )
	{
		var element = new XElement( node.Type, new XAttribute( "name", node.Name ) );
		foreach ( var pair in node.Inputs )
		{
			var input = pair.Value;
			var child = new XElement( "input",
				new XAttribute( "name", input.Name ),
				new XAttribute( "type", input.Type ) );
			if ( input.NodeName is not null )
			{
				child.Add( new XAttribute( "nodename", input.NodeName ) );
				child.Add( new XAttribute( "output", input.Output ) );
			}
			else if ( input.Value is not null )
			{
				child.Add( new XAttribute( "value", FormatTypedValue( input.Value ) ) );
			}
			element.Add( child );
		}
		foreach ( var pair in node.Outputs )
			element.Add( new XElement( "output", new XAttribute( "name", pair.Key ), new XAttribute( "type", pair.Value ) ) );
		return element;
	}

	/// <summary>Read a material out of the document as a PbrMaterial. Unmapped inputs are losses, not exceptions.</summary>
	public PbrMaterial ToPbr( string materialName )
	{
		var material = Materials.Find( m => m.Name == materialName );
		if ( material is null )
		{
			Losses.Add( $"material '{materialName}' not found" );
			return null;
		}
		var shader = material.ShaderNode is not null ? FindNode( material.ShaderNode ) : null;
		if ( shader is null )
		{
			Losses.Add( $"material '{materialName}' has no surfaceshader node" );
			return null;
		}
		var pbr = new PbrMaterial { Name = materialName };
		// Input name per shader family: standard_surface / open_pbr_surface share most names;
		// UsdPreviewSurface uses its own (diffuseColor, metallic, roughness, ...).
		var isUsd = shader.Type == "UsdPreviewSurface";
		string MapFile( string inputName )
		{
			var input = shader.Input( inputName );
			if ( input?.NodeName is null ) return null;
			var node = FindNode( input.NodeName );
			if ( node is null ) { Losses.Add( $"material '{materialName}' input {inputName} connects to missing node '{input.NodeName}'" ); return null; }
			if ( node.Type == "image" ) return node.Input( "file" )?.Value as string;
			if ( node.Type == "normalmap" )
			{
				var source = node.Input( "in" );
				var image = source?.NodeName is not null ? FindNode( source.NodeName ) : null;
				return image?.Type == "image" ? image.Input( "file" )?.Value as string : null;
			}
			Losses.Add( $"material '{materialName}' input {inputName} connects to unsupported node type '{node.Type}'" );
			return null;
		}
		float F( string inputName, float fallback )
		{
			var value = shader.Input( inputName )?.Value;
			return value is float f ? f : value is int i ? i : fallback;
		}
		Vec3 V( string inputName, Vec3 fallback )
		{
			return shader.Input( inputName )?.Value is Vec3 v ? v : fallback;
		}
		if ( isUsd )
		{
			pbr.BaseColor = V( "diffuseColor", pbr.BaseColor );
			pbr.BaseColorMap = MapFile( "diffuseColor" );
			pbr.Metallic = F( "metallic", pbr.Metallic );
			pbr.MetallicMap = MapFile( "metallic" );
			pbr.Roughness = F( "roughness", pbr.Roughness );
			pbr.RoughnessMap = MapFile( "roughness" );
			pbr.NormalMap = MapFile( "normal" );
			pbr.Opacity = F( "opacity", pbr.Opacity );
			pbr.OpacityMap = MapFile( "opacity" );
			pbr.Emissive = V( "emissiveColor", pbr.Emissive );
			pbr.EmissiveMap = MapFile( "emissiveColor" );
			pbr.Clearcoat = F( "clearcoat", pbr.Clearcoat );
			pbr.ClearcoatRoughness = F( "clearcoatRoughness", pbr.ClearcoatRoughness );
			pbr.Ior = F( "ior", pbr.Ior );
		}
		else
		{
			pbr.BaseColor = V( "base_color", pbr.BaseColor );
			pbr.BaseColorMap = MapFile( "base_color" );
			pbr.Metallic = F( "base_metalness", pbr.Metallic );
			pbr.MetallicMap = MapFile( "base_metalness" );
			pbr.Roughness = F( "specular_roughness", pbr.Roughness );
			pbr.RoughnessMap = MapFile( "specular_roughness" );
			pbr.NormalMap = MapFile( "normal" );
			pbr.Opacity = F( "opacity", pbr.Opacity );
			pbr.OpacityMap = MapFile( "opacity" );
			pbr.Emissive = V( "emission_color", pbr.Emissive );
			pbr.EmissiveMap = MapFile( "emission_color" );
			pbr.Clearcoat = F( "coat_weight", pbr.Clearcoat );
			pbr.ClearcoatRoughness = F( "coat_roughness", pbr.ClearcoatRoughness );
			pbr.Ior = F( "specular_ior", pbr.Ior );
		}
		// Anything the shader carries that the record has no field for is a named loss, not a silent drop.
		var known = isUsd
			? new HashSet<string>( StringComparer.Ordinal ) { "diffuseColor", "metallic", "roughness", "normal", "opacity", "emissiveColor", "clearcoat", "clearcoatRoughness", "ior" }
			: new HashSet<string>( StringComparer.Ordinal ) { "base_color", "base_metalness", "specular_roughness", "normal", "opacity", "emission_color", "coat_weight", "coat_roughness", "specular_ior" };
		foreach ( var pair in shader.Inputs )
			if ( !known.Contains( pair.Key ) ) Losses.Add( $"material '{materialName}' input '{pair.Key}' has no PbrMaterial field" );
		return pbr;
	}

	/// <summary>Build a document from a PbrMaterial: one nodegraph (texture nodes + shader) + one surfacematerial.</summary>
	public static MaterialXDocument FromPbr( PbrMaterial material, bool openPbr = false )
	{
		var doc = new MaterialXDocument();
		var graphName = "NG_" + material.Name;
		var graph = new MxNodeGraph { Name = graphName };
		doc.NodeGraphs.Add( graph );
		var shaderType = openPbr ? "open_pbr_surface" : "standard_surface";
		var shader = new MxNode { Name = material.Name + "_shader", Type = shaderType };
		graph.Nodes.Add( shader );
		void Image( string inputName, string inputType, string map, string nodeName, bool srgb )
		{
			if ( map is null ) return;
			var image = new MxNode { Name = nodeName, Type = "image" };
			image.Inputs["file"] = new MxInput { Name = "file", Type = "filename", Value = map };
			image.Inputs["sourceColorSpace"] = new MxInput { Name = "sourceColorSpace", Type = "string", Value = srgb ? "srgb_texture" : "raw" };
			image.Outputs["out"] = "color3";
			graph.Nodes.Add( image );
			shader.Inputs[inputName] = new MxInput { Name = inputName, Type = inputType, NodeName = nodeName, Output = "out" };
		}
		void Normal( string map )
		{
			if ( map is null ) return;
			var image = new MxNode { Name = material.Name + "_normal_tex", Type = "image" };
			image.Inputs["file"] = new MxInput { Name = "file", Type = "filename", Value = map };
			image.Inputs["sourceColorSpace"] = new MxInput { Name = "sourceColorSpace", Type = "string", Value = "raw" };
			image.Outputs["out"] = "color3";
			graph.Nodes.Add( image );
			var normalmap = new MxNode { Name = material.Name + "_normalmap", Type = "normalmap" };
			normalmap.Inputs["in"] = new MxInput { Name = "in", Type = "vector3", NodeName = image.Name, Output = "out" };
			normalmap.Inputs["space"] = new MxInput { Name = "space", Type = "string", Value = "tangent" };
			normalmap.Outputs["out"] = "vector3";
			graph.Nodes.Add( normalmap );
			shader.Inputs["normal"] = new MxInput { Name = "normal", Type = "vector3", NodeName = normalmap.Name, Output = "out" };
		}
		void Scalar( string inputName, string type, object value ) =>
			shader.Inputs[inputName] = new MxInput { Name = inputName, Type = type, Value = value };
		if ( openPbr )
		{
			if ( material.BaseColorMap is null ) Scalar( "base_color", "color3", material.BaseColor );
			else Image( "base_color", "color3", material.BaseColorMap, material.Name + "_base_tex", srgb: true );
			if ( material.MetallicMap is null ) Scalar( "base_metalness", "float", material.Metallic );
			else Image( "base_metalness", "float", material.MetallicMap, material.Name + "_metal_tex", srgb: false );
			if ( material.RoughnessMap is null ) Scalar( "specular_roughness", "float", material.Roughness );
			else Image( "specular_roughness", "float", material.RoughnessMap, material.Name + "_rough_tex", srgb: false );
			Normal( material.NormalMap );
			if ( material.OpacityMap is null && material.Opacity < 1f ) Scalar( "opacity", "float", material.Opacity );
			else if ( material.OpacityMap is not null ) Image( "opacity", "float", material.OpacityMap, material.Name + "_opacity_tex", srgb: false );
			if ( material.EmissiveMap is null && (material.Emissive.x != 0f || material.Emissive.y != 0f || material.Emissive.z != 0f) ) Scalar( "emission_color", "color3", material.Emissive );
			else if ( material.EmissiveMap is not null ) Image( "emission_color", "color3", material.EmissiveMap, material.Name + "_emissive_tex", srgb: true );
			if ( material.Clearcoat > 0f ) Scalar( "coat_weight", "float", material.Clearcoat );
			if ( material.ClearcoatRoughness != 0.1f ) Scalar( "coat_roughness", "float", material.ClearcoatRoughness );
			if ( material.Ior != 1.5f ) Scalar( "specular_ior", "float", material.Ior );
		}
		else
		{
			if ( material.BaseColorMap is null ) Scalar( "base_color", "color3", material.BaseColor );
			else Image( "base_color", "color3", material.BaseColorMap, material.Name + "_base_tex", srgb: true );
			if ( material.MetallicMap is null ) Scalar( "base_metalness", "float", material.Metallic );
			else Image( "base_metalness", "float", material.MetallicMap, material.Name + "_metal_tex", srgb: false );
			if ( material.RoughnessMap is null ) Scalar( "specular_roughness", "float", material.Roughness );
			else Image( "specular_roughness", "float", material.RoughnessMap, material.Name + "_rough_tex", srgb: false );
			Normal( material.NormalMap );
			if ( material.OpacityMap is null && material.Opacity < 1f ) Scalar( "opacity", "float", material.Opacity );
			else if ( material.OpacityMap is not null ) Image( "opacity", "float", material.OpacityMap, material.Name + "_opacity_tex", srgb: false );
			if ( material.EmissiveMap is null && (material.Emissive.x != 0f || material.Emissive.y != 0f || material.Emissive.z != 0f) ) Scalar( "emission_color", "color3", material.Emissive );
			else if ( material.EmissiveMap is not null ) Image( "emission_color", "color3", material.EmissiveMap, material.Name + "_emissive_tex", srgb: true );
			if ( material.Clearcoat > 0f ) Scalar( "coat_weight", "float", material.Clearcoat );
			if ( material.ClearcoatRoughness != 0.1f ) Scalar( "coat_roughness", "float", material.ClearcoatRoughness );
			if ( material.Ior != 1.5f ) Scalar( "specular_ior", "float", material.Ior );
		}
		shader.Outputs["out"] = "surfaceshader";
		var surface = new MxNode { Name = material.Name, Type = "surfacematerial" };
		surface.Inputs["surfaceshader"] = new MxInput { Name = "surfaceshader", Type = "surfaceshader", NodeName = shader.Name, Output = "out" };
		surface.Outputs["out"] = "material";
		doc.Nodes.Add( surface );
		doc.Materials.Add( new MxMaterial { Name = material.Name, ShaderNode = shader.Name } );
		return doc;
	}
}
