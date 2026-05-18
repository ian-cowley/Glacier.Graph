using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// A zero-allocation, array-backed Graph database using the Forward Star representation.
    /// Bypasses the GC by storing edges in primitive contiguous arrays instead of objects.
    /// </summary>
    public class GraphStore
    {
        // --- Node Maps ---
        private readonly Dictionary<string, int> _externalToInternalId = new();
        private readonly Dictionary<int, string> _internalToExternalId = new();
        private readonly Dictionary<string, int> _relationTypes = new();

        private string[] _nodeMetadata;
        private int _nodeCount = 0;

        // --- Forward Star Edge Arrays ---
        private int[] _head;
        private int[] _to;
        private int[] _relation;
        private int[] _next;
        private int _edgeCount = 0;

        public int NodeCount => _nodeCount;
        public int EdgeCount => _edgeCount;

        public GraphStore(int initialNodeCapacity = 100_000, int initialEdgeCapacity = 500_000)
        {
            _nodeMetadata = new string[initialNodeCapacity];
            _head = new int[initialNodeCapacity];
            _to = new int[initialEdgeCapacity + 1];
            _relation = new int[initialEdgeCapacity + 1];
            _next = new int[initialEdgeCapacity + 1];
        }

        // --- PERSISTENCE (RAM-to-Disk dumping) ---

        public void SaveToDisk(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            using var writer = new BinaryWriter(fs);

            // 1. Header (Magic Bytes + Version + Capacities)
            writer.Write("GLGR");
            writer.Write(1);
            writer.Write(_nodeCount);
            writer.Write(_edgeCount);
            writer.Write(_head.Length);
            writer.Write(_to.Length);

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

            // 4. String Metadata Array
            for (int i = 0; i < _head.Length; i++)
            {
                writer.Write(_nodeMetadata[i] ?? string.Empty);
            }

            // 5. Raw Array Byte Dumps (The fast part)
            fs.Write(MemoryMarshal.AsBytes(_head.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(_to.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(_relation.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(_next.AsSpan()));
        }

        public static GraphStore LoadFromDisk(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(fs);

            // 1. Header
            if (reader.ReadString() != "GLGR") throw new Exception("Invalid Glacier Graph file format.");
            int version = reader.ReadInt32(); // Reserved for future schema updates

            int nodeCount = reader.ReadInt32();
            int edgeCount = reader.ReadInt32();
            int nodeCap = reader.ReadInt32();
            int edgeCap = reader.ReadInt32();

            var store = new GraphStore(nodeCap, edgeCap - 1);
            store._nodeCount = nodeCount;
            store._edgeCount = edgeCount;

            // 2. Relation Types
            int relCount = reader.ReadInt32();
            for (int i = 0; i < relCount; i++)
            {
                store._relationTypes[reader.ReadString()] = reader.ReadInt32();
            }

            // 3. Node ID Mappings
            int idCount = reader.ReadInt32();
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

            // 5. Raw Array Byte Loads (Reading directly from disk into RAM)
            fs.ReadExactly(MemoryMarshal.AsBytes(store._head.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(store._to.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(store._relation.AsSpan()));
            fs.ReadExactly(MemoryMarshal.AsBytes(store._next.AsSpan()));

            return store;
        }

        // --- GRAPH LOGIC ---

        public int AddNode(string externalId, string metadata = "")
        {
            if (_externalToInternalId.TryGetValue(externalId, out int existingId))
            {
                _nodeMetadata[existingId] = metadata;
                return existingId;
            }

            int newId = ++_nodeCount;

            if (newId >= _head.Length)
            {
                int newSize = _head.Length * 2;
                Array.Resize(ref _head, newSize);
                Array.Resize(ref _nodeMetadata, newSize);
            }

            _externalToInternalId[externalId] = newId;
            _internalToExternalId[newId] = externalId;
            _nodeMetadata[newId] = metadata;

            return newId;
        }

        public void AddEdge(string sourceId, string targetId, string relationType)
        {
            int source = AddNode(sourceId);
            int target = AddNode(targetId);

            if (!_relationTypes.TryGetValue(relationType, out int relId))
            {
                relId = _relationTypes.Count + 1;
                _relationTypes[relationType] = relId;
            }

            int edgeIndex = ++_edgeCount;

            if (edgeIndex >= _to.Length)
            {
                int newSize = _to.Length * 2;
                Array.Resize(ref _to, newSize);
                Array.Resize(ref _relation, newSize);
                Array.Resize(ref _next, newSize);
            }

            _to[edgeIndex] = target;
            _relation[edgeIndex] = relId;
            _next[edgeIndex] = _head[source];
            _head[source] = edgeIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EdgeEnumerator GetOutwardEdges(string sourceId)
        {
            if (_externalToInternalId.TryGetValue(sourceId, out int internalId))
            {
                return new EdgeEnumerator(internalId, this);
            }
            return new EdgeEnumerator(0, this);
        }

        public string GetNodeMetadata(string externalId) =>
            _externalToInternalId.TryGetValue(externalId, out int id) ? _nodeMetadata[id] : string.Empty;

        public string GetExternalId(int internalId) =>
            _internalToExternalId.TryGetValue(internalId, out string id) ? id : string.Empty;

        public struct EdgeEnumerator
        {
            private int _currentEdgeIndex;
            private readonly GraphStore _store;

            public EdgeEnumerator(int sourceNodeId, GraphStore store)
            {
                _store = store;
                _currentEdgeIndex = sourceNodeId > 0 ? store._head[sourceNodeId] : 0;
            }

            public bool MoveNext() => _currentEdgeIndex != 0;

            public int CurrentTargetNodeId => _store._to[_currentEdgeIndex];
            public int CurrentRelationId => _store._relation[_currentEdgeIndex];

            public void Advance()
            {
                _currentEdgeIndex = _store._next[_currentEdgeIndex];
            }
        }
    }
}