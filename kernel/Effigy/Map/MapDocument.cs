using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Effigy.Map;

namespace Effigy;

/// <summary>
/// Geppetto-native map document: the working file for Hammer-style maps.
///
/// THE SOURCE OF TRUTH. VMF / .map / BSP are exports, never the working format (Report
/// §3.2 + §5.3: the working file is a versioned Geppetto-native document; the RMF trap
/// is a proprietary working format nobody else can read). The file format is
/// <see cref="MapDocumentIo"/>: hand-written, diffable text in the StudioDocument style
/// ("gmap &lt;version&gt;", one op per history entry), not JSON and not binary.
///
/// HISTORY, NOT SNAPSHOT. Like <see cref="PartStudio"/>, the document is an ordered
/// history of entries with a rollback bar: <see cref="History"/> is the whole edit log,
/// <see cref="RollbackIndex"/> is the bar, and the visible state (brushes, entities,
/// layers, visgroups, displacements, I/O, compile settings) is the fold of the entries
/// up to the bar. <see cref="Rebuild"/> refolds from scratch; a new edit made while
/// rolled back drops the entries past the bar (standard undo semantics: redo is lost
/// on a new edit). Nothing is ever silently dropped: a history entry that cannot apply
/// (duplicate id, missing target) is a fold error, and <see cref="MapDocumentIo.Read"/>
/// refuses a file whose fold has errors rather than opening a map with holes in it.
///
/// BRUSHES. A brush here is <see cref="MapBrushData"/>: faces as planes (outward unit
/// normal + distance, interior dot(N, p) &lt;= D) carrying a <see cref="FaceMaterial"/>
/// (material NAME + <see cref="FaceUv"/>). Material is a name, not a slot: that is the
/// identity the map document, the compiler and the tool-texture registry agree on
/// (FaceMaterial.cs), and what VMF writes. The plane core HAS landed:
/// <see cref="MapBrush"/> (kernel/Effigy/Map/MapBrush.cs, worker hammer-01, DONE) is
/// faces as planes + FaceMaterial, with <see cref="MapBrush.Validate"/> returning
/// named <see cref="MapBrushDefect"/> codes. MapBrushData converts to and from it —
/// <see cref="MapBrushData.ToMapBrush"/> / <see cref="MapBrushData.FromMapBrush"/> —
/// and the document's volume/centroid agree with MapBrush's by test. Two types share
/// the name MapBrushFace (Effigy.MapBrushFace, the plane-core face, and
/// Effigy.Map.MapBrushFace, Displacement.cs's corner-based face); MapBrushFaceData
/// is this file's own name and converts to both, so neither is shadowed.
/// MapBrushPrimitives' int-slot MapFace predates the landed API (it was built against
/// a stub); reconciling it is the final integrator's job, not this document's.
///
/// ENTITIES. <see cref="MapEntityData"/> is classname + keyvalues + origin/angles +
/// bound brush ids + layer/visgroup membership. Registry validation is wired, not
/// invented: <see cref="Validate(EntityRegistry)"/> runs
/// <see cref="EntityRegistry.ValidateEntity"/> per entity when a registry is supplied
/// (unknown class / unknown key / wrong key type are named defects), and the save
/// hook (<see cref="ValidationHook"/>) is where a caller installs it. Without a
/// registry the structural checks still run; an unknown class is never rejected by
/// the document itself, because the registry is data (a new JSON file adds entities
/// without code changes) and the document must not hardcode the class list.
///
/// LAYERS / VISGROUPS. <see cref="MapLayer"/> and <see cref="MapVisGroup"/> are the
/// schema foundation this file defines: id, name, colour, eye toggle, compile-visible
/// flag. The layers worker (hammer-selection-grouping-layers-visgroups-model) builds
/// Group / Selection / HideSelected / QuickHide semantics on top of them in
/// MapLayers.cs; this file only stores the section and folds it. Brushes and
/// entities reference layers/visgroups by id; a dangling reference is a validation
/// defect, never a silent clear (removing a layer does not rewrite the brushes that
/// pointed at it).
///
/// DISPLACEMENTS. <see cref="MapDisplacementData"/> is the displacement MODEL:
/// scalar offsets along the face normal + blend alphas, power 2..4, grid
/// (2^power+1)^2, row-major — the same model as Effigy.Map.Displacement, bound to a
/// brush by (BrushId, FaceIndex) instead of carrying a face snapshot. Conversions
/// to/from <see cref="Displacement"/> take the face from the caller, because the
/// displacement face (Effigy.Map.MapBrushFace) carries an int MaterialId slot while
/// the document carries a material NAME: the caller builds the face with
/// <see cref="MapBrushFaceData.ToDisplacementFace"/> and supplies the slot from its
/// own name-to-slot table. The document never invents a slot.
/// A displacement never seals the map (Displacement.SealsMap is false).
///
/// I/O. <see cref="MapIoConnection"/> is one EntityIoOutput row (the Hammer Outputs-tab
/// row, see EntityIo.cs) plus a stable connection id. The document stores I/O as
/// history ops; <see cref="Io"/> exposes the folded <see cref="EntityIoList"/>, and
/// its <see cref="EntityIoList.ToJson"/> is the verbatim "entity_io" section any
/// exporter needs — there is no second JSON block in the file, so there is exactly
/// one source of truth for I/O.
///
/// UNDO. This class implements <see cref="IMapUndoTarget"/> directly (see MapUndo.cs):
/// every mutating call appends exactly one history entry and bumps
/// <see cref="Revision"/> exactly once. The AABB-shaped operations (MoveBrush,
/// SetBrushMaterial, AddBrush(MapBrushState)) are defined at that level: MoveBrush
/// remaps the brush's AABB (translate + per-axis scale about the old centre), which
/// is exact for a box and the documented affine remap for any other convex brush.
/// <see cref="CaptureSnapshot"/> / <see cref="ApplySnapshot"/> are the undo layer's
/// AABB-level snapshots; the .gmap file stays the lossless store, and
/// <see cref="SerializeSnapshot"/> / <see cref="RestoreSnapshot"/> round-trip the
/// full .gmap text so autosave / crash recovery never loses plane detail.
///
/// VALIDATION HOOK ON SAVE. <see cref="MapDocumentIo.Save"/> runs
/// <see cref="ValidateForSave"/> (built-in structural checks + ValidationHook) and
/// refuses to write when it reports anything. A failed save never touches the file
/// and never clears the dirty flag.
///
/// DESKTOP UI: none. Backend / data-model / API / headless only, per the ticket.
/// </summary>
public sealed class MapBrushFaceData
{
	/// <summary>Outward unit normal. Interior is dot(Normal, p) &lt;= Distance.</summary>
	public Vec3 Normal;
	/// <summary>Plane distance: the plane is dot(Normal, p) = Distance.</summary>
	public float Distance;
	/// <summary>Material name + UV transform. Immutable; replaced, never mutated.</summary>
	public FaceMaterial Material;

	public MapBrushFaceData()
	{
		Material = FaceMaterial.Default;
	}

	public MapBrushFaceData( Vec3 normal, float distance, FaceMaterial material )
	{
		Normal = normal;
		Distance = distance;
		Material = material ?? throw new ArgumentNullException( nameof( material ) );
	}

	public MapBrushFaceData( Vec3 normal, float distance, string materialName )
		: this( normal, distance, FaceMaterial.ForMaterial( materialName ) )
	{
	}

	public MapBrushFaceData Clone() => new( Normal, Distance, Material );

	public bool SameAs( MapBrushFaceData other ) =>
		other is not null && Normal.Equals( other.Normal ) && Distance == other.Distance
		&& Material.Equals( other.Material );

	/// <summary>
	/// The plane-core face (Effigy.MapBrushFace, MapBrush.cs): plane + FaceMaterial.
	/// A blank material becomes nodraw (FaceMaterial.Default), mirroring MapBrushFace's
	/// own string constructor — a conversion must never throw for a brush Validate()
	/// is about to reject. A non-unit normal is normalised by MapBrush.FromPlanes
	/// (the same geometric plane, not a fix); see ValidateInto.
	/// </summary>
	public MapBrushFace ToBrushFace() => new(
		Normal, Distance,
		Material is null || string.IsNullOrWhiteSpace( Material.MaterialName )
			? FaceMaterial.Default
			: new FaceMaterial( Material.MaterialName, Material.Uv ) );

	/// <summary>
	/// The displacement face (Effigy.Map.MapBrushFace, Displacement.cs): 4 ordered
	/// corners + UV from this face's FaceUv. The caller supplies <paramref name="materialId"/>
	/// (the int slot for this face's material NAME, from the caller's name-to-slot
	/// table) — the document never invents a slot. <paramref name="corners"/> must
	/// have exactly 4 entries (FromCorners throws otherwise); they come from
	/// MapBrushData.TryComputeFaceVertices for the face's brush.
	/// </summary>
	public Effigy.Map.MapBrushFace ToDisplacementFace( Vec3[] corners, int materialId ) =>
		Effigy.Map.MapBrushFace.FromCorners( corners, materialId,
			Material?.Uv.Offset ?? FaceUv.Default.Offset,
			Material?.Uv.Scale ?? FaceUv.Default.Scale,
			Material?.Uv.RotationDegrees ?? FaceUv.Default.RotationDegrees,
			Material?.Uv.LightmapScale ?? FaceUv.Default.LightmapScale,
			Material?.Uv.SmoothingGroup ?? FaceUv.Default.SmoothingGroup );

	/// <summary>Structural defects for this face, prefixed with <paramref name="where"/>.</summary>
	public void ValidateInto( List<string> defects, string where )
	{
		if ( defects is null ) throw new ArgumentNullException( nameof( defects ) );
		if ( !float.IsFinite( Normal.x ) || !float.IsFinite( Normal.y ) || !float.IsFinite( Normal.z ) )
		{
			defects.Add( $"{where}: face normal is not finite." );
			return;
		}
		if ( Normal.LengthSquared < 1e-12f )
		{
			defects.Add( $"{where}: face normal is zero-length." );
			return;
		}
		// A non-unit normal is NOT a defect: MapBrush.FromPlanes divides it out (the
		// same geometric plane), so the document accepts what the core accepts.
		if ( !float.IsFinite( Distance ) )
			defects.Add( $"{where}: face distance is not finite." );
		if ( Material is null )
		{
			defects.Add( $"{where}: face has no material." );
			return;
		}
		if ( string.IsNullOrWhiteSpace( Material.MaterialName ) )
			defects.Add( $"{where}: face material name is blank." );
		else if ( Material.MaterialName.Any( char.IsWhiteSpace ) )
			defects.Add( $"{where}: face material '{Material.MaterialName}' contains whitespace and cannot round-trip." );
		foreach ( var d in Material.Uv.Validate() )
			defects.Add( $"{where}: {d}" );
	}
}

/// <summary>
/// One convex brush solid: faces as planes + per-face material/UV, in the MapBrush
/// spec's shape (plane normal + distance, material NAME, UV offset/scale/rotation,
/// lightmap scale, smoothing group) with the material carried as a NAME inside a
/// <see cref="FaceMaterial"/> — see the file header for why.
///
/// The plane-intersection vertex solve and the volume here are the DOCUMENT-level
/// checks (validation + bounds + vertex form). The canonical per-brush validity is
/// MapBrush.Validate() (kernel/Effigy/Map/MapBrush.cs, landed); ToMapBrush /
/// FromMapBrush convert between the two, and the tests cross-check that this
/// document's volume, centroid and validity agree with MapBrush's.
/// </summary>
public sealed class MapBrushData
{
	public string Id = "";
	public string Name = "";
	public List<MapBrushFaceData> Faces = new();
	/// <summary>Layer id, or "" for no layer.</summary>
	public string LayerId = "";
	public List<string> VisGroupIds = new();

	public MapBrushData Clone() => new()
	{
		Id = Id,
		Name = Name,
		Faces = Faces.Select( f => f.Clone() ).ToList(),
		LayerId = LayerId,
		VisGroupIds = new List<string>( VisGroupIds ),
	};

	public bool SameAs( MapBrushData other ) =>
		other is not null && Id == other.Id && Name == other.Name && LayerId == other.LayerId
		&& Faces.Count == other.Faces.Count && Faces.Zip( other.Faces, ( a, b ) => a.SameAs( b ) ).All( same => same )
		&& VisGroupIds.SequenceEqual( other.VisGroupIds );

	/// <summary>Canonical face order for a box: -X, +X, -Y, +Y, -Z, +Z.</summary>
	public static readonly int[] BoxFaceOrder = { 0, 1, 2, 3, 4, 5 };

	/// <summary>
	/// Axis-aligned box from 6 planes. Every face gets <paramref name="material"/> with
	/// the default UV. Throws naming the axis when a min exceeds its max; min == max is
	/// allowed here and produces a flat (zero-volume) brush that <see cref="Validate"/>
	/// rejects — construction must not hide a degenerate brush by refusing it.
	/// </summary>
	public static MapBrushData Box( string id, string name, Vec3 min, Vec3 max, string material ) =>
		Box( id, name, min, max, Enumerable.Repeat( material, 6 ).ToArray() );

	/// <summary>Box with one material per face, in <see cref="BoxFaceOrder"/>.</summary>
	public static MapBrushData Box( string id, string name, Vec3 min, Vec3 max, string[] materials )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) throw new ArgumentException( "A brush needs a non-blank id.", nameof( id ) );
		if ( materials is null || materials.Length != 6 )
			throw new ArgumentException( "A box needs exactly 6 face materials (-X, +X, -Y, +Y, -Z, +Z).", nameof( materials ) );
		if ( min.x > max.x ) throw new ArgumentException( $"Box min.x ({min.x}) exceeds max.x ({max.x}).", nameof( min ) );
		if ( min.y > max.y ) throw new ArgumentException( $"Box min.y ({min.y}) exceeds max.y ({max.y}).", nameof( min ) );
		if ( min.z > max.z ) throw new ArgumentException( $"Box min.z ({min.z}) exceeds max.z ({max.z}).", nameof( min ) );
		var brush = new MapBrushData { Id = id, Name = name ?? "" };
		// -X, +X, -Y, +Y, -Z, +Z. Interior is dot(N, p) <= D, so the -X face is
		// N=(-1,0,0), D=-min.x (i.e. x >= min.x) and the +X face is N=(1,0,0), D=max.x.
		var planes = new (Vec3 Normal, float Distance)[]
		{
			(new Vec3( -1f, 0f, 0f ), -min.x),
			(new Vec3( 1f, 0f, 0f ), max.x),
			(new Vec3( 0f, -1f, 0f ), -min.y),
			(new Vec3( 0f, 1f, 0f ), max.y),
			(new Vec3( 0f, 0f, -1f ), -min.z),
			(new Vec3( 0f, 0f, 1f ), max.z),
		};
		for ( var i = 0; i < 6; i++ )
			brush.Faces.Add( new MapBrushFaceData( planes[i].Normal, planes[i].Distance, materials[i] ) );
		return brush;
	}

	/// <summary>
	/// Plane-intersection vertex solve: every triple of faces gives a candidate point,
	/// kept when it lies inside every half-space. Deduplicated within 1e-3. False when
	/// fewer than 4 faces (no convex solid) or fewer than 4 vertices survive.
	/// </summary>
	public bool TryComputeVertices( out List<Vec3> vertices )
	{
		vertices = new List<Vec3>();
		if ( Faces.Count < 4 )
			return false;
		for ( var i = 0; i < Faces.Count; i++ )
			for ( var j = i + 1; j < Faces.Count; j++ )
				for ( var k = j + 1; k < Faces.Count; k++ )
				{
					var n1 = Faces[i].Normal;
					var n2 = Faces[j].Normal;
					var n3 = Faces[k].Normal;
					var det = Vec3.Dot( n1, Vec3.Cross( n2, n3 ) );
					if ( MathF.Abs( det ) < 1e-9f )
						continue;
					var p = (Faces[i].Distance * Vec3.Cross( n2, n3 )
						+ Faces[j].Distance * Vec3.Cross( n3, n1 )
						+ Faces[k].Distance * Vec3.Cross( n1, n2 )) / det;
					if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) || !float.IsFinite( p.z ) )
						continue;
					var inside = true;
					foreach ( var f in Faces )
						if ( Vec3.Dot( f.Normal, p ) > f.Distance + 1e-3f )
						{
							inside = false;
							break;
						}
					if ( !inside )
						continue;
					if ( vertices.Any( v => ( v - p ).Length < 1e-3f ) )
						continue;
					vertices.Add( p );
				}
		return vertices.Count >= 4;
	}

	/// <summary>
	/// The vertices of one face, ordered around the face centroid in the
	/// <see cref="FaceUv.PlaneAxes"/> basis (u x v == normal), so the loop is CCW seen
	/// from outside — outward-wound, matching <see cref="MapVertexBrush"/>. False when
	/// the face has fewer than 3 solved vertices.
	/// </summary>
	public bool TryComputeFaceVertices( int faceIndex, out List<Vec3> ordered )
	{
		ordered = new List<Vec3>();
		if ( faceIndex < 0 || faceIndex >= Faces.Count )
			return false;
		if ( !TryComputeVertices( out var vertices ) )
			return false;
		var face = Faces[faceIndex];
		foreach ( var v in vertices )
			if ( MathF.Abs( Vec3.Dot( face.Normal, v ) - face.Distance ) <= 1e-2f )
				ordered.Add( v );
		if ( ordered.Count < 3 )
		{
			ordered.Clear();
			return false;
		}
		var centre = Vec3.Zero;
		foreach ( var v in ordered )
			centre += v;
		centre /= ordered.Count;
		var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
		ordered.Sort( ( a, b ) =>
		{
			var aa = MathF.Atan2( Vec3.Dot( a - centre, vAxis ), Vec3.Dot( a - centre, uAxis ) );
			var bb = MathF.Atan2( Vec3.Dot( b - centre, vAxis ), Vec3.Dot( b - centre, uAxis ) );
			return aa.CompareTo( bb );
		} );
		return true;
	}

	/// <summary>
	/// Volume by tetrahedron fan from the vertex mean (an interior point for a valid
	/// convex brush): each face loop fans into tetrahedra against that point. Positive
	/// for outward normals, zero for a flat brush, negative for inward normals.
	/// False when the brush has no computable volume.
	/// </summary>
	public bool TryComputeVolume( out float volume )
	{
		volume = 0f;
		if ( !TryComputeVertices( out var vertices ) )
			return false;
		var centre = Vec3.Zero;
		foreach ( var v in vertices )
			centre += v;
		centre /= vertices.Count;
		var total = 0f;
		for ( var f = 0; f < Faces.Count; f++ )
		{
			if ( !TryComputeFaceVertices( f, out var loop ) )
				continue;
			for ( var i = 1; i + 1 < loop.Count; i++ )
				total += Vec3.Dot( loop[0] - centre,
					Vec3.Cross( loop[i] - centre, loop[i + 1] - centre ) ) / 6f;
		}
		volume = total;
		return true;
	}

	/// <summary>
	/// Volume centroid from the same tetrahedron fan as <see cref="TryComputeVolume"/>.
	/// Vec3.Zero when the brush has no positive volume.
	/// </summary>
	public Vec3 Centroid()
	{
		if ( !TryComputeVertices( out var vertices ) )
			return Vec3.Zero;
		var centre = Vec3.Zero;
		foreach ( var v in vertices )
			centre += v;
		centre /= vertices.Count;
		var totalVolume = 0f;
		var moment = Vec3.Zero;
		for ( var f = 0; f < Faces.Count; f++ )
		{
			if ( !TryComputeFaceVertices( f, out var loop ) )
				continue;
			for ( var i = 1; i + 1 < loop.Count; i++ )
			{
				var vol = Vec3.Dot( loop[0] - centre,
					Vec3.Cross( loop[i] - centre, loop[i + 1] - centre ) ) / 6f;
				totalVolume += vol;
				moment += ( centre + loop[0] + loop[i] + loop[i + 1] ) * 0.25f * vol;
			}
		}
		return totalVolume > 1e-6f ? moment / totalVolume : Vec3.Zero;
	}

	/// <summary>Axis-aligned bounds from the solved vertices. False when unsolvable.</summary>
	public bool TryBounds( out Vec3 min, out Vec3 max )
	{
		min = max = Vec3.Zero;
		if ( !TryComputeVertices( out var vertices ) )
			return false;
		min = max = vertices[0];
		foreach ( var v in vertices )
		{
			min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
			max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
		}
		return true;
	}

	/// <summary>
	/// Vertex form for the vertex-edit worker: solved vertices + outward-wound face
	/// loops + per-face material names. Throws InvalidOperationException naming the
	/// problem when the brush has no solvable vertices — a brush that cannot be
	/// solved cannot be vertex-edited, and must say so rather than hand back an empty.
	/// </summary>
	public MapVertexBrush ToVertexBrush()
	{
		if ( !TryComputeVertices( out var vertices ) )
			throw new InvalidOperationException( $"Brush '{Id}' has no solvable vertices, so it has no vertex form." );
		var faces = new List<int[]>();
		var materials = new List<string>();
		for ( var f = 0; f < Faces.Count; f++ )
		{
			if ( !TryComputeFaceVertices( f, out var loop ) )
				throw new InvalidOperationException( $"Brush '{Id}' face {f} has fewer than 3 solved vertices, so it has no vertex form." );
			var indices = new int[loop.Count];
			for ( var i = 0; i < loop.Count; i++ )
			{
				var found = -1;
				for ( var v = 0; v < vertices.Count; v++ )
					if ( ( vertices[v] - loop[i] ).Length < 1e-3f )
					{
						found = v;
						break;
					}
				if ( found < 0 )
					throw new InvalidOperationException( $"Brush '{Id}' face {f} has a vertex with no solved match." );
				indices[i] = found;
			}
			faces.Add( indices );
			materials.Add( Faces[f].Material?.MaterialName ?? "" );
		}
		return new MapVertexBrush( vertices.ToArray(), faces.ToArray(), materials.ToArray() );
	}

	/// <summary>
	/// The undo layer's AABB view: solved bounds + the first face's material. Lossy
	/// by design (a wedge's shape and per-face materials are not in a MapBrushState);
	/// the .gmap file is the lossless store, the snapshot is undo scratch.
	/// Throws InvalidOperationException when the brush has no solvable bounds.
	/// </summary>
	public MapBrushState ToBrushState()
	{
		if ( !TryBounds( out var min, out var max ) )
			throw new InvalidOperationException( $"Brush '{Id}' has no solvable bounds, so it has no AABB state." );
		return new MapBrushState
		{
			Id = Id,
			Min = min,
			Max = max,
			Material = Faces.Count > 0 ? Faces[0].Material?.MaterialName ?? "" : "",
		};
	}

	/// <summary>An AABB state back to a box brush (the exact inverse of ToBrushState for boxes).</summary>
	public static MapBrushData FromBrushState( MapBrushState state )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		return Box( state.Id, "", state.Min, state.Max,
			string.IsNullOrWhiteSpace( state.Material ) ? FaceMaterial.Default.MaterialName : state.Material );
	}

	/// <summary>
	/// The plane-core brush (MapBrush.cs): faces as planes + FaceMaterial, via
	/// MapBrush.FromPlanes. A non-unit normal is normalised there (same geometric
	/// plane); a blank material becomes nodraw (see ToBrushFace). Layer/visgroup
	/// membership is document-level and is NOT on a MapBrush — it stays behind.
	/// </summary>
	public MapBrush ToMapBrush() => MapBrush.FromPlanes( Faces.Select( f => f.ToBrushFace() ).ToList() );

	/// <summary>
	/// A plane-core brush back to document form, with the caller's id and name.
	/// Face order, planes and per-face material/UV are preserved exactly;
	/// layer/visgroup membership is document-level and comes back empty.
	/// </summary>
	public static MapBrushData FromMapBrush( MapBrush brush, string id, string name )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var data = new MapBrushData { Id = id ?? "", Name = name ?? "" };
		foreach ( var face in brush.Faces )
			data.Faces.Add( new MapBrushFaceData( face.Normal, face.Distance,
				new FaceMaterial( face.Material.MaterialName, face.Material.Uv ) ) );
		return data;
	}

	/// <summary>Named defects, MapBrush.Validate() shape: empty means usable.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		var where = $"brush '{Id}'";
		if ( string.IsNullOrWhiteSpace( Id ) )
			defects.Add( "brush has a blank id." );
		if ( Faces.Count < 4 )
		{
			defects.Add( $"{where} has {Faces.Count} faces, needs at least 4 for a convex solid." );
			return defects;
		}
		for ( var i = 0; i < Faces.Count; i++ )
			Faces[i]?.ValidateInto( defects, $"{where} face {i}" );
		for ( var i = 0; i < Faces.Count; i++ )
			for ( var j = i + 1; j < Faces.Count; j++ )
			{
				if ( Faces[i] is null || Faces[j] is null )
					continue;
				// Same-direction parallels share a plane when |d1-d2| is ~0; OPPOSITE
				// parallels share one only when |d1+d2| is ~0 (n.p=d1 and -n.p=d2 meet
				// at n.p=-d2). The old code tested |d1-d2| for both, so every
				// symmetric box (MapBrushData.Box with -min == max on an axis) was
				// flagged a duplicate - its own Box output failed its own Validate.
				// Fixed by daxter-hammer-ai-buildscript (2026-11-13): split the
				// directions; matches MapBrush.Validate, which only flags dot ~ +1.
				var sameDir = ( Faces[i].Normal - Faces[j].Normal ).Length < 1e-4f;
				var oppDir = ( Faces[i].Normal + Faces[j].Normal ).Length < 1e-4f;
				if ( sameDir && MathF.Abs( Faces[i].Distance - Faces[j].Distance ) < 1e-3f )
					defects.Add( $"{where}: faces {i} and {j} are the same plane (duplicate/coplanar-split face)." );
				else if ( oppDir && MathF.Abs( Faces[i].Distance + Faces[j].Distance ) < 1e-3f )
					defects.Add( $"{where}: faces {i} and {j} are opposite planes at the same distance (zero-thickness slab)." );
			}
		if ( !TryComputeVolume( out var volume ) )
			defects.Add( $"{where}: volume cannot be computed (unsolvable planes)." );
		else if ( volume <= 1e-6f )
			defects.Add( $"{where}: volume is {volume} (zero or negative — flat or inward-wound)." );
		return defects;
	}

	public override string ToString() => $"brush '{Id}' '{Name}' ({Faces.Count} faces)";
}

/// <summary>
/// One displacement: a subdivided quad bound to one 4-sided brush face, in the
/// Effigy.Map.Displacement model — scalar offsets along the face normal + blend
/// alphas, power 2..4, grid (2^power+1)^2, row-major — bound by (BrushId, FaceIndex)
/// instead of carrying a face snapshot. A displacement never seals the map.
/// </summary>
public sealed class MapDisplacementData
{
	public string Id = "";
	public string BrushId = "";
	public int FaceIndex;
	public int Power = Displacement.MinPower;
	public float[] Offsets = Array.Empty<float>();
	public float[] Alphas = Array.Empty<float>();

	public static int GridSizeForPower( int power ) => Displacement.GridSizeForPower( power );

	public int GridSize => ( 1 << Power ) + 1;
	public int VertexCount => GridSize * GridSize;

	/// <summary>Flat grid (all offsets and alphas zero) at the given power.</summary>
	public static MapDisplacementData Flat( string brushId, int faceIndex, int power )
	{
		var size = Displacement.GridSizeForPower( power );
		var count = size * size;
		return new MapDisplacementData
		{
			Id = brushId + "#" + faceIndex,
			BrushId = brushId,
			FaceIndex = faceIndex,
			Power = power,
			Offsets = new float[count],
			Alphas = new float[count],
		};
	}

	public MapDisplacementData Clone() => new()
	{
		Id = Id,
		BrushId = BrushId,
		FaceIndex = FaceIndex,
		Power = Power,
		Offsets = (float[])Offsets.Clone(),
		Alphas = (float[])Alphas.Clone(),
	};

	public bool SameAs( MapDisplacementData other ) =>
		other is not null && Id == other.Id && BrushId == other.BrushId && FaceIndex == other.FaceIndex
		&& Power == other.Power && Offsets.SequenceEqual( other.Offsets ) && Alphas.SequenceEqual( other.Alphas );

	/// <summary>Named defects, Displacement.Validate() shape: empty means usable.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		var where = $"displacement '{Id}'";
		if ( string.IsNullOrWhiteSpace( BrushId ) )
			defects.Add( $"{where} has no brush id." );
		if ( FaceIndex < 0 )
			defects.Add( $"{where} has a negative face index." );
		if ( Power < Displacement.MinPower || Power > Displacement.MaxPower )
		{
			defects.Add( $"{where}: power is {Power}, must be {Displacement.MinPower}..{Displacement.MaxPower}." );
			return defects;
		}
		if ( Offsets is null || Offsets.Length != VertexCount )
			defects.Add( $"{where}: offsets has {Offsets?.Length ?? 0} entries, needs {VertexCount} for power {Power}." );
		else
			for ( var i = 0; i < Offsets.Length; i++ )
				if ( !float.IsFinite( Offsets[i] ) )
				{
					defects.Add( $"{where}: offset {i} is not finite." );
					break;
				}
		if ( Alphas is null || Alphas.Length != VertexCount )
			defects.Add( $"{where}: alphas has {Alphas?.Length ?? 0} entries, needs {VertexCount} for power {Power}." );
		else
			for ( var i = 0; i < Alphas.Length; i++ )
				if ( !float.IsFinite( Alphas[i] ) || Alphas[i] < 0f || Alphas[i] > 1f )
				{
					defects.Add( $"{where}: alpha {i} is {Alphas[i]}, must be 0..1." );
					break;
				}
		return defects;
	}

	/// <summary>The undo layer's height view: offsets ARE the heights (scalar along the face normal).</summary>
	public MapDisplacementState ToDisplacementState() => new()
	{
		BrushId = BrushId,
		FaceIndex = FaceIndex,
		Power = Power,
		Heights = (float[])Offsets.Clone(),
	};

	public static MapDisplacementData FromDisplacementState( MapDisplacementState state )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		var size = Displacement.GridSizeForPower( state.Power );
		var count = size * size;
		var offsets = new float[count];
		if ( state.Heights is not null )
			Array.Copy( state.Heights, offsets, Math.Min( count, state.Heights.Length ) );
		return new MapDisplacementData
		{
			Id = state.BrushId + "#" + state.FaceIndex,
			BrushId = state.BrushId,
			FaceIndex = state.FaceIndex,
			Power = state.Power,
			Offsets = offsets,
			Alphas = new float[count],
		};
	}

	/// <summary>
	/// The Effigy.Map.Displacement for this record, bound to the caller's face
	/// (Effigy.Map.MapBrushFace — the corner-based displacement face, not the
	/// plane-core Effigy.MapBrushFace; build it with
	/// MapBrushFaceData.ToDisplacementFace, which takes the material slot from the
	/// caller's name-to-slot table). Offsets and alphas are copied in, in row-major order.
	/// </summary>
	public Displacement ToDisplacement( Effigy.Map.MapBrushFace face )
	{
		var disp = Displacement.Create( face, Power );
		Array.Copy( Offsets, disp.Offsets, Math.Min( Offsets.Length, disp.Offsets.Length ) );
		Array.Copy( Alphas, disp.Alphas, Math.Min( Alphas.Length, disp.Alphas.Length ) );
		return disp;
	}

	public static MapDisplacementData FromDisplacement( Displacement disp, string id, string brushId, int faceIndex )
	{
		if ( disp is null ) throw new ArgumentNullException( nameof( disp ) );
		return new MapDisplacementData
		{
			Id = id,
			BrushId = brushId,
			FaceIndex = faceIndex,
			Power = disp.Power,
			Offsets = (float[])disp.Offsets.Clone(),
			Alphas = (float[])disp.Alphas.Clone(),
		};
	}
}

/// <summary>
/// One map entity: classname + keyvalues + origin/angles + bound brush ids (empty
/// for a point entity) + layer/visgroup membership. Keyvalues are case-insensitive
/// by key, matching Source. "targetname" is the entity's name for I/O targeting.
/// Origin and Angles are FIELDS, never keyvalues (MapEntities agrees: "origin" is
/// never a keyvalue; "angles" is mirrored into keyvalues there and stored here as
/// the field). Setting either through SetEntityProperty or a file `key` line moves
/// the field instead of storing a competing copy; the undo state mirrors Angles
/// into Properties["angles"] and parses it back out (see ToEntityState).
/// </summary>
public sealed class MapEntityData
{
	public string Id = "";
	public string ClassName = "";
	public Dictionary<string, string> KeyValues = new( StringComparer.OrdinalIgnoreCase );
	public Vec3 Origin;
	public Vec3 Angles;
	public List<string> BrushIds = new();
	public string LayerId = "";
	public List<string> VisGroupIds = new();

	/// <summary>This entity's own name (key "targetname"), or "".</summary>
	public string TargetName => KeyValues.TryGetValue( "targetname", out var t ) ? t ?? "" : "";

	public MapEntityData Clone() => new()
	{
		Id = Id,
		ClassName = ClassName,
		KeyValues = new Dictionary<string, string>( KeyValues, StringComparer.OrdinalIgnoreCase ),
		Origin = Origin,
		Angles = Angles,
		BrushIds = new List<string>( BrushIds ),
		LayerId = LayerId,
		VisGroupIds = new List<string>( VisGroupIds ),
	};

	public bool SameAs( MapEntityData other ) =>
		other is not null && Id == other.Id && ClassName == other.ClassName
		&& Origin.Equals( other.Origin ) && Angles.Equals( other.Angles ) && LayerId == other.LayerId
		&& BrushIds.SequenceEqual( other.BrushIds ) && VisGroupIds.SequenceEqual( other.VisGroupIds )
		&& KeyValues.Count == other.KeyValues.Count
		&& KeyValues.All( kv => other.KeyValues.TryGetValue( kv.Key, out var v ) && v == kv.Value );

	/// <summary>
	/// The undo layer's entity view: id, class, origin, keyvalues. Angles, brush
	/// bindings and layer membership are document-level and not in the state — except
	/// angles ARE mirrored into Properties["angles"] (the "x y z" form MapEntities
	/// uses), because a MapEntityState has no Angles field and an undo snapshot must
	/// not lose the yaw. FromEntityState parses it back out; KeyValues never holds
	/// "angles" (or "origin") in this model — see SetEntityProperty.
	/// </summary>
	public MapEntityState ToEntityState()
	{
		var properties = new Dictionary<string, string>( KeyValues );
		properties["angles"] = StudioDocument.Vec( Angles );
		return new MapEntityState
		{
			Id = Id,
			ClassName = ClassName,
			Origin = Origin,
			Properties = properties,
		};
	}

	public static MapEntityData FromEntityState( MapEntityState state )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		var keyValues = new Dictionary<string, string>( state.Properties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase );
		var angles = Vec3.Zero;
		if ( keyValues.TryGetValue( "angles", out var anglesText ) )
		{
			keyValues.Remove( "angles" );
			if ( TryParseTriple( anglesText, out var parsed ) )
				angles = parsed;
		}
		return new MapEntityData
		{
			Id = state.Id,
			ClassName = state.ClassName,
			KeyValues = keyValues,
			Origin = state.Origin,
			Angles = angles,
		};
	}

	/// <summary>Strict "x y z" parse (invariant floats); false on anything else. StudioDocument.ParseVec3 is lenient and indexes blindly, so it is not used here.</summary>
	internal static bool TryParseTriple( string value, out Vec3 parsed )
	{
		parsed = Vec3.Zero;
		if ( string.IsNullOrWhiteSpace( value ) )
			return false;
		var p = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if ( p.Length != 3 )
			return false;
		if ( !float.TryParse( p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x ) )
			return false;
		if ( !float.TryParse( p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y ) )
			return false;
		if ( !float.TryParse( p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z ) )
			return false;
		parsed = new Vec3( x, y, z );
		return float.IsFinite( x ) && float.IsFinite( y ) && float.IsFinite( z );
	}

	/// <summary>Structural defects (no registry): empty means usable.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		var where = $"entity '{Id}'";
		if ( string.IsNullOrWhiteSpace( Id ) )
			defects.Add( "entity has a blank id." );
		if ( string.IsNullOrWhiteSpace( ClassName ) )
			defects.Add( $"{where} has a blank classname." );
		else if ( ClassName.Any( char.IsWhiteSpace ) )
			defects.Add( $"{where}: classname '{ClassName}' contains whitespace." );
		foreach ( var kv in KeyValues )
		{
			if ( string.IsNullOrWhiteSpace( kv.Key ) )
				defects.Add( $"{where} has a blank keyvalue key." );
			else if ( kv.Key.Any( char.IsWhiteSpace ) )
				defects.Add( $"{where}: keyvalue key '{kv.Key}' contains whitespace." );
			if ( kv.Value is null )
				defects.Add( $"{where}: keyvalue '{kv.Key}' has a null value." );
		}
		if ( !float.IsFinite( Origin.x ) || !float.IsFinite( Origin.y ) || !float.IsFinite( Origin.z ) )
			defects.Add( $"{where}: origin is not finite." );
		if ( !float.IsFinite( Angles.x ) || !float.IsFinite( Angles.y ) || !float.IsFinite( Angles.z ) )
			defects.Add( $"{where}: angles are not finite." );
		return defects;
	}

	public override string ToString() => $"entity '{Id}' {ClassName} at {Origin.x} {Origin.y} {Origin.z}";
}

/// <summary>
/// One layer: a named set with an eye toggle and a compile-visible flag. The
/// foundation the layers worker (MapLayers.cs) builds Group / Selection /
/// HideSelected semantics on — this file only stores the section and folds it.
/// Colour is "#RRGGBB".
/// </summary>
public sealed class MapLayer
{
	public string Id = "";
	public string Name = "";
	public string Colour = "#808080";
	public bool Visible = true;
	public bool CompileVisible = true;

	public static bool IsValidColour( string colour )
	{
		if ( string.IsNullOrEmpty( colour ) || colour.Length != 7 || colour[0] != '#' )
			return false;
		for ( var i = 1; i < 7; i++ )
		{
			var c = colour[i];
			if ( !( c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F' ) )
				return false;
		}
		return true;
	}

	public MapLayer Clone() => new() { Id = Id, Name = Name, Colour = Colour, Visible = Visible, CompileVisible = CompileVisible };

	public bool SameAs( MapLayer other ) =>
		other is not null && Id == other.Id && Name == other.Name && Colour == other.Colour
		&& Visible == other.Visible && CompileVisible == other.CompileVisible;

	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( string.IsNullOrWhiteSpace( Id ) )
			defects.Add( "layer has a blank id." );
		if ( string.IsNullOrWhiteSpace( Name ) )
			defects.Add( $"layer '{Id}' has a blank name." );
		if ( !IsValidColour( Colour ) )
			defects.Add( $"layer '{Id}': colour '{Colour}' is not #RRGGBB." );
		return defects;
	}
}

/// <summary>
/// One visgroup: a named coloured set with an eye toggle and a compile-visible flag
/// ("compile visible objects only"). Same shape as <see cref="MapLayer"/> by design:
/// Hammer treats them as the same kind of named set with the same toggles; the
/// layers worker owns the behaviour that differs (auto-visgroups, filtering).
/// </summary>
public sealed class MapVisGroup
{
	public string Id = "";
	public string Name = "";
	public string Colour = "#808080";
	public bool Visible = true;
	public bool CompileVisible = true;

	public MapVisGroup Clone() => new() { Id = Id, Name = Name, Colour = Colour, Visible = Visible, CompileVisible = CompileVisible };

	public bool SameAs( MapVisGroup other ) =>
		other is not null && Id == other.Id && Name == other.Name && Colour == other.Colour
		&& Visible == other.Visible && CompileVisible == other.CompileVisible;

	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( string.IsNullOrWhiteSpace( Id ) )
			defects.Add( "visgroup has a blank id." );
		if ( string.IsNullOrWhiteSpace( Name ) )
			defects.Add( $"visgroup '{Id}' has a blank name." );
		if ( !MapLayer.IsValidColour( Colour ) )
			defects.Add( $"visgroup '{Id}': colour '{Colour}' is not #RRGGBB." );
		return defects;
	}
}

/// <summary>Compile settings: the named knobs the compile runner reads.</summary>
public sealed class MapCompileSettings
{
	public string SkyName = "";
	public bool CompileVisibleOnly;
	public float DefaultLightmapScale = FaceUv.DefaultLightmapScale;

	public MapCompileSettings Clone() => new()
	{
		SkyName = SkyName,
		CompileVisibleOnly = CompileVisibleOnly,
		DefaultLightmapScale = DefaultLightmapScale,
	};

	public bool SameAs( MapCompileSettings other ) =>
		other is not null && SkyName == other.SkyName && CompileVisibleOnly == other.CompileVisibleOnly
		&& DefaultLightmapScale == other.DefaultLightmapScale;

	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( SkyName is not null && ( SkyName.Contains( '\n' ) || SkyName.Contains( '\r' ) ) )
			defects.Add( "compile settings: sky name contains a newline." );
		if ( DefaultLightmapScale <= 0f )
			defects.Add( $"compile settings: default lightmap scale is {DefaultLightmapScale}, must be positive." );
		return defects;
	}
}

/// <summary>
/// One entity I/O connection: a stable id plus the Hammer Outputs-tab row
/// (<see cref="EntityIoOutput"/>). The row references entities by targetname, or by
/// stable id when unnamed (EntityIo.cs) — never by list position. The undo layer's
/// <see cref="MapIoState"/> is the same row with the id attached and the delay as a
/// float; <see cref="ToIoState"/> / <see cref="FromIoState"/> convert losslessly
/// (the row's Enabled is always true through the undo layer — the undo commands
/// have no enable/disable op).
/// </summary>
public sealed class MapIoConnection
{
	public string Id = "";
	public EntityIoOutput Row;

	public MapIoConnection( string id, EntityIoOutput row )
	{
		Id = id;
		Row = row ?? throw new ArgumentNullException( nameof( row ) );
	}

	public MapIoConnection Clone() => new( Id, Row.Copy() );

	public bool SameAs( MapIoConnection other ) =>
		other is not null && Id == other.Id && Row.Equals( other.Row );

	public MapIoState ToIoState() => new()
	{
		Id = Id,
		SourceId = Row.SourceEntity,
		TargetId = Row.TargetName,
		Output = Row.Event,
		Input = Row.Input,
		Parameter = Row.Parameter,
		DelaySeconds = (float)Row.DelaySeconds,
		TimesToFire = Row.MaxFires,
	};

	public static MapIoConnection FromIoState( MapIoState state )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		return new MapIoConnection( state.Id, new EntityIoOutput(
			state.SourceId, state.Output, state.TargetId, state.Input,
			state.DelaySeconds, state.TimesToFire, state.Parameter ?? "", true ) );
	}
}

/// <summary>
/// One entry in the ordered history. Exactly one payload is set, matching
/// <see cref="Kind"/>; the fold reads only that payload. Ids are stable 8-hex guids
/// (like Feature.Id), never list positions.
/// </summary>
public sealed class MapHistoryEntry
{
	public const string AddBrush = "add-brush";
	public const string RemoveBrush = "remove-brush";
	public const string MoveBrush = "move-brush";
	public const string SetBrushMaterial = "set-brush-material";
	public const string AddEntity = "add-entity";
	public const string RemoveEntity = "remove-entity";
	public const string MoveEntity = "move-entity";
	public const string SetEntityProperty = "set-entity-property";
	public const string RemoveEntityProperty = "remove-entity-property";
	public const string AddDisplacement = "add-displacement";
	public const string SetDisplacement = "set-displacement";
	public const string RemoveDisplacement = "remove-displacement";
	public const string AddLayer = "add-layer";
	public const string RemoveLayer = "remove-layer";
	public const string AddVisGroup = "add-visgroup";
	public const string RemoveVisGroup = "remove-visgroup";
	public const string SetLayerVisible = "set-layer-visible";
	public const string SetVisGroupVisible = "set-visgroup-visible";
	public const string SetCompileSettings = "set-compile";
	public const string AddIo = "add-io";
	public const string SetIo = "set-io";
	public const string RemoveIo = "remove-io";

	public string Id = "";
	public string Kind = "";
	public string TargetId = "";
	public MapBrushData Brush;
	public MapEntityData Entity;
	public MapDisplacementData Displacement;
	public MapLayer Layer;
	public MapVisGroup VisGroup;
	public MapCompileSettings Settings;
	public MapIoConnection Io;
	/// <summary>move-brush: the new AABB.</summary>
	public Vec3 Min, Max;
	/// <summary>set-brush-material: the new material name.</summary>
	public string Material = "";
	/// <summary>move-entity: the new origin.</summary>
	public Vec3 Origin;
	/// <summary>set/remove-entity-property: the key.</summary>
	public string Key = "";
	/// <summary>set-entity-property: the new value.</summary>
	public string Value = "";
	/// <summary>set-layer-visible / set-visgroup-visible: the new eye state.</summary>
	public bool Visible;

	public MapHistoryEntry Clone() => new()
	{
		Id = Id,
		Kind = Kind,
		TargetId = TargetId,
		Brush = Brush?.Clone(),
		Entity = Entity?.Clone(),
		Displacement = Displacement?.Clone(),
		Layer = Layer?.Clone(),
		VisGroup = VisGroup?.Clone(),
		Settings = Settings?.Clone(),
		Io = Io?.Clone(),
		Min = Min,
		Max = Max,
		Material = Material,
		Origin = Origin,
		Key = Key,
		Value = Value,
		Visible = Visible,
	};
}

/// <summary>What a <see cref="MapDocument.Rebuild"/> did, returned rather than logged.</summary>
public sealed class MapRebuildReport
{
	public int EntriesFolded;
	public int Brushes;
	public int Entities;
	public int Displacements;
	public int Layers;
	public int VisGroups;
	public int IoConnections;
	public List<string> Errors = new();
	public List<string> Warnings = new();
	public bool HasErrors => Errors.Count > 0;
	public bool HasWarnings => Warnings.Count > 0;
	public override string ToString() =>
		$"{EntriesFolded} entries folded: {Brushes} brushes, {Entities} entities, {Displacements} displacements"
		+ ( HasErrors ? $", {Errors.Count} errors" : "" )
		+ ( HasWarnings ? $", {Warnings.Count} warnings" : "" );
}

/// <summary>
/// A Geppetto-native map document. See the file header for the model.
///
/// MUTATION RULES. Every public mutating call appends exactly one history entry (or,
/// for RollbackTo, moves the bar) and bumps <see cref="Revision"/> exactly once.
/// The live methods throw (ArgumentException / InvalidOperationException, naming the
/// problem) on misuse — duplicate id, unknown target — BEFORE appending, so the
/// history never records an op that cannot apply. The fold itself records errors
/// instead of throwing, because a FILE can contain ops the live API would never
/// have written; <see cref="MapDocumentIo.Read"/> refuses such a file.
/// </summary>
public sealed class MapDocument : IMapUndoTarget
{
	/// <summary>The newest map format this build writes and reads.</summary>
	public const int CurrentFormatVersion = 1;

	/// <summary>Oldest format with a named migration: 0 (no docid / rollback lines, no compile-visible flags).</summary>
	public const int OldestMigratableFormatVersion = 0;

	public string Name = "";
	public List<MapHistoryEntry> History = new();
	/// <summary>How many history entries to evaluate. int.MaxValue means all of them.</summary>
	public int RollbackIndex = int.MaxValue;
	/// <summary>What <see cref="MapDocumentIo.Read"/> had to repair or migrate, one line each. Not saved.</summary>
	public List<string> LoadNotes = new();
	/// <summary>The format version this document was loaded from (1 for a fresh document).</summary>
	public int LoadedFormatVersion = CurrentFormatVersion;
	/// <summary>Extra validation a caller installs (e.g. registry checks); runs on save after the built-in checks.</summary>
	public Func<MapDocument, IEnumerable<string>> ValidationHook { get; set; }

	public string DocumentId { get; private set; } = "";
	public string SourcePath { get; private set; } = "";
	public long Revision { get; private set; }
	long _savedRevision = -1;
	public bool IsDirty => Revision != _savedRevision;
	public MapRebuildReport LastRebuildReport { get; private set; } = new();

	List<MapBrushData> _brushes = new();
	List<MapEntityData> _entities = new();
	List<MapDisplacementData> _displacements = new();
	List<MapLayer> _layers = new();
	List<MapVisGroup> _visgroups = new();
	List<MapIoConnection> _io = new();
	MapCompileSettings _compileSettings = new();
	EntityIoList _ioList = new();

	public IReadOnlyList<MapBrushData> Brushes => _brushes;
	public IReadOnlyList<MapEntityData> Entities => _entities;
	public IReadOnlyList<MapDisplacementData> Displacements => _displacements;
	public IReadOnlyList<MapLayer> Layers => _layers;
	public IReadOnlyList<MapVisGroup> VisGroups => _visgroups;
	public IReadOnlyList<MapIoConnection> IoConnections => _io;
	/// <summary>The folded I/O list; <see cref="EntityIoList.ToJson"/> is its verbatim section JSON.</summary>
	public EntityIoList Io => _ioList;
	/// <summary>The folded compile settings. Clone before mutating; use <see cref="SetCompileSettings"/> to change them.</summary>
	public MapCompileSettings CompileSettings => _compileSettings;

	public int EffectiveCount => Math.Min( RollbackIndex, History.Count );

	public static string NewId() => Guid.NewGuid().ToString( "N" )[..8];

	public static MapDocument Create( string name )
	{
		var doc = new MapDocument
		{
			Name = name ?? "",
			DocumentId = NewId(),
		};
		doc.RebuildInternal();
		return doc;
	}

	internal void NoteOpened( string path )
	{
		SourcePath = path ?? "";
	}

	internal void NoteSaved( string path )
	{
		SourcePath = path ?? "";
		MarkSaved();
	}

	/// <summary>Used by <see cref="MapDocumentIo.Read"/> only: adopt parsed history as a fresh, clean document.</summary>
	internal void LoadFrom( List<MapHistoryEntry> history, int rollbackIndex, string documentId,
		string name, List<string> loadNotes, int loadedFormatVersion )
	{
		History = history ?? new List<MapHistoryEntry>();
		RollbackIndex = rollbackIndex;
		DocumentId = string.IsNullOrWhiteSpace( documentId ) ? NewId() : documentId;
		Name = name ?? "";
		LoadNotes = loadNotes ?? new List<string>();
		LoadedFormatVersion = loadedFormatVersion;
		Revision = 0;
		_savedRevision = 0;
		RebuildInternal();
	}

	// --- lookups -----------------------------------------------------------------------------

	public MapBrushData FindBrush( string id ) =>
		string.IsNullOrWhiteSpace( id ) ? null : _brushes.FirstOrDefault( b => b.Id == id );

	public MapEntityData FindEntity( string id ) =>
		string.IsNullOrWhiteSpace( id ) ? null : _entities.FirstOrDefault( e => e.Id == id );

	public MapDisplacementData FindDisplacement( string brushId, int faceIndex ) =>
		_displacements.FirstOrDefault( d => d.BrushId == brushId && d.FaceIndex == faceIndex );

	public MapIoConnection FindIo( string id ) =>
		string.IsNullOrWhiteSpace( id ) ? null : _io.FirstOrDefault( c => c.Id == id );

	public MapLayer FindLayer( string id ) =>
		string.IsNullOrWhiteSpace( id ) ? null : _layers.FirstOrDefault( l => l.Id == id );

	public MapVisGroup FindVisGroup( string id ) =>
		string.IsNullOrWhiteSpace( id ) ? null : _visgroups.FirstOrDefault( v => v.Id == id );

	/// <summary>The entity with this targetname (first match), or null.</summary>
	public MapEntityData FindEntityByTargetName( string targetName ) =>
		string.IsNullOrWhiteSpace( targetName ) ? null
			: _entities.FirstOrDefault( e => string.Equals( e.TargetName, targetName, StringComparison.OrdinalIgnoreCase ) );

	// --- history mutations -------------------------------------------------------------------

	static void CheckId( string id, string what )
	{
		if ( string.IsNullOrWhiteSpace( id ) )
			throw new ArgumentException( $"A {what} needs a non-blank id.", nameof( id ) );
		if ( id.Any( char.IsWhiteSpace ) )
			throw new ArgumentException( $"A {what} id '{id}' contains whitespace and cannot round-trip.", nameof( id ) );
	}

	void AppendEntry( MapHistoryEntry entry )
	{
		if ( entry is null ) throw new ArgumentNullException( nameof( entry ) );
		var effective = EffectiveCount;
		if ( effective < History.Count )
		{
			History.RemoveRange( effective, History.Count - effective );
			RollbackIndex = int.MaxValue;
		}
		History.Add( entry );
		var report = RebuildInternal();
		if ( report.HasErrors )
		{
			// Transactional: an entry the fold cannot apply does not stay in the history.
			History.RemoveAt( History.Count - 1 );
			RebuildInternal();
			throw new InvalidOperationException(
				$"History entry {entry.Kind} '{entry.TargetId}' cannot apply: {string.Join( "; ", report.Errors )}" );
		}
		Bump();
	}

	void Bump() => Revision++;

	public void MarkSaved() => _savedRevision = Revision;

	public MapRebuildReport Rebuild() => RebuildInternal();

	/// <summary>Move the rollback bar. An index at or past the end means "all entries".</summary>
	public void RollbackTo( int index )
	{
		RollbackIndex = index >= History.Count ? int.MaxValue : Math.Max( 0, index );
		RebuildInternal();
		Bump();
	}

	public void AddBrush( MapBrushData brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		CheckId( brush.Id, "brush" );
		if ( FindBrush( brush.Id ) is not null )
			throw new InvalidOperationException( $"Brush '{brush.Id}' already exists." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddBrush, TargetId = brush.Id, Brush = brush.Clone() } );
	}

	public void RemoveBrush( string id )
	{
		CheckId( id, "brush" );
		if ( FindBrush( id ) is null )
			throw new InvalidOperationException( $"No brush '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveBrush, TargetId = id } );
	}

	public void AddEntity( MapEntityData entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		CheckId( entity.Id, "entity" );
		if ( FindEntity( entity.Id ) is not null )
			throw new InvalidOperationException( $"Entity '{entity.Id}' already exists." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddEntity, TargetId = entity.Id, Entity = entity.Clone() } );
	}

	public void RemoveEntity( string id )
	{
		CheckId( id, "entity" );
		if ( FindEntity( id ) is null )
			throw new InvalidOperationException( $"No entity '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveEntity, TargetId = id } );
	}

	public void AddDisplacement( MapDisplacementData displacement )
	{
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		CheckId( displacement.Id, "displacement" );
		var defects = displacement.Validate();
		if ( defects.Count > 0 )
			throw new InvalidOperationException(
				$"Displacement '{displacement.Id}' is invalid: {string.Join( "; ", defects )}" );
		if ( FindDisplacement( displacement.BrushId, displacement.FaceIndex ) is not null )
			throw new InvalidOperationException(
				$"Displacement already exists on brush '{displacement.BrushId}' face {displacement.FaceIndex}." );
		if ( FindBrush( displacement.BrushId ) is null )
			throw new InvalidOperationException( $"No brush '{displacement.BrushId}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddDisplacement, TargetId = displacement.Id, Displacement = displacement.Clone() } );
	}

	public void RemoveDisplacement( string brushId, int faceIndex )
	{
		if ( FindDisplacement( brushId, faceIndex ) is null )
			throw new InvalidOperationException( $"No displacement on brush '{brushId}' face {faceIndex}." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveDisplacement, TargetId = brushId + "#" + faceIndex } );
	}

	public void AddLayer( MapLayer layer )
	{
		if ( layer is null ) throw new ArgumentNullException( nameof( layer ) );
		CheckId( layer.Id, "layer" );
		if ( FindLayer( layer.Id ) is not null )
			throw new InvalidOperationException( $"Layer '{layer.Id}' already exists." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddLayer, TargetId = layer.Id, Layer = layer.Clone() } );
	}

	public void RemoveLayer( string id )
	{
		CheckId( id, "layer" );
		if ( FindLayer( id ) is null )
			throw new InvalidOperationException( $"No layer '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveLayer, TargetId = id } );
	}

	public void AddVisGroup( MapVisGroup visGroup )
	{
		if ( visGroup is null ) throw new ArgumentNullException( nameof( visGroup ) );
		CheckId( visGroup.Id, "visgroup" );
		if ( FindVisGroup( visGroup.Id ) is not null )
			throw new InvalidOperationException( $"Visgroup '{visGroup.Id}' already exists." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddVisGroup, TargetId = visGroup.Id, VisGroup = visGroup.Clone() } );
	}

	public void RemoveVisGroup( string id )
	{
		CheckId( id, "visgroup" );
		if ( FindVisGroup( id ) is null )
			throw new InvalidOperationException( $"No visgroup '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveVisGroup, TargetId = id } );
	}

	public void SetLayerVisible( string id, bool visible )
	{
		CheckId( id, "layer" );
		if ( FindLayer( id ) is null )
			throw new InvalidOperationException( $"No layer '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetLayerVisible, TargetId = id, Visible = visible } );
	}

	public void SetVisGroupVisible( string id, bool visible )
	{
		CheckId( id, "visgroup" );
		if ( FindVisGroup( id ) is null )
			throw new InvalidOperationException( $"No visgroup '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetVisGroupVisible, TargetId = id, Visible = visible } );
	}

	public void SetCompileSettings( MapCompileSettings settings )
	{
		if ( settings is null ) throw new ArgumentNullException( nameof( settings ) );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetCompileSettings, TargetId = "", Settings = settings.Clone() } );
	}

	public void AddIo( string id, EntityIoOutput row )
	{
		CheckId( id, "I/O connection" );
		if ( row is null ) throw new ArgumentNullException( nameof( row ) );
		if ( FindIo( id ) is not null )
			throw new InvalidOperationException( $"I/O connection '{id}' already exists." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddIo, TargetId = id, Io = new MapIoConnection( id, row.Copy() ) } );
	}

	public void RemoveIo( string id )
	{
		CheckId( id, "I/O connection" );
		if ( FindIo( id ) is null )
			throw new InvalidOperationException( $"No I/O connection '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveIo, TargetId = id } );
	}

	// --- visibility / compile filters ---------------------------------------------------------

	bool LayerAllows( string layerId, Func<MapLayer, bool> test )
	{
		if ( string.IsNullOrEmpty( layerId ) )
			return true;
		var layer = FindLayer( layerId );
		// A dangling layer reference is a validation defect, not a reason to hide the
		// brush from a compile: the filter fails open, Validate() fails closed.
		return layer is null || test( layer );
	}

	bool VisGroupsAllow( IReadOnlyList<string> visGroupIds, Func<MapVisGroup, bool> test )
	{
		foreach ( var id in visGroupIds )
		{
			var vis = FindVisGroup( id );
			if ( vis is not null && !test( vis ) )
				return false;
		}
		return true;
	}

	/// <summary>What the 2D/3D views show: eye toggles only.</summary>
	public IReadOnlyList<MapBrushData> VisibleBrushes() =>
		_brushes.Where( b => LayerAllows( b.LayerId, l => l.Visible ) && VisGroupsAllow( b.VisGroupIds, v => v.Visible ) ).ToList();

	/// <summary>What the 2D/3D views show: eye toggles only.</summary>
	public IReadOnlyList<MapEntityData> VisibleEntities() =>
		_entities.Where( e => LayerAllows( e.LayerId, l => l.Visible ) && VisGroupsAllow( e.VisGroupIds, v => v.Visible ) ).ToList();

	/// <summary>
	/// What a compile includes. When CompileVisibleOnly is off, everything compiles.
	/// When on, only brushes on compile-visible layers and visgroups compile.
	/// </summary>
	public IReadOnlyList<MapBrushData> CompileVisibleBrushes() =>
		_compileSettings.CompileVisibleOnly
			? _brushes.Where( b => LayerAllows( b.LayerId, l => l.CompileVisible ) && VisGroupsAllow( b.VisGroupIds, v => v.CompileVisible ) ).ToList()
			: _brushes.ToList();

	/// <summary>What a compile includes (see <see cref="CompileVisibleBrushes"/>).</summary>
	public IReadOnlyList<MapEntityData> CompileVisibleEntities() =>
		_compileSettings.CompileVisibleOnly
			? _entities.Where( e => LayerAllows( e.LayerId, l => l.CompileVisible ) && VisGroupsAllow( e.VisGroupIds, v => v.CompileVisible ) ).ToList()
			: _entities.ToList();

	// --- validation ---------------------------------------------------------------------------

	/// <summary>
	/// Named defects for the whole document. Structural checks always run; when
	/// <paramref name="registry"/> is supplied, entity classes/keyvalues and I/O
	/// outputs/inputs are also checked against it (unknown class/key, wrong key type,
	/// undeclared output/input). Empty means the document is usable.
	/// </summary>
	public List<string> Validate( EntityRegistry registry = null )
	{
		var defects = new List<string>();
		CheckUniqueIds( _brushes.Select( b => b.Id ), "brush", defects );
		CheckUniqueIds( _entities.Select( e => e.Id ), "entity", defects );
		CheckUniqueIds( _layers.Select( l => l.Id ), "layer", defects );
		CheckUniqueIds( _visgroups.Select( v => v.Id ), "visgroup", defects );
		CheckUniqueIds( _io.Select( c => c.Id ), "I/O connection", defects );
		foreach ( var brush in _brushes )
		{
			if ( brush is null )
			{
				defects.Add( "brush list contains a null brush." );
				continue;
			}
			defects.AddRange( brush.Validate() );
			if ( !string.IsNullOrEmpty( brush.LayerId ) && FindLayer( brush.LayerId ) is null )
				defects.Add( $"brush '{brush.Id}' references missing layer '{brush.LayerId}'." );
			foreach ( var vis in brush.VisGroupIds )
				if ( FindVisGroup( vis ) is null )
					defects.Add( $"brush '{brush.Id}' references missing visgroup '{vis}'." );
		}
		foreach ( var disp in _displacements )
		{
			if ( disp is null )
			{
				defects.Add( "displacement list contains a null displacement." );
				continue;
			}
			defects.AddRange( disp.Validate() );
			var brush = FindBrush( disp.BrushId );
			if ( brush is null )
			{
				defects.Add( $"displacement '{disp.Id}' references missing brush '{disp.BrushId}'." );
				continue;
			}
			if ( disp.FaceIndex < 0 || disp.FaceIndex >= brush.Faces.Count )
				defects.Add( $"displacement '{disp.Id}': face index {disp.FaceIndex} is out of range for brush '{disp.BrushId}' ({brush.Faces.Count} faces)." );
			else if ( brush.TryComputeFaceVertices( disp.FaceIndex, out var loop ) && loop.Count != 4 )
				defects.Add( $"displacement '{disp.Id}': face {disp.FaceIndex} of brush '{disp.BrushId}' has {loop.Count} corners, needs exactly 4." );
		}
		foreach ( var entity in _entities )
		{
			if ( entity is null )
			{
				defects.Add( "entity list contains a null entity." );
				continue;
			}
			defects.AddRange( entity.Validate() );
			foreach ( var brushId in entity.BrushIds )
				if ( FindBrush( brushId ) is null )
					defects.Add( $"entity '{entity.Id}' references missing brush '{brushId}'." );
			if ( !string.IsNullOrEmpty( entity.LayerId ) && FindLayer( entity.LayerId ) is null )
				defects.Add( $"entity '{entity.Id}' references missing layer '{entity.LayerId}'." );
			foreach ( var vis in entity.VisGroupIds )
				if ( FindVisGroup( vis ) is null )
					defects.Add( $"entity '{entity.Id}' references missing visgroup '{vis}'." );
			if ( registry is not null )
				foreach ( var error in registry.ValidateEntity( entity.ClassName, entity.KeyValues ) )
					defects.Add( $"entity '{entity.Id}' ({entity.ClassName}): {error.Code} {error.Message}" );
		}
		foreach ( var layer in _layers )
			if ( layer is null ) defects.Add( "layer list contains a null layer." );
			else defects.AddRange( layer.Validate() );
		foreach ( var vis in _visgroups )
			if ( vis is null ) defects.Add( "visgroup list contains a null visgroup." );
			else defects.AddRange( vis.Validate() );
		if ( _compileSettings is null )
			defects.Add( "compile settings are missing." );
		else
			defects.AddRange( _compileSettings.Validate() );
		foreach ( var conn in _io )
		{
			if ( conn?.Row is null )
			{
				defects.Add( "I/O list contains a null connection." );
				continue;
			}
			var row = conn.Row;
			var rowError = row.Validate();
			if ( rowError is not null )
				defects.Add( $"I/O '{conn.Id}': {rowError}" );
			var source = FindEntity( row.SourceEntity ) ?? FindEntityByTargetName( row.SourceEntity );
			if ( source is null )
				defects.Add( $"I/O '{conn.Id}': source '{row.SourceEntity}' matches no entity." );
			else if ( registry is not null )
			{
				var cls = registry.Find( source.ClassName );
				if ( cls is not null && cls.FindOutput( row.Event ) is null )
					defects.Add( $"I/O '{conn.Id}': entity '{source.Id}' ({source.ClassName}) declares no output '{row.Event}'." );
			}
			if ( !row.TargetName.StartsWith( "!", StringComparison.Ordinal ) )
			{
				var target = FindEntity( row.TargetName ) ?? FindEntityByTargetName( row.TargetName );
				if ( target is null )
					defects.Add( $"I/O '{conn.Id}': target '{row.TargetName}' matches no entity." );
				else if ( registry is not null )
				{
					var cls = registry.Find( target.ClassName );
					if ( cls is not null && cls.FindInput( row.Input ) is null )
						defects.Add( $"I/O '{conn.Id}': entity '{target.Id}' ({target.ClassName}) declares no input '{row.Input}'." );
				}
			}
		}
		return defects;
	}

	static void CheckUniqueIds( IEnumerable<string> ids, string what, List<string> defects )
	{
		var seen = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var id in ids )
			if ( !seen.Add( id ?? "" ) )
				defects.Add( $"duplicate {what} id '{id}'." );
	}

	/// <summary>What <see cref="MapDocumentIo.Save"/> runs: built-in checks plus the hook.</summary>
	public List<string> ValidateForSave( EntityRegistry registry = null )
	{
		var defects = Validate( registry );
		if ( ValidationHook is not null )
			foreach ( var defect in ValidationHook( this ) ?? Enumerable.Empty<string>() )
				if ( !string.IsNullOrWhiteSpace( defect ) )
					defects.Add( defect );
		return defects;
	}

	// --- issue-engine projection --------------------------------------------------------------

	/// <summary>
	/// The IssueEngine's view of this map (see IssueEngine.cs: the integrator's thin
	/// adapter is this projection). Brush and entity ids become sequential ints in
	/// document order; unresolvable brush references are skipped — Validate() is
	/// where they are reported, not here.
	/// </summary>
	public IssueDocument ToIssueDocument()
	{
		var doc = new IssueDocument { Name = Name };
		var brushIndex = new Dictionary<string, int>( StringComparer.Ordinal );
		for ( var i = 0; i < _brushes.Count; i++ )
		{
			brushIndex[_brushes[i].Id] = i;
			var issueBrush = new IssueBrush( i, Enumerable.Empty<IssueFace>() );
			for ( var f = 0; f < _brushes[i].Faces.Count; f++ )
			{
				var face = _brushes[i].Faces[f];
				// Face vertices are filled when solvable; an unsolvable face keeps an
				// empty vertex list and the engine's volume treats it as no face.
				var vertices = new List<Vec3>();
				if ( _brushes[i].TryComputeFaceVertices( f, out var loop ) )
					vertices.AddRange( loop );
				issueBrush.Faces.Add( new IssueFace( face.Normal, face.Distance,
					face.Material?.MaterialName ?? "", vertices ) );
			}
			doc.Brushes.Add( issueBrush );
		}
		for ( var i = 0; i < _entities.Count; i++ )
		{
			var entity = _entities[i];
			var issueEntity = new IssueEntity( i, entity.ClassName, entity.Origin );
			foreach ( var kv in entity.KeyValues )
				issueEntity.KeyValues[kv.Key] = kv.Value;
			foreach ( var brushId in entity.BrushIds )
				if ( brushIndex.TryGetValue( brushId, out var index ) )
					issueEntity.BrushIds.Add( index );
			doc.Entities.Add( issueEntity );
		}
		return doc;
	}

	// --- fold ---------------------------------------------------------------------------------

	sealed class FoldState
	{
		public List<MapBrushData> Brushes = new();
		public List<MapEntityData> Entities = new();
		public List<MapDisplacementData> Displacements = new();
		public List<MapLayer> Layers = new();
		public List<MapVisGroup> VisGroups = new();
		public List<MapIoConnection> Io = new();
		public MapCompileSettings Settings = new();
	}

	MapRebuildReport RebuildInternal()
	{
		var state = new FoldState();
		var report = new MapRebuildReport();
		var count = EffectiveCount;
		for ( var i = 0; i < count; i++ )
		{
			try
			{
				ApplyEntry( History[i], state, report );
			}
			catch ( Exception ex ) when ( ex is ArgumentException or InvalidOperationException )
			{
				report.Errors.Add( $"entry {i} ({History[i]?.Kind} '{History[i]?.TargetId}'): {ex.Message}" );
			}
		}
		_brushes = state.Brushes;
		_entities = state.Entities;
		_displacements = state.Displacements;
		_layers = state.Layers;
		_visgroups = state.VisGroups;
		_io = state.Io;
		_compileSettings = state.Settings;
		_ioList = new EntityIoList();
		foreach ( var conn in state.Io )
			_ioList.Add( conn.Row );
		report.EntriesFolded = count;
		report.Brushes = state.Brushes.Count;
		report.Entities = state.Entities.Count;
		report.Displacements = state.Displacements.Count;
		report.Layers = state.Layers.Count;
		report.VisGroups = state.VisGroups.Count;
		report.IoConnections = state.Io.Count;
		LastRebuildReport = report;
		return report;
	}

	static MapBrushData FindIn( List<MapBrushData> brushes, string id ) =>
		brushes.FirstOrDefault( b => b.Id == id );

	static MapEntityData FindIn( List<MapEntityData> entities, string id ) =>
		entities.FirstOrDefault( e => e.Id == id );

	static void ApplyEntry( MapHistoryEntry entry, FoldState state, MapRebuildReport report )
	{
		if ( entry is null )
		{
			report.Errors.Add( "history contains a null entry." );
			return;
		}
		switch ( entry.Kind )
		{
			case MapHistoryEntry.AddBrush:
				if ( entry.Brush is null ) { report.Errors.Add( $"add-brush '{entry.TargetId}' has no brush." ); return; }
				if ( FindIn( state.Brushes, entry.TargetId ) is not null ) { report.Errors.Add( $"duplicate brush id '{entry.TargetId}'." ); return; }
				state.Brushes.Add( entry.Brush.Clone() );
				return;
			case MapHistoryEntry.RemoveBrush:
			{
				var brush = FindIn( state.Brushes, entry.TargetId );
				if ( brush is null ) { report.Errors.Add( $"remove-brush: no brush '{entry.TargetId}'." ); return; }
				state.Brushes.Remove( brush );
				// Cascade: a displacement cannot exist without its brush face.
				state.Displacements.RemoveAll( d => d.BrushId == entry.TargetId );
				// Entities keep existing; the removed id drops from their brush lists
				// (a dangling reference would make a legal edit produce an invalid doc).
				foreach ( var entity in state.Entities )
					entity.BrushIds.RemoveAll( id => id == entry.TargetId );
				return;
			}
			case MapHistoryEntry.MoveBrush:
			{
				var brush = FindIn( state.Brushes, entry.TargetId );
				if ( brush is null ) { report.Errors.Add( $"move-brush: no brush '{entry.TargetId}'." ); return; }
				var index = state.Brushes.IndexOf( brush );
				state.Brushes[index] = RemapBrush( brush, entry.Min, entry.Max );
				return;
			}
			case MapHistoryEntry.SetBrushMaterial:
			{
				var brush = FindIn( state.Brushes, entry.TargetId );
				if ( brush is null ) { report.Errors.Add( $"set-brush-material: no brush '{entry.TargetId}'." ); return; }
				if ( string.IsNullOrWhiteSpace( entry.Material ) ) { report.Errors.Add( $"set-brush-material '{entry.TargetId}': blank material." ); return; }
				var index = state.Brushes.IndexOf( brush );
				var copy = brush.Clone();
				for ( var i = 0; i < copy.Faces.Count; i++ )
					copy.Faces[i] = new MapBrushFaceData( copy.Faces[i].Normal, copy.Faces[i].Distance,
						copy.Faces[i].Material.WithMaterial( entry.Material ) );
				state.Brushes[index] = copy;
				return;
			}
			case MapHistoryEntry.AddEntity:
				if ( entry.Entity is null ) { report.Errors.Add( $"add-entity '{entry.TargetId}' has no entity." ); return; }
				if ( FindIn( state.Entities, entry.TargetId ) is not null ) { report.Errors.Add( $"duplicate entity id '{entry.TargetId}'." ); return; }
				state.Entities.Add( entry.Entity.Clone() );
				return;
			case MapHistoryEntry.RemoveEntity:
			{
				var entity = FindIn( state.Entities, entry.TargetId );
				if ( entity is null ) { report.Errors.Add( $"remove-entity: no entity '{entry.TargetId}'." ); return; }
				var targetName = entity.TargetName;
				state.Entities.Remove( entity );
				// Cascade, mirroring the undo target: I/O rows naming this entity go with it.
				state.Io.RemoveAll( c => c.Row.SourceEntity == entry.TargetId || c.Row.TargetName == entry.TargetId
					|| ( !string.IsNullOrEmpty( targetName ) && ( c.Row.SourceEntity == targetName || c.Row.TargetName == targetName ) ) );
				return;
			}
			case MapHistoryEntry.MoveEntity:
			{
				var entity = FindIn( state.Entities, entry.TargetId );
				if ( entity is null ) { report.Errors.Add( $"move-entity: no entity '{entry.TargetId}'." ); return; }
				entity.Origin = entry.Origin;
				return;
			}
			case MapHistoryEntry.SetEntityProperty:
			{
				var entity = FindIn( state.Entities, entry.TargetId );
				if ( entity is null ) { report.Errors.Add( $"set-entity-property: no entity '{entry.TargetId}'." ); return; }
				if ( string.IsNullOrWhiteSpace( entry.Key ) ) { report.Errors.Add( $"set-entity-property '{entry.TargetId}': blank key." ); return; }
				if ( entry.Value is null ) { report.Errors.Add( $"set-entity-property '{entry.TargetId}': null value." ); return; }
				// "origin" and "angles" are entity FIELDS, never keyvalues: setting
				// them here moves the field (a file op does the same), so KeyValues
				// never holds a competing copy.
				if ( string.Equals( entry.Key, "origin", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( !MapEntityData.TryParseTriple( entry.Value, out var origin ) ) { report.Errors.Add( $"set-entity-property '{entry.TargetId}': 'origin' must be three numbers, got '{entry.Value}'." ); return; }
					entity.Origin = origin;
					return;
				}
				if ( string.Equals( entry.Key, "angles", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( !MapEntityData.TryParseTriple( entry.Value, out var angles ) ) { report.Errors.Add( $"set-entity-property '{entry.TargetId}': 'angles' must be three numbers, got '{entry.Value}'." ); return; }
					entity.Angles = angles;
					return;
				}
				entity.KeyValues[entry.Key] = entry.Value;
				return;
			}
			case MapHistoryEntry.RemoveEntityProperty:
			{
				var entity = FindIn( state.Entities, entry.TargetId );
				if ( entity is null ) { report.Errors.Add( $"remove-entity-property: no entity '{entry.TargetId}'." ); return; }
				entity.KeyValues.Remove( entry.Key );
				return;
			}
			case MapHistoryEntry.AddDisplacement:
			{
				if ( entry.Displacement is null ) { report.Errors.Add( $"add-displacement '{entry.TargetId}' has no displacement." ); return; }
				var disp = entry.Displacement;
				if ( state.Displacements.Any( d => d.BrushId == disp.BrushId && d.FaceIndex == disp.FaceIndex ) )
				{ report.Errors.Add( $"duplicate displacement on brush '{disp.BrushId}' face {disp.FaceIndex}." ); return; }
				var error = CheckDisplacementApplies( state, disp );
				if ( error is not null ) { report.Errors.Add( error ); return; }
				state.Displacements.Add( disp.Clone() );
				return;
			}
			case MapHistoryEntry.SetDisplacement:
			{
				if ( entry.Displacement is null ) { report.Errors.Add( $"set-displacement '{entry.TargetId}' has no displacement." ); return; }
				var disp = entry.Displacement;
				var error = CheckDisplacementApplies( state, disp );
				if ( error is not null ) { report.Errors.Add( error ); return; }
				var existing = state.Displacements.FirstOrDefault( d => d.BrushId == disp.BrushId && d.FaceIndex == disp.FaceIndex );
				if ( existing is null )
					state.Displacements.Add( disp.Clone() );
				else
					state.Displacements[state.Displacements.IndexOf( existing )] = disp.Clone();
				return;
			}
			case MapHistoryEntry.RemoveDisplacement:
			{
				// TargetId is "brushId#faceIndex" (RemoveDisplacement builds it
				// so); displacements are keyed by (BrushId, FaceIndex), not by Id
				// (the old code matched d.Id == TargetId, so the remove could never
				// find the displacement the pre-check just found, and every
				// RemoveDisplacement threw in the fold). Fixed by
				// daxter-hammer-ai-buildscript (2026-11-13).
				var hash = entry.TargetId.LastIndexOf( '#' );
				var bId = hash >= 0 ? entry.TargetId.Substring( 0, hash ) : entry.TargetId;
				var fIdx = hash >= 0 && int.TryParse( entry.TargetId.Substring( hash + 1 ), out var n ) ? n : -1;
				var existing = state.Displacements.FirstOrDefault( d => d.BrushId == bId && d.FaceIndex == fIdx );
				if ( existing is null ) { report.Errors.Add( $"remove-displacement: no displacement '{entry.TargetId}'." ); return; }
				state.Displacements.Remove( existing );
				return;
			}
			case MapHistoryEntry.AddLayer:
				if ( entry.Layer is null ) { report.Errors.Add( $"add-layer '{entry.TargetId}' has no layer." ); return; }
				if ( state.Layers.Any( l => l.Id == entry.TargetId ) ) { report.Errors.Add( $"duplicate layer id '{entry.TargetId}'." ); return; }
				state.Layers.Add( entry.Layer.Clone() );
				return;
			case MapHistoryEntry.RemoveLayer:
				if ( state.Layers.All( l => l.Id != entry.TargetId ) ) { report.Errors.Add( $"remove-layer: no layer '{entry.TargetId}'." ); return; }
				state.Layers.RemoveAll( l => l.Id == entry.TargetId );
				return;
			case MapHistoryEntry.AddVisGroup:
				if ( entry.VisGroup is null ) { report.Errors.Add( $"add-visgroup '{entry.TargetId}' has no visgroup." ); return; }
				if ( state.VisGroups.Any( v => v.Id == entry.TargetId ) ) { report.Errors.Add( $"duplicate visgroup id '{entry.TargetId}'." ); return; }
				state.VisGroups.Add( entry.VisGroup.Clone() );
				return;
			case MapHistoryEntry.RemoveVisGroup:
				if ( state.VisGroups.All( v => v.Id != entry.TargetId ) ) { report.Errors.Add( $"remove-visgroup: no visgroup '{entry.TargetId}'." ); return; }
				state.VisGroups.RemoveAll( v => v.Id == entry.TargetId );
				return;
			case MapHistoryEntry.SetLayerVisible:
			{
				var layer = state.Layers.FirstOrDefault( l => l.Id == entry.TargetId );
				if ( layer is null ) { report.Errors.Add( $"set-layer-visible: no layer '{entry.TargetId}'." ); return; }
				layer.Visible = entry.Visible;
				return;
			}
			case MapHistoryEntry.SetVisGroupVisible:
			{
				var vis = state.VisGroups.FirstOrDefault( v => v.Id == entry.TargetId );
				if ( vis is null ) { report.Errors.Add( $"set-visgroup-visible: no visgroup '{entry.TargetId}'." ); return; }
				vis.Visible = entry.Visible;
				return;
			}
			case MapHistoryEntry.SetCompileSettings:
				if ( entry.Settings is null ) { report.Errors.Add( "set-compile has no settings." ); return; }
				state.Settings = entry.Settings.Clone();
				return;
			case MapHistoryEntry.AddIo:
				if ( entry.Io is null ) { report.Errors.Add( $"add-io '{entry.TargetId}' has no connection." ); return; }
				if ( state.Io.Any( c => c.Id == entry.TargetId ) ) { report.Errors.Add( $"duplicate I/O connection id '{entry.TargetId}'." ); return; }
				state.Io.Add( entry.Io.Clone() );
				return;
			case MapHistoryEntry.SetIo:
			{
				if ( entry.Io is null ) { report.Errors.Add( $"set-io '{entry.TargetId}' has no connection." ); return; }
				var existing = state.Io.FirstOrDefault( c => c.Id == entry.TargetId );
				if ( existing is null ) { report.Errors.Add( $"set-io: no I/O connection '{entry.TargetId}'." ); return; }
				state.Io[state.Io.IndexOf( existing )] = entry.Io.Clone();
				return;
			}
			case MapHistoryEntry.RemoveIo:
				if ( state.Io.All( c => c.Id != entry.TargetId ) ) { report.Errors.Add( $"remove-io: no I/O connection '{entry.TargetId}'." ); return; }
				state.Io.RemoveAll( c => c.Id == entry.TargetId );
				return;
			default:
				report.Errors.Add( $"unknown history kind '{entry.Kind}'." );
				return;
		}
	}

	static string CheckDisplacementApplies( FoldState state, MapDisplacementData disp )
	{
		var brush = FindIn( state.Brushes, disp.BrushId );
		if ( brush is null )
			return $"displacement '{disp.Id}': no brush '{disp.BrushId}'.";
		if ( disp.FaceIndex < 0 || disp.FaceIndex >= brush.Faces.Count )
			return $"displacement '{disp.Id}': face index {disp.FaceIndex} is out of range for brush '{disp.BrushId}'.";
		try
		{
			var size = Displacement.GridSizeForPower( disp.Power );
			if ( disp.Offsets is null || disp.Offsets.Length != size * size )
				return $"displacement '{disp.Id}': offsets has {disp.Offsets?.Length ?? 0} entries, needs {size * size} for power {disp.Power}.";
			if ( disp.Alphas is null || disp.Alphas.Length != size * size )
				return $"displacement '{disp.Id}': alphas has {disp.Alphas?.Length ?? 0} entries, needs {size * size} for power {disp.Power}.";
		}
		catch ( ArgumentOutOfRangeException ex )
		{
			return $"displacement '{disp.Id}': {ex.Message}";
		}
		return null;
	}

	/// <summary>
	/// Remap a brush to a new AABB: translate + per-axis scale about the old centre.
	/// Exact for a box; the documented affine remap for any other convex brush.
	/// Throws naming the problem when the brush has no solvable bounds, the new
	/// extents are negative, or a flat axis would have to change size.
	/// </summary>
	static MapBrushData RemapBrush( MapBrushData brush, Vec3 newMin, Vec3 newMax )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( !brush.TryBounds( out var oldMin, out var oldMax ) )
			throw new InvalidOperationException( $"Brush '{brush.Id}' has no solvable bounds, so it cannot be moved." );
		var oldSize = oldMax - oldMin;
		var newSize = newMax - newMin;
		if ( newSize.x < -1e-6f || newSize.y < -1e-6f || newSize.z < -1e-6f )
			throw new ArgumentException( $"MoveBrush '{brush.Id}': the new bounds are inverted (min {newMin} max {newMax})." );
		var scale = new Vec3(
			oldSize.x < 1e-6f ? 1f : newSize.x / oldSize.x,
			oldSize.y < 1e-6f ? 1f : newSize.y / oldSize.y,
			oldSize.z < 1e-6f ? 1f : newSize.z / oldSize.z );
		if ( ( oldSize.x < 1e-6f && MathF.Abs( newSize.x ) > 1e-6f )
			|| ( oldSize.y < 1e-6f && MathF.Abs( newSize.y ) > 1e-6f )
			|| ( oldSize.z < 1e-6f && MathF.Abs( newSize.z ) > 1e-6f ) )
			throw new ArgumentException( $"MoveBrush '{brush.Id}': cannot resize a flat brush axis." );
		var oldCentre = ( oldMin + oldMax ) * 0.5f;
		var newCentre = ( newMin + newMax ) * 0.5f;
		var copy = brush.Clone();
		for ( var i = 0; i < copy.Faces.Count; i++ )
		{
			var face = copy.Faces[i];
			var n = face.Normal;
			// Point on the plane, mapped through translate + axis scale.
			var p0 = n * face.Distance;
			var mapped = newCentre + new Vec3(
				( p0.x - oldCentre.x ) * scale.x,
				( p0.y - oldCentre.y ) * scale.y,
				( p0.z - oldCentre.z ) * scale.z );
			// Normal transforms by the inverse transpose (diagonal scale here).
			var inv = new Vec3(
				scale.x < 1e-9f ? 0f : n.x / scale.x,
				scale.y < 1e-9f ? 0f : n.y / scale.y,
				scale.z < 1e-9f ? 0f : n.z / scale.z );
			if ( inv.LengthSquared < 1e-12f )
				throw new InvalidOperationException( $"MoveBrush '{brush.Id}': face {i} normal collapses under the remap." );
			var newNormal = inv.Normal;
			copy.Faces[i] = new MapBrushFaceData( newNormal, Vec3.Dot( newNormal, mapped ), face.Material );
		}
		return copy;
	}

	// --- IMapUndoTarget -------------------------------------------------------------------------

	IReadOnlyList<MapBrushState> IMapUndoTarget.Brushes => _brushes.Select( b => b.ToBrushState() ).ToList();
	IReadOnlyList<MapEntityState> IMapUndoTarget.Entities => _entities.Select( e => e.ToEntityState() ).ToList();
	IReadOnlyList<MapDisplacementState> IMapUndoTarget.Displacements => _displacements.Select( d => d.ToDisplacementState() ).ToList();
	IReadOnlyList<MapIoState> IMapUndoTarget.IoConnections => _io.Select( c => c.ToIoState() ).ToList();

	MapBrushState IMapUndoTarget.FindBrush( string id ) => FindBrush( id )?.ToBrushState();
	MapEntityState IMapUndoTarget.FindEntity( string id ) => FindEntity( id )?.ToEntityState();
	MapDisplacementState IMapUndoTarget.FindDisplacement( string brushId, int faceIndex ) =>
		FindDisplacement( brushId, faceIndex )?.ToDisplacementState();
	MapIoState IMapUndoTarget.FindIo( string id ) => FindIo( id )?.ToIoState();

	void IMapUndoTarget.AddBrush( MapBrushState state ) => AddBrush( MapBrushData.FromBrushState( state ) );

	public void MoveBrush( string id, Vec3 min, Vec3 max )
	{
		CheckId( id, "brush" );
		var brush = FindBrush( id ) ?? throw new InvalidOperationException( $"No brush '{id}'." );
		// Validate the remap before appending, so a bad move never enters history.
		_ = RemapBrush( brush, min, max );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.MoveBrush, TargetId = id, Min = min, Max = max } );
	}

	public void SetBrushMaterial( string id, string material )
	{
		CheckId( id, "brush" );
		if ( string.IsNullOrWhiteSpace( material ) )
			throw new ArgumentException( "A brush material must not be blank.", nameof( material ) );
		if ( FindBrush( id ) is null )
			throw new InvalidOperationException( $"No brush '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetBrushMaterial, TargetId = id, Material = material } );
	}

	void IMapUndoTarget.AddEntity( MapEntityState state ) => AddEntity( MapEntityData.FromEntityState( state ) );

	public void MoveEntity( string id, Vec3 origin )
	{
		CheckId( id, "entity" );
		if ( FindEntity( id ) is null )
			throw new InvalidOperationException( $"No entity '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.MoveEntity, TargetId = id, Origin = origin } );
	}

	/// <summary>
	/// Set one keyvalue. "origin" and "angles" are entity FIELDS, never keyvalues:
	/// setting them here moves the field (a malformed "x y z" is refused before
	/// anything is appended). Every other key lands in KeyValues.
	/// </summary>
	public void SetEntityProperty( string id, string key, string value )
	{
		CheckId( id, "entity" );
		if ( FindEntity( id ) is null )
			throw new InvalidOperationException( $"No entity '{id}'." );
		if ( string.IsNullOrWhiteSpace( key ) )
			throw new ArgumentException( "Property key cannot be empty.", nameof( key ) );
		if ( value is null )
			throw new ArgumentNullException( nameof( value ) );
		if ( ( string.Equals( key, "origin", StringComparison.OrdinalIgnoreCase )
			|| string.Equals( key, "angles", StringComparison.OrdinalIgnoreCase ) )
			&& !MapEntityData.TryParseTriple( value, out _ ) )
			throw new ArgumentException( $"'{key}' must be three numbers ('x y z'), got '{value}'.", nameof( value ) );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetEntityProperty, TargetId = id, Key = key, Value = value } );
	}

	public void RemoveEntityProperty( string id, string key )
	{
		CheckId( id, "entity" );
		if ( FindEntity( id ) is null )
			throw new InvalidOperationException( $"No entity '{id}'." );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.RemoveEntityProperty, TargetId = id, Key = key ?? "" } );
	}

	void IMapUndoTarget.AddDisplacement( MapDisplacementState state ) =>
		AddDisplacement( MapDisplacementData.FromDisplacementState( state ) );

	public void SetDisplacement( string brushId, int faceIndex, float[] heights, int power )
	{
		if ( heights is null ) throw new ArgumentNullException( nameof( heights ) );
		var brush = FindBrush( brushId ) ?? throw new InvalidOperationException( $"No brush '{brushId}'." );
		if ( faceIndex < 0 || faceIndex >= brush.Faces.Count )
			throw new ArgumentOutOfRangeException( nameof( faceIndex ),
				$"Brush '{brushId}' has {brush.Faces.Count} faces; face {faceIndex} is out of range." );
		var disp = MapDisplacementData.FromDisplacementState( new MapDisplacementState
		{
			BrushId = brushId,
			FaceIndex = faceIndex,
			Power = power,
			Heights = (float[])heights.Clone(),
		} );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetDisplacement, TargetId = disp.Id, Displacement = disp } );
	}

	void IMapUndoTarget.AddIo( MapIoState state )
	{
		var conn = MapIoConnection.FromIoState( state );
		AddIo( conn.Id, conn.Row );
	}

	public void SetIo( MapIoState state )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		CheckId( state.Id, "I/O connection" );
		if ( FindIo( state.Id ) is null )
			throw new InvalidOperationException( $"No I/O connection '{state.Id}'." );
		var conn = MapIoConnection.FromIoState( state );
		AppendEntry( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetIo, TargetId = state.Id, Io = conn } );
	}

	public MapStateSnapshot CaptureSnapshot() => new()
	{
		DocumentId = DocumentId,
		Revision = Revision,
		SavedAtUtc = DateTimeOffset.UtcNow,
		Brushes = ( (IMapUndoTarget)this ).Brushes.Select( b => b.Clone() ).ToList(),
		Entities = ( (IMapUndoTarget)this ).Entities.Select( e => e.Clone() ).ToList(),
		Displacements = ( (IMapUndoTarget)this ).Displacements.Select( d => d.Clone() ).ToList(),
		IoConnections = ( (IMapUndoTarget)this ).IoConnections.Select( i => i.Clone() ).ToList(),
	};

	public void ApplySnapshot( MapStateSnapshot snapshot )
	{
		if ( snapshot is null ) throw new ArgumentNullException( nameof( snapshot ) );
		snapshot.Validate();
		// Layers, visgroups and compile settings are not in the snapshot: they survive
		// (re-appended first so brushes that reference them still resolve).
		var layers = _layers.Select( l => l.Clone() ).ToList();
		var visGroups = _visgroups.Select( v => v.Clone() ).ToList();
		var settings = _compileSettings.Clone();
		History.Clear();
		foreach ( var layer in layers )
			History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddLayer, TargetId = layer.Id, Layer = layer } );
		foreach ( var vis in visGroups )
			History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddVisGroup, TargetId = vis.Id, VisGroup = vis } );
		History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.SetCompileSettings, Settings = settings } );
		foreach ( var brush in snapshot.Brushes )
			History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddBrush, TargetId = brush.Id, Brush = MapBrushData.FromBrushState( brush ) } );
		foreach ( var entity in snapshot.Entities )
			History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddEntity, TargetId = entity.Id, Entity = MapEntityData.FromEntityState( entity ) } );
		foreach ( var disp in snapshot.Displacements )
			History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddDisplacement, TargetId = disp.BrushId + "#" + disp.FaceIndex, Displacement = MapDisplacementData.FromDisplacementState( disp ) } );
		foreach ( var io in snapshot.IoConnections )
		{
			var conn = MapIoConnection.FromIoState( io );
			History.Add( new MapHistoryEntry { Id = NewId(), Kind = MapHistoryEntry.AddIo, TargetId = conn.Id, Io = conn } );
		}
		RollbackIndex = int.MaxValue;
		var report = RebuildInternal();
		if ( report.HasErrors )
			throw new InvalidDataException(
				$"Snapshot cannot apply: {string.Join( "; ", report.Errors )}" );
		Revision = Math.Max( 1, snapshot.Revision );
		Bump();
	}

	/// <summary>
	/// Lossless snapshot: the full .gmap text (MapDocumentIo.Write, no validation —
	/// autosave must never refuse to save work because the document has a defect).
	/// </summary>
	public string SerializeSnapshot() => MapDocumentIo.Write( this );

	public void RestoreSnapshot( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new InvalidDataException( "Map snapshot is empty." );
		var restored = MapDocumentIo.Read( json );
		History = restored.History;
		RollbackIndex = restored.RollbackIndex;
		DocumentId = restored.DocumentId;
		Name = restored.Name;
		LoadNotes = restored.LoadNotes;
		LoadedFormatVersion = restored.LoadedFormatVersion;
		RebuildInternal();
		Bump();
	}

	public override string ToString() => $"map '{Name}' ({_brushes.Count} brushes, {_entities.Count} entities, rev {Revision})";
}
