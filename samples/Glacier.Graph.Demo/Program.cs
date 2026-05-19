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

            for (int i = 1; i <= 300_000; i++)
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

            // 3. Compile to CSR Format
            Console.WriteLine("\n[3] Compiling Graph to CSR Binary Format...");
            string csrPath = "graph_database.csr";
            sw.Restart();
            CsrGraphCompiler.CompileFromForwardStar(store, csrPath);
            sw.Stop();
            long csrSize = new FileInfo(csrPath).Length;
            Console.WriteLine($"    Saved {csrSize / 1024.0 / 1024.0:F2} MB to '{csrPath}' in {sw.ElapsedMilliseconds} ms.");

            // 4. Destroy and Reload
            Console.WriteLine("\n[4] Destroying graph in memory and reloading from disk...");
            store = null; // Destroy
            GC.Collect();
            GC.WaitForPendingFinalizers();

            sw.Restart();
            var revivedStore = GraphStore.LoadFromDisk(dbPath);
            sw.Stop();
            Console.WriteLine($"    Graph revived from disk in {sw.ElapsedMilliseconds} ms!");
            Console.WriteLine($"    Verified Nodes: {revivedStore.NodeCount:N0} | Verified Edges: {revivedStore.EdgeCount:N0}");

            // 5. Test Search on Revived Graph
            var search = new GraphSearch(revivedStore);

            Console.WriteLine("\n[5] Executing BFS Shortest Path on RAM Forward Star Graph (User_1 -> User_299999)...");
            sw.Restart();
            var path = search.FindShortestPath("User_1", "User_299999");
            sw.Stop();

            Console.WriteLine($"    Path found in {sw.Elapsed.TotalMilliseconds:F4} ms!");
            Console.WriteLine($"    Hops: {path.Count - 1}");
            if (path.Count > 0)
            {
                Console.WriteLine($"    Route: {path[0]} -> ... ({path.Count - 2} intermediate nodes) ... -> {path[^1]}");
            }

            // 6. Test Search on Constrained CSR Graph
            Console.WriteLine("\n[6] Executing BFS Shortest Path on CSR Graph (256KB Max RAM Limit)...");
            using var csrStore = new CsrGraphStore(csrPath, maxMemoryBytes: 256 * 1024);
            var csrSearch = new CsrGraphSearch(csrStore);
            
            sw.Restart();
            var csrPathResult = csrSearch.FindShortestPath("User_1", "User_299999");
            sw.Stop();

            Console.WriteLine($"    Path found in {sw.Elapsed.TotalMilliseconds:F4} ms!");
            Console.WriteLine($"    Hops: {csrPathResult.Count - 1}");

            // 7. Warm Cache CSR Search
            Console.WriteLine("\n[7] Executing BFS Shortest Path on CSR Graph again (Warm 256KB Cache)...");
            sw.Restart();
            csrPathResult = csrSearch.FindShortestPath("User_1", "User_299999");
            sw.Stop();
            Console.WriteLine($"    Path found in {sw.Elapsed.TotalMilliseconds:F4} ms!");

            Console.WriteLine("\n==========================================");
            Console.WriteLine(" GRAPH PERSISTENCE SUCCESSFUL");
            Console.WriteLine("==========================================");
        }
    }
}