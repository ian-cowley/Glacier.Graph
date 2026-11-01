using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// A Compressed Sparse Row (CSR) graph store supporting high-throughput direct Memory-Mapped File (MMF)
    /// pointer traversal for instant zero-lock graph queries, alongside an optional software LRU page cache
    /// for strictly memory-constrained environments.
    /// </summary>
    public unsafe class CsrGraphStore : IDisposable
    {
        private readonly Dictionary<string, int> _externalToInternalId = new();
        private readonly Dictionary<int, string> _internalToExternalId = new();
        private readonly Dictionary<string, int> _relationTypes = new();
        private readonly Dictionary<int, string> _relationTypeNames = new();
        private readonly string[] _nodeMetadata;

        public int NodeCount { get; private set; }
        public int EdgeCount { get; private set; }

        private readonly FileStream _fs;
        private readonly long _fileLength;
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private byte* _basePointer;
        private bool _disposed;

        internal readonly long _rowOffsetsStart;
        internal readonly long _columnIndicesStart;
        internal readonly long _edgeRelationsStart;
        internal readonly long _edgeWeightsStart;

        private readonly object _lock = new();

        // Software page cache to enforce strict memory limit (e.g. 256KB) when MMF is disabled
        private readonly int _pageSize = 4096;
        private readonly byte[][] _pages;
        private readonly long[] _pageTags;
        private readonly long[] _pageAccess;
        private long _accessCounter = 0;

        public CsrGraphStore(string filePath, int maxMemoryBytes = 256 * 1024, bool useMemoryMapping = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMemoryBytes, 0);

            _fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);

            try
            {
                _fileLength = _fs.Length;
                int version;

                try
                {
                    using var reader = new BinaryReader(_fs, Encoding.UTF8, leaveOpen: true);

                    if (reader.ReadString() != "GCSR")
                    {
                        throw new InvalidDataException("Invalid CSR Graph file format.");
                    }

                    version = reader.ReadInt32();
                    if (version < 1 || version > 2)
                    {
                        throw new InvalidDataException($"Unsupported CSR Graph format version: {version}.");
                    }

                    NodeCount = reader.ReadInt32();
                    EdgeCount = reader.ReadInt32();
                    if (NodeCount < 0 || EdgeCount < 0)
                    {
                        throw new InvalidDataException("Corrupt CSR Graph header: negative node or edge count.");
                    }

                    // Read Relation Types
                    int relCount = reader.ReadInt32();
                    if (relCount < 0)
                    {
                        throw new InvalidDataException("Corrupt CSR Graph header: negative relation count.");
                    }

                    for (int i = 0; i < relCount; i++)
                    {
                        string relName = reader.ReadString();
                        int relId = reader.ReadInt32();
                        _relationTypes[relName] = relId;
                        _relationTypeNames[relId] = relName;
                    }

                    // Read Node ID Mappings
                    int idCount = reader.ReadInt32();
                    if (idCount < 0)
                    {
                        throw new InvalidDataException("Corrupt CSR Graph header: negative node ID count.");
                    }

                    for (int i = 0; i < idCount; i++)
                    {
                        string key = reader.ReadString();
                        int val = reader.ReadInt32();
                        _externalToInternalId[key] = val;
                        _internalToExternalId[val] = key;
                    }

                    // Read String Metadata (only retain active slots 0..NodeCount)
                    int serializedMetadataSlots = reader.ReadInt32();
                    if (serializedMetadataSlots < 0)
                    {
                        throw new InvalidDataException("Corrupt CSR Graph header: negative metadata slot count.");
                    }

                    _nodeMetadata = new string[NodeCount + 1];
                    Array.Fill(_nodeMetadata, string.Empty);

                    for (int i = 0; i < serializedMetadataSlots; i++)
                    {
                        string meta = reader.ReadString();
                        if (i <= NodeCount)
                        {
                            _nodeMetadata[i] = meta;
                        }
                    }
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException("Truncated CSR Graph file header.", ex);
                }

                _rowOffsetsStart = _fs.Position;
                _columnIndicesStart = _rowOffsetsStart + (long)(NodeCount + 2) * sizeof(int);
                _edgeRelationsStart = _columnIndicesStart + (long)EdgeCount * sizeof(int);
                _edgeWeightsStart = version >= 2
                    ? _edgeRelationsStart + (long)EdgeCount * sizeof(int)
                    : -1L;

                long requiredLength = version >= 2
                    ? _edgeWeightsStart + (long)EdgeCount * sizeof(float)
                    : _edgeRelationsStart + (long)EdgeCount * sizeof(int);

                if (_fileLength < requiredLength)
                {
                    throw new InvalidDataException(
                        $"Truncated or corrupt CSR Graph file: expected at least {requiredLength} bytes, but file is {_fileLength} bytes.");
                }

                if (useMemoryMapping)
                {
                    try
                    {
                        _mmf = MemoryMappedFile.CreateFromFile(_fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePointer);
                    }
                    catch
                    {
                        _basePointer = null;
                    }
                }

                // Allocate Software LRU Page Cache only when MMF is disabled or unavailable
                if (_basePointer == null)
                {
                    int numPages = Math.Max(1, maxMemoryBytes / _pageSize);
                    _pages = new byte[numPages][];
                    for (int i = 0; i < numPages; i++)
                    {
                        _pages[i] = new byte[_pageSize];
                    }
                    _pageTags = new long[numPages];
                    Array.Fill(_pageTags, -1L);
                    _pageAccess = new long[numPages];
                }
                else
                {
                    _pages = Array.Empty<byte[]>();
                    _pageTags = Array.Empty<long>();
                    _pageAccess = Array.Empty<long>();
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal ReadOnlySpan<byte> GetPage(long offset)
        {
            long pageIndex = offset / _pageSize;
            int cacheSlot = -1;
            int lruSlot = 0;
            long minAccess = long.MaxValue;

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
                // Cache miss: evict LRU slot and load new page from disk
                cacheSlot = lruSlot;
                _pageTags[cacheSlot] = pageIndex;
                long fileOffset = pageIndex * _pageSize;

                long remainingBytes = _fileLength - fileOffset;
                int bytesToRead = remainingBytes > 0 ? (int)Math.Min(_pageSize, remainingBytes) : 0;

                int totalRead = 0;
                while (totalRead < bytesToRead)
                {
                    int read = RandomAccess.Read(
                        _fs.SafeFileHandle,
                        _pages[cacheSlot].AsSpan(totalRead, bytesToRead - totalRead),
                        fileOffset + totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }

                // Clear stale tail bytes when reading a partial EOF page
                if (totalRead < _pageSize)
                {
                    _pages[cacheSlot].AsSpan(totalRead, _pageSize - totalRead).Clear();
                }
            }

            _pageAccess[cacheSlot] = ++_accessCounter;
            return _pages[cacheSlot];
        }

        internal int ReadInt32(long offset)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (offset < 0 || offset + sizeof(int) > _fileLength)
            {
                throw new InvalidDataException($"CSR read offset {offset} is out of file bounds ({_fileLength}).");
            }

            if (_basePointer != null)
            {
                return *(int*)(_basePointer + offset);
            }

            lock (_lock)
            {
                var span = GetPage(offset);
                int inPageOffset = (int)(offset % _pageSize);

                // Handle 32-bit values that straddle a 4KB page boundary
                if (inPageOffset > _pageSize - sizeof(int))
                {
                    Span<byte> temp = stackalloc byte[sizeof(int)];
                    int firstPart = _pageSize - inPageOffset;
                    span.Slice(inPageOffset, firstPart).CopyTo(temp);
                    var span2 = GetPage(offset + firstPart);
                    span2.Slice(0, sizeof(int) - firstPart).CopyTo(temp.Slice(firstPart));
                    return MemoryMarshal.Read<int>(temp);
                }

                return MemoryMarshal.Read<int>(span.Slice(inPageOffset));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float ReadSingle(long offset)
        {
            return BitConverter.Int32BitsToSingle(ReadInt32(offset));
        }

        public int GetExternalToInternalId(string externalId)
        {
            ArgumentNullException.ThrowIfNull(externalId);
            return _externalToInternalId.TryGetValue(externalId, out int val) ? val : 0;
        }

        public string GetExternalId(int internalId)
            => _internalToExternalId.TryGetValue(internalId, out string? val) && val != null ? val : string.Empty;

        public string GetNodeMetadata(string externalId)
        {
            ArgumentNullException.ThrowIfNull(externalId);
            return _externalToInternalId.TryGetValue(externalId, out int id)
                ? GetNodeMetadataByInternalId(id)
                : string.Empty;
        }

        public string GetNodeMetadataByInternalId(int internalId)
        {
            if (internalId <= 0 || internalId > NodeCount || internalId >= _nodeMetadata.Length)
            {
                return string.Empty;
            }
            return _nodeMetadata[internalId] ?? string.Empty;
        }

        public string GetRelationType(int relationId)
            => _relationTypeNames.TryGetValue(relationId, out string? name) && name != null ? name : string.Empty;

        public int GetRelationId(string relationType)
        {
            ArgumentNullException.ThrowIfNull(relationType);
            return _relationTypes.TryGetValue(relationType, out int id) ? id : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateEdgeSliceBounds(int startIdx, int endIdx)
        {
            if (startIdx < 0 || endIdx < startIdx || endIdx > EdgeCount)
            {
                throw new InvalidDataException($"Corrupt CSR row offsets [{startIdx}..{endIdx}) for EdgeCount={EdgeCount}.");
            }
        }

        public bool TryGetNeighbors(int internalId, out ReadOnlySpan<int> neighbors)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_basePointer != null && internalId > 0 && internalId <= NodeCount)
            {
                int* pRowOffsets = (int*)(_basePointer + _rowOffsetsStart);
                int startIdx = pRowOffsets[internalId];
                int endIdx = pRowOffsets[internalId + 1];
                ValidateEdgeSliceBounds(startIdx, endIdx);

                neighbors = new ReadOnlySpan<int>((int*)(_basePointer + _columnIndicesStart) + startIdx, endIdx - startIdx);
                return true;
            }

            neighbors = default;
            return false;
        }

        public bool TryGetNeighborsAndWeights(int internalId, out ReadOnlySpan<int> neighbors, out ReadOnlySpan<float> weights)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_basePointer != null && _edgeWeightsStart >= 0 && internalId > 0 && internalId <= NodeCount)
            {
                int* pRowOffsets = (int*)(_basePointer + _rowOffsetsStart);
                int startIdx = pRowOffsets[internalId];
                int endIdx = pRowOffsets[internalId + 1];
                ValidateEdgeSliceBounds(startIdx, endIdx);

                int count = endIdx - startIdx;
                neighbors = new ReadOnlySpan<int>((int*)(_basePointer + _columnIndicesStart) + startIdx, count);
                weights = new ReadOnlySpan<float>((float*)(_basePointer + _edgeWeightsStart) + startIdx, count);
                return true;
            }

            neighbors = default;
            weights = default;
            return false;
        }

        public ReadOnlySpan<int> GetRowOffsetsSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_basePointer != null)
            {
                return new ReadOnlySpan<int>((int*)(_basePointer + _rowOffsetsStart), NodeCount + 2);
            }
            return default;
        }

        public ReadOnlySpan<int> GetColumnIndicesSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_basePointer != null)
            {
                return new ReadOnlySpan<int>((int*)(_basePointer + _columnIndicesStart), EdgeCount);
            }
            return default;
        }

        public ReadOnlySpan<int> GetEdgeRelationsSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_basePointer != null)
            {
                return new ReadOnlySpan<int>((int*)(_basePointer + _edgeRelationsStart), EdgeCount);
            }
            return default;
        }

        public ReadOnlySpan<float> GetEdgeWeightsSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_basePointer != null && _edgeWeightsStart >= 0)
            {
                return new ReadOnlySpan<float>((float*)(_basePointer + _edgeWeightsStart), EdgeCount);
            }
            return default;
        }

        public int GetNodeDegree(int internalId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (internalId <= 0 || internalId > NodeCount) return 0;

            if (_basePointer != null)
            {
                int* pRowOffsets = (int*)(_basePointer + _rowOffsetsStart);
                int startIdx = pRowOffsets[internalId];
                int endIdx = pRowOffsets[internalId + 1];
                ValidateEdgeSliceBounds(startIdx, endIdx);
                return endIdx - startIdx;
            }

            long rowOffsetPos = _rowOffsetsStart + (long)internalId * sizeof(int);
            int s = ReadInt32(rowOffsetPos);
            int e = ReadInt32(rowOffsetPos + sizeof(int));
            ValidateEdgeSliceBounds(s, e);
            return e - s;
        }

        public bool ContainsNeighbor(int internalId, int targetNeighborId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (internalId <= 0 || internalId > NodeCount) return false;

            if (TryGetNeighbors(internalId, out var neighbors))
            {
                return Kernels.CsrKernels.ContainsNeighbor(neighbors, targetNeighborId);
            }

            var edges = GetOutwardEdgesByInternalId(internalId);
            while (edges.MoveNext())
            {
                if (edges.CurrentTargetNodeId == targetNeighborId) return true;
                edges.Advance();
            }
            return false;
        }

        public void ComputeDegrees(int startNode, Span<int> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length == 0) return;

            ArgumentOutOfRangeException.ThrowIfLessThan(startNode, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(startNode + destination.Length, NodeCount + 1);

            if (_basePointer != null)
            {
                var rowOffsets = GetRowOffsetsSpan();
                Kernels.CsrKernels.ComputeDegrees(rowOffsets, startNode, destination);
            }
            else
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    int nodeIdx = startNode + i;
                    long rowOffsetPos = _rowOffsetsStart + (long)nodeIdx * sizeof(int);
                    int s = ReadInt32(rowOffsetPos);
                    int e = ReadInt32(rowOffsetPos + sizeof(int));
                    destination[i] = e - s;
                }
            }
        }

        public int CountCommonNeighbors(int nodeA, int nodeB, bool isSorted = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (nodeA <= 0 || nodeA > NodeCount || nodeB <= 0 || nodeB > NodeCount)
            {
                return 0;
            }

            if (TryGetNeighbors(nodeA, out var neighborsA) && TryGetNeighbors(nodeB, out var neighborsB))
            {
                return Kernels.CsrKernels.CountCommonNeighbors(neighborsA, neighborsB, isSorted);
            }

            int count = 0;
            var edgesA = GetOutwardEdgesByInternalId(nodeA);
            while (edgesA.MoveNext())
            {
                int neighbor = edgesA.CurrentTargetNodeId;
                if (ContainsNeighbor(nodeB, neighbor)) count++;
                edgesA.Advance();
            }
            return count;
        }

        public CsrEdgeEnumerator GetOutwardEdges(string externalId)
        {
            int internalId = GetExternalToInternalId(externalId);
            return GetOutwardEdgesByInternalId(internalId);
        }

        public CsrEdgeEnumerator GetOutwardEdgesByInternalId(int internalId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (internalId <= 0 || internalId > NodeCount)
            {
                return default;
            }

            if (_basePointer != null)
            {
                int* pRowOffsets = (int*)(_basePointer + _rowOffsetsStart);
                int startIdx = pRowOffsets[internalId];
                int endIdx = pRowOffsets[internalId + 1];
                ValidateEdgeSliceBounds(startIdx, endIdx);
                return new CsrEdgeEnumerator(this, startIdx, endIdx);
            }

            long rowOffsetPos = _rowOffsetsStart + (long)internalId * sizeof(int);
            int s = ReadInt32(rowOffsetPos);
            int e = ReadInt32(rowOffsetPos + sizeof(int));
            ValidateEdgeSliceBounds(s, e);
            return new CsrEdgeEnumerator(this, s, e);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_basePointer != null)
            {
                _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
                _basePointer = null;
            }
            _accessor?.Dispose();
            _mmf?.Dispose();
            _fs?.Dispose();
            GC.SuppressFinalize(this);
        }

        public unsafe struct CsrEdgeEnumerator
        {
            private readonly CsrGraphStore? _store;
            private int _currentIndex;
            private readonly int _endIndex;
            private readonly int* _pColumns;
            private readonly int* _pRelations;
            private readonly float* _pWeights;
            private readonly bool _hasWeights;

            public CsrEdgeEnumerator(CsrGraphStore store, int startIndex, int endIndex)
            {
                _store = store;
                _currentIndex = startIndex;
                _endIndex = endIndex;
                _hasWeights = store._edgeWeightsStart >= 0;

                if (store._basePointer != null)
                {
                    _pColumns = (int*)(store._basePointer + store._columnIndicesStart);
                    _pRelations = (int*)(store._basePointer + store._edgeRelationsStart);
                    _pWeights = _hasWeights
                        ? (float*)(store._basePointer + store._edgeWeightsStart)
                        : null;
                }
                else
                {
                    _pColumns = null;
                    _pRelations = null;
                    _pWeights = null;
                }
            }

            public bool MoveNext() => _store != null && _currentIndex < _endIndex;

            public int CurrentTargetNodeId => _pColumns != null
                ? _pColumns[_currentIndex]
                : _store!.ReadInt32(_store._columnIndicesStart + (long)_currentIndex * sizeof(int));

            public int CurrentRelationId => _pRelations != null
                ? _pRelations[_currentIndex]
                : _store!.ReadInt32(_store._edgeRelationsStart + (long)_currentIndex * sizeof(int));

            public float CurrentWeight => !_hasWeights
                ? 1.0f
                : (_pWeights != null
                    ? _pWeights[_currentIndex]
                    : _store!.ReadSingle(_store._edgeWeightsStart + (long)_currentIndex * sizeof(float)));

            public void Advance()
            {
                _currentIndex++;
            }
        }
    }
}
