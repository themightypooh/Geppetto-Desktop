#nullable enable
using System;

namespace Marionette.Desktop;

// 2D flipbook evaluator for Geppetto's 2D particles (wave-6 lane P10, particles session).
//
// A flipbook is a sprite-sheet animation: a grid atlas (Columns x Rows) plus
// playback metadata kept alongside it (Unity Texture Sheet Animation model;
// Heaps h2d.Particles frame count + frame division + animation repeat over
// lifetime). This file owns ONLY the evaluation: given a particle's age,
// speed, base frame, and seed it returns the frame index, and given a frame
// index it returns the atlas UV rect. There is no rendering, no QML, no
// texture loading, and no canvas/UI here — the 2D suite draws the records,
// and the atlas image itself is caller-supplied layout, not a file this
// module opens. Units are canvas pixels, seconds, Y-down, like Particle2D.
//
// Determinism: the evaluator is a pure function of its inputs plus its own
// configured fields. Random start offsets come from ParticleRng (the same
// SplitMix64/xorshift RNG Particle2D uses), never System.Random, so the
// same (settings, seed) pair always yields the same frame.
//
// Relationship to Particle2D's built-in frames: Particle2D already cycles
// FrameBase..FrameMax at FrameAdvance fps from spawn. This module is the
// richer form of that idea — playback modes (Loop / Once / PingPong),
// speed-scaled frame rate, random start offsets, and atlas UVs — for
// effects that need more than a fixed fps cycle. It does NOT replace the
// built-in cycle: the coordinator sweep hooks it up (see the integration
// block in the lane report), and until then it is consumed directly by
// whoever holds a Particle2DFlipbook instance.
//
// Frame-rate rule, stated once: effectiveFps = FramesPerSecond +
// SpeedScale * speed, where speed is in px/s and SpeedScale is in added
// frames-per-second per px/s. At speed 0 the rate is exactly
// FramesPerSecond; SpeedScale 0 disables speed scaling. FramesPerSecond
// 0 means static: the frame is the base frame plus the random start
// offset (when RandomStart), and never advances with age.

/// <summary>How a flipbook plays through its frames.</summary>
public enum Particle2DFlipbookMode
{
	/// <summary>Wrap to the first frame after the last, forever.</summary>
	Loop,
	/// <summary>Advance to the last frame and hold it.</summary>
	Once,
	/// <summary>Advance to the last frame, then run backwards, then forwards, forever.</summary>
	PingPong,
}

/// <summary>UV rect of one atlas cell. Row 0 is the TOP row (Y-down canvas).</summary>
public struct Particle2DFlipbookUv
{
	public float U0, V0, U1, V1;
	public Particle2DFlipbookUv( float u0, float v0, float u1, float v1 ) { U0 = u0; V0 = v0; U1 = u1; V1 = v1; }
	public override string ToString() => $"({U0:F3},{V0:F3})-({U1:F3},{V1:F3})";
}

/// <summary>Self-contained 2D flipbook evaluator. Configure, call <see cref="Validate"/>, then evaluate.</summary>
public sealed class Particle2DFlipbook
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
	public Particle2DFlipbookMode Mode = Particle2DFlipbookMode.Loop;
	/// <summary>Base frames per second. 0 = static (base + start offset only, never advances).</summary>
	public float FramesPerSecond;
	/// <summary>Added fps per px/s of particle speed. 0 = no speed scaling.</summary>
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
	public Particle2DFlipbookUv UvFor( int frame )
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
		return new Particle2DFlipbookUv( u0, v0, u1, v1 );
	}

	/// <summary>Deterministic start offset in [0, FrameCount) for a particle seed. Same seed always gives the same offset.</summary>
	public int StartOffsetFor( uint seed )
	{
		Validate();
		return (int)(ParticleRng.For( seed, 0 ).NextFloat() * FrameCount);
	}

	/// <summary>Pure frame evaluation: age in seconds, speed in px/s, base frame (absolute), particle seed.</summary>
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
			case Particle2DFlipbookMode.Loop:
				local = (b + advanced) % n;
				break;
			case Particle2DFlipbookMode.Once:
				local = Math.Min( b + advanced, n - 1 );
				break;
			case Particle2DFlipbookMode.PingPong:
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
}
