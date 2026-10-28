using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Emitter: one deterministic baked sample of a particle system, as one merged
/// body — the same "one body out" pattern Scatter uses (ScatterFeature.cs:232).
/// Geppetto documents rebuild; there is no per-frame tick for a feature to hook
/// into, so "emitter" here means: given a sample time, bake the particles that
/// would be alive then. Change Sample (or use Frames) and rebuild for a
/// different bake. It is not a live simulation and must not be described as one.
///
/// Ballistics are analytic, not stepped: with drag d, velocity obeys
/// dv/dt = gravity - d*v, so v(t) = g/d + (v0 - g/d)*exp(-d*t) and position is
/// its integral; with d ~ 0 that reduces to spawn + v0*t + 0.5*g*t^2.
/// Birth rules: Burst births every particle at Start; Continuous births
/// seeded-uniform over [Start, Start + Duration] with
/// count = round(Rate * Duration). Loop wraps Sample into
/// [Start, Start + Duration); PingPong mirrors it without doubling endpoints
/// (the endpoint rule borrowed from Render/SpriteAnim.cs:464-470, names only).
/// A sample with no particle alive fails honestly ("Emitter emitted nothing"),
/// it never emits a silent empty body.
///
/// Parameter-name notes (collisions with Scatter's dials, resolved in favour
/// of not silently redefining a known dial):
/// - Size/Length/Thickness/Sides/PartAngle are the library-part dials, exactly
///   as in Scatter; Length also doubles as the Cone shape length, and
///   Thickness as the part flatten.
/// - Fill is the FINDINGS' thickness dial, renamed: 0 = shell/surface only,
///   1 = full volume. It is not Scatter's Thickness.
/// - ParticleSize is the FINDINGS' size= dial, renamed: the base scale of one
///   particle. Size stays the library-part size dial.
/// - Rate is ignored in Burst mode. Material only affects library-part
///   sources; body sources keep their own face materials.
/// - Origin is an extension beyond the FINDINGS: the emitter origin in
///   document space (default 0,0,0). Without it every emitter sits at the
///   origin and only a later Transform can move it.
/// - Streak stretches each instance along its Z axis — the velocity axis,
///   or the surface normal for shape=Surface — by
///   1 + Streak * (v_now/v0) * (age/life), bounded by 1 + Streak.
/// </summary>
public sealed class EmitterFeature : Feature
{
	public override string TypeName => "Emitter";

	public readonly StringParam Source = new( "Source", "" );
	public readonly ChoiceParam Shape = new( "Shape", new[] { "Point", "Sphere", "Box", "Disc", "Cone", "Surface" } );
	public readonly BodySelectionParam Target = new( "Target" );
	public readonly Vec3Param Origin = new( "Origin", Vec3.Zero );
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Spread = new( "Spread", 0f, 0f, 180f, "deg" );
	public readonly FloatParam Speed = new( "Speed", 10f, 0f, 10000f, "u/s" );
	public readonly FloatParam SpeedJitter = new( "Speed jitter", 0f, 0f, 1f );
	public readonly Vec3Param Gravity = new( "Gravity", Vec3.Zero );
	public readonly FloatParam Drag = new( "Drag", 0f, 0f, 100f, "1/s" );
	public readonly FloatParam Life = new( "Life", 1f, 0.01f, 100000f, "s" );
	public readonly FloatParam LifeJitter = new( "Life jitter", 0f, 0f, 1f );
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Continuous", "Burst" } );
	public readonly FloatParam Rate = new( "Rate", 60f, 0f, 1000000f, "1/s" );
	public readonly IntParam Count = new( "Count", 24, 1, 100000 );
	public readonly FloatParam Start = new( "Start", 0f, -100000f, 100000f, "s" );
	public readonly FloatParam Duration = new( "Duration", 1f, 0f, 100000f, "s" );
	public readonly FloatParam Sample = new( "Sample", 0f, -100000f, 100000f, "s" );
	public readonly ChoiceParam Loop = new( "Loop", new[] { "Once", "Loop", "PingPong" } );
	public readonly IntParam Seed = new( "Seed", 12345, 0, 1000000 );
	public readonly FloatParam Size = new( "Size", 4f, 0.01f, unit: "u" );
	public readonly FloatParam Length = new( "Length", 8f, 0.01f, unit: "u" );
	public readonly FloatParam Thickness = new( "Thickness", 1f, 0.01f, unit: "u" );
	public readonly IntParam Sides = new( "Sides", 12, 3, 128 );
	public readonly FloatParam PartAngle = new( "Part angle", 90f, 0f, 360f, "deg" );
	public readonly FloatParam RotJitter = new( "Rot jitter", 0f, 0f, 180f, "deg" );
	public readonly FloatParam ScaleJitter = new( "Scale jitter", 0f, 0f, 1f );
	public readonly FloatParam ParticleSize = new( "Particle size", 1f, 0.01f, 10000f );
	public readonly StringParam Variants = new( "Variants", "" );
	public readonly IntParam Material = new( "Material", 0, 0, 255 );
	public readonly FloatParam Fill = new( "Fill", 1f, 0f, 1f );
	public readonly FloatParam Radius = new( "Radius", 5f, 0f, 100000f, "u" );
	public readonly FloatParam ConeAngle = new( "Cone angle", 0f, 0f, 89f, "deg" );
	public readonly StringParam SizeCurve = new( "Size curve", "" );
	public readonly IntParam Frames = new( "Frames", 1, 1, 1024 );
	public readonly FloatParam FrameStep = new( "Frame step", 1f, 0f, 100000f, "s" );
	public readonly FloatParam Streak = new( "Streak", 0f, 0f, 100f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Source, Shape, Target, Origin, Direction, Spread, Speed, SpeedJitter, Gravity, Drag, Life, LifeJitter, Mode, Rate, Count, Start, Duration, Sample, Loop, Seed, Size, Length, Thickness, Sides, PartAngle, RotJitter, ScaleJitter, ParticleSize, Variants, Material, Fill, Radius, ConeAngle, SizeCurve, Frames, FrameStep, Streak };

	readonly struct Particle
	{
		public readonly Vec3 Spawn, Normal, Velocity;
		public readonly float Birth, Life, BaseScale;
		public readonly Vec3 RotJitterEuler;
		public readonly PolyMesh Mesh;

		public Particle( Vec3 spawn, Vec3 normal, Vec3 velocity, float birth, float life, float baseScale, Vec3 rotJitterEuler, PolyMesh mesh )
		{
			Spawn = spawn;
			Normal = normal;
			Velocity = velocity;
			Birth = birth;
			Life = life;
			BaseScale = baseScale;
			RotJitterEuler = rotJitterEuler;
			Mesh = mesh;
		}
	}

	protected override void Execute( FeatureContext ctx )
	{
		var source = ResolveSource( ctx );
		var variantList = ParseVariants( Variants.Value, ctx );
		var sizeCurve = ParseSizeCurve( SizeCurve.Value );
		var dir = Direction.Value;

		if ( dir.LengthSquared < 1e-12f )
			Fail( "Emitter's direction is zero.", "A zero direction gives no cone axis.",
				"Give direction a non-zero vector, e.g. direction=0,0,1" );

		dir = dir.Normal;
		var shape = Shape.Value;
		PolyMesh targetMesh = null;

		if ( shape == "Surface" )
		{
			if ( Target.BodyIds.Count == 0 )
				Fail( "Emitter's Surface shape needs a target body.", "Target is empty, so there is no surface to emit from.",
					"Pick the body to emit from" );

			var targetBody = ctx.Bodies.FirstOrDefault( b => Target.BodyIds.Contains( b.Id ) );

			if ( targetBody is null )
				Fail( "Emitter's target body is not above it.",
					$"Target names '{string.Join( ",", Target.BodyIds )}', which is not a body that has run before this one.",
					"Put the target body first", "Check the target name" );

			targetMesh = targetBody!.Mesh;

			if ( targetMesh is null || targetMesh.VertexCount == 0 )
				Fail( "Emitter's target body has no mesh.", "There is no surface to emit from.",
					"Give the target body geometry first" );
		}
		else if ( Target.BodyIds.Count > 0 )
		{
			// A target on a non-Surface shape is ignored, not half-used.
			Warn( "Emitter's target is ignored for this shape.", "Target only matters for shape=Surface.",
				"Remove target, or set shape=Surface" );
		}

		if ( Mode.Value == "Continuous" && Duration.Clamped <= 0f )
			Fail( "Emitter's Continuous mode needs a duration above zero.", "Duration is zero, so no birth window exists.",
				"Set duration above zero, or use mode=Burst" );

		var rng = new Random( Seed.Clamped );
		var particles = GenerateParticles( ctx, source, variantList, targetMesh, dir, rng );
		var frames = Frames.Clamped;
		var emitted = 0;

		for ( var i = 0; i < frames; i++ )
		{
			var rawSample = frames == 1 ? Sample.Clamped : Start.Clamped + i * FrameStep.Clamped;
			var effSample = WrapSample( rawSample );
			var result = Bake( particles, sizeCurve, effSample );

			if ( result is null )
			{
				if ( frames == 1 )
					Fail( "Emitter emitted nothing.",
						$"At sample {effSample} no particle is alive: every birth is outside its life window.",
						"Move sample into [start, start + duration] and within life of a birth",
						"Raise life", "Check start and duration" );

				Warn( $"Emitter frame {i} emitted nothing, so it was skipped.",
					$"At sample {effSample} no particle is alive.",
					"Move the frame range into the alive window", "Raise life" );
				continue;
			}

			var bodyName = frames == 1 ? (Name ?? $"Emitter {Source.Value}") : $"{Name ?? $"Emitter {Source.Value}"}_{i}";
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), bodyName, result ) );
			emitted++;
		}

		if ( emitted == 0 )
			Fail( "Emitter emitted nothing.",
				"No frame had a particle alive.",
				"Move sample into [start, start + duration] and within life of a birth",
				"Raise life", "Check start and duration" );
	}

	float WrapSample( float sample )
	{
		var loop = Loop.Value;
		var duration = Duration.Clamped;

		if ( loop == "Once" || duration <= 0f )
			return sample;

		var t = (sample - Start.Clamped) % duration;

		if ( t < 0f )
			t += duration;

		if ( loop == "Loop" )
			return Start.Clamped + t;

		// PingPong: mirror over a 2*duration period, endpoints not doubled.
		var period = 2f * duration;
		var p = (sample - Start.Clamped) % period;

		if ( p < 0f )
			p += period;

		return Start.Clamped + (p <= duration ? p : period - p);
	}

	List<Particle> GenerateParticles( FeatureContext ctx, PolyMesh source, List<VariantPick> variantList, PolyMesh targetMesh, Vec3 dir, Random rng )
	{
		var count = Mode.Value == "Continuous"
			? Math.Clamp( (int)MathF.Round( Rate.Clamped * Duration.Clamped ), 1, 100000 )
			: Count.Clamped;
		var particles = new List<Particle>( count );
		var fill = Fill.Clamped;
		var radius = Radius.Clamped;
		var spreadRad = Spread.Clamped * MathF.PI / 180f;
		var cosSpread = MathF.Cos( spreadRad );

		for ( var i = 0; i < count; i++ )
		{
			var birth = Mode.Value == "Continuous"
				? Start.Clamped + (float)rng.NextDouble() * Duration.Clamped
				: Start.Clamped;

			// Spawn point + surface normal (normal only meaningful for Surface).
			var spawn = Origin.Value;
			var normal = dir;

			switch ( Shape.Value )
			{
				case "Point":
					break;
				case "Sphere":
				{
					var d = UniformSphereDir( rng );
					var r = radius * (fill * MathF.Cbrt( (float)rng.NextDouble() ) + (1f - fill));
					spawn = Origin.Value + d * r;
					break;
				}
				case "Box":
				{
					if ( rng.NextDouble() >= fill )
					{
						// Shell: a random face, a random point on it.
						var face = rng.Next( 6 );
						var a = (float)rng.NextDouble() * 2f - 1f;
						var b = (float)rng.NextDouble() * 2f - 1f;
						spawn = face switch
						{
							0 => Origin.Value + new Vec3( radius, a * radius, b * radius ),
							1 => Origin.Value + new Vec3( -radius, a * radius, b * radius ),
							2 => Origin.Value + new Vec3( a * radius, radius, b * radius ),
							3 => Origin.Value + new Vec3( a * radius, -radius, b * radius ),
							4 => Origin.Value + new Vec3( a * radius, b * radius, radius ),
							_ => Origin.Value + new Vec3( a * radius, b * radius, -radius ),
						};
					}
					else
					{
						spawn = Origin.Value + new Vec3(
							((float)rng.NextDouble() * 2f - 1f) * radius,
							((float)rng.NextDouble() * 2f - 1f) * radius,
							((float)rng.NextDouble() * 2f - 1f) * radius );
					}
					break;
				}
				case "Disc":
				{
					var a = (float)rng.NextDouble() * 2f * MathF.PI;
					var r = radius * (fill * MathF.Sqrt( (float)rng.NextDouble() ) + (1f - fill));
					spawn = Origin.Value + new Vec3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, 0f );
					break;
				}
				case "Cone":
				{
					var coneLen = Length.Clamped;
					var baseRadius = ConeAngle.Clamped > 0f
						? coneLen * MathF.Tan( ConeAngle.Clamped * MathF.PI / 180f )
						: radius;
					var t = (float)rng.NextDouble();
					var maxR = baseRadius * t;
					var a = (float)rng.NextDouble() * 2f * MathF.PI;
					var r = maxR * (fill * MathF.Sqrt( (float)rng.NextDouble() ) + (1f - fill));
					// Cone axis is Direction; build a perpendicular pair for the disc.
					var helper = MathF.Abs( dir.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
					var cu = Vec3.Cross( helper, dir ).Normal;
					var cv = Vec3.Cross( dir, cu );
					spawn = Origin.Value + dir * (t * coneLen) + cu * (MathF.Cos( a ) * r) + cv * (MathF.Sin( a ) * r );
					break;
				}
				case "Surface":
				{
					spawn = SampleSurface( targetMesh!, rng, out normal );
					break;
				}
				default:
					Fail( $"Emitter shape '{Shape.Value}' is not a shape.", "Point, Sphere, Box, Disc, Cone and Surface are.",
						"Pick one of those six" );
					break;
			}

			// Velocity: direction rotated by a solid-angle-uniform angle up to Spread.
			var velDir = dir;

			if ( Spread.Clamped > 0f )
			{
				var cosA = 1f - (float)rng.NextDouble() * (1f - cosSpread);
				var sinA = MathF.Sqrt( MathF.Max( 0f, 1f - cosA * cosA ) );
				var phi = (float)rng.NextDouble() * 2f * MathF.PI;
				var helper = MathF.Abs( dir.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
				var u = Vec3.Cross( helper, dir ).Normal;
				var v = Vec3.Cross( dir, u );
				velDir = dir * cosA + u * (MathF.Cos( phi ) * sinA) + v * (MathF.Sin( phi ) * sinA);
				velDir = velDir.Normal;
			}

			var speed = MathF.Max( 0f, Speed.Clamped * (1f + (float)(rng.NextDouble() * 2 - 1) * SpeedJitter.Clamped ) );
			var life = MathF.Max( 0.01f, Life.Clamped * (1f + (float)(rng.NextDouble() * 2 - 1) * LifeJitter.Clamped ) );
			var baseScale = MathF.Max( 0.01f, ParticleSize.Clamped * (1f + (float)(rng.NextDouble() * 2 - 1) * ScaleJitter.Clamped ) );
			var rotEuler = new Vec3(
				(float)(rng.NextDouble() * 2 - 1) * RotJitter.Clamped * MathF.PI / 180f,
				(float)(rng.NextDouble() * 2 - 1) * RotJitter.Clamped * MathF.PI / 180f,
				(float)(rng.NextDouble() * 2 - 1) * RotJitter.Clamped * MathF.PI / 180f );

			PolyMesh mesh = source;

			if ( variantList.Count > 0 )
			{
				var roll = (float)rng.NextDouble();
				PolyMesh picked = null;

				foreach ( var variant in variantList )
				{
					if ( roll <= variant.Cumulative )
					{
						picked = variant.Mesh;
						break;
					}
				}

				if ( picked is null )
					continue; // Empty variant: a deliberate gap.

				mesh = picked;
			}

			particles.Add( new Particle( spawn, normal, velDir * speed, birth, life, baseScale, rotEuler, mesh ) );
		}

		return particles;
	}

	Vec3 UniformSphereDir( Random rng )
	{
		var z = (float)rng.NextDouble() * 2f - 1f;
		var phi = (float)rng.NextDouble() * 2f * MathF.PI;
		var s = MathF.Sqrt( MathF.Max( 0f, 1f - z * z ) );
		return new Vec3( MathF.Cos( phi ) * s, MathF.Sin( phi ) * s, z );
	}

	Vec3 SampleSurface( PolyMesh target, Random rng, out Vec3 normal )
	{
		// Area-weighted triangle pick, as in Scatter's Poisson generator.
		var tris = new List<(int Face, Vec3 A, Vec3 B, Vec3 C, float Area)>();
		var total = 0f;

		foreach ( var fi in Enumerable.Range( 0, target.FaceCount ) )
		{
			var f = target.Faces[fi];

			for ( var k = 1; k + 1 < f.Count; k++ )
			{
				var a = target.Positions[f.Indices[0]];
				var b = target.Positions[f.Indices[k]];
				var c = target.Positions[f.Indices[k + 1]];
				var area = Vec3.Cross( b - a, c - a ).Length * 0.5f;

				if ( area <= 0f )
					continue;

				tris.Add( (fi, a, b, c, area) );
				total += area;
			}
		}

		if ( tris.Count == 0 || total <= 0f )
			Fail( "Emitter's target body has no surface area.", "Its faces are degenerate.",
				"Give the target body real faces first" );

		var pick = (float)rng.NextDouble() * total;
		var acc = 0f;
		var tri = tris[0];

		foreach ( var t in tris )
		{
			acc += t.Area;

			if ( acc >= pick )
			{
				tri = t;
				break;
			}
		}

		var r1 = (float)rng.NextDouble();
		var r2 = (float)rng.NextDouble();
		var sq = MathF.Sqrt( r1 );
		var point = tri.A * (1f - sq) + tri.B * (sq * (1f - r2)) + tri.C * (sq * r2);
		normal = target.FaceNormal( target.Faces[tri.Face] );
		return point;
	}

	PolyMesh Bake( List<Particle> particles, List<(float S, float Scale)> sizeCurve, float effSample )
	{
		var result = new PolyMesh();
		var alive = 0;
		var drag = Drag.Clamped;
		var gravity = Gravity.Value;

		foreach ( var p in particles )
		{
			var age = effSample - p.Birth;

			if ( age < 0f || age > p.Life + 1e-6f )
				continue;

			alive++;
			var vNow = VelocityAt( p.Velocity, gravity, drag, age );
			var pos = PositionAt( p.Spawn, p.Velocity, gravity, drag, age );

			// Orientation: along velocity, or along the surface normal for Surface.
			var zAxis = Shape.Value == "Surface" ? p.Normal : vNow;

			if ( zAxis.LengthSquared < 1e-12f )
				zAxis = Direction.Value.Normal;

			zAxis = zAxis.Normal;
			var helper = MathF.Abs( zAxis.y ) < 0.9f ? new Vec3( 0, 1, 0 ) : new Vec3( 1, 0, 0 );
			var xAxis = Vec3.Cross( helper, zAxis ).Normal;
			var yAxis = Vec3.Cross( zAxis, xAxis );

			if ( RotJitter.Clamped > 0f )
			{
				var jr = Xform.Rotate( new Vec3( 1, 0, 0 ), p.RotJitterEuler.x )
					* Xform.Rotate( new Vec3( 0, 1, 0 ), p.RotJitterEuler.y )
					* Xform.Rotate( new Vec3( 0, 0, 1 ), p.RotJitterEuler.z );
				xAxis = jr.TransformDirection( xAxis );
				yAxis = jr.TransformDirection( yAxis );
				zAxis = jr.TransformDirection( zAxis );
			}

			var scale = p.BaseScale * SizeAt( sizeCurve, Math.Clamp( age / p.Life, 0f, 1f ) );
			var stretch = 1f;

			if ( Streak.Clamped > 0f && p.Velocity.Length > 1e-6f )
			{
				var vRatio = Math.Clamp( vNow.Length / p.Velocity.Length, 0f, 1f );
				stretch = 1f + Streak.Clamped * vRatio * Math.Clamp( age / p.Life, 0f, 1f );
			}

			var instance = p.Mesh.Clone();

			if ( scale != 1f || stretch != 1f )
				MeshTransform.Apply( instance, Xform.Scale( new Vec3( scale, scale, scale * stretch ) ) );

			MeshTransform.Apply( instance, new Xform( xAxis, yAxis, zAxis, pos ) );
			MeshTransform.Append( result, instance );
		}

		if ( alive == 0 )
			return null;

		var tris = 0;

		foreach ( var f in result.Faces )
			tris += Math.Max( 0, f.Count - 2 );

		if ( tris > 200000 )
			Warn( $"Emitter emitted about {tris} triangles.", "Large emitters get heavy to edit, save and render.", "Lower Count or Rate", "Use a lower-poly source" );

		return result;
	}

	static Vec3 VelocityAt( Vec3 v0, Vec3 gravity, float drag, float age )
	{
		if ( drag < 1e-6f )
			return v0 + gravity * age;

		var e = MathF.Exp( -drag * age );
		return gravity / drag + (v0 - gravity / drag) * e;
	}

	static Vec3 PositionAt( Vec3 spawn, Vec3 v0, Vec3 gravity, float drag, float age )
	{
		if ( drag < 1e-6f )
			return spawn + v0 * age + gravity * (0.5f * age * age);

		var e = MathF.Exp( -drag * age );
		var gOverD = gravity / drag;
		return spawn + gOverD * age + (v0 - gOverD) * ((1f - e) / drag);
	}

	PolyMesh ResolveSource( FeatureContext ctx )
	{
		var name = Source.Value;

		if ( string.IsNullOrWhiteSpace( name ) )
			Fail( "Emitter needs a source.", "Source is empty, so there is nothing to emit.",
				"Name a library part (ToothCone, Rivet, Bolt)", "Or name an existing body to copy" );

		if ( PartLibrary.Names.Contains( name ) )
			return PartLibrary.Build( name, Size.Clamped, Length.Clamped, Thickness.Clamped, Sides.Clamped, PartAngle.Clamped, Material.Clamped );

		var body = ctx.Bodies.FirstOrDefault( b => b.Name == name )
			?? ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) );

		if ( body?.Mesh is null )
			Fail( $"Emitter's source '{name}' is neither a library part nor a body above it.",
				"Library parts are Cog, Bolt, Rivet, ToothCone and the rest of PartLibrary.Names; a body source must have run before this one.",
				"Check the source name", "Put the source body first" );

		return body!.Mesh;
	}

	readonly struct VariantPick
	{
		public readonly float Cumulative;
		public readonly PolyMesh Mesh;

		public VariantPick( float cumulative, PolyMesh mesh )
		{
			Cumulative = cumulative;
			Mesh = mesh;
		}
	}

	List<VariantPick> ParseVariants( string text, FeatureContext ctx )
	{
		var list = new List<VariantPick>();

		if ( string.IsNullOrWhiteSpace( text ) )
			return list;

		var raw = new List<(string Name, float Weight)>();
		var total = 0f;

		foreach ( var piece in text.Split( ',' ) )
		{
			var kv = piece.Trim().Split( ':' );

			float w = 0f;
			if ( kv.Length != 2 || !float.TryParse( kv[1], out w ) || w < 0f )
				Fail( $"Emitter's variants '{text}' do not parse.", "Variants want name:weight pairs, as in Bolt:0.7,LongBolt:0.2,Empty:0.1.", "Write Bolt:0.7,LongBolt:0.2,Empty:0.1" );

			raw.Add( (kv[0].Trim(), w) );
			total += w;
		}

		if ( total <= 0f )
			Fail( $"Emitter's variants '{text}' have no weight.", "At least one variant needs a weight above zero.", "Give one variant a weight, as in Bolt:1" );

		var acc = 0f;

		foreach ( var (name, weight) in raw )
		{
			acc += weight / total;
			PolyMesh mesh = null;

			if ( !string.Equals( name, "Empty", StringComparison.OrdinalIgnoreCase ) )
				mesh = ResolveVariantSource( ctx, name );

			list.Add( new VariantPick( acc, mesh ) );
		}

		return list;
	}

	PolyMesh ResolveVariantSource( FeatureContext ctx, string name )
	{
		if ( PartLibrary.Names.Contains( name ) )
			return PartLibrary.Build( name, Size.Clamped, Length.Clamped, Thickness.Clamped, Sides.Clamped, PartAngle.Clamped, Material.Clamped );

		var body = ctx.Bodies.FirstOrDefault( b => b.Name == name )
			?? ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) );

		if ( body?.Mesh is null )
			Fail( $"Emitter's variant '{name}' is neither a library part nor a body above it.", "Library parts are Cog, Bolt, Rivet, ToothCone and the rest of PartLibrary.Names; a body variant must have run before this one.", "Check the variant name", "Put the variant body first" );

		return body!.Mesh;
	}

	List<(float S, float Scale)> ParseSizeCurve( string text )
	{
		var curve = new List<(float, float)>();

		if ( string.IsNullOrWhiteSpace( text ) )
			return curve;

		foreach ( var piece in text.Split( ',' ) )
		{
			var kv = piece.Trim().Split( ':', StringSplitOptions.None );
			var s = 0f;
			var scale = 1f;

			if ( kv.Length != 2 || !float.TryParse( kv[0], out s ) || !float.TryParse( kv[1], out scale ) )
				Fail( $"Emitter's size curve '{text}' does not parse.", "Size curve wants s:multiplier pairs, as in 0:0.2,0.3:1,1:0.",
					"Write 0:0.2,0.3:1,1:0" );

			curve.Add( (s, scale) );
		}

		curve.Sort( (a, b) => a.Item1.CompareTo( b.Item1 ) );
		return curve;
	}

	float SizeAt( List<(float S, float Scale)> curve, float s )
	{
		if ( curve.Count == 0 )
			return 1f;

		if ( s <= curve[0].S )
			return curve[0].Scale;

		if ( s >= curve[^1].S )
			return curve[^1].Scale;

		for ( var i = 1; i < curve.Count; i++ )
		{
			if ( s > curve[i].S )
				continue;

			var (s0, v0) = curve[i - 1];
			var (s1, v1) = curve[i];
			return s1 == s0 ? v1 : v0 + (v1 - v0) * (s - s0) / (s1 - s0);
		}

		return curve[^1].Scale;
	}
}
