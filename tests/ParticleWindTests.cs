using System.Collections.Generic;
using System.Numerics;
using Marionette.Desktop;

// Lane P65 (particles session, 2026-10-24): tests for ParticleWind.cs.
// Same harness shape as ParticleForceFieldsTests: registry.Add("particle-wind", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   ParticleWindTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in TestRegistry.cs.
// (Other pending particle lanes claim the same anchor; the sweep orders
// the pending particle lines — this line's anchor is GpuParticleSystemTests,
// not any other pending lane's line.)
// Units throughout: inches, Z-up, seconds, wind in inches/second.
internal static class ParticleWindTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearVec( Vector3 got, Vector3 want, float tol, string what )
		{
			if ( (got - want).Length() > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		WindSineProfile Sine() => new() { BaseWind = new Vector3( 10, 0, 0 ), Direction = new Vector3( 0, 1, 0 ), Amplitude = 5, FrequencyHz = 1, PhaseRadians = 0 };
		WindBurstProfile Burst() => new() { BaseWind = new Vector3( 2, 0, 0 ), PeakWind = new Vector3( 12, 0, 0 ), AttackSeconds = 1, SustainSeconds = 1, DecaySeconds = 1, LullSeconds = 1 };
		ParticleSystem CalmSystem( float drag )
		{
			var sys = new ParticleSystem( new ParticleSystemSettings { Capacity = 100, Gravity = Vector3.Zero, Drag = drag, FixedDeltaTime = 1f / 60f, Substeps = 1, GroundEnabled = false, TurbulenceStrength = 0, VortexStrength = 0 } );
			sys.AddEmitter( new ParticleEmitter { Position = Vector3.Zero, Rate = 0, Bursts = new List<ParticleBurst> { new( 0, 1 ) }, LifetimeMin = 10, LifetimeMax = 10, SizeMin = 1, SizeMax = 1, Speed = 0, Direction = new Vector3( 0, 0, 1 ) } );
			return sys;
		}

		registry.Add( "particle-wind", "sine gust pins at quarter periods", () =>
		{
			var g = Sine();
			g.Validate();
			NearVec( g.At( 0 ), new Vector3( 10, 0, 0 ), 1e-4f, "sine t=0 is base" );
			NearVec( g.At( 0.25f ), new Vector3( 10, 5, 0 ), 1e-4f, "sine quarter period is base + amplitude" );
			NearVec( g.At( 0.5f ), new Vector3( 10, 0, 0 ), 1e-4f, "sine half period is base" );
			NearVec( g.At( 0.75f ), new Vector3( 10, -5, 0 ), 1e-4f, "sine three-quarter period is base - amplitude" );
			NearVec( g.At( 1 ), new Vector3( 10, 0, 0 ), 1e-4f, "sine full period is base" );
		} );

		registry.Add( "particle-wind", "sine phase shifts the curve", () =>
		{
			var g = Sine();
			g.PhaseRadians = MathF.PI / 2f;
			g.Validate();
			NearVec( g.At( 0 ), new Vector3( 10, 5, 0 ), 1e-4f, "phase pi/2: t=0 is the peak" );
			NearVec( g.At( 0.25f ), new Vector3( 10, 0, 0 ), 1e-4f, "phase pi/2: quarter period is base" );
		} );

		registry.Add( "particle-wind", "sine direction is normalized", () =>
		{
			var g = new WindSineProfile { BaseWind = Vector3.Zero, Direction = new Vector3( 1, 1, 0 ), Amplitude = 2, FrequencyHz = 1, PhaseRadians = 0 };
			g.Validate();
			NearVec( g.At( 0.25f ), new Vector3( 1.4142135f, 1.4142135f, 0 ), 1e-4f, "diagonal direction contributes amplitude / sqrt(2) per axis" );
		} );

		registry.Add( "particle-wind", "sine zero amplitude is steady base", () =>
		{
			var g = Sine();
			g.Amplitude = 0;
			g.Validate();
			Check( g.At( 0.37f ) == new Vector3( 10, 0, 0 ), $"zero-amplitude sine must be exactly base, got {g.At( 0.37f )}" );
			Check( g.At( 2.9f ) == new Vector3( 10, 0, 0 ), "zero-amplitude sine must be exactly base at every t" );
		} );

		registry.Add( "particle-wind", "burst pins at start, mid, and end", () =>
		{
			var g = Burst();
			g.Validate();
			NearVec( g.At( 0 ), new Vector3( 2, 0, 0 ), 1e-4f, "burst t=0 is base" );
			NearVec( g.At( 0.5f ), new Vector3( 7, 0, 0 ), 1e-4f, "burst mid-attack is halfway (raised cosine at u=0.5 is 0.5)" );
			NearVec( g.At( 1 ), new Vector3( 12, 0, 0 ), 1e-4f, "burst attack end is peak" );
			NearVec( g.At( 1.5f ), new Vector3( 12, 0, 0 ), 1e-4f, "burst mid-sustain is peak" );
			NearVec( g.At( 2 ), new Vector3( 12, 0, 0 ), 1e-4f, "burst decay start is peak" );
			NearVec( g.At( 2.5f ), new Vector3( 7, 0, 0 ), 1e-4f, "burst mid-decay is halfway" );
			NearVec( g.At( 3 ), new Vector3( 2, 0, 0 ), 1e-4f, "burst decay end is base" );
			NearVec( g.At( 3.5f ), new Vector3( 2, 0, 0 ), 1e-4f, "burst mid-lull is base" );
		} );

		registry.Add( "particle-wind", "burst repeats every period with a continuous seam", () =>
		{
			var g = Burst();
			g.Validate();
			Check( g.Period == 4, $"burst period must be 1+1+1+1=4, got {g.Period}" );
			Check( g.At( 4.5f ) == g.At( 0.5f ), "burst must repeat exactly: At(4.5) == At(0.5)" );
			Check( g.At( 4 ) == new Vector3( 2, 0, 0 ), "burst seam (t=period) is exactly base — no jump into the next burst" );
			NearVec( g.At( 2.999f ), new Vector3( 2, 0, 0 ), 1e-3f, "burst just before decay end is already ~base (continuous, no cliff)" );
			NearVec( g.At( 0.001f ), new Vector3( 2, 0, 0 ), 1e-3f, "burst just after start is already ~base (continuous, no snap-on)" );
		} );

		registry.Add( "particle-wind", "burst blends wind vectors, not just magnitudes", () =>
		{
			var g = new WindBurstProfile { BaseWind = new Vector3( 10, 0, 0 ), PeakWind = new Vector3( 0, 10, 0 ), AttackSeconds = 1, SustainSeconds = 0, DecaySeconds = 1, LullSeconds = 0 };
			g.Validate();
			Check( g.Period == 2, $"vector-blend burst period must be 2, got {g.Period}" );
			NearVec( g.At( 0 ), new Vector3( 10, 0, 0 ), 1e-4f, "blend burst start is base vector" );
			NearVec( g.At( 0.5f ), new Vector3( 5, 5, 0 ), 1e-4f, "blend burst mid-attack is the vector midpoint" );
			NearVec( g.At( 1 ), new Vector3( 0, 10, 0 ), 1e-4f, "blend burst peak is the peak vector" );
			NearVec( g.At( 1.5f ), new Vector3( 5, 5, 0 ), 1e-4f, "blend burst mid-decay is the vector midpoint" );
		} );

		registry.Add( "particle-wind", "burst sustain holds the peak", () =>
		{
			var g = Burst();
			g.Validate();
			foreach ( float t in new[] { 1f, 1.25f, 1.5f, 1.75f, 1.999f } )
				NearVec( g.At( t ), new Vector3( 12, 0, 0 ), 1e-4f, $"burst sustain at t={t} must be exactly peak" );
		} );

		registry.Add( "particle-wind", "flutter is bit-identical on repeat calls", () =>
		{
			var a = new WindFlutterProfile { BaseWind = new Vector3( 10, 0, 0 ), Direction = new Vector3( 0, 1, 0 ) };
			var b = new WindFlutterProfile { BaseWind = new Vector3( 10, 0, 0 ), Direction = new Vector3( 0, 1, 0 ) };
			a.Validate();
			b.Validate();
			foreach ( float t in new[] { 0f, 0.37f, 1.23f, 7.77f } )
			{
				Check( a.At( t ) == a.At( t ), $"flutter must be bit-identical on repeat calls at t={t}" );
				Check( a.At( t ) == b.At( t ), $"flutter must be identical across instances with the same settings at t={t}" );
			}
			Check( a.At( 0 ) != a.At( 0.5f ), "flutter must actually vary with time" );
			Check( (a.At( 0 ) - a.At( 0.5f )).Length() > 1f, $"flutter variation over 0.5 s must exceed 1 in/s, got {(a.At( 0 ) - a.At( 0.5f )).Length()}" );
		} );

		registry.Add( "particle-wind", "flutter stays inside its amplitude bound", () =>
		{
			var g = new WindFlutterProfile { BaseWind = new Vector3( 10, 0, 0 ), Direction = new Vector3( 0, 1, 0 ) };
			g.Validate();
			Check( g.AmplitudeBound == 6, $"default flutter amplitude bound must be 3+2+1=6, got {g.AmplitudeBound}" );
			float maxDev = 0;
			for ( int i = 0; i <= 10000; i++ )
			{
				float t = i * 0.001f;
				Vector3 w = g.At( t );
				float dev = (w - g.BaseWind).Length();
				if ( dev > maxDev ) maxDev = dev;
				Check( dev <= 6 + 1e-4f, $"flutter deviation {dev} at t={t} exceeds the amplitude bound 6" );
				Check( w.Length() <= g.BaseWind.Length() + 6 + 1e-4f, $"flutter magnitude {w.Length()} at t={t} exceeds |base| + bound" );
			}
			Check( maxDev > 3, $"flutter must actually use its range (max deviation over 10 s was {maxDev}, expected > 3)" );
		} );

		registry.Add( "particle-wind", "flutter single component equals the sine profile", () =>
		{
			var f = new WindFlutterProfile { BaseWind = new Vector3( 10, 0, 0 ), Direction = new Vector3( 0, 1, 0 ), Amplitudes = new[] { 5f }, FrequenciesHz = new[] { 1f }, PhasesRadians = new[] { 0f } };
			var s = Sine();
			f.Validate();
			s.Validate();
			foreach ( float t in new[] { 0f, 0.25f, 0.5f, 1.37f } )
				Check( f.At( t ) == s.At( t ), $"single-component flutter must equal the sine profile at t={t}" );
		} );

		registry.Add( "particle-wind", "combined profile is the exact sum of its components", () =>
		{
			var a = Sine();
			var b = Burst();
			var c = new WindFlutterProfile { BaseWind = Vector3.Zero, Direction = new Vector3( 0, 0, 1 ), Amplitudes = new[] { 2f }, FrequenciesHz = new[] { 2f }, PhasesRadians = new[] { 0f } };
			var ab = CombinedWindProfile.Sum( a, b );
			var abc = CombinedWindProfile.Sum( a, b, c );
			ab.Validate();
			abc.Validate();
			foreach ( float t in new[] { 0f, 0.5f, 1.25f, 3.7f } )
			{
				Check( ab.At( t ) == a.At( t ) + b.At( t ), $"combined sum must equal a+b bit-exactly at t={t}" );
				Check( abc.At( t ) == a.At( t ) + b.At( t ) + c.At( t ), $"combined sum must equal a+b+c bit-exactly at t={t}" );
			}
			var single = CombinedWindProfile.Sum( a );
			Check( single.At( 0.75f ) == a.At( 0.75f ), "single-profile combined must equal its component" );
		} );

		registry.Add( "particle-wind", "combined profile rejects empty, null, and invalid lists", () =>
		{
			Throws( () => new CombinedWindProfile( Array.Empty<IParticleWindProfile>() ).Validate(), "empty combined list" );
			Throws( () => new CombinedWindProfile( new IParticleWindProfile[] { null! } ).Validate(), "null combined entry" );
			Throws( () => new CombinedWindProfile( new IParticleWindProfile[] { null! } ).At( 0 ), "null combined entry at evaluation" );
			var bad = Sine();
			bad.FrequencyHz = -1;
			Throws( () => CombinedWindProfile.Sum( Sine(), bad ).Validate(), "invalid child" );
			Throws( () => new CombinedWindProfile( null! ).Validate(), "null profiles enumerable" );
			Throws( () => CombinedWindProfile.Sum( Sine() ).At( -1 ), "combined negative time" );
		} );

		registry.Add( "particle-wind", "sine profile fails loudly on bad config and bad time", () =>
		{
			Throws( () => { var g = Sine(); g.FrequencyHz = -1; g.Validate(); }, "negative frequency" );
			Throws( () => { var g = Sine(); g.FrequencyHz = 0; g.Validate(); }, "zero frequency" );
			Throws( () => { var g = Sine(); g.Amplitude = -1; g.Validate(); }, "negative amplitude" );
			Throws( () => { var g = Sine(); g.Amplitude = float.NaN; g.Validate(); }, "NaN amplitude" );
			Throws( () => { var g = Sine(); g.Direction = Vector3.Zero; g.Validate(); }, "zero direction" );
			Throws( () => { var g = Sine(); g.Direction = new Vector3( float.NaN, 0, 0 ); g.Validate(); }, "NaN direction" );
			Throws( () => { var g = Sine(); g.BaseWind = new Vector3( float.PositiveInfinity, 0, 0 ); g.Validate(); }, "infinite base wind" );
			Throws( () => { var g = Sine(); g.PhaseRadians = float.NaN; g.Validate(); }, "NaN phase" );
			Throws( () => Sine().At( -0.5f ), "negative time" );
			Throws( () => Sine().At( float.NaN ), "NaN time" );
			Throws( () => Sine().At( float.PositiveInfinity ), "infinite time" );
		} );

		registry.Add( "particle-wind", "burst profile fails loudly on bad config and bad time", () =>
		{
			Throws( () => { var g = Burst(); g.AttackSeconds = 0; g.Validate(); }, "zero attack (would snap on)" );
			Throws( () => { var g = Burst(); g.AttackSeconds = -1; g.Validate(); }, "negative attack" );
			Throws( () => { var g = Burst(); g.DecaySeconds = 0; g.Validate(); }, "zero decay (would snap off)" );
			Throws( () => { var g = Burst(); g.DecaySeconds = -1; g.Validate(); }, "negative decay" );
			Throws( () => { var g = Burst(); g.SustainSeconds = -1; g.Validate(); }, "negative sustain" );
			Throws( () => { var g = Burst(); g.LullSeconds = -1; g.Validate(); }, "negative lull" );
			Throws( () => { var g = Burst(); g.BaseWind = new Vector3( float.NaN, 0, 0 ); g.Validate(); }, "NaN base wind" );
			Throws( () => { var g = Burst(); g.PeakWind = new Vector3( 0, float.PositiveInfinity, 0 ); g.Validate(); }, "infinite peak wind" );
			Throws( () => Burst().At( -1 ), "negative time" );
			Throws( () => Burst().At( float.NaN ), "NaN time" );
		} );

		registry.Add( "particle-wind", "flutter profile fails loudly on bad config and bad time", () =>
		{
			Throws( () => { var g = new WindFlutterProfile(); g.Amplitudes = null!; g.Validate(); }, "null amplitudes" );
			Throws( () => { var g = new WindFlutterProfile(); g.Amplitudes = Array.Empty<float>(); g.FrequenciesHz = Array.Empty<float>(); g.PhasesRadians = Array.Empty<float>(); g.Validate(); }, "empty components" );
			Throws( () => { var g = new WindFlutterProfile(); g.FrequenciesHz = new[] { 1f, 2f }; g.Validate(); }, "mismatched frequency length" );
			Throws( () => { var g = new WindFlutterProfile(); g.PhasesRadians = new[] { 0f }; g.Validate(); }, "mismatched phase length" );
			Throws( () => { var g = new WindFlutterProfile(); g.Amplitudes = new[] { 3f, -2f, 1f }; g.Validate(); }, "negative component amplitude" );
			Throws( () => { var g = new WindFlutterProfile(); g.FrequenciesHz = new[] { 1.3f, 0f, 4.7f }; g.Validate(); }, "zero component frequency" );
			Throws( () => { var g = new WindFlutterProfile(); g.PhasesRadians = new[] { 0f, float.NaN, 2.2f }; g.Validate(); }, "NaN component phase" );
			Throws( () => { var g = new WindFlutterProfile(); g.Direction = Vector3.Zero; g.Validate(); }, "zero direction" );
			Throws( () => new WindFlutterProfile().At( -1 ), "negative time" );
		} );

		registry.Add( "particle-wind", "integration: constant gust wind drives a particle by the exact drag rule", () =>
		{
			// Drag rule (ParticleSystem.Integrate, gravity 0): vel_k = W * (1 - r^k),
			// r = 1 / (1 + Drag*h), h = 1/60, Drag = 2 -> r = 30/31.
			// After 1 step: vel = 20 * (2/60) / (1 + 2/60) = 0.6451613, pos = vel * h.
			// After 60 steps: vel = 20 * (1 - r^60) = 17.2026, pos = 11.3987 (summed vel*h).
			var gust = new WindSineProfile { BaseWind = new Vector3( 20, 0, 0 ), Direction = new Vector3( 1, 0, 0 ), Amplitude = 0, FrequencyHz = 1, PhaseRadians = 0 };
			gust.Validate();
			var sys = CalmSystem( 2f );
			sys.Settings.Wind = gust.At( (float)sys.Time );
			sys.Step();
			Check( sys.AliveCount == 1, $"burst must have spawned exactly 1 particle, alive={sys.AliveCount}" );
			Near( sys.States[0].Velocity.X, 0.6451613f, 1e-4f, "velocity after 1 step under constant 20 in/s gust, drag 2" );
			Near( sys.States[0].Position.X, 0.0107527f, 1e-4f, "position after 1 step" );
			Near( sys.States[0].Velocity.Y, 0, 1e-6f, "no cross-wind velocity Y" );
			Near( sys.States[0].Velocity.Z, 0, 1e-6f, "no cross-wind velocity Z" );
			for ( int i = 1; i < 60; i++ )
			{
				sys.Settings.Wind = gust.At( (float)sys.Time );
				sys.Step();
			}
			Near( sys.States[0].Velocity.X, 17.2026f, 1e-3f, "velocity after 60 steps approaches the 20 in/s gust (drag 2, 1 s)" );
			Near( sys.States[0].Position.X, 11.3987f, 1e-3f, "position after 60 steps under constant gust" );
			Check( sys.States[0].Velocity.X < 20, "velocity must stay below the gust wind (drag approaches, never overshoots)" );
			Check( sys.States[0].Velocity.X > 0, "velocity must be positive downwind" );
		} );

		registry.Add( "particle-wind", "integration: time-varying gust drifts a particle downwind; zero wind holds still", () =>
		{
			var gust = new WindSineProfile { BaseWind = new Vector3( 10, 0, 0 ), Direction = new Vector3( 1, 0, 0 ), Amplitude = 5, FrequencyHz = 0.5f, PhaseRadians = 0 };
			gust.Validate();
			NearVec( gust.At( 0 ), new Vector3( 10, 0, 0 ), 1e-4f, "gust at t=0 is 10 in/s" );
			NearVec( gust.At( 0.5f ), new Vector3( 15, 0, 0 ), 1e-4f, "gust at t=0.5 s peaks at 15 in/s" );
			NearVec( gust.At( 1.5f ), new Vector3( 5, 0, 0 ), 1e-4f, "gust at t=1.5 s troughs at 5 in/s" );
			var sys = CalmSystem( 2f );
			for ( int i = 0; i < 120; i++ )
			{
				sys.Settings.Wind = gust.At( (float)sys.Time );
				sys.Step();
			}
			float vx = sys.States[0].Velocity.X, px = sys.States[0].Position.X;
			Check( vx > 3 && vx < 17, $"after 2 s under a 5..15 in/s gust, velocity must track the gust band, got {vx}" );
			Check( px > 5 && px < 25, $"after 2 s the particle must have drifted substantially downwind, got x={px}" );
			Near( sys.States[0].Position.Y, 0, 1e-6f, "no cross-wind drift Y" );
			var still = CalmSystem( 2f );
			for ( int i = 0; i < 60; i++ )
			{
				still.Settings.Wind = Vector3.Zero;
				still.Step();
			}
			Check( still.States[0].Position == Vector3.Zero, $"zero wind must hold the particle at the origin, got {still.States[0].Position}" );
			Check( still.States[0].Velocity == Vector3.Zero, $"zero wind must leave velocity at zero, got {still.States[0].Velocity}" );
		} );
	}
}
