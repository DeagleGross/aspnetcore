// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks.Sources;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// A reusable awaitable that avoids allocating TaskCompletionSource for each async operation.
/// Uses ManualResetValueTaskSourceCore for zero-allocation async patterns.
///
/// Lock-free design using Interlocked.CompareExchange on an int state flag.
/// This is critical because RunContinuationsAsynchronously = false (inline continuations),
/// which means SetResult runs the continuation on the calling thread. If we held a lock
/// during SetResult, the inline continuation calling Reset() would deadlock.
///
/// Inline continuations eliminate ThreadPool dispatch overhead — the pump thread runs
/// the continuation directly, matching nginx's single-thread-per-worker model.
/// </summary>
internal sealed class SslAwaitable<T> : IValueTaskSource<T>
{
    private ManualResetValueTaskSourceCore<T> _source;

    // 0 = inactive, 1 = active (waiting for result)
    private volatile int _state;

    public SslAwaitable()
    {
        // RunContinuationsAsynchronously = false: continuations run inline on the thread
        // that calls TrySetResult/TrySetException. This is the pump thread, so the
        // receive/send loop resumes directly on the pump without a ThreadPool hop.
        _source.RunContinuationsAsynchronously = false;
    }

    /// <summary>
    /// Returns true if this awaitable is currently waiting for a result.
    /// </summary>
    public bool IsActive => _state == 1;

    /// <summary>
    /// Prepares the awaitable for a new async wait and returns a ValueTask to await.
    /// </summary>
    public ValueTask<T> Reset()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("SslAwaitable is already active");
        }

        _source.Reset();
        return new ValueTask<T>(this, _source.Version);
    }

    /// <summary>
    /// Completes the awaitable with a successful result.
    /// Thread-safe: first caller wins, subsequent calls return false.
    /// Note: with inline continuations, the awaiter's continuation runs HERE on this thread.
    /// </summary>
    public bool TrySetResult(T result)
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
        {
            return false;
        }

        // State is now 0 (inactive) BEFORE SetResult runs the continuation inline.
        // This allows the continuation to safely call Reset() without deadlock.
        _source.SetResult(result);
        return true;
    }

    /// <summary>
    /// Completes the awaitable with an exception.
    /// Thread-safe: first caller wins, subsequent calls return false.
    /// </summary>
    public bool TrySetException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
        {
            return false;
        }

        _source.SetException(exception);
        return true;
    }

    /// <summary>
    /// Cancels the awaitable.
    /// Thread-safe: first caller wins, subsequent calls return false.
    /// </summary>
    public bool TrySetCanceled()
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
        {
            return false;
        }

        _source.SetException(new OperationCanceledException());
        return true;
    }

    // IValueTaskSource<T> implementation
    public T GetResult(short token)
    {
        return _source.GetResult(token);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        return _source.GetStatus(token);
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _source.OnCompleted(continuation, state, token, flags);
    }
}