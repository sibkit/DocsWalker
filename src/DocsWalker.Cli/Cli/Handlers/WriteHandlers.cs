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
    public static int CreateNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new CreateNodeOp(
            ParentId: int.Parse(args["parent-id"], System.Globalization.CultureInfo.InvariantCulture),
            TypeName: args["type"],
            Title: args.TryGetValue("title", out var t) ? t : null,
            Name: args.TryGetValue("name", out var n) ? n : null,
            Body: ParseOptionalBody(args));
        return Run(root, op);
    }

    public static int UpdateNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var patchRaw = args["patch"];
        var patch = JsonNode.Parse(patchRaw) as JsonObject
            ?? throw new InvalidOperationException(
                "patch должен быть JSON-объектом (валидация формы — на ArgParser).");
        var op = new UpdateNodeOp(
            Id: int.Parse(args["id"], System.Globalization.CultureInfo.InvariantCulture),
            Patch: patch);
        return Run(root, op);
    }

    public static int DeleteNode(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new DeleteNodeOp(
            Id: int.Parse(args["id"], System.Globalization.CultureInfo.InvariantCulture));
        return Run(root, op);
    }

    public static int CreateRef(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new CreateRefOp(
            FromId: int.Parse(args["from-id"], System.Globalization.CultureInfo.InvariantCulture),
            RefType: args["type"],
            ToId: int.Parse(args["to-id"], System.Globalization.CultureInfo.InvariantCulture));
        return Run(root, op);
    }

    public static int DeleteRef(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new DeleteRefOp(
            FromId: int.Parse(args["from-id"], System.Globalization.CultureInfo.InvariantCulture),
            RefType: args["type"],
            ToId: int.Parse(args["to-id"], System.Globalization.CultureInfo.InvariantCulture));
        return Run(root, op);
    }

    public static int AddRefType(string root, IReadOnlyDictionary<string, string> args)
    {
        var op = new AddRefTypeOp(
            Name: args["name"],
            Direction: args["direction"],
            Description: args["description"]);
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
            Output.WriteError(ex.Code, path: null, ex.Message);
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
            Output.WriteError("validation_failed", path: null, FormatValidationMessage(ex.Errors));
            return 1;
        }
        catch (WriteApiException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
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

    private static JsonObject? ParseOptionalBody(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("body", out var body)) return null;
        var parsed = JsonNode.Parse(body);
        if (parsed is null) return null;
        if (parsed is JsonObject obj) return obj;
        throw new WriteApiException(
            "invalid_parameter",
            "body должен быть JSON-объектом.");
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
}
