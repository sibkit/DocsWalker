using System.Text.Json.Nodes;

namespace DocsWalker.Core.Mcp;

/// <summary>
/// Промежуточный DTO для регистрации одной CLI-команды как MCP-tool. CLI-слой
/// (DocsWalker.Cli/Mcp/CommandsToTools.cs) знает CommandSpec и кладёт сюда:
/// kebab-имя tool'а, description, и параметры в форме <see cref="McpToolParam"/>.
/// Core-слой (McpServer) сам строит JSON-Schema из этого списка и оформляет
/// <see cref="McpTool"/> для tools/list. Так Core не зависит от Cli.
/// <para>
/// <see cref="RawInputSchema"/> — путь обхода для tool'ов с динамическими параметрами
/// (например, <c>create-node</c>): Cli-слой может собрать готовый JSON-Schema-объект
/// из проектной Схемы и положить его сюда; <see cref="McpServer.BuildInputSchema"/>
/// в этом случае использует raw-схему вместо генерации из <see cref="Params"/>.
/// Контракт описан в docs/DocsWalker.yml/«(#377) inputSchema динамических tool».
/// </para>
/// </summary>
public sealed record McpToolDescriptor(
    string Name,
    string Description,
    IReadOnlyList<McpToolParam> Params,
    JsonObject? RawInputSchema = null);

/// <summary>
/// Один параметр CLI-команды для маппинга в JSON-Schema. <see cref="JsonType"/> —
/// строковое имя типа JSON-Schema: "string", "integer", "object", "array".
/// <see cref="ItemsJsonType"/> используется только для <c>"array"</c> и задаёт тип
/// элементов (например, "integer" для id-list).
/// </summary>
public sealed record McpToolParam(
    string Name,
    string JsonType,
    bool Required,
    string? Description,
    string? ItemsJsonType = null);
