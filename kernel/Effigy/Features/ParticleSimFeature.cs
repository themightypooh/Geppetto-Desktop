using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Script: <c>add ParticleSim name=Sparks emitter=Emitter count=200 seed=12345
/// life=2.5 velocity=0,0,20 velspread=8 gravity=0,0,-9.8 drag=0.4
/// substeps=2 dt=0.008333 frames=60 render=points</c>.
///
/// A particle BAKE, not a live simulation — the same pattern Geppetto already
/// uses for cloth drape (a settle, not a live sim) and soft-bone wobble
/// (baked frames). The solver runs N frames at a fixed timestep and the
/// result becomes static geometry in the document, so it is deterministic,
/// testable headlessly, and shippable as baked geometry.
///
/// State per particle is Verlet, copied from Rig/SoftBone: position + previous
/// position, no velocity array. Velocity when needed (drag, bounce, sleep)
/// is (pos - prev) / dt. One frame = substeps steps of size dt, so total
/// simulated time is frames * substeps * dt, independent of any viewport
/// frame rate. Leftover time is dropped, never stretched.
///
/// Emission, in order of precedence:
/// - origin=x,y,z: every particle starts at that point (the analytic-test
///   case: one particle, known start, gravity only, known landing z).
/// - points=x,y,z;x,y,z: particles start at the listed points, in order.
/// - emitter bodies: area-weighted surface samples from StrandSolver.Scatter
///   (deterministic for a fixed seed), direction = sampled face normal.
/// Burst vs rate: rate=0 emits everything at frame 0; rate=N emits N per
/// frame until count is reached. frames=0 is the emit-only edge case: all
/// particles are emitted and no stepping happens, regardless of rate.
/// Initial velocity = velocity + a uniform random offset of magnitude at
/// most velspread per component + normalvelocity * sampled face normal
/// (0 for origin/points emission, where there is no face normal).
///
/// Forces per substep, in order: gravity + wind + attractor/repeller +
/// vortex + turbulence (a gradient gust field from GeoNodes Noise.Fbm —
/// labelled as such, it is NOT divergence-free curl noise). Then drag:
/// v *= exp(-drag*dt), applied by moving prev toward pos, which is drag
/// without a velocity array. Death: age &gt; life removes the particle,
/// unless respawn=on, in which case it re-emits from its start point with
/// a fresh velocity draw.
///
/// Collision: collider bodies are triangulated into a uniform-grid
/// collider (same cell pattern as StrandSolver.SurfaceCollider, but this
/// one returns the hit normal and a hit flag, which SurfaceCollider does
/// not expose, and it collides every point rather than skipping index 0).
/// Per substep, after integration, a penetrating particle is pushed out
/// to colliderradius along the face normal, then the Verlet state gets
/// the response: bounce (v_n = -restitution * v_n, v_t *= 1 - friction),
/// stick (v = 0, and the particle can then sleep), slide (v_n = 0 only).
/// Tunnelling guard: if |v| * dt &gt; colliderradius, that particle's step
/// is subdivided (local substeps, capped at 8) instead of jumping through
/// a thin floor.
///
/// Sleep: a particle whose speed stays below sleepspeed for sleepframes
/// consecutive frames stops being integrated (prev pinned to pos) until
/// a non-gravity force larger than 2.0 acts on it. Default sleepspeed=0
/// means sleep is off. Sleep is a flag check, not a solver change.
///
/// Separation is NOT a fluid solver: one uniform-grid pass per frame,
/// each particle checks its own and neighbouring cells, pushes apart
/// from at most 8 neighbours closer than separation by half the overlap
/// each. It stops instanced grains visibly interpenetrating; it does not
/// conserve momentum and must not be sold as SPH.
///
/// Spawn-on-hit: onhit names a child output and onhitcount gives children
/// per hit. A particle records at most its FIRST collision as a hit event.
/// After the main bake, a child bake runs onhitcount particles per hit
/// (total spawned capped at max(100, 2x the parent count), so a bouncing
/// particle cannot fork-bomb the document) from the hit points, outward along the
/// hit normal at onhitspeed, with life onhitlife, gravity and drag only.
/// Children merge into the same output body.
///
/// Output (render=): points = one small quad per alive particle at its
/// final position (cheapest, the high-count escape hatch); instances =
/// copies of the source body at final positions; trails = one polyline
/// per particle through its per-frame samples, rendered with
/// StrandSolver.BuildCards. Child particles merge into the same output.
/// Counts above 10000 warn: the Scatter cap is 100000, but that is a cap,
/// not a recommendation — a 100k-instance bake is slow to render and save.
/// </summary>
public sealed class ParticleSimFeature : Feature
{
	public override string TypeName => "ParticleSim";

	public readonly BodySelectionParam Emitter = new( "Emitter" );
	public readonly StringParam Origin = new( "Origin", "" );
	public readonly StringParam Source = new( "Source", "" );
	public readonly BodySelectionParam Collider = new( "Collider" );
	public readonly ChoiceParam Render = new( "Render", new[] { "points", "instances", "trails" } );
	public readonly IntParam Count = new( "Count", 200, 1, 100000 );
	public readonly IntParam Seed = new( "Seed", 12345, 0, 1000000 ) { Slider = false };
	public readonly FloatParam Life = new( "Life", 2.5f, 0.01f, 1000f, "s" );
	public readonly FloatParam LifeJitter = new( "Life jitter", 0f, 0f, 1000f, "s" );
	public readonly Vec3Param Velocity = new( "Velocity", new Vec3( 0, 0, 20 ) );
	public readonly FloatParam VelSpread = new( "Vel spread", 8f, 0f, 1000f, "u/s" );
	public readonly FloatParam NormalVelocity = new( "Normal velocity", 0f, -1000f, 1000f, "u/s" );
	public readonly Vec3Param Gravity = new( "Gravity", new Vec3( 0, 0, -9.8f ) );
	public readonly Vec3Param Wind = new( "Wind", Vec3.Zero );
	public readonly FloatParam Drag = new( "Drag", 0.4f, 0f, 100f, "1/s" );
	public readonly IntParam Substeps = new( "Substeps", 2, 1, 16 );
	public readonly FloatParam Dt = new( "Dt", 0.008333f, 0.0001f, 1f, "s" );
	public readonly IntParam Frames = new( "Frames", 60, 0, 100000 );
	public readonly IntParam Rate = new( "Rate", 0, 0, 100000 );
	public readonly BoolParam Respawn = new( "Respawn", false );
	public readonly ChoiceParam CollideMode = new( "Collide mode", new[] { "bounce", "stick", "slide" } );
	public readonly FloatParam Restitution = new( "Restitution", 0.6f, 0f, 1f );
	public readonly FloatParam Friction = new( "Friction", 0.3f, 0f, 1f );
	public readonly FloatParam ColliderRadius = new( "Collider radius", 0.2f, 0f, 100f, "u" );
	public readonly Vec3Param AttractorPoint = new( "Attractor point", Vec3.Zero );
	public readonly FloatParam AttractorStrength = new( "Attractor strength", 0f, -100000f, 100000f );
	public readonly FloatParam AttractorFalloff = new( "Attractor falloff", 2f, 0.01f, 10000f, "u" );
	public readonly Vec3Param VortexCentre = new( "Vortex centre", Vec3.Zero );
	public readonly Vec3Param VortexAxis = new( "Vortex axis", Vec3.Zero );
	public readonly FloatParam VortexStrength = new( "Vortex strength", 0f, -100000f, 100000f );
	public readonly FloatParam Turbulence = new( "Turbulence", 0f, 0f, 1000f );
	public readonly FloatParam TurbScale = new( "Turb scale", 0.5f, 0.001f, 100f );
	public readonly IntParam TurbOctaves = new( "Turb octaves", 3, 0, 4 );
	public readonly IntParam TurbSeed = new( "Turb seed", 99, 0, 1000000 ) { Slider = false };
	public readonly FloatParam WindDrift = new( "Wind drift", 0f, 0f, 100f );
	public readonly FloatParam SleepSpeed = new( "Sleep speed", 0f, 0f, 1000f, "u/s" );
	public readonly IntParam SleepFrames = new( "Sleep frames", 5, 1, 10000 );
	public readonly FloatParam Separation = new( "Separation", 0f, 0f, 100f, "u" );
	public readonly StringParam OnHit = new( "On hit", "" );
	public readonly IntParam OnHitCount = new( "On hit count", 0, 0, 1000 );
	public readonly FloatParam OnHitSpeed = new( "On hit speed", 5f, 0f, 1000f, "u/s" );
	public readonly FloatParam OnHitLife = new( "On hit life", 0.5f, 0.01f, 1000f, "s" );
	public readonly FloatParam PointSize = new( "Point size", 0.5f, 0.01f, 100f, "u" );
	public readonly FloatParam TrailWidth = new( "Trail width", 0.3f, 0.01f, 100f, "u" );
	public readonly FloatParam TipRatio = new( "Tip ratio", 0.3f, 0f, 1f );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	// Script-assigned point list, same "x,y,z;x,y,z" form as Strands roots=.
	public List<Vec3> Points = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Emitter, Origin, Source, Collider, Render, Count, Seed, Life, LifeJitter,
		Velocity, VelSpread, NormalVelocity, Gravity, Wind, Drag, Substeps, Dt,
		Frames, Rate, Respawn, CollideMode, Restitution, Friction, ColliderRadius,
		AttractorPoint, AttractorStrength, AttractorFalloff, VortexCentre, VortexAxis,
		VortexStrength, Turbulence, TurbScale, TurbOctaves, TurbSeed, WindDrift,
		SleepSpeed, SleepFrames, Separation, OnHit, OnHitCount, OnHitSpeed, OnHitLife,
		PointSize, TrailWidth, TipRatio, Material,
	};

	sealed class Particle
	{
		public Vec3 Pos, Prev, Start, StartNormal;
		public float BirthTime, LifeSpan;
		public int SleepCount;
		public bool Sleeping, Dead, HitRecorded;
		public List<Vec3> Trail = new();
	}

	sealed class HitEvent
	{
		public Vec3 Point, Normal;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var count = Count.Clamped;
		var seed = Seed.Value;
		var dt = Dt.Clamped;
		var substeps = Substeps.Clamped;
		var frames = Frames.Clamped;
		var rng = new Random( seed );
		var useCollider = Collider.BodyIds.Count > 0;
		var collider = (ParticleCollider)null;
		var colliderMeshes = new List<PolyMesh>();

		if ( useCollider )
		{
			foreach ( var b in RequireBodies( ctx, Collider ) )
				if ( b.Mesh is not null && b.Mesh.VertexCount > 0 )
					colliderMeshes.Add( b.Mesh );
			if ( colliderMeshes.Count == 0 )
				Fail( "ParticleSim's collider bodies have no mesh.", "Collision needs real surface geometry.", "Pick bodies with geometry", "Or leave collider empty for no collision" );
			collider = ParticleCollider.Build( colliderMeshes, ColliderRadius.Clamped );
		}

		// --- emission points -------------------------------------------------
		var starts = new List<(Vec3 Point, Vec3 Normal)>();
		if ( !string.IsNullOrWhiteSpace( Origin.Value ) )
		{
			if ( !StrandSolver.TryParseVec3( Origin.Value, out var origin ) )
				FailOn( Origin.Label, $"'{Origin.Value}' is not a vector x,y,z.", "Origin is one point every particle starts from.", "Write it like origin=0,0,0" );
			for ( var i = 0; i < count; i++ )
				starts.Add( (origin, new Vec3( 0, 0, 1 )) );
		}
		else if ( Points.Count > 0 )
		{
			for ( var i = 0; i < count; i++ )
				starts.Add( (Points[i % Points.Count], new Vec3( 0, 0, 1 )) );
		}
		else
		{
			var emitters = RequireBodies( ctx, Emitter );
			var meshes = emitters.Where( b => b.Mesh is not null && b.Mesh.VertexCount > 0 ).Select( b => b.Mesh ).ToList();
			if ( meshes.Count == 0 )
				Fail( "ParticleSim needs an emitter.", "No emitter body with a mesh, no origin=, and no points= were given.", "Name an emitter body", "Or give origin=0,0,0" );
			starts.AddRange( StrandSolver.Scatter( meshes, count, seed ) );
			if ( starts.Count < count )
				Warn( $"ParticleSim sampled {starts.Count} of {count} emission points.", "The emitter surface had too little area for the rest.", "Lower count", "Use a larger emitter" );
		}

		PolyMesh sourceMesh = null;
		if ( Render.Value == "instances" )
		{
			if ( string.IsNullOrWhiteSpace( Source.Value ) )
				Fail( "ParticleSim render=instances needs a source.", "Instances are copies of a body; no source was named.", "Name a body above this feature as source=", "Or use render=points" );
			var src = ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, Source.Value, StringComparison.OrdinalIgnoreCase ) );
			if ( src?.Mesh is null )
				Fail( $"ParticleSim source '{Source.Value}' is not a body above it.", "Instances copy an existing body.", "Put the source body first", "Check the source name" );
			sourceMesh = src.Mesh;
		}

		var particles = new List<Particle>();
		var hits = new List<HitEvent>();
		var emitted = 0;
		var nextEmit = 0;
		var time = 0f;

		// Emit function: one particle from emission index i, born at time t.
		Particle MakeParticle( int i, float t )
		{
			var (pt, nrm) = starts[i % starts.Count];
			var vel = Velocity.Value
				+ new Vec3( (float)(rng.NextDouble() * 2 - 1) * VelSpread.Clamped,
					(float)(rng.NextDouble() * 2 - 1) * VelSpread.Clamped,
					(float)(rng.NextDouble() * 2 - 1) * VelSpread.Clamped )
				+ nrm * NormalVelocity.Clamped;
			var life = Math.Max( 0.01f, Life.Clamped + (float)(rng.NextDouble() * 2 - 1) * LifeJitter.Clamped );
			var p = new Particle { Pos = pt, Prev = pt - vel * dt, Start = pt, StartNormal = nrm, BirthTime = t, LifeSpan = life };
			p.Trail.Add( pt );
			return p;
		}

		void EmitFrame( float t )
		{
			if ( frames == 0 )
			{
				while ( nextEmit < count )
					{ particles.Add( MakeParticle( nextEmit, t ) ); nextEmit++; emitted++; }
				return;
			}
			if ( Rate.Clamped <= 0 )
			{
				while ( nextEmit < count )
					{ particles.Add( MakeParticle( nextEmit, t ) ); nextEmit++; emitted++; }
			}
			else
			{
				for ( var k = 0; k < Rate.Clamped && nextEmit < count; k++ )
					{ particles.Add( MakeParticle( nextEmit, t ) ); nextEmit++; emitted++; }
			}
		}

		EmitFrame( 0f );

		var slept = 0;
		var collided = 0;

		for ( var frame = 0; frame < frames; frame++ )
		{
			// Separation pass (positional, capped neighbours, not a fluid solve).
			if ( Separation.Clamped > 0f )
				ApplySeparation( particles, Separation.Clamped );

			for ( var s = 0; s < substeps; s++ )
			{
				time += dt;
				foreach ( var p in particles )
				{
					if ( p.Dead || p.Sleeping )
						continue;
					StepParticle( p, dt, rng, collider, hits, ref collided, time );
					if ( time - p.BirthTime > p.LifeSpan )
					{
						if ( Respawn.Value )
						{
							var fresh = MakeParticle( particles.IndexOf( p ), time );
							p.Pos = fresh.Pos; p.Prev = fresh.Prev; p.BirthTime = time; p.LifeSpan = fresh.LifeSpan;
							p.SleepCount = 0; p.Sleeping = false;
							p.Trail.Clear(); p.Trail.Add( fresh.Pos );
						}
						else
							p.Dead = true;
					}
				}
			}

			// Sleep check, once per frame on frame-end speeds.
			if ( SleepSpeed.Clamped > 0f )
			{
				foreach ( var p in particles )
				{
					if ( p.Dead || p.Sleeping )
						continue;
					var speed = (p.Pos - p.Prev).Length / Math.Max( dt, 1e-6f );
					if ( speed < SleepSpeed.Clamped )
					{
						if ( ++p.SleepCount >= SleepFrames.Clamped )
						{
							p.Sleeping = true; p.Prev = p.Pos; slept++;
						}
					}
					else
						p.SleepCount = 0;
				}
			}

			// Wake check: a sleeping particle wakes only on a non-gravity force.
			if ( slept > 0 )
			{
				foreach ( var p in particles )
				{
					if ( !p.Sleeping )
						continue;
					var accel = ForceAt( p.Pos, time, false );
					if ( accel.Length > 2f )
						{ p.Sleeping = false; p.SleepCount = 0; }
				}
			}

			foreach ( var p in particles )
				if ( !p.Dead )
					p.Trail.Add( p.Pos );

			EmitFrame( time );
		}

		var alive = particles.Where( p => !p.Dead ).ToList();
		if ( emitted < count )
			Warn( $"ParticleSim emitted {emitted} of {count} particles.", "Rate x frames did not reach count, or the emitter ran out of samples.", "Raise frames", "Lower count", "Use rate=0 to burst all at frame 0" );
		if ( count > 10000 && Render.Value == "instances" )
			Warn( $"ParticleSim is baking {count} instances.", "That is slow to render and save; points is the high-count escape hatch.", "Use render=points", "Lower count" );
		if ( slept > 0 )
			Warn( $"ParticleSim: {slept} particles slept.", "They stopped integrating after sitting below sleep speed for sleepframes frames.", "Raise sleep speed to sleep more", "Set sleep speed=0 to disable sleep" );

		// --- child bake: spawn-on-hit --------------------------------------
		var childParticles = new List<Particle>();
		if ( !string.IsNullOrWhiteSpace( OnHit.Value ) && OnHitCount.Clamped > 0 && hits.Count > 0 )
		{
			var cap = Math.Max( 100, 2 * count );
			var wanted = Math.Min( hits.Count * OnHitCount.Clamped, cap );
			var spawned = 0;
			var childRng = new Random( seed + 7919 );
			foreach ( var hit in hits )
			{
				for ( var k = 0; k < OnHitCount.Clamped && spawned < wanted; k++, spawned++ )
				{
					var vel = hit.Normal * OnHitSpeed.Clamped
						+ new Vec3( (float)(childRng.NextDouble() * 2 - 1) * OnHitSpeed.Clamped * 0.5f,
							(float)(childRng.NextDouble() * 2 - 1) * OnHitSpeed.Clamped * 0.5f,
							(float)(childRng.NextDouble() * 2 - 1) * OnHitSpeed.Clamped * 0.5f );
					var cp = new Particle { Pos = hit.Point, Prev = hit.Point - vel * dt, Start = hit.Point, StartNormal = hit.Normal, BirthTime = 0f, LifeSpan = OnHitLife.Clamped };
					cp.Trail.Add( hit.Point );
					childParticles.Add( cp );
				}
				if ( spawned >= wanted )
					break;
			}
			// Floor, not Ceiling: Ceiling simulates past onhitlife, which
			// kills every child on the last frame and leaves none alive.
			var childFrames = Math.Max( 1, (int)Math.Floor( OnHitLife.Clamped / (substeps * dt) ) );
			var ct = 0f;
			for ( var f = 0; f < childFrames; f++ )
			{
				for ( var s = 0; s < substeps; s++ )
				{
					ct += dt;
					foreach ( var p in childParticles )
					{
						if ( p.Dead )
							continue;
						// Children get gravity + drag only: no collider, no forces.
						var accel = Gravity.Value;
						var dragK = 1f - MathF.Exp( -Drag.Clamped * dt );
						var next = p.Pos + (p.Pos - p.Prev) * (1f - dragK) + accel * (dt * dt);
						p.Prev = p.Pos; p.Pos = next;
						if ( ct - p.BirthTime > p.LifeSpan )
							p.Dead = true;
					}
				}
				foreach ( var p in childParticles )
					if ( !p.Dead )
						p.Trail.Add( p.Pos );
			}
			if ( spawned >= cap && hits.Count * OnHitCount.Clamped > cap )
				Warn( $"ParticleSim spawn-on-hit capped at {cap} children.", "More hits asked for more children than the cap allows.", "Raise count to raise the cap", "Lower on hit count" );
		}

		var childAlive = childParticles.Where( p => !p.Dead ).ToList();

		// --- output ----------------------------------------------------------
		PolyMesh mesh;
		if ( Render.Value == "instances" )
		{
			mesh = new PolyMesh();
			foreach ( var p in alive.Concat( childAlive ) )
			{
				var inst = sourceMesh.Clone();
				MeshTransform.Apply( inst, new Xform( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), p.Pos ) );
				MeshTransform.Append( mesh, inst );
			}
			if ( mesh.VertexCount == 0 )
				Fail( "ParticleSim baked no instances.", "Every particle died before the end of the bake.", "Raise life", "Lower frames" );
		}
		else if ( Render.Value == "trails" )
		{
			var strands = alive.Concat( childAlive ).Select( p => p.Trail ).Where( t => t.Count >= 2 ).ToList();
			if ( strands.Count == 0 )
				Fail( "ParticleSim baked no trails.", "Every particle died before the end of the bake, or frames=0 left single-point trails.", "Raise life", "Raise frames" );
			mesh = StrandSolver.BuildCards( strands, TrailWidth.Clamped, TipRatio.Clamped, Material.Clamped );
		}
		else // points
		{
			mesh = new PolyMesh();
			var half = PointSize.Clamped * 0.5f;
			foreach ( var p in alive.Concat( childAlive ) )
			{
				var a = mesh.AddVertex( p.Pos + new Vec3( -half, -half, 0 ) );
				var b = mesh.AddVertex( p.Pos + new Vec3( half, -half, 0 ) );
				var c = mesh.AddVertex( p.Pos + new Vec3( half, half, 0 ) );
				var d = mesh.AddVertex( p.Pos + new Vec3( -half, half, 0 ) );
				mesh.AddFace( new[] { a, b, c, d }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, Material.Clamped );
			}
			if ( mesh.VertexCount == 0 )
				Fail( "ParticleSim baked no points.", "Every particle died before the end of the bake.", "Raise life", "Lower frames" );
		}

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "ParticleSim", mesh ) );
	}

	// One Verlet substep for one particle, with collision response.
	// rng is used only for turbulence-independent draws elsewhere; this
	// step itself is deterministic given the particle state.
	void StepParticle( Particle p, float dt, Random rng, ParticleCollider collider, List<HitEvent> hits, ref int collided, float time )
	{
		// Tunnelling guard: split this particle's step if it would jump
		// further than the collider radius in one step.
		var speed0 = (p.Pos - p.Prev).Length / Math.Max( dt, 1e-6f );
		if ( collider is not null && ColliderRadius.Clamped > 0f && speed0 * dt > ColliderRadius.Clamped )
		{
			var k = Math.Min( 8, (int)Math.Ceiling( speed0 * dt / ColliderRadius.Clamped ) );
			var sub = dt / k;
			for ( var i = 0; i < k; i++ )
				StepOnce( p, sub, collider, hits, ref collided, time );
			return;
		}
		StepOnce( p, dt, collider, hits, ref collided, time );
	}

	void StepOnce( Particle p, float dt, ParticleCollider collider, List<HitEvent> hits, ref int collided, float time )
	{
		var accel = ForceAt( p.Pos, time, true );
		var dragK = 1f - MathF.Exp( -Drag.Clamped * dt );
		var vel = (p.Pos - p.Prev) * (1f - dragK);
		var next = p.Pos + vel + accel * (dt * dt);
		p.Prev = p.Pos;
		p.Pos = next;

		if ( collider is null || ColliderRadius.Clamped <= 0f )
			return;

		if ( !collider.Query( p.Pos, ColliderRadius.Clamped, out var hitPt, out var normal ) )
			return;

		collided++;
		// Incoming velocity BEFORE the push-out: computing it after the
		// push would contaminate it with the push distance / dt.
		var v = (p.Pos - p.Prev) / Math.Max( dt, 1e-6f );
		// Push out to exactly the radius along the face normal.
		p.Pos = hitPt + normal * ColliderRadius.Clamped;
		var vn = Vec3.Dot( v, normal );
		var vt = v - normal * vn;
		Vec3 outV;
		switch ( CollideMode.Value )
		{
			case "stick":
				outV = Vec3.Zero;
				break;
			case "slide":
				outV = vt;
				break;
			default: // bounce
				outV = vt * (1f - Friction.Clamped) - normal * (vn * Restitution.Clamped);
				// vn is negative for an incoming particle (moving against the
				// outward normal), so -vn*restitution is the outgoing normal part.
				// The line above already handles the sign: outV_n = -restitution*vn.
				break;
		}
		p.Prev = p.Pos - outV * dt;

		if ( !p.HitRecorded )
		{
			p.HitRecorded = true;
			hits.Add( new HitEvent { Point = p.Pos, Normal = normal } );
		}
	}

	// Total acceleration at a point and time. includeGravity=false is the
	// sleep wake check: gravity alone must not wake a settled particle.
	Vec3 ForceAt( Vec3 pos, float time, bool includeGravity )
	{
		var accel = Wind.Value;
		if ( includeGravity )
			accel += Gravity.Value;

		if ( AttractorStrength.Clamped != 0f )
		{
			var d = AttractorPoint.Value - pos;
			var dist = d.Length;
			var dir = dist > 1e-6f ? d / dist : Vec3.Zero;
			// Softened 1/(1+(d/falloff)^2): finite at d=0, no attractor blow-up.
			var f = AttractorFalloff.Clamped;
			accel += dir * (AttractorStrength.Clamped / (1f + (dist / f) * (dist / f)));
		}

		if ( VortexStrength.Clamped != 0f && VortexAxis.Value.LengthSquared > 1e-12f )
		{
			var r = pos - VortexCentre.Value;
			var axis = VortexAxis.Value.Normal;
			var tang = Vec3.Cross( axis, r );
			var tl = tang.Length;
			if ( tl > 1e-6f )
				accel += tang / tl * (VortexStrength.Clamped / (1f + r.Length));
		}

		if ( Turbulence.Clamped != 0f && TurbOctaves.Clamped > 0 )
		{
			// Forward-difference gradient of FBM: a gust field, NOT curl
			// noise. Sampled at p*scale + time*drift so it moves over time.
			var q = pos * TurbScale.Clamped + new Vec3( time * WindDrift.Clamped, 0, 0 );
			var h = 1f;
			var seed = (uint)TurbSeed.Value;
			var oct = TurbOctaves.Clamped;
			var f0 = Noise.Fbm( q, oct, seed );
			var gx = Noise.Fbm( q + new Vec3( h, 0, 0 ), oct, seed ) - f0;
			var gy = Noise.Fbm( q + new Vec3( 0, h, 0 ), oct, seed ) - f0;
			var gz = Noise.Fbm( q + new Vec3( 0, 0, h ), oct, seed ) - f0;
			accel += new Vec3( gx, gy, gz ) * Turbulence.Clamped;
		}

		return accel;
	}

	static void ApplySeparation( List<Particle> particles, float separation )
	{
		var alive = particles.Where( p => !p.Dead && !p.Sleeping ).ToList();
		if ( alive.Count < 2 )
			return;
		var cell = Math.Max( separation, 0.5f );
		var grid = new Dictionary<(int, int, int), List<Particle>>();
		foreach ( var p in alive )
		{
			var key = ((int)Math.Floor( p.Pos.x / cell ), (int)Math.Floor( p.Pos.y / cell ), (int)Math.Floor( p.Pos.z / cell ));
			if ( !grid.TryGetValue( key, out var list ) )
				grid[key] = list = new List<Particle>();
			list.Add( p );
		}
		foreach ( var p in alive )
		{
			var cx = (int)Math.Floor( p.Pos.x / cell );
			var cy = (int)Math.Floor( p.Pos.y / cell );
			var cz = (int)Math.Floor( p.Pos.z / cell );
			var neighbours = new List<(float Dist, Particle Other)>();
			for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
			for ( var dz = -1; dz <= 1; dz++ )
			{
				if ( !grid.TryGetValue( (cx + dx, cy + dy, cz + dz), out var list ) )
					continue;
				foreach ( var o in list )
				{
					if ( ReferenceEquals( o, p ) )
						continue;
					var d = (p.Pos - o.Pos).Length;
					if ( d < separation && d > 1e-6f )
						neighbours.Add( (d, o) );
				}
			}
			neighbours.Sort( (a, b) => a.Dist.CompareTo( b.Dist ) );
			foreach ( var (_, o) in neighbours.Take( 8 ) )
			{
				var dvec = p.Pos - o.Pos;
				var d = dvec.Length;
				if ( d >= separation || d < 1e-6f )
					continue;
				var push = dvec / d * ((separation - d) * 0.5f);
				p.Pos += push;
				o.Pos -= push;
			}
		}
	}

	/// <summary>
	/// Uniform-grid mesh collider that returns the hit point and normal,
	/// unlike StrandSolver.SurfaceCollider.Collide, which only pushes a
	/// list of points out and never says what it hit. Same bucketing
	/// pattern: triangles bucketed by their radius-expanded AABB, so a
	/// query only tests triangles in the point's own cell.
	/// </summary>
	sealed class ParticleCollider
	{
		readonly List<(Vec3 A, Vec3 B, Vec3 C, Vec3 Normal)> _tris = new();
		readonly Dictionary<(int, int, int), List<int>> _grid = new();
		float _cell = 1f;

		public static ParticleCollider Build( IReadOnlyList<PolyMesh> meshes, float radius )
		{
			var sc = new ParticleCollider();
			foreach ( var mesh in meshes )
			{
				if ( mesh is null )
					continue;
				for ( var fi = 0; fi < mesh.FaceCount; fi++ )
				{
					var f = mesh.Faces[fi];
					for ( var k = 1; k + 1 < f.Indices.Length; k++ )
						sc._tris.Add( (mesh.Positions[f.Indices[0]], mesh.Positions[f.Indices[k]], mesh.Positions[f.Indices[k + 1]], mesh.FaceNormal( f )) );
				}
			}
			if ( sc._tris.Count > 0 )
			{
				var min = sc._tris[0].A; var max = sc._tris[0].A;
				foreach ( var tri in sc._tris )
					foreach ( var pt in new[] { tri.A, tri.B, tri.C } )
					{
						min = new Vec3( Math.Min( min.x, pt.x ), Math.Min( min.y, pt.y ), Math.Min( min.z, pt.z ) );
						max = new Vec3( Math.Max( max.x, pt.x ), Math.Max( max.y, pt.y ), Math.Max( max.z, pt.z ) );
					}
				sc._cell = Math.Max( 0.5f, Math.Max( max.x - min.x, Math.Max( max.y - min.y, max.z - min.z ) ) / 20f );
				var expand = Math.Max( radius, 0.5f );
				for ( var ti = 0; ti < sc._tris.Count; ti++ )
				{
					var tri = sc._tris[ti];
					var lo = new Vec3( Math.Min( tri.A.x, Math.Min( tri.B.x, tri.C.x ) ) - expand, Math.Min( tri.A.y, Math.Min( tri.B.y, tri.C.y ) ) - expand, Math.Min( tri.A.z, Math.Min( tri.B.z, tri.C.z ) ) - expand );
					var hi = new Vec3( Math.Max( tri.A.x, Math.Max( tri.B.x, tri.C.x ) ) + expand, Math.Max( tri.A.y, Math.Max( tri.B.y, tri.C.y ) ) + expand, Math.Max( tri.A.z, Math.Max( tri.B.z, tri.C.z ) ) + expand );
					for ( var cx = (int)Math.Floor( lo.x / sc._cell ); cx <= (int)Math.Floor( hi.x / sc._cell ); cx++ )
					for ( var cy = (int)Math.Floor( lo.y / sc._cell ); cy <= (int)Math.Floor( hi.y / sc._cell ); cy++ )
					for ( var cz = (int)Math.Floor( lo.z / sc._cell ); cz <= (int)Math.Floor( hi.z / sc._cell ); cz++ )
					{
						var key = (cx, cy, cz);
						if ( !sc._grid.TryGetValue( key, out var list ) )
							sc._grid[key] = list = new List<int>();
						list.Add( ti );
					}
				}
			}
			return sc;
		}

		// True when the point is within radius of the surface (penetrating
		// or resting on it). Returns the closest surface point and its
		// face normal. A point exactly on the surface counts as a hit so
		// a resting particle stays resting instead of sinking on the
		// next step.
		public bool Query( Vec3 p, float radius, out Vec3 hitPt, out Vec3 normal )
		{
			hitPt = default; normal = default;
			if ( _tris.Count == 0 )
				return false;
			var key = ((int)Math.Floor( p.x / _cell ), (int)Math.Floor( p.y / _cell ), (int)Math.Floor( p.z / _cell ));
			if ( !_grid.TryGetValue( key, out var tris ) )
				return false;
			var best = float.MaxValue; var bestPt = Vec3.Zero; var bestN = Vec3.Zero;
			foreach ( var ti in tris )
			{
				var tri = _tris[ti];
				var cp = ClosestPointTriangle( p, tri.A, tri.B, tri.C );
				var d = (p - cp).Length;
				if ( d < best )
				{
					best = d;
					bestPt = cp;
					bestN = tri.Normal.LengthSquared > 1e-12f ? tri.Normal.Normal : new Vec3( 0, 0, 1 );
				}
			}
			if ( best > radius )
				return false;
			// Only a hit when the point is on the inner side or resting:
			// signed distance along the normal below the radius. A point
			// hovering just above the surface at exactly radius is a
			// rest contact, not a penetration, but treating it as a hit
			// keeps bounce/stick stable instead of jittering in and out.
			var signed = Vec3.Dot( p - bestPt, bestN );
			if ( signed >= radius && best > 1e-6f )
				return false;
			hitPt = bestPt; normal = bestN;
			return true;
		}

		static Vec3 ClosestPointTriangle( Vec3 p, Vec3 a, Vec3 b, Vec3 c )
		{
			var ab = b - a; var ac = c - a; var ap = p - a;
			var d1 = Vec3.Dot( ab, ap ); var d2 = Vec3.Dot( ac, ap );
			if ( d1 <= 0f && d2 <= 0f ) return a;
			var bp = p - b; var d3 = Vec3.Dot( ab, bp ); var d4 = Vec3.Dot( ac, bp );
			if ( d3 >= 0f && d4 <= d3 ) return b;
			var vc = d1 * d4 - d3 * d2;
			if ( vc <= 0f && d1 >= 0f && d3 <= 0f ) { var v = d1 / (d1 - d3); return a + ab * v; }
			var cp = p - c; var d5 = Vec3.Dot( ab, cp ); var d6 = Vec3.Dot( ac, cp );
			if ( d6 >= 0f && d5 <= d6 ) return c;
			var vb = d5 * d2 - d1 * d6;
			if ( vb <= 0f && d2 >= 0f && d6 <= 0f ) { var w = d2 / (d2 - d6); return a + ac * w; }
			var va = d3 * d6 - d5 * d4;
			if ( va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f ) { var w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + (c - b) * w; }
			var denom = 1f / (va + vb + vc);
			var v2 = vb * denom; var w2 = vc * denom;
			return a + ab * v2 + ac * w2;
		}
	}
}
