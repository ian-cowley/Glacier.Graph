using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// A zero-allocation, array-backed Graph database using the Forward Star representation.
    /// Bypasses the GC by storing edges in primitive contiguous arrays instead of objects.
    /// Thread-safe for concurrent readers and writers via <see cref="ReaderWriterLockSlim"/>.
    /// </summary>
    public class GraphStore : IDisposable
    {
        // --- Node Maps ---
        internal readonly Dictionary<string, int> _externalToInternalId = new();
        internal readonly Dictionary<int, string> _internalToExternalId = new();
        internal readonly Dictionary<string, int> _relationTypes = new();
        internal readonly Dictionary<int, string> _relationTypeNames = new();

        internal string[] _nodeMetadata;
        internal int _nodeCount = 0;

        // --- Forward Star Edge Arrays ---
        internal int[] _head;
        internal int[] _to;
        internal int[] _relation;
        internal int[] _next;
        internal float[] _weights;
        internal int _edgeCount = 0;

        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);
        private bool _disposed;

        public int NodeCount
        {
            get
            {
                EnterReadLock();
                try { return _nodeCount; }
                finally { ExitReadLock(); }
            }
        }

        public int EdgeCount
        {
            get
            {
                EnterReadLock();
                try { return _edgeCount; }
                finally { ExitReadLock(); }
            }
        }

        public GraphStore(int initialNodeCapacity = 100_000, int initialEdgeCapacity = 500_000)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(initialNodeCapacity);
            ArgumentOutOfRangeException.ThrowIfNegative(initialEdgeCapacity);

            int nodeArrayLength = Math.Max(2, initialNodeCapacity + 1);
            int edgeArrayLength = Math.Max(2, initialEdgeCapacity + 1);

            _nodeMetadata = new string[nodeArrayLength];
            _head = new int[nodeArrayLength];
            _to = new int[edgeArrayLength];
            _relation = new int[edgeArrayLength];
            _next = new int[edgeArrayLength];
            _weights = new float[edgeArrayLength];
            Array.Fill(_weights, 1.0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnterReadLock()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rwLock.EnterReadLock();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ExitReadLock()
        {
            _rwLock.ExitReadLock();
        }

        /// <summary>
        /// Acquires a disposable read-lock scope over the <see cref="GraphStore"/>.
        /// </summary>
        public ReadLockScope AcquireReadLock()
        {
            EnterReadLock();
            return new ReadLockScope(this);
        }

        // --- PERSISTENCE (RAM-to-Disk dumping) ---

        public void SaveToDisk(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            EnterReadLock();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                using var writer = new BinaryWriter(fs);

                int activeNodeSlots = _nodeCount + 1;
                int activeEdgeSlots = _edgeCount + 1;

                // 1. Header (Magic Bytes + Version + Active Counts + Active Slot Lengths)
                writer.Write("GLGR");
                writer.Write(2); // Version 2 includes weights
                writer.Write(_nodeCount);
                writer.Write(_edgeCount);
                writer.Write(activeNodeSlots);
                writer.Write(activeEdgeSlots);

                // 2. Relation Types Dict
                writer.Write(_relationTypes.Count);
                foreach (var kvp in _relationTypes)
                {
                    writer.Write(kvp.Key);
                    writer.Write(kvp.Value);
                }

                // 3. Node ID Mappings
                writer.Write(_externalToInternalId.Count);
                foreach (var kvp in _externalToInternalId)
                {
                    writer.Write(kvp.Key);
                    writer.Write(kvp.Value);
                }

                // 4. String Metadata Array (only active node slots 0.._nodeCount)
                for (int i = 0; i < activeNodeSlots; i++)
                {
                    writer.Write(_nodeMetadata[i] ?? string.Empty);
                }

                // 5. Raw Array Byte Dumps (active slices only)
                fs.Write(MemoryMarshal.AsBytes(_head.AsSpan(0, activeNodeSlots)));
                fs.Write(MemoryMarshal.AsBytes(_to.AsSpan(0, activeEdgeSlots)));
                fs.Write(MemoryMarshal.AsBytes(_relation.AsSpan(0, activeEdgeSlots)));
                fs.Write(MemoryMarshal.AsBytes(_next.AsSpan(0, activeEdgeSlots)));
                fs.Write(MemoryMarshal.AsBytes(_weights.AsSpan(0, activeEdgeSlots)));
            }
            finally
            {
                ExitReadLock();
            }
        }

        public static GraphStore LoadFromDisk(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(fs);

            try
            {
                // 1. Header
                if (reader.ReadString() != "GLGR")
                {
                    throw new InvalidDataException("Invalid Glacier Graph file format.");
                }

                int version = reader.ReadInt32();
                if (version < 1 || version > 2)
                {
                    throw new InvalidDataException($"Unsupported Glacier Graph format version: {version}.");
                }

                int nodeCount = reader.ReadInt32();
                int edgeCount = reader.ReadInt32();
                int nodeCap = reader.ReadInt32();
                int edgeCap = reader.ReadInt32();

                if (nodeCount < 0 || edgeCount < 0 || nodeCap < 0 || edgeCap < 0)
                {
                    throw new InvalidDataException("Corrupt Glacier Graph header: negative count or capacity.");
                }

                if ((nodeCount > 0 && nodeCap <= nodeCount) || (edgeCount > 0 && edgeCap <= edgeCount))
                {
                    throw new InvalidDataException("Corrupt Glacier Graph header: active count exceeds serialized slot capacity.");
                }

                int initialNodeCap = Math.Max(16, nodeCap > 0 ? nodeCap - 1 : 0);
                int initialEdgeCap = Math.Max(16, edgeCap > 0 ? edgeCap - 1 : 0);

                var store = new GraphStore(initialNodeCap, initialEdgeCap);
                store._nodeCount = nodeCount;
                store._edgeCount = edgeCount;

                // 2. Relation Types
                int relCount = reader.ReadInt32();
                if (relCount < 0)
                {
                    store.Dispose();
                    throw new InvalidDataException("Corrupt Glacier Graph relation table count.");
                }

                for (int i = 0; i < relCount; i++)
                {
                    string rName = reader.ReadString();
                    int rId = reader.ReadInt32();
                    store._relationTypes[rName] = rId;
                    store._relationTypeNames[rId] = rName;
                }

                // 3. Node ID Mappings
                int idCount = reader.ReadInt32();
                if (idCount < 0)
                {
                    store.Dispose();
                    throw new InvalidDataException("Corrupt Glacier Graph node mapping count.");
                }

                for (int i = 0; i < idCount; i++)
                {
                    string key = reader.ReadString();
                    int val = reader.ReadInt32();
                    store._externalToInternalId[key] = val;
                    store._internalToExternalId[val] = key;
                }

                // 4. String Metadata Array
                for (int i = 0; i < nodeCap; i++)
                {
                    store._nodeMetadata[i] = reader.ReadString();
                }

                // 5. Raw Array Byte Loads
                if (nodeCap > 0)
                {
                    fs.ReadExactly(MemoryMarshal.AsBytes(store._head.AsSpan(0, nodeCap)));
                }

                if (edgeCap > 0)
                {
                    fs.ReadExactly(MemoryMarshal.AsBytes(store._to.AsSpan(0, edgeCap)));
                    fs.ReadExactly(MemoryMarshal.AsBytes(store._relation.AsSpan(0, edgeCap)));
                    fs.ReadExactly(MemoryMarshal.AsBytes(store._next.AsSpan(0, edgeCap)));

                    if (version >= 2)
                    {
                        fs.ReadExactly(MemoryMarshal.AsBytes(store._weights.AsSpan(0, edgeCap)));
                    }
                    else
                    {
                        Array.Fill(store._weights, 1.0f);
                    }
                }

                return store;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Truncated Glacier Graph file.", ex);
            }
        }

        // --- GRAPH LOGIC ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetInternalId(string externalId, out int internalId)
        {
            ArgumentNullException.ThrowIfNull(externalId);

            EnterReadLock();
            try
            {
                return _externalToInternalId.TryGetValue(externalId, out internalId);
            }
            finally
            {
                ExitReadLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetInternalIdUnsafe(string externalId, out int internalId)
        {
            return _externalToInternalId.TryGetValue(externalId, out internalId);
        }

        public int AddNode(string externalId, string metadata = "")
        {
            ArgumentNullException.ThrowIfNull(externalId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            _rwLock.EnterWriteLock();
            try
            {
                return AddNodeUnsafe(externalId, metadata);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AddNodeUnsafe(string externalId, string metadata)
        {
            if (_externalToInternalId.TryGetValue(externalId, out int existingId))
            {
                if (!string.IsNullOrEmpty(metadata))
                {
                    _nodeMetadata[existingId] = metadata;
                }
                return existingId;
            }

            int newId = ++_nodeCount;

            if (newId >= _head.Length)
            {
                int newSize = Math.Max(newId + 1, _head.Length * 2);
                Array.Resize(ref _head, newSize);
                Array.Resize(ref _nodeMetadata, newSize);
            }

            _externalToInternalId[externalId] = newId;
            _internalToExternalId[newId] = externalId;
            _nodeMetadata[newId] = metadata;

            return newId;
        }

        public void AddEdge(string sourceId, string targetId, string relationType, float weight = 1.0f)
        {
            ArgumentNullException.ThrowIfNull(sourceId);
            ArgumentNullException.ThrowIfNull(targetId);
            ArgumentNullException.ThrowIfNull(relationType);

            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "Edge weight must be a finite non-negative number.");
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            _rwLock.EnterWriteLock();
            try
            {
                int source = AddNodeUnsafe(sourceId, string.Empty);
                int target = AddNodeUnsafe(targetId, string.Empty);

                if (!_relationTypes.TryGetValue(relationType, out int relId))
                {
                    relId = _relationTypes.Count + 1;
                    _relationTypes[relationType] = relId;
                    _relationTypeNames[relId] = relationType;
                }

                int edgeIndex = ++_edgeCount;

                if (edgeIndex >= _to.Length)
                {
                    int oldLength = _weights.Length;
                    int newSize = Math.Max(edgeIndex + 1, _to.Length * 2);
                    Array.Resize(ref _to, newSize);
                    Array.Resize(ref _relation, newSize);
                    Array.Resize(ref _next, newSize);
                    Array.Resize(ref _weights, newSize);
                    Array.Fill(_weights, 1.0f, oldLength, newSize - oldLength);
                }

                _to[edgeIndex] = target;
                _relation[edgeIndex] = relId;
                _weights[edgeIndex] = weight;
                _next[edgeIndex] = _head[source];
                _head[source] = edgeIndex;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Returns a thread-safe outward edge enumerator for the specified external node ID.
        /// Captures an atomic snapshot under the read lock so callers cannot encounter torn reads during concurrent mutations.
        /// </summary>
        public EdgeEnumerator GetOutwardEdges(string sourceId)
        {
            ArgumentNullException.ThrowIfNull(sourceId);

            EnterReadLock();
            try
            {
                if (_externalToInternalId.TryGetValue(sourceId, out int internalId))
                {
                    return SnapshotEdgesUnsafe(internalId);
                }
                return new EdgeEnumerator(Array.Empty<EdgeRecord>());
            }
            finally
            {
                ExitReadLock();
            }
        }

        /// <summary>
        /// Returns a thread-safe outward edge enumerator for the specified internal node ID.
        /// Captures an atomic snapshot under the read lock so callers cannot encounter torn reads during concurrent mutations.
        /// </summary>
        public EdgeEnumerator GetOutwardEdgesByInternalId(int internalId)
        {
            EnterReadLock();
            try
            {
                return SnapshotEdgesUnsafe(internalId);
            }
            finally
            {
                ExitReadLock();
            }
        }

        private EdgeEnumerator SnapshotEdgesUnsafe(int internalId)
        {
            if (internalId <= 0 || internalId > _nodeCount)
            {
                return new EdgeEnumerator(Array.Empty<EdgeRecord>());
            }

            int head = _head[internalId];
            if (head == 0)
            {
                return new EdgeEnumerator(Array.Empty<EdgeRecord>());
            }

            int degree = 0;
            int idx = head;
            while (idx != 0)
            {
                degree++;
                idx = _next[idx];
            }

            var records = new EdgeRecord[degree];
            idx = head;
            int pos = 0;
            while (idx != 0 && pos < degree)
            {
                records[pos++] = new EdgeRecord(_to[idx], _relation[idx], _weights[idx]);
                idx = _next[idx];
            }

            return new EdgeEnumerator(records);
        }

        /// <summary>
        /// Zero-allocation raw enumerator over the Forward Star arrays.
        /// Caller MUST hold <see cref="EnterReadLock"/> for the entire lifetime of iteration.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal RawEdgeEnumerator GetOutwardEdgesUnsafe(int internalId)
        {
            return new RawEdgeEnumerator(internalId, this);
        }

        public string GetNodeMetadata(string externalId)
        {
            ArgumentNullException.ThrowIfNull(externalId);

            EnterReadLock();
            try
            {
                return _externalToInternalId.TryGetValue(externalId, out int id)
                    ? (_nodeMetadata[id] ?? string.Empty)
                    : string.Empty;
            }
            finally
            {
                ExitReadLock();
            }
        }

        public string GetExternalId(int internalId)
        {
            EnterReadLock();
            try
            {
                return GetExternalIdUnsafe(internalId);
            }
            finally
            {
                ExitReadLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string GetExternalIdUnsafe(int internalId)
        {
            return _internalToExternalId.TryGetValue(internalId, out string? id) && id != null ? id : string.Empty;
        }

        public string GetRelationType(int relationId)
        {
            EnterReadLock();
            try
            {
                return GetRelationTypeUnsafe(relationId);
            }
            finally
            {
                ExitReadLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string GetRelationTypeUnsafe(int relationId)
        {
            return _relationTypeNames.TryGetValue(relationId, out string? name) && name != null ? name : string.Empty;
        }

        public int GetNodeDegree(int internalId)
        {
            EnterReadLock();
            try
            {
                return GetNodeDegreeUnsafe(internalId);
            }
            finally
            {
                ExitReadLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetNodeDegreeUnsafe(int internalId)
        {
            if (internalId <= 0 || internalId > _nodeCount) return 0;
            int degree = 0;
            int edgeIndex = _head[internalId];
            while (edgeIndex != 0)
            {
                degree++;
                edgeIndex = _next[edgeIndex];
            }
            return degree;
        }

        public int GetNodeDegree(string externalId)
        {
            ArgumentNullException.ThrowIfNull(externalId);

            EnterReadLock();
            try
            {
                return _externalToInternalId.TryGetValue(externalId, out int id) ? GetNodeDegreeUnsafe(id) : 0;
            }
            finally
            {
                ExitReadLock();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rwLock.Dispose();
            GC.SuppressFinalize(this);
        }

        public readonly record struct EdgeRecord(int TargetNodeId, int RelationId, float Weight);

        public readonly struct ReadLockScope : IDisposable
        {
            private readonly GraphStore _store;

            internal ReadLockScope(GraphStore store)
            {
                _store = store;
            }

            public void Dispose()
            {
                _store?.ExitReadLock();
            }
        }

        public struct EdgeEnumerator
        {
            private readonly EdgeRecord[] _edges;
            private int _index;

            internal EdgeEnumerator(EdgeRecord[] edges)
            {
                _edges = edges ?? Array.Empty<EdgeRecord>();
                _index = 0;
            }

            public EdgeEnumerator(int sourceNodeId, GraphStore store)
            {
                ArgumentNullException.ThrowIfNull(store);
                store.EnterReadLock();
                try
                {
                    this = store.SnapshotEdgesUnsafe(sourceNodeId);
                }
                finally
                {
                    store.ExitReadLock();
                }
            }

            public bool MoveNext() => _edges != null && _index < _edges.Length;

            public int CurrentTargetNodeId => _edges[_index].TargetNodeId;
            public int CurrentRelationId => _edges[_index].RelationId;
            public float CurrentWeight => _edges[_index].Weight;

            public void Advance()
            {
                _index++;
            }
        }

        internal struct RawEdgeEnumerator
        {
            private int _currentEdgeIndex;
            private readonly GraphStore _store;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public RawEdgeEnumerator(int sourceNodeId, GraphStore store)
            {
                _store = store;
                _currentEdgeIndex = sourceNodeId > 0 && sourceNodeId <= store._nodeCount ? store._head[sourceNodeId] : 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext() => _currentEdgeIndex != 0;

            public int CurrentTargetNodeId
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _store._to[_currentEdgeIndex];
            }

            public int CurrentRelationId
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _store._relation[_currentEdgeIndex];
            }

            public float CurrentWeight
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _store._weights[_currentEdgeIndex];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Advance()
            {
                _currentEdgeIndex = _store._next[_currentEdgeIndex];
            }
        }
    }
}
