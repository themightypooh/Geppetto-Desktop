#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Headless test (paintshow_test.gd): painted textures and painted weights show on the imported /
/// refreshed Body, not only while a brush is up. On a COPY of g1-spike.effigy it paints a red stroke
/// and a weight stroke through the paint slice's tools, saves, re-imports through
/// <see cref="EffigySceneImporter"/>, and checks the Body's material carries the painted texture and
/// the painted bone's skin weights grew. Also checks <see cref="GeppettoDocument.ApplyTo"/> and that a
/// hidden body doesn't break the painted-weights bind.
/// </summary>
public partial class paintshow_test : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	public Godot.Collections.Dictionary Run( string fixtureRes )
	{
		string dir = Path.Combine( Path.GetTempPath(), "geppetto-paintshow-" + Guid.NewGuid().ToString( "N" )[..8] );
		Directory.CreateDirectory( dir );
		try
		{
			string copy = Path.Combine( dir, "paintshow.effigy" );
			File.Copy( ProjectSettings.GlobalizePath( fixtureRes ), copy );
			Go( copy );
		}
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		finally
		{
			GeppettoDocument.CloseAll();
			try { Directory.Delete( dir, true ); } catch { }
		}
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	void Go( string path )
	{
		var importer = new EffigySceneImporter();

		// ---- before: the plain import ----
		var root0 = importer._ImportScene( path, 0, new Godot.Collections.Dictionary() ) as Node3D;
		var body0 = Body( root0 );
		Add( "fixture imports with a Body", body0?.Mesh is ArrayMesh, "" );
		if ( body0?.Mesh is not ArrayMesh mesh0 ) return;
		Add( "no paint texture before painting", PaintedSurfaces( mesh0 ) == 0, $"{PaintedSurfaces( mesh0 )}" );

		// ---- texture stroke ----
		var doc = GeppettoDocument.Get( path );
		var studio = doc.Studio;
		var target = studio.Bodies.Where( b => b.Visible && !b.IsReference && b.Mesh is { FaceCount: > 0 } )
			.OrderByDescending( b => b.Mesh.FaceCount ).First();
		var brush = new PaintBrushTool( doc, null, null );
		var r = PaintBrushTool.Remembered;
		r.Erasing = false; r.R = 1f; r.G = 0f; r.B = 0f; r.A = 1f; r.Strength = 1f; r.Radius = null;
		r.Channel = PaintChannel.Color;
		var (o, d, side) = AimAt( target.Mesh, target.Id, brush.BodyUnder );
		Add( "ray finds the paint target", o is not null, target.Name );
		if ( o is null ) { brush.Exit(); return; }
		brush.Press( o.Value, d, ctrl: false, shift: false );   // opens the part's paint layer
		Add( "brush opens a paint session", brush.Session != null, brush.BodyId ?? "none" );
		if ( brush.Session == null ) { brush.Exit(); return; }
		Stroke( ( a, b ) => brush.Press( a, b, false, false ), brush.Hover, brush.Release, o.Value, d, side, brush.Session.Radius );
		brush.Exit();   // the brush's preview goes away here: what shows now is the Body itself
		Add( "Body mesh carries the paint after the brush exits", BodyOf( studio, target.Id )?.Mesh.Paint != null, "" );

		// GeppettoDocument.ApplyTo (what every Refresh runs on open scenes) shows it.
		doc.ApplyTo( root0! );
		var applied = Body( root0 )?.Mesh as ArrayMesh;
		Add( "ApplyTo binds the paint texture", applied != null && PaintedSurfaces( applied ) > 0, $"{(applied == null ? -1 : PaintedSurfaces( applied ))}" );
		Add( "ApplyTo texture holds the red stroke", applied != null && HasRed( applied ), "" );

		// ---- weight stroke ----
		var wt = new PaintWeightsTool( doc, null, null );
		Add( "Paint Weights opens", wt.Open( 0 ) && wt.Session != null, "" );
		if ( wt.Session == null ) { wt.Exit(); return; }
		var s = wt.Session;
		wt.SetBrush( WeightBrushKind.Add );
		wt.SetStrength( 1f );
		var (wo, wd, wside) = AimAt( s.Mesh, "", ( a, b ) => s.Hover( a, b ) is null ? null : "" );
		Add( "ray hits the skinned mesh", wo is not null, "" );
		if ( wo is null ) { wt.Exit(); return; }
		var hit = s.Hover( wo.Value, wd )!.Value;
		var face = s.Mesh.Faces[hit.FaceIndex];
		int bone = Enumerable.Range( 0, s.Skeleton.Count ).OrderBy( b => face.Indices.Sum( v => s.Influence( b )[v] ) ).First();
		string boneName = s.Skeleton.Bones[bone].Name;
		wt.SetBone( bone );
		float before0 = BoneTotal( mesh0, Skel( root0 ), boneName );
		Stroke( ( a, b ) => wt.Press( a, b, false ), wt.Hover, wt.Release, wo.Value, wd, wside, s.Radius );
		wt.Exit();
		Add( "weight layer written", studio.WeightPaint is { Count: > 0 }, $"{studio.WeightPaint?.Count}" );

		var saved = doc.Bridge.SaveAll();
		Add( "save writes the .effigy", saved.Any( x => x.Outcome == EffigySaveOutcome.Written ), string.Join( ", ", saved.Select( x => $"{x.Kind}:{x.Outcome}" ) ) );
		GeppettoDocument.CloseAll();

		// ---- after: re-import from disk ----
		var root1 = importer._ImportScene( path, 0, new Godot.Collections.Dictionary() ) as Node3D;
		var mesh1 = Body( root1 )?.Mesh as ArrayMesh;
		Add( "re-import has a Body", mesh1 != null, "" );
		if ( mesh1 == null ) return;
		Add( "re-imported Body material has the painted texture", PaintedSurfaces( mesh1 ) > 0, $"{PaintedSurfaces( mesh1 )} surface(s)" );
		Add( "re-imported texture holds the red stroke", HasRed( mesh1 ), "" );
		float after1 = BoneTotal( mesh1, Skel( root1 ), boneName );
		Add( $"skin weights for {boneName} grew in the import", after1 > before0 + 1e-3f, $"{before0:0.###} -> {after1:0.###}" );

		// The texture is a real resource on the material (saved with an imported scene), not a node.
		var tex = PaintTexture( mesh1 );
		Add( "paint texture is an ImageTexture resource on the material", tex is ImageTexture && tex.GetImage() is { } img && img.GetWidth() > 0, $"{tex?.GetWidth()}x{tex?.GetHeight()}" );
		var packed = new PackedScene();
		Add( "imported scene packs (bakes) with the texture", root1 != null && packed.Pack( root1 ) == Error.Ok, "" );

		// A hidden body: the painted-weights bind slices the full merge back to the visible bodies.
		var reread = StudioDocument.ReadFile( path );
		reread.Rebuild();
		var hide = reread.Bodies.FirstOrDefault( b => !b.IsReference && b.Id != target.Id && b.Mesh is { VertexCount: > 0 } );
		if ( hide != null )
		{
			hide.Visible = false;
			CpuMesh? cpu = null;
			string err = "";
			try { cpu = CpuMesh.FromStudio( reread, 0, path ); } catch ( Exception e ) { err = e.Message; }
			Add( "hidden body: painted bind still skins the visible mesh", cpu is { HasSkin: true }, err );
		}

		root0?.Free();
		root1?.Free();
	}

	// ---- helpers ----

	static MeshInstance3D? Body( Node? root ) => root?.FindChild( "Body", true, false ) as MeshInstance3D;
	static Skeleton3D? Skel( Node? root ) => root?.FindChild( "*", true, false ) is Skeleton3D s ? s : FindSkel( root );

	static Skeleton3D? FindSkel( Node? n )
	{
		if ( n == null ) return null;
		if ( n is Skeleton3D s ) return s;
		foreach ( var c in n.GetChildren() ) if ( FindSkel( c ) is { } k ) return k;
		return null;
	}

	static Texture2D? PaintTexture( ArrayMesh m )
	{
		for ( int i = 0; i < m.GetSurfaceCount(); i++ )
			if ( m.SurfaceGetMaterial( i ) is StandardMaterial3D mat && mat.AlbedoTexture is { ResourceName: "paint_color" } t ) return t;
		return null;
	}

	static int PaintedSurfaces( ArrayMesh m )
	{
		int n = 0;
		for ( int i = 0; i < m.GetSurfaceCount(); i++ )
			if ( m.SurfaceGetMaterial( i ) is StandardMaterial3D { AlbedoTexture.ResourceName: "paint_color" } ) n++;
		return n;
	}

	static bool HasRed( ArrayMesh m )
	{
		var img = PaintTexture( m )?.GetImage();
		if ( img == null ) return false;
		if ( img.IsCompressed() ) img.Decompress();
		var data = img.GetData();
		int texels = img.GetWidth() * img.GetHeight();
		for ( int i = 0; i < texels; i++ )
			if ( data[i * 4] > 200 && data[i * 4 + 1] < 60 && data[i * 4 + 2] < 60 ) return true;
		return false;
	}

	/// <summary>Sum of one bone's weight over every Godot vertex of the mesh.</summary>
	static float BoneTotal( ArrayMesh m, Skeleton3D? skel, string bone )
	{
		int b = skel?.FindBone( bone ) ?? -1;
		if ( b < 0 ) return -1f;
		float total = 0f;
		for ( int i = 0; i < m.GetSurfaceCount(); i++ )
		{
			var arrays = m.SurfaceGetArrays( i );
			if ( arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil ) continue;
			var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
			var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
			for ( int k = 0; k < bones.Length && k < weights.Length; k++ )
				if ( bones[k] == b ) total += weights[k];
		}
		return total;
	}

	static void Stroke( Action<Vec3, Vec3> press, Func<Vec3, Vec3, MeshHit?> hover, Action release, Vec3 o, Vec3 d, Vec3 side, float radius )
	{
		hover( o, d );
		press( o, d );
		for ( int i = 1; i <= 6; i++ ) hover( o + side * (radius * 0.4f * i), d );
		release();
	}

	static (Vec3? O, Vec3 D, Vec3 Side) AimAt( PolyMesh mesh, string id, Func<Vec3, Vec3, string?> under )
	{
		float size = MathF.Max( mesh.BoundsDiagonal, 1f );
		foreach ( var face in mesh.Faces.OrderByDescending( f => mesh.FaceArea( f ) ).Take( 40 ) )
		{
			var c = Vec3.Zero;
			foreach ( var vi in face.Indices ) c += mesh.Positions[vi];
			c /= face.Indices.Length;
			var n = mesh.FaceNormal( face ).Normal;
			var o = c + n * (size * 2f);
			if ( under( o, -n ) == id )
			{
				var refAxis = MathF.Abs( n.z ) > 0.9f ? new Vec3( 1, 0, 0 ) : new Vec3( 0, 0, 1 );
				return (o, -n, Vec3.Cross( n, refAxis ).Normal);
			}
		}
		return (null, default, default);
	}

	static Body? BodyOf( PartStudio studio, string id ) => studio.Bodies.FirstOrDefault( b => b.Id == id );

	void Add( string name, bool ok, string value )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } } );
	}
}
#endif
