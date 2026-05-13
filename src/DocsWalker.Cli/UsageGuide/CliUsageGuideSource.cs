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
                "LLM-facing JSON API: чтение данных по select- и grep-операциям. С session_id автоматически пополняет read_workset.",
                "tools/call name=query arguments={\"session_id\":\"stg-0015\",\"ops\":[{\"op\":\"grep\",\"pattern\":\"validation_failed\",\"limit\":20}]}"),
            LlmTool(
                "tx",
                "write",
                "LLM-facing JSON API: атомарное внесение изменений. По умолчанию mode=apply_if_safe: kernel запускает preview/guard и не пишет вне session workset.",
                "tools/call name=tx arguments={\"session_id\":\"stg-0015\",\"intent\":\"обновить прочитанный узел\",\"mode\":\"apply_if_safe\",\"ops\":[{\"op\":\"update\",\"id\":42,\"set\":{\"text\":\"...\"}}]}"),
            SessionTool(
                "brief",
                "read",
                "Session lifecycle: собрать compact context pack по goal перед началом или возобновлением задачи.",
                "tools/call name=brief arguments={\"goal\":\"Починить validation_failed в tx\",\"max_tokens\":4000}"),
            SessionTool(
                "checkpoint",
                "write",
                "Session lifecycle: сохранить явный handoff work_session для последующего resume.",
                "tools/call name=checkpoint arguments={\"session_id\":\"stg-0013\",\"summary\":\"...\",\"touched_nodes\":[438]}"),
            SessionTool(
                "resume",
                "read",
                "Session lifecycle: вернуть сохраненный handoff work_session по session_id.",
                "tools/call name=resume arguments={\"session_id\":\"stg-0013\"}"),
            SessionTool(
                "context-check",
                "read",
                "Session lifecycle: проверить будущую запись против session workset и graph revision.",
                "tools/call name=context-check arguments={\"session_id\":\"stg-0013\",\"intent\":\"обновить спецификацию\",\"write\":{\"ops\":[{\"op\":\"update\",\"id\":438,\"set\":{\"text\":\"...\"}}]}}"),
        };

    private static UsageGuideCommand LlmTool(
        string name,
        string kind,
        string description,
        string example)
    {
        var parameters = new List<UsageGuideParameter>
        {
            new(
                "defaults",
                "json",
                Required: false,
                "Опциональные defaults LLM JSON API: path_parent и coordinates."),
            new(
                "ops",
                "json_array",
                Required: true,
                "Массив операций LLM JSON API. Метод задается именем tool."),
        };

        if (name is "query" or "tx")
        {
            parameters.Add(new UsageGuideParameter(
                "session_id",
                "string",
                Required: false,
                "Id work_session; query пополняет read_workset, tx использует session guard."));
        }

        if (name == "tx")
        {
            parameters.Add(new UsageGuideParameter(
                "intent",
                "string",
                Required: false,
                "Зачем нужна запись; обязательно для mode=apply_if_safe/apply."));
            parameters.Add(new UsageGuideParameter(
                "mode",
                "string",
                Required: false,
                "preview | apply_if_safe | apply. По умолчанию apply_if_safe."));
        }

        return new UsageGuideCommand(
            Name: name,
            Kind: kind,
            Description: description,
            Parameters: parameters,
            Examples: new[] { example });
    }

    private static UsageGuideCommand SessionTool(
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
                    "session_id",
                    "string",
                    Required: name is not "brief",
                    "Id work_session; для brief опционален."),
                new UsageGuideParameter(
                    name == "brief" ? "goal" : "payload",
                    name == "brief" ? "string" : "json",
                    Required: name is "brief" or "context-check",
                    "Основной payload session tool."),
            },
            Examples: new[] { example });
}
