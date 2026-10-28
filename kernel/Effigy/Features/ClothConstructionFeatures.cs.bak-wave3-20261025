using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Body measurement from a mesh, via the existing section machinery: a horizontal
/// cut at height z, chained into outlines, whose largest outer loop's perimeter
/// is the circumference at that height. A closed loop is a real circumference;
/// an open chain (a garment sheet, a missing wall) reports its chain length and
/// says so, rather than pretending to be round.
/// </summary>
public static class BodyMeasure
{
	public sealed class Reading
	{
		public float Z;
		public float Circumference;
		public bool Closed;
		public Vec3 Centre;
		public int Loops;
	}

	public static Reading Circumference( PolyMesh mesh, float z )
	{
		if ( mesh is null || mesh.FaceCount == 0 )
			return new Reading { Z = z, Circumference = 0f, Closed = false, Centre = Vec3.Zero, Loops = 0 };
		var slice = SectionReport.Cut( mesh, new Vec3( 0f, 0f, z ), new Vec3( 0f, 0f, 1f ) );
		if ( !slice.Hit )
			return new Reading { Z = z, Circumference = 0f, Closed = false, Centre = Vec3.Zero, Loops = 0 };
		var outer = slice.Loops.Where( l => !l.IsHole ).OrderByDescending( l => l.Perimeter ).FirstOrDefault()
			?? slice.Loops[0];
		return new Reading
		{
			Z = z,
			Circumference = outer.Perimeter,
			Closed = outer.Closed,
			Centre = outer.Centre,
			Loops = slice.Loops.Count,
		};
	}

	/// <summary>Landmark heights as fractions of the body's height from its feet:
	/// chest 0.78, waist 0.58, hip 0.42 on a standing figure. Explicit z values
	/// always beat the fractions; the fractions are a starting point, not anatomy.</summary>
	public static Dictionary<string, float> LandmarkHeights( PolyMesh mesh )
	{
		var minZ = mesh.Positions.Min( p => p.z );
		var maxZ = mesh.Positions.Max( p => p.z );
		var h = Math.Max( maxZ - minZ, 1e-6f );
		return new Dictionary<string, float>
		{
			["chest"] = minZ + 0.78f * h,
			["waist"] = minZ + 0.58f * h,
			["hip"] = minZ + 0.42f * h,
		};
	}
}

static class ClothMesh
{
	public static int AddPoint( PolyMesh m, Vec3 p ) => m.AddVertex( p );

	public static void AddQuad( PolyMesh m, int a, int b, int c, int d, int slot )
		=> m.AddFace( new[] { a, b, c, d }, new Vec2[4], slot );

	public static void AddTri( PolyMesh m, int a, int b, int c, int slot )
		=> m.AddFace( new[] { a, b, c }, new Vec2[3], slot );

	/// <summary>Append a source mesh into a target, offset, with all face indices rebased.</summary>
	public static void Append( PolyMesh target, PolyMesh source, Vec3 offset, int slot )
	{
		var baseIndex = target.Positions.Count;
		foreach ( var p in source.Positions )
			target.AddVertex( p + offset );
		foreach ( var f in source.Faces )
			target.AddFace( f.Indices.Select( i => i + baseIndex ).ToArray(), new Vec2[f.Count], slot );
	}

	/// <summary>Rotate a mesh's points about the origin so a Z-built primitive faces axis X or Y.</summary>
	public static void FaceAxis( PolyMesh m, string axis )
	{
		if ( string.Equals( axis, "x", StringComparison.OrdinalIgnoreCase ) )
		{
			for ( var i = 0; i < m.Positions.Count; i++ )
			{
				var p = m.Positions[i];
				m.Positions[i] = new Vec3( p.z, p.y, -p.x );
			}
		}
		else if ( string.Equals( axis, "y", StringComparison.OrdinalIgnoreCase ) )
		{
			for ( var i = 0; i < m.Positions.Count; i++ )
			{
				var p = m.Positions[i];
				m.Positions[i] = new Vec3( p.x, p.z, -p.y );
			}
		}
	}
}

/// <summary>
/// P1, the drafted block: front and back bodice panels drafted from real
/// measurements, not cut out of the wearer. Chest/waist/hip are inches round;
/// 0 means "measure this body at the landmark heights". Ease is per region
/// (chest/waist/hip separately, and it may be negative for knits), which is
/// the whole point: a waist that needs 1 inch and a hip that needs 3 inches
/// can finally both be right. Panels are flat pattern pieces in the XY plane,
/// laid side by side, ready to be sewn by PatternStitch / PatternPanels.
/// Sleeve panels are tapered rectangles, one per side when sleeves are on.
/// </summary>
public sealed class DraftPanelsFeature : Feature
{
	public override string TypeName => "DraftPanels";
	public override GeometryKind Accepts => GeometryKind.Body;
	public readonly BodySelectionParam Bodies = new( "On" );
	public readonly FloatParam Chest = new( "Chest", 0f, 0f, 200f, "in" );
	public readonly FloatParam Waist = new( "Waist", 0f, 0f, 200f, "in" );
	public readonly FloatParam Hip = new( "Hip", 0f, 0f, 200f, "in" );
	public readonly FloatParam EaseChest = new( "Chest ease", 3f, -12f, 20f, "in" );
	public readonly FloatParam EaseWaist = new( "Waist ease", 1f, -12f, 20f, "in" );
	public readonly FloatParam EaseHip = new( "Hip ease", 3f, -12f, 20f, "in" );
	public readonly FloatParam Length = new( "Panel length", 24f, 1f, 100f, "in" );
	public readonly FloatParam SleeveLength = new( "Sleeve length", 20f, 0f, 60f, "in" );
	public readonly FloatParam Bicep = new( "Bicep", 0f, 0f, 100f, "in" );
	public readonly FloatParam SleeveEase = new( "Sleeve ease", 1.5f, -6f, 12f, "in" );
	public readonly BoolParam Sleeves = new( "Sleeves", true );
	public readonly IntParam MaterialSlot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Chest, Waist, Hip, EaseChest, EaseWaist, EaseHip, Length, SleeveLength, Bicep, SleeveEase, Sleeves, MaterialSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );
		var wearer = bodies.FirstOrDefault( b => b.Mesh is { FaceCount: > 0 } );
		if ( wearer is null )
			Fail( "DraftPanels needs a body to measure.", "Panels are drafted from a body's measurements.", "Give on=<body name> for a body that is still in the studio" );
		var mesh = wearer.Mesh;
		var marks = BodyMeasure.LandmarkHeights( mesh );
		float Measure( string name, float given )
		{
			if ( given > 0f ) return given;
			var r = BodyMeasure.Circumference( mesh, marks[name] );
			if ( r.Circumference <= 0f )
				Fail( $"DraftPanels could not measure {name} at z={marks[name]:0.##}.",
					"The slice at that height found no outline on this body.",
					$"Give {name.ToLowerInvariant()}= in inches explicitly" );
			return r.Circumference;
		}
		var chest = Measure( "chest", Chest.Clamped ) + EaseChest.Clamped;
		var waist = Measure( "waist", Waist.Clamped ) + EaseWaist.Clamped;
		var hip = Measure( "hip", Hip.Clamped ) + EaseHip.Clamped;
		if ( chest <= 0f || waist <= 0f || hip <= 0f )
			Fail( "DraftPanels ease made a panel width zero or negative.",
				"Ease may be negative for knits, but not more negative than the body measurement it applies to.",
				"Raise the ease, or check the measured circumferences" );

		var len = Length.Clamped;
		var topY = len;
		var chestY = len * 0.82f;
		var waistY = len * 0.45f;
		// Half-widths: a front panel is half the finished round.
		var chestW = chest * 0.5f * 0.5f;
		var waistW = waist * 0.5f * 0.5f;
		var hipW = hip * 0.5f * 0.5f;
		var shoulderW = chestW * 0.92f;
		var neckW = chestW * 0.32f;
		var outline = new (float X, float Y)[]
		{
			(-hipW, 0f), (hipW, 0f), (waistW, waistY), (chestW, chestY),
			(shoulderW, topY), (neckW, topY), (-neckW, topY), (-shoulderW, topY),
			(-chestW, chestY), (-waistW, waistY),
		};
		PolyMesh Panel( float xOff )
		{
			var m = new PolyMesh();
			var centre = m.AddVertex( new Vec3( xOff, len * 0.5f, 0f ) );
			var ids = outline.Select( p => m.AddVertex( new Vec3( xOff + p.X, p.Y, 0f ) ) ).ToArray();
			for ( var i = 0; i < ids.Length; i++ )
			{
				var j = (i + 1) % ids.Length;
				m.AddFace( new[] { centre, ids[i], ids[j] }, new Vec2[3], MaterialSlot.Clamped );
			}
			return m;
		}
		var gap = hipW + 2f;
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "DraftFront", Panel( 0f ) ) { IsGarment = true } );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "DraftBack", Panel( gap * 2f ) ) { IsGarment = true } );
		if ( Sleeves.Value && SleeveLength.Clamped > 0f )
		{
			var bicep = (Bicep.Clamped > 0f ? Bicep.Clamped : chest * 0.32f) + SleeveEase.Clamped;
			if ( bicep <= 0f )
				Fail( "DraftPanels sleeve ease made the sleeve width zero or negative.",
					"Sleeve ease may be negative for knits, but not more negative than the bicep.",
					"Raise sleeve ease, or give bicep= in inches" );
			var sw = bicep * 0.5f * 0.5f;
			var sl = SleeveLength.Clamped;
			PolyMesh Sleeve( float xOff )
			{
				var m = new PolyMesh();
				var pts = new (float X, float Y)[] { (-sw, 0f), (sw, 0f), (sw * 0.8f, sl), (-sw * 0.8f, sl) };
				var centre = m.AddVertex( new Vec3( xOff, sl * 0.5f, 0f ) );
				var ids = pts.Select( p => m.AddVertex( new Vec3( xOff + p.X, p.Y, 0f ) ) ).ToArray();
				for ( var i = 0; i < ids.Length; i++ )
				{
					var j = (i + 1) % ids.Length;
					m.AddFace( new[] { centre, ids[i], ids[j] }, new Vec2[3], MaterialSlot.Clamped );
				}
				return m;
			}
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), "DraftSleeveL", Sleeve( -gap ) ) { IsGarment = true } );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), "DraftSleeveR", Sleeve( gap * 3f ) ) { IsGarment = true } );
		}
	}
}

/// <summary>
/// P2, a dart as construction, not decoration: a tapered fold that comes to
/// a point at one end and opens to its full width and height at the other,
/// built as two triangles meeting on a raised ridge. It sits on the garment
/// at the given point; the ridge runs along dir, the width across it, and
/// the fold rises along the outward direction implied by the two.
/// </summary>
public sealed class DartFeature : Feature
{
	public override string TypeName => "Dart";
	public override GeometryKind Accepts => GeometryKind.Body;
	public readonly BodySelectionParam Bodies = new( "On" );
	public readonly Vec3Param At = new( "At", new Vec3( 0f, 0f, 0f ) );
	public readonly Vec3Param Dir = new( "Direction", new Vec3( 0f, 0f, 1f ) );
	public readonly FloatParam Length = new( "Length", 6f, 0.1f, 60f, "in" );
	public readonly FloatParam Width = new( "Width", 2f, 0.1f, 30f, "in" );
	public readonly FloatParam Depth = new( "Depth", 0.4f, 0.01f, 10f, "in" );
	public readonly IntParam MaterialSlot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, At, Dir, Length, Width, Depth, MaterialSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );
		if ( bodies.Count == 0 )
			Fail( "Dart needs a garment to sit on.", "A dart folds cloth; with no cloth there is nothing to fold.", "Add a Garment first, then give on=<garment name>" );
		var dir = Dir.Value;
		if ( dir.Length < 1e-6f )
			Fail( "Dart direction is zero.", "The ridge needs a direction to run along.", "Give direction= as a non-zero vector" );
		dir = dir.Normal;
		var reference = MathF.Abs( dir.z ) < 0.9f ? new Vec3( 0f, 0f, 1f ) : new Vec3( 1f, 0f, 0f );
		var widthDir = Vec3.Cross( dir, reference ).Normal;
		var outDir = Vec3.Cross( widthDir, dir ).Normal;
		var at = At.Value;
		var half = Length.Clamped * 0.5f;
		var point = at - dir * half;
		var baseCentre = at + dir * half;
		var b1 = baseCentre + widthDir * (Width.Clamped * 0.5f);
		var b2 = baseCentre - widthDir * (Width.Clamped * 0.5f);
		var ridge = baseCentre + outDir * Depth.Clamped;
		var m = new PolyMesh();
		var iP = m.AddVertex( point );
		var iB1 = m.AddVertex( b1 );
		var iB2 = m.AddVertex( b2 );
		var iR = m.AddVertex( ridge );
		m.AddFace( new[] { iP, iB1, iR }, new Vec2[3], MaterialSlot.Clamped );
		m.AddFace( new[] { iP, iR, iB2 }, new Vec2[3], MaterialSlot.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "Dart", m ) { IsGarment = true, IsGarmentDetail = true } );
	}
}

/// <summary>
/// P6, a button placket as hardware, not a painted line: buttons (small
/// cylinders) and buttonholes (small flat rings) spaced down a line, with
/// the lap side following the placket's direction. Buttons and holes are
/// merged into one body each so the placket is two parts, not dozens.
/// </summary>
public sealed class ButtonPlacketFeature : Feature
{
	public override string TypeName => "ButtonPlacket";
	public override GeometryKind Accepts => GeometryKind.Body;
	public readonly BodySelectionParam Bodies = new( "On" );
	public readonly Vec3Param At = new( "At", new Vec3( 0f, 0f, 0f ) );
	public readonly Vec3Param Dir = new( "Direction", new Vec3( 0f, 0f, 1f ) );
	public readonly ChoiceParam Axis = new( "Faces", new[] { "+X", "-Y", "+Y", "-X" } );
	public readonly IntParam Count = new( "Buttons", 5, 1, 30 );
	public readonly FloatParam Spacing = new( "Spacing", 3f, 0.5f, 20f, "in" );
	public readonly FloatParam ButtonRadius = new( "Button radius", 0.4f, 0.05f, 3f, "in" );
	public readonly IntParam MaterialSlot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, At, Dir, Axis, Count, Spacing, ButtonRadius, MaterialSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );
		if ( bodies.Count == 0 )
			Fail( "ButtonPlacket needs a garment to sit on.", "A placket fastens a garment; with no garment there is nothing to fasten.", "Add a Garment first, then give on=<garment name>" );
		var dir = Dir.Value;
		if ( dir.Length < 1e-6f )
			Fail( "ButtonPlacket direction is zero.", "The buttons need a line to run down.", "Give direction= as a non-zero vector" );
		dir = dir.Normal;
		var axis = Axis.Index switch { 1 => "y", 2 => "y", 3 => "x", _ => "x" };
		// Buttons face outward: the cylinder's axis is turned to the facing axis,
		// and the hole ring sits beside each button, offset across the placket.
		var reference = MathF.Abs( dir.z ) < 0.9f ? new Vec3( 0f, 0f, 1f ) : new Vec3( 1f, 0f, 0f );
		var across = Vec3.Cross( dir, reference ).Normal;
		var buttons = new PolyMesh();
		var holes = new PolyMesh();
		for ( var i = 0; i < Count.Clamped; i++ )
		{
			var centre = At.Value + dir * (Spacing.Clamped * i);
			var button = Primitives.Cylinder( ButtonRadius.Clamped, 0.25f, 16, MaterialSlot.Clamped );
			ClothMesh.FaceAxis( button, axis );
			ClothMesh.Append( buttons, button, centre, MaterialSlot.Clamped );
			var hole = Primitives.Tube( ButtonRadius.Clamped * 1.6f, ButtonRadius.Clamped * 1.1f, 0.12f, 16, MaterialSlot.Clamped );
			ClothMesh.FaceAxis( hole, axis );
			ClothMesh.Append( holes, hole, centre + across * (ButtonRadius.Clamped * 2.5f), MaterialSlot.Clamped );
		}
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "PlacketButtons", buttons ) { IsGarment = true, IsGarmentDetail = true } );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "PlacketHoles", holes ) { IsGarment = true, IsGarmentDetail = true } );
	}
}

/// <summary>
/// P6, a double-fold hem and a collar stand, both as bands built from a
/// real slice of the garment rather than a tube drawn on top of it.
/// DoubleFoldHem follows the garment's own outline at height z, folded
/// back on itself (two layers, the fold at the bottom edge). CollarStand
/// follows the outline near the garment's top, rising standheight with a
/// slight flare, in two layers: the stand and its interfacing.
/// </summary>
public sealed class DoubleFoldHemFeature : Feature
{
	public override string TypeName => "DoubleFoldHem";
	public override GeometryKind Accepts => GeometryKind.Body;
	public readonly BodySelectionParam Bodies = new( "On" );
	public readonly FloatParam Z = new( "Height", 0f, -1000f, 1000f, "in" );
	public readonly FloatParam Height = new( "Fold height", 1.5f, 0.1f, 20f, "in" );
	public readonly FloatParam Thickness = new( "Fold thickness", 0.12f, 0.01f, 2f, "in" );
	public readonly IntParam MaterialSlot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Z, Height, Thickness, MaterialSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );
		var target = bodies.FirstOrDefault( b => b.Mesh is { FaceCount: > 0 } );
		if ( target is null )
			Fail( "DoubleFoldHem needs a body with a mesh.", "The hem follows the garment's own outline at the given height.", "Give on=<garment name> for a garment that is still in the studio" );
		var slice = SectionReport.Cut( target.Mesh, new Vec3( 0f, 0f, Z.Clamped ), new Vec3( 0f, 0f, 1f ) );
		if ( !slice.Hit )
			Fail( $"DoubleFoldHem found no outline at z={Z.Clamped:0.##}.",
				"The garment does not reach that height, or has a hole there.",
				"Give height= within the garment's height range" );
		var loop = slice.Loops.Where( l => !l.IsHole ).OrderByDescending( l => l.Perimeter ).First();
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "DoubleFoldHem", Band( loop, Z.Clamped, Height.Clamped, Thickness.Clamped, MaterialSlot.Clamped, fold: true ) ) { IsGarment = true, IsGarmentDetail = true } );
	}

	public static PolyMesh Band( SectionReport.Loop loop, float z, float height, float thickness, int slot, bool fold )
	{
		var m = new PolyMesh();
		var pts = loop.Points;
		var centre = loop.Centre;
		List<int> MakeRing( float zz, float scale )
		{
			var ids = new List<int>();
			foreach ( var p in pts )
			{
				var q = centre + (p - centre) * scale;
				ids.Add( m.AddVertex( new Vec3( q.x, q.y, zz ) ) );
			}
			return ids;
		}
		var n = pts.Count;
		if ( n < 3 )
			return m;
		var bottom = MakeRing( z, 1f );
		var top = MakeRing( z + height, 1f );
		var topIn = MakeRing( z + height, 0.98f );
		var bottomIn = MakeRing( z, 0.98f );
		void Strip( List<int> a, List<int> b, bool closed )
		{
			var count = closed ? n : n - 1;
			for ( var i = 0; i < count; i++ )
			{
				var j = (i + 1) % n;
				m.AddFace( new[] { a[i], b[i], b[j], a[j] }, new Vec2[4], slot );
			}
		}
		var closed = loop.Closed;
		Strip( bottom, top, closed );
		if ( fold )
		{
			// The fold: the cloth doubles back down inside itself.
			Strip( top, topIn, closed );
			Strip( topIn, bottomIn, closed );
		}
		return m;
	}
}

public sealed class CollarStandFeature : Feature
{
	public override string TypeName => "CollarStand";
	public override GeometryKind Accepts => GeometryKind.Body;
	public readonly BodySelectionParam Bodies = new( "On" );
	public readonly FloatParam StandHeight = new( "Stand height", 2f, 0.1f, 12f, "in" );
	public readonly FloatParam Flare = new( "Flare", 0.1f, -0.5f, 1f );
	public readonly IntParam MaterialSlot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, StandHeight, Flare, MaterialSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );
		var target = bodies.FirstOrDefault( b => b.Mesh is { FaceCount: > 0 } );
		if ( target is null )
			Fail( "CollarStand needs a body with a mesh.", "The stand follows the garment's own neck outline.", "Give on=<garment name> for a garment that is still in the studio" );
		var maxZ = target.Mesh.Positions.Max( p => p.z );
		var slice = SectionReport.Cut( target.Mesh, new Vec3( 0f, 0f, maxZ - 0.5f ), new Vec3( 0f, 0f, 1f ) );
		if ( !slice.Hit )
			Fail( "CollarStand found no neck outline near the garment's top.",
				"The garment's top is open or missing there.",
				"Check the garment reaches a neck, or use a taller garment" );
		var loop = slice.Loops.Where( l => !l.IsHole ).OrderByDescending( l => l.Perimeter ).First();
		var m = new PolyMesh();
		var n = loop.Points.Count;
		if ( n < 3 )
			Fail( "CollarStand neck outline has too few points to build a band.", "The slice at the neck found a degenerate outline.", "Use a garment with a closed neck opening" );
		List<int> Ring( float z, float scale )
		{
			var ids = new List<int>();
			foreach ( var p in loop.Points )
			{
				var q = loop.Centre + (p - loop.Centre) * scale;
				ids.Add( m.AddVertex( new Vec3( q.x, q.y, z ) ) );
			}
			return ids;
		}
		var z0 = maxZ - 0.5f;
		var bottom = Ring( z0, 1f );
		var top = Ring( z0 + StandHeight.Clamped, 1f + Flare.Clamped );
		var topIn = Ring( z0 + StandHeight.Clamped, (1f + Flare.Clamped) * 0.97f );
		void Strip( List<int> a, List<int> b )
		{
			var count = loop.Closed ? n : n - 1;
			for ( var i = 0; i < count; i++ )
			{
				var j = (i + 1) % n;
				m.AddFace( new[] { a[i], b[i], b[j], a[j] }, new Vec2[4], MaterialSlot.Clamped );
			}
		}
		Strip( bottom, top );
		Strip( top, topIn ); // interfacing layer, folded back over the stand's top edge
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "CollarStand", m ) { IsGarment = true, IsGarmentDetail = true } );
	}
}
