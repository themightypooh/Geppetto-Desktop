using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Effigy
{
	/// <summary>
	/// Reads the two things a ModelDoc says that a viewer needs: the mesh file it draws and
	/// the material group that binds each mesh material to a .vmat.
	///
	/// WHY THE KERNEL OWNS THIS. The kernel already reads s&amp;box's .riganim and writes
	/// ModelDocs, so the file formats live here and the application never has to know one.
	/// This reader is plain text: no engine types, no desktop types, no game engine.
	///
	/// WHY A TARGETED READER AND NOT A FULL kv3 PARSER. kv3 is a document format with typed
	/// values, sub-messages and arrays. A viewer needs two lists out of it and both are text,
	/// so this reads them as text and says so. Anything it does not understand is ignored
	/// rather than guessed at: a ModelDoc that cannot be read leaves the model's materials
	/// alone, and the caller says so.
	///
	/// READING IS READ-ONLY. Nothing here writes to a project.
	/// </summary>
	public static class VmdlReader
	{
		/// <summary>One material binding: the name the mesh file carries, and the .vmat it means.</summary>
		public sealed class MaterialRemap
		{
			public string From = "";
			public string To = "";
		}

		/// <summary>What a ModelDoc says about itself.</summary>
		public sealed class Model
		{
			/// <summary>Where the ModelDoc was read from.</summary>
			public string Path = "";
			/// <summary>The mesh file the model draws, project-relative. Empty when the ModelDoc names none.</summary>
			public string RenderMeshFile = "";
			/// <summary>The model's import scale. 1 unless the ModelDoc says otherwise.</summary>
			public float ImportScale = 1f;
			/// <summary>The material bindings, in the order the ModelDoc lists them.</summary>
			public IReadOnlyList<MaterialRemap> Remaps = Array.Empty<MaterialRemap>();
		}

		static readonly Regex RenderMeshFilePattern = new Regex( @"filename\s*=\s*""([^""]*)""", RegexOptions.Compiled );
		static readonly Regex ImportScalePattern = new Regex( @"import_scale\s*=\s*(-?[0-9.eE+]+)", RegexOptions.Compiled );
		static readonly Regex RemapPattern = new Regex( @"from\s*=\s*""([^""]*)""\s*(?:,|\s)to\s*=\s*""([^""]*)""", RegexOptions.Compiled );

		/// <summary>Read a ModelDoc. Returns null when the file cannot be read at all; a node
		/// that cannot be read inside a readable file leaves that node empty.</summary>
		public static Model Read( string path )
		{
			if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) ) return null;
			string text;
			try { text = File.ReadAllText( path ); }
			catch ( IOException ) { return null; }
			catch ( UnauthorizedAccessException ) { return null; }

			var model = new Model { Path = path };
			int meshAt = text.IndexOf( "RenderMeshFile", StringComparison.Ordinal );
			if ( meshAt >= 0 )
			{
				string block = Block( text, meshAt );
				var file = RenderMeshFilePattern.Match( block );
				if ( file.Success ) model.RenderMeshFile = file.Groups[1].Value;
				var scale = ImportScalePattern.Match( block );
				if ( scale.Success )
				{
					float parsed;
					if ( float.TryParse( scale.Groups[1].Value, System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture, out parsed )
						&& !float.IsNaN( parsed ) && !float.IsInfinity( parsed ) && parsed > 0 )
						model.ImportScale = parsed;
				}
			}

			var remaps = new List<MaterialRemap>();
			int remapsAt = text.IndexOf( "remaps", StringComparison.Ordinal );
			if ( remapsAt >= 0 )
			{
				int end = text.IndexOf( "]", remapsAt, StringComparison.Ordinal );
				if ( end < 0 ) end = text.Length;
				foreach ( Match match in RemapPattern.Matches( text.Substring( remapsAt, end - remapsAt ) ) )
				{
					string from = match.Groups[1].Value.Trim();
					string to = match.Groups[2].Value.Trim();
					if ( from.Length == 0 || to.Length == 0 ) continue;
					remaps.Add( new MaterialRemap { From = from, To = to } );
				}
			}
			model.Remaps = remaps;
			return model;
		}

		/// <summary>The node's own text: up to the closing brace of that child.</summary>
		static string Block( string text, int from )
		{
			int end = text.IndexOf( "\n\t\t\t}", from, StringComparison.Ordinal );
			if ( end < 0 ) end = text.Length;
			return text.Substring( from, end - from );
		}

		/// <summary>Every spelling of a material name a mesh file might carry. The mesh
		/// file's own name comes first; ModelDoc's importer is documented to keep only the
		/// last segment of a path, and to drop everything after a period in a name, so the
		/// bare stem is registered too - for a plain name as much as for a path.</summary>
		public static IEnumerable<string> NameSpellings( string name )
		{
			if ( string.IsNullOrWhiteSpace( name ) ) yield break;
			yield return name;
			bool hasExtension = name.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase );
			if ( !hasExtension ) yield return name + ".vmat";
			string stem = hasExtension && name.Length > 5 ? name.Substring( 0, name.Length - 5 ) : name;
			int slash = name.LastIndexOf( '/' );
			string tail = slash >= 0 && slash + 1 < name.Length ? name.Substring( slash + 1 ) : stem;
			yield return tail;
			if ( !tail.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ) yield return tail + ".vmat";
			if ( tail.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) && tail.Length > 5 )
				yield return tail.Substring( 0, tail.Length - 5 );
		}

		/// <summary>
		/// Find the ModelDoc that draws a mesh file, by looking for a .vmdl whose render mesh
		/// names it. The search is bounded and read-only.
		///
		/// The match is by the path the ModelDoc names, which is project-relative. A file name
		/// alone is only a fallback, because two models in different folders can share a mesh
		/// file name; the one in the same folder as the mesh wins.
		/// </summary>
		public static Model FindForRenderMesh( string projectRoot, string meshFile, int maxFiles = 4000 )
		{
			if ( string.IsNullOrWhiteSpace( projectRoot ) || !Directory.Exists( projectRoot ) ) return null;
			if ( string.IsNullOrWhiteSpace( meshFile ) ) return null;
			string wanted = meshFile.Replace( '\\', '/' ).TrimStart( '/' );
			string wantedName = Path.GetFileName( wanted );
			if ( wantedName.Length == 0 ) return null;
			string wantedFolder = Path.GetDirectoryName( wanted ) == null ? "" : Path.GetDirectoryName( wanted ).Replace( '\\', '/' );
			Model byName = null;
			int seen = 0;
			foreach ( string file in EnumerateModelDocs( projectRoot ) )
			{
				if ( ++seen > maxFiles ) break;
				Model model;
				try { model = Read( file ); }
				catch ( IOException ) { continue; }
				if ( model == null || model.RenderMeshFile.Length == 0 ) continue;
				string candidate = model.RenderMeshFile.Replace( '\\', '/' ).TrimStart( '/' );
				if ( string.Equals( candidate, wanted, StringComparison.OrdinalIgnoreCase ) )
					return model;
				if ( byName == null && string.Equals( Path.GetFileName( candidate ), wantedName, StringComparison.OrdinalIgnoreCase ) )
				{
					string candidateFolder = Path.GetDirectoryName( candidate ) == null ? "" : Path.GetDirectoryName( candidate ).Replace( '\\', '/' );
					byName = string.Equals( candidateFolder, wantedFolder, StringComparison.OrdinalIgnoreCase ) ? model : byName ?? model;
				}
			}
			return byName;
		}

		static IEnumerable<string> EnumerateModelDocs( string root )
		{
			var stack = new Stack<string>();
			stack.Push( root );
			while ( stack.Count > 0 )
			{
				string dir = stack.Pop();
				string[] files;
				string[] dirs;
				try
				{
					files = Directory.GetFiles( dir );
					dirs = Directory.GetDirectories( dir );
				}
				catch ( IOException ) { continue; }
				catch ( UnauthorizedAccessException ) { continue; }
				foreach ( string file in files )
					if ( file.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
						yield return file;
				foreach ( string sub in dirs )
				{
					string name = Path.GetFileName( sub );
					if ( name.StartsWith( ".", StringComparison.Ordinal ) ) continue;
					if ( string.Equals( name, "backups", StringComparison.OrdinalIgnoreCase ) ) continue;
					stack.Push( sub );
				}
			}
		}
	}
}
