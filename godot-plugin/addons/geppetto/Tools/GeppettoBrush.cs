#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// What the paint and weight brushes share: turning a viewport ray into the kernel's space (inches,
/// Z-up, relative to the character's Body), drawing s&amp;box's brush ring over the viewport, and the
/// while-the-tool-is-active preview of the mesh being painted.
///
/// Godot local = 0.0254 * (y, z, x) of Effigy (x, y, z), the same mapping EffigyMeshBridge uses.
/// </summary>
public static class GeppettoBrush
{
	const float InchesToMetres = 0.0254f;

	public static Vector3 ToGodot( Vec3 p ) => new Vector3( p.y, p.z, p.x ) * InchesToMetres;
	public static Vector3 ToGodotDir( Vec3 d ) => new( d.y, d.z, d.x );
	public static Vec3 ToEffigy( Vector3 g ) => new( g.Z / InchesToMetres, g.X / InchesToMetres, g.Y / InchesToMetres );
	public static Vec3 ToEffigyDir( Vector3 g ) => new Vec3( g.Z, g.X, g.Y ).Normal;

	/// <summary>The mouse ray under <paramref name="screen"/> in the kernel's space for the mesh drawn by
	/// <paramref name="space"/> (the character's Body). Null space = the world is the kernel's space.</summary>
	public static (Vec3 Origin, Vec3 Direction) Ray( Camera3D camera, Vector2 screen, Node3D? space )
	{
		var o = camera.ProjectRayOrigin( screen );
		var d = camera.ProjectRayNormal( screen );
		if ( space != null )
		{
			var inv = space.GlobalTransform.AffineInverse();
			o = inv * o;
			d = (inv.Basis * d).Normalized();
		}
		return (ToEffigy( o ), ToEffigyDir( d ));
	}

	/// <summary>s&amp;box's brush cursor: a 40-segment ring in the surface plane at the hit, lifted a hair
	/// off it, plus a short tick along the normal (0.35 x radius). Projected onto the 2D overlay.</summary>
	public static void DrawRing( Control overlay, Camera3D camera, Node3D? space, MeshHit hit, float radius, Color colour )
	{
		var n = hit.Normal.Normal;
		var centre = hit.Point;
		var reference = MathF.Abs( n.z ) > 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
		var right = Vec3.Cross( n, reference ).Normal;
		var up = Vec3.Cross( n, right ).Normal;
		var lift = n * (radius * 0.01f);
		const int Segments = 40;

		var ring = new List<Vector2>( Segments + 1 );
		for ( int i = 0; i <= Segments; i++ )
		{
			float a = i / (float)Segments * MathF.PI * 2f;
			var p = centre + (right * MathF.Cos( a ) + up * MathF.Sin( a )) * radius + lift;
			if ( !Project( camera, space, p, out var s ) ) return;
			ring.Add( s );
		}
		overlay.DrawPolyline( ring.ToArray(), colour, 1.5f, true );

		if ( Project( camera, space, centre, out var c0 ) && Project( camera, space, centre + n * (radius * 0.35f), out var c1 ) )
			overlay.DrawLine( c0, c1, colour, 1.5f, true );
	}

	static bool Project( Camera3D camera, Node3D? space, Vec3 p, out Vector2 screen )
	{
		var world = ToGodot( p );
		if ( space != null ) world = space.GlobalTransform * world;
		screen = default;
		if ( camera.IsPositionBehind( world ) ) return false;
		screen = camera.UnprojectPosition( world );
		return true;
	}

	/// <summary>
	/// A Godot mesh of one kernel <see cref="PolyMesh"/> with its own UVs, for the paint/weight preview
	/// (the textured canvas needs the painted body's own atlas, which the merged import mesh does not
	/// keep apart). Normals are smooth across soft edges and split at hard ones (over 40 degrees).
	/// </summary>
	public static ArrayMesh BuildMesh( PolyMesh mesh, Material material )
	{
		var verts = new List<Vector3>();
		var normals = new List<Vector3>();
		var uvs = new List<Vector2>();
		var smooth = mesh.ComputeVertexNormals();
		float hard = MathF.Cos( 40f * MathF.PI / 180f );
		var corner = new List<Vec3>( 8 );

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 ) continue;
			corner.Clear();
			foreach ( var vi in face.Indices ) corner.Add( mesh.Positions[vi] );
			var fn = mesh.FaceNormal( face );
			List<(int A, int B, int C)> tris;
			try { tris = Triangulate.Face( corner ); }
			catch { continue; }

			foreach ( var (a, b, c) in tris )
				// Reversed winding, like EffigyMeshBridge: the axis swap keeps handedness, Godot's front
				// face is the other way round.
				foreach ( int k in new[] { a, c, b } )
				{
					int vi = face.Indices[k];
					verts.Add( ToGodot( mesh.Positions[vi] ) );
					var vn = vi < smooth.Length ? smooth[vi] : fn;
					normals.Add( ToGodotDir( Vec3.Dot( vn, fn ) >= hard ? vn : fn ).Normalized() );
					var uv = face.UVs is { Length: > 0 } && k < face.UVs.Length ? face.UVs[k] : new Vec2( 0, 0 );
					uvs.Add( new Vector2( uv.x, uv.y ) );
				}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize( (int)Mesh.ArrayType.Max );
		arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		var am = new ArrayMesh();
		if ( verts.Count > 0 )
		{
			am.AddSurfaceFromArrays( Mesh.PrimitiveType.Triangles, arrays );
			am.SurfaceSetMaterial( 0, material );
		}
		return am;
	}

	/// <summary>The character's merged mesh node (the importer names it "Body").</summary>
	public static MeshInstance3D? BodyNode( GeppettoCharacter? ch ) =>
		ch?.Root.FindChild( "Body", true, false ) as MeshInstance3D;
}

/// <summary>An RGBA8 canvas on the GPU, updated by dirty rectangle like s&amp;box's dynamic paint texture.</summary>
public sealed class GeppettoCanvasTexture
{
	readonly Image _image;
	public ImageTexture Texture { get; }
	public int Width { get; }
	public int Height { get; }

	public GeppettoCanvasTexture( int width, int height, byte[] rgba )
	{
		Width = width;
		Height = height;
		_image = Image.CreateFromData( width, height, false, Image.Format.Rgba8, rgba );
		Texture = ImageTexture.CreateFromImage( _image );
	}

	public void UpdateRect( int x, int y, int w, int h, byte[] rgba )
	{
		var sub = Image.CreateFromData( w, h, false, Image.Format.Rgba8, rgba );
		_image.BlitRect( sub, new Rect2I( 0, 0, w, h ), new Vector2I( x, y ) );
		Texture.Update( _image );
	}

	public void UpdateAll( byte[] rgba )
	{
		_image.SetData( Width, Height, false, Image.Format.Rgba8, rgba );
		Texture.Update( _image );
	}

	public StandardMaterial3D Material() => new()
	{
		AlbedoTexture = Texture,
		Roughness = 0.85f,
		TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
	};
}

/// <summary>
/// The brush preview in the 3D view: s&amp;box swapped the viewport's model for the painted mesh while a
/// brush was up; here a temporary MeshInstance3D stands next to the character's Body (same parent, same
/// transform) and the Body is hidden. The node has no owner, so it is never saved into the scene, and
/// <see cref="Close"/> frees it and shows the Body again.
/// </summary>
public sealed class GeppettoBrushPreview
{
	MeshInstance3D? _node;
	MeshInstance3D? _body;
	bool _bodyWasVisible;

	public bool IsShowing => _node != null && GodotObject.IsInstanceValid( _node );

	public void Show( MeshInstance3D? body, Mesh mesh )
	{
		if ( body == null || !GodotObject.IsInstanceValid( body ) || body.GetParent() is not Node parent ) return;
		if ( !IsShowing )
		{
			_node = new MeshInstance3D { Name = "GeppettoBrushPreview" };
			parent.AddChild( _node );
			_body = body;
			_bodyWasVisible = body.Visible;
		}
		_node!.Transform = body.Transform;
		_node.Mesh = mesh;
		body.Visible = false;
	}

	public void Close()
	{
		if ( _node != null && GodotObject.IsInstanceValid( _node ) )
		{
			_node.GetParent()?.RemoveChild( _node );
			_node.QueueFree();
		}
		_node = null;
		if ( _body != null && GodotObject.IsInstanceValid( _body ) ) _body.Visible = _bodyWasVisible;
		_body = null;
	}
}
#endif
