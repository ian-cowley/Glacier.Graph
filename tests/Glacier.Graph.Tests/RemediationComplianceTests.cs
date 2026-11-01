namespace Glacier.Graph.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Glacier.Graph.Mcp;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Xunit;

public class RemediationComplianceTests : IDisposable
{
    private readonly string _tempDir;

    public RemediationComplianceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_remediation_test_{Guid.NewGuid():N}");
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
    public void CsrDijkstra_WeightedEdges_PrefersMultiHopLowWeightOverDirectHighWeight()
    {
        using var store = new GraphStore(initialNodeCapacity: 16, initialEdgeCapacity: 32);

        store.AddNode("S", "StartNode");
        store.AddNode("Mid", "RelayNode");
        store.AddNode("T", "TargetNode");

        // Direct edge S -> T has high weight 5.0
        // Multi-hop S -> Mid -> T has low weight 0.2 + 0.2 = 0.4
        store.AddEdge("S", "T", "DIRECT_EXPENSIVE", 5.0f);
        store.AddEdge("S", "Mid", "FAST_HOP", 0.2f);
        store.AddEdge("Mid", "T", "FAST_HOP", 0.2f);

        string csrPath = Path.Combine(_tempDir, "weighted_dijkstra.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        // 1. Verify Memory-Mapped CSR mode
        using (var csrMmf = new CsrGraphStore(csrPath, useMemoryMapping: true))
        {
            Assert.Equal("StartNode", csrMmf.GetNodeMetadata("S"));
            Assert.Equal("RelayNode", csrMmf.GetNodeMetadata("Mid"));

            var csrSearchMmf = new CsrGraphSearch(csrMmf);

            // Unweighted BFS picks direct 1-hop S -> T
            var bfsPath = csrSearchMmf.FindShortestPath("S", "T");
            Assert.Equal(["S", "T"], bfsPath);

            // Weighted Dijkstra MUST pick S -> Mid -> T (cost 0.4 < 5.0)
            var dijkstraPath = csrSearchMmf.FindShortestPathDijkstra("S", "T");
            Assert.Equal(["S", "Mid", "T"], dijkstraPath);

            // Verify zero-alloc Span<int> overload also picks S -> Mid -> T
            int sId = csrMmf.GetExternalToInternalId("S");
            int midId = csrMmf.GetExternalToInternalId("Mid");
            int tId = csrMmf.GetExternalToInternalId("T");

            Span<int> buf = stackalloc int[8];
            int len = csrSearchMmf.FindShortestPathDijkstra(sId, tId, buf);
            Assert.Equal(3, len);
            Assert.Equal(sId, buf[0]);
            Assert.Equal(midId, buf[1]);
            Assert.Equal(tId, buf[2]);
        }

        // 2. Verify Software LRU Page Cache CSR mode (MMF disabled)
        using (var csrPaged = new CsrGraphStore(csrPath, maxMemoryBytes: 4096, useMemoryMapping: false))
        {
            var csrSearchPaged = new CsrGraphSearch(csrPaged);
            var dijkstraPaged = csrSearchPaged.FindShortestPathDijkstra("S", "T");
            Assert.Equal(["S", "Mid", "T"], dijkstraPaged);

            int sId = csrPaged.GetExternalToInternalId("S");
            int midId = csrPaged.GetExternalToInternalId("Mid");
            int tId = csrPaged.GetExternalToInternalId("T");

            Span<int> buf = stackalloc int[8];
            int len = csrSearchPaged.FindShortestPathDijkstra(sId, tId, buf);
            Assert.Equal(3, len);
            Assert.Equal(sId, buf[0]);
            Assert.Equal(midId, buf[1]);
            Assert.Equal(tId, buf[2]);
        }
    }

    [Fact]
    public void ZeroWeightEdges_NotCoercedToOne_InBothGraphSearchAndCsrGraphSearch()
    {
        using var store = new GraphStore();

        // Direct path: S -> Direct -> T has cost 0.4 + 0.4 = 0.8
        // Zero-weight bridge path: S -> Z1 (0.0) -> Z2 (0.0) -> Z3 (0.0) -> T (0.1) has total cost 0.1!
        // Also add a zero-weight cycle Z1 <-> Z2 to ensure Dijkstra terminates cleanly.
        store.AddEdge("S", "Direct", "PAID", 0.4f);
        store.AddEdge("Direct", "T", "PAID", 0.4f);

        store.AddEdge("S", "Z1", "FREE", 0.0f);
        store.AddEdge("Z1", "Z2", "FREE", 0.0f);
        store.AddEdge("Z2", "Z1", "FREE_CYCLE", 0.0f);
        store.AddEdge("Z2", "Z3", "FREE", 0.0f);
        store.AddEdge("Z3", "T", "LAST_MILE", 0.1f);

        var fsSearch = new GraphSearch(store);
        var fsPath = fsSearch.FindShortestPathDijkstra("S", "T");
        Assert.Equal(["S", "Z1", "Z2", "Z3", "T"], fsPath);

        string csrPath = Path.Combine(_tempDir, "zero_weights.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        using var csrStore = new CsrGraphStore(csrPath, useMemoryMapping: true);
        var csrSearch = new CsrGraphSearch(csrStore);
        var csrPathResult = csrSearch.FindShortestPathDijkstra("S", "T");
        Assert.Equal(["S", "Z1", "Z2", "Z3", "T"], csrPathResult);
    }

    [Fact]
    public void NegativeAndInvalidWeights_ThrowExpectedExceptions()
    {
        using var store = new GraphStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddEdge("A", "B", "NEG", -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddEdge("A", "B", "NAN", float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddEdge("A", "B", "INF", float.PositiveInfinity));

        // Simulate corrupt negative weight in internal array to verify Dijkstra guard
        store.AddEdge("A", "B", "VALID", 1.0f);
        store._weights[1] = -2.0f;

        var search = new GraphSearch(store);
        Assert.Throws<InvalidOperationException>(() => search.FindShortestPathDijkstra("A", "B"));
    }

    [Fact]
    public void CompactSerialization_EliminatesEmptyCapacityBloat_AndHandlesEmptyGraph()
    {
        // Default capacities are 100,000 nodes and 500,000 edges
        using var store = new GraphStore();
        store.AddEdge("Alpha", "Beta", "LINKS", 0.75f);

        string binPath = Path.Combine(_tempDir, "compact.bin");
        string csrPath = Path.Combine(_tempDir, "compact.gcsr");

        store.SaveToDisk(binPath);
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        // Both files must be tiny (< 4 KB), NOT 8-10 MB of empty capacity slots!
        Assert.True(new FileInfo(binPath).Length < 4096, $"Expected compact .bin < 4KB, got {new FileInfo(binPath).Length} bytes");
        Assert.True(new FileInfo(csrPath).Length < 4096, $"Expected compact .gcsr < 4KB, got {new FileInfo(csrPath).Length} bytes");

        // Verify empty graph roundtrip + crafted edgeCap == 0 file
        using var emptyStore = new GraphStore();
        string emptyBin = Path.Combine(_tempDir, "empty.bin");
        emptyStore.SaveToDisk(emptyBin);

        using var loadedEmpty = GraphStore.LoadFromDisk(emptyBin);
        Assert.Equal(0, loadedEmpty.NodeCount);
        Assert.Equal(0, loadedEmpty.EdgeCount);
        loadedEmpty.AddEdge("NewA", "NewB", "REL", 1.25f);
        Assert.Equal(2, loadedEmpty.NodeCount);
        Assert.Equal(1, loadedEmpty.EdgeCount);

        // Crafted v2 header with nodeCount=0, edgeCount=0, nodeCap=0, edgeCap=0
        string zeroCapBin = Path.Combine(_tempDir, "zero_cap.bin");
        using (var fs = new FileStream(zeroCapBin, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("GLGR");
            writer.Write(2);
            writer.Write(0); // nodeCount
            writer.Write(0); // edgeCount
            writer.Write(0); // nodeCap
            writer.Write(0); // edgeCap
            writer.Write(0); // relCount
            writer.Write(0); // idCount
        }

        using var loadedZeroCap = GraphStore.LoadFromDisk(zeroCapBin);
        Assert.Equal(0, loadedZeroCap.NodeCount);
        Assert.Equal(0, loadedZeroCap.EdgeCount);
    }

    [Fact]
    public async Task Concurrency_SimultaneousArrayResizeWritesAndTraversals_ZeroTornReadsOrExceptions()
    {
        // Tiny initial capacities force frequent Array.Resize mutations during concurrent reads
        using var store = new GraphStore(initialNodeCapacity: 2, initialEdgeCapacity: 2);
        store.AddEdge("Seed_0", "Seed_1", "INIT", 0.5f);

        var search = new GraphSearch(store);
        var errors = new ConcurrentBag<Exception>();

        var writerTasks = new Task[4];
        for (int w = 0; w < writerTasks.Length; w++)
        {
            int workerId = w;
            writerTasks[w] = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 400; i++)
                    {
                        string src = i == 0 ? "Seed_0" : $"W{workerId}_N{i - 1}";
                        string dst = $"W{workerId}_N{i}";
                        store.AddNode(dst, $"Meta_{workerId}_{i}");
                        store.AddEdge(src, dst, "CHAIN", (i % 5) * 0.25f);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        var readerTasks = new Task[8];
        for (int r = 0; r < readerTasks.Length; r++)
        {
            int readerId = r;
            readerTasks[r] = Task.Run(() =>
            {
                try
                {
                    Span<int> spanBuf = stackalloc int[64];
                    for (int iter = 0; iter < 300; iter++)
                    {
                        string target = $"W{readerId % 4}_N{iter % 200}";
                        _ = search.FindShortestPath("Seed_0", target);
                        _ = search.FindPathDfs("Seed_0", target);
                        _ = search.FindShortestPathDijkstra("Seed_0", target);
                        _ = search.FindNeighborhood("Seed_0", maxHops: 3);
                        _ = search.FindNeighborhoodTriplets("Seed_0", maxHops: 2, maxDegreePerNode: 10, maxTriplets: 25);
                        _ = search.FindShortestPath(1, 2, spanBuf);

                        var edges = store.GetOutwardEdges("Seed_0");
                        while (edges.MoveNext())
                        {
                            _ = edges.CurrentTargetNodeId;
                            _ = edges.CurrentRelationId;
                            _ = edges.CurrentWeight;
                            edges.Advance();
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        await Task.WhenAll([.. writerTasks, .. readerTasks]);
        Assert.Empty(errors);
        Assert.Equal(2 + 4 * 400, store.NodeCount);
        Assert.Equal(1 + 4 * 400, store.EdgeCount);
    }

    [Fact]
    public void CsrGraphStore_TruncatedFilesAndOutOfBoundsIds_HandledSafely()
    {
        using var store = new GraphStore();
        for (int i = 0; i < 10; i++)
        {
            store.AddEdge($"N{i}", $"N{i + 1}", "NEXT", (i + 1) * 0.5f);
        }

        string validCsrPath = Path.Combine(_tempDir, "valid.csr");
        CsrGraphCompiler.CompileFromForwardStar(store, validCsrPath);
        byte[] fullBytes = File.ReadAllBytes(validCsrPath);

        // 1. Truncated header (3 bytes)
        string truncHeaderPath = Path.Combine(_tempDir, "trunc_header.csr");
        File.WriteAllBytes(truncHeaderPath, fullBytes[..3]);
        Assert.Throws<InvalidDataException>(() => new CsrGraphStore(truncHeaderPath));

        // 2. Truncated body (missing edgeWeights tail bytes)
        string truncBodyPath = Path.Combine(_tempDir, "trunc_body.csr");
        File.WriteAllBytes(truncBodyPath, fullBytes[..(fullBytes.Length - 8)]);
        Assert.Throws<InvalidDataException>(() => new CsrGraphStore(truncBodyPath, useMemoryMapping: true));
        Assert.Throws<InvalidDataException>(() => new CsrGraphStore(truncBodyPath, useMemoryMapping: false));

        // 3. Corrupt magic bytes
        string badMagicPath = Path.Combine(_tempDir, "bad_magic.csr");
        byte[] badMagic = (byte[])fullBytes.Clone();
        badMagic[1] = (byte)'X';
        File.WriteAllBytes(badMagicPath, badMagic);
        Assert.Throws<InvalidDataException>(() => new CsrGraphStore(badMagicPath));

        // 4. Out-of-bounds internal IDs in both MMF and Paged modes
        foreach (bool useMmf in new[] { true, false })
        {
            using var csr = new CsrGraphStore(validCsrPath, maxMemoryBytes: 4096, useMemoryMapping: useMmf);

            foreach (int badId in new[] { int.MinValue, -1, 0, csr.NodeCount + 1, int.MaxValue })
            {
                var enumerator = csr.GetOutwardEdgesByInternalId(badId);
                Assert.False(enumerator.MoveNext());
                Assert.Equal(0, csr.GetNodeDegree(badId));
                Assert.False(csr.TryGetNeighbors(badId, out _));
                Assert.False(csr.TryGetNeighborsAndWeights(badId, out _, out _));
                Assert.False(csr.ContainsNeighbor(badId, 1));
                Assert.Equal(0, csr.CountCommonNeighbors(badId, 1));
                Assert.Equal(string.Empty, csr.GetNodeMetadataByInternalId(badId));
            }
        }

        // 5. Verify LRU access counter uses 64-bit long without overflowing past int.MaxValue
        using (var csrLru = new CsrGraphStore(validCsrPath, maxMemoryBytes: 4096, useMemoryMapping: false))
        {
            var counterField = typeof(CsrGraphStore).GetField("_accessCounter", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Equal(typeof(long), counterField.FieldType);
            counterField.SetValue(csrLru, (long)int.MaxValue - 1);

            // Trigger multiple page accesses across int.MaxValue boundary
            for (int i = 1; i <= csrLru.NodeCount; i++)
            {
                _ = csrLru.GetNodeDegree(i);
            }

            long updatedCounter = (long)counterField.GetValue(csrLru)!;
            Assert.True(updatedCounter > int.MaxValue);
        }
    }

    [Fact]
    public async Task GraphMcpServer_AotJsonRpcRoundtrip_ExecutesAllToolsWithoutDiskLogs()
    {
        using var store = new GraphStore();
        var search = new GraphSearch(store);

        string savedGraphPath = Path.Combine(_tempDir, "mcp_roundtrip.bin");
        string escapedPath = savedGraphPath.Replace("\\", "\\\\");

        var requests = new StringBuilder();
        requests.AppendLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ping"}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"add_node","arguments":{"id":"A","metadata":"Origin"}}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"add_edge","arguments":{"source":"A","target":"C","relation":"DIRECT","weight":9.0}}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"add_edge","arguments":{"source":"A","target":"B","relation":"FAST","weight":0.25}}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"add_edge","arguments":{"source":"B","target":"C","relation":"FAST","weight":0.25}}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"find_shortest_path","arguments":{"startId":"A","targetId":"C","weighted":true}}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"find_neighborhood","arguments":{"sourceId":"A","maxHops":1}}}""");
        requests.AppendLine($$$$"""{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"save_graph","arguments":{"filePath":"{{{{escapedPath}}}}"}}}""");
        requests.AppendLine($$$$"""{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"load_graph","arguments":{"filePath":"{{{{escapedPath}}}}"}}}""");
        requests.AppendLine("""{"jsonrpc":"2.0","id":"err-1","method":"unknown_method"}""");
        requests.AppendLine("""{malformed json""");

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(requests.ToString()));
        using var outputStream = new MemoryStream();

        var server = new GraphMcpServer(store, search, inputStream, outputStream);
        await server.RunAsync();

        string outputText = Encoding.UTF8.GetString(outputStream.ToArray());
        string[] lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 13 responses expected (notifications/initialized produces no response)
        Assert.Equal(13, lines.Length);

        // Verify initialize response (id=1)
        using (var initDoc = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal("2.0", initDoc.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, initDoc.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("1.1.0", initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
        }

        // Verify tools/list response (id=2)
        using (var listDoc = JsonDocument.Parse(lines[1]))
        {
            var tools = listDoc.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Equal(7, tools.GetArrayLength());
        }

        // Verify ping response (id=3)
        using (var pingDoc = JsonDocument.Parse(lines[2]))
        {
            string text = pingDoc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Equal("pong", text);
        }

        // Verify weighted shortest path response (id=8 -> index 7) picks A -> B -> C
        using (var pathDoc = JsonDocument.Parse(lines[7]))
        {
            string text = pathDoc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("A -> B -> C", text);
        }

        // Verify unknown method error (id="err-1" -> index 11)
        using (var errDoc = JsonDocument.Parse(lines[11]))
        {
            Assert.Equal("err-1", errDoc.RootElement.GetProperty("id").GetString());
            Assert.Equal(-32601, errDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        // Verify parse error (-32700 -> index 12)
        using (var parseErrDoc = JsonDocument.Parse(lines[12]))
        {
            Assert.Equal(-32700, parseErrDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        // Verify no synchronous disk log files were created in BaseDirectory
        string legacyLog1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_log.txt");
        string legacyLog2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "graph_mcp_log.txt");
        Assert.False(File.Exists(legacyLog1));
        Assert.False(File.Exists(legacyLog2));
    }

    [Fact]
    public void CsrGraphStore_LegacyV1Format_LoadsCleanlyWithDefaultUnitWeights()
    {
        // Construct a valid GCSR v1 binary file (no edgeWeights section)
        string v1Path = Path.Combine(_tempDir, "legacy_v1.gcsr");
        using (var fs = new FileStream(v1Path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("GCSR");
            writer.Write(1); // Version 1
            writer.Write(2); // NodeCount = 2
            writer.Write(1); // EdgeCount = 1

            writer.Write(1); // relCount = 1
            writer.Write("LINKS");
            writer.Write(1);

            writer.Write(2); // idCount = 2
            writer.Write("N1");
            writer.Write(1);
            writer.Write("N2");
            writer.Write(2);

            writer.Write(3); // metadata slots
            writer.Write("");
            writer.Write("Meta1");
            writer.Write("Meta2");

            // rowOffsets: [0, 0, 1, 1] (4 ints = NodeCount + 2)
            writer.Write(0);
            writer.Write(0);
            writer.Write(1);
            writer.Write(1);

            // columnIndices: [2]
            writer.Write(2);

            // edgeRelations: [1]
            writer.Write(1);
        }

        using var csr = new CsrGraphStore(v1Path, useMemoryMapping: true);
        Assert.Equal(2, csr.NodeCount);
        Assert.Equal(1, csr.EdgeCount);
        Assert.Equal("Meta1", csr.GetNodeMetadata("N1"));
        Assert.Equal("LINKS", csr.GetRelationType(1));

        var edges = csr.GetOutwardEdges("N1");
        Assert.True(edges.MoveNext());
        Assert.Equal(2, edges.CurrentTargetNodeId);
        Assert.Equal(1.0f, edges.CurrentWeight);

        var search = new CsrGraphSearch(csr);
        Assert.Equal(["N1", "N2"], search.FindShortestPathDijkstra("N1", "N2"));
    }

    [Fact]
    public void CsrGraphSearch_Dijkstra_MatchesFloydWarshallOracle_BothMmfAndPaged()
    {
        const int v = 16;
        var rng = new Random(2026);
        using var store = new GraphStore(initialNodeCapacity: 32, initialEdgeCapacity: 256);

        for (int i = 0; i < v; i++)
        {
            store.AddNode($"C{i}");
        }

        float[,] distMatrix = new float[v, v];
        for (int i = 0; i < v; i++)
        {
            for (int j = 0; j < v; j++)
            {
                distMatrix[i, j] = i == j ? 0f : float.PositiveInfinity;
            }
        }

        for (int i = 0; i < v; i++)
        {
            for (int j = 0; j < v; j++)
            {
                if (i != j && rng.NextDouble() < 0.35)
                {
                    // Include occasional 0.0f weights alongside fractional weights
                    float weight = rng.NextDouble() < 0.15
                        ? 0.0f
                        : (float)Math.Round(rng.NextDouble() * 8.0 + 0.1, 2);
                    store.AddEdge($"C{i}", $"C{j}", "EDGE", weight);
                    if (weight < distMatrix[i, j])
                    {
                        distMatrix[i, j] = weight;
                    }
                }
            }
        }

        for (int k = 0; k < v; k++)
        {
            for (int i = 0; i < v; i++)
            {
                for (int j = 0; j < v; j++)
                {
                    if (distMatrix[i, k] + distMatrix[k, j] < distMatrix[i, j])
                    {
                        distMatrix[i, j] = distMatrix[i, k] + distMatrix[k, j];
                    }
                }
            }
        }

        string csrPath = Path.Combine(_tempDir, "oracle_csr.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        foreach (bool useMmf in new[] { true, false })
        {
            using var csrStore = new CsrGraphStore(csrPath, maxMemoryBytes: 4096, useMemoryMapping: useMmf);
            var csrSearch = new CsrGraphSearch(csrStore);

            for (int i = 0; i < v; i++)
            {
                for (int j = 0; j < v; j++)
                {
                    string start = $"C{i}";
                    string target = $"C{j}";
                    var path = csrSearch.FindShortestPathDijkstra(start, target);
                    float expectedDist = distMatrix[i, j];

                    if (float.IsPositiveInfinity(expectedDist))
                    {
                        Assert.Empty(path);
                    }
                    else if (i == j)
                    {
                        Assert.Single(path);
                        Assert.Equal(start, path[0]);
                    }
                    else
                    {
                        Assert.NotEmpty(path);
                        Assert.Equal(start, path[0]);
                        Assert.Equal(target, path[^1]);

                        float actualDist = 0f;
                        for (int step = 0; step < path.Count - 1; step++)
                        {
                            var edges = csrStore.GetOutwardEdges(path[step]);
                            float minStep = float.PositiveInfinity;
                            while (edges.MoveNext())
                            {
                                if (csrStore.GetExternalId(edges.CurrentTargetNodeId) == path[step + 1]
                                    && edges.CurrentWeight < minStep)
                                {
                                    minStep = edges.CurrentWeight;
                                }
                                edges.Advance();
                            }
                            actualDist += minStep;
                        }

                        Assert.True(Math.Abs(actualDist - expectedDist) < 1e-3f,
                            $"CSR Dijkstra (MMF={useMmf}) cost {actualDist} != oracle {expectedDist} for {start} -> {target}");
                    }
                }
            }
        }
    }
}
