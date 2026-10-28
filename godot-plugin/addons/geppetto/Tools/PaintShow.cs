#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Shows a document's texture paint on the Body's own materials, outside the paint brush: the importer
/// and <see cref="GeppettoDocument.ApplyTo"/> call it after building the ArrayMesh. Same chain as the
/// desktop viewer's DocumentSession.ApplyPaintTextures and s&amp;box's compiled paint .vmat
/// (PaintTextureSet): every visible painted body's composite canvas (<c>body.Mesh.Paint</c>, all its
/// Paint layers already composited by the rebuild) becomes an opaque colour texture, plus roughness,
/// metalness and a height-derived normal map where those channels were painted, bound on the material
/// slots that body's faces wear. The scalar factors go to 1/white because the maps hold absolute values.
///
/// The textures are ordinary <see cref="ImageTexture"/> resources on the surface materials, so an
/// imported scene saves them as real (embedded) resources, not runtime nodes. Two painted bodies on one
/// slot share it, first one wins (a limit of slot materials, as in the desktop viewer).
/// </summary>
public static class PaintShow
{
	sealed class Maps
	{
		public ImageTexture Color = null!;
		public ImageTexture? Rough, Metal, Normal;
		public ulong Stamp;
	}

	// Keyed on the canvas object plus a hash of its pixels: a rebuild that did not touch the paint hands
	// back the same canvas, so a Refresh for an unrelated edit does not re-bake a 1024² texture (s&box's BuildPaintPreview cache), while a stroke,
	// undo or mask change that recomposites a reused canvas in place still re-bakes.
	static readonly ConditionalWeakTable<PaintCanvas, Maps> Cache = new();

	/// <summary>Binds the painted maps onto <paramref name="mesh"/>'s surfaces (built from
	/// <paramref name="cpu"/> by <see cref="EffigyMeshBridge.ToGodotArrayMesh"/>). Returns how many
	/// surfaces got paint.</summary>
	public static int Apply( ArrayMesh mesh, CpuMesh cpu, PartStudio studio )
	{
		var paintBySlot = new Dictionary<int, PaintCanvas>();
		foreach ( var body in studio.Bodies )
		{
			if ( body is null || !body.Visible || body.IsReference || body.Mesh?.Paint is not { } canvas ) continue;
			foreach ( var face in body.Mesh.Faces )
				paintBySlot.TryAdd( face.Material, canvas );
		}
		if ( paintBySlot.Count == 0 || cpu.Batches == null ) return 0;

		int applied = 0, surface = 0;
		foreach ( var batch in cpu.Batches )
		{
			// ToGodotArrayMesh makes one surface per non-empty batch, in order.
			if ( batch.IndexCount == 0 ) continue;
			int s = surface++;
			if ( s >= mesh.GetSurfaceCount() ) break;
			if ( !paintBySlot.TryGetValue( batch.MaterialSlot, out var canvas ) ) continue;
			if ( mesh.SurfaceGetMaterial( s ) is not StandardMaterial3D mat )
			{
				mat = new StandardMaterial3D { ResourceName = batch.MaterialName ?? $"slot_{batch.MaterialSlot}" };
				mesh.SurfaceSetMaterial( s, mat );
			}
			Bind( mat, MapsFor( canvas ) );
			applied++;
		}
		return applied;
	}

	static void Bind( StandardMaterial3D mat, Maps maps )
	{
		mat.AlbedoTexture = maps.Color;
		mat.AlbedoColor = new Color( 1f, 1f, 1f, mat.AlbedoColor.A );
		if ( maps.Rough != null )
		{
			mat.RoughnessTexture = maps.Rough;
			mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red;
			mat.Roughness = 1f;
		}
		if ( maps.Metal != null )
		{
			mat.MetallicTexture = maps.Metal;
			mat.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Red;
			mat.Metallic = 1f;
		}
		if ( maps.Normal != null )
		{
			// Same green convention as the s&box export (PaintTextureSet.HeightToNormal: +green along
			// -dP/dv), which is Godot's too once the bridge's tangent sign flip is applied.
			mat.NormalEnabled = true;
			mat.NormalTexture = maps.Normal;
			mat.NormalScale = 1f;
		}
	}

	// FNV-1a over 8-byte words of every map: ~1 ms for a 1024² canvas, cheap next to a rebuild.
	static ulong Stamp( PaintCanvas c )
	{
		ulong h = 1469598103934665603UL;
		void Mix( PaintCanvas? m )
		{
			if ( m == null ) { h = (h ^ 0x9E3779B97F4A7C15UL) * 1099511628211UL; return; }
			h = (h ^ (ulong)m.Width ^ ((ulong)m.Height << 32)) * 1099511628211UL;
			foreach ( var w in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>( m.Rgba.AsSpan() ) )
				h = (h ^ w) * 1099511628211UL;
		}
		Mix( c ); Mix( c.RoughnessMap ); Mix( c.MetalnessMap ); Mix( c.HeightMap );
		return h;
	}

	static Maps MapsFor( PaintCanvas canvas )
	{
		ulong stamp = Stamp( canvas );
		if ( Cache.TryGetValue( canvas, out var hit ) && hit.Stamp == stamp ) return hit;
		var maps = new Maps
		{
			Stamp = stamp,
			Color = Texture( "paint_color", canvas.Width, canvas.Height, PaintLayers.Opaque( canvas, PaintChannel.Color ) ),
			Rough = canvas.RoughnessMap is { } r ? Texture( "paint_rough", r.Width, r.Height, PaintLayers.Opaque( canvas, PaintChannel.Roughness ) ) : null,
			Metal = canvas.MetalnessMap is { } m ? Texture( "paint_metal", m.Width, m.Height, PaintLayers.Opaque( canvas, PaintChannel.Metalness ) ) : null,
			Normal = canvas.HeightMap is { } h ? Texture( "paint_normal", h.Width, h.Height, PaintTextureSet.HeightToNormal( h ) ) : null,
		};
		Cache.AddOrUpdate( canvas, maps );
		return maps;
	}

	static ImageTexture Texture( string name, int w, int h, byte[] rgba )
	{
		var img = Image.CreateFromData( w, h, false, Image.Format.Rgba8, rgba );
		img.GenerateMipmaps();
		var tex = ImageTexture.CreateFromImage( img );
		tex.ResourceName = name;
		return tex;
	}
}
#endif
