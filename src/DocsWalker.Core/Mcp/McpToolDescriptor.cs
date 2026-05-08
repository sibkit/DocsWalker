namespace DocsWalker.Core.Mcp;

/// <summary>
/// Промежуточный DTO для регистрации одной CLI-команды как MCP-tool. CLI-слой
/// (DocsWalker.Cli/Mcp/CommandsToTools.cs) знает CommandSpec и кладёт сюда:
/// kebab-имя tool'а, description, и параметры в форме <see cref="McpToolParam"/>.
/// Core-слой (McpServer) сам строит JSON-Schema из этого списка и оформляет
/// <see cref="McpTool"/> для tools/list. Так Core не зависит от Cli.
/// </summary>
public sealed record McpToolDescriptor(
    string Name,
    string Description,
    IReadOnlyList<McpToolParam> Params);

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
