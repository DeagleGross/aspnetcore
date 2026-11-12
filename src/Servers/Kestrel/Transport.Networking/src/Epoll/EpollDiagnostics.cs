// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

// Opt-in (NETWORKPROTO_DIAG=1) live view of the server workers: prints cumulative syscall-level counters summed over all workers once per second,
// because a crank run stops the process without letting the workers print their end-of-run metrics.
internal static class EpollDiagnostics
{
    private static readonly List<EpollWorker> s_workers = new();
    private static Timer? s_timer;

    internal static void RegisterIfRequested(EpollWorker worker)
    {
        if (Environment.GetEnvironmentVariable("NETWORKPROTO_DIAG") != "1")
        {
            return;
        }

        lock (s_workers)
        {
            s_workers.Add(worker);
            s_timer ??= new Timer(static _ => Print(), null, 1000, 1000);
        }
    }

    private static void Print()
    {
        long waits = 0, events = 0, recv = 0, send = 0, recvBlocked = 0, wakeReads = 0, wakeWrites = 0, commands = 0, iterations = 0, controls = 0, returned = 0;
        int connections = 0, min = int.MaxValue, max = 0, count;
        lock (s_workers)
        {
            count = s_workers.Count;
            foreach (var worker in s_workers)
            {
                var metrics = worker.Metrics;
                waits += metrics.WaitCalls;
                events += metrics.ReadyEvents;
                recv += metrics.RecvCalls;
                send += metrics.SendCalls;
                recvBlocked += metrics.ReceiveWouldBlock;
                wakeReads += metrics.WakeReads;
                controls += metrics.ControlCalls;
                returned += metrics.ReturnedPages;
                wakeWrites += worker.WakeWrites;
                commands += worker.CommandsProcessed;
                iterations += worker.Iterations;
                var live = worker.ConnectionCount;
                connections += live;
                min = Math.Min(min, live);
                max = Math.Max(max, live);
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"DIAG_EPOLL workers={count} conns={connections} minConns={min} maxConns={max} waits={waits} events={events} iterations={iterations} recv={recv} recvBlocked={recvBlocked} send={send} wakeWrites={wakeWrites} wakeReads={wakeReads} commands={commands} controls={controls} returnedPages={returned}"));
    }
}
