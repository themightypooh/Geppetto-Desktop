using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Lane L — linework verification engine (s&box Geppetto, kernel side).
///
/// Runs a named set of linework test groups, collects a pass/fail count
/// per group, prints a one-line-per-group summary, and yields a
/// non-zero exit code when anything fails.
///
/// Deliberately dependency-free: no Effigy.Tests types, no Sandbox or
/// engine types, so this file compiles wherever the kernel is copied
/// loose (the test project compiles Effigy/**/*.cs from source).
/// The caller supplies the pass/fail tally — in Effigy.Tests that is
/// Report.Passed / Report.Failed — so the engine never depends on the
/// runner. A group that throws, or that runs zero checks, is a failure,
/// never a silent pass, and a failure in one group never stops the
/// groups after it.
/// </summary>
public sealed class LineworkVerifyResult
{
	public string Name;
	public int Passed;
	public int Failed;
	public string Error;

	public bool Ok => Error is null && Failed == 0 && Passed > 0;
}

public sealed class LineworkVerifyGroup
{
	public string Name;
	public Action Run;

	public LineworkVerifyGroup( string name, Action run )
	{
		Name = name;
		Run = run;
	}
}

public static class LineworkVerify
{
	public static LineworkVerifyResult RunOne( LineworkVerifyGroup group, Func<int> passed, Func<int> failed )
	{
		if ( group is null )
			return new LineworkVerifyResult { Name = "(null group)", Failed = 1, Error = "null group" };
		if ( string.IsNullOrWhiteSpace( group.Name ) )
			return new LineworkVerifyResult { Name = "(unnamed group)", Failed = 1, Error = "unnamed group" };
		if ( group.Run is null )
			return new LineworkVerifyResult { Name = group.Name, Failed = 1, Error = "group has no runner" };

		var beforePassed = passed();
		var beforeFailed = failed();
		string error = null;
		try
		{
			group.Run();
		}
		catch ( Exception ex )
		{
			error = ex.GetType().Name + ": " + ex.Message;
		}

		var result = new LineworkVerifyResult
		{
			Name = group.Name,
			Passed = passed() - beforePassed,
			Failed = failed() - beforeFailed,
			Error = error
		};
		if ( error != null )
			result.Failed += 1;
		return result;
	}

	public static List<LineworkVerifyResult> RunAll( IReadOnlyList<LineworkVerifyGroup> groups, Func<int> passed, Func<int> failed )
	{
		var results = new List<LineworkVerifyResult>();
		if ( groups is null || groups.Count == 0 )
		{
			results.Add( new LineworkVerifyResult { Name = "(no groups)", Failed = 1, Error = "no groups to verify" } );
			return results;
		}
		foreach ( var group in groups )
			results.Add( RunOne( group, passed, failed ) );
		return results;
	}

	/// <summary>Exactly one line per group. Never contains a newline.</summary>
	public static string Summarize( LineworkVerifyResult result )
	{
		if ( result is null )
			return "LINEWORK-VERIFY (null result): 0 passed, 1 failed [ERROR: null result]";
		var line = "LINEWORK-VERIFY " + result.Name + ": " + result.Passed + " passed, " + result.Failed + " failed";
		if ( result.Error != null )
			line += " [ERROR: " + result.Error + "]";
		else if ( result.Passed == 0 && result.Failed == 0 )
			line += " [NO CHECKS RAN]";
		return line;
	}

	public static int TotalPassed( IReadOnlyList<LineworkVerifyResult> results )
	{
		var total = 0;
		if ( results is null )
			return total;
		foreach ( var result in results )
			total += result.Passed;
		return total;
	}

	public static int TotalFailed( IReadOnlyList<LineworkVerifyResult> results )
	{
		var total = 0;
		if ( results is null )
			return total;
		foreach ( var result in results )
			total += result.Failed;
		return total;
	}

	/// <summary>0 only when every result is Ok. Null or empty results are a failure: nothing verified is not a pass.</summary>
	public static int ExitCode( IReadOnlyList<LineworkVerifyResult> results )
	{
		if ( results is null || results.Count == 0 )
			return 1;
		foreach ( var result in results )
		{
			if ( result is null || !result.Ok )
				return 1;
		}
		return 0;
	}
}
