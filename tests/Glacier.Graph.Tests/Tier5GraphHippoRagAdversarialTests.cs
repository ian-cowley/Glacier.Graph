namespace Glacier.Graph.Tests;

using System;
using System.Collections.Generic;
using Glacier.Graph.Kernels;
using Glacier.Graph.Storage;
using Xunit;

/// <summary>
/// Tier-5 White-Box Adversarial Coverage Hardening for Glacier.Graph.
/// Stress-tests SIMD CSR SpMV HippoRAG PPR power iteration, probability conservation,
/// teleportation vector scaling, and isolated node stability.
/// </summary>
public class Tier5GraphHippoRagAdversarialTests
{
    [Theory]
    [InlineData("Ring", 20)]
    [InlineData("Complete", 10)]
    [InlineData("BinaryTree", 31)]
    [InlineData("Star", 51)]
    [InlineData("RandomWithDangling", 80)]
    public void HippoRagPpr_ProbabilityConservation_AcrossDiverseTopologies(string topology, int nodeCount)
    {
        // Build topology
        var (rowOffsets, colIndices, weights) = GenerateTopology(topology, nodeCount);
        float[] scores = new float[nodeCount + 1];

        // Seed with arbitrary subsets
        int[] seeds = [1, Math.Min(3, nodeCount), Math.Min(7, nodeCount)];
        float[] seedWeights = [2.0f, 1.0f, 0.5f];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            weights,
            nodeCount,
            seeds,
            seedWeights,
            scores,
            alpha: 0.85f,
            maxIterations: 50,
            tolerance: 1e-6f);

        // Calculate total probability mass on active nodes 1..nodeCount
        float totalMass = 0f;
        for (int u = 1; u <= nodeCount; u++)
        {
            Assert.True(scores[u] >= 0f, $"PPR score at node {u} was negative: {scores[u]}");
            totalMass += scores[u];
        }

        // Probability conservation axiom: sum must equal 1.0f within floating point tolerance
        Assert.Equal(1.0f, totalMass, precision: 3);
        // Node 0 is 1-based placeholder and must be 0
        Assert.Equal(0.0f, scores[0]);
    }

    [Fact]
    public void HippoRagPpr_DampingFactorZero_ReturnsTeleportationVectorImmediately()
    {
        const int nodeCount = 5;
        // Ring 1 -> 2 -> 3 -> 4 -> 5 -> 1
        int[] rowOffsets = [0, 0, 1, 2, 3, 4, 5];
        int[] colIndices = [2, 3, 4, 5, 1];
        float[] scores = new float[nodeCount + 1];

        int[] seeds = [2, 4];
        float[] seedWeights = [0.75f, 0.25f];

        // Alpha = 0.0f means zero random walk propagation: stationary distribution MUST BE p0
        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount,
            seeds,
            seedWeights,
            scores,
            alpha: 0.0f,
            maxIterations: 10);

        Assert.Equal(0.75f, scores[2], precision: 4);
        Assert.Equal(0.25f, scores[4], precision: 4);
        Assert.Equal(0.0f, scores[1], precision: 4);
        Assert.Equal(0.0f, scores[3], precision: 4);
        Assert.Equal(0.0f, scores[5], precision: 4);
    }

    [Fact]
    public void HippoRagPpr_TeleportationVectorScaling_ExtremeSeeds_HandledGracefully()
    {
        const int nodeCount = 4;
        int[] rowOffsets = [0, 0, 1, 2, 3, 4];
        int[] colIndices = [2, 3, 4, 1];
        float[] scores = new float[nodeCount + 1];

        // 1. Out-of-bounds seeds: ID 0, ID -5, ID 999 must be ignored without throwing
        int[] invalidSeeds = [0, -5, 999];
        float[] weights = [1.0f, 2.0f, 3.0f];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount,
            invalidSeeds,
            weights,
            scores,
            alpha: 0.85f,
            maxIterations: 20);

        // When all seeds are invalid, fallback to uniform teleportation 1/4 = 0.25 on each node
        for (int u = 1; u <= nodeCount; u++)
        {
            Assert.Equal(0.25f, scores[u], precision: 3);
        }

        // 2. Non-positive seed weights: (seedWeights[s] > 0f ? seedWeights[s] : 1.0f) defaults to 1.0f for seed nodes
        int[] validSeeds = [1, 2];
        float[] nonPosWeights = [-1.0f, 0.0f];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount,
            validSeeds,
            nonPosWeights,
            scores,
            alpha: 0.85f,
            maxIterations: 20);

        // Probability mass is conserved
        float sumNonPos = scores[1] + scores[2] + scores[3] + scores[4];
        Assert.Equal(1.0f, sumNonPos, precision: 3);
        // In directed ring 1 -> 2 -> 3 -> 4 -> 1 with seeds at 1 and 2,
        // node 2 receives flow from node 1, so scores[2] > scores[1]
        Assert.True(scores[2] > scores[1]);
        Assert.True(scores[1] > scores[3]);
        Assert.True(scores[3] > scores[4]);

        // 3. Empty seeds: strictly falls back to uniform 1/N on all nodes
        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount,
            ReadOnlySpan<int>.Empty,
            ReadOnlySpan<float>.Empty,
            scores,
            alpha: 0.85f,
            maxIterations: 20);

        for (int u = 1; u <= nodeCount; u++)
        {
            Assert.Equal(0.25f, scores[u], precision: 4);
        }
    }

    [Fact]
    public void HippoRagPpr_IsolatedNodes_StableAndRedistributesMassWithoutLoss()
    {
        // 5 nodes total.
        // Node 1 -> 2
        // Node 2 -> 1
        // Node 3 is completely isolated (degree 0)
        // Node 4 is a dangling sink (1 -> 4, but 4 has no outgoing edges)
        // Node 5 is completely isolated (degree 0)
        const int nodeCount = 5;
        // rowOffsets: size = nodeCount + 2 = 7
        // 1: 0..2 (targets 2, 4)
        // 2: 2..3 (target 1)
        // 3: 3..3 (empty)
        // 4: 3..3 (empty, dangling)
        // 5: 3..3 (empty)
        int[] rowOffsets = [0, 0, 2, 3, 3, 3, 3];
        int[] colIndices = [2, 4, 1];
        float[] scores = new float[nodeCount + 1];

        // Seed exclusively at node 1
        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount,
            seedEntities: [1],
            seedWeights: [1.0f],
            scores,
            alpha: 0.85f,
            maxIterations: 50);

        float totalMass = 0f;
        for (int u = 1; u <= nodeCount; u++)
        {
            totalMass += scores[u];
        }
        Assert.Equal(1.0f, totalMass, precision: 3);

        // Node 1 and 2 participate in active cycle and receive highest mass
        Assert.True(scores[1] > scores[3]);
        Assert.True(scores[2] > scores[3]);

        // Node 4 receives direct mass from 1, so it is > isolated nodes 3 and 5
        Assert.True(scores[4] > scores[3]);

        // Completely isolated nodes 3 and 5 receive 0 mass because seed is at 1 and they have no incoming edges
        Assert.Equal(0.0f, scores[3], precision: 4);
        Assert.Equal(0.0f, scores[5], precision: 4);
    }

    [Fact]
    public void HippoRagPpr_ZeroEdgesGraph_TerminatesCleanlyWithExactSeedDistribution()
    {
        // Graph with 10 nodes and 0 edges
        const int nodeCount = 10;
        int[] rowOffsets = new int[nodeCount + 2];
        int[] colIndices = [];
        float[] scores = new float[nodeCount + 1];

        int[] seeds = [3, 7];
        float[] seedWeights = [0.6f, 0.4f];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            ReadOnlySpan<float>.Empty,
            nodeCount,
            seeds,
            seedWeights,
            scores,
            alpha: 0.85f,
            maxIterations: 20);

        // All nodes are dangling, so power iteration terminates in 1 iteration with p = p0
        Assert.Equal(0.6f, scores[3], precision: 4);
        Assert.Equal(0.4f, scores[7], precision: 4);

        float sum = 0f;
        for (int u = 1; u <= nodeCount; u++) sum += scores[u];
        Assert.Equal(1.0f, sum, precision: 4);
    }

    [Fact]
    public void HippoRagPpr_LargeGraph_Vector512AndVector256SimdAlignment_ConservedAndDeterministic()
    {
        // 512 nodes with random edges to stress-test Vector512 / Vector256 loop boundaries
        const int nodeCount = 512;
        var (rowOffsets, colIndices, weights) = GenerateTopology("RandomWithDangling", nodeCount);

        float[] scores1 = new float[nodeCount + 1];
        float[] scores2 = new float[nodeCount + 1];

        int[] seeds = [1, 64, 128, 256, 511];
        float[] seedWeights = [1f, 1f, 1f, 1f, 1f];

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            weights,
            nodeCount,
            seeds,
            seedWeights,
            scores1,
            alpha: 0.85f,
            maxIterations: 30);

        CsrKernels.PersonalizedPageRank(
            rowOffsets,
            colIndices,
            weights,
            nodeCount,
            seeds,
            seedWeights,
            scores2,
            alpha: 0.85f,
            maxIterations: 30);

        float sum = 0f;
        for (int u = 1; u <= nodeCount; u++)
        {
            sum += scores1[u];
            Assert.Equal(scores1[u], scores2[u], precision: 6); // 100% deterministic
        }
        Assert.Equal(1.0f, sum, precision: 3);
    }

    private static (int[] rowOffsets, int[] colIndices, float[] weights) GenerateTopology(string type, int nodeCount)
    {
        int[] rowOffsets = new int[nodeCount + 2];
        var colList = new List<int>();
        var weightList = new List<float>();
        var rng = new Random(12345 + nodeCount);

        for (int u = 1; u <= nodeCount; u++)
        {
            rowOffsets[u] = colList.Count;

            switch (type)
            {
                case "Ring":
                    int next = (u % nodeCount) + 1;
                    colList.Add(next);
                    weightList.Add(1.0f);
                    break;

                case "Complete":
                    for (int v = 1; v <= nodeCount; v++)
                    {
                        if (u != v)
                        {
                            colList.Add(v);
                            weightList.Add(1.0f);
                        }
                    }
                    break;

                case "BinaryTree":
                    int left = 2 * u;
                    int right = 2 * u + 1;
                    if (left <= nodeCount) { colList.Add(left); weightList.Add(1.0f); }
                    if (right <= nodeCount) { colList.Add(right); weightList.Add(1.0f); }
                    break;

                case "Star":
                    if (u == 1)
                    {
                        for (int v = 2; v <= nodeCount; v++) { colList.Add(v); weightList.Add(1.0f); }
                    }
                    else
                    {
                        colList.Add(1);
                        weightList.Add(1.0f);
                    }
                    break;

                case "RandomWithDangling":
                    // 20% of nodes are dangling sinks
                    if (u % 5 != 0)
                    {
                        int degree = rng.Next(1, 6);
                        for (int k = 0; k < degree; k++)
                        {
                            int target = rng.Next(1, nodeCount + 1);
                            if (target != u)
                            {
                                colList.Add(target);
                                weightList.Add((float)(rng.NextDouble() * 3.0 + 0.5));
                            }
                        }
                    }
                    break;
            }
        }
        rowOffsets[nodeCount + 1] = colList.Count;

        return (rowOffsets, colList.ToArray(), weightList.ToArray());
    }
}
