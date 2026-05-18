using System;
using System.IO;
using System.Threading.Tasks;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Glacier.Graph.Mcp;

namespace Glacier.Graph.Host
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            string dbPath = "graph_database.bin";

            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                dbPath = args[0];
            }

            GraphStore store;

            // Load persisted graph if it exists, otherwise start fresh
            if (File.Exists(dbPath))
            {
                Console.Error.WriteLine($"Loading existing graph database from '{dbPath}'...");
                try
                {
                    store = GraphStore.LoadFromDisk(dbPath);
                    Console.Error.WriteLine($"Graph database successfully revived! Nodes: {store.NodeCount:N0} | Edges: {store.EdgeCount:N0}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error loading graph database: {ex.Message}");
                    Console.Error.WriteLine("Starting with a fresh empty graph store instead.");
                    store = new GraphStore();
                }
            }
            else
            {
                Console.Error.WriteLine($"No graph database found at '{dbPath}'. Initializing a fresh graph store.");
                store = new GraphStore();
            }

            var search = new GraphSearch(store);
            var server = new GraphMcpServer(store, search);

            Console.Error.WriteLine("Glacier.Graph MCP Server started.");
            Console.Error.WriteLine($"Database Path: {Path.GetFullPath(dbPath)}");
            Console.Error.WriteLine("Listening for JSON-RPC on stdin...");

            await server.RunAsync();
        }
    }
}
