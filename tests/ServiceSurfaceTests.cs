using System.Text;
using System.Text.RegularExpressions;

// R11 anti-drift check. Enumerates the service/renderer surface (every top-level *Service.cs in the
// project root, plus the BOARD.md section 2a/2b files) and fails if any listed file has neither a
// non-test caller nor a reference from tests/. Decisions and reasons: handoffs/R11-DECISIONS.md.
//
// "Caller" = another production source file (the desktop project's compile set: everything except
// tests/, kernel/, tools/, bin/, obj/) that names one of the file's top-level types in code. Comments,
// string literals and nested type names (Options, Result, Buffers...) do not count. "Test" = a
// tests/*.cs file that names one of those types. A listed file that no longer exists is fine (gone).
internal static class ServiceSurfaceTests
{
	const string Group = "service-surface";
	const string DecisionFile = "handoffs/R11-DECISIONS.md";

	// BOARD.md 2a (no caller, no test as of 2026-10-20), the 2b names that do not end in Service.cs,
	// and SsrPass.cs (R5 stub, orphaned when ScreenSpaceTests got real probes).
	internal static readonly string[] ExplicitSurface =
	{
		"ParticleSystem.cs", "GeometryNodes.Core.cs", "BaseMotionLibrary.cs", "TexturePaintLayers.cs",
		"SkinningLodService.cs", "GpuAnimationBlendService.cs", "GpuConstraintSolverService.cs",
		"GpuExportBakeService.cs", "GpuFluidSimService.cs", "GpuIkSolverService.cs",
		"GpuLodGeneratorService.cs", "GpuMorphService.cs", "GpuPhysicsService.cs",
		"GpuRayTracingService.cs", "GpuSkinningService.cs", "GpuTerrainService.cs",
		"GpuTextureCompressionService.cs", "GpuVoxelizerService.cs", "PathTracerService.cs",
		"DependencyWatcher.cs", "AutosaveScheduler.cs", "PartTrackEvaluator.cs",
		"ClipCapabilityValidator.cs", "TimelineEventDispatcher.cs", "ConstraintParityInventory.cs",
		// Renderer files found orphaned after the board lists were written.
		"SsrPass.cs",
	};

	// ONLY files awaiting the user's wire-or-delete decision. Each entry must still be uncalled and
	// untested, must still exist, and must be named in handoffs/R11-DECISIONS.md; otherwise the
	// allowlist test fails so the entry gets removed. Do not add a file here to silence a new failure:
	// wire it, test it, or record it in R11-DECISIONS.md as awaiting the user's decision.
	internal static readonly IReadOnlyDictionary<string, string> AwaitingDecision = new Dictionary<string, string>
	{
	};

	internal sealed record SurfaceFinding( string File, bool Exists, IReadOnlyList<string> Types, IReadOnlyList<string> Callers, IReadOnlyList<string> TestRefs )
	{
		public bool Orphan => Exists && Callers.Count == 0 && TestRefs.Count == 0;
	}

	public static void Register( TestRegistry suite, TestSettings settings )
	{
		suite.Add( Group, "every listed service/renderer file has a non-test caller or a test", () =>
		{
			string root = SourceRoot()!;
			var findings = Scan( root, SurfaceFiles( root, ExplicitSurface ) );
			Require( findings.Count >= ExplicitSurface.Length, $"surface enumeration found only {findings.Count} files under {root}" );
			var orphans = findings.Where( f => f.Orphan && !AwaitingDecision.ContainsKey( f.File ) ).Select( f => f.File ).ToList();
			Require( orphans.Count == 0, "no non-test caller and no test (wire it, test it, or record it in " + DecisionFile + "): " + string.Join( ", ", orphans ) );
		}, () => SourceRoot() is null ? "desktop source root (Marionette.Desktop.csproj + tests/Desktop.Tests.csproj) not found above " + AppContext.BaseDirectory : null, required: true );

		suite.Add( Group, "awaiting-decision allowlist is current and documented", () =>
		{
			string root = SourceRoot()!;
			string decisionsPath = Path.Combine( root, DecisionFile );
			Require( File.Exists( decisionsPath ), "missing " + decisionsPath );
			string decisions = File.ReadAllText( decisionsPath );
			var byFile = Scan( root, SurfaceFiles( root, ExplicitSurface ) ).ToDictionary( f => f.File );
			var problems = new List<string>();
			foreach ( var (file, reason) in AwaitingDecision )
			{
				if ( string.IsNullOrWhiteSpace( reason ) || !reason.Contains( "R11-DECISIONS.md", StringComparison.Ordinal ) ) problems.Add( file + ": reason must point to R11-DECISIONS.md" );
				if ( !decisions.Contains( "`" + file + "`", StringComparison.Ordinal ) ) problems.Add( file + ": not recorded in " + DecisionFile );
				if ( !byFile.TryGetValue( file, out var f ) ) problems.Add( file + ": not part of the enumerated surface" );
				else if ( !f.Exists ) problems.Add( file + ": deleted - remove the allowlist entry" );
				else if ( !f.Orphan ) problems.Add( file + ": now has " + (f.Callers.Count > 0 ? "a caller (" + f.Callers[0] + ")" : "a test (" + f.TestRefs[0] + ")") + " - remove the allowlist entry" );
			}
			Require( problems.Count == 0, string.Join( "; ", problems ) );
		}, () => SourceRoot() is null ? "desktop source root not found above " + AppContext.BaseDirectory : null, required: true );

		suite.Add( Group, "self-test: a synthetic uncalled, untested type is flagged", () =>
		{
			string root = Path.Combine( settings.WorkDirectory, "service-surface", Guid.NewGuid().ToString( "N" ) );
			Write( root, "Program.cs", """"
				namespace Fixture;
				// OrphanService is mentioned in a comment only.
				/* OrphanService in a block comment */
				static class Program
				{
					static void Main()
					{
						string s = "OrphanService in a string";
						string v = @"OrphanService ""verbatim""";
						char q = '"';
						int Options = 1; // nested-type name used as an identifier must not count
						CalledService.Run();
					}
				}
				"""" );
			Write( root, "CalledService.cs", "namespace Fixture { public static class CalledService { public static void Run() { } } }" );
			Write( root, "TestedService.cs", "namespace Fixture; public sealed class TestedService { }" );
			Write( root, "OrphanService.cs", "namespace Fixture; public static class OrphanService { public sealed class Options { } }" );
			Write( root, "OrphanHelper.cs", "namespace Fixture; public enum OrphanHelperKind { A, B }" );
			Write( root, "kernel/UsesOrphan.cs", "class K { void M() { OrphanService.ToString(); OrphanHelperKind.A.ToString(); } }" );
			Write( root, "tools/UsesOrphan.cs", "class T { object o = typeof(OrphanService); }" );
			Write( root, "obj/Generated.cs", "class G { object o = typeof(OrphanService); }" );
			Write( root, "tests/TestedTests.cs", "class X { object o = new Fixture.TestedService(); }" );
			Write( root, "tests/bin/Stale.cs", "class S { object o = typeof(OrphanHelperKind); }" );

			var findings = Scan( root, SurfaceFiles( root, new[] { "OrphanHelper.cs", "Missing.cs" } ) ).ToDictionary( f => f.File );
			Require( findings.Keys.OrderBy( k => k, StringComparer.Ordinal ).SequenceEqual( new[] { "CalledService.cs", "Missing.cs", "OrphanHelper.cs", "OrphanService.cs", "TestedService.cs" } ),
				"fixture surface was " + string.Join( ",", findings.Keys ) );
			Require( findings["OrphanService.cs"].Orphan, "uncalled, untested OrphanService was not flagged (comment/string/kernel/tools/obj/nested-name references leaked)" );
			Require( findings["OrphanHelper.cs"].Orphan, "explicitly listed uncalled OrphanHelper.cs was not flagged (kernel or tests/bin reference leaked)" );
			Require( !findings["CalledService.cs"].Orphan && findings["CalledService.cs"].Callers.Single().StartsWith( "Program.cs:", StringComparison.Ordinal ), "production caller not detected" );
			Require( !findings["TestedService.cs"].Orphan && findings["TestedService.cs"].TestRefs.SequenceEqual( new[] { "TestedTests.cs" } ), "test reference not detected" );
			Require( !findings["Missing.cs"].Exists && !findings["Missing.cs"].Orphan, "a deleted listed file must count as gone, not as an orphan" );
		} );

		suite.Add( Group, "self-test: the real tree is not flagged and the check is not vacuous", () =>
		{
			string root = SourceRoot()!;
			var findings = Scan( root, SurfaceFiles( root, ExplicitSurface ) );
			// Known-wired anchors: if these stop resolving, the scanner broke, not the tree.
			var byFile = findings.ToDictionary( f => f.File );
			Require( byFile.TryGetValue( "ParticleSystem.cs", out var p ) && p.Callers.Any( c => c.StartsWith( "LiveGeometry.cs:", StringComparison.Ordinal ) ), "ParticleSystem.cs should be called from LiveGeometry.cs" );
			Require( byFile.TryGetValue( "ViewportHandleService.cs", out var v ) && v.Callers.Any( c => c.StartsWith( "Program.cs:", StringComparison.Ordinal ) ), "ViewportHandleService.cs should be called from Program.cs" );
			Require( byFile.TryGetValue( "GpuFluidSimService.cs", out var g ) && (!g.Exists || g.Orphan), "GpuFluidSimService.cs is known uncalled+untested; a non-orphan result means references are leaking" );
			Require( findings.Where( f => f.Exists ).All( f => f.Types.Count > 0 ), "files with no uniquely-owned top-level type: " + string.Join( ", ", findings.Where( f => f.Exists && f.Types.Count == 0 ).Select( f => f.File ) ) );
		}, () => SourceRoot() is null ? "desktop source root not found above " + AppContext.BaseDirectory : null, required: true );
	}

	// ---- scanner -----------------------------------------------------------------------------

	static readonly string[] ExcludedDirs = { "tests", "kernel", "tools", "bin", "obj", "node_modules" };

	/// <summary>Walks up from the test binary (then from the working directory, for builds placed
	/// elsewhere with --artifacts-path) to the folder holding both project files.</summary>
	internal static string? SourceRoot()
	{
		foreach ( var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory } )
			for ( var dir = new DirectoryInfo( start ); dir is not null; dir = dir.Parent )
				if ( File.Exists( Path.Combine( dir.FullName, "Marionette.Desktop.csproj" ) ) && File.Exists( Path.Combine( dir.FullName, "tests", "Desktop.Tests.csproj" ) ) )
					return dir.FullName.TrimEnd( Path.DirectorySeparatorChar );
		return null;
	}

	/// <summary>Top-level *Service.cs files in <paramref name="root"/> plus the explicit list (relative names).</summary>
	internal static IReadOnlyList<string> SurfaceFiles( string root, IEnumerable<string> explicitFiles )
	{
		var set = new SortedSet<string>( StringComparer.Ordinal );
		foreach ( var path in Directory.EnumerateFiles( root, "*.cs", SearchOption.TopDirectoryOnly ) )
			if ( Path.GetFileName( path ).EndsWith( "Service.cs", StringComparison.Ordinal ) ) set.Add( Path.GetFileName( path ) );
		foreach ( var f in explicitFiles ) set.Add( f );
		return set.ToList();
	}

	internal static List<SurfaceFinding> Scan( string root, IReadOnlyList<string> surface )
	{
		var production = ProductionSources( root ).ToDictionary( p => p, p => StripNonCode( File.ReadAllText( p ) ) );
		string testsDir = Path.Combine( root, "tests" );
		var tests = Directory.Exists( testsDir )
			? Directory.EnumerateFiles( testsDir, "*.cs", SearchOption.TopDirectoryOnly )
				.Where( p => !Path.GetFileName( p ).Equals( nameof( ServiceSurfaceTests ) + ".cs", StringComparison.Ordinal ) )
				.ToDictionary( p => p, p => StripNonCode( File.ReadAllText( p ) ) )
			: new Dictionary<string, string>();

		var owners = new Dictionary<string, HashSet<string>>( StringComparer.Ordinal );
		foreach ( var (path, code) in production )
			foreach ( var type in TopLevelTypes( code ) )
				(owners.TryGetValue( type, out var s ) ? s : owners[type] = new HashSet<string>()).Add( path );

		var findings = new List<SurfaceFinding>();
		foreach ( var file in surface )
		{
			string path = Path.Combine( root, file );
			if ( !production.TryGetValue( path, out var own ) )
			{
				findings.Add( new( file, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() ) );
				continue;
			}
			var types = TopLevelTypes( own ).Distinct().Where( t => owners[t].Count == 1 ).OrderBy( t => t, StringComparer.Ordinal ).ToList();
			var callers = new List<string>();
			var testRefs = new List<string>();
			if ( types.Count > 0 )
			{
				var word = new Regex( @"\b(?:" + string.Join( "|", types.Select( Regex.Escape ) ) + @")\b", RegexOptions.CultureInvariant );
				foreach ( var (other, code) in production )
				{
					if ( other == path ) continue;
					var lines = code.Split( '\n' );
					for ( int i = 0; i < lines.Length; i++ )
						if ( word.IsMatch( lines[i] ) ) callers.Add( Path.GetRelativePath( root, other ) + ":" + (i + 1) );
				}
				foreach ( var (test, code) in tests )
					if ( word.IsMatch( code ) ) testRefs.Add( Path.GetFileName( test ) );
			}
			testRefs.Sort( StringComparer.Ordinal );
			findings.Add( new( file, true, types, callers, testRefs ) );
		}
		return findings;
	}

	/// <summary>The desktop project's compile set: every *.cs under root except the excluded folders.</summary>
	static IEnumerable<string> ProductionSources( string root )
	{
		var pending = new Stack<string>();
		pending.Push( root );
		while ( pending.Count > 0 )
		{
			string dir = pending.Pop();
			IEnumerable<string> files, dirs;
			try { files = Directory.EnumerateFiles( dir, "*.cs" ).ToList(); dirs = Directory.EnumerateDirectories( dir ).ToList(); }
			catch ( Exception e ) when ( e is UnauthorizedAccessException or IOException ) { continue; }
			foreach ( var f in files ) yield return f;
			foreach ( var d in dirs )
			{
				string name = Path.GetFileName( d );
				if ( name.StartsWith( '.' ) ) continue;
				if ( dir == root && ExcludedDirs.Contains( name, StringComparer.Ordinal ) ) continue;
				if ( name is "bin" or "obj" or "node_modules" ) continue;
				pending.Push( d );
			}
		}
	}

	static readonly Regex NonCode = new(
		"\"\"\"[\\s\\S]*?\"\"\"" +                 // raw string literal
		"|(?:\\$@|@\\$|@)\"(?:[^\"]|\"\")*\"" +     // verbatim (optionally interpolated) string
		"|\\$?\"(?:\\\\.|[^\"\\\\\\n])*\"" +        // regular (optionally interpolated) string
		"|'(?:\\\\.|[^'\\\\\\n])+'" +               // char literal
		"|//[^\\n]*" +                              // line comment
		"|/\\*[\\s\\S]*?\\*/",                      // block comment
		RegexOptions.CultureInvariant );

	/// <summary>Blanks comments and literals, keeping line breaks so caller line numbers stay right.</summary>
	internal static string StripNonCode( string source ) =>
		NonCode.Replace( source, m => new string( '\n', m.Value.Count( c => c == '\n' ) ) + " " );

	static readonly Regex TypeToken = new( @"\bnamespace\s+[\w.]+\s*\{|\b(?:class|struct|record|interface|enum)\s+([A-Z]\w*)|[{}]", RegexOptions.CultureInvariant );

	/// <summary>Types declared outside any type body (block or file-scoped namespaces both count as top level).</summary>
	internal static List<string> TopLevelTypes( string code )
	{
		var result = new List<string>();
		var braces = new Stack<bool>(); // true = namespace brace
		int depth = 0;
		foreach ( Match m in TypeToken.Matches( code ) )
		{
			string token = m.Value;
			if ( token.StartsWith( "namespace", StringComparison.Ordinal ) ) braces.Push( true );
			else if ( token == "{" ) { braces.Push( false ); depth++; }
			else if ( token == "}" ) { if ( braces.Count > 0 && !braces.Pop() ) depth--; }
			else if ( depth == 0 ) result.Add( m.Groups[1].Value );
		}
		return result;
	}

	static void Write( string root, string relative, string text )
	{
		string path = Path.Combine( root, relative );
		Directory.CreateDirectory( Path.GetDirectoryName( path )! );
		File.WriteAllText( path, text, Encoding.UTF8 );
	}

	static void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
}
