#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// The Top/Front/Right origin planes and the datum planes (PlaneFeature) in the viewport.
/// The origin planes are drawn in Onshape's colours (Top orange, Front blue, Right green, as
/// s&amp;box's EffigyViewport.DrawReferencePlanes), but only while a tool is waiting for a plane (<see cref="Wanted"/>).
/// Datum planes: A Plane feature makes no geometry, so without this a
/// new plane showed nothing at all and nothing could be drawn on it. Each one the last rebuild
/// published is drawn as a lilac square (its name on hover) while a plane is wanted, and Sketch
/// takes a click on one as the plane to draw on (s&amp;box's EffigyViewport.Planes).
/// </summary>
public static class GeppettoDatumPlanes
{
	/// <summary>Lilac: none of the three axis colours, so it isn't read as Top/Front/Right (as in s&amp;box).</summary>
	static readonly Color Lilac = new( 0.68f, 0.55f, 0.9f, 0.85f );

	/// <summary>Sketch lines nothing has used yet: the sketch amber, a little dimmer than the one being drawn.</summary>
	static readonly Color SketchLine = new( 1f, 0.66f, 0.2f, 0.8f );

	/// <summary>Top, Front, Right: the order of SketchFeature.Plane's choices.</summary>
	public static readonly (string Name, SketchPlane Plane, Color Colour)[] Origin =
	{
		( "Top", SketchPlane.XY, new Color( 0.85f, 0.55f, 0.25f, 0.6f ) ),
		( "Front", SketchPlane.XZ, new Color( 0.25f, 0.5f, 0.85f, 0.6f ) ),
		( "Right", SketchPlane.YZ, new Color( 0.25f, 0.78f, 0.45f, 0.6f ) ),
	};

	/// <summary>A plane a ray hit: Index 0-2 is an origin plane (Id "#Top" etc.), -1 a datum plane.</summary>
	public readonly record struct Hit( string Id, int Index, SketchPlane Plane, float T, string Name );

	/// <summary>The plane under the cursor while Sketch is choosing one, washed in its colour; null for none.</summary>
	public static string? Hovered { get; set; }

	/// <summary>Where the mouse was over the viewport when <see cref="Hovered"/> was set: the name goes there.</summary>
	public static Vector2 HoverAt { get; set; }

	/// <summary>The planes to show: every unsuppressed PlaneFeature the last rebuild published.</summary>
	public static IEnumerable<(PlaneFeature Feature, SketchPlane Plane)> Published( PartStudio studio ) =>
		studio.Features.OfType<PlaneFeature>()
			.Where( f => !f.Suppressed && f.Visible && studio.Planes.ContainsKey( f.Id ) )
			.Select( f => ( f, studio.Planes[f.Id] ) );

	/// <summary>Half the square's side in inches: sized to the model so a plane reads as a plane
	/// next to it without burying it.</summary>
	public static float HalfSize( Node? root )
	{
		if ( root?.FindChild( "Body", true, false ) is MeshInstance3D body && body.Mesh != null )
		{
			var size = body.GetAabb().Size;
			float metres = Math.Max( size.X, Math.Max( size.Y, size.Z ) );
			return Math.Max( 12f, metres / EffigyMeshBridge.ScaleToMetres * 0.8f );
		}
		return 24f;
	}

	/// <summary>Half the origin planes' side in inches: reaches past the model on every side of the
	/// origin, so the part sits on them rather than beside a small square.</summary>
	public static float OriginHalfSize( Node? root )
	{
		if ( root?.FindChild( "Body", true, false ) is MeshInstance3D body && body.Mesh is { } mesh && mesh.GetSurfaceCount() > 0 )
		{
			var a = body.GetAabb();
			float m = 0;
			foreach ( var v in new[] { a.Position, a.End } )
				m = Math.Max( m, Math.Max( Math.Abs( v.X ), Math.Max( Math.Abs( v.Y ), Math.Abs( v.Z ) ) ) );
			return Math.Max( 12f, m / EffigyMeshBridge.ScaleToMetres * 2.0f );
		}
		return 48f;   // a new, empty model: about two metres across, so the planes read at the editor's starting zoom
	}

	/// <summary>Where a ray (Effigy space) crosses a plane inside its square of half-side half, or null.</summary>
	static float? Cross( SketchPlane plane, Vec3 origin, Vec3 dir, float half )
	{
		float den = Vec3.Dot( plane.Normal, dir );
		if ( MathF.Abs( den ) < 1e-6f ) return null;
		float t = Vec3.Dot( plane.Origin - origin, plane.Normal ) / den;
		if ( t < 0 ) return null;
		var p = plane.ToPlane( origin + dir * t );
		return MathF.Abs( p.x ) > half || MathF.Abs( p.y ) > half ? null : t;
	}

	/// <summary>The nearest origin or datum plane a ray (Effigy space) crosses inside its square, or null.</summary>
	public static Hit? PickAny( PartStudio studio, Vec3 origin, Vec3 dir, float datumHalf, float originHalf )
	{
		// A datum plane is an intentional feature: if the ray hits one, it wins over the origin planes.
		if ( Pick( studio, origin, dir, datumHalf ) is { } dp )
		{
			var f = studio.Features.FirstOrDefault( x => x.Id == dp.Id );
			return new Hit( dp.Id, -1, dp.Plane, dp.T, string.IsNullOrEmpty( f?.Name ) ? "Plane" : f!.Name );
		}
		Hit? best = null;
		for ( int i = 0; i < Origin.Length; i++ )
			if ( Cross( Origin[i].Plane, origin, dir, originHalf ) is { } t && ( best is not { } b || t < b.T ) )
				best = new Hit( "#" + Origin[i].Name, i, Origin[i].Plane, t, Origin[i].Name );
		return best;
	}

	/// <summary>The nearest datum plane a ray (Effigy space) crosses inside its square, or null.</summary>
	public static (string Id, SketchPlane Plane, float T)? Pick( PartStudio studio, Vec3 origin, Vec3 dir, float half )
	{
		(string, SketchPlane, float)? best = null;
		foreach ( var (f, plane) in Published( studio ) )
		{
			float den = Vec3.Dot( plane.Normal, dir );
			if ( MathF.Abs( den ) < 1e-6f ) continue;
			float t = Vec3.Dot( plane.Origin - origin, plane.Normal ) / den;
			if ( t < 0 || ( best is { } b && t >= b.Item3 ) ) continue;
			var p = plane.ToPlane( origin + dir * t );
			if ( MathF.Abs( p.x ) > half || MathF.Abs( p.y ) > half ) continue;
			best = ( f.Id, plane, t );
		}
		return best;
	}

	public static void Draw( Control overlay, Camera3D cam, GeppettoCharacter character, bool planes )
	{
		if ( character.Doc is not { } doc || !GodotObject.IsInstanceValid( character.Root ) ) return;
		var xf = GeppettoPicker.BodyTransformOf( character.Root );
		float half = HalfSize( character.Root );
		// Finished sketches no feature has used yet. Without this a sketch vanished the moment it was
		// made, so there was nothing to see or extrude (s&amp;box shows them until something consumes them).
		var consumed = doc.Studio.ConsumedSketchIds();
		foreach ( var s in doc.Studio.Features.OfType<SketchFeature>() )
		{
			if ( s.Suppressed || !s.Visible || s.Error != null || consumed.Contains( s.Id ) ) continue;
			var sk = s.Sketch;
			foreach ( var c in sk.Curves )
			{
				var pts = c.Tessellate( sk, sk.Tolerance );
				var col = c.Construction ? SketchLine with { A = .35f } : SketchLine;
				for ( int i = 0; i + 1 < pts.Count; i++ )
				{
					var a = sk.Plane.ToWorld( pts[i] );
					var b = sk.Plane.ToWorld( pts[i + 1] );
					GeppettoPicker.DrawWorldLine( overlay, cam, xf * EffigyMeshBridge.TransformPosition( a.x, a.y, a.z ), xf * EffigyMeshBridge.TransformPosition( b.x, b.y, b.z ), col, 2f );
				}
			}
		}

		if ( !planes ) return;
		float originHalf = OriginHalfSize( character.Root );
		foreach ( var (name, plane, colour) in Origin )
			DrawSquare( overlay, cam, xf, plane, originHalf, colour, name, "#" + name );
		foreach ( var (f, plane) in Published( doc.Studio ) )
			DrawSquare( overlay, cam, xf, plane, half, Lilac, string.IsNullOrEmpty( f.Name ) ? f.TypeName : f.Name, f.Id );
	}

	/// <summary>True while a tool is waiting for a plane: Sketch before one is chosen, or the Plane tool
	/// picking its face. The planes are only drawn then, so the view is calm the rest of the time.</summary>
	public static bool Wanted( IGeppettoViewportTool? tool ) =>
		tool is CadSketch { Editing: null, Plane: null } or GeppettoPicker { Name: "Plane" };

	/// <summary>One plane: a faint wash in its colour and a thin outline. The hovered one is washed
	/// stronger with a solid outline, and its name is written in its colour beside the mouse (no box).</summary>
	static void DrawSquare( Control overlay, Camera3D cam, Transform3D xf, SketchPlane plane, float half, Color colour, string name, string id )
	{
		Vector3 W( float x, float y ) { var e = plane.ToWorld( new Vec2( x, y ) ); return xf * EffigyMeshBridge.TransformPosition( e.x, e.y, e.z ); }
		var c = new[] { W( half, half ), W( -half, half ), W( -half, -half ), W( half, -half ) };
		bool hot = Hovered == id;
		if ( c.All( p => !cam.IsPositionBehind( p ) ) )
			overlay.DrawColoredPolygon( c.Select( p => cam.UnprojectPosition( p ) ).ToArray(), colour with { A = hot ? 0.22f : 0.08f } );
		for ( int i = 0; i < 4; i++ )
			GeppettoPicker.DrawWorldLine( overlay, cam, c[i], c[( i + 1 ) % 4], colour with { A = hot ? 1f : 0.35f }, hot ? 2f : 1f );
		if ( !hot ) return;
		var font = overlay.GetThemeDefaultFont();
		int fontSize = overlay.GetThemeDefaultFontSize();
		var ts = font.GetStringSize( name, HorizontalAlignment.Left, -1, fontSize );
		var at = HoverAt + new Vector2( 16, 20 );
		at = new Vector2( Math.Clamp( at.X, 4, Math.Max( 4, overlay.Size.X - ts.X - 4 ) ), Math.Clamp( at.Y, 4, Math.Max( 4, overlay.Size.Y - ts.Y - 4 ) ) );
		var pos = at + new Vector2( 0, font.GetAscent( fontSize ) );
		// A dark 1px outline keeps the coloured name readable over a light model without a box.
		overlay.DrawStringOutline( font, pos, name, HorizontalAlignment.Left, -1, fontSize, 3, new Color( 0.05f, 0.05f, 0.06f, 0.8f ) );
		overlay.DrawString( font, pos, name, HorizontalAlignment.Left, -1, fontSize, ( colour with { A = 1 } ).Lightened( 0.4f ) );
	}
}
#endif
