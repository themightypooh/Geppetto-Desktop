#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// Saved materials, the same layout Linework writes: <c>&lt;root&gt;/&lt;name&gt;/&lt;name&gt;.tres</c> (a
/// StandardMaterial3D) with optional maps beside it (<c>_albedo</c>, <c>_orm</c> R=AO G=rough B=metal,
/// <c>_normal</c> OpenGL, <c>_height</c>, <c>_emission</c>) and a <c>&lt;name&gt;.json</c> sidecar
/// {name, tags, swatch, rough, metal, source}. Roots: res://materials/ (this project) and
/// ~/.local/share/poohdot/materials/ (every project). Deleting moves the folder into <c>.trash</c>.
/// </summary>
public static class GeppettoMaterialLibrary
{
	public static string UserRoot =>
		Path.Combine( System.Environment.GetFolderPath( System.Environment.SpecialFolder.UserProfile ), ".local", "share", "poohdot", "materials" );

	public static string ProjectRoot => ProjectSettings.GlobalizePath( "res://materials" );

	public static string Root( string source ) => source == "project" ? ProjectRoot : UserRoot;

	/// <summary>Every saved material, this project's first, then the shared library. Names already taken by
	/// an earlier root are skipped, so a project copy wins over the library one.</summary>
	public static List<GeppettoDialMaterial> Scan()
	{
		var list = new List<GeppettoDialMaterial>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (root, source) in new[] { ( ProjectRoot, "project" ), ( UserRoot, "user" ) } )
		{
			if ( !Directory.Exists( root ) ) continue;
			IEnumerable<string> files;
			try { files = Directory.EnumerateFiles( root, "*.tres", SearchOption.AllDirectories ).OrderBy( f => f, StringComparer.OrdinalIgnoreCase ).ToList(); }
			catch ( Exception e ) { GD.PrintErr( $"[Geppetto] can't read materials in {root}: {e.Message}" ); continue; }
			foreach ( var tres in files )
			{
				if ( tres.Contains( Path.DirectorySeparatorChar + ".trash" + Path.DirectorySeparatorChar ) ) continue;
				var dir = Path.GetDirectoryName( tres )!;
				// only <name>/<name>.tres counts; other .tres files in the folder belong to something else
				if ( !string.Equals( Path.GetFileName( dir ), Path.GetFileNameWithoutExtension( tres ), StringComparison.OrdinalIgnoreCase ) ) continue;
				try
				{
					var m = Read( dir, source );
					if ( m != null && seen.Add( m.Name ) ) list.Add( m );
				}
				catch ( Exception e ) { GD.PrintErr( $"[Geppetto] skipped material {tres}: {e.Message}" ); }
			}
		}
		return list;
	}

	/// <summary>Reads one material folder: the sidecar if there is one, else the .tres values.</summary>
	public static GeppettoDialMaterial? Read( string dir, string source )
	{
		string stem = Path.GetFileName( dir );
		string tres = Path.Combine( dir, stem + ".tres" );
		if ( !File.Exists( tres ) ) return null;
		string name = stem;
		Color color = new( 0.62f, 0.6f, 0.57f );
		float rough = 0.5f, metal = 0f;
		var (tc, tr, tm) = ReadTres( tres );
		if ( tc is { } c0 ) color = c0;
		if ( tr is { } r0 ) rough = r0;
		if ( tm is { } m0 ) metal = m0;

		string json = Path.Combine( dir, stem + ".json" );
		if ( File.Exists( json ) )
		{
			using var doc = JsonDocument.Parse( File.ReadAllText( json ) );
			var o = doc.RootElement;
			if ( o.TryGetProperty( "name", out var n ) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( n.GetString() ) ) name = n.GetString()!;
			if ( o.TryGetProperty( "swatch", out var s ) && s.ValueKind == JsonValueKind.String && Color.HtmlIsValid( s.GetString() ) ) color = Color.FromHtml( s.GetString() );
			if ( o.TryGetProperty( "rough", out var r ) && r.ValueKind == JsonValueKind.Number ) rough = r.GetSingle();
			if ( o.TryGetProperty( "metal", out var m ) && m.ValueKind == JsonValueKind.Number ) metal = m.GetSingle();
		}

		var look = new SlotLook { Color = new Vec3( color.R, color.G, color.B ), Rough = rough, Metal = metal };
		string? Map( string suffix )
		{
			var p = Path.Combine( dir, $"{stem}_{suffix}.png" );
			return File.Exists( p ) ? p : null;
		}
		look.AlbedoMap = Map( "albedo" );
		if ( Map( "orm" ) is { } orm ) { look.RoughnessMap = orm; look.MetalnessMap = orm; look.AoMap = orm; }
		look.NormalMap = Map( "normal" );
		look.HeightMap = Map( "height" );
		look.EmitMap = Map( "emission" );
		// a texture already carries the colour (the swatch is just for the ball): tint white so it isn't darkened twice
		if ( look.AlbedoMap != null ) look.Color = Vec3.One;
		return new GeppettoDialMaterial( name, color, rough, metal, look, source, dir );
	}

	static readonly Regex ColorRx = new( @"^albedo_color\s*=\s*Color\(\s*([-\d.eE]+)\s*,\s*([-\d.eE]+)\s*,\s*([-\d.eE]+)", RegexOptions.Multiline );
	static readonly Regex RoughRx = new( @"^roughness\s*=\s*([-\d.eE]+)", RegexOptions.Multiline );
	static readonly Regex MetalRx = new( @"^metallic\s*=\s*([-\d.eE]+)", RegexOptions.Multiline );

	static (Color?, float?, float?) ReadTres( string path )
	{
		var text = File.ReadAllText( path );
		static float F( string s ) => float.Parse( s, CultureInfo.InvariantCulture );
		Color? c = null; float? r = null, m = null;
		if ( ColorRx.Match( text ) is { Success: true } cm ) c = new Color( F( cm.Groups[1].Value ), F( cm.Groups[2].Value ), F( cm.Groups[3].Value ) );
		if ( RoughRx.Match( text ) is { Success: true } rm ) r = F( rm.Groups[1].Value );
		if ( MetalRx.Match( text ) is { Success: true } mm ) m = F( mm.Groups[1].Value );
		return (c, r, m);
	}

	/// <summary>A folder-safe name: letters, digits, '-' and '_'; spaces become '_'.</summary>
	public static string Safe( string name )
	{
		var s = new string( name.Trim().Select( ch => char.IsLetterOrDigit( ch ) || ch is '-' or '_' ? char.ToLowerInvariant( ch ) : '_' ).ToArray() ).Trim( '_' );
		while ( s.Contains( "__" ) ) s = s.Replace( "__", "_" );
		return s.Length == 0 ? "material" : s;
	}

	public static bool Exists( string name, string source ) => Directory.Exists( Path.Combine( Root( source ), Safe( name ) ) );

	/// <summary>Writes (or overwrites) a material folder. <paramref name="albedoImage"/> is copied in as
	/// <c>_albedo.png</c>; null keeps an albedo that's already there unless <paramref name="clearAlbedo"/>.</summary>
	public static GeppettoDialMaterial Save( string name, Color color, float rough, float metal, string? albedoImage, bool clearAlbedo, string source, string? replaceDir = null )
	{
		name = name.Trim();
		if ( name.Length == 0 ) throw new ArgumentException( "A material needs a name." );
		string stem = Safe( name );
		string dir = Path.Combine( Root( source ), stem );
		// editing in place under a new name: move the old folder first so its maps come along
		if ( replaceDir != null && Directory.Exists( replaceDir ) && !PathEq( replaceDir, dir ) )
		{
			if ( Directory.Exists( dir ) ) throw new InvalidOperationException( $"There is already a material called {name}." );
			MoveFolder( replaceDir, dir );
		}
		Directory.CreateDirectory( dir );
		RenameFiles( dir, stem );

		string albedo = Path.Combine( dir, stem + "_albedo.png" );
		if ( clearAlbedo && File.Exists( albedo ) ) File.Delete( albedo );
		if ( !string.IsNullOrEmpty( albedoImage ) )
		{
			var img = Image.LoadFromFile( albedoImage );
			if ( img == null || img.IsEmpty() ) throw new InvalidOperationException( $"Couldn't read the image {Path.GetFileName( albedoImage )}." );
			img.SavePng( albedo );
		}

		File.WriteAllText( Path.Combine( dir, stem + ".tres" ), Tres( dir, stem, color, rough, metal ) );
		var sidecar = new Dictionary<string, object>
		{
			["name"] = name,
			["tags"] = Array.Empty<string>(),
			["swatch"] = "#" + color.ToHtml( false ),
			["rough"] = MathF.Round( rough, 3 ),
			["metal"] = MathF.Round( metal, 3 ),
			["source"] = "geppetto",
		};
		File.WriteAllText( Path.Combine( dir, stem + ".json" ), JsonSerializer.Serialize( sidecar, new JsonSerializerOptions { WriteIndented = true } ) );
		if ( source == "project" ) EditorInterface.Singleton.GetResourceFilesystem().Scan();
		return Read( dir, source )!;
	}

	/// <summary>The .tres text: a StandardMaterial3D, with the maps that exist in the folder bound.</summary>
	static string Tres( string dir, string stem, Color color, float rough, float metal )
	{
		var maps = new List<(string Prop, string File)>();
		void Add( string suffix, string prop ) { if ( File.Exists( Path.Combine( dir, $"{stem}_{suffix}.png" ) ) ) maps.Add( (prop, $"{stem}_{suffix}.png") ); }
		Add( "albedo", "albedo_texture" );
		Add( "orm", "orm" );
		Add( "normal", "normal_texture" );
		Add( "height", "heightmap_texture" );
		Add( "emission", "emission_texture" );
		var inv = CultureInfo.InvariantCulture;
		var sb = new System.Text.StringBuilder();
		sb.Append( $"[gd_resource type=\"StandardMaterial3D\" load_steps={maps.Count + 1} format=3]\n\n" );
		for ( int i = 0; i < maps.Count; i++ )
			sb.Append( $"[ext_resource type=\"Texture2D\" path=\"{maps[i].File}\" id=\"{i + 1}\"]\n" );
		if ( maps.Count > 0 ) sb.Append( '\n' );
		sb.Append( "[resource]\n" );
		sb.Append( $"resource_name = \"{stem}\"\n" );
		bool hasAlbedo = maps.Any( m => m.Prop == "albedo_texture" );
		var tint = hasAlbedo ? Colors.White : color;
		sb.Append( string.Format( inv, "albedo_color = Color({0:0.####}, {1:0.####}, {2:0.####}, 1)\n", tint.R, tint.G, tint.B ) );
		bool orm = maps.Any( m => m.Prop == "orm" );
		sb.Append( string.Format( inv, "roughness = {0:0.###}\nmetallic = {1:0.###}\n", orm ? 1f : rough, orm ? 1f : metal ) );
		for ( int i = 0; i < maps.Count; i++ )
		{
			var (prop, _) = maps[i];
			if ( prop == "orm" )
			{
				sb.Append( $"roughness_texture = ExtResource(\"{i + 1}\")\nroughness_texture_channel = 1\n" );
				sb.Append( $"metallic_texture = ExtResource(\"{i + 1}\")\nmetallic_texture_channel = 2\n" );
				sb.Append( $"ao_enabled = true\nao_texture = ExtResource(\"{i + 1}\")\n" );
			}
			else
			{
				if ( prop == "normal_texture" ) sb.Append( "normal_enabled = true\n" );
				if ( prop == "heightmap_texture" ) sb.Append( "heightmap_enabled = true\n" );
				if ( prop == "emission_texture" ) sb.Append( "emission_enabled = true\n" );
				sb.Append( $"{prop} = ExtResource(\"{i + 1}\")\n" );
			}
		}
		return sb.ToString();
	}

	/// <summary>Renames a saved material (folder, files, sidecar name) and repoints open documents' slot maps.</summary>
	public static GeppettoDialMaterial Rename( GeppettoDialMaterial m, string newName )
	{
		if ( !m.Custom || m.Dir == null ) throw new InvalidOperationException( "Only saved materials can be renamed." );
		string oldDir = m.Dir;
		var saved = Save( newName, m.Color, m.Rough, m.Metal, null, false, m.Source, oldDir );
		RepointMaps( oldDir, saved.Dir! );
		return saved;
	}

	/// <summary>Moves a saved material's folder into <c>&lt;root&gt;/.trash</c>; nothing is erased.</summary>
	public static string Delete( GeppettoDialMaterial m )
	{
		if ( !m.Custom || m.Dir == null ) throw new InvalidOperationException( "Only saved materials can be deleted." );
		string trash = Path.Combine( Root( m.Source ), ".trash" );
		Directory.CreateDirectory( trash );
		string to = Path.Combine( trash, $"{Path.GetFileName( m.Dir )}-{DateTime.Now:yyyyMMdd-HHmmss}" );
		MoveFolder( m.Dir, to );
		// Characters using it keep their look: their slot maps follow the files into .trash.
		RepointMaps( m.Dir, to, keepNames: true );
		if ( m.Source == "project" ) EditorInterface.Singleton.GetResourceFilesystem().Scan();
		return to;
	}

	static void RenameFiles( string dir, string stem )
	{
		foreach ( var f in Directory.GetFiles( dir ) )
		{
			string fn = Path.GetFileName( f );
			string ext = Path.GetExtension( fn );
			string bare = Path.GetFileNameWithoutExtension( fn );
			int us = bare.LastIndexOf( '_' );
			string suffix = us > 0 && bare[( us + 1 )..] is "albedo" or "orm" or "normal" or "height" or "emission" ? bare[us..] : "";
			string want = stem + suffix + ext;
			if ( ext is ".tres" or ".json" or ".png" && fn != want && !fn.EndsWith( ".import" ) )
			{
				string to = Path.Combine( dir, want );
				if ( !File.Exists( to ) ) File.Move( f, to );
			}
		}
	}

	static void MoveFolder( string from, string to )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( to )! );
		try { Directory.Move( from, to ); }
		catch ( IOException )
		{
			// different drives (project vs ~/.local): copy then remove the original
			CopyDir( from, to );
			Directory.Delete( from, true );
		}
	}

	static void CopyDir( string from, string to )
	{
		Directory.CreateDirectory( to );
		foreach ( var f in Directory.GetFiles( from ) ) File.Copy( f, Path.Combine( to, Path.GetFileName( f ) ), true );
		foreach ( var d in Directory.GetDirectories( from ) ) CopyDir( d, Path.Combine( to, Path.GetFileName( d ) ) );
	}

	/// <summary>Slot looks in open documents that point at files in <paramref name="oldDir"/> now point at
	/// the same-suffix file in <paramref name="newDir"/>.</summary>
	static void RepointMaps( string oldDir, string newDir, bool keepNames = false )
	{
		string newStem = Path.GetFileName( newDir );
		string? Fix( string? p )
		{
			if ( string.IsNullOrEmpty( p ) || !p.StartsWith( oldDir + Path.DirectorySeparatorChar ) ) return p;
			if ( keepNames ) return Path.Combine( newDir, Path.GetFileName( p ) );
			string bare = Path.GetFileNameWithoutExtension( p );
			int us = bare.LastIndexOf( '_' );
			string suffix = us > 0 ? bare[us..] : "";
			return Path.Combine( newDir, newStem + suffix + Path.GetExtension( p ) );
		}
		foreach ( var doc in GeppettoDocument.All )
			foreach ( var look in doc.Studio.MaterialLooks.Values )
			{
				look.AlbedoMap = Fix( look.AlbedoMap ); look.RoughnessMap = Fix( look.RoughnessMap ); look.MetalnessMap = Fix( look.MetalnessMap );
				look.AoMap = Fix( look.AoMap ); look.NormalMap = Fix( look.NormalMap ); look.HeightMap = Fix( look.HeightMap ); look.EmitMap = Fix( look.EmitMap );
			}
	}

	static bool PathEq( string a, string b ) => string.Equals( Path.GetFullPath( a ).TrimEnd( '/' ), Path.GetFullPath( b ).TrimEnd( '/' ), StringComparison.Ordinal );
}
#endif
