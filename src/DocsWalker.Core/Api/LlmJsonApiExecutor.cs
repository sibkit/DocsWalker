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

    public LlmJsonApiExecutor(
        GraphModel graph,
        SchemaDocument schema,
        WriteApi writeApi)
        : this(graph, schema, ops => writeApi.Apply(ops))
    {
    }

    public LlmJsonApiExecutor(
        GraphModel graph,
        SchemaDocument schema,
        Func<IReadOnlyList<WriteOp>, WriteResult> apply)
    {
        _graph = graph;
        _schema = schema;
        _apply = apply;
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
            return BuildError("invalid_json", details);
        }
    }

    public JsonObject Execute(JsonNode? input)
    {
        LlmRequest request;
        try
        {
            request = LlmJsonApiParser.Parse(input);
        }
        catch (LlmJsonApiParseException ex)
        {
            return BuildError(ex.Code, new JsonObject { ["path"] = ex.Path });
        }

        try
        {
            var results = ExecuteParsed(request);
            if (RequiresTopLevelValidationFailure(request.Method) &&
                TryFindValidationFailure(results, out var failedResult, out var validation))
            {
                return BuildOperationValidationError(failedResult, validation);
            }

            return BuildSuccess(results);
        }
        catch (LlmJsonApiResolveException ex)
        {
            return BuildError(ex.Code, BuildExceptionDetails(ex.Path, ex.Details));
        }
        catch (WriteApiException ex)
        {
            var details = ex.RefName is null
                ? null
                : new JsonObject { ["ref"] = ex.RefName };
            return BuildError(ex.Code, details);
        }
        catch (WriteValidationException ex)
        {
            return BuildError("validation_failed", BuildValidationErrors(ex));
        }
    }

    private IReadOnlyList<LlmOperationResult> ExecuteParsed(LlmRequest request) =>
        request.Method switch
        {
            LlmJsonApiMethod.Query => new LlmJsonApiQueryExecutor(_graph, _schema).Execute(request),
            LlmJsonApiMethod.Tx => new LlmJsonApiTxExecutor(_graph, _schema, _apply).Execute(request),
            LlmJsonApiMethod.Scheme => new LlmJsonApiSchemeExecutor(_schema).Execute(request),
            _ => throw new LlmJsonApiResolveException(
                "unknown_method",
                "$.method",
                $"Неизвестный method '{request.Method}'."),
        };

    private static JsonObject BuildSuccess(IReadOnlyList<LlmOperationResult> results) =>
        new()
        {
            ["result"] = BuildResult(results),
        };

    private static JsonObject BuildOperationValidationError(
        LlmOperationResult failedResult,
        JsonObject validation)
    {
        var code = ReadString(validation, "code") ?? "validation_failed";
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

        return BuildError(code, details);
    }

    private static JsonObject BuildError(
        string code,
        JsonObject? details) =>
        new()
        {
            ["code"] = code,
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
                ["node_id"] = error.NodeId,
                ["path"] = error.Path,
                ["ref"] = error.RefName,
            });
        }
        return new JsonObject { ["errors"] = errors };
    }

    private static JsonNode BuildResult(IReadOnlyList<LlmOperationResult> results)
    {
        if (results.Count == 0)
            return JsonValue.Create("ok")!;

        if (results.Count == 1)
            return results[0].Data.Count == 0
                ? JsonValue.Create("ok")!
                : results[0].Data.DeepClone();

        var array = new JsonArray();
        foreach (var result in results)
            array.Add(result.Data.DeepClone());
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
        method is LlmJsonApiMethod.Query or LlmJsonApiMethod.Tx or LlmJsonApiMethod.Scheme;

    private static string? ReadString(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) &&
        node is JsonValue value &&
        value.TryGetValue<string>(out var text)
            ? text
            : null;
}
