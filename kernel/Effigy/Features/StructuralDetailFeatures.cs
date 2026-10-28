using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Ring math shared by the structural details that wrap a body part: collars round a neck,
/// cuffs round a wrist or ankle. The garment's own <see cref="DetailSurface.Ring"/> drops an
/// angle outright when its ray finds nothing, which unpairs the lower and upper rings a band
/// is built from; this one returns exactly <paramref name="count"/> points, matched by angle,
/// with gaps interpolated from their neighbours.
/// </summary>
public static class StructuralRings
{
	/// <summary>
	/// The centre and axis of the body part under a spot frame. The frame sits on the cloth at
	/// the front of the part, so the centre is half the inward ray's distance in from it. When
	/// the ray finds nothing (an open garment, a missing wall) it falls back to a neck-sized
	/// 2.2 inches in, and still returns a usable centre rather than failing the whole detail.
	/// Returns false when the fallback was used, so callers that need a real centre can say so.
	/// </summary>
	public static bool Centre( DetailSurface surface, SurfaceFrame f, out Vec3 centre, out Vec3 axis )
	{
		axis = f.Up;
		var origin = f.Point + f.Normal * 0.1f;
		// A limb is a tube: the ray enters one wall and leaves the other, and the centre
		// is the midpoint of that crossing, not the first wall: a single nearest hit is
		// the near wall 0.1 away and says nothing about where the middle is. Double walls
		// (a solidified garment has an inner and an outer skin ~0.06 apart) are skipped by
		// the 1 inch gap rule, so the crossing is wall-pair to wall-pair.
		var hits = MeshRaycast.AllHits( surface.Mesh, origin, -f.Normal )
			.Where( h => h.Distance > 1e-3f )
			.OrderBy( h => h.Distance )
			.ToList();
		if ( hits.Count >= 2 )
		{
			var first = hits[0].Distance;
			foreach ( var h in hits.Skip( 1 ) )
			{
				var span = h.Distance - first;
				if ( span <= 1f )
					continue;
				if ( span < 60f )
				{
					centre = origin - f.Normal * ( first + span * 0.5f );
					return true;
				}
				break;
			}
		}
		centre = f.Point - f.Normal * 2.2f;
		return false;
	}

	/// <summary>Pull each ring point back onto the cloth along its outward direction, when
	/// the cloth is within reach: a dipped V neckline drops down the chest, and the chest
	/// curves away from the neck axis, so dipped points would otherwise float off it.</summary>
	public static List<(Vec3 Point, Vec3 Out)> Snap( List<(Vec3 Point, Vec3 Out)> ring, DetailSurface surface, float maxDist )
	{
		var snapped = new List<(Vec3 Point, Vec3 Out)>( ring.Count );
		foreach ( var p in ring )
		{
			var hit = surface.Ray( p.Point + p.Out * 0.5f, -p.Out );
			if ( hit is { } h && h.Distance < maxDist )
				snapped.Add( (h.Point, p.Out) );
			else
				snapped.Add( p );
		}
		return snapped;
	}

	public static List<(Vec3 Point, Vec3 Out)> Ring( DetailSurface surface, Vec3 centre, Vec3 axis, int count )
	{
		axis = axis.Normal;
		var reference = MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		var e1 = Vec3.Cross( axis, reference ).Normal;
		var e2 = Vec3.Cross( axis, e1 ).Normal;
		var pts = new Vec3?[count];
		var dirs = new Vec3[count];
		for ( var k = 0; k < count; k++ )
		{
			var a = k / (float)count * MathF.PI * 2f;
			var dir = e1 * MathF.Cos( a ) + e2 * MathF.Sin( a );
			dirs[k] = dir;
			var hits = MeshRaycast.AllHits( surface.Mesh, centre, dir ).Where( h => h.Distance > 1e-3f ).ToList();
			if ( hits.Count == 0 )
				continue;
			var first = hits[0].Distance;
			pts[k] = hits.Last( h => h.Distance <= first + 0.6f ).Point;
		}
		var ring = new List<(Vec3 Point, Vec3 Out)>();
		var good = new List<int>();
		for ( var k = 0; k < count; k++ )
			if ( pts[k] is not null )
				good.Add( k );
		if ( good.Count == 0 )
			return ring;
		for ( var k = 0; k < count; k++ )
		{
			if ( pts[k] is { } p )
			{
				ring.Add( (p, dirs[k]) );
				continue;
			}
			// A gap means the wall is missing in the ring's plane in this direction —
			// usually a slanted hem: the cloth ends just before or after the plane.
			// Step along the axis both ways and take the nearest hit, so the ring
			// follows the hem edge instead of cutting across the opening (a chord
			// across a sleeve hem floats an inch off the cloth and drags the band
			// and the roll with it). Only if no edge is found within 2 inches does
			// it fall back to a chord between the nearest good neighbours.
			Vec3? edge = null;
			foreach ( var mag in new[] { 0.25f, 0.5f, 1f, 1.5f, 2f } )
			{
				foreach ( var sign in new[] { 1f, -1f } )
				{
					var eh = MeshRaycast.AllHits( surface.Mesh, centre + axis * mag * sign, dirs[k] )
						.Where( h => h.Distance > 1e-3f ).ToList();
					if ( eh.Count == 0 )
						continue;
					var efirst = eh[0].Distance;
					edge = eh.Last( h => h.Distance <= efirst + 0.6f ).Point;
					break;
				}
				if ( edge is not null )
					break;
			}
			if ( edge is { } e )
			{
				ring.Add( (e, dirs[k]) );
				continue;
			}
			var prev = -1; var next = -1;
			for ( var s = 1; s <= count; s++ )
			{
				if ( prev < 0 && pts[( k - s + count * 2 ) % count] is not null )
					prev = ( k - s + count * 2 ) % count;
				if ( next < 0 && pts[( k + s ) % count] is not null )
					next = ( k + s ) % count;
				if ( prev >= 0 && next >= 0 )
					break;
			}
			var span = ( next - prev + count ) % count;
			var at = ( k - prev + count ) % count;
			var t = span > 0 ? at / (float)span : 0f;
			ring.Add( (Vec3.Lerp( pts[prev]!.Value, pts[next]!.Value, t ), dirs[k]) );
		}
		return ring;
	}

	/// <summary>
	/// A V dip: points at the front of the ring drop by up to <paramref name="depth"/>, fading
	/// to nothing at the sides, so a band follows a V neckline instead of a level one. The body
	/// faces +X in the turned space the detail is built in, so front is +X.
	/// </summary>
	public static List<(Vec3 Point, Vec3 Out)> Dip( List<(Vec3 Point, Vec3 Out)> ring, Vec3 axis, float depth, Vec3 front )
	{
		var dipped = new List<(Vec3 Point, Vec3 Out)>( ring.Count );
		foreach ( var (p, o) in ring )
		{
			var w = MathF.Max( 0f, Vec3.Dot( o, front ) );
			dipped.Add( (p - axis * depth * w * w, o) );
		}
		return dipped;
	}

	/// <summary>Pull a ring toward its centre line: a collar that tapers in at the top.</summary>
	public static List<(Vec3 Point, Vec3 Out)> Taper( List<(Vec3 Point, Vec3 Out)> ring, Vec3 centre, float factor )
	{
		var tapered = new List<(Vec3 Point, Vec3 Out)>( ring.Count );
		foreach ( var (p, o) in ring )
			tapered.Add( (centre + ( p - centre ) * ( 1f - factor ), o) );
		return tapered;
	}

	/// <summary>
	/// A lathe with a gap in it: like <see cref="DetailShapes.Latke"/> but sweeping only
	/// <paramref name="sweepDeg"/> from <paramref name="startDeg"/>, so a hood can be open at
	/// the face. Winding copies Lathe exactly (profile bottom to top, angle increasing), so
	/// the faces come out pointing outward. The gap edges and the bottom ring are left open:
	/// those are the hood's face and neck openings, not cracks.
	/// </summary>
	public static void PartialLathe( PolyMesh into, Vec3 centre, Vec3 axis, Vec3 front,
		(float Radius, float Height)[] profile, int segments, float startDeg, float sweepDeg, int slot )
	{
		axis = axis.Normal;
		var side = Vec3.Cross( axis, front );
		if ( side.LengthSquared < 1e-6f )
			side = new Vec3( 0, 1, 0 );
		side = ( side - axis * Vec3.Dot( side, axis ) ).Normal;
		front = ( front - axis * Vec3.Dot( front, axis ) ).Normal;
		var rings = new List<int[]>();
		foreach ( var (r, h) in profile )
		{
			if ( r < 0.05f )
			{
				rings.Add( new[] { into.AddVertex( centre + axis * h ) } );
				continue;
			}
			var ring = new int[segments + 1];
			for ( var s = 0; s <= segments; s++ )
			{
				var a = ( startDeg + sweepDeg * s / segments ) * MathF.PI / 180f;
				var dir = front * MathF.Cos( a ) + side * MathF.Sin( a );
				ring[s] = into.AddVertex( centre + axis * h + dir * r );
			}
			rings.Add( ring );
		}
		for ( var k = 0; k + 1 < rings.Count; k++ )
		{
			var a = rings[k]; var b = rings[k + 1];
			if ( a.Length == 1 && b.Length == 1 )
				continue;
			for ( var s = 0; s < segments; s++ )
			{
				if ( a.Length == 1 )
					into.AddFace( new[] { a[0], b[s + 1], b[s] }, null, slot );
				else if ( b.Length == 1 )
					into.AddFace( new[] { a[s], a[s + 1], b[0] }, null, slot );
				else
					into.AddFace( new[] { a[s], a[s + 1], b[s + 1], b[s] }, null, slot );
			}
		}
	}
}

/// <summary>
/// Structural neckwear: collars, lapels and hoods, built as bands round the neck or panels on
/// the chest. This is not the generic TrimFeature, which hangs frills and pleats off an
/// opening: a collar finishes the neck itself, a lapel is a folded front panel, a hood stands
/// over the head. Spot is forced per style (neck for bands, chest for lapels, back for the
/// hood drape) and is therefore not offered as a setting: picking "Chest" on a turtleneck
/// would build a ring round nothing.
/// </summary>
public sealed class CollarFeature : GarmentDetailFeature
{
	public override string TypeName => "Collar";

	public readonly ChoiceParam Style = new( "Style", new[]
	{
		"Crew", "VNeck", "PoloTurnDown", "StandMandarin", "Turtleneck",
		"NotchedLapel", "PeakLapel", "ShawlCollar", "HoodDown", "HoodUp",
	} );

	/// <summary>Stand height. 0 means the style's own height: crew/V 0.8, polo 1.2, mandarin 1.75, turtleneck 3.5, hoods 1.0.</summary>
	public readonly FloatParam Height = new( "Stand height", 0f, 0f, 6f, "in" );

	/// <summary>How far the V drops at the front, for VNeck.</summary>
	public readonly FloatParam Depth = new( "V depth", 2.5f, 0f, 5f, "in" );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Side, Across, Up, Angle, Facing, Style, Height, Depth };

	float EffectiveHeight() => Height.Value > 0f ? Height.Clamped : Style.Value switch
	{
		"Crew" or "VNeck" => 0.8f,
		"PoloTurnDown" => 1.2f,
		"StandMandarin" => 1.75f,
		"Turtleneck" => 3.5f,
		_ => 1.0f,
	};

	static int CollarSpot => Array.IndexOf( DetailSpots.Names, "Collar" );
	static int ChestSpot => Array.IndexOf( DetailSpots.Names, "Chest" );
	static int BackSpot => Array.IndexOf( DetailSpots.Names, "Back" );

	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		// One ring/drape/dome per garment, whatever Side says: a collar is central, and the
		// base build calls the maker once per side, which would otherwise double it.
		var ringDone = new HashSet<Body>();
		var drapeDone = new HashSet<Body>();
		var domeDone = new HashSet<Body>();
		switch ( Style.Value )
		{
			case "NotchedLapel":
			case "PeakLapel":
			case "ShawlCollar":
				Spot.Index = ChestSpot;
				Build( ctx, targets, ( mesh, surface, f, body, side ) => Lapel( mesh, surface, f, body, side ) );
				break;
			case "HoodDown":
				Spot.Index = CollarSpot;
				Build( ctx, targets, ( mesh, surface, f, body, side ) => { if ( ringDone.Add( body ) ) Band( mesh, surface, f, body ); } );
				Spot.Index = BackSpot;
				Build( ctx, targets, ( mesh, surface, f, body, side ) => { if ( drapeDone.Add( body ) ) Drape( mesh, surface, f, body ); } );
				break;
			case "HoodUp":
				Spot.Index = CollarSpot;
				Build( ctx, targets, ( mesh, surface, f, body, side ) => { if ( ringDone.Add( body ) ) Band( mesh, surface, f, body ); } );
				Build( ctx, targets, ( mesh, surface, f, body, side ) => { if ( domeDone.Add( body ) ) Dome( mesh, surface, f, body ); } );
				break;
			default:
				Spot.Index = CollarSpot;
				Build( ctx, targets, ( mesh, surface, f, body, side ) => { if ( ringDone.Add( body ) ) Band( mesh, surface, f, body ); } );
				break;
		}
	}

	void Band( PolyMesh mesh, DetailSurface surface, SurfaceFrame f, Body body )
	{
		StructuralRings.Centre( surface, f, out var centre, out var axis );
		var lower = StructuralRings.Ring( surface, centre, axis, 28 );
		if ( lower.Count < 8 )
			return;
		var height = EffectiveHeight();
		var upperCentre = centre + axis * height;
		var upper = StructuralRings.Ring( surface, upperCentre, axis, 28 );
		if ( upper.Count < 8 )
			return;
		var cloth = ClothSlot( body );
		switch ( Style.Value )
		{
			case "VNeck":
				lower = StructuralRings.Snap( StructuralRings.Dip( lower, axis, Depth.Clamped, new Vec3( 1, 0, 0 ) ), surface, 2f );
				upper = StructuralRings.Snap( StructuralRings.Dip( upper, axis, Depth.Clamped, new Vec3( 1, 0, 0 ) ), surface, 2f );
				break;
			case "StandMandarin":
				upper = StructuralRings.Taper( upper, upperCentre, 0.08f );
				break;
			case "Turtleneck":
				upper = StructuralRings.Taper( upper, upperCentre, 0.12f );
				break;
		}
		DetailShapes.Band( mesh, lower, upper, 0.05f, 0.06f, cloth );
		if ( Style.Value == "PoloTurnDown" )
		{
			var folded = upper.Select( p => (Point: p.Point + p.Out * 0.35f - axis * height * 0.7f, Out: p.Out) ).ToList();
			DetailShapes.Band( mesh, upper, folded, 0.05f, 0.06f, cloth );
			DetailShapes.Tube( mesh, folded.Select( p => p.Point + p.Out * 0.08f ).ToList(), 0.08f, 6, true, cloth );
		}
		if ( Style.Value == "Turtleneck" )
			DetailShapes.Tube( mesh, upper.Select( p => p.Point + p.Out * 0.12f ).ToList(), 0.12f, 6, true, cloth );
	}

	void Lapel( PolyMesh mesh, DetailSurface surface, SurfaceFrame f, Body body, int side )
	{
		var cloth = ClothSlot( body );
		var style = Style.Value;
		Panel( mesh, surface, f, 5.5f, 8.5f, ( x, y ) =>
		{
			// Frames are not mirrored per side, so outward is +x on the left and -x on the right.
			var u = x * side;
			var radius = style == "ShawlCollar" ? 1.8f : 0.9f;
			var d = DetailShapes.RoundedRectDistance( x, y, 2.3f, 3.9f, radius );
			var edge = 1f - DetailShapes.Smooth( -0.05f, 0.25f, d );
			if ( edge <= 0f )
				return 0f;
			var raise = 0.10f * edge;
			// The fold roll, along the lapel's inner edge.
			raise += 0.05f * MathF.Exp( -MathF.Pow( ( u + 1.1f ) / 0.22f, 2 ) ) * edge;
			if ( style == "NotchedLapel" && u > 0.3f && u < 2.3f && y > 1.2f && y < 2.6f )
				return 0f;
			if ( style == "PeakLapel" && u > 0.3f && u < 2.3f && y > 3.4f && y < 4.25f
				&& y - 3.4f <= ( u - 0.3f ) * 0.425f )
				raise = MathF.Max( raise, 0.10f );
			return raise;
		}, cloth );
	}

	void Drape( PolyMesh mesh, DetailSurface surface, SurfaceFrame f, Body body )
	{
		var cloth = ClothSlot( body );
		var down = surface.Offset( f, 0f, -3f );
		Panel( mesh, surface, down, 9f, 10f, ( x, y ) =>
		{
			var d = DetailShapes.RoundedRectDistance( x, y, 4.2f, 4.7f, 1.2f );
			return 0.08f * ( 1f - DetailShapes.Smooth( -0.05f, 0.3f, d ) );
		}, cloth );
	}

	void Dome( PolyMesh mesh, DetailSurface surface, SurfaceFrame f, Body body )
	{
		StructuralRings.Centre( surface, f, out var centre, out var axis );
		var cloth = ClothSlot( body );
		var foot = centre + axis * EffectiveHeight();
		// A hood over the head, open at the face: 240 degrees of dome, 120 degrees of gap
		// centred on the front (+X). The base ring is wider than the neck, so the hood sits
		// on the shoulders rather than strangling the collar band.
		var profile = new (float Radius, float Height)[]
		{
			(4.2f, 0f), (4.0f, 1.2f), (3.3f, 2.6f), (2.2f, 3.8f), (0.6f, 4.6f), (0f, 5.0f),
		};
		StructuralRings.PartialLathe( mesh, foot, axis, new Vec3( 1, 0, 0 ), profile, 28, 60f, 240f, cloth );
	}
}

/// <summary>
/// Cuffs round a wrist or ankle: a band on the limb, with the finish that makes the style.
/// Ribbed knit gets corrugation rings, a rolled cuff gets a fat roll at its edge, a French
/// cuff folds back over itself, a shirt cuff gets a button. Spot is forced by Place (wrist
/// or ankle) and is therefore not offered as a setting.
/// </summary>
public sealed class CuffFeature : GarmentDetailFeature
{
	public override string TypeName => "Cuff";

	public readonly ChoiceParam Place = new( "Place", new[] { "Wrist", "Ankle" } );
	public readonly ChoiceParam Style = new( "Style", new[] { "RibbedKnit", "RolledUp", "FrenchTurnback", "ShirtButtoned" } );

	/// <summary>Band height. 0 means the style's own height: ribbed knit 2.5, the rest 1.75.</summary>
	public readonly FloatParam Height = new( "Band height", 0f, 0f, 6f, "in" );

	public readonly ChoiceParam Finish = new( "Button finish", Effigy.DetailMaterials.Finishes );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Place, Side, Across, Up, Angle, Facing, Style, Height, Finish };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		Spot.Index = Array.IndexOf( DetailSpots.Names, Place.Value );
		var metal = -1;
		if ( Style.Value == "ShirtButtoned" )
			metal = Metal( ctx, Finish.Value );
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			StructuralRings.Centre( surface, f, out var centre, out var axis );
			var lower = StructuralRings.Ring( surface, centre, axis, 24 );
			if ( lower.Count < 8 )
				return;
			var height = Height.Value > 0f ? Height.Clamped : Style.Value == "RibbedKnit" ? 2.5f : 1.75f;
			var upper = StructuralRings.Ring( surface, centre + axis * height, axis, 24 );
			if ( upper.Count < 8 )
				return;
			var cloth = ClothSlot( body );
			DetailShapes.Band( mesh, lower, upper, 0.05f, 0.06f, cloth );
			switch ( Style.Value )
			{
				case "RibbedKnit":
					for ( var k = 0; k < 6; k++ )
					{
						var ring = StructuralRings.Ring( surface, centre + axis * height * ( k + 0.5f ) / 6f, axis, 24 );
						if ( ring.Count >= 8 )
							DetailShapes.Tube( mesh, ring.Select( p => p.Point + p.Out * 0.05f ).ToList(), 0.05f, 4, true, cloth );
					}
					break;
				case "RolledUp":
					DetailShapes.Tube( mesh, lower.Select( p => p.Point + p.Out * 0.18f ).ToList(), 0.18f, 6, true, cloth );
					break;
				case "FrenchTurnback":
					var folded = upper.Select( p => (Point: p.Point + p.Out * 0.25f - axis * height * 0.6f, Out: p.Out) ).ToList();
					DetailShapes.Band( mesh, upper, folded, 0.05f, 0.06f, cloth );
					break;
				case "ShirtButtoned":
					DetailShapes.Place( mesh, DetailShapes.Button( 0.32f, metal ), f, 0.1f );
					break;
			}
		} );
	}
}

/// <summary>
/// Pleats and gathers as a folded panel on the cloth, not as a trim at an opening (the
/// generic TrimFeature's pleat is a hem finish; this is the fold structure itself). The folds
/// run up the body and vary across the panel: knife pleats all fall one way, box pleats are
/// two knife pleats facing each other, inverted box pleats are the same shape pressed the
/// other way (ridges at the edges, a valley in the middle), accordion pleats are sharp both
/// ways. Where the panel goes is the spot setting, so a skirt's pleats and a sleeve's pleats
/// are the same feature in different places.
/// </summary>
public sealed class PleatFeature : GarmentDetailFeature
{
	public override string TypeName => "Pleat";

	public readonly ChoiceParam Style = new( "Style", new[] { "Knife", "Box", "Inverted", "Accordion" } );
	public readonly FloatParam Width = new( "Width", 12f, 1f, 36f, "in" );
	public readonly FloatParam Length = new( "Length", 10f, 1f, 30f, "in" );
	public readonly FloatParam Depth = new( "Fold depth", 0.35f, 0.05f, 1.5f, "in" );
	public readonly FloatParam Count = new( "Pleats", 6f, 1f, 24f );

	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Width, Length, Depth, Count } ).ToArray();

	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			var w = Width.Clamped;
			var l = Length.Clamped;
			var depth = Depth.Clamped;
			var n = Math.Max( 1, (int)MathF.Round( Count.Clamped ) );
			var cloth = ClothSlot( body );
			var style = Style.Value;
			// Four samples per fold across, so the folds come out crisp rather than wavy.
			var grid = surface.Grid( f, w, l, n * 2, 4 );
			DetailShapes.Panel( mesh, grid, w, l, ( x, y ) =>
			{
				var ph = ( x + w * 0.5f ) / w * n;
				ph -= MathF.Floor( ph );
				var profile = style switch
				{
					"Knife" => ph < 0.25f ? ph / 0.25f : ( 1f - ph ) / 0.75f,
					"Accordion" => ph < 0.5f ? 2f * ph : 2f * ( 1f - ph ),
					"Inverted" => 0.3f + 0.7f * ( 1f - BoxProfile( ph ) ),
					_ => BoxProfile( ph ),
				};
				return depth * profile;
			}, ( x, y ) => cloth, 1f / 12f );
		} );
	}

	static float BoxProfile( float ph ) =>
		DetailShapes.Smooth( 0f, 0.2f, ph ) * ( 1f - DetailShapes.Smooth( 0.8f, 1f, ph ) );
}

/// <summary>
/// A seam as a line on the cloth: the stitching itself, walked across the surface from the
/// spot in the direction the Angle setting turns the frame to. Flat is a fine stitch line;
/// a welt is the raised cord of a welt seam. This is the seam line only — joining two pieces
/// of cloth together is PatternStitch's job, and frayed thread ends are WearFeature's.
/// </summary>
public sealed class SeamFeature : GarmentDetailFeature
{
	public override string TypeName => "Seam";

	public readonly ChoiceParam Style = new( "Style", new[] { "Flat", "Welt" } );
	public readonly FloatParam Length = new( "Length", 8f, 0.5f, 40f, "in" );

	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Length } ).ToArray();

	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			var length = Length.Clamped;
			var steps = Math.Max( 1, (int)MathF.Ceiling( length / 0.5f ) );
			var step = length / steps;
			var frames = new List<SurfaceFrame> { f };
			var cur = f;
			for ( var i = 0; i < steps; i++ )
			{
				cur = surface.Walk( cur, cur.Right, step );
				frames.Add( cur );
			}
			var welt = Style.Value == "Welt";
			var radius = welt ? 0.12f : 0.03f;
			// The cord sits ON the cloth, not sunk halfway into it.
			DetailShapes.Tube( mesh, frames.Select( fr => fr.Point + fr.Normal * radius ).ToList(), radius, welt ? 6 : 4, false, ClothSlot( body ) );
		} );
	}
}

/// <summary>
/// Topstitching: the visible contrast stitching on top of a seam or edge, in dark thread.
/// Single is one stitch line; twin-needle is two parallel lines 0.12 inches apart, offset
/// along the surface on either side of the walked path. The path is walked from the spot in
/// the direction Angle turns the frame to, exactly like <see cref="SeamFeature"/>.
/// </summary>
public sealed class TopstitchFeature : GarmentDetailFeature
{
	public override string TypeName => "Topstitch";

	public readonly ChoiceParam Style = new( "Style", new[] { "Single", "TwinNeedle" } );
	public readonly FloatParam Length = new( "Length", 8f, 0.5f, 40f, "in" );

	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Length } ).ToArray();

	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		var dark = Dark( ctx );
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			var length = Length.Clamped;
			var steps = Math.Max( 1, (int)MathF.Ceiling( length / 0.5f ) );
			var step = length / steps;
			var frames = new List<SurfaceFrame> { f };
			var cur = f;
			for ( var i = 0; i < steps; i++ )
			{
				cur = surface.Walk( cur, cur.Right, step );
				frames.Add( cur );
			}
			if ( Style.Value == "TwinNeedle" )
			{
				DetailShapes.Tube( mesh, frames.Select( fr => fr.Point + fr.Up * 0.06f + fr.Normal * 0.02f ).ToList(), 0.02f, 4, false, dark );
				DetailShapes.Tube( mesh, frames.Select( fr => fr.Point - fr.Up * 0.06f + fr.Normal * 0.02f ).ToList(), 0.02f, 4, false, dark );
			}
			else
			{
				DetailShapes.Tube( mesh, frames.Select( fr => fr.Point + fr.Normal * 0.02f ).ToList(), 0.02f, 4, false, dark );
			}
		} );
	}
}

/// <summary>
/// Piping: a cord sewn into an edge, following a garment boundary loop — the neck, a cuff,
/// a hem — rather than a walked path. The loop nearest the spot frame is the one piped, so
/// putting the spot on the neck pipes the neck and putting it on a cuff pipes that cuff. A
/// garment with no openings fails loudly instead of piping nothing: piping with no edge is
/// a contradiction, not a detail.
/// </summary>
public sealed class PipingFeature : GarmentDetailFeature
{
	public override string TypeName => "Piping";

	public readonly FloatParam Radius = new( "Cord radius", 0.09f, 0.02f, 0.5f, "in" );

	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Radius } ).ToArray();

	protected override void Execute( FeatureContext ctx )
	{
		var targets = Targets( ctx );
		foreach ( var t in targets )
		{
			var loops = GarmentTrim.OrderedBoundaryLoops( DetailEdges.Sheet( t.Mesh ) );
			if ( loops.Count == 0 )
				Fail( "This garment has no edge to pipe",
					"Piping finishes an opening, and this garment has none.",
					"Pick a garment with a neck, cuff or hem opening" );
		}
		Build( ctx, targets, ( mesh, surface, f, body, side ) =>
		{
			var loops = GarmentTrim.OrderedBoundaryLoops( surface.Mesh );
			List<int> best = null;
			var bestD = float.MaxValue;
			foreach ( var loop in loops )
			{
				if ( loop.Count < 3 )
					continue;
				var centre = Vec3.Zero;
				foreach ( var i in loop )
					centre += surface.Mesh.Positions[i];
				centre = centre * ( 1f / loop.Count );
				var d = ( centre - f.Point ).Length;
				if ( d < bestD )
				{
					bestD = d;
					best = loop;
				}
			}
			if ( best is null )
				return;
			var path = best.Select( i => surface.Mesh.Positions[i] ).ToList();
			DetailShapes.Tube( mesh, path, Radius.Clamped, 6, true, ClothSlot( body ) );
		} );
	}
}
