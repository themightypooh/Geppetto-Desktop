using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

// HARNESS-ONLY STUBS. Mirrors the real tests/TestRegistry.cs API shape
// (Add / Run / ExitCode) so the real tests/HintSkipTests.cs compiles and runs
// verbatim here. Output format matches the real host's PASS/FAIL lines.

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
			output.WriteLine( $"{status} {test.Group}/{test.Name}{( reason.Length == 0 ? "" : ": " + reason )}" );
		}
		return results;
	}

	public static int ExitCode( IReadOnlyList<TestOutcome> results ) => results.Count == 0 || results.Any( r => r.Status == "FAIL" ) ? 1 : 0;
}

internal sealed class TestSettings
{
}
