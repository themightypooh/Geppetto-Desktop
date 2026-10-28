#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Marionette.Desktop;

// Particle verb pack (particles lane P51, 2026-10-24).
//
// WHAT THIS IS: four more `particle` script-verb forms, in lane 5's
// ParticleVerb style (same parsing discipline, same loud failures), for
// script-level emitter control. It does NOT touch ParticleAuthoring.cs,
// ScriptLive.cs, BuildScript.cs, or any other lane's file.
//
//   particle burst preset=<name> at=<x,y,z> count=<n>
//   particle loop preset=<name> at=<x,y,z> rate=<n>
//   particle stop id=<handle>
//   particle set-rate id=<handle> rate=<n>
//
// SEMANTICS, EACH FORM:
// - burst: builds a fresh system with the preset's medium (SettingsFor,
//   exactly like lane 5, so a bubbles burst rises) and one emitter from
//   the preset, then FIRES it at once: the preset's own bursts are
//   REPLACED by a single step-0 burst of exactly <n> particles, the
//   emitter rate is forced to 0 so nothing else trickles out, and Run
//   steps the system once — so after Run returns, AliveCount == count.
//   count OVERRIDES the preset's burst count (sparks carries 70; a
//   burst count=5 fires 5, not 70, not 75). count must be 1..Capacity
//   of the built system (default 10,000); over capacity fails loudly
//   instead of silently capping, because a capped burst would lie
//   about how many particles the script asked for.
// - loop: builds a fresh system the same way, but the emitter is a
//   LOOPING rate emitter: the preset's bursts are CLEARED (a loop that
//   also fired its preset burst would double-emit on step 0) and Rate
//   is set to <n> per second. Run does NOT step: the loop emits as the
//   caller steps the returned system. Run returns a handle the stop /
//   set-rate forms use.
// - stop: sets the loop emitter's Enabled = false, so later steps emit
//   nothing new (already-alive particles live out their lifetimes).
//   The handle is CONSUMED: a second stop, or a set-rate after a stop,
//   fails loudly as an unknown handle, never silently no-ops.
// - set-rate: changes a running loop emitter's Rate live. The change
//   takes effect on the next step; already-emitted particles are
//   untouched.
//
// HANDLES: "loop-1", "loop-2", ... — a per-registry counter starting at
// 1, never reused within a registry's lifetime, so a stale handle can
// never accidentally address a newer emitter. Handles are only
// meaningful inside the ParticleLoopRegistry that issued them; the
// script pre-pass owns one registry per run (see the sweep lines).
//
// WIRING (same fact lane 5 proved, not re-litigated): the `particle`
// verb is wired via a desktop pre-pass in ScriptLive.Drain, NOT a
// kernel BuildScript case (desktop references kernel, never reverse),
// and Mutates("particle") stays false. The pre-pass dispatch is by the
// second token: a bare word burst/loop/stop/set-rate goes here (see
// IsPackLine); a key=value second token (preset=/file=) goes to lane
// 5's ParticleVerb. This file only parses and runs; the sweep owns
// the Drain edit. This is a script-level emitter control, NOT a
// timeline scheduler: no cue seeking, no loop scheduling, no canvas/UI,
// no save beyond lane 5's existing save form.
//
// BAD FORMS (every one fails loudly with ArgumentException, and every
// one has a test in ParticleVerbPackTests): empty line; line not
// starting with `particle`; a token that is not key=value; a duplicate
// key; an unknown pack verb word; a form key on the wrong form
// (e.g. rate= on burst, count= on loop); burst/loop missing preset, at,
// or count/rate; unknown preset; malformed at (not exactly 3 finite
// numbers); burst count 0, negative, or non-integer; loop/set-rate
// rate negative, non-numeric, or non-finite; stop/set-rate missing id;
// stop/set-rate with an unknown handle (includes double-stop and
// set-rate-after-stop, because stop consumes the handle); burst count
// over the built system's capacity; unterminated quote; an empty
// value; at= and position= together (ambiguous).

/// <summary>Which pack form a parsed line is.</summary>
public enum ParticleVerbPackKind { Burst, Loop, Stop, SetRate }

/// <summary>One parsed pack line. Only the fields for its
/// <see cref="Kind"/> are set; the rest stay null.</summary>
public sealed class ParticleVerbPackCommand
{
	public ParticleVerbPackKind Kind { get; set; }
	public string? Preset { get; set; }
	public Vector3? At { get; set; }
	public int? Count { get; set; }
	public float? Rate { get; set; }
	public string? Handle { get; set; }
}

/// <summary>What <see cref="ParticleVerbPack.Run"/> produced.</summary>
public sealed class ParticleVerbPackResult
{
	public ParticleVerbPackCommand Command { get; set; } = new();
	public ParticleSystem? System { get; set; }
	public ParticleEmitter? Emitter { get; set; }
	public string? Handle { get; set; }
}

/// <summary>Live looping emitters started by the loop form, keyed by
/// handle. One registry per script run; handles are never recycled.</summary>
public sealed class ParticleLoopRegistry
{
	readonly Dictionary<string, (ParticleSystem System, ParticleEmitter Emitter)> _loops = new( StringComparer.Ordinal );
	int _next = 1;

	public int Count => _loops.Count;
	public bool Contains( string handle ) => _loops.ContainsKey( handle );

	public string Start( ParticleSystem system, ParticleEmitter emitter )
	{
		ArgumentNullException.ThrowIfNull( system );
		ArgumentNullException.ThrowIfNull( emitter );
		string handle = $"loop-{_next++}";
		_loops.Add( handle, (system, emitter) );
		return handle;
	}

	/// <summary>Stop a loop: disables its emitter and consumes the
	/// handle. Unknown handle fails loudly, never silently no-ops.</summary>
	public (ParticleSystem System, ParticleEmitter Emitter) Stop( string handle )
	{
		var loop = Get( handle );
		loop.Emitter.Enabled = false;
		_loops.Remove( handle );
		return loop;
	}

	/// <summary>Change a running loop's rate live. Unknown handle fails
	/// loudly; a non-finite or negative rate fails loudly.</summary>
	public (ParticleSystem System, ParticleEmitter Emitter) SetRate( string handle, float rate )
	{
		if ( !float.IsFinite( rate ) || rate < 0 )
			throw new ArgumentException( $"particle set-rate: rate must be finite and >= 0, got '{rate}'." );
		var loop = Get( handle );
		loop.Emitter.Rate = rate;
		return loop;
	}

	(ParticleSystem System, ParticleEmitter Emitter) Get( string handle )
	{
		if ( string.IsNullOrWhiteSpace( handle ) || !_loops.TryGetValue( handle, out var loop ) )
			throw new ArgumentException( $"particle verb pack: unknown loop handle '{handle}'. Handles come from 'particle loop ...' and are consumed by 'particle stop ...'." );
		return loop;
	}
}

public static class ParticleVerbPack
{
	public const string Usage =
		"particle burst preset=<name> at=x,y,z count=<n> | " +
		"particle loop preset=<name> at=x,y,z rate=<n> | " +
		"particle stop id=<handle> | " +
		"particle set-rate id=<handle> rate=<n>";

	static readonly HashSet<string> BurstKeys = new( StringComparer.OrdinalIgnoreCase ) { "preset", "at", "position", "count" };
	static readonly HashSet<string> LoopKeys = new( StringComparer.OrdinalIgnoreCase ) { "preset", "at", "position", "rate" };
	static readonly HashSet<string> StopKeys = new( StringComparer.OrdinalIgnoreCase ) { "id" };
	static readonly HashSet<string> SetRateKeys = new( StringComparer.OrdinalIgnoreCase ) { "id", "rate" };

	/// <summary>True when a line is one of this pack's forms: it starts
	/// with `particle` and its second token is a bare burst/loop/stop/
	/// set-rate word. Never throws: anything unparseable returns false
	/// so the caller can route it to lane 5's parser, which fails
	/// loudly there. The sweep's Drain pre-pass dispatches on this.</summary>
	public static bool IsPackLine( string? line )
	{
		if ( string.IsNullOrWhiteSpace( line ) ) return false;
		List<string> tokens;
		try { tokens = Tokenize( line ); }
		catch ( ArgumentException ) { return false; }
		return tokens.Count >= 2
			&& tokens[0].Equals( "particle", StringComparison.OrdinalIgnoreCase )
			&& ( tokens[1].Equals( "burst", StringComparison.OrdinalIgnoreCase )
				|| tokens[1].Equals( "loop", StringComparison.OrdinalIgnoreCase )
				|| tokens[1].Equals( "stop", StringComparison.OrdinalIgnoreCase )
				|| tokens[1].Equals( "set-rate", StringComparison.OrdinalIgnoreCase ) );
	}

	public static ParticleVerbPackCommand Parse( string line )
	{
		if ( string.IsNullOrWhiteSpace( line ) )
			throw new ArgumentException( $"particle verb pack: empty line. {Usage}" );
		var tokens = Tokenize( line );
		if ( tokens.Count == 0 || !tokens[0].Equals( "particle", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( $"particle verb pack: line must start with 'particle'. {Usage}" );
		if ( tokens.Count < 2 )
			throw new ArgumentException( $"particle verb pack: need burst, loop, stop, or set-rate after 'particle'. {Usage}" );
		string verb = tokens[1].ToLowerInvariant();
		HashSet<string> allowed = verb switch
		{
			"burst" => BurstKeys,
			"loop" => LoopKeys,
			"stop" => StopKeys,
			"set-rate" => SetRateKeys,
			_ => throw new ArgumentException( $"particle verb pack: unknown form '{tokens[1]}'; want burst, loop, stop, or set-rate. {Usage}" ),
		};
		var args = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		for ( int i = 2; i < tokens.Count; i++ )
		{
			int eq = tokens[i].IndexOf( '=' );
			if ( eq <= 0 )
				throw new ArgumentException( $"particle verb pack: token '{tokens[i]}' is not key=value. {Usage}" );
			string key = tokens[i][..eq], value = tokens[i][( eq + 1 )..];
			if ( string.IsNullOrWhiteSpace( value ) )
				throw new ArgumentException( $"particle verb pack: key '{key}' needs a value. {Usage}" );
			if ( !allowed.Contains( key ) )
				throw new ArgumentException( $"particle verb pack: key '{key}' is not allowed on the {verb} form. {Usage}" );
			if ( !args.TryAdd( key, value ) )
				throw new ArgumentException( $"particle verb pack: duplicate key '{key}'." );
		}
		var cmd = new ParticleVerbPackCommand { Kind = verb switch
		{
			"burst" => ParticleVerbPackKind.Burst,
			"loop" => ParticleVerbPackKind.Loop,
			"stop" => ParticleVerbPackKind.Stop,
			_ => ParticleVerbPackKind.SetRate,
		} };
		if ( cmd.Kind is ParticleVerbPackKind.Burst or ParticleVerbPackKind.Loop )
		{
			if ( !args.TryGetValue( "preset", out string? preset ) )
				throw new ArgumentException( $"particle verb pack: {verb} needs preset=<name>. {Usage}" );
			cmd.Preset = ParticleEmitterPresets.ResolveName( preset )
				?? throw new ArgumentException( $"particle verb pack: unknown preset '{preset}'. Presets: {string.Join( ", ", ParticleEmitterPresets.Names )}." );
			bool hasAt = args.TryGetValue( "at", out string? at );
			bool hasPos = args.TryGetValue( "position", out string? pos );
			if ( hasAt && hasPos )
				throw new ArgumentException( "particle verb pack: at= and position= together are ambiguous; use one." );
			if ( !hasAt && !hasPos )
				throw new ArgumentException( $"particle verb pack: {verb} needs at=<x,y,z>. {Usage}" );
			cmd.At = Vec( hasAt ? at! : pos!, "at" );
		}
		if ( cmd.Kind == ParticleVerbPackKind.Burst )
		{
			if ( !args.TryGetValue( "count", out string? count ) )
				throw new ArgumentException( $"particle verb pack: burst needs count=<n>. {Usage}" );
			cmd.Count = Count( count );
		}
		if ( cmd.Kind is ParticleVerbPackKind.Loop or ParticleVerbPackKind.SetRate )
		{
			if ( !args.TryGetValue( "rate", out string? rate ) )
				throw new ArgumentException( $"particle verb pack: {verb} needs rate=<n>. {Usage}" );
			cmd.Rate = Rate( rate );
		}
		if ( cmd.Kind is ParticleVerbPackKind.Stop or ParticleVerbPackKind.SetRate )
		{
			if ( !args.TryGetValue( "id", out string? id ) )
				throw new ArgumentException( $"particle verb pack: {verb} needs id=<handle>. {Usage}" );
			cmd.Handle = id;
		}
		return cmd;
	}

	/// <summary>Parse and execute a pack line against a loop registry.
	/// Burst returns a stepped system with exactly count particles
	/// alive; loop returns a registered handle; stop/set-rate return
	/// the loop's system and emitter.</summary>
	public static ParticleVerbPackResult Run( string line, ParticleLoopRegistry registry )
	{
		ArgumentNullException.ThrowIfNull( registry );
		var cmd = Parse( line );
		switch ( cmd.Kind )
		{
			case ParticleVerbPackKind.Burst:
			{
				var (system, emitter) = Build( cmd.Preset!, cmd.At!.Value );
				if ( cmd.Count!.Value > system.Settings.Capacity )
					throw new ArgumentException( $"particle burst: count {cmd.Count} exceeds the system capacity {system.Settings.Capacity}; a capped burst would lie about its count." );
				emitter.Bursts.Clear();
				emitter.Bursts.Add( new ParticleBurst( 0, cmd.Count.Value ) );
				emitter.Rate = 0;
				system.AddEmitter( emitter );
				system.Step();
				return new ParticleVerbPackResult { Command = cmd, System = system, Emitter = emitter };
			}
			case ParticleVerbPackKind.Loop:
			{
				var (system, emitter) = Build( cmd.Preset!, cmd.At!.Value );
				emitter.Bursts.Clear();
				emitter.Rate = cmd.Rate!.Value;
				emitter.Enabled = true;
				system.AddEmitter( emitter );
				string handle = registry.Start( system, emitter );
				return new ParticleVerbPackResult { Command = cmd, System = system, Emitter = emitter, Handle = handle };
			}
			case ParticleVerbPackKind.Stop:
			{
				var (system, emitter) = registry.Stop( cmd.Handle! );
				return new ParticleVerbPackResult { Command = cmd, System = system, Emitter = emitter, Handle = cmd.Handle };
			}
			default:
			{
				var (system, emitter) = registry.SetRate( cmd.Handle!, cmd.Rate!.Value );
				return new ParticleVerbPackResult { Command = cmd, System = system, Emitter = emitter, Handle = cmd.Handle };
			}
		}
	}

	static (ParticleSystem System, ParticleEmitter Emitter) Build( string canonicalPreset, Vector3 at )
	{
		var settings = ParticleEmitterPresets.SettingsFor( canonicalPreset, new ParticleSystemSettings() );
		var system = new ParticleSystem( settings );
		var emitter = ParticleEmitterPresets.Create( canonicalPreset, at );
		return (system, emitter);
	}

	static int Count( string value )
	{
		if ( !int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n ) )
			throw new ArgumentException( $"particle burst: count must be a whole number, got '{value}'." );
		if ( n < 1 )
			throw new ArgumentException( $"particle burst: count must be >= 1, got {n}." );
		return n;
	}

	static float Rate( string value )
	{
		if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f ) || !float.IsFinite( f ) )
			throw new ArgumentException( $"particle verb pack: rate must be a finite number, got '{value}'." );
		if ( f < 0 )
			throw new ArgumentException( $"particle verb pack: rate must be >= 0, got {f}." );
		return f;
	}

	static Vector3 Vec( string value, string key )
	{
		var parts = value.Split( ',' );
		if ( parts.Length != 3 )
			throw new ArgumentException( $"particle verb pack: {key} needs exactly 3 comma-separated numbers, got '{value}'." );
		float[] v = new float[3];
		for ( int i = 0; i < 3; i++ )
			if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i] ) || !float.IsFinite( v[i] ) )
				throw new ArgumentException( $"particle verb pack: {key} component '{parts[i]}' is not a finite number." );
		return new Vector3( v[0], v[1], v[2] );
	}

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
		if ( quoted ) throw new ArgumentException( "particle verb pack: unterminated quote." );
		if ( inToken ) tokens.Add( cur.ToString() );
		return tokens;
	}
}
