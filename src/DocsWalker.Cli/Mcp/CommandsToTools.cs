using DocsWalker.Cli.Cli;
using DocsWalker.Core.Mcp;

namespace DocsWalker.Cli.Mcp;

/// <summary>
/// Конвертирует <see cref="Commands.All"/> CLI-команд в манифест MCP-tools.
/// Команда <c>run</c> исключается — это серверная сама-по-себе команда, через
/// MCP её вызывать не предполагается. Все остальные read+write команды доступны
/// LLM как MCP-tools 1:1 с CLI ((#364) docs/DocsWalker.yml).
/// </summary>
internal static class CommandsToTools
{
    public static IReadOnlyList<McpToolDescriptor> Build()
    {
        var list = new List<McpToolDescriptor>();
        foreach (var spec in Commands.All)
        {
            if (spec.KebabName == "run") continue;
            // mcp-server тоже исключаем — сама себя как tool регистрировать незачем.
            if (spec.KebabName == "mcp-server") continue;

            var parameters = new List<McpToolParam>(spec.Params.Count);
            foreach (var p in spec.Params)
            {
                var (jsonType, itemsType) = MapParamType(p.Type);
                parameters.Add(new McpToolParam(
                    Name: p.KebabName,
                    JsonType: jsonType,
                    Required: p.Required,
                    Description: p.Description,
                    ItemsJsonType: itemsType));
            }

            // Универсальные параметры, не входящие в CommandSpec.Params, но
            // обрабатываемые на уровне Dispatcher: --root (резолв корня) и
            // --dry-run (write-команды). LLM полезно их видеть в схеме.
            parameters.Add(new McpToolParam(
                Name: "root",
                JsonType: "string",
                Required: false,
                Description: "Каталог проекта (содержит docs/). Если не указан — auto-detect от cwd."));
            if (spec.Kind == CommandKind.Write)
            {
                parameters.Add(new McpToolParam(
                    Name: "dry-run",
                    JsonType: "boolean",
                    Required: false,
                    Description: "true → не записывать на FS, вернуть applied=false. По умолчанию false."));
            }

            var description = spec.Description ?? $"CLI-команда {spec.KebabName}.";
            if (spec.Examples is { Count: > 0 })
            {
                description += "\n\nПримеры CLI:\n" + string.Join("\n", spec.Examples);
            }

            list.Add(new McpToolDescriptor(
                Name: spec.KebabName,
                Description: description,
                Params: parameters));
        }
        return list;
    }

    private static (string JsonType, string? ItemsType) MapParamType(ParamType type) => type switch
    {
        ParamType.String    => ("string", null),
        ParamType.Integer   => ("integer", null),
        ParamType.IdList    => ("array", "integer"),
        ParamType.Json      => ("object", null),
        // Array of object: MCP-клиент шлёт arguments.<name>=[...]
        // напрямую (а не через escape-string). Конвертер McpServer.JsonValueToCliString
        // распознаёт пару (array, object) и передаёт raw JSON со скобками в CLI.
        ParamType.JsonArray => ("array", "object"),
        _ => ("string", null),
    };
}
