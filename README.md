# Glacier.Graph

[![DEV.to Story](https://img.shields.io/badge/DEV.to-Story-0a0a0a?style=for-the-badge&logo=devto&logoColor=white)](https://dev.to/iancowley/i-built-a-zero-allocation-c-knowledge-graph-because-jvm-graphs-are-too-bloated-4pej)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Graph.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Graph/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Graph.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Graph/)

> 📖 **Read the Deep-Dive**: **[I built a zero-allocation C# knowledge graph because JVM graphs are too bloated.](https://dev.to/iancowley/i-built-a-zero-allocation-c-knowledge-graph-because-jvm-graphs-are-too-bloated-4pej)**


**Glacier.Graph** is a high-performance, zero-allocation array-backed graph database and traversal engine for .NET 10. Designed for AI agents, semantic reasoning, and ultra-low latency graph operations, it uses a **Forward Star representation** to pack nodes, edges, and relationships in primitive contiguous arrays, completely bypassing Garbage Collector (GC) pressure and maximizing CPU cache hits.

---

## Key Features

*   ⚡ **Zero-Allocation Architecture**: Stores all nodes, edges, and relationships inside contiguous primitive arrays (`int[]`). Bypasses the .NET Garbage Collector for high-frequency traversal operations.
*   🚀 **Forward Star Representation**: An optimized data structure representation for graphs that guarantees sequential memory access and extreme traversal performance.
*   🧠 **High-Speed Traversal**: Features built-in, cache-line optimized algorithms for Breadth-First Search (BFS) and hop-distance neighborhood discovery.
*   💾 **RAM-to-Disk Dumping**: Support for ultra-fast, zero-copy serialization (`GLGR` binary format) to quickly dump and revive huge graphs from disk in milliseconds.
*   🤖 **MCP Server Support**: Includes a built-in Model Context Protocol (MCP) server interface, making it seamlessly compatible with LLM agents (like Claude or Antigravity) as an active memory reasoning tool.

---

## Installation

Glacier.Graph is available as a [NuGet package](https://www.nuget.org/packages/Glacier.Graph). Install it using the .NET CLI:

```bash
dotnet add package Glacier.Graph
```

---

## Quick Start

### 1. Initialize the Graph Store

```csharp
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

// Instantiate the GC-optimized graph store
var store = new GraphStore(initialNodeCapacity: 100_000, initialEdgeCapacity: 500_000);
```

### 2. Add Nodes and Edges

```csharp
// Add nodes and define metadata
store.AddNode("User_1", "Developer");
store.AddNode("User_2", "Manager");

// Create directed relationships
store.AddEdge("User_1", "User_2", "REPORTS_TO");
store.AddEdge("User_2", "User_3", "KNOWS");
```

### 3. Traversal and Path Finding

```csharp
var search = new GraphSearch(store);

// Find the shortest path using ultra-fast BFS
List<string> path = search.FindShortestPath("User_1", "User_3");
Console.WriteLine($"Path: {string.Join(" -> ", path)}");
// Output: Path: User_1 -> User_2 -> User_3

// Find all neighbors within a 2-hop radius
List<string> neighbors = search.FindNeighborhood("User_1", maxHops: 2);
Console.WriteLine($"Neighbors: {string.Join(", ", neighbors)}");
```

### 4. RAM-to-Disk Serialization

```csharp
// Save the raw arrays directly to disk (GLGR binary schema)
store.SaveToDisk("graph_database.bin");

// Instantly restore and revive the graph database on startup
GraphStore loadedStore = GraphStore.LoadFromDisk("graph_database.bin");
```

---

## MCP Server Setup

Glacier.Graph contains a dedicated MCP (Model Context Protocol) server runner. This allows AI clients (like Claude Desktop or developer agents) to query, insert, and navigate the graph in real-time.

### 1. Compile the Host Runner
Build the runner console application in Release mode:

```bash
dotnet build src/Glacier.Graph.Host/Glacier.Graph.Host.csproj -c Release
```

### 2. Configure Your Client
Add the following entry to your `mcp_config.json` (usually located in your agent's config directory, or `%AppData%\Roaming\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "glacier-graph": {
      "command": "dotnet",
      "args": [
        "ABS_PATH_TO_REPO/src/Glacier.Graph.Host/bin/Release/net10.0/Glacier.Graph.Host.dll",
        "ABS_PATH_TO_REPO/graph_database.bin"
      ]
    }
  }
}
```

### 3. Exposed MCP Tools

*   **`ping`**: Simple health check to verify server responsiveness.
*   **`add_node`**: Registers or updates a node and its metadata.
*   **`add_edge`**: Links two nodes with a typed relationship.
*   **`find_shortest_path`**: Computes the shortest sequence of nodes connecting two IDs.
*   **`find_neighborhood`**: Discovers all neighbors within a specified hop distance.
*   **`save_graph`**: Persists active memory arrays to a disk file.
*   **`load_graph`**: Replaces the active memory database with a persisted disk file.

---

## Architecture Overview

1.  **Forward Star Storage**: Contiguous arrays `_head`, `_to`, `_relation`, and `_next` manage relations safely inside primitive integer indices.
2.  **GC Bypass**: De-allocates traditional object-oriented object nodes, leveraging standard struct `EdgeEnumerator` loops to walk paths.
3.  **Flat Array Tracking**: Traversal algorithms leverage flat `bool[]` arrays pre-sized to Node Capacity to eliminate hash-set map collisions during hops.
4.  **BOM-Free Connection**: The stdio channel enforces standard UTF-8 stream writer loops (without Byte Order Marks) to ensure smooth communication with Node-based MCP orchestrators.

---

## Contributing

We welcome community contributions! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for local setups, branch models, and PR checklist details.

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
