using System;
using Effigy;

namespace Marionette.Desktop;

public static class SdfShapes
{
	public static PolyMesh Box( float sx, float sy, float sz )
	{
		var m = new PolyMesh();
		float hx=sx/2, hy=sy/2, hz=sz/2;
		Vec3[] c = {
			new(-hx,-hy,-hz), new(hx,-hy,-hz), new(hx,hy,-hz), new(-hx,hy,-hz),
			new(-hx,-hy,hz), new(hx,-hy,hz), new(hx,hy,hz), new(-hx,hy,hz)
		};
		foreach ( var v in c ) m.Positions.Add( v );
		int[] faces = {0,1,2,3, 4,5,6,7, 0,1,5,4, 2,3,7,6, 0,3,7,4, 1,2,6,5};
		for ( int i=0;i<faces.Length;i+=4 )
			m.Faces.Add( new Face( new[]{faces[i],faces[i+1],faces[i+2],faces[i+3]} ) );
		return m;
	}

	public static PolyMesh Cylinder( float r, float h, int segs=16 )
	{
		var m = new PolyMesh();
		float hh=h/2;
		// bottom ring
		for ( int i=0;i<segs;i++ ){ float a=2*MathF.PI*i/segs; m.Positions.Add(new Vec3(r*MathF.Cos(a), r*MathF.Sin(a), -hh)); }
		// top ring
		for ( int i=0;i<segs;i++ ){ float a=2*MathF.PI*i/segs; m.Positions.Add(new Vec3(r*MathF.Cos(a), r*MathF.Sin(a), hh)); }
		// bottom center, top center
		int bc=m.Positions.Count; m.Positions.Add(new Vec3(0,0,-hh));
		int tc=m.Positions.Count; m.Positions.Add(new Vec3(0,0,hh));
		for ( int i=0;i<segs;i++ )
		{
			int j=(i+1)%segs;
			// side quad
			m.Faces.Add(new Face(new[]{i, j, segs+j, segs+i}));
			// bottom fan tri
			m.Faces.Add(new Face(new[]{bc, j, i}));
			// top fan tri
			m.Faces.Add(new Face(new[]{tc, segs+i, segs+j}));
		}
		return m;
	}

	public static PolyMesh Sphere( float r, int stacks=12, int slices=16 )
	{
		var m = new PolyMesh();
		for ( int i = 0; i <= stacks; i++ )
		{
			float phi = ( float ) Math.PI * i / stacks;
			for ( int j = 0; j <= slices; j++ )
			{
				float th = 2f * ( float ) Math.PI * j / slices;
				m.Positions.Add( new Vec3( r * MathF.Sin( phi ) * MathF.Cos( th ), r * MathF.Sin( phi ) * MathF.Sin( th ), r * MathF.Cos( phi ) ) );
			}
		}
		for ( int i = 0; i < stacks; i++ )
		for ( int j = 0; j < slices; j++ )
		{
			int a = i * ( slices + 1 ) + j;
			int b = a + 1;
			int c = a + ( slices + 1 );
			int d = c + 1;
			m.Faces.Add( new Face( new[] { a, b, d, c } ) );
		}
		return m;
	}

	public static PolyMesh Cone( float r, float h, int segs=16 )
	{
		var m = new PolyMesh();
		float hh = h / 2f;
		for ( int i = 0; i < segs; i++ )
		{
			float a = 2f * MathF.PI * i / segs;
			m.Positions.Add( new Vec3( r * MathF.Cos( a ), r * MathF.Sin( a ), -hh ) );
		}
		int apex = m.Positions.Count; m.Positions.Add( new Vec3( 0, 0, hh ) );
		int center = m.Positions.Count; m.Positions.Add( new Vec3( 0, 0, -hh ) );
		for ( int i = 0; i < segs; i++ )
		{
			int j = ( i + 1 ) % segs;
			m.Faces.Add( new Face( new[] { i, j, apex } ) );
			m.Faces.Add( new Face( new[] { center, j, i } ) );
		}
		return m;
	}

	public static PolyMesh Torus( float R, float r, int rings=24, int segs=16 )
	{
		var m = new PolyMesh();
		for ( int i=0;i<rings;i++ )
		{
			float u=2*MathF.PI*i/rings;
			for ( int j=0;j<segs;j++ )
			{
				float v=2*MathF.PI*j/segs;
				float x=(R+r*MathF.Cos(v))*MathF.Cos(u);
				float y=(R+r*MathF.Cos(v))*MathF.Sin(u);
				float z=r*MathF.Sin(v);
				m.Positions.Add(new Vec3(x,y,z));
			}
		}
		for ( int i=0;i<rings;i++ )
		{
			for ( int j=0;j<segs;j++ )
			{
				int a=i*segs+j;
				int b=i*segs+(j+1)%segs;
				int c=((i+1)%rings)*segs+(j+1)%segs;
				int d=((i+1)%rings)*segs+j;
				m.Faces.Add(new Face(new[]{a,b,c,d}));
			}
		}
		return m;
	}
}
