using System;
using System.IO;
using Effigy;

/// <summary>
/// Targeted tests for the ported KernelLog facility
/// (port-mobile-kernellog-facility-to-desktop-kernel). KernelLog is the
/// kernel's soft-fail reporter: a host installs Sink, otherwise failures
/// go to stderr, and logging itself must never throw into the caller.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// KernelLogTests.Register(registry, settings); line, as was done for
/// KernelParityTests.
/// </summary>
internal static class KernelLogTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("kernel-log", "sink receives where and exception", () =>
        {
            Action<string, Exception> previous = KernelLog.Sink;
            string? seenWhere = null;
            Exception? seenError = null;
            try
            {
                KernelLog.Sink = (where, e) => { seenWhere = where; seenError = e; };
                var failure = new InvalidOperationException("boom");
                KernelLog.Soft("Test.Where", failure);
                if (seenWhere != "Test.Where") throw new Exception("sink got wrong where: " + seenWhere);
                if (!ReferenceEquals(seenError, failure)) throw new Exception("sink got wrong exception instance");
            }
            finally { KernelLog.Sink = previous; }
        });

        registry.Add("kernel-log", "no sink writes to stderr without throwing", () =>
        {
            Action<string, Exception> previous = KernelLog.Sink;
            TextWriter previousError = Console.Error;
            var captured = new StringWriter();
            try
            {
                KernelLog.Sink = null!;
                Console.SetError(captured);
                KernelLog.Soft("Test.Stderr", new InvalidOperationException("loud"));
                string output = captured.ToString();
                if (!output.Contains("[kernel soft-fail]", StringComparison.Ordinal)) throw new Exception("stderr missing soft-fail marker: " + output);
                if (!output.Contains("Test.Stderr", StringComparison.Ordinal)) throw new Exception("stderr missing where: " + output);
                if (!output.Contains("InvalidOperationException", StringComparison.Ordinal)) throw new Exception("stderr missing exception type: " + output);
                if (!output.Contains("loud", StringComparison.Ordinal)) throw new Exception("stderr missing message: " + output);
            }
            finally { Console.SetError(previousError); KernelLog.Sink = previous; }
        });

        registry.Add("kernel-log", "throwing sink never propagates", () =>
        {
            Action<string, Exception> previous = KernelLog.Sink;
            try
            {
                KernelLog.Sink = (where, e) => throw new Exception("sink exploded");
                KernelLog.Soft("Test.ThrowingSink", new Exception("original"));
            }
            finally { KernelLog.Sink = previous; }
        });

        registry.Add("kernel-log", "null exception is tolerated on both paths", () =>
        {
            Action<string, Exception> previous = KernelLog.Sink;
            TextWriter previousError = Console.Error;
            var captured = new StringWriter();
            try
            {
                bool sinkCalled = false;
                Exception? sinkError = new Exception("sentinel");
                KernelLog.Sink = (where, e) => { sinkCalled = true; sinkError = e; };
                KernelLog.Soft("Test.NullSink", null!);
                if (!sinkCalled) throw new Exception("sink not called for null exception");
                if (sinkError != null) throw new Exception("sink should receive the null exception as-is");
                KernelLog.Sink = null!;
                Console.SetError(captured);
                KernelLog.Soft("Test.NullStderr", null!);
                if (!captured.ToString().Contains("Test.NullStderr", StringComparison.Ordinal)) throw new Exception("stderr missing where for null exception");
            }
            finally { Console.SetError(previousError); KernelLog.Sink = previous; }
        });

        registry.Add("kernel-log", "sink can be cleared back to stderr", () =>
        {
            Action<string, Exception> previous = KernelLog.Sink;
            TextWriter previousError = Console.Error;
            var captured = new StringWriter();
            try
            {
                bool sinkCalled = false;
                KernelLog.Sink = (where, e) => { sinkCalled = true; };
                KernelLog.Sink = null!;
                Console.SetError(captured);
                KernelLog.Soft("Test.Cleared", new Exception("after clear"));
                if (sinkCalled) throw new Exception("cleared sink was still called");
                if (!captured.ToString().Contains("[kernel soft-fail]", StringComparison.Ordinal)) throw new Exception("clearing the sink did not fall back to stderr");
            }
            finally { Console.SetError(previousError); KernelLog.Sink = previous; }
        });
    }
}
