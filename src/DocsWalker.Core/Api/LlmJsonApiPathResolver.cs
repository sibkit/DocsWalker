using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocsWalker.Core.Graph;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Ошибка resolve-слоя LLM-facing JSON API. Публичный envelope появится отдельным
/// шагом; здесь фиксируются стабильные LLM-коды и JSON pointer проблемного поля.
/// </summary>
public sealed class LlmJsonApiResolveException : Exception
{
    public string Code { get; }
    public string Path { get; }
    public JsonObject? Details { get; }

    public LlmJsonApiResolveException(string code, string path, string message, JsonObject? details = null)
        : base(message)
    {
        Code = code;
        Path = path;
        Details = details;
    }
}

public sealed record LlmResolvedPath(string Path, bool HasWildcard);

public sealed record LlmResolvedNode(int Id, string Path, Node Node);

/// <summary>
/// Резолвит LLM path поверх единственного v1 address-space: path-tree DocsWalker.
/// Координаты и alias намеренно остаются вне этого класса: они добавляются
/// следующими шагами стратегии.
/// </summary>
public sealed class LlmJsonApiPathResolver
{
    private static readonly char[] PathSeparators = { '/' };

    private readonly GraphModel _graph;
    private readonly Dictionary<string, List<Node>> _nodesByPath = new(StringComparer.Ordinal);
    private readonly List<LlmResolvedNode> _allNodes = new();
    private readonly List<string> _rootPathPrefixes = new();

    public LlmJsonApiPathResolver(GraphModel graph)
    {
        _graph = graph;
        BuildPathIndex();
    }

    public IReadOnlyList<LlmResolvedNode> AllNodes => _allNodes;

    public LlmResolvedPath NormalizePath(string path, LlmRequestDefaults defaults, string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw Error("not_found", jsonPath, "Path не должен быть пустым.");

        var normalized = NormalizeSlashes(path);
        var hasWildcard = ContainsWildcard(normalized);

        if (defaults.PathParent is null)
            return new LlmResolvedPath(normalized, hasWildcard);

        var parent = NormalizeSlashes(defaults.PathParent);
        if (ContainsWildcard(parent))
            throw Error(
                "ambiguous_path_scope",
                "$.defaults.path_parent",
                "defaults.path_parent должен быть exact path без wildcard.");

        if (IsFullPathLike(normalized))
            throw Error(
                "ambiguous_path_scope",
                jsonPath,
                "При defaults.path_parent поле path должно быть относительным путем внутри parent.");

        return new LlmResolvedPath(JoinPath(parent, normalized), hasWildcard);
    }

    public IReadOnlyList<LlmResolvedNode> ResolvePath(string path, LlmRequestDefaults defaults, string jsonPath)
    {
        var resolvedPath = NormalizePath(path, defaults, jsonPath);
        var matches = resolvedPath.HasWildcard
            ? ResolveWildcard(resolvedPath.Path)
            : ResolveExact(resolvedPath.Path);

        if (matches.Count == 0)
            throw Error(
                "not_found",
                jsonPath,
                $"Path '{resolvedPath.Path}' не найден.");

        return matches;
    }

    public LlmResolvedNode ResolveSinglePath(string path, LlmRequestDefaults defaults, string jsonPath)
    {
        var matches = ResolvePath(path, defaults, jsonPath);
        if (matches.Count > 1)
            throw Error(
                "ambiguous_selector",
                jsonPath,
                $"Path selector '{NormalizePath(path, defaults, jsonPath).Path}' нашел {matches.Count} узлов.");
        return matches[0];
    }

    public IReadOnlyList<LlmResolvedNode> ResolveSelectorPath(LlmSelector selector, LlmRequestDefaults defaults, string jsonPath)
    {
        if (selector.Path is null)
            throw Error(
                "not_found",
                jsonPath,
                "Selector без path пока не резолвится path-resolver-слоем.");

        return ResolvePath(selector.Path, defaults, $"{jsonPath}.path");
    }

    public LlmResolvedNode ResolveSingleSelectorPath(LlmSelector selector, LlmRequestDefaults defaults, string jsonPath)
    {
        var matches = ResolveSelectorPath(selector, defaults, jsonPath);
        if (matches.Count > 1)
            throw Error(
                "ambiguous_selector",
                jsonPath,
                $"Selector нашел {matches.Count} узлов, но операция требует одну цель.");
        return matches[0];
    }

    public LlmResolvedNode ResolveExistingId(int id, string jsonPath)
    {
        var node = _graph.GetById(id);
        if (node is null)
            throw Error("not_found", jsonPath, $"Узел с id={id} не найден.");

        return new LlmResolvedNode(id, FormatPath(node), node);
    }

    private IReadOnlyList<LlmResolvedNode> ResolveExact(string path)
    {
        if (!_nodesByPath.TryGetValue(path, out var nodes))
            return Array.Empty<LlmResolvedNode>();

        return nodes
            .Select(n => new LlmResolvedNode(n.Id, path, n))
            .OrderBy(n => n.Path, StringComparer.Ordinal)
            .ThenBy(n => n.Id)
            .ToArray();
    }

    private IReadOnlyList<LlmResolvedNode> ResolveWildcard(string pattern)
    {
        var regex = BuildWildcardRegex(pattern);
        return _allNodes
            .Where(n => regex.IsMatch(n.Path))
            .OrderBy(n => n.Path, StringComparer.Ordinal)
            .ThenBy(n => n.Id)
            .ToArray();
    }

    private void BuildPathIndex()
    {
        var rootChildren = _graph.GetScopeChildren(Node.RootId, Node.PathRefName)
            .Select(id => _graph.GetById(id))
            .Where(n => n is not null)
            .Cast<Node>()
            .OrderBy(n => n.Title, StringComparer.Ordinal)
            .ThenBy(n => n.Id)
            .ToArray();

        foreach (var child in rootChildren)
        {
            _rootPathPrefixes.Add(child.Title);
            AddSubtreeToIndex(child, child.Title);
        }

        _rootPathPrefixes.Sort((left, right) =>
        {
            var len = right.Length.CompareTo(left.Length);
            return len != 0 ? len : string.CompareOrdinal(left, right);
        });
    }

    private void AddSubtreeToIndex(Node node, string path)
    {
        if (!_nodesByPath.TryGetValue(path, out var nodes))
        {
            nodes = new List<Node>();
            _nodesByPath[path] = nodes;
        }
        nodes.Add(node);
        _allNodes.Add(new LlmResolvedNode(node.Id, path, node));

        var children = _graph.GetScopeChildren(node.Id, Node.PathRefName)
            .Select(id => _graph.GetById(id))
            .Where(n => n is not null)
            .Cast<Node>()
            .OrderBy(n => n.Title, StringComparer.Ordinal)
            .ThenBy(n => n.Id);

        foreach (var child in children)
            AddSubtreeToIndex(child, JoinPath(path, child.Title));
    }

    private bool IsFullPathLike(string path)
    {
        foreach (var prefix in _rootPathPrefixes)
        {
            if (string.Equals(path, prefix, StringComparison.Ordinal))
                return true;
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private string FormatPath(Node node)
    {
        var stack = new List<string>();
        var current = node;
        while (current.Id != Node.RootId)
        {
            stack.Add(current.Title);
            var parentId = current.ParentId;
            if (parentId is null)
                break;
            var parent = _graph.GetById(parentId.Value);
            if (parent is null)
                break;
            current = parent;
        }
        stack.Reverse();
        return string.Join('/', stack);
    }

    private static Regex BuildWildcardRegex(string pattern)
    {
        var segments = SplitPath(pattern);
        var expression = "^" + BuildWildcardExpression(segments, 0) + "$";
        return new Regex(expression, RegexOptions.CultureInvariant);
    }

    private static string BuildWildcardExpression(IReadOnlyList<string> segments, int index)
    {
        if (index >= segments.Count)
            return "";

        var segment = segments[index];
        if (segment == "**")
        {
            if (index == segments.Count - 1)
                return "(?:/[^/]+)*";

            var rest = BuildWildcardExpression(segments, index + 1);
            return "(?:/[^/]+)*" + rest;
        }

        var prefix = index == 0 ? "" : "/";
        return prefix + BuildSegmentExpression(segment) + BuildWildcardExpression(segments, index + 1);
    }

    private static string BuildSegmentExpression(string segment)
    {
        if (segment.Length == 0)
            return Regex.Escape(segment);

        var parts = segment.Split('*');
        if (parts.Length == 1)
            return Regex.Escape(segment);

        return string.Join("[^/]*", parts.Select(Regex.Escape));
    }

    private static string NormalizeSlashes(string path)
    {
        var segments = SplitPath(path);
        return string.Join('/', segments);
    }

    private static string JoinPath(string parent, string relative)
    {
        if (string.IsNullOrEmpty(parent))
            return relative;
        if (string.IsNullOrEmpty(relative))
            return parent;
        return parent + "/" + relative;
    }

    private static string[] SplitPath(string path) =>
        path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ContainsWildcard(string path) =>
        path.Contains('*', StringComparison.Ordinal);

    private static LlmJsonApiResolveException Error(string code, string path, string message) =>
        new(code, path, message);
}
