#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Engine-independent glTF 2.0 reader (binary <c>.glb</c> and JSON <c>.gltf</c>) that produces
/// the shared imported-model shape (<see cref="FbxImport.Result"/>), so a GLB loads through
/// the same session/snapshot path as an FBX. No engine, no new package: System.Text.Json only.
///
/// UNITS AND AXES. glTF is metres, Y-up, right-handed. The kernel is inches, Z-up, with
/// right = +X, up = +Z, front = -Y (see <see cref="FbxImport.AxisMap"/>). A glTF point
/// (x, y, z) lands at (x, -z, y) inches — the same -90° rotation about X the FBX path uses
/// for a Y-up file — scaled by 100 / 2.54 inches per metre. The rotation is proper
/// (determinant +1), so winding survives; only a mirroring node transform flips it, and
/// that is handled the way the FBX path handles it (reversed corner order).
/// UVs pass through unchanged: the kernel's V convention is glTF's V convention (Mobile's
/// GLB writer writes kernel UVs verbatim), unlike FBX, whose V is flipped on import.
///
/// STATIC ONLY. A file carrying a skin is refused (its vertices are joint-space without the
/// bind pose applied, so a static read would be wrong, not merely incomplete). Animations
/// and morph targets are noted and ignored: they do not change the static bind shape.
/// Imported NORMALs are not retained — <see cref="PolyMesh"/> carries no normal storage and
/// the viewport recomputes corner normals from faces — and are noted as such.
///
/// TEXTURES. Embedded PNGs are decoded (validation + dimensions) and reported in
/// <see cref="FbxImport.Result.Notes"/>; JPEGs cannot be decoded — the engine-free kernel
/// has no JPEG decoder — and are noted as undecoded rather than silently dropped. Nothing
/// is written to disk on import: there is no session slot for imported textures yet (that
/// needs a <c>DocumentSession</c> change), so the maps are validated and reported here and
/// the session keeps geometry, names and material names.
/// </summary>
public static class GlbImport
{
	/// <summary>Metres per kernel inch (glTF is metres, the kernel is inches).</summary>
	public const double InchesToMetres = 0.0254;

	/// <summary>Inches per glTF metre (100 cm per metre / 2.54 cm per inch).</summary>
	public const double MetresToInches = 100.0 / 2.54;

	const uint GlbMagic = 0x46546C67; // "glTF"
	const uint GlbJsonChunk = 0x4E4F534A; // "JSON"
	const uint GlbBinChunk = 0x004E4942; // "BIN\0"

	/// <summary>Read a <c>.glb</c> or <c>.gltf</c> file. External buffers/images in a
	/// <c>.gltf</c> resolve relative to the file's directory; absolute paths are refused.</summary>
	public static FbxImport.Result ReadFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Path cannot be empty.", nameof( path ) );
		return Read( File.ReadAllBytes( path ), path );
	}

	/// <summary>Read <c>.glb</c> bytes, or <c>.gltf</c> JSON bytes. <paramref name="sourcePath"/>
	/// is the file the bytes came from: it supplies the base directory for a <c>.gltf</c>'s
	/// external buffers/images and names the file in error messages. Null means no external
	/// reference may resolve (embedded/data-URI content only).</summary>
	public static FbxImport.Result Read( byte[] bytes, string? sourcePath = null )
	{
		if ( bytes is null ) throw new ArgumentNullException( nameof( bytes ) );
		if ( !BitConverter.IsLittleEndian ) throw new NotSupportedException( "glTF is little-endian; this platform is not." );
		string display = string.IsNullOrWhiteSpace( sourcePath ) ? "<bytes>" : sourcePath;
		string? baseDir = string.IsNullOrWhiteSpace( sourcePath )
			? null
			: Path.GetDirectoryName( Path.GetFullPath( sourcePath ) );

		byte[] jsonBytes;
		byte[]? binChunk;
		if ( bytes.Length >= 4 && BitConverter.ToUInt32( bytes, 0 ) == GlbMagic )
		{
			(jsonBytes, binChunk) = ReadGlbContainer( bytes, display );
		}
		else
		{
			jsonBytes = bytes;
			binChunk = null;
		}

		JsonDocument doc;
		try { doc = JsonDocument.Parse( jsonBytes ); }
		catch ( JsonException ex ) { throw new InvalidDataException( $"glTF JSON in '{display}' does not parse: {ex.Message}" ); }
		using ( doc )
		{
			var reader = new Reader( doc.RootElement, binChunk, baseDir, display );
			return reader.Run();
		}
	}

	static (byte[] Json, byte[]? Bin) ReadGlbContainer( byte[] bytes, string display )
	{
		if ( bytes.Length < 12 ) throw new InvalidDataException( $"GLB '{display}' is truncated (header needs 12 bytes, got {bytes.Length})." );
		uint version = BitConverter.ToUInt32( bytes, 4 );
		if ( version != 2 ) throw new NotSupportedException( $"GLB '{display}' is glTF version {version}; only 2.0 is supported." );
		uint totalLength = BitConverter.ToUInt32( bytes, 8 );
		if ( totalLength > bytes.Length ) throw new InvalidDataException( $"GLB '{display}' declares {totalLength} bytes but only {bytes.Length} were read." );

		int offset = 12;
		if ( offset + 8 > bytes.Length ) throw new InvalidDataException( $"GLB '{display}' has no JSON chunk." );
		uint jsonLength = BitConverter.ToUInt32( bytes, offset );
		uint jsonType = BitConverter.ToUInt32( bytes, offset + 4 );
		if ( jsonType != GlbJsonChunk ) throw new InvalidDataException( $"GLB '{display}' first chunk is not JSON (type 0x{jsonType:X8})." );
		if ( jsonLength > bytes.Length - offset - 8 ) throw new InvalidDataException( $"GLB '{display}' JSON chunk overruns the file." );
		byte[] json = new byte[jsonLength];
		Array.Copy( bytes, offset + 8, json, 0, (int)jsonLength );
		offset += 8 + (int)jsonLength;

		byte[]? bin = null;
		if ( offset < totalLength )
		{
			if ( offset + 8 > bytes.Length ) throw new InvalidDataException( $"GLB '{display}' BIN chunk header is truncated." );
			uint binLength = BitConverter.ToUInt32( bytes, offset );
			uint binType = BitConverter.ToUInt32( bytes, offset + 4 );
			if ( binType != GlbBinChunk ) throw new InvalidDataException( $"GLB '{display}' second chunk is not BIN (type 0x{binType:X8})." );
			if ( binLength > bytes.Length - offset - 8 ) throw new InvalidDataException( $"GLB '{display}' BIN chunk overruns the file." );
			bin = new byte[binLength];
			Array.Copy( bytes, offset + 8, bin, 0, (int)binLength );
		}
		return (json, bin);
	}

	/// <summary>One material as the file describes it, for the import notes. Factors are
	/// glTF-linear; textures are reported with their decoded size or the reason they could
	/// not be decoded.</summary>
	public sealed record ImportedMaterial(
		string Name,
		float[] BaseColorFactor,
		float MetallicFactor,
		float RoughnessFactor,
		float[] EmissiveFactor,
		IReadOnlyList<string> TextureNotes );

	sealed class Reader
	{
		readonly JsonElement _root;
		readonly byte[]? _glbBin;
		readonly string? _baseDir;
		readonly string _display;
		readonly List<string> _notes = new();
		readonly List<byte[]> _buffers = new();
		readonly List<(int Buffer, int Offset, int Length, int? Stride)> _views = new();
		readonly List<Accessor> _accessors = new();
		readonly List<ImportedMaterial> _materials = new();
		readonly Dictionary<string, int> _slotByName = new( StringComparer.Ordinal );
		readonly SortedDictionary<int, string> _slots = new();
		bool _notedNormals;
		bool _notedMorphs;

		sealed record Accessor( int View, int Offset, int ComponentType, int Count, string Type, bool Normalized, bool Sparse );

		public Reader( JsonElement root, byte[]? glbBin, string? baseDir, string display )
		{
			_root = root;
			_glbBin = glbBin;
			_baseDir = baseDir;
			_display = display;
		}

		public FbxImport.Result Run()
		{
			RequireObject( _root, "glTF root" );
			string version = GetString( _root, "asset", "version" ) ?? "";
			if ( version != "2.0" ) throw new NotSupportedException( $"glTF '{_display}' declares asset version '{version}'; only 2.0 is supported." );

			if ( _root.TryGetProperty( "extensionsRequired", out var required ) )
			{
				foreach ( var ext in required.EnumerateArray() )
				{
					string name = ext.GetString() ?? "";
					if ( name == "KHR_draco_mesh_compression" )
						throw new NotSupportedException( $"glTF '{_display}' requires KHR_draco_mesh_compression (Draco); no Draco decoder is available." );
					_notes.Add( $"Required extension '{name}' is not understood; the file was read without it." );
				}
			}
			if ( _root.TryGetProperty( "extensionsUsed", out var used ) )
			{
				foreach ( var ext in used.EnumerateArray() )
				{
					string name = ext.GetString() ?? "";
					if ( name != "KHR_draco_mesh_compression" && name != "KHR_materials_unlit" )
						_notes.Add( $"Extension '{name}' is present; its effect was not applied." );
				}
			}

			LoadBuffers();
			LoadViews();
			LoadAccessors();
			LoadMaterials();

			if ( _root.TryGetProperty( "skins", out var skins ) && skins.GetArrayLength() > 0 )
				throw new NotSupportedException( $"glTF '{_display}' carries {skins.GetArrayLength()} skin(s); skinned import is not supported in this static slice." );
			if ( _root.TryGetProperty( "animations", out var anims ) && anims.GetArrayLength() > 0 )
			{
				_notes.Add( $"{anims.GetArrayLength()} animation(s) present; static import ignores them." );
			}

			var result = new FbxImport.Result { UnitScale = 100.0, AppliedScale = MetresToInches };
			var meshes = GetArray( _root, "meshes" );
			if ( meshes is null ) throw new InvalidDataException( $"glTF '{_display}' has no meshes." );
			var nodes = GetArray( _root, "nodes" );
			var scenes = GetArray( _root, "scenes" );
			int sceneIndex = GetInt( _root, "scene", 0 );
			var sceneNodes = scenes is not null && sceneIndex < scenes.Value.GetArrayLength()
				? scenes.Value[sceneIndex]
				: default;
			var roots = new List<int>();
			if ( sceneNodes.ValueKind == JsonValueKind.Object && sceneNodes.TryGetProperty( "nodes", out var sceneRoots ) )
				foreach ( var n in sceneRoots.EnumerateArray() ) roots.Add( n.GetInt32() );
			else if ( nodes is not null )
			{
				// No scene: every node that is nobody's child is a root.
				var children = new HashSet<int>();
				for ( int i = 0; i < nodes.Value.GetArrayLength(); i++ )
				{
					var n = nodes.Value[i];
					if ( n.TryGetProperty( "children", out var ch ) )
						foreach ( var c in ch.EnumerateArray() ) children.Add( c.GetInt32() );
				}
				for ( int i = 0; i < nodes.Value.GetArrayLength(); i++ )
					if ( !children.Contains( i ) ) roots.Add( i );
			}

			int piecesBefore = result.Pieces.Count;
			foreach ( int rootNode in roots )
				WalkNode( rootNode, Xform.Identity, meshes.Value, nodes, result );
			if ( result.Pieces.Count == piecesBefore )
				throw new InvalidDataException( $"glTF '{_display}' contains no mesh geometry." );

			foreach ( var (slot, name) in _slots ) result.MaterialSlots[slot] = name;
			result.Notes.Add( $"glTF 2.0 '{Path.GetFileName( _display )}': {result.Pieces.Count} piece(s), {_slots.Count} material(s); metres × {MetresToInches:F4} to inches, Y-up → Z-up." );
			result.Notes.AddRange( _notes );
			return result;
		}

		void WalkNode( int nodeIndex, Xform parentWorld, JsonElement meshes, JsonElement? nodes, FbxImport.Result result )
		{
			if ( nodes is null || nodeIndex < 0 || nodeIndex >= nodes.Value.GetArrayLength() )
				throw new InvalidDataException( $"glTF '{_display}' references missing node {nodeIndex}." );
			var node = nodes.Value[nodeIndex];
			Xform local = NodeLocal( node );
			Xform world = parentWorld * local;

			if ( node.TryGetProperty( "mesh", out var meshProp ) )
			{
				int meshIndex = meshProp.GetInt32();
				string? nodeName = GetString( node, "name" );
				ReadMesh( meshIndex, nodeName, world, result );
			}
			if ( node.TryGetProperty( "children", out var children ) )
				foreach ( var child in children.EnumerateArray() )
					WalkNode( child.GetInt32(), world, meshes, nodes, result );
		}

		static Xform NodeLocal( JsonElement node )
		{
			if ( node.TryGetProperty( "matrix", out var matrix ) )
			{
				var m = ReadFloatArray( matrix, 16, "node matrix" );
				// Column-major: columns are the images of the basis vectors.
				return new Xform(
					new Vec3( m[0], m[1], m[2] ),
					new Vec3( m[4], m[5], m[6] ),
					new Vec3( m[8], m[9], m[10] ),
					new Vec3( m[12], m[13], m[14] ) );
			}
			Vec3 translation = Vec3.Zero, scale = Vec3.One;
			Quat rotation = Quat.Identity;
			if ( node.TryGetProperty( "translation", out var t ) )
			{
				var v = ReadFloatArray( t, 3, "node translation" );
				translation = new Vec3( v[0], v[1], v[2] );
			}
			if ( node.TryGetProperty( "rotation", out var r ) )
			{
				var v = ReadFloatArray( r, 4, "node rotation" );
				rotation = NormalizeQuat( new Quat( v[0], v[1], v[2], v[3] ) );
			}
			if ( node.TryGetProperty( "scale", out var s ) )
			{
				var v = ReadFloatArray( s, 3, "node scale" );
				scale = new Vec3( v[0], v[1], v[2] );
			}
			return Xform.Translate( translation ) * RotationXform( rotation ) * Xform.Scale( scale );
		}

		static Quat NormalizeQuat( Quat q )
		{
			float len2 = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
			if ( len2 < 1e-16f ) return Quat.Identity;
			float inv = 1f / MathF.Sqrt( len2 );
			return new Quat( q.x * inv, q.y * inv, q.z * inv, q.w * inv );
		}

		static Xform RotationXform( Quat q )
		{
			float x = q.x, y = q.y, z = q.z, w = q.w;
			float xx = x * x, yy = y * y, zz = z * z;
			float xy = x * y, xz = x * z, yz = y * z;
			float wx = w * x, wy = w * y, wz = w * z;
			// Columns are the images of the basis vectors.
			return new Xform(
				new Vec3( 1f - 2f * ( yy + zz ), 2f * ( xy + wz ), 2f * ( xz - wy ) ),
				new Vec3( 2f * ( xy - wz ), 1f - 2f * ( xx + zz ), 2f * ( yz + wx ) ),
				new Vec3( 2f * ( xz + wy ), 2f * ( yz - wx ), 1f - 2f * ( xx + yy ) ),
				Vec3.Zero );
		}

		void ReadMesh( int meshIndex, string? nodeName, Xform world, FbxImport.Result result )
		{
			var meshes = GetArray( _root, "meshes" );
			if ( meshes is null || meshIndex < 0 || meshIndex >= meshes.Value.GetArrayLength() )
				throw new InvalidDataException( $"glTF '{_display}' references missing mesh {meshIndex}." );
			var mesh = meshes.Value[meshIndex];
			string meshName = GetString( mesh, "name" ) ?? $"mesh_{meshIndex}";
			var primitives = GetArray( mesh, "primitives" );
			if ( primitives is null || primitives.Value.GetArrayLength() == 0 )
			{
				_notes.Add( $"Mesh '{meshName}' has no primitives; skipped." );
				return;
			}
			bool mirror = world.FlipsWinding;
			for ( int pi = 0; pi < primitives.Value.GetArrayLength(); pi++ )
			{
				var prim = primitives.Value[pi];
				string pieceName = nodeName ?? meshName;
				if ( primitives.Value.GetArrayLength() > 1 ) pieceName += $":{pi}";
				ReadPrimitive( prim, pieceName, world, mirror, result );
			}
		}

		void ReadPrimitive( JsonElement prim, string pieceName, Xform world, bool mirror, FbxImport.Result result )
		{
			int mode = GetInt( prim, "mode", 4 );
			if ( mode != 4 ) throw new NotSupportedException( $"glTF '{_display}' primitive '{pieceName}' uses mode {mode}; only TRIANGLES (4) is supported." );
			if ( prim.TryGetProperty( "targets", out var targets ) && targets.GetArrayLength() > 0 && !_notedMorphs )
			{
				_notes.Add( "Morph targets are present; static import uses the base shape and ignores target weights." );
				_notedMorphs = true;
			}
			var attributes = GetObject( prim, "attributes" ) ?? throw new InvalidDataException( $"glTF '{_display}' primitive '{pieceName}' has no attributes." );
			if ( !attributes.TryGetProperty( "POSITION", out var posAcc ) )
				throw new InvalidDataException( $"glTF '{_display}' primitive '{pieceName}' has no POSITION attribute." );

			float[] positions = ReadFloatAccessor( posAcc.GetInt32(), 3, "POSITION" );
			int vertexCount = positions.Length / 3;
			if ( vertexCount == 0 )
			{
				_notes.Add( $"Primitive '{pieceName}' has no vertices; skipped." );
				return;
			}
			if ( attributes.TryGetProperty( "NORMAL", out _ ) && !_notedNormals )
			{
				_notes.Add( "NORMAL attributes are present; they are not retained (the viewport recomputes corner normals from faces)." );
				_notedNormals = true;
			}
			Vec2[]? uvs = null;
			if ( attributes.TryGetProperty( "TEXCOORD_0", out var uvAcc ) )
			{
				float[] uvData = ReadFloatAccessor( uvAcc.GetInt32(), 2, "TEXCOORD_0" );
				uvs = new Vec2[vertexCount];
				for ( int i = 0; i < vertexCount; i++ ) uvs[i] = new Vec2( uvData[i * 2], uvData[i * 2 + 1] );
			}
			Vec4[]? colors = null;
			if ( attributes.TryGetProperty( "COLOR_0", out var colorAcc ) )
				colors = ReadColorAccessor( colorAcc.GetInt32(), vertexCount );

			int[] indices;
			if ( prim.TryGetProperty( "indices", out var idxAcc ) )
				indices = ReadIndexAccessor( idxAcc.GetInt32() );
			else
			{
				indices = new int[vertexCount];
				for ( int i = 0; i < vertexCount; i++ ) indices[i] = i;
			}
			if ( indices.Length % 3 != 0 ) throw new InvalidDataException( $"glTF '{_display}' primitive '{pieceName}' has {indices.Length} indices, not a multiple of 3." );

			var mesh = new PolyMesh();
			for ( int v = 0; v < vertexCount; v++ )
			{
				var p = new Vec3( positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2] );
				// Apply the node world transform in glTF space first, then convert the
				// result: glTF (metres, Y-up) → kernel (inches, Z-up) is (x, y, z) →
				// (x, -z, y), scaled by inches per metre. The conversion is a pure
				// rotation, so the glTF-space world transform's determinant (and hence
				// the winding flip computed from it) survives the conversion unchanged.
				var worldPoint = world.TransformPoint( p );
				var converted = new Vec3( worldPoint.x, -worldPoint.z, worldPoint.y ) * (float)MetresToInches;
				mesh.Positions.Add( converted );
			}
			if ( colors is not null ) mesh.VertexColors = colors;

			int slot = 0;
			if ( prim.TryGetProperty( "material", out var matProp ) )
			{
				int matIndex = matProp.GetInt32();
				if ( matIndex < 0 || matIndex >= _materials.Count )
					throw new InvalidDataException( $"glTF '{_display}' primitive '{pieceName}' references missing material {matIndex}." );
				slot = AssignSlot( _materials[matIndex].Name );
			}
			else
			{
				slot = AssignSlot( ObjWriter.DefaultMaterialName( 0 ) );
			}

			int dropped = 0;
			for ( int t = 0; t < indices.Length; t += 3 )
			{
				int a = indices[t], b = indices[t + 1], c = indices[t + 2];
				if ( a < 0 || a >= vertexCount || b < 0 || b >= vertexCount || c < 0 || c >= vertexCount )
					throw new InvalidDataException( $"glTF '{_display}' primitive '{pieceName}' has an out-of-range index ({a}, {b}, {c} for {vertexCount} vertices)." );
				if ( a == b || b == c || a == c ) { dropped++; continue; }
				var corners = mirror ? new[] { a, c, b } : new[] { a, b, c };
				var faceUvs = new Vec2[3];
				for ( int k = 0; k < 3; k++ )
					faceUvs[k] = uvs is not null ? uvs[corners[k]] : new Vec2( 0f, 0f );
				mesh.AddFace( corners, faceUvs, slot );
			}
			if ( dropped > 0 ) _notes.Add( $"Primitive '{pieceName}': {dropped} degenerate triangle(s) dropped." );
			if ( mesh.FaceCount == 0 )
			{
				_notes.Add( $"Primitive '{pieceName}' produced no faces; skipped." );
				return;
			}
			result.Pieces.Add( new ObjReader.ObjPiece( pieceName, mesh ) );
			result.Weights.Add( null );
		}

		int AssignSlot( string name )
		{
			if ( _slotByName.TryGetValue( name, out int slot ) ) return slot;
			if ( TryDefaultSlot( name, out slot ) && !_slots.ContainsKey( slot ) )
			{
				_slotByName[name] = slot;
				_slots[slot] = name;
				return slot;
			}
			slot = 0;
			while ( _slots.ContainsKey( slot ) ) slot++;
			_slotByName[name] = slot;
			_slots[slot] = name;
			return slot;
		}

		static bool TryDefaultSlot( string name, out int slot )
		{
			slot = 0;
			const string prefix = "material_";
			if ( !name.StartsWith( prefix, StringComparison.Ordinal ) ) return false;
			return int.TryParse( name.AsSpan( prefix.Length ), out slot ) && slot >= 0;
		}

		void LoadBuffers()
		{
			var buffers = GetArray( _root, "buffers" );
			if ( buffers is null ) throw new InvalidDataException( $"glTF '{_display}' has no buffers." );
			for ( int i = 0; i < buffers.Value.GetArrayLength(); i++ )
			{
				var buffer = buffers.Value[i];
				int byteLength = GetInt( buffer, "byteLength", -1 );
				if ( byteLength < 0 ) throw new InvalidDataException( $"glTF '{_display}' buffer {i} has no byteLength." );
				string? uri = GetString( buffer, "uri" );
				if ( _glbBin is not null && i == 0 && uri is null )
				{
					_buffers.Add( _glbBin );
					continue;
				}
				if ( uri is null ) throw new InvalidDataException( $"glTF '{_display}' buffer {i} has no uri and no GLB BIN chunk." );
				_buffers.Add( ResolveBytes( uri, $"buffer {i}", byteLength ) );
			}
		}

		byte[] ResolveBytes( string uri, string what, int expectedLength )
		{
			byte[] bytes;
			if ( uri.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ) )
			{
				int comma = uri.IndexOf( ',' );
				if ( comma < 0 || !uri.Contains( ";base64", StringComparison.OrdinalIgnoreCase ) )
					throw new InvalidDataException( $"glTF '{_display}' {what} has a data: URI that is not base64." );
				try { bytes = Convert.FromBase64String( uri.Substring( comma + 1 ) ); }
				catch ( FormatException ex ) { throw new InvalidDataException( $"glTF '{_display}' {what} has malformed base64: {ex.Message}" ); }
			}
			else
			{
				if ( _baseDir is null )
					throw new InvalidDataException( $"glTF '{_display}' {what} references external file '{uri}' but no source path was given to resolve it against." );
				if ( Path.IsPathFullyQualified( uri ) )
					throw new InvalidDataException( $"glTF '{_display}' {what} uses an absolute path '{uri}'; only document-relative references are portable." );
				string full = Path.GetFullPath( Path.Combine( _baseDir, uri.Replace( '/', Path.DirectorySeparatorChar ) ) );
				string root = _baseDir.EndsWith( Path.DirectorySeparatorChar ) ? _baseDir : _baseDir + Path.DirectorySeparatorChar;
				if ( !full.StartsWith( root, StringComparison.Ordinal ) )
					throw new InvalidDataException( $"glTF '{_display}' {what} escapes the document directory ('{uri}')." );
				if ( !File.Exists( full ) ) throw new FileNotFoundException( $"glTF '{_display}' {what} file not found: '{full}'." );
				bytes = File.ReadAllBytes( full );
			}
			if ( bytes.Length < expectedLength )
				throw new InvalidDataException( $"glTF '{_display}' {what} needs {expectedLength} bytes but resolved to {bytes.Length}." );
			return bytes;
		}

		void LoadViews()
		{
			var views = GetArray( _root, "bufferViews" );
			if ( views is null ) return;
			for ( int i = 0; i < views.Value.GetArrayLength(); i++ )
			{
				var view = views.Value[i];
				int buffer = GetInt( view, "buffer", -1 );
				int offset = GetInt( view, "byteOffset", 0 );
				int length = GetInt( view, "byteLength", -1 );
				int? stride = view.TryGetProperty( "byteStride", out var s ) ? s.GetInt32() : null;
				if ( buffer < 0 || buffer >= _buffers.Count ) throw new InvalidDataException( $"glTF '{_display}' bufferView {i} references missing buffer {buffer}." );
				if ( length < 0 ) throw new InvalidDataException( $"glTF '{_display}' bufferView {i} has no byteLength." );
				if ( offset < 0 || length < 0 || offset + length > _buffers[buffer].Length )
					throw new InvalidDataException( $"glTF '{_display}' bufferView {i} (offset {offset}, length {length}) overruns buffer {buffer} ({_buffers[buffer].Length} bytes)." );
				if ( stride.HasValue && ( stride.Value < 4 || stride.Value > 252 ) )
					throw new InvalidDataException( $"glTF '{_display}' bufferView {i} has byteStride {stride.Value}." );
				_views.Add( (buffer, offset, length, stride) );
			}
		}

		void LoadAccessors()
		{
			var accessors = GetArray( _root, "accessors" );
			if ( accessors is null ) return;
			for ( int i = 0; i < accessors.Value.GetArrayLength(); i++ )
			{
				var acc = accessors.Value[i];
				int view = GetInt( acc, "bufferView", -1 );
				int offset = GetInt( acc, "byteOffset", 0 );
				int componentType = GetInt( acc, "componentType", -1 );
				int count = GetInt( acc, "count", -1 );
				string type = GetString( acc, "type" ) ?? "";
				bool normalized = acc.TryGetProperty( "normalized", out var n ) && n.GetBoolean();
				bool sparse = acc.TryGetProperty( "sparse", out _ );
				if ( view < 0 || view >= _views.Count ) throw new InvalidDataException( $"glTF '{_display}' accessor {i} references missing bufferView {view}." );
				if ( componentType is not (5120 or 5121 or 5122 or 5123 or 5125 or 5126) )
					throw new InvalidDataException( $"glTF '{_display}' accessor {i} has unknown componentType {componentType}." );
				if ( count < 0 ) throw new InvalidDataException( $"glTF '{_display}' accessor {i} has no count." );
				if ( type is not ("SCALAR" or "VEC2" or "VEC3" or "VEC4" or "MAT2" or "MAT3" or "MAT4") )
					throw new InvalidDataException( $"glTF '{_display}' accessor {i} has unknown type '{type}'." );
				if ( sparse ) throw new NotSupportedException( $"glTF '{_display}' accessor {i} is sparse; sparse accessors are not supported." );
				_accessors.Add( new Accessor( view, offset, componentType, count, type, normalized, sparse ) );
			}
		}

		void LoadMaterials()
		{
			var materials = GetArray( _root, "materials" );
			if ( materials is null ) return;
			var images = LoadImages();
			var textures = GetArray( _root, "textures" );
			for ( int i = 0; i < materials.Value.GetArrayLength(); i++ )
			{
				var mat = materials.Value[i];
				string name = GetString( mat, "name" ) ?? ObjWriter.DefaultMaterialName( i );
				float[] baseColor = { 1f, 1f, 1f, 1f };
				float metallic = 1f, roughness = 1f;
				float[] emissive = { 0f, 0f, 0f };
				if ( mat.TryGetProperty( "pbrMetallicRoughness", out var pbr ) )
				{
					if ( pbr.TryGetProperty( "baseColorFactor", out var bcf ) ) baseColor = ReadFloatArray( bcf, 4, "baseColorFactor" );
					if ( pbr.TryGetProperty( "metallicFactor", out var mf ) ) metallic = (float)mf.GetDouble();
					if ( pbr.TryGetProperty( "roughnessFactor", out var rf ) ) roughness = (float)rf.GetDouble();
				}
				if ( mat.TryGetProperty( "emissiveFactor", out var ef ) ) emissive = ReadFloatArray( ef, 3, "emissiveFactor" );
				var textureNotes = new List<string>();
				if ( mat.TryGetProperty( "pbrMetallicRoughness", out var pbr2 ) )
				{
					NoteTexture( pbr2, "baseColorTexture", "base colour", images, textures, textureNotes );
					NoteTexture( pbr2, "metallicRoughnessTexture", "metallic-roughness", images, textures, textureNotes );
				}
				NoteTexture( mat, "normalTexture", "normal", images, textures, textureNotes );
				NoteTexture( mat, "occlusionTexture", "occlusion", images, textures, textureNotes );
				NoteTexture( mat, "emissiveTexture", "emissive", images, textures, textureNotes );
				_materials.Add( new ImportedMaterial( name, baseColor, metallic, roughness, emissive, textureNotes ) );
				_notes.Add( $"Material '{name}': baseColorFactor [{string.Join( ", ", baseColor.Select( v => v.ToString( "0.###" ) ) )}], metallic {metallic:0.###}, roughness {roughness:0.###}" +
					( textureNotes.Count == 0 ? "; no textures." : "; " + string.Join( "; ", textureNotes ) ) );
			}
		}

		void NoteTexture( JsonElement parent, string property, string label, List<(string Name, string Mime, byte[]? Bytes, string Note)> images,
			JsonElement? textures, List<string> textureNotes )
		{
			if ( !parent.TryGetProperty( property, out var texRef ) ) return;
			int texIndex = GetInt( texRef, "index", -1 );
			if ( texIndex < 0 || textures is null || texIndex >= textures.Value.GetArrayLength() )
			{
				textureNotes.Add( $"{label} texture references missing texture {texIndex}" );
				return;
			}
			var tex = textures.Value[texIndex];
			int imgIndex = GetInt( tex, "source", -1 );
			if ( imgIndex < 0 || imgIndex >= images.Count )
			{
				textureNotes.Add( $"{label} texture references missing image {imgIndex}" );
				return;
			}
			var img = images[imgIndex];
			textureNotes.Add( $"{label} texture '{img.Name}' ({img.Mime}): {img.Note}" );
		}

		List<(string Name, string Mime, byte[]? Bytes, string Note)> LoadImages()
		{
			var result = new List<(string Name, string Mime, byte[]? Bytes, string Note)>();
			var images = GetArray( _root, "images" );
			if ( images is null ) return result;
			for ( int i = 0; i < images.Value.GetArrayLength(); i++ )
			{
				var img = images.Value[i];
				string name = GetString( img, "name" ) ?? $"image_{i}";
				string mime = GetString( img, "mimeType" ) ?? "";
				byte[]? bytes = null;
				string note;
				if ( img.TryGetProperty( "bufferView", out var bv ) )
				{
					int viewIndex = bv.GetInt32();
					if ( viewIndex < 0 || viewIndex >= _views.Count )
						throw new InvalidDataException( $"glTF '{_display}' image {i} references missing bufferView {viewIndex}." );
					var (buffer, offset, length, _) = _views[viewIndex];
					bytes = new byte[length];
					Array.Copy( _buffers[buffer], offset, bytes, 0, length );
					if ( string.IsNullOrEmpty( mime ) ) mime = "image/png";
				}
				else if ( img.TryGetProperty( "uri", out var uriProp ) )
				{
					string uri = uriProp.GetString() ?? "";
					if ( uri.StartsWith( "data:", StringComparison.OrdinalIgnoreCase ) )
					{
						if ( string.IsNullOrEmpty( mime ) )
						{
							int semi = uri.IndexOf( ';' );
							if ( semi > 5 ) mime = uri.Substring( 5, semi - 5 );
						}
						try { bytes = ResolveBytes( uri, $"image {i}", 0 ); }
						catch ( Exception ex ) { bytes = null; note = $"unreadable ({ex.Message})"; result.Add( (name, mime, null, note) ); continue; }
					}
					else
					{
						if ( string.IsNullOrEmpty( mime ) ) mime = Path.GetExtension( uri ).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => "" };
						try { bytes = ResolveBytes( uri, $"image {i}", 0 ); }
						catch ( Exception ex ) { bytes = null; note = $"unreadable ({ex.Message})"; result.Add( (name, mime, null, note) ); continue; }
					}
				}
				else
				{
					result.Add( (name, mime, null, "no bufferView or uri") );
					continue;
				}
				if ( bytes is null )
				{
					result.Add( (name, mime, null, "empty") );
					continue;
				}
				if ( mime == "image/png" )
				{
					try
					{
						var (rgba, w, h) = PngReader.Read( bytes );
						note = $"PNG {w}x{h}, decoded OK";
					}
					catch ( Exception ex ) { note = $"PNG decode failed ({ex.Message})"; }
				}
				else if ( mime == "image/jpeg" )
				{
					note = $"JPEG {bytes.Length} bytes, not decoded (no JPEG decoder in the kernel)";
				}
				else
				{
					note = $"{bytes.Length} bytes, mime '{mime}' not decoded";
				}
				result.Add( (name, mime, bytes, note) );
			}
			return result;
		}

		float[] ReadFloatAccessor( int accessorIndex, int width, string what )
		{
			if ( accessorIndex < 0 || accessorIndex >= _accessors.Count )
				throw new InvalidDataException( $"glTF '{_display}' references missing accessor {accessorIndex} for {what}." );
			var acc = _accessors[accessorIndex];
			if ( acc.ComponentType != 5126 )
				throw new NotSupportedException( $"glTF '{_display}' {what} uses componentType {acc.ComponentType}; only float32 (5126) is supported." );
			if ( acc.Normalized )
				throw new NotSupportedException( $"glTF '{_display}' {what} is normalized; only plain float32 is supported." );
			int components = TypeWidth( acc.Type );
			if ( components != width )
				throw new InvalidDataException( $"glTF '{_display}' {what} is {acc.Type} but {width} components were expected." );
			var (buffer, viewOffset, viewLength, stride) = _views[acc.View];
			int elementSize = 4 * width;
			int step = stride ?? elementSize;
			long need = (long)acc.Offset + (long)( acc.Count - 1 ) * step + elementSize;
			if ( acc.Count > 0 && need > viewLength )
				throw new InvalidDataException( $"glTF '{_display}' {what} accessor overruns its bufferView." );
			var data = _buffers[buffer];
			var values = new float[acc.Count * width];
			for ( int i = 0; i < acc.Count; i++ )
			{
				int at = viewOffset + acc.Offset + i * step;
				for ( int c = 0; c < width; c++ )
					values[i * width + c] = BitConverter.ToSingle( data, at + c * 4 );
			}
			return values;
		}

		Vec4[]? ReadColorAccessor( int accessorIndex, int vertexCount )
		{
			if ( accessorIndex < 0 || accessorIndex >= _accessors.Count )
				throw new InvalidDataException( $"glTF '{_display}' references missing accessor {accessorIndex} for COLOR_0." );
			var acc = _accessors[accessorIndex];
			int width = TypeWidth( acc.Type );
			if ( width is not (3 or 4) )
				throw new InvalidDataException( $"glTF '{_display}' COLOR_0 is {acc.Type}; VEC3 or VEC4 expected." );
			var (buffer, viewOffset, viewLength, stride) = _views[acc.View];
			var data = _buffers[buffer];
			var colors = new Vec4[acc.Count];
			if ( acc.ComponentType == 5126 )
			{
				int elementSize = 4 * width;
				int step = stride ?? elementSize;
				long need = (long)acc.Offset + (long)( acc.Count - 1 ) * step + elementSize;
				if ( acc.Count > 0 && need > viewLength ) throw new InvalidDataException( $"glTF '{_display}' COLOR_0 accessor overruns its bufferView." );
				for ( int i = 0; i < acc.Count; i++ )
				{
					int at = viewOffset + acc.Offset + i * step;
					float r = BitConverter.ToSingle( data, at );
					float g = BitConverter.ToSingle( data, at + 4 );
					float b = BitConverter.ToSingle( data, at + 8 );
					float a = width == 4 ? BitConverter.ToSingle( data, at + 12 ) : 1f;
					colors[i] = new Vec4( r, g, b, a );
				}
			}
			else if ( ( acc.ComponentType == 5121 || acc.ComponentType == 5123 ) && acc.Normalized )
			{
				int size = acc.ComponentType == 5121 ? 1 : 2;
				float max = size == 1 ? 255f : 65535f;
				int elementSize = size * width;
				int step = stride ?? elementSize;
				long need = (long)acc.Offset + (long)( acc.Count - 1 ) * step + elementSize;
				if ( acc.Count > 0 && need > viewLength ) throw new InvalidDataException( $"glTF '{_display}' COLOR_0 accessor overruns its bufferView." );
				for ( int i = 0; i < acc.Count; i++ )
				{
					int at = viewOffset + acc.Offset + i * step;
					float Read( int c ) => size == 1 ? data[at + c] / max : BitConverter.ToUInt16( data, at + c * 2 ) / max;
					colors[i] = new Vec4( Read( 0 ), Read( 1 ), Read( 2 ), width == 4 ? Read( 3 ) : 1f );
				}
			}
			else
			{
				_notes.Add( "COLOR_0 uses an unsupported component type; vertex colours were ignored." );
				return null;
			}
			if ( colors.Length != vertexCount )
				throw new InvalidDataException( $"glTF '{_display}' COLOR_0 has {colors.Length} values for {vertexCount} vertices." );
			return colors;
		}

		int[] ReadIndexAccessor( int accessorIndex )
		{
			if ( accessorIndex < 0 || accessorIndex >= _accessors.Count )
				throw new InvalidDataException( $"glTF '{_display}' references missing accessor {accessorIndex} for indices." );
			var acc = _accessors[accessorIndex];
			if ( acc.Type != "SCALAR" ) throw new InvalidDataException( $"glTF '{_display}' indices are {acc.Type}; SCALAR expected." );
			int size = acc.ComponentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException( $"glTF '{_display}' indices use componentType {acc.ComponentType}; only uint8/uint16/uint32 are supported." ) };
			var (buffer, viewOffset, viewLength, stride) = _views[acc.View];
			int step = stride ?? size;
			long need = (long)acc.Offset + (long)( acc.Count - 1 ) * step + size;
			if ( acc.Count > 0 && need > viewLength ) throw new InvalidDataException( $"glTF '{_display}' indices accessor overruns its bufferView." );
			var data = _buffers[buffer];
			var indices = new int[acc.Count];
			for ( int i = 0; i < acc.Count; i++ )
			{
				int at = viewOffset + acc.Offset + i * step;
				indices[i] = size switch
				{
					1 => data[at],
					2 => BitConverter.ToUInt16( data, at ),
					_ => (int)BitConverter.ToUInt32( data, at ),
				};
			}
			return indices;
		}

		static int TypeWidth( string type ) => type switch
		{
			"SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => 0,
		};

		static float[] ReadFloatArray( JsonElement element, int count, string what )
		{
			if ( element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count )
				throw new InvalidDataException( $"glTF value '{what}' must be an array of {count} numbers." );
			var values = new float[count];
			int i = 0;
			foreach ( var item in element.EnumerateArray() ) values[i++] = (float)item.GetDouble();
			return values;
		}

		static JsonElement? GetArray( JsonElement parent, string property )
		{
			if ( parent.ValueKind != JsonValueKind.Object ) return null;
			if ( !parent.TryGetProperty( property, out var value ) ) return null;
			if ( value.ValueKind != JsonValueKind.Array ) throw new InvalidDataException( $"glTF property '{property}' must be an array." );
			return value;
		}

		static JsonElement? GetObject( JsonElement parent, string property )
		{
			if ( parent.ValueKind != JsonValueKind.Object ) return null;
			if ( !parent.TryGetProperty( property, out var value ) ) return null;
			if ( value.ValueKind != JsonValueKind.Object ) throw new InvalidDataException( $"glTF property '{property}' must be an object." );
			return value;
		}

		static string? GetString( JsonElement parent, string property )
		{
			if ( parent.ValueKind != JsonValueKind.Object ) return null;
			if ( !parent.TryGetProperty( property, out var value ) ) return null;
			if ( value.ValueKind != JsonValueKind.String ) throw new InvalidDataException( $"glTF property '{property}' must be a string." );
			return value.GetString();
		}

		static string? GetString( JsonElement parent, string objectProperty, string property )
		{
			var obj = GetObject( parent, objectProperty );
			return obj is null ? null : GetString( obj.Value, property );
		}

		static int GetInt( JsonElement parent, string property, int fallback )
		{
			if ( parent.ValueKind != JsonValueKind.Object ) return fallback;
			if ( !parent.TryGetProperty( property, out var value ) ) return fallback;
			if ( value.ValueKind != JsonValueKind.Number ) throw new InvalidDataException( $"glTF property '{property}' must be a number." );
			return value.GetInt32();
		}

		static void RequireObject( JsonElement element, string what )
		{
			if ( element.ValueKind != JsonValueKind.Object ) throw new InvalidDataException( $"glTF {what} must be a JSON object." );
		}
	}
}
