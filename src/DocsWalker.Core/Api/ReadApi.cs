using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Ошибка чтения, выбрасываемая операциями <see cref="ReadApi"/>: запрашиваемый
/// узел не существует, путь не разрешается, фильтр имеет некорректное значение и т. п.
/// Соответствует структурированной ошибке CLI (code + message).
/// </summary>
public sealed class ReadApiException : Exception
{
    public string Code { get; }
    public string? Hint { get; }

    public ReadApiException(string code, string message, string? hint = null) : base(message)
    {
        Code = code;
        Hint = hint;
    }
}

public sealed record DocumentSummary(int Id, string Title);

/// <summary>
/// Элемент карты документации: узел и его дети по path-связям.
/// Дети упорядочены так же, как в <see cref="Graph.GetChildren"/>.
/// </summary>
public sealed record MapNode(
    int Id,
    string Title,
    string TypeName,
    IReadOnlyList<MapNode> Children);

/// <summary>
/// Связь в форме, удобной для read-API: с явно указанным направлением,
/// id противоположного узла и его человекочитаемым путём.
/// </summary>
public sealed record RefView(
    string Direction,
    string TypeName,
    RefOrigin Origin,
    int OtherId,
    string OtherTitle,
    string OtherPath);

public sealed record RefSet(
    IReadOnlyList<RefView> In,
    IReadOnlyList<RefView> Out);

/// <summary>
/// Полное поддерево узла: сам узел плюс дочерние поддеревья (path-связи).
/// </summary>
public sealed record NodeSubtree(Node Node, IReadOnlyList<NodeSubtree> Children);

public sealed record SearchHit(
    int Id,
    string Title,
    string TypeName,
    IReadOnlyList<string> Fragments);

/// <summary>
/// Read-API DocsWalker (см. docs/DocsWalker.yml/«Операции чтения»).
/// Все операции read-only, без побочных эффектов; работают по уже загруженному
/// <see cref="Graph"/>. Состояние не кэшируется внутри — каждый вызов идёт по графу
/// напрямую. Ошибки разрешения id/path/фильтров — через <see cref="ReadApiException"/>.
/// </summary>
public sealed class ReadApi
{
    private readonly GraphModel _graph;

    public ReadApi(GraphModel graph)
    {
        _graph = graph;
    }

    public IReadOnlyList<DocumentSummary> ListDocuments()
    {
        var docs = _graph.Documents;
        var result = new List<DocumentSummary>(docs.Count);
        foreach (var d in docs.OrderBy(d => d.Title, StringComparer.Ordinal))
        {
            result.Add(new DocumentSummary(d.Id, d.Title));
        }
        return result;
    }

    public IReadOnlyList<MapNode> GetMap()
    {
        var docs = _graph.Documents;
        var result = new List<MapNode>(docs.Count);
        foreach (var d in docs.OrderBy(d => d.Title, StringComparer.Ordinal))
        {
            result.Add(BuildMapNode(d));
        }
        return result;
    }

    private MapNode BuildMapNode(Node node)
    {
        var children = _graph.GetChildren(node.Id);
        var mapped = new List<MapNode>(children.Count);
        foreach (var c in children) mapped.Add(BuildMapNode(c));
        return new MapNode(node.Id, node.Title, node.TypeName, mapped);
    }

    /// <summary>
    /// Возвращает полные узлы по списку id в том же порядке, что был передан.
    /// Бросает <see cref="ReadApiException"/> с кодом "node_not_found" для первого
    /// id, которого нет в графе.
    /// </summary>
    public IReadOnlyList<Node> GetNodes(IReadOnlyList<int> ids)
    {
        var result = new List<Node>(ids.Count);
        foreach (var id in ids)
        {
            var node = _graph.GetById(id) ?? throw new ReadApiException(
                "node_not_found", $"Узел с id={id} не найден.");
            result.Add(node);
        }
        return result;
    }

    /// <summary>
    /// Поддерево по человекочитаемому пути вида "Документ/Раздел/Подраздел".
    /// Разделитель — '/'. Имя документа может содержать '/' (если файл лежит в
    /// подкаталоге docs/) — берётся самый длинный префикс пути, совпадающий
    /// с title какого-либо документа.
    /// </summary>
    public NodeSubtree GetByPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ReadApiException("invalid_path", "Путь не должен быть пустым.");

        var segments = path.Split('/');
        Node? doc = null;
        int prefixLen = 0;
        for (int k = segments.Length; k >= 1; k--)
        {
            var candidate = string.Join('/', segments, 0, k);
            var match = _graph.GetDocumentByTitle(candidate);
            if (match is not null)
            {
                doc = match;
                prefixLen = k;
                break;
            }
        }
        if (doc is null)
            throw new ReadApiException(
                "path_not_found",
                $"В docs/ нет документа, совпадающего с префиксом пути '{path}'.");

        var current = doc;
        for (int i = prefixLen; i < segments.Length; i++)
        {
            var title = segments[i];
            Node? next = null;
            foreach (var child in _graph.GetChildren(current.Id))
            {
                if (string.Equals(child.Title, title, StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
            }
            if (next is null)
                throw new ReadApiException(
                    "path_not_found",
                    $"У узла '{current.Title}' (id={current.Id}) нет дочернего узла с title='{title}'.");
            current = next;
        }

        return BuildSubtree(current);
    }

    private NodeSubtree BuildSubtree(Node node)
    {
        var children = _graph.GetChildren(node.Id);
        var sub = new List<NodeSubtree>(children.Count);
        foreach (var c in children) sub.Add(BuildSubtree(c));
        return new NodeSubtree(node, sub);
    }

    /// <summary>
    /// Все связи узла в обе стороны. Фильтры по type и origin применяются к каждой
    /// стороне независимо. Для каждой связи строится <see cref="RefView"/> с
    /// id/title/path противоположного узла.
    /// </summary>
    public RefSet GetRefs(int id, string? type = null, RefOrigin? origin = null)
    {
        var node = _graph.GetById(id) ?? throw new ReadApiException(
            "node_not_found", $"Узел с id={id} не найден.");

        var outRefs = _graph.GetOutRefs(id);
        var inRefs = _graph.GetInRefs(id);

        var outViews = new List<RefView>();
        foreach (var r in outRefs)
        {
            if (!Match(r, type, origin)) continue;
            outViews.Add(BuildView("out", r, otherId: r.ToId));
        }

        var inViews = new List<RefView>();
        foreach (var r in inRefs)
        {
            if (!Match(r, type, origin)) continue;
            inViews.Add(BuildView("in", r, otherId: r.FromId));
        }

        return new RefSet(inViews, outViews);
    }

    /// <summary>
    /// Только входящие связи; эквивалент <see cref="GetRefs"/> с direction=in.
    /// </summary>
    public RefSet GetInRefs(int id, string? type = null, RefOrigin? origin = null)
    {
        var full = GetRefs(id, type, origin);
        return new RefSet(full.In, Array.Empty<RefView>());
    }

    private static bool Match(Ref r, string? type, RefOrigin? origin)
    {
        if (type is not null && !string.Equals(r.TypeName, type, StringComparison.Ordinal)) return false;
        if (origin is RefOrigin o && r.Origin != o) return false;
        return true;
    }

    private RefView BuildView(string direction, Ref r, int otherId)
    {
        var other = _graph.GetById(otherId);
        var title = other?.Title ?? string.Empty;
        var path = other is null ? string.Empty : FormatPath(other);
        return new RefView(direction, r.TypeName, r.Origin, otherId, title, path);
    }

    /// <summary>
    /// Человекочитаемый путь от документа-корня до узла включительно, разделитель '/'.
    /// </summary>
    public string FormatPath(int id)
    {
        var node = _graph.GetById(id) ?? throw new ReadApiException(
            "node_not_found", $"Узел с id={id} не найден.");
        return FormatPath(node);
    }

    private string FormatPath(Node node)
    {
        var stack = new List<string>();
        var current = node;
        while (current is not null)
        {
            stack.Add(current.Title);
            current = current.ParentId is int pid ? _graph.GetById(pid) : null;
        }
        stack.Reverse();
        return string.Join('/', stack);
    }

    /// <summary>
    /// Полнотекстовый поиск (case-insensitive substring) по описаниям и блокам узлов.
    /// Title и структурные ключи не индексируются.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query)
    {
        if (string.IsNullOrEmpty(query))
            throw new ReadApiException("invalid_query", "Параметр query не должен быть пустым.");

        var hits = new List<SearchHit>();
        foreach (var node in _graph.ById.Values.OrderBy(n => n.Id))
        {
            var fragments = CollectMatches(node, query);
            if (fragments.Count == 0) continue;
            hits.Add(new SearchHit(node.Id, node.Title, node.TypeName, fragments));
        }
        return hits;
    }

    private static List<string> CollectMatches(Node node, string query)
    {
        var matches = new List<string>();

        if (node.InlineValue is not null && Contains(node.InlineValue, query))
            matches.Add(node.InlineValue);

        if (node.Fields is not null)
        {
            foreach (var f in node.Fields)
            {
                if (string.Equals(f.Name, "id", StringComparison.Ordinal)) continue;
                if (string.Equals(f.Name, "name", StringComparison.Ordinal)) continue;
                if (f.Scalar is not null && Contains(f.Scalar, query))
                    matches.Add(f.Scalar);
                if (f.Items is not null)
                {
                    foreach (var item in f.Items)
                        if (Contains(item, query)) matches.Add(item);
                }
            }
        }

        if (node.Blocks is not null)
        {
            foreach (var b in node.Blocks)
            {
                if (b is TextBlock tb)
                {
                    foreach (var item in tb.Items)
                        if (Contains(item, query)) matches.Add(item);
                }
                // ChildrenBlock и OutRefsBlock — структурные, не индексируются.
            }
        }

        return matches;
    }

    private static bool Contains(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Разбор имени origin из CLI-параметра: "explicit" / "system" / "default".
    /// </summary>
    public static bool TryParseOrigin(string raw, out RefOrigin origin)
    {
        switch (raw)
        {
            case "explicit": origin = RefOrigin.Explicit; return true;
            case "system":   origin = RefOrigin.System;   return true;
            case "default":  origin = RefOrigin.Default;  return true;
            default: origin = default; return false;
        }
    }

    public static string OriginToString(RefOrigin origin) => origin switch
    {
        RefOrigin.Explicit => "explicit",
        RefOrigin.System   => "system",
        RefOrigin.Default  => "default",
        _ => origin.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Полный прогон <see cref="Validator"/> на текущем графе без записи. Возвращает
    /// <see cref="ValidationResult"/> — успешное завершение операции (CLI exit=0)
    /// независимо от наличия ошибок графа; ошибки графа — данные ответа.
    /// Используется LLM-агентом как «sanity check» перед началом цепочки правок и для
    /// аудита после ручных изменений в YAML.
    /// </summary>
    public ValidationResult CheckIntegrity(MetaSchemaDocument meta, SchemaDocument schema, int? sequence = null)
    {
        var validator = new Validator(meta, schema);
        return validator.Validate(_graph, sequence);
    }
}
