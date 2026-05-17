using System.Text.Json.Nodes;

namespace DocsWalker.Core.Api;

/// <summary>
/// Ошибка разбора LLM-facing JSON API. Это синтаксический/модельный слой;
/// mapping в публичный envelope делается отдельным шагом реализации.
/// </summary>
public sealed class LlmJsonApiParseException : Exception
{
    public string Code { get; }
    public string Path { get; }

    public LlmJsonApiParseException(string code, string path, string message) : base(message)
    {
        Code = code;
        Path = path;
    }
}

public enum LlmJsonApiMethod
{
    Query,
    Tx,
    Scheme,
}

public enum LlmOperationKind
{
    Select,
    Create,
    Update,
    Delete,
    Move,
    Link,
    Unlink,
    SchemeGet,
    SchemeDescribeType,
    SchemeDescribeTree,
}

public enum LlmRelationPatchMode
{
    Add,
    Remove,
    Replace,
}

public sealed record LlmRequest(
    LlmJsonApiMethod Method,
    LlmRequestDefaults Defaults,
    IReadOnlyList<LlmOperation> Ops);

public sealed record LlmRequestDefaults(string? PathParent, LlmCoordinates Coordinates)
{
    public static LlmRequestDefaults Empty { get; } =
        new(null, LlmCoordinates.Empty);
}

public sealed record LlmCoordinates(IReadOnlyDictionary<string, string> Values)
{
    public static LlmCoordinates Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    public string? Type =>
        Values.TryGetValue("type", out var typeName) ? typeName : null;

    public string? Get(string name) =>
        Values.TryGetValue(name, out var value) ? value : null;
}

public sealed record LlmSelector(
    string? Path,
    LlmCoordinates Coordinates,
    LlmSelectorMatch? Match,
    JsonNode? Expect);

public sealed record LlmSelectorMatch(
    string Regex,
    IReadOnlyList<string> Fields,
    bool CaseSensitive);

public sealed record LlmTarget(
    int? Id,
    IReadOnlyList<int> Ids,
    string? Path,
    string? Alias,
    LlmSelector? Select)
{
    public static LlmTarget Empty { get; } =
        new(null, Array.Empty<int>(), null, null, null);

    public bool HasAny =>
        Id.HasValue ||
        Ids.Count > 0 ||
        Path is not null ||
        Alias is not null ||
        Select is not null;
}

public sealed record LlmNodeSet(
    string? Text,
    LlmCoordinates Coordinates,
    IReadOnlyDictionary<string, LlmRelationChange> Relations)
{
    public static LlmNodeSet Empty { get; } =
        new(null, LlmCoordinates.Empty, new Dictionary<string, LlmRelationChange>(StringComparer.Ordinal));
}

public sealed record LlmRelationChange(
    LlmRelationPatchMode? Mode,
    IReadOnlyList<LlmTarget> Targets);

public abstract record LlmOperation(
    LlmOperationKind Kind,
    string Op,
    string? Alias);

public sealed record LlmSelectOperation(
    LlmSelector Select,
    IReadOnlyList<string> Include,
    int? MaxTokens,
    string? SelectAlias = null)
    : LlmOperation(LlmOperationKind.Select, "select", SelectAlias);

public sealed record LlmCreateOperation(
    string? Path,
    LlmNodeSet Set,
    string? CreateAlias = null)
    : LlmOperation(LlmOperationKind.Create, "create", CreateAlias);

public sealed record LlmUpdateOperation(
    LlmTarget Target,
    int? ExpectedCount,
    LlmNodeSet Set)
    : LlmOperation(LlmOperationKind.Update, "update", null);

public sealed record LlmDeleteOperation(
    LlmTarget Target,
    int? ExpectedCount)
    : LlmOperation(LlmOperationKind.Delete, "delete", null);

public sealed record LlmMoveOperation(
    LlmTarget Source,
    string To)
    : LlmOperation(LlmOperationKind.Move, "move", null);

public sealed record LlmLinkOperation(
    LlmTarget From,
    string Name,
    LlmTarget To)
    : LlmOperation(LlmOperationKind.Link, "link", null);

public sealed record LlmUnlinkOperation(
    LlmTarget From,
    string Name,
    LlmTarget To)
    : LlmOperation(LlmOperationKind.Unlink, "unlink", null);

public sealed record LlmSchemeGetOperation(
    IReadOnlyList<string> Include,
    IReadOnlyList<string> TypeNames,
    IReadOnlyList<string> TreeNames)
    : LlmOperation(LlmOperationKind.SchemeGet, "get", null);

public sealed record LlmSchemeDescribeTypeOperation(string Name)
    : LlmOperation(LlmOperationKind.SchemeDescribeType, "describe_type", null);

public sealed record LlmSchemeDescribeTreeOperation(string Name)
    : LlmOperation(LlmOperationKind.SchemeDescribeTree, "describe_tree", null);

public abstract record LlmResponseEnvelope;

public sealed record LlmSuccessEnvelope(JsonNode Result)
    : LlmResponseEnvelope;

public sealed record LlmErrorEnvelope(
    string Code,
    JsonObject? Details)
    : LlmResponseEnvelope;

public sealed record LlmOperationResult(
    int Index,
    string Op,
    string? Alias,
    JsonObject Data);

public sealed record LlmCompactNode(
    int Id,
    string Path,
    LlmCoordinates Coordinates,
    string Title,
    int Tokens,
    int? SubtreeTokens);

public sealed record LlmTokenSummary(int Tokens, int SubtreeTokens);

public sealed record LlmSelectResult(
    int Count,
    LlmTokenSummary TokenSummary,
    IReadOnlyDictionary<string, int> BreakdownByType,
    IReadOnlyList<LlmCompactNode> Samples,
    bool WithinBudget);

/// <summary>
/// Парсер request envelope в AST. Он проверяет JSON-форму и wire-имена, но не
/// резолвит path/coordinates/alias и не проверяет cardinality контрактов.
/// </summary>
public static class LlmJsonApiParser
{
    public static LlmRequest Parse(JsonNode? input)
    {
        if (input is not JsonObject obj)
            throw Error("invalid_request", "$", "Запрос LLM JSON API должен быть JSON-объектом.");

        var method = ParseMethod(ReadRequiredString(obj, "method", "$.method"), "$.method");
        var defaults = ParseDefaults(ReadOptionalObject(obj, "defaults", "$.defaults"));
        var opsNode = ReadRequiredNode(obj, "ops", "$.ops");
        if (opsNode is not JsonArray opsArray)
            throw Error("invalid_request", "$.ops", "Поле ops должно быть JSON-массивом.");

        var ops = new List<LlmOperation>(opsArray.Count);
        for (int i = 0; i < opsArray.Count; i++)
            ops.Add(ParseOperation(opsArray[i], $"$.ops[{i}]"));

        return new LlmRequest(method, defaults, ops);
    }

    private static LlmRequestDefaults ParseDefaults(JsonObject? obj)
    {
        if (obj is null)
            return LlmRequestDefaults.Empty;

        var pathParent = ReadOptionalString(obj, "path_parent", "$.defaults.path_parent");
        var coordinates = ParseCoordinates(
            ReadOptionalObject(obj, "coordinates", "$.defaults.coordinates"),
            "$.defaults.coordinates");
        return new LlmRequestDefaults(pathParent, coordinates);
    }

    private static LlmOperation ParseOperation(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw Error("invalid_op", path, "Элемент ops[] должен быть JSON-объектом.");

        var op = ReadRequiredString(obj, "op", $"{path}.op");
        return op switch
        {
            "select" => ParseSelectOperation(obj, path),
            "create" => ParseCreateOperation(obj, path),
            "update" => ParseUpdateOperation(obj, path),
            "delete" => ParseDeleteOperation(obj, path),
            "move" => ParseMoveOperation(obj, path),
            "link" => ParseLinkOperation(obj, path),
            "unlink" => ParseUnlinkOperation(obj, path),
            "get" => ParseSchemeGetOperation(obj, path),
            "describe_type" => ParseSchemeDescribeTypeOperation(obj, path),
            "describe_tree" => ParseSchemeDescribeTreeOperation(obj, path),
            _ => throw Error("unknown_op", $"{path}.op", $"Неизвестное имя операции '{op}'."),
        };
    }

    private static LlmSelectOperation ParseSelectOperation(JsonObject obj, string path)
    {
        var select = ParseSelector(ReadRequiredObject(obj, "select", $"{path}.select"), $"{path}.select");
        var include = ReadOptionalStringArray(obj, "include", $"{path}.include");
        var maxTokens = ReadOptionalInt(obj, "max_tokens", $"{path}.max_tokens");
        var alias = ReadOptionalString(obj, "as", $"{path}.as");
        return new LlmSelectOperation(select, include, maxTokens, alias);
    }

    private static LlmCreateOperation ParseCreateOperation(JsonObject obj, string path)
    {
        var opPath = ReadOptionalString(obj, "path", $"{path}.path");
        var set = ParseSet(ReadRequiredObject(obj, "set", $"{path}.set"), $"{path}.set");
        var alias = ReadOptionalString(obj, "as", $"{path}.as");
        return new LlmCreateOperation(opPath, set, alias);
    }

    private static LlmUpdateOperation ParseUpdateOperation(JsonObject obj, string path)
    {
        var target = ParseTargetObject(obj, path, allowIds: true);
        var expectedCount = ReadOptionalInt(obj, "expected_count", $"{path}.expected_count");
        var set = ParseSet(ReadRequiredObject(obj, "set", $"{path}.set"), $"{path}.set");
        return new LlmUpdateOperation(target, expectedCount, set);
    }

    private static LlmDeleteOperation ParseDeleteOperation(JsonObject obj, string path)
    {
        var target = ParseTargetObject(obj, path, allowIds: true);
        var expectedCount = ReadOptionalInt(obj, "expected_count", $"{path}.expected_count");
        return new LlmDeleteOperation(target, expectedCount);
    }

    private static LlmMoveOperation ParseMoveOperation(JsonObject obj, string path)
    {
        var source = ParseTargetObject(obj, path, allowIds: false);
        var to = ReadRequiredString(obj, "to", $"{path}.to");
        return new LlmMoveOperation(source, to);
    }

    private static LlmLinkOperation ParseLinkOperation(JsonObject obj, string path)
    {
        var from = ParseEmbeddedTarget(ReadRequiredNode(obj, "from", $"{path}.from"), $"{path}.from");
        var to = ParseEmbeddedTarget(ReadRequiredNode(obj, "to", $"{path}.to"), $"{path}.to");
        var name = ReadRelationName(obj, path);
        return new LlmLinkOperation(from, name, to);
    }

    private static LlmUnlinkOperation ParseUnlinkOperation(JsonObject obj, string path)
    {
        var from = ParseEmbeddedTarget(ReadRequiredNode(obj, "from", $"{path}.from"), $"{path}.from");
        var to = ParseEmbeddedTarget(ReadRequiredNode(obj, "to", $"{path}.to"), $"{path}.to");
        var name = ReadRelationName(obj, path);
        return new LlmUnlinkOperation(from, name, to);
    }

    private static LlmSchemeGetOperation ParseSchemeGetOperation(JsonObject obj, string path)
    {
        var include = ReadOptionalStringArray(obj, "include", $"{path}.include");
        var typeNames = ReadOptionalStringArray(obj, "type_names", $"{path}.type_names");
        var treeNames = ReadOptionalStringArray(obj, "tree_names", $"{path}.tree_names");
        return new LlmSchemeGetOperation(include, typeNames, treeNames);
    }

    private static LlmSchemeDescribeTypeOperation ParseSchemeDescribeTypeOperation(JsonObject obj, string path)
    {
        var name = ReadRequiredString(obj, "name", $"{path}.name");
        return new LlmSchemeDescribeTypeOperation(name);
    }

    private static LlmSchemeDescribeTreeOperation ParseSchemeDescribeTreeOperation(JsonObject obj, string path)
    {
        var name = ReadRequiredString(obj, "name", $"{path}.name");
        return new LlmSchemeDescribeTreeOperation(name);
    }

    private static string ReadRelationName(JsonObject obj, string path)
    {
        var name = ReadOptionalString(obj, "name", $"{path}.name");
        if (name is not null)
            return name;
        return ReadRequiredString(obj, "relation", $"{path}.relation");
    }

    private static LlmSelector ParseSelector(JsonObject obj, string path)
    {
        EnsureOnlyFields(obj, path, "path", "coordinates", "match", "expect");
        var selectorPath = ReadOptionalString(obj, "path", $"{path}.path");
        var coordinates = ParseCoordinates(
            ReadOptionalObject(obj, "coordinates", $"{path}.coordinates"),
            $"{path}.coordinates");
        var match = ReadOptionalObject(obj, "match", $"{path}.match") is { } matchObj
            ? ParseSelectorMatch(matchObj, $"{path}.match")
            : null;
        var expect = ReadOptionalNode(obj, "expect");
        return new LlmSelector(selectorPath, coordinates, match, expect);
    }

    private static LlmSelectorMatch ParseSelectorMatch(JsonObject obj, string path)
    {
        EnsureOnlyFields(obj, path, "regex", "fields", "case_sensitive");
        var regex = ReadRequiredString(obj, "regex", $"{path}.regex");
        var fields = ReadOptionalStringArray(obj, "fields", $"{path}.fields");
        if (fields.Count == 0)
            fields = new[] { "title", "text" };
        var caseSensitive = ReadOptionalBool(obj, "case_sensitive", $"{path}.case_sensitive") ?? false;
        return new LlmSelectorMatch(regex, fields, caseSensitive);
    }

    private static LlmNodeSet ParseSet(JsonObject obj, string path)
    {
        var text = ReadOptionalString(obj, "text", $"{path}.text");
        var coordinates = ParseCoordinates(
            ReadOptionalObject(obj, "coordinates", $"{path}.coordinates"),
            $"{path}.coordinates");
        var relations = ParseRelations(
            ReadOptionalObject(obj, "relations", $"{path}.relations"),
            $"{path}.relations");
        return new LlmNodeSet(text, coordinates, relations);
    }

    private static IReadOnlyDictionary<string, LlmRelationChange> ParseRelations(JsonObject? obj, string path)
    {
        if (obj is null)
            return new Dictionary<string, LlmRelationChange>(StringComparer.Ordinal);

        var result = new Dictionary<string, LlmRelationChange>(StringComparer.Ordinal);
        foreach (var prop in obj)
            result[prop.Key] = ParseRelationChange(prop.Value, $"{path}.{prop.Key}");
        return result;
    }

    private static LlmRelationChange ParseRelationChange(JsonNode? node, string path)
    {
        if (node is JsonArray shortArray)
            return new LlmRelationChange(null, ParseTargetArray(shortArray, path));

        if (node is not JsonObject obj)
            throw Error("invalid_field_type", path, "Relation change должен быть массивом или объектом patch.");

        foreach (var (field, mode) in RelationPatchFields())
        {
            if (obj.TryGetPropertyValue(field, out var patchNode))
                return new LlmRelationChange(mode, ParseTargetArray(patchNode, $"{path}.{field}"));
        }

        var modeName = ReadOptionalString(obj, "mode", $"{path}.mode");
        if (modeName is not null)
        {
            var mode = ParseRelationPatchMode(modeName, $"{path}.mode");
            return new LlmRelationChange(mode, ParseTargetArray(ReadRequiredNode(obj, "targets", $"{path}.targets"), $"{path}.targets"));
        }

        throw Error(
            "invalid_field_type",
            path,
            "Relation patch должен содержать add/remove/replace либо mode+targets.");
    }

    private static IEnumerable<(string Field, LlmRelationPatchMode Mode)> RelationPatchFields()
    {
        yield return ("add", LlmRelationPatchMode.Add);
        yield return ("remove", LlmRelationPatchMode.Remove);
        yield return ("replace", LlmRelationPatchMode.Replace);
    }

    private static IReadOnlyList<LlmTarget> ParseTargetArray(JsonNode? node, string path)
    {
        if (node is not JsonArray arr)
            throw Error("invalid_field_type", path, "Список relation targets должен быть JSON-массивом.");

        var result = new List<LlmTarget>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
            result.Add(ParseEmbeddedTarget(arr[i], $"{path}[{i}]", stringAsAlias: false));
        return result;
    }

    private static LlmTarget ParseTargetObject(JsonObject obj, string path, bool allowIds)
    {
        var id = ReadOptionalInt(obj, "id", $"{path}.id");
        var ids = allowIds
            ? ReadOptionalIntArray(obj, "ids", $"{path}.ids")
            : Array.Empty<int>();
        var targetPath = ReadOptionalString(obj, "path", $"{path}.path");

        string? alias = null;
        LlmSelector? selector = null;
        if (obj.TryGetPropertyValue("target", out var targetNode) && targetNode is not null)
        {
            var target = ParseEmbeddedTarget(targetNode, $"{path}.target", stringAsAlias: true);
            id ??= target.Id;
            ids = target.Ids.Count > 0 ? target.Ids : ids;
            targetPath ??= target.Path;
            alias ??= target.Alias;
            selector ??= target.Select;
        }
        if (obj.TryGetPropertyValue("select", out var selectNode) && selectNode is not null)
            selector = ParseSelectorNode(selectNode, $"{path}.select");

        return new LlmTarget(id, ids, targetPath, alias, selector);
    }

    private static LlmTarget ParseEmbeddedTarget(JsonNode? node, string path, bool stringAsAlias = false)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var id))
                return LlmTarget.Empty with { Id = id };
            if (value.TryGetValue<long>(out var longId))
                return LlmTarget.Empty with { Id = checked((int)longId) };
            if (value.TryGetValue<string>(out var text))
                return ParseStringTarget(text, stringAsAlias);
        }

        if (node is JsonObject obj)
        {
            if (LooksLikeSelector(obj))
                return LlmTarget.Empty with { Select = ParseSelector(obj, path) };
            return ParseTargetObject(obj, path, allowIds: true);
        }

        throw Error("invalid_field_type", path, "Target должен быть id, строкой или JSON-объектом.");
    }

    private static LlmTarget ParseStringTarget(string text, bool stringAsAlias)
    {
        if (text.StartsWith("$", StringComparison.Ordinal))
            return LlmTarget.Empty with { Alias = text[1..] };
        return stringAsAlias
            ? LlmTarget.Empty with { Alias = text }
            : LlmTarget.Empty with { Path = text };
    }

    private static bool LooksLikeSelector(JsonObject obj) =>
        obj.ContainsKey("coordinates") ||
        obj.ContainsKey("expect");

    private static LlmSelector ParseSelectorNode(JsonNode node, string path)
    {
        if (node is not JsonObject obj)
            throw Error("invalid_field_type", path, "Selector должен быть JSON-объектом.");
        return ParseSelector(obj, path);
    }

    private static LlmCoordinates ParseCoordinates(JsonObject? obj, string path)
    {
        if (obj is null)
            return LlmCoordinates.Empty;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in obj)
        {
            if (prop.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                values[prop.Key] = text;
                continue;
            }
            throw Error("invalid_field_type", $"{path}.{prop.Key}", "Значение coordinates должно быть строкой.");
        }
        return new LlmCoordinates(values);
    }

    private static LlmJsonApiMethod ParseMethod(string value, string path) =>
        value switch
        {
            "query" => LlmJsonApiMethod.Query,
            "tx" => LlmJsonApiMethod.Tx,
            "scheme" => LlmJsonApiMethod.Scheme,
            _ => throw Error("unknown_method", path, $"Неизвестный method '{value}'."),
        };

    private static LlmRelationPatchMode ParseRelationPatchMode(string value, string path) =>
        value switch
        {
            "add" => LlmRelationPatchMode.Add,
            "remove" => LlmRelationPatchMode.Remove,
            "replace" => LlmRelationPatchMode.Replace,
            _ => throw Error("invalid_relation_patch_mode", path, $"Неизвестный режим relation patch '{value}'."),
        };

    private static JsonNode ReadRequiredNode(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            throw Error("missing_required_field", path, $"Обязательное поле {field} не задано.");
        return node;
    }

    private static JsonNode? ReadOptionalNode(JsonObject obj, string field) =>
        obj.TryGetPropertyValue(field, out var node) ? node : null;

    private static void EnsureOnlyFields(JsonObject obj, string path, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var prop in obj)
        {
            if (allowedSet.Contains(prop.Key))
                continue;

            throw Error(
                "invalid_request",
                $"{path}.{prop.Key}",
                $"Поле {path}.{prop.Key} не поддерживается.");
        }
    }

    private static JsonObject ReadRequiredObject(JsonObject obj, string field, string path)
    {
        var node = ReadRequiredNode(obj, field, path);
        if (node is not JsonObject child)
            throw Error("invalid_field_type", path, $"Поле {field} должно быть JSON-объектом.");
        return child;
    }

    private static JsonObject? ReadOptionalObject(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        if (node is not JsonObject child)
            throw Error("invalid_field_type", path, $"Поле {field} должно быть JSON-объектом.");
        return child;
    }

    private static string ReadRequiredString(JsonObject obj, string field, string path)
    {
        var value = ReadOptionalString(obj, field, path);
        if (value is null)
            throw Error("missing_required_field", path, $"Обязательное поле {field} не задано.");
        return value;
    }

    private static string? ReadOptionalString(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        throw Error("invalid_field_type", path, $"Поле {field} должно быть строкой.");
    }

    private static int? ReadOptionalInt(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
                return intValue;
            if (value.TryGetValue<long>(out var longValue))
                return checked((int)longValue);
        }
        throw Error("invalid_field_type", path, $"Поле {field} должно быть целым числом.");
    }

    private static bool? ReadOptionalBool(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue))
            return boolValue;
        throw Error("invalid_field_type", path, $"РџРѕР»Рµ {field} РґРѕР»Р¶РЅРѕ Р±С‹С‚СЊ boolean.");
    }

    private static IReadOnlyList<int> ReadOptionalIntArray(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            return Array.Empty<int>();
        if (node is not JsonArray arr)
            throw Error("invalid_field_type", path, $"Поле {field} должно быть массивом целых чисел.");

        var result = new List<int>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue value)
            {
                if (value.TryGetValue<int>(out var intValue))
                {
                    result.Add(intValue);
                    continue;
                }
                if (value.TryGetValue<long>(out var longValue))
                {
                    result.Add(checked((int)longValue));
                    continue;
                }
            }
            throw Error("invalid_field_type", $"{path}[{i}]", $"Поле {field} должно быть массивом целых чисел.");
        }
        return result;
    }

    private static IReadOnlyList<string> ReadOptionalStringArray(JsonObject obj, string field, string path)
    {
        if (!obj.TryGetPropertyValue(field, out var node) || node is null)
            return Array.Empty<string>();
        if (node is not JsonArray arr)
            throw Error("invalid_field_type", path, $"Поле {field} должно быть массивом строк.");

        var result = new List<string>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result.Add(text);
                continue;
            }
            throw Error("invalid_field_type", $"{path}[{i}]", $"Поле {field} должно быть массивом строк.");
        }
        return result;
    }

    private static LlmJsonApiParseException Error(string code, string path, string message) =>
        new(code, path, message);
}
