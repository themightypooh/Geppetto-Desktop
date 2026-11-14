using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A brake-formed sheet metal part: a flat base flange of one thickness with an edge flange bent up
/// on either side (length 0 = no flange), each through its own angle around a real inside bend
/// radius. Flat pattern lays the same part out unbent, with each bend replaced by its bend
/// allowance angle * (R + K * T), so the blank size is what a shop would cut.
///
/// The cross-section is one closed outline (inside and outside of the sheet, joined at the tips)
/// swept along the bend lines, so the solid is one manifold body whatever the angles are.
/// Effigy is Z up: the base lies on z = 0..T along X, the bend lines run along Y.
/// </summary>
public sealed class SheetMetalFeature : Feature
{
	public override string TypeName => "Sheet metal";

	public readonly FloatParam Thickness = new( "Thickness", 0.06f, 0.001f, 10f, unit: "u" );
	public readonly FloatParam Width = new( "Width", 4f, 0.01f, unit: "u" );
	public readonly FloatParam BaseLength = new( "Base length", 4f, 0.01f, unit: "u" );
	public readonly FloatParam FlangeA = new( "Left flange", 1.5f, 0f, unit: "u" );
	public readonly FloatParam AngleA = new( "Left angle", 90f, 0f, 180f, unit: "deg" );
	public readonly FloatParam FlangeB = new( "Right flange", 1.5f, 0f, unit: "u" );
	public readonly FloatParam AngleB = new( "Right angle", 90f, 0f, 180f, unit: "deg" );
	public readonly FloatParam BendRadius = new( "Bend radius", 0.06f, 0f, unit: "u" );
	public readonly FloatParam KFactor = new( "K-factor", 0.44f, 0f, 1f );
	public readonly BoolParam FlatPattern = new( "Flat pattern", false );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Thickness, Width, BaseLength, FlangeA, AngleA, FlangeB, AngleB, BendRadius, KFactor, FlatPattern,
	};

	/// <summary>Bend allowance of one bend: the length of sheet the bend uses up.</summary>
	public static float BendAllowance( float angleDeg, float radius, float k, float thickness ) =>
		angleDeg * MathF.PI / 180f * (radius + k * thickness);

	static bool Has( FloatParam length, FloatParam angle ) => length.Clamped > 0f && angle.Clamped > 0f;

	/// <summary>Length of the flat blank, end to end.</summary>
	public float FlatLength
	{
		get
		{
			float t = Thickness.Clamped, r = BendRadius.Clamped, k = KFactor.Clamped, len = BaseLength.Clamped;
			if ( Has( FlangeA, AngleA ) ) len += FlangeA.Clamped + BendAllowance( AngleA.Clamped, r, k, t );
			if ( Has( FlangeB, AngleB ) ) len += FlangeB.Clamped + BendAllowance( AngleB.Clamped, r, k, t );
			return len;
		}
	}

	protected override void Execute( FeatureContext ctx )
	{
		float t = Thickness.Clamped, r = BendRadius.Clamped;
		bool flat = FlatPattern.Value;
		var mid = new List<(Vec2 P, Vec2 D)>();
		var p = Vec2.Zero;
		bool a = Has( FlangeA, AngleA ), b = Has( FlangeB, AngleB );
		float aRad = AngleA.Clamped * MathF.PI / 180f, bRad = AngleB.Clamped * MathF.PI / 180f;

		// Walk the middle of the sheet from the left tip to the right tip. Every bend turns left
		// (towards +z), so both flanges stand up on the same side.
		var d = a && !flat ? Rot( new Vec2( 1, 0 ), -aRad ) : new Vec2( 1, 0 );
		mid.Add( (p, d) );
		if ( a )
		{
			p += d * FlangeA.Clamped; mid.Add( (p, d) );
			Bend( mid, ref p, ref d, aRad, r, t, KFactor.Clamped, flat );
		}
		p += d * BaseLength.Clamped; mid.Add( (p, d) );
		if ( b )
		{
			Bend( mid, ref p, ref d, bRad, r, t, KFactor.Clamped, flat );
			p += d * FlangeB.Clamped; mid.Add( (p, d) );
		}

		// Inside face (left of travel) tip to tip, then the outside face back.
		var outline = new List<Vec2>();
		foreach ( var (q, dir) in mid ) outline.Add( q + Left( dir ) * (t * 0.5f) );
		for ( var i = mid.Count - 1; i >= 0; i-- ) outline.Add( mid[i].P - Left( mid[i].D ) * (t * 0.5f) );
		Dedupe( outline );

		// Base underside sits on z = 0.
		float w = Width.Clamped, lift = float.MaxValue;
		foreach ( var q in outline ) lift = MathF.Min( lift, q.y );
		lift = -lift;
		var mesh = Sweep( outline, w, lift, false );
		if ( mesh.SignedVolume() < 0f ) mesh = Sweep( outline, w, lift, true );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}

	static void Bend( List<(Vec2 P, Vec2 D)> mid, ref Vec2 p, ref Vec2 d, float angle, float r, float t, float k, bool flat )
	{
		if ( flat )
		{
			p += d * BendAllowance( angle * 180f / MathF.PI, r, k, t ); mid.Add( (p, d) );
			return;
		}
		var centre = p + Left( d ) * (r + t * 0.5f);
		int steps = Math.Max( 2, (int)MathF.Ceiling( angle / (MathF.PI / 16f) ) );
		var start = p - centre;
		for ( var s = 1; s <= steps; s++ )
		{
			float th = angle * s / steps;
			mid.Add( (centre + Rot( start, th ), Rot( d, th )) );
		}
		p = centre + Rot( start, angle ); d = Rot( d, angle );
	}

	static Vec2 Left( Vec2 d ) => new( -d.y, d.x );
	static Vec2 Rot( Vec2 v, float a ) => new( v.x * MathF.Cos( a ) - v.y * MathF.Sin( a ), v.x * MathF.Sin( a ) + v.y * MathF.Cos( a ) );

	static void Dedupe( List<Vec2> pts )
	{
		for ( var i = pts.Count - 1; i >= 0 && pts.Count > 3; i-- )
		{
			var n = pts[(i + 1) % pts.Count];
			if ( MathF.Abs( pts[i].x - n.x ) < 1e-6f && MathF.Abs( pts[i].y - n.y ) < 1e-6f ) pts.RemoveAt( i );
		}
	}

	static PolyMesh Sweep( List<Vec2> outline, float width, float lift, bool reverse )
	{
		var near = new List<Vec3>(); var far = new List<Vec3>();
		for ( var i = 0; i < outline.Count; i++ )
		{
			var q = outline[reverse ? outline.Count - 1 - i : i];
			near.Add( new Vec3( q.x, 0f, q.y + lift ) );
			far.Add( new Vec3( q.x, width, q.y + lift ) );
		}
		return Skinner.Skin( new[] { near, far }, capEnds: true, 0 );
	}
}
