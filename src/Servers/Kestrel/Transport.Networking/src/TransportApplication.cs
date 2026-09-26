// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking;

// The prototype keeps the application's receive/close boundary independent of
// the completion engine. Memory belongs to the application until the terminal
// receive CQE; the next receive waits for OnReceiveAsync (backpressure).
internal abstract class TransportApplication
{
    public abstract Memory<byte> GetReceiveMemory();
    public abstract ValueTask<FlushResult> OnReceiveAsync(int count);
    public abstract void OnClosed(Exception? error);
    public abstract PipeReader Output { get; }
}

internal sealed class PipelineApplication : TransportApplication
{
    private static readonly PipeOptions SharedOptions = CreateOptions();
    private readonly Pipe _input;
    private readonly Pipe _output;

    public IDuplexPipe Transport { get; }

    public PipelineApplication(bool shareOptions)
    {
        _input = new Pipe(shareOptions ? SharedOptions : CreateOptions());
        _output = new Pipe(shareOptions ? SharedOptions : CreateOptions());
        Transport = new DuplexPipe(_input.Reader, _output.Writer);
    }

    private static PipeOptions CreateOptions() => new(readerScheduler: PipeScheduler.ThreadPool, writerScheduler: PipeScheduler.ThreadPool,
        pauseWriterThreshold: 65536, resumeWriterThreshold: 32768, useSynchronizationContext: false);

    public override PipeReader Output => _output.Reader;

    public override Memory<byte> GetReceiveMemory() => _input.Writer.GetMemory(16384);

    public override ValueTask<FlushResult> OnReceiveAsync(int count)
    {
        _input.Writer.Advance(count);
        return _input.Writer.FlushAsync();
    }

    public override void OnClosed(Exception? error) => _input.Writer.Complete(error);

    public void Cancel()
    {
        _input.Writer.CancelPendingFlush();
        _output.Reader.CancelPendingRead();
    }
}
