using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// A Compressed Sparse Row (CSR) graph store that simulates embedded system constraints.
    /// It enforces a strict memory limit using a software LRU page cache over flash/disk reads.
    /// </summary>
    public class CsrGraphStore : IDisposable
    {
        private readonly Dictionary<string, int> _externalToInternalId = new();
        private readonly Dictionary<int, string> _internalToExternalId = new();
        private readonly Dictionary<string, int> _relationTypes = new();
        private string[] _nodeMetadata;

        public int NodeCount { get; private set; }
        public int EdgeCount { get; private set; }

        private FileStream _fs;
        internal long _rowOffsetsStart;
        internal long _columnIndicesStart;
        internal long _edgeRelationsStart;

        private readonly object _lock = new();

        // Software page cache to enforce strict memory limit (e.g. 256KB)
        private readonly int _pageSize = 4096;
        private readonly byte[][] _pages;
        private readonly long[] _pageTags;
        private readonly int[] _pageAccess; 
        private int _accessCounter = 0;

        public CsrGraphStore(string filePath, int maxMemoryBytes = 256 * 1024)
        {
            _fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            using var reader = new BinaryReader(_fs, System.Text.Encoding.UTF8, leaveOpen: true);
            
            if (reader.ReadString() != "GCSR") throw new Exception("Invalid CSR Graph file format.");
            int version = reader.ReadInt32();

            NodeCount = reader.ReadInt32();
            EdgeCount = reader.ReadInt32();

            // Read Relation Types
            int relCount = reader.ReadInt32();
            for (int i = 0; i < relCount; i++) _relationTypes[reader.ReadString()] = reader.ReadInt32();

            // Read Node ID Mappings
            int idCount = reader.ReadInt32();
            for (int i = 0; i < idCount; i++)
            {
                string key = reader.ReadString();
                int val = reader.ReadInt32();
                _externalToInternalId[key] = val;
                _internalToExternalId[val] = key;
            }

            // String Metadata
            int nodeCap = reader.ReadInt32();
            _nodeMetadata = new string[nodeCap];
            for (int i = 0; i < nodeCap; i++) _nodeMetadata[i] = reader.ReadString();

            _rowOffsetsStart = _fs.Position;
            _columnIndicesStart = _rowOffsetsStart + (NodeCount + 2) * sizeof(int);
            _edgeRelationsStart = _columnIndicesStart + EdgeCount * sizeof(int);

            // Initialize Page Cache
            int numPages = Math.Max(1, maxMemoryBytes / _pageSize);
            _pages = new byte[numPages][];
            for (int i = 0; i < numPages; i++) _pages[i] = new byte[_pageSize];
            _pageTags = new long[numPages];
            Array.Fill(_pageTags, -1);
            _pageAccess = new int[numPages];
        }

        internal ReadOnlySpan<byte> GetPage(long offset)
        {
            long pageIndex = offset / _pageSize;
            int cacheSlot = -1;
            int lruSlot = 0;
            int minAccess = int.MaxValue;

            for (int i = 0; i < _pageTags.Length; i++)
            {
                if (_pageTags[i] == pageIndex)
                {
                    cacheSlot = i;
                    break;
                }
                if (_pageAccess[i] < minAccess)
                {
                    minAccess = _pageAccess[i];
                    lruSlot = i;
                }
            }

            if (cacheSlot == -1)
            {
                // Cache miss, evict LRU slot and load new page from disk
                cacheSlot = lruSlot;
                _pageTags[cacheSlot] = pageIndex;
                long fileOffset = pageIndex * _pageSize;
                
                // If the page is partially past the end of the file, read what we can
                long remainingBytes = _fs.Length - fileOffset;
                int bytesToRead = (int)Math.Min(_pageSize, remainingBytes);
                
                if (bytesToRead > 0)
                {
                    RandomAccess.Read(_fs.SafeFileHandle, _pages[cacheSlot].AsSpan(0, bytesToRead), fileOffset);
                }
            }

            _pageAccess[cacheSlot] = ++_accessCounter;
            return _pages[cacheSlot];
        }

        internal int ReadInt32(long offset)
        {
            lock (_lock)
            {
                var span = GetPage(offset);
                int inPageOffset = (int)(offset % _pageSize);
                
                // Handle integers that cross the 4KB page boundary
                if (inPageOffset > _pageSize - 4)
                {
                    Span<byte> temp = stackalloc byte[4];
                    int firstPart = _pageSize - inPageOffset;
                    span.Slice(inPageOffset, firstPart).CopyTo(temp);
                    var span2 = GetPage(offset + firstPart);
                    span2.Slice(0, 4 - firstPart).CopyTo(temp.Slice(firstPart));
                    return MemoryMarshal.Read<int>(temp);
                }
                
                return MemoryMarshal.Read<int>(span.Slice(inPageOffset));
            }
        }

        public int GetExternalToInternalId(string externalId) => _externalToInternalId.TryGetValue(externalId, out int val) ? val : 0;
        public string GetExternalId(int internalId) => _internalToExternalId.TryGetValue(internalId, out string val) ? val : string.Empty;

        public CsrEdgeEnumerator GetOutwardEdgesByInternalId(int internalId)
        {
            long rowOffsetPos = _rowOffsetsStart + internalId * sizeof(int);
            int startIdx = ReadInt32(rowOffsetPos);
            int endIdx = ReadInt32(rowOffsetPos + sizeof(int));
            return new CsrEdgeEnumerator(this, startIdx, endIdx);
        }

        public void Dispose()
        {
            _fs?.Dispose();
        }

        public struct CsrEdgeEnumerator
        {
            private readonly CsrGraphStore _store;
            private int _currentIndex;
            private readonly int _endIndex;

            public CsrEdgeEnumerator(CsrGraphStore store, int startIndex, int endIndex)
            {
                _store = store;
                _currentIndex = startIndex;
                _endIndex = endIndex;
            }

            public bool MoveNext() => _currentIndex < _endIndex;

            public int CurrentTargetNodeId => _store.ReadInt32(_store._columnIndicesStart + _currentIndex * sizeof(int));
            public int CurrentRelationId => _store.ReadInt32(_store._edgeRelationsStart + _currentIndex * sizeof(int));

            public void Advance()
            {
                _currentIndex++;
            }
        }
    }
}
