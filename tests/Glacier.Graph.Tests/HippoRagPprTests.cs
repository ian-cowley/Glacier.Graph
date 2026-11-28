namespace Glacier.Graph.Tests;

using System;
using System.IO;
using System.Linq;
using Glacier.Graph.Kernels;
using Glacier.Graph.Storage;
using Xunit;

public class HippoRagPprTests : IDisposable
{
    private readonly string _tempDir;

    public HippoRagPprTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_ppr_test_{Guid.NewGuid():N}");
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
    public void PersonalizedPageRank_UniformSeeds_RingGraph_IsSymmetricAndConserved()
    {
        // 4 nodes in a ring: 1 -> 2 -> 3 -> 4 -> 1
        // rowOffsets: size = nodeCount + 2 = 6
        int[] rowOffsets = [0, 0, 1, 2, 3, 4];
        int[] columnIndices = [2, 3, 4, 1];
        float[] scores = new float[5];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            columnIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount: 4,
            seedEntities: ReadOnlySpan<int>.Empty, // Uniform seeds
            seedWeights: ReadOnlySpan<float>.Empty,
            scores,
            alpha: 0.85f,
            maxIterations: 50);

        // Conservation of probability
        float sum = scores[1] + scores[2] + scores[3] + scores[4];
        Assert.Equal(1.0f, sum, precision: 4);

        // Symmetry: in a symmetric ring, all nodes should have identical stationary probability = 0.25
        Assert.Equal(0.25f, scores[1], precision: 4);
        Assert.Equal(0.25f, scores[2], precision: 4);
        Assert.Equal(0.25f, scores[3], precision: 4);
        Assert.Equal(0.25f, scores[4], precision: 4);
    }

    [Fact]
    public void PersonalizedPageRank_SingleSeed_DirectedChain_MonotonicallyDecays()
    {
        // 4 nodes in a chain: 1 -> 2 -> 3 -> 4 -> 4 (self-loop at 4 so not dangling)
        int[] rowOffsets = [0, 0, 1, 2, 3, 4];
        int[] columnIndices = [2, 3, 4, 4];
        float[] scores = new float[5];

        // Seed exclusively at node 1
        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            columnIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount: 4,
            seedEntities: [1],
            seedWeights: [1.0f],
            scores,
            alpha: 0.5f,
            maxIterations: 30);

        float sum = scores[1] + scores[2] + scores[3] + scores[4];
        Assert.Equal(1.0f, sum, precision: 4);

        // Node 1 receives 50% restart every iteration, so it has higher probability than 2, which is higher than 3
        Assert.True(scores[1] > scores[2]);
        Assert.True(scores[2] > scores[3]);
    }

    [Fact]
    public void PersonalizedPageRank_DanglingNodes_RedistributesMassWithoutLoss()
    {
        // Star out: 1 -> 2, 1 -> 3. Nodes 2 and 3 are dangling (out-degree 0).
        // rowOffsets: 1 has edges 0..2, 2 has edges 2..2 (empty), 3 has edges 2..2 (empty)
        int[] rowOffsets = [0, 0, 2, 2, 2];
        int[] columnIndices = [2, 3];
        float[] scores = new float[4];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            columnIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount: 3,
            seedEntities: [1],
            seedWeights: [1.0f],
            scores,
            alpha: 0.85f,
            maxIterations: 40);

        float sum = scores[1] + scores[2] + scores[3];
        Assert.Equal(1.0f, sum, precision: 4);
        Assert.True(scores[1] > 0f);
        Assert.True(scores[2] > 0f);
        Assert.True(scores[3] > 0f);
        // By symmetry, nodes 2 and 3 receive equal mass from node 1
        Assert.Equal(scores[2], scores[3], precision: 4);
    }

    [Fact]
    public void PersonalizedPageRank_EdgeWeights_BiasesProbabilityAlongHeavyEdges()
    {
        // 1 -> 2 with weight 10.0f, 1 -> 3 with weight 1.0f
        // 2 -> 2 (weight 1.0f), 3 -> 3 (weight 1.0f)
        int[] rowOffsets = [0, 0, 2, 3, 4];
        int[] columnIndices = [2, 3, 2, 3];
        float[] weights = [10.0f, 1.0f, 1.0f, 1.0f];
        float[] scores = new float[4];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            columnIndices,
            weights,
            nodeCount: 3,
            seedEntities: [1],
            seedWeights: [1.0f],
            scores,
            alpha: 0.85f,
            maxIterations: 20);

        float sum = scores[1] + scores[2] + scores[3];
        Assert.Equal(1.0f, sum, precision: 4);

        // Node 2 should have significantly higher probability than Node 3 due to 10x edge weight
        Assert.True(scores[2] > scores[3] * 5.0f);
    }

    [Fact]
    public void PersonalizedPageRank_CsrGraphStore_MmfAndPaged_MatchSpanOracle()
    {
        using var store = new GraphStore();
        store.AddEdge("N1", "N2", "REL", 10.0f);
        store.AddEdge("N1", "N3", "REL", 1.0f);
        store.AddEdge("N2", "N4", "REL", 2.0f);
        store.AddEdge("N3", "N4", "REL", 3.0f);
        store.AddEdge("N4", "N1", "REL", 0.5f);
        store.AddNode("N5_Dangling");

        string csrPath = Path.Combine(_tempDir, "ppr_weighted.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        using var csrMmf = new CsrGraphStore(csrPath, useMemoryMapping: true);
        using var csrPaged = new CsrGraphStore(csrPath, maxMemoryBytes: 4096, useMemoryMapping: false);

        int n1 = csrMmf.GetExternalToInternalId("N1");
        int n4 = csrMmf.GetExternalToInternalId("N4");
        int[] seeds = [n1, n4];
        float[] seedWeights = [2.0f, 1.0f];

        float[] spanScores = new float[csrMmf.NodeCount + 1];
        float[] mmfScores = new float[csrMmf.NodeCount + 1];
        float[] pagedScores = new float[csrPaged.NodeCount + 1];

        CsrKernels.PersonalizedPageRank(
            csrMmf.GetRowOffsetsSpan(),
            csrMmf.GetColumnIndicesSpan(),
            csrMmf.GetEdgeWeightsSpan(),
            csrMmf.NodeCount,
            seeds,
            seedWeights,
            spanScores,
            alpha: 0.85f,
            maxIterations: 30,
            tolerance: 1e-6f);

        CsrKernels.PersonalizedPageRank(csrMmf, seeds, seedWeights, mmfScores, alpha: 0.85f, maxIterations: 30, tolerance: 1e-6f);
        CsrKernels.PersonalizedPageRank(csrPaged, seeds, seedWeights, pagedScores, alpha: 0.85f, maxIterations: 30, tolerance: 1e-6f);

        float mmfSum = 0f;
        float pagedSum = 0f;
        for (int u = 1; u <= csrMmf.NodeCount; u++)
        {
            Assert.True(MathF.Abs(spanScores[u] - mmfScores[u]) < 1e-5f, $"MMF mismatch at node {u}");
            Assert.True(MathF.Abs(spanScores[u] - pagedScores[u]) < 1e-5f, $"Paged mismatch at node {u}: expected {spanScores[u]}, got {pagedScores[u]}");
            mmfSum += mmfScores[u];
            pagedSum += pagedScores[u];
        }

        Assert.Equal(1.0f, mmfSum, precision: 4);
        Assert.Equal(1.0f, pagedSum, precision: 4);
    }

    [Fact]
    public void PersonalizedPageRank_Over1024Nodes_PoolsBoolBufferAndConservesMass()
    {
        const int nodeCount = 2048;
        using var store = new GraphStore(initialNodeCapacity: nodeCount + 16, initialEdgeCapacity: nodeCount * 2);
        for (int i = 1; i <= nodeCount; i++)
        {
            store.AddNode($"V{i}");
        }
        for (int i = 1; i < nodeCount; i++)
        {
            store.AddEdge($"V{i}", $"V{i + 1}", "NEXT", 1.0f + (i % 5));
            if (i % 10 == 0)
            {
                store.AddEdge($"V{i}", "V1", "BACK", 0.5f);
            }
        }
        // Leave nodeCount as a dangling node

        string csrPath = Path.Combine(_tempDir, "ppr_2048.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        using var csrMmf = new CsrGraphStore(csrPath, useMemoryMapping: true);
        using var csrPaged = new CsrGraphStore(csrPath, maxMemoryBytes: 8192, useMemoryMapping: false);

        int[] seeds = [csrMmf.GetExternalToInternalId("V1")];
        float[] mmfScores = new float[nodeCount + 1];
        float[] pagedScores = new float[nodeCount + 1];

        CsrKernels.PersonalizedPageRank(csrMmf, seeds, ReadOnlySpan<float>.Empty, mmfScores, alpha: 0.85f, maxIterations: 25);
        CsrKernels.PersonalizedPageRank(csrPaged, seeds, ReadOnlySpan<float>.Empty, pagedScores, alpha: 0.85f, maxIterations: 25);

        float mmfSum = 0f;
        float pagedSum = 0f;
        for (int u = 1; u <= nodeCount; u++)
        {
            Assert.False(float.IsNaN(mmfScores[u]));
            Assert.False(float.IsNaN(pagedScores[u]));
            Assert.True(mmfScores[u] >= 0f);
            Assert.True(pagedScores[u] >= 0f);
            Assert.True(MathF.Abs(mmfScores[u] - pagedScores[u]) < 1e-5f);
            mmfSum += mmfScores[u];
            pagedSum += pagedScores[u];
        }

        Assert.Equal(1.0f, mmfSum, precision: 3);
        Assert.Equal(1.0f, pagedSum, precision: 3);
    }

    [Fact]
    public void PersonalizedPageRank_ZeroEdgeStore_TerminatesCleanlyInMmfAndPagedModes()
    {
        using var store = new GraphStore();
        for (int i = 1; i <= 10; i++)
        {
            store.AddNode($"Iso{i}");
        }

        string csrPath = Path.Combine(_tempDir, "ppr_zero_edges.gcsr");
        CsrGraphCompiler.CompileFromForwardStar(store, csrPath);

        using var csrMmf = new CsrGraphStore(csrPath, useMemoryMapping: true);
        using var csrPaged = new CsrGraphStore(csrPath, maxMemoryBytes: 4096, useMemoryMapping: false);

        int seed = csrMmf.GetExternalToInternalId("Iso3");
        float[] mmfScores = new float[11];
        float[] pagedScores = new float[11];

        CsrKernels.PersonalizedPageRank(csrMmf, [seed], [1.0f], mmfScores);
        CsrKernels.PersonalizedPageRank(csrPaged, [seed], [1.0f], pagedScores);

        Assert.Equal(1.0f, mmfScores[seed], precision: 5);
        Assert.Equal(1.0f, pagedScores[seed], precision: 5);
        Assert.Equal(1.0f, mmfScores.Sum(), precision: 5);
        Assert.Equal(1.0f, pagedScores.Sum(), precision: 5);
    }
}
