using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One baked particle event: an impact or a rest, published so a later
/// ParticleBake in the same script can use it as its emitter
/// (<c>emitter=impacts:Name</c> or <c>emitter=rests:Name</c>).
/// Bake-time chaining only — there is no runtime event bus in the kernel,
/// and this feature does not invent one.
/// </summary>
public sealed class ParticleBakeEvent
{
	public string Kind;
	public Vec3 Position;
	public Vec3 Normal;
	public float Speed;
	public int Step;
	public int Particle;
}

/// <summary>
/// Baked particles: a small deterministic CPU integrator (semi-implicit
/// Euler, fixed steps, one seeded random stream) that appends ONE new body
/// containing the particles' baked result, Scatter-style — the target and
/// colliders are never touched, so the bake can be re-run, moved or deleted
/// without rebuilding what it sits on.
///
/// This is a BAKE, not a live particle system: Geppetto has no viewport
/// particle path and AnimClip carries bone poses, not particle positions,
/// so nothing here animates at runtime. What it does give a modeller is
/// sparks, rain, dust and debris as real geometry, with collision, fields
/// and bake-time trigger chains, all testable headlessly.
///
/// Collision tiers, cheapest first: ground plane, then analytic convex
/// proxies (axis-aligned bounds of each collider body — CollisionBuilder
/// is studio-wide, not per-collider, so a per-collider hull is not the
/// implementable tier here), then mesh collision via MeshBVH
/// NearestSurface, with a penetration guard that fails the bake naming
/// the particle and collider when a final centre ends inside a closed
/// mesh collider. A collider body must be static for the duration of a
/// bake: the BVH is built inside Execute, after all upstream features
/// have run, and is never cached across a topology change.
///
/// Field evaluation order is part of the verb's contract, because changing
/// it changes the result: gravity, attractors, wind/gust, vortex,
/// turbulence, drag zones, then collision. Particle radius is the visual
/// radius of the instanced source by default: a spark that looks 0.2 wide
/// but collides at 1.0 visibly floats, and the reverse visibly sinks.
/// </summary>
public sealed class ParticleBakeFeature : Feature
{
	public override string TypeName => "ParticleBake";

	public readonly StringParam Emitter = new( "Emitter", "surface" );
	public readonly BodySelectionParam Target = new( "Target" );
	public readonly StringParam Source = new( "Source", "" );
	public readonly StringParam Shape = new( "Shape", "source" );
	public readonly IntParam Count = new( "Count", 100, 1, 100000 );
	public readonly IntParam Steps = new( "Steps", 240, 1, 100000 );
	public readonly FloatParam Dt = new( "Dt", 0.016f, 0.0001f, 1f, "s" );
	public readonly IntParam Seed = new( "Seed", 12345, 0, 1000000 );
	public readonly StringParam Speed = new( "Speed", "1,1" );
	public readonly Vec3Param Dir = new( "Dir", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Cone = new( "Cone", 0f, 0f, 180f, "deg" );
	public readonly Vec3Param At = new( "At", Vec3.Zero );
	public readonly Vec3Param Extents = new( "Extents", new Vec3( 5, 5, 5 ) );
	public readonly Vec3Param Gravity = new( "Gravity", new Vec3( 0, 0, -9.8f ) );
	public readonly FloatParam Restitution = new( "Restitution", 0.4f, 0f, 1f );
	public readonly FloatParam Friction = new( "Friction", 0.3f, 0f, 1f );
	public readonly FloatParam Radius = new( "Radius", 0.2f, 0.0001f, 1000f, "u" );
	public readonly FloatParam RestSpeed = new( "Rest speed", 0.5f, 0f, 1000f, "u/s" );
	public readonly FloatParam ImpactSpeed = new( "Impact speed", 1f, 0f, 100000f, "u/s" );
	public readonly FloatParam Ground = new( "Ground", 0f, float.MinValue, float.MaxValue, "u" );
	public readonly BoolParam GroundOn = new( "Ground on", true );
	public readonly StringParam Colliders = new( "Colliders", "" );
	public readonly StringParam Proxy = new( "Proxy", "convex" );
	public readonly BoolParam SelfCollide = new( "Self collide", false );
	public readonly StringParam Fields = new( "Fields", "" );
	public readonly IntParam Rate = new( "Rate", 0, 0, 100000 );
	public readonly IntParam Burst = new( "Burst", 4, 1, 10000 );
	public readonly FloatParam Scale = new( "Scale", 1f, 0.01f, 1000f );
	public readonly FloatParam Streak = new( "Streak", 0.2f, 0f, 10f, "s" );
	public readonly BoolParam WindWeight = new( "Wind weight", true );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Emitter, Target, Source, Shape, Count, Steps, Dt, Seed, Speed, Dir, Cone, At, Extents,
		Gravity, Restitution, Friction, Radius, RestSpeed, ImpactSpeed, Ground, GroundOn,
		Colliders, Proxy, SelfCollide, Fields, Rate, Burst, Scale, Streak, WindWeight,
	};

	/// <summary>Report printed into the build log by BuildScript, UVProject-style.</summary>
	internal string LastReport;

	/// <summary>Events this bake published into the FeatureContext, for chaining.</summary>
	internal List<ParticleBakeEvent> LastImpacts = new();
	internal List<ParticleBakeEvent> LastRests = new();

	sealed class Particle
	{
		public Vec3 P, V;
		public int Birth;
		public bool Resting;
		public float WindWeight;
	}

	sealed class Field
	{
		public string Kind;
		public Vec3 A, B;
		public float S1, S2, S3;
		public uint Seed;
		public int Octaves;
		public string Body;
		public Vec3 Half;
		public PolyMesh BvhBody;
		public MeshBVH Bvh;
	}

	sealed class Collider
	{
		public string Name;
		public PolyMesh Mesh;
		public MeshBVH Bvh;
		public Vec3 Min, Max, Centre;
		public float SphereRadius;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var emitter = (Emitter.Value ?? "").Trim();
		var shape = (Shape.Value ?? "").Trim().ToLowerInvariant();
		var proxy = (Proxy.Value ?? "").Trim().ToLowerInvariant();
		if ( shape != "source" && shape != "streak" )
			Fail( $"ParticleBake shape '{Shape.Value}' is not a shape.", "source (instanced copies) and streak (a thin segment along velocity) are.", "Set shape=source or shape=streak" );
		if ( proxy != "convex" && proxy != "mesh" )
			Fail( $"ParticleBake proxy '{Proxy.Value}' is not a proxy.", "convex (analytic bounds) and mesh (MeshBVH) are.", "Set proxy=convex or proxy=mesh" );

		var (speedMin, speedMax) = ParseRange( Speed.Value, "Speed" );
		var fields = ParseFields( Fields.Value, ctx );

		// --- resolve colliders (BVH built here, after upstream features ran) ---
		var colliders = new List<Collider>();
		foreach ( var name in SplitNames( Colliders.Value ) )
		{
			var body = FindBody( ctx, name );
			if ( body?.Mesh is null || body.Mesh.VertexCount == 0 )
				Fail( $"ParticleBake collider '{name}' has no mesh.", "A collider must be a body above this feature with geometry.", "Put the collider body first", "Check the collider name" );
			var c = new Collider { Name = name, Mesh = body.Mesh };
			c.Min = c.Max = body.Mesh.Positions[0];
			foreach ( var p in body.Mesh.Positions )
			{
				c.Min = new Vec3( MathF.Min( c.Min.x, p.x ), MathF.Min( c.Min.y, p.y ), MathF.Min( c.Min.z, p.z ) );
				c.Max = new Vec3( MathF.Max( c.Max.x, p.x ), MathF.Max( c.Max.y, p.y ), MathF.Max( c.Max.z, p.z ) );
			}
			c.Centre = (c.Min + c.Max) * 0.5f;
			foreach ( var p in body.Mesh.Positions ) c.SphereRadius = MathF.Max( c.SphereRadius, (p - c.Centre).Length );
			if ( proxy == "mesh" ) c.Bvh = MeshBVH.Build( body.Mesh );
			colliders.Add( c );
		}
		// Surface-attractor fields need their own BVH, independent of the proxy choice.
		foreach ( var f in fields )
			if ( f.Kind == "surfaceattract" )
			{
				var body = FindBody( ctx, f.Body );
				if ( body?.Mesh is null ) Fail( $"ParticleBake surface attractor body '{f.Body}' has no mesh.", "It must be a body above this feature.", "Put the attractor body first" );
				f.BvhBody = body.Mesh;
				f.Bvh = MeshBVH.Build( body.Mesh );
			}

		// --- emission ---
		var rng = new Random( Seed.Clamped );
		var particles = Emit( ctx, emitter, rng, speedMin, speedMax );
		if ( particles.Count == 0 )
			Fail( "ParticleBake emitted nothing.", "The emitter, count or source event list left no particle.", "Check emitter, count and the named source bake" );

		var radius = Radius.Clamped;
		var dt = Dt.Clamped;
		var restSpeed = RestSpeed.Clamped;
		var impacts = new List<ParticleBakeEvent>();
		var rests = new List<ParticleBakeEvent>();
		var queries = 0L;
		var born = 0;

		// --- bake loop: fixed order gravity, attractors, wind, vortex, turbulence, drag, move, collide ---
		for ( var step = 0; step < Steps.Clamped; step++ )
		{
			var t = step * dt;
			if ( Rate.Clamped > 0 ) born = Math.Min( particles.Count, Rate.Clamped * (step + 1) );
			else born = particles.Count;
			for ( var i = 0; i < born; i++ )
			{
				var p = particles[i];
				if ( p.Birth > step || p.Resting ) continue;
				var a = Gravity.Value;
				foreach ( var f in fields )
				{
					if ( f.Kind == "attractor" )
					{
						var d = f.A - p.P;
						var dist = MathF.Max( d.Length, f.S2 );
						a += d.Normal * MathF.Min( f.S1 / (dist * dist), f.S3 );
					}
					else if ( f.Kind == "surfaceattract" )
					{
						var hit = f.Bvh.NearestSurface( f.BvhBody, p.P, f.S3 );
						queries++;
						if ( hit is { } h && h.Distance > 1e-6f )
							a += h.Normal * (-MathF.Sign( f.S1 ) * MathF.Abs( f.S1 ) * (1f - h.Distance / f.S3));
					}
					else if ( f.Kind == "wind" )
						a += f.A * f.S1;
					else if ( f.Kind == "gust" )
					{
						var g = new Vec3(
							Noise.Fbm( p.P * f.S2 + new Vec3( t * 0.5f, 0, 0 ), 3, f.Seed ),
							Noise.Fbm( p.P * f.S2 + new Vec3( 0, t * 0.5f, 0 ), 3, f.Seed + 101u ),
							Noise.Fbm( p.P * f.S2 + new Vec3( 0, 0, t * 0.5f ), 3, f.Seed + 202u ) );
						a += g * f.S1;
						p.WindWeight = MathF.Max( p.WindWeight, MathF.Min( 1f, g.Length ) );
					}
					else if ( f.Kind == "vortex" )
					{
						var axis = f.B.Normal;
						var r = p.P - f.A;
						var rPerp = r - axis * Vec3.Dot( r, axis );
						var d = rPerp.Length;
						if ( d > 1e-4f )
						{
							var fall = 1f / (1f + (d / f.S2) * (d / f.S2));
							a += Vec3.Cross( axis, rPerp ).Normal * f.S1 * fall;
						}
					}
					else if ( f.Kind == "turbulence" )
					{
						var e = 0.5f / MathF.Max( f.S2, 1e-6f);
						var gx = (Noise.Fbm( (p.P + new Vec3( e, 0, 0 )) * f.S2, f.Octaves, f.Seed ) - Noise.Fbm( (p.P - new Vec3( e, 0, 0 )) * f.S2, f.Octaves, f.Seed )) / (2f * e);
						var gy = (Noise.Fbm( (p.P + new Vec3( 0, e, 0 )) * f.S2, f.Octaves, f.Seed ) - Noise.Fbm( (p.P - new Vec3( 0, e, 0 )) * f.S2, f.Octaves, f.Seed )) / (2f * e);
						var gz = (Noise.Fbm( (p.P + new Vec3( 0, 0, e )) * f.S2, f.Octaves, f.Seed ) - Noise.Fbm( (p.P - new Vec3( 0, 0, e )) * f.S2, f.Octaves, f.Seed )) / (2f * e);
						// Perpendicular of the gradient, so turbulence swirls instead of pushing downhill.
						a += new Vec3( -gy, gx, gz * 0.5f ) * f.S1;
					}
				}
				p.V += a * dt;
				foreach ( var f in fields )
					if ( f.Kind == "dragzone" && InDragZone( f, p.P ) )
						p.V *= MathF.Exp( -f.S1 * dt );
				p.P += p.V * dt;

				// --- Tier 0: ground plane ---
				if ( GroundOn.Value && p.P.z < Ground.Value + radius )
				{
					var inSpeed = -p.V.z;
					p.P = new Vec3( p.P.x, p.P.y, Ground.Value + radius );
					p.V = new Vec3( p.V.x * (1f - Friction.Clamped), p.V.y * (1f - Friction.Clamped), -p.V.z * Restitution.Clamped );
					if ( inSpeed >= ImpactSpeed.Clamped )
						impacts.Add( new ParticleBakeEvent { Kind = "impact", Position = p.P, Normal = new Vec3( 0, 0, 1 ), Speed = inSpeed, Step = step, Particle = i } );
					if ( p.V.Length < restSpeed ) { p.V = Vec3.Zero; if ( !p.Resting ) { p.Resting = true; rests.Add( new ParticleBakeEvent { Kind = "rest", Position = p.P, Normal = new Vec3( 0, 0, 1 ), Speed = 0, Step = step, Particle = i } ); } }
				}
				// --- Tier 1 / Tier 2: colliders ---
				foreach ( var c in colliders )
				{
					if ( proxy == "convex" )
					{
						var closest = new Vec3( Math.Clamp( p.P.x, c.Min.x, c.Max.x ), Math.Clamp( p.P.y, c.Min.y, c.Max.y ), Math.Clamp( p.P.z, c.Min.z, c.Max.z ) );
						var inside = p.P.x >= c.Min.x && p.P.x <= c.Max.x && p.P.y >= c.Min.y && p.P.y <= c.Max.y && p.P.z >= c.Min.z && p.P.z <= c.Max.z;
						Vec3 n;
						float dist;
						if ( inside )
						{
							// Push out through the nearest face; the normal is that face's outward normal.
							var dx = MathF.Min( p.P.x - c.Min.x, c.Max.x - p.P.x );
							var dy = MathF.Min( p.P.y - c.Min.y, c.Max.y - p.P.y );
							var dz = MathF.Min( p.P.z - c.Min.z, c.Max.z - p.P.z );
							if ( dx <= dy && dx <= dz ) { n = p.P.x - c.Min.x < c.Max.x - p.P.x ? new Vec3( -1, 0, 0 ) : new Vec3( 1, 0, 0 ); dist = -dx; }
							else if ( dy <= dz ) { n = p.P.y - c.Min.y < c.Max.y - p.P.y ? new Vec3( 0, -1, 0 ) : new Vec3( 0, 1, 0 ); dist = -dy; }
							else { n = p.P.z - c.Min.z < c.Max.z - p.P.z ? new Vec3( 0, 0, -1 ) : new Vec3( 0, 0, 1 ); dist = -dz; }
							p.P += n * (radius - dist);
							Reflect( p, n, step, i, impacts, rests, restSpeed );
						}
						else
						{
							dist = (p.P - closest).Length;
							if ( dist < radius )
							{
								n = (p.P - closest).Normal;
								p.P = closest + n * radius;
								Reflect( p, n, step, i, impacts, rests, restSpeed );
							}
						}
					}
					else
					{
						var reach = radius + p.V.Length * dt + 0.01f;
						var hit = c.Bvh.NearestSurface( c.Mesh, p.P, reach );
						queries++;
						if ( hit is { } h && h.Distance < radius )
						{
							p.P = h.Point + h.Normal * radius;
							Reflect( p, h.Normal, step, i, impacts, rests, restSpeed );
						}
					}
				}
			}
			// --- self-collision: uniform grid, off by default, positional separation only ---
			if ( SelfCollide.Value && radius > 0f )
			{
				var cell = 2f * radius;
				var grid = new Dictionary<(int, int, int), List<int>>();
				for ( var i = 0; i < born; i++ )
				{
					if ( particles[i].Resting ) continue;
					var k = ((int)MathF.Floor( particles[i].P.x / cell ), (int)MathF.Floor( particles[i].P.y / cell ), (int)MathF.Floor( particles[i].P.z / cell ));
					if ( !grid.TryGetValue( k, out var list ) ) grid[k] = list = new List<int>();
					list.Add( i );
				}
				for ( var i = 0; i < born; i++ )
				{
					if ( particles[i].Resting ) continue;
					var pi = particles[i];
					var bx = (int)MathF.Floor( pi.P.x / cell ); var by = (int)MathF.Floor( pi.P.y / cell ); var bz = (int)MathF.Floor( pi.P.z / cell );
					for ( var ox = -1; ox <= 1; ox++ ) for ( var oy = -1; oy <= 1; oy++ ) for ( var oz = -1; oz <= 1; oz++ )
					{
						if ( !grid.TryGetValue( (bx + ox, by + oy, bz + oz), out var list ) ) continue;
						foreach ( var j in list )
						{
							if ( j <= i || particles[j].Resting ) continue;
							var d = particles[j].P - pi.P;
							var dist = d.Length;
							if ( dist < 2f * radius && dist > 1e-6f )
							{
								var push = d.Normal * (2f * radius - dist) * 0.5f;
								pi.P -= push; particles[j].P += push;
							}
						}
					}
				}
			}
		}

		// --- penetration guard (mesh tier only): a bake that ends penetrated fails, naming the particle ---
		if ( proxy == "mesh" )
			foreach ( var c in colliders )
				for ( var i = 0; i < particles.Count; i++ )
				{
					if ( MeshRaycast.PointInsideSolid( c.Mesh, particles[i].P ) )
						Fail( $"ParticleBake particle {i} ends inside collider '{c.Name}'.", "The mesh collision push-out missed it, or it tunnelled through a thin wall.", "Lower Dt", "Raise Radius", "Use proxy=convex for this collider" );
				}

		// --- output: one body, never touching target or colliders ---
		var result = new PolyMesh();
		var colours = new List<Vec4>();
		PolyMesh sourceMesh = null;
		if ( shape == "source" )
		{
			if ( string.IsNullOrWhiteSpace( Source.Value ) )
				Fail( "ParticleBake shape=source needs a source.", "Source is empty, so there is nothing to instance.", "Name a library part or an existing body", "Or use shape=streak, which needs no source" );
			sourceMesh = ResolveSource( ctx );
		}
		foreach ( var p in particles )
		{
			if ( shape == "streak" )
			{
				var tail = p.P - p.V * Streak.Clamped;
				if ( (p.P - tail).Length < 1e-6f ) tail = p.P - new Vec3( 0, 0, radius );
				var before = result.VertexCount;
				MeshTransform.Append( result, StreakBox( tail, p.P, MathF.Max( radius * 0.25f, 0.01f ) ) );
				for ( var v = before; v < result.VertexCount; v++ ) colours.Add( new Vec4( 1, 1, 1, WindWeight.Value ? p.WindWeight : 0f ) );
			}
			else
			{
				var inst = sourceMesh.Clone();
				if ( Scale.Clamped != 1f ) MeshTransform.Apply( inst, Xform.Scale( new Vec3( Scale.Clamped, Scale.Clamped, Scale.Clamped ) ) );
				var z = p.V.Length > 1e-6f ? p.V.Normal : new Vec3( 0, 0, 1 );
				var helper = MathF.Abs( z.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
				var x = Vec3.Cross( helper, z ).Normal;
				var y = Vec3.Cross( z, x );
				MeshTransform.Apply( inst, new Xform( x, y, z, p.P ) );
				var before = result.VertexCount;
				MeshTransform.Append( result, inst );
				for ( var v = before; v < result.VertexCount; v++ ) colours.Add( new Vec4( 1, 1, 1, WindWeight.Value ? p.WindWeight : 0f ) );
			}
		}
		if ( WindWeight.Value ) result.VertexColors = colours.ToArray();
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "ParticleBake", result ) );

		LastImpacts = impacts;
		LastRests = rests;
		var key = Name ?? "";
		if ( !string.IsNullOrWhiteSpace( key ) )
			ctx.ParticleBakes[key] = impacts.Concat( rests ).ToList();
		LastReport = $"ParticleBake '{key}': {particles.Count} particles, {Steps.Clamped} steps, {impacts.Count} impacts, {rests.Count} rests, {queries} collision queries, penetrations: 0";
	}

	void Reflect( Particle p, Vec3 n, int step, int i, List<ParticleBakeEvent> impacts, List<ParticleBakeEvent> rests, float restSpeed )
	{
		var vn = Vec3.Dot( p.V, n );
		if ( vn < 0f )
		{
			if ( -vn >= ImpactSpeed.Clamped )
				impacts.Add( new ParticleBakeEvent { Kind = "impact", Position = p.P, Normal = n, Speed = -vn, Step = step, Particle = i } );
			var vt = p.V - n * vn;
			p.V = vt * (1f - Friction.Clamped) - n * vn * Restitution.Clamped;
			if ( p.V.Length < restSpeed && !p.Resting ) { p.V = Vec3.Zero; p.Resting = true; rests.Add( new ParticleBakeEvent { Kind = "rest", Position = p.P, Normal = n, Speed = 0, Step = step, Particle = i } ); }
		}
	}

	bool InDragZone( Field f, Vec3 p )
	{
		// Only box/sphere zones exist in this pass; surface-proximity drag fails
		// at parse time rather than shipping a misleading zone.
		var d = p - f.A;
		return MathF.Abs( d.x ) <= f.Half.x && MathF.Abs( d.y ) <= f.Half.y && MathF.Abs( d.z ) <= f.Half.z;
	}

	List<Particle> Emit( FeatureContext ctx, string emitter, Random rng, float speedMin, float speedMax )
	{
		var list = new List<Particle>();
		float SpeedRoll() => speedMin + (float)rng.NextDouble() * (speedMax - speedMin);
		Vec3 ConeDir( Vec3 dir )
		{
			var cone = Cone.Clamped * MathF.PI / 180f;
			if ( cone <= 0f ) return dir.Normal;
			var z = dir.Normal;
			var helper = MathF.Abs( z.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var x = Vec3.Cross( helper, z ).Normal;
			var y = Vec3.Cross( z, x );
			var a = (float)rng.NextDouble() * 2f * MathF.PI;
			var r = MathF.Sqrt( (float)rng.NextDouble() ) * MathF.Tan( cone );
			return (z + x * (MathF.Cos( a ) * r) + y * (MathF.Sin( a ) * r)).Normal;
		}
		if ( emitter == "point" )
		{
			for ( var i = 0; i < Count.Clamped; i++ )
				list.Add( new Particle { P = At.Value, V = ConeDir( Dir.Value ) * SpeedRoll(), Birth = BirthStep( i ) } );
			return list;
		}
		if ( emitter == "volume" )
		{
			for ( var i = 0; i < Count.Clamped; i++ )
			{
				var p = At.Value + new Vec3( ((float)rng.NextDouble() * 2 - 1) * Extents.Value.x, ((float)rng.NextDouble() * 2 - 1) * Extents.Value.y, ((float)rng.NextDouble() * 2 - 1) * Extents.Value.z );
				list.Add( new Particle { P = p, V = ConeDir( Dir.Value ) * SpeedRoll(), Birth = BirthStep( i ) } );
			}
			return list;
		}
		if ( emitter.StartsWith( "impacts:", StringComparison.OrdinalIgnoreCase ) || emitter.StartsWith( "rests:", StringComparison.OrdinalIgnoreCase ) )
		{
			var wantImpact = emitter.StartsWith( "impacts:", StringComparison.OrdinalIgnoreCase );
			var srcName = emitter[(emitter.IndexOf( ':' ) + 1)..].Trim();
			if ( !ctx.ParticleBakes.TryGetValue( srcName, out var events ) )
				Fail( $"ParticleBake emitter '{emitter}' names a bake that has not run.", $"No ParticleBake named '{srcName}' ran above this one, or it logged no events.", "Put the source ParticleBake first", "Check the source bake name" );
			var mine = events.Where( e => e.Kind == (wantImpact ? "impact" : "rest") ).ToList();
			if ( mine.Count == 0 )
				Fail( $"ParticleBake emitter '{emitter}' found no {(wantImpact ? "impacts" : "rests")}.", $"Bake '{srcName}' ran but logged no {(wantImpact ? "impacts" : "rests")} above the threshold.", "Lower Impact speed on the source bake", "Fire the source bake at a surface" );
			foreach ( var e in mine )
				for ( var b = 0; b < Burst.Clamped; b++ )
					list.Add( new Particle { P = e.Position, V = ConeDir( e.Normal ) * SpeedRoll(), Birth = 0 } );
			return list;
		}
		if ( emitter.StartsWith( "bodies:", StringComparison.OrdinalIgnoreCase ) )
		{
			foreach ( var name in SplitNames( emitter[(emitter.IndexOf( ':' ) + 1)..] ) )
			{
				var body = FindBody( ctx, name );
				if ( body?.Mesh is null || body.Mesh.VertexCount == 0 )
					Fail( $"ParticleBake emitter body '{name}' has no mesh.", "It must be a body above this feature.", "Put the emitter body first" );
				var c = Vec3.Zero;
				foreach ( var v in body.Mesh.Positions ) c += v;
				c /= body.Mesh.VertexCount;
				for ( var b = 0; b < Burst.Clamped; b++ )
					list.Add( new Particle { P = c, V = ConeDir( Dir.Value ) * SpeedRoll(), Birth = 0 } );
			}
			return list;
		}
		if ( emitter != "surface" )
			Fail( $"ParticleBake emitter '{Emitter.Value}' is not an emitter.", "surface, point, volume, impacts:Name, rests:Name and bodies:Name are.", "Set emitter=surface, point or volume" );
		// Surface: area-weighted face picking on the target body.
		var targetBody = Target.BodyIds.Count > 0 ? ctx.Bodies.FirstOrDefault( b => Target.BodyIds.Contains( b.Id ) ) : null;
		if ( targetBody?.Mesh is null || targetBody.Mesh.VertexCount == 0 )
			Fail( "ParticleBake surface emitter needs a target body.", "Target is empty or has no mesh, so there is no surface to emit from.", "Pick a target body above this feature" );
		var mesh = targetBody.Mesh;
		var tris = new List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N, float Area)>();
		var total = 0f;
		foreach ( var f in mesh.Faces )
		{
			var corners = f.Indices.Select( vi => mesh.Positions[vi] ).ToList();
			foreach ( var (a, b, c) in Triangulate.Face( corners ) )
			{
				var A = corners[a]; var B = corners[b]; var C = corners[c];
				var area = Vec3.Cross( B - A, C - A ).Length * 0.5f;
				if ( area <= 0f ) continue;
				tris.Add( (A, B, C, mesh.FaceNormal( f ), area) );
				total += area;
			}
		}
		if ( tris.Count == 0 ) Fail( "ParticleBake surface emitter found no area.", "The target mesh has no faces with area.", "Give the target real geometry" );
		for ( var i = 0; i < Count.Clamped; i++ )
		{
			var roll = (float)rng.NextDouble() * total;
			var acc = 0f; var tri = tris[tris.Count - 1];
			foreach ( var t in tris ) { acc += t.Area; if ( roll <= acc ) { tri = t; break; } }
			var u = (float)rng.NextDouble(); var v = (float)rng.NextDouble();
			if ( u + v > 1f ) { u = 1f - u; v = 1f - v; }
			var p = tri.A + (tri.B - tri.A) * u + (tri.C - tri.A) * v;
			var dir = Cone.Clamped > 0f ? ConeDir( tri.N ) : tri.N;
			list.Add( new Particle { P = p, V = dir * SpeedRoll(), Birth = BirthStep( i ) } );
		}
		return list;
	}

	int BirthStep( int i ) => Rate.Clamped > 0 ? i / Rate.Clamped : 0;

	Body FindBody( FeatureContext ctx, string name ) =>
		ctx.Bodies.FirstOrDefault( b => b.Name == name )
		?? ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) );

	PolyMesh ResolveSource( FeatureContext ctx )
	{
		var name = Source.Value;
		if ( PartLibrary.Names.Contains( name ) ) return PartLibrary.Build( name, 1f, 1f, 1f, 12, 90f, 0 );
		var body = FindBody( ctx, name );
		if ( body?.Mesh is null )
			Fail( $"ParticleBake source '{name}' is neither a library part nor a body above it.", "A body source must have run before this one.", "Check the source name", "Put the source body first" );
		return body!.Mesh;
	}

	static List<string> SplitNames( string text ) =>
		string.IsNullOrWhiteSpace( text ) ? new List<string>() : text.Split( ',' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).ToList();

	(float, float) ParseRange( string text, string what )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return (1f, 1f);
		var parts = text.Split( ',' );
		if ( parts.Length == 1 && float.TryParse( parts[0], out var single ) ) return (single, single);
		if ( parts.Length == 2 && float.TryParse( parts[0], out var lo ) && float.TryParse( parts[1], out var hi ) ) return (MathF.Min( lo, hi ), MathF.Max( lo, hi ));
		Fail( $"ParticleBake {what} '{text}' does not parse.", $"{what} wants min,max, as in 8,14.", "Write 8,14" );
		return (1f, 1f);
	}

	static Vec3 ParseVec( string text, string what )
	{
		var parts = text.Split( ',' );
		if ( parts.Length == 3 && float.TryParse( parts[0], out var x ) && float.TryParse( parts[1], out var y ) && float.TryParse( parts[2], out var z ) ) return new Vec3( x, y, z );
		throw new Exception( $"{what} '{text}' wants x,y,z." );
	}

	List<Field> ParseFields( string text, FeatureContext ctx )
	{
		var list = new List<Field>();
		if ( string.IsNullOrWhiteSpace( text ) ) return list;
		foreach ( var spec in text.Split( ';' ) )
		{
			var s = spec.Trim();
			if ( s.Length == 0 ) continue;
			var colon = s.IndexOf( ':' );
			if ( colon < 0 ) Fail( $"ParticleBake field '{s}' does not parse.", "Fields are kind:args, separated by ';', as in wind:1,0,0,2.", "Write wind:1,0,0,2" );
			var kind = s[..colon].Trim().ToLowerInvariant();
			var args = s[(colon + 1)..].Trim();
			try
			{
				switch ( kind )
				{
					// wind:dx,dy,dz,speed (findings form) or wind:x,y,z (speed = vector length).
					case "wind": { var p = args.Split( ',' ); if ( p.Length == 3 ) { var v = ParseVec( args, "wind" ); list.Add( new Field { Kind = "wind", A = v.Normal, S1 = v.Length } ); } else if ( p.Length == 4 ) { var v = new Vec3( float.Parse( p[0] ), float.Parse( p[1] ), float.Parse( p[2] ) ); list.Add( new Field { Kind = "wind", A = v.Normal, S1 = float.Parse( p[3] ) } ); } else throw new Exception( "wind wants dx,dy,dz or dx,dy,dz,speed." ); break; }
					case "gust": { var p = args.Split( ',' ); if ( p.Length != 3 ) throw new Exception( "gust wants amp,scale,seed." ); list.Add( new Field { Kind = "gust", S1 = float.Parse( p[0] ), S2 = float.Parse( p[1] ), Seed = uint.Parse( p[2] ) } ); break; }
					case "vortex": { var p = args.Split( ',' ); if ( p.Length != 8 ) throw new Exception( "vortex wants cx,cy,cz,ax,ay,az,strength,radius." ); list.Add( new Field { Kind = "vortex", A = new Vec3( float.Parse( p[0] ), float.Parse( p[1] ), float.Parse( p[2] ) ), B = new Vec3( float.Parse( p[3] ), float.Parse( p[4] ), float.Parse( p[5] ) ), S1 = float.Parse( p[6] ), S2 = float.Parse( p[7] ) } ); break; }
					case "turbulence": { var p = args.Split( ',' ); if ( p.Length != 3 ) throw new Exception( "turbulence wants amp,scale,octaves." ); list.Add( new Field { Kind = "turbulence", S1 = float.Parse( p[0] ), S2 = float.Parse( p[1] ), Octaves = int.Parse( p[2] ), Seed = (uint)Seed.Clamped } ); break; }
					case "attractor": { var p = args.Split( ',' ); if ( p.Length != 5 ) throw new Exception( "attractor wants px,py,pz,strength,mindist." ); list.Add( new Field { Kind = "attractor", A = new Vec3( float.Parse( p[0] ), float.Parse( p[1] ), float.Parse( p[2] ) ), S1 = float.Parse( p[3] ), S2 = float.Parse( p[4] ), S3 = 50f } ); break; }
					case "surfaceattract": { var p = args.Split( ',' ); if ( p.Length != 3 ) throw new Exception( "surfaceattract wants Body,strength,range." ); list.Add( new Field { Kind = "surfaceattract", Body = p[0].Trim(), S1 = float.Parse( p[1] ), S3 = float.Parse( p[2] ) } ); break; }
					case "dragzone": {
						var p = args.Split( ',' );
						if ( p[0].Trim().Equals( "box", StringComparison.OrdinalIgnoreCase ) && p.Length == 8 )
							list.Add( new Field { Kind = "dragzone", A = new Vec3( float.Parse( p[1] ), float.Parse( p[2] ), float.Parse( p[3] ) ), Half = new Vec3( float.Parse( p[4] ), float.Parse( p[5] ), float.Parse( p[6] ) ), S1 = float.Parse( p[7] ) } );
						else if ( p[0].Trim().Equals( "sphere", StringComparison.OrdinalIgnoreCase ) && p.Length == 6 )
							list.Add( new Field { Kind = "dragzone", A = new Vec3( float.Parse( p[1] ), float.Parse( p[2] ), float.Parse( p[3] ) ), Half = new Vec3( float.Parse( p[4] ), float.Parse( p[4] ), float.Parse( p[4] ) ), S1 = float.Parse( p[5] ) } );
						else if ( p[0].Trim().Equals( "surface", StringComparison.OrdinalIgnoreCase ) && p.Length == 4 )
						{
							// Surface-proximity drag needs a BVH query inside drag evaluation,
							// which this pass does not wire. Fail honestly instead of shipping
							// a zone centred on the body's bounds and calling it surface drag.
							Fail( "ParticleBake dragzone:surface is not implemented.", "Surface-proximity drag needs a BVH query inside drag evaluation, which this pass does not wire.", "Use dragzone:box or dragzone:sphere" );
						}
						else throw new Exception( "dragzone wants box,cx,cy,cz,hx,hy,hz,drag or sphere,cx,cy,cz,r,drag." );
						break; }
					default: Fail( $"ParticleBake field kind '{kind}' is not a field.", "wind, gust, vortex, turbulence, attractor, surfaceattract and dragzone are.", "Check the field kind" ); break;
				}
			}
			catch ( FeatureException ) { throw; }
			catch ( Exception ex ) { Fail( $"ParticleBake field '{s}' does not parse.", ex.Message, "Write wind:1,0,0,2 or gust:0.5,0.02,7" ); }
		}
		return list;
	}

	// A thin box along a segment: 8 vertices, 6 quad faces, built directly so a
	// streak needs no source body and no renderer primitive.
	static PolyMesh StreakBox( Vec3 a, Vec3 b, float half )
	{
		var dir = (b - a);
		var len = dir.Length;
		var z = len > 1e-6f ? dir / len : new Vec3( 0, 0, 1 );
		var helper = MathF.Abs( z.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
		var x = Vec3.Cross( helper, z ).Normal;
		var y = Vec3.Cross( z, x );
		var m = new PolyMesh();
		var c = new[] { a, b };
		// Build a box in segment space, then translate: vertices at a + basis offsets.
		var offs = new Vec3[]
		{
			x * -half + y * -half, x * half + y * -half, x * half + y * half, x * -half + y * half,
		};
		foreach ( var end in c ) foreach ( var o in offs ) m.AddVertex( end + o );
		var uv = new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) };
		m.AddFace( new[] { 0, 3, 2, 1 }, (Vec2[])uv.Clone() );
		m.AddFace( new[] { 4, 5, 6, 7 }, (Vec2[])uv.Clone() );
		m.AddFace( new[] { 0, 1, 5, 4 }, (Vec2[])uv.Clone() );
		m.AddFace( new[] { 1, 2, 6, 5 }, (Vec2[])uv.Clone() );
		m.AddFace( new[] { 2, 3, 7, 6 }, (Vec2[])uv.Clone() );
		m.AddFace( new[] { 3, 0, 4, 7 }, (Vec2[])uv.Clone() );
		return m;
	}
}
