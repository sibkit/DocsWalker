using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Tokens;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Read-only executor метода query: возвращает compact-узлы по умолчанию и расширенный
/// контекст только по явному include.
/// </summary>
public sealed class LlmJsonApiQueryExecutor
{
    private readonly GraphModel _graph;
    private readonly SchemaDocument _schema;
    private readonly LlmJsonApiPathResolver _pathResolver;
    private readonly LlmJsonApiCoordinateResolver _coordinateResolver;
    private readonly Dictionary<string, HashSet<string>> _treeRefNamesByType;

    public LlmJsonApiQueryExecutor(GraphModel graph, SchemaDocument schema)
    {
        _graph = graph;
        _schema = schema;
        _pathResolver = new LlmJsonApiPathResolver(graph);
        _coordinateResolver = new LlmJsonApiCoordinateResolver(graph, schema);
        _treeRefNamesByType = BuildTreeRefIndex(schema);
    }

    public IReadOnlyList<LlmOperationResult> Execute(LlmRequest request)
    {
        if (request.Method != LlmJsonApiMethod.Query)
            throw new LlmJsonApiResolveException(
                "invalid_method",
                "$.method",
                "LlmJsonApiQueryExecutor принимает только method='query'.");

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
        string jsonPath)
    {
        if (op is not LlmSelectOperation select)
            throw new LlmJsonApiResolveException(
                "invalid_op",
                $"{jsonPath}.op",
                "query принимает только op='select'.");

        var include = QueryInclude.Parse(select.Include, $"{jsonPath}.include");
        var nodes = _coordinateResolver.ResolveSelector(select.Select, defaults, _pathResolver, $"{jsonPath}.select");
        var data = BuildQueryData(nodes, include, select.MaxTokens, $"{jsonPath}.max_tokens");
        aliases.DeclareSelectAlias(select, nodes, index, $"{jsonPath}.as");
        return new LlmOperationResult(index, select.Op, select.Alias, data);
    }

    private JsonObject BuildQueryData(
        IReadOnlyList<LlmResolvedNode> nodes,
        QueryInclude include,
        int? maxTokens,
        string maxTokensPath)
    {
        if (maxTokens is <= 0)
            throw new LlmJsonApiResolveException(
                "invalid_max_tokens",
                maxTokensPath,
                "max_tokens должен быть положительным целым числом.");

        var items = nodes
            .Select(node => new QueryItem(
                node,
                TokenCounter.CountNode(node.Node),
                EstimateNodePayloadTokens(node, include)))
            .ToArray();

        var resultNodes = new JsonArray();
        var tokensUsed = 0;
        int? stoppedAt = null;
        foreach (var item in items)
        {
            if (maxTokens is int budget && tokensUsed + item.PayloadTokens > budget)
            {
                stoppedAt = item.Node.Id;
                break;
            }

            resultNodes.Add((JsonNode)BuildNodeJson(item.Node, item.Tokens, include));
            tokensUsed += item.PayloadTokens;
        }

        var truncated = stoppedAt.HasValue;
        var result = new JsonObject
        {
            ["count"] = items.Length,
            ["tokens"] = items.Sum(i => i.Tokens),
            ["tokens_used"] = tokensUsed,
            ["truncated"] = truncated,
            ["nodes"] = resultNodes,
        };

        if (stoppedAt is int stoppedId)
        {
            result["stopped_at"] = stoppedId;
            result["omitted_count"] = items.Length - resultNodes.Count;
        }

        return result;
    }

    private JsonObject BuildNodeJson(LlmResolvedNode resolved, int tokens, QueryInclude include)
    {
        var result = new JsonObject
        {
            ["id"] = resolved.Id,
            ["path"] = resolved.Path,
            ["type"] = resolved.Node.TypeName,
            ["tokens"] = tokens,
        };

        if (include.Coordinates)
            result["coordinates"] = BuildCoordinates(resolved);
        if (include.Text)
            result["text"] = resolved.Node.Text;
        if (include.Relations)
            result["relations"] = BuildRelations(resolved);

        return result;
    }

    private JsonObject BuildCoordinates(LlmResolvedNode resolved)
    {
        var result = new JsonObject();

        foreach (var tree in _schema.Trees
                     .Where(t => !string.Equals(t.Name, Node.PathRefName, StringComparison.Ordinal))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var parentId = _graph.GetScopeParent(resolved.Id, tree.Name);
            if (parentId is null or Node.RootId)
                continue;

            result[tree.Name] = BuildClassifierPath(parentId.Value, tree.Name);
        }

        return result;
    }

    private JsonObject BuildRelations(LlmResolvedNode resolved)
    {
        return new JsonObject
        {
            ["out"] = BuildOutRelations(resolved.Node),
            ["in"] = BuildInRelations(resolved.Id),
        };
    }

    private JsonObject BuildOutRelations(Node node)
    {
        var result = new JsonObject();
        foreach (var (name, targetIds) in node.OutRefs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (IsTreeRef(node.TypeName, name))
                continue;

            var targets = new JsonArray();
            foreach (var target in targetIds.Select(TryResolveNode).Where(n => n is not null).Cast<LlmResolvedNode>())
                targets.Add((JsonNode)BuildRelationNode(target));

            result[name] = targets;
        }
        return result;
    }

    private JsonObject BuildInRelations(int id)
    {
        var result = new JsonObject();
        foreach (var group in _graph.GetInRefs(id)
                     .Select(inRef => new { Ref = inRef, Source = _graph.GetById(inRef.SourceId) })
                     .Where(item => item.Source is not null && !IsTreeRef(item.Source.TypeName, item.Ref.Name))
                     .GroupBy(item => item.Ref.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var sources = new JsonArray();
            foreach (var item in group
                         .Select(item => TryResolveNode(item.Ref.SourceId))
                         .Where(n => n is not null)
                         .Cast<LlmResolvedNode>()
                         .OrderBy(n => n.Path, StringComparer.Ordinal)
                         .ThenBy(n => n.Id))
            {
                sources.Add((JsonNode)BuildRelationNode(item));
            }

            result[group.Key] = sources;
        }
        return result;
    }

    private static JsonObject BuildRelationNode(LlmResolvedNode resolved) =>
        new()
        {
            ["id"] = resolved.Id,
            ["path"] = resolved.Path,
            ["type"] = resolved.Node.TypeName,
            ["tokens"] = TokenCounter.CountNode(resolved.Node),
        };

    private string BuildClassifierPath(int nodeId, string treeName)
    {
        var segments = new List<string>();
        var currentId = nodeId;
        while (currentId != Node.RootId)
        {
            var node = _graph.GetById(currentId);
            if (node is null)
                break;

            segments.Add(node.Title);
            var parent = _graph.GetScopeParent(currentId, treeName);
            if (parent is null)
                break;
            currentId = parent.Value;
        }

        segments.Reverse();
        return string.Join('/', segments);
    }

    private LlmResolvedNode? TryResolveNode(int id)
    {
        var node = _graph.GetById(id);
        return node is null ? null : _pathResolver.ResolveExistingId(id, "$.id");
    }

    private bool IsTreeRef(string typeName, string refName) =>
        _treeRefNamesByType.TryGetValue(typeName, out var names) && names.Contains(refName);

    private int EstimateNodePayloadTokens(LlmResolvedNode resolved, QueryInclude include)
    {
        var text = $"{resolved.Id} {resolved.Path} {resolved.Node.TypeName} {resolved.Node.Title}";
        var tokens = Math.Max(1, TokenCounter.Count(text));

        if (include.Text)
            tokens += TokenCounter.Count(resolved.Node.Text);
        if (include.Relations)
            tokens += EstimateRelationsTokens(resolved.Node);
        if (include.Coordinates)
            tokens += EstimateCoordinatesTokens(resolved);

        return tokens;
    }

    private int EstimateRelationsTokens(Node node)
    {
        var count = 0;
        foreach (var (name, targets) in node.OutRefs)
        {
            if (IsTreeRef(node.TypeName, name))
                continue;
            count += TokenCounter.Count(name) + targets.Count * 3;
        }
        return count;
    }

    private int EstimateCoordinatesTokens(LlmResolvedNode resolved)
    {
        var count = 0;
        foreach (var tree in _schema.Trees)
        {
            if (string.Equals(tree.Name, Node.PathRefName, StringComparison.Ordinal))
                continue;
            if (_graph.GetScopeParent(resolved.Id, tree.Name) is int parentId && parentId != Node.RootId)
                count += TokenCounter.Count(tree.Name) + TokenCounter.Count(BuildClassifierPath(parentId, tree.Name));
        }
        return count;
    }

    private static Dictionary<string, HashSet<string>> BuildTreeRefIndex(SchemaDocument schema)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in schema.Types)
        {
            var names = type.OutRefs
                .Where(rd => rd.Tree is not null)
                .Select(rd => rd.Name)
                .ToHashSet(StringComparer.Ordinal);
            result[type.Name] = names;
        }
        return result;
    }

    private static JsonObject BuildValidationFailureData(LlmJsonApiResolveException ex) =>
        new()
        {
            ["validation"] = new JsonObject
            {
                ["ok"] = false,
                ["code"] = ex.Code,
                ["path"] = ex.Path,
                ["details"] = ex.Details?.DeepClone(),
            },
        };

    private sealed record QueryItem(LlmResolvedNode Node, int Tokens, int PayloadTokens);

    private sealed record QueryInclude(
        bool Text,
        bool Relations,
        bool Coordinates)
    {
        public static QueryInclude Empty { get; } = new(false, false, false);

        public static QueryInclude Parse(IReadOnlyList<string> values, string jsonPath)
        {
            if (values.Count == 0)
                return Empty;

            var set = values.ToHashSet(StringComparer.Ordinal);
            foreach (var value in set)
            {
                if (!Allowed.Contains(value))
                {
                    throw new LlmJsonApiResolveException(
                        "invalid_include",
                        jsonPath,
                        $"query.include содержит неизвестное поле '{value}'.",
                        new JsonObject
                        {
                            ["include"] = value,
                        });
                }
            }

            return new QueryInclude(
                set.Contains("text"),
                set.Contains("relations"),
                set.Contains("coordinates"));
        }

        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "text",
            "relations",
            "coordinates",
        };
    }
}
