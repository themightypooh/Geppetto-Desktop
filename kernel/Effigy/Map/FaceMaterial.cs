using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// Per-face lock flags for a brush face's UVs. A lock is a promise about a brush
/// operation: the operation must preserve the named property, or say so.
/// <para>
/// LOCK SEMANTICS (this ticket, not Valve). In this ticket "lock" means the UV is
/// glued to the FACE: the texture travels with the face. That is the OPPOSITE of
/// Valve's "texture lock" toggle (Valve lock ON = the texture is anchored to the
/// WORLD, which here is simply "carry the mapping unchanged"). Worker 04's
/// <c>MapTransform.ApplyTextureLock</c> already implements the glued semantics
/// for the world-space <c>MapUvMapping</c> model; <see cref="TextureLock.ApplyUvLock"/>
/// is the same promise for the face-projected <see cref="FaceUv"/> model, and
/// <c>MapTransform.ApplyUvLock</c> is the flag-named alias for it.
/// </para><para>
/// <see cref="ScaleLock"/>: the texture's world size survives a resize. In the
/// <see cref="FaceUv"/> model the UV is projected face inches times
/// <see cref="FaceUv.Scale"/>, so a face that doubles in U must halve its stored
/// U scale (<see cref="TextureLock.ApplyScaleLock"/>); the UV range across the
/// face — and therefore the texture's world size — is unchanged. In the
/// world-space <c>MapUvMapping</c> model the scale already IS world-relative, so
/// <c>MapTransform.ApplyScaleLock</c> keeps the stored scale and rotates the
/// axes with the face, anchoring the UV at the operation's pivot.
/// </para><para>
/// <see cref="UvLock"/>: the UV alignment survives shear/skew/vertex edits. For
/// <c>MapUvMapping</c> this is exact for every invertible transform
/// (<c>MapTransform.ApplyUvLock</c>). For <see cref="FaceUv"/> the stored form
/// (offset + per-axis scale + rotation) can only represent maps with orthogonal
/// columns, so <see cref="TextureLock.ApplyUvLock"/> fits the closest
/// representable map: exact for translations, uniform resizes and axis-aligned
/// rotations, a bounded approximation for a shear (the face-centre UV is still
/// exact). See that method's docs; do not claim exactness the form cannot give.
/// </para><para>
/// Precedence: when both flags are set, <see cref="UvLock"/> wins — a UvLock
/// application preserves the mapped UVs, which subsumes what ScaleLock promises
/// for the same edit.
/// </para>
/// </summary>
[Flags]
public enum LockFlags
{
	/// <summary>No lock: the operation carries the UV unchanged (world-anchored).</summary>
	None = 0,
	/// <summary>The texture's world size survives a resize.</summary>
	ScaleLock = 1,
	/// <summary>UV alignment survives shear/skew/vertex edits.</summary>
	UvLock = 2,
	/// <summary>Both locks.</summary>
	All = ScaleLock | UvLock,
}

/// <summary>
/// Which edge or centre of a face's projected bounds an <see cref="FaceUv.AlignJustify"/>
/// lands on the matching edge or centre of the 0..1 texture square. Tiling continues
/// outside the square, so Left and Right differ by where the tile boundary sits, not by
/// whether the texture repeats.
/// </summary>
public enum UvJustify
{
	/// <summary>Face's left edge (smallest U) lands on U = 0, V centred on 0.5.</summary>
	Left,
	/// <summary>Face's right edge (largest U) lands on U = 1, V centred on 0.5.</summary>
	Right,
	/// <summary>Face's top edge (largest V) lands on V = 1, U centred on 0.5.</summary>
	Top,
	/// <summary>Face's bottom edge (smallest V) lands on V = 0, U centred on 0.5.</summary>
	Bottom,
	/// <summary>Face's centre lands on (0.5, 0.5).</summary>
	Center,
	/// <summary>Same as <see cref="FaceUv.AlignFit"/>: the whole bounds map to 0..1.</summary>
	Fit,
}

/// <summary>
/// The per-face UV transform of a Hammer-style brush face: offset (shift), scale, rotation,
/// lightmap scale and smoothing group. This is the UV half of the face record the
/// MapBrush ticket (hammer-brush-core-plane-based-brushes) specifies — "per-face material id
/// + UV offset/scale/rotation + lightmap scale + smoothing group" — so a MapBrush face can
/// carry a <see cref="FaceMaterial"/> (material + this struct) without redefining the model.
/// MapBrush HAS landed (kernel/Effigy/Map/MapBrush.cs): <c>MapBrushFace</c> stores one
/// <see cref="FaceMaterial"/> per face, and its <c>Uv</c> is this struct — so the
/// <see cref="Locks"/> below ride on every landed brush face, and
/// <c>MapBrushFace.Uv</c> is where a caller reads them.
///
/// UV CONVENTION. <see cref="Transform"/> applies scale first, then rotation about the
/// origin, then offset: uv' = R * (uv * Scale) + Offset. <see cref="ComputeUvs"/> projects
/// a face's 3D corners onto the face plane with V running upward in face-plane space
/// (the kernel's FBX convention); any V flip for a particular exporter belongs to that
/// exporter, not here. Angles are degrees, counter-clockwise, matching Hammer's Face Edit
/// rotation field.
///
/// LOCKS. <see cref="Locks"/> is the per-face lock set (<see cref="LockFlags"/>): the
/// promise a brush operation must keep. Lock application lives in
/// <see cref="TextureLock"/> (same file): <c>ApplyScaleLock</c> /
/// <c>ApplyUvLock</c> / <c>ApplyLocksToFace</c> / the persistence check
/// <c>CheckLockPersistence</c>. The transform-path wiring is
/// <c>MapTransform.TransformFaces</c> (MapTransform.cs). The clip, CSG and vertex
/// paths are NOT wired yet — their files are owned by workers still in progress
/// (MapClip.cs, MapCsg.cs) or locked by another worker (MapVertexEdit.cs, worker
/// 45) — so this file documents their exact call sites instead; see
/// <see cref="TextureLock"/>.
/// </summary>
public readonly struct FaceUv : IEquatable<FaceUv>
{
	public readonly Vec2 Offset;
	public readonly Vec2 Scale;
	public readonly float RotationDegrees;
	public readonly float LightmapScale;
	public readonly int SmoothingGroup;
	/// <summary>The per-face lock set. Default <see cref="LockFlags.None"/>.</summary>
	public readonly LockFlags Locks;

	public FaceUv( Vec2 offset, Vec2 scale, float rotationDegrees, float lightmapScale, int smoothingGroup )
		: this( offset, scale, rotationDegrees, lightmapScale, smoothingGroup, LockFlags.None ) { }

	public FaceUv( Vec2 offset, Vec2 scale, float rotationDegrees, float lightmapScale, int smoothingGroup, LockFlags locks )
	{
		Offset = offset;
		Scale = scale;
		RotationDegrees = rotationDegrees;
		LightmapScale = lightmapScale;
		SmoothingGroup = smoothingGroup;
		Locks = locks;
	}

	/// <summary>The same UV transform with a different lock set; everything else untouched.</summary>
	public FaceUv WithLocks( LockFlags locks ) => new( Offset, Scale, RotationDegrees, LightmapScale, SmoothingGroup, locks );

	/// <summary>Hammer's default lightmap scale: 16 world units per luxel.</summary>
	public const float DefaultLightmapScale = 16f;

	/// <summary>No shift, unit scale, no rotation, lightmap 16, smoothing group 0.</summary>
	public static readonly FaceUv Default = new( Vec2.Zero, new Vec2( 1f, 1f ), 0f, DefaultLightmapScale, 0 );

	/// <summary>Apply this transform to one UV: scale, rotate about the origin, then offset.</summary>
	public Vec2 Transform( Vec2 uv )
	{
		var scaled = new Vec2( uv.x * Scale.x, uv.y * Scale.y );
		var radians = RotationDegrees * MathF.PI / 180f;
		var cos = MathF.Cos( radians );
		var sin = MathF.Sin( radians );
		var rotated = new Vec2( scaled.x * cos - scaled.y * sin, scaled.x * sin + scaled.y * cos );
		return new Vec2( rotated.x + Offset.x, rotated.y + Offset.y );
	}

	/// <summary>
	/// Named defects, never a silent fix: a zero scale component collapses the texture to a
	/// line, a non-positive lightmap scale has no meaning, and a negative smoothing group is
	/// not a group. Empty means the transform is usable.
	/// </summary>
	public IReadOnlyList<string> Validate()
	{
		var defects = new List<string>();
		if ( Scale.x == 0f ) defects.Add( "UV scale X is zero: the texture collapses to a line in U." );
		if ( Scale.y == 0f ) defects.Add( "UV scale Y is zero: the texture collapses to a line in V." );
		if ( LightmapScale <= 0f ) defects.Add( $"Lightmap scale is {LightmapScale}: it must be positive." );
		if ( SmoothingGroup < 0 ) defects.Add( $"Smoothing group is {SmoothingGroup}: it must be 0 or positive." );
		return defects;
	}

	public bool IsValid => Validate().Count == 0;

	/// <summary>
	/// Fit: the face's projected bounds map exactly to UV 0..1. Scale is 1/extent per axis,
	/// offset puts the bounds' minimum at UV 0, rotation is 0. A zero extent in either axis
	/// is a degenerate face and throws <see cref="ArgumentException"/> naming the axis —
	/// fitting a line or a point to a square has no answer.
	/// </summary>
	public static FaceUv AlignFit( float uMin, float uMax, float vMin, float vMax,
		float lightmapScale = DefaultLightmapScale, int smoothingGroup = 0 )
	{
		var uExtent = uMax - uMin;
		var vExtent = vMax - vMin;
		if ( uExtent <= 1e-6f )
			throw new ArgumentException( $"Cannot Fit: face has zero U extent (uMin={uMin}, uMax={uMax})." );
		if ( vExtent <= 1e-6f )
			throw new ArgumentException( $"Cannot Fit: face has zero V extent (vMin={vMin}, vMax={vMax})." );
		return new FaceUv(
			new Vec2( -uMin / uExtent, -vMin / vExtent ),
			new Vec2( 1f / uExtent, 1f / vExtent ),
			0f, lightmapScale, smoothingGroup );
	}

	/// <summary>
	/// Justify: keep this transform's scale, rotation, lightmap scale and smoothing group,
	/// and move only the offset so the named edge or centre of the face's projected bounds
	/// lands on the matching edge or centre of the 0..1 texture square (Left: uMin -&gt; U 0
	/// with V centred; Right: uMax -&gt; U 1; Top: vMax -&gt; V 1; Bottom: vMin -&gt; V 0;
	/// Center: middle -&gt; (0.5, 0.5)). Exact for any rotation: the offset is solved from
	/// target - R * (Scale * anchor), not guessed. <see cref="UvJustify.Fit"/> is
	/// <see cref="AlignFit"/> on these bounds.
	/// </summary>
	public FaceUv AlignJustify( UvJustify anchor, float uMin, float uMax, float vMin, float vMax )
	{
		if ( anchor == UvJustify.Fit )
			return AlignFit( uMin, uMax, vMin, vMax, LightmapScale, SmoothingGroup ).WithLocks( Locks );

		var uMid = (uMin + uMax) / 2f;
		var vMid = (vMin + vMax) / 2f;
		Vec2 anchorPoint, target;
		switch ( anchor )
		{
			case UvJustify.Left: anchorPoint = new Vec2( uMin, vMid ); target = new Vec2( 0f, 0.5f ); break;
			case UvJustify.Right: anchorPoint = new Vec2( uMax, vMid ); target = new Vec2( 1f, 0.5f ); break;
			case UvJustify.Top: anchorPoint = new Vec2( uMid, vMax ); target = new Vec2( 0.5f, 1f ); break;
			case UvJustify.Bottom: anchorPoint = new Vec2( uMid, vMin ); target = new Vec2( 0.5f, 0f ); break;
			case UvJustify.Center: anchorPoint = new Vec2( uMid, vMid ); target = new Vec2( 0.5f, 0.5f ); break;
			default: throw new ArgumentException( $"Unknown justify anchor: {anchor}." );
		}

		var scaled = new Vec2( anchorPoint.x * Scale.x, anchorPoint.y * Scale.y );
		var radians = RotationDegrees * MathF.PI / 180f;
		var cos = MathF.Cos( radians );
		var sin = MathF.Sin( radians );
		var rotated = new Vec2( scaled.x * cos - scaled.y * sin, scaled.x * sin + scaled.y * cos );
		return new FaceUv( new Vec2( target.x - rotated.x, target.y - rotated.y ),
			Scale, RotationDegrees, LightmapScale, SmoothingGroup, Locks );
	}

	/// <summary>
	/// A stable right-handed tangent basis for a face plane: uAxis and vAxis are unit,
	/// perpendicular to each other and to <paramref name="normal"/>, with
	/// uAxis x vAxis == normal. The reference axis is the world axis least aligned with the
	/// normal, so no face orientation degenerates. Throws for a zero-length normal.
	/// </summary>
	public static (Vec3 uAxis, Vec3 vAxis) PlaneAxes( Vec3 normal )
	{
		if ( normal.LengthSquared <= 1e-12f )
			throw new ArgumentException( "Cannot build face-plane axes: the face normal is zero-length." );
		var n = normal.Normal;
		var helper = MathF.Abs( n.y ) < 0.9f ? new Vec3( 0f, 1f, 0f ) : new Vec3( 1f, 0f, 0f );
		var uAxis = Vec3.Cross( helper, n ).Normal;
		var vAxis = Vec3.Cross( n, uAxis ).Normal;
		return (uAxis, vAxis);
	}

	/// <summary>
	/// The face's corner positions projected onto its plane, as (uMin, uMax, vMin, vMax) in
	/// the <see cref="PlaneAxes"/> basis, measured from the first vertex. Throws for an empty
	/// vertex list or a zero-length normal, naming the problem.
	/// </summary>
	public static (float uMin, float uMax, float vMin, float vMax) ProjectedBounds(
		IReadOnlyList<Vec3> vertices, Vec3 normal )
	{
		if ( vertices is null || vertices.Count == 0 )
			throw new ArgumentException( "Cannot project a face with no vertices." );
		var (uAxis, vAxis) = PlaneAxes( normal );
		var origin = vertices[0];
		var uMin = float.MaxValue; var uMax = float.MinValue;
		var vMin = float.MaxValue; var vMax = float.MinValue;
		foreach ( var p in vertices )
		{
			var d = p - origin;
			var u = Vec3.Dot( d, uAxis );
			var v = Vec3.Dot( d, vAxis );
			if ( u < uMin ) uMin = u;
			if ( u > uMax ) uMax = u;
			if ( v < vMin ) vMin = v;
			if ( v > vMax ) vMax = v;
		}
		return (uMin, uMax, vMin, vMax);
	}

	/// <summary>
	/// One UV per corner: project each vertex onto the face plane, then
	/// <see cref="Transform"/> it. Length always equals the vertex count.
	/// </summary>
	public Vec2[] ComputeUvs( IReadOnlyList<Vec3> vertices, Vec3 normal )
	{
		var projected = ProjectedCoords( vertices, normal );
		var uvs = new Vec2[projected.Length];
		for ( var i = 0; i < projected.Length; i++ )
			uvs[i] = Transform( projected[i] );
		return uvs;
	}

	/// <summary>
	/// The face's corner positions projected onto its plane, as (u, v) pairs in
	/// the <see cref="PlaneAxes"/> basis, measured from the first vertex — the
	/// raw projected coordinates <see cref="ComputeUvs"/> transforms. This is
	/// what the lock helpers compare across an edit: the old and new projected
	/// coordinates of the same corner indices. Throws for an empty vertex list
	/// or a zero-length normal, naming the problem.
	/// </summary>
	public static Vec2[] ProjectedCoords( IReadOnlyList<Vec3> vertices, Vec3 normal )
	{
		if ( vertices is null || vertices.Count == 0 )
			throw new ArgumentException( "Cannot project a face with no vertices." );
		var (uAxis, vAxis) = PlaneAxes( normal );
		var origin = vertices[0];
		var projected = new Vec2[vertices.Count];
		for ( var i = 0; i < vertices.Count; i++ )
		{
			var d = vertices[i] - origin;
			projected[i] = new Vec2( Vec3.Dot( d, uAxis ), Vec3.Dot( d, vAxis ) );
		}
		return projected;
	}

	/// <summary>JSON form: {"offset":[x,y],"scale":[x,y],"rotation":deg,"lightmapScale":n,"smoothingGroup":n,"locks":n}.</summary>
	public JsonObject ToJson() => new()
	{
		["offset"] = new JsonArray( (double)Offset.x, (double)Offset.y ),
		["scale"] = new JsonArray( (double)Scale.x, (double)Scale.y ),
		["rotation"] = (double)RotationDegrees,
		["lightmapScale"] = (double)LightmapScale,
		["smoothingGroup"] = SmoothingGroup,
		["locks"] = (int)Locks,
	};

	static float Num( JsonObject json, string key, float fallback )
	{
		if ( json[key] is null ) return fallback;
		try { return (float)json[key]!.GetValue<double>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"FaceUv JSON: '{key}' is not a number.", ex ); }
	}

	static Vec2 Vec( JsonObject json, string key, Vec2 fallback )
	{
		if ( json[key] is not JsonArray pair || pair.Count != 2 ) return fallback;
		try { return new Vec2( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>() ); }
		catch ( Exception ex ) { throw new ArgumentException( $"FaceUv JSON: '{key}' is not a [x, y] pair.", ex ); }
	}

	/// <summary>Parse <see cref="ToJson"/> output. Missing fields fall back to <see cref="Default"/> values; a missing "locks" falls back to <see cref="LockFlags.None"/>.</summary>
	public static FaceUv FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var smoothing = json["smoothingGroup"] is null ? 0 : json["smoothingGroup"]!.GetValue<int>();
		return new FaceUv(
			Vec( json, "offset", Vec2.Zero ),
			Vec( json, "scale", new Vec2( 1f, 1f ) ),
			Num( json, "rotation", 0f ),
			Num( json, "lightmapScale", DefaultLightmapScale ),
			smoothing,
			LocksFromJson( json ) );
	}

	/// <summary>
	/// Read the "locks" field: an integer (the <see cref="LockFlags"/> value, as
	/// written by <see cref="ToJson"/>), or a name / comma-separated names
	/// ("ScaleLock", "UvLock", "ScaleLock,UvLock"). Missing or unparsable falls
	/// back to <see cref="LockFlags.None"/> — an old document without locks
	/// loads as unlocked, never as an error.
	/// </summary>
	static LockFlags LocksFromJson( JsonObject json )
	{
		var node = json["locks"];
		if ( node is null ) return LockFlags.None;
		try { return (LockFlags)node.GetValue<int>(); }
		catch ( Exception ) { }
		try
		{
			if ( Enum.TryParse<LockFlags>( node.GetValue<string>(), ignoreCase: true, out var parsed ) )
				return parsed;
		}
		catch ( Exception ) { }
		return LockFlags.None;
	}

	public bool Equals( FaceUv o ) =>
		Offset.Equals( o.Offset ) && Scale.Equals( o.Scale ) &&
		RotationDegrees == o.RotationDegrees && LightmapScale == o.LightmapScale &&
		SmoothingGroup == o.SmoothingGroup && Locks == o.Locks;

	public override bool Equals( object obj ) => obj is FaceUv other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Offset, Scale, RotationDegrees, LightmapScale, SmoothingGroup, Locks );
	public override string ToString() =>
		$"offset {Offset} scale {Scale} rot {RotationDegrees:0.###}deg lightmap {LightmapScale:0.###} smooth {SmoothingGroup} locks {Locks}";
}

/// <summary>
/// One Hammer-style brush face's material assignment: a material name plus its
/// <see cref="FaceUv"/>. Immutable — <see cref="SetMaterial"/> and <see cref="WithUv"/>
/// return a new value, so a brush can be rebuilt from its faces without aliasing surprises.
///
/// MATERIAL NAMES. A Hammer material is a path like "brick/brickwall001" or a tool texture
/// like "tools/toolsnodraw" (see <see cref="ToolTexture"/>). This is deliberately NOT the
/// PaintMaterial chain in PaintMaterial.cs: that file is the painted-canvas -&gt; PNG -&gt;
/// .vmat chain for sculpt paint, a static helper class with no per-face reference type to
/// reuse — a brush face references its material by name, and the name is the identity the
/// map document, the compiler and the tool-texture registry all agree on. (The sculpt side
/// already has its own per-face slot model, FaceMaterialFeature/FaceMaterialEdit, which
/// paints PolyMesh faces by slot number; this type is the Hammer/map side, keyed by name.)
///
/// UNSEEN FACES ARE NODRAW. <see cref="Default"/> is tools/toolsnodraw: a brush face nobody
/// has textured yet contributes nothing to the baked mesh but stays in the brush, exactly
/// as Hammer leaves unseen faces nodraw.
/// </summary>
public sealed class FaceMaterial : IEquatable<FaceMaterial>
{
	public string MaterialName { get; }
	public FaceUv Uv { get; }

	public FaceMaterial( string materialName ) : this( materialName, FaceUv.Default ) { }

	public FaceMaterial( string materialName, FaceUv uv )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) )
			throw new ArgumentException( "A face material must name a material; a blank name is not a material." );
		MaterialName = materialName.Trim();
		Uv = uv;
	}

	/// <summary>The unseen-face default: tools/toolsnodraw with a default UV.</summary>
	public static FaceMaterial Default => new( ToolTexture.NoDrawName );

	/// <summary>Same as <see cref="Default"/> — the material for a face nobody has textured.</summary>
	public static FaceMaterial NoDraw => Default;

	/// <summary>Shorthand for new FaceMaterial(name) with a default UV.</summary>
	public static FaceMaterial ForMaterial( string materialName ) => new( materialName );

	/// <summary>
	/// SetMaterial(face, material): the face's material replaced, its UV untouched. Returns a
	/// new <see cref="FaceMaterial"/>; the input is unchanged. A null face or a blank material
	/// name throws, naming the problem — assigning "nothing" is what <see cref="NoDraw"/> is for.
	/// </summary>
	public static FaceMaterial SetMaterial( FaceMaterial face, string materialName )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		return new FaceMaterial( materialName, face.Uv );
	}

	/// <summary>Same as <see cref="SetMaterial(FaceMaterial, string)"/>, as an instance call.</summary>
	public FaceMaterial WithMaterial( string materialName ) => SetMaterial( this, materialName );

	/// <summary>The same face with a different UV transform; the material is untouched.</summary>
	public FaceMaterial WithUv( FaceUv uv ) => new( MaterialName, uv );

	/// <summary>The same face with a different lock set on its UV; material and UV transform untouched.</summary>
	public FaceMaterial WithLocks( LockFlags locks ) => new( MaterialName, Uv.WithLocks( locks ) );

	/// <summary>The tool texture this face uses, or null when it uses an ordinary material.</summary>
	public ToolTexture Tool => ToolTexture.Lookup( MaterialName );

	/// <summary>True when the material is a tool texture (nodraw, skybox, trigger, ...).</summary>
	public bool IsTool => Tool is not null;

	/// <summary>True when the material is tools/toolsnodraw (or an equivalent NoDraw tool texture).</summary>
	public bool IsNoDraw => ToolTexture.IsNoDraw( MaterialName );

	/// <summary>
	/// False for nodraw faces only: a nodraw face is excluded from the baked mesh but kept in
	/// the brush (the brush stays solid; only its rendered face is dropped). Every other face
	/// — ordinary materials and non-nodraw tool textures — contributes.
	/// </summary>
	public bool ContributesToBakedMesh => !IsNoDraw;

	/// <summary>
	/// The indices of <paramref name="faces"/> that contribute to the baked mesh: every face
	/// except the nodraw ones. The nodraw faces are NOT removed from the input — they stay in
	/// the brush, they just do not bake.
	/// </summary>
	public static IReadOnlyList<int> BakedFaceIndices( IReadOnlyList<FaceMaterial> faces )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		var baked = new List<int>();
		for ( var i = 0; i < faces.Count; i++ )
			if ( faces[i] is not null && faces[i].ContributesToBakedMesh )
				baked.Add( i );
		return baked;
	}

	/// <summary>
	/// Apply-Current-Texture to all faces of a brush: every face gets
	/// <paramref name="materialName"/>, each keeping its own UV. Returns a new list; the
	/// input faces are unchanged. Yes, this re-textures nodraw faces too — that is what
	/// applying the current texture to a brush means in Hammer; a face that must stay
	/// nodraw is re-nodrawed afterwards with <see cref="SetMaterial"/>.
	/// </summary>
	public static IReadOnlyList<FaceMaterial> ApplyCurrentTexture(
		IReadOnlyList<FaceMaterial> faces, string materialName )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( string.IsNullOrWhiteSpace( materialName ) )
			throw new ArgumentException( "Apply-Current-Texture needs a material name; a blank name is not a material." );
		return faces.Select( f => SetMaterial( f, materialName ) ).ToList();
	}

	/// <summary>JSON form: {"material":"brick/brickwall001","uv":{...}} — the map-document form.</summary>
	public JsonObject ToJson() => new()
	{
		["material"] = MaterialName,
		["uv"] = Uv.ToJson(),
	};

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>
	/// Parse <see cref="ToJson"/> output. A missing "uv" falls back to
	/// <see cref="FaceUv.Default"/>; a missing or blank "material" throws naming the problem.
	/// Unknown fields are ignored so older documents keep loading.
	/// </summary>
	public static FaceMaterial FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var name = json["material"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "FaceMaterial JSON is missing 'material'." );
		var uv = json["uv"] is JsonObject uvJson ? FaceUv.FromJson( uvJson ) : FaceUv.Default;
		return new FaceMaterial( name, uv );
	}

	public static FaceMaterial FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "FaceMaterial JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "FaceMaterial JSON text is not an object." );
		return FromJson( obj );
	}

	public bool Equals( FaceMaterial other ) =>
		other is not null && MaterialName == other.MaterialName && Uv.Equals( other.Uv );

	public override bool Equals( object obj ) => obj is FaceMaterial other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( MaterialName, Uv );
	public override string ToString() => $"{MaterialName} [{Uv}]";
}

/// <summary>
/// The document-level default lock set: the locks a newly created face gets
/// when the caller does not name any. This is the ticket's "document-level
/// default" in its smallest honest form — a process-wide default the map
/// document owns once it lands (hammer-map-document-model, worker 12); until
/// then a caller sets this once per document session and new faces take it via
/// <see cref="ForNewFace(FaceUv)"/> / <see cref="ForNewFace(FaceMaterial)"/>.
/// It never changes an existing face: only faces created after the set.
/// Default is <see cref="LockFlags.None"/>.
/// </summary>
public static class LockDefaults
{
	/// <summary>The locks new faces get. Set once per document session.</summary>
	public static LockFlags Default { get; set; } = LockFlags.None;

	/// <summary>The same UV transform with the document default locks.</summary>
	public static FaceUv ForNewFace( FaceUv uv ) => uv.WithLocks( Default );

	/// <summary>The same face with the document default locks on its UV.</summary>
	public static FaceMaterial ForNewFace( FaceMaterial face )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		return face.WithLocks( Default );
	}
}

/// <summary>
/// Lock application for the face-projected <see cref="FaceUv"/> model: the
/// helpers every brush-op path calls, and the persistence check.
///
/// SEMANTICS (ticket, not Valve — see <see cref="LockFlags"/>): a lock means
/// the UV is glued to the face. <see cref="ApplyScaleLock"/> keeps the
/// texture's world size across a resize; <see cref="ApplyUvLock"/> keeps each
/// corner's UV across a vertex edit (exactly where the <see cref="FaceUv"/>
/// form allows it, closest-representable otherwise — see that method).
/// <see cref="ApplyLocksToFace"/> is the one-call dispatcher the op paths use:
/// it reads <c>uv.Locks</c> and applies the right helper, so a path never
/// re-implements the precedence (UvLock wins over ScaleLock).
///
/// CALL SITES — the wiring this ticket does NOT do, because the owning files
/// are not mine to edit. The follow-up worker adds the two lines named here;
/// the helpers and the tests for them already exist.
/// <para>
/// 1. TRANSFORM path — WIRED in this ticket: <c>MapTransform.TransformFaces</c>
///    (MapTransform.cs) applies <c>MapTransform.ApplyLocks</c> per face, which
///    is the world-space (<c>MapUvMapping</c>) form of the same promise.
///    The <see cref="FaceUv"/> form below is for the paths whose geometry is
///    face-projected vertices, not a matrix.
/// </para><para>
/// 2. VERTEX path — MapVertexEdit.cs (worker 45 holds the claim; do NOT edit
///    until it lands). Every move funnels through the private
///    <c>Finish(MapVertexBrush brush, int[] affected, double planarTolerance)</c>
///    (line ~935). Prerequisite first: <c>MapVertexBrush</c> carries only
///    <c>string[] Materials</c> — no <see cref="FaceUv"/> per face — so the
///    integrator must give it a per-face <see cref="FaceMaterial"/> (or a
///    parallel <c>FaceUv[]</c>) before any lock can ride a vertex edit. Then,
///    in <c>Finish</c>, for each affected face index <c>i</c>:
///    <code>
///    uvs[i] = TextureLock.ApplyLocksToFace( uvs[i], oldVerts[i], oldNormal[i], newVerts[i], newNormal[i] );
///    </code>
///    where old/new vertices are that face's corner loops before/after the
///    move (same corner order, same count) and the normals are the face's
///    re-solved planes. Faces outside <c>affected</c> are untouched — their
///    geometry did not move, so their locks need no application.
/// </para><para>
/// 3. CLIP path — MapClip.cs (worker 06 has not claimed; the file on disk was
///    written against an assumed <c>MapFace</c> API that the LANDED MapBrush
///    does not have — landed is <c>MapBrushFace</c> with a
///    <see cref="FaceMaterial"/>, <c>FromPlanes(IEnumerable&lt;MapBrushFace&gt;)</c>,
///    <c>Validate()</c> returning <c>IReadOnlyList&lt;MapBrushDefect&gt;</c> —
///    so the clip file must be reconciled to the landed API first). Once it
///    is: in <c>BuildSide</c> the kept faces are the ORIGINAL face objects
///    (<c>kept.Add(faces[i])</c>), so their <see cref="FaceMaterial"/> — locks
///    included — already survive a clip by construction, and no
///    <c>Apply</c> call is needed for them. The wiring is the CAP face and
///    the check: build the cap face as
///    <code>
///    new MapBrushFace( capNormal, -keepOffset,
///        new FaceMaterial( CapMaterial( brush, opt ),
///            FaceUv.Default.WithLocks( LockDefaults.Default ) ) );
///    </code>
///    and, in <c>Clip(...)</c> after <c>BuildSide</c> returns, verify with
///    <code>
///    var defects = TextureLock.CheckLockPersistence( sourceFaces, keptFaces, keptSourceIndices );
///    </code>
///    (kept faces in original order, <c>keptSourceIndices</c> mapping each
///    kept face to its source index). The full-clip-path test in
///    tests/TextureLockTests.cs covers exactly this helper contract; the
///    end-to-end <c>MapClip.Clip</c> run is DEFERRED until the wiring lands.
/// </para><para>
/// 4. CSG path — MapCsg.cs (worker 05, in progress; do NOT edit). CSG works
///    on <c>PolyMesh</c>, whose faces carry only an int material slot — no
///    <see cref="FaceUv"/> — so the carriage must come first: the
///    MapBrush-to-PolyMesh adapter (final integrator) carries each source
///    face's <see cref="FaceMaterial"/> alongside its <c>BrushPlane</c>.
///    Then, in the private <c>Apply(BooleanOp op, PolyMesh target, PolyMesh
///    tool, IMeshBoolean provider)</c>, after the provider returns a result:
///    match each result face to its source plane by (normal, distance) via
///    <c>BrushPlanes</c>, copy that source face's <see cref="FaceMaterial"/>
///    (locks included) onto the result face, and give faces with no source
///    match (new cut faces) <c>FaceUv.Default.WithLocks(LockDefaults.Default)</c>.
///    Verify with <see cref="CheckLockPersistence(IReadOnlyList{FaceMaterial}, IReadOnlyList{FaceMaterial}, IReadOnlyList{int})"/>
///    over the matched pairs.
/// </para><para>
/// PERSISTENCE GAP (flagged, not mine to fix): <c>MapBrushFeature</c>
/// (MapBrush.cs, worker 01) serializes UvOffsets, UvScales, SmoothingGroups
/// and MaterialIds — it does NOT serialize rotation, lightmap scale, or
/// <see cref="LockFlags"/>. Locks survive <see cref="FaceUv.ToJson"/> /
/// <see cref="FaceMaterial.ToJson"/> round-trips (tested), but a brush saved
/// through <c>MapBrushFeature</c> today loses its locks (and rotation) on
/// reload. Worker 01 / the integrator must add the missing lists before
/// locks can be called persistent across save/load.
/// </para>
/// </summary>
public static class TextureLock
{
	/// <summary>
	/// ScaleLock for the face-projected model: adjust the stored scale so the
	/// texture's world size survives a resize. The UV range across the face
	/// (extent * scale per axis) is unchanged: <c>Scale' = Scale * oldExtent /
	/// newExtent</c> per axis. Offset, rotation, lightmap scale, smoothing
	/// group and locks are preserved. A zero or negative extent on either axis
	/// throws <see cref="ArgumentException"/> naming the axis — a face with no
	/// extent in an axis has no texture scale to preserve in it.
	/// </summary>
	public static FaceUv ApplyScaleLock( FaceUv uv, float oldExtentU, float oldExtentV, float newExtentU, float newExtentV )
	{
		if ( oldExtentU <= 0f ) throw new ArgumentException( $"Scale lock needs a positive old U extent; got {oldExtentU}.", nameof( oldExtentU ) );
		if ( oldExtentV <= 0f ) throw new ArgumentException( $"Scale lock needs a positive old V extent; got {oldExtentV}.", nameof( oldExtentV ) );
		if ( newExtentU <= 0f ) throw new ArgumentException( $"Scale lock needs a positive new U extent; got {newExtentU}.", nameof( newExtentU ) );
		if ( newExtentV <= 0f ) throw new ArgumentException( $"Scale lock needs a positive new V extent; got {newExtentV}.", nameof( newExtentV ) );
		return new FaceUv(
			uv.Offset,
			new Vec2( uv.Scale.x * oldExtentU / newExtentU, uv.Scale.y * oldExtentV / newExtentV ),
			uv.RotationDegrees, uv.LightmapScale, uv.SmoothingGroup, uv.Locks );
	}

	/// <summary>
	/// ScaleLock from geometry: the extents come from
	/// <see cref="FaceUv.ProjectedBounds"/> of the old and new vertex lists
	/// under their normals. The lists need not have the same count — only
	/// their projected extents are used.
	/// </summary>
	public static FaceUv ApplyScaleLock( FaceUv uv,
		IReadOnlyList<Vec3> oldVertices, Vec3 oldNormal,
		IReadOnlyList<Vec3> newVertices, Vec3 newNormal )
	{
		if ( oldVertices is null ) throw new ArgumentNullException( nameof( oldVertices ) );
		if ( newVertices is null ) throw new ArgumentNullException( nameof( newVertices ) );
		var (oldUMin, oldUMax, oldVMin, oldVMax) = FaceUv.ProjectedBounds( oldVertices, oldNormal );
		var (newUMin, newUMax, newVMin, newVMax) = FaceUv.ProjectedBounds( newVertices, newNormal );
		return ApplyScaleLock( uv,
			oldUMax - oldUMin, oldVMax - oldVMin,
			newUMax - newUMin, newVMax - newVMin );
	}

	/// <summary>
	/// UvLock for the face-projected model: re-solve the UV transform so each
	/// corner keeps the UV it had before the edit — the UV of a material point
	/// is invariant. The old and new vertex lists are the same face's corner
	/// loops before/after the edit, in the same corner order and with the same
	/// count; the normals are the face's plane normals before/after.
	/// <para>
	/// HOW. The old corner UVs are <c>uv.Transform(q_i)</c> over the old
	/// projected coordinates <c>q_i</c>; the new projected coordinates are
	/// <c>q'_i</c>. A least-squares affine map <c>A, o</c> from <c>q'</c> to
	/// the old UVs is fitted, then replaced by the closest map the
	/// <see cref="FaceUv"/> form can hold — rotation times per-axis scale,
	/// i.e. a matrix with orthogonal columns (polar fit: the rotation that
	/// minimises the off-diagonal energy, scales from the diagonal). The
	/// offset is solved so the fitted map sends the new projected centroid to
	/// the old UV centroid exactly, so the face-centre UV is always exact.
	/// </para><para>
	/// EXACTNESS. The fit is exact when the affine map already has orthogonal
	/// columns: translations, uniform resizes, and rotations by multiples of
	/// 90 degrees (and any edit when the scale is uniform). It is a bounded
	/// approximation otherwise — a shear, or an in-plane rotation with an
	/// anisotropic scale, cannot be represented by offset + per-axis scale +
	/// rotation at all, and no choice of those five numbers preserves every
	/// corner. Use <see cref="UvLockResidual"/> to measure the gap; the
	/// world-space <c>MapTransform.ApplyUvLock</c> is exact for a shear and
	/// is the right tool when the caller holds a matrix.
	/// </para><para>
	/// Throws <see cref="ArgumentException"/> for null lists, mismatched
	/// counts, fewer than 3 corners, or a face that projects to a line or a
	/// point (no plane to fit on); <see cref="InvalidOperationException"/>
	/// when the fit collapses a scale axis to ~zero.
	/// </para>
	/// </summary>
	public static FaceUv ApplyUvLock( FaceUv uv,
		IReadOnlyList<Vec3> oldVertices, Vec3 oldNormal,
		IReadOnlyList<Vec3> newVertices, Vec3 newNormal )
	{
		if ( oldVertices is null ) throw new ArgumentNullException( nameof( oldVertices ) );
		if ( newVertices is null ) throw new ArgumentNullException( nameof( newVertices ) );
		if ( oldVertices.Count != newVertices.Count )
			throw new ArgumentException( $"UvLock needs the same corner count before and after the edit; got {oldVertices.Count} before and {newVertices.Count} after." );
		if ( oldVertices.Count < 3 )
			throw new ArgumentException( $"UvLock needs at least 3 corners; got {oldVertices.Count}." );

		var oldProjected = FaceUv.ProjectedCoords( oldVertices, oldNormal );
		var newProjected = FaceUv.ProjectedCoords( newVertices, newNormal );
		var oldUvs = new Vec2[oldProjected.Length];
		for ( var i = 0; i < oldProjected.Length; i++ )
			oldUvs[i] = uv.Transform( oldProjected[i] );

		// Least-squares affine fit q' -> old UV, via centred covariances.
		var qc = Centroid( newProjected );
		var tc = Centroid( oldUvs );
		float sqq11 = 0f, sqq12 = 0f, sqq22 = 0f;
		float stq11 = 0f, stq12 = 0f, stq21 = 0f, stq22 = 0f;
		for ( var i = 0; i < newProjected.Length; i++ )
		{
			var dq = newProjected[i] - qc;
			var dt = oldUvs[i] - tc;
			sqq11 += dq.x * dq.x; sqq12 += dq.x * dq.y; sqq22 += dq.y * dq.y;
			stq11 += dt.x * dq.x; stq12 += dt.x * dq.y;
			stq21 += dt.y * dq.x; stq22 += dt.y * dq.y;
		}
		var det = sqq11 * sqq22 - sqq12 * sqq12;
		if ( MathF.Abs( det ) < 1e-12f )
			throw new ArgumentException( "UvLock cannot fit a face that projects to a line or a point: the projected corners have no area." );
		// A = Stq * Sqq^-1.
		var a11 = (stq11 * sqq22 - stq12 * sqq12) / det;
		var a12 = (stq12 * sqq11 - stq11 * sqq12) / det;
		var a21 = (stq21 * sqq22 - stq22 * sqq12) / det;
		var a22 = (stq22 * sqq11 - stq21 * sqq12) / det;

		// Closest rotation * per-axis-scale map: the rotation minimising the
		// off-diagonal energy of R(-theta) * A.
		var p = a12 * a12 + a21 * a21;
		var q = a11 * a11 + a22 * a22;
		var r = 2f * (a12 * a22 - a11 * a21);
		var theta = 0.5f * MathF.Atan2( -r, -(p - q) );
		var cos = MathF.Cos( theta );
		var sin = MathF.Sin( theta );
		var scaleX = cos * a11 + sin * a21;
		var scaleY = -sin * a12 + cos * a22;
		if ( MathF.Abs( scaleX ) < 1e-6f || MathF.Abs( scaleY ) < 1e-6f )
			throw new InvalidOperationException( $"UvLock fit collapses a texture axis to ~zero (fitted scales {scaleX:0.###}, {scaleY:0.###})." );
		var degrees = theta * 180f / MathF.PI;
		// Offset: the fitted map sends the new projected centroid to the old
		// UV centroid exactly.
		var scaledCentroid = new Vec2( qc.x * scaleX, qc.y * scaleY );
		var rotatedCentroid = new Vec2(
			scaledCentroid.x * cos - scaledCentroid.y * sin,
			scaledCentroid.x * sin + scaledCentroid.y * cos );
		var offset = new Vec2( tc.x - rotatedCentroid.x, tc.y - rotatedCentroid.y );
		return new FaceUv( offset, new Vec2( scaleX, scaleY ), degrees,
			uv.LightmapScale, uv.SmoothingGroup, uv.Locks );
	}

	static Vec2 Centroid( IReadOnlyList<Vec2> points )
	{
		var sum = Vec2.Zero;
		foreach ( var p in points ) sum += p;
		return sum / points.Count;
	}

	/// <summary>
	/// The worst corner error of a UvLock fit: the largest distance between
	/// <c>fitted.Transform(q'_i)</c> and the UV corner <c>i</c> had before the
	/// edit. Zero (within float noise) means the fit is exact; a shear or an
	/// anisotropic in-plane rotation leaves a positive residual — see
	/// <see cref="ApplyUvLock(FaceUv, IReadOnlyList{Vec3}, Vec3, IReadOnlyList{Vec3}, Vec3)"/>.
	/// Same argument rules as <see cref="ApplyUvLock(FaceUv, IReadOnlyList{Vec3}, Vec3, IReadOnlyList{Vec3}, Vec3)"/>.
	/// </summary>
	public static float UvLockResidual( FaceUv fitted,
		IReadOnlyList<Vec3> oldVertices, Vec3 oldNormal,
		IReadOnlyList<Vec3> newVertices, Vec3 newNormal, FaceUv original )
	{
		if ( oldVertices is null ) throw new ArgumentNullException( nameof( oldVertices ) );
		if ( newVertices is null ) throw new ArgumentNullException( nameof( newVertices ) );
		if ( oldVertices.Count != newVertices.Count )
			throw new ArgumentException( $"UvLockResidual needs the same corner count before and after the edit; got {oldVertices.Count} before and {newVertices.Count} after." );
		var oldProjected = FaceUv.ProjectedCoords( oldVertices, oldNormal );
		var newProjected = FaceUv.ProjectedCoords( newVertices, newNormal );
		var worst = 0f;
		for ( var i = 0; i < oldProjected.Length; i++ )
		{
			var before = original.Transform( oldProjected[i] );
			var after = fitted.Transform( newProjected[i] );
			var err = (after - before).Length;
			if ( err > worst ) worst = err;
		}
		return worst;
	}

	/// <summary>
	/// The one-call dispatcher the brush-op paths use: read
	/// <paramref name="uv"/>'s <see cref="LockFlags"/> and apply the promise.
	/// <see cref="LockFlags.UvLock"/> wins when both flags are set (it
	/// subsumes ScaleLock for the same edit); <see cref="LockFlags.ScaleLock"/>
	/// alone adjusts the scale from the projected extents; no flag returns the
	/// UV unchanged. Old/new vertices are the face's corner loops before/after
	/// the edit in the same corner order; the normals are the face's plane
	/// normals before/after.
	/// </summary>
	public static FaceUv ApplyLocksToFace( FaceUv uv,
		IReadOnlyList<Vec3> oldVertices, Vec3 oldNormal,
		IReadOnlyList<Vec3> newVertices, Vec3 newNormal )
	{
		if ( (uv.Locks & LockFlags.UvLock) != 0 )
			return ApplyUvLock( uv, oldVertices, oldNormal, newVertices, newNormal );
		if ( (uv.Locks & LockFlags.ScaleLock) != 0 )
			return ApplyScaleLock( uv, oldVertices, oldNormal, newVertices, newNormal );
		return uv;
	}

	/// <summary>
	/// Persistence check for same-count operations (transform, vertex edit):
	/// <paramref name="before"/> and <paramref name="after"/> are the face
	/// lists before and after the operation, in the same face order. Returns
	/// one defect string per problem, empty when every lock persisted:
	/// a count mismatch, a null face, or a face whose lock set changed. A
	/// defect names the face index and both lock sets — a lost lock is never
	/// a silent success.
	/// </summary>
	public static IReadOnlyList<string> CheckLockPersistence(
		IReadOnlyList<FaceMaterial> before, IReadOnlyList<FaceMaterial> after )
	{
		var defects = new List<string>();
		if ( before is null ) throw new ArgumentNullException( nameof( before ) );
		if ( after is null ) throw new ArgumentNullException( nameof( after ) );
		if ( before.Count != after.Count )
		{
			defects.Add( $"lock persistence: face count changed from {before.Count} to {after.Count}; locks cannot be matched by index." );
			return defects;
		}
		for ( var i = 0; i < before.Count; i++ )
		{
			if ( before[i] is null || after[i] is null )
			{
				defects.Add( $"lock persistence: face {i} is null on one side of the operation." );
				continue;
			}
			if ( before[i].Uv.Locks != after[i].Uv.Locks )
				defects.Add( $"lock persistence: face {i} locks changed from {before[i].Uv.Locks} to {after[i].Uv.Locks}." );
		}
		return defects;
	}

	/// <summary>
	/// Persistence check for operations that drop or add faces (clip, CSG):
	/// kept face <c>j</c> of <paramref name="after"/> came from source face
	/// <c>sourceIndexForKeptFace[j]</c> of <paramref name="before"/> — the
	/// caller (clip's <c>BuildSide</c>, CSG's plane match) owns that mapping.
	/// Returns one defect string per problem, empty when every kept face kept
	/// its source face's locks. Faces added by the operation (a clip cap, a
	/// CSG cut face) are NOT in <paramref name="after"/> here: the caller
	/// checks those separately — a new face takes
	/// <see cref="LockDefaults.Default"/> locks, it never inherits a lock
	/// from a face it did not come from. A defect names the kept index, the
	/// source index, and both lock sets.
	/// </summary>
	public static IReadOnlyList<string> CheckLockPersistence(
		IReadOnlyList<FaceMaterial> before, IReadOnlyList<FaceMaterial> after,
		IReadOnlyList<int> sourceIndexForKeptFace )
	{
		var defects = new List<string>();
		if ( before is null ) throw new ArgumentNullException( nameof( before ) );
		if ( after is null ) throw new ArgumentNullException( nameof( after ) );
		if ( sourceIndexForKeptFace is null ) throw new ArgumentNullException( nameof( sourceIndexForKeptFace ) );
		if ( after.Count != sourceIndexForKeptFace.Count )
		{
			defects.Add( $"lock persistence: {after.Count} kept faces but {sourceIndexForKeptFace.Count} source indices; the mapping must cover every kept face." );
			return defects;
		}
		for ( var j = 0; j < after.Count; j++ )
		{
			var source = sourceIndexForKeptFace[j];
			if ( source < 0 || source >= before.Count )
			{
				defects.Add( $"lock persistence: kept face {j} maps to source index {source}, outside 0..{before.Count - 1}." );
				continue;
			}
			if ( before[source] is null || after[j] is null )
			{
				defects.Add( $"lock persistence: kept face {j} (source {source}) is null on one side of the operation." );
				continue;
			}
			if ( before[source].Uv.Locks != after[j].Uv.Locks )
				defects.Add( $"lock persistence: kept face {j} (source {source}) locks changed from {before[source].Uv.Locks} to {after[j].Uv.Locks}." );
		}
		return defects;
	}
}
