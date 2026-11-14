using System;

namespace Effigy;

/// <summary>
/// Where the kernel reports a failure it deliberately carries on past. A host installs a sink (the phone
/// sends it to its log); without one it goes to stderr, so a swallowed failure is never silent.
/// </summary>
public static class KernelLog
{
	public static Action<string, Exception> Sink { get; set; }

	public static void Soft( string where, Exception e )
	{
		try
		{
			if ( Sink != null ) Sink( where, e );
			else Console.Error.WriteLine( $"[kernel soft-fail] {where}: {e?.GetType().Name}: {e?.Message}" );
		}
		catch { /* logging itself must never throw into the caller */ }
	}
}
