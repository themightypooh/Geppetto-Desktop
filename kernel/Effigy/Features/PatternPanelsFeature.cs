using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>2D sewing pattern panels from Linework, roadmap Lane O item 5 (Marvelous-Designer
/// style): closed .linework contours become pattern panels, stitched edge-to-edge and draped
/// onto a 3D wearer. The drape pass is Settle PLUS a one-pass expulsion: the cloth
/// solver only corrects particles near a collider surface, so vertices placed DEEP
/// inside the wearer survive Settle untouched (measured: 4 of 4 stayed inside) —
/// after Settle, any vertex still inside the wearer is pushed out along its
/// nearest-surface direction to the surface + clearance. Without drape, no expulsion
/// runs and InsideVerts is reported honestly for the user to fix by placement.
///
///   add Primitive name=Wearer shape=Sphere radius=8 position=0,0,0
///   add PatternPanels name=Tube linework=tube.linework
///     place=front:off=0,10,0;axis=xz|back:off=0,-10,0;axis=xz|left:off=-10,0,0;axis=yz|right:off=10,0,0;axis=yz
///     seams=auto wearer=Wearer drape=true
///
/// What it reuses, not duplicates: LineworkReader (wave 4) for profiles, PatternStitch
/// (wave 3) for placement + triangulation + seam welding + collider push-out, and
/// GarmentHang.Settle (the drape pass PatternStitch's own docs point at) for draping.
/// What is new here: the .linework → piece bridge (panel naming, layer/panel selection,
/// scale), the seams=auto edge pairing, the wearer + drape wiring, and the clipping
/// evidence (garment vertices inside the wearer, counted with a real containment test).
///
/// HONEST SCOPE. Panels are simple outer rings: a profile WITH hole rings fails loudly,
/// because PatternStitch triangulates simple polygons and silently dropping a hole would
/// sew the wrong garment. Placement is the PatternStitch axis/offset/rotation grammar —
/// there is no automatic body measurement or panel shaping around a specific body; the
/// wearer collider push-out + drape settle is what makes the stitched sheets sit ON the
/// wearer instead of through them. Seams=auto pairs whole edges whose placed endpoints
/// coincide within SeamTol — it does not guess which edges SHOULD be sewn on a real
/// pattern; explicit seams= is the precise path, auto is the coincident-edge path.
/// A 2-panel garment sewn at both side edges is a flat double layer, not a tube
/// (PatternStitch's own documented consequence) — a wearable tube needs side panels,
/// exactly like the diver demo's four-panel tube, which is what the proof builds.</summary>
public sealed class PatternPanelsFeature : Feature
{
	public override string TypeName => "PatternPanels";

	public readonly StringParam Linework = new( "Linework", "" );
	public readonly StringParam Layer = new( "Layer", "" );

	/// <summary>Comma-separated stroke ids to use as panels. Empty = every closed profile.</summary>
	public readonly StringParam Panels = new( "Panels", "" );

	/// <summary>Placement per panel, pipe-separated: name:off=x,y,z;axis=xz|yz|xy;rot=deg.
/// Same grammar as PatternStitchFeature's place=. A panel with no entry sits at the
	/// origin in xz.</summary>
	public readonly StringParam Place = new( "Place", "" );

	/// <summary>Seams, comma-separated: piece:vertex-piece:vertex, or piece:edge-piece:edge
	/// (edge shorthand on 4-point pieces only, same as PatternStitchFeature). The literal
	/// "auto" pairs whole edges whose placed endpoints coincide within SeamTol.</summary>
	public readonly StringParam Seams = new( "Seams", "" );

	public readonly FloatParam SeamTol = new( "Seam tolerance", 0.5f, 0f, 100f, "u" );
	public readonly FloatParam Scale = new( "Scale", 1f, 0.001f, 1000f );

	/// <summary>Body the garment must sit outside of. Empty = free-standing garment (and
	/// then drape=true is refused: draping needs a body to drape onto).</summary>
	public readonly BodySelectionParam Wearer = new( "Wearer" );

	public readonly FloatParam Clearance = new( "Clearance", 0.05f, 0f, 2f, "u" );
	public readonly IntParam Relax = new( "Relax steps", 10, 0, 200 );
	public readonly BoolParam Drape = new( "Drape", false );
	public readonly IntParam DrapeSteps = new( "Drape steps", 60, 0, 500 );
	public readonly FloatParam DrapeStiff = new( "Drape stiffness", 0.8f, 0f, 1f );

	/// <summary>Which vertices the drape pass pins in place: top (highest band, the
	/// default — a garment hangs on its wearer from its top edge, like shoulders), bottom,
	/// or none (free fall: the cloth settles under gravity and slides off an unpinned
	/// form, which is physically honest but means the garment does NOT stay on the wearer).</summary>
	public readonly StringParam Pin = new( "Pin", "top" );

	/// <summary>Script panels, same grammar as PatternStitchFeature: pipe-separated
	/// name:u,v;u,v;... — when given, no .linework drawing is loaded at all. This
	/// is the script-native path the sewn-panel feature was missing.</summary>
	public readonly StringParam Panelscript = new( "Panel script", "" );

	/// <summary>Place script/linework panels automatically around the wearer:
	/// front on +Y, back on -Y, left on -X, right on +X, sleeves on their side,
	/// centred on the wearer and clear of its surface. Explicit place= entries
	/// always win over auto-placement.</summary>
	public readonly BoolParam Autoplace = new( "Auto-place", false );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Linework, Layer, Panels, Place, Seams, SeamTol, Scale, Wearer, Clearance, Relax, Drape, DrapeSteps, DrapeStiff, Pin, Panelscript, Autoplace };

	/// <summary>Panel names the last Execute built, for tests and callers.</summary>
	public List<string> PanelsUsed { get; } = new();

	/// <summary>Seam vertex pairs the last Execute paired (explicit + auto).</summary>
	public int SeamsPaired { get; private set; }

	public int WeldedPairs { get; private set; }
	public int BoundaryEdges { get; private set; }

	/// <summary>Garment vertices found INSIDE the wearer mesh after build (+drape), via
	/// MeshRaycast.PointInsideSolid. -1 when no wearer was given. 0 is the no-clip proof.</summary>
	public int InsideVerts { get; private set; } = -1;

	/// <summary>Vertices the drape pass expelled from inside the wearer (0 without drape).</summary>
	public int ExpelledVerts { get; private set; }

	/// <summary>Smallest distance from any garment vertex to the wearer surface. NaN when
	/// no wearer was given. A distance alone does not prove outside-ness — InsideVerts
	/// is the clipping evidence; this is the fit margin.</summary>
	public float MinClearance { get; private set; } = float.NaN;

	protected override void Execute( FeatureContext ctx )
	{
		PanelsUsed.Clear();
		SeamsPaired = 0;
		WeldedPairs = 0;
		BoundaryEdges = 0;
		InsideVerts = -1;
		ExpelledVerts = 0;
		MinClearance = float.NaN;

		// Script panels (P3) take precedence over linework: when panelscript= is
		// given, no drawing is loaded at all.
		List<PatternStitch.Piece> pieces;
		if ( !string.IsNullOrWhiteSpace( Panelscript.Value ) )
			pieces = ParseScriptPieces();
		else
			pieces = BuildPieces( ResolveProfiles() );
		var placements = ParsePlace( pieces );
		if ( Autoplace.Value && Wearer.BodyIds.Count > 0 )
		{
			var autoFound = ctx.Bodies.Where( b => Wearer.Matches( b ) && b.Mesh is not null ).ToList();
			if ( autoFound.Count == 1 )
				ApplyAutoplace( placements, pieces, autoFound[0].Mesh );
		}
		foreach ( var piece in pieces )
			if ( placements.TryGetValue( piece.Name, out var p ) )
			{
				piece.Axis = p.Axis;
				piece.Offset = p.Offset;
				piece.RotationDeg = p.RotationDeg;
			}
		var seams = ParseSeams( pieces );
		SeamsPaired = seams.Count;

		PolyMesh wearerMesh = null;
		if ( Wearer.BodyIds.Count > 0 )
		{
			var found = ctx.Bodies.Where( b => Wearer.Matches( b ) && b.Mesh is not null ).ToList();
			if ( found.Count == 0 )
				Fail( "No wearer body matches the Wearer selection.",
					"The garment was asked to sit outside a body that is not in the studio.",
					"Clear Wearer for a free-standing garment",
					"Pick a body that is still in the studio" );
			if ( found.Count > 1 )
				Fail( $"PatternPanels takes one wearer body, got {found.Count} ({string.Join( ", ", found.Select( b => b.Name ) )}).",
					"Push-out and drape are measured against a single wearer form.",
					"Sew the garment against the torso, then drape it over the rest" );
			wearerMesh = found[0].Mesh;
		}

		if ( Drape.Value && wearerMesh is null )
			Fail( "PatternPanels drape=true needs a Wearer.",
				"Draping settles cloth onto a body; with no body there is nothing to drape onto.",
				"Give wearer=<body name>, or leave drape off for a free-standing garment" );

		var result = PatternStitch.Build( pieces, seams, new PatternStitch.Options
		{
			Clearance = Clearance.Clamped,
			RelaxSteps = Relax.Clamped,
			Collider = wearerMesh,
		} );
		if ( !result.Ok )
			Fail( $"PatternPanels stitch failed: {result.Reason}",
				"Check the panel shapes, the seam names, and that seams join edges, not whole pieces.",
				"Use panels= to drop a bad panel, or seams=auto with a larger seamtol" );

		var mesh = result.Mesh;
		if ( Drape.Value )
		{
			GarmentHang.Settle( mesh, new[] { wearerMesh }, ResolvePins( mesh ), new GarmentHang.SettleOptions
			{
				Steps = DrapeSteps.Clamped,
				Clearance = Clearance.Clamped,
				Stiffness = DrapeStiff.Clamped,
				Tether = 0f,
			} );
			ExpelledVerts = ExpelInside( mesh, wearerMesh, Clearance.Clamped );
		}

		WeldedPairs = result.WeldedPairs;
		BoundaryEdges = result.BoundaryEdges;
		if ( wearerMesh is not null )
			MeasureFit( mesh, wearerMesh );

		var body = new Body( ctx.NewBodyId(), Name, mesh ) { IsGarment = true };
		ctx.Bodies.Add( body );
		PanelsUsed.AddRange( pieces.Select( p => p.Name ) );
	}

	List<int> ResolvePins( PolyMesh mesh )
	{
		var pin = (Pin.Value ?? "top").Trim().ToLowerInvariant();
		if ( pin is "none" or "" )
			return new List<int>();
		var minZ = mesh.Positions.Min( p => p.z );
		var maxZ = mesh.Positions.Max( p => p.z );
		var band = 0.14f * (maxZ - minZ);
		var pinned = new List<int>();
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			if ( pin == "top" && mesh.Positions[i].z >= maxZ - band )
				pinned.Add( i );
			else if ( pin == "bottom" && mesh.Positions[i].z <= minZ + band )
				pinned.Add( i );
		}
		if ( pin is not ("top" or "bottom") )
			Fail( $"Pin '{Pin.Value}' is not top, bottom, or none.",
				"Pin says which vertices stay put while the rest drapes.",
				"Write pin=top, pin=bottom, or pin=none" );
		return pinned;
	}

	/// <summary>One pass: push every vertex still inside the wearer out to its nearest
	/// surface point + clearance. Returns how many were expelled. This is the step the
	/// cloth solver lacks (its correction only reaches particles near the surface), so
	/// without it a badly placed panel stays embedded in the wearer after draping.</summary>
	int ExpelInside( PolyMesh garment, PolyMesh wearer, float clearance )
	{
		var bvh = MeshBVH.Build( wearer );
		var expelled = 0;
		for ( var i = 0; i < garment.Positions.Count; i++ )
		{
			var v = garment.Positions[i];
			if ( !MeshRaycast.PointInsideSolid( wearer, v ) )
				continue;
			var hit = bvh.NearestSurface( wearer, v, 1e6f );
			if ( hit is not { } h )
				continue;
			// Outward for an INSIDE vertex is surface-minus-vertex, not the reverse:
			// v - h.Point points from the surface back toward the centre, i.e. deeper
			// inside. (PatternStitch's push-out uses v - hit because its vertices start
			// OUTSIDE near the surface, where that direction is outward. Here the vertex
			// starts inside, so the direction flips.)
			var outward = h.Point - v;
			var dir = outward.Length > 1e-9f ? outward.Normal : h.Normal;
			garment.Positions[i] = h.Point + dir * MathF.Max( clearance, 1e-3f );
			expelled++;
		}
		return expelled;
	}

	void MeasureFit( PolyMesh garment, PolyMesh wearer )
	{
		var inside = 0;
		foreach ( var v in garment.Positions )
			if ( MeshRaycast.PointInsideSolid( wearer, v ) )
				inside++;
		InsideVerts = inside;

		var bvh = MeshBVH.Build( wearer );
		var min = float.MaxValue;
		foreach ( var v in garment.Positions )
		{
			var hit = bvh.NearestSurface( wearer, v, 1e6f );
			if ( hit is { } h && h.Distance < min )
				min = h.Distance;
		}
		MinClearance = min == float.MaxValue ? float.NaN : min;
	}

	List<LineworkProfile> ResolveProfiles()
	{
		if ( string.IsNullOrWhiteSpace( Linework.Value ) )
			Fail( "PatternPanels needs a linework= drawing.",
				"Panels come from closed .linework strokes; no drawing was given.",
				"Give linework=<path or drawing id> from ~/.local/share/linework/drawings/" );
		if ( !LineworkReader.TryLoad( Linework.Value, out var doc, out _, out var lerr ) )
			Fail( $"PatternPanels cannot load its drawing: {lerr}",
				"The path or drawing id did not resolve to a .linework file.",
				"Check the path, or copy the drawing into ~/.local/share/linework/drawings/" );
		var layer = Layer.Value.Trim();
		if ( layer.Length > 0 && !doc.Layers.Any( l => l.Name.Equals( layer, StringComparison.OrdinalIgnoreCase ) || l.Id.Equals( layer, StringComparison.OrdinalIgnoreCase ) ) )
			Fail( $"PatternPanels found no layer '{layer}'.",
				$"Layers in this drawing: {string.Join( ", ", doc.Layers.Select( l => l.Name ) )}.",
				"Give layer= with one of those names, or leave layer= empty for all visible layers" );
		var profiles = LineworkReader.Profiles( doc, layer );
		var wanted = (Panels.Value ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( wanted.Length > 0 )
		{
			var byId = profiles.GroupBy( p => p.StrokeId, StringComparer.OrdinalIgnoreCase )
				.ToDictionary( g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase );
			var kept = new List<LineworkProfile>();
			foreach ( var id in wanted )
				if ( !byId.TryGetValue( id, out var p ) )
					Fail( $"PatternPanels found no panel '{id}'.",
						$"Panels in this drawing: {string.Join( ", ", profiles.Select( p => p.StrokeId ) )}.",
						"Give panels= with one of those stroke ids, or leave panels= empty for all of them" );
				else
					kept.Add( p );
			profiles = kept;
		}
		if ( profiles.Count == 0 )
			Fail( "PatternPanels found no closed profile to sew.",
				"Linework panels are closed strokes; open strokes are drawing lines, not shapes.",
				"Close the shape in Linework (closed=true)" );
		return profiles;
	}

	List<PatternStitch.Piece> BuildPieces( List<LineworkProfile> profiles )
	{
		var scale = Scale.Clamped;
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var pieces = new List<PatternStitch.Piece>();
		for ( var i = 0; i < profiles.Count; i++ )
		{
			var profile = profiles[i];
			if ( profile.Holes.Count > 0 )
				Fail( $"Panel '{profile.StrokeId}' has {profile.Holes.Count} hole ring(s), which PatternPanels cannot sew.",
					"PatternStitch triangulates simple polygons; a hole ring has no seam in that triangulation.",
					"Drop the panel with panels=, or redraw it without holes (a dart or pocket is a separate panel)" );
			var name = string.IsNullOrWhiteSpace( profile.StrokeId )
				? $"{profile.Layer}_{i}"
				: profile.StrokeId.Trim();
			var baseName = name;
			var n = 2;
			while ( !used.Add( name ) )
				name = $"{baseName}#{n++}";
			pieces.Add( new PatternStitch.Piece
			{
				Name = name,
				Points = profile.Outer.Select( p => p * scale ).ToList(),
			} );
		}
		return pieces;
	}

	List<PatternStitch.Piece> ParseScriptPieces()
	{
		var pieces = new List<PatternStitch.Piece>();
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in (Panelscript.Value ?? "").Split( '|' ) )
		{
			var colon = entry.IndexOf( ':' );
			if ( colon <= 0 )
				Fail( $"Panel script entry '{entry}' has no name:points split.",
					"Each script panel is name, colon, then u,v points separated by semicolons.",
					"Write it like front:0,0;20,0;20,24;0,24|back:0,0;20,0;20,24;0,24" );
			var name = entry[..colon].Trim();
			var pts = new List<Vec2>();
			foreach ( var pair in entry[(colon + 1)..].Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			{
				var xy = pair.Split( ',', StringSplitOptions.TrimEntries );
				float x = 0f, y = 0f;
				if ( xy.Length != 2 || !float.TryParse( xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x )
					|| !float.TryParse( xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y ) )
					Fail( $"Panel script point '{pair}' in panel '{name}' is not u,v.",
					"Points are plain numbers, comma-separated, semicolon-separated.",
					"Write it like front:0,0;20,0;20,24;0,24" );
				pts.Add( new Vec2( x, y ) );
			}
			if ( pts.Count < 3 )
				Fail( $"Panel script panel '{name}' has {pts.Count} point(s).",
				"A panel needs at least a triangle to sew.",
				"Give at least three u,v points for the panel" );
			var baseName = name;
			var n = 2;
			while ( !used.Add( name ) )
				name = $"{baseName}#{n++}";
			pieces.Add( new PatternStitch.Piece { Name = name, Points = pts } );
		}
		if ( pieces.Count == 0 )
			Fail( "Panel script gave no panels.",
				"The panelscript= value was empty or only separators.",
				"Write panelscript=front:0,0;20,0;20,24;0,24" );
		return pieces;
	}

	void ApplyAutoplace( Dictionary<string, Placement> placements, List<PatternStitch.Piece> pieces, PolyMesh wearer )
	{
		var minX = wearer.Positions.Min( p => p.x ); var maxX = wearer.Positions.Max( p => p.x );
		var minY = wearer.Positions.Min( p => p.y ); var maxY = wearer.Positions.Max( p => p.y );
		var minZ = wearer.Positions.Min( p => p.z ); var maxZ = wearer.Positions.Max( p => p.z );
		var cx = (minX + maxX) * 0.5f; var cy = (minY + maxY) * 0.5f; var cz = (minZ + maxZ) * 0.5f;
		var halfW = (maxX - minX) * 0.5f; var halfD = (maxY - minY) * 0.5f;
		var clear = Clearance.Clamped;
		foreach ( var piece in pieces )
		{
			if ( placements.ContainsKey( piece.Name ) )
				continue;
			var name = piece.Name.ToLowerInvariant();
			var minU = piece.Points.Min( p => p.x ); var maxU = piece.Points.Max( p => p.x );
			var minV = piece.Points.Min( p => p.y ); var maxV = piece.Points.Max( p => p.y );
			var cu = (minU + maxU) * 0.5f; var cv = (minV + maxV) * 0.5f;
			if ( name.Contains( "front" ) )
				placements[piece.Name] = new Placement( "xz", new Vec3( cx - cu, cy + halfD + clear, cz - cv ), 0f );
			else if ( name.Contains( "back" ) )
				placements[piece.Name] = new Placement( "xz", new Vec3( cx - cu, cy - halfD - clear, cz - cv ), 0f );
			else if ( name.Contains( "left" ) )
				placements[piece.Name] = new Placement( "yz", new Vec3( cx - halfW - clear, cy - cu, cz - cv ), 0f );
			else if ( name.Contains( "right" ) )
				placements[piece.Name] = new Placement( "yz", new Vec3( cx + halfW + clear, cy - cu, cz - cv ), 0f );
			else
				placements[piece.Name] = new Placement( "xz", new Vec3( cx - cu, cy, cz - cv ), 0f );
		}
	}

	record Placement( string Axis, Vec3 Offset, float RotationDeg );

	Dictionary<string, Placement> ParsePlace( List<PatternStitch.Piece> pieces )
	{
		var known = new HashSet<string>( pieces.Select( p => p.Name ), StringComparer.OrdinalIgnoreCase );
		var placements = new Dictionary<string, Placement>( StringComparer.OrdinalIgnoreCase );
		var raw = (Place.Value ?? "").Trim();
		if ( raw.Length == 0 )
			return placements;
		foreach ( var entry in raw.Split( '|' ) )
		{
			var colon = entry.IndexOf( ':' );
			if ( colon <= 0 )
				Fail( $"Placement '{entry}' has no name:fields split.",
					"Each placement is name, colon, then off/axis/rot fields.",
					"Write it like front:off=0,10,0;axis=xz;rot=0" );
			var name = entry[..colon].Trim();
			if ( !known.Contains( name ) )
				Fail( $"Placement names panel '{name}', which is not in this drawing.",
					"Place only places panels the drawing defines.",
					$"Place one of: {string.Join( ", ", pieces.Select( p => p.Name ) )}" );
			var axis = "xz";
			var offset = Vec3.Zero;
			var rot = 0f;
			foreach ( var field in entry[(colon + 1)..].Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
			{
				var eq = field.IndexOf( '=' );
				if ( eq <= 0 )
					Fail( $"Placement field '{field}' for '{name}' is not key=value.",
						"Placement fields are off, axis, and rot.",
						"Write them like off=0,10,0;axis=xz;rot=0" );
				var key = field[..eq].Trim().ToLowerInvariant();
				var value = field[(eq + 1)..].Trim();
				if ( key == "axis" )
				{
					if ( value is not ("xz" or "yz" or "xy") )
						Fail( $"Axis '{value}' for '{name}' is not xz, yz, or xy.",
							"Axis says which plane the pattern lies in.",
							"Use axis=xz for a torso panel, axis=yz for a side panel" );
					axis = value;
				}
				else if ( key == "off" )
				{
					var parts = value.Split( ',' );
					float ox = 0f, oy = 0f, oz = 0f;
					if ( parts.Length != 3
						|| !float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ox )
						|| !float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out oy )
						|| !float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out oz ) )
						Fail( $"Offset '{value}' for '{name}' is not x,y,z.",
							"Offset is a 3D translation applied after the axis mapping.",
							"Write it like off=0,10,0" );
					offset = new Vec3( ox, oy, oz );
				}
				else if ( key == "rot" )
				{
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out rot ) )
						Fail( $"Rotation '{value}' for '{name}' is not a number.",
							"Rot is the in-plane rotation in degrees.",
							"Write it like rot=90" );
				}
				else
					Fail( $"Placement field '{key}' for '{name}' is not off, axis, or rot.",
						"Placement only takes those three fields.",
						"Write them like off=0,10,0;axis=xz;rot=0" );
			}
			placements[name] = new Placement( axis, offset, rot );
		}
		return placements;
	}

	List<PatternStitch.SeamPair> ParseSeams( List<PatternStitch.Piece> pieces )
	{
		var raw = (Seams.Value ?? "").Trim();
		if ( raw.Length == 0 )
			return new List<PatternStitch.SeamPair>();
		if ( raw.Equals( "auto", StringComparison.OrdinalIgnoreCase ) )
			return AutoPair( pieces );
		var byName = pieces.ToDictionary( p => p.Name, StringComparer.OrdinalIgnoreCase );
		var seams = new List<PatternStitch.SeamPair>();
		foreach ( var token in raw.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var dash = token.IndexOf( '-' );
			if ( dash <= 0 )
				Fail( $"Seam '{token}' does not join two sides.",
					"A seam is piece:vertex-piece:vertex, or piece:edge-piece:edge.",
					"Write it like front:0-back:3, or front:left-back:left" );
			var left = ParseSide( token[..dash].Trim(), byName );
			var right = ParseSide( token[(dash + 1)..].Trim(), byName );
			if ( left.Count != right.Count )
				Fail( $"Seam '{token}' joins {left.Count} vertices to {right.Count}.",
					"An edge seam joins the same number of vertices on both sides.",
					"Join vertex to vertex, or edge to edge on 4-point pieces" );
			if ( left.Count == 1 )
			{
				seams.Add( new PatternStitch.SeamPair { A = left[0].Piece, Ai = left[0].Index, B = right[0].Piece, Bi = right[0].Index } );
				continue;
			}
			// Edge-to-edge: pair by nearest placed position (greedy, each vertex used
			// once) — the same pairing PatternStitchFeature uses, because two panels'
			// shared edge is traversed in opposite directions and pairing in order
			// would weld vertices far apart.
			var used = new HashSet<int>();
			var candidates = new List<(float Dist, int L, int R)>();
			for ( var li = 0; li < left.Count; li++ )
				for ( var ri = 0; ri < right.Count; ri++ )
					candidates.Add( ((PlacedPosition( byName[left[li].Piece], left[li].Index )
						- PlacedPosition( byName[right[ri].Piece], right[ri].Index )).Length, li, ri) );
			candidates.Sort( (a, b) => a.Dist.CompareTo( b.Dist ) );
			foreach ( var (dist, li, ri) in candidates )
			{
				if ( used.Contains( ri ) || seams.Any( pr => pr.A == left[li].Piece && pr.Ai == left[li].Index ) )
					continue;
				seams.Add( new PatternStitch.SeamPair { A = left[li].Piece, Ai = left[li].Index, B = right[ri].Piece, Bi = right[ri].Index } );
				used.Add( ri );
				if ( used.Count == right.Count )
					break;
			}
		}
		return seams;
	}

	/// <summary>Auto seam pairing: for every panel pair, every edge pair, if both
	/// endpoints coincide (direct or reversed) within SeamTol, both endpoint pairs
	/// become seam welds. Whole edges only — stray near vertices never weld alone.</summary>
	List<PatternStitch.SeamPair> AutoPair( List<PatternStitch.Piece> pieces )
	{
		var tol = SeamTol.Clamped;
		var seams = new List<PatternStitch.SeamPair>();
		var seen = new HashSet<(string, int, string, int)>();
		void Add( PatternStitch.Piece a, int ai, PatternStitch.Piece b, int bi )
		{
			var key = (a.Name, ai, b.Name, bi);
			var rev = (b.Name, bi, a.Name, ai);
			if ( seen.Contains( key ) || seen.Contains( rev ) )
				return;
			seen.Add( key );
			seams.Add( new PatternStitch.SeamPair { A = a.Name, Ai = ai, B = b.Name, Bi = bi } );
		}
		for ( var pi = 0; pi < pieces.Count; pi++ )
			for ( var pj = pi + 1; pj < pieces.Count; pj++ )
			{
				var a = pieces[pi];
				var b = pieces[pj];
				for ( var ei = 0; ei < a.Points.Count; ei++ )
				{
					var a0 = PlacedPosition( a, ei );
					var a1 = PlacedPosition( a, (ei + 1) % a.Points.Count );
					for ( var ej = 0; ej < b.Points.Count; ej++ )
					{
						var b0 = PlacedPosition( b, ej );
						var b1 = PlacedPosition( b, (ej + 1) % b.Points.Count );
						var direct = (a0 - b0).Length <= tol && (a1 - b1).Length <= tol;
						var reversed = (a0 - b1).Length <= tol && (a1 - b0).Length <= tol;
						if ( direct )
						{
							Add( a, ei, b, ej );
							Add( a, (ei + 1) % a.Points.Count, b, (ej + 1) % b.Points.Count );
						}
						else if ( reversed )
						{
							Add( a, ei, b, (ej + 1) % b.Points.Count );
							Add( a, (ei + 1) % a.Points.Count, b, ej );
						}
					}
				}
			}
		return seams;
	}

	/// <summary>Where a pattern vertex lands in 3D. Must match PatternStitch's placement
	/// exactly: centre the piece, rotate in-plane, map by axis, add the offset.</summary>
	static Vec3 PlacedPosition( PatternStitch.Piece piece, int index )
	{
		var centre = Vec2.Zero;
		foreach ( var p in piece.Points )
			centre += p;
		centre /= piece.Points.Count;
		var point = piece.Points[index];
		var lx = point.x - centre.x;
		var ly = point.y - centre.y;
		var rot = piece.RotationDeg * MathF.PI / 180f;
		var rx = lx * MathF.Cos( rot ) - ly * MathF.Sin( rot );
		var ry = lx * MathF.Sin( rot ) + ly * MathF.Cos( rot );
		return piece.Axis switch
		{
			"yz" => new Vec3( 0f, rx, ry ) + piece.Offset,
			"xy" => new Vec3( rx, ry, 0f ) + piece.Offset,
			_ => new Vec3( rx, 0f, ry ) + piece.Offset,
		};
	}

	List<(string Piece, int Index)> ParseSide( string side, Dictionary<string, PatternStitch.Piece> byName )
	{
		var colon = side.IndexOf( ':' );
		if ( colon <= 0 )
			Fail( $"Seam side '{side}' is not piece:vertex or piece:edge.",
				"A seam side names a piece and a vertex index or an edge.",
				"Write it like front:0, or front:left" );
		var name = side[..colon].Trim();
		var spec = side[(colon + 1)..].Trim();
		if ( !byName.TryGetValue( name, out var piece ) )
			Fail( $"Seam names panel '{name}', which is not in this drawing.",
				"Seams only join panels the drawing defines.",
				$"Join one of: {string.Join( ", ", byName.Keys )}" );
		if ( int.TryParse( spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index ) )
		{
			if ( index < 0 || index >= piece.Points.Count )
				Fail( $"Seam vertex {index} is outside panel '{name}' ({piece.Points.Count} points).",
					"Vertex indices run 0 to points-1 in the order the points were written.",
					$"Use 0..{piece.Points.Count - 1} for '{name}'" );
			return new List<(string, int)> { (name, index) };
		}
		if ( piece.Points.Count != 4 )
			Fail( $"Edge seam '{spec}' on '{name}' needs a 4-point panel, got {piece.Points.Count}.",
				"Edge names only make sense on a quadrilateral panel.",
				"Join vertices by index instead, e.g. front:0-back:3" );
		var edge = spec.ToLowerInvariant() switch
		{
			"bottom" => new[] { 0, 1 },
			"right" => new[] { 1, 2 },
			"top" => new[] { 2, 3 },
			"left" => new[] { 3, 0 },
			_ => null,
		};
		if ( edge is null )
			Fail( $"Edge '{spec}' on '{name}' is not bottom, right, top, or left.",
				"Edge seams name one side of a 4-point panel.",
				"Use bottom, right, top, left — or a vertex index" );
		return edge.Select( i => (name, i) ).ToList();
	}
}
