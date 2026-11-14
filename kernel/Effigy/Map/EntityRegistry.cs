using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// FGD-like entity registry (hammer-entity-registry-fgd-json).
///
/// Data-driven: entity classes (point vs brush), their keyvalues, spawnflags,
/// inputs, outputs and editor helpers are described by JSON files, never by
/// code. Loading a new registry JSON adds entities without code changes.
/// This is the native Geppetto Desktop equivalent of a Source FGD file:
/// Hammer cannot be used for a game without the right FGD, and Geppetto
/// needs the same class/keyvalue knowledge to place and validate entities.
///
/// JSON schema (schemaVersion 1):
/// <code>
/// {
///   "schemaVersion": 1,
///   "name": "source1-core",
///   "classes": [
///     {
///       "name": "func_door", "kind": "brush",
///       "description": "Sliding / rotating door.",
///       "baseClasses": [ "Targetname" ],
///       "keyvalues": [
///         { "name": "speed", "type": "float", "default": "100",
///           "description": "Movement speed in units per second." },
///         { "name": "spawnflags", "type": "int" }
///       ],
///       "spawnflags": [
///         { "value": 1, "name": "Starts Open", "description": "..." }
///       ],
///       "inputs":  [ { "name": "Open",  "argumentType": "void", "description": "..." } ],
///       "outputs": [ { "name": "OnFullyOpen", "argumentType": "void", "description": "..." } ],
///       "helpers": [ { "kind": "origin" }, { "kind": "angles" } ]
///     }
///   ]
/// }
/// </code>
///
/// Keyvalue types: string, int, float, colour (also spelled "color"),
/// angle, target, model, sound.
///
/// Implicit base keyvalues: every class accepts these without declaring
/// them (mirrors the FGD base classes Targetname / Origin / Angles):
/// "classname" (string), "targetname" (string), "parentname" (target),
/// "angles" (angle), "spawnflags" (int). Declaring one of them in JSON
/// with a different type is an InvalidRegistry error.
///
/// Validation is strict and named: an unknown class, an unknown keyvalue
/// or a mistyped value is rejected with an <see cref="EntityRegistryError"/>
/// whose <see cref="EntityRegistryError.Code"/> is one of
/// UnknownEntityClass, UnknownKeyValue, InvalidKeyValueType,
/// DuplicateClass, UnknownKeyValueType, InvalidEntityKind or
/// InvalidRegistry. The throwing helpers raise
/// <see cref="EntityRegistryException"/> carrying the same code.
/// No Desktop UI reads this registry yet; UI is deferred per the ticket.
/// </summary>
public enum EntityKind
{
	Point,
	Brush
}

/// <summary>Keyvalue value types, as named in FGD files and registry JSON.</summary>
public enum KeyValueType
{
	String,
	Int,
	Float,
	Colour,
	Angle,
	Target,
	Model,
	Sound
}

/// <summary>
/// Editor helper attached to an entity class (FGD helpers): what the
/// editor shows for placement/editing — an origin handle, an angles
/// handle, a colour picker, a target picker, or a sprite.
/// </summary>
public enum EntityHelperKind
{
	Origin,
	Angles,
	Colour,
	Target,
	Sprite
}

/// <summary>One declared keyvalue on an entity class.</summary>
public sealed class EntityKeyValue
{
	public string Name = "";
	public KeyValueType Type = KeyValueType.String;
	public string DefaultValue = "";
	public string Description = "";
	/// <summary>Allowed values, when the keyvalue is a closed choice list. Empty = free-form.</summary>
	public string[] Choices = Array.Empty<string>();
}

/// <summary>One spawnflag bit on an entity class.</summary>
public sealed class EntitySpawnflag
{
	public int Value;
	public string Name = "";
	public bool DefaultOn;
	public string Description = "";
}

/// <summary>One input an entity class accepts (FGD input).</summary>
public sealed class EntityInput
{
	public string Name = "";
	public string ArgumentType = "void";
	public string Description = "";
}

/// <summary>One output an entity class fires (FGD output).</summary>
public sealed class EntityOutput
{
	public string Name = "";
	public string ArgumentType = "void";
	public string Description = "";
}

/// <summary>One editor helper on an entity class.</summary>
public sealed class EntityHelper
{
	public EntityHelperKind Kind = EntityHelperKind.Origin;
	/// <summary>Keyvalue the helper edits (target/colour helpers). Empty when not applicable.</summary>
	public string Key = "";
	/// <summary>Sprite/material path (sprite helpers). Empty when not applicable.</summary>
	public string Path = "";
}

/// <summary>One entity class from a registry file.</summary>
public sealed class EntityClass
{
	public string Name = "";
	public EntityKind Kind = EntityKind.Point;
	public string Description = "";
	public string[] BaseClasses = Array.Empty<string>();
	public EntityKeyValue[] KeyValues = Array.Empty<EntityKeyValue>();
	public EntitySpawnflag[] Spawnflags = Array.Empty<EntitySpawnflag>();
	public EntityInput[] Inputs = Array.Empty<EntityInput>();
	public EntityOutput[] Outputs = Array.Empty<EntityOutput>();
	public EntityHelper[] Helpers = Array.Empty<EntityHelper>();

	/// <summary>Declared keyvalue by name (case-insensitive), or null. Implicit base keyvalues are not listed here; see <see cref="EntityRegistry.ImplicitKeyValue"/>.</summary>
	public EntityKeyValue FindKeyValue( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		foreach ( var kv in KeyValues )
			if ( string.Equals( kv.Name, name, StringComparison.OrdinalIgnoreCase ) ) return kv;
		return null;
	}

	public EntityInput FindInput( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		foreach ( var input in Inputs )
			if ( string.Equals( input.Name, name, StringComparison.OrdinalIgnoreCase ) ) return input;
		return null;
	}

	public EntityOutput FindOutput( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		foreach ( var output in Outputs )
			if ( string.Equals( output.Name, name, StringComparison.OrdinalIgnoreCase ) ) return output;
		return null;
	}

	public bool HasHelper( EntityHelperKind kind )
	{
		foreach ( var helper in Helpers )
			if ( helper.Kind == kind ) return true;
		return false;
	}
}

/// <summary>A single named validation failure. <see cref="Code"/> is the stable machine-readable name.</summary>
public sealed class EntityRegistryError
{
	public string Code = "";
	public string Message = "";
	public string ClassName = "";
	public string KeyName = "";

	public EntityRegistryError() { }
	public EntityRegistryError( string code, string message, string className = "", string keyName = "" )
	{
		Code = code; Message = message; ClassName = className; KeyName = keyName;
	}

	public override string ToString() => string.IsNullOrEmpty( KeyName )
		? $"[{Code}] {Message}"
		: $"[{Code}] {Message} (class '{ClassName}', key '{KeyName}')";
}

/// <summary>Thrown by the strict (Check*) registry APIs. <see cref="Code"/> is the named error.</summary>
public sealed class EntityRegistryException : Exception
{
	public string Code = "";
	public string ClassName = "";
	public string KeyName = "";

	public EntityRegistryException( string code, string message, string className = "", string keyName = "" )
		: base( message ) { Code = code; ClassName = className; KeyName = keyName; }
}

/// <summary>
/// FGD-like entity registry: schema + loader + validation, class lookup by
/// name, keyvalue type checking. Load a registry JSON file (or several, via
/// <see cref="LoadAdditional"/> / <see cref="Merge"/>); a new file adds
/// entities without code changes. Class names are matched case-insensitively.
/// </summary>
public sealed class EntityRegistry
{
	/// <summary>Named error codes. Keep in sync with the class doc above.</summary>
	public const string CodeUnknownEntityClass = "UnknownEntityClass";
	public const string CodeUnknownKeyValue = "UnknownKeyValue";
	public const string CodeInvalidKeyValueType = "InvalidKeyValueType";
	public const string CodeDuplicateClass = "DuplicateClass";
	public const string CodeUnknownKeyValueType = "UnknownKeyValueType";
	public const string CodeInvalidEntityKind = "InvalidEntityKind";
	public const string CodeInvalidRegistry = "InvalidRegistry";

	/// <summary>Registry schema version this loader understands.</summary>
	public const int CurrentSchemaVersion = 1;

	/// <summary>Keyvalues every class accepts without declaring them (FGD base classes).</summary>
	public static readonly IReadOnlyDictionary<string, KeyValueType> ImplicitKeyValues =
		new Dictionary<string, KeyValueType>( StringComparer.OrdinalIgnoreCase )
		{
			{ "classname", KeyValueType.String },
			{ "targetname", KeyValueType.String },
			{ "parentname", KeyValueType.Target },
			{ "angles", KeyValueType.Angle },
			{ "spawnflags", KeyValueType.Int },
		};

	static readonly string[] ModelExtensions = { ".mdl", ".vmdl", ".glb", ".obj", ".fbx", ".effigy" };
	static readonly string[] SoundExtensions = { ".wav", ".mp3", ".ogg", ".flac" };

	readonly Dictionary<string, EntityClass> _classes = new( StringComparer.OrdinalIgnoreCase );

	public string Name = "";
	public int SchemaVersion = CurrentSchemaVersion;
	public string SourcePath = "";

	public IReadOnlyList<EntityClass> Classes => _classes.Values.OrderBy( c => c.Name, StringComparer.OrdinalIgnoreCase ).ToArray();
	public int Count => _classes.Count;

	// ---- Loading ----

	/// <summary>Load a registry from a JSON file on disk.</summary>
	public static EntityRegistry Load( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new EntityRegistryException( CodeInvalidRegistry, "Registry path is empty." );
		string json;
		try { json = File.ReadAllText( path ); }
		catch ( Exception ex ) { throw new EntityRegistryException( CodeInvalidRegistry, $"Cannot read registry file '{path}': {ex.Message}" ); }
		var registry = LoadFromJson( json, path );
		registry.SourcePath = path;
		return registry;
	}

	/// <summary>Load a registry from JSON text. <paramref name="sourceName"/> is only used in error messages.</summary>
	public static EntityRegistry LoadFromJson( string json, string sourceName = "" )
	{
		var registry = new EntityRegistry();
		registry.LoadJsonInto( json, sourceName );
		return registry;
	}

	/// <summary>
	/// Load every *.json registry file in a directory (sorted by file name) and merge them.
	/// A malformed file or a duplicate class fails the whole load: a registry directory
	/// must be internally consistent, never silently partial.
	/// </summary>
	public static EntityRegistry LoadDirectory( string directoryPath, string searchPattern = "*.json" )
	{
		if ( string.IsNullOrWhiteSpace( directoryPath ) || !Directory.Exists( directoryPath ) )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Registry directory not found: '{directoryPath}'." );
		var registry = new EntityRegistry { Name = Path.GetFileName( directoryPath ), SourcePath = directoryPath };
		string[] files = Directory.EnumerateFiles( directoryPath, searchPattern, SearchOption.TopDirectoryOnly )
			.OrderBy( f => f, StringComparer.OrdinalIgnoreCase ).ToArray();
		if ( files.Length == 0 )
			throw new EntityRegistryException( CodeInvalidRegistry, $"No registry files ('{searchPattern}') in directory '{directoryPath}'." );
		foreach ( string file in files ) registry.LoadAdditional( file );
		return registry;
	}

	/// <summary>Load an additional registry file into this registry. Duplicate class names are rejected (DuplicateClass).</summary>
	public void LoadAdditional( string path )
	{
		Merge( Load( path ) );
	}

	/// <summary>Merge another registry into this one. Duplicate class names are rejected (DuplicateClass).</summary>
	public void Merge( EntityRegistry other )
	{
		if ( other is null ) throw new ArgumentNullException( nameof( other ) );
		foreach ( var cls in other._classes.Values ) Add( cls );
		if ( string.IsNullOrEmpty( Name ) ) Name = other.Name;
	}

	/// <summary>Add one class. Duplicate class names are rejected (DuplicateClass).</summary>
	public void Add( EntityClass cls )
	{
		if ( cls is null ) throw new ArgumentNullException( nameof( cls ) );
		if ( string.IsNullOrWhiteSpace( cls.Name ) )
			throw new EntityRegistryException( CodeInvalidRegistry, "Entity class has no name." );
		if ( _classes.ContainsKey( cls.Name ) )
			throw new EntityRegistryException( CodeDuplicateClass, $"Duplicate entity class '{cls.Name}'.", cls.Name );
		_classes.Add( cls.Name, cls );
	}

	// ---- Lookup ----

	/// <summary>Class by name (case-insensitive), or null when unknown.</summary>
	public EntityClass Find( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		return _classes.TryGetValue( name.Trim(), out var cls ) ? cls : null;
	}

	public bool TryGet( string name, out EntityClass cls ) => ( cls = Find( name ) ) is not null;

	/// <summary>Class by name, or throws <see cref="EntityRegistryException"/> with code UnknownEntityClass.</summary>
	public EntityClass Get( string name )
	{
		var cls = Find( name );
		if ( cls is null ) throw new EntityRegistryException( CodeUnknownEntityClass, $"Unknown entity class '{name}'.", string.IsNullOrWhiteSpace( name ) ? "" : name.Trim() );
		return cls;
	}

	public bool Contains( string name ) => Find( name ) is not null;

	// ---- Validation ----

	/// <summary>
	/// Validate one entity instance: unknown class, unknown key or mistyped
	/// value each produce a named <see cref="EntityRegistryError"/>.
	/// Returns an empty list when the entity is valid.
	/// </summary>
	public IReadOnlyList<EntityRegistryError> ValidateEntity( string className, IReadOnlyDictionary<string, string> keyValues )
	{
		var errors = new List<EntityRegistryError>();
		var cls = Find( className );
		if ( cls is null )
		{
			errors.Add( new EntityRegistryError( CodeUnknownEntityClass, $"Unknown entity class '{className}'.", className ?? "" ) );
			return errors;
		}
		if ( keyValues is null ) return errors;
		foreach ( var pair in keyValues )
		{
			var error = ValidateKeyValue( cls, pair.Key, pair.Value );
			if ( error is not null ) errors.Add( error );
		}
		return errors;
	}

	/// <summary>
	/// Validate a serialized entity instance: {"classname": "...", "keyvalues": {"speed": "100"}}.
	/// Malformed JSON / missing classname is an InvalidRegistry error, not a validation pass.
	/// </summary>
	public IReadOnlyList<EntityRegistryError> ValidateEntityJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			return new[] { new EntityRegistryError( CodeInvalidRegistry, "Entity JSON is empty." ) };
		try
		{
			using var doc = JsonDocument.Parse( json );
			var root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object )
				return new[] { new EntityRegistryError( CodeInvalidRegistry, "Entity JSON must be an object." ) };
			if ( !root.TryGetProperty( "classname", out var classEl ) || classEl.ValueKind != JsonValueKind.String
				|| string.IsNullOrWhiteSpace( classEl.GetString() ) )
				return new[] { new EntityRegistryError( CodeInvalidRegistry, "Entity JSON has no 'classname' string." ) };
			var keyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			if ( root.TryGetProperty( "keyvalues", out var kvEl ) )
			{
				if ( kvEl.ValueKind != JsonValueKind.Object )
					return new[] { new EntityRegistryError( CodeInvalidRegistry, "Entity 'keyvalues' must be an object.", classEl.GetString() ) };
				foreach ( var prop in kvEl.EnumerateObject() )
					keyValues[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
			}
			return ValidateEntity( classEl.GetString(), keyValues );
		}
		catch ( JsonException ex )
		{
			return new[] { new EntityRegistryError( CodeInvalidRegistry, $"Entity JSON is malformed: {ex.Message}" ) };
		}
	}

	/// <summary>Validate one keyvalue on a class. Returns null when valid.</summary>
	public EntityRegistryError ValidateKeyValue( string className, string keyName, string value )
	{
		var cls = Find( className );
		if ( cls is null ) return new EntityRegistryError( CodeUnknownEntityClass, $"Unknown entity class '{className}'.", className ?? "", keyName ?? "" );
		return ValidateKeyValue( cls, keyName, value );
	}

	/// <summary>Validate one keyvalue on a known class. Returns null when valid.</summary>
	public EntityRegistryError ValidateKeyValue( EntityClass cls, string keyName, string value )
	{
		if ( cls is null ) throw new ArgumentNullException( nameof( cls ) );
		if ( string.IsNullOrWhiteSpace( keyName ) )
			return new EntityRegistryError( CodeUnknownKeyValue, "Keyvalue has no name.", cls.Name, "" );
		var declared = cls.FindKeyValue( keyName );
		KeyValueType type;
		if ( declared is not null ) type = declared.Type;
		else if ( ImplicitKeyValues.TryGetValue( keyName.Trim(), out var implicitType ) ) type = implicitType;
		else return new EntityRegistryError( CodeUnknownKeyValue, $"Unknown keyvalue '{keyName}' for entity class '{cls.Name}'.", cls.Name, keyName );
		if ( !TryCheckValue( type, value, out string error ) )
			return new EntityRegistryError( CodeInvalidKeyValueType, error, cls.Name, keyName );
		if ( declared is not null && declared.Choices.Length > 0 && !declared.Choices.Contains( value ?? "", StringComparer.OrdinalIgnoreCase ) )
			return new EntityRegistryError( CodeInvalidKeyValueType, $"Value '{value}' for '{keyName}' is not one of: {string.Join( ", ", declared.Choices )}.", cls.Name, keyName );
		return null;
	}

	/// <summary>Throwing form of <see cref="ValidateEntity"/>: raises the first error as <see cref="EntityRegistryException"/>.</summary>
	public void CheckEntity( string className, IReadOnlyDictionary<string, string> keyValues )
	{
		var errors = ValidateEntity( className, keyValues );
		if ( errors.Count > 0 ) throw ToException( errors[0] );
	}

	/// <summary>Throwing form of <see cref="ValidateKeyValue(string, string, string)"/>.</summary>
	public void CheckKeyValue( string className, string keyName, string value )
	{
		var error = ValidateKeyValue( className, keyName, value );
		if ( error is not null ) throw ToException( error );
	}

	static EntityRegistryException ToException( EntityRegistryError error )
		=> new( error.Code, error.Message, error.ClassName, error.KeyName );

	/// <summary>
	/// Type-check one raw keyvalue string. Returns false with a human-readable
	/// <paramref name="error"/> when the value does not match <paramref name="type"/>.
	/// Rules: string = anything; int = invariant integer; float = invariant
	/// finite number; colour = "R G B" (or "R G B A") with each 0..255;
	/// angle = one float (yaw) or three floats (pitch yaw roll); target =
	/// non-empty with no whitespace; model = non-empty path ending in a known
	/// model extension (.mdl/.vmdl/.glb/.obj/.fbx/.effigy); sound = non-empty
	/// path ending in a known sound extension (.wav/.mp3/.ogg/.flac).
	/// </summary>
	public static bool TryCheckValue( KeyValueType type, string value, out string error )
	{
		error = "";
		if ( value is null ) { error = "Value is null."; return false; }
		switch ( type )
		{
			case KeyValueType.String:
				return true;
			case KeyValueType.Int:
				if ( int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _ ) ) return true;
				error = $"Value '{value}' is not an integer."; return false;
			case KeyValueType.Float:
				if ( double.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d ) && !double.IsNaN( d ) && !double.IsInfinity( d ) ) return true;
				error = $"Value '{value}' is not a finite number."; return false;
			case KeyValueType.Colour:
			{
				string[] parts = value.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts.Length != 3 && parts.Length != 4 ) { error = $"Colour '{value}' must be 'R G B' (or 'R G B A'), each 0-255."; return false; }
				foreach ( string part in parts )
					if ( !int.TryParse( part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c ) || c < 0 || c > 255 )
					{ error = $"Colour '{value}' must be 'R G B' (or 'R G B A'), each 0-255."; return false; }
				return true;
			}
			case KeyValueType.Angle:
			{
				string[] parts = value.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts.Length != 1 && parts.Length != 3 ) { error = $"Angle '{value}' must be one number (yaw) or three numbers (pitch yaw roll)."; return false; }
				foreach ( string part in parts )
					if ( !double.TryParse( part, NumberStyles.Float, CultureInfo.InvariantCulture, out double a ) || double.IsNaN( a ) || double.IsInfinity( a ) )
					{ error = $"Angle '{value}' must be one number (yaw) or three numbers (pitch yaw roll)."; return false; }
				return true;
			}
			case KeyValueType.Target:
				if ( value.Length > 0 && value.All( c => !char.IsWhiteSpace( c ) ) ) return true;
				error = $"Target '{value}' must be a non-empty name with no whitespace."; return false;
			case KeyValueType.Model:
				if ( value.Length > 0 && value.All( c => !char.IsWhiteSpace( c ) ) && ModelExtensions.Any( ext => value.EndsWith( ext, StringComparison.OrdinalIgnoreCase ) ) ) return true;
				error = $"Model '{value}' must be a non-empty path ending in {string.Join( "/", ModelExtensions )}."; return false;
			case KeyValueType.Sound:
				if ( value.Length > 0 && value.All( c => !char.IsWhiteSpace( c ) ) && SoundExtensions.Any( ext => value.EndsWith( ext, StringComparison.OrdinalIgnoreCase ) ) ) return true;
				error = $"Sound '{value}' must be a non-empty path ending in {string.Join( "/", SoundExtensions )}."; return false;
			default:
				error = $"Unknown keyvalue type '{type}'."; return false;
		}
	}

	/// <summary>Parse a keyvalue type name from JSON ("colour" and "color" both map to Colour).</summary>
	public static KeyValueType ParseKeyValueType( string name, string sourceName = "" )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new EntityRegistryException( CodeUnknownKeyValueType, "Keyvalue has no type.", "", "" );
		switch ( name.Trim().ToLowerInvariant() )
		{
			case "string": return KeyValueType.String;
			case "int": case "integer": return KeyValueType.Int;
			case "float": case "double": return KeyValueType.Float;
			case "colour": case "color": return KeyValueType.Colour;
			case "angle": return KeyValueType.Angle;
			case "target": case "targetname": return KeyValueType.Target;
			case "model": return KeyValueType.Model;
			case "sound": return KeyValueType.Sound;
			default: throw new EntityRegistryException( CodeUnknownKeyValueType, $"Unknown keyvalue type '{name}' in registry '{sourceName}'.", "", name );
		}
	}

	// ---- JSON parsing ----

	void LoadJsonInto( string json, string sourceName )
	{
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException ex ) { throw new EntityRegistryException( CodeInvalidRegistry, $"Registry '{sourceName}' is malformed JSON: {ex.Message}" ); }
		using ( doc )
		{
			var root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Registry '{sourceName}' must be a JSON object." );
			if ( root.TryGetProperty( "schemaVersion", out var versionEl ) )
			{
				if ( versionEl.ValueKind != JsonValueKind.Number || !versionEl.TryGetInt32( out int version ) || version != CurrentSchemaVersion )
					throw new EntityRegistryException( CodeInvalidRegistry, $"Registry '{sourceName}' has unsupported schemaVersion '{versionEl.GetRawText()}'; expected {CurrentSchemaVersion}." );
				SchemaVersion = version;
			}
			if ( root.TryGetProperty( "name", out var nameEl ) && nameEl.ValueKind == JsonValueKind.String )
				Name = nameEl.GetString() ?? "";
			if ( !root.TryGetProperty( "classes", out var classesEl ) || classesEl.ValueKind != JsonValueKind.Array )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Registry '{sourceName}' has no 'classes' array." );
			foreach ( var classEl in classesEl.EnumerateArray() )
				Add( ParseClass( classEl, sourceName ) );
		}
	}

	static EntityClass ParseClass( JsonElement el, string sourceName )
	{
		if ( el.ValueKind != JsonValueKind.Object )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Registry '{sourceName}': each class must be an object." );
		string name = RequiredString( el, "name", sourceName );
		var cls = new EntityClass { Name = name.Trim() };
		if ( el.TryGetProperty( "kind", out var kindEl ) )
		{
			if ( kindEl.ValueKind != JsonValueKind.String )
				throw new EntityRegistryException( CodeInvalidEntityKind, $"Class '{name}': 'kind' must be \"point\" or \"brush\".", name );
			switch ( kindEl.GetString().Trim().ToLowerInvariant() )
			{
				case "point": cls.Kind = EntityKind.Point; break;
				case "brush": cls.Kind = EntityKind.Brush; break;
				default: throw new EntityRegistryException( CodeInvalidEntityKind, $"Class '{name}': unknown kind '{kindEl.GetString()}'; expected \"point\" or \"brush\".", name );
			}
		}
		cls.Description = OptionalString( el, "description" );
		cls.BaseClasses = OptionalStringArray( el, "baseClasses" );
		var keyValues = new List<EntityKeyValue>();
		if ( el.TryGetProperty( "keyvalues", out var kvEl ) )
		{
			if ( kvEl.ValueKind != JsonValueKind.Array )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{name}': 'keyvalues' must be an array.", name );
			foreach ( var kv in kvEl.EnumerateArray() )
			{
				var parsed = ParseKeyValue( kv, name, sourceName );
				if ( keyValues.Any( k => string.Equals( k.Name, parsed.Name, StringComparison.OrdinalIgnoreCase ) ) )
					throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{name}': duplicate keyvalue '{parsed.Name}'.", name, parsed.Name );
				if ( ImplicitKeyValues.TryGetValue( parsed.Name, out var implicitType ) && implicitType != parsed.Type )
					throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{name}': keyvalue '{parsed.Name}' redeclares an implicit base keyvalue with a different type.", name, parsed.Name );
				keyValues.Add( parsed );
			}
		}
		cls.KeyValues = keyValues.ToArray();
		cls.Spawnflags = ParseSpawnflags( el, name, sourceName );
		cls.Inputs = ParseInputs( el, name, sourceName );
		cls.Outputs = ParseOutputs( el, name, sourceName );
		cls.Helpers = ParseHelpers( el, name, sourceName );
		return cls;
	}

	static EntityKeyValue ParseKeyValue( JsonElement el, string className, string sourceName )
	{
		if ( el.ValueKind != JsonValueKind.Object )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': each keyvalue must be an object.", className );
		string name = RequiredString( el, "name", sourceName, className );
		string typeName = RequiredString( el, "type", sourceName, className );
		KeyValueType type;
		try { type = ParseKeyValueType( typeName, sourceName ); }
		catch ( EntityRegistryException ex ) { throw new EntityRegistryException( ex.Code, ex.Message, className, name ); }
		return new EntityKeyValue
		{
			Name = name.Trim(),
			Type = type,
			DefaultValue = OptionalString( el, "default" ),
			Description = OptionalString( el, "description" ),
			Choices = OptionalStringArray( el, "choices" ),
		};
	}

	static EntitySpawnflag[] ParseSpawnflags( JsonElement el, string className, string sourceName )
	{
		if ( !el.TryGetProperty( "spawnflags", out var arr ) ) return Array.Empty<EntitySpawnflag>();
		if ( arr.ValueKind != JsonValueKind.Array )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': 'spawnflags' must be an array.", className );
		var list = new List<EntitySpawnflag>();
		foreach ( var item in arr.EnumerateArray() )
		{
			if ( item.ValueKind != JsonValueKind.Object || !item.TryGetProperty( "value", out var valueEl )
				|| valueEl.ValueKind != JsonValueKind.Number || !valueEl.TryGetInt32( out int value ) )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': each spawnflag needs an integer 'value'.", className );
			if ( list.Any( f => f.Value == value ) )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': duplicate spawnflag value {value}.", className );
			list.Add( new EntitySpawnflag
			{
				Value = value,
				Name = OptionalString( item, "name" ),
				DefaultOn = item.TryGetProperty( "defaultOn", out var onEl ) && onEl.ValueKind == JsonValueKind.True,
				Description = OptionalString( item, "description" ),
			} );
		}
		return list.ToArray();
	}

	static EntityInput[] ParseInputs( JsonElement el, string className, string sourceName )
	{
		if ( !el.TryGetProperty( "inputs", out var arr ) ) return Array.Empty<EntityInput>();
		if ( arr.ValueKind != JsonValueKind.Array )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': 'inputs' must be an array.", className );
		var list = new List<EntityInput>();
		foreach ( var item in arr.EnumerateArray() )
		{
			if ( item.ValueKind != JsonValueKind.Object )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': each input must be an object.", className );
			list.Add( new EntityInput
			{
				Name = RequiredString( item, "name", sourceName, className ).Trim(),
				ArgumentType = OptionalString( item, "argumentType", "void" ),
				Description = OptionalString( item, "description" ),
			} );
		}
		return list.ToArray();
	}

	static EntityOutput[] ParseOutputs( JsonElement el, string className, string sourceName )
	{
		if ( !el.TryGetProperty( "outputs", out var arr ) ) return Array.Empty<EntityOutput>();
		if ( arr.ValueKind != JsonValueKind.Array )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': 'outputs' must be an array.", className );
		var list = new List<EntityOutput>();
		foreach ( var item in arr.EnumerateArray() )
		{
			if ( item.ValueKind != JsonValueKind.Object )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': each output must be an object.", className );
			list.Add( new EntityOutput
			{
				Name = RequiredString( item, "name", sourceName, className ).Trim(),
				ArgumentType = OptionalString( item, "argumentType", "void" ),
				Description = OptionalString( item, "description" ),
			} );
		}
		return list.ToArray();
	}

	static EntityHelper[] ParseHelpers( JsonElement el, string className, string sourceName )
	{
		if ( !el.TryGetProperty( "helpers", out var arr ) ) return Array.Empty<EntityHelper>();
		if ( arr.ValueKind != JsonValueKind.Array )
			throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': 'helpers' must be an array.", className );
		var list = new List<EntityHelper>();
		foreach ( var item in arr.EnumerateArray() )
		{
			if ( item.ValueKind != JsonValueKind.Object || !item.TryGetProperty( "kind", out var kindEl ) || kindEl.ValueKind != JsonValueKind.String )
				throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': each helper needs a string 'kind'.", className );
			EntityHelperKind kind;
			switch ( kindEl.GetString().Trim().ToLowerInvariant() )
			{
				case "origin": kind = EntityHelperKind.Origin; break;
				case "angles": kind = EntityHelperKind.Angles; break;
				case "colour": case "color": kind = EntityHelperKind.Colour; break;
				case "target": kind = EntityHelperKind.Target; break;
				case "sprite": kind = EntityHelperKind.Sprite; break;
				default: throw new EntityRegistryException( CodeInvalidRegistry, $"Class '{className}': unknown helper kind '{kindEl.GetString()}'.", className );
			}
			list.Add( new EntityHelper { Kind = kind, Key = OptionalString( item, "key" ), Path = OptionalString( item, "path" ) } );
		}
		return list.ToArray();
	}

	static string RequiredString( JsonElement el, string property, string sourceName, string className = "" )
	{
		if ( !el.TryGetProperty( property, out var valueEl ) || valueEl.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace( valueEl.GetString() ) )
			throw new EntityRegistryException( CodeInvalidRegistry,
				string.IsNullOrEmpty( className )
					? $"Registry '{sourceName}': missing or empty string property '{property}'."
					: $"Class '{className}': missing or empty string property '{property}'.", className, property );
		return valueEl.GetString();
	}

	static string OptionalString( JsonElement el, string property, string fallback = "" )
	{
		if ( el.TryGetProperty( property, out var valueEl ) && valueEl.ValueKind == JsonValueKind.String )
			return valueEl.GetString() ?? fallback;
		return fallback;
	}

	static string[] OptionalStringArray( JsonElement el, string property )
	{
		if ( !el.TryGetProperty( property, out var arr ) || arr.ValueKind != JsonValueKind.Array ) return Array.Empty<string>();
		var list = new List<string>();
		foreach ( var item in arr.EnumerateArray() )
			if ( item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( item.GetString() ) )
				list.Add( item.GetString() );
		return list.ToArray();
	}
}
