// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// To activate counter collection in both engines, uncomment the following
// line OR pass -p:DefineConstants=DIRECTSSL_DEBUG_COUNTERS to `dotnet build`.
// #define DIRECTSSL_DEBUG_COUNTERS

using System.Text;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// Comparison-prototype aggregator that prints a side-by-side dump of the
/// per-engine counters (declared as <c>public static long Total*</c> fields on
/// each engine's <c>SslEventPump</c>) so the two TLS engines can be benchmarked
/// against each other without instrumentation drift. The counters themselves
/// only update when <c>DIRECTSSL_DEBUG_COUNTERS</c> is defined at compile-time
/// (the same gate the engines use internally).
/// </summary>
internal static class DirectSslMetrics
{
    /// <summary>
    /// Returns a single text block with both engines' counters side-by-side
    /// (TlsSession on the left, OpenSslDirect on the right). Use from the
    /// sample app to log a summary on Ctrl+C or after a benchmark run.
    /// </summary>
    public static string DumpComparison()
    {
        var tls = ReadTlsSessionCounters();
        var ssl = ReadOpenSslDirectCounters();

        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine("================ DirectSsl engine comparison ================");
        sb.AppendLine($"{"Counter",-32} {"TlsSession",18} {"OpenSslDirect",18}");
        sb.AppendLine(new string('-', 72));
        foreach (var key in tls.Keys)
        {
            long a = tls[key];
            long b = ssl.TryGetValue(key, out var v) ? v : 0;
            sb.AppendLine($"{key,-32} {a,18:N0} {b,18:N0}");
        }
#if !DIRECTSSL_DEBUG_COUNTERS
        sb.AppendLine();
        sb.AppendLine("(All values will be zero unless DIRECTSSL_DEBUG_COUNTERS is defined at compile time.)");
#endif
        sb.AppendLine("============================================================");
        return sb.ToString();
    }

    /// <summary>
    /// Snapshot of the HEAD (TlsSession-engine) <c>SslEventPump</c> static counters.
    /// </summary>
    public static IDictionary<string, long> ReadTlsSessionCounters()
    {
        return new Dictionary<string, long>
        {
            ["TotalWriteEof"]                  = SslEventPump.TotalWriteEof,
            ["TotalReadEof"]                   = SslEventPump.TotalReadEof,
            ["TotalWriteErrors"]               = SslEventPump.TotalWriteErrors,
            ["TotalReadErrors"]                = SslEventPump.TotalReadErrors,
            ["TotalSslErrorSyscall"]           = SslEventPump.TotalSslErrorSyscall,
            ["TotalSslErrorSyscallImmediate"]  = SslEventPump.TotalSslErrorSyscallImmediate,
            ["TotalSslErrorSyscallAfterEpoll"] = SslEventPump.TotalSslErrorSyscallAfterEpoll,
            ["TotalSslErrorSyscallRet0"]       = SslEventPump.TotalSslErrorSyscallRet0,
            ["TotalSslErrorSyscallRetNeg1"]    = SslEventPump.TotalSslErrorSyscallRetNeg1,
            ["TotalSslErrorSyscallErrno0"]     = SslEventPump.TotalSslErrorSyscallErrno0,
            ["TotalSslErrorSyscallErrno11"]    = SslEventPump.TotalSslErrorSyscallErrno11,
            ["TotalSslErrorSyscallErrnoOther"] = SslEventPump.TotalSslErrorSyscallErrnoOther,
            ["TotalSslErrorZeroReturn"]        = SslEventPump.TotalSslErrorZeroReturn,
            ["TotalSslErrorSsl"]               = SslEventPump.TotalSslErrorSsl,
            ["TotalSslErrorOther"]             = SslEventPump.TotalSslErrorOther,
            ["TotalWriteWouldBlock"]           = SslEventPump.TotalWriteWouldBlock,
            ["TotalWriteImmediate"]            = SslEventPump.TotalWriteImmediate,
            ["TotalRequestsCompleted"]         = SslEventPump.TotalRequestsCompleted,
        };
    }

    /// <summary>
    /// Snapshot of the resurrected OpenSslDirect-engine <c>SslEventPump</c> static counters.
    /// </summary>
    public static IDictionary<string, long> ReadOpenSslDirectCounters()
    {
        return new Dictionary<string, long>
        {
            ["TotalWriteEof"]                  = Engines.OpenSslDirect.SslEventPump.TotalWriteEof,
            ["TotalReadEof"]                   = Engines.OpenSslDirect.SslEventPump.TotalReadEof,
            ["TotalWriteErrors"]               = Engines.OpenSslDirect.SslEventPump.TotalWriteErrors,
            ["TotalReadErrors"]                = Engines.OpenSslDirect.SslEventPump.TotalReadErrors,
            ["TotalSslErrorSyscall"]           = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscall,
            ["TotalSslErrorSyscallImmediate"]  = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallImmediate,
            ["TotalSslErrorSyscallAfterEpoll"] = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallAfterEpoll,
            ["TotalSslErrorSyscallRet0"]       = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallRet0,
            ["TotalSslErrorSyscallRetNeg1"]    = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallRetNeg1,
            ["TotalSslErrorSyscallErrno0"]     = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallErrno0,
            ["TotalSslErrorSyscallErrno11"]    = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallErrno11,
            ["TotalSslErrorSyscallErrnoOther"] = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSyscallErrnoOther,
            ["TotalSslErrorZeroReturn"]        = Engines.OpenSslDirect.SslEventPump.TotalSslErrorZeroReturn,
            ["TotalSslErrorSsl"]               = Engines.OpenSslDirect.SslEventPump.TotalSslErrorSsl,
            ["TotalSslErrorOther"]             = Engines.OpenSslDirect.SslEventPump.TotalSslErrorOther,
            ["TotalWriteWouldBlock"]           = Engines.OpenSslDirect.SslEventPump.TotalWriteWouldBlock,
            ["TotalWriteImmediate"]            = Engines.OpenSslDirect.SslEventPump.TotalWriteImmediate,
            ["TotalRequestsCompleted"]         = Engines.OpenSslDirect.SslEventPump.TotalRequestsCompleted,
        };
    }
}
