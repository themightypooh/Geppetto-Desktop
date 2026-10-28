using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Particles — game-ready export to s&box (research: agent-research-particles-export).
///
/// Verbs:
///   particles check [name]            budget gate (conventions, not engine limits)
///   particles export &lt;folder&gt; [name=...]  PNG + .sprite + generated s&amp;box Fx.cs per effect
///   export particles &lt;folder&gt; [name=...]  same export, via the export verb
///
/// Why code, not a file format: s&amp;box has no particle asset format. Its
/// effects are components created in code (ParticleEffect +
/// ParticleSpriteRenderer, hand-rolled emit + update). So the export writes
/// a PNG sprite, a .sprite JSON wrapper pointing at it, and a C# component
/// following the PodPlume/JunkFx pattern (component owns the cap and the
/// clock; the generated controller owns the look; curves are baked as
/// constants in the update loop). The generated C# cannot be compile-checked
/// headlessly — no s&amp;box toolchain here — so in-game verification is owed
/// and is never claimed from a headless run. Parked, not built: .vpcf
/// import/export, a live viewport simulation, trails/mesh particles,
/// sub-emitters, collision callbacks, GPU particles, and the unverified
/// built-in ParticleEmitter property surface.
/// </summary>
public static partial class BuildScript
{
	const string ParticlesUsage = "particles check [name] | particles export <folder> [name=...] | export particles <folder> [name=...]";

	static bool ParticlesVerb( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count < 1 )
			return Fail( r, $"particles needs a subcommand. {ParticlesUsage}" );
		return args[0].ToLowerInvariant() switch
		{
			"check" => ParticlesCheck( studio, args.Skip( 1 ).ToList(), r ),
			"export" => ParticlesExport( studio, args.Skip( 1 ).ToList(), baseDir, r ),
			_ => Fail( r, $"particles has no subcommand '{args[0]}'. {ParticlesUsage}" ),
		};
	}

	static List<ParticleFxFeature> ParticleEffects( PartStudio studio, string nameFilter, out string why )
	{
		why = null;
		var all = studio.Features.OfType<ParticleFxFeature>().Where( f => !f.Suppressed && f.Error is null ).ToList();
		if ( string.IsNullOrWhiteSpace( nameFilter ) )
			return all;
		var one = all.FirstOrDefault( f => string.Equals( f.Name, nameFilter, StringComparison.OrdinalIgnoreCase ) );
		if ( one is null )
		{
			why = $"No ParticleFx effect called '{nameFilter}'. Effects: {string.Join( ", ", all.Select( f => f.Name ?? f.TypeName ) )}.";
			return null;
		}
		return new List<ParticleFxFeature> { one };
	}

	static bool ParticlesCheck( PartStudio studio, List<string> args, Result r )
	{
		var named = args.Where( a => !a.Contains( '=' ) ).ToList();
		var opts = Pairs( args.Where( a => a.Contains( '=' ) ) );
		var filter = opts.TryGetValue( "name", out var n ) ? n : named.FirstOrDefault();
		var effects = ParticleEffects( studio, filter, out var why );
		if ( effects is null )
			return Fail( r, why );
		if ( effects.Count == 0 )
			return Fail( r, "particles check: no ParticleFx effects in this model. Add one first: add ParticleFx name=... mode=burst burst=40 max=256 life=0.8." );

		// Tiers are project conventions grounded in the local s&box exemplars
		// (largest exemplar effect 260 particles), never engine limits.
		r.Log.Add( "particles check: tiers are project conventions, not engine limits — WARN above 256 per effect, FAIL above 1024 per effect or 4096 total, FAIL when a continuous effect's steady-state count (rate x life) exceeds its max." );
		var fails = new List<string>();
		var total = 0;
		foreach ( var f in effects )
		{
			var name = f.Name ?? f.TypeName;
			total += f.Max.Clamped;
			var steady = f.SteadyCount;
			var status = "OK";
			if ( f.Mode.Value == "continuous" && steady > f.Max.Clamped )
			{
				fails.Add( $"{name}: steady-state {steady:0.#} (rate {f.Rate.Clamped} x life {f.Life.Clamped}) exceeds max {f.Max.Clamped}" );
				status = "FAIL";
			}
			else if ( f.Mode.Value == "burst" && f.Burst.Clamped > f.Max.Clamped )
			{
				fails.Add( $"{name}: burst {f.Burst.Clamped} exceeds max {f.Max.Clamped}" );
				status = "FAIL";
			}
			if ( f.Max.Clamped > 1024 )
			{
				fails.Add( $"{name}: max {f.Max.Clamped} exceeds the 1024 per-effect tier" );
				status = "FAIL";
			}
			else if ( f.Max.Clamped > 256 && status == "OK" )
			{
				status = "WARN";
			}
			r.Log.Add( $"particles check: {name}: mode {f.Mode.Value}, steady {steady:0.#}, max {f.Max.Clamped} — {status}." );
		}
		if ( total > 4096 )
			fails.Add( $"total max {total} exceeds the 4096 total tier" );
		if ( fails.Count > 0 )
			return Fail( r, "particles check FAILED: " + string.Join( "; ", fails ) + "." );
		r.Log.Add( $"particles check: PASS ({effects.Count} effect(s), total max {total})." );
		return true;
	}

	static bool ParticlesExport( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count < 1 )
			return Fail( r, $"particles export needs a folder. {ParticlesUsage}" );
		var folder = args[0];
		var opts = Pairs( args.Skip( 1 ) );
		var filter = opts.TryGetValue( "name", out var n ) ? n : null;
		var effects = ParticleEffects( studio, filter, out var why );
		if ( effects is null )
			return Fail( r, why );
		if ( effects.Count == 0 )
			return Fail( r, "particles export: no ParticleFx effects in this model. Add one first: add ParticleFx name=... mode=burst burst=40 max=256 life=0.8." );

		var dir = Resolve( baseDir, folder );
		Directory.CreateDirectory( dir );
		foreach ( var f in effects )
		{
			var safe = Safe( f.Name ?? f.TypeName );
			var pngName = safe + ".png";
			var spriteName = safe + ".sprite";
			var csName = safe + "Fx.cs";
			var pngPath = Path.Combine( dir, pngName );
			var sprite = (f.Sprite.Value ?? "").Trim();
			if ( ParticleFxFeature.IsPreset( sprite ) )
			{
				File.WriteAllBytes( pngPath, ParticleSprites.Generate( sprite ) );
			}
			else if ( ParticleFxFeature.IsPngPath( sprite ) )
			{
				var src = Resolve( baseDir, sprite );
				if ( !File.Exists( src ) )
					return Fail( r, $"particles export: sprite PNG '{sprite}' for '{f.Name}' not found at {src}." );
				try { _ = Raster.LoadPng( src ); }
				catch ( Exception e ) { return Fail( r, $"particles export: sprite PNG '{sprite}' for '{f.Name}' does not decode: {e.Message}." ); }
				File.Copy( src, pngPath, overwrite: true );
			}
			else
			{
				return Fail( r, $"particles export: sprite '{sprite}' for '{f.Name}' is not a preset and not a .png path." );
			}
			var spriteJson = "{\n  \"Animations\": [\n    {\n      \"FrameRate\": 1,\n      \"Origin\": [0.5, 0.5],\n      \"LoopMode\": \"Loop\",\n      \"Frames\": [\n        { \"Texture\": \"" + pngName + "\" }\n      ]\n    }\n  ]\n}\n";
			File.WriteAllText( Path.Combine( dir, spriteName ), spriteJson );
			File.WriteAllText( Path.Combine( dir, csName ), ParticleFxCode.Generate( f, spriteName ) );
			r.Written.Add( pngPath );
			r.Written.Add( Path.Combine( dir, spriteName ) );
			r.Written.Add( Path.Combine( dir, csName ) );
			r.Log.Add( $"particles export: wrote {pngPath}, {spriteName}, {csName} for '{f.Name}'." );
			if ( Math.Abs( f.Scale.Clamped - 1f ) > 1e-6f )
				r.Log.Add( $"particles export: scale {f.Scale.Clamped} applied to the speed/size/gravity literals in {csName} (Geppetto units to game units)." );
		}
		return true;
	}

	/// <summary>Generated s&amp;box component code for one ParticleFx effect. Only API names
	/// verified from the local exemplars are used (ParticleEffect,
	/// ParticleSpriteRenderer, Emit with a null return when full, Velocity /
	/// Radius / Color / Angles, MaxParticles / Lifetime / Apply* / Force /
	/// Collision / Damping, ResourceLibrary.Get&lt;Sprite&gt;, Additive /
	/// Lighting / Shadows / DepthFeather, GameObjectFlags.NotSaved).
	/// Curves are baked as constants in the update loop — editing a curve
	/// means re-exporting. In-game verification is owed: drop the component
	/// on a GameObject, fire the burst, confirm the count never exceeds
	/// MaxParticles, no null-Emit exception, particles die at Lifetime,
	/// and a continuous effect holds a steady count.</summary>
	static class ParticleFxCode
	{
		static string F( float v ) => v.ToString( "0.####", CultureInfo.InvariantCulture );

		static string ClassName( ParticleFxFeature f )
		{
			var sb = new StringBuilder();
			foreach ( var c in (f.Name ?? "Particle") )
				if ( char.IsLetterOrDigit( c ) ) sb.Append( c );
			var name = sb.Length == 0 ? "Particle" : sb.ToString();
			if ( char.IsDigit( name[0] ) ) name = "Fx" + name;
			return name;
		}

		static string ColorLiteral( string hex, float alpha )
		{
			ParticleFxFeature.TryParseColor( hex, out var r, out var g, out var b );
			return $"new Color({F( r )}f, {F( g )}f, {F( b )}f, {F( alpha )}f)";
		}

		public static string Generate( ParticleFxFeature f, string spriteName )
		{
			var cls = ClassName( f );
			var scale = f.Scale.Clamped;
			var speed = f.Speed.Clamped * scale;
			var speedVar = f.SpeedVar.Clamped * scale;
			var size0 = f.Size0.Clamped * scale;
			var size1 = f.Size1.Clamped * scale;
			var gravity = f.Gravity.Clamped * scale;
			var shape = f.Shape.Value;
			var sb = new StringBuilder();
			sb.AppendLine( "// GENERATED by Geppetto `particles export` — do not hand-edit. Re-export to change it." );
			sb.AppendLine( "// Pattern: PodPlume/JunkFx — ParticleEffect owns the cap and the clock, this" );
			sb.AppendLine( "// controller owns the look. Curves are baked as constants below; editing a" );
			sb.AppendLine( "// curve means re-exporting. In-game verification owed: burst count never" );
			sb.AppendLine( "// exceeds MaxParticles, no null-Emit exception, particles die at Lifetime," );
			sb.AppendLine( "// continuous effects hold a steady count instead of thinning out." );
			sb.AppendLine( "using Sandbox;" );
			sb.AppendLine( "using System.Collections.Generic;" );
			sb.AppendLine();
			sb.AppendLine( $"public sealed class {cls}Fx : Component" );
			sb.AppendLine( "{" );
			sb.AppendLine( "	private sealed class Part" );
			sb.AppendLine( "	{" );
			sb.AppendLine( "		public Particle Particle;" );
			sb.AppendLine( "		public float Born, Life, Size0, Size1;" );
			sb.AppendLine( "		public Color Color0, Color1;" );
			sb.AppendLine( "		public float Alpha0, Alpha1;" );
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	private ParticleEffect _effect;" );
			sb.AppendLine( "	private readonly List<Part> _parts = new();" );
			sb.AppendLine( "	private float _time;" );
			if ( f.Mode.Value == "continuous" )
				sb.AppendLine( "	private float _accumulator;" );
			sb.AppendLine();
			sb.AppendLine( "	protected override void OnStart()" );
			sb.AppendLine( "	{" );
			sb.AppendLine( "		// A transient FX object: it must not persist into saved scenes." );
			sb.AppendLine( "		var fxObject = new GameObject();" );
			sb.AppendLine( "		fxObject.Flags = GameObjectFlags.NotSaved;" );
			sb.AppendLine( "		fxObject.SetParent( GameObject );" );
			sb.AppendLine( "		_effect = fxObject.AddComponent<ParticleEffect>();" );
			sb.AppendLine( $"		_effect.MaxParticles = {f.Max.Clamped};" );
			sb.AppendLine( $"		_effect.Lifetime = {F( f.Life.Clamped )}f;" );
			sb.AppendLine( "		_effect.ApplyColor = false;" );
			sb.AppendLine( "		_effect.ApplyAlpha = false;" );
			sb.AppendLine( "		_effect.ApplyShape = false;" );
			sb.AppendLine( "		_effect.Force = false;" );
			sb.AppendLine( "		_effect.Collision = false;" );
			sb.AppendLine( "		_effect.Damping = 0f;" );
			sb.AppendLine( "		var renderer = fxObject.AddComponent<ParticleSpriteRenderer>();" );
			sb.AppendLine( $"		renderer.Sprite = ResourceLibrary.Get<Sprite>( \"{spriteName}\" );" );
			sb.AppendLine( $"		renderer.Additive = {(f.Additive.Value ? "true" : "false")};" );
			sb.AppendLine( $"		renderer.Lighting = {(f.Lit.Value ? "true" : "false")};" );
			sb.AppendLine( "		renderer.Shadows = false;" );
			sb.AppendLine( "		renderer.DepthFeather = true;" );
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	// One burst, on an event. Emit returns null when full — checked, never assumed." );
			sb.AppendLine( "	public void FireBurst()" );
			sb.AppendLine( "	{" );
			sb.AppendLine( $"		for ( int i = 0; i < {f.Burst.Clamped}; i++ ) EmitOne();" );
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	private void EmitOne()" );
			sb.AppendLine( "	{" );
			sb.AppendLine( "		var pos = SpawnPosition();" );
			sb.AppendLine( "		var vel = SpawnVelocity();" );
			sb.AppendLine( "		var p = _effect.Emit( pos, Random.Shared.Float( 0f, 1f ) );" );
			sb.AppendLine( "		if ( p is null ) return;" );
			sb.AppendLine( "		p.Velocity = vel;" );
			sb.AppendLine( $"		p.Radius = {F( size0 )}f;" );
			sb.AppendLine( $"		p.Color = {ColorLiteral( f.Color0.Value, f.Alpha0.Clamped )};" );
			sb.AppendLine( "		p.Angles = new Angles( 0, 0, Random.Shared.Float( 0f, 360f ) );" );
			sb.AppendLine( $"		var life = {F( f.Life.Clamped )}f + Random.Shared.Float( -{F( f.LifeVar.Clamped )}f, {F( f.LifeVar.Clamped )}f );" );
			sb.AppendLine( "		if ( life < 0.05f ) life = 0.05f;" );
			sb.AppendLine( "		_parts.Add( new Part" );
			sb.AppendLine( "		{" );
			sb.AppendLine( "			Particle = p," );
			sb.AppendLine( "			Born = _time," );
			sb.AppendLine( "			Life = life," );
			sb.AppendLine( $"			Size0 = {F( size0 )}f," );
			sb.AppendLine( $"			Size1 = {F( size1 )}f," );
			sb.AppendLine( $"			Color0 = {ColorLiteral( f.Color0.Value, f.Alpha0.Clamped )}," );
			sb.AppendLine( $"			Color1 = {ColorLiteral( f.Color1.Value, f.Alpha1.Clamped )}," );
			sb.AppendLine( $"			Alpha0 = {F( f.Alpha0.Clamped )}f," );
			sb.AppendLine( $"			Alpha1 = {F( f.Alpha1.Clamped )}f," );
			sb.AppendLine( "		} );" );
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	private Vector3 SpawnPosition()" );
			sb.AppendLine( "	{" );
			sb.AppendLine( "		var origin = Transform.WorldPosition;" );
			if ( shape == "disc" )
			{
				sb.AppendLine( "		var r = System.MathF.Sqrt( Random.Shared.Float( 0f, 1f ) );" );
				sb.AppendLine( "		var phi = Random.Shared.Float( 0f, System.MathF.PI * 2f );" );
				sb.AppendLine( "		return origin + Transform.Rotation * new Vector3( r * System.MathF.Cos( phi ), r * System.MathF.Sin( phi ), 0f );" );
			}
			else if ( shape == "box" )
			{
				sb.AppendLine( "		return origin + Transform.Rotation * new Vector3( Random.Shared.Float( -1f, 1f ), Random.Shared.Float( -1f, 1f ), Random.Shared.Float( -1f, 1f ) );" );
			}
			else
			{
				sb.AppendLine( "		return origin;" );
			}
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	private Vector3 SpawnVelocity()" );
			sb.AppendLine( "	{" );
			sb.AppendLine( $"		var speed = {F( speed )}f + Random.Shared.Float( -{F( speedVar )}f, {F( speedVar )}f );" );
			sb.AppendLine( "		if ( speed < 0f ) speed = 0f;" );
			if ( shape == "box" )
			{
				sb.AppendLine( "		return Transform.Rotation * Vector3.Up * speed;" );
			}
			else
			{
				sb.AppendLine( $"		var dir = RandomInCone( Transform.Rotation * Vector3.Up, {F( f.Spread.Clamped )}f );" );
				sb.AppendLine( "		return dir * speed;" );
			}
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	private static Vector3 RandomInCone( Vector3 forward, float spreadDeg )" );
			sb.AppendLine( "	{" );
			sb.AppendLine( "		var cosMax = System.MathF.Cos( spreadDeg * System.MathF.PI / 180f );" );
			sb.AppendLine( "		var z = Random.Shared.Float( cosMax, 1f );" );
			sb.AppendLine( "		var phi = Random.Shared.Float( 0f, System.MathF.PI * 2f );" );
			sb.AppendLine( "		var r = System.MathF.Sqrt( System.MathF.Max( 0f, 1f - z * z ) );" );
			sb.AppendLine( "		var helper = System.MathF.Abs( forward.y ) < 0.9f ? Vector3.Up : Vector3.Right;" );
			sb.AppendLine( "		var right = Vector3.Cross( helper, forward ).Normal;" );
			sb.AppendLine( "		var up = Vector3.Cross( forward, right );" );
			sb.AppendLine( "		return (right * (r * System.MathF.Cos( phi )) + up * (r * System.MathF.Sin( phi )) + forward * z).Normal;" );
			sb.AppendLine( "	}" );
			sb.AppendLine();
			sb.AppendLine( "	protected override void OnUpdate()" );
			sb.AppendLine( "	{" );
			sb.AppendLine( "		var dt = Time.Delta;" );
			sb.AppendLine( "		_time += dt;" );
			if ( f.Mode.Value == "continuous" )
			{
				sb.AppendLine( $"		_accumulator += {F( f.Rate.Clamped )}f * dt;" );
				sb.AppendLine( "		while ( _accumulator >= 1f )" );
				sb.AppendLine( "		{" );
				sb.AppendLine( "			_accumulator -= 1f;" );
				sb.AppendLine( "			EmitOne();" );
				sb.AppendLine( "		}" );
			}
			sb.AppendLine( "		for ( int i = _parts.Count - 1; i >= 0; i-- )" );
			sb.AppendLine( "		{" );
			sb.AppendLine( "			var part = _parts[i];" );
			sb.AppendLine( "			var u = ( _time - part.Born ) / part.Life;" );
			sb.AppendLine( "			if ( u >= 1f )" );
			sb.AppendLine( "			{" );
			sb.AppendLine( "				// The component's Lifetime expires the particle itself;" );
			sb.AppendLine( "				// this only drops it from the controller's tracking list." );
			sb.AppendLine( "				_parts.RemoveAt( i );" );
			sb.AppendLine( "				continue;" );
			sb.AppendLine( "			}" );
			sb.AppendLine( "			part.Particle.Radius = part.Size0 + ( part.Size1 - part.Size0 ) * u;" );
			sb.AppendLine( $"			part.Particle.Velocity *= System.MathF.Exp( -{F( f.Drag.Clamped )}f * dt );" );
			sb.AppendLine( $"			part.Particle.Velocity += Vector3.Up * {F( gravity )}f * dt;" );
			sb.AppendLine( "			var alpha = part.Alpha0 + ( part.Alpha1 - part.Alpha0 ) * u;" );
			sb.AppendLine( "			var c0 = part.Color0;" );
			sb.AppendLine( "			var c1 = part.Color1;" );
			sb.AppendLine( "			part.Particle.Color = new Color(" );
			sb.AppendLine( "				c0.R + ( c1.R - c0.R ) * u," );
			sb.AppendLine( "				c0.G + ( c1.G - c0.G ) * u," );
			sb.AppendLine( "				c0.B + ( c1.B - c0.B ) * u, alpha );" );
			sb.AppendLine( "		}" );
			sb.AppendLine( "	}" );
			sb.AppendLine( "}" );
			return sb.ToString();
		}
	}

	/// <summary>Sprite presets (P4): three generated RGBA buffers through
	/// SpriteSheetBaker.EncodeRgbaPng — no new texture system, no copied
	/// assets. Sprites are white so the particle colour tints them in-game;
	/// the alpha channel carries the shape. Smoke is a noisy radial falloff
	/// from a seeded value-noise field, not a stock image.</summary>
	static class ParticleSprites
	{
		public static byte[] Generate( string preset )
		{
			const int size = 256;
			var colour = new Raster( size, size, 0xffffff );
			var alpha = new byte[size * size];
			var name = preset.Trim().ToLowerInvariant();
			var noise = new Random( 12345 );
			var grid = new float[9, 9];
			for ( var y = 0; y < 9; y++ )
				for ( var x = 0; x < 9; x++ )
					grid[x, y] = (float)noise.NextDouble();
			float NoiseAt( float u, float v )
			{
				var gx = u * 8f; var gy = v * 8f;
				var x0 = Math.Clamp( (int)MathF.Floor( gx ), 0, 7 ); var y0 = Math.Clamp( (int)MathF.Floor( gy ), 0, 7 );
				var fx = gx - x0; var fy = gy - y0;
				var sx = fx * fx * (3f - 2f * fx); var sy = fy * fy * (3f - 2f * fy);
				return grid[x0, y0] * (1 - sx) * (1 - sy) + grid[x0 + 1, y0] * sx * (1 - sy)
					+ grid[x0, y0 + 1] * (1 - sx) * sy + grid[x0 + 1, y0 + 1] * sx * sy;
			}
			for ( var y = 0; y < size; y++ )
			{
				for ( var x = 0; x < size; x++ )
				{
					var dx = (x + 0.5f) / size - 0.5f;
					var dy = (y + 0.5f) / size - 0.5f;
					var r = MathF.Sqrt( dx * dx + dy * dy );
					float a;
					if ( name == "spark" )
					{
						a = r < 0.15f ? 1f : Math.Max( 0f, 1f - (r - 0.15f) / 0.35f );
						a *= a;
					}
					else if ( name == "smoke" )
					{
						var fall = Math.Max( 0f, 1f - (r / 0.5f) * (r / 0.5f) );
						a = fall * (0.35f + 0.65f * NoiseAt( (x + 0.5f) / size, (y + 0.5f) / size ));
					}
					else // softdisc
					{
						var fall = Math.Max( 0f, 1f - (r / 0.5f) * (r / 0.5f) );
						a = fall * fall;
					}
					alpha[y * size + x] = (byte)Math.Clamp( (int)(a * 255f), 0, 255 );
				}
			}
			return SpriteSheetBaker.EncodeRgbaPng( colour, alpha );
		}
	}
}
