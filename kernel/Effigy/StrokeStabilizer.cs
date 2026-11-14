using System;

namespace Effigy;

/// <summary>
/// 2nd-order critically damped spring-mass inking stabilizer (Lazy Nezumi style).
/// Replaces raw mouse/stylus jitter with a physically modeled trailing pen tip.
/// </summary>
public sealed class StrokeStabilizer
{
	public float Stiffness = 180.0f;
	public float Damping = 24.0f;
	public bool Enabled = true;

	public Vec2 PenPos;
	public Vec2 PenVel;
	private bool _initialized;

	public void Reset( Vec2 initialPosition )
	{
		PenPos = initialPosition;
		PenVel = new Vec2( 0f, 0f );
		_initialized = true;
	}

	public Vec2 Update( Vec2 targetPos, float dt )
	{
		if ( !_initialized || !Enabled )
		{
			PenPos = targetPos;
			PenVel = new Vec2( 0f, 0f );
			_initialized = true;
			return targetPos;
		}

		dt = Math.Clamp( dt, 0.001f, 0.05f );
		var diff = targetPos - PenPos;
		var acc = (diff * Stiffness) - (PenVel * Damping);
		PenVel += acc * dt;
		PenPos += PenVel * dt;
		return PenPos;
	}
}
