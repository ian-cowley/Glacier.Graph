using System.Collections.Generic;
using Glacier.Graph.Storage;

namespace Glacier.Graph.Traversal
{
    /// <summary>
    /// High-performance traversal algorithms for Glacier.Graph.
    /// Uses flat arrays for tracking visited state to guarantee maximum CPU cache hits.
    /// </summary>
    public class GraphSearch
    {
        private readonly GraphStore _store;

        public GraphSearch(GraphStore store)
        {
            _store = store;
        }

        /// <summary>
        /// Finds the shortest path between two nodes using Breadth-First Search.
        /// Returns the sequence of node IDs making up the path, or an empty list if no path exists.
        /// </summary>
        public List<string> FindShortestPath(string startId, string targetId)
        {
            // We use AddNode as a clever "GetOrAddId" to fetch internal IDs safely.
            int startInternal = _store.AddNode(startId);
            int targetInternal = _store.AddNode(targetId);

            if (startInternal == targetInternal) return new List<string> { startId };

            // Flat arrays for tracking state. Vasly faster than Dictionary or HashSet!
            bool[] visited = new bool[_store.NodeCount + 1];
            int[] parent = new int[_store.NodeCount + 1];

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

                // Use our zero-allocation struct enumerator
                var edges = new GraphStore.EdgeEnumerator(current, _store);
                while (edges.MoveNext())
                {
                    int neighbor = edges.CurrentTargetNodeId;
                    if (!visited[neighbor])
                    {
                        visited[neighbor] = true;
                        parent[neighbor] = current; // Record where we came from
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
                path.Reverse(); // Path was built backwards, flip it
            }

            return path;
        }

        /// <summary>
        /// Finds all neighbors within a certain depth (hops) from the source node.
        /// Perfect for Agent queries like "Who are the suppliers connected to this failing part?"
        /// </summary>
        public List<string> FindNeighborhood(string sourceId, int maxHops)
        {
            int startInternal = _store.AddNode(sourceId);

            bool[] visited = new bool[_store.NodeCount + 1];
            int[] distance = new int[_store.NodeCount + 1];

            Queue<int> queue = new Queue<int>();
            queue.Enqueue(startInternal);
            visited[startInternal] = true;
            distance[startInternal] = 0;

            var neighborhood = new List<string>();

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int currentDist = distance[current];

                if (currentDist > 0) // Don't add the source node itself
                {
                    neighborhood.Add(_store.GetExternalId(current));
                }

                if (currentDist >= maxHops) continue;

                var edges = new GraphStore.EdgeEnumerator(current, _store);
                while (edges.MoveNext())
                {
                    int neighbor = edges.CurrentTargetNodeId;
                    if (!visited[neighbor])
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
    }
}