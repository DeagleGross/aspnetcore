// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

internal sealed class NativeSendObserver : ILoggerFactory, ILogger
{
    private readonly Lock _gate = new();
    private readonly List<(long Address, int Length)> _expected = new();
    private readonly List<(long Address, int Length)> _submitted = new();

    internal unsafe void Expect(ReadOnlyMemory<byte> memory)
    {
        using var pin = memory.Pin();
        _expected.Add(((long)pin.Pointer, memory.Length));
    }

    internal void Verify()
    {
        lock (_gate)
        {
            if (_expected.Count < 2)
            {
                throw new InvalidOperationException("The demo did not exercise a multi-page message.");
            }
            foreach (var expected in _expected)
            {
                var end = expected.Address + expected.Length;
                var cursor = expected.Address;
                foreach (var actual in _submitted.OrderBy(item => item.Address))
                {
                    if (actual.Address >= expected.Address && actual.Address <= cursor && actual.Address + actual.Length <= end)
                    {
                        cursor = Math.Max(cursor, actual.Address + actual.Length);
                    }
                }
                if (cursor != end)
                {
                    throw new InvalidOperationException("The actual native send did not use the original retained payload address.");
                }
                Console.WriteLine($"SERVER native send preserved page address=0x{expected.Address:x}, length={expected.Length}.");
            }
            Console.WriteLine($"PASS: {_expected.Sum(item => item.Length)} payload bytes submitted from the original receive-page addresses; no payload staging copy.");
        }
    }

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("The demo uses one fixed native-send capture logger.");
    public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Trace or >= LogLevel.Error;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Dispose() { }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (eventId.Id == 1010 && state is IEnumerable<KeyValuePair<string, object?>> fields)
        {
            long? address = null;
            int? length = null;
            foreach (var field in fields)
            {
                if (field.Key == "Address" && field.Value is long value)
                {
                    address = value;
                }
                else if (field.Key == "Length" && field.Value is int count)
                {
                    length = count;
                }
            }
            if (address is null || length is null || length <= 0)
            {
                throw new InvalidDataException("The native send trace omitted its address/length.");
            }
            lock (_gate)
            {
                _submitted.Add((address.Value, length.Value));
            }
        }
        if (logLevel >= LogLevel.Error)
        {
            Console.Error.WriteLine(formatter(state, exception));
        }
    }
}
