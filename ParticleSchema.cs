#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Marionette.Desktop;

// Particles wave-6 lane P49: JSON Schema + validator for lane 5's definition
// schema v1 (ParticleAuthoring.cs). This file validates a definition JSON
// DOCUMENT only. It never builds a system: it never calls
// ParticleSystemDef.Build(), ParticleAuthoring.FromSystem(), or
// ParticleSystem.AddEmitter(), and it never constructs a ParticleSystem.
// What it does borrow from lane 5, deliberately, so the two cannot drift:
//   - the version constant (ParticleAuthoring.CurrentVersion),
//   - preset resolution (ParticleEmitterPresets.ResolveName — the loose
//     fx:/word-carrying matching is lane 5's rule, not a second table),
//   - enum parsing (Enum.TryParse, case-insensitive, whitespace-trimming,
//     numeric strings — the same API lane 5's ParseEnum uses),
//   - preset/plain emitter DEFAULTS (ParticleEmitterPresets.Create and the
//     ParticleEmitter defaults) as the base the def's overrides merge onto,
//     so a preset override such as lifetimeMax below the preset's minimum
//     fails here exactly as it fails in lane 5's Build().
// The range rules below are a line-by-line mirror of
// ParticleSystemSettings.Validate() and ParticleEmitter.Validate().
// Unknown JSON fields are IGNORED at every level (lane 5's forward-compat
// rule); only known fields are type/range checked.
//
// PROBE-PROVEN LANE 5 EDGE SEMANTICS (first P49 agent's probe harness,
// re-run 2026-10-24 against canonical lane 5; the validator encodes all of
// these, and tests/ParticleSchemaTests.cs re-proves them):
//   - "version" is matched CASE-SENSITIVELY for presence (a lone "Version"
//     fails as missing), but deserialization is case-insensitive and
//     LAST-WINS: the effective version is the last case-insensitive
//     "version" property, and it must be the integer 1. "1.0" and "1e0"
//     fail (JsonElement.TryGetInt32 rejects them), as do strings, bools,
//     and null. Duplicate "version": 2-then-1 passes, 1-then-2 fails.
//   - Every other field is matched case-insensitively, last-wins
//     ("Capacity" then "capacity" takes the second value).
//   - "settings" / "emitters" are OPTIONAL at top level (defaults / empty
//     list); an explicit null for either FAILS. A bare {} emitter is valid.
//   - Settings value-type fields reject JSON null (conversion error);
//     settings vector fields reject null as "must be a [x, y, z] array";
//     settings renderType null fails ParseEnum; settings collisionMesh
//     null is fine. Emitter fields are all nullable: JSON null = absent.
//   - Shape: a numeric string that parses to a DEFINED value is that shape
//     ("0" = Point, "4" = Sphere); an undefined numeric ("5", "-1") fails
//     in Validate's default case. RenderType: ANY integer-parseable string
//     is accepted ("3", "99", "-1" all build) because Validate only
//     special-cases Strand; only the Strand + StrandPoints < 2 rule fails.
//     The schema file lists the canonical names; this validator is the
//     authoritative lane 5 mirror for these parser tolerances.
//   - Floats: "1e400" deserializes to Infinity in lane 5 and then fails
//     the finite checks (it is NOT a conversion error). Ints reject
//     "100.0" / "100.5" / "1e10" (conversion errors). Bools reject 1.
//   - Mesh geometry is validated whenever a mesh is present, whatever the
//     shape (a Point emitter with a degenerate mesh fails); MeshSurface
//     without a mesh fails. Mesh vertices/indices are both required in
//     effect: a mesh missing either always fails in lane 5 (empty indices,
//     or an index outside an empty vertex list).
//   - Direction-zero rule: zero direction + speed > 0 fails, EXCEPT for
//     Area/MeshSurface with UseSurfaceNormal; and a preset emitter's zero
//     direction override is IGNORED (Create keeps the preset direction),
//     so preset + direction [0,0,0] + speed 99 is valid.
//   - Seed 0 is valid (Validate has no seed rule); capacity 4000000 is
//     valid; fixedDeltaTime must be in (0, 1].
//
// The companion document ParticleDefinition.schema.json states the
// canonical document shape (canonical spellings, required = ["version"]
// only, ranges from Validate). It cannot express the parser tolerances
// above (case-insensitive enums, loose preset names, cross-field
// min<=max, conditional non-zero rules, mesh area) — those live here and
// are marked as validator-only in the schema's descriptions. P50
// (definition migration) consumes the schema file; the authoring load
// path calls Validate() before Build() (sweep lines in the lane report).

/// <summary>One validation failure: the JSON path of the offending field
/// (for example "$.settings.capacity" or "$.emitters[0].preset") plus a
/// human-readable reason. An empty list means the document is valid.</summary>
public sealed record ParticleSchemaError( string Path, string Reason )
{
	public override string ToString() => $"{Path}: {Reason}";
}

/// <summary>Validator for lane 5 particle definition JSON (schema v1).
/// Returns a structured error list, never a bare true/false.</summary>
public static class ParticleSchema
{
	/// <summary>The schema version lane 5 accepts (its own constant).</summary>
	public static int CurrentVersion => ParticleAuthoring.CurrentVersion;

	/// <summary>Canonical preset names, from lane 5's own table.</summary>
	public static IReadOnlyList<string> PresetNames => ParticleEmitterPresets.Names;

	/// <summary>Canonical emitter shape names.</summary>
	public static string[] ShapeNames => Enum.GetNames<ParticleEmitterShape>();

	/// <summary>Canonical render type names.</summary>
	public static string[] RenderTypeNames => Enum.GetNames<ParticleRenderType>();

	/// <summary>Validates a definition JSON document. Returns an empty list
	/// when the document agrees with lane 5's parser; otherwise one error
	/// per problem, each with a field path and a reason. Never throws for
	/// bad input (null/empty/malformed documents yield errors).</summary>
	public static IReadOnlyList<ParticleSchemaError> Validate( string? json )
	{
		var errors = new List<ParticleSchemaError>();
		if ( string.IsNullOrWhiteSpace( json ) )
		{
			errors.Add( new ParticleSchemaError( "$", "document must not be empty." ) );
			return errors;
		}
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException e )
		{
			errors.Add( new ParticleSchemaError( "$", $"malformed JSON: {e.Message}" ) );
			return errors;
		}
		using ( doc )
		{
			var root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object )
			{
				errors.Add( new ParticleSchemaError( "$", "top level must be a JSON object." ) );
				return errors;
			}
			ValidateVersion( root, errors );
			if ( TryField( root, "name", out var name ) && name.ValueKind != JsonValueKind.String && name.ValueKind != JsonValueKind.Null )
				errors.Add( new ParticleSchemaError( "$.name", "must be a string." ) );
			if ( TryField( root, "settings", out var settings ) )
			{
				if ( settings.ValueKind == JsonValueKind.Null )
					errors.Add( new ParticleSchemaError( "$.settings", "must be present." ) );
				else if ( settings.ValueKind != JsonValueKind.Object )
					errors.Add( new ParticleSchemaError( "$.settings", "must be a JSON object." ) );
				else
					ValidateSettings( settings, errors );
			}
			if ( TryField( root, "emitters", out var emitters ) )
			{
				if ( emitters.ValueKind == JsonValueKind.Null )
					errors.Add( new ParticleSchemaError( "$.emitters", "must be present (an empty list is a valid, emitterless system)." ) );
				else if ( emitters.ValueKind != JsonValueKind.Array )
					errors.Add( new ParticleSchemaError( "$.emitters", "must be an array." ) );
				else
				{
					int i = 0;
					foreach ( var em in emitters.EnumerateArray() )
					{
						string path = $"$.emitters[{i}]";
						if ( em.ValueKind == JsonValueKind.Null )
							errors.Add( new ParticleSchemaError( path, "must be an object." ) );
						else if ( em.ValueKind != JsonValueKind.Object )
							errors.Add( new ParticleSchemaError( path, "must be an object." ) );
						else
							ValidateEmitter( em, path, errors );
						i++;
					}
				}
			}
		}
		return errors;
	}

	// Last property matching `name`, case-insensitively (lane 5's
	// deserializer is case-insensitive and last-wins).
	static bool TryField( JsonElement obj, string name, out JsonElement value )
	{
		value = default;
		bool found = false;
		foreach ( var prop in obj.EnumerateObject() )
			if ( prop.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) )
			{
				value = prop.Value;
				found = true;
			}
		return found;
	}

	static void ValidateVersion( JsonElement root, List<ParticleSchemaError> errors )
	{
		JsonElement? lastExact = null, lastInsensitive = null;
		foreach ( var prop in root.EnumerateObject() )
		{
			if ( prop.Name.Equals( "version", StringComparison.Ordinal ) )
				lastExact = prop.Value;
			if ( prop.Name.Equals( "version", StringComparison.OrdinalIgnoreCase ) )
				lastInsensitive = prop.Value;
		}
		if ( lastExact is null )
		{
			errors.Add( new ParticleSchemaError( "$.version", "missing \"version\" (this build reads version 1)." ) );
			return;
		}
		if ( !IsVersionOne( lastExact.Value ) )
			errors.Add( new ParticleSchemaError( "$.version", $"unsupported version '{lastExact.Value}' (this build reads version 1)." ) );
		else if ( lastInsensitive is not null && !IsVersionOne( lastInsensitive.Value ) )
			errors.Add( new ParticleSchemaError( "$.version", $"unsupported version '{lastInsensitive.Value}' (this build reads version 1)." ) );
	}

	static bool IsVersionOne( JsonElement v )
		=> v.ValueKind == JsonValueKind.Number && v.TryGetInt32( out int n ) && n == CurrentVersion;

	// ---- scalar readers: type errors recorded, value left at the default ----

	static bool ReadInt( JsonElement el, string path, List<ParticleSchemaError> errors, out int value )
	{
		if ( el.ValueKind != JsonValueKind.Number || !el.TryGetInt32( out value ) )
		{
			errors.Add( new ParticleSchemaError( path, "must be an integer." ) );
			value = 0;
			return false;
		}
		return true;
	}

	static bool ReadUInt( JsonElement el, string path, List<ParticleSchemaError> errors, out uint value )
	{
		if ( el.ValueKind != JsonValueKind.Number || !el.TryGetUInt32( out value ) )
		{
			errors.Add( new ParticleSchemaError( path, "must be a non-negative integer within 0..4294967295." ) );
			value = 0;
			return false;
		}
		return true;
	}

	static bool ReadFloat( JsonElement el, string path, List<ParticleSchemaError> errors, out float value )
	{
		if ( el.ValueKind != JsonValueKind.Number )
		{
			errors.Add( new ParticleSchemaError( path, "must be a number." ) );
			value = 0;
			return false;
		}
		if ( el.TryGetSingle( out value ) )
			return true;
		// Overflow (for example 1e400): lane 5's deserializer yields
		// Infinity, which the finite checks then reject. Mirror that
		// instead of reporting a type error.
		value = el.GetRawText().TrimStart().StartsWith( "-", StringComparison.Ordinal ) ? float.NegativeInfinity : float.PositiveInfinity;
		return true;
	}

	static bool ReadBool( JsonElement el, string path, List<ParticleSchemaError> errors, out bool value )
	{
		if ( el.ValueKind == JsonValueKind.True ) { value = true; return true; }
		if ( el.ValueKind == JsonValueKind.False ) { value = false; return true; }
		errors.Add( new ParticleSchemaError( path, "must be true or false." ) );
		value = false;
		return false;
	}

	static bool ReadVec( JsonElement el, string path, List<ParticleSchemaError> errors, out Vector3 value )
	{
		value = default;
		if ( el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 3 )
		{
			errors.Add( new ParticleSchemaError( path, "must be a [x, y, z] array." ) );
			return false;
		}
		var comps = new float[3];
		bool ok = true;
		int j = 0;
		foreach ( var c in el.EnumerateArray() )
		{
			if ( !ReadFloat( c, $"{path}[{j}]", errors, out comps[j] ) )
				ok = false;
			j++;
		}
		value = new Vector3( comps[0], comps[1], comps[2] );
		if ( ok && !ParticleMath.Finite( value ) )
		{
			errors.Add( new ParticleSchemaError( path, "must be finite." ) );
			return false;
		}
		return ok;
	}

	// Nullable string field: JSON null counts as absent (no error).
	// Returns false only for a wrong JSON type; `value` is null when absent.
	static bool ReadOptString( JsonElement el, string path, List<ParticleSchemaError> errors, out string? value )
	{
		if ( el.ValueKind == JsonValueKind.Null ) { value = null; return true; }
		if ( el.ValueKind != JsonValueKind.String )
		{
			errors.Add( new ParticleSchemaError( path, "must be a string." ) );
			value = null;
			return false;
		}
		value = el.GetString();
		return true;
	}

	static void ValidateSettings( JsonElement s, List<ParticleSchemaError> errors )
	{
		const string p = "$.settings";
		var bad = new HashSet<string>( StringComparer.Ordinal );
		// Lane 5 defaults (ParticleSettingsDef).
		int capacity = 10_000, substeps = 1, strandPoints = 0;
		uint seed = 1;
		float fixedDeltaTime = 1f / 60f, drag = 0, turbStrength = 0, turbFreq = 0.1f, turbSpeed = 0,
			vortexStrength = 0, vortexRadius = 1, groundOffset = 0, collisionRadius = 0.01f,
			restitution = 0.3f, friction = 0.2f;
		Vector3 gravity = new( 0, 0, -386.0886f ), wind = new(), vortexCenter = new(),
			vortexAxis = new( 0, 0, 1 ), groundNormal = new( 0, 0, 1 );
		bool groundEnabled = false;
		ParticleRenderType renderType = ParticleRenderType.Billboard;
		bool renderTypeOk = true;

		void IntField( string name, ref int slot )
		{
			if ( TryField( s, name, out var el ) )
			{
				if ( !ReadInt( el, $"{p}.{name}", errors, out int v ) )
					bad.Add( name );
				else
					slot = v;
			}
		}
		void FloatField( string name, ref float slot )
		{
			if ( TryField( s, name, out var el ) )
			{
				if ( !ReadFloat( el, $"{p}.{name}", errors, out float v ) )
					bad.Add( name );
				else
					slot = v;
			}
		}
		void VecField( string name, ref Vector3 slot )
		{
			if ( TryField( s, name, out var el ) )
			{
				if ( el.ValueKind == JsonValueKind.Null )
				{
					errors.Add( new ParticleSchemaError( $"{p}.{name}", "must be a [x, y, z] array." ) );
					bad.Add( name );
				}
				else if ( !ReadVec( el, $"{p}.{name}", errors, out var v ) )
					bad.Add( name );
				else
					slot = v;
			}
		}

		IntField( "capacity", ref capacity );
		if ( TryField( s, "seed", out var seedEl ) && !ReadUInt( seedEl, $"{p}.seed", errors, out seed ) )
			bad.Add( "seed" );
		FloatField( "fixedDeltaTime", ref fixedDeltaTime );
		IntField( "substeps", ref substeps );
		VecField( "gravity", ref gravity );
		VecField( "wind", ref wind );
		FloatField( "drag", ref drag );
		FloatField( "turbulenceStrength", ref turbStrength );
		FloatField( "turbulenceFrequency", ref turbFreq );
		FloatField( "turbulenceSpeed", ref turbSpeed );
		FloatField( "vortexStrength", ref vortexStrength );
		VecField( "vortexCenter", ref vortexCenter );
		VecField( "vortexAxis", ref vortexAxis );
		FloatField( "vortexRadius", ref vortexRadius );
		if ( TryField( s, "groundEnabled", out var geEl ) && !ReadBool( geEl, $"{p}.groundEnabled", errors, out groundEnabled ) )
			bad.Add( "groundEnabled" );
		VecField( "groundNormal", ref groundNormal );
		FloatField( "groundOffset", ref groundOffset );
		if ( TryField( s, "collisionMesh", out var cmEl ) && cmEl.ValueKind != JsonValueKind.Null )
		{
			if ( cmEl.ValueKind != JsonValueKind.Object )
				errors.Add( new ParticleSchemaError( $"{p}.collisionMesh", "must be an object." ) );
			else
				ValidateMesh( cmEl, $"{p}.collisionMesh", errors );
		}
		FloatField( "collisionRadius", ref collisionRadius );
		FloatField( "restitution", ref restitution );
		FloatField( "friction", ref friction );
		if ( TryField( s, "renderType", out var rtEl ) )
		{
			if ( rtEl.ValueKind == JsonValueKind.Null )
			{
				errors.Add( new ParticleSchemaError( $"{p}.renderType", $"'' is not one of: {string.Join( ", ", RenderTypeNames )}." ) );
				renderTypeOk = false;
			}
			else if ( !ReadOptString( rtEl, $"{p}.renderType", errors, out string? rt ) || rt is null )
				renderTypeOk = false;
			else if ( !Enum.TryParse<ParticleRenderType>( rt, ignoreCase: true, out renderType ) )
			{
				errors.Add( new ParticleSchemaError( $"{p}.renderType", $"'{rt}' is not one of: {string.Join( ", ", RenderTypeNames )}." ) );
				renderTypeOk = false;
			}
			// NOTE: a parsed-but-undefined numeric render type ("3", "-1")
			// is ACCEPTED here, matching lane 5: Validate only special-cases
			// Strand and never checks definedness for render types.
		}
		IntField( "strandPoints", ref strandPoints );

		// Mirror of ParticleSystemSettings.Validate(). Fields that failed
		// parsing are skipped here so one bad field yields one error.
		if ( !bad.Contains( "capacity" ) && ( capacity <= 0 || capacity > 4_000_000 ) )
			errors.Add( new ParticleSchemaError( $"{p}.capacity", "must be 1..4000000." ) );
		if ( !bad.Contains( "fixedDeltaTime" ) && ( !float.IsFinite( fixedDeltaTime ) || fixedDeltaTime <= 0 || fixedDeltaTime > 1 ) )
			errors.Add( new ParticleSchemaError( $"{p}.fixedDeltaTime", "must be in (0, 1] s." ) );
		if ( !bad.Contains( "substeps" ) && ( substeps < 1 || substeps > 64 ) )
			errors.Add( new ParticleSchemaError( $"{p}.substeps", "must be 1..64." ) );
		if ( !bad.Contains( "drag" ) && ( !float.IsFinite( drag ) || drag < 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.drag", "must be finite and >= 0." ) );
		if ( !bad.Contains( "turbulenceStrength" ) && ( !float.IsFinite( turbStrength ) || turbStrength < 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.turbulenceStrength", "must be finite and >= 0." ) );
		if ( !bad.Contains( "turbulenceFrequency" ) && ( !float.IsFinite( turbFreq ) || turbFreq <= 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.turbulenceFrequency", "must be finite and > 0." ) );
		if ( !bad.Contains( "turbulenceSpeed" ) && !float.IsFinite( turbSpeed ) )
			errors.Add( new ParticleSchemaError( $"{p}.turbulenceSpeed", "must be finite." ) );
		if ( !bad.Contains( "vortexStrength" ) && !float.IsFinite( vortexStrength ) )
			errors.Add( new ParticleSchemaError( $"{p}.vortexStrength", "must be finite." ) );
		if ( !bad.Contains( "vortexRadius" ) && ( !float.IsFinite( vortexRadius ) || vortexRadius <= 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.vortexRadius", "must be finite and > 0." ) );
		if ( !bad.Contains( "vortexStrength" ) && !bad.Contains( "vortexAxis" ) && vortexStrength != 0 && vortexAxis.LengthSquared() < 1e-12f )
			errors.Add( new ParticleSchemaError( $"{p}.vortexAxis", "must be non-zero when vortexStrength is non-zero." ) );
		if ( !bad.Contains( "groundEnabled" ) && !bad.Contains( "groundNormal" ) && groundEnabled && groundNormal.LengthSquared() < 1e-12f )
			errors.Add( new ParticleSchemaError( $"{p}.groundNormal", "must be non-zero when groundEnabled is true." ) );
		if ( !bad.Contains( "groundOffset" ) && !float.IsFinite( groundOffset ) )
			errors.Add( new ParticleSchemaError( $"{p}.groundOffset", "must be finite." ) );
		if ( !bad.Contains( "collisionRadius" ) && ( !float.IsFinite( collisionRadius ) || collisionRadius <= 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.collisionRadius", "must be finite and > 0." ) );
		if ( !bad.Contains( "restitution" ) && ( !float.IsFinite( restitution ) || restitution < 0 || restitution > 1 ) )
			errors.Add( new ParticleSchemaError( $"{p}.restitution", "must be 0..1." ) );
		if ( !bad.Contains( "friction" ) && ( !float.IsFinite( friction ) || friction < 0 || friction > 1 ) )
			errors.Add( new ParticleSchemaError( $"{p}.friction", "must be 0..1." ) );
		if ( !bad.Contains( "strandPoints" ) && ( strandPoints < 0 || strandPoints > 256 ) )
			errors.Add( new ParticleSchemaError( $"{p}.strandPoints", "must be 0..256." ) );
		if ( renderTypeOk && renderType == ParticleRenderType.Strand && !bad.Contains( "strandPoints" ) && strandPoints < 2 )
			errors.Add( new ParticleSchemaError( $"{p}.strandPoints", "Strand render type needs StrandPoints >= 2." ) );
		_ = seed;
		_ = wind;
		_ = vortexCenter;
	}

	static void ValidateEmitter( JsonElement e, string p, List<ParticleSchemaError> errors )
	{
		var bad = new HashSet<string>( StringComparer.Ordinal );
		string? preset = null, shapeText = null;
		bool presetPresent = false, presetOk = true, shapeOk = true, meshPresent = false;
		bool burstsOk = true;
		var bursts = new List<(int Step, int Count)>();

		if ( TryField( e, "name", out var nameEl ) )
			ReadOptString( nameEl, $"{p}.name", errors, out _ );
		if ( TryField( e, "preset", out var presetEl ) )
		{
			presetPresent = true;
			if ( ReadOptString( presetEl, $"{p}.preset", errors, out preset ) && preset is not null )
			{
				string? canonical = ParticleEmitterPresets.ResolveName( preset );
				if ( canonical is null )
				{
					errors.Add( new ParticleSchemaError( $"{p}.preset", $"unknown preset '{preset}'. Presets: {string.Join( ", ", PresetNames )}." ) );
					presetOk = false;
				}
				else
					preset = canonical;
			}
			else if ( preset is null && presetEl.ValueKind != JsonValueKind.Null )
				presetOk = false;
		}
		if ( TryField( e, "shape", out var shapeEl ) )
		{
			if ( ReadOptString( shapeEl, $"{p}.shape", errors, out shapeText ) && shapeText is not null )
			{
				if ( !Enum.TryParse<ParticleEmitterShape>( shapeText, ignoreCase: true, out var parsed ) )
				{
					errors.Add( new ParticleSchemaError( $"{p}.shape", $"'{shapeText}' is not one of: {string.Join( ", ", ShapeNames )}." ) );
					shapeOk = false;
				}
				else if ( !Enum.IsDefined( parsed ) )
				{
					errors.Add( new ParticleSchemaError( $"{p}.shape", $"unknown shape {parsed}." ) );
					shapeOk = false;
				}
			}
			else if ( shapeText is null && shapeEl.ValueKind != JsonValueKind.Null )
				shapeOk = false;
		}

		// Nullable vector / scalar / bool fields: JSON null = absent.
		Vector3? position = null, areaU = null, areaV = null, boxHalfExtents = null, direction = null, baseVelocity = null;
		float? sphereRadius = null, rate = null, lifetimeMin = null, lifetimeMax = null,
			sizeMin = null, sizeMax = null, speed = null, speedJitter = null,
			spreadDegrees = null, angularSpeedMax = null;
		bool? useSurfaceNormal = null, enabled = null;

		Vector3? VecField( string name )
		{
			if ( !TryField( e, name, out var el ) || el.ValueKind == JsonValueKind.Null )
				return null;
			if ( !ReadVec( el, $"{p}.{name}", errors, out var v ) )
			{
				bad.Add( name );
				return null;
			}
			return v;
		}
		float? FloatField( string name )
		{
			if ( !TryField( e, name, out var el ) || el.ValueKind == JsonValueKind.Null )
				return null;
			if ( !ReadFloat( el, $"{p}.{name}", errors, out var v ) )
			{
				bad.Add( name );
				return null;
			}
			return v;
		}
		bool? BoolField( string name )
		{
			if ( !TryField( e, name, out var el ) || el.ValueKind == JsonValueKind.Null )
				return null;
			if ( !ReadBool( el, $"{p}.{name}", errors, out var v ) )
			{
				bad.Add( name );
				return null;
			}
			return v;
		}

		position = VecField( "position" );
		areaU = VecField( "areaU" );
		areaV = VecField( "areaV" );
		boxHalfExtents = VecField( "boxHalfExtents" );
		sphereRadius = FloatField( "sphereRadius" );
		if ( TryField( e, "mesh", out var meshEl ) && meshEl.ValueKind != JsonValueKind.Null )
		{
			meshPresent = true;
			if ( meshEl.ValueKind != JsonValueKind.Object )
				errors.Add( new ParticleSchemaError( $"{p}.mesh", "must be an object." ) );
			else
				ValidateMesh( meshEl, $"{p}.mesh", errors );
		}
		rate = FloatField( "rate" );
		if ( TryField( e, "bursts", out var burstsEl ) && burstsEl.ValueKind != JsonValueKind.Null )
		{
			if ( burstsEl.ValueKind != JsonValueKind.Array )
			{
				errors.Add( new ParticleSchemaError( $"{p}.bursts", "must be an array." ) );
				burstsOk = false;
			}
			else
			{
				int j = 0;
				foreach ( var b in burstsEl.EnumerateArray() )
				{
					string bp = $"{p}.bursts[{j}]";
					if ( b.ValueKind == JsonValueKind.Null )
					{
						errors.Add( new ParticleSchemaError( bp, "burst entries must be {\"step\": N, \"count\": N}." ) );
						burstsOk = false;
					}
					else if ( b.ValueKind != JsonValueKind.Object )
					{
						errors.Add( new ParticleSchemaError( bp, "must be an object." ) );
						burstsOk = false;
					}
					else
					{
						int step = 0, count = 0;
						bool ok = true;
						if ( TryField( b, "step", out var stepEl ) && !ReadInt( stepEl, $"{bp}.step", errors, out step ) )
							ok = false;
						if ( TryField( b, "count", out var countEl ) && !ReadInt( countEl, $"{bp}.count", errors, out count ) )
							ok = false;
						if ( ok )
						{
							if ( step < 0 )
								errors.Add( new ParticleSchemaError( $"{bp}.step", "must be >= 0." ) );
							if ( count < 0 )
								errors.Add( new ParticleSchemaError( $"{bp}.count", "must be >= 0." ) );
							bursts.Add( (step, count) );
						}
						else
							burstsOk = false;
					}
					j++;
				}
			}
		}
		lifetimeMin = FloatField( "lifetimeMin" );
		lifetimeMax = FloatField( "lifetimeMax" );
		sizeMin = FloatField( "sizeMin" );
		sizeMax = FloatField( "sizeMax" );
		direction = VecField( "direction" );
		useSurfaceNormal = BoolField( "useSurfaceNormal" );
		speed = FloatField( "speed" );
		speedJitter = FloatField( "speedJitter" );
		spreadDegrees = FloatField( "spreadDegrees" );
		baseVelocity = VecField( "baseVelocity" );
		angularSpeedMax = FloatField( "angularSpeedMax" );
		enabled = BoolField( "enabled" );

		// Effective emitter: lane 5's defaults merged with the def's
		// overrides, in lane 5's own order. This holder is never built
		// into a system; it exists only so the range checks below see
		// the same merged values lane 5's Validate() sees.
		if ( presetPresent && !presetOk )
			return; // Preset error already recorded; merged checks would be guesses.
		ParticleEmitter em;
		if ( preset is not null )
			em = ParticleEmitterPresets.Create( preset,
				position ?? new Vector3( 0, 0, 0 ),
				direction );
		else
		{
			em = new ParticleEmitter();
			if ( position is not null ) em.Position = position.Value;
			if ( direction is not null ) em.Direction = direction.Value;
		}
		if ( shapeOk && shapeText is not null && Enum.TryParse<ParticleEmitterShape>( shapeText, ignoreCase: true, out var parsedShape ) )
			em.Shape = parsedShape;
		if ( areaU is not null ) em.AreaU = areaU.Value;
		if ( areaV is not null ) em.AreaV = areaV.Value;
		if ( boxHalfExtents is not null ) em.BoxHalfExtents = boxHalfExtents.Value;
		if ( sphereRadius is not null ) em.SphereRadius = sphereRadius.Value;
		if ( rate is not null ) em.Rate = rate.Value;
		if ( burstsOk && TryField( e, "bursts", out var burstsEl2 ) && burstsEl2.ValueKind == JsonValueKind.Array )
			em.Bursts = bursts.Select( b => new ParticleBurst( b.Step, b.Count ) ).ToList();
		if ( lifetimeMin is not null ) em.LifetimeMin = lifetimeMin.Value;
		if ( lifetimeMax is not null ) em.LifetimeMax = lifetimeMax.Value;
		if ( sizeMin is not null ) em.SizeMin = sizeMin.Value;
		if ( sizeMax is not null ) em.SizeMax = sizeMax.Value;
		if ( useSurfaceNormal is not null ) em.UseSurfaceNormal = useSurfaceNormal.Value;
		if ( speed is not null ) em.Speed = speed.Value;
		if ( speedJitter is not null ) em.SpeedJitter = speedJitter.Value;
		if ( spreadDegrees is not null ) em.SpreadDegrees = spreadDegrees.Value;
		if ( baseVelocity is not null ) em.BaseVelocity = baseVelocity.Value;
		if ( angularSpeedMax is not null ) em.AngularSpeedMax = angularSpeedMax.Value;
		if ( enabled is not null ) em.Enabled = enabled.Value;

		// Mirror of ParticleEmitter.Validate().
		if ( !bad.Contains( "rate" ) && ( !float.IsFinite( em.Rate ) || em.Rate < 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.rate", "must be finite and >= 0." ) );
		if ( !bad.Contains( "lifetimeMin" ) && !bad.Contains( "lifetimeMax" ) )
		{
			if ( !float.IsFinite( em.LifetimeMin ) || em.LifetimeMin <= 0 )
				errors.Add( new ParticleSchemaError( $"{p}.lifetimeMin", "must satisfy 0 < LifetimeMin <= LifetimeMax." ) );
			else if ( !float.IsFinite( em.LifetimeMax ) || em.LifetimeMax < em.LifetimeMin )
				errors.Add( new ParticleSchemaError( $"{p}.lifetimeMax", "must satisfy 0 < LifetimeMin <= LifetimeMax." ) );
		}
		if ( !bad.Contains( "sizeMin" ) && !bad.Contains( "sizeMax" ) )
		{
			if ( !float.IsFinite( em.SizeMin ) || em.SizeMin <= 0 )
				errors.Add( new ParticleSchemaError( $"{p}.sizeMin", "must satisfy 0 < SizeMin <= SizeMax." ) );
			else if ( !float.IsFinite( em.SizeMax ) || em.SizeMax < em.SizeMin )
				errors.Add( new ParticleSchemaError( $"{p}.sizeMax", "must satisfy 0 < SizeMin <= SizeMax." ) );
		}
		if ( !bad.Contains( "speed" ) && ( !float.IsFinite( em.Speed ) || em.Speed < 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.speed", "must be finite and >= 0." ) );
		if ( !bad.Contains( "speedJitter" ) && ( !float.IsFinite( em.SpeedJitter ) || em.SpeedJitter < 0 || em.SpeedJitter > 1 ) )
			errors.Add( new ParticleSchemaError( $"{p}.speedJitter", "must be 0..1." ) );
		if ( !bad.Contains( "spreadDegrees" ) && ( !float.IsFinite( em.SpreadDegrees ) || em.SpreadDegrees < 0 || em.SpreadDegrees > 180 ) )
			errors.Add( new ParticleSchemaError( $"{p}.spreadDegrees", "must be in 0..180." ) );
		if ( !bad.Contains( "angularSpeedMax" ) && ( !float.IsFinite( em.AngularSpeedMax ) || em.AngularSpeedMax < 0 ) )
			errors.Add( new ParticleSchemaError( $"{p}.angularSpeedMax", "must be finite and >= 0." ) );
		if ( shapeOk && !bad.Contains( "direction" ) && !bad.Contains( "useSurfaceNormal" ) && !bad.Contains( "speed" )
			&& em.Direction.LengthSquared() < 1e-12f
			&& !( em.UseSurfaceNormal && em.Shape is ParticleEmitterShape.Area or ParticleEmitterShape.MeshSurface )
			&& em.Speed > 0 )
			errors.Add( new ParticleSchemaError( $"{p}.direction", "must be non-zero." ) );
		if ( shapeOk )
		{
			switch ( em.Shape )
			{
				case ParticleEmitterShape.Area:
					if ( !bad.Contains( "areaU" ) && !bad.Contains( "areaV" )
						&& Vector3.Cross( em.AreaU, em.AreaV ).LengthSquared() < 1e-20f )
						errors.Add( new ParticleSchemaError( $"{p}.areaU", "area edges must be finite and non-parallel." ) );
					break;
				case ParticleEmitterShape.Box:
					if ( !bad.Contains( "boxHalfExtents" ) && ( em.BoxHalfExtents.X < 0 || em.BoxHalfExtents.Y < 0 || em.BoxHalfExtents.Z < 0 ) )
						errors.Add( new ParticleSchemaError( $"{p}.boxHalfExtents", "must be finite and >= 0." ) );
					break;
				case ParticleEmitterShape.Sphere:
					if ( !bad.Contains( "sphereRadius" ) && ( !float.IsFinite( em.SphereRadius ) || em.SphereRadius < 0 ) )
						errors.Add( new ParticleSchemaError( $"{p}.sphereRadius", "must be finite and >= 0." ) );
					break;
				case ParticleEmitterShape.MeshSurface:
					if ( !meshPresent )
						errors.Add( new ParticleSchemaError( $"{p}.mesh", "MeshSurface shape needs a \"mesh\" (vertices + indices) in the def." ) );
					break;
			}
		}
		_ = enabled;
	}

	// Mesh geometry mirror of ParticleMeshDef.ToMesh() + ParticleMesh's
	// constructor checks. Called whenever a mesh is present, whatever the
	// emitter shape (lane 5 validates it on set, not on shape).
	static void ValidateMesh( JsonElement m, string p, List<ParticleSchemaError> errors )
	{
		List<Vector3>? verts = null;
		List<int>? indices = null;
		if ( TryField( m, "vertices", out var vertsEl ) )
		{
			if ( vertsEl.ValueKind == JsonValueKind.Null )
				errors.Add( new ParticleSchemaError( $"{p}.vertices", "must be present." ) );
			else if ( vertsEl.ValueKind != JsonValueKind.Array )
				errors.Add( new ParticleSchemaError( $"{p}.vertices", "must be an array." ) );
			else
			{
				verts = new List<Vector3>();
				bool ok = true;
				int i = 0;
				foreach ( var v in vertsEl.EnumerateArray() )
				{
					if ( !ReadVec( v, $"{p}.vertices[{i}]", errors, out var vec ) )
						ok = false;
					else
						verts.Add( vec );
					i++;
				}
				if ( !ok )
					verts = null;
			}
		}
		else
			errors.Add( new ParticleSchemaError( $"{p}.vertices", "must be present." ) );
		if ( TryField( m, "indices", out var idxEl ) )
		{
			if ( idxEl.ValueKind == JsonValueKind.Null )
				errors.Add( new ParticleSchemaError( $"{p}.indices", "must be present." ) );
			else if ( idxEl.ValueKind != JsonValueKind.Array )
				errors.Add( new ParticleSchemaError( $"{p}.indices", "must be an array." ) );
			else
			{
				indices = new List<int>();
				bool ok = true;
				int j = 0;
				foreach ( var ix in idxEl.EnumerateArray() )
				{
					if ( !ReadInt( ix, $"{p}.indices[{j}]", errors, out int v ) )
						ok = false;
					else
						indices.Add( v );
					j++;
				}
				if ( !ok )
					indices = null;
			}
		}
		else
			errors.Add( new ParticleSchemaError( $"{p}.indices", "must be present." ) );
		if ( verts is null || indices is null )
			return;
		if ( indices.Count == 0 || indices.Count % 3 != 0 )
		{
			errors.Add( new ParticleSchemaError( $"{p}.indices", "must be a non-empty multiple of 3." ) );
			return;
		}
		bool rangeOk = true;
		for ( int j = 0; j < indices.Count; j++ )
			if ( (uint)indices[j] >= (uint)verts.Count )
			{
				errors.Add( new ParticleSchemaError( $"{p}.indices[{j}]", $"index {indices[j]} is outside 0..{verts.Count - 1}." ) );
				rangeOk = false;
			}
		if ( !rangeOk )
			return;
		double total = 0;
		var mn = new Vector3( float.MaxValue );
		var mx = new Vector3( float.MinValue );
		for ( int t = 0; t < indices.Count / 3; t++ )
		{
			var a = verts[indices[t * 3]];
			var b = verts[indices[t * 3 + 1]];
			var c = verts[indices[t * 3 + 2]];
			total += 0.5 * Vector3.Cross( b - a, c - a ).Length();
			mn = Vector3.Min( mn, Vector3.Min( a, Vector3.Min( b, c ) ) );
			mx = Vector3.Max( mx, Vector3.Max( a, Vector3.Max( b, c ) ) );
		}
		if ( !( total > 0 ) )
		{
			errors.Add( new ParticleSchemaError( p, "mesh has zero total area (all triangles degenerate)." ) );
			return;
		}
		// Collision-grid size guard, same arithmetic as ParticleMesh's ctor.
		var extent = mx - mn;
		float maxExtent = MathF.Max( extent.X, MathF.Max( extent.Y, extent.Z ) );
		float side = MathF.Sqrt( MathF.Max( 1, indices.Count / 3 ) / 2f );
		float cellSize = maxExtent / MathF.Max( 1, side );
		if ( cellSize <= 0 )
			cellSize = 1f;
		float pad = cellSize * 0.01f + 1e-4f;
		var size = extent + new Vector3( pad * 2 );
		long dimX = Math.Clamp( (long)MathF.Ceiling( size.X / cellSize ), 1, 256 );
		long dimY = Math.Clamp( (long)MathF.Ceiling( size.Y / cellSize ), 1, 256 );
		long dimZ = Math.Clamp( (long)MathF.Ceiling( size.Z / cellSize ), 1, 256 );
		if ( dimX * dimY * dimZ > 4_000_000 )
			errors.Add( new ParticleSchemaError( p, "collision grid too large." ) );
	}
}
