// G11 focused runner: same Register(registry, settings) shape as tests/TestRegistry.cs,
// so tests/ControlRegistryTests.cs compiles unchanged in both places.

var registry = new TestRegistry();
ControlRegistryTests.Register( registry, new TestSettings() );
string? filter = args.Length >= 2 && args[0] == "--filter" ? args[1] : null;
int failed = 0, passed = 0;
foreach ( var (group, name, body) in registry.Cases )
{
	if ( filter is not null && !$"{group}/{name}".Contains( filter, StringComparison.OrdinalIgnoreCase ) ) continue;
	try { body(); passed++; Console.WriteLine( $"PASS {group}/{name}" ); }
	catch ( Exception ex ) { failed++; Console.WriteLine( $"FAIL {group}/{name}: {ex.Message}" ); }
}
Console.WriteLine( $"{passed} passed, {failed} failed" );
return failed == 0 && passed > 0 ? 0 : 1;

internal sealed class TestRegistry
{
	public readonly List<(string Group, string Name, Action Body)> Cases = new();
	public void Add( string group, string name, Action body, Func<string?>? missing = null, bool required = false ) => Cases.Add( (group, name, body) );
}

internal sealed class TestSettings { }
