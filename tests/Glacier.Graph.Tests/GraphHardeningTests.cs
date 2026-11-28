namespace Glacier.Graph.Tests;

using System;
using System.IO;
using System.Linq;
using Glacier.Graph.Storage;
using Xunit;

public class GraphHardeningTests : IDisposable
{
    private readonly string _tempDir;

    public GraphHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_graph_hardening_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)] // nodeCount = 2 -> out of bounds
    public void GraphStore_LoadFromDisk_RejectsOutOfRangeNodeIdMappings(int badInternalId)
    {
        string path = Path.Combine(_tempDir, $"bad_id_{badInternalId}.bin");
        WriteSyntheticGlgrV2(
            path,
            nodeCount: 2,
            edgeCount: 0,
            idMappings: [("A", 1), ("B", badInternalId)],
            head: [0, 0, 0],
            to: [0],
            rel: [0],
            next: [0],
            weights: [1.0f]);

        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(path));
    }

    [Fact]
    public void GraphStore_LoadFromDisk_RejectsIdCountExceedingNodeCount_AndDuplicateMappings()
    {
        string tooManyPath = Path.Combine(_tempDir, "too_many_ids.bin");
        WriteSyntheticGlgrV2(
            tooManyPath,
            nodeCount: 1,
            edgeCount: 0,
            idMappings: [("A", 1), ("B", 1)],
            head: [0, 0],
            to: [0],
            rel: [0],
            next: [0],
            weights: [1.0f]);

        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(tooManyPath));

        string dupInternalPath = Path.Combine(_tempDir, "dup_internal_id.bin");
        WriteSyntheticGlgrV2(
            dupInternalPath,
            nodeCount: 2,
            edgeCount: 0,
            idMappings: [("A", 1), ("B", 1)],
            head: [0, 0, 0],
            to: [0],
            rel: [0],
            next: [0],
            weights: [1.0f]);

        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(dupInternalPath));
    }

    [Fact]
    public void GraphStore_LoadFromDisk_RejectsCorruptEdgeArraysAndCycles()
    {
        // 1. _head[1] > edgeCount
        string badHeadPath = Path.Combine(_tempDir, "bad_head.bin");
        WriteSyntheticGlgrV2(
            badHeadPath,
            nodeCount: 2,
            edgeCount: 1,
            idMappings: [("A", 1), ("B", 2)],
            head: [0, 2, 0],
            to: [0, 2],
            rel: [0, 1],
            next: [0, 0],
            weights: [1.0f, 1.0f]);
        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(badHeadPath));

        // 2. _to[1] == 0 and _to[1] > nodeCount
        foreach (int badTarget in new[] { 0, 3 })
        {
            string badToPath = Path.Combine(_tempDir, $"bad_to_{badTarget}.bin");
            WriteSyntheticGlgrV2(
                badToPath,
                nodeCount: 2,
                edgeCount: 1,
                idMappings: [("A", 1), ("B", 2)],
                head: [0, 1, 0],
                to: [0, badTarget],
                rel: [0, 1],
                next: [0, 0],
                weights: [1.0f, 1.0f]);
            Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(badToPath));
        }

        // 3. _next[1] > edgeCount
        string badNextPath = Path.Combine(_tempDir, "bad_next.bin");
        WriteSyntheticGlgrV2(
            badNextPath,
            nodeCount: 2,
            edgeCount: 1,
            idMappings: [("A", 1), ("B", 2)],
            head: [0, 1, 0],
            to: [0, 2],
            rel: [0, 1],
            next: [0, 5],
            weights: [1.0f, 1.0f]);
        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(badNextPath));

        // 4. Self-loop cycle _next[1] = 1
        string cyclePath = Path.Combine(_tempDir, "cycle_next.bin");
        WriteSyntheticGlgrV2(
            cyclePath,
            nodeCount: 2,
            edgeCount: 1,
            idMappings: [("A", 1), ("B", 2)],
            head: [0, 1, 0],
            to: [0, 2],
            rel: [0, 1],
            next: [0, 1],
            weights: [1.0f, 1.0f]);
        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(cyclePath));

        // 5. Negative / NaN edge weight
        foreach (float badWeight in new[] { -0.5f, float.NaN, float.PositiveInfinity })
        {
            string badWeightPath = Path.Combine(_tempDir, $"bad_w_{badWeight.GetHashCode()}.bin");
            WriteSyntheticGlgrV2(
                badWeightPath,
                nodeCount: 2,
                edgeCount: 1,
                idMappings: [("A", 1), ("B", 2)],
                head: [0, 1, 0],
                to: [0, 2],
                rel: [0, 1],
                next: [0, 0],
                weights: [1.0f, badWeight]);
            Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(badWeightPath));
        }

        // 6. Truncated body throws InvalidDataException without leaking file handle
        string validPath = Path.Combine(_tempDir, "valid_for_trunc.bin");
        using (var validStore = new GraphStore())
        {
            validStore.AddEdge("A", "B", "REL", 1.5f);
            validStore.SaveToDisk(validPath);
        }
        byte[] validBytes = File.ReadAllBytes(validPath);
        string truncPath = Path.Combine(_tempDir, "trunc_body.bin");
        File.WriteAllBytes(truncPath, validBytes[..(validBytes.Length - 2)]);
        Assert.Throws<InvalidDataException>(() => GraphStore.LoadFromDisk(truncPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void CsrGraphStore_RejectsOutOfRangeNodeIdMappings(int badInternalId)
    {
        string path = Path.Combine(_tempDir, $"csr_bad_id_{badInternalId}.gcsr");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("GCSR");
            writer.Write(3); // version 3
            writer.Write(2); // NodeCount = 2
            writer.Write(0); // EdgeCount = 0
            writer.Write(1); // flags = IsSorted
            writer.Write(0); // relCount
            writer.Write(2); // idCount
            writer.Write("A");
            writer.Write(1);
            writer.Write("B");
            writer.Write(badInternalId);
            writer.Write(3); // metadata slots
            writer.Write("");
            writer.Write("");
            writer.Write("");
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
        }

        Assert.Throws<InvalidDataException>(() => new CsrGraphStore(path));
    }

    [Fact]
    public void CsrGraphCompiler_SortsNeighborsPreservingParallelRelationsAndWeights()
    {
        using var store = new GraphStore();
        int n1 = store.AddNode("N1");
        int n2 = store.AddNode("N2");
        int n3 = store.AddNode("N3");
        int n4 = store.AddNode("N4");
        int n5 = store.AddNode("N5");
        int n6 = store.AddNode("N6");

        // Add N1's neighbors intentionally out of ascending target order, including parallel edges
        store.AddEdge("N1", "N5", "REL_Z", 9.0f);
        store.AddEdge("N1", "N3", "REL_B", 4.5f);
        store.AddEdge("N1", "N3", "REL_A", 1.25f);
        store.AddEdge("N1", "N2", "REL_C", 2.0f);

        // Add N6's neighbors sharing {N3, N5} with N1, out of order
        store.AddEdge("N6", "N5", "REL_A", 1.0f);
        store.AddEdge("N6", "N4", "REL_A", 1.0f);
        store.AddEdge("N6", "N3", "REL_B", 1.0f);

        string csrPath = Path.Combine(_tempDir, "sorted_parallel.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        int relA = 0, relB = 0, relC = 0, relZ = 0;
        foreach (bool useMmf in new[] { true, false })
        {
            using var csr = new CsrGraphStore(csrPath, maxMemoryBytes: 4096, useMemoryMapping: useMmf);
            Assert.True(csr.IsSorted);

            if (useMmf)
            {
                relA = csr.GetRelationId("REL_A");
                relB = csr.GetRelationId("REL_B");
                relC = csr.GetRelationId("REL_C");
                relZ = csr.GetRelationId("REL_Z");

                Assert.True(csr.TryGetNeighbors(n1, out var nbrs));
                Assert.Equal([n2, n3, n3, n5], nbrs.ToArray());
            }

            var edges = csr.GetOutwardEdgesByInternalId(n1);
            var actual = new System.Collections.Generic.List<(int Target, int Rel, float Weight)>();
            while (edges.MoveNext())
            {
                actual.Add((edges.CurrentTargetNodeId, edges.CurrentRelationId, edges.CurrentWeight));
                edges.Advance();
            }

            // REL_Z=1, REL_B=2, REL_A=3, REL_C=4 -> sorted by (TargetNodeId, RelationId, Weight)
            Assert.True(relB < relA);
            Assert.Equal(
                [
                    (n2, relC, 2.0f),
                    (n3, relB, 4.5f),
                    (n3, relA, 1.25f),
                    (n5, relZ, 9.0f)
                ],
                actual);

            // Common neighbors between N1 ([2, 3, 3, 5]) and N6 ([3, 4, 5]): 3 and 5 -> 2
            Assert.Equal(2, csr.CountCommonNeighbors(n1, n6, isSorted: true));
            Assert.Equal(2, csr.CountCommonNeighbors(n1, n6, isSorted: false));
        }
    }

    [Fact]
    public void CsrGraphStore_LegacyV2Format_LoadsWithIsSortedFalse_AndCountCommonNeighborsIsSafe()
    {
        // Craft an unsorted GCSR v2 file:
        // Node 1 neighbors: [4, 2] (descending, unsorted!)
        // Node 2 neighbors: [2, 4] (ascending)
        // Common neighbors are {2, 4} -> true count = 2.
        // An unsafe two-pointer merge on [4, 2] vs [2, 4] would return 1 instead of 2!
        string v2Path = Path.Combine(_tempDir, "legacy_v2_unsorted.gcsr");
        using (var fs = new FileStream(v2Path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("GCSR");
            writer.Write(2); // Version 2 (no flags field)
            writer.Write(4); // NodeCount = 4
            writer.Write(4); // EdgeCount = 4

            writer.Write(1); // relCount = 1
            writer.Write("LINKS");
            writer.Write(1);

            writer.Write(4); // idCount = 4
            writer.Write("N1"); writer.Write(1);
            writer.Write("N2"); writer.Write(2);
            writer.Write("N3"); writer.Write(3);
            writer.Write("N4"); writer.Write(4);

            writer.Write(5); // metadata slots 0..4
            for (int i = 0; i < 5; i++) writer.Write("");

            // rowOffsets (NodeCount + 2 = 6 ints): N1=[0..2), N2=[2..4), N3=[4..4), N4=[4..4)
            writer.Write(0);
            writer.Write(0);
            writer.Write(2);
            writer.Write(4);
            writer.Write(4);
            writer.Write(4);

            // columnIndices: N1 has [4, 2] (descending!), N2 has [2, 4] (ascending)
            writer.Write(4);
            writer.Write(2);
            writer.Write(2);
            writer.Write(4);

            // edgeRelations
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);

            // edgeWeights
            writer.Write(1.5f);
            writer.Write(2.5f);
            writer.Write(3.5f);
            writer.Write(4.5f);
        }

        foreach (bool useMmf in new[] { true, false })
        {
            using var csr = new CsrGraphStore(v2Path, maxMemoryBytes: 4096, useMemoryMapping: useMmf);
            Assert.False(csr.IsSorted);
            // Even when caller passes isSorted: true, v2 store must NOT use unsafe two-pointer intersection
            Assert.Equal(2, csr.CountCommonNeighbors(1, 2, isSorted: true));
            Assert.Equal(2, csr.CountCommonNeighbors(1, 2, isSorted: false));
        }
    }

    [Fact]
    public void CsrGraphCompiler_AtomicOverwrite_LeavesNoTempFiles_AndSurvivesLargeGraph()
    {
        const int nodeCount = 5_000;
        const int edgeCount = 30_000; // > 16,384 chunk size and > 85 KB LOH threshold

        using var store = new GraphStore(initialNodeCapacity: nodeCount + 16, initialEdgeCapacity: edgeCount + 16);
        for (int i = 1; i <= nodeCount; i++)
        {
            store.AddNode($"Node_{i}");
        }

        for (int e = 0; e < edgeCount; e++)
        {
            int src = (e % nodeCount) + 1;
            int dst = ((e * 7 + 3) % nodeCount) + 1;
            store.AddEdge($"Node_{src}", $"Node_{dst}", "EDGE", (e % 10) * 0.5f + 0.25f);
        }

        string outDir = Path.Combine(_tempDir, "atomic_nested");
        string csrPath = Path.Combine(outDir, "large_graph.gcsr");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(csrPath, new byte[] { 1, 2, 3 }); // Pre-existing file to overwrite atomically

        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));

        using var csr = new CsrGraphStore(csrPath, useMemoryMapping: true);
        Assert.Equal(nodeCount, csr.NodeCount);
        Assert.Equal(edgeCount, csr.EdgeCount);
        Assert.True(csr.IsSorted);

        // Verify sortedness across chunk boundary nodes
        for (int u = 1; u <= nodeCount; u += 137)
        {
            Assert.True(csr.TryGetNeighbors(u, out var nbrs));
            for (int k = 1; k < nbrs.Length; k++)
            {
                Assert.True(nbrs[k - 1] <= nbrs[k], $"Neighbors of node {u} not sorted at index {k}.");
            }
        }
    }

    [Fact]
    public void CsrGraphStore_And_CsrEdgeEnumerator_ThrowObjectDisposedAfterDispose()
    {
        using var store = new GraphStore();
        store.AddNode("A", "MetaA");
        store.AddEdge("A", "B", "LINK", 2.5f);

        string csrPath = Path.Combine(_tempDir, "dispose_guard.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        // default(CsrEdgeEnumerator) must remain safe
        var defaultEnum = default(CsrGraphStore.CsrEdgeEnumerator);
        Assert.False(defaultEnum.MoveNext());
        defaultEnum.Advance();

        foreach (bool useMmf in new[] { true, false })
        {
            var csr = new CsrGraphStore(csrPath, maxMemoryBytes: 4096, useMemoryMapping: useMmf);
            var activeEnum = csr.GetOutwardEdgesByInternalId(1);
            Assert.True(activeEnum.MoveNext());

            csr.Dispose();
            csr.Dispose(); // Idempotent

            Assert.Throws<ObjectDisposedException>(() => activeEnum.MoveNext());
            Assert.Throws<ObjectDisposedException>(() => _ = activeEnum.CurrentTargetNodeId);
            Assert.Throws<ObjectDisposedException>(() => _ = activeEnum.CurrentRelationId);
            Assert.Throws<ObjectDisposedException>(() => _ = activeEnum.CurrentWeight);
            Assert.Throws<ObjectDisposedException>(() => activeEnum.Advance());

            Assert.Throws<ObjectDisposedException>(() => csr.GetExternalToInternalId("A"));
            Assert.Throws<ObjectDisposedException>(() => csr.TryGetInternalId("A", out _));
            Assert.Throws<ObjectDisposedException>(() => csr.GetExternalId(1));
            Assert.Throws<ObjectDisposedException>(() => csr.GetNodeMetadata("A"));
            Assert.Throws<ObjectDisposedException>(() => csr.GetNodeMetadataByInternalId(1));
            Assert.Throws<ObjectDisposedException>(() => csr.GetRelationType(1));
            Assert.Throws<ObjectDisposedException>(() => csr.GetRelationId("LINK"));
            Assert.Throws<ObjectDisposedException>(() => csr.GetOutwardEdges("A"));
            Assert.Throws<ObjectDisposedException>(() => csr.GetOutwardEdgesByInternalId(1));
        }
    }

    private static void WriteSyntheticGlgrV2(
        string path,
        int nodeCount,
        int edgeCount,
        (string Key, int Val)[] idMappings,
        int[] head,
        int[] to,
        int[] rel,
        int[] next,
        float[] weights)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);
        writer.Write("GLGR");
        writer.Write(2);
        writer.Write(nodeCount);
        writer.Write(edgeCount);
        writer.Write(head.Length);
        writer.Write(to.Length);

        writer.Write(1);
        writer.Write("REL");
        writer.Write(1);

        writer.Write(idMappings.Length);
        foreach (var (key, val) in idMappings)
        {
            writer.Write(key);
            writer.Write(val);
        }

        for (int i = 0; i < head.Length; i++)
        {
            writer.Write($"M{i}");
        }

        foreach (int v in head) writer.Write(v);
        foreach (int v in to) writer.Write(v);
        foreach (int v in rel) writer.Write(v);
        foreach (int v in next) writer.Write(v);
        foreach (float w in weights) writer.Write(w);
    }
}
