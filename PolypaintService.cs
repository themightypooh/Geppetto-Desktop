using System;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Per-vertex polypaint: colour lives on vertices, so it is only as fine as the
/// mesh — the tradeoff the kernel's PolyMesh docs already state, and the reason
/// this is a sculpt companion, not a replacement for texture paint.
///
/// Two calling styles, both kept:
/// - Copy style (<see cref="Paint"/>): returns a new mesh, for one-off dabs.
/// - Session style (<see cref="ApplyDab"/>): mutates the working mesh in place and
///   returns how many vertices it touched, for a sculpt stroke's per-sample dab.
///   This is the hook SculptSession's per-sample Dab can call to sculpt form and
///   paint vertex colour in a single pass (roadmap E2.2); SculptSession itself is
///   another lane's file and is not edited here.
/// </summary>
public static class PolypaintService
{
	public sealed class Brush
	{
		public Vec3 Center { get; set; }
		public float Radius { get; set; } = 1f;
		public Vec4 Color { get; set; } = new Vec4( 1, 0, 0, 1 );
		public float Strength { get; set; } = 1f;
		public float Falloff { get; set; } = 1f;
	}

	/// <summary>
	/// One polypaint dab, in the terms a sculpt stroke already speaks: a centre,
	/// a radius, a colour and a strength. <see cref="CavityMask"/> is an optional
	/// per-vertex 0..1 array that multiplies the dab weight — 1 paints fully,
	/// 0 blocks paint — so a caller can confine a dab to crevices (pass cavity)
	/// or to ridges (pass 1 - cavity). Null means no masking.
	/// </summary>
	public sealed class DabOptions
	{
		public Vec3 Center { get; set; }
		public float Radius { get; set; } = 1f;
		public Vec4 Color { get; set; } = new Vec4( 1, 0, 0, 1 );
		public float Strength { get; set; } = 1f;
		public float Falloff { get; set; } = 1f;
		public float[] CavityMask { get; set; }
	}

	static void CheckBrush( float radius )
	{
		if ( radius <= 0f || !float.IsFinite( radius ) ) throw new ArgumentException( "radius must be positive" );
	}

	static float DabWeight( Vec3 pos, Vec3 center, float radius, float strength, float falloff, float mask )
	{
		float d = ( pos - center ).Length;
		if ( d > radius ) return 0f;
		float t = 1f - ( d / radius );
		t = MathF.Pow( t, falloff );
		float w = t * strength * mask;
		return w > 1f ? 1f : w < 0f ? 0f : w;
	}

	static Vec4 LerpColor( Vec4 from, Vec4 to, float w ) =>
		new Vec4(
			from.x + ( to.x - from.x ) * w,
			from.y + ( to.y - from.y ) * w,
			from.z + ( to.z - from.z ) * w,
			from.w + ( to.w - from.w ) * w );

	/// <summary>Copy style: returns a new mesh with the dab applied. Original behaviour, kept.</summary>
	public static PolyMesh Paint( PolyMesh source, Brush brush )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		CheckBrush( brush.Radius );

		var outMesh = source.Clone();
		if ( !outMesh.HasVertexColors )
		{
			outMesh.VertexColors = new Vec4[ source.Positions.Count ];
			for ( int i = 0; i < outMesh.VertexColors.Length; i++ ) outMesh.VertexColors[ i ] = new Vec4( 1, 1, 1, 1 );
		}

		for ( int i = 0; i < source.Positions.Count; i++ )
		{
			float w = DabWeight( source.Positions[ i ], brush.Center, brush.Radius, brush.Strength, brush.Falloff, 1f );
			if ( w <= 0f ) continue;
			outMesh.VertexColors[ i ] = LerpColor( outMesh.VertexColors[ i ], brush.Color, w );
		}
		return outMesh;
	}

	/// <summary>
	/// Session style: paints the dab onto <paramref name="mesh"/> in place and
	/// returns the number of vertices whose colour actually changed. A mesh with
	/// no vertex colours yet is seeded white first. Throws on a null mesh/dab,
	/// a non-positive radius, or a cavity mask whose length does not match the
	/// vertex count — a wrong-length mask silently painting the wrong vertices
	/// would be worse than failing.
	/// </summary>
	public static int ApplyDab( PolyMesh mesh, DabOptions dab )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( dab is null ) throw new ArgumentNullException( nameof( dab ) );
		CheckBrush( dab.Radius );
		if ( dab.CavityMask is not null && dab.CavityMask.Length != mesh.Positions.Count )
			throw new ArgumentException( "CavityMask length must match the vertex count" );

		if ( !mesh.HasVertexColors )
		{
			mesh.VertexColors = new Vec4[ mesh.Positions.Count ];
			for ( int i = 0; i < mesh.VertexColors.Length; i++ ) mesh.VertexColors[ i ] = new Vec4( 1, 1, 1, 1 );
		}

		int painted = 0;
		for ( int i = 0; i < mesh.Positions.Count; i++ )
		{
			float mask = dab.CavityMask is null ? 1f : dab.CavityMask[ i ];
			float w = DabWeight( mesh.Positions[ i ], dab.Center, dab.Radius, dab.Strength, dab.Falloff, mask );
			if ( w <= 0f ) continue;
			mesh.VertexColors[ i ] = LerpColor( mesh.VertexColors[ i ], dab.Color, w );
			painted++;
		}
		return painted;
	}

	/// <summary>Copy style for a dab: clone, then <see cref="ApplyDab"/>.</summary>
	public static PolyMesh PaintDab( PolyMesh source, DabOptions dab )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		var m = source.Clone();
		ApplyDab( m, dab );
		return m;
	}

	/// <summary>
	/// One sculpt-plus-paint pass: run the dyntopo brush stroke, then the
	/// polypaint dab on the result, so form and colour land together without
	/// the caller juggling two meshes. Topology does not change under
	/// <see cref="DyntopoService.Sculpt"/> (it moves vertices only), so the
	/// dab's vertex indices still line up after the stroke.
	/// </summary>
	public static PolyMesh SculptAndPaint( PolyMesh source, DyntopoService.SculptOptions sculpt, DabOptions dab )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		var m = DyntopoService.Sculpt( source, sculpt );
		ApplyDab( m, dab );
		return m;
	}
}
