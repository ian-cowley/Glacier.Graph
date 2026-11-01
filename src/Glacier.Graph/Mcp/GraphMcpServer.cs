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
    /// A zero-dependency, Native AOT-compatible Model Context Protocol (MCP) server over Stdio.
    /// Exposes the high-performance Glacier.Graph engine to AI agents using source-generated JSON serialization.
    /// </summary>
    public class GraphMcpServer
    {
        private readonly object _stateLock = new();
        private GraphStore _store;
        private GraphSearch _search;
        private bool _ownsCurrentStore;

        private readonly TextReader _reader;
        private readonly TextWriter _writer;
        private readonly bool _debugLoggingEnabled;

        public GraphMcpServer(GraphStore store, GraphSearch search)
            : this(
                store,
                search,
                new StreamReader(Console.OpenStandardInput(), Encoding.UTF8),
                CreateStdOutWriter())
        {
        }

        public GraphMcpServer(GraphStore store, GraphSearch search, Stream input, Stream output)
            : this(
                store,
                search,
                new StreamReader(input ?? throw new ArgumentNullException(nameof(input)), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true),
                CreateUtf8NoBomWriter(output ?? throw new ArgumentNullException(nameof(output))))
        {
        }

        public GraphMcpServer(GraphStore store, GraphSearch search, TextReader reader, TextWriter writer)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));

            string? debugEnv = Environment.GetEnvironmentVariable("GLACIER_GRAPH_MCP_DEBUG");
            _debugLoggingEnabled = string.Equals(debugEnv, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(debugEnv, "true", StringComparison.OrdinalIgnoreCase);

            Log("Glacier.Graph MCP server initialized.");
        }

        private static StreamWriter CreateStdOutWriter()
        {
            return CreateUtf8NoBomWriter(Console.OpenStandardOutput());
        }

        private static StreamWriter CreateUtf8NoBomWriter(Stream output)
        {
            var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            return writer;
        }

        private void Log(string message)
        {
            if (!_debugLoggingEnabled) return;

            try
            {
                Console.Error.WriteLine($"[Glacier.Graph.MCP {DateTime.UtcNow:O}] {message}");
            }
            catch
            {
                // Never crash on diagnostic stderr output failure
            }
        }

        public async Task RunAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement? requestId = null;
                try
                {
                    Log($"Received: {line}");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("id", out JsonElement idElem) && idElem.ValueKind != JsonValueKind.Undefined)
                    {
                        requestId = idElem.Clone();
                    }

                    if (!root.TryGetProperty("method", out JsonElement methodElem))
                    {
                        Log("Message missing 'method' property.");
                        if (requestId.HasValue && requestId.Value.ValueKind != JsonValueKind.Null)
                        {
                            await SendErrorAsync(requestId, -32600, "Invalid Request: missing 'method' property.");
                        }
                        continue;
                    }

                    string method = methodElem.GetString() ?? string.Empty;
                    Log($"Method: {method}");

                    switch (method)
                    {
                        case "initialize":
                            await SendInitializeResponseAsync(requestId, HandleInitialize());
                            break;

                        case "notifications/initialized":
                            Log("Initialized notification received.");
                            break;

                        case "notifications/cancelled":
                            Log("Client cancelled a request.");
                            break;

                        case "tools/list":
                            await SendToolsListResponseAsync(requestId, HandleToolsList());
                            break;

                        case "tools/call":
                            try
                            {
                                if (root.TryGetProperty("params", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object)
                                {
                                    McpToolCallResult result = HandleToolCall(parameters);
                                    await SendToolCallResponseAsync(requestId, result);
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
                            if (requestId.HasValue && requestId.Value.ValueKind != JsonValueKind.Null)
                            {
                                await SendErrorAsync(requestId, -32601, $"Method '{method}' not found.");
                            }
                            break;
                    }
                }
                catch (JsonException jsonEx)
                {
                    Log($"JSON parse error: {jsonEx.Message}");
                    await SendErrorAsync(requestId, -32700, "Parse error.");
                }
                catch (Exception ex)
                {
                    Log($"CRITICAL ERROR: {ex.Message}");
                    if (requestId.HasValue && requestId.Value.ValueKind != JsonValueKind.Null)
                    {
                        try { await SendErrorAsync(requestId, -32603, "Internal server error."); } catch { }
                    }
                }
            }
        }

        private static McpInitializeResult HandleInitialize()
        {
            return new McpInitializeResult
            {
                ProtocolVersion = "2024-11-05",
                Capabilities = new McpCapabilities
                {
                    Tools = new McpToolsCapability { ListChanged = false }
                },
                ServerInfo = new McpServerInfo
                {
                    Name = "Glacier.Graph",
                    Version = "1.1.0"
                }
            };
        }

        private static McpToolsListResult HandleToolsList()
        {
            return new McpToolsListResult
            {
                Tools =
                [
                    new McpToolDefinition
                    {
                        Name = "add_node",
                        Description = "Registers a new node in the graph, or updates its metadata if it already exists.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["id"] = new() { Type = "string", Description = "The unique identifier of the node." },
                                ["metadata"] = new() { Type = "string", Description = "Optional JSON or text metadata associated with the node." }
                            },
                            Required = ["id"]
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "add_edge",
                        Description = "Creates a directed relationship between two nodes with an optional non-negative weight. Automatically creates the nodes if they don't exist.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["source"] = new() { Type = "string", Description = "The starting node identifier." },
                                ["target"] = new() { Type = "string", Description = "The ending node identifier." },
                                ["relation"] = new() { Type = "string", Description = "The type of relation (e.g., KNOWS, FOLLOWS, REPORTS_TO)." },
                                ["weight"] = new() { Type = "number", Description = "Optional non-negative edge weight (defaults to 1.0)." }
                            },
                            Required = ["source", "target", "relation"]
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "find_shortest_path",
                        Description = "Finds the shortest path between two nodes using Breadth-First Search (BFS) or weighted Dijkstra when 'weighted' is true.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["startId"] = new() { Type = "string", Description = "The starting node identifier." },
                                ["targetId"] = new() { Type = "string", Description = "The target node identifier." },
                                ["weighted"] = new() { Type = "boolean", Description = "Optional flag to run weighted Dijkstra instead of unweighted BFS." }
                            },
                            Required = ["startId", "targetId"]
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "find_neighborhood",
                        Description = "Finds all connected neighbor nodes within a specific hop distance from a source node.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["sourceId"] = new() { Type = "string", Description = "The source node identifier." },
                                ["maxHops"] = new() { Type = "integer", Description = "The maximum hop distance (depth) of neighbors to retrieve." }
                            },
                            Required = ["sourceId", "maxHops"]
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "save_graph",
                        Description = "Persists the current raw graph memory arrays to a disk file.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["filePath"] = new() { Type = "string", Description = "The file path to save to (defaults to graph_database.bin)." }
                            }
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "load_graph",
                        Description = "Loads a persisted graph database file from disk into memory.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["filePath"] = new() { Type = "string", Description = "The file path to load from (defaults to graph_database.bin)." }
                            }
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "ping",
                        Description = "A simple ping tool to verify the graph server is responding.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>()
                        }
                    }
                ]
            };
        }

        private McpToolCallResult HandleToolCall(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("name", out JsonElement nameElem))
            {
                throw new ArgumentException("Missing tool 'name'.");
            }

            string name = nameElem.GetString() ?? string.Empty;
            JsonElement args = parameters.TryGetProperty("arguments", out JsonElement argsElem) && argsElem.ValueKind == JsonValueKind.Object
                ? argsElem
                : default;

            switch (name)
            {
                case "ping":
                    return CreateToolResponse("pong");

                case "add_node":
                {
                    string nodeId = GetRequiredString(args, "id");
                    string metadata = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("metadata", out JsonElement m)
                        ? (m.GetString() ?? string.Empty)
                        : string.Empty;

                    lock (_stateLock)
                    {
                        int internalId = _store.AddNode(nodeId, metadata);
                        return CreateToolResponse(
                            $"Successfully added node: '{nodeId}' with metadata: '{metadata}' (Internal ID: {internalId}). Total Nodes: {_store.NodeCount}");
                    }
                }

                case "add_edge":
                {
                    string source = GetRequiredString(args, "source");
                    string target = GetRequiredString(args, "target");
                    string relation = GetRequiredString(args, "relation");
                    float weight = args.ValueKind == JsonValueKind.Object
                        && args.TryGetProperty("weight", out JsonElement wElem)
                        && wElem.ValueKind == JsonValueKind.Number
                        ? wElem.GetSingle()
                        : 1.0f;

                    lock (_stateLock)
                    {
                        _store.AddEdge(source, target, relation, weight);
                        return CreateToolResponse(
                            $"Successfully linked {source} -> {relation} -> {target}. Total Edges: {_store.EdgeCount}");
                    }
                }

                case "find_shortest_path":
                {
                    string start = GetRequiredString(args, "startId");
                    string target = GetRequiredString(args, "targetId");
                    bool weighted = args.ValueKind == JsonValueKind.Object
                        && args.TryGetProperty("weighted", out JsonElement weightedElem)
                        && (weightedElem.ValueKind == JsonValueKind.True || weightedElem.ValueKind == JsonValueKind.False)
                        && weightedElem.GetBoolean();

                    List<string> path;
                    lock (_stateLock)
                    {
                        path = weighted
                            ? _search.FindShortestPathDijkstra(start, target)
                            : _search.FindShortestPath(start, target);
                    }

                    string content = path.Count > 0
                        ? $"Path found: {string.Join(" -> ", path)}"
                        : $"No path exists between '{start}' and '{target}'.";

                    return CreateToolResponse(content);
                }

                case "find_neighborhood":
                {
                    string source = GetRequiredString(args, "sourceId");
                    if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("maxHops", out JsonElement maxHopsElem))
                    {
                        throw new ArgumentException("Missing required argument 'maxHops'.");
                    }

                    int maxHops = maxHopsElem.GetInt32();
                    List<string> neighbors;
                    lock (_stateLock)
                    {
                        neighbors = _search.FindNeighborhood(source, maxHops);
                    }

                    string content = neighbors.Count > 0
                        ? $"Found {neighbors.Count} nodes within {maxHops} hops:\n{string.Join(", ", neighbors)}"
                        : $"No neighbors found within {maxHops} hops for '{source}'.";

                    return CreateToolResponse(content);
                }

                case "save_graph":
                {
                    string filePath = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("filePath", out JsonElement pathElem)
                        ? (pathElem.GetString() ?? "graph_database.bin")
                        : "graph_database.bin";

                    int nodeCount;
                    int edgeCount;
                    lock (_stateLock)
                    {
                        _store.SaveToDisk(filePath);
                        nodeCount = _store.NodeCount;
                        edgeCount = _store.EdgeCount;
                    }

                    long size = new FileInfo(filePath).Length;
                    return CreateToolResponse(
                        $"Successfully persisted raw graph arrays to '{filePath}' ({size / 1024.0 / 1024.0:F2} MB). Verified Nodes: {nodeCount} | Edges: {edgeCount}");
                }

                case "load_graph":
                {
                    string filePath = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("filePath", out JsonElement pathElem)
                        ? (pathElem.GetString() ?? "graph_database.bin")
                        : "graph_database.bin";

                    if (!File.Exists(filePath))
                    {
                        throw new FileNotFoundException($"Graph database file not found at path: '{filePath}'", filePath);
                    }

                    var revivedStore = GraphStore.LoadFromDisk(filePath);
                    var revivedSearch = new GraphSearch(revivedStore);
                    GraphStore? previousOwnedStore = null;
                    int nodeCount;
                    int edgeCount;

                    lock (_stateLock)
                    {
                        if (_ownsCurrentStore && !ReferenceEquals(_store, revivedStore))
                        {
                            previousOwnedStore = _store;
                        }

                        _store = revivedStore;
                        _search = revivedSearch;
                        _ownsCurrentStore = true;
                        nodeCount = _store.NodeCount;
                        edgeCount = _store.EdgeCount;
                    }

                    previousOwnedStore?.Dispose();

                    return CreateToolResponse(
                        $"Successfully loaded graph database from '{filePath}'. Verified Nodes: {nodeCount} | Edges: {edgeCount}");
                }

                default:
                    throw new NotSupportedException($"Tool '{name}' is not supported.");
            }
        }

        private static string GetRequiredString(JsonElement args, string propertyName)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(propertyName, out JsonElement prop)
                || prop.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"Missing required string argument '{propertyName}'.");
            }

            return prop.GetString() ?? string.Empty;
        }

        private static McpToolCallResult CreateToolResponse(string text)
        {
            return new McpToolCallResult
            {
                Content =
                [
                    new McpContentItem { Type = "text", Text = text }
                ]
            };
        }

        private async Task SendInitializeResponseAsync(JsonElement? id, McpInitializeResult result)
        {
            var envelope = new McpInitializeResponse
            {
                Id = id,
                Result = result
            };
            string json = JsonSerializer.Serialize(envelope, GraphMcpJsonContext.Default.McpInitializeResponse);
            Log($"Sending response: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendToolsListResponseAsync(JsonElement? id, McpToolsListResult result)
        {
            var envelope = new McpToolsListResponse
            {
                Id = id,
                Result = result
            };
            string json = JsonSerializer.Serialize(envelope, GraphMcpJsonContext.Default.McpToolsListResponse);
            Log($"Sending response: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendToolCallResponseAsync(JsonElement? id, McpToolCallResult result)
        {
            var envelope = new McpToolCallResponse
            {
                Id = id,
                Result = result
            };
            string json = JsonSerializer.Serialize(envelope, GraphMcpJsonContext.Default.McpToolCallResponse);
            Log($"Sending response: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendErrorAsync(JsonElement? id, int code, string message)
        {
            var envelope = new McpErrorResponse
            {
                Id = id,
                Error = new McpRpcError { Code = code, Message = message }
            };
            string json = JsonSerializer.Serialize(envelope, GraphMcpJsonContext.Default.McpErrorResponse);
            Log($"Sending error: {json}");
            await _writer.WriteLineAsync(json);
        }
    }

    // --- Native AOT Typed MCP DTOs ---

    public sealed class McpInitializeResponse
    {
        public string Jsonrpc { get; init; } = "2.0";
        public JsonElement? Id { get; init; }
        public required McpInitializeResult Result { get; init; }
    }

    public sealed class McpToolsListResponse
    {
        public string Jsonrpc { get; init; } = "2.0";
        public JsonElement? Id { get; init; }
        public required McpToolsListResult Result { get; init; }
    }

    public sealed class McpToolCallResponse
    {
        public string Jsonrpc { get; init; } = "2.0";
        public JsonElement? Id { get; init; }
        public required McpToolCallResult Result { get; init; }
    }

    public sealed class McpErrorResponse
    {
        public string Jsonrpc { get; init; } = "2.0";
        public JsonElement? Id { get; init; }
        public required McpRpcError Error { get; init; }
    }

    public sealed class McpRpcError
    {
        public int Code { get; init; }
        public required string Message { get; init; }
    }

    public sealed class McpInitializeResult
    {
        public required string ProtocolVersion { get; init; }
        public required McpCapabilities Capabilities { get; init; }
        public required McpServerInfo ServerInfo { get; init; }
    }

    public sealed class McpCapabilities
    {
        public required McpToolsCapability Tools { get; init; }
    }

    public sealed class McpToolsCapability
    {
        public bool ListChanged { get; init; }
    }

    public sealed class McpServerInfo
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
    }

    public sealed class McpToolsListResult
    {
        public required McpToolDefinition[] Tools { get; init; }
    }

    public sealed class McpToolDefinition
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required McpInputSchema InputSchema { get; init; }
    }

    public sealed class McpInputSchema
    {
        public required string Type { get; init; }
        public required Dictionary<string, McpPropertySchema> Properties { get; init; }
        public string[]? Required { get; init; }
    }

    public sealed class McpPropertySchema
    {
        public required string Type { get; init; }
        public string? Description { get; init; }
    }

    public sealed class McpToolCallResult
    {
        public required McpContentItem[] Content { get; init; }
        public bool? IsError { get; init; }
    }

    public sealed class McpContentItem
    {
        public required string Type { get; init; }
        public required string Text { get; init; }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    [JsonSerializable(typeof(McpInitializeResponse))]
    [JsonSerializable(typeof(McpToolsListResponse))]
    [JsonSerializable(typeof(McpToolCallResponse))]
    [JsonSerializable(typeof(McpErrorResponse))]
    public partial class GraphMcpJsonContext : JsonSerializerContext
    {
    }
}
