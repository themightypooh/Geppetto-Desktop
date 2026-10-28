using System;
using System.Numerics;
using Marionette.Desktop;

// Lane P68 (particles session, 2026-10-24): generic 3D domain warp.
// Group: particle-warp.
// Sweep line (scratch only — do NOT add to the canonical TestRegistry.cs):
//   ParticleWarpTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line.
// Integration (coordinator sweep only, NOT applied here): this lane owns
// a pure field transform, not a force, so there is no drop-in Integrate
// line. The suggested future hookup, once a caller owns a warp settings
// field, is to evaluate a turbulence field through the transform, e.g.
//   Vector3 turb = ParticleWarp.WarpField( pos,
//       q => ParticleMath.Curl( q * p.TurbFreq, p.Seed ),
//       q => ParticleNoise.CurlA( q, noiseConfig ), warpSettings );
// or, for a P29 field, ParticleWarp.WarpCurlFbm( pos, noiseConfig,
// warpSettings ). No such settings field exists on any settings type
// today, so no one-liner is claimed as drop-in. This file is NOT wired
// into ParticleSystem.Integrate, and its tests prove the transform only.

internal static class ParticleWarpTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void NearVec( Vector3 got, Vector3 want, float tol, string what ) { if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}

		// Hand-computable fields only — no noise anywhere in this file's
		// core tests, so every expected value below is analytic.
		Vector3 Linear( Vector3 p ) => p;
		Vector3 XOnly( Vector3 p ) => new( p.X, 0, 0 );
		Vector3 Poly( Vector3 p ) => new( 2 * p.X + 1, p.Y * p.Y - 3, p.X * p.Z + 5 );
		Vector3 ConstX( Vector3 p ) => new( 1, 0, 0 );
		Vector3 SinY( Vector3 p ) => new( MathF.Sin( p.Y ), 0, 0 );
		Vector3 SinX( Vector3 p ) => new( MathF.Sin( p.X ), 0, 0 );
		Vector3 XWarp( Vector3 p ) => new( p.X, 0, 0 );
		float ScalarX( Vector3 p ) => p.X;

		Vector3[] Probes() => new[]
		{
			new Vector3( 0, 0, 0 ), new Vector3( 1, 2, 3 ), new Vector3( -4, 0, 7 ),
			new Vector3( 3, 7, 11 ), new Vector3( -5, 20, 2 ), new Vector3( 10, -3, 1 ),
		};

		registry.Add( "particle-warp", "amplitude zero is identity, bit-identical", () =>
		{
			var s = new ParticleWarpSettings { Amplitude = 0, WarpFrequency = 1 };
			foreach ( var p in Probes() )
			{
				var warped = ParticleWarp.WarpField( p, Poly, SinY, s );
				var direct = Poly( p );
				Check( warped == direct, $"amp 0 not bit-identical at {p}: {warped} vs {direct}" );
				var chain = ParticleWarp.WarpChain( p, Poly, SinY, new[] { 0f, 0f }, 1f );
				Check( chain == direct, $"amp-0 chain not bit-identical at {p}: {chain} vs {direct}" );
			}
			Console.WriteLine( "PROBE amp0 identity holds bit-identically at all 6 probes (vector + chain)" );
		} );

		registry.Add( "particle-warp", "constant warp shifts a linear field by exactly amplitude", () =>
		{
			// f(p) = p, w(p) = (1,0,0), amplitude 2.5: output = p + (2.5,0,0).
			var s = new ParticleWarpSettings { Amplitude = 2.5f, WarpFrequency = 1 };
			foreach ( var p in Probes() )
			{
				var got = ParticleWarp.WarpField( p, Linear, ConstX, s );
				var want = p + new Vector3( 2.5f, 0, 0 );
				Check( got == want, $"constant shift at {p}: got {got}, want {want} exactly" );
			}
			var a = ParticleWarp.WarpField( new Vector3( 1, 2, 3 ), Linear, ConstX, s );
			var b = ParticleWarp.WarpField( new Vector3( -4, 0, 7 ), Linear, ConstX, s );
			Console.WriteLine( $"PROBE constant shift (1,2,3)->{a} (-4,0,7)->{b} (exact: +2.5 on X)" );
			Check( a == new Vector3( 3.5f, 2, 3 ), "constant shift probe 1 not exact" );
			Check( b == new Vector3( -1.5f, 0, 7 ), "constant shift probe 2 not exact" );
		} );

		registry.Add( "particle-warp", "sinusoidal warp gives the analytic value", () =>
		{
			// f(p) = (p.X,0,0), w(p) = (sin(p.Y),0,0), amplitude 2, freq 1:
			// f(p') = (p.X + 2*sin(p.Y), 0, 0).
			var s = new ParticleWarpSettings { Amplitude = 2f, WarpFrequency = 1 };
			var cases = new[]
			{
				(new Vector3( 3, MathF.PI / 2, 0 ), new Vector3( 5, 0, 0 )),
				(new Vector3( 3, 0, 0 ), new Vector3( 3, 0, 0 )),
				(new Vector3( 3, -MathF.PI / 2, 0 ), new Vector3( 1, 0, 0 )),
			};
			foreach ( var (p, want) in cases )
			{
				var got = ParticleWarp.WarpField( p, XOnly, SinY, s );
				NearVec( got, want, 1e-5f, $"sinusoidal warp at {p}" );
				Console.WriteLine( $"PROBE sinusoidal f(p)=(p.X,0,0) w=sin(p.Y) amp=2 at {p} -> {got} (want {want})" );
			}
		} );

		registry.Add( "particle-warp", "warp frequency scales only the warp field input", () =>
		{
			// f(p) = p, w(p) = (p.X,0,0), amplitude 1, freq 0.5, p=(2,3,4):
			// warp input = (1,1.5,2), w = (1,0,0), p' = (3,3,4) exactly.
			// The base field is NOT frequency-scaled: Y and Z pass through.
			var got = ParticleWarp.WarpField( new Vector3( 2, 3, 4 ), Linear, XWarp, 1f, 0.5f );
			Check( got == new Vector3( 3, 3, 4 ), $"frequency test: got {got}, want (3,3,4) exactly" );
			var freq0 = ParticleWarp.WarpField( new Vector3( 2, 3, 4 ), Linear, SinX, 1f, 0f );
			Check( freq0 == new Vector3( 2, 3, 4 ), $"freq 0 should sample warp at origin: got {freq0}" );
			Console.WriteLine( $"PROBE frequency: (2,3,4) freq=0.5 -> {got}; freq=0 -> {freq0}" );
		} );

		registry.Add( "particle-warp", "chained constant warp equals one stage of summed amplitude", () =>
		{
			// Constant w: two stages of 1.5 = one stage of 3.0, bit-identical.
			foreach ( var p in Probes() )
			{
				var chain = ParticleWarp.WarpChain( p, Linear, ConstX, new[] { 1.5f, 1.5f }, 1f );
				var single = ParticleWarp.WarpField( p, Linear, ConstX, 3f, 1f );
				Check( chain == single, $"constant chain at {p}: chain {chain} vs single {single}" );
			}
			var pos = ParticleWarp.WarpChainPosition( new Vector3( 1, 2, 3 ), ConstX, new[] { 1.5f, 1.5f, 2f }, 1f );
			Check( pos == new Vector3( 6, 2, 3 ), $"3-stage constant chain position: got {pos}, want (6,2,3)" );
			Console.WriteLine( "PROBE constant chain: 1.5+1.5 == 3.0 bit-identically at all probes; 1.5+1.5+2 shifts (1,2,3) to (6,2,3)" );
		} );

		registry.Add( "particle-warp", "chained varying warp does NOT equal one summed stage", () =>
		{
			// w(p) = (sin(p.X),0,0), p=(1,0,0), freq 1, stages 1.5+1.5:
			// single 3.0: 1 + 3*sin(1) = 3.5244; chain: p1 = 1+1.5*sin(1)
			// = 2.2622, p2 = p1+1.5*sin(p1) = 3.417...; difference ~0.107.
			var p = new Vector3( 1, 0, 0 );
			var chain = ParticleWarp.WarpChain( p, Linear, SinX, new[] { 1.5f, 1.5f }, 1f );
			var single = ParticleWarp.WarpField( p, Linear, SinX, 3f, 1f );
			float diff = (chain - single).Length();
			Console.WriteLine( $"PROBE varying chain: chain={chain} single={single} diff={diff} (must be > 0.01)" );
			Check( diff > 0.01f, $"varying chain unexpectedly equal to summed stage: diff {diff}" );
			NearVec( single, new Vector3( 1 + 3 * MathF.Sin( 1f ), 0, 0 ), 1e-5f, "single-stage analytic value" );
			Check( chain.X > 3.3f && chain.X < 3.5f, $"chain value outside analytic band: {chain}" );
		} );

		registry.Add( "particle-warp", "chain position composes stage by stage", () =>
		{
			// Manual two-step composition must match WarpChainPosition
			// bit-identically for a varying warp field.
			var p = new Vector3( 2, 1, -3 );
			var p1 = ParticleWarp.WarpPosition( p, SinX, 1.5f, 1f );
			var p2 = ParticleWarp.WarpPosition( p1, SinX, 0.5f, 1f );
			var chain = ParticleWarp.WarpChainPosition( p, SinX, new[] { 1.5f, 0.5f }, 1f );
			Check( chain == p2, $"chain position {chain} != manual two-step {p2}" );
			var viaSettings = ParticleWarp.WarpChainPosition( p, SinX, new ParticleWarpSettings { Amplitude = 9f, WarpFrequency = 1f, StageAmplitudes = new[] { 1.5f, 0.5f } } );
			Check( viaSettings == chain, "settings chain != explicit chain" );
			Console.WriteLine( $"PROBE chain composition: p={p} p1={p1} p2=chain={chain}" );
		} );

		registry.Add( "particle-warp", "determinism: repeat evaluations are bit-identical", () =>
		{
			var s = new ParticleWarpSettings { Amplitude = 2f, WarpFrequency = 0.7f, StageAmplitudes = new[] { 1f, 2f } };
			foreach ( var p in Probes() )
			{
				Check( ParticleWarp.WarpField( p, Poly, SinY, s ) == ParticleWarp.WarpField( p, Poly, SinY, s ), $"WarpField not bit-identical at {p}" );
				Check( ParticleWarp.WarpChain( p, Poly, SinY, s ) == ParticleWarp.WarpChain( p, Poly, SinY, s ), $"WarpChain not bit-identical at {p}" );
				Check( ParticleWarp.WarpScalar( p, ScalarX, SinY, s ) == ParticleWarp.WarpScalar( p, ScalarX, SinY, s ), $"WarpScalar not bit-identical at {p}" );
			}
			Console.WriteLine( "PROBE determinism: vector, chain, and scalar warps bit-identical on repeat at all probes" );
		} );

		registry.Add( "particle-warp", "Validate rejects bad settings and null fields", () =>
		{
			Throws( () => new ParticleWarpSettings { Amplitude = -1, WarpFrequency = 1 }.Validate(), "negative amplitude" );
			Throws( () => new ParticleWarpSettings { Amplitude = float.NaN, WarpFrequency = 1 }.Validate(), "NaN amplitude" );
			Throws( () => new ParticleWarpSettings { Amplitude = float.PositiveInfinity, WarpFrequency = 1 }.Validate(), "+Inf amplitude" );
			Throws( () => new ParticleWarpSettings { Amplitude = 1, WarpFrequency = float.NaN }.Validate(), "NaN frequency" );
			Throws( () => new ParticleWarpSettings { Amplitude = 1, WarpFrequency = float.PositiveInfinity }.Validate(), "+Inf frequency" );
			Throws( () => new ParticleWarpSettings { Amplitude = 1, WarpFrequency = -0.5f }.Validate(), "negative frequency" );
			Throws( () => new ParticleWarpSettings { Amplitude = 1, WarpFrequency = 1, StageAmplitudes = new[] { 1f, -2f } }.Validate(), "negative stage amplitude" );
			Throws( () => new ParticleWarpSettings { Amplitude = 1, WarpFrequency = 1, StageAmplitudes = new[] { float.NaN } }.Validate(), "NaN stage amplitude" );
			var p = new Vector3( 1, 2, 3 );
			var s = new ParticleWarpSettings { Amplitude = 1, WarpFrequency = 1 };
			Throws( () => ParticleWarp.WarpField( p, null!, SinY, s ), "null base field" );
			Throws( () => ParticleWarp.WarpField( p, Linear, null!, s ), "null warp field" );
			Throws( () => ParticleWarp.WarpScalar( p, null!, SinY, s ), "null scalar field" );
			Throws( () => ParticleWarp.WarpChain( p, Linear, SinY, null!, 1f ), "null stage array" );
			Throws( () => ParticleWarp.WarpChain( p, Linear, SinY, Array.Empty<float>(), 1f ), "empty stage array" );
			Throws( () => ParticleWarp.WarpChain( p, Linear, SinY, new ParticleWarpSettings { Amplitude = 1, WarpFrequency = 1, StageAmplitudes = null! } ), "null stages in settings chain" );
			Throws( () => ParticleWarp.WarpField( p, Linear, SinY, -1f, 1f ), "negative amplitude overload" );
			Throws( () => ParticleWarp.WarpField( p, Linear, SinY, 1f, float.NaN ), "NaN frequency overload" );
		} );

		registry.Add( "particle-warp", "scalar field warp: identity, constant shift, chain", () =>
		{
			var p = new Vector3( 5, 2, 0 );
			var id = ParticleWarp.WarpScalar( p, ScalarX, SinY, new ParticleWarpSettings { Amplitude = 0, WarpFrequency = 1 } );
			Check( id == 5f, $"scalar amp-0 identity: got {id}, want 5 exactly" );
			var shifted = ParticleWarp.WarpScalar( p, ScalarX, ConstX, new ParticleWarpSettings { Amplitude = 2, WarpFrequency = 1 } );
			Check( shifted == 7f, $"scalar constant shift: got {shifted}, want 7 exactly" );
			var chain = ParticleWarp.WarpScalarChain( p, ScalarX, ConstX, new[] { 1.5f, 1.5f }, 1f );
			Check( chain == 8f, $"scalar constant chain: got {chain}, want 8 exactly" );
			var sin = ParticleWarp.WarpScalar( new Vector3( 3, MathF.PI / 2, 0 ), ScalarX, SinY, new ParticleWarpSettings { Amplitude = 2, WarpFrequency = 1 } );
			Near( sin, 5f, 1e-5f, "scalar sinusoidal warp" );
			Console.WriteLine( $"PROBE scalar: identity={id} constant-shift={shifted} chain={chain} sinusoidal={sin}" );
		} );

		registry.Add( "particle-warp", "warp position: identity and constant shift", () =>
		{
			var p = new Vector3( 4, -2, 9 );
			Check( ParticleWarp.WarpPosition( p, SinY, 0f, 1f ) == p, "WarpPosition amp 0 not identity" );
			Check( ParticleWarp.WarpPosition( p, ConstX, 2.5f, 1f ) == new Vector3( 6.5f, -2, 9 ), "WarpPosition constant shift not exact" );
			Console.WriteLine( "PROBE WarpPosition: amp 0 -> (4,-2,9); const amp 2.5 -> (6.5,-2,9)" );
		} );

		registry.Add( "particle-warp", "noise convenience delegates to the generic warp", () =>
		{
			// ParticleNoise.cs IS present in the canonical tree, so the
			// helpers are built and tested. They must be exactly the
			// generic transform applied to CurlA/CurlFbm — no new kernel.
			var cfg = new ParticleNoiseConfig { Seed = 4242u, Scale = 0.1f, Octaves = 4, Lacunarity = 2f, Gain = 0.5f, WarpAmplitude = 2f, WarpFrequency = 0.05f, TimeScale = 1f };
			var p = new Vector3( 3, 7, 11 );
			var zero = new ParticleWarpSettings { Amplitude = 0, WarpFrequency = 1 };
			Check( ParticleWarp.WarpCurlA( p, cfg, zero ) == ParticleNoise.CurlA( p, cfg ), "WarpCurlA amp 0 != CurlA" );
			Check( ParticleWarp.WarpCurlFbm( p, cfg, zero ) == ParticleNoise.CurlFbm( p, cfg ), "WarpCurlFbm amp 0 != CurlFbm" );
			var s = new ParticleWarpSettings { Amplitude = 2, WarpFrequency = 1 };
			var helper = ParticleWarp.WarpCurlA( p, cfg, s );
			var generic = ParticleWarp.WarpField( p, q => ParticleNoise.CurlA( q, cfg ), q => ParticleNoise.CurlA( q, cfg ), s );
			Check( helper == generic, $"WarpCurlA != generic WarpField: {helper} vs {generic}" );
			var helperFbm = ParticleWarp.WarpCurlFbm( p, cfg, s );
			var genericFbm = ParticleWarp.WarpField( p, q => ParticleNoise.CurlFbm( q, cfg ), q => ParticleNoise.CurlA( q, cfg ), s );
			Check( helperFbm == genericFbm, "WarpCurlFbm != generic WarpField" );
			Check( helper != ParticleNoise.CurlA( p, cfg ), "warped CurlA unexpectedly identical to unwarped" );
			Check( ParticleMath.Finite( helper ) && ParticleMath.Finite( helperFbm ), "warped noise helpers non-finite" );
			Console.WriteLine( $"PROBE noise delegation: CurlA={ParticleNoise.CurlA( p, cfg )} WarpCurlA={helper} WarpCurlFbm={helperFbm}" );
		} );
	}
}
