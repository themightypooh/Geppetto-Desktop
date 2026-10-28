// Tests for wave-6 lane P47: classic 2D-editor .plist import into Particle2D
// settings. Group: particle-plist. Headless only; no texture is ever loaded
// (a texture name pointing at a nonexistent file must still parse fine).
using System.Numerics;
using Marionette.Desktop;

internal static class ParticlePlistImportTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			throw new Exception( $"{what}: expected ArgumentException, none thrown" );
		}
		bool Warned( ParticlePlistResult r, string fragment )
		{
			foreach ( var w in r.Warnings ) if ( w.Contains( fragment, StringComparison.Ordinal ) ) return true;
			return false;
		}

		string FullPlist() => """
			<?xml version="1.0" encoding="UTF-8"?>
			<plist version="1.0"><dict>
			<key>emitterType</key><integer>0</integer>
			<key>duration</key><integer>-1</integer>
			<key>emissionRate</key><real>120</real>
			<key>maxParticles</key><integer>500</integer>
			<key>particleLifeSpan</key><real>2</real>
			<key>particleLifespanVariance</key><real>0.5</real>
			<key>sourcePosition</key><dict><key>x</key><real>100</real><key>y</key><real>200</real></dict>
			<key>sourcePositionVariance</key><dict><key>x</key><real>10</real><key>y</key><real>20</real></dict>
			<key>angle</key><real>90</real>
			<key>angleVariance</key><real>30</real>
			<key>speed</key><real>150</real>
			<key>speedVariance</key><real>30</real>
			<key>gravity</key><dict><key>x</key><real>0</real><key>y</key><real>-50</real></dict>
			<key>radialAcceleration</key><real>10</real>
			<key>tangentialAcceleration</key><real>5</real>
			<key>startSize</key><real>20</real>
			<key>startSizeVariance</key><real>4</real>
			<key>endSize</key><real>10</real>
			<key>endSizeVariance</key><real>2</real>
			<key>startColor</key><dict><key>r</key><real>1</real><key>g</key><real>0.5</real><key>b</key><real>0</real><key>a</key><real>1</real></dict>
			<key>finishColor</key><dict><key>r</key><real>0</real><key>g</key><real>0</real><key>b</key><real>1</real><key>a</key><real>0</real></dict>
			<key>finishColorVariance</key><dict><key>r</key><real>0.1</real><key>g</key><real>0.1</real><key>b</key><real>0.1</real><key>a</key><real>0.1</real></dict>
			<key>blendFuncSource</key><integer>770</integer>
			<key>blendFuncDestination</key><integer>1</integer>
			<key>textureFileName</key><string>spark.png</string>
			<key>rotationStart</key><real>0</real>
			<key>rotationEnd</key><real>90</real>
			</dict></plist>
			""";

		registry.Add( "particle-plist", "full sample plist maps field by field", () =>
		{
			var r = ParticlePlistImport.Parse( FullPlist() );
			Check( r.Settings.Capacity == 500, $"Capacity: got {r.Settings.Capacity}, want 500" );
			Check( r.Emitter.Rate == 120, $"Rate: got {r.Emitter.Rate}, want 120" );
			Near( r.Emitter.LifetimeMin, 1.5f, 1e-6f, "LifetimeMin" );
			Near( r.Emitter.LifetimeMax, 2.5f, 1e-6f, "LifetimeMax" );
			Check( r.Emitter.Position == new Vector2( 100, 200 ), $"Position: got {r.Emitter.Position}, want (100,200)" );
			Check( r.Emitter.Shape == Particle2DEmitterShape.Box, $"Shape: got {r.Emitter.Shape}, want Box" );
			Check( r.Emitter.BoxHalfExtents == new Vector2( 10, 20 ), $"BoxHalfExtents: got {r.Emitter.BoxHalfExtents}, want (10,20)" );
			// Angle 90 in Y-up space is straight up; Y-down it is (0,-1) (rising).
			Near( r.Emitter.Direction.X, 0, 1e-6f, "Direction.X" );
			Near( r.Emitter.Direction.Y, -1, 1e-6f, "Direction.Y" );
			Near( r.Emitter.SpreadDegrees, 30, 1e-6f, "SpreadDegrees" );
			Near( r.Emitter.Speed, 150, 1e-6f, "Speed" );
			Near( r.Emitter.SpeedJitter, 0.2f, 1e-6f, "SpeedJitter" );
			// Plist gravity (0,-50) falls in Y-up space; Y-down it is (0,+50).
			Check( r.Settings.Gravity == new Vector2( 0, 50 ), $"Gravity: got {r.Settings.Gravity}, want (0,50)" );
			Near( r.Emitter.SizeMin, 16, 1e-6f, "SizeMin" );
			Near( r.Emitter.SizeMax, 24, 1e-6f, "SizeMax" );
			Near( r.Emitter.SizeEndScale, 0.5f, 1e-6f, "SizeEndScale" );
			Check( r.Emitter.ColorStart.R == 1 && r.Emitter.ColorStart.G == 0.5f && r.Emitter.ColorStart.B == 0 && r.Emitter.ColorStart.A == 1,
				$"ColorStart: got {r.Emitter.ColorStart}, want (1,0.5,0,1)" );
			Check( r.Emitter.ColorEnd.R == 0 && r.Emitter.ColorEnd.G == 0 && r.Emitter.ColorEnd.B == 1 && r.Emitter.ColorEnd.A == 0,
				$"ColorEnd: got {r.Emitter.ColorEnd}, want (0,0,1,0)" );
			Check( r.TextureName == "spark.png", $"TextureName: got {r.TextureName}, want spark.png" );
			Check( Warned( r, "radialAcceleration" ), "radialAcceleration warning missing" );
			Check( Warned( r, "tangentialAcceleration" ), "tangentialAcceleration warning missing" );
			Check( Warned( r, "endSizeVariance" ), "endSizeVariance warning missing" );
			Check( Warned( r, "finishColorVariance" ), "finishColorVariance warning missing" );
			Check( Warned( r, "blendFuncSource" ), "blendFuncSource warning missing" );
			Check( Warned( r, "rotationStart" ), "rotationStart warning missing" );
			Check( !Warned( r, "duration" ), "infinite duration (-1) must not warn" );
		} );

		registry.Add( "particle-plist", "imported settings build a live 2D system and emit", () =>
		{
			var r = ParticlePlistImport.Parse( FullPlist() );
			var sys = new Particle2DSystem( r.Settings );
			sys.AddEmitter( r.Emitter );
			sys.Step();
			Check( sys.AliveCount > 0, $"expected particles after one step, got AliveCount {sys.AliveCount}" );
			var rec = sys.DrawRecordFor( 0 );
			Check( rec.Size > 0, $"expected a positive draw size, got {rec.Size}" );
		} );

		registry.Add( "particle-plist", "variance mapping: ranges, jitter, box, clamps", () =>
		{
			var r = ParticlePlistImport.Parse( """
				<plist version="1.0"><dict>
				<key>particleLifeSpan</key><real>1</real>
				<key>particleLifeSpanVariance</key><real>0.25</real>
				<key>speed</key><real>100</real>
				<key>speedVariance</key><real>150</real>
				<key>startSize</key><real>10</real>
				<key>startSizeVariance</key><real>12</real>
				<key>sourcePositionVariance</key><dict><key>x</key><real>0</real><key>y</key><real>0</real></dict>
				</dict></plist>
				""" );
			Near( r.Emitter.LifetimeMin, 0.75f, 1e-6f, "LifetimeMin" );
			Near( r.Emitter.LifetimeMax, 1.25f, 1e-6f, "LifetimeMax" );
			Near( r.Emitter.SpeedJitter, 1, 1e-6f, "SpeedJitter clamped" );
			Check( Warned( r, "SpeedJitter clamped" ), "speed-jitter clamp warning missing" );
			Check( r.Emitter.SizeMin > 0, $"SizeMin must stay positive after clamping, got {r.Emitter.SizeMin}" );
			Near( r.Emitter.SizeMax, 22, 1e-6f, "SizeMax" );
			Check( Warned( r, "SizeMin clamped" ), "size clamp warning missing" );
			Check( r.Emitter.Shape == Particle2DEmitterShape.Point, $"zero position variance must stay Point, got {r.Emitter.Shape}" );
		} );

		registry.Add( "particle-plist", "dropped-field warnings list unmapped keys by name", () =>
		{
			var r = ParticlePlistImport.Parse( """
				<plist version="1.0"><dict>
				<key>emissionRate</key><real>10</real>
				<key>radialAcceleration</key><real>3</real>
				<key>tangentialAccelVariance</key><real>1</real>
				<key>blendFuncDestination</key><integer>1</integer>
				<key>positionType</key><integer>0</integer>
				<key>maxRadius</key><real>50</real>
				<key>rotatePerSecond</key><real>2</real>
				<key>someFutureKey</key><string>hello</string>
				</dict></plist>
				""" );
			Check( r.Emitter.Rate == 10, $"mapped Rate must survive alongside warnings, got {r.Emitter.Rate}" );
			foreach ( var fragment in new[] { "radialAcceleration", "tangentialAccelVariance", "blendFuncDestination", "positionType", "maxRadius", "rotatePerSecond", "someFutureKey" } )
				Check( Warned( r, fragment ), $"warning for '{fragment}' missing; warnings were: {string.Join( " | ", r.Warnings )}" );
		} );

		registry.Add( "particle-plist", "malformed XML fails loudly", () =>
		{
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>emissionRate</key>" ), "truncated XML" );
			Throws( () => ParticlePlistImport.Parse( "not xml at all" ), "non-XML text" );
			Throws( () => ParticlePlistImport.Parse( "<dict><key>emissionRate</key><real>10</real></dict>" ), "dict without plist root" );
			Throws( () => ParticlePlistImport.Parse( "<plist><array></array></plist>" ), "array root value" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>emissionRate</key></dict></plist>" ), "key with no value" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>emissionRate</key><real>10</real><key>emissionRate</key><real>20</real></dict></plist>" ), "duplicate key" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>emissionRate</key><string>fast</string></dict></plist>" ), "string where a number belongs" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>startColor</key><dict><key>r</key><real>1</real></dict></dict></plist>" ), "colour missing channels" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>emitterType</key><integer>99</integer></dict></plist>" ), "unknown emitterType" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>speedVariance</key><real>-5</real></dict></plist>" ), "negative variance" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>angleVariance</key><real>270</real></dict></plist>" ), "spread beyond 180" );
			Throws( () => ParticlePlistImport.Parse( "<plist><dict><key>textureFileName</key><data>AAAA</data></dict></plist>" ), "unsupported data type" );
		} );

		registry.Add( "particle-plist", "texture name passes through as a string only", () =>
		{
			var r = ParticlePlistImport.Parse( """
				<plist version="1.0"><dict>
				<key>textureFileName</key><string>this-file-does-not-exist.png</string>
				</dict></plist>
				""" );
			Check( r.TextureName == "this-file-does-not-exist.png", $"TextureName: got {r.TextureName}" );
			var none = ParticlePlistImport.Parse( "<plist><dict></dict></plist>" );
			Check( none.TextureName is null, $"absent texture must be null, got {none.TextureName}" );
			Check( none.Warnings.Count == 0, $"empty plist must warn about nothing, got {string.Join( " | ", none.Warnings )}" );
		} );

		registry.Add( "particle-plist", "coordinate conversion: angles, gravity, canvas height", () =>
		{
			var right = ParticlePlistImport.Parse( "<plist><dict><key>angle</key><real>0</real></dict></plist>" );
			Near( right.Emitter.Direction.X, 1, 1e-6f, "angle 0 Direction.X" );
			Near( right.Emitter.Direction.Y, 0, 1e-6f, "angle 0 Direction.Y" );
			var up = ParticlePlistImport.Parse( "<plist><dict><key>angle</key><real>90</real><key>gravity</key><dict><key>x</key><real>10</real><key>y</key><real>20</real></dict></dict></plist>" );
			Near( up.Emitter.Direction.Y, -1, 1e-6f, "angle 90 Direction.Y (up in editor, rising here)" );
			Check( up.Settings.Gravity == new Vector2( 10, -20 ), $"gravity Y must flip once: got {up.Settings.Gravity}, want (10,-20)" );
			var flipped = ParticlePlistImport.Parse(
				"<plist><dict><key>sourcePosition</key><dict><key>x</key><real>100</real><key>y</key><real>200</real></dict>" +
				"<key>gravity</key><dict><key>x</key><real>10</real><key>y</key><real>20</real></dict></dict></plist>", 480 );
			Check( flipped.Emitter.Position == new Vector2( 100, 280 ), $"canvasHeight flip: got {flipped.Emitter.Position}, want (100,280)" );
			Check( flipped.Settings.Gravity == new Vector2( 10, -20 ), $"canvasHeight must not double-flip gravity: got {flipped.Settings.Gravity}" );
			var plain = ParticlePlistImport.Parse(
				"<plist><dict><key>sourcePosition</key><dict><key>x</key><real>100</real><key>y</key><real>200</real></dict></dict></plist>" );
			Check( plain.Emitter.Position == new Vector2( 100, 200 ), $"no canvasHeight means verbatim position: got {plain.Emitter.Position}" );
		} );

		registry.Add( "particle-plist", "duration and emitterType edge cases", () =>
		{
			var finite = ParticlePlistImport.Parse( "<plist><dict><key>duration</key><real>3</real></dict></plist>" );
			Check( Warned( finite, "duration" ), "finite duration must warn (no stop time in Particle2D)" );
			Check( finite.Emitter.Enabled, "finite duration must still leave the emitter enabled" );
			var radial = ParticlePlistImport.Parse( "<plist><dict><key>emitterType</key><integer>1</integer><key>emissionRate</key><real>5</real></dict></plist>" );
			Check( Warned( radial, "emitterType 1" ), "radial emitterType must warn" );
			Check( radial.Emitter.Rate == 5, "radial warning must not drop mapped fields" );
		} );

		registry.Add( "particle-plist", "colour arrays and endColor alias", () =>
		{
			var r = ParticlePlistImport.Parse( """
				<plist version="1.0"><dict>
				<key>startColor</key><array><real>0.25</real><real>0.5</real><real>0.75</real><real>1</real></array>
				<key>endColor</key><array><real>0</real><real>0</real><real>0</real><real>0</real></array>
				<key>startColorVariance</key><dict><key>r</key><real>0.3</real><key>g</key><real>0.1</real><key>b</key><real>0.2</real><key>a</key><real>0.4</real></dict>
				</dict></plist>
				""" );
			Check( r.Emitter.ColorStart.R == 0.25f && r.Emitter.ColorStart.G == 0.5f && r.Emitter.ColorStart.B == 0.75f,
				$"array startColor: got {r.Emitter.ColorStart}" );
			Check( r.Emitter.ColorEnd.A == 0, $"endColor alias must set ColorEnd, got {r.Emitter.ColorEnd}" );
			Near( r.Emitter.ColorJitter, 0.3f, 1e-6f, "ColorJitter = max RGB variance" );
			Check( Warned( r, "alpha" ), "alpha colour-variance drop must warn" );
		} );

		registry.Add( "particle-plist", "absent gravity means zero, not the Geppetto default", () =>
		{
			var r = ParticlePlistImport.Parse( "<plist><dict><key>emissionRate</key><real>10</real></dict></plist>" );
			Check( r.Settings.Gravity == Vector2.Zero, $"absent gravity: got {r.Settings.Gravity}, want (0,0)" );
			Check( r.Emitter.Direction == new Vector2( 0, 1 ), $"absent angle: got {r.Emitter.Direction}, want (0,1) falling" );
		} );
	}
}
