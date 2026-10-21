using System;
using Effigy;

namespace Marionette.Desktop;

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

	public static PolyMesh Paint( PolyMesh source, Brush brush )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( brush.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );

		var outMesh = new PolyMesh();
		outMesh.Positions.AddRange( source.Positions );
		outMesh.Faces.AddRange( source.Faces );
		outMesh.Skin = source.Skin;
		outMesh.VertexColors = source.HasVertexColors
			? (Vec4[])source.VertexColors.Clone()
			: new Vec4[ source.Positions.Count ];
		if ( !source.HasVertexColors )
			for ( int i = 0; i < outMesh.VertexColors.Length; i++ ) outMesh.VertexColors[ i ] = new Vec4( 1, 1, 1, 1 );

		for ( int i = 0; i < source.Positions.Count; i++ )
		{
			float d = ( source.Positions[ i ] - brush.Center ).Length;
			if ( d > brush.Radius ) continue;
			float t = 1f - ( d / brush.Radius );
			t = MathF.Pow( t, brush.Falloff );
			float w = t * brush.Strength;
			if ( w > 1f ) w = 1f;
			var c = outMesh.VertexColors[ i ];
			outMesh.VertexColors[ i ] = new Vec4(
				c.x + ( brush.Color.x - c.x ) * w,
				c.y + ( brush.Color.y - c.y ) * w,
				c.z + ( brush.Color.z - c.z ) * w,
				c.w + ( brush.Color.w - c.w ) * w );
		}
		return outMesh;
	}
}
