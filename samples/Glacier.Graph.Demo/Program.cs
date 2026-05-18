using System;
using System.Diagnostics;
using System.IO;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

namespace Glacier.Graph.Demo
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("==========================================");
            Console.WriteLine(" Glacier.Graph | High-Performance Graph DB");
            Console.WriteLine("==========================================");

            string dbPath = "graph_database.bin";

            // 1. Initialize and Load Data
            Console.WriteLine("\n[1] Initializing Graph Engine and generating data...");
            var store = new GraphStore(initialNodeCapacity: 500_000, initialEdgeCapacity: 2_000_000);

            for (int i = 1; i <= 100_000; i++)
            {
                store.AddEdge($"User_{i}", $"User_{i + 1}", "KNOWS");
                store.AddEdge($"User_{i}", $"User_{i * 2}", "FOLLOWS");
                if (i % 3 == 0) store.AddEdge($"User_{i}", $"User_{i / 3}", "REPORTS_TO");
            }
            Console.WriteLine($"    Total Nodes: {store.NodeCount:N0} | Total Edges: {store.EdgeCount:N0}");

            // 2. Persist to Disk
            Console.WriteLine("\n[2] Saving raw memory arrays to disk...");
            var sw = Stopwatch.StartNew();
            store.SaveToDisk(dbPath);
            sw.Stop();

            long fileSize = new FileInfo(dbPath).Length;
            Console.WriteLine($"    Saved {fileSize / 1024.0 / 1024.0:F2} MB to '{dbPath}' in {sw.ElapsedMilliseconds} ms.");

            // 3. Destroy and Reload
            Console.WriteLine("\n[3] Destroying graph in memory and reloading from disk...");
            store = null; // Destroy
            GC.Collect();
            GC.WaitForPendingFinalizers();

            sw.Restart();
            var revivedStore = GraphStore.LoadFromDisk(dbPath);
            sw.Stop();
            Console.WriteLine($"    Graph revived from disk in {sw.ElapsedMilliseconds} ms!");
            Console.WriteLine($"    Verified Nodes: {revivedStore.NodeCount:N0} | Verified Edges: {revivedStore.EdgeCount:N0}");

            // 4. Test Search on Revived Graph
            var search = new GraphSearch(revivedStore);

            Console.WriteLine("\n[4] Executing BFS Shortest Path on Revived Graph (User_1 -> User_99999)...");
            sw.Restart();
            var path = search.FindShortestPath("User_1", "User_99999");
            sw.Stop();

            Console.WriteLine($"    Path found in {sw.Elapsed.TotalMilliseconds:F4} ms!");
            Console.WriteLine($"    Hops: {path.Count - 1}");
            if (path.Count > 0)
            {
                Console.WriteLine($"    Route: {path[0]} -> ... ({path.Count - 2} intermediate nodes) ... -> {path[^1]}");
            }

            Console.WriteLine("\n==========================================");
            Console.WriteLine(" GRAPH PERSISTENCE SUCCESSFUL");
            Console.WriteLine("==========================================");
        }
    }
}