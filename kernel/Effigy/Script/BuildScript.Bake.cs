using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

public static partial class BuildScript
{
	const string BakeUsage = "bake Low High[,High2…] folder | bake * folder — [size=1024] "
		+ "[maps=normal,ao,curvature,cavity,height,thickness,bentnormal,objectnormal,position,id|all|everything] "
		+ "[cage=0|distance|CageBody] [cagegen=0.2|Name:0.1] [distance=0] [round=0] [thickdist=0] [thickrays=32] [ss=1] [bits=8|16] [id=material|body] [green=gl|dx] [flipv=0] "
		+ "[vmat=0|1] [shader=complex|skin] [preset=Body|Head|Mouth|Wet flesh] [k=1] [color=] [rough=] [metal=] [name=] [assets=] "
		+ "[cagegen=0.2|auto] [cageauto=1] [skew=0..1] [skewedge=deg] [unwrap=1] [slot=N] [report=1] [mirror=1] [preview=1] [pairs=Name,Name] [curvatureworld=1]";

	/// <summary>
	/// `bake`: high detail onto a low body's UVs, as PNG maps and optionally a .vmat.
	///
	///   bake Low High folder        one low body from one or more high bodies (High,High2 - combined).
	///   bake * folder               by name: every X_low from its X_high bodies (X_high_2 … too) and
	///                               X_cage if there is one. Each pair only sees its own highs - the
	///                               exploded bake - and all of them land in one set of maps, so lows
	///                               sharing an atlas come out as one texture.
	///   cage=                       a number pushes the rays' start out that far; a body name uses
	///                               that body as the cage mesh (a pushed-out copy of the low body).
///   cagegen=                     generate each pair's cage mesh from its own low body
///                               (inflated along averaged normals, see BakeCage): one distance
///                               for all pairs (cagegen=0.2), or per pair (cagegen=Arm:0.1,
///                               Leg:0.3, or a default plus overrides). An authored cage
///                               (X_cage body, or cage= body) always wins for its pair.
///                               cagegen= cannot combine with a scalar cage= distance.
///   thickdist=0                 thickness white-point in model units: the grey map reads
///                               white at this depth or more (0 = a quarter of the sculpt's
///                               diagonal, the baker's default). thickrays=32 sets the inward
///                               rays per texel (the baker floors it at 4).
	///   bits=16                     height, normal, bent normal, object normal and position as 16-bit PNGs.
	///   round=0                     rounded-edge radius in model units: a texel near a hard edge
	///                               blends the neighbour face normal, reading as a baked fillet along
	///                               sharp hard-surface edges without a subdivided high source. 0
	///                               (default) bakes the sculpt normals exactly, so a hard low edge stays hard.
	///   thickdist=0                 thickness white-point in model units: the grey map reads
	///                               white at this depth or more (0 = a quarter of the sculpt's
	///                               diagonal, the baker's default). thickrays=32 sets the inward
	///                               rays per texel (the baker floors it at 4).
	///   id=body                     the ID map colours by high body instead of material slot.
	///   cagegen= / cageauto=        P1: generate a cage per pair with BakeCage.Inflate instead of
	///                               a hand-built cage body; cagegen=auto estimates each pair's
	///                               distance from its proud detail. Takes precedence over X_cage.
	///   skew= / skewedge=           P2: blend ray direction toward the low face normal (0..1);
	///                               skewedge=deg turns the blend fully on past that hard-edge angle.
	///   unwrap=1                    P5: unwrap lows that fail the UV gate into one atlas first.
	///   slot=N                      P5: write the baked normal/ao into material slot N and log
	///                               the equivalent material lines.
	///   report=1                    P4: also write a _miss.png (white hit, black missed).
	///   mirror=1                    P7: allow mirrored (overlapping) UVs; last-wins, valid only
	///                               for symmetric detail, and logged as such.
	///   preview=1                   P8: bake at 256 for a fast look before the full-size bake.
	///   pairs=Name,Name             P8: bake only the named pairs (by-name bakes).
	///   curvatureworld=1            P6: also write curvature3d, derived from object-space
	///                               normals in 3D, so UV seams cannot draw lines in it.
	///   shader=skin preset= k=      the vmat is s&amp;box's skin shader with a preset; k is translucency
	///                               (1 real skin, 3-10 gel-like flesh) for the transmission mask, which
	///                               is written when thickness is baked. A baked bent normal is bound.
	/// </summary>
	static bool Bake( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count < 2 || (args[0] != "*" && args.Count < 3) )
			return Fail( r, "bake needs the low body, the high body and a folder, or * and a folder to pair bodies by name: " + BakeUsage );

		var byName = args[0] == "*";
		var opts = Pairs( args.Skip( byName ? 2 : 3 ) );
		var dir = Resolve( baseDir, args[byName ? 1 : 2] );
		var preview = Truthy( opts.GetValueOrDefault( "preview", "0" ) );
		var size = preview ? 256 : (int)Number( opts.GetValueOrDefault( "size", "1024" ) );

		if ( preview )
			r.Log.Add( "bake: preview=1: baking at 256 for a fast look, not the requested size." );

		if ( size < 1 || size > 8192 )
			return Fail( r, $"size={size}: a map is 1 to 8192 texels a side." );

		if ( !BakeMapList( opts.GetValueOrDefault( "maps", "normal,ao" ), out var maps, out var whyMaps ) )
			return Fail( r, whyMaps );

		// P9: 16-bit by default for hero sizes, unless bits= says otherwise: 8-bit
		// height banding on smooth curves is visible and no supersampling fixes it.
		var bits = opts.TryGetValue( "bits", out var bitsText ) ? (int)Number( bitsText ) : (size >= 2048 ? 16 : 8);

		if ( bits != 8 && bits != 16 )
			return Fail( r, $"bits={bits}: a bake is written at 8 or 16 bits." );

		var round = opts.TryGetValue( "round", out var roundText ) ? Number( roundText ) : 0f;

		if ( round < 0f )
			return Fail( r, $"round={roundText}: a rounded-edge radius is 0 or above, in model units." );

		var thickDist = opts.TryGetValue( "thickdist", out var thickDistText ) ? Number( thickDistText ) : 0f;

		if ( thickDist < 0f )
			return Fail( r, $"thickdist={thickDistText}: the thickness white-point is 0 or above, in model units (0 = a quarter of the sculpt's diagonal)." );

		var thickRays = (int)Number( opts.GetValueOrDefault( "thickrays", "32" ) );

		if ( thickRays < 1 )
			return Fail( r, $"thickrays={opts.GetValueOrDefault( "thickrays", "32" )}: at least 1 inward ray per texel (the baker floors it at 4)." );

		var idBy = opts.GetValueOrDefault( "id", "material" ).Trim().ToLowerInvariant();

		if ( idBy is not ("material" or "body") )
			return Fail( r, $"id={idBy}: the ID map colours by material (each material slot) or body (each high body)." );

		var shader = opts.GetValueOrDefault( "shader", "complex" ).Trim().ToLowerInvariant();

		if ( shader is not ("complex" or "skin") )
			return Fail( r, $"shader={shader}: the bake's vmat is complex or skin." );

		List<BakePairs.Pair> pairs;
		string name;
		var cageDistance = 0f;

		if ( byName )
		{
			pairs = BakePairs.Match( studio.Bodies.Select( b => (b.Name ?? b.Id, b.Mesh) ), out var problems );

			foreach ( var p in problems )
				r.Log.Add( $"bake: warning: {p}" );

			if ( pairs.Count == 0 )
				return Fail( r, "bake *: no low body has a high body to bake from. Name them as pairs - Arm_low and Arm_high (Arm_high_2, Arm_cage …) - "
					+ $"or name the bodies: bake Low High {args[1]}. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}."
					+ (problems.Count > 0 ? " " + string.Join( " ", problems ) : "") );

			name = Safe( opts.GetValueOrDefault( "name", pairs.Count == 1 ? pairs[0].Name : "bake" ) );

			if ( opts.TryGetValue( "cage", out var cageText ) )
			{
				if ( !TryNumber( cageText, out cageDistance ) )
					return Fail( r, $"cage={cageText}: in a by-name bake each pair's cage is its X_cage body, so cage= takes only a distance." );
			}
		}
		else
		{
			var low = ModelMeasure.FindBody( studio, args[0] );

			if ( low?.Mesh is null )
				return Fail( r, $"No body called '{args[0]}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

			var pair = new BakePairs.Pair { Name = low.Name ?? low.Id, LowName = args[0], Low = low.Mesh };

			foreach ( var highName in args[1].Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
			{
				var high = ModelMeasure.FindBody( studio, highName.Trim() );

				if ( high?.Mesh is null )
					return Fail( r, $"No body called '{highName.Trim()}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );

				pair.Highs.Add( (highName.Trim(), high.Mesh) );
			}

			if ( opts.TryGetValue( "cage", out var cageText ) && !TryNumber( cageText, out cageDistance ) )
			{
				var cage = ModelMeasure.FindBody( studio, cageText );

				if ( cage?.Mesh is null )
					return Fail( r, $"cage={cageText}: not a distance and no body has that name." );

				if ( NormalBake.CageMismatch( low.Mesh, cage.Mesh ) is { } why )
					return Fail( r, $"cage={cageText}: the cage {why} Make it from a copy of {args[0]} pushed outwards." );

				pair.Cage = cage.Mesh;
				pair.CageName = cageText;
			}

			pairs = new List<BakePairs.Pair> { pair };
			name = Safe( opts.GetValueOrDefault( "name", low.Name ?? low.Id ) );
		}

		// P8: bake only the named pairs, if pairs= says so.
		if ( opts.TryGetValue( "pairs", out var pairsText ) )
		{
			var wanted = pairsText.Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( s => s.Trim() ).ToList();
			var kept = pairs.Where( pr => wanted.Any( w => w.Equals( pr.Name, StringComparison.OrdinalIgnoreCase ) || w.Equals( pr.LowName, StringComparison.OrdinalIgnoreCase ) ) ).ToList();

			if ( kept.Count == 0 )
				return Fail( r, $"pairs={pairsText}: no pair matches that name. Pairs: {string.Join( ", ", pairs.Select( pr => pr.Name ) )}." );

			pairs = kept;
			r.Log.Add( $"bake: pairs=: baking only {string.Join( ", ", pairs.Select( pr => pr.Name ) )}; other islands in this run's maps are unfilled, not preserved from an earlier bake." );
		}

		// P1: generated cages, taking precedence over a hand-built cage body.
		var cagegenGiven = opts.TryGetValue( "cagegen", out var cagegenText );
		var cageauto = Truthy( opts.GetValueOrDefault( "cageauto", "0" ) ) || (cagegenGiven && cagegenText.Trim().Equals( "auto", StringComparison.OrdinalIgnoreCase ) );
		float? cageDefault = null;
		Dictionary<string, float> cagePerPair = null;

		if ( cagegenGiven && !cageauto && !BakeCage.TryParseDistances( cagegenText, out cageDefault, out cagePerPair, out var cageWhy ) )
			return Fail( r, cageWhy );

		if ( cagegenGiven || cageauto )
		{
			foreach ( var pr in pairs )
			{
				float cageDist;

				if ( cagePerPair is not null && BakeCage.DistanceFor( pr.Name, cageDefault, cagePerPair ) is { } named )
				{
					cageDist = named;
				}
				else if ( cageauto )
				{
					var combined = BakePairs.Combine( pr.Highs.Select( h => h.Mesh ).ToList(), out _ );
					cageDist = BakeCage.EstimateDistance( pr.Low, combined );
				}
				else if ( cageDefault is { } dflt )
				{
					cageDist = dflt;
				}
				else
				{
					continue;
				}

				if ( pr.Cage is not null )
					r.Log.Add( $"bake: cagegen overrides the hand-built cage for {pr.Name}." );

				// P3: validate and shrink before baking, rather than baking wrong quietly.
				var cage = BakeCage.InflateValidated( pr.Low, cageDist, out var applied, out var flagged );
				pr.Cage = cage;
				pr.CageName = $"cagegen {applied:0.####}";

				if ( flagged > 0 )
					r.Log.Add( $"bake: warning: cage for {pr.Name} still has {flagged} vertices past half their shortest edge at distance {applied:0.####} after shrinking; rays there may start on the wrong side." );
				else if ( applied != cageDist )
					r.Log.Add( $"bake: cage for {pr.Name} shrank from {cageDist:0.####} to {applied:0.####} after validation." );
			}
		}

		// P10: thickness on an open sheet is meaningless — warn, still write the map.
		if ( maps.HasFlag( BakeMaps.Thickness ) )
		{
			foreach ( var pr in pairs )
			{
				var combined = BakePairs.Combine( pr.Highs.Select( h => h.Mesh ).ToList(), out _ );
				var open = NormalBake.CountOpenEdges( combined );

				if ( open > 0 )
					r.Log.Add( $"bake: warning: high for {pr.Name} has {open} open edges, so its thickness map is not a closed-volume thickness — useful as a mask at most, not as a transmission measure." );
			}
		}

		// Refused up front, with the reason: an overlapping bake does not fail, it writes a map that
		// is wrong wherever two faces share texels. A by-name bake is checked as ONE atlas.
		var coverage = NormalBake.Measure( pairs.Select( p => p.Low ) );

		// P5: unwrap first instead of refusing for missing UVs.
		if ( !coverage.CanBake && Truthy( opts.GetValueOrDefault( "unwrap", "0" ) ) )
		{
			var report = UVUnwrap.UnwrapAll( pairs.Select( pr => pr.Low ).ToList(), 66f, 0.01f, mode: UnwrapMode.Angle );
			r.Log.Add( $"bake: unwrap=1: unwrapped {pairs.Count} low(s) into one atlas ({report})." );
			coverage = NormalBake.Measure( pairs.Select( p => p.Low ) );
		}

		// P7: mirrored UVs are allowed only when asked for, and only when nothing
		// escapes the square — overlapping texels then bake last-wins, which is
		// only correct for symmetric detail.
		var mirrorAllowed = Truthy( opts.GetValueOrDefault( "mirror", "0" ) ) && coverage.CoveredTexels > 0 && coverage.FacesOutsideTheSquare == 0;

		if ( mirrorAllowed && !coverage.CanBake )
			r.Log.Add( "bake: warning: mirror=1: baking mirrored (overlapping) UVs last-wins. Only valid for symmetric detail; asymmetric detail will bake the wrong side onto shared texels." );

		if ( !coverage.CanBake && !mirrorAllowed )
		{
			var lows = string.Join( ", ", pairs.Select( p => p.LowName ) );
			return Fail( r, $"{lows} cannot carry a bake: {coverage.Problem} "
				+ (pairs.Count > 1 ? "Low bodies baked by name share one texture, so their UVs must not overlap each other either. " : "")
				+ $"Unwrap it first: add UVProject bodies={pairs[0].LowName} mode=Unwrap" );
		}

		var skew = opts.TryGetValue( "skew", out var skewText ) ? Number( skewText ) : 0f;

		if ( skew < 0f || skew > 1f )
			return Fail( r, $"skew={skewText}: skew is 0 (averaged normals, today's behaviour) to 1 (fully the low face normal)." );

		var skewEdge = opts.TryGetValue( "skewedge", out var skewEdgeText ) ? Number( skewEdgeText ) : 0f;

		if ( skewEdge < 0f )
			return Fail( r, $"skewedge={skewEdgeText}: a hard-edge angle is 0 (off) or above, in degrees." );

		var curvatureWorld = Truthy( opts.GetValueOrDefault( "curvatureworld", "0" ) );

		if ( curvatureWorld )
			maps |= BakeMaps.ObjectNormal | BakeMaps.Position;

		// P9: padding scales with size unless padding= is explicit: 4 texels at
		// 1K is proportionally 4x thinner at 4K, and mipmaps show the seams sooner.
		var padding = opts.TryGetValue( "padding", out var paddingText ) ? (int)Number( paddingText )
			: size >= 8192 ? 32 : size >= 4096 ? 16 : size >= 2048 ? 8 : 4;

		var options = new BakeOptions
		{
			Cage = cageDistance,
			Skew = skew,
			SkewEdgeAngle = skewEdge,
			CurvatureWorld = curvatureWorld,
			ReportMiss = Truthy( opts.GetValueOrDefault( "report", "0" ) ),
			MaxDistance = opts.TryGetValue( "distance", out var distance ) ? Number( distance ) : 0f,
			Supersample = (int)Number( opts.GetValueOrDefault( "ss", "1" ) ),
			FlipGreen = opts.GetValueOrDefault( "green", "gl" ).Equals( "dx", StringComparison.OrdinalIgnoreCase ),
			AoRays = (int)Number( opts.GetValueOrDefault( "rays", "64" ) ),
			AoDistance = opts.TryGetValue( "aodistance", out var aoDistance ) ? Number( aoDistance ) : 0f,
			Padding = padding,
			BevelRadius = round,
			ThicknessDistance = thickDist,
			ThicknessRays = thickRays,
			SixteenBit = bits == 16,
		};

		Directory.CreateDirectory( dir );
		var flipV = Truthy( opts.GetValueOrDefault( "flipv", "0" ) );
		var set = NormalBake.BakeGroups( BakePairs.ToGroups( pairs, idBy == "body" ), size, size, maps, options );
		var written = new Dictionary<string, string>();

		foreach ( var (mapName, map) in set.Maps )
		{
			var path = Path.Combine( dir, $"{name}_{mapName}.png" );
			PngWriter.WriteFile( path, map, flipV );
			written[mapName] = path;
			r.Written.Add( path );
			r.Log.Add( $"baked {mapName} {map.Width}x{map.Height}{(map.Rgb16 is not null ? " 16-bit" : "")} to {path}" );
		}

		if ( options.ReportMiss && set.MissMap is not null )
		{
			var missPath = Path.Combine( dir, $"{name}_miss.png" );
			PngWriter.WriteFile( missPath, set.MissMap, flipV );
			r.Written.Add( missPath );
			r.Log.Add( $"baked miss map (white hit, black missed) to {missPath}" );
		}

		// P5: wire the baked maps into a material slot so the model previews with
		// them immediately, and log the equivalent material lines for other scripts.
		if ( opts.TryGetValue( "slot", out var slotText ) && int.TryParse( slotText, out var slot ) && slot >= 0 )
		{
			var lookPairs = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

			if ( written.TryGetValue( "normal", out var normalPath ) ) lookPairs["normal"] = normalPath;
			if ( written.TryGetValue( "ao", out var aoPath ) ) lookPairs["ao"] = aoPath;

			if ( lookPairs.Count > 0 )
			{
				var look = SlotLook.Parse( lookPairs, studio.MaterialLooks.GetValueOrDefault( slot ), out var lookError );

				if ( look is null )
					return Fail( r, $"slot={slot}: {lookError}" );

				studio.MaterialLooks[slot] = look;
				studio.MarkDirty( 0 );
				r.Log.Add( $"slot {slot} looks {look.Describe()}" );
				r.Log.Add( "material lines for other scripts: " + string.Join( " ", lookPairs.Select( kv => $"material {slot} {kv.Key}={kv.Value}" ) ) );
			}
		}

		foreach ( var p in pairs )
			r.Log.Add( $"bake: {p.LowName} from {string.Join( ", ", p.Highs.Select( h => h.Name ) )}" + (p.Cage is not null ? $", cage {p.CageName}" : "") );

		foreach ( var line in set.Report.Split( '\n' ) )
			r.Log.Add( $"bake: {line}" );

		if ( cagegenGiven || cageauto )
		{
			foreach ( var g in set.GroupMiss )
			{
				if ( g.Samples > 0 && (float)(g.Samples - g.Hits) / g.Samples > 0.02f )
					r.Log.Add( $"bake: warning: cagegen distance for {g.Name} may be too small: {(float)(g.Samples - g.Hits) / g.Samples:P0} of its texels missed." );
			}
		}

		r.Log.Add( $"bake: {(options.FlipGreen ? "DirectX" : "OpenGL")} green, v = 0 at the {(flipV ? "bottom" : "top")}, "
			+ (pairs.Any( p => p.CageName is not null && ( p.CageName.StartsWith( "auto:", StringComparison.Ordinal ) || p.CageName.StartsWith( "cagegen ", StringComparison.Ordinal ) ) ) ? "auto cage" : pairs.Any( p => p.Cage is not null ) ? "cage mesh" : options.Cage > 0f ? $"cage {N( options.Cage )}" : "no cage")
			+ $", {options.Supersample}x{options.Supersample} samples per texel" + (options.BevelRadius > 0f ? $", round {N( options.BevelRadius )}" : "") + (pairs.Count > 1 ? $", {pairs.Count} pairs baked apart" : "") );

		if ( !Truthy( opts.GetValueOrDefault( "vmat", "0" ) ) )
			return true;

		// Texture paths in a .vmat are relative to the assets root, which is the base folder unless
		// assets= says otherwise — the same rule export uses.
		var root = opts.TryGetValue( "assets", out var assets ) ? assets : baseDir;
		string Asset( string path ) => path is null ? null : Path.GetRelativePath( root, path ).Replace( '\\', '/' );
		string Map( string map ) => written.TryGetValue( map, out var path ) ? Asset( path ) : null;
		var vmatPath = Path.Combine( dir, name + ".vmat" );

		if ( shader == "skin" )
		{
			var preset = SkinMaterial.Preset( opts.GetValueOrDefault( "preset", "Body" ) );
			var k = opts.TryGetValue( "k", out var kText ) ? Number( kText ) : 1f;

			if ( k <= 0f )
				return Fail( r, $"k={kText}: translucency is above 0 (1 is real skin, 3-10 gel-like flesh)." );

			// Transmission only where the preset transmits and thickness was baked: the mask is built
			// from the unquantised thickness, never from the 8-bit grey map.
			string transmission = null;

			if ( set.ThicknessUnits is not null && preset.TransmissionColor.x + preset.TransmissionColor.y + preset.TransmissionColor.z > 0f )
			{
				var path = Path.Combine( dir, $"{name}_transmission.png" );
				PngWriter.WriteFile( path, SkinMaterial.TransmissionMask( set, k, padding: options.Padding ), flipV );
				r.Written.Add( path );
				transmission = Asset( path );
			}

			File.WriteAllText( vmatPath, SkinMaterial.VmatSource( preset, opts.GetValueOrDefault( "color" ), Map( "normal" ),
				opts.GetValueOrDefault( "rough" ), Map( "ao" ), transmission, Map( "bentnormal" ) ) );
			r.Written.Add( vmatPath );
			r.Log.Add( $"wrote {vmatPath} (skin {preset.Name}: normal {Map( "normal" ) ?? "default"}, ao {Map( "ao" ) ?? "default"}, "
				+ $"bent normal {Map( "bentnormal" ) ?? "off"}, transmission {transmission ?? "none"}, k {N( k )})" );
		}
		else
		{
			File.WriteAllText( vmatPath, BakeMaterial.VmatSource(
				color: opts.GetValueOrDefault( "color" ),
				normal: Map( "normal" ),
				roughness: opts.GetValueOrDefault( "rough" ),
				ambientOcclusion: Map( "ao" ),
				metalness: opts.TryGetValue( "metal", out var metal ) ? Number( metal ) : 0f ) );

			r.Written.Add( vmatPath );
			r.Log.Add( $"wrote {vmatPath} (complex: normal {Map( "normal" ) ?? "default"}, "
				+ $"ao {Map( "ao" ) ?? "default"}, colour {opts.GetValueOrDefault( "color", "default" )}, rough {opts.GetValueOrDefault( "rough", "default" )})" );
		}

		if ( Asset( dir ).StartsWith( "..", StringComparison.Ordinal ) )
			r.Log.Add( $"bake: note: {dir} is outside the assets root {root}, so the vmat's texture paths will not resolve; pass assets=<root>" );

		return true;
	}
}
