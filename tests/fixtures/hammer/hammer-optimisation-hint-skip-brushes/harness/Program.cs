using System;
using System.Linq;

var registry = new TestRegistry();
var settings = new TestSettings();
HintSkipTests.Register( registry, settings );
var filter = args.Length > 0 ? args[0] : "hintskip";
var results = registry.Run( Console.Out, filter );
int passed = results.Count( r => r.Status == "PASS" ), failed = results.Count( r => r.Status == "FAIL" ), skipped = results.Count( r => r.Status == "SKIP" );
Console.WriteLine( $"{passed} passed, {failed} failed, {skipped} skipped; {results.Count}/{registry.Cases.Count} selected/registered" );
return TestRegistry.ExitCode( results );
