using System.Numerics;
using Marionette.Desktop;

// Lane 6 (particles session, 2026-10-24): 2D additions + emission masks on top of lane 1's Particle2D.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-additions", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DAdditionsTests.Register( registry, settings );
// placed after the Particle2DTests.Register line.
internal static class Particle2DAdditionsTests
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
		Particle2DSystem NoGravity( uint seed = 7 ) => System( new Particle2DSettings { Seed = seed, Gravity = Vector2.Zero } );
		Particle2DDrawRecord[] Records( Particle2DSystem sys )
		{
			var arr = new Particle2DDrawRecord[sys.AliveCount];
			int n = sys.WriteDrawRecords( arr );
			Check( n == sys.AliveCount, $"WriteDrawRecords returned {n} but AliveCount is {sys.AliveCount}" );
			return arr;
		}

		registry.Add( "particle-2d-additions", "direction mode spreads along the perpendicular", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Shape = Particle2DEmitterShape.Direction, Position = new Vector2( 100, 100 ),
				Direction = new Vector2( 1, 0 ), EmitDist = 10, Speed = 0,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			sys.Step( 1 );
			var recs = Records( sys );
			float minY = float.MaxValue, maxY = float.MinValue;
			foreach ( var r in recs )
			{
				Check( r.Position.X == 100, $"direction particle off the perpendicular line: X {r.Position.X}" );
				Check( r.Position.Y >= 90 && r.Position.Y <= 110, $"direction particle outside EmitDist band: Y {r.Position.Y}" );
				minY = MathF.Min( minY, r.Position.Y ); maxY = MathF.Max( maxY, r.Position.Y );
			}
			Check( minY < 95 && maxY > 105, $"direction spread should reach both band edges, got [{minY}, {maxY}]" );
		} );

		registry.Add( "particle-2d-additions", "direction mode launches along the direction", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Shape = Particle2DEmitterShape.Direction, Position = new Vector2( 0, 0 ),
				Direction = new Vector2( 1, 0 ), EmitDist = 5, Speed = 100,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 50 ) },
			} );
			sys.Step( 1 );
			foreach ( var st in sys.States )
				if ( st.Alive )
				{
					Near( st.Velocity.X, 100, 0.01f, "direction launch velocity X" );
					Near( st.Velocity.Y, 0, 0.01f, "direction launch velocity Y" );
				}
		} );

		registry.Add( "particle-2d-additions", "emit angle rotates the direction", () =>
		{
			Particle2DSystem Run( float angle )
			{
				var sys = NoGravity();
				sys.AddEmitter( new Particle2DEmitter
				{
					Shape = Particle2DEmitterShape.Direction, Position = new Vector2( 0, 0 ),
					Direction = new Vector2( 1, 0 ), EmitAngleDegrees = angle, Speed = 100,
					LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
				} );
				sys.Step( 1 );
				return sys;
			}
			var down = Run( 90 ).States[0];
			Near( down.Velocity.X, 0, 0.01f, "emit angle 90 velocity X (Y-down: 90 = down)" );
			Near( down.Velocity.Y, 100, 0.01f, "emit angle 90 velocity Y" );
			var up = Run( -90 ).States[0];
			Near( up.Velocity.X, 0, 0.01f, "emit angle -90 velocity X" );
			Near( up.Velocity.Y, -100, 0.01f, "emit angle -90 velocity Y" );
		} );

		registry.Add( "particle-2d-additions", "gravity angle redirects gravity, keeping magnitude", () =>
		{
			Particle2DSystem Run( float? angle )
			{
				var sys = System( new Particle2DSettings { Seed = 7, Gravity = new Vector2( 0, 980 ), GravityAngleDegrees = angle } );
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 0, 0 ), Direction = new Vector2( 0, 1 ), Speed = 0,
					LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
				} );
				sys.Step( 60 );
				return sys;
			}
			var right = Records( Run( 0 ) )[0];
			Near( right.Position.X, 498.2f, 15f, "gravity angle 0 should fall +X the same distance gravity falls +Y" );
			Near( right.Position.Y, 0, 0.01f, "gravity angle 0 must not move Y" );
			var left = Records( Run( 180 ) )[0];
			Near( left.Position.X, -498.2f, 15f, "gravity angle 180 should fall -X" );
			Near( left.Position.Y, 0, 0.01f, "gravity angle 180 must not move Y" );
			var plain = Records( Run( null ) )[0];
			Near( plain.Position.Y, 498.2f, 15f, "null gravity angle keeps the gravity vector (falls +Y)" );
		} );

		registry.Add( "particle-2d-additions", "rotAuto follows movement direction, still particles keep rotation", () =>
		{
			Particle2DSystem Run( Vector2 dir, float speed, bool rotAuto )
			{
				var sys = NoGravity();
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 0, 0 ), Direction = dir, Speed = speed, RotAuto = rotAuto,
					LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
				} );
				sys.Step( 1 );
				return sys;
			}
			Near( Records( Run( new Vector2( 1, 0 ), 100, true ) )[0].Rotation, 0, 0.01f, "rotAuto moving +X" );
			Near( Records( Run( new Vector2( 0, 1 ), 100, true ) )[0].Rotation, MathF.PI / 2, 0.01f, "rotAuto moving +Y (down)" );
			Near( Records( Run( new Vector2( 1, 0 ), 0, true ) )[0].Rotation, 0, 0.01f, "rotAuto with zero velocity keeps stored rotation" );
			Near( Records( Run( new Vector2( 0, 1 ), 100, false ) )[0].Rotation, 0, 0.01f, "without rotAuto rotation stays at spawn value" );
		} );

		registry.Add( "particle-2d-additions", "emit direction as angle sets spawn rotation", () =>
		{
			Particle2DSystem Run( bool flag )
			{
				var sys = NoGravity();
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 0, 0 ), Direction = new Vector2( 0, 1 ), Speed = 0,
					EmitDirectionAsAngle = flag,
					LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
				} );
				sys.Step( 1 );
				return sys;
			}
			Near( Records( Run( true ) )[0].Rotation, MathF.PI / 2, 0.01f, "emit direction (0,1) as angle is pi/2" );
			Near( Records( Run( false ) )[0].Rotation, 0, 0.01f, "without the flag spawn rotation is 0" );
		} );

		registry.Add( "particle-2d-additions", "fadeIn fades alpha in linearly", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 1, LifetimeMax = 1,
				FadeIn = 0.5f, FadePower = 1f,
				ColorStart = new Particle2DColor( 1, 0, 0, 1 ), ColorEnd = new Particle2DColor( 0, 0, 1, 1 ),
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 15 );
			Near( Records( sys )[0].Color.A, 0.5f, 0.01f, "at t=0.25 with fadeIn 0.5 alpha should be 0.5" );
			sys.Step( 16 );
			Near( Records( sys )[0].Color.A, 1f, 0.01f, "past fadeIn alpha should be full" );
		} );

		registry.Add( "particle-2d-additions", "fadePower curves the fade-in", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 1, LifetimeMax = 1,
				FadeIn = 0.5f, FadePower = 2f,
				ColorStart = new Particle2DColor( 1, 1, 1, 1 ), ColorEnd = new Particle2DColor( 1, 1, 1, 1 ),
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 15 );
			Near( Records( sys )[0].Color.A, 0.25f, 0.01f, "at t=0.25 with fadeIn 0.5 and power 2 alpha should be 0.25" );
		} );

		registry.Add( "particle-2d-additions", "size growth splits by axis and clamps at zero", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				SizeMin = 10, SizeMax = 10, SizeEndScale = 1f, SizeIncrX = 60, SizeIncrY = 0,
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 60 );
			var r = Records( sys )[0];
			Near( r.Size, 10, 0.01f, "uniform size unchanged by axis growth" );
			Near( r.SizeX, 70, 0.1f, "size X after 1s at +60 px/s" );
			Near( r.SizeY, 10, 0.01f, "size Y with zero Y growth" );
			var shrink = NoGravity();
			shrink.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				SizeMin = 10, SizeMax = 10, SizeIncrX = -1000,
				Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			shrink.Step( 1 );
			Check( Records( shrink )[0].SizeX == 0, "negative size growth must clamp at 0, not go negative" );
		} );

		registry.Add( "particle-2d-additions", "speed change accelerates, decelerates, and leaves still particles still", () =>
		{
			Particle2DSystem Run( float speed, float incr )
			{
				var sys = NoGravity();
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 0, 0 ), Direction = new Vector2( 1, 0 ), Speed = speed, SpeedIncr = incr,
					LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
				} );
				sys.Step( 60 );
				return sys;
			}
			Near( Run( 100, 60 ).States[0].Velocity.X, 160, 0.1f, "speed 100 + 60 px/s² for 1s" );
			Near( Run( 100, -60 ).States[0].Velocity.X, 40, 0.1f, "speed 100 - 60 px/s² for 1s" );
			Near( Run( 0, 60 ).States[0].Velocity.X, 0, 0.001f, "motionless particle must not gain speed" );
		} );

		registry.Add( "particle-2d-additions", "lifetime sort puts oldest first", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 100, 100 ), Direction = new Vector2( 1, 0 ), Speed = 60,
				SortMode = Particle2DSortMode.Lifetime, LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 3 ), new( 60, 3 ) },
			} );
			sys.Step( 61 );
			var recs = Records( sys );
			Check( recs.Length == 6, $"expected 6 records, got {recs.Length}" );
			Check( recs[0].Position.X > 150, $"oldest group should be near X 161, got {recs[0].Position.X}" );
			Check( recs[5].Position.X < 110, $"newest group should be near X 101, got {recs[5].Position.X}" );
			Check( recs[0].Position.X > recs[5].Position.X, "lifetime sort must order oldest before newest" );
		} );

		registry.Add( "particle-2d-additions", "distance sort puts farthest from the emitter first", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Shape = Particle2DEmitterShape.Line, Position = new Vector2( 0, 0 ), LineEnd = new Vector2( 100, 0 ),
				Speed = 0, SortMode = Particle2DSortMode.Distance, LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			sys.Step( 1 );
			var recs = Records( sys );
			Check( recs[0].Position.X > 90, $"farthest record should be near X 100, got {recs[0].Position.X}" );
			Check( recs[recs.Length - 1].Position.X < 10, $"nearest record should be near X 0, got {recs[recs.Length - 1].Position.X}" );
			for ( int i = 0; i + 1 < recs.Length; i++ )
				Check( recs[i].Position.X >= recs[i + 1].Position.X - 0.001f, $"distance sort must be non-increasing, index {i} X {recs[i].Position.X} < {recs[i + 1].Position.X}" );
		} );

		registry.Add( "particle-2d-additions", "index sort keeps slot order across emitters", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				ColorStart = new Particle2DColor( 1, 0, 0, 1 ), ColorEnd = new Particle2DColor( 1, 0, 0, 1 ),
				Bursts = new List<Particle2DBurst> { new( 0, 3 ) },
			} );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				ColorStart = new Particle2DColor( 0, 0, 1, 1 ), ColorEnd = new Particle2DColor( 0, 0, 1, 1 ),
				Bursts = new List<Particle2DBurst> { new( 0, 3 ) },
			} );
			sys.Step( 1 );
			var recs = Records( sys );
			Check( recs.Length == 6, $"expected 6 records, got {recs.Length}" );
			for ( int i = 0; i < 3; i++ ) Check( recs[i].Color.R > 0.9f, $"record {i} should be from the red (first) emitter" );
			for ( int i = 3; i < 6; i++ ) Check( recs[i].Color.B > 0.9f, $"record {i} should be from the blue (second) emitter" );
		} );

		registry.Add( "particle-2d-additions", "mixed sort groups by emitter, then by each group's mode", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, LifetimeMin = 10, LifetimeMax = 10,
				ColorStart = new Particle2DColor( 1, 0, 0, 1 ), ColorEnd = new Particle2DColor( 1, 0, 0, 1 ),
				Bursts = new List<Particle2DBurst> { new( 0, 2 ) },
			} );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 100, 100 ), Direction = new Vector2( 1, 0 ), Speed = 60,
				SortMode = Particle2DSortMode.Lifetime, LifetimeMin = 10, LifetimeMax = 10,
				ColorStart = new Particle2DColor( 0, 0, 1, 1 ), ColorEnd = new Particle2DColor( 0, 0, 1, 1 ),
				Bursts = new List<Particle2DBurst> { new( 0, 2 ), new( 60, 2 ) },
			} );
			sys.Step( 61 );
			var recs = Records( sys );
			Check( recs.Length == 6, $"expected 6 records, got {recs.Length}" );
			Check( recs[0].Color.R > 0.9f && recs[1].Color.R > 0.9f, "first group must be the red index emitter" );
			for ( int i = 2; i < 6; i++ ) Check( recs[i].Color.B > 0.9f, $"record {i} should be from the blue lifetime emitter" );
			Check( recs[2].Position.X > recs[4].Position.X, "within the blue group oldest must come before newest" );
		} );

		registry.Add( "particle-2d-additions", "mask solid spawns on every opaque cell", () =>
		{
			var sys = NoGravity();
			var mask = new Particle2DMask( 3, 3, new bool[9] { true, true, true, true, true, true, true, true, true } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, Mask = mask, MaskMode = Particle2DMaskMode.Solid,
				MaskCellSize = 10, LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 300 ) },
			} );
			sys.Step( 1 );
			foreach ( var r in Records( sys ) )
			{
				Check( r.Position.X == 5 || r.Position.X == 15 || r.Position.X == 25, $"solid mask X off the cell grid: {r.Position.X}" );
				Check( r.Position.Y == 5 || r.Position.Y == 15 || r.Position.Y == 25, $"solid mask Y off the cell grid: {r.Position.Y}" );
			}
		} );

		registry.Add( "particle-2d-additions", "mask border excludes the interior", () =>
		{
			var sys = NoGravity();
			var mask = new Particle2DMask( 3, 3, new bool[9] { true, true, true, true, true, true, true, true, true } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, Mask = mask, MaskMode = Particle2DMaskMode.Border,
				MaskCellSize = 10, LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 300 ) },
			} );
			sys.Step( 1 );
			var recs = Records( sys );
			Check( recs.Length == 300, $"expected 300 border records, got {recs.Length}" );
			foreach ( var r in recs )
				Check( !(r.Position.X == 15 && r.Position.Y == 15), "border mask must never spawn on the interior cell (15,15)" );
		} );

		registry.Add( "particle-2d-additions", "mask directed border launches along outward normals", () =>
		{
			var sys = NoGravity();
			var mask = new Particle2DMask( 3, 3, new bool[9] { true, true, true, true, true, true, true, true, true } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Direction = new Vector2( 0, 1 ), Speed = 100,
				Mask = mask, MaskMode = Particle2DMaskMode.DirectedBorder, MaskCellSize = 10,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			sys.Step( 1 );
			bool sawLeft = false, sawRight = false;
			foreach ( var st in sys.States )
			{
				if ( !st.Alive ) continue;
				Near( st.Velocity.Length(), 100, 0.1f, "directed border speed must stay 100" );
				float ax = MathF.Abs( st.Velocity.X ), ay = MathF.Abs( st.Velocity.Y );
				bool axial = (ax > 99.9f && ay < 0.1f) || (ay > 99.9f && ax < 0.1f);
				bool diagonal = MathF.Abs( ax - 70.7107f ) < 0.1f && MathF.Abs( ay - 70.7107f ) < 0.1f;
				Check( axial || diagonal, $"directed velocity must be an outward normal, got ({st.Velocity.X}, {st.Velocity.Y})" );
				if ( st.Velocity.X < -99 ) sawLeft = true;
				if ( st.Velocity.X > 99 ) sawRight = true;
			}
			Check( sawLeft && sawRight, "directed border must launch both left and right, not one direction" );
		} );

		registry.Add( "particle-2d-additions", "mask directed border falls back to launch direction when outward is undefined", () =>
		{
			var sys = NoGravity();
			var mask = new Particle2DMask( 1, 1, new bool[1] { true } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Direction = new Vector2( 1, 0 ), Speed = 100,
				Mask = mask, MaskMode = Particle2DMaskMode.DirectedBorder, MaskCellSize = 10,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 1 );
			var st = sys.States[0];
			Near( st.Velocity.X, 100, 0.01f, "lone-cell directed fallback velocity X" );
			Near( st.Velocity.Y, 0, 0.01f, "lone-cell directed fallback velocity Y" );
		} );

		registry.Add( "particle-2d-additions", "mask solid keeps the emitter launch direction", () =>
		{
			var sys = NoGravity();
			var mask = new Particle2DMask( 3, 3, new bool[9] { true, true, true, true, true, true, true, true, true } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Direction = new Vector2( 0, 1 ), Speed = 100,
				Mask = mask, MaskMode = Particle2DMaskMode.Solid, MaskCellSize = 10,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 50 ) },
			} );
			sys.Step( 1 );
			foreach ( var st in sys.States )
				if ( st.Alive )
				{
					Near( st.Velocity.X, 0, 0.01f, "solid mask launch velocity X" );
					Near( st.Velocity.Y, 100, 0.01f, "solid mask launch velocity Y" );
				}
		} );

		registry.Add( "particle-2d-additions", "mask capture colour inherits the cell colour", () =>
		{
			var cell = new Particle2DColor( 0.2f, 0.4f, 0.6f, 1 );
			var sys = NoGravity();
			var mask = new Particle2DMask( 1, 1, new bool[1] { true }, new Particle2DColor[1] { cell } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, Mask = mask, CaptureColorFromMask = true, EndAlpha = 1,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 1 );
			var c = Records( sys )[0].Color;
			Near( c.R, 0.2f, 0.01f, "captured red" );
			Near( c.G, 0.4f, 0.01f, "captured green" );
			Near( c.B, 0.6f, 0.01f, "captured blue" );
			Near( c.A, 1f, 0.01f, "captured alpha with EndAlpha 1" );
			var plain = NoGravity();
			plain.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, Mask = mask,
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			plain.Step( 1 );
			Near( Records( plain )[0].Color.R, 1f, 0.01f, "without capture the emitter colour stands, not the cell colour" );
		} );

		registry.Add( "particle-2d-additions", "explicit palette wins over mask capture", () =>
		{
			var sys = NoGravity();
			var mask = new Particle2DMask( 1, 1, new bool[1] { true }, new Particle2DColor[1] { new( 0.2f, 0.4f, 0.6f, 1 ) } );
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Speed = 0, Mask = mask, CaptureColorFromMask = true, EndAlpha = 1,
				Palette = new List<Particle2DColor> { new( 0.9f, 0.1f, 0.1f, 1 ) },
				LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
			} );
			sys.Step( 1 );
			Near( Records( sys )[0].Color.R, 0.9f, 0.01f, "palette colour must win over captured cell colour" );
		} );

		registry.Add( "particle-2d-additions", "mask and new-field validation throws loudly", () =>
		{
			Throws( () => new Particle2DMask( 0, 3, new bool[0] ), "mask width zero" );
			Throws( () => new Particle2DMask( 2, 2, new bool[3] { true, true, true } ), "opaque grid length mismatch" );
			Throws( () => new Particle2DMask( 1, 1, new bool[1] { true }, new Particle2DColor[2] ), "colour grid length mismatch" );
			Throws( () => new Particle2DMask( 1, 1, new bool[1] { true }, new Particle2DColor[1] { new( 1.5f, 0, 0, 1 ) } ), "colour channel above 1" );
			var maskNoColors = new Particle2DMask( 1, 1, new bool[1] { true } );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { Mask = maskNoColors, CaptureColorFromMask = true } ), "capture without a colour grid" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { CaptureColorFromMask = true } ), "capture without a mask" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { Mask = maskNoColors, MaskCellSize = 0 } ), "mask cell size zero" );
			Throws( () => maskNoColors.SpawnCells( (Particle2DMaskMode)99 ), "unknown mask mode" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { SortMode = (Particle2DSortMode)99 } ), "unknown sort mode" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { EmitDist = -1 } ), "negative emit distance" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { FadeIn = 1.5f } ), "fadeIn above 1" );
			Throws( () => Seeded( 7 ).AddEmitter( new Particle2DEmitter { FadePower = 0 } ), "fadePower zero" );
			Throws( () => System( new Particle2DSettings { GravityAngleDegrees = float.NaN } ), "NaN gravity angle" );
			Throws( () => System( new Particle2DSettings { WrapEnabled = true, WrapMin = new Vector2( 50, 50 ), WrapMax = new Vector2( 10, 10 ) } ), "wrap max below min" );
		} );

		registry.Add( "particle-2d-additions", "wrap bounds teleport to the opposite side", () =>
		{
			Particle2DSystem Run( Vector2 start, Vector2 dir )
			{
				var sys = System( new Particle2DSettings { Seed = 7, Gravity = Vector2.Zero, WrapEnabled = true, WrapMin = new Vector2( 0, 0 ), WrapMax = new Vector2( 100, 100 ) } );
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = start, Direction = dir, Speed = 60,
					LifetimeMin = 10, LifetimeMax = 10, Bursts = new List<Particle2DBurst> { new( 0, 1 ) },
				} );
				sys.Step( 60 );
				return sys;
			}
			var right = Records( Run( new Vector2( 99, 50 ), new Vector2( 1, 0 ) ) )[0];
			Near( right.Position.X, 59, 0.1f, "99 + 60 across a 0..100 box wraps to 59" );
			Near( right.Position.Y, 50, 0.001f, "wrap must not move Y" );
			Near( Run( new Vector2( 99, 50 ), new Vector2( 1, 0 ) ).States[0].Velocity.X, 60, 0.01f, "wrap must not change velocity" );
			var left = Records( Run( new Vector2( 1, 50 ), new Vector2( -1, 0 ) ) )[0];
			Near( left.Position.X, 41, 0.1f, "1 - 60 across a 0..100 box wraps to 41" );
		} );

		registry.Add( "particle-2d-additions", "preset clone carries the new settings fields", () =>
		{
			var baseSettings = new Particle2DSettings { Seed = 7, GravityAngleDegrees = 45, WrapEnabled = true, WrapMin = new Vector2( 0, 0 ), WrapMax = new Vector2( 50, 50 ) };
			var s = Particle2DPresets.SettingsFor( "sparks", baseSettings );
			Check( s.GravityAngleDegrees == 45, $"SettingsFor must carry GravityAngleDegrees, got {s.GravityAngleDegrees}" );
			Check( s.WrapEnabled && s.WrapMax == new Vector2( 50, 50 ), "SettingsFor must carry wrap bounds" );
			Check( baseSettings.GravityAngleDegrees == 45 && baseSettings.WrapEnabled, "SettingsFor must not mutate the base settings" );
		} );

		registry.Add( "particle-2d-additions", "new features stay deterministic across identical seeds", () =>
		{
			Particle2DDrawRecord[] Build()
			{
				var sys = Seeded( 42 );
				var mask = new Particle2DMask( 3, 3, new bool[9] { true, true, true, true, true, true, true, true, true } );
				sys.AddEmitter( new Particle2DEmitter
				{
					Shape = Particle2DEmitterShape.Direction, Position = new Vector2( 100, 100 ),
					Direction = new Vector2( 1, 0 ), EmitDist = 20, EmitAngleDegrees = 30,
					Speed = 150, SpeedJitter = 0.3f, SpeedIncr = 20, SizeIncrX = 5, SizeIncrY = -2,
					FadeIn = 0.3f, FadePower = 2f, RotAuto = true, SortMode = Particle2DSortMode.Lifetime,
					Rate = 60, LifetimeMin = 1f, LifetimeMax = 2f,
				} );
				sys.AddEmitter( new Particle2DEmitter
				{
					Position = new Vector2( 0, 0 ), Speed = 0, Mask = mask, MaskMode = Particle2DMaskMode.Border,
					MaskCellSize = 10, Rate = 30, LifetimeMin = 1f, LifetimeMax = 1f,
				} );
				sys.Step( 90 );
				return Records( sys );
			}
			var a = Build();
			var b = Build();
			Check( a.Length == b.Length && a.Length > 0, $"determinism comparison needs equal non-empty record sets, got {a.Length} vs {b.Length}" );
			for ( int i = 0; i < a.Length; i++ )
			{
				Check( a[i].Position == b[i].Position, $"record {i} position differs" );
				Check( a[i].Size == b[i].Size && a[i].SizeX == b[i].SizeX && a[i].SizeY == b[i].SizeY, $"record {i} size differs" );
				Check( a[i].Rotation == b[i].Rotation, $"record {i} rotation differs" );
				Check( a[i].Color.R == b[i].Color.R && a[i].Color.G == b[i].Color.G && a[i].Color.B == b[i].Color.B && a[i].Color.A == b[i].Color.A, $"record {i} color differs" );
				Check( a[i].Frame == b[i].Frame, $"record {i} frame differs" );
			}
		} );

		registry.Add( "particle-2d-additions", "speed and rotation variation already exist as jitter fields", () =>
		{
			var sys = NoGravity();
			sys.AddEmitter( new Particle2DEmitter
			{
				Position = new Vector2( 0, 0 ), Direction = new Vector2( 1, 0 ), Speed = 100, SpeedJitter = 0.5f,
				AngularSpeedMax = 10, LifetimeMin = 10, LifetimeMax = 10,
				Bursts = new List<Particle2DBurst> { new( 0, 200 ) },
			} );
			sys.Step( 1 );
			float minSpeed = float.MaxValue, maxSpeed = float.MinValue, minAng = float.MaxValue, maxAng = float.MinValue;
			foreach ( var st in sys.States )
			{
				if ( !st.Alive ) continue;
				minSpeed = MathF.Min( minSpeed, st.Velocity.Length() ); maxSpeed = MathF.Max( maxSpeed, st.Velocity.Length() );
				minAng = MathF.Min( minAng, st.AngularVelocity ); maxAng = MathF.Max( maxAng, st.AngularVelocity );
			}
			Check( minSpeed < 90 && maxSpeed > 110, $"SpeedJitter 0.5 should spread speeds around 100, got [{minSpeed}, {maxSpeed}]" );
			Check( minAng < 0 && maxAng > 0, $"AngularSpeedMax should spread angular velocity both ways, got [{minAng}, {maxAng}]" );
		} );
	}
}
