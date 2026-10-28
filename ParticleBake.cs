#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Marionette.Desktop;

// Particles lane P39 (bake/cache, 2026-10-24) — definition-hash-keyed bake of a
// 3D particle sim to P40 frames, plus an in-memory cache. Units are
// ParticleSystem's: kernel inches, Z-up, seconds, the definition's
// FixedDeltaTime fixed step (60 Hz by default).
//
// ADAPTATION NOTE (re-spawn, 2026-10-24): a first P39 agent left a complete
// ParticleBake.cs + tests in the local workspace but errored before any
// report or scratch run. That draft is the base of this file — its binary
// canonical hash, live-system hash, and FIFO cache counters are kept —
// but it is NOT verified as-is, because it diverged from the brief in
// ways that break composition: it invented its own BakedFrame type
// (with rotation/angular velocity/slot/id) instead of feeding P40's
// format, its Bake() took no frameRate, its cache key had no frameRate,
// it allowed a 0-step bake, and it had no missing-seed JSON check.
// This version fixes exactly those divergences (plus a real bug in
// CacheClear, below) and keeps everything else.
//
// WHAT THIS IS: Bake() steps a FRESH system built from a lane-5
// ParticleSystemDef (schema v1) and returns P40 frames — one
// ParticleBakeFrame per captured step, built ONLY by calling
// ParticleBakeExport.FromStates(), never by redefining the format.
// BakeDump() wraps those frames with ParticleBakeExport.Create() so a
// bake feeds P40's SaveFile / LoadFile with no translation layer.
// This file does NOT draw anything, does NOT write files itself, and
// does NOT wire bake into the viewport, cues, or the renderer.
//
// DEFINITION HASH (DefinitionHash):
//   SHA-256 (lowercase hex) over a canonical byte encoding of the
//   definition, built field by field in a fixed order (see
//   CanonicalWriter): version, name, every settings field, then every
//   emitter field in order, then every burst / mesh element in order.
//   Integers are little-endian bytes; floats are their IEEE-754 bit
//   patterns (with -0.0 normalised to +0.0 and NaN normalised to one
//   fixed pattern, so equal values hash equal even if they arrived by
//   different arithmetic); strings are length-prefixed UTF-8 (-1 length
//   = null, so null and "" differ, and null and an empty list differ);
//   bools and nullable fields carry an explicit null marker. No
//   GetHashCode, no pointer, no dictionary order, no culture-sensitive
//   formatting anywhere — the same definition hashes the same on every
//   run and every machine. Emitter ORDER matters: the sim seeds one RNG
//   stream per emitter by index, so swapping two emitters is a different
//   sim and a different hash.
//   The hash is over the definition AS WRITTEN, not over the system it
//   builds: two defs that happen to build identical systems (e.g. a
//   preset name vs the same fields spelled out) hash differently and
//   bake twice. That is deliberate — the key is definition identity, and
//   a cache that silently merged distinct definitions would be a lie
//   about what was baked. Labels count: the def name, emitter names,
//   and preset names are part of the hash, because a definition change
//   is a definition change even when the sim it builds is unchanged.
//   DefinitionHash is a PURE data hash: it does not validate. An invalid
//   def (bad version, bad values) still hashes; Validate()/Bake() are the
//   ones that reject it, loudly.
//   Collision behaviour, picked and stated: a cache entry stores its
//   definition's canonical bytes ALONGSIDE the hash, and a hit verifies
//   byte equality before returning frames. A SHA-256 collision is
//   cryptographically negligible, and if one ever occurred the verify
//   step would turn it into a re-bake, never a wrong hit. Bare-hash
//   equality is NOT accepted by the cache.
//   KNOWN GAP, stated not hidden: the lane-5 schema carries no kill
//   volumes and no sub-emitters, so a def hash cannot see them. A live
//   system that gained kill volumes or sub-emitters after Build() is NOT
//   the same sim as its def, and its def hash does not know. The
//   DefinitionHash(ParticleSystem) overload exists for that case: it
//   hashes the live system's settings (including kill volumes) and
//   emitters (including sub-emitter configs, template chains depth-capped
//   at 32 like Validate), but NOT runtime state (RNG position, rate
//   remainder, slot state) — so a mid-sim system hashes the same as a
//   fresh system with the same configuration.
//
// BAKE SEMANTICS (Bake), exactly:
//   Bake(def, steps, frameRate, frameStride = 1) validates, builds a
//   fresh system, and steps it `steps` times. A frame is captured after
//   every `frameStride`-th step, plus one final frame after the last
//   step when `steps` is not a multiple of `frameStride`. Default stride
//   1 therefore gives exactly `steps` frames. Frame indices are
//   sequential from 0 (P40 requires it). There is no pre-step t=0
//   frame — a burst at step 0 first exists in frame 0, which is what a
//   timeline wants.
//   frameRate does NOT resample the sim. The sim always advances at the
//   definition's FixedDeltaTime. frameRate is the playback rate stamped
//   on a P40 dump (BakeDump) and part of the cache key. A caller that
//   wants a lower-rate dump from a 60 Hz sim passes a frameStride (e.g.
//   stride 2 for a 30 fps dump) and the matching frameRate; this file
//   never silently drops or interpolates frames to fake a rate.
//   Baking the same definition twice gives bit-identical frames: same
//   seed, same fixed step, same RNG streams, no wall clock, no
//   System.Random anywhere in the path. Tests compare floats with ==,
//   not within tolerance.
//   STORED per frame, per ALIVE particle, in slot order (P40's format):
//   position, velocity, size, age, alive flag. Rotation and angular
//   velocity are NOT in baked frames: P40's format does not carry them.
//   A bake is a playback record, not a full ParticleState replay and
//   not a resumable sim: a particle that died between frames leaves no
//   record beyond its absence from the next frame. DROPPED, by design:
//   dead slots, the strand history ring buffer, per-slot lifetime /
//   death-step internals, death/collision events, the
//   DroppedForCapacity count, and emitter runtime state.
//
// CACHE (ParticleBakeCache):
//   Key = (DefinitionHash(def), steps, frameRate bits, frameStride) —
//   the FULL bake identity. The same def at a different step count, a
//   different frameRate, or a different stride is a DIFFERENT entry,
//   never a wrong-length hit. A hit returns the SAME list instance that
//   was stored (reference-identical; callers must not mutate it) and
//   does not re-simulate: StepsSimulated only grows on misses.
//   Eviction is FIFO: when the cache is full, the OLDEST-inserted entry
//   is evicted first; a hit does NOT refresh recency (this is not LRU
//   — FIFO is the documented, testable policy). The bound counts ENTRIES
//   (whole bakes), not frames or particles: a 1000-frame bake is one
//   entry. Default bound is 8 entries. CacheClear() empties the entries
//   AND the insertion order (the first draft cleared entries only, which
//   left stale order keys that could evict a live re-added entry —
//   fixed here, pinned by a test). Hit/miss/step counters are lifetime
//   counters and are NOT reset by CacheClear() — they are the audit
//   trail. The cache is NOT thread-safe, matching the sim itself.
//   The cache is IN-MEMORY ONLY: no disk persistence here beyond P40's
//   SaveFile, and a file-cache extension is explicitly out of scope.
//
// VALIDATE:
//   Validate(def, steps, frameRate, frameStride) rejects, in order: a
//   null def, non-positive steps, a non-finite or non-positive
//   frameRate, a non-positive frameStride, a non-1 version, missing
//   settings/emitters, a null emitter entry, and anything
//   ParticleSystem's own Validate() semantics reject — settings through
//   ParticleSettingsDef.ToSettings() (which runs
//   ParticleSystemSettings.Validate()), each emitter through its live
//   Validate(index) after ToEmitter() (unknown preset/shape, bad rate /
//   lifetime / size / speed / spread, MeshSurface without a mesh, ...).
//   Bake() re-validates through Build() anyway, so a def that passes
//   Validate() builds.
//   A definition whose seed is MISSING is an Error — but only the JSON
//   entry points can see that: a ParticleSystemDef object always
//   carries a Seed value (lane 5 defaults it to 1), so object-form
//   Validate cannot distinguish "seed 1" from "seed omitted".
//   BakeFromJson / BakeDumpFromJson therefore require a "seed" property
//   inside "settings" and throw when it is absent, before lane-5
//   deserialization would silently default it.
//
// SWEEP WIRING (coordinator only — this lane edits nothing else):
//   No viewport, script, or save wiring is done here. If the sweep wires
//   bake into playback, the call is
//   ParticleBakeCache.BakeCached(def, steps, frameRate) at the timeline
//   owner, and frames play back as ParticleBakeFrame.Particles
//   positions/sizes per frame index — the renderer change itself belongs
//   to a render lane, not this one. A bake dump for disk is
//   ParticleBake.BakeDump(def, steps, frameRate), saved with
//   ParticleBakeExport.SaveFile. Out of scope, restated: no file export
//   of baked frames beyond P40's SaveFile, no GPU baking, no definition
//   save/serialization (lane 5 owns that), no VMDL, no canvas/UI.

/// <summary>Definition hashing, validation, and uncached baking. See the file header for the contract.</summary>
public static class ParticleBake
{
	public static string DefinitionHash( ParticleSystemDef def )
	{
		ArgumentNullException.ThrowIfNull( def );
		return Convert.ToHexString( SHA256.HashData( CanonicalBytes( def ) ) ).ToLowerInvariant();
	}

	internal static byte[] CanonicalBytes( ParticleSystemDef def )
	{
		var w = new CanonicalWriter();
		w.Int( def.Version );
		w.Str( def.Name );
		if ( def.Settings is null ) w.Int( -1 );
		else
		{
			var s = def.Settings;
			w.Int( 1 );
			w.Int( s.Capacity );
			w.UInt( s.Seed );
			w.Float( s.FixedDeltaTime );
			w.Int( s.Substeps );
			w.Floats( s.Gravity );
			w.Floats( s.Wind );
			w.Float( s.Drag );
			w.Float( s.TurbulenceStrength );
			w.Float( s.TurbulenceFrequency );
			w.Float( s.TurbulenceSpeed );
			w.Float( s.VortexStrength );
			w.Floats( s.VortexCenter );
			w.Floats( s.VortexAxis );
			w.Float( s.VortexRadius );
			w.Bool( s.GroundEnabled );
			w.Floats( s.GroundNormal );
			w.Float( s.GroundOffset );
			w.MeshDef( s.CollisionMesh );
			w.Float( s.CollisionRadius );
			w.Float( s.Restitution );
			w.Float( s.Friction );
			w.Str( s.RenderType );
			w.Int( s.StrandPoints );
		}
		if ( def.Emitters is null ) w.Int( -1 );
		else
		{
			w.Int( def.Emitters.Count );
			foreach ( var e in def.Emitters )
			{
				if ( e is null ) { w.Int( -1 ); continue; }
				w.Int( 1 );
				w.Str( e.Name );
				w.Str( e.Preset );
				w.Str( e.Shape );
				w.Floats( e.Position );
				w.Floats( e.AreaU );
				w.Floats( e.AreaV );
				w.Floats( e.BoxHalfExtents );
				w.NFloat( e.SphereRadius );
				w.MeshDef( e.Mesh );
				w.NFloat( e.Rate );
				if ( e.Bursts is null ) w.Int( -1 );
				else
				{
					w.Int( e.Bursts.Count );
					foreach ( var b in e.Bursts )
					{
						if ( b is null ) { w.Int( -1 ); continue; }
						w.Int( 1 );
						w.Int( b.Step );
						w.Int( b.Count );
					}
				}
				w.NFloat( e.LifetimeMin );
				w.NFloat( e.LifetimeMax );
				w.NFloat( e.SizeMin );
				w.NFloat( e.SizeMax );
				w.Floats( e.Direction );
				w.NBool( e.UseSurfaceNormal );
				w.NFloat( e.Speed );
				w.NFloat( e.SpeedJitter );
				w.NFloat( e.SpreadDegrees );
				w.Floats( e.BaseVelocity );
				w.NFloat( e.AngularSpeedMax );
				w.NBool( e.Enabled );
			}
		}
		return w.Bytes();
	}

	/// <summary>Hash of a LIVE system: configuration only (settings incl. kill volumes, emitters incl. sub-emitters), never runtime state.</summary>
	public static string DefinitionHash( ParticleSystem system )
	{
		ArgumentNullException.ThrowIfNull( system );
		var w = new CanonicalWriter();
		var s = system.Settings;
		w.Int( s.Capacity );
		w.UInt( s.Seed );
		w.Float( s.FixedDeltaTime );
		w.Int( s.Substeps );
		w.Vec( s.Gravity );
		w.Vec( s.Wind );
		w.Float( s.Drag );
		w.Float( s.TurbulenceStrength );
		w.Float( s.TurbulenceFrequency );
		w.Float( s.TurbulenceSpeed );
		w.Float( s.VortexStrength );
		w.Vec( s.VortexCenter );
		w.Vec( s.VortexAxis );
		w.Float( s.VortexRadius );
		w.Bool( s.GroundEnabled );
		w.Vec( s.GroundNormal );
		w.Float( s.GroundOffset );
		w.Mesh( s.CollisionMesh );
		w.Float( s.CollisionRadius );
		w.Float( s.Restitution );
		w.Float( s.Friction );
		w.Int( (int)s.RenderType );
		w.Int( s.StrandPoints );
		w.Int( s.KillVolumes.Count );
		foreach ( var k in s.KillVolumes )
		{
			w.Int( (int)k.Kind );
			w.Vec( k.Normal );
			w.Float( k.Offset );
			w.Vec( k.Center );
			w.Float( k.Radius );
			w.Bool( k.KillInside );
		}
		w.Int( system.Emitters.Count );
		foreach ( var e in system.Emitters ) HashEmitter( w, e, new List<ParticleEmitter>(), 0 );
		return w.Hex();
	}

	static bool Seen( List<ParticleEmitter> seen, ParticleEmitter e )
	{
		foreach ( var s in seen ) if ( ReferenceEquals( s, e ) ) return true;
		return false;
	}

	static void HashEmitter( CanonicalWriter w, ParticleEmitter e, List<ParticleEmitter> seen, int depth )
	{
		w.Int( (int)e.Shape );
		w.Vec( e.Position );
		w.Vec( e.AreaU );
		w.Vec( e.AreaV );
		w.Vec( e.BoxHalfExtents );
		w.Float( e.SphereRadius );
		w.Mesh( e.Mesh );
		w.Float( e.Rate );
		w.Int( e.Bursts.Count );
		foreach ( var b in e.Bursts ) { w.Int( b.Step ); w.Int( b.Count ); }
		w.Float( e.LifetimeMin );
		w.Float( e.LifetimeMax );
		w.Float( e.SizeMin );
		w.Float( e.SizeMax );
		w.Vec( e.Direction );
		w.Bool( e.UseSurfaceNormal );
		w.Float( e.Speed );
		w.Float( e.SpeedJitter );
		w.Float( e.SpreadDegrees );
		w.Vec( e.BaseVelocity );
		w.Float( e.AngularSpeedMax );
		w.Bool( e.Enabled );
		w.Bool( e.DieOnCollision );
		HashSub( w, e.DeathSubEmitter, seen, depth );
		HashSub( w, e.CollisionSubEmitter, seen, depth );
	}

	static void HashSub( CanonicalWriter w, ParticleSubEmitter? sub, List<ParticleEmitter> seen, int depth )
	{
		if ( sub is null ) { w.Int( -1 ); return; }
		w.Int( 1 );
		w.Int( sub.Count );
		w.Float( sub.VelocityInherit );
		w.Int( sub.MaxDepth );
		w.Bool( sub.ThrowOnDepthExceeded );
		w.Str( sub.ChildPreset );
		if ( sub.ChildTemplate is null ) w.Int( -1 );
		else if ( depth >= 32 || Seen( seen, sub.ChildTemplate ) ) w.Int( -2 ); // cycle/depth cap: marker, never loops
		else { w.Int( 1 ); seen.Add( sub.ChildTemplate ); HashEmitter( w, sub.ChildTemplate, seen, depth + 1 ); seen.Remove( sub.ChildTemplate ); }
	}

	/// <summary>Loud validation: counts and rate first, then the definition through ParticleSystem's own Validate semantics.</summary>
	public static void Validate( ParticleSystemDef? definition, int steps, float frameRate, int frameStride = 1 )
	{
		ArgumentNullException.ThrowIfNull( definition );
		if ( steps <= 0 ) throw new ArgumentException( $"particle bake: steps must be > 0 (got {steps})." );
		if ( !float.IsFinite( frameRate ) || frameRate <= 0 ) throw new ArgumentException( "particle bake: frameRate must be finite and > 0." );
		if ( frameStride <= 0 ) throw new ArgumentException( $"particle bake: frameStride must be > 0 (got {frameStride})." );
		if ( definition.Version != ParticleAuthoring.CurrentVersion ) throw new ArgumentException( $"particle bake: unsupported definition version {definition.Version} (this build reads version {ParticleAuthoring.CurrentVersion})." );
		if ( definition.Settings is null ) throw new ArgumentException( "particle bake: definition settings must be present." );
		if ( definition.Emitters is null ) throw new ArgumentException( "particle bake: definition emitters must be present (an empty list is a valid, emitterless system)." );
		// ToSettings() runs ParticleSystemSettings.Validate() and wraps failures; ToEmitter()
		// + live Validate(index) run ParticleEmitter.Validate() — the sim's own rules, not copies.
		_ = definition.Settings.ToSettings();
		for ( int i = 0; i < definition.Emitters.Count; i++ )
		{
			if ( definition.Emitters[i] is null ) throw new ArgumentException( $"particle bake: definition emitter {i} is null." );
			definition.Emitters[i].ToEmitter().Validate( i );
		}
	}

	/// <summary>Uncached bake: validates, builds a fresh system, steps it, and snapshots P40 frames via FromStates. See the file header for the stride rule.</summary>
	public static IReadOnlyList<ParticleBakeFrame> Bake( ParticleSystemDef definition, int steps, float frameRate, int frameStride = 1 )
	{
		Validate( definition, steps, frameRate, frameStride );
		var system = definition.Build();
		var frames = new List<ParticleBakeFrame>();
		for ( int step = 1; step <= steps; step++ )
		{
			system.Step();
			if ( step % frameStride == 0 || step == steps )
				frames.Add( ParticleBakeExport.FromStates( system.States.ToArray(), frames.Count ) );
		}
		return frames;
	}

	/// <summary>Bake and wrap the frames in a P40 dump, stamped with this definition's hash. Composition, not a new format.</summary>
	public static ParticleBakeDump BakeDump( ParticleSystemDef definition, int steps, float frameRate, int frameStride = 1 )
		=> ParticleBakeExport.Create( Bake( definition, steps, frameRate, frameStride ), frameRate, DefinitionHash( definition ) );

	/// <summary>Bake from a lane-5 JSON document. A missing "seed" inside "settings" is an Error here (lane 5 would silently default it to 1).</summary>
	public static IReadOnlyList<ParticleBakeFrame> BakeFromJson( string json, int steps, float frameRate, int frameStride = 1 )
		=> Bake( ParseJson( json ), steps, frameRate, frameStride );

	/// <summary>BakeDump from a lane-5 JSON document, with the same missing-seed rule as <see cref="BakeFromJson"/>.</summary>
	public static ParticleBakeDump BakeDumpFromJson( string json, int steps, float frameRate, int frameStride = 1 )
	{
		var definition = ParseJson( json );
		return ParticleBakeExport.Create( Bake( definition, steps, frameRate, frameStride ), frameRate, DefinitionHash( definition ) );
	}

	static ParticleSystemDef ParseJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "particle bake: empty definition JSON." );
		try
		{
			using var doc = JsonDocument.Parse( json );
			if ( doc.RootElement.ValueKind != JsonValueKind.Object )
				throw new ArgumentException( "particle bake: top level must be a JSON object." );
			if ( !doc.RootElement.TryGetProperty( "version", out var version )
				|| version.ValueKind != JsonValueKind.Number
				|| !version.TryGetInt32( out int v )
				|| v != ParticleAuthoring.CurrentVersion )
				throw new ArgumentException( $"particle bake: unsupported definition version (this build reads version {ParticleAuthoring.CurrentVersion})." );
			if ( !doc.RootElement.TryGetProperty( "settings", out var settings )
				|| settings.ValueKind != JsonValueKind.Object
				|| !settings.TryGetProperty( "seed", out _ ) )
				throw new ArgumentException( "particle bake: definition settings must carry an explicit \"seed\" (lane 5 always serializes it; a missing seed is an error here)." );
		}
		catch ( JsonException e )
		{ throw new ArgumentException( $"particle bake: malformed definition JSON: {e.Message}", e ); }
		return ParticleAuthoring.FromJson( json );
	}

	/// <summary>Canonical byte writer: fixed field order, length-prefixed strings/arrays, float bit patterns. No culture, no GetHashCode.</summary>
	sealed class CanonicalWriter
	{
		readonly MemoryStream _ms = new();
		public void Int( int v ) => Write( BitConverter.GetBytes( v ) );
		public void UInt( uint v ) => Write( BitConverter.GetBytes( v ) );
		public void Bool( bool v ) => Write( new[] { v ? (byte)1 : (byte)0 } );
		public void NBool( bool? v ) { if ( v is null ) Int( -1 ); else { Int( 1 ); Bool( v.Value ); } }
		public void Float( float v )
		{
			if ( v == 0f ) v = 0f; // collapses -0.0 onto +0.0
			if ( float.IsNaN( v ) ) v = float.NaN; // normalised below to one fixed pattern
			int bits = float.IsNaN( v ) ? unchecked((int)0x7FC00000) : BitConverter.SingleToInt32Bits( v );
			Write( BitConverter.GetBytes( bits ) );
		}
		public void NFloat( float? v ) { if ( v is null ) Int( -1 ); else { Int( 1 ); Float( v.Value ); } }
		public void Vec( Vector3 v ) { Float( v.X ); Float( v.Y ); Float( v.Z ); }
		public void Str( string? v )
		{
			if ( v is null ) { Int( -1 ); return; }
			byte[] bytes = Encoding.UTF8.GetBytes( v );
			Int( bytes.Length );
			Write( bytes );
		}
		public void Floats( float[]? v )
		{
			if ( v is null ) { Int( -1 ); return; }
			Int( v.Length );
			foreach ( float f in v ) Float( f );
		}
		public void MeshDef( ParticleMeshDef? m )
		{
			if ( m is null ) { Int( -1 ); return; }
			Int( 1 );
			if ( m.Vertices is null ) Int( -1 );
			else
			{
				Int( m.Vertices.Count );
				foreach ( var vert in m.Vertices ) Floats( vert );
			}
			if ( m.Indices is null ) Int( -1 );
			else
			{
				Int( m.Indices.Count );
				foreach ( int i in m.Indices ) Int( i );
			}
		}
		public void Mesh( ParticleMesh? m )
		{
			if ( m is null ) { Int( -1 ); return; }
			Int( 1 );
			Int( m.Vertices.Length );
			foreach ( var v in m.Vertices ) Vec( v );
			Int( m.Indices.Length );
			foreach ( int i in m.Indices ) Int( i );
		}
		void Write( byte[] bytes ) => _ms.Write( bytes, 0, bytes.Length );
		public byte[] Bytes() => _ms.ToArray();
		public string Hex() => Convert.ToHexString( SHA256.HashData( _ms.ToArray() ) ).ToLowerInvariant();
	}
}

/// <summary>Definition-hash-keyed in-memory bake cache. Key = hash + steps + frameRate bits + frameStride; hit verifies canonical bytes. FIFO eviction, bound counts whole bakes. Not thread-safe.</summary>
public sealed class ParticleBakeCache
{
	public const int DefaultMaxEntries = 8;

	sealed record Entry( byte[] Canonical, IReadOnlyList<ParticleBakeFrame> Frames );

	readonly Dictionary<string, Entry> _entries = new();
	readonly Queue<string> _order = new();

	public ParticleBakeCache( int maxEntries = DefaultMaxEntries )
	{
		if ( maxEntries < 1 ) throw new ArgumentOutOfRangeException( nameof( maxEntries ), "Cache bound must be >= 1 entry." );
		MaxEntries = maxEntries;
	}

	public int MaxEntries { get; }
	public int Count => _entries.Count;
	/// <summary>Lifetime counters: NOT reset by CacheClear().</summary>
	public long CacheHits { get; private set; }
	public long CacheMisses { get; private set; }
	/// <summary>Total fixed steps actually simulated (misses only — a hit simulates nothing).</summary>
	public long StepsSimulated { get; private set; }

	/// <summary>Empty the cache: entries AND insertion order (stale order keys must not survive to evict a live re-added entry).</summary>
	public void CacheClear()
	{
		_entries.Clear();
		_order.Clear();
	}

	/// <summary>True when this exact (definition, steps, frameRate, frameStride) is cached. Returns false, never throws, for invalid arguments.</summary>
	public bool Contains( ParticleSystemDef? definition, int steps, float frameRate, int frameStride = 1 )
	{
		if ( definition is null || steps <= 0 || !float.IsFinite( frameRate ) || frameRate <= 0 || frameStride <= 0 ) return false;
		return _entries.ContainsKey( CacheKey( definition, steps, frameRate, frameStride ) );
	}

	/// <summary>Cached bake: hit (hash + canonical bytes verified) returns the stored list instance without re-simulating; miss bakes, stores, and FIFO-evicts the oldest entry if full.</summary>
	public IReadOnlyList<ParticleBakeFrame> BakeCached( ParticleSystemDef definition, int steps, float frameRate, int frameStride = 1 )
	{
		ParticleBake.Validate( definition, steps, frameRate, frameStride );
		string hash = ParticleBake.DefinitionHash( definition );
		// Same assembly as ParticleBake, so the cache can verify against the
		// exact canonical bytes the hash was computed over — not a second encoding.
		byte[] canonical = ParticleBake.CanonicalBytes( definition );
		string key = CacheKey( definition, steps, frameRate, frameStride );
		if ( _entries.TryGetValue( key, out var hit ) && BytesEqual( hit.Canonical, canonical ) ) { CacheHits++; return hit.Frames; }
		CacheMisses++;
		var frames = ParticleBake.Bake( definition, steps, frameRate, frameStride );
		StepsSimulated += steps;
		while ( _entries.Count >= MaxEntries && _order.Count > 0 )
		{
			string oldest = _order.Dequeue();
			_entries.Remove( oldest );
		}
		_entries[key] = new Entry( canonical, frames );
		_order.Enqueue( key );
		return frames;
	}

	/// <summary>Cached bake wrapped in a P40 dump, stamped with the definition's hash.</summary>
	public ParticleBakeDump BakeCachedDump( ParticleSystemDef definition, int steps, float frameRate, int frameStride = 1 )
		=> ParticleBakeExport.Create( BakeCached( definition, steps, frameRate, frameStride ), frameRate, ParticleBake.DefinitionHash( definition ) );

	static string CacheKey( ParticleSystemDef def, int steps, float frameRate, int frameStride ) =>
		ParticleBake.DefinitionHash( def ) + "|" + steps + "|" + BitConverter.SingleToInt32Bits( frameRate ) + "|" + frameStride;

	static bool BytesEqual( byte[] a, byte[] b )
	{
		if ( a.Length != b.Length ) return false;
		for ( int i = 0; i < a.Length; i++ ) if ( a[i] != b[i] ) return false;
		return true;
	}

}
