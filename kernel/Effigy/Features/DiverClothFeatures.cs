using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Diver cloth, wave 3: the two pieces the full Deep-Sea Diver was blocked on.
///
/// PressureBalloon inflates a closed mesh under pressure (a suit bladder, a helmet
/// bubble). PatternStitch sews flat 2D pattern pieces into a 3D garment (a suit torso
/// from front/back/side panels). Neither duplicates GarmentFeature (a fitted recipe)
/// or DrapeFeature (gravity settle): the balloon is pressure, the stitch is pattern
/// construction, and both build on the solvers in ClothInflate.cs / PatternStitch.cs.
///
/// Hair and skin SSS are separate wave-3 pieces; live in-game cloth testing is a
/// separate queued task and is NOT attempted here.
/// </summary>
public sealed class PressureBalloonFeature : Feature
{
	public override string TypeName => "PressureBalloon";

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>Fraction of rest volume to add: 0.5 = 50% bigger. -0.9..2.</summary>
	public readonly FloatParam Pressure = new( "Pressure", 0.5f, -0.9f, 2f );

	public readonly IntParam Steps = new( "Steps", 40, 1, 500 );
	public readonly FloatParam Stiffness = new( "Stiffness", 0.8f, 0f, 1f );

	/// <summary>Which vertices never move: none, top (highest band), bottom (lowest band).</summary>
	public readonly StringParam Pin = new( "Pin", "none" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Pressure, Steps, Stiffness, Pin };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );

		foreach ( var body in bodies )
		{
			if ( body.Mesh is null || body.Mesh.VertexCount == 0 )
			{
				Fail( $"Body '{body.Name}' has no mesh to inflate.",
					"PressureBalloon works on a closed solid, not an empty body.",
					"Give it a Primitive sphere, cylinder, or another closed mesh" );
			}

			var pins = ResolvePins( body.Mesh );
			var result = PressureBalloon.Inflate( body.Mesh, new PressureBalloon.Options
			{
				Pressure = Pressure.Clamped,
				Steps = Steps.Clamped,
				Stiffness = Stiffness.Clamped,
				Pins = pins,
			} );

			if ( !result.Ok )
			{
				Fail( $"PressureBalloon failed on '{body.Name}': {result.Reason}",
					"Inflation needs a sealed volume it can push outward.",
					"Use a closed Primitive (Sphere, Cylinder, Box) as the starting mesh",
					"Close any open edges first — a sheet has no inside to pressurise" );
			}

			body.Mesh = result.Mesh;
		}
	}

	List<int> ResolvePins( PolyMesh mesh )
	{
		var pin = (Pin.Value ?? "none").Trim().ToLowerInvariant();

		if ( pin is "none" or "" )
			return new List<int>();

		var minZ = mesh.Positions.Min( p => p.z );
		var maxZ = mesh.Positions.Max( p => p.z );
		var band = 0.14f * (maxZ - minZ);
		var pinned = new List<int>();

		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			if ( pin == "top" && mesh.Positions[i].z >= maxZ - band )
				pinned.Add( i );
			else if ( pin == "bottom" && mesh.Positions[i].z <= minZ + band )
				pinned.Add( i );
		}

		if ( pin is not ("top" or "bottom") )
		{
			Fail( $"Pin '{Pin.Value}' is not none, top, or bottom.",
				"Pin says which vertices stay put while the rest inflates.",
				"Write pin=none, pin=top, or pin=bottom" );
		}

		return pinned;
	}
}

public sealed class PatternStitchFeature : Feature
{
	public override string TypeName => "PatternStitch";

	/// <summary>
	/// Pattern pieces, pipe-separated. Each piece: name:u,v;u,v;... — e.g.
	/// front:0,0;20,0;20,24;0,24|back:0,0;20,0;20,24;0,24
	/// </summary>
	public readonly StringParam Pieces = new( "Pieces", "" );

	/// <summary>
	/// Seams, comma-separated. Each seam joins two piece vertices (welded into one):
	/// front:0-back:3 — or a whole edge of a 4-point piece: front:left-back:left.
	/// Edges of a CCW piece starting bottom-left: bottom = 0-1, right = 1-2, top = 2-3, left = 3-0.
	/// Edge-to-edge seams pair vertices by NEAREST PLACED POSITION, not by list order:
	/// two panels' shared edge is traversed in opposite directions, so pairing in order
	/// would weld vertices far apart. Index-to-index seams pair exactly as written.
	/// </summary>
	public readonly StringParam Seams = new( "Seams", "" );

	/// <summary>
	/// Placement per piece, pipe-separated: name:off=x,y,z;axis=xz|yz|xy;rot=deg — e.g.
	/// front:off=0,5,0;axis=xz|back:off=0,-5,0;axis=xz. A piece with no entry sits at the origin in xz.
	/// </summary>
	public readonly StringParam Place = new( "Place", "" );

	/// <summary>Bodies the garment must sit outside of. Empty = free-standing garment.</summary>
	public readonly BodySelectionParam Colliders = new( "Colliders" );

	public readonly FloatParam Clearance = new( "Clearance", 0.05f, 0f, 2f, unit: "u" );
	public readonly IntParam Relax = new( "Relax steps", 10, 0, 200 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Pieces, Seams, Place, Colliders, Clearance, Relax };

	protected override void Execute( FeatureContext ctx )
	{
		var pieces = ParsePieces();
		var placements = ParsePlace( pieces );
		var seams = ParseSeams( pieces );

		foreach ( var piece in pieces )
		{
			if ( placements.TryGetValue( piece.Name, out var placement ) )
			{
				piece.Axis = placement.Axis;
				piece.Offset = placement.Offset;
				piece.RotationDeg = placement.RotationDeg;
			}
		}

		PolyMesh collider = null;

		if ( Colliders.BodyIds.Count > 0 )
		{
			// Colliders is a selection, not a requirement: a named body that is gone
			// must fail honestly rather than silently sewing a free-standing garment.
			var found = ctx.Bodies.Where( b => Colliders.Matches( b ) && b.Mesh is not null ).ToList();

			if ( found.Count == 0 )
			{
				Fail( "No collider body matches the Colliders selection.",
					"The garment was asked to sit outside a body that is not in the studio.",
					"Clear Colliders for a free-standing garment",
					"Pick a body that is still in the studio" );
			}

			// One collider: the wearer form. A second collider is a second body to sit
			// outside of, which this pass does not do — say so, don't silently drop it.
			if ( found.Count > 1 )
			{
				Fail( $"PatternStitch takes one collider body, got {found.Count} ({string.Join( ", ", found.Select( b => b.Name ) )}).",
					"Push-out is measured against a single wearer form.",
					"Sew the garment against the torso, then drape it over the rest" );
			}

			collider = found[0].Mesh;
		}

		var result = PatternStitch.Build( pieces, seams, new PatternStitch.Options
		{
			Clearance = Clearance.Clamped,
			RelaxSteps = Relax.Clamped,
			Collider = collider,
		} );

		if ( !result.Ok )
		{
			Fail( $"PatternStitch failed: {result.Reason}",
				"Check the piece points, the seam names, and that seams join edges, not whole pieces.",
				"Write pieces as name:u,v;u,v;... separated by |",
				"Write seams as piece:index-piece:index, or piece:edge-piece:edge" );
		}

		var body = new Body( ctx.NewBodyId(), Name, result.Mesh ) { IsGarment = true };
		ctx.Bodies.Add( body );
	}

	List<PatternStitch.Piece> ParsePieces()
	{
		var raw = (Pieces.Value ?? "").Trim();

		if ( raw.Length == 0 )
		{
			Fail( "PatternStitch has no Pieces.",
				"A garment is sewn from pattern pieces, and none were given.",
				"Write pieces as name:u,v;u,v;... separated by |, e.g. front:0,0;20,0;20,24;0,24" );
		}

		var pieces = new List<PatternStitch.Piece>();

		foreach ( var entry in raw.Split( '|' ) )
		{
			var colon = entry.IndexOf( ':' );

			if ( colon <= 0 )
			{
				Fail( $"Piece '{entry}' has no name:points split.",
					"Each piece is name, colon, then its 2D points.",
					"Write it like front:0,0;20,0;20,24;0,24" );
			}

			var name = entry[..colon].Trim();
			var points = new List<Vec2>();

			foreach ( var point in entry[(colon + 1)..].Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
			{
				var xy = point.Split( ',' );
				float x = 0f, y = 0f;

				if ( xy.Length != 2
					|| !float.TryParse( xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x )
					|| !float.TryParse( xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y ) )
				{
					Fail( $"Point '{point}' in piece '{name}' is not u,v.",
						"Pattern points are two numbers separated by a comma.",
						"Write them like 0,0;20,0;20,24;0,24" );
				}

				points.Add( new Vec2( x, y ) );
			}

			pieces.Add( new PatternStitch.Piece { Name = name, Points = points } );
		}

		return pieces;
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
			{
				Fail( $"Placement '{entry}' has no name:fields split.",
					"Each placement is name, colon, then off/axis/rot fields.",
					"Write it like front:off=0,5,0;axis=xz;rot=0" );
			}

			var name = entry[..colon].Trim();

			if ( !known.Contains( name ) )
			{
				Fail( $"Placement names piece '{name}', which is not in Pieces.",
					"Place only places pieces the pattern defines.",
					$"Place one of: {string.Join( ", ", pieces.Select( p => p.Name ) )}" );
			}

			var axis = "xz";
			var offset = Vec3.Zero;
			var rot = 0f;

			foreach ( var field in entry[(colon + 1)..].Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
			{
				var eq = field.IndexOf( '=' );

				if ( eq <= 0 )
				{
					Fail( $"Placement field '{field}' for '{name}' is not key=value.",
						"Placement fields are off, axis, and rot.",
						"Write them like off=0,5,0;axis=xz;rot=0" );
				}

				var key = field[..eq].Trim().ToLowerInvariant();
				var value = field[(eq + 1)..].Trim();

				if ( key == "axis" )
				{
					if ( value is not ("xz" or "yz" or "xy") )
					{
						Fail( $"Axis '{value}' for '{name}' is not xz, yz, or xy.",
							"Axis says which plane the pattern lies in.",
							"Use axis=xz for a torso panel, axis=yz for a side panel" );
					}

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
					{
						Fail( $"Offset '{value}' for '{name}' is not x,y,z.",
							"Offset is a 3D translation applied after the axis mapping.",
							"Write it like off=0,5,0" );
					}

					offset = new Vec3( ox, oy, oz );
				}
				else if ( key == "rot" )
				{
					if ( !float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out rot ) )
					{
						Fail( $"Rotation '{value}' for '{name}' is not a number.",
							"Rot is the in-plane rotation in degrees.",
							"Write it like rot=90" );
					}
				}
				else
				{
					Fail( $"Placement field '{key}' for '{name}' is not off, axis, or rot.",
						"Placement only takes those three fields.",
						"Write it like front:off=0,5,0;axis=xz;rot=0" );
				}
			}

			placements[name] = new Placement( axis, offset, rot );
		}

		return placements;
	}

	List<PatternStitch.SeamPair> ParseSeams( List<PatternStitch.Piece> pieces )
	{
		var byName = pieces.ToDictionary( p => p.Name, StringComparer.OrdinalIgnoreCase );
		var seams = new List<PatternStitch.SeamPair>();
		var raw = (Seams.Value ?? "").Trim();

		if ( raw.Length == 0 )
			return seams;

		foreach ( var token in raw.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var dash = token.IndexOf( '-' );

			if ( dash <= 0 )
			{
				Fail( $"Seam '{token}' does not join two sides.",
					"A seam is piece:vertex-piece:vertex, or piece:edge-piece:edge.",
					"Write it like front:0-back:3, or front:left-back:left" );
			}

			var left = ParseSide( token[..dash].Trim(), byName );
			var right = ParseSide( token[(dash + 1)..].Trim(), byName );

			if ( left.Count != right.Count )
			{
				Fail( $"Seam '{token}' joins {left.Count} vertices to {right.Count}.",
					"An edge seam joins the same number of vertices on both sides.",
					"Join vertex to vertex, or edge to edge on 4-point pieces" );
			}

			if ( left.Count == 1 )
			{
				seams.Add( new PatternStitch.SeamPair { A = left[0].Piece, Ai = left[0].Index, B = right[0].Piece, Bi = right[0].Index } );
				continue;
			}

			// Edge-to-edge: pair by nearest placed position (greedy, each vertex used
			// once). Placed positions mirror PatternStitch's own placement exactly:
			// centre, in-plane rotation, axis mapping, offset.
			var used = new HashSet<int>();
			var candidates = new List<(float Dist, int L, int R)>();

			for ( var li = 0; li < left.Count; li++ )
			{
				for ( var ri = 0; ri < right.Count; ri++ )
				{
					var dist = (PlacedPosition( byName[left[li].Piece], left[li].Index )
						- PlacedPosition( byName[right[ri].Piece], right[ri].Index )).Length;
					candidates.Add( (dist, li, ri) );
				}
			}

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

	/// <summary>Where a pattern vertex lands in 3D. Must match PatternStitch's placement:
	/// centre the piece, rotate in-plane, map by axis, add the offset.</summary>
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
		{
			Fail( $"Seam side '{side}' is not piece:vertex or piece:edge.",
				"A seam side names a piece and a vertex index or an edge.",
				"Write it like front:0, or front:left" );
		}

		var name = side[..colon].Trim();
		var spec = side[(colon + 1)..].Trim();

		if ( !byName.TryGetValue( name, out var piece ) )
		{
			Fail( $"Seam names piece '{name}', which is not in Pieces.",
				"Seams only join pieces the pattern defines.",
				$"Join one of: {string.Join( ", ", byName.Keys )}" );
		}

		if ( int.TryParse( spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index ) )
		{
			if ( index < 0 || index >= piece.Points.Count )
			{
				Fail( $"Seam vertex {index} is outside piece '{name}' ({piece.Points.Count} points).",
					"Vertex indices run 0 to points-1 in the order the points were written.",
					$"Use 0..{piece.Points.Count - 1} for '{name}'" );
			}

			return new List<(string, int)> { (name, index) };
		}

		// Edge shorthand, only for 4-point pieces written CCW from bottom-left:
		// bottom 0-1, right 1-2, top 2-3, left 3-0.
		if ( piece.Points.Count != 4 )
		{
			Fail( $"Edge seam '{spec}' on '{name}' needs a 4-point piece, got {piece.Points.Count}.",
				"Edge names only make sense on a quadrilateral piece.",
				"Join vertices by index instead, e.g. front:0-back:3" );
		}

		var edge = spec.ToLowerInvariant() switch
		{
			"bottom" => new[] { 0, 1 },
			"right" => new[] { 1, 2 },
			"top" => new[] { 2, 3 },
			"left" => new[] { 3, 0 },
			_ => null,
		};

		if ( edge is null )
		{
			Fail( $"Edge '{spec}' on '{name}' is not bottom, right, top, or left.",
				"Edge seams name one side of a 4-point piece.",
				"Use bottom, right, top, left — or a vertex index" );
		}

		return edge.Select( i => (name, i) ).ToList();
	}
}
