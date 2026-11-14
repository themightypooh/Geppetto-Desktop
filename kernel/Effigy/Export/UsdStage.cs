using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Effigy;

namespace Effigy.Export;

/// <summary>
/// USD stage as the native scene model (godlike-usd-stage-alembic-materialx,
/// GODLIKE WAVE 1 P0, roadmap §10 Platform).
///
/// Scope of this slice — read it before assuming more:
///  * Layers, references, payloads (deferred until requested), variants and
///    overrides with LIVRPS strength order (Local &gt; Variant &gt; Reference
///    &gt; Payload; first-listed sublayer is strongest). `inherits` and
///    `specializes` are parsed but NOT composed; that is a named limitation,
///    never a silent drop (see <see cref="KnownLimitations"/>).
///  * Read-only .usda text import first (<see cref="UsdAscii"/>); binary
///    .usdc crates are NOT supported (named limitation).
///  * USD export with a prim map from <see cref="PolyMesh"/> and a published
///    loss list on every export result — nothing is dropped silently.
///  * <see cref="UsdPreviewSurface"/> + MaterialX/OpenPBR (see
///    <see cref="MaterialXDocument"/>). No Hydra viewport delegate: the
///    stage is a data model only.
///  * No native OpenUSD library is linked. Parsing is a .usda text subset;
///    native bindings are the packaging task's job
///    (package-universal-desktop-cli-native-dependencies-and-headless-renderer).
///
/// Units and axes: the kernel is inches and Z-up. USD is metres and Y-up,
/// so export converts with the same rotation as the GLB exporter
/// (G(v) = (x, z, -y), a proper rotation, winding preserved) and
/// 0.0254 m per inch; import inverts it exactly, so a Geppetto round-trip
/// is exact. The file declares `metersPerUnit = 1` and `upAxis = "Y"`.
/// </summary>
public static class UsdInterchange
{
	/// <summary>Metres per kernel inch (same constant as GlbExport).</summary>
	public const float InchesToMetres = 0.0254f;

	/// <summary>Kernel Z-up inches to USD Y-up metres.</summary>
	public static Vec3 ToUsd( Vec3 v ) => new Vec3( v.x, v.z, -v.y ) * InchesToMetres;

	/// <summary>USD Y-up metres back to kernel Z-up inches (exact inverse of <see cref="ToUsd"/>).</summary>
	public static Vec3 FromUsd( Vec3 v ) => new Vec3( v.x / InchesToMetres, -v.z / InchesToMetres, v.y / InchesToMetres );

	/// <summary>Kernel direction to USD (rotation only, no unit scale).</summary>
	public static Vec3 ToUsdDirection( Vec3 v ) => new Vec3( v.x, v.z, -v.y );

	/// <summary>USD direction back to kernel (exact inverse of <see cref="ToUsdDirection"/>).</summary>
	public static Vec3 FromUsdDirection( Vec3 v ) => new Vec3( v.x, -v.z, v.y );

	/// <summary>Kernel transform to USD (basis vectors rotated, origin scaled).</summary>
	public static Xform ToUsd( Xform x ) => new Xform( ToUsdDirection( x.X ), ToUsdDirection( x.Y ), ToUsdDirection( x.Z ), ToUsd( x.Origin ) );

	/// <summary>USD transform back to kernel.</summary>
	public static Xform FromUsd( Xform x ) => new Xform( FromUsdDirection( x.X ), FromUsdDirection( x.Y ), FromUsdDirection( x.Z ), FromUsd( x.Origin ) );

	/// <summary>
	/// Published loss list for the whole interchange slice. Every entry is a
	/// named, deliberate gap — nothing on this list is silently dropped.
	/// Tests assert each entry is present (UsdAlembicMaterialXTests).
	/// </summary>
	public static IReadOnlyList<string> KnownLimitations => new List<string>
	{
		"Binary .usdc crates are not read or written; .usda text only.",
		"UsdSkel skeletal animation and blend shapes are not exported or imported in this slice.",
		"Hydra viewport delegate: no Hydra render delegate is implemented; the stage is a data model only.",
		"inherits and specializes opinions are parsed but not composed.",
		"External file references and payloads resolve only for layers registered in the stage; unregistered files are a named loss.",
		"Native OpenUSD library is not linked; parsing is a .usda text subset (see UsdAscii).",
		"Native Ogawa/HDF5 .abc containers are not read or written; AlembicCache is the Effigy subset container.",
		"Per-frame topology change is refused by AlembicCache (vertex count must be constant).",
		"KTX2 textures are not produced; glTF/KTX2 remains the GLB exporter's scope (see GlbInterchangeTests).",
		"OpenPBR sheen/fuzz and thin-film have no UsdPreviewSurface equivalent and are a named loss on conversion.",
	};
}

/// <summary>Prim kinds the stage model understands. Anything else parses as <see cref="Unknown"/> and keeps its attributes.</summary>
public enum UsdPrimKind { Unknown, Scope, Xform, Mesh, Material, Shader, Camera, Light }

/// <summary>One typed USD value. <see cref="TypeName"/> is the USD type spelling (float, float3, color3f, int[], ...); <see cref="Value"/> is the parsed payload (float, int, string, bool, Vec3, Vec2, float[], int[], Vec3[], Vec2[], string[]).</summary>
public sealed class UsdValue
{
	public string TypeName { get; }
	public object Value { get; }

	public UsdValue( string typeName, object value )
	{
		TypeName = typeName;
		Value = value;
	}

	public static UsdValue Float( float v ) => new UsdValue( "float", v );
	public static UsdValue Double( double v ) => new UsdValue( "double", v );
	public static UsdValue Int( int v ) => new UsdValue( "int", v );
	public static UsdValue Bool( bool v ) => new UsdValue( "bool", v );
	public static UsdValue String( string v ) => new UsdValue( "string", v );
	public static UsdValue Token( string v ) => new UsdValue( "token", v );
	public static UsdValue Asset( string v ) => new UsdValue( "asset", v );
	public static UsdValue Vec3( string typeName, Vec3 v ) => new UsdValue( typeName, v );
	public static UsdValue Vec2( string typeName, Vec2 v ) => new UsdValue( typeName, v );
	public static UsdValue FloatArray( float[] v ) => new UsdValue( "float[]", v );
	public static UsdValue IntArray( int[] v ) => new UsdValue( "int[]", v );
	public static UsdValue Vec3Array( string typeName, Vec3[] v ) => new UsdValue( typeName, v );
	public static UsdValue Vec2Array( string typeName, Vec2[] v ) => new UsdValue( typeName, v );
	public static UsdValue StringArray( string[] v ) => new UsdValue( "string[]", v );

	public float AsFloat() => Value is float f ? f : Value is double d ? (float)d : Value is int i ? i : throw new InvalidOperationException( "UsdValue is not numeric: " + TypeName );
	public int AsInt() => Value is int i ? i : throw new InvalidOperationException( "UsdValue is not an int: " + TypeName );
	public string AsString() => Value as string ?? throw new InvalidOperationException( "UsdValue is not a string: " + TypeName );
	public bool AsBool() => Value is bool b ? b : throw new InvalidOperationException( "UsdValue is not a bool: " + TypeName );
	public Vec3 AsVec3() => Value is Vec3 v ? v : throw new InvalidOperationException( "UsdValue is not a Vec3: " + TypeName );
	public Vec2 AsVec2() => Value is Vec2 v ? v : throw new InvalidOperationException( "UsdValue is not a Vec2: " + TypeName );
	public int[] AsIntArray() => Value as int[] ?? throw new InvalidOperationException( "UsdValue is not an int[]: " + TypeName );
	public Vec3[] AsVec3Array() => Value as Vec3[] ?? throw new InvalidOperationException( "UsdValue is not a Vec3[]: " + TypeName );
	public Vec2[] AsVec2Array() => Value as Vec2[] ?? throw new InvalidOperationException( "UsdValue is not a Vec2[]: " + TypeName );
	public float[] AsFloatArray() => Value as float[] ?? throw new InvalidOperationException( "UsdValue is not a float[]: " + TypeName );
	public string[] AsStringArray() => Value as string[] ?? throw new InvalidOperationException( "UsdValue is not a string[]: " + TypeName );

	/// <summary>Invariant, round-trip formatting (floats use "R").</summary>
	public string Format()
	{
		if ( Value == null ) return "None";
		switch ( Value )
		{
			case float f: return f.ToString( "R", CultureInfo.InvariantCulture );
			case double d: return d.ToString( "R", CultureInfo.InvariantCulture );
			case int i: return i.ToString( CultureInfo.InvariantCulture );
			case bool b: return b ? "true" : "false";
			case string s: return "\"" + s.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ) + "\"";
			case Vec3 v: return "(" + v.x.ToString( "R", CultureInfo.InvariantCulture ) + ", " + v.y.ToString( "R", CultureInfo.InvariantCulture ) + ", " + v.z.ToString( "R", CultureInfo.InvariantCulture ) + ")";
			case Vec2 v: return "(" + v.x.ToString( "R", CultureInfo.InvariantCulture ) + ", " + v.y.ToString( "R", CultureInfo.InvariantCulture ) + ")";
			case float[] a: return "[" + string.Join( ", ", Array.ConvertAll( a, x => x.ToString( "R", CultureInfo.InvariantCulture ) ) ) + "]";
			case int[] a: return "[" + string.Join( ", ", Array.ConvertAll( a, x => x.ToString( CultureInfo.InvariantCulture ) ) ) + "]";
			case Vec3[] a: return "[" + string.Join( ", ", Array.ConvertAll( a, x => new UsdValue( TypeName, x ).Format() ) ) + "]";
			case Vec2[] a: return "[" + string.Join( ", ", Array.ConvertAll( a, x => new UsdValue( TypeName, x ).Format() ) ) + "]";
			case string[] a: return "[" + string.Join( ", ", Array.ConvertAll( a, x => "\"" + x.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ) + "\"" ) ) + "]";
			default: return Value.ToString();
		}
	}
}

/// <summary>One named attribute on a prim (the <c>uniform</c>/<c>custom</c> prefix is carried in <see cref="Prefix"/>).</summary>
public sealed class UsdAttribute
{
	public string Name { get; set; }
	public string Prefix { get; set; } = "";
	public UsdValue Value { get; set; }

	public UsdAttribute( string name, UsdValue value, string prefix = "" )
	{
		Name = name;
		Value = value;
		Prefix = prefix;
	}
}

/// <summary>One prim in a layer. References and payloads are stored raw (e.g. "@./other.usda@&lt;/Lib/Bolt&gt;" or "&lt;/Lib/Bolt&gt;") and resolved by <see cref="UsdStage.Compose"/>.</summary>
public sealed class UsdPrim
{
	public string Path { get; set; }
	public UsdPrimKind Kind { get; set; } = UsdPrimKind.Unknown;
	/// <summary>USD type spelling as written in the file (e.g. "SkelRoot" for kinds this slice does not model); defaults to the kind name.</summary>
	public string TypeName { get; set; }
	public bool IsOver { get; set; }
	public List<UsdPrim> Children { get; } = new List<UsdPrim>();
	public List<UsdAttribute> Attributes { get; } = new List<UsdAttribute>();
	public List<KeyValuePair<string, string>> Relationships { get; } = new List<KeyValuePair<string, string>>();
	public List<string> References { get; } = new List<string>();
	public string Payload { get; set; }
	public bool PayloadDeferred { get; set; } = true;
	public Dictionary<string, List<string>> VariantSets { get; } = new Dictionary<string, List<string>>();
	public Dictionary<string, string> SelectedVariants { get; } = new Dictionary<string, string>();
	public Dictionary<string, List<UsdPrim>> VariantChildren { get; } = new Dictionary<string, List<UsdPrim>>();
	public List<string> Inherits { get; } = new List<string>();
	public List<string> Specializes { get; } = new List<string>();
	public bool Instanceable { get; set; }
	public bool Active { get; set; } = true;
	public List<string> MetadataRaw { get; } = new List<string>();

	public UsdPrim( string path, UsdPrimKind kind = UsdPrimKind.Unknown )
	{
		Path = path;
		Kind = kind;
		TypeName = kind.ToString();
	}

	public string Name
	{
		get
		{
			int slash = Path.LastIndexOf( '/' );
			return slash < 0 ? Path : Path.Substring( slash + 1 );
		}
	}

	public UsdValue GetAttribute( string name )
	{
		foreach ( var a in Attributes ) if ( a.Name == name ) return a.Value;
		return null;
	}

	public void SetAttribute( string name, UsdValue value, string prefix = "" )
	{
		for ( int i = 0; i < Attributes.Count; i++ )
			if ( Attributes[i].Name == name ) { Attributes[i] = new UsdAttribute( name, value, prefix ); return; }
		Attributes.Add( new UsdAttribute( name, value, prefix ) );
	}

	public UsdPrim FindChild( string name )
	{
		foreach ( var c in Children ) if ( c.Name == name ) return c;
		return null;
	}

	public UsdPrim FindOrAddChild( string name, UsdPrimKind kind )
	{
		var child = FindChild( name );
		if ( child != null ) return child;
		child = new UsdPrim( Path == "/" ? "/" + name : Path + "/" + name, kind );
		Children.Add( child );
		return child;
	}
}

/// <summary>One .usda layer: root prims plus layer metadata. Sublayers are asset paths; the first listed is strongest.</summary>
public sealed class UsdLayer
{
	public string Name { get; set; }
	public string FilePath { get; set; }
	public List<string> Sublayers { get; } = new List<string>();
	public string DefaultPrim { get; set; }
	public string UpAxis { get; set; } = "Y";
	public double MetersPerUnit { get; set; } = 1.0;
	public double TimeCodesPerSecond { get; set; } = 24.0;
	public double StartTimeCode { get; set; } = 0.0;
	public double EndTimeCode { get; set; } = 0.0;
	public List<UsdPrim> Roots { get; } = new List<UsdPrim>();
	public string CustomLayerDataRaw { get; set; }

	public UsdLayer( string name )
	{
		Name = name;
	}

	public UsdPrim Find( string path )
	{
		if ( string.IsNullOrEmpty( path ) || path == "/" ) return null;
		string[] parts = path.Split( new[] { '/' }, StringSplitOptions.RemoveEmptyEntries );
		List<UsdPrim> level = Roots;
		UsdPrim found = null;
		foreach ( string part in parts )
		{
			found = null;
			foreach ( var p in level ) if ( p.Name == part ) { found = p; break; }
			if ( found == null ) return null;
			level = found.Children;
		}
		return found;
	}
}

/// <summary>A composed prim: the LIVRPS merge of every opinion on a path, plus which layers contributed and any named notes/losses.</summary>
public sealed class UsdComposedPrim
{
	public UsdPrim Prim { get; set; }
	public List<string> ContributingLayers { get; } = new List<string>();
	public List<string> Notes { get; } = new List<string>();
}

/// <summary>Result of a USD export: the prim written plus every loss and warning, by name. An empty <see cref="Losses"/> means nothing was dropped.</summary>
public sealed class UsdExportResult
{
	public UsdPrim Prim { get; set; }
	public List<string> Losses { get; } = new List<string>();
	public List<string> Warnings { get; } = new List<string>();
}

/// <summary>
/// UsdPreviewSurface material: the kernel-side PBR material for USD/MaterialX.
/// Colour values are linear (like the GLB exporter's factors); texture paths
/// are asset references as written into the file. Maps to/from
/// <see cref="MaterialXDocument"/> and, in tests, to core
/// <c>MaterialDescriptor</c> (same factors the GLB exporter uses).
/// </summary>
public sealed class UsdPreviewSurface
{
	public string Name { get; set; } = "Material";
	public Vec3 DiffuseColor { get; set; } = Vec3.One;
	public string DiffuseMap { get; set; }
	public float Metallic { get; set; } = 0f;
	public string MetallicMap { get; set; }
	public float Roughness { get; set; } = 0.5f;
	public string RoughnessMap { get; set; }
	public float Opacity { get; set; } = 1f;
	public float OpacityThreshold { get; set; } = 0f;
	public Vec3 EmissiveColor { get; set; } = Vec3.Zero;
	public string EmissiveMap { get; set; }
	public float Clearcoat { get; set; } = 0f;
	public float ClearcoatRoughness { get; set; } = 0f;
	public float Ior { get; set; } = 1.5f;
	public string NormalMap { get; set; }

	public UsdPrim ToPrim( string path )
	{
		var shader = new UsdPrim( path, UsdPrimKind.Shader );
		shader.SetAttribute( "info:id", UsdValue.Token( "UsdPreviewSurface" ) );
		shader.SetAttribute( "inputs:diffuseColor", UsdValue.Vec3( "color3f", DiffuseColor ) );
		shader.SetAttribute( "inputs:metallic", UsdValue.Float( Metallic ) );
		shader.SetAttribute( "inputs:roughness", UsdValue.Float( Roughness ) );
		shader.SetAttribute( "inputs:opacity", UsdValue.Float( Opacity ) );
		shader.SetAttribute( "inputs:opacityThreshold", UsdValue.Float( OpacityThreshold ) );
		shader.SetAttribute( "inputs:emissiveColor", UsdValue.Vec3( "color3f", EmissiveColor ) );
		shader.SetAttribute( "inputs:clearcoat", UsdValue.Float( Clearcoat ) );
		shader.SetAttribute( "inputs:clearcoatRoughness", UsdValue.Float( ClearcoatRoughness ) );
		shader.SetAttribute( "inputs:ior", UsdValue.Float( Ior ) );
		if ( !string.IsNullOrEmpty( DiffuseMap ) ) shader.SetAttribute( "inputs:diffuseColor.connect", UsdValue.Asset( DiffuseMap ) );
		if ( !string.IsNullOrEmpty( NormalMap ) ) shader.SetAttribute( "inputs:normal.connect", UsdValue.Asset( NormalMap ) );
		return shader;
	}

	public static UsdPreviewSurface FromPrim( UsdPrim shaderPrim, out List<string> losses )
	{
		losses = new List<string>();
		var surface = new UsdPreviewSurface { Name = shaderPrim.Name };
		UsdValue v;
		if ( ( v = shaderPrim.GetAttribute( "inputs:diffuseColor" ) ) != null ) surface.DiffuseColor = v.AsVec3();
		if ( ( v = shaderPrim.GetAttribute( "inputs:metallic" ) ) != null ) surface.Metallic = v.AsFloat();
		if ( ( v = shaderPrim.GetAttribute( "inputs:roughness" ) ) != null ) surface.Roughness = v.AsFloat();
		if ( ( v = shaderPrim.GetAttribute( "inputs:opacity" ) ) != null ) surface.Opacity = v.AsFloat();
		if ( ( v = shaderPrim.GetAttribute( "inputs:opacityThreshold" ) ) != null ) surface.OpacityThreshold = v.AsFloat();
		if ( ( v = shaderPrim.GetAttribute( "inputs:emissiveColor" ) ) != null ) surface.EmissiveColor = v.AsVec3();
		if ( ( v = shaderPrim.GetAttribute( "inputs:clearcoat" ) ) != null ) surface.Clearcoat = v.AsFloat();
		if ( ( v = shaderPrim.GetAttribute( "inputs:clearcoatRoughness" ) ) != null ) surface.ClearcoatRoughness = v.AsFloat();
		if ( ( v = shaderPrim.GetAttribute( "inputs:ior" ) ) != null ) surface.Ior = v.AsFloat();
		foreach ( var a in shaderPrim.Attributes )
		{
			if ( a.Name.StartsWith( "inputs:", StringComparison.Ordinal ) && a.Name != "inputs:diffuseColor" && a.Name != "inputs:metallic" && a.Name != "inputs:roughness" && a.Name != "inputs:opacity" && a.Name != "inputs:opacityThreshold" && a.Name != "inputs:emissiveColor" && a.Name != "inputs:clearcoat" && a.Name != "inputs:clearcoatRoughness" && a.Name != "inputs:ior" && a.Name != "info:id" )
				losses.Add( "UsdPreviewSurface input '" + a.Name + "' on " + shaderPrim.Path + " has no kernel mapping and was not carried into the material" );
		}
		return surface;
	}
}

/// <summary>
/// A USD stage: a root layer plus registered sublayers, with LIVRPS composition
/// and a prim map from kernel meshes. Read-only import first: the stage never
/// writes back to a source asset; export writes a new file.
/// </summary>
public sealed class UsdStage
{
	public UsdLayer RootLayer { get; private set; }
	public List<UsdLayer> Layers { get; } = new List<UsdLayer>();

	public UsdStage( string name = "root" )
	{
		RootLayer = new UsdLayer( name );
		Layers.Add( RootLayer );
	}

	public void RegisterLayer( UsdLayer layer )
	{
		if ( layer == null ) throw new ArgumentNullException( nameof( layer ) );
		if ( !Layers.Contains( layer ) ) Layers.Add( layer );
	}

	public UsdPrim Find( string path )
	{
		foreach ( var layer in Layers )
		{
			var prim = layer.Find( path );
			if ( prim != null ) return prim;
		}
		return null;
	}

	/// <summary>Create the prim at <paramref name="path"/> (and any missing Xform parents) in the root layer.</summary>
	public UsdPrim CreatePrim( string path, UsdPrimKind kind )
	{
		if ( string.IsNullOrEmpty( path ) || path[0] != '/' ) throw new ArgumentException( "USD prim paths are absolute: " + path );
		string[] parts = path.Split( new[] { '/' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 0 ) throw new ArgumentException( "USD prim path has no name: " + path );
		List<UsdPrim> level = RootLayer.Roots;
		UsdPrim prim = null;
		string built = "";
		for ( int i = 0; i < parts.Length; i++ )
		{
			built += "/" + parts[i];
			prim = null;
			foreach ( var p in level ) if ( p.Name == parts[i] ) { prim = p; break; }
			if ( prim == null )
			{
				prim = new UsdPrim( built, i == parts.Length - 1 ? kind : UsdPrimKind.Xform );
				level.Add( prim );
			}
			level = prim.Children;
		}
		return prim;
	}

	/// <summary>
	/// Compose the prim at <paramref name="path"/> under LIVRPS strength order:
	/// local opinions beat variant opinions, which beat references, which beat
	/// payloads; the first-listed sublayer is strongest. Payloads resolve only
	/// when <paramref name="loadPayloads"/> is true; otherwise the composed
	/// prim carries a "payload deferred" note and no payload opinions.
	/// </summary>
	public UsdComposedPrim Compose( string path, bool loadPayloads = false )
	{
		var losses = new List<string>();
		var visiting = new HashSet<string>( StringComparer.Ordinal );
		var merged = ComposeInternal( path, loadPayloads, visiting, losses );
		var result = new UsdComposedPrim { Prim = merged };
		result.Notes.AddRange( losses );
		// Contributing layers: every layer that holds an opinion on this path.
		foreach ( var layer in Layers )
			if ( layer.Find( path ) != null && !result.ContributingLayers.Contains( layer.Name ) )
				result.ContributingLayers.Add( layer.Name );
		return result;
	}

	UsdPrim ComposeInternal( string path, bool loadPayloads, HashSet<string> visiting, List<string> losses )
	{
		if ( !visiting.Add( path ) )
		{
			losses.Add( "reference cycle at " + path + ": the repeated opinion was not composed twice" );
			return new UsdPrim( path, UsdPrimKind.Unknown );
		}
		try
		{
			// Layers weakest-first: registered sublayers in reverse list order, root last (strongest).
			var ordered = new List<UsdLayer>();
			for ( int i = Layers.Count - 1; i >= 0; i-- ) ordered.Add( Layers[i] );
			UsdPrim merged = null;
			foreach ( var layer in ordered )
			{
				var local = layer.Find( path );
				if ( local == null ) continue;
				foreach ( var opinion in CollectOpinions( layer, local, loadPayloads, visiting, losses ) )
				{
					if ( merged == null ) { merged = new UsdPrim( path, opinion.Kind ); MergeInto( merged, opinion ); }
					else MergeInto( merged, opinion );
				}
			}
			return merged ?? new UsdPrim( path, UsdPrimKind.Unknown );
		}
		finally { visiting.Remove( path ); }
	}

	/// <summary>Opinions on one prim, weakest first: payload, references, variants, local.</summary>
	List<UsdPrim> CollectOpinions( UsdLayer layer, UsdPrim local, bool loadPayloads, HashSet<string> visiting, List<string> losses )
	{
		var opinions = new List<UsdPrim>();
		if ( local.Inherits.Count > 0 || local.Specializes.Count > 0 )
			losses.Add( "prim " + local.Path + ": inherits/specializes opinions are parsed but not composed (see KnownLimitations)" );
		if ( !string.IsNullOrEmpty( local.Payload ) )
		{
			if ( !loadPayloads )
				losses.Add( "prim " + local.Path + ": payload " + local.Payload + " deferred (Compose with loadPayloads: true to resolve it)" );
			else
			{
				var target = ResolveReference( local.Payload, layer );
				if ( target == null ) losses.Add( "prim " + local.Path + ": payload target " + local.Payload + " could not be resolved" );
				else opinions.Add( ComposeInternal( target.Path, loadPayloads, visiting, losses ) );
			}
		}
		foreach ( string reference in local.References )
		{
			var target = ResolveReference( reference, layer );
			if ( target == null ) losses.Add( "prim " + local.Path + ": reference " + reference + " could not be resolved" );
			else opinions.Add( ComposeInternal( target.Path, loadPayloads, visiting, losses ) );
		}
		foreach ( var pair in local.SelectedVariants )
		{
			string key = pair.Key + "\n" + pair.Value;
			if ( local.VariantChildren.TryGetValue( key, out var variants ) )
				foreach ( var variantPrim in variants ) opinions.Add( variantPrim );
		}
		opinions.Add( local );
		return opinions;
	}

	/// <summary>Resolve a reference target: "&lt;/Prim/Path&gt;" in the same layer, or "@file.usda@&lt;/Prim/Path&gt;" in a registered layer with that file path.</summary>
	UsdPrim ResolveReference( string target, UsdLayer fromLayer )
	{
		if ( string.IsNullOrEmpty( target ) ) return null;
		string file = null;
		string primPath = target;
		if ( target[0] == '@' )
		{
			int close = target.IndexOf( '@', 1 );
			if ( close < 0 ) return null;
			file = target.Substring( 1, close - 1 );
			primPath = target.Substring( close + 1 );
		}
		primPath = primPath.Trim();
		if ( primPath.StartsWith( "<", StringComparison.Ordinal ) && primPath.EndsWith( ">", StringComparison.Ordinal ) )
			primPath = primPath.Substring( 1, primPath.Length - 2 );
		if ( file == null ) return fromLayer.Find( primPath );
		foreach ( var layer in Layers )
			if ( layer.FilePath == file || layer.Name == file ) return layer.Find( primPath );
		return null;
	}

	static void MergeInto( UsdPrim target, UsdPrim opinion )
	{
		if ( opinion.Kind != UsdPrimKind.Unknown ) target.Kind = opinion.Kind;
		if ( opinion.IsOver ) target.IsOver = true;
		foreach ( var attr in opinion.Attributes ) target.SetAttribute( attr.Name, attr.Value, attr.Prefix );
		foreach ( var rel in opinion.Relationships )
		{
			bool replaced = false;
			for ( int i = 0; i < target.Relationships.Count; i++ )
				if ( target.Relationships[i].Key == rel.Key ) { target.Relationships[i] = rel; replaced = true; break; }
			if ( !replaced ) target.Relationships.Add( rel );
		}
		foreach ( var child in opinion.Children )
		{
			var existing = target.FindChild( child.Name );
			if ( existing == null ) target.Children.Add( ClonePrim( child ) );
			else MergeInto( existing, child );
		}
	}

	static UsdPrim ClonePrim( UsdPrim prim )
	{
		var copy = new UsdPrim( prim.Path, prim.Kind )
		{
			IsOver = prim.IsOver,
			Payload = prim.Payload,
			PayloadDeferred = prim.PayloadDeferred,
			Instanceable = prim.Instanceable,
			Active = prim.Active,
		};
		foreach ( var a in prim.Attributes ) copy.Attributes.Add( new UsdAttribute( a.Name, a.Value, a.Prefix ) );
		foreach ( var r in prim.Relationships ) copy.Relationships.Add( r );
		foreach ( var r in prim.References ) copy.References.Add( r );
		foreach ( var pair in prim.VariantSets ) copy.VariantSets[pair.Key] = new List<string>( pair.Value );
		foreach ( var pair in prim.SelectedVariants ) copy.SelectedVariants[pair.Key] = pair.Value;
		foreach ( var pair in prim.VariantChildren ) copy.VariantChildren[pair.Key] = new List<UsdPrim>( pair.Value );
		foreach ( var s in prim.Inherits ) copy.Inherits.Add( s );
		foreach ( var s in prim.Specializes ) copy.Specializes.Add( s );
		foreach ( var m in prim.MetadataRaw ) copy.MetadataRaw.Add( m );
		foreach ( var c in prim.Children ) copy.Children.Add( ClonePrim( c ) );
		return copy;
	}

	/// <summary>
	/// Map a kernel mesh to a Mesh prim (prim map). Positions become USD
	/// metres Y-up; face counts/indices and per-corner UVs are carried
	/// exactly; vertex colours become primvars:displayColor. Morphs,
	/// skinning, paint layers and a second UV set are NOT exported — each is
	/// a named loss on the result, never a silent drop.
	/// </summary>
	public UsdExportResult AddMesh( PolyMesh mesh, string path, string materialPath = null )
	{
		if ( mesh == null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( mesh.FaceCount == 0 ) throw new InvalidOperationException( "AddMesh refuses an empty mesh: a silent empty export is not an export" );
		var result = new UsdExportResult();
		var prim = CreatePrim( path, UsdPrimKind.Mesh );
		result.Prim = prim;
		var points = new Vec3[mesh.Positions.Count];
		for ( int i = 0; i < points.Length; i++ ) points[i] = UsdInterchange.ToUsd( mesh.Positions[i] );
		prim.SetAttribute( "points", UsdValue.Vec3Array( "point3f[]", points ) );
		var counts = new List<int>();
		var indices = new List<int>();
		var uvs = new List<Vec2>();
		foreach ( var face in mesh.Faces )
		{
			counts.Add( face.Indices.Length );
			foreach ( int index in face.Indices ) indices.Add( index );
			if ( face.UVs != null )
				foreach ( var uv in face.UVs ) uvs.Add( uv );
		}
		prim.SetAttribute( "faceVertexCounts", UsdValue.IntArray( counts.ToArray() ) );
		prim.SetAttribute( "faceVertexIndices", UsdValue.IntArray( indices.ToArray() ) );
		if ( uvs.Count > 0 )
		{
			prim.SetAttribute( "primvars:st", UsdValue.Vec2Array( "texCoord2f[]", uvs.ToArray() ), "uniform" );
			prim.SetAttribute( "primvars:st:interpolation", UsdValue.Token( "faceVarying" ), "uniform" );
		}
		var normals = mesh.ComputeVertexNormals();
		var usdNormals = new Vec3[normals.Length];
		for ( int i = 0; i < usdNormals.Length; i++ ) usdNormals[i] = UsdInterchange.ToUsdDirection( normals[i] );
		prim.SetAttribute( "normals", UsdValue.Vec3Array( "normal3f[]", usdNormals ) );
		if ( mesh.HasVertexColors )
		{
			var colors = new Vec3[mesh.VertexColors.Length];
			for ( int i = 0; i < colors.Length; i++ ) colors[i] = new Vec3( mesh.VertexColors[i].x, mesh.VertexColors[i].y, mesh.VertexColors[i].z );
			prim.SetAttribute( "primvars:displayColor", UsdValue.Vec3Array( "color3f[]", colors ) );
		}
		if ( !string.IsNullOrEmpty( materialPath ) )
			prim.Relationships.Add( new KeyValuePair<string, string>( "material:binding", "<" + materialPath + ">" ) );
		if ( mesh.HasMorphs ) result.Losses.Add( "mesh " + path + ": " + CountValidMorphs( mesh ) + " morph(s) not exported — UsdSkel/blend shapes are not in this slice" );
		if ( mesh.IsRigged ) result.Losses.Add( "mesh " + path + ": skinning not exported — UsdSkel is not in this slice" );
		if ( mesh.HasPaint ) result.Losses.Add( "mesh " + path + ": paint layers not exported — bake to textures first" );
		if ( mesh.HasUV2 ) result.Losses.Add( "mesh " + path + ": second UV set (UV2) not exported" );
		return result;
	}

	static int CountValidMorphs( PolyMesh mesh )
	{
		int count = 0;
		foreach ( var m in mesh.ValidMorphs ) count++;
		return count;
	}

	/// <summary>Author a Material prim with a UsdPreviewSurface shader child; returns the material prim.</summary>
	public UsdPrim AddMaterial( UsdPreviewSurface surface, string path )
	{
		if ( surface == null ) throw new ArgumentNullException( nameof( surface ) );
		var material = CreatePrim( path, UsdPrimKind.Material );
		var shader = surface.ToPrim( path + "/" + surface.Name );
		material.Children.Add( shader );
		material.Relationships.Add( new KeyValuePair<string, string>( "outputs:surface", "<" + shader.Path + ">" ) );
		return material;
	}

	/// <summary>Write the root layer to a .usda file. Returns the export result (losses are the stage-level ones; per-prim losses live on their own results).</summary>
	public UsdExportResult WriteUsda( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "USD output path is empty" );
		RootLayer.FilePath = filePath;
		var dir = Path.GetDirectoryName( filePath );
		if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
		File.WriteAllText( filePath, UsdAscii.Write( RootLayer ) );
		return new UsdExportResult();
	}

	/// <summary>Read a .usda file into a stage (read-only import; the source file is never modified).</summary>
	public static UsdStage ReadUsda( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "USD input path is empty" );
		var layer = UsdAscii.ReadFile( filePath );
		var stage = new UsdStage( layer.Name );
		stage.Layers.Clear();
		stage.RootLayer = layer;
		stage.Layers.Add( layer );
		return stage;
	}
}
