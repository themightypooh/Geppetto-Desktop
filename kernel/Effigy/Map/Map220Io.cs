using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy.Map;

// Valve 220 .map reader/writer — the interchange lingua franca (TrenchBroom,
// J.A.C.K.). Same transfer model as VmfIo (VmfDocument) so a map converts
// VMF <-> .map without loss of planes, materials or UV axes.
//
// A .map brush face line, Valve 220 form:
//   ( x y z ) ( x y z ) ( x y z ) MATERIAL [ ux uy uz uoff ] [ vx vy vz voff ] rotation xscale yscale
// The legacy (non-220) form is also accepted on import:
//   MATERIAL xoffset yoffset rotation xscale yscale
// and written back in legacy form for faces that came from one
// (VmfFace.UvFromStandardMap). Everything else about the legacy form has no
// explicit axes, so import uses the conventional defaults U=(1,0,0),
// V=(0,-1,0).
//
// KNOWN LOSSES (Valve 220 .map has no place for them; reported in the
// warnings of Write(doc, out warnings), never silently dropped):
//   - displacements (VMF dispinfo): dropped, one warning per face.
//   - entity I/O (VMF connections): dropped, one warning per entity.
//   - visgroups, cameras, cordon, versioninfo, editor blocks: not part of
//     the .map format; visgroup ids ride on brushes only as comments where
//     the writer can (brush ids are kept in "// brush N" comments).
// RMF is out of scope for this ticket (read-only import only if cheap —
// noted, not implemented).

/// <summary>Valve 220 .map reader/writer over the shared VmfDocument model.</summary>
public static class Map220Io
{
	public static VmfDocument Parse( string text )
	{
		if ( text == null ) throw new ArgumentNullException( nameof( text ) );
		var parser = new MapParser( text );
		return parser.ParseDocument();
	}

	public static VmfDocument ParseFile( string path ) => Parse( File.ReadAllText( path ) );

	/// <summary>Write Valve 220 .map text. Warnings name every dropped displacement / I/O row.</summary>
	public static string Write( VmfDocument doc, out List<string> warnings )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		warnings = new List<string>();
		var sb = new StringBuilder();
		sb.AppendLine( "// Game: Half-Life 2" );
		sb.AppendLine( "// Format: Valve" );
		sb.AppendLine( "// Geppetto Desktop Valve 220 export — planes, materials and UV axes preserved; displacements and entity I/O do not exist in .map and are listed in the export warnings" );
		WriteEntity( sb, doc, null, warnings, isWorld: true );
		foreach ( var e in doc.Entities ) WriteEntity( sb, doc, e, warnings, isWorld: false );
		return sb.ToString();
	}

	public static string Write( VmfDocument doc )
	{
		List<string> ignored;
		return Write( doc, out ignored );
	}

	public static void WriteFile( VmfDocument doc, string path )
	{
		List<string> ignored;
		File.WriteAllText( path, Write( doc, out ignored ) );
	}

	static void WriteEntity( StringBuilder sb, VmfDocument doc, VmfEntity entity, List<string> warnings, bool isWorld )
	{
		var id = isWorld ? 0 : entity.Id;
		sb.AppendLine( $"// entity {id}" );
		sb.AppendLine( "{" );
		if ( isWorld )
		{
			sb.AppendLine( "\"classname\" \"worldspawn\"" );
			foreach ( var kv in doc.WorldspawnKeyValues )
				sb.AppendLine( $"{MapIoText.Quote( kv.Key )} {MapIoText.Quote( kv.Value )}" );
		}
		else
		{
			sb.AppendLine( $"\"classname\" {MapIoText.Quote( entity.ClassName )}" );
			foreach ( var kv in entity.KeyValues )
				sb.AppendLine( $"{MapIoText.Quote( kv.Key )} {MapIoText.Quote( kv.Value )}" );
			if ( entity.Outputs.Count > 0 )
				warnings.Add( $"entity '{entity.ClassName}'#{entity.Id} ({entity.TargetName}): {entity.Outputs.Count} I/O output(s) dropped — Valve 220 .map has no connections section" );
		}
		var brushes = isWorld ? doc.WorldBrushes : entity.Brushes;
		foreach ( var b in brushes )
		{
			sb.AppendLine( $"// brush {b.Id}" );
			sb.AppendLine( "{" );
			foreach ( var f in b.Faces )
			{
				if ( f.Displacement != null )
					warnings.Add( $"brush #{b.Id} face #{f.Id}: displacement (power {f.Displacement.Power}) dropped — Valve 220 .map has no dispinfo" );
				sb.AppendLine( WriteFace( f ) );
			}
			sb.AppendLine( "}" );
		}
		sb.AppendLine( "}" );
	}

	static string WriteFace( VmfFace f )
	{
		// PLANE FIDELITY: the plane is written from the face's stored plane
		// points, never from triangulated vertices.
		var sb = new StringBuilder();
		sb.Append( f.PlanePoints[0].ToVmfString() ).Append( ' ' );
		sb.Append( f.PlanePoints[1].ToVmfString() ).Append( ' ' );
		sb.Append( f.PlanePoints[2].ToVmfString() ).Append( ' ' );
		sb.Append( f.Material ).Append( ' ' );
		if ( f.UvFromStandardMap )
		{
			sb.Append( MapIoText.Num( f.UAxis.Offset ) ).Append( ' ' );
			sb.Append( MapIoText.Num( f.VAxis.Offset ) ).Append( ' ' );
			sb.Append( MapIoText.Num( f.Rotation ) ).Append( ' ' );
			sb.Append( MapIoText.Num( f.UAxis.Scale ) ).Append( ' ' );
			sb.Append( MapIoText.Num( f.VAxis.Scale ) );
		}
		else
		{
			// Valve 220: "[u] [v] rotation xscale yscale" — the scale follows
			// both brackets; it must NOT be written after each bracket (that
			// is the VMF form, and this parser rightly rejects it).
			sb.Append( f.UAxis.ToBracketString() ).Append( ' ' );
			sb.Append( f.VAxis.ToBracketString() ).Append( ' ' );
			sb.Append( MapIoText.Num( f.Rotation ) ).Append( ' ' );
			sb.Append( MapIoText.Num( f.UAxis.Scale ) ).Append( ' ' );
			sb.Append( MapIoText.Num( f.VAxis.Scale ) );
		}
		return sb.ToString();
	}

	// ----- line-based parser -----

	sealed class MapParser
	{
		readonly string[] _lines;

		public MapParser( string text ) { _lines = text.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Split( '\n' ); }

		public VmfDocument ParseDocument()
		{
			var doc = new VmfDocument();
			int i = 0;
			int pendingEntityId = 0;
			bool firstEntity = true;
			while ( i < _lines.Length )
			{
				var raw = StripComment( _lines[i] );
				var line = raw.Trim();
				int lineNo = i + 1;
				i++;
				if ( line.Length == 0 ) continue;
				if ( raw.TrimStart().StartsWith( "//", StringComparison.Ordinal ) )
				{
					// A "// entity N" comment ahead of a block recovers the id.
					var comment = raw.Trim();
					if ( comment.StartsWith( "// entity", StringComparison.Ordinal ) )
					{
						var num = comment.Substring( "// entity".Length ).Trim();
						pendingEntityId = MapIoText.ParseInt( num, "map220", lineNo );
					}
					continue;
				}
				if ( line == "{" )
				{
					var entity = ParseEntity( ref i, pendingEntityId, lineNo );
					pendingEntityId = 0;
					if ( firstEntity || entity.ClassName == "worldspawn" )
					{
						firstEntity = false;
						if ( entity.ClassName == "worldspawn" )
						{
							doc.WorldspawnKeyValues.AddRange( entity.KeyValues );
							doc.WorldBrushes.AddRange( entity.Brushes );
							continue;
						}
					}
					firstEntity = false;
					doc.Entities.Add( entity );
					continue;
				}
				throw new MapParseException( "map220", lineNo, $"expected '{{' to start an entity, got '{line}'" );
			}
			return doc;
		}

		VmfEntity ParseEntity( ref int i, int pendingId, int openLine )
		{
			var entity = new VmfEntity { Id = pendingId };
			int pendingBrushId = 0;
			while ( i < _lines.Length )
			{
				var raw = StripComment( _lines[i] );
				var line = raw.Trim();
				int lineNo = i + 1;
				i++;
				if ( line.Length == 0 ) continue;
				if ( raw.TrimStart().StartsWith( "//", StringComparison.Ordinal ) )
				{
					var comment = raw.Trim();
					if ( comment.StartsWith( "// brush", StringComparison.Ordinal ) )
						pendingBrushId = MapIoText.ParseInt( comment.Substring( "// brush".Length ).Trim(), "map220", lineNo );
					continue;
				}
				if ( line == "}" ) return entity;
				if ( line == "{" )
				{
					entity.Brushes.Add( ParseBrush( ref i, pendingBrushId, lineNo ) );
					pendingBrushId = 0;
					continue;
				}
				if ( line.StartsWith( "\"", StringComparison.Ordinal ) )
				{
					var kv = ParseKeyValue( line, lineNo );
					if ( kv.Key == "classname" ) entity.ClassName = kv.Value;
					else entity.KeyValues.Add( kv );
					continue;
				}
				throw new MapParseException( "map220", lineNo, $"unexpected line inside entity: '{line}'" );
			}
			throw new MapParseException( "map220", openLine, "unexpected end of file inside entity (opened line " + openLine + ")" );
		}

		VmfBrush ParseBrush( ref int i, int pendingId, int openLine )
		{
			var brush = new VmfBrush { Id = pendingId };
			int faceId = 0;
			while ( i < _lines.Length )
			{
				var raw = StripComment( _lines[i] );
				var line = raw.Trim();
				int lineNo = i + 1;
				i++;
				if ( line.Length == 0 ) continue;
				if ( raw.TrimStart().StartsWith( "//", StringComparison.Ordinal ) ) continue;
				if ( line == "}" )
				{
					if ( brush.Faces.Count == 0 ) throw new MapParseException( "map220", openLine, $"brush #{brush.Id} has no faces" );
					return brush;
				}
				if ( line.StartsWith( "(", StringComparison.Ordinal ) )
				{
					brush.Faces.Add( ParseFace( line, lineNo, ++faceId ) );
					continue;
				}
				throw new MapParseException( "map220", lineNo, $"unexpected line inside brush: '{line}'" );
			}
			throw new MapParseException( "map220", openLine, "unexpected end of file inside brush (opened line " + openLine + ")" );
		}

		VmfFace ParseFace( string line, int lineNo, int faceId )
		{
			var tokens = TokenizeFaceLine( line, lineNo );
			// ( p ) ( p ) ( p ) MATERIAL ... — the first three tokens are paren groups.
			if ( tokens.Count < 4 || tokens[0].Kind != FaceTokKind.Paren || tokens[1].Kind != FaceTokKind.Paren || tokens[2].Kind != FaceTokKind.Paren )
				throw new MapParseException( "map220", lineNo, $"face line needs three plane points '(x y z) (x y z) (x y z)', got '{line}'" );
			var face = new VmfFace { Id = faceId };
			face.PlanePoints = new[]
			{
				VmfVec.Parse( tokens[0].Text, "map220", lineNo ),
				VmfVec.Parse( tokens[1].Text, "map220", lineNo ),
				VmfVec.Parse( tokens[2].Text, "map220", lineNo ),
			};
			face.Material = tokens[3].Kind == FaceTokKind.Word ? MapIoText.Unquote( tokens[3].Text ) : tokens[3].Text;
			var rest = tokens.GetRange( 4, tokens.Count - 4 );
			if ( rest.Count == 5 && rest[0].Kind == FaceTokKind.Bracket && rest[1].Kind == FaceTokKind.Bracket
				&& rest[2].Kind == FaceTokKind.Word && rest[3].Kind == FaceTokKind.Word && rest[4].Kind == FaceTokKind.Word )
			{
				// Valve 220: [uaxis] [vaxis] rotation xscale yscale.
				face.UAxis = ParseUvAxisWithScale( rest[0].Text, rest[3].Text, lineNo );
				face.VAxis = ParseUvAxisWithScale( rest[1].Text, rest[4].Text, lineNo );
				face.Rotation = MapIoText.ParseDouble( rest[2].Text, "map220", lineNo );
				face.UvFromStandardMap = false;
			}
			else if ( rest.Count == 5 && rest.TrueForAll( t => t.Kind == FaceTokKind.Word ) )
			{
				// Legacy standard .map: xoffset yoffset rotation xscale yscale.
				face.UAxis = new VmfUvAxis( new VmfVec( 1, 0, 0 ), MapIoText.ParseDouble( rest[0].Text, "map220", lineNo ), MapIoText.ParseDouble( rest[3].Text, "map220", lineNo ) );
				face.VAxis = new VmfUvAxis( new VmfVec( 0, -1, 0 ), MapIoText.ParseDouble( rest[1].Text, "map220", lineNo ), MapIoText.ParseDouble( rest[4].Text, "map220", lineNo ) );
				face.Rotation = MapIoText.ParseDouble( rest[2].Text, "map220", lineNo );
				face.UvFromStandardMap = true;
			}
			else
			{
				throw new MapParseException( "map220", lineNo,
					$"face line UVs must be Valve 220 '[u] [v] rotation xscale yscale' or legacy 'xoff yoff rotation xscale yscale', got '{line}'" );
			}
			return face;
		}

		static VmfUvAxis ParseUvAxisWithScale( string bracket, string scaleText, int lineNo )
		{
			// "[ux uy uz uoff]" + a separate scale token.
			var inner = bracket.Trim().TrimStart( '[' ).TrimEnd( ']' ).Trim()
				.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( inner.Length != 4 )
				throw new MapParseException( "map220", lineNo, $"expected a UV axis '[x y z off]', got '{bracket}'" );
			return new VmfUvAxis(
				new VmfVec(
					MapIoText.ParseDouble( inner[0], "map220", lineNo ),
					MapIoText.ParseDouble( inner[1], "map220", lineNo ),
					MapIoText.ParseDouble( inner[2], "map220", lineNo ) ),
				MapIoText.ParseDouble( inner[3], "map220", lineNo ),
				MapIoText.ParseDouble( scaleText, "map220", lineNo ) );
		}

		enum FaceTokKind { Paren, Bracket, Word }

		sealed class FaceTok
		{
			public FaceTokKind Kind; public string Text = "";
		}

		static List<FaceTok> TokenizeFaceLine( string line, int lineNo )
		{
			var tokens = new List<FaceTok>();
			int i = 0;
			while ( i < line.Length )
			{
				while ( i < line.Length && char.IsWhiteSpace( line[i] ) ) i++;
				if ( i >= line.Length ) break;
				char c = line[i];
				if ( c == '(' || c == '[' )
				{
					char close = c == '(' ? ')' : ']';
					int end = line.IndexOf( close, i );
					if ( end < 0 ) throw new MapParseException( "map220", lineNo, $"unterminated '{c}...{close}' group in '{line}'" );
					tokens.Add( new FaceTok { Kind = c == '(' ? FaceTokKind.Paren : FaceTokKind.Bracket, Text = line.Substring( i, end - i + 1 ) } );
					i = end + 1;
				}
				else if ( c == '"' )
				{
					int end = line.IndexOf( '"', i + 1 );
					if ( end < 0 ) throw new MapParseException( "map220", lineNo, $"unterminated quoted string in '{line}'" );
					tokens.Add( new FaceTok { Kind = FaceTokKind.Word, Text = line.Substring( i, end - i + 1 ) } );
					i = end + 1;
				}
				else
				{
					int start = i;
					while ( i < line.Length && !char.IsWhiteSpace( line[i] ) ) i++;
					tokens.Add( new FaceTok { Kind = FaceTokKind.Word, Text = line.Substring( start, i - start ) } );
				}
			}
			return tokens;
		}

		static KeyValuePair<string, string> ParseKeyValue( string line, int lineNo )
		{
			var tokens = TokenizeFaceLine( line, lineNo );
			if ( tokens.Count != 2 )
				throw new MapParseException( "map220", lineNo, $"expected '\"key\" \"value\"', got '{line}'" );
			return new KeyValuePair<string, string>( MapIoText.Unquote( tokens[0].Text ), MapIoText.Unquote( tokens[1].Text ) );
		}

		/// <summary>Strip a // comment, respecting quoted strings.</summary>
		static string StripComment( string line )
		{
			bool inQuotes = false;
			for ( int i = 0; i + 1 < line.Length; i++ )
			{
				if ( line[i] == '"' ) inQuotes = !inQuotes;
				if ( !inQuotes && line[i] == '/' && line[i + 1] == '/' ) return line.Substring( 0, i );
			}
			return line;
		}
	}
}
