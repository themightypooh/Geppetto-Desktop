#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

// Wave-6 lane P21: self-contained 3D emitter-shape samplers for the particle system.
// Units are kernel inches, Z-up, seconds — the same convention as ParticleSystem.cs.
// Every sample is relative to the emitter origin; the caller adds the emitter Position.
// Every sampler is a pure function of a seeded ParticleRng (never System.Random):
// the same seed and the same call order give the same samples, so effects replay
// deterministically. This file is NOT wired into ParticleSystem.cs — the hookup
// (new ParticleEmitterShape values + cases in the spawn path) is reported as
// integration lines for the coordinator sweep, not made here.
// What this file does NOT do: mesh-surface emission (ParticleSystem.cs already has
// it), skinned-mesh emission (lane P78), texture-density emission, 2D shapes,
// save/serialization (lane 5), or rendering (lanes 2/7/8 own draw paths).
// Degenerate inputs fail loudly (ArgumentException, or InvalidOperationException
// if a rejection sampler ever fails to converge) — never silent garbage.

/// <summary>One spawn point from a shape sampler: a position and an outward direction.</summary>
public readonly struct ParticleShapeSample
{
	public ParticleShapeSample( Vector3 position, Vector3 normal )
	{
		Position = position;
		Normal = normal;
	}

	/// <summary>Spawn position, relative to the emitter origin, kernel inches.</summary>
	public readonly Vector3 Position;
	/// <summary>Outward direction, unit length. For volume samples this is the outward
	/// direction of the shape at the sample angle (tube-radial for torus volume,
	/// lateral for cone volume, hemisphere direction for capsule volume, radial for
	/// cylinder/capsule volume); for the grid it is the configured Axis, since a
	/// lattice has no surface.</summary>
	public readonly Vector3 Normal;
}

/// <summary>Shared sampling helpers. Internal: only the shape classes in this file use them.</summary>
internal static class ParticleShapeSampling
{
	public const float TwoPi = (float)(Math.PI * 2.0);

	/// <summary>Orthonormal basis with Axis as the third vector: U × V = Axis.
	/// Axis-aligned axes map to themselves (Z → U=(1,0,0), V=(0,1,0) up to sign),
	/// so axis-aligned shapes behave axis-aligned; anything else gets a stable
	/// general basis (helper = Z unless Axis is near Z, then X).</summary>
	public static void AxisBasis( Vector3 axis, out Vector3 u, out Vector3 v )
	{
		var a = Vector3.Normalize( axis );
		if ( MathF.Abs( a.Z ) > 0.999f )
		{
			u = new Vector3( 1, 0, 0 );
			v = new Vector3( 0, MathF.Sign( a.Z ), 0 );
			return;
		}
		if ( MathF.Abs( a.X ) > 0.999f )
		{
			u = new Vector3( 0, 1, 0 );
			v = new Vector3( 0, 0, MathF.Sign( a.X ) );
			return;
		}
		if ( MathF.Abs( a.Y ) > 0.999f )
		{
			u = new Vector3( 0, 0, 1 );
			v = new Vector3( MathF.Sign( a.Y ), 0, 0 );
			return;
		}
		var helper = MathF.Abs( a.Z ) < 0.9f ? new Vector3( 0, 0, 1 ) : new Vector3( 1, 0, 0 );
		u = Vector3.Normalize( Vector3.Cross( a, helper ) );
		v = Vector3.Cross( a, u );
	}

	/// <summary>Map an axis-frame point (x, y along U/V, z along Axis) to world axes.</summary>
	public static Vector3 ToWorld( Vector3 u, Vector3 v, Vector3 axis, float x, float y, float z )
		=> u * x + v * y + axis * z;

	/// <summary>Uniform point in a disc of the given radius (area element r dr dθ, so r = R·√u).</summary>
	public static void DiscPoint( ref ParticleRng rng, float radius, out float x, out float y )
	{
		var r = radius * MathF.Sqrt( rng.NextFloat() );
		var theta = rng.Range( 0, TwoPi );
		x = r * MathF.Cos( theta );
		y = r * MathF.Sin( theta );
	}

	/// <summary>Uniform direction on the +Z hemisphere (z uniform in [0, 1], θ uniform).</summary>
	public static Vector3 HemisphereDirection( ref ParticleRng rng )
	{
		var z = rng.NextFloat();
		var theta = rng.Range( 0, TwoPi );
		var r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		return new Vector3( r * MathF.Cos( theta ), r * MathF.Sin( theta ), z );
	}

	public static void CheckAxis( Vector3 axis, string who )
	{
		if ( !ParticleMath.Finite( axis ) || axis.LengthSquared() < 1e-20f )
			throw new ArgumentException( $"{who}: Axis must be finite and non-zero." );
	}

	public static void CheckPositive( float value, string who, string what )
	{
		if ( !float.IsFinite( value ) || value <= 0 )
			throw new ArgumentException( $"{who}: {what} must be finite and > 0." );
	}
}

public enum ParticleTorusMode { Surface, Volume }

/// <summary>Torus (donut) emitter shape. MajorRadius is the ring radius, MinorRadius the
/// tube radius, Axis the ring axis (default Z). Surface samples are area-uniform: the
/// tube angle φ is rejection-sampled with density ∝ (MajorRadius + MinorRadius·cos φ),
/// because the torus area element is r·(R + r·cos φ) dθ dφ — uniform φ would over-emit
/// on the inner equator. A MinorRadius larger than MajorRadius (spindle torus) is
/// allowed: the tube self-intersects, and volume samples whose ring radius goes
/// non-positive inside the overlap are rejected.</summary>
public sealed class ParticleTorusShape
{
	public float MajorRadius = 1f;
	public float MinorRadius = 0.25f;
	public Vector3 Axis = new( 0, 0, 1 );
	public ParticleTorusMode Mode = ParticleTorusMode.Surface;

	public void Validate()
	{
		ParticleShapeSampling.CheckPositive( MajorRadius, "Torus", "MajorRadius" );
		ParticleShapeSampling.CheckPositive( MinorRadius, "Torus", "MinorRadius" );
		ParticleShapeSampling.CheckAxis( Axis, "Torus" );
	}

	public ParticleShapeSample Sample( ref ParticleRng rng )
	{
		Validate();
		ParticleShapeSampling.AxisBasis( Axis, out var u, out var v );
		var axis = Vector3.Normalize( Axis );
		var theta = rng.Range( 0, ParticleShapeSampling.TwoPi );
		var cosTheta = MathF.Cos( theta );
		var sinTheta = MathF.Sin( theta );
		float phi, tubeR;
		if ( Mode == ParticleTorusMode.Surface )
		{
			// Rejection-sample φ with density ∝ (R + r·cos φ) / (R + r).
			var attempts = 0;
			do
			{
				if ( ++attempts > 1000 )
					throw new InvalidOperationException( "Torus: tube-angle rejection sampling failed to converge." );
				phi = rng.Range( 0, ParticleShapeSampling.TwoPi );
			} while ( rng.NextFloat() > (MajorRadius + MinorRadius * MathF.Cos( phi )) / (MajorRadius + MinorRadius) );
			tubeR = MinorRadius;
		}
		else
		{
			// Volume: propose a point in the tube disc, accept with density ∝ ring radius.
			var attempts = 0;
			float ringR;
			do
			{
				if ( ++attempts > 1000 )
					throw new InvalidOperationException( "Torus: tube-volume rejection sampling failed to converge." );
				tubeR = MinorRadius * MathF.Sqrt( rng.NextFloat() );
				phi = rng.Range( 0, ParticleShapeSampling.TwoPi );
				ringR = MajorRadius + tubeR * MathF.Cos( phi );
			} while ( ringR <= 0 || rng.NextFloat() > ringR / (MajorRadius + tubeR) );
		}
		var ringRadius = MajorRadius + tubeR * MathF.Cos( phi );
		var local = new Vector3( ringRadius * cosTheta, ringRadius * sinTheta, tubeR * MathF.Sin( phi ) );
		var normalLocal = new Vector3( MathF.Cos( phi ) * cosTheta, MathF.Cos( phi ) * sinTheta, MathF.Sin( phi ) );
		return new ParticleShapeSample(
			ParticleShapeSampling.ToWorld( u, v, axis, local.X, local.Y, local.Z ),
			ParticleShapeSampling.ToWorld( u, v, axis, normalLocal.X, normalLocal.Y, normalLocal.Z ) );
	}
}

public enum ParticleCylinderMode { Side, Caps, SideAndCaps, Volume }

/// <summary>Cylinder emitter shape along Axis (default Z), centred on the emitter origin.
/// SideAndCaps picks side vs caps by area (side 2πRH vs caps 2πR², so side with
/// probability H / (H + R)). Volume samples carry the radial outward direction.</summary>
public sealed class ParticleCylinderShape
{
	public float Radius = 1f;
	public float Height = 1f;
	public Vector3 Axis = new( 0, 0, 1 );
	public ParticleCylinderMode Mode = ParticleCylinderMode.SideAndCaps;

	public void Validate()
	{
		ParticleShapeSampling.CheckPositive( Radius, "Cylinder", "Radius" );
		ParticleShapeSampling.CheckPositive( Height, "Cylinder", "Height" );
		ParticleShapeSampling.CheckAxis( Axis, "Cylinder" );
	}

	public ParticleShapeSample Sample( ref ParticleRng rng )
	{
		Validate();
		ParticleShapeSampling.AxisBasis( Axis, out var u, out var v );
		var axis = Vector3.Normalize( Axis );
		var half = Height * 0.5f;
		Vector3 local, normalLocal;
		var mode = Mode;
		if ( mode == ParticleCylinderMode.SideAndCaps )
			mode = rng.NextFloat() < Height / (Height + Radius) ? ParticleCylinderMode.Side : ParticleCylinderMode.Caps;
		switch ( mode )
		{
			case ParticleCylinderMode.Side:
			{
				var theta = rng.Range( 0, ParticleShapeSampling.TwoPi );
				local = new Vector3( Radius * MathF.Cos( theta ), Radius * MathF.Sin( theta ), rng.Range( -half, half ) );
				normalLocal = new Vector3( MathF.Cos( theta ), MathF.Sin( theta ), 0 );
				break;
			}
			case ParticleCylinderMode.Caps:
			{
				var sign = rng.NextFloat() < 0.5f ? -1f : 1f;
				ParticleShapeSampling.DiscPoint( ref rng, Radius, out var x, out var y );
				local = new Vector3( x, y, sign * half );
				normalLocal = new Vector3( 0, 0, sign );
				break;
			}
			default: // Volume
			{
				ParticleShapeSampling.DiscPoint( ref rng, Radius, out var x, out var y );
				local = new Vector3( x, y, rng.Range( -half, half ) );
				var radial = MathF.Sqrt( x * x + y * y );
				normalLocal = radial > 1e-6f ? new Vector3( x / radial, y / radial, 0 ) : new Vector3( 1, 0, 0 );
				break;
			}
		}
		return new ParticleShapeSample(
			ParticleShapeSampling.ToWorld( u, v, axis, local.X, local.Y, local.Z ),
			ParticleShapeSampling.ToWorld( u, v, axis, normalLocal.X, normalLocal.Y, normalLocal.Z ) );
	}
}

public enum ParticleConeMode { Lateral, LateralAndBase, Volume }

/// <summary>Cone emitter shape. Apex at the local origin, base disc at +Axis·Height with
/// radius Radius. Lateral area is π·R·L (L = slant length √(R²+H²)); base area is π·R²,
/// so LateralAndBase picks the lateral surface with probability L / (L + R).
/// Lateral samples use t = √u because lateral area up to height tH is ∝ t²; volume
/// samples use t = ∛u because volume up to tH is ∝ t³, then a disc of radius tR.</summary>
public sealed class ParticleConeShape
{
	public float Radius = 1f;
	public float Height = 1f;
	public Vector3 Axis = new( 0, 0, 1 );
	public ParticleConeMode Mode = ParticleConeMode.Lateral;

	public void Validate()
	{
		ParticleShapeSampling.CheckPositive( Radius, "Cone", "Radius" );
		ParticleShapeSampling.CheckPositive( Height, "Cone", "Height" );
		ParticleShapeSampling.CheckAxis( Axis, "Cone" );
	}

	public ParticleShapeSample Sample( ref ParticleRng rng )
	{
		Validate();
		ParticleShapeSampling.AxisBasis( Axis, out var u, out var v );
		var axis = Vector3.Normalize( Axis );
		var slant = MathF.Sqrt( Radius * Radius + Height * Height );
		Vector3 local, normalLocal;
		if ( Mode == ParticleConeMode.LateralAndBase && rng.NextFloat() >= slant / (slant + Radius) )
		{
			// Base disc.
			ParticleShapeSampling.DiscPoint( ref rng, Radius, out var x, out var y );
			local = new Vector3( x, y, Height );
			normalLocal = new Vector3( 0, 0, 1 );
		}
		else if ( Mode == ParticleConeMode.Volume )
		{
			var t = MathF.Cbrt( rng.NextFloat() );
			var rad = t * Radius * MathF.Sqrt( rng.NextFloat() );
			var theta = rng.Range( 0, ParticleShapeSampling.TwoPi );
			local = new Vector3( rad * MathF.Cos( theta ), rad * MathF.Sin( theta ), t * Height );
			normalLocal = new Vector3( Height * MathF.Cos( theta ), Height * MathF.Sin( theta ), -Radius ) / slant;
		}
		else
		{
			// Lateral surface (also the lateral half of LateralAndBase).
			var t = MathF.Sqrt( rng.NextFloat() );
			var theta = rng.Range( 0, ParticleShapeSampling.TwoPi );
			local = new Vector3( t * Radius * MathF.Cos( theta ), t * Radius * MathF.Sin( theta ), t * Height );
			normalLocal = new Vector3( Height * MathF.Cos( theta ), Height * MathF.Sin( theta ), -Radius ) / slant;
		}
		return new ParticleShapeSample(
			ParticleShapeSampling.ToWorld( u, v, axis, local.X, local.Y, local.Z ),
			ParticleShapeSampling.ToWorld( u, v, axis, normalLocal.X, normalLocal.Y, normalLocal.Z ) );
	}
}

/// <summary>Flat disc emitter shape: uniform in the disc, one-sided normal = Axis.
/// The disc lies in the plane through the emitter origin perpendicular to Axis.</summary>
public sealed class ParticleDiscShape
{
	public float Radius = 1f;
	public Vector3 Axis = new( 0, 0, 1 );

	public void Validate()
	{
		ParticleShapeSampling.CheckPositive( Radius, "Disc", "Radius" );
		ParticleShapeSampling.CheckAxis( Axis, "Disc" );
	}

	public ParticleShapeSample Sample( ref ParticleRng rng )
	{
		Validate();
		ParticleShapeSampling.AxisBasis( Axis, out var u, out var v );
		var axis = Vector3.Normalize( Axis );
		ParticleShapeSampling.DiscPoint( ref rng, Radius, out var x, out var y );
		return new ParticleShapeSample(
			ParticleShapeSampling.ToWorld( u, v, axis, x, y, 0 ),
			axis );
	}
}

public enum ParticleCapsuleMode { Surface, Volume }

/// <summary>Capsule emitter shape: a cylinder section of length CylinderHeight plus a
/// hemisphere of radius Radius at each end, along Axis. CylinderHeight 0 is a sphere
/// (both modes still valid). Surface picks side vs caps by area (2πRH vs 4πR², so side
/// with probability H / (H + 2R)); volume picks cylinder vs caps by volume (πR²H vs
/// 4πR³/3, so cylinder with probability H / (H + 4R/3)).</summary>
public sealed class ParticleCapsuleShape
{
	public float Radius = 1f;
	public float CylinderHeight = 1f;
	public Vector3 Axis = new( 0, 0, 1 );
	public ParticleCapsuleMode Mode = ParticleCapsuleMode.Surface;

	public void Validate()
	{
		ParticleShapeSampling.CheckPositive( Radius, "Capsule", "Radius" );
		if ( !float.IsFinite( CylinderHeight ) || CylinderHeight < 0 )
			throw new ArgumentException( "Capsule: CylinderHeight must be finite and >= 0." );
		ParticleShapeSampling.CheckAxis( Axis, "Capsule" );
	}

	public ParticleShapeSample Sample( ref ParticleRng rng )
	{
		Validate();
		ParticleShapeSampling.AxisBasis( Axis, out var u, out var v );
		var axis = Vector3.Normalize( Axis );
		var half = CylinderHeight * 0.5f;
		Vector3 local, normalLocal;
		if ( Mode == ParticleCapsuleMode.Surface )
		{
			if ( CylinderHeight > 0 && rng.NextFloat() < CylinderHeight / (CylinderHeight + 2 * Radius) )
			{
				var theta = rng.Range( 0, ParticleShapeSampling.TwoPi );
				local = new Vector3( Radius * MathF.Cos( theta ), Radius * MathF.Sin( theta ), rng.Range( -half, half ) );
				normalLocal = new Vector3( MathF.Cos( theta ), MathF.Sin( theta ), 0 );
			}
			else
			{
				var sign = rng.NextFloat() < 0.5f ? -1f : 1f;
				var dir = ParticleShapeSampling.HemisphereDirection( ref rng );
				dir.Z *= sign;
				local = new Vector3( dir.X * Radius, dir.Y * Radius, sign * half + dir.Z * Radius );
				normalLocal = dir;
			}
		}
		else
		{
			if ( CylinderHeight > 0 && rng.NextFloat() < CylinderHeight / (CylinderHeight + 4 * Radius / 3 ) )
			{
				ParticleShapeSampling.DiscPoint( ref rng, Radius, out var x, out var y );
				local = new Vector3( x, y, rng.Range( -half, half ) );
				var radial = MathF.Sqrt( x * x + y * y );
				normalLocal = radial > 1e-6f ? new Vector3( x / radial, y / radial, 0 ) : new Vector3( 1, 0, 0 );
			}
			else
			{
				var sign = rng.NextFloat() < 0.5f ? -1f : 1f;
				var dir = ParticleShapeSampling.HemisphereDirection( ref rng );
				dir.Z *= sign;
				var r = Radius * MathF.Cbrt( rng.NextFloat() );
				local = new Vector3( dir.X * r, dir.Y * r, sign * half + dir.Z * r );
				normalLocal = dir;
			}
		}
		return new ParticleShapeSample(
			ParticleShapeSampling.ToWorld( u, v, axis, local.X, local.Y, local.Z ),
			ParticleShapeSampling.ToWorld( u, v, axis, normalLocal.X, normalLocal.Y, normalLocal.Z ) );
	}
}

/// <summary>Regular lattice emitter shape: CountX × CountY × CountZ cells spaced by
/// Spacing along the axis frame (U, V, Axis), centred on the emitter origin.
/// Jitter is a fraction of Spacing (0..0.5) applied per axis, so jittered points
/// never cross into a neighbouring cell. The reported normal is Axis: a lattice
/// has no surface. Sample picks a uniformly random cell; CellAt addresses a cell
/// deterministically by index (X fastest).</summary>
public sealed class ParticleGridShape
{
	public int CountX = 1;
	public int CountY = 1;
	public int CountZ = 1;
	public Vector3 Spacing = new( 1, 1, 1 );
	public float Jitter;
	public Vector3 Axis = new( 0, 0, 1 );

	public int TotalCells => CountX * CountY * CountZ;

	public void Validate()
	{
		if ( CountX < 1 || CountY < 1 || CountZ < 1 )
			throw new ArgumentException( "Grid: counts must be >= 1." );
		if ( (long)CountX * CountY * CountZ > int.MaxValue )
			throw new ArgumentException( "Grid: total cell count overflows int." );
		if ( !ParticleMath.Finite( Spacing ) || Spacing.X <= 0 || Spacing.Y <= 0 || Spacing.Z <= 0 )
			throw new ArgumentException( "Grid: Spacing must be finite and > 0 on every axis." );
		if ( !float.IsFinite( Jitter ) || Jitter < 0 || Jitter > 0.5f )
			throw new ArgumentException( "Grid: Jitter must be in 0..0.5." );
		ParticleShapeSampling.CheckAxis( Axis, "Grid" );
	}

	public ParticleShapeSample Sample( ref ParticleRng rng )
	{
		Validate();
		var index = (int)(rng.NextFloat() * TotalCells);
		return CellAt( index, ref rng );
	}

	public ParticleShapeSample CellAt( int index, ref ParticleRng rng )
	{
		Validate();
		if ( index < 0 || index >= TotalCells )
			throw new ArgumentOutOfRangeException( nameof( index ), $"Grid: cell index {index} out of range 0..{TotalCells - 1}." );
		var ix = index % CountX;
		var iy = (index / CountX) % CountY;
		var iz = index / (CountX * CountY);
		ParticleShapeSampling.AxisBasis( Axis, out var u, out var v );
		var axis = Vector3.Normalize( Axis );
		var lx = (ix - (CountX - 1) * 0.5f) * Spacing.X + rng.Range( -Jitter * Spacing.X, Jitter * Spacing.X );
		var ly = (iy - (CountY - 1) * 0.5f) * Spacing.Y + rng.Range( -Jitter * Spacing.Y, Jitter * Spacing.Y );
		var lz = (iz - (CountZ - 1) * 0.5f) * Spacing.Z + rng.Range( -Jitter * Spacing.Z, Jitter * Spacing.Z );
		return new ParticleShapeSample(
			ParticleShapeSampling.ToWorld( u, v, axis, lx, ly, lz ),
			axis );
	}
}
