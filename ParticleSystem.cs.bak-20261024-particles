#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Effigy;

namespace Marionette.Desktop;

// Ticket 17: deterministic fixed-step particle system (CPU reference). Units are kernel inches,
// Z-up, seconds. GpuParticleSystem runs the same per-particle integration in a compute shader;
// any change to Integrate/ParticleMath must be mirrored in GpuParticleSystem's GLSL.

public enum ParticleEmitterShape { Point, Area, MeshSurface, Box, Sphere }

/// <summary>How a renderer should draw the particles. The simulation is identical for all three.</summary>
public enum ParticleRenderType { Billboard, MeshInstance, Strand }

/// <summary>Per-slot simulation state. 64 bytes, laid out as four std430 vec4s so the same
/// array uploads unchanged to <see cref="GpuParticleSystem"/>.</summary>
[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct ParticleState
{
	public Vector3 Position;
	public float Age;
	public Vector3 Velocity;
	/// <summary>1 when the slot holds a live particle, 0 when free.</summary>
	public float Alive;
	public Quaternion Rotation;
	/// <summary>World-space angular velocity, radians per second.</summary>
	public Vector3 AngularVelocity;
	public float Size;

	public const int SizeInBytes = 64;
}

/// <summary>Emit <paramref name="Count"/> particles at the start of fixed step <paramref name="Step"/>.</summary>
public readonly record struct ParticleBurst( int Step, int Count );

/// <summary>Billboard draw record: centre, size, normalized age 0..1 (for fades) and velocity (for stretch/motion vectors).</summary>
[StructLayout( LayoutKind.Sequential, Pack = 4 )]
public struct ParticleBillboard
{
	public Vector3 Position;
	public float Size;
	public Vector3 Velocity;
	public float NormalizedAge;
}

/// <summary>
/// Immutable triangle soup used for mesh-surface emission (area-weighted) and mesh collision
/// (uniform spatial grid, CSR cell lists). Geometry in kernel inches.
/// </summary>
public sealed class ParticleMesh
{
	public readonly Vector3[] Vertices;
	public readonly int[] Indices;
	public int TriangleCount => Indices.Length / 3;
	public readonly float TotalArea;
	/// <summary>Cumulative triangle area, length TriangleCount; last entry == TotalArea.</summary>
	public readonly float[] AreaCdf;

	// Uniform grid over the (slightly padded) bounds.
	public readonly Vector3 GridOrigin;
	public readonly float CellSize;
	public readonly int DimX, DimY, DimZ;
	/// <summary>CSR: triangles of cell c are CellTriangles[CellStart[c] .. CellStart[c+1]).</summary>
	public readonly int[] CellStart;
	public readonly int[] CellTriangles;
	public readonly Vector3 BoundsMin, BoundsMax;

	public ParticleMesh( IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, float cellSize = 0f )
	{
		ArgumentNullException.ThrowIfNull( vertices );
		ArgumentNullException.ThrowIfNull( indices );
		if ( indices.Count == 0 || indices.Count % 3 != 0 ) throw new ArgumentException( "Indices must be a non-empty multiple of 3.", nameof( indices ) );
		if ( !float.IsFinite( cellSize ) || cellSize < 0 ) throw new ArgumentOutOfRangeException( nameof( cellSize ) );
		Vertices = new Vector3[vertices.Count];
		for ( int i = 0; i < Vertices.Length; i++ )
		{
			var v = vertices[i];
			if ( !ParticleMath.Finite( v ) ) throw new ArgumentException( $"Vertex {i} is not finite.", nameof( vertices ) );
			Vertices[i] = v;
		}
		Indices = new int[indices.Count];
		for ( int i = 0; i < Indices.Length; i++ )
		{
			int ix = indices[i];
			if ( (uint)ix >= (uint)Vertices.Length ) throw new ArgumentOutOfRangeException( nameof( indices ), $"Index {ix} at {i} is outside 0..{Vertices.Length - 1}." );
			Indices[i] = ix;
		}

		AreaCdf = new float[TriangleCount];
		double total = 0;
		Vector3 mn = new( float.MaxValue ), mx = new( float.MinValue );
		for ( int t = 0; t < TriangleCount; t++ )
		{
			GetTriangle( t, out var a, out var b, out var c );
			total += 0.5 * Vector3.Cross( b - a, c - a ).Length();
			AreaCdf[t] = (float)total;
			mn = Vector3.Min( mn, Vector3.Min( a, Vector3.Min( b, c ) ) );
			mx = Vector3.Max( mx, Vector3.Max( a, Vector3.Max( b, c ) ) );
		}
		TotalArea = (float)total;
		if ( !(TotalArea > 0) ) throw new ArgumentException( "Mesh has zero total area (all triangles degenerate)." );

		var extent = mx - mn;
		float maxExtent = MathF.Max( extent.X, MathF.Max( extent.Y, extent.Z ) );
		if ( cellSize <= 0 )
		{
			// Aim for roughly 2 triangles per occupied cell along the dominant surface.
			float side = MathF.Sqrt( MathF.Max( 1, TriangleCount ) / 2f );
			cellSize = maxExtent / MathF.Max( 1, side );
		}
		if ( cellSize <= 0 ) cellSize = 1f;
		float pad = cellSize * 0.01f + 1e-4f;
		BoundsMin = mn - new Vector3( pad );
		BoundsMax = mx + new Vector3( pad );
		GridOrigin = BoundsMin;
		CellSize = cellSize;
		var size = BoundsMax - BoundsMin;
		DimX = Math.Clamp( (int)MathF.Ceiling( size.X / cellSize ), 1, 256 );
		DimY = Math.Clamp( (int)MathF.Ceiling( size.Y / cellSize ), 1, 256 );
		DimZ = Math.Clamp( (int)MathF.Ceiling( size.Z / cellSize ), 1, 256 );
		// If the dims were clamped, grow the cell so the grid still covers the bounds.
		CellSize = MathF.Max( cellSize, MathF.Max( size.X / DimX, MathF.Max( size.Y / DimY, size.Z / DimZ ) ) );
		long cells = (long)DimX * DimY * DimZ;
		if ( cells > 4_000_000 ) throw new ArgumentException( "Collision grid too large; pass a larger cellSize." );

		var counts = new int[cells + 1];
		void ForCells( int t, Action<int> visit )
		{
			GetTriangle( t, out var a, out var b, out var c );
			var lo = Vector3.Min( a, Vector3.Min( b, c ) );
			var hi = Vector3.Max( a, Vector3.Max( b, c ) );
			CellRange( lo, hi, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1 );
			for ( int z = z0; z <= z1; z++ )
				for ( int y = y0; y <= y1; y++ )
					for ( int x = x0; x <= x1; x++ )
						visit( (z * DimY + y) * DimX + x );
		}
		for ( int t = 0; t < TriangleCount; t++ ) ForCells( t, c => counts[c + 1]++ );
		for ( int c = 0; c < cells; c++ ) counts[c + 1] += counts[c];
		CellStart = counts;
		CellTriangles = new int[counts[cells]];
		var fill = new int[cells];
		for ( int t = 0; t < TriangleCount; t++ )
		{
			int tri = t;
			ForCells( t, c => CellTriangles[CellStart[c] + fill[c]++] = tri );
		}
	}

	/// <summary>Fan-triangulates a kernel PolyMesh (optionally transformed) for emission/collision.</summary>
	public static ParticleMesh FromPolyMesh( PolyMesh mesh, Xform? transform = null, float cellSize = 0f )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		var xf = transform ?? Xform.Identity;
		var verts = new Vector3[mesh.Positions.Count];
		for ( int i = 0; i < verts.Length; i++ )
		{
			var p = xf.TransformPoint( mesh.Positions[i] );
			verts[i] = new Vector3( p.x, p.y, p.z );
		}
		var idx = new List<int>();
		foreach ( var f in mesh.Faces )
			for ( int k = 1; k + 1 < f.Indices.Length; k++ )
			{
				idx.Add( f.Indices[0] ); idx.Add( f.Indices[k] ); idx.Add( f.Indices[k + 1] );
			}
		return new ParticleMesh( verts, idx, cellSize );
	}

	public void GetTriangle( int t, out Vector3 a, out Vector3 b, out Vector3 c )
	{
		a = Vertices[Indices[t * 3]];
		b = Vertices[Indices[t * 3 + 1]];
		c = Vertices[Indices[t * 3 + 2]];
	}

	/// <summary>Inclusive cell range covering an AABB, clamped to the grid.</summary>
	public void CellRange( Vector3 lo, Vector3 hi, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1 )
	{
		var a = (lo - GridOrigin) / CellSize;
		var b = (hi - GridOrigin) / CellSize;
		x0 = Math.Clamp( (int)MathF.Floor( a.X ), 0, DimX - 1 ); x1 = Math.Clamp( (int)MathF.Floor( b.X ), 0, DimX - 1 );
		y0 = Math.Clamp( (int)MathF.Floor( a.Y ), 0, DimY - 1 ); y1 = Math.Clamp( (int)MathF.Floor( b.Y ), 0, DimY - 1 );
		z0 = Math.Clamp( (int)MathF.Floor( a.Z ), 0, DimZ - 1 ); z1 = Math.Clamp( (int)MathF.Floor( b.Z ), 0, DimZ - 1 );
	}

	/// <summary>Earliest crossing of segment p0→p1 with any triangle, via the grid. Normal faces p0.</summary>
	public bool Raycast( Vector3 p0, Vector3 p1, out float tHit, out Vector3 normal )
	{
		tHit = 2f; normal = default;
		var lo = Vector3.Min( p0, p1 );
		var hi = Vector3.Max( p0, p1 );
		if ( hi.X < BoundsMin.X || hi.Y < BoundsMin.Y || hi.Z < BoundsMin.Z || lo.X > BoundsMax.X || lo.Y > BoundsMax.Y || lo.Z > BoundsMax.Z )
			return false;
		CellRange( lo, hi, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1 );
		bool hit = false;
		for ( int z = z0; z <= z1; z++ )
			for ( int y = y0; y <= y1; y++ )
				for ( int x = x0; x <= x1; x++ )
				{
					int c = (z * DimY + y) * DimX + x;
					for ( int k = CellStart[c]; k < CellStart[c + 1]; k++ )
					{
						GetTriangle( CellTriangles[k], out var a, out var b, out var cc );
						if ( ParticleMath.SegmentTriangle( p0, p1, a, b, cc, out float t, out var n ) && t < tHit )
						{
							tHit = t; normal = n; hit = true;
						}
					}
				}
		return hit;
	}

	/// <summary>Brute-force reference (no grid), for tests.</summary>
	public bool RaycastBruteForce( Vector3 p0, Vector3 p1, out float tHit, out Vector3 normal )
	{
		tHit = 2f; normal = default;
		bool hit = false;
		for ( int t = 0; t < TriangleCount; t++ )
		{
			GetTriangle( t, out var a, out var b, out var c );
			if ( ParticleMath.SegmentTriangle( p0, p1, a, b, c, out float th, out var n ) && th < tHit ) { tHit = th; normal = n; hit = true; }
		}
		return hit;
	}
}

/// <summary>An emitter. Configure before or between steps; values are validated on each step.</summary>
public sealed class ParticleEmitter
{
	public ParticleEmitterShape Shape = ParticleEmitterShape.Point;
	/// <summary>Point position / area, box, sphere centre.</summary>
	public Vector3 Position;
	/// <summary>Area: half-extent edge vectors of the parallelogram (normal = normalize(U × V)).</summary>
	public Vector3 AreaU = new( 1, 0, 0 ), AreaV = new( 0, 1, 0 );
	/// <summary>Box half extents (axis aligned).</summary>
	public Vector3 BoxHalfExtents = new( 1, 1, 1 );
	public float SphereRadius = 1f;
	public ParticleMesh? Mesh;

	/// <summary>Particles per second (continuous, fractional remainder carried between steps).</summary>
	public float Rate;
	public List<ParticleBurst> Bursts = new();

	public float LifetimeMin = 1f, LifetimeMax = 1f;
	public float SizeMin = 1f, SizeMax = 1f;
	/// <summary>Launch direction; for Area/MeshSurface with <see cref="UseSurfaceNormal"/> the surface normal is used.</summary>
	public Vector3 Direction = new( 0, 0, 1 );
	public bool UseSurfaceNormal = true;
	public float Speed;
	/// <summary>Speed is uniformly in Speed*(1 ± SpeedJitter).</summary>
	public float SpeedJitter;
	/// <summary>Cone half-angle around the launch direction, degrees 0..180 (180 = any direction).</summary>
	public float SpreadDegrees;
	/// <summary>Added to every launch velocity (e.g. emitter motion).</summary>
	public Vector3 BaseVelocity;
	/// <summary>Max angular speed (rad/s) about a random axis; initial rotation is random when > 0.</summary>
	public float AngularSpeedMax;
	public bool Enabled = true;

	internal ParticleRng Rng;
	internal double RateRemainder;

	internal void Validate( int index )
	{
		ParticleEmitterName who = new( index );
		if ( !ParticleMath.Finite( Position ) || !ParticleMath.Finite( Direction ) || !ParticleMath.Finite( BaseVelocity ) )
			throw new ArgumentException( $"{who}: position/direction/velocity must be finite." );
		if ( !float.IsFinite( Rate ) || Rate < 0 ) throw new ArgumentException( $"{who}: Rate must be finite and >= 0." );
		if ( !float.IsFinite( LifetimeMin ) || !float.IsFinite( LifetimeMax ) || LifetimeMin <= 0 || LifetimeMax < LifetimeMin )
			throw new ArgumentException( $"{who}: lifetime must satisfy 0 < LifetimeMin <= LifetimeMax." );
		if ( !float.IsFinite( SizeMin ) || !float.IsFinite( SizeMax ) || SizeMin <= 0 || SizeMax < SizeMin )
			throw new ArgumentException( $"{who}: size must satisfy 0 < SizeMin <= SizeMax." );
		if ( !float.IsFinite( Speed ) || Speed < 0 || !float.IsFinite( SpeedJitter ) || SpeedJitter < 0 || SpeedJitter > 1 )
			throw new ArgumentException( $"{who}: Speed >= 0 and SpeedJitter in 0..1 required." );
		if ( !float.IsFinite( SpreadDegrees ) || SpreadDegrees < 0 || SpreadDegrees > 180 )
			throw new ArgumentException( $"{who}: SpreadDegrees must be in 0..180." );
		if ( !float.IsFinite( AngularSpeedMax ) || AngularSpeedMax < 0 ) throw new ArgumentException( $"{who}: AngularSpeedMax must be >= 0." );
		if ( Direction.LengthSquared() < 1e-12f && !(UseSurfaceNormal && Shape is ParticleEmitterShape.Area or ParticleEmitterShape.MeshSurface) && Speed > 0 )
			throw new ArgumentException( $"{who}: Direction must be non-zero." );
		foreach ( var b in Bursts )
			if ( b.Step < 0 || b.Count < 0 ) throw new ArgumentException( $"{who}: bursts need Step >= 0 and Count >= 0." );
		switch ( Shape )
		{
			case ParticleEmitterShape.Area:
				if ( !ParticleMath.Finite( AreaU ) || !ParticleMath.Finite( AreaV ) || Vector3.Cross( AreaU, AreaV ).LengthSquared() < 1e-20f )
					throw new ArgumentException( $"{who}: area edges must be finite and non-parallel." );
				break;
			case ParticleEmitterShape.Box:
				if ( !ParticleMath.Finite( BoxHalfExtents ) || BoxHalfExtents.X < 0 || BoxHalfExtents.Y < 0 || BoxHalfExtents.Z < 0 )
					throw new ArgumentException( $"{who}: box half extents must be finite and >= 0." );
				break;
			case ParticleEmitterShape.Sphere:
				if ( !float.IsFinite( SphereRadius ) || SphereRadius < 0 ) throw new ArgumentException( $"{who}: sphere radius must be >= 0." );
				break;
			case ParticleEmitterShape.MeshSurface:
				if ( Mesh is null ) throw new ArgumentException( $"{who}: MeshSurface emitter needs a Mesh." );
				break;
			case ParticleEmitterShape.Point: break;
			default: throw new ArgumentException( $"{who}: unknown shape {Shape}." );
		}
	}
}

/// <summary>Simulation settings. Defaults: kernel inches, Z-up, 60 Hz fixed step.</summary>
public sealed class ParticleSystemSettings
{
	public int Capacity = 10_000;
	public uint Seed = 1;
	/// <summary>Fixed step, seconds. The bake frame rate is 1/FixedDeltaTime.</summary>
	public float FixedDeltaTime = 1f / 60f;
	public int Substeps = 1;

	/// <summary>Default 9.80665 m/s² in inches/s², pointing −Z.</summary>
	public Vector3 Gravity = new( 0, 0, -386.0886f );
	/// <summary>Air velocity. Only acts through <see cref="Drag"/> (drag pulls velocity toward wind).</summary>
	public Vector3 Wind;
	/// <summary>Linear drag coefficient, 1/s; integrated semi-implicitly (stable for any value >= 0).</summary>
	public float Drag;

	/// <summary>Curl-noise acceleration magnitude scale (in/s²). 0 disables.</summary>
	public float TurbulenceStrength;
	/// <summary>Noise lattice frequency, 1/inch.</summary>
	public float TurbulenceFrequency = 0.1f;
	/// <summary>How fast the field scrolls along +Z of noise space, noise units/s.</summary>
	public float TurbulenceSpeed;

	/// <summary>Vortex: acceleration = cross(axis, r⊥) · Strength / (|r⊥|² + Radius²). 0 disables.</summary>
	public float VortexStrength;
	public Vector3 VortexCenter;
	public Vector3 VortexAxis = new( 0, 0, 1 );
	public float VortexRadius = 1f;

	public bool GroundEnabled;
	/// <summary>Half space Normal·x >= Offset + CollisionRadius is free.</summary>
	public Vector3 GroundNormal = new( 0, 0, 1 );
	public float GroundOffset;
	public ParticleMesh? CollisionMesh;
	/// <summary>Particle radius / contact skin, inches (> 0).</summary>
	public float CollisionRadius = 0.01f;
	public float Restitution = 0.3f;
	/// <summary>Tangential velocity retained on contact is (1 − Friction).</summary>
	public float Friction = 0.2f;

	public ParticleRenderType RenderType = ParticleRenderType.Billboard;
	/// <summary>Strand type: points of trailing history kept per particle (0 = off). Ring buffer, one point per step.</summary>
	public int StrandPoints;

	public void Validate()
	{
		if ( Capacity <= 0 || Capacity > 4_000_000 ) throw new ArgumentOutOfRangeException( nameof( Capacity ), "Capacity must be 1..4,000,000." );
		if ( !float.IsFinite( FixedDeltaTime ) || FixedDeltaTime <= 0 || FixedDeltaTime > 1 ) throw new ArgumentOutOfRangeException( nameof( FixedDeltaTime ), "Fixed step must be in (0, 1] s." );
		if ( Substeps < 1 || Substeps > 64 ) throw new ArgumentOutOfRangeException( nameof( Substeps ), "Substeps must be 1..64." );
		if ( !ParticleMath.Finite( Gravity ) || !ParticleMath.Finite( Wind ) || !ParticleMath.Finite( VortexCenter ) || !ParticleMath.Finite( VortexAxis ) || !ParticleMath.Finite( GroundNormal ) )
			throw new ArgumentException( "Force vectors must be finite." );
		if ( !float.IsFinite( Drag ) || Drag < 0 ) throw new ArgumentOutOfRangeException( nameof( Drag ) );
		if ( !float.IsFinite( TurbulenceStrength ) || TurbulenceStrength < 0 || !float.IsFinite( TurbulenceFrequency ) || TurbulenceFrequency <= 0 || !float.IsFinite( TurbulenceSpeed ) )
			throw new ArgumentException( "Turbulence needs strength >= 0, frequency > 0, finite speed." );
		if ( !float.IsFinite( VortexStrength ) || !float.IsFinite( VortexRadius ) || VortexRadius <= 0 ) throw new ArgumentException( "Vortex needs finite strength and radius > 0." );
		if ( VortexStrength != 0 && VortexAxis.LengthSquared() < 1e-12f ) throw new ArgumentException( "Vortex axis must be non-zero." );
		if ( GroundEnabled && GroundNormal.LengthSquared() < 1e-12f ) throw new ArgumentException( "Ground normal must be non-zero." );
		if ( !float.IsFinite( GroundOffset ) ) throw new ArgumentException( "Ground offset must be finite." );
		if ( !float.IsFinite( CollisionRadius ) || CollisionRadius <= 0 ) throw new ArgumentOutOfRangeException( nameof( CollisionRadius ), "Collision radius must be > 0." );
		if ( !float.IsFinite( Restitution ) || Restitution < 0 || Restitution > 1 ) throw new ArgumentOutOfRangeException( nameof( Restitution ), "Restitution must be 0..1." );
		if ( !float.IsFinite( Friction ) || Friction < 0 || Friction > 1 ) throw new ArgumentOutOfRangeException( nameof( Friction ), "Friction must be 0..1." );
		if ( StrandPoints < 0 || StrandPoints > 256 ) throw new ArgumentOutOfRangeException( nameof( StrandPoints ), "StrandPoints must be 0..256." );
		if ( RenderType == ParticleRenderType.Strand && StrandPoints < 2 ) throw new ArgumentException( "Strand render type needs StrandPoints >= 2." );
	}
}

/// <summary>Formats "Emitter N" only when an error message is actually built (keeps validation allocation-free).</summary>
internal readonly struct ParticleEmitterName
{
	readonly int _index;
	public ParticleEmitterName( int index ) { _index = index; }
	public override string ToString() => $"Emitter {_index}";
}

/// <summary>Deterministic SplitMix64-seeded xorshift RNG; never uses System.Random.</summary>
public struct ParticleRng
{
	ulong _s;
	public ParticleRng( ulong seed ) { _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }
	public static ParticleRng For( uint systemSeed, int stream )
	{
		ulong z = ((ulong)systemSeed << 32) ^ (ulong)(uint)stream ^ 0xD1B54A32D192ED03UL;
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
		z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
		return new ParticleRng( z ^ (z >> 31) );
	}
	public ulong NextU64()
	{
		ulong x = _s;
		x ^= x << 13; x ^= x >> 7; x ^= x << 17;
		_s = x;
		return x * 0x2545F4914F6CDD1DUL;
	}
	/// <summary>Uniform in [0, 1).</summary>
	public float NextFloat() => (NextU64() >> 40) * (1f / 16777216f);
	public float Range( float a, float b ) => a + (b - a) * NextFloat();
}

/// <summary>Shared particle math. Mirrored exactly in the GLSL of <see cref="GpuParticleSystem"/>.</summary>
public static class ParticleMath
{
	public static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );

	public static uint Hash( int x, int y, int z, uint seed )
	{
		unchecked
		{
			uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ (uint)z * 0xcb1ab31fu ^ seed;
			h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
			return h;
		}
	}

	static float Lattice( int x, int y, int z, uint seed ) => (Hash( x, y, z, seed ) & 0xFFFFFFu) * (2f / 16777215f) - 1f;

	/// <summary>Value noise in [-1,1] with smoothstep fade; returns value and analytic gradient.</summary>
	public static float NoiseGrad( Vector3 p, uint seed, out Vector3 grad )
	{
		float fx = MathF.Floor( p.X ), fy = MathF.Floor( p.Y ), fz = MathF.Floor( p.Z );
		int ix = (int)fx, iy = (int)fy, iz = (int)fz;
		float tx = p.X - fx, ty = p.Y - fy, tz = p.Z - fz;
		float ux = tx * tx * (3f - 2f * tx), uy = ty * ty * (3f - 2f * ty), uz = tz * tz * (3f - 2f * tz);
		float dx = 6f * tx * (1f - tx), dy = 6f * ty * (1f - ty), dz = 6f * tz * (1f - tz);
		float c000 = Lattice( ix, iy, iz, seed ), c100 = Lattice( ix + 1, iy, iz, seed );
		float c010 = Lattice( ix, iy + 1, iz, seed ), c110 = Lattice( ix + 1, iy + 1, iz, seed );
		float c001 = Lattice( ix, iy, iz + 1, seed ), c101 = Lattice( ix + 1, iy, iz + 1, seed );
		float c011 = Lattice( ix, iy + 1, iz + 1, seed ), c111 = Lattice( ix + 1, iy + 1, iz + 1, seed );
		float x00 = c000 + (c100 - c000) * ux, x10 = c010 + (c110 - c010) * ux;
		float x01 = c001 + (c101 - c001) * ux, x11 = c011 + (c111 - c011) * ux;
		float y0 = x00 + (x10 - x00) * uy, y1 = x01 + (x11 - x01) * uy;
		float v = y0 + (y1 - y0) * uz;
		// d/dx
		float dx00 = (c100 - c000) * dx, dx10 = (c110 - c010) * dx, dx01 = (c101 - c001) * dx, dx11 = (c111 - c011) * dx;
		float dy0x = dx00 + (dx10 - dx00) * uy, dy1x = dx01 + (dx11 - dx01) * uy;
		float gx = dy0x + (dy1x - dy0x) * uz;
		// d/dy
		float gy0 = (x10 - x00) * dy, gy1 = (x11 - x01) * dy;
		float gy = gy0 + (gy1 - gy0) * uz;
		// d/dz
		float gz = (y1 - y0) * dz;
		grad = new Vector3( gx, gy, gz );
		return v;
	}

	/// <summary>Divergence-free curl of a 3-channel value-noise potential at noise-space point p.</summary>
	public static Vector3 Curl( Vector3 p, uint seed )
	{
		NoiseGrad( p, seed, out var g1 );
		NoiseGrad( p, seed ^ 0x68bc21ebu, out var g2 );
		NoiseGrad( p, seed ^ 0x02e5be93u, out var g3 );
		return new Vector3( g3.Y - g2.Z, g1.Z - g3.X, g2.X - g1.Y );
	}

	/// <summary>Two-sided segment/triangle intersection (Möller–Trumbore), t in [0,1]; normal faces p0.</summary>
	public static bool SegmentTriangle( Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c, out float t, out Vector3 normal )
	{
		t = 0; normal = default;
		var e1 = b - a; var e2 = c - a; var d = p1 - p0;
		var pv = Vector3.Cross( d, e2 );
		float det = Vector3.Dot( e1, pv );
		if ( MathF.Abs( det ) < 1e-20f ) return false;
		float inv = 1f / det;
		var tv = p0 - a;
		float u = Vector3.Dot( tv, pv ) * inv;
		if ( u < -1e-5f || u > 1.00001f ) return false;
		var qv = Vector3.Cross( tv, e1 );
		float w = Vector3.Dot( d, qv ) * inv;
		if ( w < -1e-5f || u + w > 1.00001f ) return false;
		t = Vector3.Dot( e2, qv ) * inv;
		if ( t < 0f || t > 1f ) return false;
		var n = Vector3.Cross( e1, e2 );
		float len = n.Length();
		if ( !(len > 0) ) return false;
		n /= len;
		if ( Vector3.Dot( n, d ) > 0 ) n = -n;
		normal = n;
		return true;
	}

	/// <summary>Hamilton product a*b with (x,y,z,w) layout, written out to match GLSL.</summary>
	public static Quaternion Mul( Quaternion a, Quaternion b ) => new(
		a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
		a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
		a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
		a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z );
}

/// <summary>
/// Fixed-timestep, seeded particle system. <see cref="Step"/> advances exactly one fixed step:
/// expire → emit → <see cref="ParticleSystemSettings.Substeps"/> × (forces, integrate, collide).
/// After the first step the CPU path does not allocate.
/// </summary>
public sealed class ParticleSystem
{
	public ParticleSystemSettings Settings { get; }
	readonly List<ParticleEmitter> _emitters = new();
	public IReadOnlyList<ParticleEmitter> Emitters => _emitters;

	readonly ParticleState[] _state;
	readonly uint[] _ids;
	readonly long[] _deathStep;
	readonly float[] _lifeSeconds;
	readonly int[] _freeStack;
	int _freeCount;
	int _highWater;
	uint _nextId;

	// Slots whose CPU state changed outside simulation this step (spawned or killed); for GPU upload.
	readonly int[] _changed;
	readonly long[] _changedStamp;
	int _changedCount;

	// Strand ring buffer: slot * StrandPoints + (step % StrandPoints).
	readonly Vector3[]? _history;

	// Sort scratch (allocated once).
	readonly uint[] _keysA, _keysB;
	readonly int[] _idxA, _idxB;
	readonly int[] _radixCounts = new int[256];

	public long StepIndex { get; private set; }
	public int AliveCount { get; private set; }
	/// <summary>Highest used slot + 1. Live particles are in slots [0, HighWater) with Alive == 1.</summary>
	public int HighWater => _highWater;
	public long DroppedForCapacity { get; private set; }
	public long TotalEmitted => _nextId;
	/// <summary>Set by <see cref="GpuParticleSystem"/>: CPU state of surviving particles is then stale.</summary>
	public bool SimulatedOnGpu { get; internal set; }

	public ReadOnlySpan<ParticleState> States => _state.AsSpan( 0, _highWater );
	internal ParticleState[] StateArray => _state;
	public ReadOnlySpan<uint> Ids => _ids.AsSpan( 0, _highWater );
	public ReadOnlySpan<int> ChangedSlots => _changed.AsSpan( 0, _changedCount );
	internal Vector3[]? History => _history;
	public double Time => StepIndex * (double)Settings.FixedDeltaTime;

	public ParticleSystem( ParticleSystemSettings settings )
	{
		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		Settings = settings;
		int n = settings.Capacity;
		_state = new ParticleState[n];
		_ids = new uint[n];
		_deathStep = new long[n];
		_lifeSeconds = new float[n];
		_freeStack = new int[n];
		_changed = new int[n];
		_changedStamp = new long[n];
		Array.Fill( _changedStamp, -1 );
		// Free stack pops lowest slot first.
		for ( int i = 0; i < n; i++ ) _freeStack[i] = n - 1 - i;
		_freeCount = n;
		if ( settings.StrandPoints > 0 ) _history = new Vector3[(long)n * settings.StrandPoints];
		_keysA = new uint[n]; _keysB = new uint[n]; _idxA = new int[n]; _idxB = new int[n];
		for ( int i = 0; i < n; i++ ) _state[i].Rotation = Quaternion.Identity;
	}

	public ParticleEmitter AddEmitter( ParticleEmitter emitter )
	{
		ArgumentNullException.ThrowIfNull( emitter );
		if ( _emitters.Contains( emitter ) ) throw new ArgumentException( "Emitter already added." );
		emitter.Validate( _emitters.Count );
		emitter.Rng = ParticleRng.For( Settings.Seed, _emitters.Count );
		emitter.RateRemainder = 0;
		_emitters.Add( emitter );
		return emitter;
	}

	public bool IsAlive( int slot ) => (uint)slot < (uint)_highWater && _state[slot].Alive != 0;
	public uint IdOf( int slot ) => _ids[slot];
	public float LifetimeOf( int slot ) => _lifeSeconds[slot];

	/// <summary>One fixed step on the CPU.</summary>
	public void Step()
	{
		if ( SimulatedOnGpu ) throw new InvalidOperationException( "This system is driven by a GpuParticleSystem; call GpuParticleSystem.Step." );
		BeginStep();
		SimulateCpu();
		EndStep();
	}

	public void Step( int count )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ) );
		for ( int i = 0; i < count; i++ ) Step();
	}

	/// <summary>Expire and emit for the current step. Deterministic and independent of positions,
	/// so the GPU path mirrors it on the CPU without readback.</summary>
	internal void BeginStep()
	{
		Settings.Validate();
		_changedCount = 0;
		long step = StepIndex;
		for ( int s = 0; s < _highWater; s++ )
		{
			if ( _state[s].Alive == 0 || step < _deathStep[s] ) continue;
			_state[s].Alive = 0;
			_state[s].Velocity = Vector3.Zero;
			_freeStack[_freeCount++] = s;
			AliveCount--;
			MarkChanged( s );
		}
		// Keep the free stack popping the lowest slot so slot reuse is compact and deterministic.
		if ( _changedCount > 0 ) SortFreeStackDescending();
		while ( _highWater > 0 && _state[_highWater - 1].Alive == 0 ) _highWater--;

		float dt = Settings.FixedDeltaTime;
		for ( int e = 0; e < _emitters.Count; e++ )
		{
			var em = _emitters[e];
			em.Validate( e );
			if ( !em.Enabled ) continue;
			int count = 0;
			foreach ( var b in em.Bursts ) if ( b.Step == step ) count += b.Count;
			em.RateRemainder += em.Rate * (double)dt;
			int fromRate = (int)Math.Floor( em.RateRemainder );
			em.RateRemainder -= fromRate;
			count += fromRate;
			for ( int i = 0; i < count; i++ ) Spawn( em );
		}
	}

	void SortFreeStackDescending()
	{
		// Insertion-friendly: stack is mostly sorted. Array.Sort on int span does not allocate.
		var span = _freeStack.AsSpan( 0, _freeCount );
		span.Sort();
		span.Reverse();
	}

	void MarkChanged( int slot )
	{
		if ( _changedStamp[slot] == StepIndex ) return;
		_changedStamp[slot] = StepIndex;
		_changed[_changedCount++] = slot;
	}

	void Spawn( ParticleEmitter em )
	{
		ref var rng = ref em.Rng;
		// Always draw the same number of random values, so a full pool does not shift later streams.
		Vector3 pos = em.Position, normal = em.Direction;
		switch ( em.Shape )
		{
			case ParticleEmitterShape.Area:
			{
				float u = rng.NextFloat() * 2 - 1, v = rng.NextFloat() * 2 - 1;
				pos = em.Position + em.AreaU * u + em.AreaV * v;
				var n = Vector3.Normalize( Vector3.Cross( em.AreaU, em.AreaV ) );
				if ( em.UseSurfaceNormal ) normal = n;
				break;
			}
			case ParticleEmitterShape.MeshSurface:
			{
				var mesh = em.Mesh!;
				float r = rng.NextFloat() * mesh.TotalArea;
				int tri = Array.BinarySearch( mesh.AreaCdf, r );
				if ( tri < 0 ) tri = ~tri;
				tri = Math.Min( tri, mesh.TriangleCount - 1 );
				// Skip zero-area triangles that share a CDF value with the previous one.
				while ( tri > 0 && mesh.AreaCdf[tri] == mesh.AreaCdf[tri - 1] && r < mesh.AreaCdf[tri - 1] ) tri--;
				float s1 = rng.NextFloat(), s2 = rng.NextFloat();
				if ( s1 + s2 > 1 ) { s1 = 1 - s1; s2 = 1 - s2; }
				mesh.GetTriangle( tri, out var a, out var b, out var c );
				pos = a + (b - a) * s1 + (c - a) * s2;
				var n = Vector3.Cross( b - a, c - a );
				if ( em.UseSurfaceNormal && n.LengthSquared() > 0 ) normal = Vector3.Normalize( n );
				break;
			}
			case ParticleEmitterShape.Box:
			{
				float x = rng.NextFloat() * 2 - 1, y = rng.NextFloat() * 2 - 1, z = rng.NextFloat() * 2 - 1;
				pos = em.Position + em.BoxHalfExtents * new Vector3( x, y, z );
				break;
			}
			case ParticleEmitterShape.Sphere:
			{
				var dir = RandomUnit( ref rng );
				float rad = em.SphereRadius * MathF.Cbrt( rng.NextFloat() );
				pos = em.Position + dir * rad;
				break;
			}
		}
		var launch = normal.LengthSquared() > 1e-12f ? Vector3.Normalize( normal ) : new Vector3( 0, 0, 1 );
		launch = RandomInCone( ref rng, launch, em.SpreadDegrees * (MathF.PI / 180f) );
		float speed = em.Speed * (1f + em.SpeedJitter * (rng.NextFloat() * 2 - 1));
		float life = rng.Range( em.LifetimeMin, em.LifetimeMax );
		float size = rng.Range( em.SizeMin, em.SizeMax );
		var spinAxis = RandomUnit( ref rng );
		float spin = rng.NextFloat() * em.AngularSpeedMax;
		var rotAxis = RandomUnit( ref rng );
		float rotAngle = rng.NextFloat() * MathF.PI * 2;

		if ( _freeCount == 0 ) { DroppedForCapacity++; return; }
		int slot = _freeStack[--_freeCount];
		if ( slot + 1 > _highWater ) _highWater = slot + 1;
		ref var s = ref _state[slot];
		s.Position = pos;
		s.Velocity = em.BaseVelocity + launch * speed;
		s.Age = 0;
		s.Alive = 1;
		s.Size = size;
		s.AngularVelocity = spinAxis * spin;
		s.Rotation = em.AngularSpeedMax > 0 ? Quaternion.CreateFromAxisAngle( rotAxis, rotAngle ) : Quaternion.Identity;
		_ids[slot] = _nextId++;
		_lifeSeconds[slot] = life;
		long lifeSteps = Math.Max( 1L, (long)Math.Ceiling( life / (double)Settings.FixedDeltaTime - 1e-6 ) );
		_deathStep[slot] = StepIndex + lifeSteps;
		AliveCount++;
		if ( _history is not null )
		{
			int k = Settings.StrandPoints;
			_history.AsSpan( slot * k, k ).Fill( pos );
		}
		MarkChanged( slot );
	}

	static Vector3 RandomUnit( ref ParticleRng rng )
	{
		float z = rng.NextFloat() * 2 - 1;
		float a = rng.NextFloat() * MathF.PI * 2;
		float r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		return new Vector3( r * MathF.Cos( a ), r * MathF.Sin( a ), z );
	}

	static Vector3 RandomInCone( ref ParticleRng rng, Vector3 axis, float halfAngle )
	{
		float cosMax = MathF.Cos( halfAngle );
		float z = 1 - rng.NextFloat() * (1 - cosMax);
		float a = rng.NextFloat() * MathF.PI * 2;
		if ( halfAngle <= 0 ) return axis;
		float r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		var helper = MathF.Abs( axis.Z ) < 0.9f ? new Vector3( 0, 0, 1 ) : new Vector3( 1, 0, 0 );
		var t = Vector3.Normalize( Vector3.Cross( helper, axis ) );
		var b = Vector3.Cross( axis, t );
		return Vector3.Normalize( t * (r * MathF.Cos( a )) + b * (r * MathF.Sin( a )) + axis * z );
	}

	void SimulateCpu()
	{
		var st = Settings;
		int sub = st.Substeps;
		float h = st.FixedDeltaTime / sub;
		float time0 = (float)(StepIndex * (double)st.FixedDeltaTime);
		var p = new SimParams( st );
		var mesh = st.CollisionMesh;
		for ( int s = 0; s < _highWater; s++ )
		{
			ref var ps = ref _state[s];
			if ( ps.Alive == 0 ) continue;
			for ( int k = 0; k < sub; k++ )
				Integrate( ref ps, h, time0 + k * h, in p, mesh );
		}
	}

	internal void EndStep()
	{
		if ( _history is not null && !SimulatedOnGpu )
		{
			int k = Settings.StrandPoints;
			int head = (int)(StepIndex % k);
			for ( int s = 0; s < _highWater; s++ )
				if ( _state[s].Alive != 0 ) _history[s * k + head] = _state[s].Position;
		}
		StepIndex++;
	}

	internal readonly struct SimParams
	{
		public readonly Vector3 Gravity, Wind, VortexCenter, VortexAxis, GroundNormal;
		public readonly float Drag, TurbStrength, TurbFreq, TurbSpeed, VortexStrength, VortexRadius2, GroundOffset, Radius, Restitution, Friction;
		public readonly bool Ground;
		public readonly uint Seed;
		public SimParams( ParticleSystemSettings s )
		{
			Gravity = s.Gravity; Wind = s.Wind; Drag = s.Drag;
			TurbStrength = s.TurbulenceStrength; TurbFreq = s.TurbulenceFrequency; TurbSpeed = s.TurbulenceSpeed;
			VortexStrength = s.VortexStrength; VortexCenter = s.VortexCenter;
			VortexAxis = s.VortexAxis.LengthSquared() > 0 ? Vector3.Normalize( s.VortexAxis ) : new Vector3( 0, 0, 1 );
			VortexRadius2 = s.VortexRadius * s.VortexRadius;
			Ground = s.GroundEnabled;
			GroundNormal = s.GroundNormal.LengthSquared() > 0 ? Vector3.Normalize( s.GroundNormal ) : new Vector3( 0, 0, 1 );
			GroundOffset = s.GroundOffset; Radius = s.CollisionRadius; Restitution = s.Restitution; Friction = s.Friction;
			Seed = s.Seed;
		}
	}

	/// <summary>One substep for one particle. GLSL mirror: GpuParticleSystem.SimSrc integrate().</summary>
	internal static void Integrate( ref ParticleState ps, float h, float time, in SimParams p, ParticleMesh? mesh )
	{
		Vector3 pos = ps.Position, vel = ps.Velocity;
		Vector3 acc = p.Gravity;
		if ( p.TurbStrength > 0 )
			acc += ParticleMath.Curl( pos * p.TurbFreq + new Vector3( 0, 0, time * p.TurbSpeed ), p.Seed ) * p.TurbStrength;
		if ( p.VortexStrength != 0 )
		{
			var r = pos - p.VortexCenter;
			var rp = r - p.VortexAxis * Vector3.Dot( r, p.VortexAxis );
			float d2 = Vector3.Dot( rp, rp );
			acc += Vector3.Cross( p.VortexAxis, rp ) * (p.VortexStrength / (d2 + p.VortexRadius2));
		}
		// Semi-implicit linear drag toward the wind velocity.
		vel = (vel + (acc + p.Wind * p.Drag) * h) / (1f + p.Drag * h);
		Vector3 next = pos + vel * h;

		if ( mesh is not null && mesh.Raycast( pos, next, out float t, out var n ) )
		{
			var hit = pos + (next - pos) * t;
			next = hit + n * p.Radius;
			Respond( ref vel, n, p.Restitution, p.Friction );
		}
		if ( p.Ground )
		{
			float d = Vector3.Dot( p.GroundNormal, next ) - p.GroundOffset - p.Radius;
			if ( d < 0 )
			{
				next -= p.GroundNormal * d;
				Respond( ref vel, p.GroundNormal, p.Restitution, p.Friction );
			}
		}
		ps.Position = next;
		ps.Velocity = vel;
		ps.Age += h;
		var w = ps.AngularVelocity;
		if ( w.X != 0 || w.Y != 0 || w.Z != 0 )
		{
			var q = ps.Rotation;
			var dq = ParticleMath.Mul( new Quaternion( w.X, w.Y, w.Z, 0 ), q );
			q = new Quaternion( q.X + dq.X * (0.5f * h), q.Y + dq.Y * (0.5f * h), q.Z + dq.Z * (0.5f * h), q.W + dq.W * (0.5f * h) );
			float len = MathF.Sqrt( q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W );
			ps.Rotation = new Quaternion( q.X / len, q.Y / len, q.Z / len, q.W / len );
		}
	}

	static void Respond( ref Vector3 vel, Vector3 n, float restitution, float friction )
	{
		float vn = Vector3.Dot( vel, n );
		if ( vn >= 0 ) return;
		var vt = vel - n * vn;
		vel = vt * (1f - friction) - n * (vn * restitution);
	}

	// ---- Render data (CPU path) ----

	void ThrowIfGpu()
	{
		if ( SimulatedOnGpu ) throw new InvalidOperationException( "CPU render data needs CPU positions; a GPU-driven system keeps them in the GPU buffer (use GpuParticleSystem.Download for verification only)." );
	}

	/// <summary>
	/// Live slots ordered back-to-front (farthest first) along <paramref name="viewForward"/> from
	/// <paramref name="cameraPosition"/>. Stable LSD radix sort into preallocated buffers: no allocation.
	/// The returned span is valid until the next call.
	/// </summary>
	public ReadOnlySpan<int> SortBackToFront( Vector3 cameraPosition, Vector3 viewForward )
	{
		if ( !ParticleMath.Finite( cameraPosition ) || !ParticleMath.Finite( viewForward ) || viewForward.LengthSquared() < 1e-20f )
			throw new ArgumentException( "Camera position/forward must be finite and forward non-zero." );
		ThrowIfGpu();
		var fwd = Vector3.Normalize( viewForward );
		int n = 0;
		for ( int s = 0; s < _highWater; s++ )
		{
			if ( _state[s].Alive == 0 ) continue;
			float depth = Vector3.Dot( _state[s].Position - cameraPosition, fwd );
			uint bits = BitConverter.SingleToUInt32Bits( depth );
			uint ascending = (bits & 0x80000000u) != 0 ? ~bits : bits | 0x80000000u;
			_keysA[n] = ~ascending; // descending depth
			_idxA[n] = s;
			n++;
		}
		uint[] ka = _keysA, kb = _keysB;
		int[] ia = _idxA, ib = _idxB;
		var counts = _radixCounts;
		for ( int shift = 0; shift < 32; shift += 8 )
		{
			Array.Clear( counts );
			for ( int i = 0; i < n; i++ ) counts[(ka[i] >> shift) & 0xFF]++;
			int sum = 0;
			for ( int b = 0; b < 256; b++ ) { int c = counts[b]; counts[b] = sum; sum += c; }
			for ( int i = 0; i < n; i++ )
			{
				int dst = counts[(ka[i] >> shift) & 0xFF]++;
				kb[dst] = ka[i]; ib[dst] = ia[i];
			}
			(ka, kb) = (kb, ka); (ia, ib) = (ib, ia);
		}
		// Four passes: result is back in the A buffers.
		return _idxA.AsSpan( 0, n );
	}

	/// <summary>Writes billboard records for <paramref name="order"/> (e.g. from <see cref="SortBackToFront"/>). Returns count written.</summary>
	public int WriteBillboards( ReadOnlySpan<int> order, Span<ParticleBillboard> destination )
	{
		if ( destination.Length < order.Length ) throw new ArgumentException( "Destination too small." );
		ThrowIfGpu();
		for ( int i = 0; i < order.Length; i++ )
		{
			int s = order[i];
			if ( !IsAlive( s ) ) throw new ArgumentException( $"Slot {s} is not alive." );
			ref readonly var p = ref _state[s];
			destination[i] = new ParticleBillboard
			{
				Position = p.Position, Size = p.Size, Velocity = p.Velocity,
				NormalizedAge = Math.Clamp( p.Age / _lifeSeconds[s], 0f, 1f )
			};
		}
		return order.Length;
	}

	/// <summary>Per-particle model matrix (scale·rotation·translation, System.Numerics row-vector
	/// convention: rows are scaled basis vectors then origin), the same packing as
	/// <see cref="MarionettePlayer.ToGlMatrix"/>; upload with transpose=false.</summary>
	public static Matrix4x4 InstanceMatrix( in ParticleState p ) =>
		Matrix4x4.CreateScale( p.Size ) * Matrix4x4.CreateFromQuaternion( p.Rotation ) * Matrix4x4.CreateTranslation( p.Position );

	public int WriteInstanceMatrices( ReadOnlySpan<int> order, Span<Matrix4x4> destination )
	{
		if ( destination.Length < order.Length ) throw new ArgumentException( "Destination too small." );
		ThrowIfGpu();
		for ( int i = 0; i < order.Length; i++ )
		{
			int s = order[i];
			if ( !IsAlive( s ) ) throw new ArgumentException( $"Slot {s} is not alive." );
			destination[i] = InstanceMatrix( _state[s] );
		}
		return order.Length;
	}

	/// <summary>Strand type: the slot's trailing points, newest first. Points before birth repeat the spawn position.</summary>
	public int WriteStrand( int slot, Span<Vector3> destination )
	{
		if ( _history is null ) throw new InvalidOperationException( "StrandPoints is 0; strands are disabled." );
		ThrowIfGpu();
		if ( !IsAlive( slot ) ) throw new ArgumentException( $"Slot {slot} is not alive." );
		int k = Settings.StrandPoints;
		if ( destination.Length < k ) throw new ArgumentException( "Destination too small." );
		if ( StepIndex == 0 ) { destination[..k].Fill( _state[slot].Position ); return k; }
		int newest = (int)((StepIndex - 1) % k);
		for ( int i = 0; i < k; i++ ) destination[i] = _history[slot * k + ((newest - i) % k + k) % k];
		return k;
	}

	/// <summary>Fire a timeline cue as particles. This is the entry point the
	/// desktop viewport's timeline dispatcher calls when it crosses a
	/// <c>RigMark</c> or a <c>RigEvent</c> start: pass the cue's name and the
	/// world position it belongs to (a bone's world position for a bone cue).
	/// Burst presets (splash, mist, sparks, frost) fire once, at the current
	/// step — a step-0 burst on an already-running system would never be seen.
	/// Rate presets (bubbles, dust) start looping; the caller stops them by
	/// setting the returned emitter's <c>Enabled</c> to false at the event end.
	/// Returns null when the cue names no preset, so an unrelated mark (a
	/// camera cut, a prop attach) never becomes an explosion by accident.</summary>
	public ParticleEmitter? TriggerCue( string cueName, Vector3 origin, Vector3? direction = null )
	{
		if ( ParticleEmitterPresets.ResolveName( cueName ) is not { } name ) return null;
		var emitter = ParticleEmitterPresets.Create( name, origin, direction );
		if ( emitter.Bursts.Count > 0 )
			emitter.Bursts = new List<ParticleBurst> { new( (int)StepIndex, emitter.Bursts[0].Count ) };
		return AddEmitter( emitter );
	}
}

/// <summary>Lane N built-in particle emitter presets. Units are kernel inches
/// and seconds, Z-up, like the rest of <see cref="ParticleSystem"/>; a preset
/// is an emitter shape plus launch numbers, and <see cref="SettingsFor"/> is
/// the gravity/wind/drag/turbulence that makes it read (bubbles rise, mist
/// hangs). Settings are per effect system: a preset's gravity override must
/// not be poured into a system that is also simulating something else.
/// Cue names resolve by <see cref="ResolveName"/>: an exact preset name, an
/// <c>fx:</c>/<c>vfx:</c>-prefixed one, or a name that carries the preset as
/// a whole underscore-separated word (<c>footstep_water_splash</c>).</summary>
public static class ParticleEmitterPresets
{
	public static readonly string[] Names =
		{ "water_splash", "bubbles", "blood_mist", "sparks", "dust_motes", "breath_frost" };

	public static string? ResolveName( string cueName )
	{
		if ( string.IsNullOrWhiteSpace( cueName ) ) return null;
		var n = cueName.Trim().ToLowerInvariant();
		foreach ( var p in new[] { "fx:", "fx_", "vfx:", "vfx_", "particle:", "particle_" } )
			if ( n.StartsWith( p ) ) { n = n[p.Length..]; break; }
		var flat = n.Replace( '-', '_' ).Replace( ':', '_' ).Replace( ' ', '_' ).Replace( '/', '_' );
		foreach ( var name in Names )
			if ( flat == name || flat.StartsWith( name + "_" ) || flat.EndsWith( "_" + name ) || flat.Contains( "_" + name + "_" ) )
				return name;
		return null;
	}

	/// <summary>Fresh emitter for a preset at <paramref name="origin"/>. A
	/// non-zero <paramref name="direction"/> re-aims the launch (a character's
	/// facing for breath, a surface normal for a splash); otherwise the
	/// preset's own direction stands. Burst presets carry one burst at step 0
	/// for a fresh system; <see cref="ParticleSystem.TriggerCue"/> retimes it.</summary>
	public static ParticleEmitter Create( string name, Vector3 origin, Vector3? direction = null )
	{
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var em = new ParticleEmitter { Position = origin };
		switch ( key )
		{
			case "water_splash":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 260f; em.SpeedJitter = 0.4f; em.SpreadDegrees = 35f;
				em.LifetimeMin = 0.4f; em.LifetimeMax = 0.9f; em.SizeMin = 1.5f; em.SizeMax = 3.5f;
				em.AngularSpeedMax = 6f; em.Bursts.Add( new ParticleBurst( 0, 120 ) );
				break;
			case "bubbles":
				em.Shape = ParticleEmitterShape.Sphere; em.SphereRadius = 6f; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 30f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 20f;
				em.LifetimeMin = 1.5f; em.LifetimeMax = 3.5f; em.SizeMin = 0.6f; em.SizeMax = 1.6f;
				em.Rate = 40f;
				break;
			case "blood_mist":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 60f; em.SpeedJitter = 0.6f; em.SpreadDegrees = 70f;
				em.LifetimeMin = 0.8f; em.LifetimeMax = 1.8f; em.SizeMin = 2f; em.SizeMax = 5f;
				em.Bursts.Add( new ParticleBurst( 0, 90 ) );
				break;
			case "sparks":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 420f; em.SpeedJitter = 0.5f; em.SpreadDegrees = 50f;
				em.LifetimeMin = 0.2f; em.LifetimeMax = 0.6f; em.SizeMin = 0.3f; em.SizeMax = 0.9f;
				em.AngularSpeedMax = 20f; em.Bursts.Add( new ParticleBurst( 0, 70 ) );
				break;
			case "dust_motes":
				em.Shape = ParticleEmitterShape.Box; em.BoxHalfExtents = new Vector3( 30f, 30f, 10f ); em.Direction = new Vector3( 0, 0, 1 );
				em.Speed = 5f; em.SpeedJitter = 0.8f; em.SpreadDegrees = 180f;
				em.LifetimeMin = 3f; em.LifetimeMax = 6f; em.SizeMin = 0.2f; em.SizeMax = 0.6f;
				em.Rate = 12f;
				break;
			case "breath_frost":
				em.Shape = ParticleEmitterShape.Point; em.Direction = new Vector3( 1, 0, 0 );
				em.Speed = 70f; em.SpeedJitter = 0.3f; em.SpreadDegrees = 12f;
				em.LifetimeMin = 0.5f; em.LifetimeMax = 1.1f; em.SizeMin = 1f; em.SizeMax = 2.5f;
				em.Bursts.Add( new ParticleBurst( 0, 45 ) );
				break;
		}
		if ( direction is { } d && d.LengthSquared() > 1e-12f ) em.Direction = d;
		return em;
	}

	/// <summary>A copy of <paramref name="baseSettings"/> with this preset's
	/// medium applied. The base is never mutated: two presets on one system
	/// would otherwise fight over one gravity.</summary>
	public static ParticleSystemSettings SettingsFor( string name, ParticleSystemSettings baseSettings )
	{
		ArgumentNullException.ThrowIfNull( baseSettings );
		var key = ResolveName( name ) ?? throw new ArgumentException( $"Unknown particle preset '{name}'. Presets: {string.Join( ", ", Names )}." );
		var s = Clone( baseSettings );
		switch ( key )
		{
			case "water_splash": s.Drag = 0.1f; break;
			case "bubbles":
				s.Gravity = new Vector3( 0, 0, 60f ); s.Drag = 0.8f;
				s.TurbulenceStrength = 8f; s.TurbulenceFrequency = 0.3f;
				break;
			case "blood_mist":
				s.Gravity = new Vector3( 0, 0, -120f ); s.Drag = 2.5f;
				s.TurbulenceStrength = 30f; s.TurbulenceFrequency = 0.2f;
				break;
			case "sparks": s.Drag = 0.05f; break;
			case "dust_motes":
				s.Gravity = new Vector3( 0, 0, -8f ); s.Drag = 0.5f;
				s.TurbulenceStrength = 6f; s.TurbulenceFrequency = 0.15f; s.Wind = new Vector3( 4f, 2f, 0f );
				break;
			case "breath_frost":
				s.Drag = 1.5f; s.TurbulenceStrength = 10f; s.TurbulenceFrequency = 0.4f;
				break;
		}
		return s;
	}

	static ParticleSystemSettings Clone( ParticleSystemSettings s ) => new()
	{
		Capacity = s.Capacity, Seed = s.Seed, FixedDeltaTime = s.FixedDeltaTime, Substeps = s.Substeps,
		Gravity = s.Gravity, Wind = s.Wind, Drag = s.Drag,
		TurbulenceStrength = s.TurbulenceStrength, TurbulenceFrequency = s.TurbulenceFrequency, TurbulenceSpeed = s.TurbulenceSpeed,
		VortexStrength = s.VortexStrength, VortexCenter = s.VortexCenter, VortexAxis = s.VortexAxis, VortexRadius = s.VortexRadius,
		GroundEnabled = s.GroundEnabled, GroundNormal = s.GroundNormal, GroundOffset = s.GroundOffset,
		CollisionMesh = s.CollisionMesh, CollisionRadius = s.CollisionRadius, Restitution = s.Restitution, Friction = s.Friction,
		RenderType = s.RenderType, StrandPoints = s.StrandPoints,
	};
}
