using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace hpack_encoder
{

    public sealed class HPackEncoder
    {
        private const HPackFlags IndexingMask = HPackFlags.WithoutIndexing | HPackFlags.NewIndexed | HPackFlags.NeverIndexed;

        private Dictionary<TableEntry, HashSet<int>> _dynamicTableMap = new Dictionary<TableEntry, HashSet<int>>();
        private TableEntry[] _dynamicTable = new TableEntry[32];
        private int _dynamicHead, _dynamicCount, _dynamicSize, _dynamicMaxSize;

        private Memory<byte> _buffer;
        private int _bufferConsumed;

        private System.Net.Http.HPack.HPackDecoder _decoder = new System.Net.Http.HPack.HPackDecoder();

        public int BytesWritten => _bufferConsumed;
        public Memory<byte> HeadersWritten => _buffer.Slice(0, _bufferConsumed);

        public IEnumerable<TableEntry> DynamicTable => _dynamicTableMap.Keys;
        public int DynamicTableCount => _dynamicTableMap.Count;
        public int DynamicTableSize => _dynamicSize;

        public HPackEncoder(Memory<byte> buffer, int dynamicTableMaxSize = 4096)
        {
            _buffer = buffer;
            _dynamicMaxSize = dynamicTableMaxSize;
        }

        public HPackEncoder Encode(string name, string value, HPackFlags flags = HPackFlags.None)
        {
            int index = 0;

            switch (flags & IndexingMask)
            {
                case HPackFlags.WithoutIndexing:
                    if (TryGetIndex(name, value, out index))
                    {
                        return FinishWrite(EncodeHeader(index, _buffer.Span.Slice(_bufferConsumed)));
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

            return FinishWrite(EncodeHeaderImpl(index, name, value, flags, _buffer.Span.Slice(_bufferConsumed)));
        }

        public HPackEncoder EncodeNewDynamicTableSize(int size)
        {
            if (size != _dynamicMaxSize)
            {
                _dynamicMaxSize = size;

                if ((_dynamicMaxSize - _dynamicSize) < 0)
                {
                    EnsureDynamicSpaceAvailable(0);
                }
            }

            return FinishWrite(EncodeDynamicTableSizeUpdate(size, _buffer.Span.Slice(_bufferConsumed)));
        }

        private HPackEncoder FinishWrite(int totalWritten)
        {
            _decoder.Decode(_buffer.Span.Slice(_bufferConsumed, totalWritten), false, (o, n, v) => { }, null);
            _bufferConsumed += totalWritten;
            return this;
        }

        public HPackEncoder VerifyComplete()
        {
            _decoder.CompleteDecode();
            return this;
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
                Console.WriteLine($"Evicting from {_dynamicSize}");
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

        public static readonly TableEntry[] s_staticTable = new TableEntry[]
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

        public struct TableEntry : IComparable<TableEntry>, IEquatable<TableEntry>
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

            public override string? ToString()
            {
                if (Name?.Length > 0)
                {
                    if (Value?.Length > 0)
                    {
                        return Name + ": " + Value;
                    }

                    return Name + ": <no value>";
                }

                return "<empty>";
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
}
