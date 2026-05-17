using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

public sealed record LlmResolvedCoordinate(
    string Name,
    string Value,
    int Id,
    string Path,
    Node Node);

public sealed record LlmResolvedCoordinates(
    TypeDefinition? Type,
    IReadOnlyList<LlmResolvedCoordinate> Classifiers)
{
    public string? TypeName => Type?.Name;

    public IReadOnlyList<TreeFilter> ToTreeFilters() =>
        Classifiers
            .Select(c => new TreeFilter(c.Name, c.Id))
            .ToArray();
}

/// <summary>
/// Резолвит string-only coordinates LLM JSON API: coordinates.type проверяется по
/// текущей Схеме, остальные координаты трактуются как пути в classifier trees.
/// </summary>
public sealed class LlmJsonApiCoordinateResolver
{
    private static readonly char[] CoordinateSeparators = { '/' };
    private static readonly TimeSpan MatchRegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly GraphModel _graph;
    private readonly Dictionary<string, TypeDefinition> _typesByName;
    private readonly HashSet<string> _treeNames;

    public LlmJsonApiCoordinateResolver(GraphModel graph, SchemaDocument schema)
    {
        _graph = graph;
        _typesByName = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _treeNames = schema.Trees.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    }

    public LlmResolvedCoordinates Resolve(
        LlmCoordinates coordinates,
        LlmRequestDefaults defaults,
        string jsonPath)
    {
        var values = MergeCoordinateValues(coordinates, defaults, jsonPath);
        if (values.Count == 0)
            return new LlmResolvedCoordinates(null, Array.Empty<LlmResolvedCoordinate>());

        TypeDefinition? type = null;
        var classifiers = new List<LlmResolvedCoordinate>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value.Value))
                throw UnknownCoordinate(
                    value.JsonPath,
                    $"Coordinate '{value.Name}' не должен быть пустым.",
                    value.Name,
                    value.Value);

            var normalizedValue = value.Value.Trim();
            if (string.Equals(value.Name, "type", StringComparison.Ordinal))
            {
                if (!_typesByName.TryGetValue(normalizedValue, out type))
                    throw UnknownCoordinate(
                        value.JsonPath,
                        $"coordinates.type='{normalizedValue}' не найден в текущей Схеме.",
                        value.Name,
                        normalizedValue);
                continue;
            }

            if (string.Equals(value.Name, Node.PathRefName, StringComparison.Ordinal) ||
                !_treeNames.Contains(value.Name))
            {
                throw UnknownCoordinate(
                    value.JsonPath,
                    $"Coordinate '{value.Name}' не является classifier tree текущей Схемы.",
                    value.Name,
                    normalizedValue);
            }

            classifiers.Add(ResolveClassifierCoordinate(value.Name, normalizedValue, value.JsonPath));
        }

        return new LlmResolvedCoordinates(
            type,
            classifiers
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<LlmResolvedNode> FilterNodes(
        IReadOnlyList<LlmResolvedNode> nodes,
        LlmCoordinates coordinates,
        LlmRequestDefaults defaults,
        string jsonPath)
    {
        var resolved = Resolve(coordinates, defaults, jsonPath);
        return FilterNodes(nodes, resolved);
    }

    public IReadOnlyList<LlmResolvedNode> FilterNodes(
        IReadOnlyList<LlmResolvedNode> nodes,
        LlmResolvedCoordinates coordinates)
    {
        IEnumerable<LlmResolvedNode> source = nodes;
        if (coordinates.Type is not null)
            source = source.Where(n => string.Equals(n.Node.TypeName, coordinates.Type.Name, StringComparison.Ordinal));

        foreach (var classifier in coordinates.Classifiers)
        {
            var allowed = CollectScopeDescendants(classifier.Id, classifier.Name);
            source = source.Where(n =>
                _graph.GetScopeParent(n.Id, classifier.Name) is int parentId &&
                allowed.Contains(parentId));
        }

        return source.ToArray();
    }

    public IReadOnlyList<LlmResolvedNode> ResolveSelector(
        LlmSelector selector,
        LlmRequestDefaults defaults,
        LlmJsonApiPathResolver pathResolver,
        string jsonPath)
    {
        var hasCoordinates =
            defaults.Coordinates.Values.Count > 0 ||
            selector.Coordinates.Values.Count > 0;
        var hasMatch = selector.Match is not null;
        var candidates = selector.Path is not null
            ? pathResolver.ResolvePath(selector.Path, defaults, $"{jsonPath}.path")
            : hasCoordinates || hasMatch
                ? pathResolver.AllNodes
                : throw new LlmJsonApiResolveException(
                    "not_found",
                    jsonPath,
                    "Selector должен содержать path, coordinates или match.");

        var result = FilterNodes(candidates, selector.Coordinates, defaults, $"{jsonPath}.coordinates");
        result = FilterByMatch(result, selector.Match, $"{jsonPath}.match");
        if (result.Count == 0)
            throw new LlmJsonApiResolveException(
                "not_found",
                jsonPath,
                "Selector не нашел узлов после применения filters.");
        return result;
    }

    private static IReadOnlyList<LlmResolvedNode> FilterByMatch(
        IReadOnlyList<LlmResolvedNode> nodes,
        LlmSelectorMatch? match,
        string jsonPath)
    {
        if (match is null)
            return nodes;

        if (string.IsNullOrWhiteSpace(match.Regex))
        {
            throw new LlmJsonApiResolveException(
                "invalid_match_regex",
                $"{jsonPath}.regex",
                "select.match.regex должен быть непустой строкой.");
        }

        var fields = MatchFields.Parse(match.Fields, $"{jsonPath}.fields");
        var regex = BuildMatchRegex(match, $"{jsonPath}.regex");
        try
        {
            return nodes
                .Where(node =>
                    (fields.Title && regex.IsMatch(node.Node.Title)) ||
                    (fields.Text && regex.IsMatch(node.Node.Text)))
                .ToArray();
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new LlmJsonApiResolveException(
                "match_timeout",
                $"{jsonPath}.regex",
                ex.Message,
                new JsonObject
                {
                    ["regex"] = match.Regex,
                    ["timeout_ms"] = (int)MatchRegexTimeout.TotalMilliseconds,
                });
        }
    }

    private static Regex BuildMatchRegex(LlmSelectorMatch match, string jsonPath)
    {
        var options = RegexOptions.CultureInvariant;
        if (!match.CaseSensitive)
            options |= RegexOptions.IgnoreCase;

        try
        {
            return new Regex(match.Regex, options, MatchRegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new LlmJsonApiResolveException(
                "invalid_match_regex",
                jsonPath,
                ex.Message,
                new JsonObject { ["regex"] = match.Regex });
        }
    }

    private LlmResolvedCoordinate ResolveClassifierCoordinate(
        string name,
        string value,
        string jsonPath)
    {
        var segments = SplitCoordinatePath(value);
        if (segments.Count == 0)
            throw UnknownCoordinate(jsonPath, $"Coordinate '{name}' не должен быть пустым.", name, value);

        var parentId = Node.RootId;
        Node? current = null;
        var resolvedSegments = new List<string>(segments.Count);
        foreach (var segment in segments)
        {
            var matches = _graph.GetScopeChildren(parentId, name)
                .Select(id => _graph.GetById(id))
                .Where(node => node is not null)
                .Cast<Node>()
                .Where(node => string.Equals(node.Title, segment, StringComparison.Ordinal))
                .OrderBy(node => node.Id)
                .ToArray();

            if (matches.Length == 0)
                throw UnknownCoordinate(
                    jsonPath,
                    $"Coordinate '{name}={value}' не найден в classifier tree '{name}'.",
                    name,
                    value);
            if (matches.Length > 1)
                throw new LlmJsonApiResolveException(
                    "ambiguous_selector",
                    jsonPath,
                    $"Coordinate '{name}={value}' нашел несколько веток classifier tree.");

            current = matches[0];
            parentId = current.Id;
            resolvedSegments.Add(current.Title);
        }

        return new LlmResolvedCoordinate(
            name,
            value,
            current!.Id,
            string.Join('/', resolvedSegments),
            current);
    }

    private HashSet<int> CollectScopeDescendants(int rootId, string scope)
    {
        var result = new HashSet<int> { rootId };
        var stack = new Stack<int>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            foreach (var childId in _graph.GetScopeChildren(id, scope))
            {
                if (result.Add(childId))
                    stack.Push(childId);
            }
        }
        return result;
    }

    private static IReadOnlyList<CoordinateValue> MergeCoordinateValues(
        LlmCoordinates coordinates,
        LlmRequestDefaults defaults,
        string jsonPath)
    {
        var merged = new Dictionary<string, CoordinateValue>(StringComparer.Ordinal);
        foreach (var (name, value) in defaults.Coordinates.Values)
            merged[name] = new CoordinateValue(name, value, $"$.defaults.coordinates.{name}");
        foreach (var (name, value) in coordinates.Values)
            merged[name] = new CoordinateValue(name, value, $"{jsonPath}.{name}");
        return merged.Values.ToArray();
    }

    private static IReadOnlyList<string> SplitCoordinatePath(string value) =>
        value
            .Split(CoordinateSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment.Length > 0)
            .ToArray();

    private static LlmJsonApiResolveException UnknownCoordinate(
        string jsonPath,
        string message,
        string name,
        string value) =>
        new(
            "unknown_coordinate",
            jsonPath,
            message,
            new JsonObject
            {
                ["coordinate"] = name,
                ["value"] = value,
            });

    private sealed record MatchFields(bool Title, bool Text)
    {
        public static MatchFields Parse(IReadOnlyList<string> values, string jsonPath)
        {
            var set = values.Count == 0
                ? new HashSet<string>(new[] { "title", "text" }, StringComparer.Ordinal)
                : values.ToHashSet(StringComparer.Ordinal);

            foreach (var value in set)
            {
                if (value is not ("title" or "text"))
                {
                    throw new LlmJsonApiResolveException(
                        "invalid_match_fields",
                        jsonPath,
                        $"select.match.fields содержит неизвестное значение '{value}'.",
                        new JsonObject { ["field"] = value });
                }
            }

            return new MatchFields(set.Contains("title"), set.Contains("text"));
        }
    }

    private sealed record CoordinateValue(string Name, string Value, string JsonPath);
}
