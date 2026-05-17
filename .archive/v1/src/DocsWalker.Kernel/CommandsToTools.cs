using DocsWalker.Cli.Cli;
using DocsWalker.Core.Mcp;

namespace DocsWalker.Kernel;

/// <summary>
/// Конвертирует command contracts в манифест MCP-tools.
/// Возвращает только компактную LLM-facing surface. Старый legacy command set
/// не публикуется и не остаётся callable через kernel MCP.
/// <para>
/// Жильё в DocsWalker.Kernel: MCP-сервер живёт в kernel'е
/// (RpcDispatcher.HandleListTools/HandleCallToolAsync), а DocsWalker.Mcp.exe —
/// лишь stdio↔HTTP bridge.
/// </para>
/// </summary>
internal static class CommandsToTools
{
    private static readonly HashSet<string> AdvertisedCommandNames = new(StringComparer.Ordinal)
    {
        "describe-type",
        "get-overview",
        "get-usage-guide",
        "get-schema",
    };

    public static IReadOnlyList<McpToolDescriptor> Build()
        => BuildCore();

    private static IReadOnlyList<McpToolDescriptor> BuildCore()
    {
        var list = new List<McpToolDescriptor>
        {
            BuildLlmJsonApiTool(
                "query",
                "LLM-facing JSON API: чтение данных по select-операциям."),
            BuildLlmJsonApiTool(
                "tx",
                "LLM-facing JSON API: атомарное внесение изменений с intent, expected_count и server-side validation."),
            BuildLlmJsonApiTool(
                "rollback",
                "LLM-facing JSON API: откат транзакции по tx_id."),
            BuildLlmJsonApiTool(
                "scheme",
                "LLM-facing JSON API: чтение контракта Схемы через JSON ops get, describe_type и describe_tree."),
        };
        foreach (var spec in Commands.All)
        {
            if (!IsAdvertisedCommand(spec)) continue;

            var parameters = new List<McpToolParam>(spec.Params.Count);
            foreach (var p in spec.Params)
            {
                var (jsonType, itemsType) = MapParamType(p.Type);
                parameters.Add(new McpToolParam(
                    Name: p.KebabName,
                    JsonType: jsonType,
                    Required: p.Required,
                    Description: NormalizeMcpText(p.Description),
                    ItemsJsonType: itemsType));
            }

            // Универсальный --dry-run для write-команд (на уровне Dispatcher,
            // не часть CommandSpec.Params). --root убран в stg-0010 step-06:
            // клиент про FS не знает, kernel инжектит storage-path сам.
            if (spec.Kind == CommandKind.Write)
            {
                parameters.Add(new McpToolParam(
                    Name: "dry-run",
                    JsonType: "boolean",
                    Required: false,
                    Description: "true → не записывать на FS, вернуть applied=false. По умолчанию false."));
            }

            var description = NormalizeMcpText(spec.Description) ?? $"MCP tool {spec.KebabName}.";

            list.Add(new McpToolDescriptor(
                Name: spec.KebabName,
                Description: description,
                Params: parameters));
        }
        return list;
    }

    private static bool IsAdvertisedCommand(CommandSpec spec) =>
        AdvertisedCommandNames.Contains(spec.KebabName);

    private static McpToolDescriptor BuildLlmJsonApiTool(string name, string description)
    {
        var parameters = new List<McpToolParam>();
        if (name == "rollback")
        {
            parameters.Add(new McpToolParam(
                Name: "tx_id",
                JsonType: "string",
                Required: true,
                Description: "Непрозрачный tx_id, который вернул успешный tx."));
            return new(
                Name: name,
                Description: description,
                Params: parameters);
        }

        if (name != "scheme")
        {
            parameters.Add(new McpToolParam(
                Name: "defaults",
                JsonType: "object",
                Required: false,
                Description: "Опциональные defaults LLM JSON API: path_parent и coordinates."));
        }

        parameters.Add(new McpToolParam(
            Name: "ops",
            JsonType: "array",
            Required: true,
            Description: "Массив операций LLM JSON API. Метод задается именем MCP tool.",
            ItemsJsonType: "object"));

        if (name == "tx")
        {
            parameters.Add(new McpToolParam(
                Name: "intent",
                JsonType: "string",
                Required: false,
                Description: "Зачем нужна запись. Обязательно для tx."));
        }

        return new(
            Name: name,
            Description: description,
            Params: parameters);
    }

    private static (string JsonType, string? ItemsType) MapParamType(ParamType type) => type switch
    {
        ParamType.String => ("string", null),
        ParamType.Integer => ("integer", null),
        ParamType.IdList => ("array", "integer"),
        ParamType.Json => ("object", null),
        // Array of object: MCP-клиент шлёт arguments.<name>=[...]
        // напрямую (а не через escape-string). Конвертер McpServer.JsonValueToCliString
        // распознаёт пару (array, object) и передаёт raw JSON со скобками в CLI.
        ParamType.JsonArray => ("array", "object"),
        ParamType.Boolean => ("boolean", null),
        _ => ("string", null),
    };

    private static string? NormalizeMcpText(string? text)
    {
        if (text is null) return null;

        return text
            .Replace("--<имя_связи>=<id|csv>", "одноимённый relation argument")
            .Replace("--command=<kebab-name>", "argument command=<kebab-name>")
            .Replace("--fields=<csv>", "argument fields=<csv>")
            .Replace("--ids=", "argument ids=")
            .Replace("--from-subtree", "argument from-subtree")
            .Replace("--dry-run", "argument dry-run")
            .Replace("--version", "version")
            .Replace("--compact", "argument compact")
            .Replace("--max-tokens", "argument max-tokens")
            .Replace("--from", "argument from")
            .Replace("--root", "root")
            .Replace("--path", "argument path")
            .Replace("--tree", "argument tree")
            .Replace("--under", "argument under")
            .Replace("--regex", "argument regex")
            .Replace("--unlink", "argument unlink")
            .Replace("--help", "help");
    }

}
