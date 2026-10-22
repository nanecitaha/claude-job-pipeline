using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobPipeline.Core;

namespace JobPipeline.Mcp;

/// <summary>
/// Minimal MCP server over stdio: newline-delimited JSON-RPC 2.0 on stdin/stdout, logs on stderr.
/// Implements initialize, ping, tools/list and tools/call, which is all Claude needs for tools.
/// </summary>
public sealed class McpServer(IReadOnlyList<ToolDef> tools, string instructions)
{
    const string Name = "job-pipeline";
    const string Version = "1.0.0";
    const string FallbackProtocol = "2025-06-18";

    readonly Dictionary<string, ToolDef> _tools = tools.ToDictionary(t => t.Name);

    public async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        await using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? reply;
            try
            {
                var node = JsonNode.Parse(line);
                reply = node is JsonArray batch
                    ? new JsonArray(batch.Select(m => Handle(m as JsonObject)).Where(r => r is not null).ToArray())
                    : Handle(node as JsonObject);
                if (reply is JsonArray { Count: 0 }) reply = null;
            }
            catch (JsonException)
            {
                reply = Error(null, -32700, "Parse error");
            }
            if (reply is not null) await writer.WriteLineAsync(reply.ToJsonString());
        }
    }

    JsonNode? Handle(JsonObject? msg)
    {
        if (msg is null) return Error(null, -32600, "Invalid request");
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (method is null) return null;            // a response to something we never send; ignore
        var isNotification = !msg.ContainsKey("id");

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(msg["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => CallTool(msg["params"] as JsonObject),
                _ when method.StartsWith("notifications/") => null,
                _ => throw new RpcException(-32601, $"Method not found: {method}"),
            };
            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (RpcException ex)
        {
            return isNotification ? null : Error(id, ex.Code, ex.Message);
        }
    }

    JsonObject Initialize(JsonObject? p)
    {
        // Answer with the client's version: the tools surface used here is identical across versions.
        var requested = p?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(requested) ? FallbackProtocol : requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = Version },
            ["instructions"] = instructions,
        };
    }

    JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(_tools.Values.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = JsonNode.Parse(t.InputSchema),
        }).ToArray()),
    };

    JsonObject CallTool(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing tool name");
        if (!_tools.TryGetValue(name, out var tool)) throw new RpcException(-32602, $"Unknown tool: {name}");

        var args = JsonSerializer.SerializeToElement(p?["arguments"] ?? new JsonObject());
        try
        {
            var result = tool.Handler(args);
            return ToolResult(JsonSerializer.Serialize(result, JobStore.Json), isError: false);
        }
        catch (Exception ex) when (ex is PipelineException or ArgumentException or FormatException or JsonException or KeyNotFoundException)
        {
            // Business-rule failures go back to the model as a tool error it can read and react to.
            return ToolResult(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] {name} failed: {ex}");
            return ToolResult($"Internal error in {name}: {ex.Message}", isError: true);
        }
    }

    static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

public sealed record ToolDef(string Name, string Description, string InputSchema, Func<JsonElement, object?> Handler);
