using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// A 2D animation clip: one timeline, three track types, no 3D rig required.
///
/// (a) Drawing tracks — exposure sheets: which drawing an element shows, and for how many
/// frames it holds it. (b) Property tracks — one full-state key per element per keyframe:
/// x/y, rotation, scale, opacity, draw order, sprite frame index, eased with
/// <see cref="RigEase"/> via <see cref="RigKey.Apply"/> (reused, never redefined, and
/// <see cref="RigAnimFile"/> / <see cref="RigTrackKind"/> are NOT touched here — their order
/// is Marionette's and stays frozen). (c) Event tracks — things that FIRE on a frame: a
/// particle cue, a sound cue, a switch key, a draw-order key. A particle event is only a
/// cue record (name + optional retime/value for the external effect); this file contains
/// no particle simulation, and the particles session owns that. Likewise this file owns no
/// sprite sheet: <see cref="Anim2DPropertyKey.SpriteFrame"/> is an index into a sheet some
/// other lane bakes, and <see cref="Anim2DDrawingTrack"/> drawings are IDs, not pixels.
///
/// Evaluation mirrors <see cref="RigTrack.Evaluate"/>: held before the first key and after
/// the last, eased between two keys by the FIRST of them. Discrete fields (draw order,
/// sprite frame) always hold — they never interpolate, whatever ease the key carries.
/// Keys need not be pre-sorted; evaluation picks the bracketing pair by frame, last in
/// list winning a tie, so an unsorted authoring list still samples deterministically.
/// </summary>
public sealed class Anim2DClip
{
	public string Name = "anim2d";

	/// <summary>Frames per second the frame numbers below are counted in.</summary>
	public int FrameRate = 30;

	/// <summary>Declared length in frames; 0 means "derive it" (see <see cref="LastFrame"/>).</summary>
	public int FrameCount;

	public List<Anim2DDrawingTrack> DrawingTracks = new();
	public List<Anim2DPropertyTrack> PropertyTracks = new();
	public List<Anim2DEventTrack> EventTracks = new();

	/// <summary>The last frame anything is keyed, exposed or fired on, or -1 for an empty clip.</summary>
	public int LastFrame
	{
		get
		{
			var last = -1;
			foreach ( var t in DrawingTracks )
				foreach ( var e in t.Exposures )
					last = Math.Max( last, e.Frame + Math.Max( e.Hold - 1, 0 ) );
			foreach ( var t in PropertyTracks )
				foreach ( var k in t.Keys )
					last = Math.Max( last, k.Frame );
			foreach ( var t in EventTracks )
				foreach ( var e in t.Events )
					last = Math.Max( last, e.Frame );
			return last;
		}
	}

	/// <summary>Effective length: the declared count, or last-key-plus-one when undeclared.</summary>
	public int EffectiveFrameCount => FrameCount > 0 ? FrameCount : LastFrame + 1;

	/// <summary>Every element the clip mentions, in first-appearance order.</summary>
	public IEnumerable<string> Elements
	{
		get
		{
			var seen = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var t in DrawingTracks )
				if ( seen.Add( t.Element ) ) yield return t.Element;
			foreach ( var t in PropertyTracks )
				if ( seen.Add( t.Element ) ) yield return t.Element;
			foreach ( var t in EventTracks )
				if ( seen.Add( t.Element ) ) yield return t.Element;
		}
	}

	/// <summary>Sample the whole clip at a (possibly fractional) frame.</summary>
	public Anim2DFrame Sample( float frame )
	{
		var result = new Anim2DFrame { Frame = (int)MathF.Floor( frame ) };
		var byElement = new Dictionary<string, Anim2DElementState>( StringComparer.Ordinal );

		Anim2DElementState State( string element )
		{
			if ( !byElement.TryGetValue( element, out var s ) )
			{
				s = new Anim2DElementState { Element = element };
				byElement.Add( element, s );
			}
			return s;
		}

		foreach ( var track in DrawingTracks )
			State( track.Element ).DrawingId = track.DrawingAt( result.Frame );

		foreach ( var track in PropertyTracks )
			track.ApplyTo( frame, State( track.Element ) );

		foreach ( var track in EventTracks )
			foreach ( var e in track.EventsAt( result.Frame ) )
				State( track.Element ).Events.Add( e );

		// Draw order first, name second so equal orders still come out stable.
		result.Elements = byElement.Values
			.OrderBy( s => s.DrawOrder )
			.ThenBy( s => s.Element, StringComparer.Ordinal )
			.ToList();
		return result;
	}

	/// <summary>
	/// Write the clip as JSON in <see cref="RigAnimWriter"/>'s shape — "x,y" position strings,
	/// enums by name, AnimationSpeed/FrameCount — WITHOUT touching RigAnimWriter itself, so a
	/// 2D clip file reads like a .riganim to any tool that already reads those conventions.
	/// </summary>
	public string WriteJson()
	{
		var root = new JsonObject
		{
			["Format"] = "anim2d",
			["Name"] = Name,
			["AnimationSpeed"] = FrameRate,
			["FrameCount"] = EffectiveFrameCount,
		};

		var drawings = new JsonArray();
		foreach ( var t in DrawingTracks )
		{
			var exposures = new JsonArray();
			foreach ( var e in t.Exposures )
				exposures.Add( new JsonObject { ["Frame"] = e.Frame, ["Drawing"] = e.DrawingId ?? "", ["Hold"] = e.Hold } );
			drawings.Add( new JsonObject { ["Element"] = t.Element, ["Exposures"] = exposures } );
		}
		root["DrawingTracks"] = drawings;

		var props = new JsonArray();
		foreach ( var t in PropertyTracks )
		{
			var keys = new JsonArray();
			foreach ( var k in t.Keys )
				keys.Add( new JsonObject
				{
					["Frame"] = k.Frame,
					["Position"] = F( k.X ) + "," + F( k.Y ),
					["Rotation"] = k.RotationDegrees,
					["Scale"] = F( k.ScaleX ) + "," + F( k.ScaleY ),
					["Opacity"] = k.Opacity,
					["DrawOrder"] = k.DrawOrder,
					["SpriteFrame"] = k.SpriteFrame,
					["Interpolation"] = k.Ease.ToString(),
				} );
			props.Add( new JsonObject { ["Element"] = t.Element, ["Keys"] = keys } );
		}
		root["PropertyTracks"] = props;

		var events = new JsonArray();
		foreach ( var t in EventTracks )
		{
			var list = new JsonArray();
			foreach ( var e in t.Events )
				list.Add( new JsonObject
				{
					["Frame"] = e.Frame,
					["Kind"] = e.Kind.ToString(),
					["Target"] = e.Target ?? "",
					["Name"] = e.Name ?? "",
					["Value"] = e.Value,
				} );
			events.Add( new JsonObject { ["Element"] = t.Element, ["Events"] = list } );
		}
		root["EventTracks"] = events;

		return root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
	}

	/// <summary>Read back exactly what <see cref="WriteJson"/> writes; unknown fields are ignored.</summary>
	public static Anim2DClip ParseJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new ArgumentException( "empty clip JSON", nameof( json ) );
		var root = JsonNode.Parse( json.TrimStart( '﻿' ) )?.AsObject()
			?? throw new InvalidOperationException( "clip JSON is not an object" );
		var clip = new Anim2DClip
		{
			Name = root["Name"]?.GetValue<string>() ?? "anim2d",
			FrameRate = root["AnimationSpeed"]?.GetValue<int>() ?? 30,
			FrameCount = root["FrameCount"]?.GetValue<int>() ?? 0,
		};

		foreach ( var node in root["DrawingTracks"] as JsonArray ?? new JsonArray() )
		{
			if ( node is not JsonObject t ) continue;
			var track = new Anim2DDrawingTrack { Element = t["Element"]?.GetValue<string>() ?? "" };
			foreach ( var en in t["Exposures"] as JsonArray ?? new JsonArray() )
				if ( en is JsonObject e )
					track.Exposures.Add( new Anim2DExposure
					{
						Frame = e["Frame"]?.GetValue<int>() ?? 0,
						DrawingId = e["Drawing"]?.GetValue<string>() ?? "",
						Hold = e["Hold"]?.GetValue<int>() ?? 0,
					} );
			clip.DrawingTracks.Add( track );
		}

		foreach ( var node in root["PropertyTracks"] as JsonArray ?? new JsonArray() )
		{
			if ( node is not JsonObject t ) continue;
			var track = new Anim2DPropertyTrack { Element = t["Element"]?.GetValue<string>() ?? "" };
			foreach ( var kn in t["Keys"] as JsonArray ?? new JsonArray() )
			{
				if ( kn is not JsonObject k ) continue;
				var pos = (k["Position"]?.GetValue<string>() ?? "0,0").Split( ',' );
				var scale = (k["Scale"]?.GetValue<string>() ?? "1,1").Split( ',' );
				track.Keys.Add( new Anim2DPropertyKey
				{
					Frame = k["Frame"]?.GetValue<int>() ?? 0,
					X = pos.Length > 0 && float.TryParse( pos[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x ) ? x : 0f,
					Y = pos.Length > 1 && float.TryParse( pos[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y ) ? y : 0f,
					RotationDegrees = k["Rotation"]?.GetValue<float>() ?? 0f,
					ScaleX = scale.Length > 0 && float.TryParse( scale[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sx ) ? sx : 1f,
					ScaleY = scale.Length > 1 && float.TryParse( scale[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sy ) ? sy : 1f,
					Opacity = k["Opacity"]?.GetValue<float>() ?? 1f,
					DrawOrder = k["DrawOrder"]?.GetValue<int>() ?? 0,
					SpriteFrame = k["SpriteFrame"]?.GetValue<int>() ?? 0,
					Ease = Enum.TryParse<RigEase>( k["Interpolation"]?.GetValue<string>(), out var ease ) ? ease : RigEase.Smooth,
				} );
			}
			clip.PropertyTracks.Add( track );
		}

		foreach ( var node in root["EventTracks"] as JsonArray ?? new JsonArray() )
		{
			if ( node is not JsonObject t ) continue;
			var track = new Anim2DEventTrack { Element = t["Element"]?.GetValue<string>() ?? "" };
			foreach ( var en in t["Events"] as JsonArray ?? new JsonArray() )
				if ( en is JsonObject e )
					track.Events.Add( new Anim2DEvent
					{
						Frame = e["Frame"]?.GetValue<int>() ?? 0,
						Kind = Enum.TryParse<Anim2DEventKind>( e["Kind"]?.GetValue<string>(), out var kind ) ? kind : Anim2DEventKind.Sound,
						Target = e["Target"]?.GetValue<string>() ?? "",
						Name = e["Name"]?.GetValue<string>() ?? "",
						Value = e["Value"]?.GetValue<float>() ?? 0f,
					} );
			clip.EventTracks.Add( track );
		}

		return clip;
	}

	static string F( float v ) => v.ToString( "R", CultureInfo.InvariantCulture );
}

/// <summary>One drawing held on an exposure sheet. Hold 0 means "until the next exposure".</summary>
public sealed class Anim2DExposure
{
	public int Frame;
	public string DrawingId = "";
	public int Hold;
}

/// <summary>
/// An element's exposure sheet. The drawing at a frame is the last exposure that started at
/// or before it and has not run out its hold; before the first exposure there is no drawing.
/// </summary>
public sealed class Anim2DDrawingTrack
{
	public string Element = "";
	public List<Anim2DExposure> Exposures = new();

	public string DrawingAt( int frame )
	{
		Anim2DExposure best = null;
		foreach ( var e in Exposures )
		{
			if ( e.Frame > frame ) continue;
			if ( best is not null && e.Frame < best.Frame ) continue;
			if ( e.Hold > 0 && frame >= e.Frame + e.Hold ) continue;
			best = e;
		}
		return best?.DrawingId;
	}
}

/// <summary>
/// One full-state key on an element's property track. Rotation is in DEGREES (animator units;
/// the converter turns it into radians). DrawOrder and SpriteFrame are discrete: they hold
/// from their key and never interpolate, whatever <see cref="Ease"/> says.
/// </summary>
public sealed class Anim2DPropertyKey
{
	public int Frame;
	public float X, Y;
	public float RotationDegrees;
	public float ScaleX = 1f, ScaleY = 1f;
	public float Opacity = 1f;
	public int DrawOrder;
	public int SpriteFrame;
	public RigEase Ease = RigEase.Smooth;
}

public sealed class Anim2DPropertyTrack
{
	public string Element = "";
	public List<Anim2DPropertyKey> Keys = new();

	/// <summary>Evaluate this element's state at a (possibly fractional) frame, in place.</summary>
	public void ApplyTo( float frame, Anim2DElementState state )
	{
		if ( Keys.Count == 0 ) return;

		// Bracketing pair by frame, last in list winning a tie — order-independent.
		Anim2DPropertyKey prev = null, next = null;
		foreach ( var k in Keys )
		{
			if ( k.Frame <= frame && (prev is null || k.Frame >= prev.Frame) ) prev = k;
			if ( k.Frame > frame && (next is null || k.Frame < next.Frame) ) next = k;
		}

		if ( prev is null ) { Copy( Keys.OrderBy( k => k.Frame ).First(), state ); return; }
		if ( next is null ) { Copy( prev, state ); return; }

		var span = next.Frame - prev.Frame;
		var t = span > 0 ? (frame - prev.Frame) / span : 0f;
		state.X = Eval( prev, next, t, k => k.X );
		state.Y = Eval( prev, next, t, k => k.Y );
		state.RotationDegrees = Eval( prev, next, t, k => k.RotationDegrees );
		state.ScaleX = Eval( prev, next, t, k => k.ScaleX );
		state.ScaleY = Eval( prev, next, t, k => k.ScaleY );
		state.Opacity = Math.Clamp( Eval( prev, next, t, k => k.Opacity ), 0f, 1f );
		// Discrete fields hold from the outgoing key.
		state.DrawOrder = prev.DrawOrder;
		state.SpriteFrame = prev.SpriteFrame;
	}

	void Copy( Anim2DPropertyKey k, Anim2DElementState state )
	{
		state.X = k.X;
		state.Y = k.Y;
		state.RotationDegrees = k.RotationDegrees;
		state.ScaleX = k.ScaleX;
		state.ScaleY = k.ScaleY;
		state.Opacity = Math.Clamp( k.Opacity, 0f, 1f );
		state.DrawOrder = k.DrawOrder;
		state.SpriteFrame = k.SpriteFrame;
	}

	static float Eval( Anim2DPropertyKey a, Anim2DPropertyKey b, float t, Func<Anim2DPropertyKey, float> pick )
	{
		// Stepped holds outright. Cubic runs Catmull-Rom through the neighbours (needs the
		// bracketing keys' neighbours, which ApplyTo does not pass — so Cubic here degrades
		// to smoothstep, exactly as RigKey.Apply degrades Cubic/Monotone to a plain remap
		// outside RigHermite; the full Hermite path lives on the converted Bone track).
		// Monotone is smoothstep, which never leaves the segment's endpoints.
		return a.Ease switch
		{
			RigEase.Stepped => pick( a ),
			RigEase.Cubic or RigEase.Monotone => pick( a ) + (pick( b ) - pick( a )) * (t * t * (3f - 2f * t)),
			_ => pick( a ) + (pick( b ) - pick( a )) * RigKey.Apply( a.Ease, t ),
		};
	}
}

/// <summary>
/// What an event track fires. Particle = fire/retime an EXTERNAL particle effect by cue name;
/// this clip never simulates anything. Sound = a cue name for the audio side. Switch = change
/// an element's active drawing/state to <see cref="Anim2DEvent.Name"/>. DrawOrder = a
/// draw-order key carried as an event for tools that author order as events rather than
/// property keys (its <see cref="Anim2DEvent.Value"/> is the new order).
/// </summary>
public enum Anim2DEventKind
{
	Particle,
	Sound,
	Switch,
	DrawOrder,
}

public sealed class Anim2DEvent
{
	public int Frame;
	public Anim2DEventKind Kind;
	public string Target = "";
	public string Name = "";
	public float Value;
}

public sealed class Anim2DEventTrack
{
	public string Element = "";
	public List<Anim2DEvent> Events = new();

	/// <summary>Events fire on their exact frame only — they do not hold.</summary>
	public IEnumerable<Anim2DEvent> EventsAt( int frame )
	{
		foreach ( var e in Events )
			if ( e.Frame == frame ) yield return e;
	}
}

/// <summary>One element's sampled state on one frame.</summary>
public sealed class Anim2DElementState
{
	public string Element = "";
	public float X, Y;
	public float RotationDegrees;
	public float ScaleX = 1f, ScaleY = 1f;
	public float Opacity = 1f;
	public int DrawOrder;
	public int SpriteFrame;
	public string DrawingId;
	public List<Anim2DEvent> Events = new();
}

/// <summary>A whole sampled frame: elements already sorted by draw order, then name.</summary>
public sealed class Anim2DFrame
{
	public int Frame;
	public List<Anim2DElementState> Elements = new();

	public Anim2DElementState this[ string element ] =>
		Elements.FirstOrDefault( s => s.Element == element );
}

/// <summary>
/// 2D clip -> keyed <see cref="RigAnimFile"/> for preview in the existing
/// <c>MarionettePlayer</c>: one Bone track per mapped element, x/y as the bone's local
/// position (z = 0), rotation about Z, scale as (ScaleX, ScaleY, 1), ease preserved, so the
/// Hermite path in <see cref="RigTrack.Evaluate"/> runs for Cubic/Monotone keys.
/// </summary>
public static class Anim2DConverter
{
	public sealed class Result
	{
		public RigAnimFile Clip;
		public string[] SkippedElements = Array.Empty<string>();
	}

	/// <param name="elementToBone">2D element name -> bone name. Unmapped elements are skipped
	/// and reported in <see cref="Result.SkippedElements"/>; they are not silently renamed.</param>
	public static Result ToRigAnim( Anim2DClip clip, IReadOnlyDictionary<string, string> elementToBone )
	{
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );
		if ( elementToBone is null ) throw new ArgumentNullException( nameof( elementToBone ) );

		var out_ = new RigAnimFile
		{
			FrameRate = clip.FrameRate,
			FrameCount = clip.EffectiveFrameCount,
		};
		var skipped = new List<string>();

		foreach ( var track in clip.PropertyTracks )
		{
			if ( !elementToBone.TryGetValue( track.Element, out var bone ) || string.IsNullOrWhiteSpace( bone ) )
			{
				skipped.Add( track.Element );
				continue;
			}
			var boneTrack = new RigTrack { Name = bone, Kind = RigTrackKind.Bone };
			foreach ( var k in track.Keys )
				boneTrack.Keys.Add( new RigKey
				{
					Frame = k.Frame,
					Ease = k.Ease,
					Local = new RigPose(
						new Vec3( k.X, k.Y, 0f ),
						Quat.FromAxisAngle( new Vec3( 0f, 0f, 1f ), k.RotationDegrees * MathF.PI / 180f ),
						new Vec3( k.ScaleX, k.ScaleY, 1f ) ),
				} );
			out_.Tracks.Add( boneTrack );
		}

		return new Result { Clip = out_, SkippedElements = skipped.ToArray() };
	}
}
