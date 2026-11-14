using System;
using System.Collections.Generic;

namespace Marionette.Desktop;

/// <summary>
/// The one place a tolerated failure goes on Desktop. A catch that carries on (a leftover temp file, a
/// settings file that will not load, a cleanup that races) still leaves a line in the host log, so a
/// report shows what went wrong instead of nothing. Repeats of the same spot are throttled so a loop
/// cannot flood the log: the first 3 print, then every 100th.
/// Mirrors Mobile app/SoftFail.cs; the phone writes to logcat via GD.PrintErr, Desktop writes to
/// Console.Error, which is this host's log channel (see Program.Main's error reporting).
/// NOTE: Mobile redacts secrets via SecretsSafe.Redact; Desktop has no such helper, so Note prints the
/// exception type and message as-is — the same content KernelLog's stderr fallback prints.
/// </summary>
public static class SoftFail
{
	static readonly Dictionary<string, int> _seen = new();
	static readonly object _lock = new();

	public static void Note( string where, Exception e )
	{
		int n;
		lock ( _lock ) { _seen.TryGetValue( where, out n ); _seen[where] = ++n; }
		if ( n > 3 && n % 100 != 0 ) return;
		var detail = e == null ? "" : e.GetType().Name + ": " + e.Message;
		var repeat = n > 3 ? $" (x{n})" : "";
		Console.Error.WriteLine( $"[soft-fail] {where}{repeat}: {detail}" );
	}

	/// <summary>The kernel has no host types: route its tolerated failures here too.</summary>
	internal static void HookKernel() => Effigy.KernelLog.Sink = ( where, e ) => Note( "kernel " + where, e );
}
