using System.Text.Json.Nodes;

namespace DocsWalker.Core.Api;

/// <summary>
/// Парсер входной структуры transaction (плоский массив операций) в список
/// типизированных <see cref="WriteOp"/>. Используется CLI-обработчиком команды
/// <c>transaction</c> и тестами. Любая ошибка парсинга — <see cref="WriteApiException"/>
/// с указанием индекса операции.
///
/// Форма входа (refs-модель):
/// <code>
/// [
///   {
///     "op": "create-node",
///     "type": "section",
///     "title": "...",
///     "text": "...",
///     "refs": { "path": [1] }
///   },
///   { "op": "update-node",    "id": 42, "title": "...", "text": "..." },
///   { "op": "delete-nodes",   "ids": [42, 43, 44] },
///   { "op": "move-node",      "id": 42, "new_parent_id": 8, "tree": "path" },
///   { "op": "create-ref",     "from_id": 42, "name": "related", "to_id": 8 },
///   { "op": "delete-ref",     "from_id": 42, "name": "related", "to_id": 8 },
///   { "op": "redirect-refs",  "from_ids": [42], "to_id": 100, "name": "related" },
///   { "op": "redirect-refs",  "from_ids": [42], "unlink": true }
/// ]
/// </code>
/// </summary>
public static class TransactionParser
{
    public static IReadOnlyList<WriteOp> Parse(JsonNode? input)
    {
        if (input is null)
            throw new WriteApiException(
                "invalid_transaction_input",
                "Входное значение transaction = null; ожидается JSON-массив операций.");
        if (input is not JsonArray arr)
            throw new WriteApiException(
                "invalid_transaction_input",
                "Входное значение transaction должно быть JSON-массивом операций.");

        var ops = new List<WriteOp>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            try
            {
                ops.Add(ParseOp(arr[i]));
            }
            catch (WriteApiException ex)
            {
                throw new WriteApiException(ex.Code, $"Операция #{i}: {ex.Message}", ex.Hint);
            }
        }
        return ops;
    }

    private static WriteOp ParseOp(JsonNode? node)
    {
        if (node is not JsonObject obj)
            throw new WriteApiException(
                "invalid_op",
                "Элемент массива операций должен быть JSON-объектом.");
        var opName = ReadRequiredString(obj, "op");
        return opName switch
        {
            "create-node"    => ParseCreateNode(obj),
            "update-node"    => ParseUpdateNode(obj),
            "delete-nodes"   => ParseDeleteNodes(obj),
            "move-node"      => ParseMoveNode(obj),
            "create-ref"     => ParseCreateRef(obj),
            "delete-ref"     => ParseDeleteRef(obj),
            "redirect-refs"  => ParseRedirectRefs(obj),
            _ => throw new WriteApiException(
                    "unknown_op",
                    $"Неизвестное имя операции '{opName}'."),
        };
    }

    private static CreateNodeOp ParseCreateNode(JsonObject obj)
    {
        var typeName = ReadRequiredString(obj, "type");
        var title = ReadRequiredString(obj, "title");
        var text = ReadOptionalString(obj, "text");
        var refsObj = ReadOptionalObject(obj, "refs");
        var refs = ParseRefsMap(refsObj);
        return new CreateNodeOp(typeName, title, text, refs);
    }

    /// <summary>
    /// Разбирает объект <c>refs</c>: ключ — имя связи, значение — массив целочисленных id.
    /// Возвращает пустую карту, если refs не задан.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ParseRefsMap(JsonObject? refsObj)
    {
        if (refsObj is null)
            return new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var prop in refsObj)
        {
            if (prop.Value is not JsonArray arr)
                throw new WriteApiException(
                    "invalid_field_type",
                    $"Поле refs.{prop.Key} должно быть массивом целочисленных id.");
            var list = new List<int>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue jv)
                {
                    if (jv.TryGetValue<int>(out var iv)) { list.Add(iv); continue; }
                    if (jv.TryGetValue<long>(out var lv)) { list.Add(checked((int)lv)); continue; }
                }
                throw new WriteApiException(
                    "invalid_field_type",
                    $"Поле refs.{prop.Key}[{i}] должно быть целым числом.");
            }
            result[prop.Key] = list;
        }
        return result;
    }

    private static UpdateNodeOp ParseUpdateNode(JsonObject obj) =>
        new(
            Id: ReadRequiredInt(obj, "id"),
            NewTitle: ReadOptionalString(obj, "title"),
            NewText: ReadOptionalString(obj, "text"));

    private static DeleteNodesOp ParseDeleteNodes(JsonObject obj) =>
        new(ReadRequiredIntArray(obj, "ids"));

    private static RedirectRefsOp ParseRedirectRefs(JsonObject obj)
    {
        var fromIds = ReadRequiredIntArray(obj, "from_ids");
        int? toId = ReadOptionalInt(obj, "to_id");
        var name = ReadOptionalString(obj, "name");
        bool unlink = ReadOptionalBool(obj, "unlink") ?? false;
        return new RedirectRefsOp(fromIds, toId, name, unlink);
    }

    private static MoveNodeOp ParseMoveNode(JsonObject obj) =>
        new(
            Id: ReadRequiredInt(obj, "id"),
            NewParentId: ReadRequiredInt(obj, "new_parent_id"),
            Tree: ReadOptionalString(obj, "tree") ?? DocsWalker.Core.Graph.Node.PathRefName);

    private static CreateRefOp ParseCreateRef(JsonObject obj) =>
        new(
            FromId: ReadRequiredInt(obj, "from_id"),
            Name: ReadRequiredString(obj, "name"),
            ToId: ReadRequiredInt(obj, "to_id"));

    private static DeleteRefOp ParseDeleteRef(JsonObject obj) =>
        new(
            FromId: ReadRequiredInt(obj, "from_id"),
            Name: ReadRequiredString(obj, "name"),
            ToId: ReadRequiredInt(obj, "to_id"));

    private static string ReadRequiredString(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            throw new WriteApiException(
                "missing_field",
                $"Поле '{field}' обязательно.");
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        throw new WriteApiException(
            "invalid_field_type",
            $"Поле '{field}' должно быть строкой.");
    }

    private static string? ReadOptionalString(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null) return null;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        throw new WriteApiException(
            "invalid_field_type",
            $"Поле '{field}' должно быть строкой.");
    }

    private static int ReadRequiredInt(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            throw new WriteApiException(
                "missing_field",
                $"Поле '{field}' обязательно.");
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<long>(out var l)) return checked((int)l);
        }
        throw new WriteApiException(
            "invalid_field_type",
            $"Поле '{field}' должно быть целым числом.");
    }

    private static JsonObject? ReadOptionalObject(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null) return null;
        if (node is JsonObject o) return o;
        throw new WriteApiException(
            "invalid_field_type",
            $"Поле '{field}' должно быть объектом.");
    }

    private static int? ReadOptionalInt(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<long>(out var l)) return checked((int)l);
        }
        throw new WriteApiException(
            "invalid_field_type",
            $"Поле '{field}' должно быть целым числом.");
    }

    private static bool? ReadOptionalBool(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null) return null;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        throw new WriteApiException(
            "invalid_field_type",
            $"Поле '{field}' должно быть булевым (true/false).");
    }

    private static IReadOnlyList<int> ReadRequiredIntArray(JsonObject obj, string field)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            throw new WriteApiException(
                "missing_field",
                $"Поле '{field}' обязательно.");
        if (node is not JsonArray arr)
            throw new WriteApiException(
                "invalid_field_type",
                $"Поле '{field}' должно быть массивом целых чисел.");
        var list = new List<int>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue jv)
            {
                if (jv.TryGetValue<int>(out var iv)) { list.Add(iv); continue; }
                if (jv.TryGetValue<long>(out var lv)) { list.Add(checked((int)lv)); continue; }
            }
            throw new WriteApiException(
                "invalid_field_type",
                $"Поле '{field}'[{i}] должно быть целым числом.");
        }
        return list;
    }
}
