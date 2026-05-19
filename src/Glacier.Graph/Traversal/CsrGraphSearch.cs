using System;
using System.Collections.Generic;
using Glacier.Graph.Storage;

namespace Glacier.Graph.Traversal
{
    /// <summary>
    /// High-performance traversal algorithms for CsrGraphStore.
    /// Operates under strict memory limitations using the simulated out-of-core CSR backend.
    /// </summary>
    public class CsrGraphSearch
    {
        private readonly CsrGraphStore _store;

        public CsrGraphSearch(CsrGraphStore store)
        {
            _store = store;
        }

        public List<string> FindShortestPath(string startId, string targetId)
        {
            int startInternal = _store.GetExternalToInternalId(startId);
            int targetInternal = _store.GetExternalToInternalId(targetId);

            if (startInternal == 0 || targetInternal == 0) return new List<string>();
            if (startInternal == targetInternal) return new List<string> { startId };

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

                var edges = _store.GetOutwardEdgesByInternalId(current);
                while (edges.MoveNext())
                {
                    int neighbor = edges.CurrentTargetNodeId;
                    if (!visited[neighbor])
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
    }
}
