using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Turn on <c>F_MORPH_SUPPORTED</c> in a .vmat - the material switch s&amp;box's shaders need before
/// the GPU applies a mesh's morphs (core/shaders/vr_common_features.fxc: <c>Feature( F_MORPH_SUPPORTED,
/// 0..1, "Animation" )</c>; Facepunch's human vmats set it as <c>F_MORPH_SUPPORTED 1</c> under an
/// "Animation" heading right after the shader line - see SkinMaterial).
///
/// ONLY EFFIGY'S OWN FILES. A material the export did not write (a library or engine vmat) is never
/// touched: <see cref="Enable"/> leaves any text without Effigy's AUTO-GENERATED header as it is.
/// The export calls this for every material on a mesh with shape keys; a writer that already sets
/// the flag (SkinMaterial with Morphs on) is left alone.
/// MORPHING BETWEEN LOOKS (v4 Lane H expansion). A look preset is Effigy's material
/// definition, and <see cref="SlotLook.Vmat"/> is how it becomes a .vmat, so morphing
/// between .vmat material definitions means blending two SlotLooks and rendering the
/// result -- see <see cref="Blend"/> and <see cref="MorphVmat"/>. The F_MORPH_SUPPORTED
/// flag work below is unrelated (mesh shape keys, not look blending) and is unchanged.
/// </summary>
public static class VmatMorph
{
	/// <summary>Whether <paramref name="vmat"/> already turns morphs on.</summary>
	public static bool IsOn( string vmat ) => vmat?.Contains( "F_MORPH_SUPPORTED 1" ) == true;

	/// <summary>The vmat with the flag on, or null when it is not Effigy's to change (or has no
	/// shader line to put it under). Unchanged text when it is already on.</summary>
	public static string Enable( string vmat )
	{
		if ( vmat is null || !vmat.Contains( "AUTO-GENERATED" ) )
			return null;

		if ( IsOn( vmat ) )
			return vmat;

		var shader = vmat.IndexOf( "shader \"", StringComparison.Ordinal );

		if ( shader < 0 )
			return null;

		var lineEnd = vmat.IndexOf( '\n', shader );

		if ( lineEnd < 0 )
			return null;

		var newline = lineEnd > 0 && vmat[lineEnd - 1] == '\r' ? "\r\n" : "\n";
		return vmat.Insert( lineEnd + 1, $"{newline}\t//---- Animation ----{newline}\tF_MORPH_SUPPORTED 1{newline}" );
	}

	/// <summary>
	/// Turn the flag on in every material file <paramref name="mesh"/>'s faces use, the same pass the
	/// editor's Compile runs after writing a DMX with shape keys. <paramref name="pathFor"/> maps a
	/// material name (what the slot exports as) to its file, or null when it has none. Returns one
	/// line per material that needed something said: turned on, or why it could not be.
	/// </summary>
	public static List<string> EnableFiles( PolyMesh mesh, Func<int, string> nameForSlot, Func<string, string> pathFor )
	{
		var said = new List<string>();

		if ( mesh is null || !mesh.HasMorphs )
			return said;

		foreach ( var slot in mesh.Faces.Select( f => f.Material ).Distinct().OrderBy( s => s ) )
		{
			var name = nameForSlot( slot );
			var path = string.IsNullOrWhiteSpace( name ) ? null : pathFor( name );

			if ( path is null || !File.Exists( path ) )
			{
				said.Add( $"slot {slot} ({name}) has no .vmat file at {path ?? "(no path)"}, so its shape keys will not show "
					+ $"until its material sets F_MORPH_SUPPORTED 1 (Animation). Assign one with `material {slot} materials/x.vmat`." );
				continue;
			}

			var text = File.ReadAllText( path );

			if ( IsOn( text ) )
				continue;

			if ( Enable( text ) is { } on )
			{
				File.WriteAllText( path, on );
				said.Add( $"{name}: F_MORPH_SUPPORTED on, for the shape keys" );
			}
			else
				said.Add( $"{name} is not an Effigy-written material, so its shape keys will not show until it sets F_MORPH_SUPPORTED 1 (Animation)" );
		}

		return said;
	}

	/// <summary>
	/// Blend two material looks into a new one: every numeric field lerps, colours lerp
	/// component-wise, and <see cref="SlotLook.ToonBands"/> rounds to the nearest band count.
	/// <paramref name="t"/> is clamped to 0..1, so a blend never extrapolates into an invalid
	/// look (negative opacity, roughness above 1). Neither input is mutated; the result is a
	/// fresh <see cref="SlotLook"/> whose <see cref="SlotLook.Preset"/> is null unless both
	/// inputs carry the same preset name -- a blend is a new look, not a preset.
	/// Only the fields <see cref="SlotLook"/> actually carries are blended, including
	/// ShadowTint and Iridescence (wave-2 look wiring added them to SlotLook).
	/// </summary>
	public static SlotLook Blend( SlotLook a, SlotLook b, float t )
	{
		if ( a is null ) return b?.Clone();
		if ( b is null ) return a.Clone();
		t = Math.Clamp( t, 0f, 1f );
		float L( float x, float y ) => x + (y - x) * t;
		return new SlotLook
		{
			Preset = !string.IsNullOrEmpty( a.Preset ) && string.Equals( a.Preset, b.Preset, StringComparison.OrdinalIgnoreCase ) ? a.Preset : null,
			Color = Vec3.Lerp( a.Color, b.Color, t ),
			Rough = Math.Clamp( L( a.Rough, b.Rough ), 0f, 1f ),
			Metal = Math.Clamp( L( a.Metal, b.Metal ), 0f, 1f ),
			Emit = Vec3.Lerp( a.Emit, b.Emit, t ),
			Toon = Math.Clamp( L( a.Toon, b.Toon ), 0f, 1f ),
			ToonBands = Math.Max( 1, (int)MathF.Round( L( a.ToonBands, b.ToonBands ), MidpointRounding.AwayFromZero ) ),
			ToonRim = Math.Clamp( L( a.ToonRim, b.ToonRim ), 0f, 1f ),
			Stylized = Math.Clamp( L( a.Stylized, b.Stylized ), 0f, 1f ),
			OutlineWidth = Math.Max( 0f, L( a.OutlineWidth, b.OutlineWidth ) ),
			OutlineColor = Vec3.Lerp( a.OutlineColor, b.OutlineColor, t ),
			Clearcoat = Math.Clamp( L( a.Clearcoat, b.Clearcoat ), 0f, 1f ),
			ClearcoatRoughness = Math.Max( 0f, L( a.ClearcoatRoughness, b.ClearcoatRoughness ) ),
			Subsurface = Math.Clamp( L( a.Subsurface, b.Subsurface ), 0f, 1f ),
			SubsurfaceColor = Vec3.Lerp( a.SubsurfaceColor, b.SubsurfaceColor, t ),
			Opacity = Math.Clamp( L( a.Opacity, b.Opacity ), 0f, 1f ),
			ShadowTint = Vec3.Lerp( a.ShadowTint, b.ShadowTint, t ),
			Iridescence = Math.Clamp( L( a.Iridescence, b.Iridescence ), 0f, 1f ),
		};
	}

	/// <summary>
	/// Blend two named presets (see <see cref="SlotLook.Presets"/>) into a new look.
	/// False, with <paramref name="look"/> null, when either name is not a preset.
	/// </summary>
	public static bool TryBlendPresets( string from, string to, float t, out SlotLook look )
	{
		if ( SlotLook.TryPreset( from, out var a ) && SlotLook.TryPreset( to, out var b ) ) { look = Blend( a, b, t ); return true; }
		look = null;
		return false;
	}

	/// <summary>
	/// The blended look's .vmat text, rendered through <see cref="SlotLook.Vmat"/> -- the same
	/// text <see cref="SlotLook.WriteVmats"/> writes for a slot. Null when both looks are null.
	/// There is deliberately no blend of two raw .vmat texts: Effigy has no parser from .vmat
	/// text back to a SlotLook, and hand-written .vmats do not share a fixed parameter set, so
	/// text-level interpolation would silently drop or invent parameters. Morph at the look
	/// level (this method) and let Vmat() render the result.
	/// </summary>
	public static string MorphVmat( SlotLook a, SlotLook b, float t ) => Blend( a, b, t )?.Vmat();

	/// <summary>
	/// The blend as a frame strip: <paramref name="steps"/> intervals, so steps + 1 looks from
	/// <paramref name="a"/> (index 0) to <paramref name="b"/> (last index). For a
	/// stylized-to-realistic morph over a turntable or a clip, one look per frame.
	/// </summary>
	public static List<SlotLook> Steps( SlotLook a, SlotLook b, int steps )
	{
		if ( steps < 1 ) throw new ArgumentOutOfRangeException( nameof( steps ), "Steps needs at least 1 interval (2 looks: the endpoints)." );
		var list = new List<SlotLook>( steps + 1 );
		for ( var i = 0; i <= steps; i++ ) list.Add( Blend( a, b, (float)i / steps ) );
		return list;
	}

}
