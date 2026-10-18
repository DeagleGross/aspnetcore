// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Net.Transport;

internal readonly record struct DemoSettings(NetworkEngineOptions EngineOptions, IPEndPoint EndPoint, TimeSpan Timeout)
{
    internal static DemoSettings Parse(string[] args, int defaultPort)
    {
        if (args.Length > 3)
        {
            throw new ArgumentException("Usage: [iocp|rio|epoll|uring] [port] [timeout-seconds; 0 disables timeout]");
        }
        var backend = args.Length > 0 ? args[0] : OperatingSystem.IsWindows() ? "iocp" : "epoll";
        NetworkEngineOptions options = backend switch
        {
            "iocp" => new IocpEngineOptions(),
            "rio" => new RioEngineOptions(),
            "epoll" => new EpollEngineOptions(),
            "uring" => new IoUringEngineOptions(),
            _ => throw new ArgumentException("Select iocp, rio, epoll or uring.")
        };
        options.WorkerCount = 1;
        options.WorkerProcessors = [0];
        var port = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : defaultPort;
        var seconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        return new(options, new IPEndPoint(IPAddress.Loopback, port),
            seconds == 0 ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds));
    }
}
