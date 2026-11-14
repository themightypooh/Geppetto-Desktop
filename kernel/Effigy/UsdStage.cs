using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// USD stage as the native scene model — read-only .usda (ASCII) import first
/// (godlike-usd-stage-alembic-materialx, Platform P0).
///
/// WHAT THIS IS: a bounded, pure-C# .usda reader/writer for the authored subset
/// Geppetto actually exchanges: layers (subLayers), references, payloads,
/// variant sets/selections, inherits/specializes, instancing, UsdPreviewSurface
/// materials, and Mesh/Xform/Scope/SkelRoot prims with points/UVs/normals.
/// It follows the conventions Mobile's PlyUsdExport.Usda already writes:
/// metres, Z up, metersPerUnit 1, positions in inches x 0.0254, primvars:st
/// faceVarying with V flipped (1 - uv.y), UsdPreviewSurface roughness 0.6 /
/// metallic 0 defaults, one Mesh prim per body under one Xform.
///
/// WHAT IT IS NOT: .usdc (binary crate) and .usd (package) need a native USD
/// library — they are refused honestly (Parse throws FormatException naming
/// the format, never a silent empty stage), and the native-dependency work
/// belongs to package-universal-desktop-cli-native-dependencies-and-headless-renderer.
/// TimeSamples are not flattened: the default value is used and the loss is
/// named. Payloads/references to layers that are not supplied resolve to a
/// named loss ("deferred"), never a silently dropped prim.
///
/// LOSSES: every dropped or unparsed thing lands in Stage.Losses by name —
/// no silent drops. Flatten() applies variant selections and in-stage inherits
/// under the LIVRPS strength order (strongest first: Local, Inherits, Variant,
/// References, Payload, Specializes) for attributes.
/// </summary>
public sealed class UsdStage
{
	/// <summary>Inches to metres: Desktop meshes are inches, USD is metres.</summary>
	public const float MetresPerUnit = 0.0254f;

	public string DefaultPrim = "Root";
	public string Doc = "Geppetto Desktop export, metres, Z up";
	public float MetersPerUnitValue = 1f;
	public string UpAxis = "Z";
	public List<string> SubLayers = new();
	public List<UsdPrim> Roots = new();
	/// <summary>Named loss list: everything dropped, deferred or unparsed, in order.</summary>
	public List<string> Losses = new();

	public static string F( float v ) => v.ToString( "0.######", CultureInfo.InvariantCulture );

	/// <summary>Parse .usda ASCII text into a stage. Binary .usdc/.usd are refused, not guessed.</summary>
	public static UsdStage Parse( string text )
	{
		if ( text is null ) throw new ArgumentNullException( nameof( text ) );
		if ( text.StartsWith( "PXR-USDC", StringComparison.Ordinal ) )
			throw new FormatException( "Binary .usdc crate needs the native USD library (see package-universal-desktop-cli-native-dependencies-and-headless-renderer); .usda ASCII only." );
		return new UsdParser( text ).ParseStage();
	}

	/// <summary>Find a prim by absolute path ("/Root/Looks/Body_mat") or null.</summary>
	public UsdPrim Find( string path )
	{
		if ( string.IsNullOrEmpty( path ) || path[0] != '/' ) return null;
		var parts = path.Split( new[] { '/' }, StringSplitOptions.RemoveEmptyEntries );
		List<UsdPrim> level = Roots;
		UsdPrim found = null;
		foreach ( var part in parts )
		{
			found = null;
			foreach ( var p in level )
				if ( p.Name == part ) { found = p; break; }
			if ( found is null ) return null;
			level = found.Children;
		}
		return found;
	}

	/// <summary>
	/// Apply variant selections (drop unselected variant prims) and in-stage inherits
	/// under LIVRPS strength, and record references/payloads to layers that were not
	/// supplied as deferred losses. Returns a new stage; the source is untouched.
	/// </summary>
	public UsdStage Flatten( IReadOnlyDictionary<string, UsdStage> availableLayers = null )
	{
		var flat = new UsdStage
		{
			DefaultPrim = DefaultPrim, Doc = Doc, MetersPerUnitValue = MetersPerUnitValue, UpAxis = UpAxis,
			SubLayers = new List<string>( SubLayers ), Losses = new List<string>( Losses ),
		};
		var classes = new Dictionary<string, UsdPrim>( StringComparer.Ordinal );
		CollectClasses( Roots, classes );
		foreach ( var root in Roots )
		{
			var copy = FlattenPrim( root, classes, availableLayers, flat.Losses );
			if ( copy is not null ) flat.Roots.Add( copy );
		}
		return flat;
	}

	static void CollectClasses( IEnumerable<UsdPrim> prims, Dictionary<string, UsdPrim> classes, string parentPath = "" )
	{
		foreach ( var p in prims )
		{
			var path = parentPath + "/" + p.Name;
			if ( p.Spec == "class" ) classes[path] = p;
			CollectClasses( p.Children, classes, path );
		}
	}

	static UsdPrim FlattenPrim( UsdPrim prim, Dictionary<string, UsdPrim> classes, IReadOnlyDictionary<string, UsdStage> layers, List<string> losses )
	{
		if ( !prim.Active ) return null;
		var copy = prim.ShallowCopy();
		// Weakest first so stronger opinions overwrite: Specializes, Payload, References, Variant, Inherits, Local.
		foreach ( var path in prim.Specializes )
			ApplyClassOpinion( copy, classes, path, losses );
		foreach ( var asset in prim.Payloads )
		{
			if ( layers is not null && layers.TryGetValue( asset, out var stage ) && stage.Find( prim.Path ) is { } target )
				ApplyOpinions( copy, target );
			else losses.Add( $"payload deferred: {prim.Path} payload @{asset}@ not supplied" );
		}
		foreach ( var asset in prim.References )
		{
			if ( layers is not null && layers.TryGetValue( asset, out var stage ) && stage.Find( prim.Path ) is { } target )
				ApplyOpinions( copy, target );
			else losses.Add( $"reference deferred: {prim.Path} references @{asset}@ not supplied" );
		}
		foreach ( var path in prim.Inherits )
			ApplyClassOpinion( copy, classes, path, losses );
		// Local opinions already sit on the copy (strongest); children flatten recursively.
		var children = new List<UsdPrim>();
		foreach ( var child in prim.Children )
		{
			var flat = FlattenPrim( child, classes, layers, losses );
			if ( flat is not null ) children.Add( flat );
		}
		// Selected variant prims become ordinary children of the flattened prim.
		// They must be appended AFTER the regular children are collected: the
		// copy.Children assignment below used to wipe out prims added above it.
		// Variant prims are flattened too, so nested variants/inherits inside
		// a selected variant resolve the same way as anywhere else.
		foreach ( var pair in prim.VariantSelection )
		{
			var setName = pair.Key;
			if ( setName == "" && prim.VariantSets.Count == 1 ) setName = prim.VariantSets.Keys.First();
			if ( prim.VariantPrims.TryGetValue( setName, out var variants ) && variants.TryGetValue( pair.Value, out var variantPrims ) )
				foreach ( var vp in variantPrims )
				{
					var flat = FlattenPrim( vp, classes, layers, losses );
					if ( flat is not null ) children.Add( flat );
				}
			else losses.Add( $"variant not found: {prim.Path} variantSet {setName} has no variant {pair.Value}" );
		}
		copy.Children = children;
		copy.VariantSets = new Dictionary<string, List<string>>();
		copy.VariantSelection = new Dictionary<string, string>();
		copy.VariantPrims = new Dictionary<string, Dictionary<string, List<UsdPrim>>>();
		return copy;
	}

	static void ApplyClassOpinion( UsdPrim copy, Dictionary<string, UsdPrim> classes, string path, List<string> losses )
	{
		var name = path.TrimStart( '/' );
		if ( classes.TryGetValue( "/" + name, out var klass ) ) ApplyOpinions( copy, klass );
		else losses.Add( $"inherits/specializes not found: {copy.Path} -> {path}" );
	}

	/// <summary>Copy opinions from a weaker prim onto the copy, without overwriting local values.</summary>
	static void ApplyOpinions( UsdPrim copy, UsdPrim weaker )
	{
		foreach ( var attr in weaker.Attributes )
			if ( copy.Attribute( attr.Name ) is null ) copy.Attributes.Add( attr.Clone() );
		foreach ( var child in weaker.Children )
		{
			var existing = copy.Children.Find( c => c.Name == child.Name );
			if ( existing is null ) copy.Children.Add( child.ShallowCopyDeep() );
		}
	}

	/// <summary>Write the stage back to .usda text, losses included as a comment block. Round-trips Parse.</summary>
	public string Write()
	{
		var sb = new StringBuilder();
		sb.Append( "#usda 1.0\n(\n" );
		sb.Append( $"    defaultPrim = \"{DefaultPrim}\"\n" );
		sb.Append( $"    doc = \"{Doc.Replace( "\"", "\\\"" )}\"\n" );
		sb.Append( $"    metersPerUnit = {F( MetersPerUnitValue )}\n    upAxis = \"{UpAxis}\"\n" );
		if ( SubLayers.Count > 0 )
			sb.Append( "    subLayers = [" ).Append( string.Join( ", ", SubLayers.Select( s => "@" + s + "@" ) ) ).Append( "]\n" );
		sb.Append( ")\n" );
		if ( Losses.Count > 0 )
		{
			sb.Append( "\n# Geppetto loss list: everything below was dropped, deferred or unparsed — no silent drops.\n" );
			foreach ( var loss in Losses ) sb.Append( "# loss: " ).Append( loss.Replace( "\n", " " ) ).Append( "\n" );
		}
		sb.Append( "\n" );
		foreach ( var root in Roots ) WritePrim( sb, root, 0 );
		return sb.ToString();
	}

	static void WritePrim( StringBuilder sb, UsdPrim prim, int depth )
	{
		var indent = new string( ' ', depth * 4 );
		sb.Append( indent ).Append( prim.Spec ).Append( ' ' ).Append( prim.TypeName ).Append( " \"" ).Append( prim.Name ).Append( "\"" );
		var meta = new List<string>();
		if ( prim.Kind is not null ) meta.Add( $"kind = \"{prim.Kind}\"" );
		if ( prim.ApiSchemas.Count > 0 ) meta.Add( "prepend apiSchemas = [" + string.Join( ", ", prim.ApiSchemas.Select( s => "\"" + s + "\"" ) ) + "]" );
		if ( !prim.Active ) meta.Add( "active = false" );
		if ( prim.Instanceable ) meta.Add( "instanceable = true" );
		if ( prim.References.Count > 0 ) meta.Add( "references = [" + string.Join( ", ", prim.References.Select( s => "@" + s + "@" ) ) + "]" );
		if ( prim.Payloads.Count > 0 ) meta.Add( "payload = [" + string.Join( ", ", prim.Payloads.Select( s => "@" + s + "@" ) ) + "]" );
		if ( prim.Inherits.Count > 0 ) meta.Add( "inherits = [" + string.Join( ", ", prim.Inherits.Select( s => "<" + s + ">" ) ) + "]" );
		if ( prim.Specializes.Count > 0 ) meta.Add( "specializes = [" + string.Join( ", ", prim.Specializes.Select( s => "<" + s + ">" ) ) + "]" );
		if ( prim.VariantSelection.Count > 0 ) meta.Add( "variants = { " + string.Join( ", ", prim.VariantSelection.Select( pair => $"string {pair.Key} = \"{pair.Value}\"" ) ) + " }" );
		if ( meta.Count > 0 ) sb.Append( " (\n" ).Append( indent ).Append( "    " ).Append( string.Join( $"\n{indent}    ", meta ) ).Append( $"\n{indent}" ).Append( ")" );
		sb.Append( "\n" ).Append( indent ).Append( "{\n" );
		foreach ( var attr in prim.Attributes ) sb.Append( indent ).Append( "    " ).Append( attr.Write() ).Append( "\n" );
		if ( !string.IsNullOrEmpty( prim.MaterialBinding ) )
			sb.Append( indent ).Append( "    rel material:binding = <" ).Append( prim.MaterialBinding ).Append( ">\n" );
		foreach ( var set in prim.VariantSets )
		{
			sb.Append( indent ).Append( "    variantSet \"" ).Append( set.Key ).Append( "\"\n" ).Append( indent ).Append( "    {\n" );
			foreach ( var variant in set.Value )
			{
				sb.Append( indent ).Append( "        \"" ).Append( variant ).Append( "\"\n" ).Append( indent ).Append( "        {\n" );
				if ( prim.VariantPrims.TryGetValue( set.Key, out var variants ) && variants.TryGetValue( variant, out var prims ) )
					foreach ( var vp in prims ) WritePrim( sb, vp, depth + 3 );
				sb.Append( indent ).Append( "        }\n" );
			}
			sb.Append( indent ).Append( "    }\n" );
		}
		foreach ( var child in prim.Children ) WritePrim( sb, child, depth + 1 );
		sb.Append( indent ).Append( "}\n" );
	}

	/// <summary>Build a Mesh prim from a Desktop mesh (inches), in Mobile's Usda conventions.</summary>
	public static UsdPrim MeshPrim( string name, PolyMesh mesh )
	{
		var prim = new UsdPrim { Name = UsdStageIdent( name ), TypeName = "Mesh", Spec = "def" };
		prim.ApiSchemas.Add( "MaterialBindingAPI" );
		prim.Attributes.Add( new UsdAttribute { Name = "doubleSided", TypeName = "bool", IsUniform = true, Value = false } );
		var polys = new List<int[]>();
		var losses = new List<string>();
		foreach ( var f in mesh.Faces )
		{
			if ( f.Count < 3 ) continue;
			if ( f.Count > 255 )
			{
				// Mobile's Usda fans >255-corner faces too; record it, don't silently re-topologise.
				for ( var i = 1; i + 1 < f.Count; i++ ) polys.Add( new[] { f.Indices[0], f.Indices[i], f.Indices[i + 1] } );
				losses.Add( $"fanned n-gon: {name} face with {f.Count} corners written as {f.Count - 2} triangles" );
			}
			else polys.Add( (int[])f.Indices.Clone() );
		}
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		var points = new List<Vec3>();
		foreach ( var p in mesh.Positions )
		{
			var m = p * MetresPerUnit;
			points.Add( m );
			min = new Vec3( MathF.Min( min.x, m.x ), MathF.Min( min.y, m.y ), MathF.Min( min.z, m.z ) );
			max = new Vec3( MathF.Max( max.x, m.x ), MathF.Max( max.y, m.y ), MathF.Max( max.z, m.z ) );
		}
		prim.Attributes.Add( new UsdAttribute { Name = "extent", TypeName = "float3[]", Value = new List<Vec3> { min, max } } );
		prim.Attributes.Add( new UsdAttribute { Name = "faceVertexCounts", TypeName = "int[]", Value = polys.Select( p => p.Length ).ToList() } );
		prim.Attributes.Add( new UsdAttribute { Name = "faceVertexIndices", TypeName = "int[]", Value = polys.SelectMany( p => p ).ToList() } );
		prim.Attributes.Add( new UsdAttribute { Name = "points", TypeName = "point3f[]", Value = points } );
		var (corner, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
		var faceNormals = new List<Vec3>();
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			if ( f.Count < 3 ) continue;
			var corners = f.Count <= 255 ? f.Count : (f.Count - 2) * 3;
			for ( var k = 0; k < corners; k++ )
			{
				var ci = f.Count <= 255 ? k : (k % 3 == 0 ? 0 : k / 3 + k % 3);
				faceNormals.Add( normals[corner[fi][ci]] );
			}
		}
		prim.Attributes.Add( new UsdAttribute { Name = "normals", TypeName = "normal3f[]", Interpolation = "faceVarying", Value = faceNormals } );
		if ( mesh.HasVertexColors )
		{
			var colours = new List<Vec3>();
			foreach ( var c in mesh.VertexColors ) colours.Add( new Vec3( c.x, c.y, c.z ) );
			prim.Attributes.Add( new UsdAttribute { Name = "primvars:displayColor", TypeName = "color3f[]", Interpolation = "vertex", Value = colours } );
		}
		var hasUv = mesh.Faces.Any( f => f.UVs is not null && f.UVs.Length == f.Count && f.UVs.Any( q => q.x != 0f || q.y != 0f ) );
		if ( hasUv )
		{
			var st = new List<Vec2>();
			foreach ( var f in mesh.Faces )
			{
				if ( f.Count < 3 ) continue;
				var corners = f.Count <= 255
					? Enumerable.Range( 0, f.Count ).Select( i => f.UVs is not null && f.UVs.Length == f.Count ? f.UVs[i] : default( Vec2 ) )
					: Enumerable.Repeat( default( Vec2 ), (f.Count - 2) * 3 );
				foreach ( var uv in corners ) st.Add( new Vec2( uv.x, 1f - uv.y ) ); // USD st is V up
			}
			prim.Attributes.Add( new UsdAttribute { Name = "primvars:st", TypeName = "texCoord2f[]", Interpolation = "faceVarying", Value = st } );
		}
		prim.Attributes.Add( new UsdAttribute { Name = "subdivisionScheme", TypeName = "token", IsUniform = true, Value = "none" } );
		foreach ( var loss in losses ) prim.Losses.Add( loss );
		return prim;
	}

	/// <summary>Read a Mesh prim back into a Desktop mesh (inches). Returns null + a loss when it is not a mesh.</summary>
	public static PolyMesh PrimToMesh( UsdPrim prim, List<string> losses )
	{
		if ( prim is null || prim.TypeName != "Mesh" )
		{
			losses?.Add( $"not a Mesh prim: {prim?.Path ?? "(null)"}" );
			return null;
		}
		var points = prim.Attribute( "points" )?.Value as List<Vec3>;
		var counts = prim.Attribute( "faceVertexCounts" )?.Value as List<int>;
		var indices = prim.Attribute( "faceVertexIndices" )?.Value as List<int>;
		if ( points is null || counts is null || indices is null )
		{
			losses?.Add( $"Mesh prim {prim.Path} is missing points/faceVertexCounts/faceVertexIndices" );
			return null;
		}
		var mesh = new PolyMesh();
		foreach ( var p in points ) mesh.AddVertex( p * (1f / MetresPerUnit) );
		var st = prim.Attribute( "primvars:st" )?.Value as List<Vec2>;
		var colours = prim.Attribute( "primvars:displayColor" )?.Value as List<Vec3>;
		if ( colours is not null && colours.Count == mesh.VertexCount )
		{
			mesh.VertexColors = colours.Select( c => new Vec4( c.x, c.y, c.z, 1f ) ).ToArray();
		}
		else if ( colours is not null )
		{
			losses?.Add( $"displayColor dropped: {prim.Path} has {colours.Count} colours for {mesh.VertexCount} vertices" );
		}
		var cursor = 0;
		var stCursor = 0;
		foreach ( var count in counts )
		{
			if ( count < 3 || cursor + count > indices.Count )
			{
				losses?.Add( $"face dropped: {prim.Path} faceVertexCounts entry {count} does not fit faceVertexIndices" );
				break;
			}
			var idx = indices.GetRange( cursor, count ).ToArray();
			cursor += count;
			Vec2[] uvs = null;
			if ( st is not null && stCursor + count <= st.Count )
			{
				uvs = new Vec2[count];
				for ( var c = 0; c < count; c++ ) uvs[c] = new Vec2( st[stCursor + c].x, 1f - st[stCursor + c].y );
				stCursor += count;
			}
			mesh.Faces.Add( new Face( idx, uvs ) );
		}
		return mesh;
	}

	/// <summary>A valid USD prim name: [A-Za-z_][A-Za-z0-9_]* — same rule Mobile's Ident uses.</summary>
	public static string UsdStageIdent( string s )
	{
		var t = new string( (s ?? "").Select( ch => ch < 128 && char.IsLetterOrDigit( ch ) ? ch : '_' ).ToArray() ).Trim( '_' );
		if ( t.Length == 0 ) t = "Body";
		if ( char.IsDigit( t[0] ) ) t = "_" + t;
		return t.Length > 60 ? t.Substring( 0, 60 ) : t;
	}

	/// <summary>
	/// Build a whole stage from bodies, mirroring Mobile's Usda layout: one Xform root
	/// (kind = component), one Mesh prim per body, one Looks Scope with one Material
	/// per body, material:binding wired to /{root}/Looks/{name}_mat. Prim losses are
	/// aggregated onto the stage loss list.
	/// </summary>
	public static UsdStage FromMeshes( string rootName, IReadOnlyList<(string Name, PolyMesh Mesh, UsdPreviewSurface Material)> bodies )
	{
		var root = UsdStageIdent( rootName );
		var stage = new UsdStage { DefaultPrim = root };
		var rootPrim = new UsdPrim { Name = root, TypeName = "Xform", Spec = "def", Kind = "component" };
		stage.Roots.Add( rootPrim );
		var looks = new UsdPrim { Name = "Looks", TypeName = "Scope", Spec = "def", Parent = rootPrim };
		rootPrim.Children.Add( looks );
		var used = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var body in bodies )
		{
			var name = UsdStageIdent( body.Name );
			var unique = name;
			for ( var i = 2; !used.Add( unique ); i++ ) unique = $"{name}_{i}";
			var meshPrim = MeshPrim( unique, body.Mesh );
			meshPrim.Parent = rootPrim;
			if ( body.Material is not null ) meshPrim.MaterialBinding = $"/{root}/Looks/{unique}_mat";
			stage.Losses.AddRange( meshPrim.Losses );
			meshPrim.Losses.Clear();
			rootPrim.Children.Add( meshPrim );
			if ( body.Material is not null )
			{
				var materialPrim = body.Material.ToMaterialPrim( $"/{root}/Looks", unique + "_mat" );
				materialPrim.Parent = looks;
				looks.Children.Add( materialPrim );
			}
		}
		return stage;
	}
}

/// <summary>One attribute on a prim. Value is a scalar or a List&lt;T&gt; (see Parse).</summary>
public sealed class UsdAttribute
{
	public string Name;
	public string TypeName;
	public object Value;
	public bool IsUniform;
	public bool IsCustom;
	public string Interpolation;
	/// <summary>Connection target path ("/Root/Looks/Mat/Colour.outputs:rgb"), or null for a literal value.</summary>
	public string Connection;

	public UsdAttribute Clone() => new()
	{
		Name = Name, TypeName = TypeName, Value = CloneValue( Value ),
		IsUniform = IsUniform, IsCustom = IsCustom, Interpolation = Interpolation, Connection = Connection,
	};

	static object CloneValue( object value )
	{
		switch ( value )
		{
			case List<int> ints: return new List<int>( ints );
			case List<float> floats: return new List<float>( floats );
			case List<string> strings: return new List<string>( strings );
			case List<Vec2> v2s: return new List<Vec2>( v2s );
			case List<Vec3> v3s: return new List<Vec3>( v3s );
			default: return value;
		}
	}

	public string Write()
	{
		var sb = new StringBuilder();
		if ( IsUniform ) sb.Append( "uniform " );
		if ( IsCustom ) sb.Append( "custom " );
		sb.Append( TypeName ).Append( ' ' ).Append( Name );
		if ( Connection is not null )
		{
			sb.Append( ".connect = <" ).Append( Connection ).Append( '>' );
			return sb.ToString();
		}
		if ( Value is null ) return sb.ToString(); // declaration only, e.g. float3 outputs:rgb
		sb.Append( " = " ).Append( FormatValue( Value ) );
		if ( Interpolation is not null ) sb.Append( " (\n            interpolation = \"" ).Append( Interpolation ).Append( "\"\n        )" );
		return sb.ToString();
	}

	public static string FormatValue( object value )
	{
		switch ( value )
		{
			case null: return "None";
			case bool b: return b ? "1" : "0";
			case int i: return i.ToString( CultureInfo.InvariantCulture );
			case float f: return UsdStage.F( f );
			case double d: return UsdStage.F( (float)d );
			case string s: return "\"" + s.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ) + "\"";
			case Vec2 v2: return $"({UsdStage.F( v2.x )}, {UsdStage.F( v2.y )})";
			case Vec3 v3: return $"({UsdStage.F( v3.x )}, {UsdStage.F( v3.y )}, {UsdStage.F( v3.z )})";
			case List<int> ints: return "[" + string.Join( ", ", ints.Select( i => i.ToString( CultureInfo.InvariantCulture ) ) ) + "]";
			case List<float> floats: return "[" + string.Join( ", ", floats.Select( UsdStage.F ) ) + "]";
			case List<string> strings: return "[" + string.Join( ", ", strings.Select( s => "\"" + s + "\"" ) ) + "]";
			case List<Vec2> v2s: return "[" + string.Join( ", ", v2s.Select( v => $"({UsdStage.F( v.x )}, {UsdStage.F( v.y )})" ) ) + "]";
			case List<Vec3> v3s: return "[" + string.Join( ", ", v3s.Select( v => $"({UsdStage.F( v.x )}, {UsdStage.F( v.y )}, {UsdStage.F( v.z )})" ) ) + "]";
			default: return "\"" + value.ToString() + "\"";
		}
	}
}

/// <summary>One prim in the stage: a named node with a type, children, attributes and composition arcs.</summary>
public sealed class UsdPrim
{
	public string Name;
	public string TypeName = "Xform";
	public string Spec = "def";
	public UsdPrim Parent;
	public List<UsdPrim> Children = new();
	public List<UsdAttribute> Attributes = new();
	public List<string> References = new();
	public List<string> Payloads = new();
	public List<string> Inherits = new();
	public List<string> Specializes = new();
	public Dictionary<string, List<string>> VariantSets = new();
	public Dictionary<string, string> VariantSelection = new();
	public Dictionary<string, Dictionary<string, List<UsdPrim>>> VariantPrims = new();
	public bool Instanceable;
	public List<string> ApiSchemas = new();
	public string MaterialBinding;
	public string Kind;
	public string Purpose;
	public bool Active = true;
	/// <summary>Diagnostics attached to this prim (fanned faces, dropped values); aggregated onto the stage by FromMeshes.</summary>
	public List<string> Losses = new();

	public string Path
	{
		get
		{
			var names = new List<string>();
			for ( var p = this; p is not null; p = p.Parent ) names.Add( p.Name );
			names.Reverse();
			return "/" + string.Join( "/", names );
		}
	}

	public UsdAttribute Attribute( string name )
	{
		foreach ( var a in Attributes )
			if ( a.Name == name ) return a;
		return null;
	}

	public UsdPrim ShallowCopy()
	{
		var copy = new UsdPrim
		{
			Name = Name, TypeName = TypeName, Spec = Spec, Kind = Kind, Purpose = Purpose,
			Active = Active, Instanceable = Instanceable, MaterialBinding = MaterialBinding,
			ApiSchemas = new List<string>( ApiSchemas ), References = new List<string>( References ),
			Payloads = new List<string>( Payloads ), Inherits = new List<string>( Inherits ),
			Specializes = new List<string>( Specializes ), Losses = new List<string>( Losses ),
		};
		foreach ( var a in Attributes ) copy.Attributes.Add( a.Clone() );
		return copy;
	}

	public UsdPrim ShallowCopyDeep()
	{
		var copy = ShallowCopy();
		foreach ( var child in Children )
		{
			var deep = child.ShallowCopyDeep();
			deep.Parent = copy;
			copy.Children.Add( deep );
		}
		return copy;
	}
}

/// <summary>
/// A UsdPreviewSurface material: the portable look every DCC reads. Linear colours,
/// Mobile's defaults (roughness 0.6, metallic 0). The PbrMaterial bridge lives in
/// MaterialX.cs; this type only knows how to read/write itself as USD Shader prims.
/// </summary>
public sealed class UsdPreviewSurface
{
	public Vec3 DiffuseColor = new( 0.18f, 0.18f, 0.18f );
	public string DiffuseMap;
	public float Metallic;
	public string MetallicMap;
	public float Roughness = 0.6f;
	public string RoughnessMap;
	public string NormalMap;
	public float Opacity = 1f;
	public string OpacityMap;
	public Vec3 EmissiveColor = new( 0f, 0f, 0f );
	public string EmissiveMap;
	public float Clearcoat;
	public float ClearcoatRoughness = 0.1f;
	public float Ior = 1.5f;

	/// <summary>Read the Surface shader out of a Material prim (sibling UsdUVTexture shaders resolve map paths).</summary>
	public static UsdPreviewSurface FromMaterial( UsdPrim materialPrim, List<string> losses )
	{
		var surface = new UsdPreviewSurface();
		if ( materialPrim is null ) return surface;
		UsdPrim shader = null;
		foreach ( var child in materialPrim.Children )
			if ( child.TypeName == "Shader" && child.Attribute( "info:id" )?.Value as string == "UsdPreviewSurface" ) shader = child;
		if ( shader is null )
		{
			losses?.Add( $"material {materialPrim.Path} has no UsdPreviewSurface shader" );
			return surface;
		}
		string MapPath( string input )
		{
			var attr = shader.Attribute( input );
			if ( attr?.Connection is null ) return null;
			// Connection looks like "/Root/Looks/Mat/Colour.outputs:rgb" — the shader is the segment before ".outputs".
			var target = attr.Connection;
			var dot = target.IndexOf( ".outputs", StringComparison.Ordinal );
			if ( dot < 0 ) return null;
			var shaderPath = target.Substring( 0, dot );
			var slash = shaderPath.LastIndexOf( '/' );
			var shaderName = slash >= 0 ? shaderPath.Substring( slash + 1 ) : shaderPath;
			UsdPrim texture = null;
			foreach ( var child in materialPrim.Children )
				if ( child.Name == shaderName ) texture = child;
			if ( texture is null ) return null;
			var id = texture.Attribute( "info:id" )?.Value as string;
			if ( id == "UsdUVTexture" ) return texture.Attribute( "inputs:file" )?.Value as string;
			if ( id == "UsdNormalMap" )
			{
				// One level of indirection: normal map shaders wrap a UsdUVTexture.
				var source = texture.Attribute( "inputs:source" )?.Connection;
				if ( source is null ) return null;
				var dot2 = source.IndexOf( ".outputs", StringComparison.Ordinal );
				if ( dot2 < 0 ) return null;
				var innerPath = source.Substring( 0, dot2 );
				var slash2 = innerPath.LastIndexOf( '/' );
				var innerName = slash2 >= 0 ? innerPath.Substring( slash2 + 1 ) : innerPath;
				foreach ( var child in materialPrim.Children )
					if ( child.Name == innerName ) return child.Attribute( "inputs:file" )?.Value as string;
			}
			losses?.Add( $"material {materialPrim.Path} input {input} connects to unsupported shader {id ?? shaderName}" );
			return null;
		}
		float F( string input, float fallback )
		{
			var v = shader.Attribute( input )?.Value;
			return v is float f ? f : v is int i ? i : fallback;
		}
		Vec3 V( string input, Vec3 fallback )
		{
			return shader.Attribute( input )?.Value is Vec3 v ? v : fallback;
		}
		surface.DiffuseColor = V( "inputs:diffuseColor", surface.DiffuseColor );
		surface.DiffuseMap = MapPath( "inputs:diffuseColor" );
		surface.Metallic = F( "inputs:metallic", surface.Metallic );
		surface.MetallicMap = MapPath( "inputs:metallic" );
		surface.Roughness = F( "inputs:roughness", surface.Roughness );
		surface.RoughnessMap = MapPath( "inputs:roughness" );
		surface.NormalMap = MapPath( "inputs:normal" );
		surface.Opacity = F( "inputs:opacity", surface.Opacity );
		surface.OpacityMap = MapPath( "inputs:opacity" );
		surface.EmissiveColor = V( "inputs:emissiveColor", surface.EmissiveColor );
		surface.EmissiveMap = MapPath( "inputs:emissiveColor" );
		surface.Clearcoat = F( "inputs:clearcoat", surface.Clearcoat );
		surface.ClearcoatRoughness = F( "inputs:clearcoatRoughness", surface.ClearcoatRoughness );
		surface.Ior = F( "inputs:ior", surface.Ior );
		return surface;
	}

	/// <summary>Build a Material prim (parentPath is the absolute Looks path, e.g. "/Root/Looks").</summary>
	public UsdPrim ToMaterialPrim( string parentPath, string materialName )
	{
		var name = UsdStage.UsdStageIdent( materialName );
		var self = parentPath.TrimEnd( '/' ) + "/" + name;
		var material = new UsdPrim { Name = name, TypeName = "Material", Spec = "def" };
		material.Attributes.Add( new UsdAttribute { Name = "outputs:surface", TypeName = "token", Connection = self + "/Surface.outputs:surface" } );
		var surface = new UsdPrim { Name = "Surface", TypeName = "Shader", Spec = "def", Parent = material };
		material.Children.Add( surface );
		surface.Attributes.Add( new UsdAttribute { Name = "info:id", TypeName = "token", IsUniform = true, Value = "UsdPreviewSurface" } );
		void Scalar( string input, string type, object value ) =>
			surface.Attributes.Add( new UsdAttribute { Name = input, TypeName = type, Value = value } );
		void Map( string input, string type, string map, string textureName, bool srgb )
		{
			if ( map is null ) return;
			var texture = new UsdPrim { Name = textureName, TypeName = "Shader", Spec = "def", Parent = material };
			material.Children.Add( texture );
			texture.Attributes.Add( new UsdAttribute { Name = "info:id", TypeName = "token", IsUniform = true, Value = "UsdUVTexture" } );
			texture.Attributes.Add( new UsdAttribute { Name = "inputs:file", TypeName = "asset", Value = map } );
			texture.Attributes.Add( new UsdAttribute { Name = "inputs:sourceColorSpace", TypeName = "token", Value = srgb ? "sRGB" : "raw" } );
			texture.Attributes.Add( new UsdAttribute { Name = "inputs:st", TypeName = "float2", Connection = self + "/St.outputs:result" } );
			texture.Attributes.Add( new UsdAttribute { Name = "inputs:wrapS", TypeName = "token", Value = "repeat" } );
			texture.Attributes.Add( new UsdAttribute { Name = "inputs:wrapT", TypeName = "token", Value = "repeat" } );
			texture.Attributes.Add( new UsdAttribute { Name = "outputs:rgb", TypeName = "float3" } );
			texture.Attributes.Add( new UsdAttribute { Name = "outputs:r", TypeName = "float" } );
			surface.Attributes.Add( new UsdAttribute { Name = input, TypeName = type, Connection = self + "/" + textureName + (type == "float" ? ".outputs:r" : ".outputs:rgb") } );
		}
		var needSt = DiffuseMap is not null || NormalMap is not null || RoughnessMap is not null || MetallicMap is not null || OpacityMap is not null || EmissiveMap is not null;
		if ( needSt )
		{
			var st = new UsdPrim { Name = "St", TypeName = "Shader", Spec = "def", Parent = material };
			material.Children.Add( st );
			st.Attributes.Add( new UsdAttribute { Name = "info:id", TypeName = "token", IsUniform = true, Value = "UsdPrimvarReader_float2" } );
			st.Attributes.Add( new UsdAttribute { Name = "inputs:varname", TypeName = "string", Value = "st" } );
			st.Attributes.Add( new UsdAttribute { Name = "outputs:result", TypeName = "float2" } );
		}
		if ( DiffuseMap is null ) Scalar( "inputs:diffuseColor", "color3f", DiffuseColor );
		else Map( "inputs:diffuseColor", "color3f", DiffuseMap, "Colour", srgb: true );
		if ( NormalMap is not null ) Map( "inputs:normal", "normal3f", NormalMap, "Normal", srgb: false );
		if ( RoughnessMap is null ) Scalar( "inputs:roughness", "float", Roughness );
		else Map( "inputs:roughness", "float", RoughnessMap, "Roughness", srgb: false );
		if ( MetallicMap is null ) Scalar( "inputs:metallic", "float", Metallic );
		else Map( "inputs:metallic", "float", MetallicMap, "Metallic", srgb: false );
		if ( OpacityMap is null && Opacity < 1f ) Scalar( "inputs:opacity", "float", Opacity );
		else if ( OpacityMap is not null ) Map( "inputs:opacity", "float", OpacityMap, "Opacity", srgb: false );
		if ( EmissiveMap is null && (EmissiveColor.x != 0f || EmissiveColor.y != 0f || EmissiveColor.z != 0f) ) Scalar( "inputs:emissiveColor", "color3f", EmissiveColor );
		else if ( EmissiveMap is not null ) Map( "inputs:emissiveColor", "color3f", EmissiveMap, "Emissive", srgb: true );
		if ( Clearcoat > 0f ) Scalar( "inputs:clearcoat", "float", Clearcoat );
		if ( ClearcoatRoughness != 0.1f ) Scalar( "inputs:clearcoatRoughness", "float", ClearcoatRoughness );
		if ( Ior != 1.5f ) Scalar( "inputs:ior", "float", Ior );
		return material;
	}
}

/// <summary>Tokenizer + recursive-descent reader for the .usda subset UsdStage models.</summary>
sealed class UsdParser
{
	readonly List<UsdToken> _tokens;
	int _pos;
	readonly UsdStage _stage;

	public UsdParser( string text )
	{
		_tokens = Tokenize( text );
		_stage = new UsdStage();
	}

	sealed class UsdToken
	{
		public string Kind;
		public string Text;
	}

	static List<UsdToken> Tokenize( string text )
	{
		var tokens = new List<UsdToken>();
		var i = 0;
		while ( i < text.Length )
		{
			var ch = text[i];
			if ( char.IsWhiteSpace( ch ) ) { i++; continue; }
			if ( ch == '#' ) { while ( i < text.Length && text[i] != '\n' ) i++; continue; }
			if ( ch == '"' )
			{
				var sb = new StringBuilder();
				i++;
				while ( i < text.Length && text[i] != '"' )
				{
					if ( text[i] == '\\' && i + 1 < text.Length )
					{
						i++;
						sb.Append( text[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[i] } );
						i++;
					}
					else { sb.Append( text[i] ); i++; }
				}
				if ( i < text.Length ) i++; // closing quote
				tokens.Add( new UsdToken { Kind = "string", Text = sb.ToString() } );
				continue;
			}
			if ( ch == '@' )
			{
				var end = text.IndexOf( '@', i + 1 );
				if ( end < 0 ) throw new FormatException( "Unterminated asset path in .usda text." );
				tokens.Add( new UsdToken { Kind = "asset", Text = text.Substring( i + 1, end - i - 1 ) } );
				i = end + 1;
				continue;
			}
			if ( char.IsDigit( ch ) || ch == '.' && i + 1 < text.Length && char.IsDigit( text[i + 1] )
				|| (ch == '-' || ch == '+') && i + 1 < text.Length && (char.IsDigit( text[i + 1] ) || text[i + 1] == '.') )
			{
				var start = i;
				if ( text[i] == '-' || text[i] == '+' ) i++;
				while ( i < text.Length && (char.IsDigit( text[i] ) || text[i] == '.') ) i++;
				if ( i < text.Length && (text[i] == 'e' || text[i] == 'E') )
				{
					i++;
					if ( i < text.Length && (text[i] == '-' || text[i] == '+') ) i++;
					while ( i < text.Length && char.IsDigit( text[i] ) ) i++;
				}
				tokens.Add( new UsdToken { Kind = "number", Text = text.Substring( start, i - start ) } );
				continue;
			}
			if ( char.IsLetter( ch ) || ch == '_' )
			{
				var start = i;
				while ( i < text.Length && (char.IsLetterOrDigit( text[i] ) || text[i] == '_' || text[i] == ':' || text[i] == '.') ) i++;
				tokens.Add( new UsdToken { Kind = "ident", Text = text.Substring( start, i - start ) } );
				continue;
			}
			if ( "{}()[]=<>;,".IndexOf( ch ) >= 0 || ch == '/' )
			{
				tokens.Add( new UsdToken { Kind = "punct", Text = ch.ToString() } );
				i++;
				continue;
			}
			throw new FormatException( $"Unexpected character '{ch}' in .usda text at offset {i}." );
		}
		tokens.Add( new UsdToken { Kind = "end", Text = "" } );
		return tokens;
	}

	UsdToken Peek( int ahead = 0 ) => _tokens[Math.Min( _pos + ahead, _tokens.Count - 1 )];
	UsdToken Next() => _tokens[_pos++];
	bool AtEnd => Peek().Kind == "end";
	bool IsPunct( string p ) => Peek().Kind == "punct" && Peek().Text == p;
	bool IsIdent( string s ) => Peek().Kind == "ident" && Peek().Text == s;
	bool IsIdent() => Peek().Kind == "ident";
	void ExpectPunct( string p )
	{
		if ( !IsPunct( p ) ) throw new FormatException( $"Expected '{p}' in .usda text, found '{Peek().Text}'." );
		_pos++;
	}
	string ExpectIdent()
	{
		if ( !IsIdent() ) throw new FormatException( $"Expected an identifier in .usda text, found '{Peek().Text}'." );
		return Next().Text;
	}
	string ExpectString()
	{
		if ( Peek().Kind != "string" ) throw new FormatException( $"Expected a quoted string in .usda text, found '{Peek().Text}'." );
		return Next().Text;
	}
	void Loss( string loss ) => _stage.Losses.Add( loss );

	public UsdStage ParseStage()
	{
		while ( !AtEnd )
		{
			if ( IsPunct( "(" ) ) ParseHeader();
			else if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) ) _stage.Roots.Add( ParsePrim( null ) );
			else { Loss( $"unparsed top-level statement starting at '{Peek().Text}'" ); SkipStatement(); }
		}
		return _stage;
	}

	void ParseHeader()
	{
		ExpectPunct( "(" );
		while ( !IsPunct( ")" ) && !AtEnd )
		{
			var key = ExpectIdent();
			ExpectPunct( "=" );
			switch ( key )
			{
				case "defaultPrim": _stage.DefaultPrim = ExpectString(); break;
				case "doc": _stage.Doc = ExpectString(); break;
				case "metersPerUnit": _stage.MetersPerUnitValue = ParseFloat(); break;
				case "upAxis": _stage.UpAxis = ExpectString(); break;
				case "subLayers": _stage.SubLayers = ParseAssetList(); break;
				case "customLayerData": SkipValue(); break;
				default: Loss( $"header metadata '{key}' ignored" ); SkipValue(); break;
			}
		}
		ExpectPunct( ")" );
	}

	List<string> ParseAssetList()
	{
		var list = new List<string>();
		if ( Peek().Kind == "asset" ) { list.Add( Next().Text ); return list; }
		ExpectPunct( "[" );
		while ( !IsPunct( "]" ) && !AtEnd )
		{
			if ( Peek().Kind == "asset" ) list.Add( Next().Text );
			else if ( IsPunct( "," ) ) _pos++;
			else throw new FormatException( $"Expected an asset path in subLayers, found '{Peek().Text}'." );
		}
		ExpectPunct( "]" );
		return list;
	}

	UsdPrim ParsePrim( UsdPrim parent )
	{
		var spec = ExpectIdent(); // def | over | class
		var type = ExpectIdent();
		var name = ExpectString();
		var prim = new UsdPrim { Name = name, TypeName = type, Spec = spec, Parent = parent };
		if ( IsPunct( "(" ) ) ParsePrimMetadata( prim );
		ExpectPunct( "{" );
		ParsePrimBody( prim );
		ExpectPunct( "}" );
		return prim;
	}

	void ParsePrimMetadata( UsdPrim prim )
	{
		ExpectPunct( "(" );
		while ( !IsPunct( ")" ) && !AtEnd )
		{
			var key = ExpectIdent();
			if ( key == "prepend" || key == "append" || key == "add" || key == "delete" ) key = ExpectIdent();
			ExpectPunct( "=" );
			switch ( key )
			{
				case "kind": prim.Kind = ExpectString(); break;
				case "active": prim.Active = ParseBool(); break;
				case "instanceable": prim.Instanceable = ParseBool(); break;
				case "apiSchemas": prim.ApiSchemas = ParseStringList(); break;
				case "references": prim.References = ParseAssetList(); break;
				case "payload": prim.Payloads = ParseAssetList(); break;
				case "inherits": prim.Inherits = ParsePathList(); break;
				case "specializes": prim.Specializes = ParsePathList(); break;
				case "variants": ParseVariantsDict( prim ); break;
				case "variantSelection": prim.VariantSelection[""] = ExpectString(); break;
				case "doc": SkipValue(); break;
				case "customData": SkipValue(); break;
				default: Loss( $"prim metadata '{key}' on {prim.Path} ignored" ); SkipValue(); break;
			}
		}
		ExpectPunct( ")" );
	}

	void ParseVariantsDict( UsdPrim prim )
	{
		ExpectPunct( "{" );
		while ( !IsPunct( "}" ) && !AtEnd )
		{
			if ( IsIdent( "string" ) ) _pos++;
			var set = ExpectIdent();
			ExpectPunct( "=" );
			var variant = ExpectString();
			prim.VariantSelection[set] = variant;
			if ( IsPunct( "," ) ) _pos++;
		}
		ExpectPunct( "}" );
	}

	List<string> ParseStringList()
	{
		var list = new List<string>();
		if ( Peek().Kind == "string" ) { list.Add( Next().Text ); return list; }
		ExpectPunct( "[" );
		while ( !IsPunct( "]" ) && !AtEnd )
		{
			if ( Peek().Kind == "string" ) list.Add( Next().Text );
			else if ( IsPunct( "," ) ) _pos++;
			else throw new FormatException( $"Expected a string in a list, found '{Peek().Text}'." );
		}
		ExpectPunct( "]" );
		return list;
	}

	List<string> ParsePathList()
	{
		var list = new List<string>();
		if ( IsPunct( "<" ) ) { list.Add( ParsePath() ); return list; }
		ExpectPunct( "[" );
		while ( !IsPunct( "]" ) && !AtEnd )
		{
			if ( IsPunct( "<" ) ) list.Add( ParsePath() );
			else if ( IsPunct( "," ) ) _pos++;
			else throw new FormatException( $"Expected a prim path in a list, found '{Peek().Text}'." );
		}
		ExpectPunct( "]" );
		return list;
	}

	string ParsePath()
	{
		ExpectPunct( "<" );
		var sb = new StringBuilder();
		while ( !IsPunct( ">" ) && !AtEnd )
		{
			if ( Peek().Kind == "ident" ) sb.Append( Next().Text );
			else if ( IsPunct( "/" ) ) { sb.Append( "/" ); _pos++; }
			else throw new FormatException( $"Expected a prim path, found '{Peek().Text}'." );
		}
		ExpectPunct( ">" );
		return sb.ToString();
	}

	void ParsePrimBody( UsdPrim prim )
	{
		while ( !IsPunct( "}" ) && !AtEnd )
		{
			if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) )
			{
				prim.Children.Add( ParsePrim( prim ) );
				continue;
			}
			if ( IsIdent( "variantSet" ) )
			{
				_pos++;
				var set = ExpectString();
				ExpectPunct( "{" );
				var variants = new List<string>();
				var map = new Dictionary<string, List<UsdPrim>>();
				while ( !IsPunct( "}" ) && !AtEnd )
				{
					var variant = ExpectString();
					variants.Add( variant );
					ExpectPunct( "{" );
					var prims = new List<UsdPrim>();
					while ( !IsPunct( "}" ) && !AtEnd )
					{
						if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) ) prims.Add( ParsePrim( prim ) );
						else { Loss( $"unparsed statement in variant {variant} of {prim.Path}" ); SkipStatement(); }
					}
					ExpectPunct( "}" );
					map[variant] = prims;
				}
				ExpectPunct( "}" );
				prim.VariantSets[set] = variants;
				prim.VariantPrims[set] = map;
				continue;
			}
			if ( IsIdent( "rel" ) )
			{
				_pos++;
				var name = ExpectIdent();
				if ( name.EndsWith( ".connect", StringComparison.Ordinal ) ) name = name.Substring( 0, name.Length - ".connect".Length );
				ExpectPunct( "=" );
				var path = ParsePath();
				if ( name == "material:binding" ) prim.MaterialBinding = path;
				else prim.Attributes.Add( new UsdAttribute { Name = name, TypeName = "rel", Connection = path } );
				continue;
			}
			if ( IsIdent( "prepend" ) || IsIdent( "append" ) || IsIdent( "add" ) || IsIdent( "delete" ) )
			{
				_pos++;
				ParseCompositionList( prim );
				continue;
			}
			if ( IsIdent( "references" ) || IsIdent( "payload" ) || IsIdent( "inherits" ) || IsIdent( "specializes" ) || IsIdent( "apiSchemas" ) )
			{
				ParseCompositionList( prim );
				continue;
			}
			if ( IsIdent( "active" ) ) { _pos++; ExpectPunct( "=" ); prim.Active = ParseBool(); continue; }
			if ( IsIdent( "instanceable" ) ) { _pos++; ExpectPunct( "=" ); prim.Instanceable = ParseBool(); continue; }
			if ( IsIdent( "kind" ) ) { _pos++; ExpectPunct( "=" ); prim.Kind = ExpectString(); continue; }
			if ( IsIdent( "purpose" ) ) { _pos++; ExpectPunct( "=" ); prim.Purpose = ExpectString(); continue; }
			ParseAttribute( prim );
		}
	}

	void ParseCompositionList( UsdPrim prim )
	{
		var key = ExpectIdent();
		ExpectPunct( "=" );
		switch ( key )
		{
			case "references": prim.References = ParseAssetList(); break;
			case "payload": prim.Payloads = ParseAssetList(); break;
			case "inherits": prim.Inherits = ParsePathList(); break;
			case "specializes": prim.Specializes = ParsePathList(); break;
			case "apiSchemas": prim.ApiSchemas = ParseStringList(); break;
			default: Loss( $"composition list '{key}' on {prim.Path} ignored" ); SkipValue(); break;
		}
	}

	void ParseAttribute( UsdPrim prim )
	{
		var isUniform = false;
		var isCustom = false;
		if ( IsIdent( "uniform" ) ) { isUniform = true; _pos++; }
		if ( IsIdent( "custom" ) ) { isCustom = true; _pos++; }
		if ( !IsIdent() ) { Loss( $"unparsed statement in {prim.Path} starting at '{Peek().Text}'" ); SkipStatement(); return; }
		var type = ExpectIdent();
		if ( IsPunct( "[" ) ) { _pos++; ExpectPunct( "]" ); type += "[]"; }
		if ( !IsIdent() ) { Loss( $"unparsed attribute in {prim.Path} after type '{type}'" ); SkipStatement(); return; }
		var name = ExpectIdent();
		if ( name.EndsWith( ".connect", StringComparison.Ordinal ) ) name = name.Substring( 0, name.Length - ".connect".Length );
		var attr = new UsdAttribute { Name = name, TypeName = type, IsUniform = isUniform, IsCustom = isCustom };
		if ( name.Contains( ".timeSamples", StringComparison.Ordinal ) )
		{
			// Animated values are not flattened: keep the loss, use the default (already parsed or absent).
			Loss( $"timeSamples on {prim.Path}.{name} not flattened; default value used" );
			if ( IsPunct( "=" ) ) { _pos++; SkipValue(); }
			return;
		}
		if ( IsPunct( "=" ) )
		{
			_pos++;
			if ( IsPunct( "<" ) ) attr.Connection = ParsePath();
			else attr.Value = ParseValue( type );
		}
		if ( IsPunct( "(" ) )
		{
			_pos++;
			while ( !IsPunct( ")" ) && !AtEnd )
			{
				var key = ExpectIdent();
				ExpectPunct( "=" );
				if ( key == "interpolation" ) attr.Interpolation = ExpectString();
				else if ( key == "elementSize" ) SkipValue();
				else { Loss( $"attribute metadata '{key}' on {prim.Path}.{name} ignored" ); SkipValue(); }
			}
			ExpectPunct( ")" );
		}
		prim.Attributes.Add( attr );
	}

	object ParseValue( string type )
	{
		if ( Peek().Kind == "string" ) return Next().Text;
		if ( Peek().Kind == "asset" ) return Next().Text;
		if ( IsPunct( "[" ) )
		{
			var element = type.EndsWith( "[]", StringComparison.Ordinal ) ? type.Substring( 0, type.Length - 2 ) : type;
			_pos++;
			var items = new List<object>();
			while ( !IsPunct( "]" ) && !AtEnd )
			{
				if ( IsPunct( "," ) ) { _pos++; continue; }
				items.Add( ParseValue( element ) );
			}
			ExpectPunct( "]" );
			return CoerceList( element, items );
		}
		if ( IsPunct( "(" ) )
		{
			_pos++;
			var numbers = new List<float>();
			while ( !IsPunct( ")" ) && !AtEnd )
			{
				if ( IsPunct( "," ) ) { _pos++; continue; }
				numbers.Add( ParseFloat() );
			}
			ExpectPunct( ")" );
			if ( numbers.Count == 2 ) return new Vec2( numbers[0], numbers[1] );
			if ( numbers.Count == 3 ) return new Vec3( numbers[0], numbers[1], numbers[2] );
			return numbers;
		}
		if ( IsPunct( "{" ) ) { SkipValue(); return null; }
		if ( IsIdent( "None" ) ) { _pos++; return null; }
		if ( IsIdent( "true" ) ) { _pos++; return true; }
		if ( IsIdent( "false" ) ) { _pos++; return false; }
		if ( Peek().Kind == "number" )
		{
			var text = Next().Text;
			if ( type == "bool" ) return text != "0"; // USDA writes bools as 0/1
			if ( type == "int" || type == "uint" ) return int.Parse( text, CultureInfo.InvariantCulture );
			return float.Parse( text, CultureInfo.InvariantCulture );
		}
		if ( IsIdent() ) return Next().Text; // bare token value
		throw new FormatException( $"Cannot parse a '{type}' value starting at '{Peek().Text}'." );
	}

	static object CoerceList( string element, List<object> items )
	{
		switch ( element )
		{
			case "int": case "uint":
				var ints = new List<int>();
				foreach ( var item in items ) ints.Add( item is int i ? i : (int)(float)item );
				return ints;
			case "float": case "double":
				var floats = new List<float>();
				foreach ( var item in items ) floats.Add( item is float f ? f : (float)(int)item );
				return floats;
			case "string": case "token": case "asset":
				var strings = new List<string>();
				foreach ( var item in items ) strings.Add( item?.ToString() );
				return strings;
			case "float2": case "texCoord2f":
				var v2s = new List<Vec2>();
				foreach ( var item in items ) if ( item is Vec2 v2 ) v2s.Add( v2 );
				return v2s;
			default: // float3, point3f, normal3f, color3f, vector3f, ...
				var v3s = new List<Vec3>();
				foreach ( var item in items ) if ( item is Vec3 v3 ) v3s.Add( v3 );
				return v3s;
		}
	}

	float ParseFloat()
	{
		if ( Peek().Kind != "number" ) throw new FormatException( $"Expected a number in .usda text, found '{Peek().Text}'." );
		return float.Parse( Next().Text, CultureInfo.InvariantCulture );
	}

	bool ParseBool()
	{
		if ( IsIdent( "true" ) ) { _pos++; return true; }
		if ( IsIdent( "false" ) ) { _pos++; return false; }
		if ( Peek().Kind == "number" ) return Next().Text != "0";
		throw new FormatException( $"Expected a bool in .usda text, found '{Peek().Text}'." );
	}

	void SkipValue()
	{
		if ( IsPunct( "(" ) || IsPunct( "[" ) || IsPunct( "{" ) )
		{
			var open = Next().Text;
			var close = open == "(" ? ")" : open == "[" ? "]" : "}";
			var depth = 1;
			while ( depth > 0 && !AtEnd )
			{
				if ( IsPunct( open ) ) depth++;
				else if ( IsPunct( close ) ) depth--;
				_pos++;
			}
			return;
		}
		if ( IsPunct( "<" ) ) { ParsePath(); return; }
		_pos++; // one scalar token
	}

	void SkipStatement()
	{
		while ( !AtEnd && !IsPunct( "}" ) )
		{
			if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) || IsIdent( "variantSet" ) || IsIdent( "rel" ) ) return;
			if ( LooksLikeAttributeStart() ) return;
			if ( IsPunct( "{" ) || IsPunct( "(" ) || IsPunct( "[" ) ) SkipValue();
			else _pos++;
		}
	}

	bool LooksLikeAttributeStart()
	{
		if ( !IsIdent() ) return false;
		var type = Peek().Text;
		if ( type == "uniform" || type == "custom" ) return Peek( 1 ).Kind == "ident";
		if ( Peek( 1 ).Kind != "ident" ) return false;
		var after = Peek( 2 );
		if ( after.Kind == "punct" && after.Text == "=" ) return true;
		if ( after.Kind == "punct" && after.Text == "[" ) return true;
		if ( after.Kind == "punct" && after.Text == "(" ) return true;
		return false;
	}
}
