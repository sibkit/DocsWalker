using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Верхний executor LLM-facing JSON API: парсит request envelope, диспатчит метод
/// и возвращает единый wire-level response envelope.
/// </summary>
public sealed class LlmJsonApiExecutor
{
    private readonly GraphModel _graph;
    private readonly SchemaDocument _schema;
    private readonly Func<IReadOnlyList<WriteOp>, WriteResult> _apply;
    private readonly Func<long> _baseRevisionProvider;

    public LlmJsonApiExecutor(
        GraphModel graph,
        SchemaDocument schema,
        WriteApi writeApi,
        Func<long>? baseRevisionProvider = null)
        : this(graph, schema, ops => writeApi.Apply(ops), baseRevisionProvider)
    {
    }

    public LlmJsonApiExecutor(
        GraphModel graph,
        SchemaDocument schema,
        Func<IReadOnlyList<WriteOp>, WriteResult> apply,
        Func<long>? baseRevisionProvider = null)
    {
        _graph = graph;
        _schema = schema;
        _apply = apply;
        _baseRevisionProvider = baseRevisionProvider ?? (() => 0);
    }

    public JsonObject Execute(string json)
    {
        try
        {
            return Execute(JsonNode.Parse(json));
        }
        catch (JsonException ex)
        {
            var details = new JsonObject();
            if (ex.Path is not null)
                details["path"] = ex.Path;
            return BuildError(null, "invalid_json", "Запрос LLM JSON API должен быть корректным JSON.", details);
        }
    }

    public JsonObject Execute(JsonNode? input)
    {
        var rawMethod = TryReadMethod(input);
        LlmRequest request;
        try
        {
            request = LlmJsonApiParser.Parse(input);
        }
        catch (LlmJsonApiParseException ex)
        {
            return BuildError(rawMethod, ex.Code, ex.Message, new JsonObject { ["path"] = ex.Path });
        }

        var method = FormatMethod(request.Method);
        try
        {
            var results = ExecuteParsed(request);
            if (RequiresTopLevelValidationFailure(request.Method) &&
                TryFindValidationFailure(results, out var failedResult, out var validation))
            {
                return BuildOperationValidationError(method, failedResult, validation);
            }

            return BuildSuccess(request.Method, results);
        }
        catch (LlmJsonApiResolveException ex)
        {
            return BuildError(method, ex.Code, ex.Message, BuildExceptionDetails(ex.Path, ex.Details));
        }
        catch (WriteApiException ex)
        {
            var details = ex.RefName is null
                ? null
                : new JsonObject { ["ref"] = ex.RefName };
            return BuildError(method, ex.Code, ex.Message, details);
        }
        catch (WriteValidationException ex)
        {
            return BuildError(method, "validation_failed", ex.Message, BuildValidationErrors(ex));
        }
    }

    private IReadOnlyList<LlmOperationResult> ExecuteParsed(LlmRequest request) =>
        request.Method switch
        {
            LlmJsonApiMethod.Hit => new LlmJsonApiHitExecutor(_graph, _schema).Execute(request),
            LlmJsonApiMethod.Query => new LlmJsonApiQueryExecutor(_graph, _schema).Execute(request),
            LlmJsonApiMethod.Tx => new LlmJsonApiTxExecutor(_graph, _schema, _apply).Execute(request),
            _ => throw new LlmJsonApiResolveException(
                "unknown_method",
                "$.method",
                $"Неизвестный method '{request.Method}'."),
        };

    private JsonObject BuildSuccess(
        LlmJsonApiMethod method,
        IReadOnlyList<LlmOperationResult> results) =>
        new()
        {
            ["ok"] = true,
            ["method"] = FormatMethod(method),
            ["base_revision"] = _baseRevisionProvider(),
            ["summary"] = BuildSummary(method, results),
            ["results"] = BuildResults(results),
        };

    private static JsonObject BuildOperationValidationError(
        string method,
        LlmOperationResult failedResult,
        JsonObject validation)
    {
        var code = ReadString(validation, "code") ?? "validation_failed";
        var message = ReadString(validation, "message") ?? "Операция LLM JSON API не прошла validation.";
        var details = new JsonObject
        {
            ["operation_index"] = failedResult.Index,
            ["op"] = failedResult.Op,
        };
        if (failedResult.Alias is not null)
            details["alias"] = failedResult.Alias;
        if (ReadString(validation, "path") is string path)
            details["path"] = path;
        if (validation.TryGetPropertyValue("details", out var nestedDetails) && nestedDetails is not null)
            details["details"] = nestedDetails.DeepClone();

        return BuildError(method, code, message, details);
    }

    private static JsonObject BuildError(
        string? method,
        string code,
        string message,
        JsonObject? details) =>
        new()
        {
            ["ok"] = false,
            ["method"] = method,
            ["code"] = code,
            ["message"] = message,
            ["details"] = details ?? new JsonObject(),
        };

    private static JsonObject BuildExceptionDetails(string path, JsonObject? details)
    {
        var result = new JsonObject { ["path"] = path };
        if (details is not null)
            result["details"] = details.DeepClone();
        return result;
    }

    private static JsonObject BuildValidationErrors(WriteValidationException ex)
    {
        var errors = new JsonArray();
        foreach (var error in ex.Errors)
        {
            errors.Add((JsonNode)new JsonObject
            {
                ["code"] = error.Code,
                ["message"] = error.Message,
                ["node_id"] = error.NodeId,
                ["path"] = error.Path,
                ["ref"] = error.RefName,
            });
        }
        return new JsonObject { ["errors"] = errors };
    }

    private static JsonArray BuildResults(IReadOnlyList<LlmOperationResult> results)
    {
        var array = new JsonArray();
        foreach (var result in results)
        {
            var item = new JsonObject
            {
                ["index"] = result.Index,
                ["op"] = result.Op,
                ["alias"] = result.Alias,
                ["data"] = result.Data.DeepClone(),
            };
            array.Add((JsonNode)item);
        }
        return array;
    }

    private static bool TryFindValidationFailure(
        IReadOnlyList<LlmOperationResult> results,
        out LlmOperationResult failedResult,
        out JsonObject validation)
    {
        foreach (var result in results)
        {
            if (!result.Data.TryGetPropertyValue("validation", out var node) || node is not JsonObject obj)
                continue;

            if (obj.TryGetPropertyValue("ok", out var okNode) &&
                okNode is JsonValue okValue &&
                okValue.TryGetValue<bool>(out var ok) &&
                !ok)
            {
                failedResult = result;
                validation = obj;
                return true;
            }
        }

        failedResult = null!;
        validation = null!;
        return false;
    }

    private static bool RequiresTopLevelValidationFailure(LlmJsonApiMethod method) =>
        method is LlmJsonApiMethod.Hit or LlmJsonApiMethod.Query or LlmJsonApiMethod.Tx;

    private static string BuildSummary(
        LlmJsonApiMethod method,
        IReadOnlyList<LlmOperationResult> results) =>
        method switch
        {
            LlmJsonApiMethod.Hit => $"hit: проверено операций {results.Count}.",
            LlmJsonApiMethod.Query => $"query: выполнено операций чтения {results.Count}.",
            LlmJsonApiMethod.Tx => BuildTxSummary(results),
            _ => $"выполнено операций {results.Count}.",
        };

    private static string BuildTxSummary(IReadOnlyList<LlmOperationResult> results)
    {
        var applied = results.Any(result =>
            result.Data.TryGetPropertyValue("applied", out var appliedNode) &&
            appliedNode is JsonValue value &&
            value.TryGetValue<bool>(out var flag) &&
            flag);

        return applied
            ? $"tx: применено операций {results.Count}."
            : $"tx: выполнено без записи, операций {results.Count}.";
    }

    private static string FormatMethod(LlmJsonApiMethod method) =>
        method switch
        {
            LlmJsonApiMethod.Hit => "hit",
            LlmJsonApiMethod.Query => "query",
            LlmJsonApiMethod.Tx => "tx",
            _ => method.ToString().ToLowerInvariant(),
        };

    private static string? TryReadMethod(JsonNode? input)
    {
        if (input is not JsonObject obj ||
            !obj.TryGetPropertyValue("method", out var methodNode) ||
            methodNode is not JsonValue value ||
            !value.TryGetValue<string>(out var method))
        {
            return null;
        }

        return method;
    }

    private static string? ReadString(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) &&
        node is JsonValue value &&
        value.TryGetValue<string>(out var text)
            ? text
            : null;
}
