using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Effigy;

namespace Effigy.Export;

/// <summary>Thrown when a .usda file cannot be parsed; <see cref="Line"/> names where, so a bad file fails clearly instead of importing half a stage.</summary>
public sealed class UsdParseException : Exception
{
	public int Line { get; }

	public UsdParseException( string message, int line ) : base( message + " (line " + line + ")" )
	{
		Line = line;
	}
}

/// <summary>
/// .usda text reader and writer (godlike-usd-stage-alembic-materialx).
///
/// Subset supported: `#usda 1.0` headers, layer statements (defaultPrim,
/// subLayers, upAxis, metersPerUnit, timeCodesPerSecond, start/endTimeCode,
/// customLayerData), `def` / `over` / `class` prim blocks with metadata
/// (references, payload, inherits, specializes, variantSets, variants,
/// kind, active, instanceable), typed attributes (scalar, tuple and array
/// values, `uniform`/`custom` prefixes), relationships, and
/// `variantSet` blocks. Binary .usdc crates are NOT supported — the reader
/// refuses them by magic instead of misreading them.
/// </summary>
public static class UsdAscii
{
	public static string Write( UsdLayer layer )
	{
		if ( layer == null ) throw new ArgumentNullException( nameof( layer ) );
		var sb = new StringBuilder();
		sb.AppendLine( "#usda 1.0" );
		sb.AppendLine( "# Exported by Geppetto Desktop (godlike-usd-stage-alembic-materialx)" );
		sb.AppendLine( "# Units: metres, upAxis Y. Kernel inches Z-up converted on write (see UsdInterchange)." );
		if ( !string.IsNullOrEmpty( layer.DefaultPrim ) ) sb.AppendLine( "defaultPrim = \"" + layer.DefaultPrim + "\"" );
		if ( layer.Sublayers.Count > 0 )
		{
			sb.Append( "subLayers = [" );
			for ( int i = 0; i < layer.Sublayers.Count; i++ )
			{
				if ( i > 0 ) sb.Append( ", " );
				sb.Append( "@" + layer.Sublayers[i] + "@" );
			}
			sb.AppendLine( "]" );
		}
		sb.AppendLine( "upAxis = \"" + layer.UpAxis + "\"" );
		sb.AppendLine( "metersPerUnit = " + layer.MetersPerUnit.ToString( "R", CultureInfo.InvariantCulture ) );
		if ( layer.TimeCodesPerSecond != 24.0 ) sb.AppendLine( "timeCodesPerSecond = " + layer.TimeCodesPerSecond.ToString( "R", CultureInfo.InvariantCulture ) );
		if ( layer.StartTimeCode != 0.0 || layer.EndTimeCode != 0.0 )
		{
			sb.AppendLine( "startTimeCode = " + layer.StartTimeCode.ToString( "R", CultureInfo.InvariantCulture ) );
			sb.AppendLine( "endTimeCode = " + layer.EndTimeCode.ToString( "R", CultureInfo.InvariantCulture ) );
		}
		sb.AppendLine();
		foreach ( var root in layer.Roots ) WritePrim( sb, root, 0 );
		return sb.ToString();
	}

	static void WritePrim( StringBuilder sb, UsdPrim prim, int indent )
	{
		string pad = new string( '\t', indent );
		string keyword = prim.IsOver ? "over" : "def";
		sb.Append( pad + keyword + " " + prim.TypeName + " \"" + prim.Name + "\"" );
		var meta = new List<string>();
		if ( prim.References.Count > 0 )
		{
			var refs = new List<string>();
			foreach ( string r in prim.References ) refs.Add( FormatReferenceTarget( r ) );
			meta.Add( "references = " + ( refs.Count == 1 ? refs[0] : "[" + string.Join( ", ", refs ) + "]" ) );
		}
		if ( !string.IsNullOrEmpty( prim.Payload ) ) meta.Add( "payload = " + FormatReferenceTarget( prim.Payload ) );
		if ( prim.Inherits.Count > 0 ) meta.Add( "inherits = " + FormatPathList( prim.Inherits ) );
		if ( prim.Specializes.Count > 0 ) meta.Add( "specializes = " + FormatPathList( prim.Specializes ) );
		if ( prim.VariantSets.Count > 0 )
		{
			var sets = new List<string>();
			foreach ( var pair in prim.VariantSets ) sets.Add( "\"" + pair.Key + "\"" );
			meta.Add( "variantSets = [" + string.Join( ", ", sets ) + "]" );
		}
		if ( prim.SelectedVariants.Count > 0 )
		{
			var sels = new List<string>();
			foreach ( var pair in prim.SelectedVariants ) sels.Add( "string " + pair.Key + " = \"" + pair.Value + "\"" );
			meta.Add( "variants = { " + string.Join( ", ", sels ) + " }" );
		}
		if ( prim.Instanceable ) meta.Add( "instanceable = true" );
		if ( !prim.Active ) meta.Add( "active = false" );
		foreach ( string raw in prim.MetadataRaw ) meta.Add( raw );
		if ( meta.Count > 0 ) sb.Append( " (\n" + pad + "\t" + string.Join( "\n" + pad + "\t", meta ) + "\n" + pad + ")" );
		sb.AppendLine();
		sb.AppendLine( pad + "{" );
		foreach ( var attr in prim.Attributes )
		{
			string prefix = string.IsNullOrEmpty( attr.Prefix ) ? "" : attr.Prefix + " ";
			sb.AppendLine( pad + "\t" + prefix + attr.Value.TypeName + " " + attr.Name + " = " + attr.Value.Format() );
		}
		foreach ( var rel in prim.Relationships )
			sb.AppendLine( pad + "\trel " + rel.Key + " = " + rel.Value );
		foreach ( var pair in prim.VariantSets )
		{
			sb.AppendLine( pad + "\tvariantSet \"" + pair.Key + "\" {" );
			foreach ( string variant in pair.Value )
			{
				string key = pair.Key + "\n" + variant;
				sb.AppendLine( pad + "\t\t\"" + variant + "\" {" );
				if ( prim.VariantChildren.TryGetValue( key, out var children ) )
					foreach ( var child in children ) WritePrim( sb, child, indent + 3 );
				sb.AppendLine( pad + "\t\t}" );
			}
			sb.AppendLine( pad + "\t}" );
		}
		foreach ( var child in prim.Children ) WritePrim( sb, child, indent + 1 );
		sb.AppendLine( pad + "}" );
		sb.AppendLine();
	}

	static string FormatReferenceTarget( string target )
	{
		if ( string.IsNullOrEmpty( target ) ) return "\"\"";
		if ( target[0] == '@' || target[0] == '<' ) return target;
		return "<" + target + ">";
	}

	static string FormatPathList( List<string> paths )
	{
		var formatted = new List<string>();
		foreach ( string p in paths ) formatted.Add( "<" + p + ">" );
		return "[" + string.Join( ", ", formatted ) + "]";
	}

	public static UsdLayer ReadFile( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "USD input path is empty" );
		if ( !File.Exists( filePath ) ) throw new FileNotFoundException( "USD file not found: " + filePath );
		byte[] head = new byte[4];
		using ( var fs = File.OpenRead( filePath ) ) fs.Read( head, 0, 4 );
		if ( head[0] == (byte)'P' && head[1] == (byte)'N' && head[2] == (byte)'A' && head[3] == (byte)'R' )
			throw new UsdParseException( "binary .usdc crate: this slice reads .usda text only (see KnownLimitations)", 1 );
		return Read( File.ReadAllText( filePath ), Path.GetFileNameWithoutExtension( filePath ) );
	}

	public static UsdLayer Read( string text, string name = "root" )
	{
		if ( text == null ) throw new ArgumentNullException( nameof( text ) );
		var tokens = Tokenize( text );
		var parser = new Parser( tokens, text );
		return parser.ParseLayer( name );
	}

	// ----- tokenizer -----

	sealed class Token
	{
		public enum Kind { Ident, Number, Str, Asset, PrimRef, Symbol }
		public Kind Type;
		public string Text;
		public int Line;
		public int Start;
		public int End;
	}

	static List<Token> Tokenize( string text )
	{
		var tokens = new List<Token>();
		int i = 0;
		int line = 1;
		while ( i < text.Length )
		{
			char c = text[i];
			if ( c == '\n' ) { line++; i++; continue; }
			if ( c == ' ' || c == '\t' || c == '\r' ) { i++; continue; }
			if ( c == '#' ) { while ( i < text.Length && text[i] != '\n' ) i++; continue; }
			if ( c == '"' )
			{
				int start = i;
				if ( i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"' )
				{
					int end = text.IndexOf( "\"\"\"", i + 3, StringComparison.Ordinal );
					if ( end < 0 ) throw new UsdParseException( "unterminated triple-quoted string", line );
					string body = text.Substring( i + 3, end - ( i + 3 ) );
					line += CountNewlines( body );
					tokens.Add( new Token { Type = Token.Kind.Str, Text = body, Line = line, Start = start, End = end + 3 } );
					i = end + 3;
					continue;
				}
				var sb = new StringBuilder();
				i++;
				while ( i < text.Length && text[i] != '"' )
				{
					if ( text[i] == '\\' && i + 1 < text.Length )
					{
						i++;
						switch ( text[i] )
						{
							case 'n': sb.Append( '\n' ); break;
							case 't': sb.Append( '\t' ); break;
							case 'r': sb.Append( '\r' ); break;
							default: sb.Append( text[i] ); break;
						}
						i++;
					}
					else if ( text[i] == '\n' ) throw new UsdParseException( "newline inside string literal", line );
					else { sb.Append( text[i] ); i++; }
				}
				if ( i >= text.Length ) throw new UsdParseException( "unterminated string literal", line );
				i++;
				tokens.Add( new Token { Type = Token.Kind.Str, Text = sb.ToString(), Line = line, Start = start, End = i } );
				continue;
			}
			if ( c == '@' )
			{
				int start = i;
				int end = text.IndexOf( '@', i + 1 );
				if ( end < 0 ) throw new UsdParseException( "unterminated asset path", line );
				tokens.Add( new Token { Type = Token.Kind.Asset, Text = text.Substring( i + 1, end - ( i + 1 ) ), Line = line, Start = start, End = end + 1 } );
				i = end + 1;
				continue;
			}
			if ( c == '<' )
			{
				int start = i;
				int end = text.IndexOf( '>', i + 1 );
				if ( end < 0 ) throw new UsdParseException( "unterminated prim path", line );
				tokens.Add( new Token { Type = Token.Kind.PrimRef, Text = text.Substring( i + 1, end - ( i + 1 ) ), Line = line, Start = start, End = end + 1 } );
				i = end + 1;
				continue;
			}
			if ( char.IsDigit( c ) || ( ( c == '-' || c == '+' || c == '.' ) && i + 1 < text.Length && char.IsDigit( text[i + 1] ) ) )
			{
				int start = i;
				while ( i < text.Length && ( char.IsDigit( text[i] ) || text[i] == '.' || text[i] == 'e' || text[i] == 'E' || text[i] == '-' || text[i] == '+' ) ) i++;
				tokens.Add( new Token { Type = Token.Kind.Number, Text = text.Substring( start, i - start ), Line = line, Start = start, End = i } );
				continue;
			}
			if ( char.IsLetter( c ) || c == '_' )
			{
				int start = i;
				while ( i < text.Length && ( char.IsLetterOrDigit( text[i] ) || text[i] == '_' || text[i] == ':' || text[i] == '.' ) ) i++;
				tokens.Add( new Token { Type = Token.Kind.Ident, Text = text.Substring( start, i - start ), Line = line, Start = start, End = i } );
				continue;
			}
			if ( "{}()[],=;".IndexOf( c ) >= 0 )
			{
				tokens.Add( new Token { Type = Token.Kind.Symbol, Text = c.ToString(), Line = line, Start = i, End = i + 1 } );
				i++;
				continue;
			}
			throw new UsdParseException( "unexpected character '" + c + "'", line );
		}
		return tokens;
	}

	static int CountNewlines( string s )
	{
		int n = 0;
		foreach ( char c in s ) if ( c == '\n' ) n++;
		return n;
	}

	// ----- parser -----

	sealed class Parser
	{
		readonly List<Token> _tokens;
		readonly string _text;
		int _pos;

		public Parser( List<Token> tokens, string text )
		{
			_tokens = tokens;
			_text = text;
		}

		Token Peek() => _pos < _tokens.Count ? _tokens[_pos] : null;
		Token Next() => _pos < _tokens.Count ? _tokens[_pos++] : null;
		int Line() => _pos < _tokens.Count ? _tokens[_pos].Line : ( _tokens.Count > 0 ? _tokens[_tokens.Count - 1].Line : 1 );

		bool IsSymbol( string s )
		{
			var t = Peek();
			return t != null && t.Type == Token.Kind.Symbol && t.Text == s;
		}

		bool EatSymbol( string s )
		{
			if ( !IsSymbol( s ) ) return false;
			_pos++;
			return true;
		}

		void ExpectSymbol( string s )
		{
			if ( !EatSymbol( s ) ) throw new UsdParseException( "expected '" + s + "'", Line() );
		}

		bool IsIdent( string s )
		{
			var t = Peek();
			return t != null && t.Type == Token.Kind.Ident && t.Text == s;
		}

		string ExpectIdent( string what = "identifier" )
		{
			var t = Next();
			if ( t == null || t.Type != Token.Kind.Ident ) throw new UsdParseException( "expected " + what, Line() );
			return t.Text;
		}

		string ExpectString( string what = "string" )
		{
			var t = Next();
			if ( t == null || t.Type != Token.Kind.Str ) throw new UsdParseException( "expected " + what, Line() );
			return t.Text;
		}

		public UsdLayer ParseLayer( string name )
		{
			var layer = new UsdLayer( name );
			while ( Peek() != null )
			{
				if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) )
				{
					layer.Roots.Add( ParsePrim( "/" ) );
					continue;
				}
				string key = ExpectIdent( "layer statement" );
				if ( IsSymbol( "=" ) )
				{
					_pos++;
					switch ( key )
					{
						case "defaultPrim": layer.DefaultPrim = ExpectString( "default prim name" ); break;
						case "upAxis": layer.UpAxis = ExpectString( "up axis" ); break;
						case "metersPerUnit": layer.MetersPerUnit = ParseDouble(); break;
						case "timeCodesPerSecond": layer.TimeCodesPerSecond = ParseDouble(); break;
						case "startTimeCode": layer.StartTimeCode = ParseDouble(); break;
						case "endTimeCode": layer.EndTimeCode = ParseDouble(); break;
						case "subLayers":
							foreach ( string asset in ParseAssetArray() ) layer.Sublayers.Add( asset );
							break;
						case "customLayerData":
							layer.CustomLayerDataRaw = SkipBalancedBracesRaw();
							break;
						default:
							SkipValue();
							break;
					}
					EatSymbol( ";" );
				}
				else throw new UsdParseException( "expected '=' after layer statement '" + key + "'", Line() );
			}
			return layer;
		}

		List<string> ParseAssetArray()
		{
			var assets = new List<string>();
			ExpectSymbol( "[" );
			while ( !IsSymbol( "]" ) )
			{
				var t = Next();
				if ( t == null ) throw new UsdParseException( "unterminated subLayers list", Line() );
				if ( t.Type == Token.Kind.Asset ) assets.Add( t.Text );
				EatSymbol( "," );
			}
			ExpectSymbol( "]" );
			return assets;
		}

		double ParseDouble()
		{
			var t = Next();
			if ( t == null || t.Type != Token.Kind.Number ) throw new UsdParseException( "expected a number", Line() );
			if ( !double.TryParse( t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value ) )
				throw new UsdParseException( "bad number '" + t.Text + "'", t.Line );
			return value;
		}

		/// <summary>Skip a value of unknown shape (used for layer statements this slice does not model).</summary>
		void SkipValue()
		{
			var t = Peek();
			if ( t == null ) return;
			if ( IsSymbol( "[" ) ) { SkipBalanced( "[", "]" ); return; }
			if ( IsSymbol( "{" ) ) { SkipBalanced( "{", "}" ); return; }
			if ( IsSymbol( "(" ) ) { SkipBalanced( "(", ")" ); return; }
			_pos++;
		}

		void SkipBalanced( string open, string close )
		{
			ExpectSymbol( open );
			int depth = 1;
			while ( depth > 0 )
			{
				var t = Next();
				if ( t == null ) throw new UsdParseException( "unterminated '" + open + "' block", Line() );
				if ( t.Type == Token.Kind.Symbol && t.Text == open ) depth++;
				if ( t.Type == Token.Kind.Symbol && t.Text == close ) depth--;
			}
		}

		string SkipBalancedBracesRaw()
		{
			var open = Peek();
			if ( open == null || !IsSymbol( "{" ) ) throw new UsdParseException( "expected '{' for customLayerData", Line() );
			int start = open.Start;
			SkipBalanced( "{", "}" );
			var close = _tokens[_pos - 1];
			return _text.Substring( start, close.End - start );
		}

		UsdPrim ParsePrim( string parentPath )
		{
			string keyword = ExpectIdent( "def/over/class" );
			string typeName = ExpectIdent( "prim type" );
			string name = ExpectString( "prim name" );
			string path = name.StartsWith( "/", StringComparison.Ordinal )
				? name
				: ( parentPath == "/" ? "/" + name : parentPath + "/" + name );
			var prim = new UsdPrim( path, KindFromName( typeName ) );
			prim.TypeName = typeName;
			prim.IsOver = keyword == "over";
			if ( IsSymbol( "(" ) ) ParseMetadata( prim );
			ExpectSymbol( "{" );
			ParsePrimBody( prim );
			ExpectSymbol( "}" );
			return prim;
		}

		static UsdPrimKind KindFromName( string typeName )
		{
			switch ( typeName )
			{
				case "Scope": return UsdPrimKind.Scope;
				case "Xform": return UsdPrimKind.Xform;
				case "Mesh": return UsdPrimKind.Mesh;
				case "Material": return UsdPrimKind.Material;
				case "Shader": return UsdPrimKind.Shader;
				case "Camera": return UsdPrimKind.Camera;
				case "Light": return UsdPrimKind.Light;
				default: return UsdPrimKind.Unknown;
			}
		}

		void ParseMetadata( UsdPrim prim )
		{
			ExpectSymbol( "(" );
			while ( !IsSymbol( ")" ) )
			{
				string key = ExpectIdent( "metadata key" );
				if ( !EatSymbol( "=" ) ) throw new UsdParseException( "expected '=' in prim metadata for '" + key + "'", Line() );
				int valueLine = Line();
				switch ( key )
				{
					case "references":
						foreach ( string r in ParseReferenceList() ) prim.References.Add( r );
						break;
					case "payload":
						prim.Payload = ParseReferenceTarget();
						break;
					case "inherits":
						foreach ( string r in ParsePathList() ) prim.Inherits.Add( r );
						break;
					case "specializes":
						foreach ( string r in ParsePathList() ) prim.Specializes.Add( r );
						break;
					case "variantSets":
						foreach ( string s in ParseStringArray() )
							if ( !prim.VariantSets.ContainsKey( s ) ) prim.VariantSets[s] = new List<string>();
						break;
					case "variants":
						ParseVariantSelections( prim );
						break;
					case "kind":
						// kind = "component" etc: kept raw, not a prim kind.
						prim.MetadataRaw.Add( "kind = \"" + ExpectString( "kind value" ) + "\"" );
						break;
					case "active":
						prim.Active = ParseBool();
						break;
					case "instanceable":
						prim.Instanceable = ParseBool();
						break;
					case "customLayerData":
						prim.MetadataRaw.Add( "customLayerData = " + SkipBalancedBracesRaw() );
						break;
					default:
					{
						// Unknown metadata: keep the raw text so a re-export does not silently drop it.
						string raw = ReadRawValue();
						prim.MetadataRaw.Add( key + " = " + raw );
						break;
					}
				}
				EatSymbol( "," );
				if ( Peek() == null ) throw new UsdParseException( "unterminated prim metadata", valueLine );
			}
			ExpectSymbol( ")" );
		}

		bool ParseBool()
		{
			var t = Next();
			if ( t != null && t.Type == Token.Kind.Ident && ( t.Text == "true" || t.Text == "false" ) ) return t.Text == "true";
			throw new UsdParseException( "expected true/false", Line() );
		}

		List<string> ParseReferenceList()
		{
			var refs = new List<string>();
			if ( IsSymbol( "[" ) )
			{
				ExpectSymbol( "[" );
				while ( !IsSymbol( "]" ) )
				{
					refs.Add( ParseReferenceTarget() );
					EatSymbol( "," );
				}
				ExpectSymbol( "]" );
			}
			else refs.Add( ParseReferenceTarget() );
			return refs;
		}

		string ParseReferenceTarget()
		{
			var t = Next();
			if ( t == null ) throw new UsdParseException( "expected a reference target", Line() );
			if ( t.Type == Token.Kind.Asset )
			{
				var prim = Next();
				if ( prim == null || prim.Type != Token.Kind.PrimRef ) throw new UsdParseException( "expected a prim path after an asset reference", Line() );
				return "@" + t.Text + "@<" + prim.Text + ">";
			}
			if ( t.Type == Token.Kind.PrimRef ) return "<" + t.Text + ">";
			throw new UsdParseException( "expected a reference target (@file@</Prim> or </Prim>)", t.Line );
		}

		List<string> ParsePathList()
		{
			var paths = new List<string>();
			if ( IsSymbol( "[" ) )
			{
				ExpectSymbol( "[" );
				while ( !IsSymbol( "]" ) )
				{
					var t = Next();
					if ( t == null || t.Type != Token.Kind.PrimRef ) throw new UsdParseException( "expected a prim path", Line() );
					paths.Add( t.Text );
					EatSymbol( "," );
				}
				ExpectSymbol( "]" );
			}
			else
			{
				var t = Next();
				if ( t == null || t.Type != Token.Kind.PrimRef ) throw new UsdParseException( "expected a prim path", Line() );
				paths.Add( t.Text );
			}
			return paths;
		}

		List<string> ParseStringArray()
		{
			var values = new List<string>();
			ExpectSymbol( "[" );
			while ( !IsSymbol( "]" ) )
			{
				values.Add( ExpectString( "string in list" ) );
				EatSymbol( "," );
			}
			ExpectSymbol( "]" );
			return values;
		}

		void ParseVariantSelections( UsdPrim prim )
		{
			ExpectSymbol( "{" );
			while ( !IsSymbol( "}" ) )
			{
				if ( !IsIdent( "string" ) ) throw new UsdParseException( "expected 'string <set> = \"<variant>\"' in variants", Line() );
				_pos++;
				string set = ExpectIdent( "variant set name" );
				ExpectSymbol( "=" );
				string variant = ExpectString( "variant name" );
				prim.SelectedVariants[set] = variant;
				if ( !prim.VariantSets.ContainsKey( set ) ) prim.VariantSets[set] = new List<string>();
				if ( !prim.VariantSets[set].Contains( variant ) ) prim.VariantSets[set].Add( variant );
				EatSymbol( "," );
			}
			ExpectSymbol( "}" );
		}

		/// <summary>Read one metadata value as raw source text (for unknown keys we preserve, not drop).</summary>
		string ReadRawValue()
		{
			var t = Peek();
			if ( t == null ) throw new UsdParseException( "expected a metadata value", Line() );
			int start = t.Start;
			SkipValue();
			var last = _tokens[_pos - 1];
			return _text.Substring( start, last.End - start );
		}

		void ParsePrimBody( UsdPrim prim )
		{
			while ( !IsSymbol( "}" ) )
			{
				if ( Peek() == null ) throw new UsdParseException( "unterminated prim block for " + prim.Path, Line() );
				if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) )
				{
					prim.Children.Add( ParsePrim( prim.Path ) );
					continue;
				}
				if ( IsIdent( "variantSet" ) )
				{
					ParseVariantSet( prim );
					continue;
				}
				if ( IsIdent( "rel" ) )
				{
					_pos++;
					string relName = ExpectIdent( "relationship name" );
					ExpectSymbol( "=" );
					var targets = ParsePathList();
					foreach ( string target in targets ) prim.Relationships.Add( new KeyValuePair<string, string>( relName, "<" + target + ">" ) );
					EatSymbol( ";" );
					continue;
				}
				if ( IsIdent( "references" ) || IsIdent( "payload" ) || IsIdent( "inherits" ) || IsIdent( "specializes" ) )
				{
					// Tolerate references/payload written as body statements (some exporters do this).
					string key = ExpectIdent( "prim statement" );
					ExpectSymbol( "=" );
					switch ( key )
					{
						case "references": foreach ( string r in ParseReferenceList() ) prim.References.Add( r ); break;
						case "payload": prim.Payload = ParseReferenceTarget(); break;
						case "inherits": foreach ( string r in ParsePathList() ) prim.Inherits.Add( r ); break;
						case "specializes": foreach ( string r in ParsePathList() ) prim.Specializes.Add( r ); break;
					}
					EatSymbol( ";" );
					continue;
				}
				ParseAttribute( prim );
			}
		}

		void ParseVariantSet( UsdPrim prim )
		{
			_pos++; // variantSet
			string set = ExpectString( "variant set name" );
			if ( !prim.VariantSets.ContainsKey( set ) ) prim.VariantSets[set] = new List<string>();
			ExpectSymbol( "{" );
			while ( !IsSymbol( "}" ) )
			{
				string variant = ExpectString( "variant name" );
				if ( !prim.VariantSets[set].Contains( variant ) ) prim.VariantSets[set].Add( variant );
				ExpectSymbol( "{" );
				var children = new List<UsdPrim>();
				while ( !IsSymbol( "}" ) )
				{
					if ( IsIdent( "def" ) || IsIdent( "over" ) || IsIdent( "class" ) ) children.Add( ParsePrim( prim.Path ) );
					else throw new UsdParseException( "expected a prim definition inside variant '" + variant + "'", Line() );
				}
				ExpectSymbol( "}" );
				prim.VariantChildren[set + "\n" + variant] = children;
			}
			ExpectSymbol( "}" );
		}

		void ParseAttribute( UsdPrim prim )
		{
			string prefix = "";
			if ( IsIdent( "uniform" ) || IsIdent( "custom" ) ) { prefix = Next().Text; }
			string type = ExpectIdent( "attribute type" );
			if ( IsSymbol( "[" ) )
			{
				// Array type spelled with a space: "float []" — tolerate it.
				_pos++;
				ExpectSymbol( "]" );
				type += "[]";
			}
			string name = ExpectIdent( "attribute name" );
			if ( !EatSymbol( "=" ) )
			{
				// A declaration with no value (e.g. "custom float x;") — keep the name with no value.
				EatSymbol( ";" );
				return;
			}
			UsdValue value = ParseTypedValue( type );
			prim.SetAttribute( name, value, prefix );
			EatSymbol( ";" );
		}

		UsdValue ParseTypedValue( string type )
		{
			var t = Peek();
			if ( t == null ) throw new UsdParseException( "expected a value for type '" + type + "'", Line() );
			bool isArray = type.EndsWith( "[]", StringComparison.Ordinal );
			string baseType = isArray ? type.Substring( 0, type.Length - 2 ) : type;
			if ( IsSymbol( "[" ) ) return ParseArrayValue( baseType, type );
			if ( IsSymbol( "(" ) ) return ParseTupleValue( baseType, type );
			if ( t.Type == Token.Kind.Str )
			{
				_pos++;
				if ( baseType == "asset" ) return UsdValue.Asset( t.Text );
				return baseType == "token" ? UsdValue.Token( t.Text ) : UsdValue.String( t.Text );
			}
			if ( t.Type == Token.Kind.Asset ) { _pos++; return UsdValue.Asset( t.Text ); }
			if ( t.Type == Token.Kind.PrimRef ) { _pos++; return UsdValue.String( "<" + t.Text + ">" ); }
			if ( t.Type == Token.Kind.Ident )
			{
				if ( t.Text == "true" || t.Text == "false" ) { _pos++; return UsdValue.Bool( t.Text == "true" ); }
				if ( t.Text == "None" ) { _pos++; return new UsdValue( type, null ); }
				throw new UsdParseException( "unexpected identifier '" + t.Text + "' as a value", t.Line );
			}
			if ( t.Type == Token.Kind.Number )
			{
				_pos++;
				if ( !double.TryParse( t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number ) )
					throw new UsdParseException( "bad number '" + t.Text + "'", t.Line );
				switch ( baseType )
				{
					case "int": return UsdValue.Int( (int)number );
					case "double": return UsdValue.Double( number );
					default: return UsdValue.Float( (float)number );
				}
			}
			throw new UsdParseException( "unexpected token '" + t.Text + "' as a value", t.Line );
		}

		UsdValue ParseTupleValue( string baseType, string fullType )
		{
			ExpectSymbol( "(" );
			var numbers = new List<double>();
			var strings = new List<string>();
			bool sawString = false;
			while ( !IsSymbol( ")" ) )
			{
				var t = Next();
				if ( t == null ) throw new UsdParseException( "unterminated tuple", Line() );
				if ( t.Type == Token.Kind.Number )
				{
					if ( !double.TryParse( t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number ) )
						throw new UsdParseException( "bad number '" + t.Text + "'", t.Line );
					numbers.Add( number );
				}
				else if ( t.Type == Token.Kind.Str ) { strings.Add( t.Text ); sawString = true; }
				EatSymbol( "," );
			}
			ExpectSymbol( ")" );
			if ( sawString ) throw new UsdParseException( "string tuple is not a supported attribute value", Line() );
			if ( numbers.Count == 2 ) return UsdValue.Vec2( fullType, new Vec2( (float)numbers[0], (float)numbers[1] ) );
			if ( numbers.Count == 3 ) return UsdValue.Vec3( fullType, new Vec3( (float)numbers[0], (float)numbers[1], (float)numbers[2] ) );
			if ( numbers.Count == 4 ) throw new UsdParseException( "4-component tuples (e.g. quaternions) are not a supported attribute value; split them or file a follow-up", Line() );
			throw new UsdParseException( "tuple with " + numbers.Count + " numbers is not a supported attribute value", Line() );
		}

		UsdValue ParseArrayValue( string baseType, string fullType )
		{
			ExpectSymbol( "[" );
			var ints = new List<int>();
			var floats = new List<float>();
			var vec2s = new List<Vec2>();
			var vec3s = new List<Vec3>();
			var strings = new List<string>();
			while ( !IsSymbol( "]" ) )
			{
				var t = Peek();
				if ( t == null ) throw new UsdParseException( "unterminated array", Line() );
				if ( IsSymbol( "(" ) )
				{
					var tuple = ParseTupleValue( baseType, fullType );
					if ( tuple.Value is Vec2 v2 ) vec2s.Add( v2 );
					else if ( tuple.Value is Vec3 v3 ) vec3s.Add( v3 );
				}
				else if ( t.Type == Token.Kind.Str ) { strings.Add( t.Text ); _pos++; }
				else if ( t.Type == Token.Kind.Asset ) { strings.Add( "@" + t.Text + "@" ); _pos++; }
				else if ( t.Type == Token.Kind.PrimRef ) { strings.Add( "<" + t.Text + ">" ); _pos++; }
				else if ( t.Type == Token.Kind.Number )
				{
					_pos++;
					if ( !double.TryParse( t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number ) )
						throw new UsdParseException( "bad number '" + t.Text + "'", t.Line );
					if ( baseType == "int" ) ints.Add( (int)number );
					else floats.Add( (float)number );
				}
				else if ( t.Type == Token.Kind.Ident && ( t.Text == "true" || t.Text == "false" ) )
				{
					throw new UsdParseException( "bool arrays are not a supported attribute value", t.Line );
				}
				else throw new UsdParseException( "unexpected token '" + t.Text + "' in array", t.Line );
				EatSymbol( "," );
			}
			ExpectSymbol( "]" );
			if ( strings.Count > 0 ) return UsdValue.StringArray( strings.ToArray() );
			if ( vec3s.Count > 0 ) return UsdValue.Vec3Array( fullType, vec3s.ToArray() );
			if ( vec2s.Count > 0 ) return UsdValue.Vec2Array( fullType, vec2s.ToArray() );
			if ( ints.Count > 0 ) return UsdValue.IntArray( ints.ToArray() );
			return UsdValue.FloatArray( floats.ToArray() );
		}
	}
}
