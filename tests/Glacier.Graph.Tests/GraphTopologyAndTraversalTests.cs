namespace Glacier.Graph.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Xunit;

public class GraphTopologyAndTraversalTests : IDisposable
{
    private readonly string _tempDir;

    public GraphTopologyAndTraversalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_graph_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ForwardStar_AddNodeAndEdge_PreservesTopologyAndDegrees()
    {
        var store = new GraphStore(initialNodeCapacity: 16, initialEdgeCapacity: 32);

        int a = store.AddNode("NodeA", "MetaA");
        int b = store.AddNode("NodeB", "MetaB");
        int c = store.AddNode("NodeC", "MetaC");

        store.AddEdge("NodeA", "NodeB", "POINTS_TO", 1.5f);
        store.AddEdge("NodeA", "NodeC", "DEPENDS_ON", 2.5f);

        Assert.Equal(3, store.NodeCount);
        Assert.Equal(2, store.EdgeCount);
        Assert.Equal(2, store.GetNodeDegree("NodeA"));
        Assert.Equal(0, store.GetNodeDegree("NodeB"));
        Assert.Equal("MetaA", store.GetNodeMetadata("NodeA"));

        // Verify enumerator
        var edges = store.GetOutwardEdges("NodeA");
        var targets = new List<int>();
        var weights = new List<float>();
        while (edges.MoveNext())
        {
            targets.Add(edges.CurrentTargetNodeId);
            weights.Add(edges.CurrentWeight);
            edges.Advance();
        }
        Assert.Contains(b, targets);
        Assert.Contains(c, targets);
        Assert.Contains(1.5f, weights);
        Assert.Contains(2.5f, weights);
    }

    [Fact]
    public void ReadQueries_NeverMutateGraph_WhenNodesDoNotExist()
    {
        var store = new GraphStore();
        store.AddEdge("NodeA", "NodeB", "CONNECTED");
        int initialNodeCount = store.NodeCount;
        int initialEdgeCount = store.EdgeCount;

        var search = new GraphSearch(store);

        // Search for nonexistent nodes
        var path = search.FindShortestPath("GhostX", "GhostY");
        Assert.Empty(path);

        var dfs = search.FindPathDfs("GhostX", "NodeA");
        Assert.Empty(dfs);

        var dijkstra = search.FindShortestPathDijkstra("NodeA", "GhostY");
        Assert.Empty(dijkstra);

        var neighborhood = search.FindNeighborhood("GhostX", maxHops: 3);
        Assert.Empty(neighborhood);

        var triplets = search.FindNeighborhoodTriplets("GhostX", maxHops: 2);
        Assert.Empty(triplets);

        // NodeCount and EdgeCount must be strictly unchanged!
        Assert.Equal(initialNodeCount, store.NodeCount);
        Assert.Equal(initialEdgeCount, store.EdgeCount);
    }

    [Fact]
    public void BfsShortestPath_FindsOptimalPath_InComplexGraph()
    {
        var store = new GraphStore();
        var search = new GraphSearch(store);

        // Construct: S -> A -> B -> T (length 3) and S -> C -> T (length 2)
        store.AddEdge("S", "A", "LINK");
        store.AddEdge("A", "B", "LINK");
        store.AddEdge("B", "T", "LINK");
        store.AddEdge("S", "C", "LINK");
        store.AddEdge("C", "T", "LINK");

        var path = search.FindShortestPath("S", "T");

        Assert.Equal(new[] { "S", "C", "T" }, path);
    }

    [Fact]
    public void BfsShortestPath_ReturnsEmpty_WhenNoPathExists()
    {
        var store = new GraphStore();
        var search = new GraphSearch(store);

        store.AddEdge("A", "B", "LINK");
        store.AddEdge("C", "D", "LINK");

        var path = search.FindShortestPath("A", "D");
        Assert.Empty(path);
    }

    [Fact]
    public void DfsTraversal_FindsValidPath_InDirectedAcyclicGraph()
    {
        var store = new GraphStore();
        var search = new GraphSearch(store);

        // Path: Start -> Step1 -> Step2 -> Goal
        store.AddEdge("Start", "Step1", "STEP");
        store.AddEdge("Step1", "Step2", "STEP");
        store.AddEdge("Step2", "Goal", "STEP");

        var path = search.FindPathDfs("Start", "Goal");

        Assert.NotEmpty(path);
        Assert.Equal("Start", path[0]);
        Assert.Equal("Goal", path[^1]);
    }

    [Fact]
    public void DijkstraShortestPath_RespectsEdgeWeights()
    {
        var store = new GraphStore();
        var search = new GraphSearch(store);

        // S -> Direct -> T with weight 10.0
        // S -> Cheap1 -> Cheap2 -> T with total weight 1.0 + 1.0 + 1.0 = 3.0
        store.AddEdge("S", "Direct", "HIGHWAY", weight: 10.0f);
        store.AddEdge("Direct", "T", "HIGHWAY", weight: 10.0f);

        store.AddEdge("S", "Cheap1", "PATH", weight: 1.0f);
        store.AddEdge("Cheap1", "Cheap2", "PATH", weight: 1.0f);
        store.AddEdge("Cheap2", "T", "PATH", weight: 1.0f);

        var weightedPath = search.FindShortestPathDijkstra("S", "T");

        // Dijkstra must pick the cheaper path even though it has more hops!
        Assert.Equal(new[] { "S", "Cheap1", "Cheap2", "T" }, weightedPath);
    }

    [Fact]
    public void FindNeighborhood_RespectsMaxHops()
    {
        var store = new GraphStore();
        var search = new GraphSearch(store);

        // Chain: A -> B -> C -> D -> E
        store.AddEdge("A", "B", "LINK");
        store.AddEdge("B", "C", "LINK");
        store.AddEdge("C", "D", "LINK");
        store.AddEdge("D", "E", "LINK");

        var hop1 = search.FindNeighborhood("A", maxHops: 1);
        Assert.Single(hop1);
        Assert.Contains("B", hop1);

        var hop2 = search.FindNeighborhood("A", maxHops: 2);
        Assert.Equal(2, hop2.Count);
        Assert.Contains("B", hop2);
        Assert.Contains("C", hop2);
    }

    [Fact]
    public void FindNeighborhoodTriplets_ExtractsPredicatesAndLimitsHubDegrees()
    {
        var store = new GraphStore();
        var search = new GraphSearch(store);

        store.AddEdge("Hub", "Service1", "ROUTES_TO");
        store.AddEdge("Hub", "Service2", "ROUTES_TO");
        store.AddEdge("Hub", "Service3", "ROUTES_TO");
        store.AddEdge("Service1", "Db", "WRITES_TO");

        var triplets = search.FindNeighborhoodTriplets("Hub", maxHops: 2, maxDegreePerNode: 2, maxTriplets: 5);

        Assert.NotEmpty(triplets);
        Assert.Contains(triplets, t => t.Source == "Hub" && t.Predicate == "ROUTES_TO");
        Assert.True(triplets.Count <= 5);
    }

    [Fact]
    public void Persistence_SaveToDiskAndLoadFromDisk_RoundtripsWeights()
    {
        var store = new GraphStore();
        store.AddEdge("NodeX", "NodeY", "RELATES", 4.2f);
        store.AddEdge("NodeY", "NodeZ", "DEPENDS", 1.8f);

        string binPath = Path.Combine(_tempDir, "graph_v2.bin");
        store.SaveToDisk(binPath);

        Assert.True(File.Exists(binPath));

        var loaded = GraphStore.LoadFromDisk(binPath);
        Assert.Equal(store.NodeCount, loaded.NodeCount);
        Assert.Equal(store.EdgeCount, loaded.EdgeCount);

        var search = new GraphSearch(loaded);
        var path = search.FindShortestPathDijkstra("NodeX", "NodeZ");
        Assert.Equal(new[] { "NodeX", "NodeY", "NodeZ" }, path);
    }

    [Fact]
    public void CsrCompiler_CompilesAndRoundtrips_WithCsrGraphStore()
    {
        var store = new GraphStore();
        store.AddEdge("User1", "DocA", "AUTHORED");
        store.AddEdge("User1", "DocB", "REVIEWED");
        store.AddEdge("DocA", "DocB", "REFERENCES");

        string binPath = Path.Combine(_tempDir, "graph.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, binPath);

        Assert.True(File.Exists(binPath));

        // Load via CsrGraphStore with MMF enabled
        using var csrMmf = new CsrGraphStore(binPath, useMemoryMapping: true);
        Assert.Equal(store.NodeCount, csrMmf.NodeCount);
        Assert.Equal(store.EdgeCount, csrMmf.EdgeCount);

        var csrSearchMmf = new CsrGraphSearch(csrMmf);
        var pathMmf = csrSearchMmf.FindShortestPath("User1", "DocB");
        Assert.Equal(new[] { "User1", "DocB" }, pathMmf);

        // Load via CsrGraphStore with Software Page Cache (MMF disabled)
        using var csrPaged = new CsrGraphStore(binPath, maxMemoryBytes: 4096, useMemoryMapping: false);
        var csrSearchPaged = new CsrGraphSearch(csrPaged);
        var pathPaged = csrSearchPaged.FindShortestPath("User1", "DocB");
        Assert.Equal(new[] { "User1", "DocB" }, pathPaged);
    }

    [Fact]
    public void Concurrency_MultiThreadedTraversals_NeverCorruptState()
    {
        var store = new GraphStore();
        for (int i = 0; i < 100; i++)
        {
            store.AddEdge($"Node_{i}", $"Node_{i + 1}", "STEP", (float)(i + 1));
            store.AddEdge($"Node_{i}", $"Node_{(i * 2) % 100}", "JUMP", 5.0f);
        }

        var search = new GraphSearch(store);

        // Run 32 concurrent read queries across multiple threads
        Parallel.For(0, 32, _ =>
        {
            var path = search.FindShortestPath("Node_0", "Node_50");
            Assert.NotEmpty(path);
            Assert.Equal("Node_0", path[0]);
            Assert.Equal("Node_50", path[^1]);

            var dijkstra = search.FindShortestPathDijkstra("Node_0", "Node_10");
            Assert.NotEmpty(dijkstra);

            var neighborhood = search.FindNeighborhood("Node_0", maxHops: 2);
            Assert.NotEmpty(neighborhood);
        });
    }
}
