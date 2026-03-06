// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.HPack;
using System.Net.Http.QPack;
using System.Text;
using Xunit;
using HeaderField = System.Net.Http.QPack.HeaderField;
#if KESTREL
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
#endif

namespace System.Net.Http.Unit.Tests.QPack
{
    public class QPackDecoderTests : IDisposable
    {
        private const int MaxHeaderFieldSize = 8192;

        // 4.5.2 - Indexed Field Line - Static Table - Index 25 (:method: GET)
        private static readonly byte[] _indexedFieldLineStatic = new byte[] { 0xd1 };

        // 4.5.4 - Literal Header Field With Name Reference - Static Table - Index 44 (content-type)
        private static readonly byte[] _literalHeaderFieldWithNameReferenceStatic = new byte[] { 0x5f, 0x1d };

        // 4.5.6 - Literal Field Line With Literal Name - (literal-header-field)
        private static readonly byte[] _literalFieldLineWithLiteralName = new byte[] { 0x37, 0x0d, 0x6c, 0x69, 0x74, 0x65, 0x72, 0x61, 0x6c, 0x2d, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72, 0x2d, 0x66, 0x69, 0x65, 0x6c, 0x64 };

        private const string _contentTypeString = "content-type";
        private const string _literalHeaderFieldString = "literal-header-field";

        // n     e     w       -      h     e     a     d     e     r      *
        // 10101000 10111110 00010110 10011100 10100011 10010000 10110110 01111111
        private static readonly byte[] _headerNameHuffmanBytes = new byte[] { 0xa8, 0xbe, 0x16, 0x9c, 0xa3, 0x90, 0xb6, 0x7f };

        private const string _headerNameString = "new-header";
        private const string _headerValueString = "value";

        private static readonly byte[] _headerValueBytes = Encoding.ASCII.GetBytes(_headerValueString);

        // v      a     l      u      e    *
        // 11101110 00111010 00101101 00101111
        private static readonly byte[] _headerValueHuffmanBytes = new byte[] { 0xee, 0x3a, 0x2d, 0x2f };

        private static readonly byte[] _headerNameHuffman = new byte[] { 0x3f, 0x01 }
            .Concat(_headerNameHuffmanBytes)
            .ToArray();

        private static readonly byte[] _headerValue = new byte[] { (byte)_headerValueBytes.Length }
            .Concat(_headerValueBytes)
            .ToArray();

        private static readonly byte[] _headerValueHuffman = new byte[] { (byte)(0x80 | _headerValueHuffmanBytes.Length) }
            .Concat(_headerValueHuffmanBytes)
            .ToArray();

        private readonly QPackDecoder _decoder;
        private readonly TestHttpHeadersHandler _handler = new TestHttpHeadersHandler();

        public QPackDecoderTests()
        {
            _decoder = new QPackDecoder(MaxHeaderFieldSize);
        }

        public void Dispose()
        {
            _decoder.Dispose();
        }

        [Fact]
        public void DecodesIndexedHeaderField_StaticTableWithValue()
        {
            _decoder.Decode(new byte[] { 0, 0 }, endHeaders: false, handler: _handler);
            _decoder.Decode(_indexedFieldLineStatic, endHeaders: true, handler: _handler);
            Assert.Equal("GET", _handler.DecodedHeaders[":method"]);

            Assert.Equal(":method", _handler.DecodedStaticHeaders[H3StaticTable.MethodGet].Key);
            Assert.Equal("GET", _handler.DecodedStaticHeaders[H3StaticTable.MethodGet].Value);
        }

        [Fact]
        public void DecodesIndexedHeaderField_StaticTableLiteralValue()
        {
            byte[] encoded = _literalHeaderFieldWithNameReferenceStatic
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0, 0 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded, endHeaders: true, handler: _handler);
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_contentTypeString]);

            Assert.Equal(_contentTypeString, _handler.DecodedStaticHeaders[H3StaticTable.ContentTypeApplicationDnsMessage].Key);
            Assert.Equal(_headerValueString, _handler.DecodedStaticHeaders[H3StaticTable.ContentTypeApplicationDnsMessage].Value);
        }

        [Fact]
        public void DecodesLiteralFieldLineWithLiteralName_Value()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            TestDecodeWithoutIndexing(encoded, _literalHeaderFieldString, _headerValueString);
        }

        [Fact]
        public void DecodesAuthority_Value()
        {
            byte[] encoded = Convert.FromBase64String("AADR11AOMTI3LjAuMC4xOjUwMDHBNwFhbHQtdXNlZA4xMjcuMC4wLjE6NTAwMQ==");

            KeyValuePair<string, string>[] expectedValues = new[]
            {
                new KeyValuePair<string, string>(":method", "GET"),
                new KeyValuePair<string, string>(":scheme", "https"),
                new KeyValuePair<string, string>(":authority", "127.0.0.1:5001"),
                new KeyValuePair<string, string>(":path", "/"),
                new KeyValuePair<string, string>("alt-used", "127.0.0.1:5001"),
            };

            TestDecodeWithoutIndexing(encoded[2..], expectedValues);
        }

        [Fact]
        public void DecodesAuthority_Empty()
        {
            byte[] encoded = Convert.FromBase64String("AAA3ADptZXRob2QDR0VUNTpwYXRoAS83ADpzY2hlbWUEaHR0cDcDOmF1dGhvcml0eQA=");

            KeyValuePair<string, string>[] expectedValues = new[]
            {
                new KeyValuePair<string, string>(":method", "GET"),
                new KeyValuePair<string, string>(":scheme", "http"),
                new KeyValuePair<string, string>(":authority", ""),
                new KeyValuePair<string, string>(":path", "/"),
            };

            TestDecodeWithoutIndexing(encoded[2..], expectedValues);
        }

        [Fact]
        public void DecodesLiteralFieldLineWithLiteralName_HuffmanEncodedValue()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValueHuffman)
                .ToArray();

            TestDecodeWithoutIndexing(encoded, _literalHeaderFieldString, _headerValueString);
        }

        [Fact]
        public void DecodesLiteralFieldLineWithLiteralName_HuffmanEncodedName()
        {
            byte[] encoded = _headerNameHuffman
                .Concat(_headerValue)
                .ToArray();

            TestDecodeWithoutIndexing(encoded, _headerNameString, _headerValueString);
        }

        [Fact]
        public void DecodesLiteralFieldLineWithLiteralName_LargeValues()
        {
            int length = 0;
            Span<byte> buffer = new byte[1024 * 1024];
            QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(":method", new string('A', 8192 / 2), buffer.Slice(length), out int bytesWritten);
            length += bytesWritten;
            QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(":path", new string('A', 8192 / 2), buffer.Slice(length), out bytesWritten);
            length += bytesWritten;
            QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReference(":scheme", "http", buffer.Slice(length), out bytesWritten);
            length += bytesWritten;

            TestDecodeWithoutIndexing(buffer.Slice(0, length).ToArray(), new[]
            {
                new KeyValuePair<string, string>(":method", new string('A', 8192 / 2)),
                new KeyValuePair<string, string>(":path", new string('A', 8192 / 2)),
                new KeyValuePair<string, string>(":scheme", "http")
            });
        }

        [Fact]
        public void LiteralFieldWithoutNameReference_SingleBuffer()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded, endHeaders: true, handler: _handler);

            Assert.Single(_handler.DecodedHeaders);
            Assert.True(_handler.DecodedHeaders.ContainsKey(_literalHeaderFieldString));
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_literalHeaderFieldString]);
        }

        [Fact]
        public void LiteralFieldWithoutNameReference_NameLengthBrokenIntoSeparateBuffers()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[..1], endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[1..], endHeaders: true, handler: _handler);

            Assert.Single(_handler.DecodedHeaders);
            Assert.True(_handler.DecodedHeaders.ContainsKey(_literalHeaderFieldString));
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_literalHeaderFieldString]);
        }

        [Fact]
        public void LiteralFieldWithoutNameReference_NameBrokenIntoSeparateBuffers()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[..(_literalHeaderFieldString.Length / 2)], endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[(_literalHeaderFieldString.Length / 2)..], endHeaders: true, handler: _handler);

            Assert.Single(_handler.DecodedHeaders);
            Assert.True(_handler.DecodedHeaders.ContainsKey(_literalHeaderFieldString));
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_literalHeaderFieldString]);
        }

        [Fact]
        public void LiteralFieldWithoutNameReference_NameAndValueBrokenIntoSeparateBuffers()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[..^_headerValue.Length], endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[^_headerValue.Length..], endHeaders: true, handler: _handler);

            Assert.Single(_handler.DecodedHeaders);
            Assert.True(_handler.DecodedHeaders.ContainsKey(_literalHeaderFieldString));
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_literalHeaderFieldString]);
        }

        [Fact]
        public void LiteralFieldWithoutNameReference_ValueLengthBrokenIntoSeparateBuffers()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[..^(_headerValue.Length - 1)], endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[^(_headerValue.Length - 1)..], endHeaders: true, handler: _handler);

            Assert.Single(_handler.DecodedHeaders);
            Assert.True(_handler.DecodedHeaders.ContainsKey(_literalHeaderFieldString));
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_literalHeaderFieldString]);
        }

        [Fact]
        public void LiteralFieldWithoutNameReference_ValueBrokenIntoSeparateBuffers()
        {
            byte[] encoded = _literalFieldLineWithLiteralName
                .Concat(_headerValue)
                .ToArray();

            _decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[..^(_headerValueString.Length / 2)], endHeaders: false, handler: _handler);
            _decoder.Decode(encoded[^(_headerValueString.Length / 2)..], endHeaders: true, handler: _handler);

            Assert.Single(_handler.DecodedHeaders);
            Assert.True(_handler.DecodedHeaders.ContainsKey(_literalHeaderFieldString));
            Assert.Equal(_headerValueString, _handler.DecodedHeaders[_literalHeaderFieldString]);
        }

        public static readonly TheoryData<byte[]> _incompleteHeaderBlockData = new TheoryData<byte[]>
        {
            // Incomplete header
            new byte[] { },
            new byte[] { 0x00 },

            // 4.5.4 - Literal Header Field With Name Reference - Static Table - Index 44 (content-type)
            new byte[] { 0x00, 0x00, 0x5f },

            // 4.5.6 - Literal Field Line With Literal Name - (translate)
            new byte[] { 0x00, 0x00, 0x37 },
            new byte[] { 0x00, 0x00, 0x37, 0x02 },
            new byte[] { 0x00, 0x00, 0x37, 0x02, 0x74, 0x72, 0x61, 0x6e, 0x73, 0x6c, 0x61, 0x74 },
        };

        [Theory]
        [MemberData(nameof(_incompleteHeaderBlockData))]
        public void DecodesIncompleteHeaderBlock_Error(byte[] encoded)
        {
            QPackDecodingException exception = Assert.Throws<QPackDecodingException>(() => _decoder.Decode(encoded, endHeaders: true, handler: _handler));
            Assert.Equal(SR.net_http_hpack_incomplete_header_block, exception.Message);
            Assert.Empty(_handler.DecodedHeaders);
        }

        [Fact]
        public void HuffmanDecodedHeaderName_ExceedsLimit_Throws()
        {
            // Use '0' (ASCII 48) which has a 5-bit Huffman code.
            // This means N wire bytes decode to floor(N*8/5) characters.
            // Choose a wire size under the limit that decodes to a size over the limit.
            // With MaxHeaderFieldSize = 8192:
            //   Wire size = 5765 bytes -> decodes to 9224 chars (5765*8/5 = 9224) > 8192
            int decodedLength = MaxHeaderFieldSize + 1032; // 9224
            int wireLength = (decodedLength * 5 + 7) / 8; // 5765 bytes

            Assert.True(wireLength <= MaxHeaderFieldSize, "Wire length must be under the limit for this test.");
            Assert.True(decodedLength > MaxHeaderFieldSize, "Decoded length must exceed the limit.");

            byte[] huffmanEncoded = HuffmanEncode(new string('0', decodedLength));
            Assert.Equal(wireLength, huffmanEncoded.Length);

            // 4.5.6 - Literal Field Line With Literal Name
            // First byte: 0b00101xxx -> 001 prefix, 0 N, 1 H (Huffman), xxx = 3-bit name length prefix
            byte[] nameLengthPrefix = EncodeQPackHuffmanNameLength(wireLength);
            byte[] encoded = nameLengthPrefix
                .Concat(huffmanEncoded)
                .Concat(new byte[] { 0x05 }) // short non-Huffman value "value"
                .Concat(_headerValueBytes)
                .ToArray();

            using QPackDecoder decoder = new QPackDecoder(MaxHeaderFieldSize);
            decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);

            QPackDecodingException exception = Assert.Throws<QPackDecodingException>(() =>
                decoder.Decode(encoded, endHeaders: true, handler: _handler));
            Assert.Equal(SR.Format(SR.net_http_headers_exceeded_length, MaxHeaderFieldSize), exception.Message);
            Assert.Empty(_handler.DecodedHeaders);
        }

        [Fact]
        public void HuffmanDecodedHeaderValue_ExceedsLimit_Throws()
        {
            int decodedLength = MaxHeaderFieldSize + 1032; // 9224
            int wireLength = (decodedLength * 5 + 7) / 8; // 5765 bytes

            Assert.True(wireLength <= MaxHeaderFieldSize, "Wire length must be under the limit for this test.");
            Assert.True(decodedLength > MaxHeaderFieldSize, "Decoded length must exceed the limit.");

            byte[] huffmanEncoded = HuffmanEncode(new string('0', decodedLength));
            Assert.Equal(wireLength, huffmanEncoded.Length);

            // 4.5.4 - Literal Header Field With Name Reference - Static Table - Index 44 (content-type)
            byte[] valueLengthPrefix = EncodeHuffmanStringLength(wireLength);
            byte[] encoded = _literalHeaderFieldWithNameReferenceStatic
                .Concat(valueLengthPrefix)
                .Concat(huffmanEncoded)
                .ToArray();

            using QPackDecoder decoder = new QPackDecoder(MaxHeaderFieldSize);
            decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: _handler);

            QPackDecodingException exception = Assert.Throws<QPackDecodingException>(() =>
                decoder.Decode(encoded, endHeaders: true, handler: _handler));
            Assert.Equal(SR.Format(SR.net_http_headers_exceeded_length, MaxHeaderFieldSize), exception.Message);
            Assert.Empty(_handler.DecodedHeaders);
        }

        [Fact]
        public void HuffmanDecodedHeaderName_UnderLimit_Succeeds()
        {
            // Verify that a Huffman-encoded header name that decodes to exactly the limit still works.
            int decodedLength = MaxHeaderFieldSize;
            int wireLength = (decodedLength * 5 + 7) / 8;

            byte[] huffmanEncoded = HuffmanEncode(new string('0', decodedLength));

            byte[] nameLengthPrefix = EncodeQPackHuffmanNameLength(wireLength);
            byte[] encoded = nameLengthPrefix
                .Concat(huffmanEncoded)
                .Concat(new byte[] { 0x05 })
                .Concat(_headerValueBytes)
                .ToArray();

            using QPackDecoder decoder = new QPackDecoder(MaxHeaderFieldSize);
            TestHttpHeadersHandler handler = new TestHttpHeadersHandler();
            decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: handler);
            decoder.Decode(encoded, endHeaders: true, handler: handler);

            Assert.Single(handler.DecodedHeaders);
        }

        /// <summary>
        /// Huffman-encodes a string of ASCII characters using the HPACK/QPACK Huffman table.
        /// Only the raw Huffman-coded bytes are returned (no length prefix).
        /// </summary>
        private static byte[] HuffmanEncode(string input)
        {
            long totalBits = 0;
            foreach (char c in input)
            {
                (_, int bitLength) = Huffman.Encode(c);
                totalBits += bitLength;
            }

            int byteLength = (int)((totalBits + 7) / 8);
            byte[] result = new byte[byteLength];

            int bitsLeft = 8;
            int currentIndex = 0;

            foreach (char c in input)
            {
                (uint code, int bitLength) = Huffman.Encode(c);

                int remaining = bitLength;
                while (remaining > 0)
                {
                    if (remaining >= bitsLeft)
                    {
                        result[currentIndex] |= (byte)(code >> (32 - bitsLeft));
                        code <<= bitsLeft;
                        remaining -= bitsLeft;
                        bitsLeft = 8;
                        currentIndex++;
                    }
                    else
                    {
                        result[currentIndex] |= (byte)(code >> (32 - bitsLeft));
                        bitsLeft -= remaining;
                        remaining = 0;
                    }
                }
            }

            // Pad with EOS (all 1s) per RFC 7541 section 5.2
            if (bitsLeft < 8)
            {
                result[currentIndex] |= (byte)(0xFF >> (8 - bitsLeft));
            }

            return result;
        }

        /// <summary>
        /// Encodes a Huffman string length as a QPACK 7-bit prefixed integer with the H (Huffman) bit set.
        /// Used for header value lengths.
        /// </summary>
        private static byte[] EncodeHuffmanStringLength(int length)
        {
            byte[] buffer = new byte[IntegerEncoder.MaxInt32EncodedLength];
            buffer[0] = 0x80; // Set H bit for Huffman encoding
            bool success = IntegerEncoder.Encode(length, 7, buffer, out int bytesWritten);
            Debug.Assert(success);
            return buffer.Take(bytesWritten).ToArray();
        }

        /// <summary>
        /// Encodes a QPACK Literal Field Line With Literal Name first byte + name length.
        /// Format: 0b001NH_LLL where N=0, H=1 (Huffman), LLL = 3-bit prefix for name length.
        /// </summary>
        private static byte[] EncodeQPackHuffmanNameLength(int length)
        {
            byte[] buffer = new byte[IntegerEncoder.MaxInt32EncodedLength];
            // 0b00101000 = 0x28: 001 prefix, 0 N, 1 H (Huffman), 000 start of 3-bit length
            buffer[0] = 0x28;
            bool success = IntegerEncoder.Encode(length, 3, buffer, out int bytesWritten);
            Debug.Assert(success);
            return buffer.Take(bytesWritten).ToArray();
        }

        private static void TestDecodeWithoutIndexing(byte[] encoded, string expectedHeaderName, string expectedHeaderValue)
        {
            KeyValuePair<string, string>[] expectedValues = new[] { new KeyValuePair<string, string>(expectedHeaderName, expectedHeaderValue) };

            TestDecodeWithoutIndexing(encoded, expectedValues);
        }

        private static void TestDecodeWithoutIndexing(byte[] encoded, KeyValuePair<string, string>[] expectedValues)
        {
            TestDecode(encoded, expectedValues, expectDynamicTableEntry: false, bytesAtATime: null);

            for (int i = 1; i <= 20; i++)
            {
                try
                {
                    TestDecode(encoded, expectedValues, expectDynamicTableEntry: false, bytesAtATime: i);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error when decoding with chunk size {i}.", ex);
                }
            }
        }

        private static void TestDecode(byte[] encoded, KeyValuePair<string, string>[] expectedValues, bool expectDynamicTableEntry, int? bytesAtATime)
        {
            using QPackDecoder decoder = new QPackDecoder(MaxHeaderFieldSize);
            TestHttpHeadersHandler handler = new TestHttpHeadersHandler();

            // Read past header
            decoder.Decode(new byte[] { 0x00, 0x00 }, endHeaders: false, handler: handler);

            if (bytesAtATime == null)
            {
                decoder.Decode(encoded, endHeaders: true, handler: handler);
            }
            else
            {
                int chunkSize = bytesAtATime.Value;

                // Parse data in chunks, separated by empty chunks
                for (int i = 0; i < encoded.Length; i += chunkSize)
                {
                    int resolvedSize = Math.Min(encoded.Length - i, chunkSize);
                    bool end = i + resolvedSize == encoded.Length;

                    Span<byte> chunk = encoded.AsSpan(i, resolvedSize);

                    decoder.Decode(Array.Empty<byte>(), endHeaders: false, handler: handler);
                    decoder.Decode(chunk, endHeaders: end, handler: handler);
                }
            }

            foreach (KeyValuePair<string, string> expectedValue in expectedValues)
            {
                try
                {
                    Assert.Equal(expectedValue.Value, handler.DecodedHeaders[expectedValue.Key]);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Error when checking header '{expectedValue.Key}'.", ex);
                }
            }
        }
    }

    public class TestHttpHeadersHandler : IHttpStreamHeadersHandler
    {
        public Dictionary<string, string> DecodedHeaders { get; } = new Dictionary<string, string>();
        public Dictionary<int, KeyValuePair<string, string>> DecodedStaticHeaders { get; } = new Dictionary<int, KeyValuePair<string, string>>();

        void IHttpStreamHeadersHandler.OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            if (name.Length == 0)
            {
                throw new InvalidOperationException("Header with length zero.");
            }

            string headerName = Encoding.ASCII.GetString(name);
            string headerValue = Encoding.ASCII.GetString(value);

            DecodedHeaders[headerName] = headerValue;
        }

        void IHttpStreamHeadersHandler.OnStaticIndexedHeader(int index)
        {
            ref readonly HeaderField entry = ref H3StaticTable.Get(index);
            ((IHttpStreamHeadersHandler)this).OnHeader(entry.Name, entry.Value);
            DecodedStaticHeaders[index] = new KeyValuePair<string, string>(Encoding.ASCII.GetString(entry.Name), Encoding.ASCII.GetString(entry.Value));
        }

        void IHttpStreamHeadersHandler.OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value)
        {
            byte[] name = H3StaticTable.Get(index).Name;
            ((IHttpStreamHeadersHandler)this).OnHeader(name, value);
            DecodedStaticHeaders[index] = new KeyValuePair<string, string>(Encoding.ASCII.GetString(name), Encoding.ASCII.GetString(value));
        }

        void IHttpStreamHeadersHandler.OnHeadersComplete(bool endStream) { }

        public void OnDynamicIndexedHeader(int? index, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            string headerName = Encoding.ASCII.GetString(name);
            string headerValue = Encoding.ASCII.GetString(value);

            DecodedHeaders[headerName] = headerValue;
        }
    }
}
