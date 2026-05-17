using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocsWalker.Core.Mcp;

/// <summary>
/// Параметры initialize-запроса MCP. Принимаем только то, что нам реально нужно;
/// остальные поля (capabilities клиента и т.п.) держим как сырой JsonElement —
/// при необходимости разберём по месту.
/// </summary>
public sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion,
    [property: JsonPropertyName("clientInfo")] JsonElement? ClientInfo,
    [property: JsonPropertyName("capabilities")] JsonElement? Capabilities);

/// <summary>
/// Результат initialize. Возвращаем фиксированную версию протокола ((#370)),
/// имя/версию сервера и плоские capabilities — у нас только tools.
/// </summary>
public sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("serverInfo")] McpServerInfo ServerInfo,
    [property: JsonPropertyName("capabilities")] McpServerCapabilities Capabilities);

public sealed record McpServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record McpServerCapabilities(
    [property: JsonPropertyName("tools")] McpToolsCapability? Tools);

/// <summary>
/// listChanged=false — мы не уведомляем об изменении манифеста после старта
/// (Commands.All статический). Поле всё равно объявляем явно, иначе клиенты
/// могут ожидать tools/list_changed и не подписываться вовремя.
/// </summary>
public sealed record McpToolsCapability(
    [property: JsonPropertyName("listChanged")] bool ListChanged);

/// <summary>
/// tools/list — без параметров (опциональные cursor/pagination мы не используем).
/// </summary>
public sealed record ListToolsResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<McpTool> Tools);

/// <summary>
/// Описание одного MCP-tool. <see cref="InputSchema"/> — JSON-Schema; держим её
/// как <see cref="JsonObject"/>, потому что схемы строятся динамически из
/// CommandSpec и не имеют единого record-типа.
/// </summary>
public sealed record McpTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonObject InputSchema);

/// <summary>
/// tools/call params. <see cref="Arguments"/> — произвольный объект; парсится в McpServer.
/// </summary>
public sealed record CallToolParams(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonElement? Arguments);

/// <summary>
/// tools/call result. <see cref="Content"/> — список content-items; для нас всегда
/// один элемент типа "text". <see cref="IsError"/>=true сигнализирует об ошибке
/// исполнения tool'а (в отличие от protocol-level error в JsonRpcResponse.Error).
/// </summary>
public sealed record CallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpContentItem> Content,
    [property: JsonPropertyName("isError")] bool? IsError);

public sealed record McpContentItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);
