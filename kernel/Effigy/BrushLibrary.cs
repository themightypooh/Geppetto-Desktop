using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// The extensible brush library (godlike-stroke-engine-sculpt-layers, Wave 2 P1).
///
/// The catalog is combinatorial by design: every <see cref="BrushKind"/> with every
/// <see cref="BrushFalloff"/> is a real, distinct configuration (kind changes what the
/// dab does, falloff changes how it fades), and the alpha-carrying kinds get one
/// preset per procedural alpha. That is how a 35-kind kernel honestly ships 200+
/// presets instead of 35 presets with 165 aliases: each entry differs in kind,
/// falloff or alpha, and <see cref="BrushPreset.FalloffWeight"/> proves it.
/// A curated essentials list carries the human names and tags on top.
///
/// Thumbnails are paths, not embedded PNGs: the editor renders a preset's
/// thumbnail from its kind + falloff + alpha on first use and caches it at
/// <see cref="BrushPreset.ThumbnailPath"/>. The library never claims a PNG exists
/// that it did not render.
///
/// Shared folders hold <c>*.brush.json</c> preset files, one preset per file:
/// <c>{"name": "My Clay", "kind": "Clay", "falloff": "Smooth", "tags": ["clay"],
/// "alpha": "proc:pores", "vdm": "vdm:ear", "spacing": 0.5, "rake": 15, "tilt": 10,
/// "thumbnail": "thumbnails/my-clay.png", "description": "..."}</c>. Only
/// <c>name</c> is required; unknown kinds, missing names and malformed JSON are
/// skipped, never thrown — a shared folder with one bad file still loads the rest.
/// </summary>
public sealed class BrushPreset
{
	public string Name = "";
	public BrushKind Kind = BrushKind.Draw;
	public BrushFalloff Falloff = BrushFalloff.Smooth;
	/// <summary>Custom falloff curve (the falloff-curve editor's output); null means the enum falloff.</summary>
	public PiecewiseCurve FalloffCurve;
	public float RakeDegrees;
	public float TiltDegrees;
	public float Spacing = StrokeEngine.DefaultSpacing;
	public float DashRatio = 1f;
	/// <summary>DynTopo target edge length in scene units (dyntopo-brush-integration). 0 = auto (radius x 0.1).</summary>
	public float DetailSize;
	/// <summary>DynTopo on for this brush (dyntopo-brush-integration). Off by default: existing strokes are unchanged.</summary>
	public bool DynTopoEnabled;
	public string AlphaName;
	public string VdmName;
	public string[] Tags = Array.Empty<string>();
	public string ThumbnailPath = "";
	public string Description = "";

	/// <summary>Falloff weight at normalised radius t: the custom curve when set, else the named falloff.</summary>
	public float FalloffWeight( float t )
	{
		if ( FalloffCurve is not null )
			return Math.Clamp( FalloffCurve.Evaluate( Math.Clamp( t, 0f, 1f ) ), 0f, 1f );
		return Brush.Falloff( t, Falloff );
	}

	public BrushPreset Clone() => new()
	{
		Name = Name,
		Kind = Kind,
		Falloff = Falloff,
		FalloffCurve = FalloffCurve?.Clone(),
		RakeDegrees = RakeDegrees,
		TiltDegrees = TiltDegrees,
		Spacing = Spacing,
		DashRatio = DashRatio,
		DetailSize = DetailSize,
		DynTopoEnabled = DynTopoEnabled,
		AlphaName = AlphaName,
		VdmName = VdmName,
		Tags = (string[])Tags.Clone(),
		ThumbnailPath = ThumbnailPath,
		Description = Description,
	};
}

/// <summary>
/// The brush library: the built-in catalog plus presets scanned from shared folders.
/// Search is a case-insensitive substring over name + tags; with a few hundred
/// presets it is a linear scan measured in microseconds (the acceptance bar is 1 s).
/// </summary>
public sealed class BrushLibrary
{
	static readonly object _builtInLock = new();
	static List<BrushPreset> _builtIn;

	/// <summary>Brush kinds whose dabs carry an alpha stamp (the rest ignore AlphaName).</summary>
	public static readonly BrushKind[] AlphaKinds =
	{
		BrushKind.Draw, BrushKind.DrawSharp, BrushKind.Clay, BrushKind.ClayStrips,
		BrushKind.Crease, BrushKind.Blob, BrushKind.Layer, BrushKind.Fill,
		BrushKind.Scrape, BrushKind.Plateau,
	};

	/// <summary>Presets loaded from shared folders (never the built-ins).</summary>
	public readonly List<BrushPreset> Custom = new();

	/// <summary>Folders <see cref="ScanFolder"/> has scanned, in order.</summary>
	public readonly List<string> SharedFolders = new();

	/// <summary>The built-in catalog (lazily built once, shared by every library).</summary>
	public static IReadOnlyList<BrushPreset> BuiltIn
	{
		get
		{
			if ( _builtIn is not null )
				return _builtIn;
			lock ( _builtInLock )
			{
				_builtIn ??= BuildCatalog();
			}
			return _builtIn;
		}
	}

	/// <summary>Built-ins plus scanned custom presets.</summary>
	public int Count => BuiltIn.Count + Custom.Count;

	/// <summary>First preset with this exact name (case-insensitive); a custom preset wins over a built-in of the same name.</summary>
	public BrushPreset ByName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;
		foreach ( var p in Custom )
			if ( string.Equals( p.Name, name, StringComparison.OrdinalIgnoreCase ) )
				return p;
		foreach ( var p in BuiltIn )
			if ( string.Equals( p.Name, name, StringComparison.OrdinalIgnoreCase ) )
				return p;
		return null;
	}

	/// <summary>Every preset carrying this tag (case-insensitive).</summary>
	public List<BrushPreset> ByTag( string tag )
	{
		var output = new List<BrushPreset>();
		if ( string.IsNullOrWhiteSpace( tag ) )
			return output;
		foreach ( var p in All() )
		{
			foreach ( var t in p.Tags )
			{
				if ( string.Equals( t, tag, StringComparison.OrdinalIgnoreCase ) )
				{
					output.Add( p );
					break;
				}
			}
		}
		return output;
	}

	/// <summary>Presets whose name or tags contain <paramref name="query"/> (case-insensitive). Empty query returns everything.</summary>
	public List<BrushPreset> Search( string query )
	{
		var output = new List<BrushPreset>();
		var q = (query ?? "").Trim().ToLowerInvariant();
		foreach ( var p in All() )
		{
			if ( q.Length == 0
				|| p.Name.ToLowerInvariant().Contains( q, StringComparison.Ordinal )
				|| p.Tags.Any( t => t.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ) )
				output.Add( p );
		}
		return output;
	}

	public IEnumerable<BrushPreset> All()
	{
		foreach ( var p in BuiltIn )
			yield return p;
		foreach ( var p in Custom )
			yield return p;
	}

	/// <summary>
	/// Scan a shared folder for <c>*.brush.json</c> preset files and add every preset
	/// that parses. Returns how many were added. A missing folder adds 0; a malformed
	/// file is skipped (counted in <paramref name="skipped"/> when given).
	/// </summary>
	public int ScanFolder( string path, out int skipped )
	{
		skipped = 0;
		if ( string.IsNullOrWhiteSpace( path ) || !Directory.Exists( path ) )
			return 0;
		if ( !SharedFolders.Contains( path, StringComparer.Ordinal ) )
			SharedFolders.Add( path );
		var added = 0;
		foreach ( var file in Directory.EnumerateFiles( path, "*.brush.json", SearchOption.TopDirectoryOnly ) )
		{
			BrushPreset preset;
			try
			{
				preset = ParsePresetFile( File.ReadAllText( file ) );
			}
			catch
			{
				skipped++;
				continue;
			}
			if ( preset is null )
			{
				skipped++;
				continue;
			}
			Custom.Add( preset );
			added++;
		}
		return added;
	}

	public int ScanFolder( string path ) => ScanFolder( path, out _ );

	static BrushPreset ParsePresetFile( string json )
	{
		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		if ( root.ValueKind != JsonValueKind.Object )
			return null;
		if ( !root.TryGetProperty( "name", out var nameEl ) || nameEl.ValueKind != JsonValueKind.String )
			return null;
		var name = nameEl.GetString();
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;
		var preset = new BrushPreset { Name = name.Trim() };
		if ( root.TryGetProperty( "kind", out var kindEl ) && kindEl.ValueKind == JsonValueKind.String
			&& Enum.TryParse<BrushKind>( kindEl.GetString(), ignoreCase: true, out var kind ) )
			preset.Kind = kind;
		else if ( root.TryGetProperty( "kind", out _ ) )
			return null;
		if ( root.TryGetProperty( "falloff", out var fallEl ) && fallEl.ValueKind == JsonValueKind.String
			&& Enum.TryParse<BrushFalloff>( fallEl.GetString(), ignoreCase: true, out var falloff ) )
			preset.Falloff = falloff;
		else if ( root.TryGetProperty( "falloff", out _ ) )
			return null;
		if ( root.TryGetProperty( "tags", out var tagsEl ) && tagsEl.ValueKind == JsonValueKind.Array )
		{
			var tags = new List<string>();
			foreach ( var t in tagsEl.EnumerateArray() )
			{
				if ( t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( t.GetString() ) )
					tags.Add( t.GetString() );
			}
			preset.Tags = tags.ToArray();
		}
		if ( root.TryGetProperty( "alpha", out var alphaEl ) && alphaEl.ValueKind == JsonValueKind.String )
			preset.AlphaName = alphaEl.GetString();
		if ( root.TryGetProperty( "vdm", out var vdmEl ) && vdmEl.ValueKind == JsonValueKind.String )
			preset.VdmName = vdmEl.GetString();
		if ( root.TryGetProperty( "thumbnail", out var thumbEl ) && thumbEl.ValueKind == JsonValueKind.String )
			preset.ThumbnailPath = thumbEl.GetString();
		if ( root.TryGetProperty( "description", out var descEl ) && descEl.ValueKind == JsonValueKind.String )
			preset.Description = descEl.GetString();
		if ( root.TryGetProperty( "spacing", out var spacingEl ) && spacingEl.ValueKind == JsonValueKind.Number )
			preset.Spacing = spacingEl.GetSingle();
		if ( root.TryGetProperty( "rake", out var rakeEl ) && rakeEl.ValueKind == JsonValueKind.Number )
			preset.RakeDegrees = rakeEl.GetSingle();
		if ( root.TryGetProperty( "tilt", out var tiltEl ) && tiltEl.ValueKind == JsonValueKind.Number )
			preset.TiltDegrees = tiltEl.GetSingle();
		return preset;
	}

	static List<BrushPreset> BuildCatalog()
	{
		var catalog = new List<BrushPreset>();
		catalog.AddRange( Essentials() );
		var kinds = Enum.GetValues<BrushKind>();
		var falloffs = Enum.GetValues<BrushFalloff>();
		foreach ( var kind in kinds )
		{
			foreach ( var falloff in falloffs )
			{
				catalog.Add( new BrushPreset
				{
					Name = $"{kind} / {falloff}",
					Kind = kind,
					Falloff = falloff,
					Tags = new[] { kind.ToString().ToLowerInvariant(), falloff.ToString().ToLowerInvariant(), "catalog" },
					ThumbnailPath = $"thumbnails/{Slug( kind.ToString() )}-{Slug( falloff.ToString() )}.png",
					Description = $"{kind} brush with {falloff} falloff.",
				} );
			}
		}
		foreach ( var kind in AlphaKinds )
		{
			foreach ( var alpha in BrushAlpha.ProceduralNames )
			{
				catalog.Add( new BrushPreset
				{
					Name = $"{kind} / {alpha}",
					Kind = kind,
					Falloff = BrushFalloff.Smooth,
					AlphaName = "proc:" + alpha,
					Tags = new[] { kind.ToString().ToLowerInvariant(), alpha, "alpha", "catalog" },
					ThumbnailPath = $"thumbnails/{Slug( kind.ToString() )}-{Slug( alpha )}.png",
					Description = $"{kind} brush stamping the {alpha} alpha.",
				} );
			}
		}
		return catalog;
	}

	static string Slug( string name )
	{
		var chars = name.ToLowerInvariant().Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray();
		return new string( chars );
	}

	/// <summary>Curated named presets with human tags — the ones an artist actually searches for.</summary>
	static IEnumerable<BrushPreset> Essentials()
	{
		yield return new BrushPreset { Name = "Clay Build", Kind = BrushKind.Clay, Falloff = BrushFalloff.Smooth, Tags = new[] { "clay", "build", "form" }, ThumbnailPath = "thumbnails/clay-build.png", Description = "Build up form toward a plane, Blender Clay." };
		yield return new BrushPreset { Name = "Clay Strips", Kind = BrushKind.ClayStrips, Falloff = BrushFalloff.Smooth, Tags = new[] { "clay", "strips", "planes" }, ThumbnailPath = "thumbnails/clay-strips.png", Description = "Square-footprint clay strips laid along the stroke." };
		yield return new BrushPreset { Name = "Crease Sharp", Kind = BrushKind.Crease, Falloff = BrushFalloff.Sharp, Tags = new[] { "crease", "groove", "hard-surface" }, ThumbnailPath = "thumbnails/crease-sharp.png", Description = "Cut a groove and pinch the sides in (Dam Standard)." };
		yield return new BrushPreset { Name = "Blob Inflate", Kind = BrushKind.Blob, Falloff = BrushFalloff.Smooth, Tags = new[] { "blob", "inflate", "organic" }, ThumbnailPath = "thumbnails/blob-inflate.png", Description = "Push out a rounded swelling." };
		yield return new BrushPreset { Name = "Draw Standard", Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth, Tags = new[] { "draw", "standard" }, ThumbnailPath = "thumbnails/draw-standard.png", Description = "The default draw brush." };
		yield return new BrushPreset { Name = "Draw Sharp Line", Kind = BrushKind.DrawSharp, Falloff = BrushFalloff.Sharp, Tags = new[] { "draw", "sharp", "line" }, ThumbnailPath = "thumbnails/draw-sharp-line.png", Description = "Fine lines with a very sharp falloff." };
		yield return new BrushPreset { Name = "Smooth", Kind = BrushKind.Smooth, Falloff = BrushFalloff.Smooth, Tags = new[] { "smooth", "polish" }, ThumbnailPath = "thumbnails/smooth.png", Description = "Even out the surface." };
		yield return new BrushPreset { Name = "Flatten", Kind = BrushKind.Flatten, Falloff = BrushFalloff.Linear, Tags = new[] { "flatten", "plane" }, ThumbnailPath = "thumbnails/flatten.png", Description = "Flatten toward the local plane." };
		yield return new BrushPreset { Name = "Pinch", Kind = BrushKind.Pinch, Falloff = BrushFalloff.Sharp, Tags = new[] { "pinch", "edge" }, ThumbnailPath = "thumbnails/pinch.png", Description = "Pull the surface toward the stroke line." };
		yield return new BrushPreset { Name = "Grab", Kind = BrushKind.Grab, Falloff = BrushFalloff.Smooth, Tags = new[] { "grab", "move" }, ThumbnailPath = "thumbnails/grab.png", Description = "Grab and move a region." };
		yield return new BrushPreset { Name = "Snake Hook", Kind = BrushKind.SnakeHook, Falloff = BrushFalloff.Smooth, Tags = new[] { "snake", "hook", "tentacle" }, ThumbnailPath = "thumbnails/snake-hook.png", Description = "Pull out tentacles and limbs." };
		yield return new BrushPreset { Name = "Scrape", Kind = BrushKind.Scrape, Falloff = BrushFalloff.Linear, Tags = new[] { "scrape", "flat", "hard-surface" }, ThumbnailPath = "thumbnails/scrape.png", Description = "Cut high points down to the plane." };
		yield return new BrushPreset { Name = "Pores Stamp", Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth, AlphaName = "proc:pores", Tags = new[] { "pores", "skin", "stamp", "alpha" }, ThumbnailPath = "thumbnails/pores-stamp.png", Description = "Stamp skin pores." };
		yield return new BrushPreset { Name = "Scales Stamp", Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth, AlphaName = "proc:scales", Tags = new[] { "scales", "reptile", "stamp", "alpha" }, ThumbnailPath = "thumbnails/scales-stamp.png", Description = "Stamp overlapping scales." };
		yield return new BrushPreset { Name = "Horn Hook VDM", Kind = BrushKind.Draw, Falloff = BrushFalloff.Smooth, VdmName = "vdm:horn_hook", Tags = new[] { "horn", "vdm", "stamp" }, ThumbnailPath = "thumbnails/horn-hook-vdm.png", Description = "Stamp a curling horn hook (vector displacement)." };
		yield return new BrushPreset { Name = "Mask", Kind = BrushKind.Draw, Falloff = BrushFalloff.Constant, Tags = new[] { "mask", "protect" }, ThumbnailPath = "thumbnails/mask.png", Description = "Paint a protective mask (constant falloff)." };
	}
}
