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

    /// <summary>
    /// Жёстко прошитый список из 7 операций <c>transaction</c>. Имена JSON-ключей
    /// и required/optional — точное зеркало <see cref="DocsWalker.Core.Api.TransactionParser"/>.
    /// Если парсер меняется (новая op, новое поле) — этот список обновляется
    /// синхронно. Альтернатива — выводить через рефлексию по WriteOp-типам — пока
    /// не делаем; явный list проще для LLM и читателей.
    /// </summary>
    public IReadOnlyList<UsageGuideTransactionOp> GetTransactionOperations() =>
        new[]
        {
            new UsageGuideTransactionOp(
                Op: "create-node",
                CliCommand: "create-node",
                Description: "Создать узел: type+title+text плюс объект refs (имя связи → массив id целей).",
                Fields: new[]
                {
                    new UsageGuideTransactionField("type",  "string", true,  "--type",  "Имя типа из Схемы."),
                    new UsageGuideTransactionField("title", "string", true,  "--title", "Path-сегмент (1–2 слова)."),
                    new UsageGuideTransactionField("text",  "string", false, "--text",  "Текст узла; обязателен для типов с text_required=true."),
                    new UsageGuideTransactionField("refs",  "object{<ref-name>: integer[]}", false, "--<ref-name>=<csv-ids>",
                        "Объект {имя_связи: [target_ids]}. CLI разбивает на отдельные --<имя-связи>= флаги; в transaction — единый объект с массивами."),
                }),
            new UsageGuideTransactionOp(
                Op: "update-node",
                CliCommand: "update-node",
                Description: "Сменить title и/или text узла. Связи этой операцией не меняются.",
                Fields: new[]
                {
                    new UsageGuideTransactionField("id",    "integer", true,  "--id",    "id обновляемого узла."),
                    new UsageGuideTransactionField("title", "string",  false, "--title", "Новый title."),
                    new UsageGuideTransactionField("text",  "string",  false, "--text",  "Новый text."),
                }),
            new UsageGuideTransactionOp(
                Op: "delete-nodes",
                CliCommand: "delete-nodes",
                Description: "Удалить узлы явным списком id (без авто-каскада).",
                Fields: new[]
                {
                    new UsageGuideTransactionField("ids", "integer[]", true, "--ids=<csv>",
                        "Массив id; в CLI — CSV-строка, в transaction — JSON-массив целых."),
                }),
            new UsageGuideTransactionOp(
                Op: "move-node",
                CliCommand: "move-node",
                Description: "Переподшить узел в указанном tree-scope. Внимание: JSON-ключ — new_parent_id, CLI-флаг — --to.",
                Fields: new[]
                {
                    new UsageGuideTransactionField("id",            "integer", true,  "--id",   "id перемещаемого узла."),
                    new UsageGuideTransactionField("new_parent_id", "integer", true,  "--to",   "id нового parent в указанном дереве. CLI-флаг --to= маппится в JSON-ключ new_parent_id."),
                    new UsageGuideTransactionField("tree",          "string",  false, "--tree", "Имя дерева. По умолчанию 'path'."),
                }),
            new UsageGuideTransactionOp(
                Op: "create-ref",
                CliCommand: "create-ref",
                Description: "Создать одну исходящую связь from→to с именем name.",
                Fields: new[]
                {
                    new UsageGuideTransactionField("from_id", "integer", true, "--from-id", "id узла-источника."),
                    new UsageGuideTransactionField("name",    "string",  true, "--name",    "Имя связи (должно быть объявлено в out_refs типа источника)."),
                    new UsageGuideTransactionField("to_id",   "integer", true, "--to-id",   "id узла-цели."),
                }),
            new UsageGuideTransactionOp(
                Op: "delete-ref",
                CliCommand: "delete-ref",
                Description: "Удалить одну исходящую связь from→to с именем name.",
                Fields: new[]
                {
                    new UsageGuideTransactionField("from_id", "integer", true, "--from-id", "id узла-источника."),
                    new UsageGuideTransactionField("name",    "string",  true, "--name",    "Имя связи."),
                    new UsageGuideTransactionField("to_id",   "integer", true, "--to-id",   "id узла-цели."),
                }),
            new UsageGuideTransactionOp(
                Op: "redirect-refs",
                CliCommand: "redirect-refs",
                Description: "Массовая переподшивка входящих cross-refs: один или несколько источников → одна цель (или unlink). В CLI — --from=<id> либо --from-subtree=<root_id> (взаимоисключающие); в transaction — единый массив from_ids.",
                Fields: new[]
                {
                    new UsageGuideTransactionField("from_ids", "integer[]", true,  "--from / --from-subtree",
                        "Массив id-источников. CLI принимает --from=<id> либо --from-subtree=<root_id>; transaction всегда требует массив from_ids."),
                    new UsageGuideTransactionField("to_id",    "integer",   false, "--to",
                        "id узла-приёмника. Взаимоисключающее с unlink=true."),
                    new UsageGuideTransactionField("unlink",   "boolean",   false, "--unlink=true",
                        "Разрыв связей вместо переноса. Взаимоисключающее с to_id."),
                    new UsageGuideTransactionField("name",     "string",    false, "--name",
                        "Опциональный фильтр по имени связи; без него — все имена кроме системного 'path'."),
                }),
        };
}
