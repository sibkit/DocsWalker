using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
using DocsWalker.Core.Validation;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Обработчики write-команд CLI: каждая дёргает <see cref="WriteApi"/> с одной операцией,
/// сериализует результат в общий success-конверт и переводит структурированные ошибки
/// (<see cref="WriteApiException"/>, <see cref="WriteValidationException"/>,
/// <see cref="GraphLoadException"/>, <see cref="SchemaLoadException"/>,
/// <see cref="AtomicWriteException"/>, <see cref="SequenceCounterException"/>) в JSON
/// формы <see cref="ErrorEnvelope"/>.
/// </summary>
internal static class WriteHandlers
{
    /// <summary>
    /// Фиксированные параметры <c>create-node</c>, известные на уровне CLI
    /// независимо от Схемы. Всё остальное в args (кроме общего <c>root</c>)
    /// трактуется как имя out_ref-связи и парсится как ID-список.
    /// </summary>
    private static readonly HashSet<string> CreateNodeFixedKeys =
        new(StringComparer.Ordinal) { "type", "title", "text", "root" };

    public static int CreateNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var typeName = args["type"];
        var title = args["title"];
        var text = args.TryGetValue("text", out var t) ? t : null;

        var refs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            if (CreateNodeFixedKeys.Contains(key))
                continue;

            if (!TryParseIdList(value, out var ids, out var parseError))
            {
                Output.WriteError(
                    "invalid_parameter",
                    path: null,
                    $"Параметр '--{key}': {parseError}",
                    "Ожидается id или список id через запятую (например, --path=1 или --rel=2,3).");
                return 1;
            }
            refs[key] = ids;
        }

        var op = new CreateNodeOp(
            TypeName: typeName,
            Title: title,
            Text: text,
            Refs: refs);
        return Run(root, op);
    }

    public static int UpdateNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var newTitle = args.TryGetValue("title", out var t) ? t : null;
        var newText  = args.TryGetValue("text",  out var x) ? x : null;
        if (newTitle is null && newText is null)
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                "Команда 'update-node' требует хотя бы один из параметров '--title' или '--text'.");
            return 1;
        }

        var op = new UpdateNodeOp(
            Id: int.Parse(args["id"], CultureInfo.InvariantCulture),
            NewTitle: newTitle,
            NewText: newText);
        return Run(root, op);
    }

    public static int DeleteNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new DeleteNodeOp(
            Id: int.Parse(args["id"], CultureInfo.InvariantCulture));
        return Run(root, op);
    }

    public static int MoveNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var tree = args.TryGetValue("tree", out var tr) && !string.IsNullOrEmpty(tr)
            ? tr
            : Node.PathRefName;
        var op = new MoveNodeOp(
            Id: int.Parse(args["id"], CultureInfo.InvariantCulture),
            NewParentId: int.Parse(args["to"], CultureInfo.InvariantCulture),
            Tree: tree);
        return Run(root, op);
    }

    public static int CreateRef(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new CreateRefOp(
            FromId: int.Parse(args["from-id"], CultureInfo.InvariantCulture),
            Name: args["name"],
            ToId: int.Parse(args["to-id"], CultureInfo.InvariantCulture));
        return Run(root, op);
    }

    public static int DeleteRef(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new DeleteRefOp(
            FromId: int.Parse(args["from-id"], CultureInfo.InvariantCulture),
            Name: args["name"],
            ToId: int.Parse(args["to-id"], CultureInfo.InvariantCulture));
        return Run(root, op);
    }

    public static int Transaction(string root, IReadOnlyDictionary<string, string> args)
    {
        IReadOnlyList<WriteOp> ops;
        try
        {
            var node = JsonNode.Parse(args["operations"]);
            ops = TransactionParser.Parse(node);
        }
        catch (WriteApiException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
            return 1;
        }
        catch (JsonException ex)
        {
            Output.WriteError("invalid_parameter", path: null, $"operations: {ex.Message}");
            return 1;
        }

        return RunMany(root, ops);
    }

    private static int Run(string root, WriteOp op) => RunCore(root, new[] { op }, transaction: false);

    private static int RunMany(string root, IReadOnlyList<WriteOp> ops) =>
        RunCore(root, ops, transaction: true);

    private static int RunCore(string root, IReadOnlyList<WriteOp> ops, bool transaction)
    {
        try
        {
            var ctx = WriteContext.FromRoot(root);
            var api = new WriteApi(ctx);
            var result = api.Apply(ops);
            Output.WriteSuccess(transaction
                ? TransactionResultToJson(result)
                : SingleResultToJson(result));
            return 0;
        }
        catch (WriteValidationException ex)
        {
            Output.WriteError("validation_failed", path: null, FormatValidationMessage(ex.Errors), FormatValidationHint(ex.Errors));
            return 1;
        }
        catch (WriteApiException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
            return 1;
        }
        catch (GraphLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
        catch (AtomicWriteException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
        catch (SequenceCounterException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
    }

    private static bool TryParseIdList(string raw, out IReadOnlyList<int> ids, out string? error)
    {
        if (string.IsNullOrEmpty(raw))
        {
            ids = Array.Empty<int>();
            error = "ожидается непустое значение (id или список id через запятую).";
            return false;
        }

        var parts = raw.Split(',');
        var list = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            var s = p.Trim();
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                ids = Array.Empty<int>();
                error = $"ожидается целое число или список целых через запятую, получено '{raw}'.";
                return false;
            }
            list.Add(id);
        }

        ids = list;
        error = null;
        return true;
    }

    /// <summary>
    /// Для одиночных write-команд (`create-node`, `update-node` и т. д.) поле `result`
    /// содержит данные единственной операции напрямую — без обёрток `operations[0].data`.
    /// </summary>
    private static JsonNode SingleResultToJson(WriteResult result)
    {
        if (result.OpResults.Count != 1)
            throw new InvalidOperationException(
                $"Single-result handler ожидает ровно 1 операцию, получено {result.OpResults.Count}.");
        return result.OpResults[0].Data.DeepClone();
    }

    /// <summary>
    /// Для команды `transaction` — массив объектов формы `{op: имя, ...поля результата}`
    /// в порядке исходных операций. Поле `op` отличает шейп от одиночной команды и
    /// позволяет LLM сопоставить элемент массива с входной операцией.
    /// </summary>
    private static JsonNode TransactionResultToJson(WriteResult result)
    {
        var arr = new JsonArray();
        foreach (var op in result.OpResults)
        {
            var flat = new JsonObject { ["op"] = op.Type };
            foreach (var kv in op.Data)
                flat[kv.Key] = kv.Value?.DeepClone();
            arr.Add((JsonNode?)flat);
        }
        return arr;
    }

    private static string FormatValidationMessage(IReadOnlyList<ValidationError> errors)
    {
        var lines = errors.Select(e =>
        {
            var loc = e.NodeId is int id ? $" id={id}" : string.Empty;
            var file = e.FilePath is not null ? $" {e.FilePath}" : string.Empty;
            return $"[{e.Code}]{loc}{file}: {e.Message}";
        });
        return "Запись отклонена валидатором:\n" + string.Join('\n', lines);
    }

    /// <summary>
    /// Сводный hint для validation_failed: первая ненулевая подсказка из списка ошибок.
    /// Для нескольких ошибок более подробно — каждая ValidationError несёт свой Hint и
    /// доступна через машинное API; CLI ограничивается короткой пометкой, чтобы LLM не
    /// читала простыню.
    /// </summary>
    private static string? FormatValidationHint(IReadOnlyList<ValidationError> errors)
    {
        foreach (var e in errors)
            if (!string.IsNullOrEmpty(e.Hint)) return e.Hint;
        return null;
    }
}
