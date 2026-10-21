#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Effigy;

namespace Marionette.Desktop;

public enum BoneMergePolicy
{
	None = 0,
	ReconcileBindPose = 1
}

public enum AuthoringFrameAdapter
{
	None = 0,
	BlenderCitizenInverted = 1 // P(model) = (-p.y, p.x, p.z)
}

/// <summary>
/// Authoring options for source documents (e.g. import scale, coordinate conventions).
/// Ephemeral caches, runtime poses, and GL handles must stay out of here.
/// </summary>
public sealed record SourceDocumentOptions
{
	public float Scale { get; init; } = 1.0f;
	public string? AuthoringTool { get; init; }
	public Dictionary<string, string>? Metadata { get; init; }

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>
/// References an underlying source document (.effigy or .fbx) via an AssetReference.
/// Does not replace the underlying format or modify user assets without explicit policy.
/// </summary>
public sealed record SourceDocument
{
	public string Id { get; init; } = "";
	public AssetReference Asset { get; init; } = null!;
	public DocumentSourceKind Format { get; init; } = DocumentSourceKind.Effigy;
	public SourceDocumentOptions? Options { get; init; }

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }

	public SourceDocument() { }

	public SourceDocument( string id, AssetReference asset, DocumentSourceKind? format = null, SourceDocumentOptions? options = null )
	{
		Id = id ?? throw new ArgumentNullException( nameof( id ) );
		Asset = asset ?? throw new ArgumentNullException( nameof( asset ) );
		Format = format ?? InferFormat( asset.Path );
		Options = options;
	}

	public static DocumentSourceKind InferFormat( string path )
	{
		if ( path.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) )
			return DocumentSourceKind.Fbx;
		return DocumentSourceKind.Effigy;
	}
}

/// <summary>
/// References an animation clip (.riganim) or control rig via an AssetReference.
/// </summary>
public sealed record ProjectClip
{
	public string Id { get; init; } = "";
	public string? Name { get; init; }
	public AssetReference Asset { get; init; } = null!;
	public string? TargetRigId { get; init; }
	public float FrameRate { get; init; } = 30f;

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }

	public ProjectClip() { }

	public ProjectClip( string id, AssetReference asset, string? targetRigId = null, float frameRate = 30f, string? name = null )
	{
		Id = id ?? throw new ArgumentNullException( nameof( id ) );
		Asset = asset ?? throw new ArgumentNullException( nameof( asset ) );
		TargetRigId = targetRigId;
		FrameRate = frameRate;
		Name = name ?? id;
	}
}

/// <summary>
/// Data-driven material override record. Carries stable identity, target slot,
/// and either a .vmat AssetReference or an inline MaterialDescriptor.
/// </summary>
public sealed record MaterialOverrideRecord
{
	public string Id { get; init; } = "";
	public string SlotName { get; init; } = "";
	public int? SlotIndex { get; init; }
	public AssetReference? MaterialAsset { get; init; }
	public MaterialDescriptor? Descriptor { get; init; }
	public bool Required { get; init; }

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }

	public MaterialOverrideRecord() { }

	public MaterialOverrideRecord( string id, string slotName, AssetReference materialAsset, bool required = false )
	{
		Id = id ?? throw new ArgumentNullException( nameof( id ) );
		SlotName = slotName ?? throw new ArgumentNullException( nameof( slotName ) );
		MaterialAsset = materialAsset ?? throw new ArgumentNullException( nameof( materialAsset ) );
		Required = required;
	}

	public MaterialOverrideRecord( string id, string slotName, MaterialDescriptor descriptor, bool required = false )
	{
		Id = id ?? throw new ArgumentNullException( nameof( id ) );
		SlotName = slotName ?? throw new ArgumentNullException( nameof( slotName ) );
		Descriptor = descriptor ?? throw new ArgumentNullException( nameof( descriptor ) );
		Required = required;
	}

	public MaterialOverrideRecord( string id, int slotIndex, MaterialDescriptor descriptor, bool required = false )
	{
		Id = id ?? throw new ArgumentNullException( nameof( id ) );
		SlotIndex = slotIndex;
		Descriptor = descriptor ?? throw new ArgumentNullException( nameof( descriptor ) );
		Required = required;
	}

	public void Apply( MaterialResolver resolver )
	{
		ArgumentNullException.ThrowIfNull( resolver );
		if ( MaterialAsset != null )
		{
			if ( !string.IsNullOrEmpty( SlotName ) )
				resolver.SetOverride( SlotName, MaterialAsset, Required );
		}
		else if ( Descriptor != null )
		{
			if ( SlotIndex.HasValue )
				resolver.SetOverride( SlotIndex.Value, Descriptor, Required );
			else if ( !string.IsNullOrEmpty( SlotName ) )
				resolver.SetOverride( SlotName, Descriptor, Required );
		}
	}
}

/// <summary>
/// Defines a part in a data-driven source assembly with parent/child relationships,
/// local offsets, piece/bone filtering rules, and bone merge / frame adapter policies.
/// </summary>
public sealed record AssemblyPart
{
	public string Id { get; init; } = "";
	public string Name { get; init; } = "";
	public string SourceDocumentId { get; init; } = "";
	public string? ParentPartId { get; init; }
	public Vec3 LocalOffset { get; init; } = Vec3.Zero;
	public Quat LocalRotation { get; init; } = Quat.Identity;
	public Vec3 LocalScale { get; init; } = Vec3.One;

	public List<string> IncludePieces { get; init; } = new();
	public List<string> ExcludePieces { get; init; } = new();
	public List<string> IncludeBones { get; init; } = new();
	public List<string> ExcludeBones { get; init; } = new();

	public BoneMergePolicy BoneMerge { get; init; } = BoneMergePolicy.None;
	public AuthoringFrameAdapter FrameAdapter { get; init; } = AuthoringFrameAdapter.None;
	public Dictionary<string, string> MaterialRemap { get; init; } = new( StringComparer.OrdinalIgnoreCase );

	public bool Enabled { get; init; } = true;

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }

	public AssemblyPart() { }

	public AssemblyPart( string id, string sourceDocumentId, string? parentPartId = null, string? name = null )
	{
		Id = id ?? throw new ArgumentNullException( nameof( id ) );
		SourceDocumentId = sourceDocumentId ?? throw new ArgumentNullException( nameof( sourceDocumentId ) );
		ParentPartId = parentPartId;
		Name = name ?? id;
	}
}

public sealed class Vec3JsonConverter : JsonConverter<Vec3>
{
	public override Vec3 Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
	{
		if ( reader.TokenType == JsonTokenType.StartArray )
		{
			reader.Read(); float x = reader.GetSingle();
			reader.Read(); float y = reader.GetSingle();
			reader.Read(); float z = reader.GetSingle();
			reader.Read();
			if ( reader.TokenType != JsonTokenType.EndArray )
				throw new JsonException( "Expected end of array for Vec3." );
			return new Vec3( x, y, z );
		}
		if ( reader.TokenType == JsonTokenType.StartObject )
		{
			float x = 0, y = 0, z = 0;
			while ( reader.Read() )
			{
				if ( reader.TokenType == JsonTokenType.EndObject )
					return new Vec3( x, y, z );
				if ( reader.TokenType == JsonTokenType.PropertyName )
				{
					string prop = reader.GetString() ?? "";
					reader.Read();
					float val = reader.GetSingle();
					if ( string.Equals( prop, "x", StringComparison.OrdinalIgnoreCase ) ) x = val;
					else if ( string.Equals( prop, "y", StringComparison.OrdinalIgnoreCase ) ) y = val;
					else if ( string.Equals( prop, "z", StringComparison.OrdinalIgnoreCase ) ) z = val;
				}
			}
			throw new JsonException( "Unexpected end of JSON while reading Vec3." );
		}
		throw new JsonException( $"Unexpected token {reader.TokenType} for Vec3." );
	}

	public override void Write( Utf8JsonWriter writer, Vec3 value, JsonSerializerOptions options )
	{
		writer.WriteStartObject();
		writer.WriteNumber( "x", value.x );
		writer.WriteNumber( "y", value.y );
		writer.WriteNumber( "z", value.z );
		writer.WriteEndObject();
	}
}

public sealed class QuatJsonConverter : JsonConverter<Quat>
{
	public override Quat Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
	{
		if ( reader.TokenType == JsonTokenType.StartArray )
		{
			reader.Read(); float x = reader.GetSingle();
			reader.Read(); float y = reader.GetSingle();
			reader.Read(); float z = reader.GetSingle();
			reader.Read(); float w = reader.GetSingle();
			reader.Read();
			if ( reader.TokenType != JsonTokenType.EndArray )
				throw new JsonException( "Expected end of array for Quat." );
			return new Quat( x, y, z, w );
		}
		if ( reader.TokenType == JsonTokenType.StartObject )
		{
			float x = 0, y = 0, z = 0, w = 1;
			while ( reader.Read() )
			{
				if ( reader.TokenType == JsonTokenType.EndObject )
					return new Quat( x, y, z, w );
				if ( reader.TokenType == JsonTokenType.PropertyName )
				{
					string prop = reader.GetString() ?? "";
					reader.Read();
					float val = reader.GetSingle();
					if ( string.Equals( prop, "x", StringComparison.OrdinalIgnoreCase ) ) x = val;
					else if ( string.Equals( prop, "y", StringComparison.OrdinalIgnoreCase ) ) y = val;
					else if ( string.Equals( prop, "z", StringComparison.OrdinalIgnoreCase ) ) z = val;
					else if ( string.Equals( prop, "w", StringComparison.OrdinalIgnoreCase ) ) w = val;
				}
			}
			throw new JsonException( "Unexpected end of JSON while reading Quat." );
		}
		throw new JsonException( $"Unexpected token {reader.TokenType} for Quat." );
	}

	public override void Write( Utf8JsonWriter writer, Quat value, JsonSerializerOptions options )
	{
		writer.WriteStartObject();
		writer.WriteNumber( "x", value.x );
		writer.WriteNumber( "y", value.y );
		writer.WriteNumber( "z", value.z );
		writer.WriteNumber( "w", value.w );
		writer.WriteEndObject();
	}
}

public sealed class Vec4JsonConverter : JsonConverter<Vec4>
{
	public override Vec4 Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
	{
		if ( reader.TokenType == JsonTokenType.StartArray )
		{
			reader.Read(); float x = reader.GetSingle();
			reader.Read(); float y = reader.GetSingle();
			reader.Read(); float z = reader.GetSingle();
			reader.Read(); float w = reader.GetSingle();
			reader.Read();
			if ( reader.TokenType != JsonTokenType.EndArray )
				throw new JsonException( "Expected end of array for Vec4." );
			return new Vec4( x, y, z, w );
		}
		if ( reader.TokenType == JsonTokenType.StartObject )
		{
			float x = 0, y = 0, z = 0, w = 1;
			while ( reader.Read() )
			{
				if ( reader.TokenType == JsonTokenType.EndObject )
					return new Vec4( x, y, z, w );
				if ( reader.TokenType == JsonTokenType.PropertyName )
				{
					string prop = reader.GetString() ?? "";
					reader.Read();
					float val = reader.GetSingle();
					if ( string.Equals( prop, "x", StringComparison.OrdinalIgnoreCase ) || string.Equals( prop, "r", StringComparison.OrdinalIgnoreCase ) ) x = val;
					else if ( string.Equals( prop, "y", StringComparison.OrdinalIgnoreCase ) || string.Equals( prop, "g", StringComparison.OrdinalIgnoreCase ) ) y = val;
					else if ( string.Equals( prop, "z", StringComparison.OrdinalIgnoreCase ) || string.Equals( prop, "b", StringComparison.OrdinalIgnoreCase ) ) z = val;
					else if ( string.Equals( prop, "w", StringComparison.OrdinalIgnoreCase ) || string.Equals( prop, "a", StringComparison.OrdinalIgnoreCase ) ) w = val;
				}
			}
			throw new JsonException( "Unexpected end of JSON while reading Vec4." );
		}
		throw new JsonException( $"Unexpected token {reader.TokenType} for Vec4." );
	}

	public override void Write( Utf8JsonWriter writer, Vec4 value, JsonSerializerOptions options )
	{
		writer.WriteStartObject();
		writer.WriteNumber( "x", value.x );
		writer.WriteNumber( "y", value.y );
		writer.WriteNumber( "z", value.z );
		writer.WriteNumber( "w", value.w );
		writer.WriteEndObject();
	}
}

/// <summary>
/// Versioned project manifest for Geppetto Desktop.
/// Stores stable document, clip, assembly, and material override references.
/// Does not replace underlying formats (.effigy, .riganim, .fbx).
/// Keeps ephemeral caches, GL handles, world poses, and temporary selections out of the authored manifest.
/// </summary>
public sealed record DesktopProject
{
	public const int CurrentSchemaVersion = 1;
	public const string ProjectExtension = ".geppetto-project.json";
	public const string ProjectAltExtension = ".project.json";

	public int SchemaVersion { get; init; } = CurrentSchemaVersion;
	public string Id { get; init; } = "";
	public string Name { get; init; } = "";
	public string? ProjectRoot { get; init; }

	public List<SourceDocument> SourceDocuments { get; init; } = new();
	public List<ProjectClip> Clips { get; init; } = new();
	public List<AssemblyPart> AssemblyParts { get; init; } = new();
	public List<MaterialOverrideRecord> MaterialOverrides { get; init; } = new();

	public string? ActiveSourceDocumentId { get; init; }
	public string? ActiveClipId { get; init; }
	public string? ActiveAssemblyPartId { get; init; }

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? AdditionalData { get; set; }

	public static JsonSerializerOptions DefaultSerializerOptions { get; } = CreateDefaultSerializerOptions();
	private static readonly JsonSerializerOptions CompactSerializerOptions = CreateDefaultSerializerOptions( indented: false );

	public static JsonSerializerOptions CreateDefaultSerializerOptions( bool indented = true )
	{
		var options = new JsonSerializerOptions
		{
			WriteIndented = indented,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};
		options.Converters.Add( new JsonStringEnumConverter( JsonNamingPolicy.CamelCase ) );
		options.Converters.Add( new Vec3JsonConverter() );
		options.Converters.Add( new QuatJsonConverter() );
		options.Converters.Add( new Vec4JsonConverter() );
		return options;
	}

	/// <summary>
	/// Validate schema version, duplicate IDs, cyclic parents, and required fields.
	/// </summary>
	public void Validate()
	{
		if ( SchemaVersion <= 0 || SchemaVersion > CurrentSchemaVersion )
			throw new NotSupportedException( $"Project schema version {SchemaVersion} is unsupported. Maximum supported schema version is {CurrentSchemaVersion}." );

		if ( string.IsNullOrWhiteSpace( Id ) )
			throw new ArgumentException( "Project Id must not be empty or whitespace.", nameof( Id ) );

		// Validate SourceDocuments: non-empty IDs, no duplicates, valid asset references
		var docIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var doc in SourceDocuments )
		{
			if ( string.IsNullOrWhiteSpace( doc.Id ) )
				throw new ArgumentException( "Source document Id must not be empty or whitespace." );
			if ( !docIds.Add( doc.Id ) )
				throw new ArgumentException( $"Duplicate source document ID '{doc.Id}'." );
			if ( doc.Asset is null || string.IsNullOrWhiteSpace( doc.Asset.Path ) )
				throw new ArgumentException( $"Source document '{doc.Id}' must declare a valid AssetReference." );
		}

		// Validate Clips: non-empty IDs, no duplicates, valid asset references
		var clipIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var clip in Clips )
		{
			if ( string.IsNullOrWhiteSpace( clip.Id ) )
				throw new ArgumentException( "Clip Id must not be empty or whitespace." );
			if ( !clipIds.Add( clip.Id ) )
				throw new ArgumentException( $"Duplicate clip ID '{clip.Id}'." );
			if ( clip.Asset is null || string.IsNullOrWhiteSpace( clip.Asset.Path ) )
				throw new ArgumentException( $"Clip '{clip.Id}' must declare a valid AssetReference." );
		}

		// Validate MaterialOverrides: non-empty IDs, no duplicates, target slot specified
		var overrideIds = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var mo in MaterialOverrides )
		{
			if ( string.IsNullOrWhiteSpace( mo.Id ) )
				throw new ArgumentException( "Material override Id must not be empty or whitespace." );
			if ( !overrideIds.Add( mo.Id ) )
				throw new ArgumentException( $"Duplicate material override ID '{mo.Id}'." );
			if ( string.IsNullOrWhiteSpace( mo.SlotName ) && !mo.SlotIndex.HasValue )
				throw new ArgumentException( $"Material override '{mo.Id}' must specify either SlotName or SlotIndex." );
		}

		// Validate AssemblyParts: non-empty IDs, no duplicates, valid source document reference
		var partIds = new HashSet<string>( StringComparer.Ordinal );
		var partsById = new Dictionary<string, AssemblyPart>( StringComparer.Ordinal );
		foreach ( var part in AssemblyParts )
		{
			if ( string.IsNullOrWhiteSpace( part.Id ) )
				throw new ArgumentException( "Assembly part Id must not be empty or whitespace." );
			if ( !partIds.Add( part.Id ) )
				throw new ArgumentException( $"Duplicate assembly part ID '{part.Id}'." );
			if ( string.IsNullOrWhiteSpace( part.SourceDocumentId ) )
				throw new ArgumentException( $"Assembly part '{part.Id}' must specify a SourceDocumentId." );
			if ( !docIds.Contains( part.SourceDocumentId ) )
				throw new ArgumentException( $"Assembly part '{part.Id}' references non-existent source document '{part.SourceDocumentId}'." );
			partsById[part.Id] = part;
		}

		// Validate hierarchy cycles and parent references
		foreach ( var part in AssemblyParts )
		{
			if ( part.ParentPartId is null ) continue;

			if ( string.Equals( part.Id, part.ParentPartId, StringComparison.Ordinal ) )
				throw new InvalidOperationException( $"Assembly part '{part.Id}' cannot be its own parent." );

			if ( !partsById.ContainsKey( part.ParentPartId ) )
				throw new ArgumentException( $"Assembly part '{part.Id}' references non-existent parent '{part.ParentPartId}'." );

			var visited = new HashSet<string>( StringComparer.Ordinal ) { part.Id };
			var current = part;
			while ( current.ParentPartId != null )
			{
				if ( !partsById.TryGetValue( current.ParentPartId, out var parentPart ) )
					break;
				if ( !visited.Add( parentPart.Id ) )
					throw new InvalidOperationException( $"Cyclic parent hierarchy detected involving assembly part '{parentPart.Id}'." );
				current = parentPart;
			}
		}

		// Validate active selections if set
		if ( ActiveSourceDocumentId != null && !docIds.Contains( ActiveSourceDocumentId ) )
			throw new ArgumentException( $"ActiveSourceDocumentId '{ActiveSourceDocumentId}' does not match any source document." );
		if ( ActiveClipId != null && !clipIds.Contains( ActiveClipId ) )
			throw new ArgumentException( $"ActiveClipId '{ActiveClipId}' does not match any clip." );
		if ( ActiveAssemblyPartId != null && !partIds.Contains( ActiveAssemblyPartId ) )
			throw new ArgumentException( $"ActiveAssemblyPartId '{ActiveAssemblyPartId}' does not match any assembly part." );
	}

	/// <summary>
	/// Serialize project manifest to JSON.
	/// </summary>
	public string ToJson( bool indented = true, JsonSerializerOptions? options = null )
	{
		Validate();
		var opt = options ?? (indented ? DefaultSerializerOptions : CompactSerializerOptions);
		return JsonSerializer.Serialize( this, opt );
	}

	/// <summary>
	/// Deserialize project manifest from JSON and validate schema/integrity.
	/// </summary>
	public static DesktopProject FromJson( string json, JsonSerializerOptions? options = null )
	{
		ArgumentNullException.ThrowIfNull( json );
		var opt = options ?? DefaultSerializerOptions;
		var project = JsonSerializer.Deserialize<DesktopProject>( json, opt )
			?? throw new InvalidDataException( "Failed to deserialize DesktopProject from JSON: result was null." );
		project.Validate();
		return project;
	}

	/// <summary>
	/// Load project manifest from file and establish its project root.
	/// </summary>
	public static DesktopProject Load( string manifestPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( manifestPath );
		if ( !File.Exists( manifestPath ) )
			throw new FileNotFoundException( $"Project manifest file not found: {manifestPath}", manifestPath );

		string json = File.ReadAllText( manifestPath );
		var project = FromJson( json );
		if ( string.IsNullOrEmpty( project.ProjectRoot ) )
		{
			string? dir = Path.GetDirectoryName( Path.GetFullPath( manifestPath ) );
			project = project with { ProjectRoot = dir };
		}
		return project;
	}

	/// <summary>
	/// Atomically write manifest using same-directory temporary file and replace.
	/// </summary>
	public void Save( string manifestPath, bool atomic = true )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( manifestPath );
		Validate();

		string fullPath = Path.GetFullPath( manifestPath );
		string? dir = Path.GetDirectoryName( fullPath );
		if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
			Directory.CreateDirectory( dir );

		string json = ToJson( indented: true );

		if ( !atomic )
		{
			File.WriteAllText( fullPath, json, Encoding.UTF8 );
			return;
		}

		string tempPath = Path.Combine( dir ?? ".", $"{Path.GetFileName( fullPath )}.tmp.{Guid.NewGuid():N}" );
		try
		{
			using ( var stream = new FileStream( tempPath, FileMode.Create, FileAccess.Write, FileShare.None ) )
			using ( var writer = new StreamWriter( stream, Encoding.UTF8 ) )
			{
				writer.Write( json );
				writer.Flush();
				stream.Flush( true );
			}

			if ( OperatingSystem.IsWindows() )
			{
				File.Replace( tempPath, fullPath, null );
			}
			else
			{
				File.Move( tempPath, fullPath, overwrite: true );
			}
		}
		finally
		{
			if ( File.Exists( tempPath ) )
			{
				try { File.Delete( tempPath ); } catch { /* ignore temp cleanup errors */ }
			}
		}
	}

	/// <summary>
	/// Relocate project asset references to a new destination project root via AssetPathResolver.
	/// </summary>
	public DesktopProject Relocate( AssetPathResolver currentResolver, AssetPathResolver destinationResolver, bool allowExternal = false )
	{
		ArgumentNullException.ThrowIfNull( currentResolver );
		ArgumentNullException.ThrowIfNull( destinationResolver );

		var newDocs = new List<SourceDocument>( SourceDocuments.Count );
		foreach ( var doc in SourceDocuments )
		{
			var relocatedRef = currentResolver.RelocateReference( doc.Asset, destinationResolver, allowExternal );
			newDocs.Add( doc with { Asset = relocatedRef } );
		}

		var newClips = new List<ProjectClip>( Clips.Count );
		foreach ( var clip in Clips )
		{
			var relocatedRef = currentResolver.RelocateReference( clip.Asset, destinationResolver, allowExternal );
			newClips.Add( clip with { Asset = relocatedRef } );
		}

		var newOverrides = new List<MaterialOverrideRecord>( MaterialOverrides.Count );
		foreach ( var mo in MaterialOverrides )
		{
			AssetReference? relocatedAsset = mo.MaterialAsset != null
				? currentResolver.RelocateReference( mo.MaterialAsset, destinationResolver, allowExternal )
				: null;

			MaterialDescriptor? relocatedDescriptor = mo.Descriptor != null
				? RelocateMaterialDescriptor( mo.Descriptor, currentResolver, destinationResolver, allowExternal )
				: null;

			newOverrides.Add( mo with
			{
				MaterialAsset = relocatedAsset,
				Descriptor = relocatedDescriptor
			} );
		}

		return this with
		{
			ProjectRoot = destinationResolver.ProjectRoot,
			SourceDocuments = newDocs,
			Clips = newClips,
			MaterialOverrides = newOverrides
		};
	}

	public DesktopProject Relocate( string currentProjectRoot, string destinationProjectRoot, bool allowExternal = false ) =>
		Relocate( new AssetPathResolver( currentProjectRoot ), new AssetPathResolver( destinationProjectRoot ), allowExternal );

	private static MaterialDescriptor RelocateMaterialDescriptor(
		MaterialDescriptor descriptor,
		AssetPathResolver currentResolver,
		AssetPathResolver destinationResolver,
		bool allowExternal )
	{
		TextureBinding? RelocateBinding( TextureBinding? binding )
		{
			if ( binding is null ) return null;
			var relocated = currentResolver.RelocateReference( binding.Reference, destinationResolver, allowExternal );
			return binding with { Reference = relocated };
		}

		return descriptor with
		{
			BaseColorMap = RelocateBinding( descriptor.BaseColorMap ),
			NormalMap = RelocateBinding( descriptor.NormalMap ),
			RoughnessMap = RelocateBinding( descriptor.RoughnessMap ),
			MetallicMap = RelocateBinding( descriptor.MetallicMap ),
			EmissionMap = RelocateBinding( descriptor.EmissionMap ),
			AmbientOcclusionMap = RelocateBinding( descriptor.AmbientOcclusionMap )
		};
	}

	/// <summary>
	/// Apply material overrides configured in the manifest to a MaterialResolver.
	/// </summary>
	public void ApplyMaterialOverrides( MaterialResolver resolver )
	{
		ArgumentNullException.ThrowIfNull( resolver );
		foreach ( var mo in MaterialOverrides )
		{
			mo.Apply( resolver );
		}
	}

	/// <summary>
	/// Check if a piece should be included for the given assembly part based on its filtering rules.
	/// </summary>
	public static bool ShouldIncludePiece( AssemblyPart part, string pieceName )
	{
		ArgumentNullException.ThrowIfNull( part );
		ArgumentNullException.ThrowIfNull( pieceName );

		if ( part.ExcludePieces.Any( p => string.Equals( p, pieceName, StringComparison.Ordinal ) ) )
			return false;

		if ( part.IncludePieces.Count > 0 && !part.IncludePieces.Any( p => string.Equals( p, pieceName, StringComparison.Ordinal ) ) )
			return false;

		return true;
	}

	/// <summary>
	/// Transform point by the data-driven authoring frame adapter.
	/// BlenderCitizenInverted converts P(model) = (model.y, -model.x, model.z) * 2.54 into inverted: P = (-p.y, p.x, p.z).
	/// </summary>
	public static Vec3 TransformPoint( Vec3 p, AuthoringFrameAdapter adapter ) => adapter switch
	{
		AuthoringFrameAdapter.BlenderCitizenInverted => new Vec3( -p.y, p.x, p.z ),
		_ => p
	};

	/// <summary>
	/// Apply authoring frame adapter to all vertex positions of a PolyMesh.
	/// </summary>
	public static void ApplyFrameAdapter( PolyMesh mesh, AuthoringFrameAdapter adapter )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		if ( adapter == AuthoringFrameAdapter.None ) return;

		for ( int i = 0; i < mesh.Positions.Count; i++ )
		{
			mesh.Positions[i] = TransformPoint( mesh.Positions[i], adapter );
		}
	}

	/// <summary>
	/// Data-driven bone merge: reconciles each child vertex's bind position against target skeleton.
	/// </summary>
	public static void ApplyBoneMerge( FbxImport.Result childResult, Skeleton targetSkeleton, AssemblyPart part )
	{
		ArgumentNullException.ThrowIfNull( childResult );
		ArgumentNullException.ThrowIfNull( targetSkeleton );
		ArgumentNullException.ThrowIfNull( part );

		if ( part.BoneMerge != BoneMergePolicy.ReconcileBindPose ) return;
		if ( childResult.Skeleton is null || childResult.Skeleton.Count == 0 ) return;

		var bindCorrections = new Dictionary<string, Xform>( StringComparer.Ordinal );
		for ( int i = 0; i < childResult.Pieces.Count; i++ )
		{
			var piece = childResult.Pieces[i];
			if ( !ShouldIncludePiece( part, piece.Name ) ) continue;

			var weights = i < childResult.Weights.Count ? childResult.Weights[i] : null;
			if ( weights is null ) continue;

			for ( int v = 0; v < piece.Mesh.VertexCount; v++ )
			{
				var p = piece.Mesh.Positions[v];
				Vec3 merged = Vec3.Zero;
				float sum = 0;
				foreach ( var (boneName, weight) in weights[v] )
				{
					if ( !bindCorrections.TryGetValue( boneName, out var correction ) )
					{
						int source = childResult.Skeleton.IndexOf( boneName );
						int target = targetSkeleton.IndexOf( boneName );
						if ( source >= 0 && target >= 0 )
						{
							correction = targetSkeleton.WorldBind( target ) * childResult.Skeleton.WorldBind( source ).Inverse;
							bindCorrections[boneName] = correction;
						}
						else
						{
							correction = Xform.Identity;
							bindCorrections[boneName] = correction;
						}
					}
					merged += correction.TransformPoint( p ) * weight;
					sum += weight;
				}
				if ( sum > 0 )
					piece.Mesh.Positions[v] = merged / sum;
			}
		}
	}
}
