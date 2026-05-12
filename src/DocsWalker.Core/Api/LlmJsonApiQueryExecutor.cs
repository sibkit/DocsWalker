using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private const int DefaultGrepLimit = 20;
    private const int MaxGrepLimit = 200;
    private const int DefaultGrepContextChars = 80;
    private const int MaxGrepContextChars = 500;
    private static readonly TimeSpan GrepRegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly GraphModel _graph;
    private readonly SchemaDocument _schema;
    private readonly LlmJsonApiPathResolver _pathResolver;
    private readonly LlmJsonApiCoordinateResolver _coordinateResolver;
    private readonly Dictionary<string, TypeDefinition> _typesByName;
    private readonly Dictionary<string, HashSet<string>> _treeRefNamesByType;

    public LlmJsonApiQueryExecutor(GraphModel graph, SchemaDocument schema)
    {
        _graph = graph;
        _schema = schema;
        _pathResolver = new LlmJsonApiPathResolver(graph);
        _coordinateResolver = new LlmJsonApiCoordinateResolver(graph, schema);
        _typesByName = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
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
        if (op is LlmGrepOperation grep)
            return ExecuteGrep(grep, defaults, index, jsonPath);

        if (op is not LlmSelectOperation select)
            throw new LlmJsonApiResolveException(
                "invalid_op",
                $"{jsonPath}.op",
                "query в v1 принимает только op='select'.");

        var include = QueryInclude.Parse(select.Include, $"{jsonPath}.include");
        var nodes = _coordinateResolver.ResolveSelector(select.Select, defaults, _pathResolver, $"{jsonPath}.select");
        var data = BuildQueryData(nodes, include, select.MaxTokens);
        aliases.DeclareSelectAlias(select, nodes, index, $"{jsonPath}.as");
        return new LlmOperationResult(index, select.Op, select.Alias, data);
    }

    private LlmOperationResult ExecuteGrep(
        LlmGrepOperation grep,
        LlmRequestDefaults defaults,
        int index,
        string jsonPath)
    {
        var nodes = ResolveGrepScope(grep, defaults, jsonPath);
        var data = BuildGrepData(grep, nodes, jsonPath);
        return new LlmOperationResult(index, grep.Op, grep.Alias, data);
    }

    private JsonObject BuildQueryData(
        IReadOnlyList<LlmResolvedNode> nodes,
        QueryInclude include,
        int? maxTokens)
    {
        if (maxTokens is <= 0)
            throw new LlmJsonApiResolveException(
                "invalid_max_tokens",
                "max_tokens",
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
            ["returned"] = resultNodes.Count,
            ["tokens"] = items.Sum(i => i.Tokens),
            ["tokens_used"] = tokensUsed,
            ["within_budget"] = !truncated,
            ["truncated"] = truncated,
            ["nodes"] = resultNodes,
        };

        if (maxTokens is int budgetValue)
            result["tokens_budget"] = budgetValue;
        if (stoppedAt is int stoppedId)
            result["stopped_at"] = stoppedId;

        return result;
    }

    private IReadOnlyList<LlmResolvedNode> ResolveGrepScope(
        LlmGrepOperation grep,
        LlmRequestDefaults defaults,
        string jsonPath)
    {
        if (grep.Scope is not null)
            return _coordinateResolver.ResolveSelector(grep.Scope, defaults, _pathResolver, $"{jsonPath}.scope");

        var nodes = _pathResolver.AllNodes;
        return defaults.Coordinates.Values.Count == 0
            ? nodes
            : _coordinateResolver.FilterNodes(nodes, LlmCoordinates.Empty, defaults, $"{jsonPath}.scope.coordinates");
    }

    private JsonObject BuildGrepData(
        LlmGrepOperation grep,
        IReadOnlyList<LlmResolvedNode> nodes,
        string jsonPath)
    {
        if (grep.Pattern.Length == 0)
            throw new LlmJsonApiResolveException(
                "invalid_grep_pattern",
                $"{jsonPath}.pattern",
                "grep.pattern РЅРµ РґРѕР»Р¶РµРЅ Р±С‹С‚СЊ РїСѓСЃС‚С‹Рј.");

        var fields = GrepFields.Parse(grep.In, $"{jsonPath}.in");
        var limit = ValidateGrepLimit(grep.Limit, $"{jsonPath}.limit");
        var contextChars = ValidateGrepContextChars(grep.ContextChars, $"{jsonPath}.context_chars");
        if (grep.MaxTokens is <= 0)
            throw new LlmJsonApiResolveException(
                "invalid_max_tokens",
                $"{jsonPath}.max_tokens",
                "max_tokens РґРѕР»Р¶РµРЅ Р±С‹С‚СЊ РїРѕР»РѕР¶РёС‚РµР»СЊРЅС‹Рј С†РµР»С‹Рј С‡РёСЃР»РѕРј.");

        var regex = grep.Regex ? BuildGrepRegex(grep, $"{jsonPath}.pattern") : null;
        var matches = new JsonArray();
        var totalMatches = 0;
        var tokensUsed = 0;
        int? stoppedAt = null;

        foreach (var node in nodes.OrderBy(n => n.Path, StringComparer.Ordinal).ThenBy(n => n.Id))
        {
            foreach (var field in EnumerateGrepFields(node, fields))
            {
                foreach (var span in FindGrepSpans(field.Value, grep, regex, $"{jsonPath}.pattern"))
                {
                    totalMatches++;
                    if (matches.Count >= limit)
                    {
                        stoppedAt ??= node.Id;
                        continue;
                    }

                    var snippet = ExtractGrepSnippet(field.Value, span.Start, span.Start + span.Length, contextChars);
                    var payloadTokens = EstimateGrepMatchPayloadTokens(node, field.Name, snippet);
                    if (grep.MaxTokens is int budget && tokensUsed + payloadTokens > budget)
                    {
                        stoppedAt ??= node.Id;
                        continue;
                    }

                    matches.Add((JsonNode)BuildGrepMatchJson(node, field.Name, span, snippet));
                    tokensUsed += payloadTokens;
                }
            }
        }

        var truncated = matches.Count < totalMatches;
        var result = new JsonObject
        {
            ["searched"] = nodes.Count,
            ["count"] = totalMatches,
            ["returned"] = matches.Count,
            ["tokens_used"] = tokensUsed,
            ["within_budget"] = !truncated,
            ["truncated"] = truncated,
            ["matches"] = matches,
        };

        if (grep.MaxTokens is int budgetValue)
            result["tokens_budget"] = budgetValue;
        if (stoppedAt is int stoppedId)
            result["stopped_at"] = stoppedId;

        return result;
    }

    private static int ValidateGrepLimit(int? value, string jsonPath)
    {
        var limit = value ?? DefaultGrepLimit;
        if (limit <= 0 || limit > MaxGrepLimit)
        {
            throw new LlmJsonApiResolveException(
                "invalid_limit",
                jsonPath,
                $"grep.limit РґРѕР»Р¶РµРЅ Р±С‹С‚СЊ РІ РґРёР°РїР°Р·РѕРЅРµ 1..{MaxGrepLimit}.");
        }
        return limit;
    }

    private static int ValidateGrepContextChars(int? value, string jsonPath)
    {
        var contextChars = value ?? DefaultGrepContextChars;
        if (contextChars < 0 || contextChars > MaxGrepContextChars)
        {
            throw new LlmJsonApiResolveException(
                "invalid_context_chars",
                jsonPath,
                $"grep.context_chars РґРѕР»Р¶РµРЅ Р±С‹С‚СЊ РІ РґРёР°РїР°Р·РѕРЅРµ 0..{MaxGrepContextChars}.");
        }
        return contextChars;
    }

    private static Regex BuildGrepRegex(LlmGrepOperation grep, string jsonPath)
    {
        var options = RegexOptions.CultureInvariant;
        if (!grep.CaseSensitive)
            options |= RegexOptions.IgnoreCase;

        try
        {
            return new Regex(grep.Pattern, options, GrepRegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new LlmJsonApiResolveException(
                "invalid_grep_pattern",
                jsonPath,
                ex.Message,
                new JsonObject { ["pattern"] = grep.Pattern });
        }
    }

    private static IEnumerable<GrepField> EnumerateGrepFields(LlmResolvedNode node, GrepFields fields)
    {
        if (fields.Title)
            yield return new GrepField("title", node.Node.Title);
        if (fields.Text)
            yield return new GrepField("text", node.Node.Text);
    }

    private static IReadOnlyList<GrepSpan> FindGrepSpans(
        string text,
        LlmGrepOperation grep,
        Regex? regex,
        string jsonPath)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<GrepSpan>();

        if (regex is null)
            return FindLiteralGrepSpans(text, grep.Pattern, grep.CaseSensitive);

        try
        {
            return regex.Matches(text)
                .Cast<Match>()
                .Where(match => match.Success && match.Length > 0)
                .Select(match => new GrepSpan(match.Index, match.Length))
                .ToArray();
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new LlmJsonApiResolveException(
                "grep_timeout",
                jsonPath,
                ex.Message,
                new JsonObject
                {
                    ["pattern"] = grep.Pattern,
                    ["timeout_ms"] = (int)GrepRegexTimeout.TotalMilliseconds,
                });
        }
    }

    private static IReadOnlyList<GrepSpan> FindLiteralGrepSpans(string text, string pattern, bool caseSensitive)
    {
        var result = new List<GrepSpan>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index <= text.Length)
        {
            var found = text.IndexOf(pattern, index, comparison);
            if (found < 0)
                break;

            result.Add(new GrepSpan(found, pattern.Length));
            index = found + pattern.Length;
        }
        return result;
    }

    private static string ExtractGrepSnippet(string text, int matchStart, int matchEnd, int contextChars)
    {
        matchStart = Math.Clamp(matchStart, 0, text.Length);
        matchEnd = Math.Clamp(matchEnd, matchStart, text.Length);

        var start = Math.Max(0, matchStart - contextChars);
        var end = Math.Min(text.Length, matchEnd + contextChars);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = end < text.Length ? "..." : string.Empty;
        return prefix + text.Substring(start, end - start) + suffix;
    }

    private static int EstimateGrepMatchPayloadTokens(LlmResolvedNode node, string field, string snippet)
    {
        var payload = $"{node.Id} {node.Path} {node.Node.TypeName} {node.Node.Title} {field} {snippet}";
        return Math.Max(1, TokenCounter.Count(payload));
    }

    private static JsonObject BuildGrepMatchJson(
        LlmResolvedNode node,
        string field,
        GrepSpan span,
        string snippet) =>
        new()
        {
            ["id"] = node.Id,
            ["path"] = node.Path,
            ["coordinates"] = new JsonObject
            {
                ["type"] = node.Node.TypeName,
            },
            ["title"] = node.Node.Title,
            ["field"] = field,
            ["start"] = span.Start,
            ["length"] = span.Length,
            ["snippet"] = snippet,
            ["tokens"] = TokenCounter.CountNode(node.Node),
        };

    private JsonObject BuildNodeJson(LlmResolvedNode resolved, int tokens, QueryInclude include)
    {
        var result = new JsonObject
        {
            ["id"] = resolved.Id,
            ["path"] = resolved.Path,
            ["coordinates"] = BuildCoordinates(resolved, include.Coordinates),
            ["title"] = resolved.Node.Title,
            ["tokens"] = tokens,
        };

        if (include.Text)
            result["text"] = resolved.Node.Text;
        if (include.Relations)
            result["relations"] = BuildRelations(resolved);
        if (include.Ancestors)
            result["ancestors"] = BuildAncestors(resolved);
        if (include.Children)
            result["children"] = BuildChildren(resolved);
        if (include.TypeContract)
            result["type_contract"] = BuildTypeContract(resolved.Node.TypeName);

        return result;
    }

    private JsonObject BuildCoordinates(LlmResolvedNode resolved, bool full)
    {
        var result = new JsonObject
        {
            ["type"] = resolved.Node.TypeName,
        };

        if (!full)
            return result;

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

    private JsonArray BuildAncestors(LlmResolvedNode resolved)
    {
        var ancestors = new List<LlmResolvedNode>();
        var current = resolved.Node;
        while (current.ParentId is int parentId && parentId != Node.RootId)
        {
            var parent = TryResolveNode(parentId);
            if (parent is null)
                break;
            ancestors.Add(parent);
            current = parent.Node;
        }

        ancestors.Reverse();
        var result = new JsonArray();
        foreach (var ancestor in ancestors)
            result.Add((JsonNode)BuildRelationNode(ancestor));
        return result;
    }

    private JsonArray BuildChildren(LlmResolvedNode resolved)
    {
        var result = new JsonArray();
        foreach (var child in _graph.GetScopeChildren(resolved.Id, Node.PathRefName)
                     .Select(TryResolveNode)
                     .Where(n => n is not null)
                     .Cast<LlmResolvedNode>()
                     .OrderBy(n => n.Path, StringComparer.Ordinal)
                     .ThenBy(n => n.Id))
        {
            result.Add((JsonNode)BuildRelationNode(child));
        }
        return result;
    }

    private JsonObject? BuildTypeContract(string typeName)
    {
        if (!_typesByName.TryGetValue(typeName, out var type))
            return null;

        var refs = new JsonArray();
        foreach (var rd in type.OutRefs.OrderBy(rd => rd.Name, StringComparer.Ordinal))
            refs.Add((JsonNode)BuildTypeRefContract(rd));

        return new JsonObject
        {
            ["name"] = type.Name,
            ["description"] = type.Description,
            ["text_required"] = type.TextRequired,
            ["out_refs"] = refs,
        };
    }

    private static JsonObject BuildTypeRefContract(RefDef rd)
    {
        return new JsonObject
        {
            ["name"] = rd.Name,
            ["tree"] = rd.Tree,
            ["cardinality"] = rd.Tree is null ? FormatCardinality(rd.Cardinality) : null,
            ["required"] = rd.Tree is null ? rd.Required : null,
            ["target_types"] = BuildStringArray(rd.TargetTypes),
            ["description"] = rd.Description,
        };
    }

    private static string FormatCardinality(Cardinality cardinality) =>
        cardinality == Cardinality.One ? "one" : "many";

    private static JsonArray BuildStringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add((JsonNode?)JsonValue.Create(value));
        return result;
    }

    private static JsonObject BuildRelationNode(LlmResolvedNode resolved) =>
        new()
        {
            ["id"] = resolved.Id,
            ["path"] = resolved.Path,
            ["coordinates"] = new JsonObject
            {
                ["type"] = resolved.Node.TypeName,
            },
            ["title"] = resolved.Node.Title,
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
        if (include.Ancestors)
            tokens += BuildAncestors(resolved).Count;
        if (include.Children)
            tokens += BuildChildren(resolved).Count;
        if (include.TypeContract && _typesByName.TryGetValue(resolved.Node.TypeName, out var type))
            tokens += TokenCounter.Count(type.Name) + type.OutRefs.Count * 3;

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
                ["message"] = ex.Message,
                ["path"] = ex.Path,
                ["details"] = ex.Details?.DeepClone(),
            },
        };

    private sealed record GrepField(string Name, string Value);

    private sealed record GrepSpan(int Start, int Length);

    private sealed record GrepFields(bool Title, bool Text)
    {
        public static GrepFields Parse(string value, string jsonPath) =>
            value switch
            {
                "title" => new GrepFields(true, false),
                "text" => new GrepFields(false, true),
                "both" => new GrepFields(true, true),
                _ => throw new LlmJsonApiResolveException(
                    "invalid_grep_in",
                    jsonPath,
                    $"grep.in СЃРѕРґРµСЂР¶РёС‚ РЅРµРёР·РІРµСЃС‚РЅРѕРµ Р·РЅР°С‡РµРЅРёРµ '{value}'.",
                    new JsonObject { ["in"] = value }),
            };
    }

    private sealed record QueryItem(LlmResolvedNode Node, int Tokens, int PayloadTokens);

    private sealed record QueryInclude(
        bool Text,
        bool Relations,
        bool Coordinates,
        bool Ancestors,
        bool Children,
        bool TypeContract)
    {
        public static QueryInclude Empty { get; } = new(false, false, false, false, false, false);

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
                set.Contains("coordinates"),
                set.Contains("ancestors"),
                set.Contains("children"),
                set.Contains("type_contract"));
        }

        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "text",
            "relations",
            "coordinates",
            "ancestors",
            "children",
            "type_contract",
        };
    }
}
