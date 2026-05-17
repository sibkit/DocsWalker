namespace DocsWalker.Core.Mcp;

/// <summary>
/// Промежуточный DTO для регистрации одного MCP-tool: kebab-имя tool'а,
/// description и параметры в форме <see cref="McpToolParam"/>.
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
