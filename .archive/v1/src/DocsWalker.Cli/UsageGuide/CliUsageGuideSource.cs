using DocsWalker.Cli.Cli;
using DocsWalker.Core.Api;

namespace DocsWalker.Cli.UsageGuide;

/// <summary>
/// Kernel/MCP-facing реализация <see cref="IUsageGuideSource"/>: ментальная модель — из
/// <see cref="UsageGuideText.MentalModel"/>, manifest — из <see cref="Commands.All"/>
/// (legacy CLI пока владеет статической декларацией tool-контрактов).
/// </summary>
internal sealed class CliUsageGuideSource : IUsageGuideSource
{
    private static readonly HashSet<string> AdvertisedCommandNames = new(StringComparer.Ordinal)
    {
        "describe-type",
        "get-overview",
        "get-usage-guide",
        "get-schema",
    };

    public string GetMentalModel() => UsageGuideText.MentalModel;

    public IReadOnlyList<UsageGuideCommand> GetCommands()
    {
        var llmTools = GetLlmJsonApiTools();
        var result = new List<UsageGuideCommand>(llmTools.Count + Commands.All.Count);
        result.AddRange(llmTools);
        foreach (var cmd in Commands.All.Where(IsAdvertisedCommand))
        {
            var parameters = new List<UsageGuideParameter>(cmd.Params.Count);
            foreach (var p in cmd.Params)
            {
                parameters.Add(new UsageGuideParameter(
                    Name: p.KebabName,
                    Type: ParamTypeToString(p.Type),
                    Required: p.Required,
                    Description: NormalizeMcpText(p.Description)));
            }
            result.Add(new UsageGuideCommand(
                Name: cmd.KebabName,
                Kind: cmd.Kind == CommandKind.Write ? "write" : "read",
                Description: NormalizeMcpText(cmd.Description),
                Parameters: parameters,
                Examples: new[] { BuildMcpToolCallExample(cmd) }));
        }
        return result;
    }

    private static bool IsAdvertisedCommand(CommandSpec cmd) =>
        AdvertisedCommandNames.Contains(cmd.KebabName);

    private static string BuildMcpToolCallExample(CommandSpec cmd)
    {
        var args = cmd.Params
            .Where(p => p.Required)
            .Select(p => $"\"{p.KebabName}\":{SampleJsonValue(p)}");
        return $"tools/call name={cmd.KebabName} arguments={{" + string.Join(",", args) + "}";
    }

    private static string SampleJsonValue(CommandParam param) => param.Type switch
    {
        ParamType.String => $"\"{SampleString(param.KebabName)}\"",
        ParamType.Integer => "42",
        ParamType.IdList => "[42]",
        ParamType.Json => "{}",
        ParamType.JsonArray => "[]",
        ParamType.Boolean => "true",
        _ => "\"...\"",
    };

    private static string SampleString(string name) => name switch
    {
        "name" => "section",
        "type" => "section",
        "title" => "Новый раздел",
        "text" => "Текст узла",
        "path" => "DocsWalker",
        "query" => "валидатор",
        "yaml-text" => "description: ...",
        _ => "...",
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

    private static string ParamTypeToString(ParamType type) => type switch
    {
        ParamType.String => "string",
        ParamType.Integer => "integer",
        ParamType.IdList => "id_list",
        ParamType.Json => "json",
        ParamType.JsonArray => "json_array",
        ParamType.Boolean => "boolean",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static IReadOnlyList<UsageGuideCommand> GetLlmJsonApiTools() =>
        new[]
        {
            LlmTool(
                "query",
                "read",
                "LLM-facing JSON API: чтение данных по select-операциям.",
                "tools/call name=query arguments={\"ops\":[{\"op\":\"select\",\"select\":{\"path\":\"DocsWalker-LLM JSON API/**\",\"match\":{\"regex\":\"validation_failed\"}}}]}"),
            LlmTool(
                "tx",
                "write",
                "LLM-facing JSON API: атомарное внесение изменений с intent, expected_count и server-side validation.",
                "tools/call name=tx arguments={\"intent\":\"обновить прочитанный узел\",\"ops\":[{\"op\":\"update\",\"ids\":[42],\"expected_count\":1,\"set\":{\"text\":\"...\"}}]}"),
            LlmTool(
                "rollback",
                "write",
                "LLM-facing JSON API: откат транзакции по tx_id.",
                "tools/call name=rollback arguments={\"tx_id\":\"tx_...\"}"),
            LlmTool(
                "scheme",
                "read",
                "LLM-facing JSON API: чтение контракта Схемы через JSON ops get, describe_type и describe_tree.",
                "tools/call name=scheme arguments={\"ops\":[{\"op\":\"describe_type\",\"name\":\"statement\"}]}"),
        };

    private static UsageGuideCommand LlmTool(
        string name,
        string kind,
        string description,
        string example)
    {
        var parameters = new List<UsageGuideParameter>();
        if (name == "rollback")
        {
            parameters.Add(new UsageGuideParameter(
                "tx_id",
                "string",
                Required: true,
                "Непрозрачный tx_id, который вернул успешный tx."));
            return new UsageGuideCommand(
                Name: name,
                Kind: kind,
                Description: description,
                Parameters: parameters,
                Examples: new[] { example });
        }

        if (name != "scheme")
        {
            parameters.Add(new UsageGuideParameter(
                "defaults",
                "json",
                Required: false,
                "Опциональные defaults LLM JSON API: path_parent и coordinates."));
        }

        parameters.Add(new UsageGuideParameter(
            "ops",
            "json_array",
            Required: true,
            "Массив операций LLM JSON API. Метод задается именем tool."));

        if (name == "tx")
        {
            parameters.Add(new UsageGuideParameter(
                "intent",
                "string",
                Required: false,
                "Зачем нужна запись; обязательно для tx."));
        }

        return new UsageGuideCommand(
            Name: name,
            Kind: kind,
            Description: description,
            Parameters: parameters,
            Examples: new[] { example });
    }
}
