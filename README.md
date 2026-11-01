![Glacier.Graph Banner](assets/banner.jpg)

# Glacier.Graph

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Graph.svg)](https://www.nuget.org/packages/Glacier.Graph/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Graph.svg)](https://www.nuget.org/packages/Glacier.Graph/)
[![Ecosystem](https://img.shields.io/badge/Glacier-Ecosystem-blue)](https://github.com/ian-cowley)
[![DEV.to Story](https://img.shields.io/badge/DEV.to-Story-0a0a0a?style=for-the-badge&logo=devto&logoColor=white)](https://dev.to/iancowley/i-built-a-zero-allocation-c-knowledge-graph-because-jvm-graphs-are-too-bloated-4pej)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-Support-orange.svg?logo=buy-me-a-coffee)](https://buymeacoffee.com/glaciersoftware)

> 📖 **Read the Deep-Dive**: **[I built a zero-allocation C# knowledge graph because JVM graphs are too bloated.](https://dev.to/iancowley/i-built-a-zero-allocation-c-knowledge-graph-because-jvm-graphs-are-too-bloated-4pej)**

```text
========================================================================================================
  GLACIER.GRAPH TRAVERSAL BENCHMARK (ZERO-ALLOCATION FORWARD STAR CSR vs JVM NEO4J)
========================================================================================================
  JVM Neo4j (Heap-Allocated Traversal) :  50 – 200 ms latency per multi-hop query
  Glacier.Graph (Pure C# Contiguous CSR) :  0.010 – 0.650 ms (Sub-Millisecond Multi-Hop Traversal)
--------------------------------------------------------------------------------------------------------
  🏆 SPEEDUP                            :  100x – 500x FASTER TRAVERSAL LATENCY
  ⚡ GC ALLOCATIONS                      :  0 BYTES (Bypasses .NET GC via Contiguous Primitive Arrays)
  ⚡ MEMORY FOOTPRINT                   :  < 10 MB (vs 2+ GB JVM Heap)
========================================================================================================
```

**Glacier.Graph** (`v1.1.0`) is a high-performance, zero-allocation, Native AOT-compatible graph database and traversal engine for **.NET 10**. Designed for AI agents, semantic knowledge graphs, and ultra-low-latency graph analytics, it pairs a concurrent **Forward Star** in-memory store with a memory-mapped / LRU-paged **Compressed Sparse Row (CSR)** engine, AVX-512 SIMD kernels, weighted Dijkstra pathfinding, and a zero-dependency MCP server.

---

## Key Features

*   ⚡ **Zero-Allocation Traversal (`TraversalScratchWorkspace`)**: Pooled primitive buffers and `O(1)` generation-tagged visited tracking achieve **0 bytes** of GC heap allocation across BFS, DFS, weighted Dijkstra, and K-hop neighborhood `Span<int>` queries.
*   🚀 **Thread-Safe Forward Star Engine (`GraphStore`)**: Contiguous primitive arrays (`_head`, `_to`, `_relation`, `_next`, `_weights`) protected by `ReaderWriterLockSlim` prevent torn `Array.Resize` reads under concurrent ingestion and querying.
*   🗜️ **Weighted CSR Memory-Mapped & Out-of-Core Execution (`CsrGraphStore`)**: Compile graphs into the compact **`GCSR` v2** format storing row offsets, column indices, relation IDs, node metadata, and `float` edge weights—queried either via zero-copy `MemoryMappedFile` spans or a strictly bounded 64-bit LRU 4 KB software page cache (`maxMemoryBytes`).
*   🧮 **SIMD Hardware Intrinsics (`CsrKernels`)**: Vectorized neighbor scanning, bitmask index extraction, degree subtraction, and set intersection using `Vector512` (AVX-512), `Vector256` (AVX2), and `Vector128` (SSE/AdvSIMD).
*   💾 **Compact Binary Persistence (`GLGR` v2)**: Serializes only active node and edge slots via `MemoryMarshal.AsBytes` with strict header and truncated-file validation (`InvalidDataException`).
*   🤖 **100% Native AOT MCP Server (`GraphMcpServer`)**: Source-generated `System.Text.Json` (`GraphMcpJsonContext`) JSON-RPC 2.0 server over UTF-8 stdio with atomic state swapping and opt-in `stderr` diagnostics (`GLACIER_GRAPH_MCP_DEBUG=1`).

---

## Installation

Glacier.Graph is available as a [NuGet package](https://www.nuget.org/packages/Glacier.Graph). Install it using the .NET CLI:

```bash
dotnet add package Glacier.Graph
```

---

## Quick Start

### 1. Initialize the Graph Store & Add Weighted Edges

```csharp
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

using var store = new GraphStore(initialNodeCapacity: 100_000, initialEdgeCapacity: 500_000);

store.AddNode("User_1", "Developer");
store.AddNode("User_2", "Manager");

// Create directed relationships with optional non-negative weights (>= 0.0f)
store.AddEdge("User_1", "User_3", "DIRECT_ROUTE", weight: 5.0f);
store.AddEdge("User_1", "User_2", "REPORTS_TO", weight: 0.2f);
store.AddEdge("User_2", "User_3", "KNOWS", weight: 0.2f);
```

### 2. Unweighted BFS, Weighted Dijkstra, & K-Hop Neighborhoods

```csharp
var search = new GraphSearch(store);

// Unweighted BFS (fewest hops: User_1 -> User_3)
List<string> bfsPath = search.FindShortestPath("User_1", "User_3");

// Weighted Dijkstra (lowest total weight 0.4 < 5.0: User_1 -> User_2 -> User_3)
List<string> weightedPath = search.FindShortestPathDijkstra("User_1", "User_3");

// Pure zero-allocation Span<int> query path
Span<int> pathBuffer = stackalloc int[64];
int length = search.FindShortestPathDijkstra(1, 3, pathBuffer);

// Discover all neighbors within 2 hops
List<string> neighbors = search.FindNeighborhood("User_1", maxHops: 2);
```

### 3. Compact RAM-to-Disk Serialization

```csharp
// Persist active graph slots directly to disk (GLGR v2 binary schema)
store.SaveToDisk("graph_database.bin");

// Restore and validate graph database
using GraphStore loadedStore = GraphStore.LoadFromDisk("graph_database.bin");
```

### 4. Memory-Mapped & Out-of-Core Weighted CSR Execution

```csharp
// Compile Forward Star topology + metadata + weights into compact GCSR v2 format
CsrGraphCompiler.CompileFromForwardStar(store, "graph_database.csr");

// Option A: High-throughput Memory-Mapped File (zero-copy Span<int> / Span<float> access)
using var csrMmf = new CsrGraphStore("graph_database.csr", useMemoryMapping: true);
var csrSearch = new CsrGraphSearch(csrMmf);
List<string> optimal = csrSearch.FindShortestPathDijkstra("User_1", "User_3");

// Option B: Strictly bounded 256 KB software LRU page cache for constrained hardware
using var csrPaged = new CsrGraphStore("graph_database.csr", maxMemoryBytes: 256 * 1024, useMemoryMapping: false);
```

---

## MCP Server Setup

Glacier.Graph includes a Native AOT-ready MCP (Model Context Protocol) server runner allowing AI clients (such as Claude Desktop or Antigravity) to query, mutate, and persist graphs in real time.

### 1. Build the Host Runner

```bash
dotnet build src/Glacier.Graph.Host/Glacier.Graph.Host.csproj -c Release
```

### 2. Configure Your Client

```json
{
  "mcpServers": {
    "glacier-graph": {
      "command": "dotnet",
      "args": [
        "ABS_PATH_TO_REPO/src/Glacier.Graph.Host/bin/Release/net10.0/Glacier.Graph.Host.dll",
        "ABS_PATH_TO_DATA/graph_database.bin"
      ]
    }
  }
}
```

### 3. Exposed MCP Tools

*   **`ping`**: Health check returning `"pong"`.
*   **`add_node`**: Registers or updates a node and its metadata.
*   **`add_edge`**: Links two nodes with a typed relationship and optional `weight`.
*   **`find_shortest_path`**: Computes shortest path via BFS or weighted Dijkstra (`"weighted": true`).
*   **`find_neighborhood`**: Discovers all neighbors within `maxHops`.
*   **`save_graph`** / **`load_graph`**: Atomically persists or reloads graph snapshots from disk.

---

## Architecture Documentation

See [ARCHITECTURE.md](ARCHITECTURE.md) for an in-depth technical breakdown of the Forward Star and `GCSR` v2 binary layouts, concurrency model, `TraversalScratchWorkspace` generation tagging, SIMD kernels, and Native AOT MCP pipeline.

---

## 🆕 What's New in v1.1.0

- **Weighted CSR v2 Format & Real CSR Dijkstra** — `CsrGraphCompiler` and `CsrGraphStore` now persist, memory-map, and page per-edge `float` weights (`GCSR` v2 while retaining `v1` read compatibility), powering true weighted `CsrGraphSearch.FindShortestPathDijkstra` including `0.0f` zero-cost edges.
- **Full Concurrency Safety & Compact Serialization** — `GraphStore` (`IDisposable`), `GraphSearch`, and `CsrGraphCompiler` now synchronize read/write scopes across `Array.Resize` mutations with atomic `EdgeEnumerator` snapshots, and eliminate empty capacity bloat when saving `.bin` and `.csr` files.
- **Hardened CSR Bounds & Truncated-File Validation** — Strict header/offset/length validation (`InvalidDataException`), safe out-of-bounds internal ID handling, partial EOF page zeroing, and 64-bit `long` LRU access counters.
- **100% Native AOT Compliance (`<IsAotCompatible>true</IsAotCompatible>`)** — `GraphMcpServer` refactored to source-generated `GraphMcpJsonContext` DTOs, atomic store swapping, and opt-in `stderr` logging (`GLACIER_GRAPH_MCP_DEBUG`).
- **48 unit & adversarial stress tests** passing with `0` warnings and `0` transitive vulnerabilities.

---

## Contributing

We welcome community contributions! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for local setup and PR guidelines.

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## ❄️ Glacier Ecosystem & Support

Part of the **[Glacier Ecosystem](https://glaciersoftware.dev/)** — high-performance, zero-allocation developer tooling and data engines natively built for modern C# and .NET 10.

* 🌐 **Website & Documentation**: [glaciersoftware.dev](https://glaciersoftware.dev/)
* ☕ **Buy Me a Coffee**: [buymeacoffee.com/glaciersoftware](https://buymeacoffee.com/glaciersoftware)
* ❤️ **GitHub Sponsors**: [github.com/sponsors/ian-cowley](https://github.com/sponsors/ian-cowley)
* 🐙 **All Repositories**: [github.com/ian-cowley](https://github.com/ian-cowley)
