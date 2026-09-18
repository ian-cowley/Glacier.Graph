namespace Glacier.Graph.Traversal;

using System;
using System.Collections.Generic;
using Glacier.Graph.Storage;

/// <summary>
/// High-performance traversal algorithms for Glacier.Graph.
/// Uses flat arrays for tracking visited state to guarantee maximum CPU cache hits.
/// Guarantees pure read-only queries with zero mutations.
/// </summary>
public class GraphSearch
{
    private readonly GraphStore _store;

    public GraphSearch(GraphStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Finds the shortest unweighted path between two nodes using Breadth-First Search (BFS).
    /// Returns the sequence of node IDs making up the path, or an empty list if no path exists.
    /// Pure read-only query: does NOT mutate graph state.
    /// </summary>
    public List<string> FindShortestPath(string startId, string targetId)
    {
        if (!_store.TryGetInternalId(startId, out int startInternal) ||
            !_store.TryGetInternalId(targetId, out int targetInternal))
        {
            return new List<string>();
        }

        if (startInternal == targetInternal) return new List<string> { startId };

        int nodeCount = _store.NodeCount;
        bool[] visited = new bool[nodeCount + 1];
        int[] parent = new int[nodeCount + 1];

        Queue<int> queue = new Queue<int>();
        queue.Enqueue(startInternal);
        visited[startInternal] = true;

        bool found = false;

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();

            if (current == targetInternal)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !visited[neighbor])
                {
                    visited[neighbor] = true;
                    parent[neighbor] = current;
                    queue.Enqueue(neighbor);
                }
                edges.Advance();
            }
        }

        var path = new List<string>();
        if (found)
        {
            int curr = targetInternal;
            while (curr != 0)
            {
                path.Add(_store.GetExternalId(curr));
                curr = parent[curr];
            }
            path.Reverse();
        }

        return path;
    }

    /// <summary>
    /// Finds a path between two nodes using Depth-First Search (DFS).
    /// Pure read-only query: does NOT mutate graph state.
    /// </summary>
    public List<string> FindPathDfs(string startId, string targetId)
    {
        if (!_store.TryGetInternalId(startId, out int startInternal) ||
            !_store.TryGetInternalId(targetId, out int targetInternal))
        {
            return new List<string>();
        }

        if (startInternal == targetInternal) return new List<string> { startId };

        int nodeCount = _store.NodeCount;
        bool[] visited = new bool[nodeCount + 1];
        int[] parent = new int[nodeCount + 1];

        var stack = new Stack<int>();
        stack.Push(startInternal);
        visited[startInternal] = true;

        bool found = false;

        while (stack.Count > 0)
        {
            int current = stack.Pop();

            if (current == targetInternal)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !visited[neighbor])
                {
                    visited[neighbor] = true;
                    parent[neighbor] = current;
                    stack.Push(neighbor);
                }
                edges.Advance();
            }
        }

        var path = new List<string>();
        if (found)
        {
            int curr = targetInternal;
            while (curr != 0)
            {
                path.Add(_store.GetExternalId(curr));
                curr = parent[curr];
            }
            path.Reverse();
        }

        return path;
    }

    /// <summary>
    /// Finds the weighted shortest path between two nodes using Dijkstra's algorithm.
    /// Uses positive edge weights and a PriorityQueue min-heap.
    /// Pure read-only query: does NOT mutate graph state.
    /// </summary>
    public List<string> FindShortestPathDijkstra(string startId, string targetId)
    {
        if (!_store.TryGetInternalId(startId, out int startInternal) ||
            !_store.TryGetInternalId(targetId, out int targetInternal))
        {
            return new List<string>();
        }

        if (startInternal == targetInternal) return new List<string> { startId };

        int nodeCount = _store.NodeCount;
        float[] distances = new float[nodeCount + 1];
        Array.Fill(distances, float.PositiveInfinity);
        int[] parent = new int[nodeCount + 1];

        var pq = new PriorityQueue<int, float>();
        distances[startInternal] = 0f;
        pq.Enqueue(startInternal, 0f);

        bool found = false;

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int current, out float curDist);

            if (curDist > distances[current]) continue;
            if (current == targetInternal)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                float weight = edges.CurrentWeight > 0f ? edges.CurrentWeight : 1.0f;
                float newDist = curDist + weight;

                if (neighbor <= nodeCount && newDist < distances[neighbor])
                {
                    distances[neighbor] = newDist;
                    parent[neighbor] = current;
                    pq.Enqueue(neighbor, newDist);
                }
                edges.Advance();
            }
        }

        var path = new List<string>();
        if (found)
        {
            int curr = targetInternal;
            while (curr != 0)
            {
                path.Add(_store.GetExternalId(curr));
                curr = parent[curr];
            }
            path.Reverse();
        }

        return path;
    }

    /// <summary>
    /// Finds all neighbors within a certain depth (hops) from the source node.
    /// Pure read-only query: does NOT mutate graph state.
    /// </summary>
    public List<string> FindNeighborhood(string sourceId, int maxHops)
    {
        if (!_store.TryGetInternalId(sourceId, out int startInternal))
        {
            return new List<string>();
        }

        int nodeCount = _store.NodeCount;
        bool[] visited = new bool[nodeCount + 1];
        int[] distance = new int[nodeCount + 1];

        Queue<int> queue = new Queue<int>();
        queue.Enqueue(startInternal);
        visited[startInternal] = true;
        distance[startInternal] = 0;

        var neighborhood = new List<string>();

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int currentDist = distance[current];

            if (currentDist > 0)
            {
                neighborhood.Add(_store.GetExternalId(current));
            }

            if (currentDist >= maxHops) continue;

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !visited[neighbor])
                {
                    visited[neighbor] = true;
                    distance[neighbor] = currentDist + 1;
                    queue.Enqueue(neighbor);
                }
                edges.Advance();
            }
        }

        return neighborhood;
    }

    /// <summary>
    /// Traverses the multi-hop neighborhood from the source node, extracting full relational triplets
    /// with edge predicates and applying degree-based hub pruning.
    /// Pure read-only query: does NOT mutate graph state.
    /// </summary>
    public List<GraphTriplet> FindNeighborhoodTriplets(
        string sourceId,
        int maxHops,
        int maxDegreePerNode = 25,
        int maxTriplets = 100)
    {
        if (!_store.TryGetInternalId(sourceId, out int startInternal))
        {
            return new List<GraphTriplet>();
        }

        int nodeCount = _store.NodeCount;
        bool[] visited = new bool[nodeCount + 1];
        int[] distance = new int[nodeCount + 1];

        var queue = new Queue<int>();
        queue.Enqueue(startInternal);
        visited[startInternal] = true;
        distance[startInternal] = 0;

        var triplets = new List<GraphTriplet>();

        while (queue.Count > 0 && triplets.Count < maxTriplets)
        {
            int current = queue.Dequeue();
            int currentDist = distance[current];
            if (currentDist >= maxHops) continue;

            string currentExternal = _store.GetExternalId(current);
            var edges = new GraphStore.EdgeEnumerator(current, _store);
            int degreeCount = 0;

            while (edges.MoveNext() && degreeCount < maxDegreePerNode && triplets.Count < maxTriplets)
            {
                int neighbor = edges.CurrentTargetNodeId;
                int relationId = edges.CurrentRelationId;
                string relationName = _store.GetRelationType(relationId);
                if (string.IsNullOrEmpty(relationName)) relationName = "CONNECTED_TO";
                string neighborExternal = _store.GetExternalId(neighbor);

                triplets.Add(new GraphTriplet(currentExternal, relationName, neighborExternal));
                degreeCount++;

                if (neighbor <= nodeCount && !visited[neighbor])
                {
                    visited[neighbor] = true;
                    distance[neighbor] = currentDist + 1;
                    queue.Enqueue(neighbor);
                }
                edges.Advance();
            }
        }

        return triplets;
    }
}

/// <summary>
/// Represents a subject-predicate-object knowledge relationship extracted from the Forward-Star graph.
/// </summary>
public readonly record struct GraphTriplet(string Source, string Predicate, string Target);