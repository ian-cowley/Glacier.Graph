namespace Glacier.Graph.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Xunit;

public class AdversarialStressTests
{
    [Fact]
    public void GraphSearch_ConcurrentReads_NodeCountAndStructure100PercentImmutable()
    {
        var store = new GraphStore(initialNodeCapacity: 64, initialEdgeCapacity: 256);
        const int nodeCount = 30;

        // Build a known graph topology
        for (int i = 0; i < nodeCount; i++)
        {
            store.AddNode($"Node_{i}", $"Meta_{i}");
        }

        var rng = new Random(42);
        int edgeCount = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            // Add 3-5 outgoing edges per node
            int targetCount = rng.Next(2, 5);
            for (int k = 0; k < targetCount; k++)
            {
                int target = rng.Next(0, nodeCount);
                if (target != i)
                {
                    store.AddEdge($"Node_{i}", $"Node_{target}", "CONNECTED_TO", (float)(rng.NextDouble() * 5.0 + 0.5));
                    edgeCount++;
                }
            }
        }

        int baselineNodeCount = store.NodeCount;
        int baselineEdgeCount = store.EdgeCount;
        Assert.Equal(nodeCount, baselineNodeCount);
        Assert.Equal(edgeCount, baselineEdgeCount);

        var search = new GraphSearch(store);

        // Snapshot initial degree for each node
        var initialDegrees = new Dictionary<string, int>();
        for (int i = 0; i < nodeCount; i++)
        {
            string id = $"Node_{i}";
            initialDegrees[id] = store.GetNodeDegree(id);
        }

        // Launch 16 concurrent reader tasks querying real and non-existent nodes
        Parallel.For(0, 16, workerId =>
        {
            var localRng = new Random(workerId * 555);
            for (int iter = 0; iter < 100; iter++)
            {
                int op = iter % 4;
                string start = localRng.Next(0, 2) == 0 ? $"Node_{localRng.Next(0, nodeCount)}" : $"MissingNode_{localRng.Next(100, 200)}";
                string target = localRng.Next(0, 2) == 0 ? $"Node_{localRng.Next(0, nodeCount)}" : $"GhostNode_{localRng.Next(300, 400)}";

                switch (op)
                {
                    case 0:
                        search.FindShortestPath(start, target);
                        break;
                    case 1:
                        search.FindShortestPathDijkstra(start, target);
                        break;
                    case 2:
                        search.FindNeighborhood(start, maxHops: localRng.Next(1, 4));
                        break;
                    case 3:
                        search.FindPathDfs(start, target);
                        break;
                }
            }
        });

        // Verify 100% immutability
        Assert.Equal(baselineNodeCount, store.NodeCount);
        Assert.Equal(baselineEdgeCount, store.EdgeCount);

        for (int i = 0; i < nodeCount; i++)
        {
            string id = $"Node_{i}";
            Assert.Equal(initialDegrees[id], store.GetNodeDegree(id));
        }

        // Verify non-existent nodes were never inserted into internal tables
        Assert.False(store.TryGetInternalId("MissingNode_101", out _));
        Assert.False(store.TryGetInternalId("GhostNode_350", out _));
        Assert.Empty(store.GetNodeMetadata("MissingNode_101"));
    }

    [Fact]
    public void GraphSearch_Dijkstra_MatchesFloydWarshallOracle()
    {
        const int v = 15;
        var rng = new Random(98765);
        var store = new GraphStore(initialNodeCapacity: 32, initialEdgeCapacity: 128);

        for (int i = 0; i < v; i++)
        {
            store.AddNode($"N{i}");
        }

        // Adjacency matrix for Floyd-Warshall
        float[,] distMatrix = new float[v, v];
        for (int i = 0; i < v; i++)
        {
            for (int j = 0; j < v; j++)
            {
                distMatrix[i, j] = i == j ? 0f : float.PositiveInfinity;
            }
        }

        // Create directed edges with positive weights
        for (int i = 0; i < v; i++)
        {
            for (int j = 0; j < v; j++)
            {
                if (i != j && rng.NextDouble() < 0.35)
                {
                    float weight = (float)Math.Round(rng.NextDouble() * 10.0 + 1.0, 2);
                    store.AddEdge($"N{i}", $"N{j}", "LINKS", weight);
                    if (weight < distMatrix[i, j])
                    {
                        distMatrix[i, j] = weight;
                    }
                }
            }
        }

        // Run Floyd-Warshall algorithm as the reference oracle
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

        var search = new GraphSearch(store);

        // Verify all-pairs shortest paths computed by Dijkstra match Floyd-Warshall
        for (int i = 0; i < v; i++)
        {
            for (int j = 0; j < v; j++)
            {
                string start = $"N{i}";
                string target = $"N{j}";

                var path = search.FindShortestPathDijkstra(start, target);
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

                    // Verify path distance
                    float pathDist = 0f;
                    for (int step = 0; step < path.Count - 1; step++)
                    {
                        string from = path[step];
                        string to = path[step + 1];

                        // Find edge weight
                        var edges = store.GetOutwardEdges(from);
                        float stepWeight = float.PositiveInfinity;
                        while (edges.MoveNext())
                        {
                            string neighbor = store.GetExternalId(edges.CurrentTargetNodeId);
                            if (neighbor == to)
                            {
                                if (edges.CurrentWeight < stepWeight)
                                {
                                    stepWeight = edges.CurrentWeight;
                                }
                            }
                            edges.Advance();
                        }

                        Assert.False(float.IsPositiveInfinity(stepWeight), $"Edge from {from} to {to} must exist");
                        pathDist += stepWeight;
                    }

                    Assert.True(Math.Abs(pathDist - expectedDist) < 1e-3f,
                        $"Dijkstra path dist ({pathDist}) did not match Floyd-Warshall oracle ({expectedDist}) for {start} -> {target}");
                }
            }
        }
    }

    [Fact]
    public void GraphSearch_BfsShortestPath_MatchesBfsOracle()
    {
        const int v = 20;
        var rng = new Random(112233);
        var store = new GraphStore(initialNodeCapacity: 32, initialEdgeCapacity: 128);

        var adj = new List<int>[v];
        for (int i = 0; i < v; i++)
        {
            adj[i] = new List<int>();
            store.AddNode($"V{i}");
        }

        for (int i = 0; i < v; i++)
        {
            for (int j = 0; j < v; j++)
            {
                if (i != j && rng.NextDouble() < 0.25)
                {
                    adj[i].Add(j);
                    store.AddEdge($"V{i}", $"V{j}", "CONNECTED");
                }
            }
        }

        var search = new GraphSearch(store);

        // Independent BFS Oracle for unweighted hops
        int BfsHopOracle(int src, int dst)
        {
            if (src == dst) return 0;
            var q = new Queue<(int Node, int Hops)>();
            var visited = new bool[v];
            q.Enqueue((src, 0));
            visited[src] = true;

            while (q.Count > 0)
            {
                var (cur, hops) = q.Dequeue();
                if (cur == dst) return hops;

                foreach (var n in adj[cur])
                {
                    if (!visited[n])
                    {
                        visited[n] = true;
                        q.Enqueue((n, hops + 1));
                    }
                }
            }
            return -1;
        }

        for (int i = 0; i < v; i += 2)
        {
            for (int j = 0; j < v; j += 2)
            {
                var path = search.FindShortestPath($"V{i}", $"V{j}");
                int oracleHops = BfsHopOracle(i, j);

                if (oracleHops == -1)
                {
                    Assert.Empty(path);
                }
                else
                {
                    Assert.Equal(oracleHops + 1, path.Count);
                    Assert.Equal($"V{i}", path[0]);
                    Assert.Equal($"V{j}", path[^1]);
                }
            }
        }
    }
}
