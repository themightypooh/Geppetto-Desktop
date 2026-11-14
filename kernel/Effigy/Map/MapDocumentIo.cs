using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// The map document file format + Create / Open / Save.
///
/// FORMAT (hand-written text, StudioDocument style — diffable, one op per history
/// entry; numbers use StudioDocument's invariant "R" formatting so a map file's
/// numbers read exactly like a studio file's):
///
/// <code>
/// gmap 1
/// docid &lt;id&gt;
/// name &lt;name, rest of line&gt;
/// rollback &lt;n&gt;                 (int.MaxValue = all entries)
/// op add-brush &lt;id&gt;
/// 	name &lt;name, rest of line&gt;
/// 	layer &lt;layerId or -&gt;
/// 	visgroups &lt;csv or -&gt;
/// 	face &lt;nx ny nz dist material uox uoy usx usy urot lightmap smooth&gt;
/// endop
/// op add-entity &lt;id&gt;
/// 	class &lt;classname&gt;
/// 	origin &lt;x y z&gt;
/// 	angles &lt;x y z&gt;
/// 	layer &lt;id or -&gt;
/// 	visgroups &lt;csv or -&gt;
/// 	key &lt;name&gt; &lt;value, rest of line&gt;
/// 	brush &lt;brushId&gt;
/// endop
/// op add-layer &lt;id&gt; / op add-visgroup &lt;id&gt;
/// 	name &lt;name&gt;
/// 	colour &lt;#RRGGBB&gt;
/// 	visible &lt;0|1&gt;
/// 	compilevisible &lt;0|1&gt;
/// endop
/// op add-displacement &lt;id&gt; / op set-displacement &lt;id&gt;
/// 	brush &lt;brushId&gt;
/// 	face &lt;faceIndex&gt;
/// 	power &lt;2..4&gt;
/// 	dispoff &lt;offset&gt;          (one line per vertex, row-major)
/// 	dispalpha &lt;alpha&gt;
/// endop
/// op add-io &lt;id&gt; / op set-io &lt;id&gt;
/// 	source &lt;sourceEntity&gt;   (rest of line)
/// 	event &lt;event&gt;
/// 	target &lt;targetName&gt;
/// 	input &lt;input&gt;
/// 	parameter &lt;parameter&gt;
/// 	delay &lt;seconds&gt;
/// 	maxfires &lt;n&gt;
/// 	enabled &lt;0|1&gt;
/// endop
/// op remove-brush &lt;id&gt; / remove-entity / remove-layer / remove-visgroup /
///    remove-displacement / remove-io
/// op set-layer-visible &lt;id&gt; &lt;0|1&gt; / set-visgroup-visible &lt;id&gt; &lt;0|1&gt;
/// op set-compile &lt;0|1&gt; &lt;lightmapScale&gt; &lt;sky, rest of line&gt;
/// op move-brush &lt;id&gt; &lt;min x y z&gt; &lt;max x y z&gt;
/// op set-brush-material &lt;id&gt; &lt;material, rest of line&gt;
/// op move-entity &lt;id&gt; &lt;origin x y z&gt;
/// op set-entity-property &lt;id&gt; &lt;key&gt; &lt;value, rest of line&gt;
/// op remove-entity-property &lt;id&gt; &lt;key&gt;
/// </code>
///
/// VERSIONS. 1 is current. 0 migrates with a named note (see
/// <see cref="MapDocument.LoadNotes"/>): v0 has no docid line (a fresh id is
/// assigned), no rollback line (defaults to end of history), and layer/visgroup
/// blocks have no compilevisible line (defaults to visible). A file claiming a
/// NEWER version is refused by name, never opened as if it were older. Anything
/// malformed — unknown top-level line, unknown op kind, wrong field count, a number
/// that is not a number, an unterminated block, a fold that cannot apply — throws
/// InvalidDataException with the line number (or the entry). Nothing is silently
/// dropped: the fold is the whole state, so a file that loads IS the map.
///
/// NUMBERS ARE STRICT here, unlike StudioDocument's lenient ParseFloat (which
/// returns 0 for garbage): a face line with a non-numeric distance is a corrupt
/// file, not a brush at the origin. That is a deliberate difference for a new
/// format, not a fork — the formatting primitives (Num / Vec / OneLine) are shared
/// with StudioDocument, see its map extension note.
///
/// SAVE CONTRACT (CONTRACTS.md): validation first (built-in + hook via
/// <see cref="MapDocument.ValidateForSave"/>), then same-directory temp +
/// complete write + atomic rename. A failed save never touches the file and never
/// clears the dirty flag.
/// </summary>
public static class MapDocumentIo
{
	public const string Extension = ".gmap";

	public const int CurrentVersion = MapDocument.CurrentFormatVersion;

	public static MapDocument Create( string name ) => MapDocument.Create( name );

	/// <summary>Lossless dump. No validation: autosave snapshots use this so a defect never blocks saving work.</summary>
	public static string Write( MapDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var sb = new StringBuilder();
		sb.Append( "gmap " ).Append( CurrentVersion ).Append( '\n' );
		sb.Append( "docid " ).Append( doc.DocumentId ).Append( '\n' );
		if ( !string.IsNullOrWhiteSpace( doc.Name ) )
			sb.Append( "name " ).Append( StudioDocument.OneLine( doc.Name ) ).Append( '\n' );
		sb.Append( "rollback " ).Append( doc.RollbackIndex ).Append( '\n' );
		foreach ( var entry in doc.History )
			WriteEntry( sb, entry );
		return sb.ToString();
	}

	/// <summary>Validate (built-in + hook, optionally against a registry), then atomic save.</summary>
	public static void Save( MapDocument doc, string path, EntityRegistry registry = null )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "A save needs a path.", nameof( path ) );
		var defects = doc.ValidateForSave( registry );
		if ( defects.Count > 0 )
			throw new InvalidDataException(
				"Map document failed validation on save:\n - " + string.Join( "\n - ", defects ) );
		StudioDocument.AtomicWriteText( path, Write( doc ) );
		doc.NoteSaved( path );
	}

	public static MapDocument Open( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "An open needs a path.", nameof( path ) );
		var doc = Read( File.ReadAllText( path ) );
		doc.NoteOpened( path );
		return doc;
	}

	// --- writing ------------------------------------------------------------------------------

	static void WriteEntry( StringBuilder sb, MapHistoryEntry entry )
	{
		if ( entry is null )
			return;
		switch ( entry.Kind )
		{
			case MapHistoryEntry.AddBrush:
			{
				var brush = entry.Brush ?? throw new InvalidDataException( $"Cannot write add-brush '{entry.TargetId}': no brush." );
				sb.Append( "op add-brush " ).Append( entry.TargetId ).Append( '\n' );
				if ( !string.IsNullOrWhiteSpace( brush.Name ) )
					sb.Append( "\tname " ).Append( StudioDocument.OneLine( brush.Name ) ).Append( '\n' );
				sb.Append( "\tlayer " ).Append( string.IsNullOrEmpty( brush.LayerId ) ? "-" : brush.LayerId ).Append( '\n' );
				sb.Append( "\tvisgroups " ).Append( brush.VisGroupIds.Count == 0 ? "-" : string.Join( ",", brush.VisGroupIds ) ).Append( '\n' );
				foreach ( var face in brush.Faces )
				{
					if ( face is null )
						throw new InvalidDataException( $"Cannot write brush '{brush.Id}': a face is null." );
					if ( face.Material is null || string.IsNullOrWhiteSpace( face.Material.MaterialName ) )
						throw new InvalidDataException( $"Cannot write brush '{brush.Id}': a face has no material." );
					if ( face.Material.MaterialName.Any( char.IsWhiteSpace ) )
						throw new InvalidDataException( $"Cannot write brush '{brush.Id}': material '{face.Material.MaterialName}' contains whitespace." );
					sb.Append( "\tface " ).Append( StudioDocument.Vec( face.Normal ) ).Append( ' ' )
						.Append( StudioDocument.Num( face.Distance ) ).Append( ' ' ).Append( face.Material.MaterialName ).Append( ' ' )
						.Append( StudioDocument.Vec2( face.Material.Uv.Offset ) ).Append( ' ' )
						.Append( StudioDocument.Vec2( face.Material.Uv.Scale ) ).Append( ' ' )
						.Append( StudioDocument.Num( face.Material.Uv.RotationDegrees ) ).Append( ' ' )
						.Append( StudioDocument.Num( face.Material.Uv.LightmapScale ) ).Append( ' ' )
						.Append( face.Material.Uv.SmoothingGroup ).Append( '\n' );
				}
				sb.Append( "endop\n" );
				return;
			}
			case MapHistoryEntry.AddEntity:
			{
				var entity = entry.Entity ?? throw new InvalidDataException( $"Cannot write add-entity '{entry.TargetId}': no entity." );
				sb.Append( "op add-entity " ).Append( entry.TargetId ).Append( '\n' );
				sb.Append( "\tclass " ).Append( entity.ClassName ).Append( '\n' );
				sb.Append( "\torigin " ).Append( StudioDocument.Vec( entity.Origin ) ).Append( '\n' );
				sb.Append( "\tangles " ).Append( StudioDocument.Vec( entity.Angles ) ).Append( '\n' );
				sb.Append( "\tlayer " ).Append( string.IsNullOrEmpty( entity.LayerId ) ? "-" : entity.LayerId ).Append( '\n' );
				sb.Append( "\tvisgroups " ).Append( entity.VisGroupIds.Count == 0 ? "-" : string.Join( ",", entity.VisGroupIds ) ).Append( '\n' );
				foreach ( var kv in entity.KeyValues.OrderBy( kv => kv.Key, StringComparer.Ordinal ) )
				{
					if ( kv.Key.Any( char.IsWhiteSpace ) )
						throw new InvalidDataException( $"Cannot write entity '{entity.Id}': key '{kv.Key}' contains whitespace." );
					sb.Append( "\tkey " ).Append( kv.Key ).Append( ' ' ).Append( StudioDocument.OneLine( kv.Value ?? "" ) ).Append( '\n' );
				}
				foreach ( var brushId in entity.BrushIds )
					sb.Append( "\tbrush " ).Append( brushId ).Append( '\n' );
				sb.Append( "endop\n" );
				return;
			}
			case MapHistoryEntry.AddLayer:
			case MapHistoryEntry.AddVisGroup:
			{
				var isLayer = entry.Kind == MapHistoryEntry.AddLayer;
				var name = isLayer ? entry.Layer?.Name : entry.VisGroup?.Name;
				var colour = isLayer ? entry.Layer?.Colour : entry.VisGroup?.Colour;
				var visible = isLayer ? entry.Layer?.Visible : entry.VisGroup?.Visible;
				var compileVisible = isLayer ? entry.Layer?.CompileVisible : entry.VisGroup?.CompileVisible;
				if ( name is null )
					throw new InvalidDataException( $"Cannot write {entry.Kind} '{entry.TargetId}': no {( isLayer ? "layer" : "visgroup" )}." );
				sb.Append( "op " ).Append( entry.Kind ).Append( ' ' ).Append( entry.TargetId ).Append( '\n' );
				if ( !string.IsNullOrWhiteSpace( name ) )
					sb.Append( "\tname " ).Append( StudioDocument.OneLine( name ) ).Append( '\n' );
				sb.Append( "\tcolour " ).Append( colour ).Append( '\n' );
				sb.Append( "\tvisible " ).Append( visible == true ? 1 : 0 ).Append( '\n' );
				sb.Append( "\tcompilevisible " ).Append( compileVisible == true ? 1 : 0 ).Append( '\n' );
				sb.Append( "endop\n" );
				return;
			}
			case MapHistoryEntry.AddDisplacement:
			case MapHistoryEntry.SetDisplacement:
			{
				var disp = entry.Displacement ?? throw new InvalidDataException( $"Cannot write {entry.Kind} '{entry.TargetId}': no displacement." );
				sb.Append( "op " ).Append( entry.Kind ).Append( ' ' ).Append( entry.TargetId ).Append( '\n' );
				sb.Append( "\tbrush " ).Append( disp.BrushId ).Append( '\n' );
				sb.Append( "\tface " ).Append( disp.FaceIndex ).Append( '\n' );
				sb.Append( "\tpower " ).Append( disp.Power ).Append( '\n' );
				foreach ( var offset in disp.Offsets ?? Array.Empty<float>() )
					sb.Append( "\tdispoff " ).Append( StudioDocument.Num( offset ) ).Append( '\n' );
				foreach ( var alpha in disp.Alphas ?? Array.Empty<float>() )
					sb.Append( "\tdispalpha " ).Append( StudioDocument.Num( alpha ) ).Append( '\n' );
				sb.Append( "endop\n" );
				return;
			}
			case MapHistoryEntry.AddIo:
			case MapHistoryEntry.SetIo:
			{
				var conn = entry.Io ?? throw new InvalidDataException( $"Cannot write {entry.Kind} '{entry.TargetId}': no connection." );
				var row = conn.Row ?? throw new InvalidDataException( $"Cannot write {entry.Kind} '{entry.TargetId}': no row." );
				sb.Append( "op " ).Append( entry.Kind ).Append( ' ' ).Append( entry.TargetId ).Append( '\n' );
				sb.Append( "\tsource " ).Append( StudioDocument.OneLine( row.SourceEntity ) ).Append( '\n' );
				sb.Append( "\tevent " ).Append( StudioDocument.OneLine( row.Event ) ).Append( '\n' );
				sb.Append( "\ttarget " ).Append( StudioDocument.OneLine( row.TargetName ) ).Append( '\n' );
				sb.Append( "\tinput " ).Append( StudioDocument.OneLine( row.Input ) ).Append( '\n' );
				sb.Append( "\tparameter " ).Append( StudioDocument.OneLine( row.Parameter ) ).Append( '\n' );
				sb.Append( "\tdelay " ).Append( row.DelaySeconds.ToString( "R", CultureInfo.InvariantCulture ) ).Append( '\n' );
				sb.Append( "\tmaxfires " ).Append( row.MaxFires ).Append( '\n' );
				sb.Append( "\tenabled " ).Append( row.Enabled ? 1 : 0 ).Append( '\n' );
				sb.Append( "endop\n" );
				return;
			}
			case MapHistoryEntry.RemoveBrush:
			case MapHistoryEntry.RemoveEntity:
			case MapHistoryEntry.RemoveLayer:
			case MapHistoryEntry.RemoveVisGroup:
			case MapHistoryEntry.RemoveDisplacement:
			case MapHistoryEntry.RemoveIo:
				sb.Append( "op " ).Append( entry.Kind ).Append( ' ' ).Append( entry.TargetId ).Append( '\n' );
				return;
			case MapHistoryEntry.SetLayerVisible:
			case MapHistoryEntry.SetVisGroupVisible:
				sb.Append( "op " ).Append( entry.Kind ).Append( ' ' ).Append( entry.TargetId ).Append( ' ' )
					.Append( entry.Visible ? 1 : 0 ).Append( '\n' );
				return;
			case MapHistoryEntry.SetCompileSettings:
			{
				var settings = entry.Settings ?? throw new InvalidDataException( "Cannot write set-compile: no settings." );
				sb.Append( "op set-compile " ).Append( settings.CompileVisibleOnly ? 1 : 0 ).Append( ' ' )
					.Append( StudioDocument.Num( settings.DefaultLightmapScale ) ).Append( ' ' )
					.Append( StudioDocument.OneLine( settings.SkyName ?? "" ) ).Append( '\n' );
				return;
			}
			case MapHistoryEntry.MoveBrush:
				sb.Append( "op move-brush " ).Append( entry.TargetId ).Append( ' ' )
					.Append( StudioDocument.Vec( entry.Min ) ).Append( ' ' ).Append( StudioDocument.Vec( entry.Max ) ).Append( '\n' );
				return;
			case MapHistoryEntry.SetBrushMaterial:
				sb.Append( "op set-brush-material " ).Append( entry.TargetId ).Append( ' ' )
					.Append( StudioDocument.OneLine( entry.Material ?? "" ) ).Append( '\n' );
				return;
			case MapHistoryEntry.MoveEntity:
				sb.Append( "op move-entity " ).Append( entry.TargetId ).Append( ' ' )
					.Append( StudioDocument.Vec( entry.Origin ) ).Append( '\n' );
				return;
			case MapHistoryEntry.SetEntityProperty:
				sb.Append( "op set-entity-property " ).Append( entry.TargetId ).Append( ' ' ).Append( entry.Key ).Append( ' ' )
					.Append( StudioDocument.OneLine( entry.Value ?? "" ) ).Append( '\n' );
				return;
			case MapHistoryEntry.RemoveEntityProperty:
				sb.Append( "op remove-entity-property " ).Append( entry.TargetId ).Append( ' ' ).Append( entry.Key ).Append( '\n' );
				return;
			default:
				throw new InvalidDataException( $"Cannot write history entry: unknown kind '{entry.Kind}'." );
		}
	}

	// --- reading ------------------------------------------------------------------------------

	/// <summary>Parse a document. Throws InvalidDataException with the line number on anything malformed.</summary>
	public static MapDocument Read( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			throw new InvalidDataException( "Map document text is empty." );
		var lines = text.Replace( "\r\n", "\n" ).Split( '\n' );
		var first = -1;
		for ( var i = 0; i < lines.Length; i++ )
			if ( lines[i].Trim().Length > 0 )
			{
				first = i;
				break;
			}
		if ( first < 0 || !lines[first].TrimStart().StartsWith( "gmap ", StringComparison.Ordinal ) )
			throw new InvalidDataException( "Not a Geppetto map document — the first line should read 'gmap <version>'." );
		var version = ParseIntStrict( lines[first].Trim()[5..].Trim(), first + 1 );
		if ( version > CurrentVersion )
			throw new InvalidDataException(
				$"This file was written by a newer Geppetto (map format {version}; this build reads {CurrentVersion})." );
		if ( version < MapDocument.OldestMigratableFormatVersion )
			throw new InvalidDataException(
				$"Map format {version} predates the oldest migratable format {MapDocument.OldestMigratableFormatVersion}." );

		var docId = "";
		var sawDocId = false;
		var name = "";
		var rollback = int.MaxValue;
		var sawRollback = false;
		var history = new List<MapHistoryEntry>();
		var missingCompileVisible = 0;

		for ( var i = first + 1; i < lines.Length; i++ )
		{
			var line = lines[i].Trim();
			if ( line.Length == 0 )
				continue;
			var (key, rest) = StudioDocument.Split( line );
			switch ( key )
			{
				case "docid":
					if ( sawDocId )
						throw new InvalidDataException( $"Line {i + 1}: duplicate docid line." );
					docId = rest.Trim();
					if ( string.IsNullOrWhiteSpace( docId ) )
						throw new InvalidDataException( $"Line {i + 1}: docid needs an id." );
					sawDocId = true;
					continue;
				case "name":
					name = rest;
					continue;
				case "rollback":
					rollback = ParseIntStrict( rest, i + 1 );
					sawRollback = true;
					continue;
				case "op":
					history.Add( ReadOp( lines, ref i, ref missingCompileVisible ) );
					continue;
				default:
					throw new InvalidDataException( $"Line {i + 1}: expected docid, name, rollback or op, found '{line}'." );
			}
		}

		var notes = new List<string>();
		if ( version == MapDocument.OldestMigratableFormatVersion )
		{
			var migrated = new List<string>();
			if ( !sawDocId ) migrated.Add( "docid assigned fresh" );
			if ( !sawRollback ) migrated.Add( "rollback defaulted to end of history" );
			if ( missingCompileVisible > 0 ) migrated.Add( $"{missingCompileVisible} layer/visgroup block(s) defaulted compile-visible to visible" );
			notes.Add( "Migrated map format 0 to 1: " + ( migrated.Count == 0 ? "no defaults needed" : string.Join( "; ", migrated ) ) + "." );
		}
		else
		{
			if ( !sawDocId ) notes.Add( "No docid line; assigned a fresh document id." );
			if ( !sawRollback ) notes.Add( "No rollback line; rolled back to end of history." );
			if ( missingCompileVisible > 0 ) notes.Add( $"{missingCompileVisible} layer/visgroup block(s) lacked compile-visible; defaulted to visible." );
		}

		var doc = new MapDocument();
		doc.LoadFrom( history, rollback, docId, name, notes, version );
		if ( doc.LastRebuildReport.HasErrors )
			throw new InvalidDataException(
				"Map document history cannot apply:\n - " + string.Join( "\n - ", doc.LastRebuildReport.Errors ) );
		return doc;
	}

	static MapHistoryEntry ReadOp( string[] lines, ref int i, ref int missingCompileVisible )
	{
		var lineNo = i + 1;
		var line = lines[i].Trim();
		var tokens = Tokens( line );
		// tokens[0] == "op", tokens[1] == kind.
		if ( tokens.Length < 2 )
			throw new InvalidDataException( $"Line {lineNo}: 'op' needs a kind." );
		var kind = tokens[1];
		switch ( kind )
		{
			case MapHistoryEntry.RemoveBrush:
			case MapHistoryEntry.RemoveEntity:
			case MapHistoryEntry.RemoveLayer:
			case MapHistoryEntry.RemoveVisGroup:
			case MapHistoryEntry.RemoveDisplacement:
			case MapHistoryEntry.RemoveIo:
				if ( tokens.Length != 3 )
					throw new InvalidDataException( $"Line {lineNo}: 'op {kind}' needs exactly one id." );
				return new MapHistoryEntry { Id = MapDocument.NewId(), Kind = kind, TargetId = tokens[2] };
			case MapHistoryEntry.SetLayerVisible:
			case MapHistoryEntry.SetVisGroupVisible:
				if ( tokens.Length != 4 )
					throw new InvalidDataException( $"Line {lineNo}: 'op {kind}' needs an id and 0/1." );
				return new MapHistoryEntry { Id = MapDocument.NewId(), Kind = kind, TargetId = tokens[2], Visible = tokens[3] != "0" };
			case MapHistoryEntry.SetCompileSettings:
			{
				// "op set-compile <0|1> <lightmap> <sky, rest of line>".
				var afterKind = StudioDocument.Split( StudioDocument.Split( line ).Value ).Value;
				var (flag, rest2) = StudioDocument.Split( afterKind );
				if ( flag != "0" && flag != "1" )
					throw new InvalidDataException( $"Line {lineNo}: 'op set-compile' needs 0/1, a lightmap scale and a sky name." );
				var (lightmapText, sky) = StudioDocument.Split( rest2 );
				return new MapHistoryEntry
				{
					Id = MapDocument.NewId(),
					Kind = kind,
					Settings = new MapCompileSettings
					{
						CompileVisibleOnly = flag == "1",
						DefaultLightmapScale = ParseFloatStrict( lightmapText, lineNo ),
						SkyName = sky,
					},
				};
			}
			case MapHistoryEntry.MoveBrush:
			{
				if ( tokens.Length != 9 )
					throw new InvalidDataException( $"Line {lineNo}: 'op move-brush' needs an id, a min and a max (6 numbers)." );
				return new MapHistoryEntry
				{
					Id = MapDocument.NewId(),
					Kind = kind,
					TargetId = tokens[2],
					Min = new Vec3( ParseFloatStrict( tokens[3], lineNo ), ParseFloatStrict( tokens[4], lineNo ), ParseFloatStrict( tokens[5], lineNo ) ),
					Max = new Vec3( ParseFloatStrict( tokens[6], lineNo ), ParseFloatStrict( tokens[7], lineNo ), ParseFloatStrict( tokens[8], lineNo ) ),
				};
			}
			case MapHistoryEntry.SetBrushMaterial:
			{
				// "op set-brush-material <id> <material, rest of line>".
				var afterKind = StudioDocument.Split( StudioDocument.Split( line ).Value ).Value;
				var (id, material) = StudioDocument.Split( afterKind );
				if ( string.IsNullOrWhiteSpace( id ) )
					throw new InvalidDataException( $"Line {lineNo}: 'op set-brush-material' needs an id and a material." );
				return new MapHistoryEntry { Id = MapDocument.NewId(), Kind = kind, TargetId = id, Material = material };
			}
			case MapHistoryEntry.MoveEntity:
			{
				if ( tokens.Length != 6 )
					throw new InvalidDataException( $"Line {lineNo}: 'op move-entity' needs an id and an origin (3 numbers)." );
				return new MapHistoryEntry
				{
					Id = MapDocument.NewId(),
					Kind = kind,
					TargetId = tokens[2],
					Origin = new Vec3( ParseFloatStrict( tokens[3], lineNo ), ParseFloatStrict( tokens[4], lineNo ), ParseFloatStrict( tokens[5], lineNo ) ),
				};
			}
			case MapHistoryEntry.SetEntityProperty:
			{
				// "op set-entity-property <id> <key> <value, rest of line>".
				var afterKind = StudioDocument.Split( StudioDocument.Split( line ).Value ).Value;
				var (id, rest2) = StudioDocument.Split( afterKind );
				var (key, value) = StudioDocument.Split( rest2 );
				if ( string.IsNullOrWhiteSpace( id ) || string.IsNullOrWhiteSpace( key ) )
					throw new InvalidDataException( $"Line {lineNo}: 'op set-entity-property' needs an id, a key and a value." );
				return new MapHistoryEntry { Id = MapDocument.NewId(), Kind = kind, TargetId = id, Key = key, Value = value };
			}
			case MapHistoryEntry.RemoveEntityProperty:
			{
				if ( tokens.Length != 4 )
					throw new InvalidDataException( $"Line {lineNo}: 'op remove-entity-property' needs an id and a key." );
				return new MapHistoryEntry { Id = MapDocument.NewId(), Kind = kind, TargetId = tokens[2], Key = tokens[3] };
			}
			case MapHistoryEntry.AddBrush:
			case MapHistoryEntry.AddEntity:
			case MapHistoryEntry.AddLayer:
			case MapHistoryEntry.AddVisGroup:
			case MapHistoryEntry.AddDisplacement:
			case MapHistoryEntry.SetDisplacement:
			case MapHistoryEntry.AddIo:
			case MapHistoryEntry.SetIo:
			{
				if ( tokens.Length != 3 )
					throw new InvalidDataException( $"Line {lineNo}: 'op {kind}' needs exactly one id." );
				return ReadBlockOp( lines, ref i, kind, tokens[2], ref missingCompileVisible );
			}
			default:
				throw new InvalidDataException( $"Line {lineNo}: unknown op kind '{kind}'." );
		}
	}

	static MapHistoryEntry ReadBlockOp( string[] lines, ref int i, string kind, string targetId, ref int missingCompileVisible )
	{
		var startLine = i + 1;
		var entry = new MapHistoryEntry { Id = MapDocument.NewId(), Kind = kind, TargetId = targetId };
		string brushName = null, layerId = null, visgroups = null;
		var brush = kind == MapHistoryEntry.AddBrush ? new MapBrushData { Id = targetId } : null;
		string entityClass = null;
		var entity = kind == MapHistoryEntry.AddEntity ? new MapEntityData { Id = targetId } : null;
		string sectionName = null, colour = null;
		var hasCompileVisible = false;
		var visible = true;
		var compileVisible = true;
		string dispBrush = null;
		var dispFace = 0;
		var dispPower = 0;
		var offsets = new List<float>();
		var alphas = new List<float>();
		string ioSource = null, ioEvent = null, ioTarget = null, ioInput = null, ioParameter = "";
		var ioDelay = 0.0;
		var ioMaxFires = -1;
		var ioEnabled = true;

		for ( i++; i < lines.Length; i++ )
		{
			var raw = lines[i].Trim();
			if ( raw.Length == 0 )
				continue;
			if ( raw == "endop" )
			{
				FinishBlock( entry, kind, targetId, brush, brushName, layerId, visgroups,
					entity, entityClass, sectionName, colour, visible, compileVisible,
					dispBrush, dispFace, dispPower, offsets, alphas,
					ioSource, ioEvent, ioTarget, ioInput, ioParameter, ioDelay, ioMaxFires, ioEnabled,
					startLine, ref missingCompileVisible, hasCompileVisible );
				return entry;
			}
			var (key, rest) = StudioDocument.Split( raw );
			var lineNo = i + 1;
			// Every inner key belongs to exactly the block kinds that write it; anything
			// else is a corrupt file, not a line to skip.
			switch ( key )
			{
				case "name":
					if ( kind == MapHistoryEntry.AddBrush ) brushName = rest;
					else if ( kind == MapHistoryEntry.AddLayer || kind == MapHistoryEntry.AddVisGroup ) sectionName = rest;
					else throw new InvalidDataException( $"Line {lineNo}: 'name' does not belong in {kind}." );
					continue;
				case "layer":
					if ( kind != MapHistoryEntry.AddBrush && kind != MapHistoryEntry.AddEntity )
						throw new InvalidDataException( $"Line {lineNo}: 'layer' does not belong in {kind}." );
					layerId = rest == "-" ? "" : rest.Trim();
					continue;
				case "visgroups":
					if ( kind != MapHistoryEntry.AddBrush && kind != MapHistoryEntry.AddEntity )
						throw new InvalidDataException( $"Line {lineNo}: 'visgroups' does not belong in {kind}." );
					visgroups = rest == "-" ? "" : rest.Trim();
					continue;
				case "face":
					if ( kind == MapHistoryEntry.AddBrush )
						brush.Faces.Add( ReadFace( rest, lineNo ) );
					else if ( kind == MapHistoryEntry.AddDisplacement || kind == MapHistoryEntry.SetDisplacement )
						dispFace = ParseIntStrict( rest, lineNo );
					else
						throw new InvalidDataException( $"Line {lineNo}: 'face' does not belong in {kind}." );
					continue;
				case "class":
					if ( kind != MapHistoryEntry.AddEntity )
						throw new InvalidDataException( $"Line {lineNo}: 'class' does not belong in {kind}." );
					entityClass = rest.Trim();
					continue;
				case "origin":
					if ( kind != MapHistoryEntry.AddEntity )
						throw new InvalidDataException( $"Line {lineNo}: 'origin' does not belong in {kind}." );
					entity.Origin = ParseVec3Strict( rest, lineNo );
					continue;
				case "angles":
					if ( kind != MapHistoryEntry.AddEntity )
						throw new InvalidDataException( $"Line {lineNo}: 'angles' does not belong in {kind}." );
					entity.Angles = ParseVec3Strict( rest, lineNo );
					continue;
				case "key":
				{
					if ( kind != MapHistoryEntry.AddEntity )
						throw new InvalidDataException( $"Line {lineNo}: 'key' does not belong in {kind}." );
					var (k, v) = StudioDocument.Split( rest );
					if ( string.IsNullOrWhiteSpace( k ) )
						throw new InvalidDataException( $"Line {lineNo}: 'key' needs a name." );
					// "origin" and "angles" are entity FIELDS (their own lines), never
					// keyvalues — a foreign file that carries them as keys is normalised
					// here, so KeyValues never holds a competing copy (see MapEntityData).
					if ( string.Equals( k, "origin", StringComparison.OrdinalIgnoreCase ) )
					{
						entity.Origin = ParseVec3Strict( v, lineNo );
						continue;
					}
					if ( string.Equals( k, "angles", StringComparison.OrdinalIgnoreCase ) )
					{
						entity.Angles = ParseVec3Strict( v, lineNo );
						continue;
					}
					entity.KeyValues[k] = v;
					continue;
				}
				case "brush":
					if ( kind == MapHistoryEntry.AddEntity )
						entity.BrushIds.Add( rest.Trim() );
					else if ( kind == MapHistoryEntry.AddDisplacement || kind == MapHistoryEntry.SetDisplacement )
						dispBrush = rest.Trim();
					else
						throw new InvalidDataException( $"Line {lineNo}: 'brush' does not belong in {kind}." );
					continue;
				case "colour":
					if ( kind != MapHistoryEntry.AddLayer && kind != MapHistoryEntry.AddVisGroup )
						throw new InvalidDataException( $"Line {lineNo}: 'colour' does not belong in {kind}." );
					colour = rest.Trim();
					continue;
				case "visible":
					if ( kind != MapHistoryEntry.AddLayer && kind != MapHistoryEntry.AddVisGroup )
						throw new InvalidDataException( $"Line {lineNo}: 'visible' does not belong in {kind}." );
					visible = rest.Trim() != "0";
					continue;
				case "compilevisible":
					if ( kind != MapHistoryEntry.AddLayer && kind != MapHistoryEntry.AddVisGroup )
						throw new InvalidDataException( $"Line {lineNo}: 'compilevisible' does not belong in {kind}." );
					compileVisible = rest.Trim() != "0";
					hasCompileVisible = true;
					continue;
				case "power":
					if ( kind != MapHistoryEntry.AddDisplacement && kind != MapHistoryEntry.SetDisplacement )
						throw new InvalidDataException( $"Line {lineNo}: 'power' does not belong in {kind}." );
					dispPower = ParseIntStrict( rest, lineNo );
					continue;
				case "dispoff":
					if ( kind != MapHistoryEntry.AddDisplacement && kind != MapHistoryEntry.SetDisplacement )
						throw new InvalidDataException( $"Line {lineNo}: 'dispoff' does not belong in {kind}." );
					offsets.Add( ParseFloatStrict( rest, lineNo ) );
					continue;
				case "dispalpha":
					if ( kind != MapHistoryEntry.AddDisplacement && kind != MapHistoryEntry.SetDisplacement )
						throw new InvalidDataException( $"Line {lineNo}: 'dispalpha' does not belong in {kind}." );
					alphas.Add( ParseFloatStrict( rest, lineNo ) );
					continue;
				case "source":
				case "event":
				case "target":
				case "input":
				case "parameter":
				case "delay":
				case "maxfires":
				case "enabled":
					if ( kind != MapHistoryEntry.AddIo && kind != MapHistoryEntry.SetIo )
						throw new InvalidDataException( $"Line {lineNo}: '{key}' does not belong in {kind}." );
					switch ( key )
					{
						case "source": ioSource = rest; break;
						case "event": ioEvent = rest; break;
						case "target": ioTarget = rest; break;
						case "input": ioInput = rest; break;
						case "parameter": ioParameter = rest; break;
						case "delay": ioDelay = ParseDoubleStrict( rest, lineNo ); break;
						case "maxfires": ioMaxFires = ParseIntStrict( rest, lineNo ); break;
						case "enabled": ioEnabled = rest.Trim() != "0"; break;
					}
					continue;
				default:
					throw new InvalidDataException( $"Line {lineNo}: unexpected '{key}' inside {kind}." );
			}
		}
		throw new InvalidDataException( $"The document ends inside op {kind} '{targetId}' (line {startLine}) — no 'endop' line." );
	}

	static void FinishBlock( MapHistoryEntry entry, string kind, string targetId,
		MapBrushData brush, string brushName, string layerId, string visgroups,
		MapEntityData entity, string entityClass, string sectionName, string colour, bool visible, bool compileVisible,
		string dispBrush, int dispFace, int dispPower, List<float> offsets, List<float> alphas,
		string ioSource, string ioEvent, string ioTarget, string ioInput, string ioParameter,
		double ioDelay, int ioMaxFires, bool ioEnabled,
		int startLine, ref int missingCompileVisible, bool hasCompileVisible )
	{
		switch ( kind )
		{
			case MapHistoryEntry.AddBrush:
				brush.Name = brushName ?? "";
				brush.LayerId = layerId ?? "";
				if ( !string.IsNullOrEmpty( visgroups ) )
					brush.VisGroupIds.AddRange( visgroups.Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( v => v.Trim() ) );
				entry.Brush = brush;
				return;
			case MapHistoryEntry.AddEntity:
				entity.ClassName = entityClass ?? "";
				entity.LayerId = layerId ?? "";
				if ( !string.IsNullOrEmpty( visgroups ) )
					entity.VisGroupIds.AddRange( visgroups.Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( v => v.Trim() ) );
				entry.Entity = entity;
				return;
			case MapHistoryEntry.AddLayer:
				if ( !hasCompileVisible ) missingCompileVisible++;
				entry.Layer = new MapLayer { Id = targetId, Name = sectionName ?? "", Colour = colour ?? "#808080", Visible = visible, CompileVisible = compileVisible };
				return;
			case MapHistoryEntry.AddVisGroup:
				if ( !hasCompileVisible ) missingCompileVisible++;
				entry.VisGroup = new MapVisGroup { Id = targetId, Name = sectionName ?? "", Colour = colour ?? "#808080", Visible = visible, CompileVisible = compileVisible };
				return;
			case MapHistoryEntry.AddDisplacement:
			case MapHistoryEntry.SetDisplacement:
			{
				if ( string.IsNullOrWhiteSpace( dispBrush ) )
					throw new InvalidDataException( $"Op {kind} '{targetId}' (line {startLine}): displacement needs a brush." );
				int size;
				try { size = Effigy.Map.Displacement.GridSizeForPower( dispPower ); }
				catch ( ArgumentOutOfRangeException ex )
				{
					throw new InvalidDataException( $"Op {kind} '{targetId}' (line {startLine}): {ex.Message}", ex );
				}
				var count = size * size;
				if ( offsets.Count != count )
					throw new InvalidDataException( $"Op {kind} '{targetId}' (line {startLine}): displacement needs {count} offsets for power {dispPower}, got {offsets.Count}." );
				if ( alphas.Count != count )
					throw new InvalidDataException( $"Op {kind} '{targetId}' (line {startLine}): displacement needs {count} alphas for power {dispPower}, got {alphas.Count}." );
				entry.Displacement = new MapDisplacementData
				{
					Id = targetId,
					BrushId = dispBrush,
					FaceIndex = dispFace,
					Power = dispPower,
					Offsets = offsets.ToArray(),
					Alphas = alphas.ToArray(),
				};
				return;
			}
			case MapHistoryEntry.AddIo:
			case MapHistoryEntry.SetIo:
			{
				try
				{
					entry.Io = new MapIoConnection( targetId, new EntityIoOutput(
						ioSource ?? "", ioEvent ?? "", ioTarget ?? "", ioInput ?? "",
						ioDelay, ioMaxFires, ioParameter ?? "", ioEnabled ) );
				}
				catch ( ArgumentException ex )
				{
					throw new InvalidDataException( $"Op {kind} '{targetId}' (line {startLine}): invalid I/O row: {ex.Message}", ex );
				}
				return;
			}
		}
	}

	static MapBrushFaceData ReadFace( string rest, int lineNo )
	{
		// 12 values: normal(3) distance material uvoffset(2) uvscale(2) rotation lightmap smooth.
		var p = Tokens( rest );
		if ( p.Length != 12 )
			throw new InvalidDataException( $"Line {lineNo}: face needs normal(3) distance material uvoffset(2) uvscale(2) rotation lightmap smooth (12 values), got {p.Length}." );
		var normal = new Vec3( ParseFloatStrict( p[0], lineNo ), ParseFloatStrict( p[1], lineNo ), ParseFloatStrict( p[2], lineNo ) );
		var distance = ParseFloatStrict( p[3], lineNo );
		var material = p[4];
		if ( string.IsNullOrWhiteSpace( material ) )
			throw new InvalidDataException( $"Line {lineNo}: face needs a material." );
		var uv = new FaceUv(
			new Vec2( ParseFloatStrict( p[5], lineNo ), ParseFloatStrict( p[6], lineNo ) ),
			new Vec2( ParseFloatStrict( p[7], lineNo ), ParseFloatStrict( p[8], lineNo ) ),
			ParseFloatStrict( p[9], lineNo ),
			ParseFloatStrict( p[10], lineNo ),
			ParseIntStrict( p[11], lineNo ) );
		return new MapBrushFaceData( normal, distance, new FaceMaterial( material, uv ) );
	}

	// --- strict numbers (fail closed, unlike StudioDocument.ParseFloat) ------------------------

	static string[] Tokens( string s ) => s.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

	static float ParseFloatStrict( string s, int lineNo ) =>
		float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ? f
			: throw new InvalidDataException( $"Line {lineNo}: '{s}' is not a number." );

	static double ParseDoubleStrict( string s, int lineNo ) =>
		double.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d ) ? d
			: throw new InvalidDataException( $"Line {lineNo}: '{s}' is not a number." );

	static int ParseIntStrict( string s, int lineNo ) =>
		int.TryParse( s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i ) ? i
			: throw new InvalidDataException( $"Line {lineNo}: '{s}' is not an integer." );

	static Vec3 ParseVec3Strict( string s, int lineNo )
	{
		var p = Tokens( s );
		if ( p.Length != 3 )
			throw new InvalidDataException( $"Line {lineNo}: expected 3 numbers, got '{s}'." );
		return new Vec3( ParseFloatStrict( p[0], lineNo ), ParseFloatStrict( p[1], lineNo ), ParseFloatStrict( p[2], lineNo ) );
	}
}
