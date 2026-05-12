using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Tokens;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Read-only executor метода hit: резолвит selector-ы и write-targets, но не применяет
/// write-операции и не возвращает полный text узлов.
/// </summary>
public sealed class LlmJsonApiHitExecutor
{
    private const int SampleLimit = 5;

    private readonly GraphModel _graph;
    private readonly LlmJsonApiPathResolver _pathResolver;
    private readonly LlmJsonApiCoordinateResolver _coordinateResolver;

    public LlmJsonApiHitExecutor(GraphModel graph, SchemaDocument schema)
    {
        _graph = graph;
        _pathResolver = new LlmJsonApiPathResolver(graph);
        _coordinateResolver = new LlmJsonApiCoordinateResolver(graph, schema);
    }

    public IReadOnlyList<LlmOperationResult> Execute(LlmRequest request)
    {
        if (request.Method != LlmJsonApiMethod.Hit)
            throw new LlmJsonApiResolveException(
                "invalid_method",
                "$.method",
                "LlmJsonApiHitExecutor принимает только method='hit'.");

        var aliases = new LlmJsonApiAliasScope(_pathResolver, _coordinateResolver);
        var results = new List<LlmOperationResult>(request.Ops.Count);

        for (int i = 0; i < request.Ops.Count; i++)
        {
            var op = request.Ops[i];
            var jsonPath = $"$.ops[{i}]";
            try
            {
                results.Add(ExecuteOperation(op, request.Defaults, aliases, i, jsonPath));
            }
            catch (LlmJsonApiResolveException ex)
            {
                results.Add(new LlmOperationResult(i, op.Op, op.Alias, BuildValidationFailureData(ex)));
            }
        }

        return results;
    }

    private LlmOperationResult ExecuteOperation(
        LlmOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath) =>
        op switch
        {
            LlmSelectOperation select => ExecuteSelect(select, defaults, aliases, index, jsonPath),
            LlmCreateOperation create => ExecuteCreate(create, defaults, aliases, index, jsonPath),
            LlmUpdateOperation update => ExecuteUpdate(update, defaults, aliases, index, jsonPath),
            LlmDeleteOperation delete => ExecuteDelete(delete, defaults, aliases, index, jsonPath),
            LlmMoveOperation move => ExecuteMove(move, defaults, aliases, index, jsonPath),
            LlmLinkOperation link => ExecuteLink(link, defaults, aliases, index, jsonPath),
            LlmUnlinkOperation unlink => ExecuteUnlink(unlink, defaults, aliases, index, jsonPath),
            _ => throw new LlmJsonApiResolveException(
                "unknown_op",
                $"{jsonPath}.op",
                $"Операция '{op.Op}' не поддерживается hit executor-ом."),
        };

    private LlmOperationResult ExecuteSelect(
        LlmSelectOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var nodes = _coordinateResolver.ResolveSelector(op.Select, defaults, _pathResolver, $"{jsonPath}.select");
        var data = BuildSelectionData(nodes, op.MaxTokens);
        aliases.DeclareSelectAlias(op, nodes, index, $"{jsonPath}.as");
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult ExecuteCreate(
        LlmCreateOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var validation = ValidateCreate(op, defaults, jsonPath, out var resolvedPath, out var parent, out var typeName);
        var data = new JsonObject
        {
            ["count"] = validation.Ok ? 1 : 0,
            ["validation"] = BuildValidationJson(validation),
            ["would_change"] = BuildCreateWouldChange(op, resolvedPath, parent, typeName),
        };

        if (validation.Ok && op.Alias is not null)
            aliases.DeclareCreateAlias(op, CreatePreviewNode(op, index, resolvedPath!, parent!.Id, typeName!), index, $"{jsonPath}.as");

        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult ExecuteUpdate(
        LlmUpdateOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var shapeValidation = ValidateTargetShape(op.Target);
        if (!shapeValidation.Ok)
            return BuildWriteValidationResult(index, op, shapeValidation, Array.Empty<LlmResolvedNode>(), null);

        var nodes = aliases.ResolveTarget(op.Target, defaults, jsonPath);
        var validation = ValidateExpectedCount(op.Target, nodes.Count, op.ExpectedCount);
        return BuildWriteValidationResult(index, op, validation, nodes, BuildUpdateWouldChange(op, nodes));
    }

    private LlmOperationResult ExecuteDelete(
        LlmDeleteOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var shapeValidation = ValidateTargetShape(op.Target);
        if (!shapeValidation.Ok)
            return BuildWriteValidationResult(index, op, shapeValidation, Array.Empty<LlmResolvedNode>(), null);

        var nodes = aliases.ResolveTarget(op.Target, defaults, jsonPath);
        var validation = ValidateExpectedCount(op.Target, nodes.Count, op.ExpectedCount);
        return BuildWriteValidationResult(index, op, validation, nodes, BuildDeleteWouldChange(nodes));
    }

    private LlmOperationResult ExecuteMove(
        LlmMoveOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var source = aliases.ResolveSingleTarget(op.Source, defaults, jsonPath);
        var validation = ValidateMoveTo(op, defaults, source, jsonPath, out var resolvedTo, out var parent);
        var data = new JsonObject
        {
            ["validation"] = BuildValidationJson(validation),
            ["would_change"] = BuildMoveWouldChange(source, resolvedTo, parent),
        };
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult ExecuteLink(
        LlmLinkOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var from = aliases.ResolveSingleTarget(op.From, defaults, $"{jsonPath}.from");
        var to = aliases.ResolveSingleTarget(op.To, defaults, $"{jsonPath}.to");
        var validation = ValidateRelationName(op.Name);
        var data = new JsonObject
        {
            ["validation"] = BuildValidationJson(validation),
            ["would_change"] = BuildLinkWouldChange("link", op.Name, from, to),
        };
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult ExecuteUnlink(
        LlmUnlinkOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var from = aliases.ResolveSingleTarget(op.From, defaults, $"{jsonPath}.from");
        var to = aliases.ResolveSingleTarget(op.To, defaults, $"{jsonPath}.to");
        var validation = ValidateRelationName(op.Name);
        var data = new JsonObject
        {
            ["validation"] = BuildValidationJson(validation),
            ["would_change"] = BuildLinkWouldChange("unlink", op.Name, from, to),
        };
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult BuildWriteValidationResult(
        int index,
        LlmOperation op,
        HitValidation validation,
        IReadOnlyList<LlmResolvedNode> nodes,
        JsonObject? wouldChange)
    {
        var data = BuildSelectionData(nodes, maxTokens: null);
        data["validation"] = BuildValidationJson(validation);
        data["would_change"] = wouldChange;
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private JsonObject BuildSelectionData(IReadOnlyList<LlmResolvedNode> nodes, int? maxTokens)
    {
        var items = nodes
            .Select(node => new SelectionItem(
                node,
                TokenCounter.CountNode(node.Node),
                CountSubtreeTokens(node)))
            .ToArray();

        var tokens = items.Sum(i => i.Tokens);
        var subtreeTokens = items.Sum(i => i.SubtreeTokens);
        var data = new JsonObject
        {
            ["count"] = items.Length,
            ["tokens"] = tokens,
            ["subtree_tokens"] = subtreeTokens,
            ["breakdown_by_type"] = BuildBreakdownByType(items),
            ["samples"] = BuildSamples(items),
            ["within_budget"] = !maxTokens.HasValue || subtreeTokens <= maxTokens.Value,
        };
        return data;
    }

    private int CountSubtreeTokens(LlmResolvedNode node)
    {
        var visited = new HashSet<int>();
        return CountSubtreeTokens(node.Id, node.Node, visited);
    }

    private int CountSubtreeTokens(int nodeId, Node node, HashSet<int> visited)
    {
        if (!visited.Add(nodeId))
            return 0;

        var result = TokenCounter.CountNode(node);
        if (_graph.GetById(nodeId) is null)
            return result;

        foreach (var childId in _graph.GetScopeChildren(nodeId, Node.PathRefName))
        {
            var child = _graph.GetById(childId);
            if (child is not null)
                result += CountSubtreeTokens(childId, child, visited);
        }
        return result;
    }

    private JsonObject BuildBreakdownByType(IReadOnlyList<SelectionItem> items)
    {
        var result = new JsonObject();
        foreach (var group in items
                     .GroupBy(item => item.Node.Node.TypeName, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            result[group.Key] = group.Count();
        }
        return result;
    }

    private JsonArray BuildSamples(IReadOnlyList<SelectionItem> items)
    {
        var result = new JsonArray();
        foreach (var item in items.Take(SampleLimit))
            result.Add((JsonNode)BuildCompactNode(item));
        return result;
    }

    private static JsonObject BuildCompactNode(SelectionItem item) =>
        new()
        {
            ["id"] = item.Node.Id,
            ["path"] = item.Node.Path,
            ["coordinates"] = new JsonObject
            {
                ["type"] = item.Node.Node.TypeName,
            },
            ["title"] = item.Node.Node.Title,
            ["tokens"] = item.Tokens,
            ["subtree_tokens"] = item.SubtreeTokens,
        };

    private HitValidation ValidateCreate(
        LlmCreateOperation op,
        LlmRequestDefaults defaults,
        string jsonPath,
        out string? resolvedPath,
        out LlmResolvedNode? parent,
        out string? typeName)
    {
        resolvedPath = null;
        parent = null;
        typeName = null;

        if (string.IsNullOrWhiteSpace(op.Path))
            return HitValidation.Invalid("not_found", "create.path должен быть задан.");

        var path = _pathResolver.NormalizePath(op.Path, defaults, $"{jsonPath}.path");
        resolvedPath = path.Path;
        if (path.HasWildcard)
            return HitValidation.Invalid("ambiguous_selector", "create.path должен быть exact path без wildcard.");

        var createPath = resolvedPath;
        if (_pathResolver.AllNodes.Any(n => string.Equals(n.Path, createPath, StringComparison.Ordinal)))
            return HitValidation.Invalid("already_exists", $"Path '{resolvedPath}' уже занят существующим узлом.");

        parent = ResolveParent(resolvedPath);
        if (parent is null)
            return HitValidation.Invalid("path_parent_not_found", $"Parent path для '{resolvedPath}' не найден.");

        var coordinates = _coordinateResolver.Resolve(op.Set.Coordinates, defaults, $"{jsonPath}.set.coordinates");
        if (coordinates.Type is null)
        {
            return HitValidation.Invalid(
                "unknown_coordinate",
                "coordinates.type обязателен для create.",
                new JsonObject
                {
                    ["coordinate"] = "type",
                    ["value"] = null,
                });
        }

        typeName = coordinates.Type.Name;
        return HitValidation.Valid();
    }

    private HitValidation ValidateMoveTo(
        LlmMoveOperation op,
        LlmRequestDefaults defaults,
        LlmResolvedNode source,
        string jsonPath,
        out string? resolvedTo,
        out LlmResolvedNode? parent)
    {
        resolvedTo = null;
        parent = null;

        var path = _pathResolver.NormalizePath(op.To, defaults, $"{jsonPath}.to");
        resolvedTo = path.Path;
        if (path.HasWildcard)
            return HitValidation.Invalid("ambiguous_selector", "move.to должен быть exact path без wildcard.");

        var movePath = resolvedTo;
        var existing = _pathResolver.AllNodes.FirstOrDefault(n => string.Equals(n.Path, movePath, StringComparison.Ordinal));
        if (existing is not null && existing.Id != source.Id)
            return HitValidation.Invalid("already_exists", $"Path '{resolvedTo}' уже занят существующим узлом.");

        parent = ResolveParent(resolvedTo);
        if (parent is null)
            return HitValidation.Invalid("path_parent_not_found", $"Parent path для '{resolvedTo}' не найден.");

        return HitValidation.Valid();
    }

    private LlmResolvedNode? ResolveParent(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            var root = _graph.GetById(Node.RootId)!;
            return new LlmResolvedNode(Node.RootId, "", root);
        }

        var parentPath = path[..lastSlash];
        return _pathResolver.AllNodes.FirstOrDefault(n => string.Equals(n.Path, parentPath, StringComparison.Ordinal));
    }

    private static HitValidation ValidateTargetShape(LlmTarget target)
    {
        var forms = 0;
        if (target.Id.HasValue) forms++;
        if (target.Ids.Count > 0) forms++;
        if (target.Path is not null) forms++;
        if (target.Alias is not null) forms++;
        if (target.Select is not null) forms++;

        return forms == 1
            ? HitValidation.Valid()
            : HitValidation.Invalid(
                "ambiguous_selector",
                "Target должен задавать ровно один способ выбора: id, ids, path, target alias или select.",
                new JsonObject { ["target_forms"] = forms });
    }

    private static HitValidation ValidateExpectedCount(LlmTarget target, int actualCount, int? expectedCount)
    {
        if (target.Ids.Count > 0 && target.Ids.Distinct().Count() != target.Ids.Count)
        {
            return HitValidation.Invalid(
                "count_mismatch",
                "ids не должен содержать повторы.",
                new JsonObject
                {
                    ["actual_count"] = actualCount,
                    ["ids_count"] = target.Ids.Count,
                    ["unique_ids_count"] = target.Ids.Distinct().Count(),
                });
        }

        if (target.Ids.Count > 0 && expectedCount is null)
        {
            return HitValidation.Invalid(
                "count_mismatch",
                "Для ids в update/delete требуется expected_count.",
                new JsonObject
                {
                    ["actual_count"] = actualCount,
                    ["ids_count"] = target.Ids.Count,
                });
        }

        if (expectedCount is int expected)
        {
            return actualCount == expected
                ? HitValidation.Valid()
                : HitValidation.Invalid(
                    "count_mismatch",
                    $"expected_count={expected}, фактически найдено {actualCount}.",
                    new JsonObject
                    {
                        ["expected_count"] = expected,
                        ["actual_count"] = actualCount,
                    });
        }

        return actualCount == 1
            ? HitValidation.Valid()
            : HitValidation.Invalid(
                actualCount == 0 ? "not_found" : "ambiguous_selector",
                $"Операция без expected_count должна выбрать ровно один узел, найдено {actualCount}.",
                new JsonObject { ["actual_count"] = actualCount });
    }

    private static HitValidation ValidateRelationName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? HitValidation.Invalid("not_found", "Имя relation не должно быть пустым.")
            : HitValidation.Valid();

    private LlmResolvedNode CreatePreviewNode(
        LlmCreateOperation op,
        int index,
        string path,
        int parentId,
        string typeName)
    {
        var title = PathTitle(path);
        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [Node.PathRefName] = new[] { parentId },
        };

        var node = new Node
        {
            Id = -(index + 1),
            TypeName = typeName,
            Title = title,
            Text = op.Set.Text ?? "",
            OutRefs = outRefs,
            SourceFile = "<hit-preview>",
        };
        return new LlmResolvedNode(node.Id, path, node);
    }

    private JsonObject BuildCreateWouldChange(
        LlmCreateOperation op,
        string? resolvedPath,
        LlmResolvedNode? parent,
        string? typeName) =>
        new()
        {
            ["op"] = "create",
            ["path"] = resolvedPath,
            ["title"] = resolvedPath is null ? null : PathTitle(resolvedPath),
            ["parent_id"] = parent?.Id,
            ["coordinates"] = typeName is null
                ? null
                : new JsonObject
                {
                    ["type"] = typeName,
                },
            ["set"] = BuildSetSummary(op.Set),
        };

    private static JsonObject BuildUpdateWouldChange(LlmUpdateOperation op, IReadOnlyList<LlmResolvedNode> nodes) =>
        new()
        {
            ["op"] = "update",
            ["count"] = nodes.Count,
            ["ids"] = BuildIds(nodes),
            ["set"] = BuildSetSummary(op.Set),
        };

    private JsonObject BuildDeleteWouldChange(IReadOnlyList<LlmResolvedNode> nodes) =>
        new()
        {
            ["op"] = "delete",
            ["count"] = nodes.Count,
            ["ids"] = BuildIds(nodes),
            ["path_children_count"] = nodes.Sum(node => _graph.GetScopeChildren(node.Id, Node.PathRefName).Count),
            ["incoming_refs_count"] = nodes.Sum(node => _graph.GetInRefs(node.Id).Count(r => r.Name != Node.PathRefName)),
        };

    private JsonObject BuildMoveWouldChange(LlmResolvedNode source, string? resolvedTo, LlmResolvedNode? parent) =>
        new()
        {
            ["op"] = "move",
            ["id"] = source.Id,
            ["from_path"] = source.Path,
            ["to_path"] = resolvedTo,
            ["to_parent_id"] = parent?.Id,
            ["title"] = resolvedTo is null ? null : PathTitle(resolvedTo),
        };

    private static JsonObject BuildLinkWouldChange(
        string op,
        string relation,
        LlmResolvedNode from,
        LlmResolvedNode to) =>
        new()
        {
            ["op"] = op,
            ["relation"] = relation,
            ["from_id"] = from.Id,
            ["from_path"] = from.Path,
            ["to_id"] = to.Id,
            ["to_path"] = to.Path,
        };

    private static JsonObject BuildSetSummary(LlmNodeSet set)
    {
        var coordinates = new JsonArray();
        foreach (var name in set.Coordinates.Values.Keys.OrderBy(name => name, StringComparer.Ordinal))
            coordinates.Add((JsonNode?)JsonValue.Create(name));

        var relations = new JsonArray();
        foreach (var name in set.Relations.Keys.OrderBy(name => name, StringComparer.Ordinal))
            relations.Add((JsonNode?)JsonValue.Create(name));

        return new JsonObject
        {
            ["text"] = set.Text is not null,
            ["coordinates"] = coordinates,
            ["relations"] = relations,
        };
    }

    private static JsonArray BuildIds(IReadOnlyList<LlmResolvedNode> nodes)
    {
        var ids = new JsonArray();
        foreach (var node in nodes)
            ids.Add((JsonNode?)JsonValue.Create(node.Id));
        return ids;
    }

    private static JsonObject BuildValidationFailureData(LlmJsonApiResolveException ex) =>
        new()
        {
            ["validation"] = BuildValidationJson(HitValidation.Invalid(
                ex.Code,
                ex.Message,
                ex.Details,
                ex.Path)),
        };

    private static JsonObject BuildValidationJson(HitValidation validation)
    {
        var result = new JsonObject
        {
            ["ok"] = validation.Ok,
        };
        if (validation.Code is not null)
            result["code"] = validation.Code;
        if (validation.Message is not null)
            result["message"] = validation.Message;
        if (validation.Path is not null)
            result["path"] = validation.Path;
        if (validation.Details is not null)
            result["details"] = validation.Details.DeepClone();
        return result;
    }

    private static string PathTitle(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }

    private sealed record SelectionItem(LlmResolvedNode Node, int Tokens, int SubtreeTokens);

    private sealed record HitValidation(
        bool Ok,
        string? Code,
        string? Message,
        JsonObject? Details,
        string? Path)
    {
        public static HitValidation Valid() =>
            new(true, null, null, null, null);

        public static HitValidation Invalid(
            string code,
            string message,
            JsonObject? details = null,
            string? path = null) =>
            new(false, code, message, details, path);
    }
}
