namespace Glacier.Graph.Tests;

using System;
using System.Linq;
using Glacier.Graph.Kernels;
using Xunit;

public class HippoRagPprTests
{
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
}
