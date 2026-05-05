using System.Text.Json.Nodes;

namespace DocsWalker.Core.Api;

/// <summary>
/// Парсер входной структуры transaction (плоский массив операций) в список
/// типизированных <see cref="WriteOp"/>. Используется CLI-обработчиком команды
/// <c>transaction</c> и тестами. Любая ошибка парсинга — <see cref="WriteApiException"/>
/// с указанием индекса операции.
///
/// Форма входа:
/// <code>
/// [
///   { "op": "create-node", "parent_id": 1, "type": "section", "title": "..." },
///   { "op": "update-node", "id": 42, "patch": { "title": "..." } },
///   { "op": "delete-node", "id": 42 },
///   { "op": "create-ref",  "from_id": 42, "type": "ref", "to_id": 8 },
///   { "op": "delete-ref",  "from_id": 42, "type": "ref", "to_id": 8 },
///   { "op": "add-ref-type","name": "defines", "direction": "from_to", "description": "..." }
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
                throw new WriteApiException(ex.Code, $"Операция #{i}: {ex.Message}");
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
            "create-node"   => ParseCreateNode(obj),
            "update-node"   => ParseUpdateNode(obj),
            "delete-node"   => ParseDeleteNode(obj),
            "create-ref"    => ParseCreateRef(obj),
            "delete-ref"    => ParseDeleteRef(obj),
            "add-ref-type"  => ParseAddRefType(obj),
            _ => throw new WriteApiException(
                    "unknown_op",
                    $"Неизвестное имя операции '{opName}'."),
        };
    }

    private static CreateNodeOp ParseCreateNode(JsonObject obj) =>
        new(
            ParentId: ReadRequiredInt(obj, "parent_id"),
            TypeName: ReadRequiredString(obj, "type"),
            Title: ReadOptionalString(obj, "title"),
            Name: ReadOptionalString(obj, "name"),
            Body: ReadOptionalObject(obj, "body"));

    private static UpdateNodeOp ParseUpdateNode(JsonObject obj)
    {
        var id = ReadRequiredInt(obj, "id");
        var patch = ReadOptionalObject(obj, "patch")
            ?? throw new WriteApiException(
                "missing_field",
                "Поле 'patch' обязательно для update-node.");
        return new UpdateNodeOp(id, patch);
    }

    private static DeleteNodeOp ParseDeleteNode(JsonObject obj) =>
        new(ReadRequiredInt(obj, "id"));

    private static CreateRefOp ParseCreateRef(JsonObject obj) =>
        new(
            FromId: ReadRequiredInt(obj, "from_id"),
            RefType: ReadRequiredString(obj, "type"),
            ToId: ReadRequiredInt(obj, "to_id"));

    private static DeleteRefOp ParseDeleteRef(JsonObject obj) =>
        new(
            FromId: ReadRequiredInt(obj, "from_id"),
            RefType: ReadRequiredString(obj, "type"),
            ToId: ReadRequiredInt(obj, "to_id"));

    private static AddRefTypeOp ParseAddRefType(JsonObject obj) =>
        new(
            Name: ReadRequiredString(obj, "name"),
            Direction: ReadRequiredString(obj, "direction"),
            Description: ReadRequiredString(obj, "description"));

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
}
