namespace Glacier.Graph.Traversal;

using System;
using System.Collections.Generic;
using Glacier.Graph.Storage;

/// <summary>
/// High-performance traversal algorithms for <see cref="CsrGraphStore"/>.
/// Operates directly over memory-mapped contiguous column index spans or software-paged cache
/// using pooled scratch arrays and O(1) generation tagging (<see cref="TraversalScratchWorkspace"/>)
/// to achieve zero GC heap allocations during traversals.
/// </summary>
public class CsrGraphSearch
{
    private readonly CsrGraphStore _store;

    public CsrGraphSearch(CsrGraphStore store)
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                    {
                        ws.SetVisited(neighbor);
                        ws.Parent[neighbor] = current;
                        ws.Enqueue(neighbor);
                    }
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
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
    /// Finds the shortest unweighted path between two nodes using Breadth-First Search (BFS).
    /// Returns the sequence of node IDs making up the path, or an empty list if no path exists.
    /// Eliminates intermediate traversal heap allocations via pooled scratch buffers.
    /// </summary>
    public List<string> FindShortestPath(string startId, string targetId)
    {
        int startInternal = _store.GetExternalToInternalId(startId);
        int targetInternal = _store.GetExternalToInternalId(targetId);

        if (startInternal == 0 || targetInternal == 0) return new List<string>();
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                    {
                        ws.SetVisited(neighbor);
                        ws.Parent[neighbor] = current;
                        ws.Enqueue(neighbor);
                    }
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
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
    /// Finds a path between two nodes using Depth-First Search (DFS).
    /// Writes the sequence of internal node IDs making up the path into <paramref name="pathBuffer"/>.
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                    {
                        ws.SetVisited(neighbor);
                        ws.Parent[neighbor] = current;
                        ws.PushStack(neighbor);
                    }
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
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
    /// </summary>
    public List<string> FindPathDfs(string startId, string targetId)
    {
        int startInternal = _store.GetExternalToInternalId(startId);
        int targetInternal = _store.GetExternalToInternalId(targetId);

        if (startInternal == 0 || targetInternal == 0) return new List<string>();
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                    {
                        ws.SetVisited(neighbor);
                        ws.Parent[neighbor] = current;
                        ws.PushStack(neighbor);
                    }
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
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
    /// Finds the shortest path between two nodes using Dijkstra's algorithm.
    /// Pure zero-allocation query path.
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    float weight = 1.0f; // CSR unit weight default
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
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
                while (edges.MoveNext())
                {
                    int neighbor = edges.CurrentTargetNodeId;
                    float weight = 1.0f;
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
    /// Finds the shortest path between two nodes using Dijkstra's algorithm.
    /// </summary>
    public List<string> FindShortestPathDijkstra(string startId, string targetId)
    {
        int startInternal = _store.GetExternalToInternalId(startId);
        int targetInternal = _store.GetExternalToInternalId(targetId);

        if (startInternal == 0 || targetInternal == 0) return new List<string>();
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    float weight = 1.0f;
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
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
                while (edges.MoveNext())
                {
                    int neighbor = edges.CurrentTargetNodeId;
                    float weight = 1.0f;
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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                    {
                        ws.SetVisited(neighbor);
                        ws.Distance[neighbor] = currentDist + 1;
                        ws.Enqueue(neighbor);
                    }
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
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
        }

        return written;
    }

    /// <summary>
    /// Finds all neighbors within a certain depth (hops) from the source node.
    /// </summary>
    public List<string> FindNeighborhood(string sourceId, int maxHops)
    {
        int startInternal = _store.GetExternalToInternalId(sourceId);
        if (startInternal == 0) return new List<string>();

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

            if (_store.TryGetNeighbors(current, out var neighbors))
            {
                for (int i = 0; i < neighbors.Length; i++)
                {
                    int neighbor = neighbors[i];
                    if (neighbor <= nodeCount && !ws.IsVisited(neighbor))
                    {
                        ws.SetVisited(neighbor);
                        ws.Distance[neighbor] = currentDist + 1;
                        ws.Enqueue(neighbor);
                    }
                }
            }
            else
            {
                var edges = _store.GetOutwardEdgesByInternalId(current);
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
        }

        return neighborhood;
    }
}
