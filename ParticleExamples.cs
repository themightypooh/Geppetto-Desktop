#nullable enable
using System;
using System.Collections.Generic;

namespace Marionette.Desktop;

// Particle example gallery (particles wave-6 lane P72, 2026-10-24).
//
// WHAT THIS IS: 20 worked particle-system definitions, as data plus
// comments, for copying and tweaking. Each entry is a complete lane-5
// schema-v1 JSON definition (settings + emitters + inline mesh geometry
// where used; here no entry needs a mesh). Load one with
// ParticleAuthoring.FromJson( example.Json ).Build().
//
// WHAT THIS IS NOT: rendered pictures. These are definitions, not
// screenshots, and nothing here is wired into authoring, UI, or cues —
// the gallery stands alone until the coordinator sweep hooks it up.
// Every entry uses ONLY base sim features: settings and emitters with
// shapes the sim actually supports (Point, Area, Box, Sphere). No entry
// references a preset (pack presets may not be present), and no entry
// fakes a colour, texture, or atlas field the sim does not carry —
// where an effect wants one, its Description says so as a noted limit.
//
// UNITS: kernel inches, Z-up, seconds — the same as ParticleSystem.
// Gravity points down by default (0, 0, -386.0886); a POSITIVE Z
// gravity lifts particles (bubbles, fire, steam), it does not mean
// "upside-down world". A seed is serialized in every definition, so a
// copied example replays identically.
//
// ORDER: All() returns the entries in exactly the order listed in this
// file (fountain first, firefly_drift last), and that order is stable
// across calls. Find() resolves an exact name, or the same name with
// one fx:/vfx:/particle: prefix stripped (including the fx_/vfx_/
// particle_ underscore forms, as in ParticleEmitterPresets.ResolveName,
// and case-insensitively). Unlike cue resolution it does NOT match a
// name carried as an underscore word inside a longer string —
// Find( "footstep_water_splash" ) is null, not water_splash — because
// gallery names are looked up, not cued. Unknown, empty, and blank
// names return null, never throw.

/// <summary>One gallery entry: a stable name, a plain description of
/// what it looks like and when to use it, and the complete lane-5
/// schema-v1 JSON definition.</summary>
public sealed class ParticleExample
{
	public ParticleExample( string name, string description, string json )
	{
		Name = name;
		Description = description;
		Json = json;
	}

	public string Name { get; }
	public string Description { get; }
	public string Json { get; }
}

/// <summary>The 20-entry example gallery, in file order.</summary>
public static class ParticleExamples
{
	static readonly ParticleExample[] _all =
	{
		// FOUNTAIN — rate 120 + speed 220 + spread 18 make a narrow jet;
		// the default gravity (0, 0, -386.0886) pulls the arc back down;
		// ground restitution 0.35 keeps the landing spray lively.
		new ParticleExample(
			"fountain",
			"A steady arc of droplets that rises, peaks, and falls back to bounce on the ground. Use it for garden fountains, plaza jets, and any looping water feature.",
			"""
			{
			  "version": 1,
			  "name": "fountain",
			  "settings": { "capacity": 2000, "seed": 101, "gravity": [0, 0, -386.0886], "drag": 0.2, "groundEnabled": true, "restitution": 0.35, "friction": 0.3, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "rate": 120, "speed": 220, "speedJitter": 0.25, "spreadDegrees": 18, "lifetimeMin": 1.2, "lifetimeMax": 2.0, "sizeMin": 1.2, "sizeMax": 2.4 }
			  ]
			}
			""" ),
		// EXPLOSION BURST — burst 180 + spread 180 fires in every direction
		// at once; drag 0.8 stops the core fast; the second, slower burst
		// of 60 is the lingering debris, not a second explosion.
		new ParticleExample(
			"explosion_burst",
			"One fast core burst plus a slower, wider debris burst that stops quickly in the air. Use it for impacts and blasts; the sim carries no per-particle colour, so the fireball and smoke cannot be tinted separately.",
			"""
			{
			  "version": 1,
			  "name": "explosion_burst",
			  "settings": { "capacity": 1000, "seed": 102, "gravity": [0, 0, -386.0886], "drag": 0.8, "turbulenceStrength": 40, "turbulenceFrequency": 0.2, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "bursts": [{ "step": 0, "count": 180 }], "speed": 380, "speedJitter": 0.5, "spreadDegrees": 180, "lifetimeMin": 0.4, "lifetimeMax": 1.0, "sizeMin": 1.0, "sizeMax": 3.0 },
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "bursts": [{ "step": 0, "count": 60 }], "speed": 150, "speedJitter": 0.4, "spreadDegrees": 90, "lifetimeMin": 0.8, "lifetimeMax": 1.6, "sizeMin": 2.0, "sizeMax": 4.0 }
			  ]
			}
			""" ),
		// RAIN — a Box 120 half-extents wide is the sky the drops fall
		// from; speed 320 + spread 4 is near-vertical; ground restitution
		// 0.05 is a splat, not a bounce.
		new ParticleExample(
			"rain",
			"Fast, thin drops falling straight down across a wide sky box, with a slight sideways wind. Use it for weather over a scene; streaks along the fall direction are not drawn, so drops read as small dots.",
			"""
			{
			  "version": 1,
			  "name": "rain",
			  "settings": { "capacity": 4000, "seed": 103, "gravity": [0, 0, -386.0886], "wind": [20, 0, 0], "drag": 0.05, "groundEnabled": true, "restitution": 0.05, "friction": 0.6, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Box", "position": [0, 0, 200], "boxHalfExtents": [120, 120, 10], "direction": [0, 0, -1], "rate": 400, "speed": 320, "speedJitter": 0.15, "spreadDegrees": 4, "lifetimeMin": 0.8, "lifetimeMax": 1.2, "sizeMin": 0.3, "sizeMax": 0.7 }
			  ]
			}
			""" ),
		// SNOW — gravity -30 (not the default -386) + drag 0.8 is a gentle
		// fall; turbulence 8 is the drift; wind (6, 2, 0) slides flakes
		// sideways as they come down.
		new ParticleExample(
			"snow",
			"Slow, wide-spread flakes that drift sideways as they fall. Use it for winter weather and calm outdoor scenes.",
			"""
			{
			  "version": 1,
			  "name": "snow",
			  "settings": { "capacity": 4000, "seed": 104, "gravity": [0, 0, -30], "wind": [6, 2, 0], "drag": 0.8, "turbulenceStrength": 8, "turbulenceFrequency": 0.1, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Box", "position": [0, 0, 200], "boxHalfExtents": [120, 120, 10], "direction": [0, 0, -1], "rate": 150, "speed": 18, "speedJitter": 0.6, "spreadDegrees": 20, "lifetimeMin": 6, "lifetimeMax": 10, "sizeMin": 0.5, "sizeMax": 1.2, "angularSpeedMax": 4 }
			  ]
			}
			""" ),
		// BUBBLES RISING — gravity +60 RISES: positive Z gravity lifts,
		// which is how buoyancy is expressed in this Z-up sim; drag 0.8
		// is the water resistance; sphere radius 10 is the spawn volume.
		new ParticleExample(
			"bubbles_rising",
			"Small spheres of air that wobble upward through water instead of falling. Use it underwater, in tanks, and for potion or boiling effects.",
			"""
			{
			  "version": 1,
			  "name": "bubbles_rising",
			  "settings": { "capacity": 1000, "seed": 105, "gravity": [0, 0, 60], "drag": 0.8, "turbulenceStrength": 8, "turbulenceFrequency": 0.3, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Sphere", "position": [0, 0, 0], "sphereRadius": 10, "direction": [0, 0, 1], "rate": 40, "speed": 30, "speedJitter": 0.5, "spreadDegrees": 20, "lifetimeMin": 1.5, "lifetimeMax": 3.5, "sizeMin": 0.6, "sizeMax": 1.6 }
			  ]
			}
			""" ),
		// FIRE — gravity +80 means hot air accelerates upward; turbulence
		// 20 is the churn; life 0.5–1.2 keeps the tongues short.
		new ParticleExample(
			"fire",
			"Tongues of flame that accelerate upward and churn as they rise. Use it for campfires, torches, and braziers; the sim carries no colour over life, so the yellow-to-red fade is a noted limit, not part of this def.",
			"""
			{
			  "version": 1,
			  "name": "fire",
			  "settings": { "capacity": 1000, "seed": 106, "gravity": [0, 0, 80], "drag": 1.2, "turbulenceStrength": 20, "turbulenceFrequency": 0.3, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "rate": 90, "speed": 120, "speedJitter": 0.5, "spreadDegrees": 30, "lifetimeMin": 0.5, "lifetimeMax": 1.2, "sizeMin": 1.5, "sizeMax": 3.5, "angularSpeedMax": 8 }
			  ]
			}
			""" ),
		// SMOKE — gravity +30 + drag 2 is a slow buoyant rise; life 2–4
		// is the long hang; turbulence 25 is the billow that breaks
		// the puff apart.
		new ParticleExample(
			"smoke",
			"Slow, fat puffs that rise and churn apart over several seconds. Use it for chimneys, smouldering ruins, and engine exhaust; size is fixed at spawn, so puffs do not grow as they age.",
			"""
			{
			  "version": 1,
			  "name": "smoke",
			  "settings": { "capacity": 1000, "seed": 107, "gravity": [0, 0, 30], "drag": 2, "turbulenceStrength": 25, "turbulenceFrequency": 0.15, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "rate": 40, "speed": 40, "speedJitter": 0.4, "spreadDegrees": 25, "lifetimeMin": 2, "lifetimeMax": 4, "sizeMin": 2, "sizeMax": 5, "angularSpeedMax": 3 }
			  ]
			}
			""" ),
		// MAGIC SPARKLES — sphere radius 8 + spread 180 glitters in every
		// direction; speed 20 + drag 1 settles quickly into drift;
		// angular speed 15 is the tumble that sells "sparkle".
		new ParticleExample(
			"magic_sparkles",
			"Tiny bright motes that glitter outward in every direction from a small sphere. Use it for spell casts, treasure glints, and fairy effects; twinkle and colour changes are not carried, so sparkles are steady single-colour dots.",
			"""
			{
			  "version": 1,
			  "name": "magic_sparkles",
			  "settings": { "capacity": 1000, "seed": 108, "gravity": [0, 0, -20], "drag": 1, "turbulenceStrength": 12, "turbulenceFrequency": 0.5, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Sphere", "position": [0, 0, 20], "sphereRadius": 8, "direction": [0, 0, 1], "rate": 60, "speed": 20, "speedJitter": 0.8, "spreadDegrees": 180, "lifetimeMin": 0.8, "lifetimeMax": 2, "sizeMin": 0.3, "sizeMax": 1, "angularSpeedMax": 15 }
			  ]
			}
			""" ),
		// WATERFALL SHEET — Area with areaU (60, 0, 0) and areaV (0, 20, 0)
		// is the wide sheet; useSurfaceNormal false + direction down
		// makes it FALL instead of launching along the sheet normal;
		// rate 300 is the dense curtain.
		new ParticleExample(
			"waterfall_sheet",
			"A wide, flat sheet of water falling from a high ledge into a splashing pool. Use it for falls, spillways, and dam overflows.",
			"""
			{
			  "version": 1,
			  "name": "waterfall_sheet",
			  "settings": { "capacity": 2000, "seed": 109, "gravity": [0, 0, -386.0886], "drag": 0.1, "turbulenceStrength": 10, "turbulenceFrequency": 0.2, "groundEnabled": true, "restitution": 0.2, "friction": 0.4, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Area", "position": [0, 0, 120], "areaU": [60, 0, 0], "areaV": [0, 20, 0], "useSurfaceNormal": false, "direction": [0, 0, -1], "rate": 300, "speed": 60, "speedJitter": 0.2, "spreadDegrees": 8, "lifetimeMin": 1.5, "lifetimeMax": 2.5, "sizeMin": 1, "sizeMax": 2.5 }
			  ]
			}
			""" ),
		// TORNADO VORTEX — vortexStrength 30000 around center (0, 0, 60)
		// with radius 12 IS the funnel: the vortex is a force, not a
		// shape; gravity -20 keeps particles from falling out of the
		// column; sphere radius 40 is the wide debris intake.
		new ParticleExample(
			"tornado_vortex",
			"A wide disc of debris that is spun around a tall vertical column as it rises. Use it for storms, dust devils, and whirlwinds; the funnel shape itself comes from the vortex force, not from a cone emitter.",
			"""
			{
			  "version": 1,
			  "name": "tornado_vortex",
			  "settings": { "capacity": 3000, "seed": 110, "gravity": [0, 0, -20], "drag": 0.3, "turbulenceStrength": 15, "turbulenceFrequency": 0.2, "vortexStrength": 30000, "vortexCenter": [0, 0, 60], "vortexAxis": [0, 0, 1], "vortexRadius": 12, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Sphere", "position": [0, 0, 20], "sphereRadius": 40, "direction": [0, 0, 1], "rate": 200, "speed": 40, "speedJitter": 0.5, "spreadDegrees": 40, "lifetimeMin": 3, "lifetimeMax": 5, "sizeMin": 1, "sizeMax": 2, "angularSpeedMax": 4 }
			  ]
			}
			""" ),
		// GROUND EMBERS — a Box at z 6 + ground bounce is embers
		// skittering LOW, not a fountain; wind (12, 4, 0) pushes them
		// sideways; gravity -60 is a light fall, not a plume arc.
		new ParticleExample(
			"ground_embers",
			"Small hot motes that skitter low over the ground, bouncing and blowing sideways. Use it for campfire surroundings, scorched battlefields, and forge floors.",
			"""
			{
			  "version": 1,
			  "name": "ground_embers",
			  "settings": { "capacity": 1000, "seed": 111, "gravity": [0, 0, -60], "wind": [12, 4, 0], "drag": 1, "turbulenceStrength": 20, "turbulenceFrequency": 0.3, "groundEnabled": true, "restitution": 0.4, "friction": 0.3, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Box", "position": [0, 0, 6], "boxHalfExtents": [60, 60, 4], "direction": [0, 0, 1], "rate": 60, "speed": 8, "speedJitter": 0.8, "spreadDegrees": 60, "lifetimeMin": 2, "lifetimeMax": 4, "sizeMin": 0.4, "sizeMax": 1, "angularSpeedMax": 10 }
			  ]
			}
			""" ),
		// FLOATING DUST — gravity -8 + drag 0.5 is a near-hang; rate 12
		// + size 0.2–0.6 is sparse and faint; turbulence 6 is a slow
		// wander, not weather.
		new ParticleExample(
			"floating_dust",
			"Barely-there motes hanging in still air, drifting on a faint breeze. Use it for sunbeams indoors, attics, and quiet rooms.",
			"""
			{
			  "version": 1,
			  "name": "floating_dust",
			  "settings": { "capacity": 500, "seed": 112, "gravity": [0, 0, -8], "wind": [4, 2, 0], "drag": 0.5, "turbulenceStrength": 6, "turbulenceFrequency": 0.15, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Box", "position": [0, 0, 40], "boxHalfExtents": [30, 30, 10], "direction": [0, 0, 1], "rate": 12, "speed": 5, "speedJitter": 0.8, "spreadDegrees": 180, "lifetimeMin": 3, "lifetimeMax": 6, "sizeMin": 0.2, "sizeMax": 0.6 }
			  ]
			}
			""" ),
		// WATER SPLASH — burst 120 + speed 260 + spread 35 is the crown;
		// life 0.4–0.9 is a quick fall; ground restitution 0.35 gives
		// the bounce droplets after the first hit.
		new ParticleExample(
			"water_splash",
			"A single crown of droplets thrown upward that falls back and bounces. Use it for footsteps in puddles, dropped objects, and small impacts.",
			"""
			{
			  "version": 1,
			  "name": "water_splash",
			  "settings": { "capacity": 1000, "seed": 113, "gravity": [0, 0, -386.0886], "drag": 0.1, "groundEnabled": true, "restitution": 0.35, "friction": 0.2, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "bursts": [{ "step": 0, "count": 120 }], "speed": 260, "speedJitter": 0.4, "spreadDegrees": 35, "lifetimeMin": 0.4, "lifetimeMax": 0.9, "sizeMin": 1.5, "sizeMax": 3.5, "angularSpeedMax": 6 }
			  ]
			}
			""" ),
		// DRIFTING LEAVES — gravity -60 + drag 1.5 is a flutter, not a
		// plummet; turbulence 35 is the strong tumble; angular speed 12
		// is the spinning leaf; ground friction 0.8 makes leaves settle
		// instead of sliding away.
		new ParticleExample(
			"drifting_leaves",
			"Broad, tumbling leaves that flutter down sideways on the wind and settle on the ground. Use it for autumn trees and windy yards; leaves are billboards, so flat leaf shapes are not drawn.",
			"""
			{
			  "version": 1,
			  "name": "drifting_leaves",
			  "settings": { "capacity": 1000, "seed": 114, "gravity": [0, 0, -60], "wind": [8, 0, 0], "drag": 1.5, "turbulenceStrength": 35, "turbulenceFrequency": 0.2, "groundEnabled": true, "restitution": 0.1, "friction": 0.8, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Box", "position": [0, 0, 60], "boxHalfExtents": [40, 40, 5], "direction": [0, 0, -1], "rate": 30, "speed": 25, "speedJitter": 0.6, "spreadDegrees": 60, "lifetimeMin": 3, "lifetimeMax": 5, "sizeMin": 1, "sizeMax": 2.5, "angularSpeedMax": 12 }
			  ]
			}
			""" ),
		// CONFETTI BURST — burst 150 + spread 70 is the wide fan; angular
		// speed 25 is the tumbling pieces; drag 0.6 + turbulence 10 is
		// the flutter on the way down.
		new ParticleExample(
			"confetti_burst",
			"A one-shot burst of tumbling pieces that arcs up, flutters, and settles. Use it for celebrations and victories; every piece shares one layer colour, so multi-colour confetti needs per-particle colour first.",
			"""
			{
			  "version": 1,
			  "name": "confetti_burst",
			  "settings": { "capacity": 1000, "seed": 115, "gravity": [0, 0, -386.0886], "drag": 0.6, "turbulenceStrength": 10, "turbulenceFrequency": 0.4, "groundEnabled": true, "restitution": 0.3, "friction": 0.5, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "bursts": [{ "step": 0, "count": 150 }], "speed": 200, "speedJitter": 0.5, "spreadDegrees": 70, "lifetimeMin": 1, "lifetimeMax": 2.5, "sizeMin": 0.8, "sizeMax": 1.8, "angularSpeedMax": 25 }
			  ]
			}
			""" ),
		// STEAM PUFF — bursts with NO rate is one puff, not a looping
		// vent; gravity +40 + drag 1.5 is a gentle buoyant rise; life
		// 1.5–3 is the slow dissolve.
		new ParticleExample(
			"steam_puff",
			"One soft puff that rises, spreads, and churns apart. Use it for kettles, vents, and hot drinks; the puff is one burst, not a looping vent.",
			"""
			{
			  "version": 1,
			  "name": "steam_puff",
			  "settings": { "capacity": 500, "seed": 116, "gravity": [0, 0, 40], "drag": 1.5, "turbulenceStrength": 15, "turbulenceFrequency": 0.25, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "bursts": [{ "step": 0, "count": 50 }], "speed": 40, "speedJitter": 0.4, "spreadDegrees": 25, "lifetimeMin": 1.5, "lifetimeMax": 3, "sizeMin": 1.5, "sizeMax": 4 }
			  ]
			}
			""" ),
		// LAVA RISE — gravity +100 is very hot, so the rise is fast;
		// speed 90 + spread 20 is a narrow jet; turbulence 25 is the
		// violent churn of a boiling pool.
		new ParticleExample(
			"lava_rise",
			"Fast, hot blobs that shoot upward from a pool and churn hard. Use it for lava pools, furnaces, and vents; glow must come from the layer material, since particles carry no colour or light of their own.",
			"""
			{
			  "version": 1,
			  "name": "lava_rise",
			  "settings": { "capacity": 1000, "seed": 117, "gravity": [0, 0, 100], "drag": 0.8, "turbulenceStrength": 25, "turbulenceFrequency": 0.3, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 0], "direction": [0, 0, 1], "rate": 70, "speed": 90, "speedJitter": 0.4, "spreadDegrees": 20, "lifetimeMin": 1, "lifetimeMax": 2, "sizeMin": 1.5, "sizeMax": 3, "angularSpeedMax": 5 }
			  ]
			}
			""" ),
		// INK CLOUD — drag 3 is the thick water: it kills speed fast, so
		// the cloud billows instead of jetting; gravity -5 is near
		// neutral, so it barely sinks; turbulence 12 at frequency 0.15
		// is the slow billow.
		new ParticleExample(
			"ink_cloud",
			"A slow underwater cloud that billows out and hangs, barely sinking. Use it for squid ink, dye in water, and murky underwater scenes; the cloud does not fade by diffusion, since opacity over life is not carried.",
			"""
			{
			  "version": 1,
			  "name": "ink_cloud",
			  "settings": { "capacity": 1000, "seed": 118, "gravity": [0, 0, -5], "wind": [2, 0, 0], "drag": 3, "turbulenceStrength": 12, "turbulenceFrequency": 0.15, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Sphere", "position": [0, 0, 40], "sphereRadius": 6, "direction": [0, 0, 1], "rate": 30, "speed": 6, "speedJitter": 0.8, "spreadDegrees": 180, "lifetimeMin": 4, "lifetimeMax": 7, "sizeMin": 1, "sizeMax": 3 }
			  ]
			}
			""" ),
		// FROST BREATH — spread 12 + direction (1, 0, 0) is a narrow
		// FORWARD cone, not an upward plume; speed 70 is breath
		// velocity; drag 1.5 makes the mist stop quickly in cold air.
		new ParticleExample(
			"frost_breath",
			"A narrow forward cone of cold mist, as if breathed out into winter air. Use it for characters breathing in the cold and for chilly spirits.",
			"""
			{
			  "version": 1,
			  "name": "frost_breath",
			  "settings": { "capacity": 500, "seed": 119, "gravity": [0, 0, -20], "drag": 1.5, "turbulenceStrength": 10, "turbulenceFrequency": 0.4, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Point", "position": [0, 0, 10], "direction": [1, 0, 0], "bursts": [{ "step": 0, "count": 45 }], "speed": 70, "speedJitter": 0.3, "spreadDegrees": 12, "lifetimeMin": 0.5, "lifetimeMax": 1.1, "sizeMin": 1, "sizeMax": 2.5 }
			  ]
			}
			""" ),
		// FIREFLY DRIFT — speed 6 + turbulence 10 is wandering drift, not
		// flight; gravity -5 is a near-hover; sphere radius 30 is the
		// clearing the fireflies wander through.
		new ParticleExample(
			"firefly_drift",
			"Tiny dots wandering slowly through the night air around a clearing. Use it for summer nights and enchanted woods; the dots do not blink, since brightness over life is not carried.",
			"""
			{
			  "version": 1,
			  "name": "firefly_drift",
			  "settings": { "capacity": 500, "seed": 120, "gravity": [0, 0, -5], "wind": [2, 1, 0], "drag": 0.8, "turbulenceStrength": 10, "turbulenceFrequency": 0.4, "renderType": "Billboard" },
			  "emitters": [
			    { "shape": "Sphere", "position": [0, 0, 30], "sphereRadius": 30, "direction": [0, 0, 1], "rate": 20, "speed": 6, "speedJitter": 0.8, "spreadDegrees": 180, "lifetimeMin": 4, "lifetimeMax": 8, "sizeMin": 0.3, "sizeMax": 0.7, "angularSpeedMax": 5 }
			  ]
			}
			""" ),
	};

	/// <summary>All 20 examples, in file order. A fresh list every call,
	/// so callers cannot disturb the gallery's order; no randomness is
	/// used anywhere in this file.</summary>
	public static IReadOnlyList<ParticleExample> All() => new List<ParticleExample>( _all );

	/// <summary>Resolve a gallery name: exact match, or the same name
	/// with one fx:/vfx:/particle: prefix stripped (underscore forms
	/// fx_/vfx_/particle_ included, case-insensitive, surrounding
	/// whitespace ignored). Unknown, empty, and blank names return
	/// null. Underscore-word containment (the cue rule) is deliberately
	/// NOT applied here.</summary>
	public static ParticleExample? Find( string? name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		string n = name.Trim().ToLowerInvariant();
		foreach ( string p in new[] { "fx:", "fx_", "vfx:", "vfx_", "particle:", "particle_" } )
		{
			if ( n.StartsWith( p, StringComparison.Ordinal ) ) { n = n[p.Length..]; break; }
		}
		foreach ( var example in _all )
			if ( example.Name == n ) return example;
		return null;
	}
}
