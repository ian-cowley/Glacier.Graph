namespace Glacier.Graph.Traversal;

using System;
using System.Collections.Generic;
using Glacier.Graph.Storage;

/// <summary>
/// High-performance traversal algorithms for Glacier.Graph.
/// Uses pooled scratch arrays and O(1) generation tagging (<see cref="TraversalScratchWorkspace"/>)
/// to achieve zero GC heap allocations during traversals.
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
    /// Writes the sequence of internal node IDs making up the path into <paramref name="pathBuffer"/>.
    /// Returns the number of nodes in the path, or 0 if no path exists or the buffer is too small.
    /// Pure zero-allocation query path.
    /// </summary>
    public int FindShortestPath(int startInternalId, int targetInternalId, Span<int> pathBuffer, TraversalScratchWorkspace? workspace = null)
    {
        int nodeCount = _store.NodeCount;
        if (startInternalId <= 0 || targetInternalId <= 0 || startInternalId > nodeCount || targetInternalId > nodeCount)
        {
            return 0;
        }

        if (startInternalId == targetInternalId)
        {
            if (pathBuffer.Length > 0)
            {
                pathBuffer[0] = startInternalId;
                return 1;
            }
            return 0;
        }

        var ws = workspace ?? TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetQueue();

        ws.Parent[startInternalId] = 0;
        ws.SetVisited(startInternalId);
        ws.Enqueue(startInternalId);

        bool found = false;

        while (ws.HasQueueItems)
        {
            int current = ws.Dequeue();

            if (current == targetInternalId)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Parent[neighbor] = current;
                    ws.Enqueue(neighbor);
                }
                edges.Advance();
            }
        }

        if (!found) return 0;

        // Calculate path length
        int length = 0;
        int curr = targetInternalId;
        while (curr != 0)
        {
            length++;
            if (curr == startInternalId) break;
            curr = ws.Parent[curr];
        }

        if (pathBuffer.Length < length) return 0;

        // Reconstruct path in forward order
        curr = targetInternalId;
        for (int i = length - 1; i >= 0; i--)
        {
            pathBuffer[i] = curr;
            curr = ws.Parent[curr];
        }

        return length;
    }

    /// <summary>
    /// Finds the shortest unweighted path between two nodes using Breadth-First Search (BFS).
    /// Returns the sequence of node IDs making up the path, or an empty list if no path exists.
    /// Eliminates intermediate traversal heap allocations via pooled scratch buffers.
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
        var ws = TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetQueue();

        ws.Parent[startInternal] = 0;
        ws.SetVisited(startInternal);
        ws.Enqueue(startInternal);

        bool found = false;

        while (ws.HasQueueItems)
        {
            int current = ws.Dequeue();

            if (current == targetInternal)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Parent[neighbor] = current;
                    ws.Enqueue(neighbor);
                }
                edges.Advance();
            }
        }

        if (!found) return new List<string>();

        // Reconstruct path
        int length = 0;
        int curr = targetInternal;
        while (curr != 0)
        {
            length++;
            if (curr == startInternal) break;
            curr = ws.Parent[curr];
        }

        var path = new List<string>(length);
        curr = targetInternal;
        while (curr != 0)
        {
            path.Add(_store.GetExternalId(curr));
            if (curr == startInternal) break;
            curr = ws.Parent[curr];
        }
        path.Reverse();

        return path;
    }

    /// <summary>
    /// Finds a path between two nodes using Depth-First Search (DFS).
    /// Writes the sequence of internal node IDs making up the path into <paramref name="pathBuffer"/>.
    /// Returns the number of nodes in the path, or 0 if no path exists or the buffer is too small.
    /// Pure zero-allocation query path.
    /// </summary>
    public int FindPathDfs(int startInternalId, int targetInternalId, Span<int> pathBuffer, TraversalScratchWorkspace? workspace = null)
    {
        int nodeCount = _store.NodeCount;
        if (startInternalId <= 0 || targetInternalId <= 0 || startInternalId > nodeCount || targetInternalId > nodeCount)
        {
            return 0;
        }

        if (startInternalId == targetInternalId)
        {
            if (pathBuffer.Length > 0)
            {
                pathBuffer[0] = startInternalId;
                return 1;
            }
            return 0;
        }

        var ws = workspace ?? TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetStack();

        ws.Parent[startInternalId] = 0;
        ws.SetVisited(startInternalId);
        ws.PushStack(startInternalId);

        bool found = false;

        while (ws.HasStackItems)
        {
            int current = ws.PopStack();

            if (current == targetInternalId)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Parent[neighbor] = current;
                    ws.PushStack(neighbor);
                }
                edges.Advance();
            }
        }

        if (!found) return 0;

        int length = 0;
        int curr = targetInternalId;
        while (curr != 0)
        {
            length++;
            if (curr == startInternalId) break;
            curr = ws.Parent[curr];
        }

        if (pathBuffer.Length < length) return 0;

        curr = targetInternalId;
        for (int i = length - 1; i >= 0; i--)
        {
            pathBuffer[i] = curr;
            curr = ws.Parent[curr];
        }

        return length;
    }

    /// <summary>
    /// Finds a path between two nodes using Depth-First Search (DFS).
    /// Pure read-only query: does NOT mutate graph state.
    /// Eliminates intermediate traversal heap allocations via pooled scratch buffers.
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
        var ws = TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetStack();

        ws.Parent[startInternal] = 0;
        ws.SetVisited(startInternal);
        ws.PushStack(startInternal);

        bool found = false;

        while (ws.HasStackItems)
        {
            int current = ws.PopStack();

            if (current == targetInternal)
            {
                found = true;
                break;
            }

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Parent[neighbor] = current;
                    ws.PushStack(neighbor);
                }
                edges.Advance();
            }
        }

        if (!found) return new List<string>();

        int length = 0;
        int curr = targetInternal;
        while (curr != 0)
        {
            length++;
            if (curr == startInternal) break;
            curr = ws.Parent[curr];
        }

        var path = new List<string>(length);
        curr = targetInternal;
        while (curr != 0)
        {
            path.Add(_store.GetExternalId(curr));
            if (curr == startInternal) break;
            curr = ws.Parent[curr];
        }
        path.Reverse();

        return path;
    }

    /// <summary>
    /// Finds the weighted shortest path between two nodes using Dijkstra's algorithm.
    /// Writes the sequence of internal node IDs making up the path into <paramref name="pathBuffer"/>.
    /// Returns the number of nodes in the path, or 0 if no path exists or the buffer is too small.
    /// Pure zero-allocation query path using pooled min-heap and generation-tagged distance arrays.
    /// </summary>
    public int FindShortestPathDijkstra(int startInternalId, int targetInternalId, Span<int> pathBuffer, TraversalScratchWorkspace? workspace = null)
    {
        int nodeCount = _store.NodeCount;
        if (startInternalId <= 0 || targetInternalId <= 0 || startInternalId > nodeCount || targetInternalId > nodeCount)
        {
            return 0;
        }

        if (startInternalId == targetInternalId)
        {
            if (pathBuffer.Length > 0)
            {
                pathBuffer[0] = startInternalId;
                return 1;
            }
            return 0;
        }

        var ws = workspace ?? TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetHeap();

        ws.Parent[startInternalId] = 0;
        ws.Distances[startInternalId] = 0f;
        ws.SetVisited(startInternalId);
        ws.EnqueueHeap(startInternalId, 0f);

        bool found = false;

        while (ws.TryDequeueHeap(out int current, out float curDist))
        {
            if (curDist > ws.Distances[current]) continue;

            if (current == targetInternalId)
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

                if (neighbor <= nodeCount)
                {
                    if (!ws.IsVisited(neighbor) || newDist < ws.Distances[neighbor])
                    {
                        ws.SetVisited(neighbor);
                        ws.Distances[neighbor] = newDist;
                        ws.Parent[neighbor] = current;
                        ws.EnqueueHeap(neighbor, newDist);
                    }
                }
                edges.Advance();
            }
        }

        if (!found) return 0;

        int length = 0;
        int curr = targetInternalId;
        while (curr != 0)
        {
            length++;
            if (curr == startInternalId) break;
            curr = ws.Parent[curr];
        }

        if (pathBuffer.Length < length) return 0;

        curr = targetInternalId;
        for (int i = length - 1; i >= 0; i--)
        {
            pathBuffer[i] = curr;
            curr = ws.Parent[curr];
        }

        return length;
    }

    /// <summary>
    /// Finds the weighted shortest path between two nodes using Dijkstra's algorithm.
    /// Uses positive edge weights and a pooled min-heap.
    /// Eliminates intermediate traversal heap allocations via pooled scratch buffers.
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
        var ws = TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetHeap();

        ws.Parent[startInternal] = 0;
        ws.Distances[startInternal] = 0f;
        ws.SetVisited(startInternal);
        ws.EnqueueHeap(startInternal, 0f);

        bool found = false;

        while (ws.TryDequeueHeap(out int current, out float curDist))
        {
            if (curDist > ws.Distances[current]) continue;

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

                if (neighbor <= nodeCount)
                {
                    if (!ws.IsVisited(neighbor) || newDist < ws.Distances[neighbor])
                    {
                        ws.SetVisited(neighbor);
                        ws.Distances[neighbor] = newDist;
                        ws.Parent[neighbor] = current;
                        ws.EnqueueHeap(neighbor, newDist);
                    }
                }
                edges.Advance();
            }
        }

        if (!found) return new List<string>();

        int length = 0;
        int curr = targetInternal;
        while (curr != 0)
        {
            length++;
            if (curr == startInternal) break;
            curr = ws.Parent[curr];
        }

        var path = new List<string>(length);
        curr = targetInternal;
        while (curr != 0)
        {
            path.Add(_store.GetExternalId(curr));
            if (curr == startInternal) break;
            curr = ws.Parent[curr];
        }
        path.Reverse();

        return path;
    }

    /// <summary>
    /// Finds all neighbors within a certain depth (hops) from the source node.
    /// Writes neighbor internal IDs into <paramref name="neighborhoodBuffer"/> and returns the count.
    /// Pure zero-allocation query path.
    /// </summary>
    public int FindNeighborhood(int sourceInternalId, int maxHops, Span<int> neighborhoodBuffer, TraversalScratchWorkspace? workspace = null)
    {
        int nodeCount = _store.NodeCount;
        if (sourceInternalId <= 0 || sourceInternalId > nodeCount || maxHops <= 0 || neighborhoodBuffer.Length == 0)
        {
            return 0;
        }

        var ws = workspace ?? TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetQueue();

        ws.Enqueue(sourceInternalId);
        ws.SetVisited(sourceInternalId);
        ws.Distance[sourceInternalId] = 0;

        int written = 0;

        while (ws.HasQueueItems)
        {
            int current = ws.Dequeue();
            int currentDist = ws.Distance[current];

            if (currentDist > 0 && written < neighborhoodBuffer.Length)
            {
                neighborhoodBuffer[written++] = current;
            }

            if (currentDist >= maxHops) continue;

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Distance[neighbor] = currentDist + 1;
                    ws.Enqueue(neighbor);
                }
                edges.Advance();
            }
        }

        return written;
    }

    /// <summary>
    /// Finds all neighbors within a certain depth (hops) from the source node.
    /// Pure read-only query: does NOT mutate graph state.
    /// Eliminates intermediate traversal heap allocations via pooled scratch buffers.
    /// </summary>
    public List<string> FindNeighborhood(string sourceId, int maxHops)
    {
        if (!_store.TryGetInternalId(sourceId, out int startInternal))
        {
            return new List<string>();
        }

        int nodeCount = _store.NodeCount;
        var ws = TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetQueue();

        ws.Enqueue(startInternal);
        ws.SetVisited(startInternal);
        ws.Distance[startInternal] = 0;

        var neighborhood = new List<string>();

        while (ws.HasQueueItems)
        {
            int current = ws.Dequeue();
            int currentDist = ws.Distance[current];

            if (currentDist > 0)
            {
                neighborhood.Add(_store.GetExternalId(current));
            }

            if (currentDist >= maxHops) continue;

            var edges = new GraphStore.EdgeEnumerator(current, _store);
            while (edges.MoveNext())
            {
                int neighbor = edges.CurrentTargetNodeId;
                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Distance[neighbor] = currentDist + 1;
                    ws.Enqueue(neighbor);
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
    /// Eliminates intermediate traversal heap allocations via pooled scratch buffers.
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
        var ws = TraversalScratchWorkspace.GetThreadStatic(nodeCount + 1);
        ws.NextGeneration(nodeCount + 1);
        ws.ResetQueue();

        ws.Enqueue(startInternal);
        ws.SetVisited(startInternal);
        ws.Distance[startInternal] = 0;

        var triplets = new List<GraphTriplet>();

        while (ws.HasQueueItems && triplets.Count < maxTriplets)
        {
            int current = ws.Dequeue();
            int currentDist = ws.Distance[current];
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

                if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                {
                    ws.SetVisited(neighbor);
                    ws.Distance[neighbor] = currentDist + 1;
                    ws.Enqueue(neighbor);
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