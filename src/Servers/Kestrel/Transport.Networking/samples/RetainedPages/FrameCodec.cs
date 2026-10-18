// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;

internal static class FrameCodec
{
    internal const int HeaderLength = 12;
    private const int Magic = 0x31474150;

    internal readonly record struct Header(int PayloadLength, int RequestId);

    internal static bool TryReadHeader(in ReadOnlySequence<byte> input, out Header header)
    {
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadLittleEndian(out int magic) || !reader.TryReadLittleEndian(out int length)
            || !reader.TryReadLittleEndian(out int requestId))
        {
            header = default;
            return false;
        }
        if (magic != Magic || length < 0)
        {
            throw new InvalidDataException("Invalid demo message framing.");
        }
        header = new Header(length, requestId);
        return true;
    }

    internal static void WriteHeader(Span<byte> output, Header header)
    {
        BinaryPrimitives.WriteInt32LittleEndian(output, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(output[4..], header.PayloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(output[8..], header.RequestId);
    }
}
