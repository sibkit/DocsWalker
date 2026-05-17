using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocsWalker.Kernel;

/// <summary>
/// JSON-RPC 2.0 envelope: запрос. <c>Id</c> хранится сырым <see cref="JsonElement"/>
/// чтобы сохранить тип клиента (number/string/null) при эхо в ответе.
/// </summary>
internal sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string? JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

internal sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data);

internal static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

// ---- MCP типы ---------------------------------------------------------

internal sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("serverInfo")] McpServerInfo ServerInfo,
    [property: JsonPropertyName("capabilities")] McpServerCapabilities Capabilities);

internal sealed record McpServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

internal sealed record McpServerCapabilities(
    [property: JsonPropertyName("tools")] McpToolsCapability Tools);

internal sealed record McpToolsCapability(
    [property: JsonPropertyName("listChanged")] bool ListChanged);

internal sealed record ListToolsResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<McpTool> Tools);

internal sealed record McpTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema);

internal sealed record CallToolParams(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments);

internal sealed record CallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpContentItem> Content,
    [property: JsonPropertyName("isError")] bool? IsError);

internal sealed record McpContentItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

// ---- /health, /api ----------------------------------------------------

internal sealed record HealthResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("graphs")] IReadOnlyList<string> Graphs);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ListToolsResult))]
[JsonSerializable(typeof(McpTool))]
[JsonSerializable(typeof(CallToolParams))]
[JsonSerializable(typeof(CallToolResult))]
[JsonSerializable(typeof(McpContentItem))]
[JsonSerializable(typeof(HealthResponse))]
internal partial class KernelJsonContext : JsonSerializerContext
{
}
