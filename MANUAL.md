# Glacier.Graph Manual

Glacier.Graph is a zero-allocation, ultra-high-performance C# .NET 10 graph database engine. It provides array-backed in-memory graph representation using the Forward Star format, out-of-core CSR (Compressed Sparse Row) storage with an LRU software page cache, and traversal utilities optimized for AI/agent execution.

---

## 1. Core Architectures

Glacier.Graph contains two distinct storage engines depending on the application context:

### A. GraphStore (In-Memory / Read-Write)
- **Forward Star Representation**: Rather than storing nodes and edges as heap objects, edges are packed contiguously in primitive arrays (`_head`, `_to`, `_relation`, `_next`).
- **Zero-Allocation**: Bypasses the Garbage Collector to avoid GC pauses during large ingestion pipelines.
- **Fast Serialization**: Dumps the raw array byte spans directly to disk for extremely quick load/save times.

### B. CsrGraphStore (Out-of-Core / Read-Only)
- **Compressed Sparse Row (CSR)**: A highly compact layout optimized for static graphs and fast querying.
- **LRU Software Page Cache**: Loads graph structure from disk in 4KB pages and caches them up to a user-defined memory limit (default: 256KB). Permits traversing billion-scale graphs on micro-devices.

---

## 2. In-Memory Graph Operations (`GraphStore`)

```csharp
using Glacier.Graph.Storage;

// Instantiate the database
var store = new GraphStore(initialNodeCapacity: 100_000, initialEdgeCapacity: 500_000);

// Add nodes and relations
store.AddEdge("Alice", "Bob", "knows");
store.AddEdge("Bob", "Charlie", "knows");

// Iterate outward edges of a node
var edges = store.GetOutwardEdges("Alice");
while (edges.MoveNext())
{
    int targetId = edges.CurrentTargetNodeId;
    string targetName = store.GetExternalId(targetId);
    int relationId = edges.CurrentRelationId;
    Console.WriteLine($"Edge: Alice -> {targetName} (Relation ID: {relationId})");
    edges.Advance();
}

// Persist the entire database to disk
store.SaveToDisk("social_graph.bin");
```

---

## 3. Out-of-Core CSR Operations (`CsrGraphStore`)

For low-memory or embedded systems, query serialized graphs directly from disk:

```csharp
using Glacier.Graph.Storage;

// Open the CSR graph with a strict 256KB memory cache limit
using var csrStore = new CsrGraphStore("social_graph.csr", maxMemoryBytes: 256 * 1024);

// Find nodes and relations (loaded on demand via the page cache)
int aliceId = csrStore.GetExternalToInternalId("Alice");
var edges = csrStore.GetOutwardEdgesByInternalId(aliceId);

while (edges.MoveNext())
{
    string targetName = csrStore.GetExternalId(edges.CurrentTargetNodeId);
    Console.WriteLine($"Outward link to: {targetName}");
    edges.Advance();
}
```

---

## 4. Traversal & Pathfinding (`GraphSearch`)

Standard traversal algorithms are built-in:
- **Shortest Path (BFS/Dijkstra)**: Find the minimum path or weighted shortest path between two nodes.
- **Neighborhood / K-Hop Queries**: Find all nodes within K steps of a starting node.

---

## 5. Model Context Protocol (MCP) Integration

Glacier.Graph includes built-in MCP server integration (`GlacierGraphEngine`), exposing the following tools directly to AI agents:
- `add_node`: Creates or updates a node.
- `add_edge`: Creates an edge between source and target.
- `find_shortest_path`: Performs pathfinding queries.
- `find_neighborhood`: Retrieves nodes in the neighborhood of a starting node.
- `save_graph` / `load_graph`: Direct persistence.
