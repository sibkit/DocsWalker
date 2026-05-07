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
/// сериализует результат напрямую в stdout (envelope-free, см. <see cref="Output"/>) и
/// переводит структурированные ошибки (<see cref="WriteApiException"/>,
/// <see cref="WriteValidationException"/>, <see cref="GraphLoadException"/>,
/// <see cref="SchemaLoadException"/>, <see cref="AtomicWriteException"/>,
/// <see cref="SequenceCounterException"/>) в плоский JSON-объект ошибки в stderr.
/// </summary>
internal static class WriteHandlers
{
    /// <summary>
    /// Фиксированные параметры <c>create-node</c>, известные на уровне CLI
    /// независимо от Схемы. Всё остальное в args (кроме общих <c>root</c> и
    /// <c>dry-run</c>) трактуется как имя out_ref-связи и парсится как ID-список.
    /// </summary>
    private static readonly HashSet<string> CreateNodeFixedKeys =
        new(StringComparer.Ordinal) { "type", "title", "text", "root", "dry-run" };

    public static int CreateNode(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
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
        return Run(root, op, dryRun);
    }

    public static int UpdateNode(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
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
        return Run(root, op, dryRun);
    }

    public static int DeleteNodes(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
    {
        if (!TryParseIdList(args["ids"], out var ids, out var parseError))
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                $"Параметр '--ids': {parseError}",
                "Ожидается список id через запятую, например, --ids=42,43,44.");
            return 1;
        }
        var op = new DeleteNodesOp(ids);
        return Run(root, op, dryRun);
    }

    public static int RedirectRefs(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
    {
        var hasFrom = args.TryGetValue("from", out var fromRaw);
        var hasFromSubtree = args.TryGetValue("from-subtree", out var fromSubtreeRaw);
        var hasTo = args.TryGetValue("to", out var toRaw);
        var hasUnlink = args.TryGetValue("unlink", out var unlinkRaw);
        var name = args.TryGetValue("name", out var nm) ? nm : null;

        if (hasFrom == hasFromSubtree)
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                "Для redirect-refs требуется ровно одно из '--from=<id>' или '--from-subtree=<root_id>'.",
                "--from=<id> переподшивает входящие cross-refs одного узла; --from-subtree=<root_id> — всего path-поддерева.");
            return 1;
        }

        bool unlink = false;
        if (hasUnlink)
        {
            unlink = string.Equals(unlinkRaw, "true", StringComparison.OrdinalIgnoreCase)
                  || unlinkRaw == "1";
            if (!unlink && !string.Equals(unlinkRaw, "false", StringComparison.OrdinalIgnoreCase) && unlinkRaw != "0")
            {
                Output.WriteError(
                    "invalid_parameter",
                    path: null,
                    $"Параметр '--unlink': ожидается 'true' или 'false', получено '{unlinkRaw}'.");
                return 1;
            }
        }

        if (unlink == hasTo)
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                "Для redirect-refs требуется ровно одно из '--to=<dst_id>' или '--unlink=true'.",
                "--to=<dst_id> переподшивает связи на новый узел; --unlink=true разрывает связи без замены.");
            return 1;
        }

        // Сборка набора FromIds: --from=<id> → [id]; --from-subtree=<root_id> → BFS вниз
        // по path-children, включая root_id (cascading set за счёт path-замкнутости).
        IReadOnlyList<int> fromIds;
        try
        {
            if (hasFrom)
            {
                fromIds = new[] { int.Parse(fromRaw!, CultureInfo.InvariantCulture) };
            }
            else
            {
                var rootId = int.Parse(fromSubtreeRaw!, CultureInfo.InvariantCulture);
                fromIds = ResolveSubtreeIds(root, rootId);
            }
        }
        catch (FormatException)
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                "Значение --from / --from-subtree должно быть целым числом.");
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

        int? toId = hasTo
            ? int.Parse(toRaw!, CultureInfo.InvariantCulture)
            : null;

        var op = new RedirectRefsOp(fromIds, toId, name, unlink);
        return Run(root, op, dryRun);
    }

    /// <summary>
    /// CLI-helper: разворачивает <c>--from-subtree=&lt;root_id&gt;</c> в полный набор id
    /// path-поддерева (включая сам root). Используется только в redirect-refs;
    /// нужен здесь, а не в WriteApi, потому что ядро принимает уже готовый плоский набор.
    /// </summary>
    private static IReadOnlyList<int> ResolveSubtreeIds(string root, int rootId)
    {
        var ctx = WriteContext.FromRoot(root);
        var schema = SchemaLoader.LoadSchema(ctx.SchemaPath);
        var loaded = DocumentLoader.Load(ctx.DocsRoot, schema);
        var graph = loaded.Graph;
        if (graph.GetById(rootId) is null && rootId != Node.RootId)
            throw new WriteApiException(
                "node_not_found",
                $"Узел id={rootId} (--from-subtree) не найден.",
                "Сверь id через get-map / get-nodes.");

        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id != Node.RootId) result.Add(id);
            foreach (var ch in graph.GetChildren(id))
                queue.Enqueue(ch.Id);
        }
        return result;
    }

    public static int MoveNode(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
    {
        var tree = args.TryGetValue("tree", out var tr) && !string.IsNullOrEmpty(tr)
            ? tr
            : Node.PathRefName;
        var op = new MoveNodeOp(
            Id: int.Parse(args["id"], CultureInfo.InvariantCulture),
            NewParentId: int.Parse(args["to"], CultureInfo.InvariantCulture),
            Tree: tree);
        return Run(root, op, dryRun);
    }

    public static int CreateRef(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
    {
        var op = new CreateRefOp(
            FromId: int.Parse(args["from-id"], CultureInfo.InvariantCulture),
            Name: args["name"],
            ToId: int.Parse(args["to-id"], CultureInfo.InvariantCulture));
        return Run(root, op, dryRun);
    }

    public static int DeleteRef(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
    {
        var op = new DeleteRefOp(
            FromId: int.Parse(args["from-id"], CultureInfo.InvariantCulture),
            Name: args["name"],
            ToId: int.Parse(args["to-id"], CultureInfo.InvariantCulture));
        return Run(root, op, dryRun);
    }

    public static int Transaction(string root, IReadOnlyDictionary<string, string> args, bool dryRun)
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

        return RunMany(root, ops, dryRun);
    }

    private static int Run(string root, WriteOp op, bool dryRun) =>
        RunCore(root, new[] { op }, transaction: false, dryRun);

    private static int RunMany(string root, IReadOnlyList<WriteOp> ops, bool dryRun) =>
        RunCore(root, ops, transaction: true, dryRun);

    private static int RunCore(string root, IReadOnlyList<WriteOp> ops, bool transaction, bool dryRun)
    {
        try
        {
            var ctx = WriteContext.FromRoot(root);
            var api = new WriteApi(ctx);
            var result = api.Apply(ops, dryRun);
            if (transaction)
            {
                // Для transaction результат — top-level массив; applied уже впечён в
                // каждый элемент TransactionResultToJson'ом. Single-arg WriteSuccess
                // печатает массив как есть.
                Output.WriteSuccess(TransactionResultToJson(result));
            }
            else
            {
                // Одиночная write-команда: result — JsonObject, two-arg overload
                // подмешивает applied как top-level-поле.
                Output.WriteSuccess(SingleResultToJson(result), applied: result.Applied);
            }
            return 0;
        }
        catch (WriteValidationException ex)
        {
            Output.WriteError(
                "validation_failed",
                path: null,
                FormatValidationMessage(ex.Errors),
                FormatValidationHint(ex.Errors),
                describeType: ErrorEnrichment.TryDescribeType(root, FirstCreateNodeType(ops)));
            return 1;
        }
        catch (WriteApiException ex)
        {
            Output.WriteError(
                ex.Code,
                path: null,
                ex.Message,
                ex.Hint,
                describeType: ErrorEnrichment.TryDescribeType(root, FirstCreateNodeType(ops)));
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

    /// <summary>
    /// Возвращает имя типа из первой <see cref="CreateNodeOp"/> в наборе операций, или null.
    /// Используется для embed'а <c>describe-type</c> в ошибки <see cref="WriteApiException"/> /
    /// <see cref="WriteValidationException"/>: контракт типа подсказывает LLM, какие
    /// именно out_refs она забыла или указала неверно. Для прочих write-операций
    /// (update/move/delete/redirect) тип целевого узла из op'а явно не выводится — там
    /// LLM получит ошибку без describe_type, что приемлемо: эти операции не требуют
    /// заполнения out_refs контракта.
    /// </summary>
    private static string? FirstCreateNodeType(IReadOnlyList<WriteOp> ops)
    {
        foreach (var op in ops)
        {
            if (op is CreateNodeOp create) return create.TypeName;
        }
        return null;
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
    /// Для команды `transaction` — массив объектов формы
    /// <c>{op: имя, ...поля результата операции, applied}</c> в порядке исходных операций
    /// (top-level массив без обёртки). Поле <c>op</c> отличает шейп от одиночной команды;
    /// <c>applied</c> повторяется в каждом элементе — значение одно (transaction атомарна),
    /// но envelope-free контракт требует, чтобы оно было видно на каждом результате.
    /// </summary>
    private static JsonNode TransactionResultToJson(WriteResult result)
    {
        var arr = new JsonArray();
        foreach (var op in result.OpResults)
        {
            var flat = new JsonObject { ["op"] = op.Type };
            foreach (var kv in op.Data)
                flat[kv.Key] = kv.Value?.DeepClone();
            flat["applied"] = JsonValue.Create(result.Applied);
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
