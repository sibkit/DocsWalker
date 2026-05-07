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
        var result = new List<UsageGuideCommand>(Commands.All.Count);
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
        ParamType.String  => "string",
        ParamType.Integer => "integer",
        ParamType.IdList  => "id_list",
        ParamType.Json    => "json",
        _ => type.ToString().ToLowerInvariant(),
    };
}
