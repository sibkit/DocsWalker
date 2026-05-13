using DocsWalker.Cli.UsageGuide;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using System.Text.Json.Nodes;

namespace DocsWalker.Cli.Cli.Handlers;

internal static class SchemaHandlers
{
    private static readonly string[] UsageGuideFieldNames =
    [
        "mental_model",
        "trees",
        "commands",
        "graph_snapshot"
    ];

    public static int GetMetaSchema(string storagePath)
    {
        var path = Path.Combine(storagePath, ".docswalker", "meta-schema.yml");
        try
        {
            var ms = SchemaLoader.LoadMetaSchema(path);
            Output.WriteSuccess(SchemaJson.ToJson(ms));
            return 0;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
    }

    public static int GetSchema(string storagePath)
    {
        var path = Path.Combine(storagePath, "Схема.yml");
        try
        {
            var schema = SchemaLoader.LoadSchema(path);
            Output.WriteSuccess(SchemaJson.ToJson(schema));
            return 0;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Узкая read-команда: описание одного типа без загрузки графа документации.
    /// Граф для <c>describe-type</c> не нужен — операция чисто схемная; чтобы не
    /// платить токенами за <c>get-schema</c>, экономим LLM.
    /// </summary>
    public static int DescribeType(string storagePath, string name)
    {
        var path = Path.Combine(storagePath, "Схема.yml");
        try
        {
            var schema = SchemaLoader.LoadSchema(path);
            var dto = ReadApi.DescribeType(schema, name);
            Output.WriteSuccess(ReadApiJson.TypeDescriptionToJson(dto));
            return 0;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
        catch (ReadApiException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
            return 1;
        }
    }

    /// <summary>
    /// Manifest для LLM-агента: ментальная модель + tree-scopes + список команд +
    /// слепок графа. Загружает Схему и граф (для snapshot), берёт mental_model и
    /// commands из <see cref="CliUsageGuideSource"/>.
    /// <para>
    /// <paramref name="commandFilter"/> (опц., kebab-имя команды) — отдаёт описание
    /// одной команды в массиве <c>commands</c>. Остальные поля guide (mental_model,
    /// trees, snapshot) остаются. Невалидное имя — exit 1 с <c>unknown_command</c>.
    /// </para>
    /// </summary>
    public static int GetUsageGuide(string storagePath, string? commandFilter = null, string? fieldsFilter = null)
    {
        var schemaPath = Path.Combine(storagePath, "Схема.yml");

        SchemaDocument schema;
        try { schema = SchemaLoader.LoadSchema(schemaPath); }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        DocumentLoadResult loaded;
        try { loaded = DocumentLoader.Load(storagePath, schema); }
        catch (GraphLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        var dto = ReadApi.GetUsageGuide(new CliUsageGuideSource(), schema, loaded.Graph);

        if (!string.IsNullOrEmpty(commandFilter))
        {
            var filtered = dto.Commands.Where(c => string.Equals(c.Name, commandFilter, StringComparison.Ordinal)).ToList();
            if (filtered.Count == 0)
            {
                Output.WriteError(
                    "unknown_command",
                    path: null,
                    $"Команда '{commandFilter}' не найдена в манифесте get-usage-guide.",
                    "Сверь kebab-имя через get-usage-guide без --command=.");
                return 1;
            }
            dto = new UsageGuideResponse(dto.MentalModel, dto.Trees, filtered, dto.Snapshot);
        }

        var json = ReadApiJson.UsageGuideToJson(dto);
        if (!ApplyUsageGuideFields(json, fieldsFilter))
            return 1;

        Output.WriteSuccess(json);
        return 0;
    }

    private static bool ApplyUsageGuideFields(JsonObject json, string? fieldsFilter)
    {
        if (string.IsNullOrWhiteSpace(fieldsFilter))
            return true;

        var requested = fieldsFilter
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        if (requested.Count == 0)
            return true;

        var allowed = UsageGuideFieldNames.ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(field => !allowed.Contains(field)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                $"Неизвестные секции get-usage-guide fields: {string.Join(", ", unknown)}.",
                $"Допустимые секции: {string.Join(", ", UsageGuideFieldNames)}.");
            return false;
        }

        foreach (var field in UsageGuideFieldNames)
        {
            if (!requested.Contains(field))
                json.Remove(field);
        }

        return true;
    }
}
