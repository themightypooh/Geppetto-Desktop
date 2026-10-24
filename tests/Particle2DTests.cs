using System.Numerics;
using Marionette.Desktop;

internal static class Particle2DTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		Particle2DSystem System( Particle2DSettings s ) => new( s );
		Particle2DSystem Seeded( uint seed, int capacity = 1000 ) => new( new Particle2DSettings { Seed = seed, Capacity = capacity } );
		Particle2DDrawRecord[] Records( Particle2DSystem sys )
		{
			var arr = new Particle2DDrawRecord[sys.AliveCount];
			int n = sys.WriteDrawRecords( arr );
			Check( n == sys.AliveCount, $"WriteDrawRecords returned {n} but AliveCount is {sys.AliveCount}" );
			return arr;
		}

		registry.Add( "particle-2d", "same seed gives identical draw records", () =>
		{
			Particle2DSystem Build()
			{
				var sys = Seeded( 42 );
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 100, 100 ), Rate = 120, Speed = 200, SpeedJitter = 0.5f, SpreadDegrees = 90,
					LifetimeMin = 0.5f, LifetimeMax = 1.5f, SizeMin = 2, SizeMax = 6, AngularSpeedMax = 6,
					ColorJitter = 0.2f, FrameMin = 0, FrameMax = 3, FrameAdvance = 8,
				} );
				sys.Step( 120 );
				return sys;
			}
			var a = Records( Build() );
			var b = Records( Build() );
			Check( a.Length == b.Length && a.Length > 0, $"expected non-empty identical record sets, got {a.Length} vs {b.Length}" );
			for ( int i = 0; i < a.Length; i++ )
			{
				Check( a[i].Position == b[i].Position, $"record {i} position differs" );
				Check( a[i].Size == b[i].Size, $"record {i} size differs" );
				Check( a[i].Rotation == b[i].Rotation, $"record {i} rotation differs" );
				Check( a[i].Color.R == b[i].Color.R && a[i].Color.G == b[i].Color.G && a[i].Color.B == b[i].Color.B && a[i].Color.A == b[i].Color.A, $"record {i} color differs" );
				Check( a[i].Frame == b[i].Frame, $"record {i} frame differs" );
			}
		} );

		registry.Add( "particle-2d", "different seed gives different records", () =>
		{
			Particle2DSystem Build( uint seed )
			{
				var sys = Seeded( seed );
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 100, 100 ), Rate = 120, Speed = 200, SpeedJitter = 0.5f, SpreadDegrees = 90,
					LifetimeMin = 0.5f, LifetimeMax = 1.5f, SizeMin = 2, SizeMax = 6,
				} );
				sys.Step( 30 );
				return sys;
			}
			var a = Records( Build( 1 ) );
			var b = Records( Build( 2 ) );
			Check( a.Length == b.Length && a.Length > 0, "seed comparison needs non-empty record sets" );
			bool anyDiff = false;
			for ( int i = 0; i < a.Length; i++ )
				if ( a[i].Position != b[i].Position || a[i].Size != b[i].Size ) { anyDiff = true; break; }
			Check( anyDiff, "different seeds produced identical records — seed is ignored" );
		} );

		registry.Add( "particle-2d", "burst count exact, then expiry on lifetime", () =>
		{
			var sys = Seeded( 7 );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), LifetimeMin = 1, LifetimeMax = 1,
				Bursts = new List<Particle2DBurst> { new( 0, 50 ) },
			} );
			sys.Step( 1 );
			Check( sys.AliveCount == 50, $"after 1 step expected 50 alive, got {sys.AliveCount}" );
			sys.Step( 59 );
			Check( sys.AliveCount == 50, $"after 60 steps (lifetime 1s) expected 50 alive, got {sys.AliveCount}" );
			sys.Step( 1 );
			Check( sys.AliveCount == 0, $"after 61 steps expected 0 alive, got {sys.AliveCount}" );
		} );

		registry.Add( "particle-2d", "rate emission gives 10 per step at 600/s", () =>
		{
			var sys = Seeded( 7 );
			sys.AddEmitter( new Particle2DEmitter { Position = new Vector2( 0, 0 ), Rate = 600, LifetimeMin = 10, LifetimeMax = 10 } );
			sys.Step( 3 );
			Check( sys.AliveCount == 30, $"expected 30 alive after 3 steps at 600/s, got {sys.AliveCount}" );
		} );

		registry.Add( "particle-2d", "floor bounce clamps and rebounds", () =>
		{
			var sys = System( new Particle2DSettings { Seed = 7, Gravity = Vector2.Zero, FloorEnabled = true, FloorY = 100, Restitution = 0.5f, Friction = 0 } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 50, 90 ), Direction = new Vector2( 0, 1 ), Speed = 100,
				LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			float maxY = float.MinValue;
			for ( int i = 0; i < 7; i++ )
			{
				sys.Step();
				foreach ( var st in sys.States ) if ( st.Alive ) maxY = MathF.Max( maxY, st.Position.Y );
			}
			Check( maxY <= 100.0001f, $"particle passed the floor: max Y {maxY}" );
			var s = sys.States[0];
			Check( s.Alive, "floor test particle died unexpectedly" );
			Near( s.Position.Y, 100, 0.01f, "particle should rest on the floor after the bounce step" );
			Near( s.Velocity.Y, -50, 0.01f, "velocity after a 0.5-restitution bounce off speed 100" );
		} );

		registry.Add( "particle-2d", "gravity falls the right distance in one second", () =>
		{
			var sys = System( new Particle2DSettings { Seed = 7, Gravity = new Vector2( 0, 980 ) } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Direction = new Vector2( 0, 1 ), Speed = 0,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 );
			var r = Records( sys )[0];
			Near( r.Position.X, 0, 0.001f, "gravity must not move X" );
			Near( r.Position.Y, 498.2f, 15f, "semi-implicit fall after 1s at 980 px/s²" );
		} );

		registry.Add( "particle-2d", "substeps integrate without error", () =>
		{
			var sys = System( new Particle2DSettings { Seed = 7, Substeps = 2, Gravity = new Vector2( 0, 980 ) } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Direction = new Vector2( 0, 1 ), Speed = 0,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 );
			Check( Records( sys )[0].Position.Y > 400, "2-substep fall should still drop hundreds of pixels in 1s" );
		} );

		registry.Add( "particle-2d", "bubbles preset rises", () =>
		{
			var baseSettings = new Particle2DSettings { Seed = 7 };
			var s = Particle2DPresets.SettingsFor( "bubbles", baseSettings );
			Check( s.Gravity.Y < 0, $"bubbles medium must have negative (rising) gravity, got {s.Gravity.Y}" );
			Check( baseSettings.Gravity.Y == 980, "SettingsFor must not mutate the base settings" );
			var sys = System( s );
			var em = Particle2DPresets.Create( "bubbles", new Vector2( 200, 200 ) );
			em.Rate = 0;
			em.Bursts = new List<Particle2DBurst> { new( 0, 1 ) };
			em.Speed = 0;
			sys.AddEmitter( em );
			sys.Step( 60 );
			var r = Records( sys )[0];
			Check( r.Position.Y < 200, $"bubbles particle should have risen, Y is {r.Position.Y}" );
			Near( r.Position.Y, 154.3f, 15f, "bubbles rise after 1s at gravity -90" );
		} );

		registry.Add( "particle-2d", "preset name resolution", () =>
		{
			Check( Particle2DPresets.ResolveName( "fx:confetti_burst" ) == "confetti", "fx:confetti_burst should resolve to confetti" );
			Check( Particle2DPresets.ResolveName( "VFX:Rain" ) == "rain", "VFX:Rain should resolve to rain" );
			Check( Particle2DPresets.ResolveName( "footstep_splash" ) == "splash", "footstep_splash should resolve to splash" );
			Check( Particle2DPresets.ResolveName( "camera_cut" ) is null, "camera_cut must not resolve to a preset" );
			Check( Particle2DPresets.ResolveName( "" ) is null, "empty name must not resolve" );
			Throws( () => Particle2DPresets.Create( "nope", new Vector2( 0, 0 ) ), "unknown preset" );
		} );

		registry.Add( "particle-2d", "validation throws on bad config", () =>
		{
			Throws( () => System( new Particle2DSettings { Capacity = 0 } ), "zero capacity" );
			Throws( () => System( new Particle2DSettings { Restitution = 1.5f } ), "restitution above 1" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { Rate = -1 } ), "negative rate" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { LifetimeMin = 0 } ), "zero lifetime" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { LifetimeMin = 2, LifetimeMax = 1 } ), "lifetime max below min" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { SizeMin = 0 } ), "zero size" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { SpeedJitter = 1.5f } ), "speed jitter above 1" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { SpreadDegrees = 200 } ), "spread above 180" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { Direction = Vector2.Zero, Speed = 100 } ), "zero direction with speed" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { ColorStart = new Particle2DColor( 1.5f, 0, 0, 1 ) } ), "color channel above 1" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { FrameMin = 3, FrameMax = 1 } ), "frame max below min" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { Shape = Particle2DEmitterShape.Line, Position = new Vector2( 5, 5 ), LineEnd = new Vector2( 5, 5 ) } ), "degenerate line" );
		} );

		registry.Add( "particle-2d", "color and size interpolate over life", () =>
		{
			var sys = Seeded( 7 );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 1, LifetimeMax = 1,
				SizeMin = 10, SizeMax = 10, SizeEndScale = 0.5f,
				ColorStart = new Particle2DColor( 1, 0, 0, 1 ), ColorEnd = new Particle2DColor( 0, 0, 1, 0 ),
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 1 );
			var early = Records( sys )[0];
			Check( early.Color.R > 0.9f && early.Color.B < 0.1f, $"at birth color should be near red, got {early.Color}" );
			sys.Step( 29 );
			var mid = Records( sys )[0];
			Near( mid.Color.R, 0.5f, 0.01f, "mid-life red" );
			Near( mid.Color.B, 0.5f, 0.01f, "mid-life blue" );
			Near( mid.Color.A, 0.5f, 0.01f, "mid-life alpha" );
			Near( mid.Size, 7.5f, 0.01f, "mid-life size" );
			sys.Step( 30 );
			var late = Records( sys )[0];
			Near( late.Color.R, 0, 0.01f, "end-of-life red" );
			Near( late.Color.B, 1, 0.01f, "end-of-life blue" );
			Near( late.Size, 5, 0.01f, "end-of-life size" );
		} );

		registry.Add( "particle-2d", "sprite frames advance and wrap in range", () =>
		{
			var sys = Seeded( 7 );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				FrameMin = 0, FrameMax = 3, FrameAdvance = 8,
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 1 );
			int f0 = Records( sys )[0].Frame;
			Check( f0 >= 0 && f0 <= 3, $"spawn frame {f0} outside [0,3]" );
			sys.Step( 7 );
			int f1 = Records( sys )[0].Frame;
			Check( f1 == (f0 + 1) % 4, $"after 8 steps at 8 fps frame should advance by 1 mod 4: {f0} -> {f1}" );
			var fixed_ = Seeded( 7 );
			fixed_.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				FrameMin = 2, FrameMax = 5, FrameAdvance = 0,
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			fixed_.Step( 1 );
			int fixedFrame = Records( fixed_ )[0].Frame;
			Check( fixedFrame >= 2 && fixedFrame <= 5, $"fixed frame {fixedFrame} outside [2,5]" );
			fixed_.Step( 30 );
			Check( Records( fixed_ )[0].Frame == fixedFrame, "frame with advance 0 must not change" );
		} );

		registry.Add( "particle-2d", "emitter shapes place particles inside their shapes", () =>
		{
			var noGravity = new Particle2DSettings { Seed = 7, Gravity = Vector2.Zero };
			var line = System( noGravity );
			line.AddEmitter( new Particle2DEmitter
			{
				Shape = Particle2DEmitterShape.Line, Position = new Vector2( 0, 0 ), LineEnd = new Vector2( 100, 0 ),
				Speed = 0, LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			line.Step( 1 );
			foreach ( var r in Records( line ) )
			{
				Check( r.Position.Y == 0, $"line particle off the line: Y {r.Position.Y}" );
				Check( r.Position.X >= 0 && r.Position.X <= 100, $"line particle outside segment: X {r.Position.X}" );
			}
			var box = System( noGravity );
			box.AddEmitter( new Particle2DEmitter
			{
				Shape = Particle2DEmitterShape.Box, Position = new Vector2( 50, 50 ), BoxHalfExtents = new Vector2( 20, 10 ),
				Speed = 0, LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			box.Step( 1 );
			foreach ( var r in Records( box ) )
			{
				Check( r.Position.X >= 30 && r.Position.X <= 70, $"box particle outside X range: {r.Position.X}" );
				Check( r.Position.Y >= 40 && r.Position.Y <= 60, $"box particle outside Y range: {r.Position.Y}" );
			}
			var circle = System( noGravity );
			circle.AddEmitter( new Particle2DEmitter
			{
				Shape = Particle2DEmitterShape.Circle, Position = new Vector2( 0, 0 ), CircleRadius = 10,
				Speed = 0, LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			circle.Step( 1 );
			foreach ( var r in Records( circle ) )
				Check( r.Position.Length() <= 10.0001f, $"circle particle outside radius: {r.Position.Length()}" );
		} );

		registry.Add( "particle-2d", "all presets create, validate, and step", () =>
		{
			foreach ( var name in Particle2DPresets.Names )
			{
				var baseSettings = new Particle2DSettings { Seed = 7 };
				var s = Particle2DPresets.SettingsFor( name, baseSettings );
				Check( baseSettings.Gravity == new Vector2( 0, 980 ), $"SettingsFor({name}) mutated the base settings" );
				var sys = System( s );
				sys.AddEmitter( Particle2DPresets.Create( name, new Vector2( 100, 100 ) ) );
				sys.Step( 60 );
			}
		} );

		registry.Add( "particle-2d", "capacity overflow drops safely", () =>
		{
			var sys = System( new Particle2DSettings { Seed = 7, Capacity = 2 } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 10 ) },
			} );
			sys.Step( 1 );
			Check( sys.AliveCount == 2, $"capacity 2 with burst 10 should hold 2, got {sys.AliveCount}" );
			sys.Step( 10 );
			Check( Records( sys ).Length == sys.AliveCount, "records must match alive count after overflow" );
		} );

		registry.Add( "particle-2d", "vortex swirls particles", () =>
		{
			var sys = System( new Particle2DSettings { Seed = 7, Gravity = Vector2.Zero, VortexStrength = 1000, VortexCenter = Vector2.Zero, VortexRadius = 1 } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 10, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 6 );
			var st = sys.States[0];
			Check( st.Velocity.Y > 0, $"vortex at (10,0) should push +Y, velocity Y is {st.Velocity.Y}" );
			Check( st.Position.Y > 0, $"vortex should have moved the particle +Y, Y is {st.Position.Y}" );
		} );

		registry.Add( "particle-2d", "turbulence changes paths", () =>
		{
			Particle2DDrawRecord[] Run( float strength )
			{
				var sys = System( new Particle2DSettings { Seed = 7, Gravity = Vector2.Zero, TurbulenceStrength = strength, TurbulenceFrequency = 0.05f } );
				sys.AddEmitter( new Particle2DEmitter
				{
					Shape = Particle2DEmitterShape.Box, Position = new Vector2( 100, 100 ), BoxHalfExtents = new Vector2( 50, 50 ),
					Speed = 10, SpreadDegrees = 180, LifetimeMin = 10, LifetimeMax = 10, Rate = 60,
				} );
				sys.Step( 60 );
				return Records( sys );
			}
			var calm = Run( 0 );
			var rough = Run( 500 );
			Check( calm.Length == rough.Length && calm.Length > 0, "turbulence comparison needs equal non-empty record sets" );
			bool anyDiff = false;
			for ( int i = 0; i < calm.Length; i++ )
				if ( (calm[i].Position - rough[i].Position).Length() > 0.5f ) { anyDiff = true; break; }
			Check( anyDiff, "turbulence strength 500 left every path within 0.5px of the calm path" );
		} );

		registry.Add( "particle-2d", "draw record errors are loud", () =>
		{
			var sys = Seeded( 7 );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 5 ) },
			} );
			sys.Step( 1 );
			Check( sys.WriteDrawRecords( new Particle2DDrawRecord[5] ) == 5, "records for 5 alive particles" );
			Throws( () => sys.WriteDrawRecords( new Particle2DDrawRecord[4] ), "too-small destination" );
			try { sys.DrawRecordFor( 999 ); throw new Exception( "dead-slot record should have thrown" ); }
			catch ( InvalidOperationException ) { }
		} );
	}
}
