#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Marionette.Desktop;

/// <summary>
/// Godlike Wave 3 P2 — text-based diffable scene format (.gtscn), the
/// collaboration half of the Plugin SDK ticket.
///
/// Why text: two artists can work on one asset without overwriting each
/// other only if the scene merges. Godot's .tscn/.tres prove the shape:
/// one record per line, canonical ordering, stable float formatting, so
/// two saves of the same scene are byte-identical and a one-node change
/// diffs to exactly one line. Binary assets (.effigy, .glb, .fbx, textures)
/// stay binary and go to Git LFS — see <see cref="LfsAttributes"/>.
///
/// Format (v1, tab-separated fields so names and paths may contain spaces;
/// tabs inside a field are forbidden and rejected on write):
///
///   gtscn 1
///   layer  &lt;name&gt;\t&lt;artist&gt;\t&lt;department&gt;
///   asset  &lt;id&gt;\t&lt;kind&gt;\t&lt;path&gt;
///   node   &lt;id&gt;\t&lt;parent-id or -&gt;\t&lt;name&gt;\t&lt;layer or -&gt;
///   xform  &lt;node-id&gt;\t&lt;px py pz&gt;\t&lt;rx ry rz&gt;\t&lt;sx sy sz&gt;
///
/// Sections always serialize in that order, records sorted by id/name.
/// Floats use round-trip ("R") invariant formatting: exact and stable.
/// Lines of an unknown record type are preserved verbatim in
/// <see cref="TextSceneDocument.UnknownLines"/> and re-emitted after the
/// known sections, so a newer field survives a round-trip through an older
/// reader instead of being silently dropped.
///
/// Layers are per artist/department (A models, B textures, C rigs — same
/// asset, three layers). They map 1:1 onto USD layers when
/// godlike-usd-stage-alembic-materialx lands; this file implements no USD.
/// </summary>
public sealed class TextSceneLayer
{
	public string Name { get; set; } = "";
	public string Artist { get; set; } = "";
	public string Department { get; set; } = "";
}

public sealed class TextSceneAsset
{
	public string Id { get; set; } = "";
	public string Kind { get; set; } = "";
	public string Path { get; set; } = "";
}

public sealed class TextSceneNode
{
	public string Id { get; set; } = "";
	/// <summary>Parent node id, or "" for a root node.</summary>
	public string ParentId { get; set; } = "";
	public string Name { get; set; } = "";
	/// <summary>Layer name, or "" for no layer.</summary>
	public string Layer { get; set; } = "";
}

public sealed class TextSceneTransform
{
	public string NodeId { get; set; } = "";
	public float[] Position { get; set; } = new[] { 0f, 0f, 0f };
	/// <summary>Euler rotation in degrees, XYZ order.</summary>
	public float[] RotationDegrees { get; set; } = new[] { 0f, 0f, 0f };
	public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
}

public sealed class TextSceneDocument
{
	public const int CurrentVersion = 1;
	public List<TextSceneLayer> Layers { get; set; } = new();
	public List<TextSceneAsset> Assets { get; set; } = new();
	public List<TextSceneNode> Nodes { get; set; } = new();
	public List<TextSceneTransform> Transforms { get; set; } = new();
	/// <summary>Unknown record lines, kept verbatim across round-trips.</summary>
	public List<string> UnknownLines { get; set; } = new();

	/// <summary>Canonical text: same document, same bytes, every time.</summary>
	public string ToText()
	{
		var sb = new StringBuilder();
		sb.Append( "gtscn " ).Append( CurrentVersion ).Append( '\n' );
		foreach ( var layer in Layers.OrderBy( l => l.Name, StringComparer.Ordinal ) )
			sb.Append( "layer\t" ).Append( Field( layer.Name ) ).Append( '\t' )
				.Append( Field( layer.Artist ) ).Append( '\t' ).Append( Field( layer.Department ) ).Append( '\n' );
		foreach ( var asset in Assets.OrderBy( a => a.Id, StringComparer.Ordinal ) )
			sb.Append( "asset\t" ).Append( Field( asset.Id ) ).Append( '\t' )
				.Append( Field( asset.Kind ) ).Append( '\t' ).Append( Field( asset.Path ) ).Append( '\n' );
		foreach ( var node in Nodes.OrderBy( n => n.Id, StringComparer.Ordinal ) )
			sb.Append( "node\t" ).Append( Field( node.Id ) ).Append( '\t' )
				.Append( Field( string.IsNullOrEmpty( node.ParentId ) ? "-" : node.ParentId ) ).Append( '\t' )
				.Append( Field( node.Name ) ).Append( '\t' )
				.Append( Field( string.IsNullOrEmpty( node.Layer ) ? "-" : node.Layer ) ).Append( '\n' );
		foreach ( var transform in Transforms.OrderBy( t => t.NodeId, StringComparer.Ordinal ) )
			sb.Append( "xform\t" ).Append( Field( transform.NodeId ) ).Append( '\t' )
				.Append( Vector( transform.Position ) ).Append( '\t' )
				.Append( Vector( transform.RotationDegrees ) ).Append( '\t' )
				.Append( Vector( transform.Scale ) ).Append( '\n' );
		foreach ( var unknown in UnknownLines )
			sb.Append( unknown ).Append( '\n' );
		return sb.ToString();
	}

	static string Field( string value )
	{
		if ( value.Contains( '\t' ) || value.Contains( '\n' ) || value.Contains( '\r' ) )
			throw new FormatException( "Text scene fields cannot contain tabs or line breaks." );
		return value;
	}

	static string Vector( float[] values )
	{
		if ( values is null || values.Length != 3 )
			throw new FormatException( "Text scene vectors need exactly three numbers." );
		return string.Join( " ", values.Select( v => v.ToString( "R", CultureInfo.InvariantCulture ) ) );
	}

	/// <summary>Parse canonical (or hand-edited) text. Throws
	/// <see cref="FormatException"/> with a line number on a bad header,
	/// duplicate id, dangling parent, unknown layer, transform for an unknown
	/// node, or malformed number. Unknown record types are kept, not dropped.</summary>
	public static TextSceneDocument Parse( string text )
	{
		ArgumentNullException.ThrowIfNull( text );
		var doc = new TextSceneDocument();
		var lines = text.Replace( "\r\n", "\n" ).Split( '\n' );
		if ( lines.Length == 0 || lines[0] != $"gtscn {CurrentVersion}" )
			throw new FormatException( "Line 1: text scene must start with 'gtscn 1'." );

		var layerNames = new HashSet<string>( StringComparer.Ordinal );
		var assetIds = new HashSet<string>( StringComparer.Ordinal );
		var nodeIds = new HashSet<string>( StringComparer.Ordinal );
		var nodes = new List<TextSceneNode>();
		var transforms = new List<TextSceneTransform>();

		for ( var i = 1; i < lines.Length; i++ )
		{
			var line = lines[i];
			if ( line.Length == 0 ) continue;
			var lineNo = i + 1;
			var tab = line.IndexOf( '\t' );
			var head = tab < 0 ? line : line[..tab];
			var rest = tab < 0 ? "" : line[( tab + 1 )..];
			string[] Fields() => rest.Split( '\t' );

			switch ( head )
			{
				case "layer":
				{
					var f = Fields();
					if ( f.Length != 3 ) throw new FormatException( $"Line {lineNo}: layer needs name, artist, department." );
					if ( !layerNames.Add( f[0] ) ) throw new FormatException( $"Line {lineNo}: duplicate layer '{f[0]}'." );
					doc.Layers.Add( new TextSceneLayer { Name = f[0], Artist = f[1], Department = f[2] } );
					break;
				}
				case "asset":
				{
					var f = Fields();
					if ( f.Length != 3 ) throw new FormatException( $"Line {lineNo}: asset needs id, kind, path." );
					if ( !assetIds.Add( f[0] ) ) throw new FormatException( $"Line {lineNo}: duplicate asset id '{f[0]}'." );
					doc.Assets.Add( new TextSceneAsset { Id = f[0], Kind = f[1], Path = f[2] } );
					break;
				}
				case "node":
				{
					var f = Fields();
					if ( f.Length != 4 ) throw new FormatException( $"Line {lineNo}: node needs id, parent, name, layer." );
					if ( !nodeIds.Add( f[0] ) ) throw new FormatException( $"Line {lineNo}: duplicate node id '{f[0]}'." );
					nodes.Add( new TextSceneNode
					{
						Id = f[0], ParentId = f[1] == "-" ? "" : f[1],
						Name = f[2], Layer = f[3] == "-" ? "" : f[3],
					} );
					break;
				}
				case "xform":
				{
					var f = Fields();
					if ( f.Length != 4 ) throw new FormatException( $"Line {lineNo}: xform needs node id, position, rotation, scale." );
					transforms.Add( new TextSceneTransform
					{
						NodeId = f[0],
						Position = ParseVector( f[1], lineNo ),
						RotationDegrees = ParseVector( f[2], lineNo ),
						Scale = ParseVector( f[3], lineNo ),
					} );
					break;
				}
				default:
					doc.UnknownLines.Add( line );
					break;
			}
		}

		// Cross-references resolve after the whole file is read, so record
		// order in the file never matters.
		foreach ( var node in nodes )
		{
			if ( node.ParentId.Length > 0 && !nodeIds.Contains( node.ParentId ) )
				throw new FormatException( $"Node '{node.Id}' has a dangling parent '{node.ParentId}'." );
			if ( node.Layer.Length > 0 && !layerNames.Contains( node.Layer ) )
				throw new FormatException( $"Node '{node.Id}' references unknown layer '{node.Layer}'." );
		}
		foreach ( var transform in transforms )
			if ( !nodeIds.Contains( transform.NodeId ) )
				throw new FormatException( $"Transform references unknown node '{transform.NodeId}'." );

		doc.Nodes = nodes;
		doc.Transforms = transforms;
		return doc;
	}

	static float[] ParseVector( string text, int lineNo )
	{
		var parts = text.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 )
			throw new FormatException( $"Line {lineNo}: a vector needs exactly three numbers." );
		var values = new float[3];
		for ( var i = 0; i < 3; i++ )
			if ( !float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i] ) )
				throw new FormatException( $"Line {lineNo}: '{parts[i]}' is not a number." );
		return values;
	}

	/// <summary>.gitattributes content for a repo holding these scenes: text
	/// scenes merge normally; every binary asset type goes to Git LFS.</summary>
	public static string LfsAttributes() =>
		"# Geppetto text scenes (.gtscn) merge as text; binary assets go to Git LFS.\n" +
		"*.gtscn text diff\n" +
		"*.effigy filter=lfs diff=lfs merge=lfs -text\n" +
		"*.glb filter=lfs diff=lfs merge=lfs -text\n" +
		"*.gltf filter=lfs diff=lfs merge=lfs -text\n" +
		"*.fbx filter=lfs diff=lfs merge=lfs -text\n" +
		"*.obj filter=lfs diff=lfs merge=lfs -text\n" +
		"*.png filter=lfs diff=lfs merge=lfs -text\n" +
		"*.jpg filter=lfs diff=lfs merge=lfs -text\n" +
		"*.exr filter=lfs diff=lfs merge=lfs -text\n" +
		"*.hdr filter=lfs diff=lfs merge=lfs -text\n" +
		"*.tif filter=lfs diff=lfs merge=lfs -text\n" +
		"*.wav filter=lfs diff=lfs merge=lfs -text\n" +
		"*.mp3 filter=lfs diff=lfs merge=lfs -text\n" +
		"*.ttf filter=lfs diff=lfs merge=lfs -text\n" +
		"*.otf filter=lfs diff=lfs merge=lfs -text\n";

	/// <summary>Short guidance for the repo README: scenes merge, binaries
	/// need LFS + a file lock before editing.</summary>
	public static string LfsGuidance() =>
		"Text scenes (*.gtscn) are plain text: commit, branch and merge them like code. " +
		"Binary assets (*.effigy, *.glb, *.fbx, textures, audio) are tracked in Git LFS " +
		"(see .gitattributes) and must NOT be edited by two artists at once: take the " +
		"per-asset file lock first (AssetLock), edit, then release it. A text scene " +
		"referencing a locked binary still merges — only the binary itself is exclusive.";
}
