#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// 3D flipbook evaluator for Geppetto's 3D particles (wave-6 lane P28, particles session).
//
// A flipbook is a sprite-sheet animation: a grid atlas (Columns x Rows) plus
// playback metadata kept alongside it (Unity Texture Sheet Animation model;
// Heaps h2d.Particles frame count + frame division + animation repeat over
// lifetime). This file owns ONLY the evaluation: given a particle's age,
// lifetime, speed, and seed it returns the frame index plus the atlas UV
// rect, as one pure function with no hidden state. There is no rendering,
// no texture/atlas loading, and no wiring here — the atlas image itself is
// caller-supplied layout, not a file this module opens, and the billboard
// layer does not consume it yet (see the integration block at the bottom).
// Units are kernel inches, seconds, Z-up, like ParticleSystem: speed is
// in inches/second, i.e. ParticleState.Velocity.Length().
//
// Relationship to Particle2DFlipbook (lane P10, the 2D flipbook): the same
// playback math, deliberately — Loop / Once / PingPong, the same additive
// speed rule, the same seeded start offset from ParticleRng, the same
// row-major atlas. What differs is the dimension: P10's speed is px/s on a
// Y-down canvas; this file's speed is inches/s in 3D space, and its UV
// convention is tied to the 3D billboard layer (below), not to a canvas.
// It does NOT replace ParticleSystem's per-particle Size or the billboard
// layer's fade-out shrink; it only answers "which atlas cell".
//
// Determinism: the evaluator is a pure function of its inputs plus its own
// configured fields. Random start offsets come from ParticleRng (the same
// SplitMix64/xorshift RNG ParticleSystem uses), never System.Random, so
// the same (settings, seed) pair always yields the same frame.
//
// Frame-rate rule, stated once: effectiveFps = FramesPerSecond +
// SpeedScale * speed, where speed is in inches/s and SpeedScale is in
// added frames-per-second per inch/s. At speed 0 the rate is exactly
// FramesPerSecond; SpeedScale 0 disables speed scaling. FramesPerSecond
// 0 means static: the frame is the base frame plus the random start
// offset (when RandomStart), and never advances with age.
//
// UV ORIGIN CONVENTION, stated once (read before wiring this into a layer):
// row-major, row 0 = the TOP row of the atlas as displayed. U0/U1 are the
// cell's left/right edges. V0 is the cell's TOP edge, V1 its BOTTOM edge —
// V increases downward on screen. This matches Particle2DFlipbook, and it
// matches the corner assignment LiveBuilder.Quad already uses for a
// full-texture quad: its bottom corners (a, b) get V = 1 and its top
// corners (d, c) get V = 0. So a future billboard quad carrying this rect
// must map a -> (U0, V1), b -> (U1, V1), c -> (U1, V0), d -> (U0, V0) —
// NOT a -> (U0, V0). The current ParticleLayer.Emit calls b.Quad(...),
// which hardcodes the full (0,0)-(1,1) rect, so no UV channel exists to
// carry a cell yet; that is a LiveGeometry.cs change, owned by the sweep,
// not by this file. Caveat, stated honestly: the layer's current Sprite
// disc texture is radially symmetric, so nothing in the live renderer
// today can disambiguate a V flip — the sweep integration must keep the
// corner order above and verify it against an asymmetric test atlas,
// not "fix" it to a GL bottom-origin reading.

/// <summary>How a 3D flipbook plays through its frames.</summary>
public enum ParticleFlipbookMode
{
	/// <summary>Wrap to the first frame after the last, forever.</summary>
	Loop,
	/// <summary>Advance to the last frame and hold it.</summary>
	Once,
	/// <summary>Advance to the last frame, then run backwards, then forwards, forever.</summary>
	PingPong,
}

/// <summary>UV rect of one atlas cell. Row 0 is the TOP row as displayed;
/// V0 is the cell's top edge, V1 its bottom edge (see the file header).</summary>
public struct ParticleFlipbookUv
{
	public float U0, V0, U1, V1;
	public ParticleFlipbookUv( float u0, float v0, float u1, float v1 ) { U0 = u0; V0 = v0; U1 = u1; V1 = v1; }
	public override string ToString() => $"({U0:F3},{V0:F3})-({U1:F3},{V1:F3})";
}

/// <summary>One pure flipbook evaluation: the frame index and its UV rect, together.</summary>
public readonly record struct ParticleFlipbookSample( int Frame, ParticleFlipbookUv Uv );

/// <summary>Self-contained 3D flipbook evaluator. Configure, call <see cref="Validate"/>, then evaluate.
/// Consumed directly by whoever holds a ParticleFlipbook instance until the coordinator sweep
/// wires it into the billboard layer (integration block in the lane report).</summary>
public sealed class ParticleFlipbook
{
	/// <summary>Atlas grid columns (>= 1).</summary>
	public int Columns = 4;
	/// <summary>Atlas grid rows (>= 1).</summary>
	public int Rows = 4;
	/// <summary>Usable frames, 1..Columns*Rows. The atlas may hold spares; only the first FrameCount cells play.</summary>
	public int FrameCount = 16;
	/// <summary>Absolute index of the first frame (0 by default; lets a flipbook sit inside a larger sheet).</summary>
	public int FirstFrame;
	/// <summary>Mirror the UV rect horizontally.</summary>
	public bool FlipU;
	/// <summary>Mirror the UV rect vertically.</summary>
	public bool FlipV;

	/// <summary>Playback mode (default Loop).</summary>
	public ParticleFlipbookMode Mode = ParticleFlipbookMode.Loop;
	/// <summary>Base frames per second. 0 = static (base + start offset only, never advances).</summary>
	public float FramesPerSecond;
	/// <summary>Added fps per inch/s of particle speed. 0 = no speed scaling.</summary>
	public float SpeedScale;
	/// <summary>When true, each particle starts at a seeded offset into the cycle (see <see cref="StartOffsetFor"/>).</summary>
	public bool RandomStart;

	public void Validate()
	{
		if ( Columns < 1 ) throw new ArgumentException( "Flipbook: Columns must be >= 1." );
		if ( Rows < 1 ) throw new ArgumentException( "Flipbook: Rows must be >= 1." );
		if ( FrameCount < 1 || FrameCount > Columns * Rows ) throw new ArgumentException( "Flipbook: FrameCount must be in 1..Columns*Rows." );
		if ( FirstFrame < 0 ) throw new ArgumentException( "Flipbook: FirstFrame must be >= 0." );
		if ( !float.IsFinite( FramesPerSecond ) || FramesPerSecond < 0 ) throw new ArgumentException( "Flipbook: FramesPerSecond must be finite and >= 0." );
		if ( !float.IsFinite( SpeedScale ) || SpeedScale < 0 ) throw new ArgumentException( "Flipbook: SpeedScale must be finite and >= 0." );
		if ( !Enum.IsDefined( Mode ) ) throw new ArgumentException( $"Flipbook: unknown mode {Mode}." );
	}

	/// <summary>UV rect of an absolute frame index. Row-major, row 0 = top row.</summary>
	public ParticleFlipbookUv UvFor( int frame )
	{
		Validate();
		if ( frame < FirstFrame || frame >= FirstFrame + FrameCount )
			throw new ArgumentOutOfRangeException( nameof( frame ), $"Flipbook: frame {frame} is outside [{FirstFrame}, {FirstFrame + FrameCount})." );
		int local = frame - FirstFrame;
		int col = local % Columns;
		int row = local / Columns;
		float u0 = col / (float)Columns, u1 = (col + 1) / (float)Columns;
		float v0 = row / (float)Rows, v1 = (row + 1) / (float)Rows;
		if ( FlipU ) (u0, u1) = (u1, u0);
		if ( FlipV ) (v0, v1) = (v1, v0);
		return new ParticleFlipbookUv( u0, v0, u1, v1 );
	}

	/// <summary>Deterministic start offset in [0, FrameCount) for a particle seed. Same seed always gives the same offset.</summary>
	public int StartOffsetFor( uint seed )
	{
		Validate();
		return (int)(ParticleRng.For( seed, 0 ).NextFloat() * FrameCount);
	}

	/// <summary>Pure frame evaluation: age in seconds, speed in inches/s, base frame (absolute), particle seed.</summary>
	public int FrameAt( float age, float speed, int baseFrame, uint seed )
	{
		Validate();
		if ( !float.IsFinite( age ) || age < 0 ) throw new ArgumentException( "Flipbook: age must be finite and >= 0." );
		if ( !float.IsFinite( speed ) || speed < 0 ) throw new ArgumentException( "Flipbook: speed must be finite and >= 0." );
		float effectiveFps = FramesPerSecond + SpeedScale * speed;
		int advanced = (int)MathF.Floor( age * effectiveFps );
		if ( RandomStart ) advanced += StartOffsetFor( seed );
		int n = FrameCount;
		int b = Math.Clamp( baseFrame - FirstFrame, 0, n - 1 );
		int local;
		switch ( Mode )
		{
			case ParticleFlipbookMode.Loop:
				local = (b + advanced) % n;
				break;
			case ParticleFlipbookMode.Once:
				local = Math.Min( b + advanced, n - 1 );
				break;
			case ParticleFlipbookMode.PingPong:
				if ( n == 1 ) { local = 0; break; }
				int period = 2 * (n - 1);
				int p = (b + advanced) % period;
				local = p < n ? p : period - p;
				break;
			default: throw new ArgumentException( $"Flipbook: unknown mode {Mode}." );
		}
		return FirstFrame + local;
	}

	/// <summary>Same as <see cref="FrameAt(float, float, int, uint)"/> but age is clamped to [0, lifetime]: a particle never reports past its own life. Lifetime-normalized repeat is a caller choice — pick FramesPerSecond so FramesPerSecond * lifetime is a whole number of cycles.</summary>
	public int FrameAt( float age, float lifetime, float speed, int baseFrame, uint seed )
	{
		if ( !float.IsFinite( lifetime ) || lifetime <= 0 ) throw new ArgumentException( "Flipbook: lifetime must be finite and > 0." );
		return FrameAt( Math.Clamp( age, 0, lifetime ), speed, baseFrame, seed );
	}

	/// <summary>Pure evaluation returning frame index + UV rect together: age in seconds, lifetime in seconds, speed in inches/s, base frame (absolute), particle seed.</summary>
	public ParticleFlipbookSample Sample( float age, float lifetime, float speed, int baseFrame, uint seed )
	{
		int frame = FrameAt( age, lifetime, speed, baseFrame, seed );
		return new ParticleFlipbookSample( frame, UvFor( frame ) );
	}

	/// <summary>Pure evaluation straight from a <see cref="ParticleState"/>: age is the state's Age, speed is its velocity magnitude, base frame is FirstFrame. Lifetime is the particle's own lifetime (see <see cref="ParticleSystem.LifetimeOf"/>); it is a caller argument, not a field of the state.</summary>
	public ParticleFlipbookSample SampleState( ParticleState state, float lifetime, uint seed )
		=> Sample( state.Age, lifetime, state.Velocity.Length(), FirstFrame, seed );
}
