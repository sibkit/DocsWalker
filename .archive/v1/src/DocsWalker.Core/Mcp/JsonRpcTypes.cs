using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocsWalker.Core.Mcp;

/// <summary>
/// JSON-RPC 2.0 запрос. <see cref="Id"/> сохраняется как сырой <see cref="JsonElement"/>,
/// чтобы вернуть его в ответе ровно в той форме, в какой пришёл (string|number|null).
/// <see cref="Params"/> и <see cref="Result"/> — тоже сырые JsonElement: тип реального
/// payload зависит от method и парсится по месту.
/// Notification — request без id; в JSON это либо отсутствие поля, либо null. Различаем
/// по <see cref="Id"/>.HasValue + ValueKind.
/// </summary>
public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

/// <summary>
/// JSON-RPC 2.0 ответ. Ровно одно из <see cref="Result"/>/<see cref="Error"/> заполнено.
/// </summary>
public sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error);

/// <summary>
/// JSON-RPC 2.0 error. Стандартные коды: -32700 parse error, -32600 invalid request,
/// -32601 method not found, -32602 invalid params, -32603 internal error.
/// </summary>
public sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data);

/// <summary>
/// Стандартные коды ошибок JSON-RPC 2.0.
/// </summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;
}
