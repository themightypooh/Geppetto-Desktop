#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Marionette.Desktop;

// Particle definition validation pack (particles lane P71, 2026-10-24).
//
// WHAT THIS IS: a STRICTER, report-style validation pass ON TOP of lane 5's
// ParticleAuthoring validation — not a replacement for it and not a second
// copy of it. Lane 5's Validate() path (ParticleSystemDef.Build, through
// ParticleSystemSettings.Validate and ParticleEmitter.Validate) THROWS on the
// first bad value, which is the right behaviour for loading: a def that
// builds is a system that runs. This pack answers a different question —
// "list EVERYTHING suspicious about this def, at once, without building
// it" — so an authoring UI (or a future lane) can show all problems
// together instead of one throw at a time. It never builds a system, never
// repairs a def (report-only), and never writes anything.
//
// HOW IT RELATES TO LANE 5, CHECK BY CHECK. Every check below is tagged
// with its Severity and with one of:
//   [REPORT]  — the same bound lane 5 / the sim already enforces by
//               throwing, restated here as a listable finding so all
//               problems surface together. The bound is NOT new; only
//               the reporting form is.
//   [NEW]     — a check lane 5 and the sim do NOT make. This is the
//               stricter part of the pack.
// Checks lane 5 already makes that this pack deliberately does NOT repeat
// are listed at the bottom under NOT DUPLICATED, with citations. Do not
// "complete" this file by adding them: the point of the split is that
// lane 5 stays the load gate and this pack stays the report.
//
// FIELD PATHS: dotted paths in the same style as the P70 diff paths:
//   "settings.capacity", "settings.seed", "settings.renderType",
//   "settings.strandPoints", "emitters[0].rate", "emitters[0].bursts[1].count".
// A whole-emitter finding (the emitter emits nothing at all) uses the
// emitter path itself, "emitters[i]". A system-level finding (expected
// live count over capacity) uses "settings.capacity", because capacity is
// the field that would have to change to fix it.
//
// SEVERITY:
//   Error   — blocks use. Either the sim/lane 5 would refuse the def, or
//             the def states something self-contradictory (a declared
//             burst that emits nothing, a preset name that resolves to
//             nothing). An Error means "do not load this as-is".
//   Warning — suspicious but loadable. The sim accepts the def and runs
//             it, but the numbers say the result is almost certainly not
//             what the author meant (expected live count over capacity:
//             the sim drops particles for capacity, see
//             ParticleSystem.DroppedForCapacity, so particles silently
//             vanish; an enabled emitter that emits nothing; a lifetime
//             longer than an hour).
//
// MALFORMED INPUT IS NOT A VALIDATION FINDING. ValidateJson throws
// ArgumentException on malformed JSON, a non-object top level, or a
// missing/wrong "version" — exactly lane 5's ParticleAuthoring.FromJson
// behaviour. A string that cannot be parsed is a load failure, not a
// finding to list alongside range errors, and this pack does not soften
// that boundary. ValidateDefinition (the object form) does NOT throw on
// bad values: it returns them as findings. It throws only on a null
// definition, which is a caller bug, not a def problem.
//
// SEED PRESENCE IS A JSON-LEVEL CHECK. Lane 5 always serializes the seed
// (its file header: a round trip that dropped the seed would silently
// change the art, since the sim is deterministic per seed). A
// deserialized ParticleSystemDef has already lost "was seed written?" —
// the property defaults to 1 when absent, indistinguishable from an
// explicit 1 — so ValidateDefinition cannot detect a missing seed and
// does not pretend to. ValidateJson checks the raw JSON for a
// "settings.seed" property (case-insensitive, matching lane 5's
// case-insensitive deserialization) and reports a missing one as an
// Error at "settings.seed".
//
// LIFETIME UPPER BOUND (a judgment call, not a sim limit — see LIMITS).
// MaxLifetimeSeconds = 3600 (one hour). Justification, tied to the sim
// rather than picked from the air: the sim steps at 60 Hz, so a 3600 s
// particle is integrated for 216,000 steps, and its expected live
// contribution alone (rate x lifetime) exceeds the 4,000,000 capacity
// for any rate above ~1111/s — the over-capacity Warning below already
// fires long before that for any busy emitter. A particle living over
// an hour is therefore almost certainly a units mistake (frames or
// minutes written where seconds were meant), not a real effect. It is
// a Warning, not an Error, precisely because the sim has no such limit
// and a genuine hour-long ambient mote must stay loadable.
//
// EFFECTIVE VALUES. Emitter def fields are nullable: null means "keep
// the preset's value" on a preset emitter, or "the live default" on a
// plain emitter (lane 5's ParticleEmitterDef contract). Range and
// cross-field checks therefore run on EFFECTIVE values — the preset's
// emitter (from ParticleEmitterPresets.Create / ParticlePresetPackB.Create
// / ParticlePresetPackC.Create) with the def's non-null fields applied
// over it, mirroring ParticleEmitterDef.ToEmitter's override order —
// never on the raw nullable fields. A preset line like
// {"preset": "sparks", "position": [0, 0, 10]} must not warn about a
// zero rate: sparks is a burst preset (rate 0, step-0 burst of 70), and
// it emits just fine. If a preset name does not resolve, the effective
// values fall back to live defaults so the remaining checks still run;
// the unresolvable name itself is the Error.

/// <summary>Severity of one <see cref="ParticleDefinitionError"/>.
/// Error blocks use; Warning is suspicious but loadable.</summary>
public enum ParticleDefinitionSeverity { Error, Warning }

/// <summary>One validation finding: where, what, and how bad.</summary>
public sealed record ParticleDefinitionError(
	string FieldPath,
	string Message,
	ParticleDefinitionSeverity Severity );

/// <summary>Stricter, report-style validation for particle defs. See the
/// file header for the lane-5 relationship, the severity meanings, the
/// malformed-input boundary, and the seed-presence rule.</summary>
public static class ParticleDefinitionValidate
{
	/// <summary>Judgment-call upper bound for a sane lifetime, seconds.
	/// Over this is a Warning, never an Error: the sim has no such limit.
	/// Justification is in the file header (60 Hz steps, capacity math).</summary>
	public const float MaxLifetimeSeconds = 3600f;

	/// <summary>Validate an already-parsed def. Returns every finding;
	/// never throws on bad VALUES (a null def throws ArgumentNullException,
	/// which is a caller bug). Does not build a system and does not repair
	/// anything. Seed presence cannot be checked here — see ValidateJson.</summary>
	public static List<ParticleDefinitionError> ValidateDefinition( ParticleSystemDef def )
	{
		ArgumentNullException.ThrowIfNull( def );
		var errors = new List<ParticleDefinitionError>();

		// [REPORT] Version. Lane 5's Build throws on any version != 1
		// (ParticleSystemDef.Build); restated here so a report can name it.
		if ( def.Version != ParticleAuthoring.CurrentVersion )
			errors.Add( new ParticleDefinitionError( "version",
				$"unsupported version {def.Version} (this build reads version {ParticleAuthoring.CurrentVersion}).",
				ParticleDefinitionSeverity.Error ) );

		if ( def.Settings is null )
			errors.Add( new ParticleDefinitionError( "settings",
				"settings must be present.",
				ParticleDefinitionSeverity.Error ) );
		else
			ValidateSettings( def.Settings, errors );

		if ( def.Emitters is null )
		{
			errors.Add( new ParticleDefinitionError( "emitters",
				"emitters must be present (an empty list is a valid, emitterless system).",
				ParticleDefinitionSeverity.Error ) );
			return errors;
		}

		double expectedPeak = 0;
		bool expectedUsable = true;
		for ( int i = 0; i < def.Emitters.Count; i++ )
		{
			var em = def.Emitters[i];
			string path = $"emitters[{i}]";
			if ( em is null )
			{
				// [REPORT] Lane 5's Build throws on a null emitter.
				errors.Add( new ParticleDefinitionError( path,
					$"emitter {i} is null.",
					ParticleDefinitionSeverity.Error ) );
				continue;
			}
			expectedUsable &= ValidateEmitter( em, path, errors, out double contribution );
			expectedPeak += contribution;
		}

		// [NEW] Cross-field: expected peak live count vs capacity. The
		// sim drops particles once capacity is full (DroppedForCapacity),
		// so an over-capacity def LOADS but silently loses particles —
		// that is exactly a Warning, not an Error. Expected peak is the
		// steady-state worst case: sum over enabled emitters of
		// rate x lifetimeMax, plus every burst's count (bursts are
		// one-shot additions on top of the steady state). Emitters whose
		// numbers already failed a range check are excluded from the sum
		// (their range errors are reported already); the sum is then a
		// lower bound, and the message says so when that happens.
		// Skipped when capacity itself failed its range check, so one
		// bad field does not produce two findings.
		if ( def.Settings is not null
			&& def.Settings.Capacity >= 1 && def.Settings.Capacity <= 4_000_000
			&& double.IsFinite( expectedPeak ) && expectedPeak > def.Settings.Capacity )
		{
			string expectedText = expectedPeak == Math.Floor( expectedPeak )
				? ( (long)expectedPeak ).ToString( CultureInfo.InvariantCulture )
				: expectedPeak.ToString( "0.##", CultureInfo.InvariantCulture );
			errors.Add( new ParticleDefinitionError( "settings.capacity",
				$"expected peak live count {expectedText} exceeds capacity {def.Settings.Capacity}"
				+ ( expectedUsable ? "" : " (a lower bound: emitters with range errors were excluded)" )
				+ "; the sim drops particles for capacity, so the def loads but loses particles.",
				ParticleDefinitionSeverity.Warning ) );
		}
		return errors;
	}

	/// <summary>Validate a def from its JSON text. Throws ArgumentException
	/// on malformed JSON, a non-object top level, or a missing/wrong
	/// "version" (lane 5's FromJson behaviour — a load failure, not a
	/// finding). Otherwise returns the same findings as
	/// ValidateDefinition, plus a settings.seed Error when the raw JSON
	/// has no seed property (see the file header for why only this form
	/// can check seed presence). Unknown JSON fields are NOT findings:
	/// lane 5 ignores them for forward compat, and this pack must not
	/// tighten that into breaking forward compat.</summary>
	public static List<ParticleDefinitionError> ValidateJson( string json )
	{
		// FromJson throws on malformed input — deliberately not caught.
		var def = ParticleAuthoring.FromJson( json );
		var errors = ValidateDefinition( def );
		if ( !JsonHasSeed( json ) )
			errors.Add( new ParticleDefinitionError( "settings.seed",
				"seed must be present in the def: lane 5 always serializes it, because the sim is deterministic per seed and a missing seed silently changes the art.",
				ParticleDefinitionSeverity.Error ) );
		return errors;
	}

	static bool JsonHasSeed( string json )
	{
		using var doc = JsonDocument.Parse( json );
		if ( !doc.RootElement.TryGetProperty( "settings", out var settings ) )
			return false;
		// Lane 5 deserializes case-insensitively, so the presence check
		// must too: "Seed" counts just as "seed" does.
		foreach ( var property in settings.EnumerateObject() )
			if ( property.Name.Equals( "seed", StringComparison.OrdinalIgnoreCase ) )
				return true;
		return false;
	}

	static void ValidateSettings( ParticleSettingsDef s, List<ParticleDefinitionError> errors )
	{
		// [REPORT] Capacity 1..4,000,000: ParticleSystemSettings.Validate.
		if ( s.Capacity < 1 || s.Capacity > 4_000_000 )
			errors.Add( new ParticleDefinitionError( "settings.capacity",
				$"capacity {s.Capacity} is outside the sim's 1..4,000,000 range.",
				ParticleDefinitionSeverity.Error ) );

		// [REPORT] Gravity must be a finite [x, y, z]: lane 5's Vec plus
		// ParticleSystemSettings.Validate's Finite check.
		if ( !IsFiniteVec( s.Gravity ) )
			errors.Add( new ParticleDefinitionError( "settings.gravity",
				"gravity must be a finite [x, y, z] array.",
				ParticleDefinitionSeverity.Error ) );

		// [REPORT] Drag >= 0 and finite: ParticleSystemSettings.Validate
		// ("linear drag coefficient, 1/s; stable for any value >= 0").
		if ( !float.IsFinite( s.Drag ) || s.Drag < 0 )
			errors.Add( new ParticleDefinitionError( "settings.drag",
				$"drag {Fmt( s.Drag )} must be finite and >= 0.",
				ParticleDefinitionSeverity.Error ) );

		// [REPORT] Turbulence strength finite and >= 0:
		// ParticleSystemSettings.Validate.
		if ( !float.IsFinite( s.TurbulenceStrength ) || s.TurbulenceStrength < 0 )
			errors.Add( new ParticleDefinitionError( "settings.turbulenceStrength",
				$"turbulenceStrength {Fmt( s.TurbulenceStrength )} must be finite and >= 0.",
				ParticleDefinitionSeverity.Error ) );

		// [NEW] Vortex strength finite and >= 0. The sim only requires
		// finite (ParticleSystemSettings.Validate: "finite strength and
		// radius > 0") — a negative strength is legal there and flips the
		// swirl direction. This pack is stricter by design: a negative
		// vortex strength in a def is treated as a sign mistake, not a
		// deliberate reverse swirl. If Joshua ever wants reverse swirls,
		// relax this check; do not work around it silently.
		if ( !float.IsFinite( s.VortexStrength ) || s.VortexStrength < 0 )
			errors.Add( new ParticleDefinitionError( "settings.vortexStrength",
				$"vortexStrength {Fmt( s.VortexStrength )} must be finite and >= 0 (the sim accepts a negative strength as a reversed swirl; this pack treats it as a sign mistake).",
				ParticleDefinitionSeverity.Error ) );

		// [REPORT] Render type must be a known enum value: lane 5's
		// ParseEnum in ParticleSettingsDef.ToSettings.
		bool renderOk = Enum.TryParse<ParticleRenderType>( s.RenderType, ignoreCase: true, out var renderType );
		if ( !renderOk )
			errors.Add( new ParticleDefinitionError( "settings.renderType",
				$"renderType '{s.RenderType}' is not one of: {string.Join( ", ", Enum.GetNames<ParticleRenderType>() )}.",
				ParticleDefinitionSeverity.Error ) );

		// [REPORT] StrandPoints 0..256: ParticleSystemSettings.Validate.
		if ( s.StrandPoints < 0 || s.StrandPoints > 256 )
			errors.Add( new ParticleDefinitionError( "settings.strandPoints",
				$"strandPoints {s.StrandPoints} must be in 0..256.",
				ParticleDefinitionSeverity.Error ) );
		// [REPORT] Strand render type needs StrandPoints >= 2:
		// ParticleSystemSettings.Validate ("Strand render type needs
		// StrandPoints >= 2"). The sim allocates the strand history ring
		// buffer whenever StrandPoints > 0, but Strand RENDERING needs at
		// least 2 points to draw a segment — 1 point is a history with
		// nothing to connect to. That is the requirement, cited, not a
		// guess: "strand history enabled" alone (StrandPoints >= 1) is
		// NOT enough for the Strand render type.
		else if ( renderOk && renderType == ParticleRenderType.Strand && s.StrandPoints < 2 )
			errors.Add( new ParticleDefinitionError( "settings.strandPoints",
				"Strand render type needs strandPoints >= 2 (the sim allocates strand history for any strandPoints > 0, but Strand rendering needs at least 2 points to draw a segment).",
				ParticleDefinitionSeverity.Error ) );
	}

	// Returns false when this emitter's numbers were unusable for the
	// expected-peak sum (a range error was already reported for it);
	// contribution is its rate x lifetimeMax plus burst counts, or 0
	// when unusable or disabled.
	static bool ValidateEmitter( ParticleEmitterDef em, string path,
		List<ParticleDefinitionError> errors, out double contribution )
	{
		contribution = 0;
		bool usable = true;

		// Resolve the base emitter: a preset's emitter, or live defaults.
		// [NEW] Preset-name resolution across every pack actually present
		// in the tree: the 14 built-ins (ParticleEmitterPresets), pack B
		// (ParticlePresetPackB: explosion, fountain, waterfall, tornado,
		// galaxy, plasma, slime, ash) and pack C (ParticlePresetPackC:
		// ink, plankton, sand, silt, caustic_motes, oil, lava,
		// frost_breath). Resolution uses each pack's own ResolveName, so
		// cue spellings ("fx:sparks", "footstep_water_splash") resolve
		// the same way lane 5 resolves them. An unresolvable name is an
		// Error carrying the name — lane 5 also throws on it at build
		// time, but this pack must name it in the report instead of
		// letting the first throw hide the rest of the findings.
		ParticleEmitter baseEm = new();
		bool presetFailed = false;
		if ( em.Preset is not null )
		{
			ParticleEmitter? created = TryCreatePreset( em.Preset );
			if ( created is null )
			{
				presetFailed = true;
				errors.Add( new ParticleDefinitionError( path + ".preset",
					$"unknown preset '{em.Preset}'. It resolves in none of the built-ins, preset pack B, or preset pack C.",
					ParticleDefinitionSeverity.Error ) );
				// Fall through on live defaults so the remaining checks
				// still run and the report is complete — but the
				// zero-emission Warning below is suppressed: a dead
				// preset name already explains the silence, and one
				// bad name must not produce two findings.
			}
			else baseEm = created;
		}

		// Effective values: base, with the def's non-null fields over it,
		// mirroring ParticleEmitterDef.ToEmitter's override order.
		float lifetimeMin = em.LifetimeMin ?? baseEm.LifetimeMin;
		float lifetimeMax = em.LifetimeMax ?? baseEm.LifetimeMax;
		float sizeMin = em.SizeMin ?? baseEm.SizeMin;
		float sizeMax = em.SizeMax ?? baseEm.SizeMax;
		float speed = em.Speed ?? baseEm.Speed;
		float speedJitter = em.SpeedJitter ?? baseEm.SpeedJitter;
		float rate = em.Rate ?? baseEm.Rate;
		bool enabled = em.Enabled ?? baseEm.Enabled;
		// Declared bursts (def type) vs inherited bursts (live type)
		// are different list types; they are summed separately below.

		ParticleEmitterShape? shape = null;
		if ( em.Shape is not null )
		{
			// [REPORT] Shape must be a known enum value: lane 5's
			// ParseEnum in ParticleEmitterDef.ToEmitter.
			if ( Enum.TryParse<ParticleEmitterShape>( em.Shape, ignoreCase: true, out var parsed ) )
				shape = parsed;
			else
				errors.Add( new ParticleDefinitionError( path + ".shape",
					$"shape '{em.Shape}' is not one of: {string.Join( ", ", Enum.GetNames<ParticleEmitterShape>() )}.",
					ParticleDefinitionSeverity.Error ) );
		}
		else shape = baseEm.Shape;
		// MeshSurface needs inline mesh geometry — NOT checked here.
		// Lane 5 fails loudly on it (ParticleEmitterDef.ToEmitter:
		// "MeshSurface shape needs a \"mesh\""), and this pack must not
		// duplicate the check: a composition of lane 5 + this pack
		// reports it exactly once, from lane 5. See the tests, which pin
		// that this pack emits no mesh finding while Build still throws.
		_ = shape;

		// [REPORT] Lifetime 0 < min <= max: ParticleEmitter.Validate.
		if ( !float.IsFinite( lifetimeMin ) || lifetimeMin <= 0 )
		{
			errors.Add( new ParticleDefinitionError( path + ".lifetimeMin",
				$"lifetimeMin {Fmt( lifetimeMin )} must be finite and > 0.",
				ParticleDefinitionSeverity.Error ) );
			usable = false;
		}
		if ( !float.IsFinite( lifetimeMax ) || lifetimeMax < lifetimeMin )
		{
			errors.Add( new ParticleDefinitionError( path + ".lifetimeMax",
				$"lifetimeMax {Fmt( lifetimeMax )} must be finite and >= lifetimeMin.",
				ParticleDefinitionSeverity.Error ) );
			usable = false;
		}
		// [NEW] Lifetime sanity upper bound: Warning only, judgment call
		// (see the file header). Checked only when the range itself is
		// sane, so one bad lifetime does not produce two findings.
		else if ( lifetimeMax > MaxLifetimeSeconds )
			errors.Add( new ParticleDefinitionError( path + ".lifetimeMax",
				$"lifetimeMax {Fmt( lifetimeMax )}s exceeds the sane upper bound of {MaxLifetimeSeconds}s ({Fmt( lifetimeMax * 60 )} steps at 60 Hz); this is almost certainly a units mistake (frames or minutes written as seconds).",
				ParticleDefinitionSeverity.Warning ) );

		// Range per the brief: sizeMin <= sizeMax, both >= 0. NOTE the
		// sim is STRICTER here, not looser: ParticleEmitter.Validate
		// requires 0 < SizeMin <= SizeMax, so a sizeMin of exactly 0
		// passes this pack and still fails lane 5's build gate. That
		// split is deliberate — this pack reports the brief's range;
		// lane 5 keeps the sim's bound — and the tests pin sizeMin 0 as
		// passing here so a future reader does not "fix" it blindly.
		if ( !float.IsFinite( sizeMin ) || sizeMin < 0 )
		{
			errors.Add( new ParticleDefinitionError( path + ".sizeMin",
				$"sizeMin {Fmt( sizeMin )} must be finite and >= 0.",
				ParticleDefinitionSeverity.Error ) );
			usable = false;
		}
		if ( !float.IsFinite( sizeMax ) || sizeMax < sizeMin )
		{
			errors.Add( new ParticleDefinitionError( path + ".sizeMax",
				$"sizeMax {Fmt( sizeMax )} must be finite and >= sizeMin.",
				ParticleDefinitionSeverity.Error ) );
			usable = false;
		}

		// [REPORT] Speed >= 0, SpeedJitter in 0..1:
		// ParticleEmitter.Validate.
		if ( !float.IsFinite( speed ) || speed < 0 )
			errors.Add( new ParticleDefinitionError( path + ".speed",
				$"speed {Fmt( speed )} must be finite and >= 0.",
				ParticleDefinitionSeverity.Error ) );
		if ( !float.IsFinite( speedJitter ) || speedJitter < 0 || speedJitter > 1 )
			errors.Add( new ParticleDefinitionError( path + ".speedJitter",
				$"speedJitter {Fmt( speedJitter )} must be finite and in 0..1.",
				ParticleDefinitionSeverity.Error ) );

		// [REPORT] Rate finite and >= 0: ParticleEmitter.Validate.
		if ( !float.IsFinite( rate ) || rate < 0 )
		{
			errors.Add( new ParticleDefinitionError( path + ".rate",
				$"rate {Fmt( rate )} must be finite and >= 0.",
				ParticleDefinitionSeverity.Error ) );
			usable = false;
		}

		// Burst counts, for bursts DECLARED in this def (def.Bursts !=
		// null). Preset-inherited bursts are the preset's business, not
		// a declaration this def made.
		long burstTotal = 0;
		bool burstsUsable = true;
		if ( em.Bursts is not null )
		{
			for ( int j = 0; j < em.Bursts.Count; j++ )
			{
				var b = em.Bursts[j];
				string bpath = $"{path}.bursts[{j}].count";
				if ( b is null )
				{
					// [REPORT] Lane 5's ToBursts throws on a null burst.
					errors.Add( new ParticleDefinitionError( bpath,
						"burst entries must be {\"step\": N, \"count\": N}.",
						ParticleDefinitionSeverity.Error ) );
					burstsUsable = false;
					continue;
				}
				// [NEW] A declared burst must declare a positive count:
				// the sim accepts Count == 0 (ParticleEmitter.Validate
				// only requires Count >= 0), but a burst that emits
				// nothing is a dead declaration — almost certainly a
				// forgotten count, not an intentional no-op. Negative
				// counts are a [REPORT] of the sim's Count >= 0 bound.
				if ( b.Count < 0 )
				{
					errors.Add( new ParticleDefinitionError( bpath,
						$"burst count {b.Count} must be >= 0.",
						ParticleDefinitionSeverity.Error ) );
					burstsUsable = false;
				}
				else if ( b.Count == 0 )
				{
					errors.Add( new ParticleDefinitionError( bpath,
						"burst count is 0: a declared burst that emits nothing. Drop the burst or give it a count.",
						ParticleDefinitionSeverity.Error ) );
					burstsUsable = false;
				}
				else burstTotal += b.Count;
			}
		}
		else
		{
			// No declared bursts: the preset's / live default's bursts
			// are what actually fires (e.g. sparks' 70-particle burst).
			foreach ( var b in baseEm.Bursts ) burstTotal += b.Count;
		}

		// [NEW] An enabled emitter that emits nothing: rate 0 AND no
		// burst particles. Warning, not Error — a silent emitter loads
		// fine and is sometimes intentional (an emitter kept for a cue
		// that never fires), but an author who meant it to emit will
		// otherwise stare at an empty scene wondering why. Disabled
		// emitters are exempt by definition. Preset bursts count: a
		// sparks line with rate 0 still emits its 70-particle burst.
		if ( enabled && usable && burstsUsable && !presetFailed
			&& float.IsFinite( rate ) && rate == 0 && burstTotal == 0 )
			errors.Add( new ParticleDefinitionError( path,
				"emitter is enabled but emits nothing (rate is 0 and it has no burst particles). Disable it, or give it a rate or a burst.",
				ParticleDefinitionSeverity.Warning ) );

		if ( enabled && usable && burstsUsable )
			contribution = (double)rate * lifetimeMax + burstTotal;
		return usable && burstsUsable;
	}

	static ParticleEmitter? TryCreatePreset( string name )
	{
		// Each pack's own ResolveName first, so a name that resolves in
		// one pack is created from that pack (names do not overlap
		// between the packs today, but creation must follow resolution).
		try
		{
			if ( ParticleEmitterPresets.ResolveName( name ) is string builtin )
				return ParticleEmitterPresets.Create( builtin, new Vector3( 0, 0, 0 ) );
			if ( ParticlePresetPackB.ResolveName( name ) is string packB )
				return ParticlePresetPackB.Create( packB, new Vector3( 0, 0, 0 ) );
			if ( ParticlePresetPackC.ResolveName( name ) is string packC )
				return ParticlePresetPackC.Create( packC, new Vector3( 0, 0, 0 ) );
		}
		catch ( ArgumentException )
		{
			// A resolving name that fails to create is a preset bug, not
			// a def problem this pack can report precisely; fall through
			// to live defaults and let lane 5's build surface it.
			return new ParticleEmitter();
		}
		return null;
	}

	static bool IsFiniteVec( float[]? v )
	{
		if ( v is null || v.Length != 3 ) return false;
		return float.IsFinite( v[0] ) && float.IsFinite( v[1] ) && float.IsFinite( v[2] );
	}

	static string Fmt( float f ) => float.IsFinite( f )
		? f.ToString( "0.##", CultureInfo.InvariantCulture )
		: f.ToString( CultureInfo.InvariantCulture );
}

// NOT DUPLICATED FROM LANE 5 (this pack deliberately does not check
// these; lane 5 / the sim remains the gate, and duplicating them would
// let the two copies drift apart):
//   - Version strictness at LOAD: ParticleAuthoring.FromJson throws on a
//     missing/wrong version and on malformed JSON. (ValidateDefinition
//     restates version as a report finding for objects; ValidateJson
//     keeps the throw — see the malformed-input boundary above.)
//   - Unknown preset/shape/render-type at BUILD: ToEmitter/ToSettings
//     throw via ResolveName/ParseEnum. (This pack restates shape and
//     render-type parse failures and unresolvable presets as findings
//     for reporting, but the build gate stays lane 5's.)
//   - MeshSurface without inline mesh: ParticleEmitterDef.ToEmitter
//     throws. NOT restated here, by explicit order: the composition
//     reports it exactly once, from lane 5.
//   - Vector shape/finiteness beyond settings.gravity (wind, vortex
//     centre/axis, ground normal, emitter position/direction): lane 5's
//     Vec plus ParticleSystemSettings/ParticleEmitter.Validate.
//   - FixedDeltaTime (0, 1], Substeps 1..64, TurbulenceFrequency > 0,
//     VortexRadius > 0, CollisionRadius > 0, Restitution/Friction 0..1,
//     SpreadDegrees 0..180, AngularSpeedMax >= 0, burst Step >= 0,
//     Area/Box/Sphere geometry rules, kill volumes, sub-emitters:
//     ParticleSystemSettings.Validate / ParticleEmitter.Validate,
//     reached through lane 5's Build.
//   - Unknown JSON fields: NOT an error anywhere, by lane 5's
//     forward-compat rule (ParticleAuthoring header). This pack's
//     unknown-field test pins that a future reader must not tighten it.
//
// LIMITS (honest, not buried):
//   - This pack stands alone: no wiring into authoring, UI, cues, or
//     saves exists. The coordinator sweep only needs the test
//     registration line; there is no production hookup line, because
//     nothing calls this validator yet.
//   - MaxLifetimeSeconds (3600) is a judgment call, not a sim limit,
//     and its finding is a Warning for exactly that reason.
//   - Negative vortex strength is an Error here but legal in the sim
//     (reversed swirl). If reverse swirls become a real authoring need,
//     relax that one check.
//   - SizeMin == 0 passes this pack (brief's >= 0 range) but fails
//     lane 5's build (sim requires > 0). The stricter bound lives in
//     lane 5 on purpose; this pack does not second-guess it.
//   - Seed presence is JSON-only (see header). An object-built def
//     cannot be checked for a missing seed.
//   - Expected peak is a worst case (rate x lifetimeMax + bursts), not
//     a simulated count: kill volumes, ground deaths, and sub-emitters
//     can only lower the real peak, so a Warning here means "at least
//     this many if nothing dies early" — the safe direction for a
//     capacity warning.
