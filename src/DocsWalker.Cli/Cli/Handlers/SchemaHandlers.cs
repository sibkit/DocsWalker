using DocsWalker.Cli.UsageGuide;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Server;

namespace DocsWalker.Cli.Cli.Handlers;

internal static class SchemaHandlers
{
    public static int GetMetaSchema(string root)
    {
        var path = Path.Combine(root, "docs", ".docswalker", "meta-schema.yml");
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

    public static int GetSchema(string root)
    {
        var path = Path.Combine(root, "docs", "Схема.yml");
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
    public static int DescribeType(string root, string name)
    {
        var path = Path.Combine(root, "docs", "Схема.yml");
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
    public static int GetUsageGuide(string root, string? commandFilter = null)
    {
        var docsRoot = Path.Combine(root, "docs");
        var schemaPath = Path.Combine(docsRoot, "Схема.yml");

        SchemaDocument schema;
        try { schema = SchemaLoader.LoadSchema(schemaPath); }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        DocumentLoadResult loaded;
        try { loaded = DocumentLoader.Load(docsRoot, schema); }
        catch (GraphLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        // (#348) Reset на guide. Любой вызов get-usage-guide в session_id —
        // маркер начала логической сессии: чистим seen-set, дальше LLM получает
        // узлы в полной форме. Идемпотентно: повторный guide-вызов без чтений
        // между ними просто заново обнуляет уже пустой set.
        var ctx = RequestContext.Current;
        if (ctx?.Sessions is { } sessions
            && !string.IsNullOrEmpty(ctx.SessionId)
            && Guid.TryParse(ctx.SessionId, out var sid))
        {
            sessions.ResetSeen(sid, DateTime.UtcNow);
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
            dto = new UsageGuideResponse(dto.MentalModel, dto.Trees, filtered, dto.Snapshot, dto.TransactionOperations);
        }

        Output.WriteSuccess(ReadApiJson.UsageGuideToJson(dto));
        return 0;
    }
}
