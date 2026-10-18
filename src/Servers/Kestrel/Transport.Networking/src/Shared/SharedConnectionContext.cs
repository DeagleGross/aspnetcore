// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Transport;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using EpollWorker = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll.EpollWorker;
using EpollState = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll.ConnectionState;
using RingEngine = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.Engine;
using RingState = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.RingConnection;
using RingNative = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.Native;
using WindowsWorker = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.WindowsWorker;
using WindowsState = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.ConnectionState;
using WindowsPool = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.NativeMemoryPool;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal sealed class SharedConnectionContext : DefaultConnectionContext, IProviderConnection
{
    private readonly Driver _driver;
    internal ProviderNetworkConnection Network { get; }
    public ConnectionContext Context => this;

    private SharedConnectionContext(string id, IPEndPoint endpoint, bool tls, Driver driver, IPEndPoint? remote = null) : base(id)
    {
        _driver = driver;
        LocalEndPoint = endpoint;
        RemoteEndPoint = remote ?? new IPEndPoint(IPAddress.Loopback, 0);
        Network = new ProviderNetworkConnection(this, driver, id, LocalEndPoint, RemoteEndPoint, tls);
        ConnectionClosed = Network.ConnectionClosed;
        if (tls && driver is not WindowsDriver)
        {
            var feature = new FixedTlsFeature();
            Features.Set<ITlsConnectionFeature>(feature);
            Features.Set<ITlsHandshakeFeature>(feature);
        }
    }

    internal static SharedConnectionContext ForEpoll(EpollWorker worker, EpollState state, IPEndPoint endpoint)
    {
        return new SharedConnectionContext($"owned-shared-epoll-{state.Id:x}", endpoint, worker.Tls, new EpollDriver(worker, state));
    }

    internal static SharedConnectionContext ForRing(RingEngine engine, RingState state, IPEndPoint endpoint)
    {
        return new SharedConnectionContext($"owned-shared-uring-{state.Id:x}", endpoint, engine.Tls, new RingDriver(engine, state));
    }

    internal static SharedConnectionContext ForWindows(WindowsWorker worker, WindowsState state)
    {
        return new SharedConnectionContext($"owned-shared-{worker.Backend}-{worker.Index}-{state.Id:x}",
            state.LocalEndPoint, state.HasTls, new WindowsDriver(worker, state), state.RemoteEndPoint);
    }

    internal ConnectionContext AttachKestrel()
    {
        Transport = Network.AsDuplexPipe();
        return this;
    }

    internal void Receive(Memory<byte> memory, int length, int page)
    {
        Network.Receive(new ReturnedMemory(memory, _driver, page), length, NetworkMemoryKind.Pinned);
    }

    internal unsafe void Receive(nint pointer, int length, int page)
    {
        Network.Receive(new ReturnedNativeMemory(pointer, length, _driver, page), length, NetworkMemoryKind.Pinned);
    }

    internal void Receive(IMemoryOwner<byte> owner, int length, NetworkMemoryKind kind)
    {
        Network.Receive(new ReturnedOwnedMemory(owner, _driver), length, kind);
    }

    public void Sent(int count, Exception? error = null)
    {
        _driver.Sent();
        Network.Sent(count, error);
    }
    public void Closed(Exception? error) => Network.Closed(error);
    public void EndReceive(Exception? error) => Network.EndReceive(error);
    public void PumpOutput() => Network.Pump();
    public override void Abort(ConnectionAbortedException abortReason) => Network.Abort(abortReason);
    public override ValueTask DisposeAsync() => Network.DisposeAsync();
    private ValueTask DisposeContextAsync() => base.DisposeAsync();

    internal sealed class ProviderNetworkConnection : NetworkConnection
    {
        private readonly SharedConnectionContext _context;
        private readonly Driver _driver;
        private bool _completingSend;

        internal ProviderNetworkConnection(SharedConnectionContext context, Driver driver, string id, EndPoint? local, EndPoint? remote, bool tls)
            : base(id, local, remote, driver.OutputPool, driver.OutputKind, tls)
        {
            _context = context;
            _driver = driver;
        }

        internal ConnectionContext AttachKestrel() => _context.AttachKestrel();
        internal void Receive(IMemoryOwner<byte> owner, int count, NetworkMemoryKind kind) => PublishReceiveCore(owner, count, kind);
        internal void Sent(int count, Exception? error)
        {
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
        internal void Closed(Exception? error) => CompleteCloseCore(error);
        internal void EndReceive(Exception? error) => EndReceiveCore(error);
        internal void Pump() => PumpOutputCore();
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
            return _context.DisposeContextAsync();
        }
    }

    internal abstract class Driver
    {
        internal virtual MemoryPool<byte>? OutputPool => null;
        internal virtual NetworkMemoryKind OutputKind => NetworkMemoryKind.Managed;
        internal virtual bool CanCompleteInline => false;
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
        private IMemoryOwner<byte>? _staging;
        internal override MemoryPool<byte> OutputPool => worker.OutputPool;
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

    private sealed class FixedTlsFeature : ITlsConnectionFeature, ITlsHandshakeFeature
    {
        public X509Certificate2? ClientCertificate { get; set; }
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
        public SslProtocols Protocol => SslProtocols.Tls12;
        public TlsCipherSuite? NegotiatedCipherSuite => TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256;
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes128;
        public int CipherStrength => 128;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256;
        public int HashStrength => 256;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.DiffieHellman;
        public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
        public string HostName => string.Empty;
    }
}
