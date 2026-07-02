// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Uncomment the following line to enable debug counters for SSL diagnostics
#define DIRECTSSL_DEBUG_COUNTERS

using System.Globalization;
using System.Text;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// Comparison-prototype aggregator that prints a side-by-side dump of the
/// per-engine counters (declared as <c>public static long Total*</c> fields on
/// each engine's <c>SslEventPump</c>) so the two TLS engines can be benchmarked
/// against each other without instrumentation drift. The counters themselves
/// only exist when <c>DIRECTSSL_DEBUG_COUNTERS</c> is defined at compile-time
/// in each engine's <c>SslEventPump.cs</c> / <c>SslConnectionState.cs</c>.
/// </summary>
public static class DirectSslMetrics
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
        sb.AppendLine(Inv, $"================ DirectSsl engine comparison ================");
        sb.AppendLine(Inv, $"{"Counter",-32} {"TlsSession",18} {"OpenSslDirect",18}");
        sb.AppendLine(new string('-', 72));
        foreach (var key in tls.Keys)
        {
            long a = tls[key];
            long b = ssl.TryGetValue(key, out var v) ? v : 0;
            sb.AppendLine(Inv, $"{key,-32} {a,18:N0} {b,18:N0}");
        }

        // Derived metrics: handshake cost, sync %, calls/handshake. Same formulas for both engines.
        AppendDerivedHandshakeStats(sb, tls, ssl);

#if !DIRECTSSL_DEBUG_COUNTERS
        sb.AppendLine();
        sb.AppendLine("(All values will be zero unless DIRECTSSL_DEBUG_COUNTERS is defined at compile time.)");
#endif
        sb.AppendLine("============================================================");
        return sb.ToString();
    }

    private static void AppendDerivedHandshakeStats(
        StringBuilder sb,
        IDictionary<string, long> tls,
        IDictionary<string, long> ssl)
    {
        sb.AppendLine();
        sb.AppendLine("-- Derived handshake stats --");
        sb.AppendLine(Inv, $"{"Stat",-32} {"TlsSession",18} {"OpenSslDirect",18}");
        sb.AppendLine(new string('-', 72));

        static long Get(IDictionary<string, long> d, string k) => d.TryGetValue(k, out var v) ? v : 0;
        long tlsCompleted = Get(tls, "TotalHandshakeStarted");
        long sslCompleted = Get(ssl, "TotalHandshakeStarted");

        // sync %
        double tlsSyncPct = tlsCompleted == 0 ? 0 : (Get(tls, "TotalHandshakeSyncComplete") * 100.0 / tlsCompleted);
        double sslSyncPct = sslCompleted == 0 ? 0 : (Get(ssl, "TotalHandshakeSyncComplete") * 100.0 / sslCompleted);
        sb.AppendLine(Inv, $"{"sync-complete %",-32} {tlsSyncPct,17:F1}% {sslSyncPct,17:F1}%");

        // avg call count per handshake
        double tlsCalls = tlsCompleted == 0 ? 0 : ((double)Get(tls, "TotalHandshakeCallCount") / tlsCompleted);
        double sslCalls = sslCompleted == 0 ? 0 : ((double)Get(ssl, "TotalHandshakeCallCount") / sslCompleted);
        sb.AppendLine(Inv, $"{"avg pump calls / handshake",-32} {tlsCalls,18:F2} {sslCalls,18:F2}");

        // avg wall-clock µs per handshake (time from first SSL_do_handshake to Complete)
        double freqUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0; // ticks per microsecond
        double tlsWall = tlsCompleted == 0 ? 0 : Get(tls, "TotalHandshakeWallTicks") / freqUs / tlsCompleted;
        double sslWall = sslCompleted == 0 ? 0 : Get(ssl, "TotalHandshakeWallTicks") / freqUs / sslCompleted;
        sb.AppendLine(Inv, $"{"avg wall µs / handshake",-32} {tlsWall,18:F1} {sslWall,18:F1}");

        // avg "busy" µs IN handshake calls (excludes idle WANT_READ wait)
        double tlsBusy = tlsCompleted == 0 ? 0 : Get(tls, "TotalHandshakeBusyTicks") / freqUs / tlsCompleted;
        double sslBusy = sslCompleted == 0 ? 0 : Get(ssl, "TotalHandshakeBusyTicks") / freqUs / sslCompleted;
        sb.AppendLine(Inv, $"{"avg busy µs / handshake",-32} {tlsBusy,18:F1} {sslBusy,18:F1}");

        // --- Read/Write per-call stats ---
        sb.AppendLine();
        sb.AppendLine("-- Derived Read/Write per-call stats --");
        sb.AppendLine(Inv, $"{"Stat",-32} {"TlsSession",18} {"OpenSslDirect",18}");
        sb.AppendLine(new string('-', 72));
        long tlsRd = Get(tls, "TotalReadCallCount"), sslRd = Get(ssl, "TotalReadCallCount");
        long tlsRdC = Get(tls, "TotalReadComplete"), sslRdC = Get(ssl, "TotalReadComplete");
        long tlsRdB = Get(tls, "TotalReadBytes"), sslRdB = Get(ssl, "TotalReadBytes");
        double tlsRdBusy = tlsRd == 0 ? 0 : Get(tls, "TotalReadBusyTicks") / freqUs / tlsRd;
        double sslRdBusy = sslRd == 0 ? 0 : Get(ssl, "TotalReadBusyTicks") / freqUs / sslRd;
        sb.AppendLine(Inv, $"{"avg µs / Read call",-32} {tlsRdBusy,18:F2} {sslRdBusy,18:F2}");
        double tlsRdMax = Get(tls, "MaxReadBusyTicks") / freqUs;
        double sslRdMax = Get(ssl, "MaxReadBusyTicks") / freqUs;
        sb.AppendLine(Inv, $"{"max µs / Read call",-32} {tlsRdMax,18:F1} {sslRdMax,18:F1}");
        double tlsRdAvgB = tlsRdC == 0 ? 0 : (double)tlsRdB / tlsRdC;
        double sslRdAvgB = sslRdC == 0 ? 0 : (double)sslRdB / sslRdC;
        sb.AppendLine(Inv, $"{"avg bytes / completed Read",-32} {tlsRdAvgB,18:F1} {sslRdAvgB,18:F1}");

        long tlsWr = Get(tls, "TotalWriteCallCount"), sslWr = Get(ssl, "TotalWriteCallCount");
        long tlsWrB = Get(tls, "TotalWriteBytes"), sslWrB = Get(ssl, "TotalWriteBytes");
        double tlsWrBusy = tlsWr == 0 ? 0 : Get(tls, "TotalWriteBusyTicks") / freqUs / tlsWr;
        double sslWrBusy = sslWr == 0 ? 0 : Get(ssl, "TotalWriteBusyTicks") / freqUs / sslWr;
        sb.AppendLine(Inv, $"{"avg µs / Write call",-32} {tlsWrBusy,18:F2} {sslWrBusy,18:F2}");
        double tlsWrMax = Get(tls, "MaxWriteBusyTicks") / freqUs;
        double sslWrMax = Get(ssl, "MaxWriteBusyTicks") / freqUs;
        sb.AppendLine(Inv, $"{"max µs / Write call",-32} {tlsWrMax,18:F1} {sslWrMax,18:F1}");
        double tlsWrAvgB = tlsWr == 0 ? 0 : (double)tlsWrB / tlsWr;
        double sslWrAvgB = sslWr == 0 ? 0 : (double)sslWrB / sslWr;
        sb.AppendLine(Inv, $"{"avg bytes / Write call",-32} {tlsWrAvgB,18:F1} {sslWrAvgB,18:F1}");
        // ReadAsync body time and gap between reads (suspend->resume)
        long tlsRE = Get(tls, "TotalReadAsyncEntries"), sslRE = Get(ssl, "TotalReadAsyncEntries");
        double tlsBody = tlsRE == 0 ? 0 : Get(tls, "TotalReadAsyncBodyTicks") / freqUs / tlsRE;
        double sslBody = sslRE == 0 ? 0 : Get(ssl, "TotalReadAsyncBodyTicks") / freqUs / sslRE;
        sb.AppendLine(Inv, $"{"avg us / ReadAsync body",-32} {tlsBody,18:F2} {sslBody,18:F2}");
        long tlsGC = Get(tls, "TotalReadAsyncGapCount"), sslGC = Get(ssl, "TotalReadAsyncGapCount");
        double tlsGap = tlsGC == 0 ? 0 : Get(tls, "TotalReadAsyncGapTicks") / freqUs / tlsGC;
        double sslGap = sslGC == 0 ? 0 : Get(ssl, "TotalReadAsyncGapTicks") / freqUs / sslGC;
        sb.AppendLine(Inv, $"{"avg us gap between Reads",-32} {tlsGap,18:F2} {sslGap,18:F2}");
        sb.AppendLine(Inv, $"{"ReadAsync entries",-32} {tlsRE,18:N0} {sslRE,18:N0}");

    }

    /// <summary>
    /// Snapshot of the HEAD (TlsSession-engine) <c>SslEventPump</c> static counters,
    /// or an empty dictionary when <c>DIRECTSSL_DEBUG_COUNTERS</c> is not defined.
    /// </summary>
    public static IDictionary<string, long> ReadTlsSessionCounters()
    {
#if DIRECTSSL_DEBUG_COUNTERS
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
                        ["TotalReadCallCount"]             = SslEventPump.TotalReadCallCount,
            ["TotalReadBytes"]                 = SslEventPump.TotalReadBytes,
            ["TotalReadBusyTicks"]             = SslEventPump.TotalReadBusyTicks,
            ["MaxReadBusyTicks"]               = SslEventPump.MaxReadBusyTicks,
            ["TotalReadComplete"]              = SslEventPump.TotalReadComplete,
            ["TotalReadWantRead"]              = SslEventPump.TotalReadWantRead,
            ["TotalWriteCallCount"]            = SslEventPump.TotalWriteCallCount,
            ["TotalWriteBytes"]                = SslEventPump.TotalWriteBytes,
            ["TotalWriteBusyTicks"]            = SslEventPump.TotalWriteBusyTicks,
            ["TotalReadAsyncEntries"]          = SslEventPump.TotalReadAsyncEntries,
            ["TotalReadAsyncBodyTicks"]        = SslEventPump.TotalReadAsyncBodyTicks,
            ["TotalReadAsyncGapTicks"]         = SslEventPump.TotalReadAsyncGapTicks,
            ["TotalReadAsyncGapCount"]         = SslEventPump.TotalReadAsyncGapCount,
            ["MaxWriteBusyTicks"]              = SslEventPump.MaxWriteBusyTicks,
            ["TotalHandshakeStarted"]          = SslEventPump.TotalHandshakeStarted,
            ["TotalHandshakeSyncComplete"]     = SslEventPump.TotalHandshakeSyncComplete,
            ["TotalHandshakeCallCount"]        = SslEventPump.TotalHandshakeCallCount,
            ["TotalHandshakeWallTicks"]        = SslEventPump.TotalHandshakeWallTicks,
            ["TotalHandshakeBusyTicks"]        = SslEventPump.TotalHandshakeBusyTicks,
        };
#else
        return EmptySnapshot();
#endif
    }

    /// <summary>
    /// Snapshot of the resurrected OpenSslDirect-engine <c>SslEventPump</c> static counters,
    /// or an empty dictionary when <c>DIRECTSSL_DEBUG_COUNTERS</c> is not defined.
    /// </summary>
    public static IDictionary<string, long> ReadOpenSslDirectCounters()
    {
#if DIRECTSSL_DEBUG_COUNTERS
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
                        ["TotalReadCallCount"]             = Engines.OpenSslDirect.SslEventPump.TotalReadCallCount,
            ["TotalReadBytes"]                 = Engines.OpenSslDirect.SslEventPump.TotalReadBytes,
            ["TotalReadBusyTicks"]             = Engines.OpenSslDirect.SslEventPump.TotalReadBusyTicks,
            ["MaxReadBusyTicks"]               = Engines.OpenSslDirect.SslEventPump.MaxReadBusyTicks,
            ["TotalReadComplete"]              = Engines.OpenSslDirect.SslEventPump.TotalReadComplete,
            ["TotalReadWantRead"]              = Engines.OpenSslDirect.SslEventPump.TotalReadWantRead,
            ["TotalWriteCallCount"]            = Engines.OpenSslDirect.SslEventPump.TotalWriteCallCount,
            ["TotalWriteBytes"]                = Engines.OpenSslDirect.SslEventPump.TotalWriteBytes,
            ["TotalWriteBusyTicks"]            = Engines.OpenSslDirect.SslEventPump.TotalWriteBusyTicks,
            ["TotalReadAsyncEntries"]          = Engines.OpenSslDirect.SslEventPump.TotalReadAsyncEntries,
            ["TotalReadAsyncBodyTicks"]        = Engines.OpenSslDirect.SslEventPump.TotalReadAsyncBodyTicks,
            ["TotalReadAsyncGapTicks"]         = Engines.OpenSslDirect.SslEventPump.TotalReadAsyncGapTicks,
            ["TotalReadAsyncGapCount"]         = Engines.OpenSslDirect.SslEventPump.TotalReadAsyncGapCount,
            ["MaxWriteBusyTicks"]              = Engines.OpenSslDirect.SslEventPump.MaxWriteBusyTicks,
            ["TotalHandshakeStarted"]          = Engines.OpenSslDirect.SslEventPump.TotalHandshakeStarted,
            ["TotalHandshakeSyncComplete"]     = Engines.OpenSslDirect.SslEventPump.TotalHandshakeSyncComplete,
            ["TotalHandshakeCallCount"]        = Engines.OpenSslDirect.SslEventPump.TotalHandshakeCallCount,
            ["TotalHandshakeWallTicks"]        = Engines.OpenSslDirect.SslEventPump.TotalHandshakeWallTicks,
            ["TotalHandshakeBusyTicks"]        = Engines.OpenSslDirect.SslEventPump.TotalHandshakeBusyTicks,
        };
#else
        return EmptySnapshot();
#endif
    }

    // private static IDictionary<string, long> EmptySnapshot() => new Dictionary<string, long>
    // {
    //     ["TotalWriteEof"]                  = 0,
    //     ["TotalReadEof"]                   = 0,
    //     ["TotalWriteErrors"]               = 0,
    //     ["TotalReadErrors"]                = 0,
    //     ["TotalSslErrorSyscall"]           = 0,
    //     ["TotalSslErrorSyscallImmediate"]  = 0,
    //     ["TotalSslErrorSyscallAfterEpoll"] = 0,
    //     ["TotalSslErrorSyscallRet0"]       = 0,
    //     ["TotalSslErrorSyscallRetNeg1"]    = 0,
    //     ["TotalSslErrorSyscallErrno0"]     = 0,
    //     ["TotalSslErrorSyscallErrno11"]    = 0,
    //     ["TotalSslErrorSyscallErrnoOther"] = 0,
    //     ["TotalSslErrorZeroReturn"]        = 0,
    //     ["TotalSslErrorSsl"]               = 0,
    //     ["TotalSslErrorOther"]             = 0,
    //     ["TotalWriteWouldBlock"]           = 0,
    //     ["TotalWriteImmediate"]            = 0,
    //     ["TotalRequestsCompleted"]         = 0,
    //     ["TotalHandshakeStarted"]          = 0,
    //     ["TotalHandshakeSyncComplete"]     = 0,
    //     ["TotalHandshakeCallCount"]        = 0,
    //     ["TotalHandshakeWallTicks"]        = 0,
    //     ["TotalHandshakeBusyTicks"]        = 0,
    // };
}
