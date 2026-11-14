// Proxy LOD per object + automatic proxy switching (godlike Platform P1).
//
// The LOD chain itself already exists: LodChain.BuildLodChain decimates a mesh
// into LOD0-LOD3 with a SwitchDistance per level (LodExport.DefaultDistance,
// diagonal-scaled). What did not exist was the RUNTIME system on top of it —
// which object is showing which level right now, when it switches, and what
// happens when the scene is too heavy for the device. That is this file.
//
// RULES, in Pooh's words (2026-11-13): as little limitation as possible beyond
// what the user's device can handle, but still optimised and efficient — no
// artificial caps; scale to the device (adaptive quality / proxy LOD /
// streaming, device detection, degrade gracefully instead of hard-blocking).
// So: there is no hard triangle cap that refuses to draw. When the scene is
// over budget the farthest objects step down a level, one at a time, until it
// fits or everything is at its coarsest level. The scene always draws.
//
// Kernel-side, engine-free, no GL: selection takes an eye position and object
// bounds (Vec3), so it is testable on the CPU. The host wires OrbitCamera.Eye
// in and the BodyPickEntry bounds (Min/Max -> centre/radius) for objects.
#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>One object's proxy-LOD state: its chain, its bounds, and the level it is
/// showing right now. Built once per object (chains are decimated up front, not
/// per frame — see <see cref="ProxyLod.ForObject"/>).</summary>
public sealed class ProxyLodObject
{
	/// <summary>Display name, used by the viewport profiler when it names the slow object.</summary>
	public string Name = "";

	/// <summary>Bounding-sphere centre, world units (inches, Z-up, like the rest of the kernel).</summary>
	public Vec3 Centre;

	/// <summary>Bounding-sphere radius, world units.</summary>
	public float Radius;

	/// <summary>Triangle count of the source (LOD0) mesh.</summary>
	public int SourceTriangles;

	/// <summary>The LOD0-LOD3 chain from <see cref="LodChain.BuildLodChain"/>.</summary>
	public List<LodChain.Level> Levels = new();

	/// <summary>The level currently shown, 0 (full) to 3 (coarsest).</summary>
	public int CurrentLevel;

	/// <summary>Multiplier on the switch distances: 1 shows the chain as authored.
/// Below 1 switches to proxies sooner (a weaker device); above 1 holds full
/// detail farther. Set from <see cref="ProxyLod.BiasFor"/>.</summary>
	public float DistanceBias = 1f;

	/// <summary>Triangles actually drawn at the current level.</summary>
	public int TrianglesDrawn => Levels.Count == 0 ? 0 : Levels[CurrentLevel].Triangles;

	/// <summary>True when the object is showing a proxy (any level below LOD0).</summary>
	public bool IsProxy => CurrentLevel > 0;
}

/// <summary>Device tiers for adaptive quality. The tier only ever moves the
/// switch distances (through <see cref="BiasFor"/>) and the triangle budget —
/// it never refuses to draw anything. Detection from the GL renderer string
/// lives host-side (<see cref="ProxyLod.DetectTier"/> is the rule, the host
/// supplies the renderer string); the measurement loop in
/// <see cref="AdaptiveTier"/> is what moves a running session between tiers.</summary>
public enum DeviceTier
{
	/// <summary>Discrete GPU, 4 GB+ VRAM: the chain as authored.</summary>
	High,
	/// <summary>Integrated / Apple Silicon / under 4 GB VRAM: proxies a little sooner.</summary>
	Medium,
	/// <summary>Software renderer or under 2 GB VRAM: proxies much sooner, lowest budget.</summary>
	Low,
}

/// <summary>Per-object proxy LOD selection, automatic switching with hysteresis,
/// device bias, and graceful degradation under a triangle budget.</summary>
public static class ProxyLod
{
	/// <summary>Hysteresis band: a level is entered past its switch distance, but only
	/// left again below 85% of it. Without the band an object orbiting near a
	/// switch distance flickers between levels every frame.</summary>
	public const float Hysteresis = 0.85f;

	/// <summary>Switch-distance multiplier per tier. Low switches at half the authored
	/// distance, Medium at three quarters, High as authored.</summary>
	public static float BiasFor( DeviceTier tier ) => tier switch
	{
		DeviceTier.Low => 0.5f,
		DeviceTier.Medium => 0.75f,
		_ => 1f,
	};

	/// <summary>Default per-frame triangle budget per tier, for <see cref="UpdateAll"/>.
	/// These are budgets for degradation, not caps: exceeding one steps objects
	/// down, it never drops a draw. High has no effective budget (int.MaxValue);
	/// the numbers below are the published targets in triangle form (5M faces
	/// interactive at 30 fps is the High bar; Medium/Low degrade toward it).</summary>
	public static int BudgetFor( DeviceTier tier ) => tier switch
	{
		DeviceTier.Low => 1_000_000,
		DeviceTier.Medium => 2_500_000,
		_ => int.MaxValue,
	};

	/// <summary>First-pass device tier from a GL renderer string and an optional VRAM
	/// figure in MB (null when unknown). A heuristic, not a benchmark — the
	/// measurement loop in <see cref="AdaptiveTier"/> corrects it at runtime.
	/// Software renderers (llvmpipe/softpipe/software) are always Low; a discrete
	/// NVIDIA/AMD/Radeon part with 4 GB+ is High; everything else is Medium.</summary>
	public static DeviceTier DetectTier( string renderer, int? vramMb )
	{
		if ( string.IsNullOrWhiteSpace( renderer ) )
			return DeviceTier.Medium;
		var r = renderer.ToLowerInvariant();
		if ( r.Contains( "llvmpipe" ) || r.Contains( "softpipe" ) || r.Contains( "software" ) )
			return DeviceTier.Low;
		if ( vramMb is < 2048 )
			return DeviceTier.Low;
		if ( (r.Contains( "nvidia" ) || r.Contains( "geforce" ) || r.Contains( "radeon" ) || r.Contains( "amd" )) && vramMb is null or >= 4096 )
			return DeviceTier.High;
		return DeviceTier.Medium;
	}

	/// <summary>Build an object's proxy state from its mesh. The chain is decimated
	/// up front (once per object, off the render thread — decimation is slow);
	/// per-frame work is only <see cref="SelectLevel"/>.</summary>
	public static ProxyLodObject ForObject( string name, PolyMesh mesh, Vec3 centre, float radius, DeviceTier tier = DeviceTier.High )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "A proxy object needs a name.", nameof( name ) );
		ArgumentNullException.ThrowIfNull( mesh );
		var levels = LodChain.BuildLodChain( mesh );
		return new ProxyLodObject
		{
			Name = name,
			Centre = centre,
			Radius = radius,
			SourceTriangles = Decimate.TriangleCount( mesh ),
			Levels = levels,
			CurrentLevel = 0,
			DistanceBias = BiasFor( tier ),
		};
	}

	/// <summary>The level <paramref name="obj"/> should show at
	/// <paramref name="distance"/> (eye to object centre), hysteresis-aware:
	/// it steps down past a level's switch distance and only steps back up
	/// below 85% of it, so an object near a boundary does not flicker.</summary>
	public static int SelectLevel( ProxyLodObject obj, float distance )
	{
		ArgumentNullException.ThrowIfNull( obj );
		if ( obj.Levels is null || obj.Levels.Count == 0 ) throw new ArgumentException( "Proxy object has no LOD levels.", nameof( obj ) );
		if ( !float.IsFinite( distance ) || distance < 0f ) throw new ArgumentOutOfRangeException( nameof( distance ), "Distance must be a finite non-negative number." );

		var level = obj.CurrentLevel;
		// Step down: past the next level's (biased) switch distance.
		while ( level < obj.Levels.Count - 1 &&
			distance > obj.Levels[level + 1].SwitchDistance * obj.DistanceBias )
			level++;
		// Step up: only below the hysteresis band of the current level's distance.
		while ( level > 0 &&
			distance < obj.Levels[level].SwitchDistance * obj.DistanceBias * Hysteresis )
			level--;
		return level;
	}

	/// <summary>Per-frame update for a whole scene: select every object's level from
	/// the eye position, then, if the drawn total is over
	/// <paramref name="triangleBudget"/>, step the farthest objects down one level
	/// at a time until it fits or everything is at its coarsest level. Returns
	/// the number of objects whose level changed this frame; the drawn total is
	/// in <paramref name="drawnTriangles"/>. Never drops an object.</summary>
	public static int UpdateAll( IReadOnlyList<ProxyLodObject> objects, Vec3 eye, int triangleBudget, out int drawnTriangles )
	{
		ArgumentNullException.ThrowIfNull( objects );
		if ( triangleBudget < 0 ) throw new ArgumentOutOfRangeException( nameof( triangleBudget ), "A triangle budget cannot be negative." );

		var changed = 0;
		drawnTriangles = 0;
		var distances = new float[objects.Count];
		for ( var i = 0; i < objects.Count; i++ )
		{
			var obj = objects[i];
			distances[i] = (eye - obj.Centre).Length - obj.Radius;
			if ( distances[i] < 0f ) distances[i] = 0f;
			var level = SelectLevel( obj, distances[i] );
			if ( level != obj.CurrentLevel ) { obj.CurrentLevel = level; changed++; }
			drawnTriangles += obj.TrianglesDrawn;
		}

		// Graceful degradation, farthest first: step the farthest object that can
		// still step down, one level, and repeat. The scene always draws.
		while ( drawnTriangles > triangleBudget )
		{
			var farthest = -1;
			var farthestDistance = float.NegativeInfinity;
			for ( var i = 0; i < objects.Count; i++ )
			{
				var obj = objects[i];
				if ( obj.CurrentLevel >= obj.Levels.Count - 1 ) continue;
				if ( distances[i] > farthestDistance ) { farthestDistance = distances[i]; farthest = i; }
			}
			if ( farthest < 0 ) break;
			var target = objects[farthest];
			drawnTriangles -= target.TrianglesDrawn;
			target.CurrentLevel++;
			drawnTriangles += target.TrianglesDrawn;
			changed++;
		}
		return changed;
	}

	/// <summary>Total triangles drawn across objects at their current levels.</summary>
	public static int TrianglesDrawn( IReadOnlyList<ProxyLodObject> objects )
	{
		ArgumentNullException.ThrowIfNull( objects );
		var total = 0;
		foreach ( var obj in objects ) total += obj.TrianglesDrawn;
		return total;
	}
}

/// <summary>Adaptive quality: the measurement loop that moves a running session
/// between <see cref="DeviceTier"/>s from observed frame times, so the tier
/// <see cref="ProxyLod.DetectTier"/> guessed at startup is corrected by what
/// the machine actually does. Slow frames step down (after 3 in a row, so one
/// hitch does not panic); sustained fast frames step back up (after 60, so it
/// does not yo-yo). The tier only ever moves switch distances and budgets —
/// it never stops drawing.</summary>
public sealed class AdaptiveTier
{
	/// <summary>Frames over the target in a row before stepping a tier down.</summary>
	public const int SlowFramesToStepDown = 3;
	/// <summary>Frames under the target in a row before stepping a tier back up.</summary>
	public const int FastFramesToStepUp = 60;

	public DeviceTier Tier { get; private set; }
	public double TargetFrameMs { get; }

	int _slowStreak;
	int _fastStreak;

	public AdaptiveTier( DeviceTier start, double targetFrameMs = 16.7 )
	{
		Tier = start;
		if ( !double.IsFinite( targetFrameMs ) || targetFrameMs <= 0 ) throw new ArgumentOutOfRangeException( nameof( targetFrameMs ), "A frame target must be a finite positive number of milliseconds." );
		TargetFrameMs = targetFrameMs;
	}

	/// <summary>Feed one measured frame time (ms). Returns true when the tier changed.</summary>
	public bool ObserveFrame( double frameMs )
	{
		if ( !double.IsFinite( frameMs ) || frameMs < 0 ) throw new ArgumentOutOfRangeException( nameof( frameMs ), "A frame time must be a finite non-negative number of milliseconds." );
		if ( frameMs > TargetFrameMs )
		{
			_slowStreak++;
			_fastStreak = 0;
			if ( _slowStreak >= SlowFramesToStepDown && Tier < DeviceTier.Low )
			{
				Tier++;
				_slowStreak = 0;
				return true;
			}
			return false;
		}
		_fastStreak++;
		_slowStreak = 0;
		if ( _fastStreak >= FastFramesToStepUp && Tier > DeviceTier.High )
		{
			Tier--;
			_fastStreak = 0;
			return true;
		}
		return false;
	}
}
