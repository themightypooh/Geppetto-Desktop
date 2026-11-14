using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// Hotspot texturing (hammer-hotspot-texturing): match brush faces to pre-defined
/// rectangles (trims / panels) and apply the matching rectangle's UVs, Hammer++-style.
/// Rectangles come from material metadata: a <see cref="HotspotRect"/> names a material
/// and a rectangle inside that material's 0..1 texture square, and the material bridge
/// carries them as VMT keys ("$hotspot_&lt;name&gt;" "uMin vMin uMax vMax") — the bridge
/// preserves unknown VMT keys, so hotspot metadata survives a VMT round-trip untouched.
/// See <see cref="HotspotSet.FromVmt"/> / <see cref="HotspotSet.WriteToVmt"/>.
///
/// DEPENDENCY NOTE. This file builds against the two filed dependencies as they landed:
///   - hammer-face-material-uv-model: <see cref="FaceMaterial"/> (material name + FaceUv)
///     and <see cref="FaceUv"/> (projected bounds, Fit, per-corner UVs). A face's geometry
///     is plain corners + normal, exactly as FaceMaterialTests does it.
///   - hammer-material-bridge-pbr-vmt-gltf: <see cref="VmtMaterial"/> (Parse / ToText /
///     Get / Set, unknown keys preserved). HotspotRect is defined HERE, not in
///     MaterialBridge.cs — that file is owned by the bridge ticket (hammer-11) and is
///     read-only for this ticket; the bridge needs no edit because it already carries
///     unknown keys losslessly.
///   - hammer-brush-core-plane-based-brushes (MapBrush) has NOT landed: no MapBrush.cs
///     exists in the tree. So there is no brush type to take here; <see cref="HotspotFace"/>
///     (FaceMaterial + corners + normal) is the per-face carrier, and
///     <see cref="HotspotTexturing.MatchHotspots"/> works over a list of those. When
///     MapBrush lands, a one-line adapter maps its faces to HotspotFace and this code
///     applies unchanged — the same arrangement FaceMaterial made.
///
/// SCORING. A face fits a rectangle by ASPECT RATIO: the rectangle's texture-space
/// aspect (or its 90-degree rotation) against the face's projected aspect. Score is
/// 1 / (1 + |ln(faceAspect / rectAspect)|): 1.0 is an exact aspect match, and a face
/// only matches when the best aspect error is within
/// <see cref="HotspotTexturing.DefaultMaxAspectError"/> (0.35, about a 42% aspect
/// deviation). Texture-space rectangles carry no world scale, so size cannot score —
/// a 128x16 trim and a 256x32 trim match the same 8:1 rectangle, which is exactly
/// how Hammer hotspots behave. Ties break deterministically: smallest error, then
/// largest rect area, then name (ordinal).
///
/// APPLYING. <see cref="HotspotTexturing.ApplyHotspot(FaceMaterial, HotspotRect)"/> is
/// the spec's two-argument form: it maps the face's already-fit 0..1 UV square onto
/// the rectangle (use it after FaceUv.AlignFit). The bounds / vertices overloads fit
/// the face's projected bounds onto the rectangle directly. A rotated match bakes the
/// 90-degree turn into the FaceUv (rotation 90, swapped scales), so the applied UVs
/// always land inside the rectangle. Applying a hotspot also sets the face's material
/// to the rectangle's material — that is how a nodraw face gets textured by a hotspot.
/// A face with no fitting rectangle is left UNCHANGED (same FaceMaterial instance)
/// and the match result names why (<see cref="HotspotMatchOutcome.NoFit"/> /
/// <see cref="HotspotMatchOutcome.NoHotspots"/>), never a silent null.
///
/// RANDOMISE-SHIFT. <see cref="HotspotOptions"/> adds a deterministic per-face UV
/// offset (seed + face index hashed, never System.Random state), bounded by MaxShift
/// as a fraction of the rect size, so repeated runs with the same seed reproduce the
/// same variation and a different seed varies it.
///
/// NO UI. Backend / data-model / API only; no Face Edit UI, no viewer code.
/// </summary>
public sealed class HotspotRect : IEquatable<HotspotRect>
{
	public string Name { get; }
	public string MaterialName { get; }
	public float UMin { get; }
	public float VMin { get; }
	public float UMax { get; }
	public float VMax { get; }

	/// <summary>Rect width in texture space (UMax - UMin).</summary>
	public float Width => UMax - UMin;

	/// <summary>Rect height in texture space (VMax - VMin).</summary>
	public float Height => VMax - VMin;

	/// <summary>Width / Height; a trim is wide (&gt; 1), a panel is near 1.</summary>
	public float Aspect => Width / Height;

	/// <summary>
	/// A hotspot rectangle inside the 0..1 texture square of <paramref name="materialName"/>.
	/// Throws <see cref="ArgumentException"/> naming the problem for a blank name/material,
	/// an inverted or zero-size rect, or a rect outside 0..1 — a hotspot outside its own
	/// texture has no meaning, so it is rejected here instead of sampling garbage later.
	/// </summary>
	public HotspotRect( string name, string materialName, float uMin, float vMin, float uMax, float vMax )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A hotspot needs a name; a blank name is not a hotspot.", nameof( name ) );
		if ( string.IsNullOrWhiteSpace( materialName ) )
			throw new ArgumentException( "A hotspot needs a material name; a blank name is not a material.", nameof( materialName ) );
		if ( !( uMax > uMin ) )
			throw new ArgumentException( $"Hotspot '{name.Trim()}': UMax ({uMax}) must be greater than UMin ({uMin})." );
		if ( !( vMax > vMin ) )
			throw new ArgumentException( $"Hotspot '{name.Trim()}': VMax ({vMax}) must be greater than VMin ({vMin})." );
		if ( uMin < 0f || vMin < 0f || uMax > 1f || vMax > 1f )
			throw new ArgumentException( $"Hotspot '{name.Trim()}': rect ({uMin}, {vMin})..({uMax}, {vMax}) must sit inside the 0..1 texture square." );
		Name = name.Trim();
		MaterialName = materialName.Trim();
		UMin = uMin;
		VMin = vMin;
		UMax = uMax;
		VMax = vMax;
	}

	/// <summary>True when the texture-space point (u, v) falls inside this rectangle (edges inclusive).</summary>
	public bool Contains( float u, float v ) => u >= UMin && u <= UMax && v >= VMin && v <= VMax;

	/// <summary>
	/// The VMT key this rect travels under: "$hotspot_&lt;name&gt;", with anything that is
	/// not a letter, digit or underscore in the name replaced by '_'. The bridge writes
	/// unknown keys verbatim, so this key survives a VMT round-trip.
	/// </summary>
	public string VmtKey
	{
		get
		{
			var chars = Name.ToCharArray();
			for ( var i = 0; i < chars.Length; i++ )
				if ( !char.IsLetterOrDigit( chars[i] ) && chars[i] != '_' ) chars[i] = '_';
			return "$hotspot_" + new string( chars );
		}
	}

	/// <summary>The VMT value: "uMin vMin uMax vMax", invariant culture, round-trip ("R") floats.</summary>
	public string VmtValue =>
		F( UMin ) + " " + F( VMin ) + " " + F( UMax ) + " " + F( VMax );

	static string F( float value ) => value.ToString( "R", CultureInfo.InvariantCulture );

	/// <summary>Write this rect into a VMT as its "$hotspot_&lt;name&gt;" key (other keys untouched).</summary>
	public void WriteToVmt( VmtMaterial vmt )
	{
		if ( vmt is null ) throw new ArgumentNullException( nameof( vmt ) );
		vmt.Set( VmtKey, VmtValue );
	}

	/// <summary>
	/// Parse one VMT hotspot value ("uMin vMin uMax vMax"). Throws
	/// <see cref="FormatException"/> naming the hotspot when the value is not four numbers.
	/// </summary>
	public static HotspotRect FromVmtValue( string name, string materialName, string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			throw new FormatException( $"Hotspot '{name}': VMT value is blank; expected \"uMin vMin uMax vMax\"." );
		var parts = value.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 4 )
			throw new FormatException( $"Hotspot '{name}': VMT value \"{value}\" is not four numbers (uMin vMin uMax vMax)." );
		var nums = new float[4];
		for ( var i = 0; i < 4; i++ )
			if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i] ) )
				throw new FormatException( $"Hotspot '{name}': VMT value \"{value}\" has a non-number at position {i}." );
		return new HotspotRect( name, materialName, nums[0], nums[1], nums[2], nums[3] );
	}

	/// <summary>JSON form: {"name": ..., "material": ..., "rect": [uMin, vMin, uMax, vMax]}.</summary>
	public JsonObject ToJson() => new()
	{
		["name"] = Name,
		["material"] = MaterialName,
		["rect"] = new JsonArray( (double)UMin, (double)VMin, (double)UMax, (double)VMax ),
	};

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>
	/// Parse <see cref="ToJson"/> output. A missing or blank "material" falls back to
	/// <paramref name="defaultMaterial"/> (the enclosing set's material); a missing "rect"
	/// or a non-number in it throws naming the hotspot.
	/// </summary>
	public static HotspotRect FromJson( JsonObject json, string defaultMaterial = null )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var name = json["name"]?.GetValue<string>();
		var material = json["material"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( material ) ) material = defaultMaterial;
		if ( json["rect"] is not JsonArray rect || rect.Count != 4 )
			throw new ArgumentException( $"Hotspot '{name ?? "?"}' JSON needs a 'rect' of [uMin, vMin, uMax, vMax]." );
		var nums = new float[4];
		for ( var i = 0; i < 4; i++ )
		{
			try { nums[i] = (float)rect[i]!.GetValue<double>(); }
			catch ( Exception ex ) { throw new ArgumentException( $"Hotspot '{name ?? "?"}': rect[{i}] is not a number.", ex ); }
		}
		return new HotspotRect( name, material, nums[0], nums[1], nums[2], nums[3] );
	}

	public bool Equals( HotspotRect other ) =>
		other is not null && Name == other.Name && MaterialName == other.MaterialName &&
		UMin == other.UMin && VMin == other.VMin && UMax == other.UMax && VMax == other.VMax;

	public override bool Equals( object obj ) => obj is HotspotRect other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Name, MaterialName, UMin, VMin, UMax, VMax );
	public override string ToString() => $"{Name} ({MaterialName}) [{UMin:0.###}, {VMin:0.###}]..[{UMax:0.###}, {VMax:0.###}]";
}

/// <summary>
/// One material's hotspot set: every rectangle samples from the same material's
/// texture. A rect whose material differs from the set's is rejected — a set is the
/// material-metadata unit, and mixing materials in it would apply the wrong texture.
/// </summary>
public sealed class HotspotSet
{
	public string MaterialName { get; }
	public IReadOnlyList<HotspotRect> Rects { get; }

	public HotspotSet( string materialName, IEnumerable<HotspotRect> rects )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) )
			throw new ArgumentException( "A hotspot set needs a material name; a blank name is not a material.", nameof( materialName ) );
		if ( rects is null ) throw new ArgumentNullException( nameof( rects ) );
		MaterialName = materialName.Trim();
		var list = new List<HotspotRect>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var rect in rects )
		{
			if ( rect is null ) throw new ArgumentException( "A hotspot set cannot contain a null rect." );
			if ( !string.Equals( rect.MaterialName, MaterialName, StringComparison.OrdinalIgnoreCase ) )
				throw new ArgumentException( $"Hotspot '{rect.Name}' belongs to material '{rect.MaterialName}', not '{MaterialName}'." );
			if ( !seen.Add( rect.Name ) )
				throw new ArgumentException( $"Hotspot set for '{MaterialName}' has two hotspots named '{rect.Name}'." );
			list.Add( rect );
		}
		Rects = list;
	}

	/// <summary>The rect with this name (case-insensitive), or null when the set has none.</summary>
	public HotspotRect Find( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;
		foreach ( var rect in Rects )
			if ( string.Equals( rect.Name, name.Trim(), StringComparison.OrdinalIgnoreCase ) ) return rect;
		return null;
	}

	/// <summary>JSON form: {"material": ..., "hotspots": [HotspotRect JSON, ...]}.</summary>
	public JsonObject ToJson()
	{
		var arr = new JsonArray();
		foreach ( var rect in Rects ) arr.Add( rect.ToJson() );
		return new JsonObject { ["material"] = MaterialName, ["hotspots"] = arr };
	}

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>
	/// Parse <see cref="ToJson"/> output. A rect entry with no "material" inherits the
	/// set's; one that names a different material throws naming both.
	/// </summary>
	public static HotspotSet FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var material = json["material"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( material ) )
			throw new ArgumentException( "Hotspot set JSON is missing 'material'." );
		var rects = new List<HotspotRect>();
		if ( json["hotspots"] is JsonArray arr )
			foreach ( var node in arr )
			{
				if ( node is not JsonObject obj )
					throw new ArgumentException( "Hotspot set JSON: each 'hotspots' entry must be an object." );
				rects.Add( HotspotRect.FromJson( obj, material ) );
			}
		return new HotspotSet( material, rects );
	}

	public static HotspotSet FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "Hotspot set JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "Hotspot set JSON text is not an object." );
		return FromJson( obj );
	}

	/// <summary>
	/// Read every "$hotspot" / "$hotspot_&lt;name&gt;" key out of a VMT (in key order, so the
	/// set is deterministic). <paramref name="materialName"/> is the material the VMT
	/// describes — a VMT carries no material path of its own, only $basetexture. A VMT
	/// with no hotspot keys yields an empty set, not an error.
	/// </summary>
	public static HotspotSet FromVmt( VmtMaterial vmt, string materialName )
	{
		if ( vmt is null ) throw new ArgumentNullException( nameof( vmt ) );
		if ( string.IsNullOrWhiteSpace( materialName ) )
			throw new ArgumentException( "A hotspot set from a VMT needs a material name; a blank name is not a material." );
		var rects = new List<HotspotRect>();
		foreach ( var key in vmt.KeyOrder )
		{
			if ( key is null ) continue;
			string name = null;
			if ( key.Equals( "$hotspot", StringComparison.OrdinalIgnoreCase ) ) name = "hotspot";
			else if ( key.StartsWith( "$hotspot_", StringComparison.OrdinalIgnoreCase ) )
				name = key.Substring( "$hotspot_".Length );
			else continue;
			if ( string.IsNullOrWhiteSpace( name ) ) continue;
			rects.Add( HotspotRect.FromVmtValue( name, materialName, vmt.Get( key ) ) );
		}
		return new HotspotSet( materialName, rects );
	}

	/// <summary>Write every rect into a VMT as its "$hotspot_&lt;name&gt;" key (other keys untouched).</summary>
	public void WriteToVmt( VmtMaterial vmt )
	{
		if ( vmt is null ) throw new ArgumentNullException( nameof( vmt ) );
		foreach ( var rect in Rects ) rect.WriteToVmt( vmt );
	}
}

/// <summary>
/// Why a hotspot match came out the way it did. Matched means the face was (or will
/// be) re-textured; NoHotspots / NoFit mean the face is left exactly as it was, and
/// <see cref="HotspotMatch.Reason"/> names the cause — a non-match is a named result,
/// never a silent null or a swallowed face.
/// </summary>
public enum HotspotMatchOutcome
{
	/// <summary>A hotspot fits: the face takes that rect's material and UVs.</summary>
	Matched,
	/// <summary>No hotspot was offered at all (empty list, or none for the requested material).</summary>
	NoHotspots,
	/// <summary>Hotspots were offered, but the closest one's aspect is too far from the face's.</summary>
	NoFit,
}

/// <summary>One <see cref="HotspotTexturing.Match"/> result: outcome, winning rect, score, rotation, reason.</summary>
public sealed class HotspotMatch
{
	public HotspotMatchOutcome Outcome { get; }
	/// <summary>The winning rect, or null unless <see cref="Outcome"/> is Matched.</summary>
	public HotspotRect Rect { get; }
	/// <summary>1 / (1 + aspect error): 1.0 is an exact aspect match, 0 when nothing matched.</summary>
	public float Score { get; }
	/// <summary>True when the rect only fits turned 90 degrees (face long axis against rect long axis).</summary>
	public bool Rotated { get; }
	/// <summary>The face's projected aspect (width / height) that was scored.</summary>
	public float FaceAspect { get; }
	/// <summary>Human-readable reason, always set: which rect won and why, or why nothing fit.</summary>
	public string Reason { get; }

	/// <summary>True exactly when <see cref="Outcome"/> is Matched.</summary>
	public bool Found => Outcome == HotspotMatchOutcome.Matched;

	public HotspotMatch( HotspotMatchOutcome outcome, HotspotRect rect, float score, bool rotated, float faceAspect, string reason )
	{
		Outcome = outcome;
		Rect = rect;
		Score = score;
		Rotated = rotated;
		FaceAspect = faceAspect;
		Reason = reason ?? "";
	}

	public override string ToString() => $"{Outcome}: {Reason}";
}

/// <summary>
/// Randomise-shift options for <see cref="HotspotTexturing.MatchHotspots"/>: a
/// deterministic per-face UV offset so repeated faces do not sample identically.
/// The offset is a hash of (seed, face index) — no shared Random state, so the same
/// seed and face order always reproduce the same variation, and a different seed
/// varies it. MaxShift is a fraction of the rect size per axis (0..0.5); the default
/// 0.1 moves a face by at most a tenth of its rect.
/// </summary>
public sealed class HotspotOptions
{
	public bool RandomiseShift { get; }
	public int Seed { get; }
	public float MaxShift { get; }

	public HotspotOptions( bool randomiseShift = false, int seed = 0, float maxShift = HotspotTexturing.DefaultMaxShift )
	{
		if ( maxShift < 0f || maxShift > 0.5f )
			throw new ArgumentException( $"Randomise-shift MaxShift is {maxShift}: it must be 0..0.5 (a fraction of the rect size)." );
		RandomiseShift = randomiseShift;
		Seed = seed;
		MaxShift = maxShift;
	}

	/// <summary>No shift: every matched face lands exactly on its rect.</summary>
	public static HotspotOptions Default => new HotspotOptions();

	/// <summary>Shift on, with this seed.</summary>
	public static HotspotOptions Randomised( int seed, float maxShift = HotspotTexturing.DefaultMaxShift ) =>
		new HotspotOptions( true, seed, maxShift );
}

/// <summary>
/// One face handed to <see cref="HotspotTexturing.MatchHotspots"/>: its
/// <see cref="FaceMaterial"/> plus its corner vertices and normal. This is the
/// per-face carrier until MapBrush lands (see the file header); a MapBrush face maps
/// to it with its material, its corners and its plane normal.
/// </summary>
public sealed class HotspotFace
{
	public FaceMaterial Material { get; }
	public IReadOnlyList<Vec3> Vertices { get; }
	public Vec3 Normal { get; }

	public HotspotFace( FaceMaterial material, IReadOnlyList<Vec3> vertices, Vec3 normal )
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		if ( vertices is null ) throw new ArgumentNullException( nameof( vertices ) );
		if ( vertices.Count < 3 )
			throw new ArgumentException( $"A hotspot face needs at least 3 corners; got {vertices.Count}." );
		Material = material;
		Vertices = vertices;
		Normal = normal;
	}
}

/// <summary>
/// One <see cref="HotspotTexturing.MatchHotspots"/> result: the input face, the match
/// (named outcome), and the resulting <see cref="FaceMaterial"/> — a new value when
/// matched, the SAME instance as the input when not, so callers can tell at a glance
/// which faces changed. <see cref="Changed"/> is true exactly when a hotspot matched.
/// </summary>
public sealed class HotspotResult
{
	public HotspotFace Input { get; }
	public HotspotMatch Match { get; }
	public FaceMaterial Face { get; }
	public bool Changed => Match.Found;

	public HotspotResult( HotspotFace input, HotspotMatch match, FaceMaterial face )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		if ( match is null ) throw new ArgumentNullException( nameof( match ) );
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		Input = input;
		Match = match;
		Face = face;
	}
}

/// <summary>
/// Hotspot matching and application. All methods are pure (inputs unchanged, new
/// <see cref="FaceMaterial"/> values returned) except that a non-matching face keeps
/// its original instance — see <see cref="HotspotResult"/>.
/// </summary>
public static class HotspotTexturing
{
	/// <summary>
	/// Default aspect tolerance: a face matches when the best rect's aspect error
	/// |ln(faceAspect / rectAspect)| is at most this (0.35 ≈ a 42% aspect deviation).
	/// A trim 8:1 rect therefore takes faces from about 5.6:1 to 11.4:1, and a square
	/// panel rect takes faces from about 0.7:1 to 1.4:1; anything further is NoFit.
	/// </summary>
	public const float DefaultMaxAspectError = 0.35f;

	/// <summary>Default randomise-shift bound: a tenth of the rect size per axis.</summary>
	public const float DefaultMaxShift = 0.1f;

	/// <summary>
	/// Aspect error between a face aspect and a rect aspect: |ln(face / rect)|, 0 for
	/// an exact match. Log space so a face twice as wide and a face half as wide as
	/// the rect are equally wrong. Both aspects must be positive and finite.
	/// </summary>
	public static float AspectError( float faceAspect, float rectAspect )
	{
		if ( !( faceAspect > 0f ) || float.IsInfinity( faceAspect ) )
			throw new ArgumentException( $"Cannot score a hotspot: face aspect is {faceAspect}; it must be positive and finite." );
		if ( !( rectAspect > 0f ) || float.IsInfinity( rectAspect ) )
			throw new ArgumentException( $"Cannot score a hotspot: rect aspect is {rectAspect}; it must be positive and finite." );
		return MathF.Abs( MathF.Log( faceAspect / rectAspect ) );
	}

	/// <summary>
	/// Score of one face aspect against one rect: 1 / (1 + aspect error), 1.0 for an
	/// exact aspect match. The rect is also tried turned 90 degrees; <paramref name="rotated"/>
	/// reports which orientation won (turned wins only on a strictly smaller error, so
	/// a square rect never reports rotated).
	/// </summary>
	public static float Score( float faceAspect, HotspotRect rect, out bool rotated )
	{
		if ( rect is null ) throw new ArgumentNullException( nameof( rect ) );
		var straight = AspectError( faceAspect, rect.Aspect );
		var turned = AspectError( faceAspect, 1f / rect.Aspect );
		rotated = turned < straight;
		return 1f / ( 1f + Math.Min( straight, turned ) );
	}

	/// <summary>
	/// Match a face of this size to the best-fitting rectangle. When
	/// <paramref name="materialName"/> is given, only hotspots of that material are
	/// considered (opt-in restriction); null means every offered rect is a candidate
	/// and the winner's material is the one applied. The face's CURRENT material is
	/// deliberately NOT a filter — a nodraw face can be hotspot-textured, which is
	/// the whole point: the hotspot supplies the material.
	/// </summary>
	public static HotspotMatch Match( IReadOnlyList<HotspotRect> hotspots, float faceWidth, float faceHeight,
		string materialName = null, float maxAspectError = DefaultMaxAspectError )
	{
		if ( hotspots is null ) throw new ArgumentNullException( nameof( hotspots ) );
		if ( !( faceWidth > 0f ) )
			throw new ArgumentException( $"Cannot match a hotspot: face width is {faceWidth}; it must be positive." );
		if ( !( faceHeight > 0f ) )
			throw new ArgumentException( $"Cannot match a hotspot: face height is {faceHeight}; it must be positive." );
		if ( maxAspectError < 0f )
			throw new ArgumentException( $"Cannot match a hotspot: maxAspectError is {maxAspectError}; it must be 0 or positive." );
		var faceAspect = faceWidth / faceHeight;
		var candidates = new List<HotspotRect>();
		foreach ( var rect in hotspots )
		{
			if ( rect is null ) continue;
			if ( materialName is not null &&
				!string.Equals( rect.MaterialName, materialName, StringComparison.OrdinalIgnoreCase ) ) continue;
			candidates.Add( rect );
		}
		if ( candidates.Count == 0 )
			return new HotspotMatch( HotspotMatchOutcome.NoHotspots, null, 0f, false, faceAspect,
				materialName is null
					? "No hotspots are defined: the face keeps its current material and UVs."
					: $"No hotspots are defined for material '{materialName}': the face keeps its current material and UVs." );
		HotspotRect best = null;
		var bestError = float.MaxValue;
		var bestRotated = false;
		foreach ( var rect in candidates )
		{
			var straight = AspectError( faceAspect, rect.Aspect );
			var turned = AspectError( faceAspect, 1f / rect.Aspect );
			var rotated = turned < straight;
			var error = Math.Min( straight, turned );
			var replace = false;
			if ( best is null || error < bestError - 1e-6f ) replace = true;
			else if ( error <= bestError + 1e-6f )
			{
				var area = rect.Width * rect.Height;
				var bestArea = best.Width * best.Height;
				if ( area > bestArea + 1e-6f ) replace = true;
				else if ( area >= bestArea - 1e-6f &&
					string.CompareOrdinal( rect.Name, best.Name ) < 0 ) replace = true;
			}
			if ( replace )
			{
				best = rect;
				bestError = error;
				bestRotated = rotated;
			}
		}
		var score = 1f / ( 1f + bestError );
		if ( bestError > maxAspectError )
			return new HotspotMatch( HotspotMatchOutcome.NoFit, null, 0f, false, faceAspect,
				$"No fit: face aspect {faceAspect:0.###} is too far from the closest hotspot '{best.Name}' " +
				$"(aspect error {bestError:0.###} exceeds {maxAspectError:0.###}): the face keeps its current material and UVs." );
		var effectiveAspect = bestRotated ? 1f / best.Aspect : best.Aspect;
		return new HotspotMatch( HotspotMatchOutcome.Matched, best, score, bestRotated, faceAspect,
			$"Matched hotspot '{best.Name}' ({best.MaterialName}): face aspect {faceAspect:0.###} vs " +
			$"rect aspect {effectiveAspect:0.###}{( bestRotated ? " (rotated 90 degrees)" : "" )}, score {score:0.###}." );
	}

	/// <summary>Match by face geometry: the projected bounds give the face's width and height.</summary>
	public static HotspotMatch Match( IReadOnlyList<HotspotRect> hotspots, IReadOnlyList<Vec3> vertices, Vec3 normal,
		string materialName = null, float maxAspectError = DefaultMaxAspectError )
	{
		var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( vertices, normal );
		return Match( hotspots, uMax - uMin, vMax - vMin, materialName, maxAspectError );
	}

	/// <summary>Match one <see cref="HotspotFace"/> by its geometry (its material is not a filter).</summary>
	public static HotspotMatch Match( IReadOnlyList<HotspotRect> hotspots, HotspotFace face,
		float maxAspectError = DefaultMaxAspectError )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		return Match( hotspots, face.Vertices, face.Normal, null, maxAspectError );
	}

	/// <summary>
	/// ApplyHotspot(face, rect): the spec's two-argument form. It maps the face's
	/// already-fit 0..1 UV square onto the rectangle — use it after
	/// <see cref="FaceUv.AlignFit"/>, or when the face's projected coordinates already
	/// span 0..1. The face's material becomes the rect's material; lightmap scale and
	/// smoothing group are kept. For a face whose projected coordinates do NOT span
	/// 0..1, use the bounds / vertices overload, which fits them instead.
	/// </summary>
	public static FaceMaterial ApplyHotspot( FaceMaterial face, HotspotRect rect )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		if ( rect is null ) throw new ArgumentNullException( nameof( rect ) );
		var uv = new FaceUv( new Vec2( rect.UMin, rect.VMin ), new Vec2( rect.Width, rect.Height ),
			0f, face.Uv.LightmapScale, face.Uv.SmoothingGroup );
		return new FaceMaterial( rect.MaterialName, uv );
	}

	/// <summary>
	/// Apply a hotspot to a face with these projected bounds: the bounds map exactly
	/// onto the rectangle (rotated: onto the rectangle turned 90 degrees). The face's
	/// material becomes the rect's material; lightmap scale and smoothing group are kept.
	/// A zero extent in either axis throws naming the axis — fitting a line to a rect
	/// has no answer, same as FaceUv.AlignFit.
	/// </summary>
	public static FaceMaterial ApplyHotspot( FaceMaterial face, HotspotRect rect,
		float uMin, float uMax, float vMin, float vMax, bool rotated = false )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		if ( rect is null ) throw new ArgumentNullException( nameof( rect ) );
		var uv = FitUv( uMin, uMax, vMin, vMax, rect, rotated, face.Uv.LightmapScale, face.Uv.SmoothingGroup );
		return new FaceMaterial( rect.MaterialName, uv );
	}

	/// <summary>Apply a hotspot to a face by its geometry: project the bounds, then fit them onto the rectangle.</summary>
	public static FaceMaterial ApplyHotspot( FaceMaterial face, IReadOnlyList<Vec3> vertices, Vec3 normal,
		HotspotRect rect, bool rotated = false )
	{
		var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( vertices, normal );
		return ApplyHotspot( face, rect, uMin, uMax, vMin, vMax, rotated );
	}

	/// <summary>
	/// Apply a hotspot with <see cref="HotspotOptions"/>: the same fit as
	/// <see cref="ApplyHotspot(FaceMaterial, IReadOnlyList{Vec3}, Vec3, HotspotRect, bool)"/>,
	/// plus the deterministic per-face randomise-shift for <paramref name="faceIndex"/>
	/// when the options ask for it.
	/// </summary>
	public static FaceMaterial ApplyHotspot( FaceMaterial face, IReadOnlyList<Vec3> vertices, Vec3 normal,
		HotspotRect rect, HotspotOptions options, int faceIndex, bool rotated = false )
	{
		var applied = ApplyHotspot( face, vertices, normal, rect, rotated );
		if ( options is null || !options.RandomiseShift ) return applied;
		var shift = RandomShift( faceIndex, options.Seed, options.MaxShift, rect.Width, rect.Height );
		var uv = new FaceUv( applied.Uv.Offset + shift, applied.Uv.Scale, applied.Uv.RotationDegrees,
			applied.Uv.LightmapScale, applied.Uv.SmoothingGroup );
		return new FaceMaterial( applied.MaterialName, uv );
	}

	/// <summary>
	/// Manual hotspot assignment per face: apply the hotspot with this name (case-insensitive)
	/// from <paramref name="hotspots"/>, choosing the rotated fit when the face's aspect
	/// suits the rect turned. An unknown name throws <see cref="ArgumentException"/> naming
	/// the name and listing the names that do exist — a mistyped hotspot is a named error,
	/// not a face silently left on the wrong texture.
	/// </summary>
	public static FaceMaterial ApplyNamedHotspot( FaceMaterial face, IReadOnlyList<Vec3> vertices, Vec3 normal,
		string hotspotName, IReadOnlyList<HotspotRect> hotspots, HotspotOptions options = null, int faceIndex = 0 )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		if ( string.IsNullOrWhiteSpace( hotspotName ) )
			throw new ArgumentException( "A manual hotspot assignment needs a hotspot name; a blank name is not a hotspot." );
		if ( hotspots is null ) throw new ArgumentNullException( nameof( hotspots ) );
		HotspotRect found = null;
		var names = new List<string>();
		foreach ( var rect in hotspots )
		{
			if ( rect is null ) continue;
			names.Add( rect.Name );
			if ( found is null && string.Equals( rect.Name, hotspotName.Trim(), StringComparison.OrdinalIgnoreCase ) )
				found = rect;
		}
		if ( found is null )
			throw new ArgumentException( $"No hotspot named '{hotspotName.Trim()}'." +
				( names.Count == 0 ? " No hotspots were offered." : $" Offered: {string.Join( ", ", names )}." ) );
		var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( vertices, normal );
		Score( ( uMax - uMin ) / ( vMax - vMin ), found, out var rotated );
		return ApplyHotspot( face, vertices, normal, found, options, faceIndex, rotated );
	}

	/// <summary>
	/// MatchHotspots(brushes): assign UVs by matching each face to the best-fitting
	/// rectangle. One result per input face, in input order. A matched face gets a new
	/// <see cref="FaceMaterial"/> (rect's material + fitted UVs, plus the options'
	/// randomise-shift when on); a non-matching face keeps its original
	/// <see cref="FaceMaterial"/> instance, unchanged. No MapBrush type exists yet (see
	/// the file header), so faces arrive as <see cref="HotspotFace"/> values.
	/// </summary>
	public static IReadOnlyList<HotspotResult> MatchHotspots( IReadOnlyList<HotspotFace> faces,
		IReadOnlyList<HotspotRect> hotspots, HotspotOptions options = null )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( hotspots is null ) throw new ArgumentNullException( nameof( hotspots ) );
		options = options ?? HotspotOptions.Default;
		var results = new List<HotspotResult>();
		for ( var i = 0; i < faces.Count; i++ )
		{
			var input = faces[i];
			if ( input is null ) throw new ArgumentException( $"MatchHotspots: face {i} is null." );
			var match = Match( hotspots, input );
			var result = match.Found
				? ApplyHotspot( input.Material, input.Vertices, input.Normal, match.Rect, options, i, match.Rotated )
				: input.Material;
			results.Add( new HotspotResult( input, match, result ) );
		}
		return results;
	}

	/// <summary>
	/// The deterministic randomise-shift for one face: a hash of (seed, face index)
	/// mapped to a UV offset in [-MaxShift, +MaxShift] x rect size per axis. Same seed
	/// and face index always give the same shift; different seeds (almost always) give
	/// different ones. The shift is deliberately NOT wrapped back into the rect — the
	/// texture tiles, so this is variation sampling around the hotspot, and wrapping
	/// would silently re-aim a trim at a different trim.
	/// </summary>
	public static Vec2 RandomShift( int faceIndex, int seed, float maxShift, float rectWidth, float rectHeight )
	{
		if ( faceIndex < 0 )
			throw new ArgumentException( $"Randomise-shift face index is {faceIndex}; it must be 0 or positive." );
		if ( maxShift < 0f || maxShift > 0.5f )
			throw new ArgumentException( $"Randomise-shift MaxShift is {maxShift}; it must be 0..0.5 (a fraction of the rect size)." );
		var a = Hash32( unchecked( (uint)seed ) ^ unchecked( (uint)( faceIndex * 2246822519u + 3266489917u ) ) );
		var b = Hash32( a ^ 0x9E3779B9u );
		var fu = a / 4294967295f - 0.5f;
		var fv = b / 4294967295f - 0.5f;
		return new Vec2( fu * 2f * maxShift * rectWidth, fv * 2f * maxShift * rectHeight );
	}

	static uint Hash32( uint x )
	{
		x ^= x >> 16;
		x *= 0x7feb352du;
		x ^= x >> 15;
		x *= 0x846ca68bu;
		x ^= x >> 16;
		return x;
	}

	/// <summary>
	/// The FaceUv that maps these projected bounds exactly onto the rect (rotated: the
	/// rect turned 90 degrees). The offset is solved from the transformed bounds
	/// corners, so it is exact for the rotated fit too — not guessed from one corner.
	/// </summary>
	static FaceUv FitUv( float uMin, float uMax, float vMin, float vMax, HotspotRect rect,
		bool rotated, float lightmapScale, int smoothingGroup )
	{
		var uExtent = uMax - uMin;
		var vExtent = vMax - vMin;
		if ( uExtent <= 1e-6f )
			throw new ArgumentException( $"Cannot apply hotspot: face has zero U extent (uMin={uMin}, uMax={uMax})." );
		if ( vExtent <= 1e-6f )
			throw new ArgumentException( $"Cannot apply hotspot: face has zero V extent (vMin={vMin}, vMax={vMax})." );
		FaceUv unoffset;
		if ( !rotated )
			unoffset = new FaceUv( Vec2.Zero, new Vec2( rect.Width / uExtent, rect.Height / vExtent ),
				0f, lightmapScale, smoothingGroup );
		else
			unoffset = new FaceUv( Vec2.Zero, new Vec2( rect.Width / vExtent, rect.Height / uExtent ),
				90f, lightmapScale, smoothingGroup );
		var corners = new[]
		{
			new Vec2( uMin, vMin ), new Vec2( uMax, vMin ),
			new Vec2( uMax, vMax ), new Vec2( uMin, vMax ),
		};
		var tMinU = float.MaxValue;
		var tMinV = float.MaxValue;
		foreach ( var corner in corners )
		{
			var t = unoffset.Transform( corner );
			if ( t.x < tMinU ) tMinU = t.x;
			if ( t.y < tMinV ) tMinV = t.y;
		}
		return new FaceUv( new Vec2( rect.UMin - tMinU, rect.VMin - tMinV ),
			unoffset.Scale, unoffset.RotationDegrees, lightmapScale, smoothingGroup );
	}
}
