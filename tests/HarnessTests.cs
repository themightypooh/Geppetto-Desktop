internal static class HarnessTests
{
    public static void Register( TestRegistry suite, TestSettings settings )
    {
        suite.Add( "harness", "registered failing assertions return nonzero", () =>
        {
            var runner = new TestRegistry();
            runner.Add( "fixture", "intentional failure", () => throw new Exception( "sentinel assertion" ) );
            var results = runner.Run( TextWriter.Null );
            Require( results.Single().Status == "FAIL" && results.Single().Reason!.Contains( "sentinel assertion" ) );
            Require( TestRegistry.ExitCode( results ) == 1 );
        } );
        suite.Add( "harness", "missing optional fixture is skipped, not passed", () =>
        {
            var runner = new TestRegistry();
            runner.Add( "fixture", "missing", () => throw new Exception( "must not run" ), () => "missing fixture sentinel" );
            var results = runner.Run( TextWriter.Null );
            Require( results.Single().Status == "SKIP" && TestRegistry.ExitCode( results ) == 0 );
        } );
        suite.Add( "harness", "missing required fixture fails without running body", () =>
        {
            var runner = new TestRegistry();
            runner.Add( "fixture", "required", () => throw new Exception( "must not run" ), () => "missing fixture sentinel", required: true );
            var results = runner.Run( TextWriter.Null );
            Require( results.Single().Status == "FAIL" && TestRegistry.ExitCode( results ) == 1 );
            Require( results.Single().Reason == "missing fixture sentinel" );
        } );
        suite.Add( "harness", "duplicate registration and empty selections cannot pass", () =>
        {
            var runner = new TestRegistry();
            runner.Add( "fixture", "registered", () => { } );
            bool rejected = false;
            try { runner.Add( "fixture", "registered", () => { } ); }
            catch ( ArgumentException ) { rejected = true; }
            Require( rejected && TestRegistry.ExitCode( runner.Run( TextWriter.Null, "no matching test" ) ) == 1 );
        } );
    }

    static void Require( bool condition )
    {
        if ( !condition ) throw new Exception( "Harness self-test failed." );
    }
}
