// Particle .plist import (particles session, wave-6 lane P47, 2026-10-24).
//
// WHAT THIS IS: a parser for the classic 2D-editor particle-definition
// .plist format (the cocos2d / Particle Designer dictionary-of-keys shape:
// <plist><dict><key>...</key><real/integer/string/dict/array>...</dict></plist>),
// mapping it onto Geppetto's flat 2D particle settings (Particle2DSettings +
// Particle2DEmitter, see Particle2D.cs). IMPORT ONLY: nothing here writes a
// .plist back out, loads a texture file, decodes an image, draws anything,
// or wires into a UI or script verb. The texture reference is carried
// through as a NAME string only; texture support is a separate lane.
// The 2D suite owns the canvas that draws the imported system.
//
// COORDINATE / UNITS CONVERSION — READ THIS, IT IS THE WHOLE TRAP:
//   * Units: plist numbers are taken as Geppetto units 1:1 — plist points
//     become canvas pixels, plist seconds stay seconds. No scale factor is
//     applied: if a source editor's points are not canvas pixels, the
//     caller scales the numbers before parsing, because the file carries
//     no scale.
//   * Angles and gravity live in the classic plist Y-UP math convention
//     (angle 0 = +X, increasing counter-clockwise, +Y up), while Geppetto
//     is Y-DOWN (angle 0 = +X, 90 = +Y down, +Y down the screen). So the
//     Y component of every DIRECTION-like quantity is negated on import:
//     launch Direction = (cos(angle), -sin(angle)), and Gravity =
//     (plistGravity.x, -plistGravity.y). A plist angle of 90 (straight up
//     in the editor) therefore becomes Direction (0, -1) (rising) here, and
//     a plist gravity of (0, -50) (falling in the editor) becomes (0, +50)
//     (falling) here. Angle VARIANCE is symmetric, so it maps unchanged
//     to SpreadDegrees.
//   * Positions are NOT Y-flipped by default: flipping a position needs
//     the source canvas height, which the file does not carry. Position
//     passes through verbatim. If the caller knows the source canvas
//     height, pass it as canvasHeight and Position.Y becomes
//     canvasHeight - y (gravity/angle are still flipped exactly once —
//     the canvas height never touches them).
//   * Plist variance is a symmetric ± range sampled uniformly. That is
//     exactly Particle2D's variance model, so NO second variance model is
//     invented here: positionVariance becomes a Box emitter (uniform in
//     ±half-extents), lifetimeVariance becomes the LifetimeMin/Max range,
//     speedVariance becomes SpeedJitter (a ± fraction), and colour
//     variance becomes ColorJitter (a per-channel ± delta). See the table.
//
// MAPPING TABLE (plist key -> Geppetto field; "dropped" = warning, never silent):
//   emitterType 0 (gravity)  -> nothing (the only mode Particle2D has)
//   emitterType 1 (radial)   -> WARNING dropped (no radial mode in Particle2D)
//   emitterType other        -> loud failure (ArgumentException)
//   duration -1 (infinite)   -> nothing (an enabled emitter is the default)
//   duration >= 0            -> WARNING dropped (Particle2D has no stop time;
//                               the emitter stays enabled; disable it yourself)
//   emissionRate             -> Emitter.Rate (particles/second)
//   maxParticles             -> Settings.Capacity
//   particleLifeSpan (+ variance) -> Emitter.LifetimeMin/Max = span ∓ variance
//   sourcePosition           -> Emitter.Position (see canvasHeight rule above)
//   sourcePositionVariance   -> Box emitter, BoxHalfExtents = variance; (0,0) = Point
//   angle (+ variance)       -> Emitter.Direction = (cos, -sin) (Y flip, see above);
//                               Emitter.SpreadDegrees = angleVariance
//   speed (+ variance)       -> Emitter.Speed; SpeedJitter = variance / speed
//   gravity                  -> Settings.Gravity = (x, -y) (Y flip, see above)
//   startSize (+ variance)   -> Emitter.SizeMin/Max = size ∓ variance
//   endSize                  -> Emitter.SizeEndScale = endSize / startSize
//   startColor               -> Emitter.ColorStart (dict {r,g,b,a} or [r,g,b,a])
//   finishColor / endColor   -> Emitter.ColorEnd (same shapes)
//   startColorVariance       -> Emitter.ColorJitter = max RGB variance channel
//   textureFileName / texture / textureName -> TextureName string ONLY (no loading)
//   radialAcceleration, radialAccelVariance, tangentialAcceleration,
//     tangentialAccelVariance -> WARNING dropped (no radial/tangential forces)
//   endSizeVariance          -> WARNING dropped (one SizeEndScale can't hold a range)
//   finishColorVariance / endColorVariance -> WARNING dropped (Particle2D shares
//                               one jitter delta between start and end colours)
//   startColorVariance alpha channel -> WARNING dropped (Particle2D jitters RGB only)
//   blendFuncSource, blendFuncDestination -> WARNING dropped (draw records carry no blend mode)
//   rotationStart, rotationStartVariance, rotationEnd, rotationEndVariance
//                            -> WARNING dropped (Particle2D has AngularSpeedMax,
//                               not start/end rotation)
//   positionType             -> WARNING dropped (no emitter-follow mode in Particle2D)
//   maxRadius, minRadius, rotatePerSecond, rotatePerSecondVariance
//                            -> WARNING dropped (radial-emitter-only fields)
//   any other key            -> WARNING dropped (unknown key, forward-compat: warned, not failed)
//   absent gravity           -> Settings.Gravity = (0, 0). A .plist is a complete
//                               effect description, so an omitted gravity means the
//                               source editor had no gravity — NOT Geppetto's
//                               Particle2D default of (0, 980). Every other absent
//                               field keeps the Particle2D live default.
//   absent angle + speed > 0 -> Emitter.Direction stays (0, 1) (falling), the
//                               Particle2D default.
// CLAMPS (each adds a warning, so the clamp is never silent):
//   lifetime span - variance <= 0 -> LifetimeMin clamped to 0.001 s.
//   size start - variance <= 0    -> SizeMin clamped to 0.001 px.
//   speed variance > speed       -> SpeedJitter clamped to 1 (the Particle2D max).
//   negative variance anywhere   -> loud failure (a negative ± range is malformed).
// LOUD FAILURE (ArgumentException, never a partial result): malformed XML, a
// root that is not <plist> holding one <dict>, a non-dict root value, a key
// with no value, a duplicate key, an unsupported plist value type (<data>,
// <date>), a value of the wrong shape for its key (e.g. a string where a
// number belongs, a 3-component colour), an unknown emitterType, or any
// mapped result the live Validate() methods reject (e.g. SpreadDegrees > 180,
// Capacity out of 1..1,000,000, a colour channel outside 0..1).

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Marionette.Desktop;

/// <summary>Result of a .plist import: live 2D settings + emitter, the
/// texture name (string only, never loaded), and every dropped-field
/// warning. Warnings is empty exactly when nothing was dropped.</summary>
public sealed class ParticlePlistResult
{
	public required Particle2DSettings Settings { get; init; }
	public required Particle2DEmitter Emitter { get; init; }
	public string? TextureName { get; init; }
	public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Parses classic 2D-editor particle .plist files into
/// <see cref="Particle2DSettings"/> + <see cref="Particle2DEmitter"/>.
/// See the file header for the mapping table and the coordinate/units
/// conversion rules. Import only — this never writes a .plist, loads a
/// texture, or touches a sim, a canvas, or a script verb.</summary>
public static class ParticlePlistImport
{
	/// <summary>Parses plist XML text. <paramref name="canvasHeight"/>,
	/// when given, flips the source position's Y (canvasHeight - y); it
	/// never affects gravity or angle, which are flipped exactly once
	/// by the Y-up -> Y-down conversion. Throws ArgumentException on
	/// any malformed input (see the file header).</summary>
	public static ParticlePlistResult Parse( string plistXml, float? canvasHeight = null )
	{
		ArgumentNullException.ThrowIfNull( plistXml );
		Dictionary<string, object?> dict;
		try
		{
			var doc = XDocument.Parse( plistXml );
			if ( doc.Root is null || doc.Root.Name.LocalName != "plist" )
				throw new ArgumentException( "particle plist: root element must be <plist>." );
			var children = new List<XElement>();
			foreach ( var el in doc.Root.Elements() ) children.Add( el );
			if ( children.Count != 1 || children[0].Name.LocalName != "dict" )
				throw new ArgumentException( "particle plist: <plist> must hold exactly one <dict>." );
			dict = ParseDict( children[0] );
		}
		catch ( ArgumentException ) { throw; }
		catch ( Exception e ) when ( e is System.Xml.XmlException or InvalidOperationException or FormatException or OverflowException )
		{ throw new ArgumentException( $"particle plist: malformed XML: {e.Message}", e ); }

		var warnings = new List<string>();
		void Warn( string message ) => warnings.Add( message );

		var settings = new Particle2DSettings { Gravity = Vector2.Zero };
		var emitter = new Particle2DEmitter();

		// Keys this parser knows. Anything else is warned-and-dropped below.
		var known = new HashSet<string>( StringComparer.Ordinal )
		{
			"emitterType", "duration", "emissionRate", "maxParticles",
			"particleLifeSpan", "particleLifespan", "particleLifeSpanVariance", "particleLifespanVariance",
			"sourcePosition", "sourcePositionVariance",
			"angle", "angleVariance", "speed", "speedVariance", "gravity",
			"radialAcceleration", "radialAccelVariance", "tangentialAcceleration", "tangentialAccelVariance",
			"startSize", "startSizeVariance", "endSize", "endSizeVariance",
			"startColor", "startColorVariance", "finishColor", "finishColorVariance", "endColor", "endColorVariance",
			"blendFuncSource", "blendFuncDestination",
			"textureFileName", "texture", "textureName",
			"rotationStart", "rotationStartVariance", "rotationEnd", "rotationEndVariance",
			"positionType", "maxRadius", "minRadius", "rotatePerSecond", "rotatePerSecondVariance",
		};

		float Num( string key ) => (float)AsNumber( dict, key );
		bool Has( string key ) => dict.ContainsKey( key );

		if ( Has( "emitterType" ) )
		{
			long mode = AsInteger( dict, "emitterType" );
			if ( mode == 0 ) { /* gravity mode: Particle2D's only mode */ }
			else if ( mode == 1 ) Warn( "emitterType 1 (radial) has no Particle2D counterpart; radial/tangential fields are dropped." );
			else throw new ArgumentException( $"particle plist: unknown emitterType {mode} (0 = gravity, 1 = radial)." );
		}
		if ( Has( "duration" ) )
		{
			float duration = Num( "duration" );
			if ( duration >= 0 )
				Warn( $"duration {duration} s has no stop-time counterpart in Particle2D; the emitter stays enabled — disable it yourself when the effect should stop." );
		}
		if ( Has( "emissionRate" ) ) emitter.Rate = Num( "emissionRate" );
		if ( Has( "maxParticles" ) ) settings.Capacity = (int)AsInteger( dict, "maxParticles" );
		if ( Has( "sourcePosition" ) )
		{
			var p = AsVector2( dict, "sourcePosition" );
			emitter.Position = canvasHeight is { } h ? new Vector2( p.X, h - p.Y ) : p;
		}
		if ( Has( "sourcePositionVariance" ) )
		{
			var v = AsVector2( dict, "sourcePositionVariance" );
			if ( v.X < 0 || v.Y < 0 ) throw new ArgumentException( "particle plist: sourcePositionVariance must be >= 0 on both axes." );
			if ( v != Vector2.Zero ) { emitter.Shape = Particle2DEmitterShape.Box; emitter.BoxHalfExtents = v; }
		}
		if ( Has( "angle" ) )
		{
			float a = Num( "angle" ) * MathF.PI / 180f;
			emitter.Direction = new Vector2( MathF.Cos( a ), -MathF.Sin( a ) );
		}
		if ( Has( "angleVariance" ) ) emitter.SpreadDegrees = Num( "angleVariance" );
		if ( Has( "speed" ) ) emitter.Speed = Num( "speed" );
		if ( Has( "speedVariance" ) )
		{
			float sv = Num( "speedVariance" );
			if ( sv < 0 ) throw new ArgumentException( "particle plist: speedVariance must be >= 0." );
			if ( emitter.Speed > 0 )
			{
				emitter.SpeedJitter = sv / emitter.Speed;
				if ( emitter.SpeedJitter > 1 )
				{
					Warn( $"speedVariance {sv} exceeds speed {emitter.Speed}; SpeedJitter clamped to 1 (the Particle2D maximum)." );
					emitter.SpeedJitter = 1;
				}
			}
			else if ( sv > 0 ) Warn( "speedVariance dropped: speed is 0, so there is no speed to vary." );
		}
		if ( Has( "gravity" ) )
		{
			var g = AsVector2( dict, "gravity" );
			settings.Gravity = new Vector2( g.X, -g.Y );
		}
		if ( Has( "particleLifeSpan" ) || Has( "particleLifespan" ) )
		{
			float span = Has( "particleLifeSpan" ) ? Num( "particleLifeSpan" ) : Num( "particleLifespan" );
			float variance = Has( "particleLifeSpanVariance" ) ? Num( "particleLifeSpanVariance" )
				: Has( "particleLifespanVariance" ) ? Num( "particleLifespanVariance" ) : 0;
			if ( variance < 0 ) throw new ArgumentException( "particle plist: particleLifeSpanVariance must be >= 0." );
			emitter.LifetimeMin = span - variance;
			if ( emitter.LifetimeMin <= 0 )
			{
				Warn( $"lifetime span {span} minus variance {variance} is <= 0; LifetimeMin clamped to 0.001 s." );
				emitter.LifetimeMin = 0.001f;
			}
			emitter.LifetimeMax = span + variance;
		}
		else if ( Has( "particleLifeSpanVariance" ) || Has( "particleLifespanVariance" ) )
			Warn( "particleLifeSpanVariance dropped: no particleLifeSpan to vary." );
		if ( Has( "startSize" ) )
		{
			float size = Num( "startSize" );
			float variance = Has( "startSizeVariance" ) ? Num( "startSizeVariance" ) : 0;
			if ( variance < 0 ) throw new ArgumentException( "particle plist: startSizeVariance must be >= 0." );
			emitter.SizeMin = size - variance;
			if ( emitter.SizeMin <= 0 )
			{
				Warn( $"startSize {size} minus variance {variance} is <= 0; SizeMin clamped to 0.001 px." );
				emitter.SizeMin = 0.001f;
			}
			emitter.SizeMax = size + variance;
			if ( Has( "endSize" ) )
				emitter.SizeEndScale = size > 0 ? Num( "endSize" ) / size : Num( "endSize" ) == 0 ? 1 : throw new ArgumentException( "particle plist: endSize > 0 with startSize 0 cannot map to SizeEndScale." );
		}
		else if ( Has( "endSize" ) )
			Warn( "endSize dropped: no startSize to scale from." );
		if ( Has( "endSizeVariance" ) )
			Warn( "endSizeVariance dropped: Particle2D holds one SizeEndScale, not an end-size range." );
		if ( Has( "startColor" ) ) emitter.ColorStart = AsColor( dict, "startColor" );
		if ( Has( "finishColor" ) ) emitter.ColorEnd = AsColor( dict, "finishColor" );
		else if ( Has( "endColor" ) ) emitter.ColorEnd = AsColor( dict, "endColor" );
		if ( Has( "startColorVariance" ) )
		{
			var v = AsColor( dict, "startColorVariance" );
			emitter.ColorJitter = MathF.Max( v.R, MathF.Max( v.G, v.B ) );
			if ( v.A > 0 ) Warn( "startColorVariance alpha dropped: Particle2D jitters RGB channels only." );
		}
		if ( Has( "finishColorVariance" ) || Has( "endColorVariance" ) )
			Warn( "finishColorVariance dropped: Particle2D shares one colour-jitter delta between start and end colours." );
		foreach ( var key in new[] { "radialAcceleration", "radialAccelVariance", "tangentialAcceleration", "tangentialAccelVariance" } )
			if ( Has( key ) ) Warn( $"{key} dropped: Particle2D has no radial/tangential acceleration forces." );
		foreach ( var key in new[] { "blendFuncSource", "blendFuncDestination" } )
			if ( Has( key ) ) Warn( $"{key} dropped: Particle2D draw records carry no blend mode." );
		foreach ( var key in new[] { "rotationStart", "rotationStartVariance", "rotationEnd", "rotationEndVariance" } )
			if ( Has( key ) ) Warn( $"{key} dropped: Particle2D has no start/end rotation (see AngularSpeedMax / EmitDirectionAsAngle)." );
		if ( Has( "positionType" ) ) Warn( "positionType dropped: Particle2D has no emitter-follow mode." );
		foreach ( var key in new[] { "maxRadius", "minRadius", "rotatePerSecond", "rotatePerSecondVariance" } )
			if ( Has( key ) ) Warn( $"{key} dropped: radial-emitter-only field, no Particle2D counterpart." );

		string? textureName = null;
		foreach ( var key in new[] { "textureFileName", "texture", "textureName" } )
			if ( Has( key ) ) textureName = AsString( dict, key );
		// The texture name is a string only: never opened, never decoded.
		// Texture support itself is a separate lane.

		foreach ( var key in dict.Keys )
			if ( !known.Contains( key ) ) Warn( $"unknown key '{key}' dropped." );

		try
		{
			settings.Validate();
			emitter.Validate( 0 );
		}
		catch ( Exception e ) when ( e is ArgumentException or ArgumentOutOfRangeException )
		{ throw new ArgumentException( $"particle plist: mapped settings are invalid: {e.Message}", e ); }

		return new ParticlePlistResult { Settings = settings, Emitter = emitter, TextureName = textureName, Warnings = warnings };
	}

	static Dictionary<string, object?> ParseDict( XElement dictEl )
	{
		var result = new Dictionary<string, object?>( StringComparer.Ordinal );
		var children = new List<XElement>();
		foreach ( var el in dictEl.Elements() ) children.Add( el );
		for ( int i = 0; i < children.Count; i++ )
		{
			if ( children[i].Name.LocalName != "key" )
				throw new ArgumentException( "particle plist: expected <key> in <dict>." );
			string key = children[i].Value;
			if ( i + 1 >= children.Count )
				throw new ArgumentException( $"particle plist: key '{key}' has no value." );
			if ( result.ContainsKey( key ) )
				throw new ArgumentException( $"particle plist: duplicate key '{key}'." );
			result[key] = ParseValue( children[i + 1] );
			i++;
		}
		return result;
	}

	static object? ParseValue( XElement el ) => el.Name.LocalName switch
	{
		"integer" => long.Parse( el.Value, NumberStyles.Integer, CultureInfo.InvariantCulture ),
		"real" => double.Parse( el.Value, NumberStyles.Float, CultureInfo.InvariantCulture ),
		"string" => el.Value,
		"true" => true,
		"false" => false,
		"dict" => ParseDict( el ),
		"array" => ParseArray( el ),
		var other => throw new ArgumentException( $"particle plist: unsupported value type <{other}>." ),
	};

	static List<object?> ParseArray( XElement arrayEl )
	{
		var list = new List<object?>();
		foreach ( var el in arrayEl.Elements() ) list.Add( ParseValue( el ) );
		return list;
	}

	static double AsNumber( Dictionary<string, object?> dict, string key )
	{
		if ( !dict.TryGetValue( key, out var value ) ) throw new ArgumentException( $"particle plist: missing key '{key}'." );
		return value switch
		{
			long l => l,
			double d => d,
			_ => throw new ArgumentException( $"particle plist: key '{key}' must be a number." ),
		};
	}

	static long AsInteger( Dictionary<string, object?> dict, string key )
	{
		if ( !dict.TryGetValue( key, out var value ) ) throw new ArgumentException( $"particle plist: missing key '{key}'." );
		return value switch
		{
			long l => l,
			double d when d == Math.Truncate( d ) => (long)d,
			_ => throw new ArgumentException( $"particle plist: key '{key}' must be an integer." ),
		};
	}

	static string AsString( Dictionary<string, object?> dict, string key )
	{
		if ( !dict.TryGetValue( key, out var value ) || value is not string s )
			throw new ArgumentException( $"particle plist: key '{key}' must be a string." );
		return s;
	}

	static Vector2 AsVector2( Dictionary<string, object?> dict, string key )
	{
		if ( !dict.TryGetValue( key, out var value ) ) throw new ArgumentException( $"particle plist: missing key '{key}'." );
		if ( value is Dictionary<string, object?> d )
			return new Vector2( (float)AsNumber( d, "x" ), (float)AsNumber( d, "y" ) );
		if ( value is List<object?> list && list.Count == 2 )
			return new Vector2( (float)ToNumber( list[0], key ), (float)ToNumber( list[1], key ) );
		throw new ArgumentException( $"particle plist: key '{key}' must be a dict {{x, y}} or a 2-number array." );
	}

	static Particle2DColor AsColor( Dictionary<string, object?> dict, string key )
	{
		if ( !dict.TryGetValue( key, out var value ) ) throw new ArgumentException( $"particle plist: missing key '{key}'." );
		if ( value is Dictionary<string, object?> d )
			return new Particle2DColor( (float)AsNumber( d, "r" ), (float)AsNumber( d, "g" ), (float)AsNumber( d, "b" ), (float)AsNumber( d, "a" ) );
		if ( value is List<object?> list && list.Count == 4 )
			return new Particle2DColor( (float)ToNumber( list[0], key ), (float)ToNumber( list[1], key ), (float)ToNumber( list[2], key ), (float)ToNumber( list[3], key ) );
		throw new ArgumentException( $"particle plist: key '{key}' must be a dict {{r, g, b, a}} or a 4-number array." );
	}

	static double ToNumber( object? value, string key ) => value switch
	{
		long l => l,
		double d => d,
		_ => throw new ArgumentException( $"particle plist: key '{key}' components must be numbers." ),
	};
}
