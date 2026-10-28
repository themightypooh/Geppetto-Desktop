#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Writes the generated looks that Fabric, Fur and the garment details ask for, and binds them to their
/// material slots - s&box's SyncFabricMaterials / SyncFurMaterials / SyncDetailMaterials, run after every
/// rebuild. Files go where s&box put them, beside the .effigy: <c>models/effigy/{fabric,fur,details}/</c>.
///
/// The PNG names carry a short hash of their recipe. The Godot material bridge caches textures by path,
/// so a fixed name would keep showing the old colour after a change; a new name per recipe shows the
/// change live. Older PNGs of the same feature are removed when a new set is written.
/// </summary>
public static class ClothingMaterials
{
	const string FabricDir = "models/effigy/fabric";
	const string FurDir = "models/effigy/fur";
	const string DetailDir = "models/effigy/details";

	static bool _installed, _busy;

	/// <summary>Hooks the sync onto every document rebuild (once).</summary>
	public static void Install()
	{
		if ( _installed ) return;
		_installed = true;
		GeppettoDocument.AnyChanged += OnChanged;
	}

	static void OnChanged( GeppettoDocument doc )
	{
		if ( _busy ) return;
		_busy = true;
		try
		{
			// A changed binding only shows after the mesh is pushed again.
			if ( Sync( doc ) ) doc.Refresh();
		}
		catch ( Exception e )
		{
			GD.PushError( $"[Geppetto] Could not write the clothing materials: {e.Message}" );
		}
		finally { _busy = false; }
	}

	/// <summary>Writes every live fabric / fur / detail material and binds its slots. True when a slot binding changed.</summary>
	public static bool Sync( GeppettoDocument doc )
	{
		var studio = doc.Studio;
		string root = Path.GetDirectoryName( doc.DiskPath ) ?? ".";
		bool changed = false;
		var names = studio.MaterialNames;

		var furs = studio.Features.OfType<FurFeature>().Where( f => !f.Suppressed && f.Error == null && f.ResolvedSlot >= 0 ).ToList();
		var furSlots = furs.Select( f => f.ResolvedSlot ).ToHashSet();

		// ---- Fabric
		var live = new HashSet<string>();
		foreach ( var fabric in studio.Features.OfType<FabricFeature>() )
		{
			if ( fabric.Suppressed || fabric.Error != null || fabric.ResolvedSlots.Count == 0 ) continue;
			string tag = Tag( fabric.Id );
			var colour = FurMaterial.ParseHex( fabric.Colour.Value, new Vec3( 0.35f, 0.43f, 0.55f ) );
			var accent = FurMaterial.ParseHex( fabric.Accent.Value, new Vec3( 0.91f, 0.89f, 0.83f ) );
			string recipe = FormattableString.Invariant( $"{fabric.PresetName}|{fabric.Colour.Value}|{fabric.Accent.Value}|{fabric.Scale.Clamped:0.##}|{fabric.Wear.Clamped:0.###}" );
			string rel = WriteFabricMaps( root, FabricDir, $"fabric_{tag}", recipe, fabric.PresetName, colour, accent,
				fabric.Scale.Clamped, fabric.Wear.Clamped, fabric.Id.GetHashCode() & 0xffff );
			live.Add( rel );
			foreach ( int slot in fabric.ResolvedSlots )
				if ( !furSlots.Contains( slot ) ) changed |= Bind( names, slot, rel );
		}
		changed |= Unbind( names, FabricDir, live );

		// ---- Fur
		live.Clear();
		foreach ( var fur in furs )
		{
			string tag = Tag( fur.Id );
			var colour = FurMaterial.ParseHex( fur.Colour.Value, new Vec3( 0.54f, 0.43f, 0.33f ) );
			var rim = FurMaterial.ParseHex( fur.RimColour.Value, new Vec3( 0.23f, 0.2f, 0.18f ) );
			string baseName = $"fur_{tag}";
			string recipe = FormattableString.Invariant( $"{fur.Colour.Value}|{fur.Clump.Clamped:R}|{fur.Id}" );
			string hash = Hash( recipe );
			string colorRel = $"{FurDir}/{baseName}_{hash}_color.png";
			string noiseRel = $"{FurDir}/{baseName}_{hash}_noise.png";
			if ( !File.Exists( Abs( root, colorRel ) ) || !File.Exists( Abs( root, noiseRel ) ) )
			{
				WriteBytes( root, colorRel, PngWriter.ToBytesRgba( FurMaterial.ColorRgba( colour ), 16, 16 ) );
				WriteBytes( root, noiseRel, PngWriter.ToBytesRgba( FurMaterial.NoiseRgba( fur.Id.GetHashCode() & 0xffff, fur.Clump.Clamped ),
					FurMaterial.NoiseSize, FurMaterial.NoiseSize ) );
			}
			Prune( root, FurDir, baseName, hash );
			string vmatRel = $"{FurDir}/{baseName}.vmat";
			WriteText( root, vmatRel, FurMaterial.VmatSource( colorRel, noiseRel, fur.Density.Clamped, fur.DarkRoots.Clamped, rim, fur.Wind.Clamped ) );
			live.Add( vmatRel );
			changed |= Bind( names, fur.ResolvedSlot, vmatRel );
		}
		changed |= Unbind( names, FurDir, live );

		// ---- Garment details (pocket finishes, hardware metal, glow panels, worn / dirty cloth)
		live.Clear();
		var fabrics = studio.Features.OfType<FabricFeature>().Where( f => !f.Suppressed && f.Error == null ).ToList();
		foreach ( var detail in studio.Features.OfType<GarmentDetailFeature>() )
		{
			if ( detail.Suppressed || detail.Error != null ) continue;
			foreach ( var m in detail.DetailMaterials )
			{
				string tag = Tag( m.Key );
				string vmatRel = $"{DetailDir}/{tag}.vmat";
				if ( m.Kind == "glow" )
				{
					string hash = Hash( m.Recipe );
					string colorRel = $"{DetailDir}/{tag}_{hash}_color.png";
					string maskRel = $"{DetailDir}/{tag}_{hash}_mask.png";
					var maps = DetailMaterials.GlowMaps( m );
					if ( !File.Exists( Abs( root, colorRel ) ) || !File.Exists( Abs( root, maskRel ) ) )
					{
						WriteBytes( root, colorRel, PngWriter.ToBytesRgba( maps.Color, maps.Width, maps.Height ) );
						WriteBytes( root, maskRel, PngWriter.ToBytesRgba( maps.Mask, maps.Width, maps.Height ) );
					}
					Prune( root, DetailDir, tag, hash );
					WriteText( root, vmatRel, DetailMaterials.GlowVmat( m, colorRel, maskRel, maps.UScale ) );
				}
				else if ( m.Kind is "worn" or "dirt" )
				{
					var fabric = fabrics.LastOrDefault( f => f.ResolvedSlots.Contains( m.SourceSlot ) );
					Func<Vec3, Vec3> tint = m.Kind == "worn" ? DetailMaterials.Worn : DetailMaterials.Dirty;
					var colour = tint( FurMaterial.ParseHex( fabric?.Colour.Value ?? "#5a6e8c", new Vec3( 0.35f, 0.43f, 0.55f ) ) );
					var accent = tint( FurMaterial.ParseHex( fabric?.Accent.Value ?? "#e8e2d4", new Vec3( 0.91f, 0.89f, 0.83f ) ) );
					string preset = fabric?.PresetName ?? "Jersey";
					float scale = fabric?.Scale.Clamped ?? 6f;
					string recipe = FormattableString.Invariant( $"{m.Recipe}|{preset}|{fabric?.Colour.Value}|{fabric?.Accent.Value}|{scale:R}" );
					WriteFabricMaps( root, DetailDir, tag, recipe, preset, colour, accent, scale, 0.8f,
						fabric is null ? 17 : fabric.Id.GetHashCode() & 0xffff, vmatRel );
				}
				else
				{
					WriteText( root, vmatRel, DetailMaterials.HardVmat( m ) );
				}
				live.Add( vmatRel );
				changed |= Bind( names, m.Slot, vmatRel );
			}
		}
		changed |= Unbind( names, DetailDir, live );

		return changed;
	}

	/// <summary>Writes a fabric's colour / normal / roughness maps (when this recipe has none yet) and its .vmat.
	/// Returns the .vmat's path relative to the document folder.</summary>
	static string WriteFabricMaps( string root, string dir, string baseName, string recipe, string preset, Vec3 colour, Vec3 accent,
		float scale, float wear, int seed, string? vmatRel = null )
	{
		string hash = Hash( recipe );
		string colorRel = $"{dir}/{baseName}_{hash}_color.png";
		string normalRel = $"{dir}/{baseName}_{hash}_normal.png";
		string roughRel = $"{dir}/{baseName}_{hash}_rough.png";
		if ( !File.Exists( Abs( root, colorRel ) ) || !File.Exists( Abs( root, normalRel ) ) || !File.Exists( Abs( root, roughRel ) ) )
		{
			var maps = FabricMaterial.Maps( preset, colour, accent, scale, wear, seed );
			int size = FabricMaterial.Size;
			WriteBytes( root, colorRel, PngWriter.ToBytesRgba( maps.Color, size, size ) );
			WriteBytes( root, normalRel, PngWriter.ToBytesRgba( maps.Normal, size, size ) );
			WriteBytes( root, roughRel, PngWriter.ToBytesRgba( maps.Roughness, size, size ) );
		}
		Prune( root, dir, baseName, hash );
		vmatRel ??= $"{dir}/{baseName}.vmat";
		WriteText( root, vmatRel, FabricMaterial.VmatSource( preset, colorRel, normalRel, roughRel ) );
		return vmatRel;
	}

	static bool Bind( Dictionary<int, string> names, int slot, string vmat )
	{
		if ( slot < 0 ) return false;
		if ( names.TryGetValue( slot, out var cur ) && cur == vmat ) return false;
		names[slot] = vmat;
		return true;
	}

	/// <summary>Drops bindings into <paramref name="dir"/> that no live feature wrote this time (a deleted
	/// or suppressed fabric gives the slot back to its plain colour). The files stay, as in s&box.</summary>
	static bool Unbind( Dictionary<int, string> names, string dir, HashSet<string> live )
	{
		var stale = names.Where( kv => kv.Value.StartsWith( dir + "/", StringComparison.Ordinal ) && !live.Contains( kv.Value ) ).Select( kv => kv.Key ).ToList();
		foreach ( var k in stale ) names.Remove( k );
		return stale.Count > 0;
	}

	/// <summary>Removes the PNGs of <paramref name="baseName"/> written for an older recipe.</summary>
	static void Prune( string root, string dir, string baseName, string keepHash )
	{
		string abs = Abs( root, dir );
		if ( !Directory.Exists( abs ) ) return;
		var re = new Regex( "^" + Regex.Escape( baseName ) + "_([0-9a-f]{8})_[a-z]+\\.png$" );
		foreach ( var file in Directory.GetFiles( abs, baseName + "_*.png" ) )
		{
			var m = re.Match( Path.GetFileName( file ) );
			if ( m.Success && m.Groups[1].Value != keepHash )
				try { File.Delete( file ); } catch ( IOException ) { }
		}
	}

	public static string Tag( string id ) => new string( ( id ?? "x" ).Select( c => char.IsAsciiLetterOrDigit( c ) ? c : '_' ).ToArray() );

	/// <summary>8 hex digits of FNV-1a over the recipe: stable across runs, unlike string.GetHashCode.</summary>
	static string Hash( string s )
	{
		uint h = 2166136261;
		foreach ( byte b in Encoding.UTF8.GetBytes( s ) ) { h ^= b; h *= 16777619; }
		return h.ToString( "x8" );
	}

	static string Abs( string root, string rel ) => Path.Combine( root, rel.Replace( '/', Path.DirectorySeparatorChar ) );

	static void WriteBytes( string root, string rel, byte[] bytes )
	{
		string path = Abs( root, rel );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllBytes( path, bytes );
	}

	static void WriteText( string root, string rel, string text )
	{
		string path = Abs( root, rel );
		if ( File.Exists( path ) && File.ReadAllText( path ) == text ) return;
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllText( path, text );
	}
}
#endif
