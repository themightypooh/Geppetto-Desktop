using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Marionette.Desktop;

internal sealed record TestCase( string Group, string Name, Action Body, Func<string?>? Missing, bool Required );
internal sealed record TestOutcome( string Group, string Name, string Status, string? Reason, double Milliseconds );

internal sealed class TestRegistry
{
    readonly List<TestCase> _cases = new();
    public IReadOnlyList<TestCase> Cases => _cases;
    public void Add( string group, string name, Action body, Func<string?>? missing = null, bool required = false )
    {
        ArgumentNullException.ThrowIfNull( body );
        if ( string.IsNullOrWhiteSpace( group ) || string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Tests need a group and name." );
        if ( _cases.Any( t => t.Group == group && t.Name == name ) ) throw new ArgumentException( $"Duplicate test: {group}/{name}" );
        _cases.Add( new( group, name, body, missing, required ) );
    }

    public List<TestOutcome> Run( TextWriter output, string? filter = null )
    {
        var results = new List<TestOutcome>();
        foreach ( var test in _cases.Where( t => filter is null || $"{t.Group}/{t.Name}".Contains( filter, StringComparison.OrdinalIgnoreCase ) ) )
        {
            var clock = Stopwatch.StartNew();
            string status = "PASS", reason = "";
            try
            {
                string? missing = test.Missing?.Invoke();
                if ( missing is not null ) { status = test.Required ? "FAIL" : "SKIP"; reason = missing; }
                else test.Body();
            }
            catch ( Exception ex ) { status = "FAIL"; reason = ex.ToString(); }
            results.Add( new( test.Group, test.Name, status, reason.Length == 0 ? null : reason, clock.Elapsed.TotalMilliseconds ) );
            output.WriteLine( $"{status} {test.Group}/{test.Name}{(reason.Length == 0 ? "" : ": " + reason)}" );
        }
        return results;
    }

    public static int ExitCode( IReadOnlyList<TestOutcome> results ) => results.Count == 0 || results.Any( r => r.Status == "FAIL" ) ? 1 : 0;
}

internal sealed class TestSettings
{
    public string SurveyPath = DesktopOptions.Parse( Array.Empty<string>() ).Model;
    public bool RequireLocalFixtures, List;
    public string? ReportPath, Filter;
    public string WorkDirectory = Path.Combine( "/tmp/opencode", "geppetto-desktop-tests-" + Guid.NewGuid().ToString( "N" ) );

    public static TestSettings Parse( string[] args )
    {
        var settings = new TestSettings();
        for ( int i = 0; i < args.Length; i++ )
        {
            string Value()
            {
                if ( i + 1 == args.Length || args[i + 1].StartsWith( "--", StringComparison.Ordinal ) ) throw new ArgumentException( $"Missing value for {args[i]}" );
                return args[++i];
            }
            switch ( args[i] )
            {
                case "--survey": settings.SurveyPath = Path.GetFullPath( Value() ); break;
                case "--require-local-fixtures": settings.RequireLocalFixtures = true; break;
                case "--report": settings.ReportPath = Path.GetFullPath( Value() ); break;
                case "--filter": settings.Filter = Value(); break;
                case "--list": settings.List = true; break;
                default: throw new ArgumentException( $"Unknown test option: {args[i]}" );
            }
        }
        return settings;
    }
}

internal static class DesktopTestHost
{
    // Explicit registration manifest. New test files expose Register(registry, settings)
    // and are added here; one executable entry point, no reflection-discovery surprises.
    public static int Run( string[] args )
    {
        try
        {
            var settings = TestSettings.Parse( args );
            Directory.CreateDirectory( settings.WorkDirectory );
            var registry = new TestRegistry();
            BaselineTests.Register( registry, settings );
            AiTests.Register( registry, settings );
            HarnessTests.Register( registry, settings );
            AssetPathTests.Register( registry, settings );
            DocumentSessionTests.Register( registry, settings );
            EditHistoryTests.Register( registry, settings );
            ClipEditTests.Register( registry, settings );
            DocumentSaveTests.Register( registry, settings );
            GizmoMathTests.Register( registry, settings );
            MaterialDescriptorTests.Register( registry, settings );
            PoseEditTests.Register( registry, settings );
            DesktopProjectTests.Register( registry, settings );
            ParametricEditTests.Register( registry, settings );
            ClipCapabilityTests.Register( registry, settings );
            ExportServiceTests.Register( registry, settings );
            IkTargetTests.Register( registry, settings );
            DependencyWatcherTests.Register( registry, settings );
            PreferencesAutosaveTests.Register( registry, settings );
            FbxDmxExportTests.Register( registry, settings );
            ClipBlendTests.Register( registry, settings );
            TimelineEventTests.Register( registry, settings );
            DistributionInventoryTests.Register( registry, settings );
            DeformationInvariantTests.Register( registry, settings );
            MorphEvaluationTests.Register( registry, settings );
            PartTrackTests.Register( registry, settings );
            ConstraintParityTests.Register( registry, settings );
            FbxAnimationFeasibilityTests.Register( registry, settings );
            PackagingTests.Register( registry, settings );
            InstalledArtifactSmokeTests.Register( registry, settings );
            MaterialGpuTests.Register( registry, settings );
            PerformanceLifecycleTests.Register( registry, settings );
            LiveIkBufferTests.Register( registry, settings );
            HeadlessImageTests.Register( registry, settings );
            FbxClipConversionTests.Register( registry, settings );
            MaterialChannelGpuTests.Register( registry, settings );
            SurveyMaterialMapTests.Register( registry, settings );
            ViewportHandleTests.Register( registry, settings );
            ResourceBudgetTests.Register( registry, settings );
            PoseAgreementTests.Register( registry, settings );
            if ( settings.List )
            {
                foreach ( var test in registry.Cases ) Console.WriteLine( $"{test.Group}/{test.Name} fixture={(test.Group == "survey" ? settings.SurveyPath : "synthetic")}" );
                Console.WriteLine( $"{registry.Cases.Count} registered tests (CPU; no EGL required)" );
                return 0;
            }
            var results = registry.Run( Console.Out, settings.Filter );
            int passed = results.Count( r => r.Status == "PASS" ), failed = results.Count( r => r.Status == "FAIL" ), skipped = results.Count( r => r.Status == "SKIP" );
            Console.WriteLine( $"{passed} passed, {failed} failed, {skipped} skipped; {results.Count}/{registry.Cases.Count} selected/registered" );
            if ( results.Count == 0 ) Console.Error.WriteLine( "No tests selected; refusing an empty green run." );
            var report = new
            {
                schemaVersion = 1, mode = "cpu", createdUtc = DateTimeOffset.UtcNow,
                selected = results.Count, registered = registry.Cases.Count, passed, failed, skipped,
                filter = settings.Filter, settings.RequireLocalFixtures, settings.SurveyPath,
                artifacts = settings.WorkDirectory,
                assemblies = new[] { Identity( Assembly.GetExecutingAssembly() ), Identity( typeof(DesktopOptions).Assembly ), Identity( typeof(Effigy.Skeleton).Assembly ) },
                inventory = registry.Cases.Select( t => new { t.Group, t.Name, t.Required, fixture = t.Group == "survey" ? settings.SurveyPath : "synthetic" } ),
                results
            };
            string reportPath = settings.ReportPath ?? Path.Combine( settings.WorkDirectory, "results.json" );
            Directory.CreateDirectory( Path.GetDirectoryName( reportPath )! );
            File.WriteAllText( reportPath, JsonSerializer.Serialize( report, new JsonSerializerOptions { WriteIndented = true } ) );
            Console.WriteLine( "report: " + reportPath );
            return TestRegistry.ExitCode( results );
        }
        catch ( Exception ex ) { Console.Error.WriteLine( "Test host error: " + ex ); return 2; }
    }

    static object Identity( Assembly assembly ) => new
    {
        name = assembly.GetName().Name, path = assembly.Location,
        mvid = assembly.ManifestModule.ModuleVersionId,
        sha256 = Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( assembly.Location ) ) )
    };
}
