using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// The kernel side of Marionette's first-person viewmodel suite (roadmap Lane L,
/// §4 Talon Plasma Pistol + PixelArmsStyle viewmodel).
///
/// WHAT THIS IS AND IS NOT. <c>Code/PixelArmsStyle.cs</c> is the engine side:
/// it is s&box-only (Sandbox types), sets material attributes on a
/// SkinnedModelRenderer, and cannot compile into the Effigy kernel. What the
/// kernel CAN own — and what lives here — is everything that does not need the
/// engine: the preset table (look + pixel-shader knobs as plain data), the
/// procedural spring math (sway / bob / ADS / recoil), and the viewmodel
/// geometry transform (in <see cref="ViewmodelFeature"/>). The engine side
/// keeps doing what only it can: applying the material, framing the
/// viewmodel camera (FOV, near plane, DepthScale wall-clip guard), and driving
/// the two-handed IK. Those are Lane L follow-ups on the engine files, not
/// things this file pretends to do by renaming a camera property.
///
/// PRESETS. The roadmap names WarmSkin, OliveGlove, DoomMarine, QuakeRanger,
/// DuskCoat, DivingSleeve. Their colours here are starting points in the same
/// sense the SlotLook presets are: the fields are what a script overrides.
/// VertexSnap / TexelGrid mirror the PixelArmsStyle shader knobs by name and
/// value range so a ServeMode or s&box caller can copy them across verbatim.
/// </summary>
public sealed class ViewmodelPreset
{
	public string Name;
	public Vec3 Color = Vec3.One;
	public float Rough = 0.85f;
	public float Metal;
	public Vec3 Emit;
	public float VertexSnap = 6f;
	public float TexelGrid = 180f;
	public float PixelBlock;
	public float FlatShade;
	public float ColorSteps;
	public float HideSide;
	public bool SelfLit = true;
}

public static class ViewmodelRig
{
	public static readonly IReadOnlyDictionary<string, ViewmodelPreset> Presets =
		new Dictionary<string, ViewmodelPreset>( StringComparer.OrdinalIgnoreCase )
		{
			["OliveGlove"] = new() { Name = "OliveGlove", Color = Parse( "#5a5a3a" ), Rough = 0.92f },
			["WarmSkin"] = new() { Name = "WarmSkin", Color = Parse( "#c49a78" ), Rough = 0.7f },
			["DoomMarine"] = new() { Name = "DoomMarine", Color = Parse( "#4a5a48" ), Rough = 0.5f, Metal = 0.3f },
			["QuakeRanger"] = new() { Name = "QuakeRanger", Color = Parse( "#6a5a4a" ), Rough = 0.8f },
			["DuskCoat"] = new() { Name = "DuskCoat", Color = Parse( "#2a2a3a" ), Rough = 0.9f },
			["DivingSleeve"] = new() { Name = "DivingSleeve", Color = Parse( "#3a4a5a" ), Rough = 0.6f },
		};

	public static bool TryPreset( string name, out ViewmodelPreset preset )
	{
		if ( !string.IsNullOrWhiteSpace( name ) && Presets.TryGetValue( name, out var p ) )
		{
			preset = p;
			return true;
		}
		preset = null;
		return false;
	}

	static Vec3 Parse( string hex ) => FurMaterial.ParseHex( hex, Vec3.One );
}

/// <summary>Procedural viewmodel spring settings. All amplitudes are in
/// viewmodel units; look deltas are caller-scaled mouse deltas, not pixels.</summary>
public sealed class ViewmodelSpringOptions
{
	public float SwayAmp = 0.5f;
	public float SwayRoll = 0.15f;
	public float AdsSwayScale = 0.3f;
	public float BobAmp = 0.3f;
	public float BobFreq = 8f;
	public float Recoil = 1f;
	public float RecoilDecay = 6f;
	public float RecoilPitch = 8f;
}

/// <summary>One evaluated viewmodel pose: an offset in viewmodel space
/// (x horizontal, y depth — positive is forward — z up) plus pitch/roll.</summary>
public readonly record struct ViewmodelPose( Vec3 Offset, float Pitch, float Roll );

/// <summary>
/// The procedural FPS layer from roadmap Lane L, as pure math: mouse-look lag
/// (Sway), sprint/walk Lissajous figure-8 (Bob), ADS blend, and impulse
/// RecoilKick, layered additively over whatever clip pose the engine holds.
/// Deterministic in time — the same inputs give the same pose, which is what
/// makes it testable without an engine and safe to layer in a cabinet preview.
/// </summary>
public static class ViewmodelSpring
{
	public static ViewmodelPose Evaluate( float time, float lookX, float lookY,
		float speed, float ads, float fireTime, ViewmodelSpringOptions options = null )
	{
		var o = options ?? new ViewmodelSpringOptions();
		var swayScale = 1f - ads + ads * o.AdsSwayScale;
		var swayX = -lookX * o.SwayAmp * swayScale;
		var swayZ = lookY * o.SwayAmp * 0.6f * swayScale;

		speed = Math.Clamp( speed, 0f, 1f );
		ads = Math.Clamp( ads, 0f, 1f );
		var bobX = MathF.Sin( time * o.BobFreq ) * o.BobAmp * speed;
		var bobZ = MathF.Abs( MathF.Cos( time * o.BobFreq ) ) * o.BobAmp * 0.6f * speed;

		var kick = 0f;
		var dt = time - fireTime;
		if ( dt >= 0f )
			kick = o.Recoil * MathF.Exp( -dt * o.RecoilDecay );

		var offset = new Vec3( swayX + bobX, -kick, swayZ + bobZ );
		return new ViewmodelPose( offset, kick * o.RecoilPitch, -lookX * o.SwayRoll );
	}
}
