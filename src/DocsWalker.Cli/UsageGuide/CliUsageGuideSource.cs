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
    public string GetMentalModel() => UsageGuideText.MentalModel;

    public IReadOnlyList<UsageGuideCommand> GetCommands()
    {
        var llmTools = GetLlmJsonApiTools();
        var result = new List<UsageGuideCommand>(llmTools.Count + Commands.All.Count);
        result.AddRange(llmTools);
        foreach (var cmd in Commands.All.Where(IsMcpFacingCommand))
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

    private static bool IsMcpFacingCommand(CommandSpec cmd) =>
        !string.Equals(cmd.KebabName, "repl", StringComparison.Ordinal);

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
                "hit",
                "read",
                "LLM-facing JSON API: безопасная проверка selector-ов и будущих write-ops без записи. Принимает defaults и ops[].",
                "tools/call name=hit arguments={\"ops\":[{\"op\":\"select\",\"select\":{\"path\":\"DocsWalker-LLM JSON API\"}}]}"),
            LlmTool(
                "query",
                "read",
                "LLM-facing JSON API: чтение данных по select- и grep-операциям. Принимает defaults и ops[].",
                "tools/call name=query arguments={\"ops\":[{\"op\":\"grep\",\"pattern\":\"validation_failed\",\"limit\":20}]}"),
            LlmTool(
                "tx",
                "write",
                "LLM-facing JSON API: атомарное внесение изменений. Принимает defaults и ops[].",
                "tools/call name=tx arguments={\"ops\":[{\"op\":\"update\",\"id\":42,\"set\":{\"text\":\"...\"}}]}"),
        };

    private static UsageGuideCommand LlmTool(
        string name,
        string kind,
        string description,
        string example) =>
        new(
            Name: name,
            Kind: kind,
            Description: description,
            Parameters: new[]
            {
                new UsageGuideParameter(
                    "defaults",
                    "json",
                    Required: false,
                    "Опциональные defaults LLM JSON API: path_parent и coordinates."),
                new UsageGuideParameter(
                    "ops",
                    "json_array",
                    Required: true,
                    "Массив операций LLM JSON API. Метод задается именем tool."),
            },
            Examples: new[] { example });
}
