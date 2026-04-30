using System.Text.Json;

namespace GodotMonoMcp;

public sealed class McpServer
{
    private readonly GodotTools _tools;
    private readonly JsonSerializerOptions _jsonOptions;

    public const string ExitMarker = "__EXIT__";

    public McpServer(GodotTools tools, JsonSerializerOptions jsonOptions)
    {
        _tools = tools;
        _jsonOptions = jsonOptions;
    }

    public async Task<string?> ProcessMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(message, _jsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(RpcResponse.FromError(null, -32700, $"Parse error: {ex.Message}"), _jsonOptions);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
        {
            return JsonSerializer.Serialize(RpcResponse.FromError(request?.Id, -32600, "Invalid request"), _jsonOptions);
        }

        if (request.Method.Equals("exit", StringComparison.Ordinal))
        {
            return ExitMarker;
        }

        var response = await HandleRequestAsync(request);
        return response is not null ? JsonSerializer.Serialize(response, _jsonOptions) : null;
    }

    private async Task<RpcResponse?> HandleRequestAsync(RpcRequest request)
    {
        var isNotification = !HasRequestId(request.Id);

        switch (request.Method)
        {
            case "ping":
                return RpcResponse.Success(request.Id, new { });

            case "initialize":
                var clientVersion = GetProtocolVersion(request);
                var protocolVersion = clientVersion switch
                {
                    "2025-03-26" => "2025-03-26",
                    "2024-11-05" => "2024-11-05",
                    _ => "2024-11-05"
                };
                return RpcResponse.Success(request.Id, new
                {
                    protocolVersion,
                    serverInfo = new { name = "godot-mono-mcp", version = "0.1.0" },
                    capabilities = new { tools = new { listChanged = false } }
                });

            case "notifications/initialized":
            case "initialized":
                return null;

            case "shutdown":
                return RpcResponse.Success(request.Id, new { ok = true });

            case "tools/list":
                return RpcResponse.Success(request.Id, new { tools = ToolCatalog.Definitions });

            case "tools/call":
                if (request.Params is null)
                {
                    return isNotification ? null : RpcResponse.FromError(request.Id, -32602, "Missing params");
                }

                var call = request.Params.Value.Deserialize<ToolCallParams>(_jsonOptions);
                if (call is null || string.IsNullOrWhiteSpace(call.Name))
                {
                    return isNotification ? null : RpcResponse.FromError(request.Id, -32602, "Invalid tool call params");
                }

                var result = await _tools.ExecuteAsync(call.Name, call.Arguments ?? new Dictionary<string, JsonElement>());
                return RpcResponse.Success(request.Id, new
                {
                    content = new[]
                    {
                        new { type = "text", text = result }
                    }
                });

            default:
                return isNotification ? null : RpcResponse.FromError(request.Id, -32601, $"Method not found: {request.Method}");
        }
    }

    private static string? GetProtocolVersion(RpcRequest request)
    {
        if (request.Params is null || request.Params.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            if (request.Params.Value.TryGetProperty("protocolVersion", out var versionProp))
            {
                return versionProp.GetString();
            }
        }
        catch { }

        return null;
    }

    private static bool HasRequestId(JsonElement? id)
    {
        return id.HasValue && id.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
    }
}
