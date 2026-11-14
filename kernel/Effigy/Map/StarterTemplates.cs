#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2b: first-map starter templates + onboarding checklist data
/// (hammer-first-map-starter-templates).
///
/// Templates are native map documents, not imported VMFs: each builder returns a
/// <see cref="StarterMap"/> (an <see cref="IssueDocument"/> snapshot + worldspawn /
/// skybox data + displacement records) that validates clean under the landed
/// <see cref="IssueEngine"/>. The checklist reuses the IssueEngine rules — it runs
/// the engine once and maps its issues onto the ordered first-map steps
/// (seal, spawn, light, skybox, compile, playtest). Zero UI code: data + API only.
///
/// Dependency status, verified against the tree 2026-11-13 (implement against the
/// spec exactly as filed; mismatches are reported here, not papered over):
///   - MapBrushPrimitives LANDED (kernel/Effigy/Map/MapBrushPrimitives.cs) but its
///     MapBrush / MapFace / MapPlane API has NOT landed (no MapBrush.cs on disk),
///     so the builders here do NOT call it. They build the SAME plane sets
///     (box = 6 axis planes, wedge = base/back/slope/end planes exactly as
///     MapBrushPrimitives.Wedge documents them) directly onto the landed
///     IssueDocument snapshot. When MapBrush lands, the adapter is: feed these
///     plane sets to MapBrush.FromPlanes instead of BrushFromPlanes below.
///   - MapDocument / MapEntities have NOT landed (no files on disk), so the
///     template document is the landed IssueDocument / IssueEntity snapshot the
///     IssueEngine header itself defines as the interim shape ("the integrator
///     adds ONE thin adapter that projects MapDocument onto IssueDocument").
///     Entities here mirror the MapEntities spec: info_player_start + light
///     (+ light_environment / sky_camera, see below) placed by classname +
///     origin + keyvalues.
///   - Skybox types have NOT landed (hammer-skybox-2d-3d-worldspawn is still
///     todo), so <see cref="WorldspawnProperties"/>, <see cref="SkyboxSettings"/>
///     and <see cref="SkyboxSection3D"/> are defined here, shaped exactly as
///     that desc specifies: skybox name on worldspawn, sky_camera entity, and a
///     3D skybox section (scaled geometry + its own origin).
///   - light_environment and sky_camera are NOT in the landed registry
///     (kernel/Effigy/Map/registry/core-source1.json has 13 classes, neither of
///     them). The templates use the standard Source classnames anyway, per the
///     skybox desc, and the checklist checks presence by classname — it does
///     NOT run EntityRegistry validation, so no registry claim is made.
///   - VMF export uses the landed VmfIo (VmfDocument / VmfIo.Write / Parse).
///     The 3D skybox section is NOT exported to VMF here: its brushes live in
///     skybox space, not world space, and the skybox ticket owns the
///     skybox-section VMF round-trip. ToVmf exports the main map only.
///
/// Geometry is the kernel's inches and Z-up (handoffs/CONTRACTS.md). Brushes are
/// convex plane intersections; face polygons are computed by triple-plane
/// intersection and ordered counter-clockwise around the outward normal (the
/// same winding IssueEngineTests proves clean under InvalidBrushRule +
/// GeometryLinter). A template that fails its own checklist is a generator bug:
/// builders throw instead of returning a broken template.
/// </summary>
public sealed class WorldspawnProperties
{
	/// <summary>2D skybox name, exported to VMF as worldspawn "skyname".</summary>
	public string SkyboxName = "sky_day01_01";
	/// <summary>Chapter title for the map. Carried in the native document; no VMF
	/// worldspawn key for it exists in the landed VmfIo fixtures, so ToVmf does
	/// not export it (the skybox ticket defines any key mapping).</summary>
	public string Chapter = "";
	/// <summary>Map title. Same carrying rule as <see cref="Chapter"/>.</summary>
	public string Title = "";
	/// <summary>Detail props on (detail.vbsp). Exported by the skybox ticket.</summary>
	public bool DetailProps = true;
	/// <summary>Fog preset name, empty = default / no fog. The fog value format
	/// is owned by the skybox ticket; this field only carries the choice.</summary>
	public string Fog = "";
}

/// <summary>
/// A 3D skybox section: scaled geometry + its own origin, per the skybox desc.
/// Brushes live in SKYBOX space (they are NOT world brushes and never take part
/// in leak / seal checks); <see cref="Scale"/> is the Source-standard 16.
/// </summary>
public sealed class SkyboxSection3D
{
	/// <summary>Anchor of the section; the sky_camera entity sits here.</summary>
	public Vec3 Origin;
	/// <summary>World units per skybox unit. Source standard is 16.</summary>
	public float Scale = 16f;
	/// <summary>Distant geometry (mountains / city) in skybox space.</summary>
	public List<IssueBrush> Brushes = new List<IssueBrush>();
}

/// <summary>
/// Skybox data for a template: 2D by default (skybox name only); 3D when
/// <see cref="Section3D"/> is set, in which case a sky_camera entity must be
/// present in the document (the checklist enforces it).
/// </summary>
public sealed class SkyboxSettings
{
	public SkyboxSection3D? Section3D;
	public bool Is3D => Section3D is not null;
}

/// <summary>
/// A displacement attached to one face of one brush: the data record the
/// displacement ticket owns the grid for. The geometry is unchanged — the
/// backing brush still seals the map — so this is data only, exported to VMF
/// as dispinfo on that face.
/// </summary>
public sealed class DisplacementInfo
{
	public int BrushId;
	/// <summary>Face index in the brush's face list (box +Z face is index 5).</summary>
	public int FaceIndex;
	/// <summary>Displacement power, 2..4 (Source range, enforced by VmfDisplacement).</summary>
	public int Power = 3;
	public Vec3 StartPosition;
	public double Elevation;
	public bool Subdiv;
}

/// <summary>
/// One starter template as a native map document: brushes + entities
/// (<see cref="Document"/>) plus worldspawn / skybox data and displacement
/// records. This is the template a "New map from template" caller opens; the
/// MapDocument ticket's adapter projects it onto MapDocument when that lands.
/// </summary>
public sealed class StarterMap
{
	public string TemplateId = "";
	public string Name = "";
	public string Description = "";
	public IssueDocument Document = new IssueDocument();
	public WorldspawnProperties Worldspawn = new WorldspawnProperties();
	public SkyboxSettings Skybox = new SkyboxSettings();
	public List<DisplacementInfo> Displacements = new List<DisplacementInfo>();

	public IReadOnlyList<IssueEntity> FindEntities( string className )
	{
		var found = new List<IssueEntity>();
		if ( Document.Entities is null || string.IsNullOrWhiteSpace( className ) )
			return found;
		foreach ( var entity in Document.Entities )
			if ( entity is not null && string.Equals( entity.ClassName, className, StringComparison.OrdinalIgnoreCase ) )
				found.Add( entity );
		return found;
	}

	public bool HasEntity( string className ) => FindEntities( className ).Count > 0;
	public bool HasSkyCamera => HasEntity( "sky_camera" );
}

/// <summary>Catalogue entry for one template (no document attached).</summary>
public sealed class StarterTemplateInfo
{
	public string Id = "";
	public string Name = "";
	public string Description = "";
}

/// <summary>
/// Template builders on the landed IssueDocument snapshot, with the plane sets
/// of MapBrushPrimitives (see the file header for the dependency status).
/// Every template is sealed, has a player start, has a light, and names a
/// skybox — i.e. it passes its own checklist — or the builder throws.
/// </summary>
public static class StarterTemplates
{
	/// <summary>Dev material for template faces (visible greybox measure texture).</summary>
	public const string DevMaterial = "dev/dev_measuregeneric01";
	/// <summary>Skybox-lid material: the seal brush over an outdoor area.</summary>
	public const string SkyboxMaterial = "tools/toolsskybox";
	/// <summary>Default 2D skybox for every template (matches the VMF fixture room.vmf).</summary>
	public const string DefaultSkybox = "sky_day01_01";
	/// <summary>Wall / floor thickness for template rooms, inches.</summary>
	public const float WallThickness = 16f;

	public static IReadOnlyList<StarterTemplateInfo> All => new[]
	{
		new StarterTemplateInfo { Id = "minimal-room", Name = "Minimal playable room",
			Description = "Sealed 512x512 room, 256 high: floor, ceiling, four walls, player start, light, skybox. The first map." },
		new StarterTemplateInfo { Id = "surf-ramp", Name = "Surf ramp starter",
			Description = "Sealed 1024x512 room with a wedge surf ramp against the -X wall, player start facing the ramp, light, skybox." },
		new StarterTemplateInfo { Id = "outdoor-displacement", Name = "Outdoor / displacement starter",
			Description = "Sealed 1024x1024 yard with a power-3 displacement ground, light_environment sun, sky_camera + 16x 3D skybox section (mountains), skybox lid seal." },
	};

	/// <summary>Build a template by id ("minimal-room", "surf-ramp",
	/// "outdoor-displacement"). Unknown ids throw ArgumentException naming the id.</summary>
	public static StarterMap Build( string templateId )
	{
		switch ( templateId )
		{
			case "minimal-room": return MinimalRoom();
			case "surf-ramp": return SurfRamp();
			case "outdoor-displacement": return OutdoorDisplacement();
			default:
				throw new ArgumentException(
					$"Unknown starter template '{templateId}'. Known templates: {string.Join( ", ", All.Select( t => t.Id ) )}.",
					nameof( templateId ) );
		}
	}

	/// <summary>Minimal playable room: sealed 512x512, floor top z=0, ceiling
	/// underside z=256. 6 brushes + player start + light + 2D skybox.</summary>
	public static StarterMap MinimalRoom()
	{
		var map = new StarterMap
		{
			TemplateId = "minimal-room",
			Name = "Minimal playable room",
			Description = "Sealed 512x512 room, 256 high: floor, ceiling, four walls, player start, light, skybox.",
		};
		map.Document.Name = "minimal-room";
		SealedRoom( map.Document, halfX: 256f, halfY: 256f, floorTopZ: 0f, ceilingBottomZ: 256f, ceilingMaterial: DevMaterial );
		map.Document.Entities.Add( PlayerStart( 100, new Vec3( 0, 0, 64 ), "0 0 0" ) );
		var light = new IssueEntity( 101, "light", new Vec3( 0, 0, 224 ) );
		light.SetKeyValue( "_light", "255 240 200 400" );
		map.Document.Entities.Add( light );
		map.Worldspawn.SkyboxName = DefaultSkybox;
		return Finish( map );
	}

	/// <summary>Surf-ramp starter: sealed 1024x512 room + one wedge ramp
	/// (256x256x128, full height at the -X wall, sloping down to +X) against
	/// the -X wall. 7 brushes + player start facing the ramp + light.</summary>
	public static StarterMap SurfRamp()
	{
		var map = new StarterMap
		{
			TemplateId = "surf-ramp",
			Name = "Surf ramp starter",
			Description = "Sealed 1024x512 room with a wedge surf ramp against the -X wall, player start facing the ramp, light, skybox.",
		};
		map.Document.Name = "surf-ramp";
		SealedRoom( map.Document, halfX: 512f, halfY: 256f, floorTopZ: 0f, ceilingBottomZ: 256f, ceilingMaterial: DevMaterial );
		// Ramp: centre (-384, 0, 64) spans x -512..-256, y -128..128, z 0..128.
		map.Document.Brushes.Add( WedgeBrush( 7, new Vec3( -384, 0, 64 ), 256f, 256f, 128f, DevMaterial ) );
		map.Document.Entities.Add( PlayerStart( 100, new Vec3( 256, 0, 64 ), "0 180 0" ) );
		var light = new IssueEntity( 101, "light", new Vec3( 0, 0, 224 ) );
		light.SetKeyValue( "_light", "255 240 200 400" );
		map.Document.Entities.Add( light );
		map.Worldspawn.SkyboxName = DefaultSkybox;
		return Finish( map );
	}

	/// <summary>Outdoor / displacement starter: sealed 1024x1024 yard, ground
	/// slab with a power-3 displacement record on its top face, light_environment
	/// sun, sky_camera + a 16x 3D skybox section with distant mountains, and a
	/// skybox-textured lid brush as the +Z seal (the Source outdoor pattern).</summary>
	public static StarterMap OutdoorDisplacement()
	{
		var map = new StarterMap
		{
			TemplateId = "outdoor-displacement",
			Name = "Outdoor / displacement starter",
			Description = "Sealed 1024x1024 yard with a power-3 displacement ground, light_environment sun, sky_camera + 16x 3D skybox section (mountains), skybox lid seal.",
		};
		map.Document.Name = "outdoor-displacement";
		SealedRoom( map.Document, halfX: 512f, halfY: 512f, floorTopZ: 0f, ceilingBottomZ: 256f, ceilingMaterial: SkyboxMaterial );
		map.Displacements.Add( new DisplacementInfo
		{
			BrushId = 1, FaceIndex = 5, Power = 3,
			StartPosition = new Vec3( -512, -512, 0 ), Elevation = 0, Subdiv = false,
		} );
		var sun = new IssueEntity( 101, "light_environment", new Vec3( 0, 0, 512 ) );
		sun.SetKeyValue( "_light", "255 244 214 400" );
		sun.SetKeyValue( "angles", "0 0 0" );
		sun.SetKeyValue( "pitch", "-45" );
		map.Document.Entities.Add( sun );
		map.Document.Entities.Add( PlayerStart( 100, new Vec3( 0, 0, 64 ), "0 0 0" ) );
		map.Document.Entities.Add( new IssueEntity( 102, "sky_camera", new Vec3( 0, 0, 0 ) ) );
		var section = new SkyboxSection3D { Origin = new Vec3( 0, 0, 0 ), Scale = 16f };
		section.Brushes.Add( BoxBrush( 101, -1200f, -400f, 0f, -800f, 400f, 512f, DevMaterial ) );
		section.Brushes.Add( BoxBrush( 102, 800f, -400f, 0f, 1200f, 400f, 384f, DevMaterial ) );
		section.Brushes.Add( BoxBrush( 103, -400f, 800f, 0f, 400f, 1200f, 640f, DevMaterial ) );
		map.Skybox.Section3D = section;
		map.Worldspawn.SkyboxName = DefaultSkybox;
		return Finish( map );
	}

	/// <summary>Run the live Issue engine over a template's document. The
	/// checklist reuses this run; callers that only need issues use this.</summary>
	public static IReadOnlyList<Issue> Validate( StarterMap map )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );
		return new IssueEngine().Run( map.Document );
	}

	/// <summary>Export a template's MAIN map to a VmfDocument (worldspawn
	/// skyname, world brushes, point entities). The 3D skybox section is not
	/// exported here — its brushes live in skybox space and the skybox ticket
	/// owns the section round-trip (see the file header).</summary>
	public static VmfDocument ToVmf( StarterMap map )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );
		var doc = new VmfDocument { MapVersion = 1 };
		doc.WorldspawnSet( "skyname", map.Worldspawn.SkyboxName );
		var faceId = 0;
		foreach ( var brush in map.Document.Brushes )
		{
			if ( brush is null )
				continue;
			var vmfBrush = new VmfBrush { Id = brush.Id };
			for ( var i = 0; i < brush.Faces.Count; i++ )
			{
				var face = brush.Faces[i];
				if ( face is null || face.Vertices.Count < 3 )
					throw new InvalidOperationException(
						$"Cannot export brush {brush.Id} face {i}: it has fewer than 3 polygon vertices." );
				var vmfFace = new VmfFace
				{
					Id = ++faceId,
					Material = face.Material,
					LightmapScale = 16,
				};
				// Plane fidelity: the first three polygon vertices are the
				// plane points; the winding is CCW around the outward normal,
				// so VmfFace.Normal (cross of p1-p0, p2-p0) points outward.
				vmfFace.PlanePoints = new[]
				{
					new VmfVec( face.Vertices[0].x, face.Vertices[0].y, face.Vertices[0].z ),
					new VmfVec( face.Vertices[1].x, face.Vertices[1].y, face.Vertices[1].z ),
					new VmfVec( face.Vertices[2].x, face.Vertices[2].y, face.Vertices[2].z ),
				};
				var disp = map.Displacements.FirstOrDefault( d => d.BrushId == brush.Id && d.FaceIndex == i );
				if ( disp != null )
					vmfFace.Displacement = VmfDisplacement.Create( disp.Power,
						new VmfVec( disp.StartPosition.x, disp.StartPosition.y, disp.StartPosition.z ), disp.Elevation );
				vmfBrush.Faces.Add( vmfFace );
			}
			doc.WorldBrushes.Add( vmfBrush );
		}
		foreach ( var entity in map.Document.Entities )
		{
			if ( entity is null )
				continue;
			var vmfEntity = new VmfEntity { Id = entity.Id, ClassName = entity.ClassName };
			vmfEntity.KeyValues.Add( new KeyValuePair<string, string>( "origin", VecString( entity.Origin ) ) );
			foreach ( var kv in entity.KeyValues )
			{
				if ( string.Equals( kv.Key, "origin", StringComparison.OrdinalIgnoreCase )
					|| string.Equals( kv.Key, "classname", StringComparison.OrdinalIgnoreCase ) )
					continue;
				vmfEntity.KeyValues.Add( new KeyValuePair<string, string>( kv.Key, kv.Value ) );
			}
			doc.Entities.Add( vmfEntity );
		}
		return doc;
	}

	/// <summary>VMF text for a template (VmfIo.Write over <see cref="ToVmf"/>).</summary>
	public static string ToVmfText( StarterMap map ) => VmfIo.Write( ToVmf( map ) );

	// ----- builders -----

	static IssueEntity PlayerStart( int id, Vec3 origin, string angles )
	{
		var spawn = new IssueEntity( id, "info_player_start", origin );
		spawn.SetKeyValue( "angles", angles );
		return spawn;
	}

	/// <summary>Sealed room, IssueEngineTests pattern: floor / ceiling span the
	/// interior, X walls span interior + thickness in Y, Y walls span the
	/// interior in X. Brush ids 1 floor, 2 ceiling, 3 -X, 4 +X, 5 -Y, 6 +Y.</summary>
	static void SealedRoom( IssueDocument doc, float halfX, float halfY, float floorTopZ, float ceilingBottomZ, string ceilingMaterial )
	{
		var t = WallThickness;
		var hx = halfX;
		var hy = halfY;
		doc.Brushes.Add( BoxBrush( 1, -hx, -hy, floorTopZ - t, hx, hy, floorTopZ, DevMaterial ) );
		doc.Brushes.Add( BoxBrush( 2, -hx, -hy, ceilingBottomZ, hx, hy, ceilingBottomZ + t, ceilingMaterial ) );
		doc.Brushes.Add( BoxBrush( 3, -hx - t, -hy - t, floorTopZ, -hx, hy + t, ceilingBottomZ, DevMaterial ) );
		doc.Brushes.Add( BoxBrush( 4, hx, -hy - t, floorTopZ, hx + t, hy + t, ceilingBottomZ, DevMaterial ) );
		doc.Brushes.Add( BoxBrush( 5, -hx, -hy - t, floorTopZ, hx, -hy, ceilingBottomZ, DevMaterial ) );
		doc.Brushes.Add( BoxBrush( 6, -hx, hy, floorTopZ, hx, hy + t, ceilingBottomZ, DevMaterial ) );
	}

	/// <summary>Axis-aligned box brush from corner coordinates, faces in
	/// -X, +X, -Y, +Y, -Z, +Z order (same order as IssueEngineTests' Box).</summary>
	internal static IssueBrush BoxBrush( int id, float x0, float y0, float z0, float x1, float y1, float z1, string material )
		=> BrushFromPlanes( id, new[]
		{
			// Plane convention is n . x = d with n OUTWARD: the -X face at
			// x = x0 has d = -x0 (not x0), and likewise for -Y / -Z.
			( new Vec3( -1, 0, 0 ), (double) -x0 ),
			( new Vec3( 1, 0, 0 ), (double) x1 ),
			( new Vec3( 0, -1, 0 ), (double) -y0 ),
			( new Vec3( 0, 1, 0 ), (double) y1 ),
			( new Vec3( 0, 0, -1 ), (double) -z0 ),
			( new Vec3( 0, 0, 1 ), (double) z1 ),
		}, material );

	/// <summary>Wedge brush, MapBrushPrimitives.Wedge orientation: full height at
	/// the -X side, sloping down to zero height at the +X side; 5 faces; volume
	/// SizeX*SizeY*SizeZ/2. Centre + size, like the primitive.</summary>
	internal static IssueBrush WedgeBrush( int id, Vec3 centre, float sizeX, float sizeY, float sizeZ, string material )
	{
		var hx = sizeX * 0.5f;
		var hy = sizeY * 0.5f;
		var hz = sizeZ * 0.5f;
		// Slope plane through (cx-hx, cz+hz) and (cx+hx, cz-hz): normal
		// (hz, 0, hx) unit, distance n . (cx-hx, *, cz+hz).
		var slope = new Vec3( hz, 0, hx ).Normal;
		var slopeDistance = Vec3.Dot( slope, new Vec3( centre.x - hx, centre.y, centre.z + hz ) );
		return BrushFromPlanes( id, new[]
		{
			( new Vec3( 0, 0, -1 ), (double) -( centre.z - hz ) ),          // base
			( new Vec3( -1, 0, 0 ), (double) -( centre.x - hx ) ),          // back, full height at -X
			( slope, (double) slopeDistance ),                               // slope
			( new Vec3( 0, -1, 0 ), (double) -( centre.y - hy ) ),          // -Y end
			( new Vec3( 0, 1, 0 ), (double) ( centre.y + hy ) ),            // +Y end
		}, material );
	}

	/// <summary>Build a convex IssueBrush from outward plane sets: polygon
	/// vertices are the triple-plane intersections inside every half-space,
	/// ordered counter-clockwise around the outward normal. Throws
	/// InvalidOperationException when a plane set does not close (a generator
	/// bug, never a caller error — the caller gave no planes).</summary>
	internal static IssueBrush BrushFromPlanes( int id,
		IReadOnlyList<(Vec3 Normal, double Distance)> planes, string material, bool isDetail = false )
	{
		if ( planes is null || planes.Count < 4 )
			throw new InvalidOperationException( $"Brush {id} needs at least 4 planes to close, got {planes?.Count ?? 0}." );
		if ( string.IsNullOrWhiteSpace( material ) )
			throw new InvalidOperationException( $"Brush {id} needs a material (an empty material fails the MissingMaterial rule)." );
		var faces = new List<IssueFace>( planes.Count );
		for ( var i = 0; i < planes.Count; i++ )
		{
			var (normal, distance) = planes[i];
			var unit = normal.Normal;
			if ( unit.Equals( Vec3.Zero ) )
				throw new InvalidOperationException( $"Brush {id} face {i} has a zero normal." );
			var points = PolygonOnPlane( planes, i, unit, distance );
			if ( points.Count < 3 )
				throw new InvalidOperationException(
					$"Brush {id} face {i} does not close: only {points.Count} polygon vertices from its plane set." );
			faces.Add( new IssueFace( unit, distance, material, points ) );
		}
		var brush = new IssueBrush( id, faces, isDetail );
		var defects = InvalidBrushRule.Validate( brush );
		if ( defects.Count > 0 )
			throw new InvalidOperationException(
				$"StarterTemplates built an invalid brush {id} (generator bug): {string.Join( "; ", defects )}" );
		return brush;
	}

	static List<Vec3> PolygonOnPlane( IReadOnlyList<(Vec3 Normal, double Distance)> planes,
		int faceIndex, Vec3 normal, double distance )
	{
		var points = new List<Vec3>();
		for ( var j = 0; j < planes.Count; j++ )
		{
			if ( j == faceIndex )
				continue;
			for ( var k = j + 1; k < planes.Count; k++ )
			{
				if ( k == faceIndex )
					continue;
				var p = IntersectThreePlanes( planes[faceIndex], planes[j], planes[k] );
				if ( p is null )
					continue;
				var point = p.Value;
				var inside = true;
				for ( var m = 0; m < planes.Count; m++ )
				{
					if ( Vec3.Dot( planes[m].Normal.Normal, point ) > planes[m].Distance + 1e-3 )
					{
						inside = false;
						break;
					}
				}
				if ( !inside )
					continue;
				var duplicate = false;
				foreach ( var existing in points )
					if ( ( existing - point ).Length <= 1e-3f )
					{
						duplicate = true;
						break;
					}
				if ( !duplicate )
					points.Add( point );
			}
		}
		if ( points.Count < 3 )
			return points;
		// Order counter-clockwise around the outward normal: basis (u, v) with
		// u x v = normal, sort by angle in that basis. (Matches the winding
		// IssueEngineTests' hand-written boxes use, up to start vertex.)
		var centre = Vec3.Zero;
		foreach ( var p in points )
			centre += p;
		centre /= points.Count;
		var axis = Math.Abs( normal.x ) <= Math.Abs( normal.y ) && Math.Abs( normal.x ) <= Math.Abs( normal.z )
			? new Vec3( 1, 0, 0 )
			: Math.Abs( normal.y ) <= Math.Abs( normal.z )
				? new Vec3( 0, 1, 0 )
				: new Vec3( 0, 0, 1 );
		var u = Vec3.Cross( normal, axis ).Normal;
		var v = Vec3.Cross( normal, u );
		points.Sort( ( a, b ) =>
		{
			var aa = Math.Atan2( Vec3.Dot( a - centre, v ), Vec3.Dot( a - centre, u ) );
			var ab = Math.Atan2( Vec3.Dot( b - centre, v ), Vec3.Dot( b - centre, u ) );
			return aa.CompareTo( ab );
		} );
		return points;
	}

	static Vec3? IntersectThreePlanes( (Vec3 Normal, double Distance) a,
		(Vec3 Normal, double Distance) b, (Vec3 Normal, double Distance) c )
	{
		var n1 = a.Normal.Normal;
		var n2 = b.Normal.Normal;
		var n3 = c.Normal.Normal;
		var det = Vec3.Dot( n1, Vec3.Cross( n2, n3 ) );
		if ( Math.Abs( det ) < 1e-9 )
			return null;
		var p = ( Vec3.Cross( n2, n3 ) * (float) a.Distance
			+ Vec3.Cross( n3, n1 ) * (float) b.Distance
			+ Vec3.Cross( n1, n2 ) * (float) c.Distance ) / det;
		return p;
	}

	/// <summary>A builder's last step: the template must pass its own checklist
	/// (zero Error issues from the IssueEngine) or it is a generator bug.</summary>
	static StarterMap Finish( StarterMap map )
	{
		var issues = Validate( map );
		var errors = issues.Where( i => i.Severity == IssueSeverity.Error ).ToList();
		if ( errors.Count > 0 )
			throw new InvalidOperationException(
				$"Starter template '{map.TemplateId}' fails its own validation (generator bug): " +
				string.Join( "; ", errors.Select( e => e.ToString() ) ) );
		return map;
	}

	static string Num( double value ) => value.ToString( "0.####", CultureInfo.InvariantCulture );
	static string VecString( Vec3 v ) => $"{Num( v.x )} {Num( v.y )} {Num( v.z )}";
}

/// <summary>One first-map checklist step (ordered): seal, spawn, light, skybox,
/// compile, playtest. Data only — evaluation lives in <see cref="Checklist"/>.</summary>
public sealed class ChecklistStep
{
	public string Id = "";
	public string Title = "";
	public string Description = "";
	public int Order;
}

/// <summary>One evaluated checklist step.</summary>
public sealed class ChecklistStepResult
{
	public string StepId = "";
	public string Title = "";
	public bool Passed;
	public string Details = "";
	public IReadOnlyList<string> BlockingIssueCodes = Array.Empty<string>();
}

/// <summary>Whole-checklist evaluation for one map.</summary>
public sealed class ChecklistResult
{
	public IReadOnlyList<ChecklistStepResult> Steps = Array.Empty<ChecklistStepResult>();
	public bool AllPassed;
	public string? FirstIncompleteStepId;
	/// <summary>The IssueEngine run the evaluation reused (empty when the caller
	/// supplied its own issues — see Checklist.Evaluate overloads).</summary>
	public IReadOnlyList<Issue> Issues = Array.Empty<Issue>();
}

/// <summary>
/// First-map onboarding checklist model. Each step has a validation query
/// against the map document, reusing the IssueEngine rules (one engine run,
/// shared by every step):
///   seal     — no Leak / UnsealedMap / InvalidBrush errors.
///   spawn    — an info_player_start exists and sits in the void (not inside
///              any solid brush). Note the seal verdict is vacuous without a
///              spawn: LeakProbe needs a player start to flood from.
///   light    — a light or light_environment exists.
///   skybox   — worldspawn names a skybox; a 3D section additionally needs a
///              sky_camera (the skybox desc's warning, as a failed step).
///   compile  — no Error-severity issue from any rule.
///   playtest — every step above passes: the map is READY for playtest.
///              The checklist reports readiness only; actually compiling is
///              hammer-compile-native-pipeline's job and actually playtesting
///              is hammer-quick-play-bsp-only-bake's — neither runs here.
/// </summary>
public static class Checklist
{
	public static IReadOnlyList<ChecklistStep> DefaultSteps() => new[]
	{
		new ChecklistStep { Id = "seal", Order = 1, Title = "Seal the map",
			Description = "No leak, no unsealed side, no invalid brush (IssueEngine Leak / UnsealedMap / InvalidBrush rules)." },
		new ChecklistStep { Id = "spawn", Order = 2, Title = "Place a player spawn",
			Description = "An info_player_start exists and sits in the void, not inside a solid brush." },
		new ChecklistStep { Id = "light", Order = 3, Title = "Add a light",
			Description = "A light or light_environment entity exists." },
		new ChecklistStep { Id = "skybox", Order = 4, Title = "Set the skybox",
			Description = "Worldspawn names a skybox; a 3D skybox section additionally needs a sky_camera entity." },
		new ChecklistStep { Id = "compile", Order = 5, Title = "Compile",
			Description = "No Error-severity issue from any IssueEngine rule. Readiness only — the compile itself is another ticket's job." },
		new ChecklistStep { Id = "playtest", Order = 6, Title = "Playtest",
			Description = "Every step above passes: the map is ready for playtest. Readiness only — the playtest run is another ticket's job." },
	};

	/// <summary>Evaluate a map: runs the IssueEngine once and maps the issues
	/// onto the six steps.</summary>
	public static ChecklistResult Evaluate( StarterMap map )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );
		return Evaluate( map, StarterTemplates.Validate( map ) );
	}

	/// <summary>Evaluate a map against a caller-supplied IssueEngine run
	/// (same result as <see cref="Evaluate(StarterMap)"/>, without re-running
	/// the engine — the live path when the editor already ran it).</summary>
	public static ChecklistResult Evaluate( StarterMap map, IReadOnlyList<Issue> issues )
	{
		if ( map is null )
			throw new ArgumentNullException( nameof( map ) );
		if ( issues is null )
			throw new ArgumentNullException( nameof( issues ) );
		var issueList = issues.ToList();

		var sealCodes = Codes( issueList, i =>
			( i.Kind == IssueKind.Leak || i.Kind == IssueKind.UnsealedMap || i.Kind == IssueKind.InvalidBrush )
			&& i.Severity == IssueSeverity.Error );
		var seal = new ChecklistStepResult
		{
			StepId = "seal", Title = "Seal the map",
			Passed = sealCodes.Count == 0,
			Details = sealCodes.Count == 0
				? "Sealed: no leak, unsealed side, or invalid brush."
				: "Not sealed: " + string.Join( ", ", sealCodes ) + ".",
			BlockingIssueCodes = sealCodes,
		};

		var spawns = map.FindEntities( "info_player_start" );
		var spawnInVoid = spawns.Any( s => IsInVoid( map.Document, s.Origin ) );
		var spawn = new ChecklistStepResult
		{
			StepId = "spawn", Title = "Place a player spawn",
			Passed = spawns.Count > 0 && spawnInVoid,
			Details = spawns.Count == 0
				? "No info_player_start in the map."
				: !spawnInVoid
					? $"info_player_start at {spawns[0].Origin} sits inside a solid brush."
					: $"{spawns.Count} info_player_start(s), in the void.",
			BlockingIssueCodes = Array.Empty<string>(),
		};

		var hasLight = map.HasEntity( "light" ) || map.HasEntity( "light_environment" );
		var light = new ChecklistStepResult
		{
			StepId = "light", Title = "Add a light",
			Passed = hasLight,
			Details = hasLight ? "Light present." : "No light or light_environment in the map.",
			BlockingIssueCodes = Array.Empty<string>(),
		};

		var skyName = map.Worldspawn.SkyboxName;
		var needsCamera = map.Skybox.Is3D;
		var hasCamera = map.HasSkyCamera;
		var skybox = new ChecklistStepResult
		{
			StepId = "skybox", Title = "Set the skybox",
			Passed = !string.IsNullOrWhiteSpace( skyName ) && ( !needsCamera || hasCamera ),
			Details = string.IsNullOrWhiteSpace( skyName )
				? "Worldspawn names no skybox."
				: needsCamera && !hasCamera
					? "3D skybox section present but no sky_camera entity."
					: map.Skybox.Is3D
						? $"3D skybox '{skyName}' with sky_camera (16x section)."
						: $"2D skybox '{skyName}'.",
			BlockingIssueCodes = Array.Empty<string>(),
		};

		var errorCodes = Codes( issueList, i => i.Severity == IssueSeverity.Error );
		var compile = new ChecklistStepResult
		{
			StepId = "compile", Title = "Compile",
			Passed = errorCodes.Count == 0,
			Details = errorCodes.Count == 0
				? "No Error-severity issues: ready to compile."
				: "Not ready to compile: " + string.Join( ", ", errorCodes ) + ".",
			BlockingIssueCodes = errorCodes,
		};

		var earlier = new[] { seal, spawn, light, skybox, compile };
		var firstFailing = earlier.FirstOrDefault( s => !s.Passed );
		var playtest = new ChecklistStepResult
		{
			StepId = "playtest", Title = "Playtest",
			Passed = firstFailing is null,
			Details = firstFailing is null
				? "All steps pass: ready for playtest."
				: $"Blocked by '{firstFailing.StepId}': {firstFailing.Details}",
			BlockingIssueCodes = firstFailing?.BlockingIssueCodes ?? Array.Empty<string>(),
		};

		var steps = new[] { seal, spawn, light, skybox, compile, playtest };
		return new ChecklistResult
		{
			Steps = steps,
			AllPassed = steps.All( s => s.Passed ),
			FirstIncompleteStepId = firstFailing?.StepId,
			Issues = issueList,
		};
	}

	static IReadOnlyList<string> Codes( IReadOnlyList<Issue> issues, Func<Issue, bool> match )
	{
		var codes = new List<string>();
		foreach ( var issue in issues )
		{
			if ( issue is null || !match( issue ) )
				continue;
			if ( !codes.Contains( issue.Code ) )
				codes.Add( issue.Code );
		}
		return codes;
	}

	/// <summary>A point is in the void when no solid (non-detail) brush
	/// contains it: for every face, Dot(normal, p) &lt;= distance.</summary>
	static bool IsInVoid( IssueDocument document, Vec3 point )
	{
		if ( document.Brushes is null )
			return true;
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null || brush.IsDetail || brush.Faces is null )
				continue;
			var inside = true;
			foreach ( var face in brush.Faces )
			{
				if ( face is null )
					continue;
				if ( Vec3.Dot( face.Normal.Normal, point ) > face.Distance + 0.01 )
				{
					inside = false;
					break;
				}
			}
			if ( inside )
				return false;
		}
		return true;
	}
}
