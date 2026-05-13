using DocsWalker.Cli.Cli;
using DocsWalker.Core.Api;

namespace DocsWalker.Cli.UsageGuide;

/// <summary>
/// CLI-реализация <see cref="IUsageGuideSource"/>: ментальная модель — из
/// <see cref="UsageGuideText.MentalModel"/>, manifest — из <see cref="Commands.All"/>
/// (одна и та же декларация, по которой парсер CLI разбирает аргументы).
/// </summary>
internal sealed class CliUsageGuideSource : IUsageGuideSource
{
    public string GetMentalModel() => UsageGuideText.MentalModel;

    public IReadOnlyList<UsageGuideCommand> GetCommands()
    {
        var llmTools = GetLlmJsonApiTools();
        var result = new List<UsageGuideCommand>(llmTools.Count + Commands.All.Count);
        result.AddRange(llmTools);
        foreach (var cmd in Commands.All)
        {
            var parameters = new List<UsageGuideParameter>(cmd.Params.Count);
            foreach (var p in cmd.Params)
            {
                parameters.Add(new UsageGuideParameter(
                    Name: p.KebabName,
                    Type: ParamTypeToString(p.Type),
                    Required: p.Required,
                    Description: p.Description));
            }
            result.Add(new UsageGuideCommand(
                Name: cmd.KebabName,
                Kind: cmd.Kind == CommandKind.Write ? "write" : "read",
                Description: cmd.Description,
                Parameters: parameters,
                Examples: cmd.Examples ?? Array.Empty<string>()));
        }
        return result;
    }

    private static string ParamTypeToString(ParamType type) => type switch
    {
        ParamType.String    => "string",
        ParamType.Integer   => "integer",
        ParamType.IdList    => "id_list",
        ParamType.Json      => "json",
        ParamType.JsonArray => "json_array",
        ParamType.Boolean   => "boolean",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static IReadOnlyList<UsageGuideCommand> GetLlmJsonApiTools() =>
        new[]
        {
            LlmTool(
                "hit",
                "read",
                "LLM-facing JSON API: безопасная проверка selector-ов и будущих write-ops без записи. Принимает defaults и ops[].",
                "hit {\"ops\":[{\"op\":\"select\",\"select\":{\"path\":\"DocsWalker-LLM JSON API\"}}]}"),
            LlmTool(
                "query",
                "read",
                "LLM-facing JSON API: чтение данных по select- и grep-операциям. Принимает defaults и ops[].",
                "query {\"ops\":[{\"op\":\"grep\",\"pattern\":\"validation_failed\",\"limit\":20}]}"),
            LlmTool(
                "tx",
                "write",
                "LLM-facing JSON API: атомарное внесение изменений. Принимает defaults и ops[].",
                "tx {\"ops\":[{\"op\":\"update\",\"id\":42,\"set\":{\"text\":\"...\"}}]}"),
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
