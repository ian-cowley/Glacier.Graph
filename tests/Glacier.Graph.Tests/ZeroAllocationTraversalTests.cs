namespace Glacier.Graph.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Xunit;

public class ZeroAllocationTraversalTests : IDisposable
{
    private readonly string _tempDir;

    public ZeroAllocationTraversalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_zero_alloc_test_{Guid.NewGuid():N}");
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
    public void TraversalScratchWorkspace_GenerationReset_AdvancesWithoutClearing()
    {
        using var ws = TraversalScratchWorkspace.Rent(100);

        // Generation 1
        ws.NextGeneration(100);
        int gen1 = ws.CurrentGeneration;
        ws.SetVisited(5);
        ws.SetVisited(42);

        Assert.True(ws.IsVisited(5));
        Assert.True(ws.IsVisited(42));
        Assert.False(ws.IsVisited(10));

        // Generation 2: advances in O(1) time
        ws.NextGeneration(100);
        int gen2 = ws.CurrentGeneration;
        Assert.Equal(gen1 + 1, gen2);

        // In generation 2, previously visited nodes evaluate as false without clearing the array!
        Assert.False(ws.IsVisited(5));
        Assert.False(ws.IsVisited(42));
        Assert.False(ws.IsVisited(10));

        // Mark node in generation 2
        ws.SetVisited(10);
        Assert.True(ws.IsVisited(10));
        Assert.False(ws.IsVisited(5));
    }

    [Fact]
    public void TraversalScratchWorkspace_IntegerOverflow_ClearsArrayAndResetsToOne()
    {
        using var ws = TraversalScratchWorkspace.Rent(50);

        // Simulate reaching near int.MaxValue
        // Set tag manually by advancing or using reflection/internal state simulation
        typeof(TraversalScratchWorkspace)
            .GetField("_currentGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(ws, int.MaxValue - 1);

        ws.SetVisited(7);
        Assert.True(ws.IsVisited(7));

        // Next generation hits int.MaxValue, triggering wrap-around to 1 and Array.Clear
        ws.NextGeneration(50);
        Assert.Equal(1, ws.CurrentGeneration);

        // Previous tag from int.MaxValue - 1 is cleared
        Assert.False(ws.IsVisited(7));

        ws.SetVisited(7);
        Assert.True(ws.IsVisited(7));
    }

    [Fact]
    public void TraversalScratchWorkspace_MinHeap_MatchesPriorityQueueOracle()
    {
        using var ws = TraversalScratchWorkspace.Rent(256);
        var oracle = new PriorityQueue<int, float>();
        var rng = new Random(1337);

        const int count = 200;
        for (int i = 0; i < count; i++)
        {
            int node = rng.Next(1, 1000);
            float prio = (float)Math.Round(rng.NextDouble() * 50.0, 3);

            ws.EnqueueHeap(node, prio);
            oracle.Enqueue(node, prio);
        }

        Assert.Equal(count, ws.HeapCount);
        Assert.Equal(count, oracle.Count);

        float lastPrio = float.NegativeInfinity;
        while (oracle.Count > 0)
        {
            oracle.TryDequeue(out int expectedNode, out float expectedPrio);
            bool dequeued = ws.TryDequeueHeap(out int actualNode, out float actualPrio);

            Assert.True(dequeued);
            Assert.True(actualPrio >= lastPrio, $"Heap property violated: {actualPrio} < {lastPrio}");
            Assert.Equal(expectedPrio, actualPrio);
            lastPrio = actualPrio;
        }

        Assert.Equal(0, ws.HeapCount);
        Assert.False(ws.TryDequeueHeap(out _, out _));
    }

    [Fact]
    public void GraphSearch_ZeroAllocationSpan_MeasuresZeroGCAllocations()
    {
        var store = new GraphStore(initialNodeCapacity: 500, initialEdgeCapacity: 2000);

        // Construct 200-node graph with multiple paths
        for (int i = 1; i <= 200; i++)
        {
            store.AddNode($"Node_{i}");
        }

        for (int i = 1; i < 200; i++)
        {
            store.AddEdge($"Node_{i}", $"Node_{i + 1}", "NEXT", 1.0f);
            if (i % 3 == 0 && i + 5 <= 200)
            {
                store.AddEdge($"Node_{i}", $"Node_{i + 5}", "JUMP", 0.5f);
            }
        }

        var search = new GraphSearch(store);

        // Warmup: primes ThreadStatic workspace and JIT-compiles methods
        Span<int> pathBuffer = stackalloc int[256];
        _ = search.FindShortestPath(1, 150, pathBuffer);
        _ = search.FindPathDfs(1, 150, pathBuffer);
        _ = search.FindShortestPathDijkstra(1, 150, pathBuffer);
        _ = search.FindNeighborhood(1, maxHops: 4, pathBuffer);

        // 1. BFS Shortest Path - Pure Zero-Allocation Verification
        int pathLen = search.FindShortestPath(1, 100, pathBuffer);
        Assert.True(pathLen > 0);
        Assert.Equal(1, pathBuffer[0]);
        Assert.Equal(100, pathBuffer[pathLen - 1]);

        long allocBeforeBfs = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = search.FindShortestPath(1, 100, pathBuffer);
        }
        long allocAfterBfs = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, allocAfterBfs - allocBeforeBfs);

        // 2. DFS Path - Pure Zero-Allocation Verification
        pathLen = search.FindPathDfs(1, 100, pathBuffer);
        Assert.True(pathLen > 0);
        Assert.Equal(1, pathBuffer[0]);
        Assert.Equal(100, pathBuffer[pathLen - 1]);

        long allocBeforeDfs = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = search.FindPathDfs(1, 100, pathBuffer);
        }
        long allocAfterDfs = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, allocAfterDfs - allocBeforeDfs);

        // 3. Dijkstra Shortest Path - Pure Zero-Allocation Verification
        pathLen = search.FindShortestPathDijkstra(1, 100, pathBuffer);
        Assert.True(pathLen > 0);
        Assert.Equal(1, pathBuffer[0]);
        Assert.Equal(100, pathBuffer[pathLen - 1]);

        long allocBeforeDijkstra = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = search.FindShortestPathDijkstra(1, 100, pathBuffer);
        }
        long allocAfterDijkstra = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, allocAfterDijkstra - allocBeforeDijkstra);

        // 4. Neighborhood Traversal - Pure Zero-Allocation Verification
        int count = search.FindNeighborhood(1, maxHops: 3, pathBuffer);
        Assert.True(count > 0);

        long allocBeforeNeigh = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = search.FindNeighborhood(1, maxHops: 3, pathBuffer);
        }
        long allocAfterNeigh = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, allocAfterNeigh - allocBeforeNeigh);
    }

    [Fact]
    public void CsrGraphSearch_ZeroAllocationSpan_MeasuresZeroGCAllocations()
    {
        var store = new GraphStore(initialNodeCapacity: 500, initialEdgeCapacity: 2000);

        for (int i = 1; i <= 100; i++)
        {
            store.AddNode($"V_{i}");
        }

        for (int i = 1; i < 100; i++)
        {
            store.AddEdge($"V_{i}", $"V_{i + 1}", "STEP", 1.0f);
            if (i % 4 == 0 && i + 8 <= 100)
            {
                store.AddEdge($"V_{i}", $"V_{i + 8}", "BYPASS", 1.0f);
            }
        }

        string csrPath = Path.Combine(_tempDir, "zero_alloc_graph.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        using var csrStore = new CsrGraphStore(csrPath, useMemoryMapping: true);
        var csrSearch = new CsrGraphSearch(csrStore);

        // Warmup
        Span<int> pathBuffer = stackalloc int[128];
        _ = csrSearch.FindShortestPath(1, 50, pathBuffer);
        _ = csrSearch.FindPathDfs(1, 50, pathBuffer);
        _ = csrSearch.FindShortestPathDijkstra(1, 50, pathBuffer);
        _ = csrSearch.FindNeighborhood(1, maxHops: 3, pathBuffer);

        // Test BFS Zero-Alloc
        int pathLen = csrSearch.FindShortestPath(1, 50, pathBuffer);
        Assert.True(pathLen > 0);
        Assert.Equal(1, pathBuffer[0]);
        Assert.Equal(50, pathBuffer[pathLen - 1]);

        long beforeBfs = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = csrSearch.FindShortestPath(1, 50, pathBuffer);
        }
        long afterBfs = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterBfs - beforeBfs);

        // Test DFS Zero-Alloc
        pathLen = csrSearch.FindPathDfs(1, 50, pathBuffer);
        Assert.True(pathLen > 0);
        Assert.Equal(1, pathBuffer[0]);
        Assert.Equal(50, pathBuffer[pathLen - 1]);

        long beforeDfs = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = csrSearch.FindPathDfs(1, 50, pathBuffer);
        }
        long afterDfs = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterDfs - beforeDfs);

        // Test Dijkstra Zero-Alloc
        pathLen = csrSearch.FindShortestPathDijkstra(1, 50, pathBuffer);
        Assert.True(pathLen > 0);
        Assert.Equal(1, pathBuffer[0]);
        Assert.Equal(50, pathBuffer[pathLen - 1]);

        long beforeDij = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = csrSearch.FindShortestPathDijkstra(1, 50, pathBuffer);
        }
        long afterDij = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterDij - beforeDij);

        // Test Neighborhood Zero-Alloc
        int count = csrSearch.FindNeighborhood(1, maxHops: 3, pathBuffer);
        Assert.True(count > 0);

        long beforeNeigh = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = csrSearch.FindNeighborhood(1, maxHops: 3, pathBuffer);
        }
        long afterNeigh = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterNeigh - beforeNeigh);
    }

    [Fact]
    public void CsrGraphSearch_FeatureParity_MatchesGraphSearch()
    {
        var store = new GraphStore();
        store.AddEdge("A", "B", "LINK", 1.0f);
        store.AddEdge("B", "C", "LINK", 1.0f);
        store.AddEdge("A", "C", "LINK", 3.0f);
        store.AddEdge("C", "D", "LINK", 1.0f);

        string csrPath = Path.Combine(_tempDir, "parity_graph.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        var searchFs = new GraphSearch(store);
        using var csrStore = new CsrGraphStore(csrPath, useMemoryMapping: true);
        var searchCsr = new CsrGraphSearch(csrStore);

        // BFS path
        var bfsFs = searchFs.FindShortestPath("A", "D");
        var bfsCsr = searchCsr.FindShortestPath("A", "D");
        Assert.Equal(bfsFs, bfsCsr);

        // DFS path
        var dfsFs = searchFs.FindPathDfs("A", "D");
        var dfsCsr = searchCsr.FindPathDfs("A", "D");
        Assert.NotEmpty(dfsCsr);
        Assert.Equal("A", dfsCsr[0]);
        Assert.Equal("D", dfsCsr[^1]);

        // Neighborhood
        var neighFs = searchFs.FindNeighborhood("A", maxHops: 2);
        var neighCsr = searchCsr.FindNeighborhood("A", maxHops: 2);
        Assert.Equal(neighFs.Count, neighCsr.Count);
        foreach (var n in neighFs) Assert.Contains(n, neighCsr);
    }
}
