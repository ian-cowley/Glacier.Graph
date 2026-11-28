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
            await RunAsync(args);
        }

        internal static async Task RunAsync(string[] args, TextReader? reader = null, TextWriter? writer = null, string? allowedBaseDirectory = null)
        {
            string resolvedArgPath = ResolveDbPath(args);
            string baseDir = !string.IsNullOrWhiteSpace(allowedBaseDirectory)
                ? Path.GetFullPath(allowedBaseDirectory)
                : ResolveAllowedBaseDirectory(args, resolvedArgPath);

            bool hasExplicitDbArg = args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]);
            string dbPath = (!hasExplicitDbArg && !Path.IsPathRooted(resolvedArgPath))
                ? Path.Combine(baseDir, resolvedArgPath)
                : resolvedArgPath;

            using GraphStore store = InitializeStore(dbPath);
            var search = new GraphSearch(store);
            using var server = reader != null && writer != null
                ? new GraphMcpServer(store, search, reader, writer, baseDir)
                : new GraphMcpServer(store, search, baseDir);

            Console.Error.WriteLine("Glacier.Graph MCP Server started.");
            Console.Error.WriteLine($"Database Path: {Path.GetFullPath(dbPath)}");
            Console.Error.WriteLine($"Allowed Data Directory: {baseDir}");
            Console.Error.WriteLine("Listening for JSON-RPC on stdin...");

            await server.RunAsync();
        }

        internal static string ResolveAllowedBaseDirectory(string[]? args, string? dbPath = null)
        {
            string? envDir = Environment.GetEnvironmentVariable("GLACIER_GRAPH_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                return Path.GetFullPath(envDir);
            }

            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                string fullDbPath = Path.GetFullPath(dbPath ?? args[0]);
                return Path.GetDirectoryName(fullDbPath) ?? GraphMcpServer.ResolveDefaultBaseDirectory();
            }

            return GraphMcpServer.ResolveDefaultBaseDirectory();
        }

        internal static string ResolveDbPath(string[] args)
        {
            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                return args[0];
            }

            return "graph_database.bin";
        }

        internal static GraphStore InitializeStore(string dbPath)
        {
            // Load persisted graph if it exists, otherwise start fresh
            if (File.Exists(dbPath))
            {
                Console.Error.WriteLine($"Loading existing graph database from '{dbPath}'...");
                try
                {
                    var store = GraphStore.LoadFromDisk(dbPath);
                    Console.Error.WriteLine($"Graph database successfully revived! Nodes: {store.NodeCount:N0} | Edges: {store.EdgeCount:N0}");
                    return store;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error loading graph database: {ex.Message}");
                    Console.Error.WriteLine("Starting with a fresh empty graph store instead.");
                    return new GraphStore();
                }
            }

            Console.Error.WriteLine($"No graph database found at '{dbPath}'. Initializing a fresh graph store.");
            return new GraphStore();
        }
    }
}
