using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Glacier.Graph.Mcp;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Xunit;

namespace Glacier.Graph.Tests
{
    public class GraphHostTests : IDisposable
    {
        private readonly string _tempDir;

        public GraphHostTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_graph_host_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        [Fact]
        public void ResolveDbPath_HandlesDefaultAndCustomArgs()
        {
            Assert.Equal("graph_database.bin", Glacier.Graph.Host.Program.ResolveDbPath(Array.Empty<string>()));
            Assert.Equal("graph_database.bin", Glacier.Graph.Host.Program.ResolveDbPath(new[] { "   " }));
            Assert.Equal("custom.bin", Glacier.Graph.Host.Program.ResolveDbPath(new[] { "custom.bin" }));
        }

        [Fact]
        public void InitializeStore_CreatesFreshStore_WhenFileMissing()
        {
            string missingPath = Path.Combine(_tempDir, "does_not_exist.bin");
            using GraphStore store = Glacier.Graph.Host.Program.InitializeStore(missingPath);

            Assert.Equal(0, store.NodeCount);
            Assert.Equal(0, store.EdgeCount);
        }

        [Fact]
        public void InitializeStore_LoadsExistingStore_WhenFileExists()
        {
            string dbPath = Path.Combine(_tempDir, "valid_graph.bin");
            using (var seed = new GraphStore())
            {
                seed.AddEdge("Alice", "Bob", "KNOWS", 1.5f);
                seed.SaveToDisk(dbPath);
            }

            using GraphStore loaded = Glacier.Graph.Host.Program.InitializeStore(dbPath);
            Assert.Equal(2, loaded.NodeCount);
            Assert.Equal(1, loaded.EdgeCount);
        }

        [Fact]
        public void InitializeStore_FallsBackToFreshStore_WhenFileCorrupt()
        {
            string corruptPath = Path.Combine(_tempDir, "corrupt_graph.bin");
            File.WriteAllBytes(corruptPath, new byte[] { 1, 2, 3, 4, 5 });

            using GraphStore fallback = Glacier.Graph.Host.Program.InitializeStore(corruptPath);
            Assert.Equal(0, fallback.NodeCount);
            Assert.Equal(0, fallback.EdgeCount);
        }

        [Fact]
        public void GraphMcpServer_Constructor_RejectsNullArguments()
        {
            using var store = new GraphStore();
            var search = new GraphSearch(store);

            Assert.Throws<ArgumentNullException>(() => new GraphMcpServer(null!, search, TextReader.Null, TextWriter.Null));
            Assert.Throws<ArgumentNullException>(() => new GraphMcpServer(store, null!, TextReader.Null, TextWriter.Null));
            Assert.Throws<ArgumentNullException>(() => new GraphMcpServer(store, search, (TextReader)null!, TextWriter.Null));
            Assert.Throws<ArgumentNullException>(() => new GraphMcpServer(store, search, TextReader.Null, null!));
            Assert.Throws<ArgumentNullException>(() => new GraphMcpServer(store, search, (Stream)null!, new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => new GraphMcpServer(store, search, new MemoryStream(), null!));
        }

        [Fact]
        public async Task HostRunAsync_SupportsInitializeToolsListAndGraphWorkflow()
        {
            string dbPath = Path.Combine(_tempDir, "host_e2e.bin");
            string savedPath = Path.Combine(_tempDir, "saved_e2e.bin");
            string savedPathJson = JsonEncodedText.Encode(savedPath).ToString();

            var requests = string.Join("\n", new[]
            {
                """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ping","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"add_node","arguments":{"id":"A","metadata":"start"}}}""",
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"add_edge","arguments":{"source":"A","target":"B","relation":"LINKS","weight":2.5}}}""",
                """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"add_edge","arguments":{"source":"B","target":"C","relation":"LINKS","weight":1.5}}}""",
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"find_shortest_path","arguments":{"startId":"A","targetId":"C","weighted":true}}}""",
                """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"find_neighborhood","arguments":{"sourceId":"A","maxHops":2}}}""",
                $"{{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{{\"name\":\"save_graph\",\"arguments\":{{\"filePath\":\"{savedPathJson}\"}}}}}}",
                $"{{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{{\"name\":\"load_graph\",\"arguments\":{{\"filePath\":\"{savedPathJson}\"}}}}}}"
            }) + "\n";

            using var reader = new StringReader(requests);
            using var writer = new StringWriter();

            await Glacier.Graph.Host.Program.RunAsync(new[] { dbPath }, reader, writer);

            var responses = ParseLines(writer.ToString());
            Assert.Equal(10, responses.Count);

            // 1: initialize
            Assert.Equal("2024-11-05", responses[0].RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            Assert.Equal("Glacier.Graph", responses[0].RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

            // 2: tools/list
            var toolNames = responses[1].RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString())
                .ToHashSet();
            Assert.Contains("add_node", toolNames);
            Assert.Contains("add_edge", toolNames);
            Assert.Contains("find_shortest_path", toolNames);
            Assert.Contains("find_neighborhood", toolNames);
            Assert.Contains("save_graph", toolNames);
            Assert.Contains("load_graph", toolNames);
            Assert.Contains("ping", toolNames);

            // 3..10: tools/call results
            Assert.Equal("pong", GetToolText(responses[2]));
            Assert.Contains("Successfully added node: 'A'", GetToolText(responses[3]));
            Assert.Contains("Successfully linked A -> LINKS -> B", GetToolText(responses[4]));
            Assert.Contains("A -> B -> C", GetToolText(responses[6]));
            Assert.Contains("Found", GetToolText(responses[7]));
            Assert.Contains("Successfully persisted raw graph arrays", GetToolText(responses[8]));
            Assert.Contains("Successfully loaded graph database", GetToolText(responses[9]));
            Assert.True(File.Exists(savedPath));
        }

        [Fact]
        public async Task HostRunAsync_ReturnsJsonRpcErrors_ForInvalidRequestsAndArguments()
        {
            string missingFile = Path.Combine(_tempDir, "missing.bin");
            string missingFileJson = JsonEncodedText.Encode(missingFile).ToString();

            var requests = string.Join("\n", new[]
            {
                "",
                "{not-valid-json",
                """{"jsonrpc":"2.0","id":1}""",
                """{"jsonrpc":"2.0","id":2,"method":"unknown/method"}""",
                """{"jsonrpc":"2.0","method":"unknown/notification"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call"}""",
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"unknown_tool","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"add_node","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"find_neighborhood","arguments":{"sourceId":"A"}}}""",
                $"{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{{\"name\":\"load_graph\",\"arguments\":{{\"filePath\":\"{missingFileJson}\"}}}}}}"
            }) + "\n";

            using var reader = new StringReader(requests);
            using var writer = new StringWriter();

            await Glacier.Graph.Host.Program.RunAsync(Array.Empty<string>(), reader, writer);

            var responses = ParseLines(writer.ToString());
            Assert.Equal(8, responses.Count);

            Assert.Equal(-32700, GetErrorCode(responses[0])); // Parse error
            Assert.Equal(-32600, GetErrorCode(responses[1])); // Missing method
            Assert.Equal(-32601, GetErrorCode(responses[2])); // Method not found
            Assert.Equal(-32602, GetErrorCode(responses[3])); // Missing params for tools/call
            Assert.Equal(-32000, GetErrorCode(responses[4])); // Unsupported tool
            Assert.Equal(-32000, GetErrorCode(responses[5])); // Missing required id
            Assert.Equal(-32000, GetErrorCode(responses[6])); // Missing required maxHops
            Assert.Equal(-32000, GetErrorCode(responses[7])); // Missing graph file
        }

        private static List<JsonDocument> ParseLines(string output)
        {
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .ToList();
        }

        private static string GetToolText(JsonDocument doc)
        {
            return doc.RootElement
                .GetProperty("result")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;
        }

        private static int GetErrorCode(JsonDocument doc)
        {
            return doc.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetInt32();
        }
    }
}
