using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

namespace Glacier.Graph.Mcp
{
    /// <summary>
    /// A zero-dependency Model Context Protocol (MCP) server over Stdio.
    /// Exposes the high-performance Glacier.Graph engine to AI agents.
    /// </summary>
    public class GraphMcpServer
    {
        private GraphStore _store;
        private GraphSearch _search;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string _logPath;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:T}] {message}\n");
            }
            catch { /* Ignore logging errors */ }
        }

        public GraphMcpServer(GraphStore store, GraphSearch search)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_log.txt");

            Log("Server instance created.");

            // Setup standard IO for MCP communication
            Stream openStandardInput = Console.OpenStandardInput();
            _reader = new StreamReader(openStandardInput);

            Stream openStandardOutput = Console.OpenStandardOutput();
            // Use UTF8 without BOM (important for some MCP hosts)
            _writer = new StreamWriter(openStandardOutput, new UTF8Encoding(false)) { AutoFlush = true };
            _writer.NewLine = "\n";
            
            Log("IO setup complete.");
        }

        public async Task RunAsync()
        {
            // The main JSON-RPC listening loop
            while (await _reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement requestId = default;
                try
                {
                    Log($"Received: {line}");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("method", out JsonElement methodElem))
                    {
                        Log("Message missing 'method' property.");
                        continue;
                    }

                    string method = methodElem.GetString() ?? "";
                    root.TryGetProperty("id", out requestId);

                    Log($"Method: {method}");

                    // Route the MCP Methods
                    switch (method)
                    {
                        case "initialize":
                            await SendResponseAsync(requestId, HandleInitialize());
                            break;

                        case "notifications/initialized":
                            Log("Initialized notification received.");
                            break;

                        case "notifications/cancelled":
                            Log("Client cancelled a request.");
                            break;

                        case "tools/list":
                            await SendResponseAsync(requestId, HandleToolsList());
                            break;

                        case "tools/call":
                            try
                            {
                                if (root.TryGetProperty("params", out JsonElement parameters))
                                {
                                    var result = await HandleToolCallAsync(parameters);
                                    await SendResponseAsync(requestId, result);
                                }
                                else
                                {
                                    await SendErrorAsync(requestId, -32602, "Missing parameters for tools/call");
                                }
                            }
                            catch (Exception toolEx)
                            {
                                Log($"Tool execution error: {toolEx.Message}");
                                await SendErrorAsync(requestId, -32000, toolEx.Message);
                            }
                            break;

                        default:
                            Log($"Method '{method}' not handled.");
                            if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                            {
                                await SendErrorAsync(requestId, -32601, $"Method '{method}' not found.");
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"CRITICAL ERROR: {ex.Message}\n{ex.StackTrace}");
                    if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                    {
                        try { await SendErrorAsync(requestId, -32603, "Internal server error."); } catch { }
                    }
                }
            }
        }

        private object HandleInitialize()
        {
            return new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "Glacier.Graph", version = "1.0.0" }
            };
        }

        private object HandleToolsList()
        {
            return new
            {
                tools = new[]
                {
                    new Dictionary<string, object>
                    {
                        { "name", "add_node" },
                        { "description", "Registers a new node in the graph, or updates its metadata if it already exists." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "id", new Dictionary<string, object> { { "type", "string" }, { "description", "The unique identifier of the node." } } },
                                        { "metadata", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional JSON or text metadata associated with the node." } } }
                                    }
                                },
                                { "required", new[] { "id" } }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "add_edge" },
                        { "description", "Creates a directed relationship between two nodes. Automatically creates the nodes if they don't exist." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "source", new Dictionary<string, object> { { "type", "string" }, { "description", "The starting node identifier." } } },
                                        { "target", new Dictionary<string, object> { { "type", "string" }, { "description", "The ending node identifier." } } },
                                        { "relation", new Dictionary<string, object> { { "type", "string" }, { "description", "The type of relation (e.g., KNOWS, FOLLOWS, REPORTS_TO)." } } }
                                    }
                                },
                                { "required", new[] { "source", "target", "relation" } }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "find_shortest_path" },
                        { "description", "Finds the shortest path between two nodes using high-performance Breadth-First Search (BFS)." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "startId", new Dictionary<string, object> { { "type", "string" }, { "description", "The starting node identifier." } } },
                                        { "targetId", new Dictionary<string, object> { { "type", "string" }, { "description", "The target node identifier." } } }
                                    }
                                },
                                { "required", new[] { "startId", "targetId" } }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "find_neighborhood" },
                        { "description", "Finds all connected neighbor nodes within a specific hop distance from a source node." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "sourceId", new Dictionary<string, object> { { "type", "string" }, { "description", "The source node identifier." } } },
                                        { "maxHops", new Dictionary<string, object> { { "type", "integer" }, { "description", "The maximum hop distance (depth) of neighbors to retrieve." } } }
                                    }
                                },
                                { "required", new[] { "sourceId", "maxHops" } }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "save_graph" },
                        { "description", "Persists the current raw graph memory arrays to a disk file." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "filePath", new Dictionary<string, object> { { "type", "string" }, { "description", "The file path to save to (defaults to graph_database.bin)." } } }
                                    }
                                }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "load_graph" },
                        { "description", "Loads a persisted graph database file from disk into memory." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "filePath", new Dictionary<string, object> { { "type", "string" }, { "description", "The file path to load from (defaults to graph_database.bin)." } } }
                                    }
                                }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "ping" },
                        { "description", "A simple ping tool to verify the graph server is responding." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>() }
                            }
                        }
                    }
                }
            };
        }

        private async Task<object> HandleToolCallAsync(JsonElement parameters)
        {
            string name = parameters.GetProperty("name").GetString() ?? "";
            JsonElement args = parameters.GetProperty("arguments");

            if (name == "ping")
            {
                return CreateToolResponse("pong");
            }
            else if (name == "add_node")
            {
                string nodeId = args.GetProperty("id").GetString()!;
                string metadata = args.TryGetProperty("metadata", out JsonElement m) ? m.GetString() ?? "" : "";
                
                int internalId = _store.AddNode(nodeId, metadata);
                return CreateToolResponse($"Successfully added node: '{nodeId}' with metadata: '{metadata}' (Internal ID: {internalId}). Total Nodes: {_store.NodeCount}");
            }
            else if (name == "add_edge")
            {
                string source = args.GetProperty("source").GetString()!;
                string target = args.GetProperty("target").GetString()!;
                string relation = args.GetProperty("relation").GetString()!;

                _store.AddEdge(source, target, relation);
                return CreateToolResponse($"Successfully linked {source} -> {relation} -> {target}. Total Edges: {_store.EdgeCount}");
            }
            else if (name == "find_shortest_path")
            {
                string start = args.GetProperty("startId").GetString()!;
                string target = args.GetProperty("targetId").GetString()!;

                var path = _search.FindShortestPath(start, target);
                string content = path.Count > 0
                    ? $"Path found: {string.Join(" -> ", path)}"
                    : $"No path exists between '{start}' and '{target}'.";

                return CreateToolResponse(content);
            }
            else if (name == "find_neighborhood")
            {
                string source = args.GetProperty("sourceId").GetString()!;
                int maxHops = args.GetProperty("maxHops").GetInt32();

                var neighbors = _search.FindNeighborhood(source, maxHops);
                string content = neighbors.Count > 0
                    ? $"Found {neighbors.Count} nodes within {maxHops} hops:\n{string.Join(", ", neighbors)}"
                    : $"No neighbors found within {maxHops} hops for '{source}'.";

                return CreateToolResponse(content);
            }
            else if (name == "save_graph")
            {
                string filePath = args.TryGetProperty("filePath", out JsonElement pathElem) ? pathElem.GetString() ?? "graph_database.bin" : "graph_database.bin";
                
                _store.SaveToDisk(filePath);
                
                long size = new FileInfo(filePath).Length;
                return CreateToolResponse($"Successfully persisted raw graph arrays to '{filePath}' ({size / 1024.0 / 1024.0:F2} MB). Verified Nodes: {_store.NodeCount} | Edges: {_store.EdgeCount}");
            }
            else if (name == "load_graph")
            {
                string filePath = args.TryGetProperty("filePath", out JsonElement pathElem) ? pathElem.GetString() ?? "graph_database.bin" : "graph_database.bin";
                
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Graph database file not found at path: '{filePath}'");
                }

                var revivedStore = GraphStore.LoadFromDisk(filePath);
                _store = revivedStore;
                _search = new GraphSearch(revivedStore);

                return CreateToolResponse($"Successfully loaded graph database from '{filePath}'. Verified Nodes: {_store.NodeCount} | Edges: {_store.EdgeCount}");
            }

            throw new Exception($"Tool '{name}' is not supported.");
        }

        private static object CreateToolResponse(string text)
        {
            return new
            {
                content = new[]
                {
                    new { type = "text", text = text }
                }
            };
        }

        private async Task SendResponseAsync(JsonElement id, object result)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["result"] = result
            };

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                response["id"] = id;
            }

            string json = JsonSerializer.Serialize(response, JsonOpts);
            Log($"Sending response: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendErrorAsync(JsonElement id, int code, string message)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new { code, message }
            };

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                response["id"] = id;
            }

            string json = JsonSerializer.Serialize(response, JsonOpts);
            Log($"Sending error: {json}");
            await _writer.WriteLineAsync(json);
        }
    }
}