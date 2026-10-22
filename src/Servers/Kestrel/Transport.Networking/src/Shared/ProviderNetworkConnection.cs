// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Transport;
using EpollWorker = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll.EpollWorker;
using EpollState = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll.ConnectionState;
using RingEngine = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.Engine;
using RingState = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.RingConnection;
using RingNative = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.Native;
using WindowsWorker = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.WindowsWorker;
using WindowsState = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.ConnectionState;
using WindowsPool = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.NativeMemoryPool;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>
/// The provider-side <see cref="NetworkConnection"/>. Workers publish this type directly, so the shared path has no
/// Kestrel connection type; Kestrel wraps it in <see cref="KestrelConnectionContext"/> only when it accepts the connection.
/// </summary>
internal sealed class ProviderNetworkConnection : NetworkConnection, IProviderConnection
{
    private readonly Driver _driver;
    private bool _completingSend;

    private ProviderNetworkConnection(string id, IPEndPoint endpoint, bool tls, Driver driver, IPEndPoint? remote = null)
        : base(id, endpoint, remote ?? new IPEndPoint(IPAddress.Loopback, 0), driver.OutputPool, driver.OutputKind, tls)
    {
        _driver = driver;
        if (tls && driver is not WindowsDriver)
        {
            // Linux providers terminate a fixed TLS configuration; Windows reports the real Schannel session through TlsEstablished.
            SetTlsInfoCore(new NetworkTlsInfo(System.Security.Authentication.SslProtocols.Tls12, System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256));
        }
    }

    internal static ProviderNetworkConnection ForEpoll(EpollWorker worker, EpollState state, IPEndPoint endpoint)
    {
        return new ProviderNetworkConnection($"owned-shared-epoll-{state.Id:x}", endpoint, worker.Tls, new EpollDriver(worker, state));
    }

    internal static ProviderNetworkConnection ForRing(RingEngine engine, RingState state, IPEndPoint endpoint)
    {
        return new ProviderNetworkConnection($"owned-shared-uring-{state.Id:x}", endpoint, engine.Tls, new RingDriver(engine, state));
    }

    internal static ProviderNetworkConnection ForWindows(WindowsWorker worker, WindowsState state)
    {
        return new ProviderNetworkConnection($"owned-shared-{worker.Backend}-{worker.Index}-{state.Id:x}",
            state.LocalEndPoint, state.HasTls, new WindowsDriver(worker, state), state.RemoteEndPoint);
    }

    internal void Receive(Memory<byte> memory, int length, int page)
    {
        PublishReceiveCore(new ReturnedMemory(memory, _driver, page), length, NetworkMemoryKind.Pinned);
    }

    internal unsafe void Receive(nint pointer, int length, int page)
    {
        PublishReceiveCore(new ReturnedNativeMemory(pointer, length, _driver, page), length, NetworkMemoryKind.Pinned);
    }

    internal void Receive(IMemoryOwner<byte> owner, int length, NetworkMemoryKind kind)
    {
        PublishReceiveCore(new ReturnedOwnedMemory(owner, _driver), length, kind);
    }

    public void Sent(int count, Exception? error = null)
    {
        _driver.Sent();
        _completingSend = true;
        try
        {
            CompleteSendCore(count, error);
        }
        finally
        {
            _completingSend = false;
        }
    }

    public void Closed(Exception? error)
    {
        _driver.EndOutput();
        CompleteCloseCore(error);
    }
    public void EndReceive(Exception? error) => EndReceiveCore(error);
    public void PumpOutput() => PumpOutputCore();
    public void TlsEstablished(NetworkTlsInfo info) => SetTlsInfoCore(info);

    protected override void ScheduleOutputCore()
    {
        if (_driver.CanCompleteInline && _completingSend)
        {
            _completingSend = false;
            PumpOutputCore();
        }
        else
        {
            _driver.Schedule();
        }
    }

    protected override void SubmitSendCore(ReadOnlyMemory<byte> data, nint pinnedAddress, bool final) => _driver.Send(data, pinnedAddress, final);
    protected override void CloseCore() => _driver.Close();
    protected override void AbortCore(Exception error) => _driver.Abort(error);
    protected override ValueTask ForgetCoreAsync()
    {
        _driver.Forget();
        return default;
    }

    internal abstract class Driver
    {
        protected AdaptiveOutputPool? AdaptivePool;
        internal virtual MemoryPool<byte>? OutputPool => null;
        internal virtual NetworkMemoryKind OutputKind => NetworkMemoryKind.Managed;
        internal virtual bool CanCompleteInline => false;
        internal void EndOutput() => AdaptivePool?.EndConnection();
        internal virtual void Sent() { }
        internal abstract void Schedule();
        internal abstract void Send(ReadOnlyMemory<byte> data, nint pinnedAddress, bool final);
        internal abstract void Close();
        internal abstract void Abort(Exception error);
        internal abstract void Forget();
        internal abstract void Return(int page);
        internal virtual void Return(IMemoryOwner<byte> owner) => throw new InvalidOperationException("This provider returns page IDs.");
    }

    private sealed class EpollDriver(EpollWorker worker, EpollState state) : Driver
    {
        internal override MemoryPool<byte>? OutputPool => worker.OutputPool is { } pool ? AdaptivePool ??= AdaptiveOutputPool.Create(pool, 4096, 262144) : null;
        internal override NetworkMemoryKind OutputKind => worker.OutputPool is null ? NetworkMemoryKind.Managed : NetworkMemoryKind.Native;
        internal override void Schedule() => worker.Enqueue(new(EpollWorker.CommandKind.OutputReady, state));
        internal override void Send(ReadOnlyMemory<byte> data, nint pinnedAddress, bool final)
        {
            worker.Send(state, pinnedAddress, data.Length, final && worker.FinalSendEnabled);
        }
        internal override void Close() => state.RequestClose();
        internal override void Abort(Exception error) => worker.Enqueue(new(EpollWorker.CommandKind.Close, state));
        internal override void Forget() => worker.Enqueue(new(EpollWorker.CommandKind.Forget, state));
        internal override void Return(int page) => worker.Enqueue(new(EpollWorker.CommandKind.ReturnPage, state, Page: page));
    }

    private sealed class RingDriver(RingEngine engine, RingState state) : Driver
    {
        internal override MemoryPool<byte>? OutputPool => engine.OutputPool is { } pool ? AdaptivePool ??= AdaptiveOutputPool.Create(pool, 4096, 262144) : null;
        internal override NetworkMemoryKind OutputKind => engine.OutputPool is null ? NetworkMemoryKind.Managed : NetworkMemoryKind.Native;
        internal override void Schedule() => engine.Enqueue(new RingNative.Command { Kind = 7, Connection = state.Id });
        internal override void Send(ReadOnlyMemory<byte> data, nint pinnedAddress, bool final)
        {
            state.Send(pinnedAddress, data.Length, final && engine.FinalSendEnabled);
        }
        internal override void Close() => engine.Enqueue(new RingNative.Command { Kind = 3, Connection = state.Id });
        internal override void Abort(Exception error) => engine.Enqueue(new RingNative.Command { Kind = 3, Connection = state.Id });
        internal override void Forget() => engine.Enqueue(new RingNative.Command { Kind = 4, Connection = state.Id });
        internal override void Return(int page) => engine.Enqueue(new RingNative.Command { Kind = 2, Connection = state.Id, Page = page });
    }

    private sealed class WindowsDriver(WindowsWorker worker, WindowsState state) : Driver
    {
        // Native sends cost a completion each, so a response should start in one block large enough to send once.
        private const int FirstOutputBlock = 65536, MaxOutputBlock = 262144, MaxRegisteredOutputBlock = 1024 * 1024;
        private IMemoryOwner<byte>? _staging;
        internal override MemoryPool<byte> OutputPool => AdaptiveOutputPool.Enabled ? AdaptivePool ??= AdaptiveOutputPool.Create(worker.OutputPool, FirstOutputBlock, worker.IsRio ? MaxRegisteredOutputBlock : MaxOutputBlock) : worker.OutputPool;
        internal override NetworkMemoryKind OutputKind => worker.IsRio ? NetworkMemoryKind.Registered : NetworkMemoryKind.Native;
        internal override bool CanCompleteInline => worker.IsCurrentThread;
        internal override void Schedule() => worker.Enqueue(new(WindowsWorker.CommandKind.OutputReady, state));
        internal override void Send(ReadOnlyMemory<byte> data, nint pinnedAddress, bool final)
        {
            if (worker.IsRio && !state.HasTls && !WindowsPool.TryGetRegisteredBuffer(data, out _))
            {
                _staging = worker.OutputPool.Rent(data.Length);
                data.Span.CopyTo(_staging.Memory.Span);
                worker.Metrics.SendAdapterCopyBytes += data.Length;
                data = _staging.Memory[..data.Length];
                pinnedAddress = 0;
            }
            try
            {
                state.Send(data, pinnedAddress, final);
            }
            catch
            {
                Sent();
                throw;
            }
        }
        internal override void Sent()
        {
            _staging?.Dispose();
            _staging = null;
        }
        internal override void Close() => state.Close(null, abortive: false);
        internal override void Abort(Exception error) => worker.Enqueue(new(WindowsWorker.CommandKind.Close, state, Error: error));
        internal override void Forget() => worker.Enqueue(new(WindowsWorker.CommandKind.Forget, state));
        internal override void Return(int page) => throw new InvalidOperationException("Windows receives return memory owners, not page IDs.");
        internal override void Return(IMemoryOwner<byte> owner) => worker.Enqueue(new(WindowsWorker.CommandKind.ReturnPage, state, owner));
    }

    private sealed class ReturnedOwnedMemory(IMemoryOwner<byte> owner, Driver driver) : IMemoryOwner<byte>
    {
        private int _returned;
        public Memory<byte> Memory => owner.Memory;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                driver.Return(owner);
            }
        }
    }

    private sealed class ReturnedMemory(Memory<byte> memory, Driver driver, int page) : IMemoryOwner<byte>
    {
        private int _returned;
        public Memory<byte> Memory => memory;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                driver.Return(page);
            }
        }
    }

    private sealed unsafe class ReturnedNativeMemory(nint pointer, int length, Driver driver, int page) : MemoryManager<byte>
    {
        private int _returned;
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_returned != 0, this);
            return new Span<byte>((void*)pointer, length);
        }
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_returned != 0, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)length);
            return new MemoryHandle((byte*)pointer + elementIndex);
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                driver.Return(page);
            }
        }
    }
}
