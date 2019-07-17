// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Linq;

namespace hpack_encoder
{
    class Program
    {
        static void Main(string[] args)
        {
            RunTests();
        }

        static void RunTests()
        {
            Test("literal header field with indexing",
                new byte[] { 0x40, 0x0a, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x6b, 0x65, 0x79, 0x0d, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72 },
                buffer =>
                {
                    int bytes = HPackEncoder.EncodeHeader("custom-key", "custom-header", HPackFlags.NewIndexed, buffer);
                    return bytes;
                });

            Test("literal header field without indexing",
                new byte[] { 0x04, 0x0c, 0x2f, 0x73, 0x61, 0x6d, 0x70, 0x6c, 0x65, 0x2f, 0x70, 0x61, 0x74, 0x68 },
                buffer =>
                {
                    int bytes = HPackEncoder.EncodeHeader(HPackEncoder.GetStaticIndex(":path"), "/sample/path", HPackFlags.WithoutIndexing, buffer);
                    return bytes;
                });

            Test("literal header never indexed",
                new byte[] { 0x10, 0x08, 0x70, 0x61, 0x73, 0x73, 0x77, 0x6f, 0x72, 0x64, 0x06, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74 },
                buffer =>
                {
                    int bytes = HPackEncoder.EncodeHeader("password", "secret", HPackFlags.NeverIndexed, buffer);
                    return bytes;
                });

            Test("indexed header field using dynamic table",
                new byte[]
                {
                    // Newly indexed literal header.
                    0x40, 0x0a, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x6b, 0x65, 0x79, 0x0d, 0x63, 0x75, 0x73, 0x74, 0x6f, 0x6d, 0x2d, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72,
                    // Indexed header using dynamic table for full name/value.
                    0xBE,
                    // Header using dynamic table for just name, with a literal value.
                    0x0F, 0x2F, 0x0A, 0x6E, 0x65, 0x77, 0x2D, 0x68, 0x65, 0x61, 0x64, 0x65, 0x72
                },
                buffer =>
                {
                    var encoder = new HPackEncoder(4096);

                    int bytes = encoder.Encode("custom-key", "custom-header", HPackFlags.NewIndexed, buffer);
                    bytes += encoder.Encode("custom-key", "custom-header", HPackFlags.WithoutIndexing, buffer.Slice(bytes));
                    bytes += encoder.Encode("custom-key", "new-header", HPackFlags.None, buffer.Slice(bytes));

                    return bytes;
                });
        }

        delegate int EncodeDelegate(Span<byte> buffer);

        static void Test(string name, byte[] expectedValue, EncodeDelegate doEncode)
        {
            Span<byte> buffer = new byte[4096];
            int len = doEncode(buffer);
            buffer = buffer.Slice(0, len);

            string success = buffer.SequenceEqual(expectedValue) ? "success" : "fail";

            Console.WriteLine($"{name}: {success}");
            Console.WriteLine($"  got:      {Encode(buffer)}");
            Console.WriteLine($"  expected: {Encode(expectedValue)}");
            Console.WriteLine();
        }

        static string Encode(ReadOnlySpan<byte> span)
        {
            StringBuilder sb = new StringBuilder(span.Length * 2);
            for (int i = 0; i < span.Length; ++i)
            {
                sb.AppendFormat(span[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }

    public sealed class HPackEncoder
    {
        private const HPackFlags IndexingMask = HPackFlags.WithoutIndexing | HPackFlags.NewIndexed | HPackFlags.NeverIndexed;

        private Dictionary<TableEntry, HashSet<int>> _dynamicTableMap = new Dictionary<TableEntry, HashSet<int>>();
        private TableEntry[] _dynamicTable = new TableEntry[32];
        private int _dynamicHead, _dynamicCount, _dynamicSize, _dynamicMaxSize;

        public HPackEncoder(int dynamicTableMaxSize)
        {
            _dynamicMaxSize = dynamicTableMaxSize;
        }

        public int Encode(string name, string value, HPackFlags flags, Span<byte> headerBlock)
        {
            int index = 0;

            switch (flags & IndexingMask)
            {
                case HPackFlags.WithoutIndexing:
                    if (TryGetIndex(name, value, out index))
                    {
                        return EncodeHeader(index, headerBlock);
                    }

                    if (TryGetIndex(name, out index))
                    {
                        name = null;
                    }
                    break;
                case HPackFlags.NewIndexed:
                    TableEntry newEntry = new TableEntry(name, value);

                    if (TryGetIndex(name, out index))
                    {
                        name = null;
                    }

                    AddDynamicEntry(newEntry);
                    break;
            }

            return EncodeHeaderImpl(index, name, value, flags, headerBlock);
        }

        public int EncodeNewDynamicTableSize(int size, Span<byte> headerBlock)
        {
            if (size != _dynamicMaxSize)
            {
                _dynamicMaxSize = size;

                if ((_dynamicMaxSize - _dynamicSize) < 0)
                {
                    EnsureDynamicSpaceAvailable(0);
                }
            }

            return EncodeDynamicTableSizeUpdate(size, headerBlock);
        }

        private bool TryGetIndex(string name, out int index)
        {
            if (TryGetStaticIndex(name, out index))
            {
                return true;
            }

            if (!_dynamicTableMap.TryGetValue(new TableEntry(name, ""), out HashSet<int> set))
            {
                index = default;
                return false;
            }

            index = MapDynamicIndex(set.First());
            return true;
        }

        private bool TryGetIndex(string name, string value, out int index)
        {
            if (TryGetStaticIndex(name, value, out index))
            {
                return true;
            }

            if (!_dynamicTableMap.TryGetValue(new TableEntry(name, value), out HashSet<int> set))
            {
                index = default;
                return false;
            }

            index = MapDynamicIndex(set.First());
            return true;
        }

        private int MapDynamicIndex(int arrayIdx)
        {
            arrayIdx -= _dynamicHead;

            if (arrayIdx < 0)
            {
                arrayIdx = _dynamicCount + arrayIdx;
            }

            return arrayIdx + s_staticTable.Length + 1;
        }

        private void AddDynamicEntry(TableEntry entry)
        {
            int entrySize = entry.DynamicSize;

            if ((_dynamicMaxSize - _dynamicSize) < entrySize)
            {
                EnsureDynamicSpaceAvailable(entrySize);
            }

            if (_dynamicCount == _dynamicTable.Length)
            {
                ResizeDynamicTable();
            }

            int insertIndex = (_dynamicHead + _dynamicCount) & (_dynamicTable.Length - 1);
            _dynamicTable[insertIndex] = entry;

            _dynamicSize += entrySize;
            _dynamicHead++;
            _dynamicCount++;

            AddMapping(entry, insertIndex);
        }

        private void ResizeDynamicTable()
        {
            var newEntries = new TableEntry[_dynamicCount * 2];

            int countA = _dynamicCount - _dynamicHead;
            int countB = _dynamicCount - countA;

            Array.Copy(_dynamicTable, _dynamicHead, newEntries, 0, countA);
            Array.Copy(_dynamicTable, 0, newEntries, countA, countB);

            _dynamicTable = newEntries;
            _dynamicHead = 0;

            _dynamicTableMap.Clear();

            for (int i = 0; i < _dynamicCount; ++i)
            {
                AddMapping(_dynamicTable[i], i);
            }
        }

        private void AddMapping(TableEntry entry, int index)
        {
            if (!_dynamicTableMap.TryGetValue(entry, out HashSet<int> set))
            {
                set = new HashSet<int>();
                _dynamicTableMap.Add(entry, set);
            }
            set.Add(index);

            if (entry.Value.Length != 0)
            {
                entry = new TableEntry(entry.Name, "");
                if (!_dynamicTableMap.TryGetValue(entry, out set))
                {
                    set = new HashSet<int>();
                    _dynamicTableMap.Add(entry, set);
                }
                set.Add(index);
            }
        }

        private void EnsureDynamicSpaceAvailable(int size)
        {
            Debug.Assert(size >= 0);
            Debug.Assert((_dynamicMaxSize - _dynamicSize) < size);

            do
            {
                ref TableEntry e = ref _dynamicTable[_dynamicHead];

                HashSet<int> set = _dynamicTableMap[e];
                set.Remove(_dynamicHead);
                if (set.Count == 0) _dynamicTableMap.Remove(e);

                if (e.Value.Length != 0)
                {
                    TableEntry nameEntry = new TableEntry(e.Name, "");
                    set = _dynamicTableMap[nameEntry];
                    set.Remove(_dynamicHead);
                    if (set.Count == 0) _dynamicTableMap.Remove(nameEntry);
                }

                _dynamicSize -= e.DynamicSize;
                e = default;

                _dynamicHead = (_dynamicHead + 1) & (_dynamicTable.Length - 1);
                _dynamicCount--;
            }
            while ((_dynamicMaxSize - _dynamicSize) < size);
        }

        public static int EncodeHeader(int headerIndex, Span<byte> headerBlock)
        {
            return EncodeInteger(headerIndex, 0b10000000, 0b10000000, headerBlock);
        }

        public static int EncodeHeader(int nameIdx, string value, HPackFlags flags, Span<byte> headerBlock)
        {
            return EncodeHeaderImpl(nameIdx, null, value, flags, headerBlock);
        }

        public static int EncodeHeader(string name, string value, HPackFlags flags, Span<byte> headerBlock)
        {
            return EncodeHeaderImpl(0, name, value, flags, headerBlock);
        }

        private static int EncodeHeaderImpl(int nameIdx, string name, string value, HPackFlags flags, Span<byte> headerBlock)
        {
            byte prefix, prefixMask;

            switch (flags & IndexingMask)
            {
                case HPackFlags.WithoutIndexing:
                    prefix = 0;
                    prefixMask = 0b11110000;
                    break;
                case HPackFlags.NewIndexed:
                    prefix = 0b01000000;
                    prefixMask = 0b11000000;
                    break;
                case HPackFlags.NeverIndexed:
                    prefix = 0b00010000;
                    prefixMask = 0b11110000;
                    break;
                default:
                    throw new Exception("invalid indexing flag");
            }

            int bytesGenerated = EncodeInteger(nameIdx, prefix, prefixMask, headerBlock);

            if (name != null)
            {
                bytesGenerated += EncodeString(name, (flags & HPackFlags.HuffmanEncodeName) != 0, headerBlock.Slice(bytesGenerated));
            }

            bytesGenerated += EncodeString(value, (flags & HPackFlags.HuffmanEncodeValue) != 0, headerBlock.Slice(bytesGenerated));
            return bytesGenerated;
        }

        public static int EncodeDynamicTableSizeUpdate(int maximumSize, Span<byte> headerBlock)
        {
            return EncodeInteger(maximumSize, 0b00100000, 0b11100000, headerBlock);
        }

        public static int EncodeString(string value, bool huffmanEncode, Span<byte> headerBlock)
        {
            byte[] data = Encoding.ASCII.GetBytes(value);
            byte prefix;

            if (!huffmanEncode)
            {
                prefix = 0;
            }
            else
            {
                int len = HuffmanEncoder.GetEncodedLength(data);

                byte[] huffmanData = new byte[len];
                HuffmanEncoder.Encode(data, huffmanData);

                data = huffmanData;
                prefix = 0x80;
            }

            int bytesGenerated = 0;

            bytesGenerated += EncodeInteger(data.Length, prefix, 0x80, headerBlock);

            data.AsSpan().CopyTo(headerBlock.Slice(bytesGenerated));
            bytesGenerated += data.Length;

            return bytesGenerated;
        }

        public static int EncodeInteger(int value, byte prefix, byte prefixMask, Span<byte> headerBlock)
        {
            byte prefixLimit = (byte)(~prefixMask);

            if (value < prefixLimit)
            {
                headerBlock[0] = (byte)(prefix | value);
                return 1;
            }

            headerBlock[0] = (byte)(prefix | prefixLimit);
            int bytesGenerated = 1;

            value -= prefixLimit;

            while (value >= 0x80)
            {
                headerBlock[bytesGenerated] = (byte)((value & 0x7F) | 0x80);
                value = value >> 7;
                bytesGenerated++;
            }

            headerBlock[bytesGenerated] = (byte)value;
            bytesGenerated++;

            return bytesGenerated;
        }

        public static int GetStaticIndex(string name)
        {
            if (!TryGetStaticIndex(name, out int staticIndex))
            {
                throw new Exception("header does not exist in static table.");
            }

            return staticIndex;
        }

        public static int GetStaticIndex(string name, string value)
        {
            if (!TryGetStaticIndex(name, value, out int staticIndex))
            {
                throw new Exception("header does not exist in static table.");
            }

            return staticIndex;
        }

        public static bool TryGetStaticIndex(string name, out int staticIndex)
        {
            int idx = Array.BinarySearch(s_staticTable, new TableEntry(name, ""));

            if (idx >= 0)
            {
                staticIndex = idx + 1;
                return true;
            }

            idx = ~idx;

            if (idx < s_staticTable.Length && string.Equals(s_staticTable[idx].Name, name, StringComparison.Ordinal))
            {
                staticIndex = idx + 1;
                return true;
            }

            staticIndex = 0;
            return false;
        }

        public static bool TryGetStaticIndex(string name, string value, out int staticIndex)
        {
            int idx = Array.BinarySearch(s_staticTable, new TableEntry(name, value));

            if (idx >= 0)
            {
                staticIndex = idx + 1;
                return true;
            }

            staticIndex = 0;
            return false;
        }

        private static readonly TableEntry[] s_staticTable = new TableEntry[]
        {
            new TableEntry(":authority", ""),
            new TableEntry(":method", "GET"),
            new TableEntry(":method", "POST"),
            new TableEntry(":path", "/"),
            new TableEntry(":path", "/index.html"),
            new TableEntry(":scheme", "http"),
            new TableEntry(":scheme", "https"),
            new TableEntry(":status", "200"),
            new TableEntry(":status", "204"),
            new TableEntry(":status", "206"),
            new TableEntry(":status", "304"),
            new TableEntry(":status", "400"),
            new TableEntry(":status", "404"),
            new TableEntry(":status", "500"),
            new TableEntry("accept-charset", ""),
            new TableEntry("accept-encoding", "gzip, deflate"),
            new TableEntry("accept-language", ""),
            new TableEntry("accept-ranges", ""),
            new TableEntry("accept", ""),
            new TableEntry("access-control-allow-origin", ""),
            new TableEntry("age", ""),
            new TableEntry("allow", ""),
            new TableEntry("authorization", ""),
            new TableEntry("cache-control", ""),
            new TableEntry("content-disposition", ""),
            new TableEntry("content-encoding", ""),
            new TableEntry("content-language", ""),
            new TableEntry("content-length", ""),
            new TableEntry("content-location", ""),
            new TableEntry("content-range", ""),
            new TableEntry("content-type", ""),
            new TableEntry("cookie", ""),
            new TableEntry("date", ""),
            new TableEntry("etag", ""),
            new TableEntry("expect", ""),
            new TableEntry("expires", ""),
            new TableEntry("from", ""),
            new TableEntry("host", ""),
            new TableEntry("if-match", ""),
            new TableEntry("if-modified-since", ""),
            new TableEntry("if-none-match", ""),
            new TableEntry("if-range", ""),
            new TableEntry("if-unmodified-since", ""),
            new TableEntry("last-modified", ""),
            new TableEntry("link", ""),
            new TableEntry("location", ""),
            new TableEntry("max-forwards", ""),
            new TableEntry("proxy-authenticate", ""),
            new TableEntry("proxy-authorization", ""),
            new TableEntry("range", ""),
            new TableEntry("referer", ""),
            new TableEntry("refresh", ""),
            new TableEntry("retry-after", ""),
            new TableEntry("server", ""),
            new TableEntry("set-cookie", ""),
            new TableEntry("strict-transport-security", ""),
            new TableEntry("transfer-encoding", ""),
            new TableEntry("user-agent", ""),
            new TableEntry("vary", ""),
            new TableEntry("via", ""),
            new TableEntry("www-authenticate", "")
        };

        struct TableEntry : IComparable<TableEntry>, IEquatable<TableEntry>
        {
            private const int DynamicOverhead = 32;

            public string Name { get; }
            public string Value { get; }
            public int DynamicSize => Name.Length + Value.Length + DynamicOverhead;

            public TableEntry(string name, string value)
            {
                Name = name;
                Value = value;
            }

            public int CompareTo(TableEntry other)
            {
                int c = string.Compare(Name, other.Name, StringComparison.Ordinal);
                if (c != 0) return c;

                return string.Compare(Value, other.Value, StringComparison.Ordinal);
            }

            public bool Equals(TableEntry other)
            {
                return string.Equals(Name, other.Name, StringComparison.Ordinal) && string.Equals(Value, other.Value, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is TableEntry e && Equals(e);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Name, Value);
            }
        }
    }

    [Flags]
    public enum HPackFlags
    {
        None = 0,

        HuffmanEncodeName = 1,
        HuffmanEncodeValue = 2,
        HuffmanEncode = HuffmanEncodeName | HuffmanEncodeValue,

        WithoutIndexing = 0,
        NewIndexed = 4,
        NeverIndexed = 8
    }

    public static class HuffmanEncoder
    {
        // Stolen from product code
        // See https://github.com/dotnet/corefx/blob/ae7b3970bb2c8d76004ea397083ce7ceb1238133/src/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HPack/Huffman.cs#L12
        private static readonly (uint code, int bitLength)[] s_encodingTable = new (uint code, int bitLength)[]
        {
            (0b11111111_11000000_00000000_00000000, 13),
            (0b11111111_11111111_10110000_00000000, 23),
            (0b11111111_11111111_11111110_00100000, 28),
            (0b11111111_11111111_11111110_00110000, 28),
            (0b11111111_11111111_11111110_01000000, 28),
            (0b11111111_11111111_11111110_01010000, 28),
            (0b11111111_11111111_11111110_01100000, 28),
            (0b11111111_11111111_11111110_01110000, 28),
            (0b11111111_11111111_11111110_10000000, 28),
            (0b11111111_11111111_11101010_00000000, 24),
            (0b11111111_11111111_11111111_11110000, 30),
            (0b11111111_11111111_11111110_10010000, 28),
            (0b11111111_11111111_11111110_10100000, 28),
            (0b11111111_11111111_11111111_11110100, 30),
            (0b11111111_11111111_11111110_10110000, 28),
            (0b11111111_11111111_11111110_11000000, 28),
            (0b11111111_11111111_11111110_11010000, 28),
            (0b11111111_11111111_11111110_11100000, 28),
            (0b11111111_11111111_11111110_11110000, 28),
            (0b11111111_11111111_11111111_00000000, 28),
            (0b11111111_11111111_11111111_00010000, 28),
            (0b11111111_11111111_11111111_00100000, 28),
            (0b11111111_11111111_11111111_11111000, 30),
            (0b11111111_11111111_11111111_00110000, 28),
            (0b11111111_11111111_11111111_01000000, 28),
            (0b11111111_11111111_11111111_01010000, 28),
            (0b11111111_11111111_11111111_01100000, 28),
            (0b11111111_11111111_11111111_01110000, 28),
            (0b11111111_11111111_11111111_10000000, 28),
            (0b11111111_11111111_11111111_10010000, 28),
            (0b11111111_11111111_11111111_10100000, 28),
            (0b11111111_11111111_11111111_10110000, 28),
            (0b01010000_00000000_00000000_00000000, 6),
            (0b11111110_00000000_00000000_00000000, 10),
            (0b11111110_01000000_00000000_00000000, 10),
            (0b11111111_10100000_00000000_00000000, 12),
            (0b11111111_11001000_00000000_00000000, 13),
            (0b01010100_00000000_00000000_00000000, 6),
            (0b11111000_00000000_00000000_00000000, 8),
            (0b11111111_01000000_00000000_00000000, 11),
            (0b11111110_10000000_00000000_00000000, 10),
            (0b11111110_11000000_00000000_00000000, 10),
            (0b11111001_00000000_00000000_00000000, 8),
            (0b11111111_01100000_00000000_00000000, 11),
            (0b11111010_00000000_00000000_00000000, 8),
            (0b01011000_00000000_00000000_00000000, 6),
            (0b01011100_00000000_00000000_00000000, 6),
            (0b01100000_00000000_00000000_00000000, 6),
            (0b00000000_00000000_00000000_00000000, 5),
            (0b00001000_00000000_00000000_00000000, 5),
            (0b00010000_00000000_00000000_00000000, 5),
            (0b01100100_00000000_00000000_00000000, 6),
            (0b01101000_00000000_00000000_00000000, 6),
            (0b01101100_00000000_00000000_00000000, 6),
            (0b01110000_00000000_00000000_00000000, 6),
            (0b01110100_00000000_00000000_00000000, 6),
            (0b01111000_00000000_00000000_00000000, 6),
            (0b01111100_00000000_00000000_00000000, 6),
            (0b10111000_00000000_00000000_00000000, 7),
            (0b11111011_00000000_00000000_00000000, 8),
            (0b11111111_11111000_00000000_00000000, 15),
            (0b10000000_00000000_00000000_00000000, 6),
            (0b11111111_10110000_00000000_00000000, 12),
            (0b11111111_00000000_00000000_00000000, 10),
            (0b11111111_11010000_00000000_00000000, 13),
            (0b10000100_00000000_00000000_00000000, 6),
            (0b10111010_00000000_00000000_00000000, 7),
            (0b10111100_00000000_00000000_00000000, 7),
            (0b10111110_00000000_00000000_00000000, 7),
            (0b11000000_00000000_00000000_00000000, 7),
            (0b11000010_00000000_00000000_00000000, 7),
            (0b11000100_00000000_00000000_00000000, 7),
            (0b11000110_00000000_00000000_00000000, 7),
            (0b11001000_00000000_00000000_00000000, 7),
            (0b11001010_00000000_00000000_00000000, 7),
            (0b11001100_00000000_00000000_00000000, 7),
            (0b11001110_00000000_00000000_00000000, 7),
            (0b11010000_00000000_00000000_00000000, 7),
            (0b11010010_00000000_00000000_00000000, 7),
            (0b11010100_00000000_00000000_00000000, 7),
            (0b11010110_00000000_00000000_00000000, 7),
            (0b11011000_00000000_00000000_00000000, 7),
            (0b11011010_00000000_00000000_00000000, 7),
            (0b11011100_00000000_00000000_00000000, 7),
            (0b11011110_00000000_00000000_00000000, 7),
            (0b11100000_00000000_00000000_00000000, 7),
            (0b11100010_00000000_00000000_00000000, 7),
            (0b11100100_00000000_00000000_00000000, 7),
            (0b11111100_00000000_00000000_00000000, 8),
            (0b11100110_00000000_00000000_00000000, 7),
            (0b11111101_00000000_00000000_00000000, 8),
            (0b11111111_11011000_00000000_00000000, 13),
            (0b11111111_11111110_00000000_00000000, 19),
            (0b11111111_11100000_00000000_00000000, 13),
            (0b11111111_11110000_00000000_00000000, 14),
            (0b10001000_00000000_00000000_00000000, 6),
            (0b11111111_11111010_00000000_00000000, 15),
            (0b00011000_00000000_00000000_00000000, 5),
            (0b10001100_00000000_00000000_00000000, 6),
            (0b00100000_00000000_00000000_00000000, 5),
            (0b10010000_00000000_00000000_00000000, 6),
            (0b00101000_00000000_00000000_00000000, 5),
            (0b10010100_00000000_00000000_00000000, 6),
            (0b10011000_00000000_00000000_00000000, 6),
            (0b10011100_00000000_00000000_00000000, 6),
            (0b00110000_00000000_00000000_00000000, 5),
            (0b11101000_00000000_00000000_00000000, 7),
            (0b11101010_00000000_00000000_00000000, 7),
            (0b10100000_00000000_00000000_00000000, 6),
            (0b10100100_00000000_00000000_00000000, 6),
            (0b10101000_00000000_00000000_00000000, 6),
            (0b00111000_00000000_00000000_00000000, 5),
            (0b10101100_00000000_00000000_00000000, 6),
            (0b11101100_00000000_00000000_00000000, 7),
            (0b10110000_00000000_00000000_00000000, 6),
            (0b01000000_00000000_00000000_00000000, 5),
            (0b01001000_00000000_00000000_00000000, 5),
            (0b10110100_00000000_00000000_00000000, 6),
            (0b11101110_00000000_00000000_00000000, 7),
            (0b11110000_00000000_00000000_00000000, 7),
            (0b11110010_00000000_00000000_00000000, 7),
            (0b11110100_00000000_00000000_00000000, 7),
            (0b11110110_00000000_00000000_00000000, 7),
            (0b11111111_11111100_00000000_00000000, 15),
            (0b11111111_10000000_00000000_00000000, 11),
            (0b11111111_11110100_00000000_00000000, 14),
            (0b11111111_11101000_00000000_00000000, 13),
            (0b11111111_11111111_11111111_11000000, 28),
            (0b11111111_11111110_01100000_00000000, 20),
            (0b11111111_11111111_01001000_00000000, 22),
            (0b11111111_11111110_01110000_00000000, 20),
            (0b11111111_11111110_10000000_00000000, 20),
            (0b11111111_11111111_01001100_00000000, 22),
            (0b11111111_11111111_01010000_00000000, 22),
            (0b11111111_11111111_01010100_00000000, 22),
            (0b11111111_11111111_10110010_00000000, 23),
            (0b11111111_11111111_01011000_00000000, 22),
            (0b11111111_11111111_10110100_00000000, 23),
            (0b11111111_11111111_10110110_00000000, 23),
            (0b11111111_11111111_10111000_00000000, 23),
            (0b11111111_11111111_10111010_00000000, 23),
            (0b11111111_11111111_10111100_00000000, 23),
            (0b11111111_11111111_11101011_00000000, 24),
            (0b11111111_11111111_10111110_00000000, 23),
            (0b11111111_11111111_11101100_00000000, 24),
            (0b11111111_11111111_11101101_00000000, 24),
            (0b11111111_11111111_01011100_00000000, 22),
            (0b11111111_11111111_11000000_00000000, 23),
            (0b11111111_11111111_11101110_00000000, 24),
            (0b11111111_11111111_11000010_00000000, 23),
            (0b11111111_11111111_11000100_00000000, 23),
            (0b11111111_11111111_11000110_00000000, 23),
            (0b11111111_11111111_11001000_00000000, 23),
            (0b11111111_11111110_11100000_00000000, 21),
            (0b11111111_11111111_01100000_00000000, 22),
            (0b11111111_11111111_11001010_00000000, 23),
            (0b11111111_11111111_01100100_00000000, 22),
            (0b11111111_11111111_11001100_00000000, 23),
            (0b11111111_11111111_11001110_00000000, 23),
            (0b11111111_11111111_11101111_00000000, 24),
            (0b11111111_11111111_01101000_00000000, 22),
            (0b11111111_11111110_11101000_00000000, 21),
            (0b11111111_11111110_10010000_00000000, 20),
            (0b11111111_11111111_01101100_00000000, 22),
            (0b11111111_11111111_01110000_00000000, 22),
            (0b11111111_11111111_11010000_00000000, 23),
            (0b11111111_11111111_11010010_00000000, 23),
            (0b11111111_11111110_11110000_00000000, 21),
            (0b11111111_11111111_11010100_00000000, 23),
            (0b11111111_11111111_01110100_00000000, 22),
            (0b11111111_11111111_01111000_00000000, 22),
            (0b11111111_11111111_11110000_00000000, 24),
            (0b11111111_11111110_11111000_00000000, 21),
            (0b11111111_11111111_01111100_00000000, 22),
            (0b11111111_11111111_11010110_00000000, 23),
            (0b11111111_11111111_11011000_00000000, 23),
            (0b11111111_11111111_00000000_00000000, 21),
            (0b11111111_11111111_00001000_00000000, 21),
            (0b11111111_11111111_10000000_00000000, 22),
            (0b11111111_11111111_00010000_00000000, 21),
            (0b11111111_11111111_11011010_00000000, 23),
            (0b11111111_11111111_10000100_00000000, 22),
            (0b11111111_11111111_11011100_00000000, 23),
            (0b11111111_11111111_11011110_00000000, 23),
            (0b11111111_11111110_10100000_00000000, 20),
            (0b11111111_11111111_10001000_00000000, 22),
            (0b11111111_11111111_10001100_00000000, 22),
            (0b11111111_11111111_10010000_00000000, 22),
            (0b11111111_11111111_11100000_00000000, 23),
            (0b11111111_11111111_10010100_00000000, 22),
            (0b11111111_11111111_10011000_00000000, 22),
            (0b11111111_11111111_11100010_00000000, 23),
            (0b11111111_11111111_11111000_00000000, 26),
            (0b11111111_11111111_11111000_01000000, 26),
            (0b11111111_11111110_10110000_00000000, 20),
            (0b11111111_11111110_00100000_00000000, 19),
            (0b11111111_11111111_10011100_00000000, 22),
            (0b11111111_11111111_11100100_00000000, 23),
            (0b11111111_11111111_10100000_00000000, 22),
            (0b11111111_11111111_11110110_00000000, 25),
            (0b11111111_11111111_11111000_10000000, 26),
            (0b11111111_11111111_11111000_11000000, 26),
            (0b11111111_11111111_11111001_00000000, 26),
            (0b11111111_11111111_11111011_11000000, 27),
            (0b11111111_11111111_11111011_11100000, 27),
            (0b11111111_11111111_11111001_01000000, 26),
            (0b11111111_11111111_11110001_00000000, 24),
            (0b11111111_11111111_11110110_10000000, 25),
            (0b11111111_11111110_01000000_00000000, 19),
            (0b11111111_11111111_00011000_00000000, 21),
            (0b11111111_11111111_11111001_10000000, 26),
            (0b11111111_11111111_11111100_00000000, 27),
            (0b11111111_11111111_11111100_00100000, 27),
            (0b11111111_11111111_11111001_11000000, 26),
            (0b11111111_11111111_11111100_01000000, 27),
            (0b11111111_11111111_11110010_00000000, 24),
            (0b11111111_11111111_00100000_00000000, 21),
            (0b11111111_11111111_00101000_00000000, 21),
            (0b11111111_11111111_11111010_00000000, 26),
            (0b11111111_11111111_11111010_01000000, 26),
            (0b11111111_11111111_11111111_11010000, 28),
            (0b11111111_11111111_11111100_01100000, 27),
            (0b11111111_11111111_11111100_10000000, 27),
            (0b11111111_11111111_11111100_10100000, 27),
            (0b11111111_11111110_11000000_00000000, 20),
            (0b11111111_11111111_11110011_00000000, 24),
            (0b11111111_11111110_11010000_00000000, 20),
            (0b11111111_11111111_00110000_00000000, 21),
            (0b11111111_11111111_10100100_00000000, 22),
            (0b11111111_11111111_00111000_00000000, 21),
            (0b11111111_11111111_01000000_00000000, 21),
            (0b11111111_11111111_11100110_00000000, 23),
            (0b11111111_11111111_10101000_00000000, 22),
            (0b11111111_11111111_10101100_00000000, 22),
            (0b11111111_11111111_11110111_00000000, 25),
            (0b11111111_11111111_11110111_10000000, 25),
            (0b11111111_11111111_11110100_00000000, 24),
            (0b11111111_11111111_11110101_00000000, 24),
            (0b11111111_11111111_11111010_10000000, 26),
            (0b11111111_11111111_11101000_00000000, 23),
            (0b11111111_11111111_11111010_11000000, 26),
            (0b11111111_11111111_11111100_11000000, 27),
            (0b11111111_11111111_11111011_00000000, 26),
            (0b11111111_11111111_11111011_01000000, 26),
            (0b11111111_11111111_11111100_11100000, 27),
            (0b11111111_11111111_11111101_00000000, 27),
            (0b11111111_11111111_11111101_00100000, 27),
            (0b11111111_11111111_11111101_01000000, 27),
            (0b11111111_11111111_11111101_01100000, 27),
            (0b11111111_11111111_11111111_11100000, 28),
            (0b11111111_11111111_11111101_10000000, 27),
            (0b11111111_11111111_11111101_10100000, 27),
            (0b11111111_11111111_11111101_11000000, 27),
            (0b11111111_11111111_11111101_11100000, 27),
            (0b11111111_11111111_11111110_00000000, 27),
            (0b11111111_11111111_11111011_10000000, 26),
            (0b11111111_11111111_11111111_11111100, 30)
        };

        public static int GetEncodedLength(ReadOnlySpan<byte> src)
        {
            int bits = 0;

            foreach (byte x in src)
            {
                bits += s_encodingTable[x].bitLength;
            }

            return (bits + 7) / 8;
        }

        public static int Encode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            ulong buffer = 0;
            int bufferLength = 0;
            int dstIdx = 0;

            foreach (byte x in src)
            {
                (uint code, int codeLength) = s_encodingTable[x];

                buffer = buffer << codeLength | code >> 32 - codeLength;
                bufferLength += codeLength;

                while (bufferLength >= 8)
                {
                    bufferLength -= 8;
                    dst[dstIdx++] = (byte)(buffer >> bufferLength);
                }
            }

            if (bufferLength != 0)
            {
                dst[dstIdx++] = (byte)(buffer << (8 - bufferLength) | 0xFFu >> bufferLength);
            }

            return dstIdx;
        }
    }
}
