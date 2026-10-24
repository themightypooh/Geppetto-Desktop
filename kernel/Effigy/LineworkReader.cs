using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Effigy;

/// <summary>One closed 2D shape from a Linework drawing: an outer ring plus its inner holes.
/// Rings are lists of 2D points in drawing units. Winding is whatever the drawing wrote;
/// consumers that build walls normalize it themselves (see Inflate2DFeature).</summary>
public sealed class LineworkProfile
{
	public string Layer = "";
	public string StrokeId = "";
	public List<Vec2> Outer = new();
	public List<List<Vec2>> Holes = new();
}

/// <summary>One stroke of a .linework document, in the Linework app's own JSON shape:
/// <c>pts</c> is flat x,y,pressure triples, <c>holes</c> is a list of flat x,y,pressure
/// rings, <c>closed</c> marks a fill/shape ring. See linework/src/model.cpp
/// (strokeToJson / docToJson) for the format this mirrors.</summary>
public sealed class LineworkStroke
{
	public string Id = "";
	public string Tool = "pen";
	public string Color = "#ecebe6";
	public float Size;
	public float Opacity = 1f;
	public bool Closed;
	public List<Vec2> Points = new();
	public List<float> Pressures = new();
	public List<List<Vec2>> Holes = new();
}

/// <summary>One layer of a .linework document.</summary>
public sealed class LineworkLayer
{
	public string Id = "";
	public string Name = "Layer";
	public bool Visible = true;
	public bool Ref;
	public List<LineworkStroke> Strokes = new();
}

/// <summary>A parsed .linework document: layers of strokes plus the document symmetry flag.</summary>
public sealed class LineworkDocument
{
	public string Name = "";
	public bool SymOn;
	public float SymX;
	public List<LineworkLayer> Layers = new();

	public int ClosedStrokeCount
	{
		get
		{
			var n = 0;
			foreach ( var l in Layers ) foreach ( var s in l.Strokes ) if ( s.Closed ) n++;
			return n;
		}
	}

	public int OpenStrokeCount
	{
		get
		{
			var n = 0;
			foreach ( var l in Layers ) foreach ( var s in l.Strokes ) if ( !s.Closed ) n++;
			return n;
		}
	}
}

/// <summary>Reads Linework 2D drawings (.linework JSON) into closed profiles Effigy can build
/// from. This is the roadmap Lane O reader (ASSET-MAKER-ROADMAP.md §O2): it parses the real
/// Linework format — layers, strokes with x,y,pressure triples, closed flags, hole rings,
/// sym on/x — and hands back outer ring + hole rings per closed stroke.
///
/// What it deliberately does NOT do (roadmap follow-ups, not faked here): open strokes as
/// pressure splines (ToPressureSplines), layer rasterization into PaintCanvas/BrushAlpha
/// (RenderLayer), and 2D booleans between strokes. A profile is exactly one closed stroke
/// plus the hole rings that stroke itself carries; a closed stroke nested inside another
/// is a second profile, not an inferred hole, because the drawing never said so.
///
/// Symmetry: when the document has sym on, each profile is ALSO emitted mirrored across
/// x = SymX (winding restored), so a half-drawn shape becomes its mirrored pair. The
/// original profile is always kept first.</summary>
public static class LineworkReader
{
	/// <summary>Standard shoelace area: positive for counter-clockwise rings in XY.</summary>
	public static float SignedArea( IReadOnlyList<Vec2> ring )
	{
		var sum = 0f;
		for ( var i = 0; i < ring.Count; i++ )
		{
			var a = ring[i];
			var b = ring[(i + 1) % ring.Count];
			sum += a.x * b.y - b.x * a.y;
		}
		return sum / 2f;
	}

	/// <summary>Drops consecutive duplicate points and a closing point equal to the first.
/// Returns false when fewer than 3 distinct points survive.</summary>
	public static bool CleanRing( List<Vec2> ring, out List<Vec2> cleaned )
	{
		cleaned = new List<Vec2>();
		foreach ( var p in ring )
		{
			if ( cleaned.Count > 0 && (cleaned[^1] - p).LengthSquared < 1e-12f )
				continue;
			cleaned.Add( p );
		}
		if ( cleaned.Count > 1 && (cleaned[0] - cleaned[^1]).LengthSquared < 1e-12f )
			cleaned.RemoveAt( cleaned.Count - 1 );
		return cleaned.Count >= 3;
	}

	static List<Vec2> ReadRing( JsonElement arr, out string error )
	{
		error = null;
		var pts = new List<Vec2>();
		if ( arr.ValueKind != JsonValueKind.Array )
		{
			error = "ring is not a number array";
			return pts;
		}
		var nums = new List<float>();
		foreach ( var v in arr.EnumerateArray() )
		{
			if ( v.ValueKind != JsonValueKind.Number )
			{
				error = "ring contains a non-number";
				return new List<Vec2>();
			}
			nums.Add( v.GetSingle() );
		}
		for ( var i = 0; i + 2 < nums.Count; i += 3 )
			pts.Add( new Vec2( nums[i], nums[i + 1] ) );
		return pts;
	}

	static List<float> ReadPressures( JsonElement arr )
	{
		var ps = new List<float>();
		if ( arr.ValueKind != JsonValueKind.Array ) return ps;
		var nums = new List<float>();
		foreach ( var v in arr.EnumerateArray() )
			if ( v.ValueKind == JsonValueKind.Number ) nums.Add( v.GetSingle() );
		for ( var i = 0; i + 2 < nums.Count; i += 3 ) ps.Add( nums[i + 2] );
		return ps;
	}

	static string Str( JsonElement o, string key, string fallback = "" ) =>
		o.TryGetProperty( key, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

	static bool Flag( JsonElement o, string key, bool fallback = false ) =>
		o.TryGetProperty( key, out var v ) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
			? v.GetBoolean() : fallback;

	static float Num( JsonElement o, string key, float fallback = 0f ) =>
		o.TryGetProperty( key, out var v ) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

	/// <summary>Parses .linework JSON text. Never throws on bad input: false + error instead.</summary>
	public static bool TryParse( string json, out LineworkDocument doc, out string error )
	{
		doc = null;
		error = null;
		JsonDocument parsed;
		try { parsed = JsonDocument.Parse( json ); }
		catch ( Exception e ) { error = $"not valid JSON: {e.Message}"; return false; }

		using ( parsed )
		{
			var root = parsed.RootElement;
			if ( root.ValueKind != JsonValueKind.Object ) { error = "document is not a JSON object"; return false; }
			if ( !root.TryGetProperty( "layers", out var layers ) || layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() == 0 )
			{
				error = "not a Linework drawing: no 'layers' array (a .linework file has layers of strokes)";
				return false;
			}
			doc = new LineworkDocument { Name = Str( root, "name" ) };
			if ( root.TryGetProperty( "sym", out var sym ) && sym.ValueKind == JsonValueKind.Object )
			{
				doc.SymOn = Flag( sym, "on" );
				doc.SymX = Num( sym, "x" );
			}
			foreach ( var lv in layers.EnumerateArray() )
			{
				if ( lv.ValueKind != JsonValueKind.Object ) continue;
				var layer = new LineworkLayer
				{
					Id = Str( lv, "id" ),
					Name = Str( lv, "name", "Layer" ),
					Visible = Flag( lv, "visible", true ),
					Ref = Flag( lv, "ref" ),
				};
				if ( !lv.TryGetProperty( "strokes", out var strokes ) || strokes.ValueKind != JsonValueKind.Array )
				{
					doc.Layers.Add( layer );
					continue;
				}
				foreach ( var sv in strokes.EnumerateArray() )
				{
					if ( sv.ValueKind != JsonValueKind.Object ) continue;
					var stroke = new LineworkStroke
					{
						Id = Str( sv, "id" ),
						Tool = Str( sv, "tool", "pen" ),
						Color = Str( sv, "color", "#ecebe6" ),
						Size = Num( sv, "size" ),
						Opacity = Num( sv, "opacity", 1f ),
						Closed = Flag( sv, "closed" ),
					};
					if ( sv.TryGetProperty( "pts", out var pts ) )
					{
						stroke.Points = ReadRing( pts, out var perr );
						if ( perr is not null ) { error = $"stroke '{stroke.Id}': {perr}"; return false; }
						stroke.Pressures = ReadPressures( pts );
					}
					if ( sv.TryGetProperty( "holes", out var holes ) && holes.ValueKind == JsonValueKind.Array )
					{
						foreach ( var h in holes.EnumerateArray() )
						{
							var ring = ReadRing( h, out var herr );
							if ( herr is not null ) { error = $"stroke '{stroke.Id}' hole: {herr}"; return false; }
							stroke.Holes.Add( ring );
						}
					}
					layer.Strokes.Add( stroke );
				}
				doc.Layers.Add( layer );
			}
		}
		return true;
	}

	/// <summary>Resolves a linework= value: an existing file path first, then a drawing id
	/// under ~/.local/share/linework/drawings/, then a bare id with .linework appended.</summary>
	public static bool TryLoad( string pathOrId, out LineworkDocument doc, out string resolvedPath, out string error )
	{
		doc = null;
		resolvedPath = null;
		error = null;
		var tried = new List<string>();
		string TryPath( string p )
		{
			tried.Add( p );
			return File.Exists( p ) ? p : null;
		}
		var found = TryPath( pathOrId );
		if ( found is null )
		{
			var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
			found = TryPath( Path.Combine( home, ".local", "share", "linework", "drawings", pathOrId ) );
		}
		if ( found is null && !pathOrId.EndsWith( ".linework", StringComparison.OrdinalIgnoreCase ) )
		{
			var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
			found = TryPath( Path.Combine( home, ".local", "share", "linework", "drawings", pathOrId + ".linework" ) );
			found ??= TryPath( pathOrId + ".linework" );
		}
		if ( found is null )
		{
			error = $"no .linework drawing at '{pathOrId}'. Tried: {string.Join( "; ", tried )}. " +
				"Give a file path, or a drawing id from ~/.local/share/linework/drawings/.";
			return false;
		}
		string text;
		try { text = File.ReadAllText( found ); }
		catch ( Exception e ) { error = $"cannot read '{found}': {e.Message}"; return false; }
		resolvedPath = found;
		if ( !TryParse( text, out doc, out error ) )
		{
			error = $"'{found}': {error}";
			return false;
		}
		return true;
	}

	/// <summary>Closed profiles from a document. layerFilter names a layer by name or id
	/// (case-insensitive); empty means every VISIBLE layer. Open strokes are never profiles
	/// and are simply not returned — the caller reports them when the result is empty.</summary>
	public static List<LineworkProfile> Profiles( LineworkDocument doc, string layerFilter )
	{
		var out_ = new List<LineworkProfile>();
		var filter = string.IsNullOrWhiteSpace( layerFilter ) ? null : layerFilter.Trim();
		foreach ( var layer in doc.Layers )
		{
			if ( filter is not null )
			{
				if ( !layer.Name.Equals( filter, StringComparison.OrdinalIgnoreCase )
					&& !layer.Id.Equals( filter, StringComparison.OrdinalIgnoreCase ) )
					continue;
			}
			else if ( !layer.Visible ) continue;

			foreach ( var stroke in layer.Strokes )
			{
				if ( !stroke.Closed ) continue;
				if ( !CleanRing( stroke.Points, out var outer ) ) continue;
				var profile = new LineworkProfile { Layer = layer.Name, StrokeId = stroke.Id, Outer = outer };
				foreach ( var hole in stroke.Holes )
					if ( CleanRing( hole, out var h ) ) profile.Holes.Add( h );
				out_.Add( profile );

				if ( doc.SymOn )
				{
					// Mirror across x = SymX, then restore each ring's winding (mirroring
					// flips it) so the mirrored profile builds exactly like the original.
					List<Vec2> Mirror( List<Vec2> ring )
					{
						var m = new List<Vec2>( ring.Count );
						foreach ( var p in ring ) m.Add( new Vec2( 2f * doc.SymX - p.x, p.y ) );
						m.Reverse();
						return m;
					}
					var mirrored = new LineworkProfile
					{
						Layer = layer.Name,
						StrokeId = stroke.Id + "#mirror",
						Outer = Mirror( outer ),
					};
					foreach ( var h in profile.Holes ) mirrored.Holes.Add( Mirror( h ) );
					out_.Add( mirrored );
				}
			}
		}
		return out_;
	}

	/// <summary>Inline ring grammar for scripts that have no .linework file, matching the
	/// ShadowBox ring grammar: rings separated by '|', points by ';', each point 'u,v'.
	/// The first ring is the outer loop; the rest are holes. This is the grammar the
	/// ShadowBox linework= form will eventually feed from parsed profiles.</summary>
	public static bool TryParseRings( string text, out LineworkProfile profile, out string error )
	{
		profile = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( text ) ) { error = "rings= is empty: give rings like '0,0;10,0;10,10;0,10'"; return false; }
		var rings = new List<List<Vec2>>();
		foreach ( var part in text.Split( '|' ) )
		{
			var ring = new List<Vec2>();
			foreach ( var pt in part.Split( ';' ) )
			{
				var xy = pt.Trim().Split( ',' );
				if ( xy.Length != 2
					|| !float.TryParse( xy[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x )
					|| !float.TryParse( xy[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y ) )
				{
					error = $"rings=: point '{pt.Trim()}' is not 'u,v'. Rings are '|'-separated, points ';'-separated.";
					return false;
				}
				ring.Add( new Vec2( x, y ) );
			}
			if ( !CleanRing( ring, out var cleaned ) )
			{
				error = "rings=: a ring has fewer than 3 distinct points, so it encloses nothing.";
				return false;
			}
			rings.Add( cleaned );
		}
		profile = new LineworkProfile { Layer = "", StrokeId = "rings", Outer = rings[0] };
		for ( var i = 1; i < rings.Count; i++ ) profile.Holes.Add( rings[i] );
		return true;
	}
}
