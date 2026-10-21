using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>R9: `--live particles,hair,cloth,instances` — demo sources placed around the loaded
/// model (sized from its bounds, Z-up, inches) so each live feature can be seen and captured.</summary>
public static class LiveFixtures
{
	public static readonly string[] Kinds = { "particles", "hair", "cloth", "instances" };

	public static void Add( LiveGeometry live, string kinds, Vector3 boundsMin, Vector3 boundsMax )
	{
		var centre = ( boundsMin + boundsMax ) * .5f;
		float r = MathF.Max( 1e-3f, ( boundsMax - boundsMin ).Length() * .5f );
		foreach ( var raw in kinds.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			switch ( raw.ToLowerInvariant() )
			{
				case "particles": live.Layers.Add( Particles( centre, boundsMin.Z, r ) ); break;
				case "hair": live.Layers.Add( Hair( centre, boundsMax.Z, r ) ); break;
				case "cloth": live.Layers.Add( Cloth( centre, boundsMax.Z, r ) ); break;
				case "instances": live.Layers.Add( Instances( centre, boundsMin.Z, r ) ); break;
				default: throw new ArgumentException( $"--live: unknown kind '{raw}' (expected {string.Join( ", ", Kinds )})" );
			}
		}
	}

	/// <summary>Ember fountain beside the model, pre-warmed so frame 0 already shows the plume.</summary>
	public static ParticleLayer Particles( Vector3 centre, float groundZ, float r )
	{
		var settings = new ParticleSystemSettings { Capacity = 4000, Seed = 7, GroundEnabled = true, GroundOffset = groundZ, Drag = .4f, Restitution = .35f };
		var system = new ParticleSystem( settings );
		float g = -settings.Gravity.Z;
		system.AddEmitter( new ParticleEmitter
		{
			Position = new Vector3( centre.X + r * .75f, centre.Y - r * .2f, groundZ ),
			Rate = 500, LifetimeMin = 1.2f, LifetimeMax = 2f,
			SizeMin = r * .02f, SizeMax = r * .04f,
			Direction = Vector3.UnitZ, Speed = MathF.Sqrt( 2 * g * r * 1.1f ), SpeedJitter = .25f, SpreadDegrees = 14,
		} );
		system.Step( 120 );
		return new ParticleLayer( system )
		{
			Material = new LiveMaterial( new Vector3( 1f, .35f, .08f ), Roughness: .9f, Emission: new Vector3( 3f, .8f, .12f ), Alpha: .9f, Blend: true ) { Sprite = true },
		};
	}

	/// <summary>A tuft of strands rising from the top of the model and drooping under gravity.</summary>
	public static StrandLayer Hair( Vector3 centre, float topZ, float r )
	{
		var hair = new HairStrandService { PreserveLength = true };
		var rng = new ParticleRng( 11 );
		const int strands = 140, points = 14;
		float len = r * .2f;
		for ( int s = 0; s < strands; s++ )
		{
			float a = rng.Range( 0, MathF.Tau ), d = MathF.Sqrt( rng.NextFloat() ) * r * .05f;
			var root = new Vector3( centre.X + MathF.Cos( a ) * d, centre.Y + MathF.Sin( a ) * d, topZ + r * .004f );
			var dir = Vector3.Normalize( new Vector3( MathF.Cos( a ) * .9f, MathF.Sin( a ) * .9f, 1f ) );
			var pts = new List<(float, float, float)>( points );
			for ( int i = 0; i < points; i++ ) pts.Add( StrandLayer.Swap( root + dir * ( len * i / ( points - 1 ) ) ) );
			hair.AddStrand( pts );
		}
		// The service is position-based (no velocity): gravity is a per-step creep, so it is scaled up.
		var layer = new StrandLayer( hair, r * .006f )
		{
			Gravity = -386.0886f * 12f,
			Environment = _ => ( new Vector3( centre.X, centre.Y, topZ - r * .11f ), r * .115f, Vector3.Zero ),
			Material = new LiveMaterial( new Vector3( .16f, .09f, .05f ), Roughness: .4f ) { Lobes = MaterialLobes.None with { Sheen = .25f, SheenTint = new Vector3( .6f, .45f, .3f ) } },
		};
		for ( int i = 0; i < 90; i++ ) layer.Advance( 1f / 60 );
		return layer;
	}

	/// <summary>A banner pinned along its top edge, hanging beside the model in a light breeze.</summary>
	public static ClothLayer Cloth( Vector3 centre, float topZ, float r )
	{
		const int n = 18;
		float w = r * .7f, h = r * .9f;
		var mesh = new PolyMesh();
		var origin = new Vector3( centre.X - r * .8f, centre.Y - w * .5f, topZ );
		for ( int j = 0; j <= n; j++ )
			for ( int i = 0; i <= n; i++ )
				mesh.AddVertex( new Vec3( origin.X, origin.Y + w * i / n, origin.Z - h * j / n ) );
		for ( int j = 0; j < n; j++ )
			for ( int i = 0; i < n; i++ )
			{
				int a = j * ( n + 1 ) + i;
				mesh.AddFace( new[] { a, a + 1, a + n + 2, a + n + 1 } );
			}
		var pinned = new List<int>();
		for ( int i = 0; i <= n; i++ ) pinned.Add( i );
		var options = new ClothSimService.Options { Gravity = new Vec3( 0, 0, -386.0886f ), ConstraintIterations = 8, Damping = .98f, Wind = new Vec3( r * 5f, 0, 0 ), WindGustStrength = 1f, WindGustFrequency = 2.5f, WindTurbulence = .3f };
		var layer = new ClothLayer( ClothSimService.Create( mesh, options, pinned ), options )
		{
			Material = new LiveMaterial( new Vector3( .55f, .06f, .05f ), Roughness: .8f ) { Lobes = MaterialLobes.None with { Sheen = .8f, SheenTint = new Vector3( 1f, .5f, .5f ), SheenRoughness = .4f } },
		};
		for ( int i = 0; i < 90; i++ ) layer.Advance( 1f / 60 );
		return layer;
	}

	/// <summary>Geometry-nodes style instances: a ring of rotated, scaled cubes around the model's feet.</summary>
	public static GeometrySetLayer Instances( Vector3 centre, float groundZ, float r )
	{
		var cube = new PolyMesh();
		for ( int i = 0; i < 8; i++ ) cube.AddVertex( new Vec3( ( i & 1 ) == 0 ? -.5f : .5f, ( i & 2 ) == 0 ? -.5f : .5f, ( i & 4 ) == 0 ? 0f : 1f ) );
		int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
		foreach ( var f in faces ) cube.AddFace( f );
		const int count = 16;
		var transforms = new Xform[count];
		var reference = new int[count];
		for ( int i = 0; i < count; i++ )
		{
			float a = MathF.Tau * i / count, s = r * ( .05f + .03f * GeoHash.Unit( 3, (uint)i ) );
			var rot = Xform.Rotate( new Vec3( 0, 0, 1 ), a * 2.3f );
			transforms[i] = new Xform( rot.X * s, rot.Y * s, rot.Z * s * ( 1f + 2f * GeoHash.Unit( 5, (uint)i ) ),
				new Vec3( centre.X + MathF.Cos( a ) * r * .7f, centre.Y + MathF.Sin( a ) * r * .7f, groundZ ) );
		}
		var set = new GeometrySet( null, null, new GeoInstances( new[] { GeometrySet.FromMesh( cube ) }, reference, transforms ) );
		return new GeometrySetLayer( set ) { Material = new LiveMaterial( new Vector3( .12f, .5f, .55f ), Roughness: .35f ) { Lobes = MaterialLobes.None with { Clearcoat = 1f } } };
	}
}
