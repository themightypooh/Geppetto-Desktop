#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marionette.Desktop;

// Particle authoring + save (particles lane 5, 2026-10-24).
//
// WHAT THIS IS: the JSON definition of a 3D particle system, the code that
// turns one into a live ParticleSystem (and back), and the `particle` script
// verb line that authors one. It is the authoring surface the 3D sim never
// had: until now a ParticleSystem could only be built in code (fixtures,
// SceneRenderer cues). This file does not draw anything and does not change
// the simulation; it only describes systems and builds them.
//
// SCHEMA (version 1, JSON, vectors as [x, y, z], kernel inches, Z-up,
// seconds — the same units as ParticleSystem):
//   {
//     "version": 1,
//     "name": "ember-fountain",
//     "settings": { "capacity": 4000, "seed": 7, "fixedDeltaTime": 0.0166667,
//       "gravity": [0, 0, -386.0886], "drag": 0.4, "groundEnabled": true,
//       "groundOffset": 0, "restitution": 0.35, "renderType": "Billboard",
//       "strandPoints": 0, ... },
//     "emitters": [
//       { "preset": "sparks", "position": [0, 0, 10] },
//       { "shape": "Box", "position": [0, 0, 0], "boxHalfExtents": [30, 30, 10],
//         "rate": 12, "lifetimeMin": 3, "lifetimeMax": 6, ... }
//     ]
//   }
// Every settings field has the live type's default when omitted, so a
// hand-written def only names what it cares about. An emitter with a
// "preset" starts from ParticleEmitterPresets.Create and only the fields
// actually present in the def override it; an emitter without a preset
// starts from the live emitter defaults. "preset" is a construction
// shortcut, not state: FromSystem writes full fields and drops the name
// (a rebuilt system steps identically, but no longer remembers it was
// once called "sparks").
// STRICTNESS, BY DESIGN: a missing or non-1 "version" fails; an unknown
// preset, shape, or render type fails; a malformed vector, a bad number,
// or any value the live Validate() methods reject fails. Unknown JSON
// FIELDS are ignored, not failed: that is the forward-compat rule (a def
// written by a newer lane with fields this build does not know yet must
// still load — the same versioned-schema practice Unity/CUGL-style JSON
// particle templates use: a version stamp plus optional attributes).
// A seed is always serialized: Geppetto's sim is deterministic per seed,
// and a round trip that dropped the seed would silently change the art
// (the Three-Nebula determinism rule: serialize seeds and zone params).
// MESHES: a MeshSurface emitter (or a settings collision mesh) carries
// its geometry inline as {"vertices": [[x,y,z],...], "indices": [...]}.
// A MeshSurface emitter with no mesh fails loudly instead of building a
// system that can never emit.
// VERB: `particle preset=sparks at=0,0,10 [rate=.. speed=.. ...]`,
// `particle file=defs/fountain.json [at=.. rate=..]`, and
// `particle preset=dust_motes save=defs/dust.json` (writes the def,
// builds nothing). Verb-built preset systems get the preset's medium
// (SettingsFor) by default — the authoring-layer fix for the Phase-1 gap
// where TriggerCue ignored it, so a verb-built "bubbles" rises.
// DOCUMENT-SAVE SURVEY (read, not changed): a .effigy save is
// StudioDocument.Write(studio) — the kernel feature tree — validated by
// reparse in DocumentSaveService, and DocumentSession carries no particle
// slot. A particle system therefore has NO home inside .effigy today.
// The no-kernel-change persistence route is a sidecar:
// <model>.particles.json next to the .effigy, written by the desktop host
// on save and loaded with `particle file=...` (or by the host on open).
// A kernel ParticleFeature would be the other route, but particles are
// live FX, not rebuildable model geometry, so a feature that pretends a
// rebuild keeps them would lie — the sidecar is the honest one. See the
// sweep notes in the lane report for the exact wiring lines.
// SWEEP WIRING, READ THIS BEFORE ADDING ANY CASE: the kernel BuildScript
// verb switch (kernel/Effigy/Script/BuildScript.cs, Command) CANNOT call
// this file — the kernel project does not reference the desktop project
// (the reference runs desktop -> kernel only), so a kernel `particle`
// case could validate text but never build a ParticleSystem. The real
// wiring is a desktop pre-pass in ScriptLive.Drain: pull `particle` lines
// out of the piece before BuildScript.Run sees them (an unknown verb
// fails the whole run otherwise), hand each line to ParticleVerb.Run,
// and keep the built systems on the desktop side. `particle` is NOT a
// model mutation (live-only, like `render`): Mutates("particle") must
// stay false, and a rebuild from the feature tree does not keep it.

/// <summary>One burst in a def: <see cref="ParticleBurst.Step"/> /
/// <see cref="ParticleBurst.Count"/>.</summary>
public sealed class ParticleBurstDef
{
	[JsonPropertyName( "step" )] public int Step { get; set; }
	[JsonPropertyName( "count" )] public int Count { get; set; }
}

/// <summary>Inline triangle geometry for a MeshSurface emitter or a
/// settings collision mesh. Indices must be a multiple of 3.</summary>
public sealed class ParticleMeshDef
{
	[JsonPropertyName( "vertices" )] public List<float[]> Vertices { get; set; } = new();
	[JsonPropertyName( "indices" )] public List<int> Indices { get; set; } = new();

	public ParticleMesh ToMesh()
	{
		if ( Vertices is null || Indices is null )
			throw new ArgumentException( "particle mesh: vertices and indices must both be present." );
		var verts = new List<Vector3>( Vertices.Count );
		for ( int i = 0; i < Vertices.Count; i++ )
			verts.Add( ParticleAuthoring.Vec( Vertices[i], $"mesh vertex {i}" ) );
		try { return new ParticleMesh( verts, Indices ); }
		catch ( Exception e ) when ( e is ArgumentException or ArgumentOutOfRangeException )
		{ throw new ArgumentException( $"particle mesh: {e.Message}", e ); }
	}

	public static ParticleMeshDef FromMesh( ParticleMesh mesh )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		var def = new ParticleMeshDef();
		foreach ( var v in mesh.Vertices )
			def.Vertices.Add( new[] { v.X, v.Y, v.Z } );
		def.Indices.AddRange( mesh.Indices );
		return def;
	}
}

/// <summary>Settings half of a particle def. Every field defaults to the
/// live <see cref="ParticleSystemSettings"/> default when omitted from
/// JSON, so hand-written defs stay short. <see cref="ToSettings"/>
/// validates through the live Validate().</summary>
public sealed class ParticleSettingsDef
{
	[JsonPropertyName( "capacity" )] public int Capacity { get; set; } = 10_000;
	[JsonPropertyName( "seed" )] public uint Seed { get; set; } = 1;
	[JsonPropertyName( "fixedDeltaTime" )] public float FixedDeltaTime { get; set; } = 1f / 60f;
	[JsonPropertyName( "substeps" )] public int Substeps { get; set; } = 1;
	[JsonPropertyName( "gravity" )] public float[] Gravity { get; set; } = new[] { 0f, 0f, -386.0886f };
	[JsonPropertyName( "wind" )] public float[] Wind { get; set; } = new[] { 0f, 0f, 0f };
	[JsonPropertyName( "drag" )] public float Drag { get; set; }
	[JsonPropertyName( "turbulenceStrength" )] public float TurbulenceStrength { get; set; }
	[JsonPropertyName( "turbulenceFrequency" )] public float TurbulenceFrequency { get; set; } = 0.1f;
	[JsonPropertyName( "turbulenceSpeed" )] public float TurbulenceSpeed { get; set; }
	[JsonPropertyName( "vortexStrength" )] public float VortexStrength { get; set; }
	[JsonPropertyName( "vortexCenter" )] public float[] VortexCenter { get; set; } = new[] { 0f, 0f, 0f };
	[JsonPropertyName( "vortexAxis" )] public float[] VortexAxis { get; set; } = new[] { 0f, 0f, 1f };
	[JsonPropertyName( "vortexRadius" )] public float VortexRadius { get; set; } = 1f;
	[JsonPropertyName( "groundEnabled" )] public bool GroundEnabled { get; set; }
	[JsonPropertyName( "groundNormal" )] public float[] GroundNormal { get; set; } = new[] { 0f, 0f, 1f };
	[JsonPropertyName( "groundOffset" )] public float GroundOffset { get; set; }
	[JsonPropertyName( "collisionMesh" )] public ParticleMeshDef? CollisionMesh { get; set; }
	[JsonPropertyName( "collisionRadius" )] public float CollisionRadius { get; set; } = 0.01f;
	[JsonPropertyName( "restitution" )] public float Restitution { get; set; } = 0.3f;
	[JsonPropertyName( "friction" )] public float Friction { get; set; } = 0.2f;
	[JsonPropertyName( "renderType" )] public string RenderType { get; set; } = "Billboard";
	[JsonPropertyName( "strandPoints" )] public int StrandPoints { get; set; }

	public ParticleSystemSettings ToSettings()
	{
		var s = new ParticleSystemSettings
		{
			Capacity = Capacity,
			Seed = Seed,
			FixedDeltaTime = FixedDeltaTime,
			Substeps = Substeps,
			Gravity = ParticleAuthoring.Vec( Gravity, "settings.gravity" ),
			Wind = ParticleAuthoring.Vec( Wind, "settings.wind" ),
			Drag = Drag,
			TurbulenceStrength = TurbulenceStrength,
			TurbulenceFrequency = TurbulenceFrequency,
			TurbulenceSpeed = TurbulenceSpeed,
			VortexStrength = VortexStrength,
			VortexCenter = ParticleAuthoring.Vec( VortexCenter, "settings.vortexCenter" ),
			VortexAxis = ParticleAuthoring.Vec( VortexAxis, "settings.vortexAxis" ),
			VortexRadius = VortexRadius,
			GroundEnabled = GroundEnabled,
			GroundNormal = ParticleAuthoring.Vec( GroundNormal, "settings.groundNormal" ),
			GroundOffset = GroundOffset,
			CollisionMesh = CollisionMesh?.ToMesh(),
			CollisionRadius = CollisionRadius,
			Restitution = Restitution,
			Friction = Friction,
			RenderType = ParticleAuthoring.ParseEnum<ParticleRenderType>( RenderType, "settings.renderType" ),
			StrandPoints = StrandPoints,
		};
		try { s.Validate(); }
		catch ( Exception e ) when ( e is ArgumentException or ArgumentOutOfRangeException )
		{ throw new ArgumentException( $"particle settings: {e.Message}", e ); }
		return s;
	}

	public static ParticleSettingsDef FromSettings( ParticleSystemSettings s )
	{
		ArgumentNullException.ThrowIfNull( s );
		return new ParticleSettingsDef
		{
			Capacity = s.Capacity,
			Seed = s.Seed,
			FixedDeltaTime = s.FixedDeltaTime,
			Substeps = s.Substeps,
			Gravity = ParticleAuthoring.Arr( s.Gravity ),
			Wind = ParticleAuthoring.Arr( s.Wind ),
			Drag = s.Drag,
			TurbulenceStrength = s.TurbulenceStrength,
			TurbulenceFrequency = s.TurbulenceFrequency,
			TurbulenceSpeed = s.TurbulenceSpeed,
			VortexStrength = s.VortexStrength,
			VortexCenter = ParticleAuthoring.Arr( s.VortexCenter ),
			VortexAxis = ParticleAuthoring.Arr( s.VortexAxis ),
			VortexRadius = s.VortexRadius,
			GroundEnabled = s.GroundEnabled,
			GroundNormal = ParticleAuthoring.Arr( s.GroundNormal ),
			GroundOffset = s.GroundOffset,
			CollisionMesh = s.CollisionMesh is null ? null : ParticleMeshDef.FromMesh( s.CollisionMesh ),
			CollisionRadius = s.CollisionRadius,
			Restitution = s.Restitution,
			Friction = s.Friction,
			RenderType = s.RenderType.ToString(),
			StrandPoints = s.StrandPoints,
		};
	}
}

/// <summary>Emitter half of a particle def. Every field is nullable: null
/// means "keep the preset's value" on a preset emitter, or "the live
/// default" on a plain emitter. That is what makes a preset line like
/// {"preset": "sparks", "position": [0, 0, 10]} work without re-stating
/// the preset's lifetime, size, and burst — and what makes a preset
/// emitter in a def different from a fully explicit one.
/// <see cref="ToEmitter"/> validates the result through AddEmitter at
/// build time, so a MeshSurface emitter with no mesh fails there, loudly,
/// instead of building a system that can never emit.</summary>
public sealed class ParticleEmitterDef
{
	[JsonPropertyName( "name" )] public string? Name { get; set; }
	[JsonPropertyName( "preset" )] public string? Preset { get; set; }
	[JsonPropertyName( "shape" )] public string? Shape { get; set; }
	[JsonPropertyName( "position" )] public float[]? Position { get; set; }
	[JsonPropertyName( "areaU" )] public float[]? AreaU { get; set; }
	[JsonPropertyName( "areaV" )] public float[]? AreaV { get; set; }
	[JsonPropertyName( "boxHalfExtents" )] public float[]? BoxHalfExtents { get; set; }
	[JsonPropertyName( "sphereRadius" )] public float? SphereRadius { get; set; }
	[JsonPropertyName( "mesh" )] public ParticleMeshDef? Mesh { get; set; }
	[JsonPropertyName( "rate" )] public float? Rate { get; set; }
	[JsonPropertyName( "bursts" )] public List<ParticleBurstDef>? Bursts { get; set; }
	[JsonPropertyName( "lifetimeMin" )] public float? LifetimeMin { get; set; }
	[JsonPropertyName( "lifetimeMax" )] public float? LifetimeMax { get; set; }
	[JsonPropertyName( "sizeMin" )] public float? SizeMin { get; set; }
	[JsonPropertyName( "sizeMax" )] public float? SizeMax { get; set; }
	[JsonPropertyName( "direction" )] public float[]? Direction { get; set; }
	[JsonPropertyName( "useSurfaceNormal" )] public bool? UseSurfaceNormal { get; set; }
	[JsonPropertyName( "speed" )] public float? Speed { get; set; }
	[JsonPropertyName( "speedJitter" )] public float? SpeedJitter { get; set; }
	[JsonPropertyName( "spreadDegrees" )] public float? SpreadDegrees { get; set; }
	[JsonPropertyName( "baseVelocity" )] public float[]? BaseVelocity { get; set; }
	[JsonPropertyName( "angularSpeedMax" )] public float? AngularSpeedMax { get; set; }
	[JsonPropertyName( "enabled" )] public bool? Enabled { get; set; }

	public ParticleEmitter ToEmitter()
	{
		ParticleEmitter em;
		if ( Preset is not null )
		{
			string canonical = ParticleEmitterPresets.ResolveName( Preset )
				?? throw new ArgumentException( $"particle emitter: unknown preset '{Preset}'. Presets: {string.Join( ", ", ParticleEmitterPresets.Names )}." );
			var origin = Position is null ? new Vector3( 0, 0, 0 ) : ParticleAuthoring.Vec( Position, "emitter.position" );
			em = ParticleEmitterPresets.Create( canonical, origin,
				Direction is null ? null : ParticleAuthoring.Vec( Direction, "emitter.direction" ) );
			// Create() leaves Bursts at the preset's step-0 burst; a def that
			// names bursts replaces them wholesale, even with an empty list.
			if ( Bursts is not null )
				em.Bursts = ToBursts( Bursts );
		}
		else
		{
			em = new ParticleEmitter();
			if ( Bursts is not null )
				em.Bursts = ToBursts( Bursts );
		}
		if ( Shape is not null )
			em.Shape = ParticleAuthoring.ParseEnum<ParticleEmitterShape>( Shape, "emitter.shape" );
		if ( Position is not null && Preset is null )
			em.Position = ParticleAuthoring.Vec( Position, "emitter.position" );
		if ( AreaU is not null ) em.AreaU = ParticleAuthoring.Vec( AreaU, "emitter.areaU" );
		if ( AreaV is not null ) em.AreaV = ParticleAuthoring.Vec( AreaV, "emitter.areaV" );
		if ( BoxHalfExtents is not null ) em.BoxHalfExtents = ParticleAuthoring.Vec( BoxHalfExtents, "emitter.boxHalfExtents" );
		if ( SphereRadius is not null ) em.SphereRadius = SphereRadius.Value;
		if ( Mesh is not null ) em.Mesh = Mesh.ToMesh();
		if ( Rate is not null ) em.Rate = Rate.Value;
		if ( LifetimeMin is not null ) em.LifetimeMin = LifetimeMin.Value;
		if ( LifetimeMax is not null ) em.LifetimeMax = LifetimeMax.Value;
		if ( SizeMin is not null ) em.SizeMin = SizeMin.Value;
		if ( SizeMax is not null ) em.SizeMax = SizeMax.Value;
		if ( Direction is not null && Preset is null )
			em.Direction = ParticleAuthoring.Vec( Direction, "emitter.direction" );
		if ( UseSurfaceNormal is not null ) em.UseSurfaceNormal = UseSurfaceNormal.Value;
		if ( Speed is not null ) em.Speed = Speed.Value;
		if ( SpeedJitter is not null ) em.SpeedJitter = SpeedJitter.Value;
		if ( SpreadDegrees is not null ) em.SpreadDegrees = SpreadDegrees.Value;
		if ( BaseVelocity is not null ) em.BaseVelocity = ParticleAuthoring.Vec( BaseVelocity, "emitter.baseVelocity" );
		if ( AngularSpeedMax is not null ) em.AngularSpeedMax = AngularSpeedMax.Value;
		if ( Enabled is not null ) em.Enabled = Enabled.Value;
		if ( em.Shape == ParticleEmitterShape.MeshSurface && em.Mesh is null )
			throw new ArgumentException( "particle emitter: MeshSurface shape needs a \"mesh\" (vertices + indices) in the def." );
		return em;
	}

	static List<ParticleBurst> ToBursts( List<ParticleBurstDef> defs )
	{
		var bursts = new List<ParticleBurst>( defs.Count );
		foreach ( var b in defs )
		{
			if ( b is null ) throw new ArgumentException( "particle emitter: burst entries must be {\"step\": N, \"count\": N}." );
			bursts.Add( new ParticleBurst( b.Step, b.Count ) );
		}
		return bursts;
	}

	public static ParticleEmitterDef FromEmitter( ParticleEmitter em )
	{
		ArgumentNullException.ThrowIfNull( em );
		var def = new ParticleEmitterDef
		{
			// Preset deliberately null: preset is a construction shortcut,
			// not state (see the file header). Fields carry the values.
			Shape = em.Shape.ToString(),
			Position = ParticleAuthoring.Arr( em.Position ),
			AreaU = ParticleAuthoring.Arr( em.AreaU ),
			AreaV = ParticleAuthoring.Arr( em.AreaV ),
			BoxHalfExtents = ParticleAuthoring.Arr( em.BoxHalfExtents ),
			SphereRadius = em.SphereRadius,
			Mesh = em.Mesh is null ? null : ParticleMeshDef.FromMesh( em.Mesh ),
			Rate = em.Rate,
			LifetimeMin = em.LifetimeMin,
			LifetimeMax = em.LifetimeMax,
			SizeMin = em.SizeMin,
			SizeMax = em.SizeMax,
			Direction = ParticleAuthoring.Arr( em.Direction ),
			UseSurfaceNormal = em.UseSurfaceNormal,
			Speed = em.Speed,
			SpeedJitter = em.SpeedJitter,
			SpreadDegrees = em.SpreadDegrees,
			BaseVelocity = ParticleAuthoring.Arr( em.BaseVelocity ),
			AngularSpeedMax = em.AngularSpeedMax,
			Enabled = em.Enabled,
		};
		def.Bursts = new List<ParticleBurstDef>();
		foreach ( var b in em.Bursts )
			def.Bursts.Add( new ParticleBurstDef { Step = b.Step, Count = b.Count } );
		return def;
	}
}

/// <summary>A whole particle system as data: versioned schema, settings,
/// emitters. <see cref="Build"/> validates through the live types, so a
/// def that builds is a system that runs.</summary>
public sealed class ParticleSystemDef
{
	[JsonPropertyName( "version" )] public int Version { get; set; } = 1;
	[JsonPropertyName( "name" )] public string? Name { get; set; }
	[JsonPropertyName( "settings" )] public ParticleSettingsDef Settings { get; set; } = new();
	[JsonPropertyName( "emitters" )] public List<ParticleEmitterDef> Emitters { get; set; } = new();

	public ParticleSystem Build()
	{
		if ( Version != 1 )
			throw new ArgumentException( $"particle def: unsupported version {Version} (this build reads version 1)." );
		if ( Settings is null )
			throw new ArgumentException( "particle def: settings must be present." );
		if ( Emitters is null )
			throw new ArgumentException( "particle def: emitters must be present (an empty list is a valid, emitterless system)." );
		var system = new ParticleSystem( Settings.ToSettings() );
		for ( int i = 0; i < Emitters.Count; i++ )
		{
			if ( Emitters[i] is null )
				throw new ArgumentException( $"particle def: emitter {i} is null." );
			try { system.AddEmitter( Emitters[i].ToEmitter() ); }
			catch ( Exception e ) when ( e is ArgumentException or ArgumentOutOfRangeException )
			{ throw new ArgumentException( $"particle def emitter {i}: {e.Message}", e ); }
		}
		return system;
	}

	public static ParticleSystemDef FromSystem( ParticleSystem system, string? name = null )
	{
		ArgumentNullException.ThrowIfNull( system );
		var def = new ParticleSystemDef { Name = name, Settings = ParticleSettingsDef.FromSettings( system.Settings ) };
		foreach ( var em in system.Emitters )
			def.Emitters.Add( ParticleEmitterDef.FromEmitter( em ) );
		return def;
	}
}

/// <summary>JSON in and out of <see cref="ParticleSystemDef"/>, plus the
/// vector/enum helpers the def types share. Unknown JSON fields are
/// ignored (forward compat); a missing or wrong "version", malformed
/// JSON, or any invalid value fails loudly.</summary>
public static class ParticleAuthoring
{
	public const int CurrentVersion = 1;

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
	};

	public static string ToJson( ParticleSystemDef def )
	{
		ArgumentNullException.ThrowIfNull( def );
		return JsonSerializer.Serialize( def, JsonOptions );
	}

	public static ParticleSystemDef FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "particle def: empty JSON." );
		try
		{
			using var doc = JsonDocument.Parse( json );
			if ( doc.RootElement.ValueKind != JsonValueKind.Object )
				throw new ArgumentException( "particle def: top level must be a JSON object." );
			if ( !doc.RootElement.TryGetProperty( "version", out var version ) )
				throw new ArgumentException( "particle def: missing \"version\" (this build reads version 1)." );
			if ( version.ValueKind != JsonValueKind.Number || !version.TryGetInt32( out int v ) || v != CurrentVersion )
				throw new ArgumentException( $"particle def: unsupported version '{version}' (this build reads version 1)." );
		}
		catch ( JsonException e )
		{ throw new ArgumentException( $"particle def: malformed JSON: {e.Message}", e ); }
		try { return JsonSerializer.Deserialize<ParticleSystemDef>( json, JsonOptions )
			?? throw new ArgumentException( "particle def: JSON deserialized to null." ); }
		catch ( JsonException e )
		{ throw new ArgumentException( $"particle def: malformed JSON: {e.Message}", e ); }
	}

	public static void SaveFile( ParticleSystemDef def, string path )
	{
		ArgumentNullException.ThrowIfNull( def );
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "particle def: save path must not be empty." );
		File.WriteAllText( path, ToJson( def ) );
	}

	public static ParticleSystemDef LoadFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "particle def: load path must not be empty." );
		try { return FromJson( File.ReadAllText( path ) ); }
		catch ( IOException e )
		{ throw new ArgumentException( $"particle def: cannot read '{path}': {e.Message}", e ); }
	}

	public static ParticleSystem BuildFile( string path ) => LoadFile( path ).Build();

	/// <summary>Save a live system as a def file (the sidecar route: write
	/// this next to the .effigy, since .effigy itself has no particle
	/// slot — see the file header).</summary>
	public static void SaveSystem( ParticleSystem system, string path, string? name = null )
		=> SaveFile( ParticleSystemDef.FromSystem( system, name ), path );

	internal static Vector3 Vec( float[]? v, string field )
	{
		if ( v is null || v.Length != 3 )
			throw new ArgumentException( $"particle def: {field} must be a [x, y, z] array." );
		var vec = new Vector3( v[0], v[1], v[2] );
		if ( !ParticleMath.Finite( vec ) )
			throw new ArgumentException( $"particle def: {field} must be finite." );
		return vec;
	}

	internal static float[] Arr( Vector3 v ) => new[] { v.X, v.Y, v.Z };

	internal static T ParseEnum<T>( string? value, string field ) where T : struct, Enum
	{
		if ( value is null || !Enum.TryParse<T>( value, ignoreCase: true, out var parsed ) )
			throw new ArgumentException( $"particle def: {field} '{value}' is not one of: {string.Join( ", ", Enum.GetNames<T>() )}." );
		return parsed;
	}
}

/// <summary>What one `particle` verb line produced: the def, the built
/// system (null when the line only saved a def), and the save path
/// (null when the line built instead).</summary>
public sealed class ParticleVerbResult
{
	public ParticleSystemDef Def { get; set; } = new();
	public ParticleSystem? System { get; set; }
	public string? SavedPath { get; set; }
}

/// <summary>The `particle` script verb, desktop-side. Syntax (values are
/// key=value pairs, vectors comma-separated, no spaces inside a value):
///   particle preset=sparks at=0,0,10 [rate=.. speed=.. spread=.. ...]
///   particle file=defs/fountain.json [at=0,0,10 rate=..]
///   particle preset=dust_motes save=defs/dust.json
/// `at` is an alias for `position`. `burst` is step:count pairs,
/// comma-separated ("0:120,30:50"). `lifetime` / `size` take a single
/// value (min = max) or a min:max pair. Settings keys (seed, capacity,
/// gravity, drag, groundEnabled, ...) override the def's settings;
/// emitter keys (rate, speed, spread, direction, ...) override the
/// def's single emitter — a multi-emitter def with emitter keys fails
/// loudly rather than guessing which emitter was meant. A preset line
/// starts from SettingsFor, so the preset's medium applies unless a
/// settings key overrides it. Unknown keys, a missing preset/file, and
/// preset+file together all fail loudly. This parser is deliberately
/// independent of the kernel BuildScript tokenizer: the kernel cannot
/// call desktop code (see the file header), so the sweep wires this in
/// as a desktop pre-pass, not a kernel case.</summary>
public static class ParticleVerb
{
	public const string Usage =
		"particle preset=<name> at=x,y,z [rate=.. speed=.. spread=.. ...] | " +
		"particle file=<path.json> [at=.. rate=..] | " +
		"particle preset=<name> save=<path.json>";

	static readonly HashSet<string> SettingsKeys = new( StringComparer.OrdinalIgnoreCase )
	{
		"seed", "capacity", "fixedDeltaTime", "substeps", "gravity", "wind", "drag",
		"turbulenceStrength", "turbulenceFrequency", "turbulenceSpeed",
		"vortexStrength", "vortexCenter", "vortexAxis", "vortexRadius",
		"groundEnabled", "groundNormal", "groundOffset",
		"collisionRadius", "restitution", "friction", "renderType", "strandPoints",
	};

	static readonly HashSet<string> EmitterKeys = new( StringComparer.OrdinalIgnoreCase )
	{
		"at", "position", "direction", "shape", "areaU", "areaV", "box", "boxHalfExtents",
		"sphereRadius", "rate", "burst", "lifetime", "lifetimeMin", "lifetimeMax",
		"size", "sizeMin", "sizeMax", "speed", "speedJitter", "spread", "spreadDegrees",
		"baseVelocity", "angularSpeed", "angularSpeedMax", "useSurfaceNormal", "enabled",
	};

	public static ParticleSystemDef ParseLine( string line, string? baseDir = null )
	{
		if ( string.IsNullOrWhiteSpace( line ) )
			throw new ArgumentException( $"particle verb: empty line. {Usage}" );
		var tokens = Tokenize( line );
		if ( tokens.Count == 0 || !tokens[0].Equals( "particle", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( $"particle verb: line must start with 'particle'. {Usage}" );
		var args = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		for ( int i = 1; i < tokens.Count; i++ )
		{
			int eq = tokens[i].IndexOf( '=' );
			if ( eq <= 0 )
				throw new ArgumentException( $"particle verb: token '{tokens[i]}' is not key=value. {Usage}" );
			string key = tokens[i][..eq], value = tokens[i][( eq + 1 )..];
			if ( string.IsNullOrWhiteSpace( value ) )
				throw new ArgumentException( $"particle verb: key '{key}' needs a value. {Usage}" );
			if ( !args.TryAdd( key, value ) )
				throw new ArgumentException( $"particle verb: duplicate key '{key}'." );
		}
		args.TryGetValue( "preset", out string? preset );
		args.TryGetValue( "file", out string? file );
		if ( preset is not null && file is not null )
			throw new ArgumentException( "particle verb: preset= and file= together are ambiguous; use one." );
		if ( preset is null && file is null )
			throw new ArgumentException( $"particle verb: need preset=<name> or file=<path.json>. {Usage}" );

		ParticleSystemDef def;
		if ( preset is not null )
		{
			string canonical = ParticleEmitterPresets.ResolveName( preset )
				?? throw new ArgumentException( $"particle verb: unknown preset '{preset}'. Presets: {string.Join( ", ", ParticleEmitterPresets.Names )}." );
			def = new ParticleSystemDef
			{
				Settings = ParticleSettingsDef.FromSettings(
					ParticleEmitterPresets.SettingsFor( canonical, new ParticleSystemSettings() ) ),
			};
			def.Emitters.Add( new ParticleEmitterDef { Preset = canonical } );
		}
		else
		{
			def = ParticleAuthoring.LoadFile( Resolve( file!, baseDir ) );
		}
		if ( args.TryGetValue( "name", out string? name ) ) def.Name = name;

		foreach ( var (key, value) in args )
		{
			if ( key.Equals( "preset", StringComparison.OrdinalIgnoreCase )
				|| key.Equals( "file", StringComparison.OrdinalIgnoreCase )
				|| key.Equals( "save", StringComparison.OrdinalIgnoreCase )
				|| key.Equals( "name", StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( SettingsKeys.Contains( key ) ) ApplySettings( def.Settings, key, value );
			else if ( EmitterKeys.Contains( key ) ) ApplyEmitter( SingleEmitter( def ), key, value );
			else throw new ArgumentException( $"particle verb: unknown key '{key}'. {Usage}" );
		}
		return def;
	}

	public static ParticleVerbResult Run( string line, string? baseDir = null )
	{
		var def = ParseLine( line, baseDir );
		// Re-parse just the save= key (ParseLine ignores it on purpose: a
		// save line is still a valid def line, and Run is what acts on it).
		string? save = null;
		foreach ( var token in Tokenize( line ) )
		{
			int eq = token.IndexOf( '=' );
			if ( eq > 0 && token[..eq].Equals( "save", StringComparison.OrdinalIgnoreCase ) )
				save = token[( eq + 1 )..];
		}
		if ( save is not null )
		{
			string path = Resolve( save, baseDir );
			ParticleAuthoring.SaveFile( def, path );
			return new ParticleVerbResult { Def = def, System = null, SavedPath = path };
		}
		return new ParticleVerbResult { Def = def, System = def.Build() };
	}

	static ParticleEmitterDef SingleEmitter( ParticleSystemDef def )
	{
		if ( def.Emitters is null || def.Emitters.Count != 1 )
			throw new ArgumentException( "particle verb: emitter keys (rate, at, speed, ...) need a def with exactly one emitter." );
		return def.Emitters[0];
	}

	static string Resolve( string path, string? baseDir )
		=> Path.IsPathRooted( path ) ? path : Path.Combine( baseDir ?? Directory.GetCurrentDirectory(), path );

	static List<string> Tokenize( string line )
	{
		var tokens = new List<string>();
		var cur = new System.Text.StringBuilder();
		bool quoted = false, inToken = false;
		foreach ( char c in line )
		{
			if ( c == '"' ) { quoted = !quoted; inToken = true; continue; }
			if ( !quoted && char.IsWhiteSpace( c ) )
			{
				if ( inToken ) { tokens.Add( cur.ToString() ); cur.Clear(); inToken = false; }
				continue;
			}
			cur.Append( c );
			inToken = true;
		}
		if ( quoted ) throw new ArgumentException( "particle verb: unterminated quote." );
		if ( inToken ) tokens.Add( cur.ToString() );
		return tokens;
	}

	static float F( string key, string value )
	{
		if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f ) || !float.IsFinite( f ) )
			throw new ArgumentException( $"particle verb: key '{key}' needs a finite number, got '{value}'." );
		return f;
	}

	static int I( string key, string value )
	{
		if ( !int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i ) )
			throw new ArgumentException( $"particle verb: key '{key}' needs an integer, got '{value}'." );
		return i;
	}

	static uint U( string key, string value )
	{
		if ( !uint.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u ) )
			throw new ArgumentException( $"particle verb: key '{key}' needs a non-negative integer, got '{value}'." );
		return u;
	}

	static bool B( string key, string value )
	{
		if ( value.Equals( "true", StringComparison.OrdinalIgnoreCase ) || value == "1" ) return true;
		if ( value.Equals( "false", StringComparison.OrdinalIgnoreCase ) || value == "0" ) return false;
		throw new ArgumentException( $"particle verb: key '{key}' needs true/false, got '{value}'." );
	}

	static float[] V( string key, string value )
	{
		var parts = value.Split( ',' );
		if ( parts.Length != 3 )
			throw new ArgumentException( $"particle verb: key '{key}' needs x,y,z, got '{value}'." );
		return new[] { F( key, parts[0] ), F( key, parts[1] ), F( key, parts[2] ) };
	}

	static (float, float) MinMax( string key, string value )
	{
		var parts = value.Split( ':' );
		if ( parts.Length == 1 ) { float v = F( key, parts[0] ); return (v, v); }
		if ( parts.Length == 2 ) return (F( key, parts[0] ), F( key, parts[1] ));
		throw new ArgumentException( $"particle verb: key '{key}' needs a value or min:max, got '{value}'." );
	}

	static void ApplySettings( ParticleSettingsDef s, string key, string value )
	{
		switch ( key.ToLowerInvariant() )
		{
			case "seed": s.Seed = U( key, value ); break;
			case "capacity": s.Capacity = I( key, value ); break;
			case "fixedDeltaTime": s.FixedDeltaTime = F( key, value ); break;
			case "substeps": s.Substeps = I( key, value ); break;
			case "gravity": s.Gravity = V( key, value ); break;
			case "wind": s.Wind = V( key, value ); break;
			case "drag": s.Drag = F( key, value ); break;
			case "turbulenceStrength": s.TurbulenceStrength = F( key, value ); break;
			case "turbulenceFrequency": s.TurbulenceFrequency = F( key, value ); break;
			case "turbulenceSpeed": s.TurbulenceSpeed = F( key, value ); break;
			case "vortexStrength": s.VortexStrength = F( key, value ); break;
			case "vortexCenter": s.VortexCenter = V( key, value ); break;
			case "vortexAxis": s.VortexAxis = V( key, value ); break;
			case "vortexRadius": s.VortexRadius = F( key, value ); break;
			case "groundEnabled": s.GroundEnabled = B( key, value ); break;
			case "groundNormal": s.GroundNormal = V( key, value ); break;
			case "groundOffset": s.GroundOffset = F( key, value ); break;
			case "collisionRadius": s.CollisionRadius = F( key, value ); break;
			case "restitution": s.Restitution = F( key, value ); break;
			case "friction": s.Friction = F( key, value ); break;
			case "renderType": _ = ParticleAuthoring.ParseEnum<ParticleRenderType>( value, "renderType" ); s.RenderType = value; break;
			case "strandPoints": s.StrandPoints = I( key, value ); break;
			default: throw new ArgumentException( $"particle verb: unknown settings key '{key}'." );
		}
	}

	static void ApplyEmitter( ParticleEmitterDef em, string key, string value )
	{
		switch ( key.ToLowerInvariant() )
		{
			case "at":
			case "position": em.Position = V( key, value ); break;
			case "direction": em.Direction = V( key, value ); break;
			case "shape": _ = ParticleAuthoring.ParseEnum<ParticleEmitterShape>( value, "shape" ); em.Shape = value; break;
			case "areaU": em.AreaU = V( key, value ); break;
			case "areaV": em.AreaV = V( key, value ); break;
			case "box":
			case "boxHalfExtents": em.BoxHalfExtents = V( key, value ); break;
			case "sphereRadius": em.SphereRadius = F( key, value ); break;
			case "rate": em.Rate = F( key, value ); break;
			case "burst":
			{
				var bursts = new List<ParticleBurstDef>();
				foreach ( var pair in value.Split( ',' ) )
				{
					var parts = pair.Split( ':' );
					if ( parts.Length != 2 )
						throw new ArgumentException( $"particle verb: burst needs step:count pairs, got '{value}'." );
					bursts.Add( new ParticleBurstDef { Step = I( key, parts[0] ), Count = I( key, parts[1] ) } );
				}
				em.Bursts = bursts;
				break;
			}
			case "lifetime": { var (a, b) = MinMax( key, value ); em.LifetimeMin = a; em.LifetimeMax = b; break; }
			case "lifetimeMin": em.LifetimeMin = F( key, value ); break;
			case "lifetimeMax": em.LifetimeMax = F( key, value ); break;
			case "size": { var (a, b) = MinMax( key, value ); em.SizeMin = a; em.SizeMax = b; break; }
			case "sizeMin": em.SizeMin = F( key, value ); break;
			case "sizeMax": em.SizeMax = F( key, value ); break;
			case "speed": em.Speed = F( key, value ); break;
			case "speedJitter": em.SpeedJitter = F( key, value ); break;
			case "spread":
			case "spreadDegrees": em.SpreadDegrees = F( key, value ); break;
			case "baseVelocity": em.BaseVelocity = V( key, value ); break;
			case "angularSpeed":
			case "angularSpeedMax": em.AngularSpeedMax = F( key, value ); break;
			case "useSurfaceNormal": em.UseSurfaceNormal = B( key, value ); break;
			case "enabled": em.Enabled = B( key, value ); break;
			default: throw new ArgumentException( $"particle verb: unknown emitter key '{key}'." );
		}
	}
}
