#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marionette.Desktop;

// Particle bake export (particles wave-6 lane P40, 2026-10-24).
//
// WHAT THIS IS: a JSON frame dump of baked particles, and back. It is
// the export half of the bake story; the bake itself is lane P39's
// file (ParticleBake.cs), which does NOT exist in the canonical tree
// at the time this lane was written — so this file invents no bake
// and computes no bake. A producer (the bake lane, or any caller with
// a list of frames) hands frames in; this file only serializes them.
//
// UNITS, same as ParticleSystem: kernel inches, Z-up, seconds.
// Positions are inches, velocities inches/second, sizes inches,
// ages seconds, frameRate frames/second.
//
// FORMAT (version 1, JSON, compact — not indented, because dumps are
// large and whitespace is pure size here):
//   {
//     "version": 1,
//     "definitionHash": "<opaque string from the bake lane, or \"\">",
//     "frameRate": 60,
//     "frameCount": 3,
//     "frames": [
//       { "index": 0, "particles": [
//         { "position": [x, y, z], "velocity": [x, y, z],
//           "size": 0.5, "age": 1.25, "alive": true } ] } ]
//   }
//
// DEFINITION HASH (lane P39's concept, documented, NOT reimplemented
// here): "definitionHash" is an opaque string the bake lane stamps on
// a dump so a reader can tell which particle definition produced it
// (section 18, item J14: definition-hash-keyed bake/cache). This lane
// never computes, checks, or trusts it: it is carried verbatim from
// Write to Read, and "" means unknown / unhashed (a hand-built dump,
// or a dump made before the bake lane lands). If a future lane wants
// hash verification, that is its lane, not a silent change here.
//
// WHAT IS STORED, AND WHAT IS NOT: per frame, the LIVE particles
// only (see FromStates, which drops dead slots) — position, velocity,
// size, age, and the alive flag. Rotation and angular velocity from
// ParticleState are NOT stored: a dump is a playback record, not a
// full ParticleState replay, and a reader must not pretend it can
// rebuild a live sim from one. If a future lane needs rotation, it
// adds a version-2 field; it does not reinterpret these fields.
//
// STRICTNESS, BY DESIGN (mirroring lane 5's rule): a missing or
// non-1 "version" fails; malformed JSON fails; a negative size, a
// negative age, a non-finite position/velocity/size/age, a
// frameRate <= 0, a frameCount that disagrees with the frames list,
// or a frame index that disagrees with its position in the list all
// fail LOUDLY (ArgumentException), never silently repaired. Unknown
// JSON FIELDS are ignored, not failed: that is the forward-compat
// rule (a dump written by a newer lane with fields this build does
// not know yet must still load).
//
// SIZE NOTE (no compression in this lane, stated plainly): the format
// stores full per-frame particle lists — every live particle, every
// frame, as decimal JSON numbers. Nothing is delta-encoded, quantized,
// or compressed here, so dump size grows linearly with
// frames x particles. Measure it with BytesPerParticle (JSON length
// divided by total particle count); the lane's own test measures it
// on a 10-frame x 100-particle sample and reports the number, so a
// future compression lane has a baseline to beat. Measured on this
// lane's own sample (10 frames x 100 particles, short decimal values):
// 82,069 bytes / 1000 particles = 82.07 bytes per particle in compact
// JSON (two 3-vectors dominate); re-measure on real dumps, since long
// decimal tails push this up.
//
// OUT OF SCOPE, NAMED: VMDL export (the exporter lane owns VMDL —
// this file never touches VMDL files or writers), the bake itself
// (lane P39), GPU baking, definition save/load (lane 5), and any
// UI or script-verb wiring (sweep integration lines only — see the
// lane report; this file has no hook into ParticleBake.cs, which
// does not exist yet to hook into).

/// <summary>One particle in one baked frame. Vectors are [x, y, z].</summary>
public sealed class ParticleBakeParticle
{
	[JsonPropertyName( "position" )] public float[] Position { get; set; } = new float[3];
	[JsonPropertyName( "velocity" )] public float[] Velocity { get; set; } = new float[3];
	[JsonPropertyName( "size" )] public float Size { get; set; }
	[JsonPropertyName( "age" )] public float Age { get; set; }
	[JsonPropertyName( "alive" )] public bool Alive { get; set; } = true;
}

/// <summary>One baked frame: its index (0-based, sequential) and its live particles, in slot order.</summary>
public sealed class ParticleBakeFrame
{
	[JsonPropertyName( "index" )] public int Index { get; set; }
	[JsonPropertyName( "particles" )] public List<ParticleBakeParticle> Particles { get; set; } = new();
}

/// <summary>One whole baked dump: version, definition hash, rate, count, frames.</summary>
public sealed class ParticleBakeDump
{
	[JsonPropertyName( "version" )] public int Version { get; set; } = ParticleBakeExport.CurrentVersion;
	[JsonPropertyName( "definitionHash" )] public string DefinitionHash { get; set; } = "";
	[JsonPropertyName( "frameRate" )] public float FrameRate { get; set; }
	[JsonPropertyName( "frameCount" )] public int FrameCount { get; set; }
	[JsonPropertyName( "frames" )] public List<ParticleBakeFrame> Frames { get; set; } = new();
}

public static class ParticleBakeExport
{
	public const int CurrentVersion = 1;

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = false,
		PropertyNameCaseInsensitive = true,
	};

	/// <summary>Build a frame from live sim states, dropping dead slots (Alive == 0). Slot order is kept.</summary>
	public static ParticleBakeFrame FromStates( IEnumerable<ParticleState> states, int index )
	{
		ArgumentNullException.ThrowIfNull( states );
		if ( index < 0 ) throw new ArgumentException( "particle bake export: frame index must be >= 0." );
		var frame = new ParticleBakeFrame { Index = index };
		foreach ( var s in states )
		{
			if ( s.Alive == 0 ) continue;
			frame.Particles.Add( new ParticleBakeParticle
			{
				Position = new[] { s.Position.X, s.Position.Y, s.Position.Z },
				Velocity = new[] { s.Velocity.X, s.Velocity.Y, s.Velocity.Z },
				Size = s.Size,
				Age = s.Age,
				Alive = true,
			} );
		}
		return frame;
	}

	/// <summary>Build a dump from frames. FrameCount is set from the frames list, so it cannot disagree.</summary>
	public static ParticleBakeDump Create( IReadOnlyList<ParticleBakeFrame> frames, float frameRate, string? definitionHash = null )
	{
		ArgumentNullException.ThrowIfNull( frames );
		var dump = new ParticleBakeDump
		{
			DefinitionHash = definitionHash ?? "",
			FrameRate = frameRate,
			Frames = new List<ParticleBakeFrame>( frames ),
		};
		dump.FrameCount = dump.Frames.Count;
		Validate( dump );
		return dump;
	}

	/// <summary>Total particle entries across all frames (the denominator for BytesPerParticle).</summary>
	public static int TotalParticles( ParticleBakeDump dump )
	{
		ArgumentNullException.ThrowIfNull( dump );
		int n = 0;
		if ( dump.Frames is not null ) foreach ( var f in dump.Frames ) if ( f.Particles is not null ) n += f.Particles.Count;
		return n;
	}

	/// <summary>Measured bytes per particle for a dump JSON string: json.Length / totalParticles. The baseline a future compression lane measures against.</summary>
	public static double BytesPerParticle( string json, int totalParticles )
	{
		ArgumentNullException.ThrowIfNull( json );
		if ( totalParticles <= 0 ) throw new ArgumentException( "particle bake export: bytes-per-particle needs at least one particle." );
		return (double)json.Length / totalParticles;
	}

	public static string Write( ParticleBakeDump dump )
	{
		ArgumentNullException.ThrowIfNull( dump );
		Validate( dump );
		try { return JsonSerializer.Serialize( dump, JsonOptions ); }
		catch ( JsonException e )
		{ throw new ArgumentException( $"particle bake export: cannot serialize dump: {e.Message}", e ); }
	}

	public static ParticleBakeDump Read( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "particle bake export: empty JSON." );
		try
		{
			using var doc = JsonDocument.Parse( json );
			if ( doc.RootElement.ValueKind != JsonValueKind.Object )
				throw new ArgumentException( "particle bake export: top level must be a JSON object." );
			if ( !doc.RootElement.TryGetProperty( "version", out var version ) )
				throw new ArgumentException( "particle bake export: missing \"version\" (this build reads version 1)." );
			if ( version.ValueKind != JsonValueKind.Number || !version.TryGetInt32( out int v ) || v != CurrentVersion )
				throw new ArgumentException( $"particle bake export: unsupported version '{version}' (this build reads version 1)." );
			if ( !doc.RootElement.TryGetProperty( "frameRate", out _ ) )
				throw new ArgumentException( "particle bake export: missing \"frameRate\"." );
			if ( !doc.RootElement.TryGetProperty( "frameCount", out _ ) )
				throw new ArgumentException( "particle bake export: missing \"frameCount\"." );
			if ( !doc.RootElement.TryGetProperty( "frames", out var frames ) || frames.ValueKind != JsonValueKind.Array )
				throw new ArgumentException( "particle bake export: missing or non-array \"frames\"." );
		}
		catch ( JsonException e )
		{ throw new ArgumentException( $"particle bake export: malformed JSON: {e.Message}", e ); }
		ParticleBakeDump dump;
		try
		{
			dump = JsonSerializer.Deserialize<ParticleBakeDump>( json, JsonOptions )
				?? throw new ArgumentException( "particle bake export: JSON deserialized to null." );
		}
		catch ( JsonException e )
		{ throw new ArgumentException( $"particle bake export: malformed JSON: {e.Message}", e ); }
		Validate( dump );
		return dump;
	}

	public static void SaveFile( ParticleBakeDump dump, string path )
	{
		ArgumentNullException.ThrowIfNull( dump );
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "particle bake export: save path must not be empty." );
		File.WriteAllText( path, Write( dump ) );
	}

	public static ParticleBakeDump LoadFile( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "particle bake export: load path must not be empty." );
		try { return Read( File.ReadAllText( path ) ); }
		catch ( IOException e )
		{ throw new ArgumentException( $"particle bake export: cannot read '{path}': {e.Message}", e ); }
	}

	static void Validate( ParticleBakeDump dump )
	{
		const string p = "particle bake export: ";
		if ( dump.Version != CurrentVersion )
			throw new ArgumentException( $"{p}unsupported version {dump.Version} (this build reads version 1)." );
		if ( dump.DefinitionHash is null )
			throw new ArgumentException( $"{p}definitionHash must not be null (use \"\" for unknown)." );
		if ( !float.IsFinite( dump.FrameRate ) || dump.FrameRate <= 0 )
			throw new ArgumentException( $"{p}frameRate must be finite and > 0." );
		if ( dump.Frames is null )
			throw new ArgumentException( $"{p}frames must not be null." );
		if ( dump.FrameCount != dump.Frames.Count )
			throw new ArgumentException( $"{p}frameCount {dump.FrameCount} does not match frames list length {dump.Frames.Count}." );
		for ( int i = 0; i < dump.Frames.Count; i++ )
		{
			var frame = dump.Frames[i];
			if ( frame is null ) throw new ArgumentException( $"{p}frame {i} is null." );
			if ( frame.Index != i )
				throw new ArgumentException( $"{p}frame at position {i} has index {frame.Index} (indices must be sequential from 0)." );
			if ( frame.Particles is null )
				throw new ArgumentException( $"{p}frame {i}: particles must not be null." );
			for ( int j = 0; j < frame.Particles.Count; j++ )
			{
				var pt = frame.Particles[j];
				if ( pt is null ) throw new ArgumentException( $"{p}frame {i} particle {j} is null." );
				Vec( pt.Position, $"frame {i} particle {j} position" );
				Vec( pt.Velocity, $"frame {i} particle {j} velocity" );
				if ( !float.IsFinite( pt.Size ) || pt.Size < 0 )
					throw new ArgumentException( $"{p}frame {i} particle {j}: size must be finite and >= 0." );
				if ( !float.IsFinite( pt.Age ) || pt.Age < 0 )
					throw new ArgumentException( $"{p}frame {i} particle {j}: age must be finite and >= 0." );
			}
		}
	}

	static void Vec( float[]? v, string field )
	{
		if ( v is null || v.Length != 3 )
			throw new ArgumentException( $"particle bake export: {field} must be a [x, y, z] array." );
		var vec = new Vector3( v[0], v[1], v[2] );
		if ( !float.IsFinite( vec.X ) || !float.IsFinite( vec.Y ) || !float.IsFinite( vec.Z ) )
			throw new ArgumentException( $"particle bake export: {field} must be finite." );
	}
}
