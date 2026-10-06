namespace Glacier.Graph.Benchmarks;

using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

[MemoryDiagnoser]
[ShortRunJob]
public class CsrTraversalBenchmarks
{
    private GraphStore _store = null!;
    private GraphSearch _search = null!;
    private CsrGraphStore _csrStore = null!;
    private CsrGraphSearch _csrSearch = null!;
    private string _tempCsrFile = null!;

    private readonly int[] _pathBuffer = new int[1024];

    private const int NodeCount = 5000;
    private const int StartNode = 1;
    private const int TargetNode = 2500;
    private string _startStr = null!;
    private string _targetStr = null!;

    [GlobalSetup]
    public void Setup()
    {
        _store = new GraphStore(initialNodeCapacity: NodeCount + 100, initialEdgeCapacity: NodeCount * 10);
        var rng = new Random(42);

        for (int i = 1; i <= NodeCount; i++)
        {
            _store.AddNode($"Node_{i}");
        }

        for (int i = 1; i < NodeCount; i++)
        {
            _store.AddEdge($"Node_{i}", $"Node_{i + 1}", "STEP", 1.0f);
            if (i % 3 == 0 && i + 10 <= NodeCount)
            {
                _store.AddEdge($"Node_{i}", $"Node_{i + 10}", "SHORTCUT", 0.5f);
            }
            if (i % 5 == 0 && i + 25 <= NodeCount)
            {
                _store.AddEdge($"Node_{i}", $"Node_{i + 25}", "HIGHWAY", 0.2f);
            }
        }

        _startStr = $"Node_{StartNode}";
        _targetStr = $"Node_{TargetNode}";

        _search = new GraphSearch(_store);

        _tempCsrFile = Path.Combine(Path.GetTempPath(), $"bdn_csr_{Guid.NewGuid():N}.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(_store, _tempCsrFile);

        _csrStore = new CsrGraphStore(_tempCsrFile, useMemoryMapping: true);
        _csrSearch = new CsrGraphSearch(_csrStore);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _csrStore.Dispose();
        if (File.Exists(_tempCsrFile))
        {
            try { File.Delete(_tempCsrFile); } catch { }
        }
    }

    [Benchmark(Baseline = true)]
    public int Bfs_ShortestPath_Allocating_Baseline()
    {
        var path = _search.FindShortestPath(_startStr, _targetStr);
        return path.Count;
    }

    [Benchmark]
    public int Bfs_ShortestPath_ZeroAlloc_Scratch()
    {
        return _search.FindShortestPath(StartNode, TargetNode, _pathBuffer.AsSpan());
    }

    [Benchmark]
    public int Dfs_Path_ZeroAlloc()
    {
        return _search.FindPathDfs(StartNode, TargetNode, _pathBuffer.AsSpan());
    }

    [Benchmark]
    public int Dijkstra_ShortestPath_ZeroAlloc()
    {
        return _search.FindShortestPathDijkstra(StartNode, TargetNode, _pathBuffer.AsSpan());
    }

    [Benchmark]
    public int Neighborhood_ZeroAlloc()
    {
        return _search.FindNeighborhood(StartNode, maxHops: 3, _pathBuffer.AsSpan());
    }

    [Benchmark]
    public int Csr_Bfs_ShortestPath_ZeroAlloc()
    {
        return _csrSearch.FindShortestPath(StartNode, TargetNode, _pathBuffer.AsSpan());
    }

    [Benchmark]
    public int Csr_Dijkstra_ZeroAlloc()
    {
        return _csrSearch.FindShortestPathDijkstra(StartNode, TargetNode, _pathBuffer.AsSpan());
    }
}
