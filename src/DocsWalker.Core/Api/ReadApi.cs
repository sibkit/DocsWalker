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
/// Связь в форме, удобной для read-API: имя связи и id противоположного узла.
/// «Противоположный узел» = цель для исходящих, источник для входящих;
/// направление определяется тем, в какой коллекции (<see cref="RefSet.Out"/>
/// или <see cref="RefSet.In"/>) лежит запись.
/// </summary>
public sealed record RefView(string Name, int TargetId);

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
    /// Полное поддерево узла в указанном дереве (tree-scope). По умолчанию <c>"path"</c> —
    /// эквивалент <see cref="GetByPath"/> по id, но без разрешения текстового пути.
    /// Для <c>tree="path"</c> используется обратный проход path-родителей (<see cref="Graph.GetChildren(int)"/>);
    /// для прочих scope'ов — обход через scope-индекс <see cref="Graph.GetScopeChildren"/>.
    /// </summary>
    public NodeSubtree GetSubtree(int rootId, string tree = Node.PathRefName)
    {
        ValidateTree(tree);
        var node = _graph.GetById(rootId)
            ?? throw new ReadApiException("node_not_found", $"Узел с id={rootId} не найден.");
        return BuildSubtreeByScope(node, tree);
    }

    private NodeSubtree BuildSubtreeByScope(Node node, string tree)
    {
        IReadOnlyList<Node> children;
        if (string.Equals(tree, Node.PathRefName, StringComparison.Ordinal))
        {
            children = _graph.GetChildren(node.Id);
        }
        else
        {
            var ids = _graph.GetScopeChildren(node.Id, tree);
            var list = new List<Node>(ids.Count);
            foreach (var id in ids)
            {
                if (_graph.GetById(id) is { } ch) list.Add(ch);
            }
            children = list;
        }
        var sub = new List<NodeSubtree>(children.Count);
        foreach (var c in children) sub.Add(BuildSubtreeByScope(c, tree));
        return new NodeSubtree(node, sub);
    }

    /// <summary>
    /// Цепочка родителей узла в указанном дереве (tree-scope), от ближайшего родителя
    /// до корня дерева включительно. Для <c>tree="path"</c> — путь до root (id=0).
    /// Если узел сам — корень scope (нет scope-ref), возвращает пустой список.
    /// </summary>
    public IReadOnlyList<Node> GetAncestors(int nodeId, string tree = Node.PathRefName)
    {
        ValidateTree(tree);
        if (_graph.GetById(nodeId) is null)
            throw new ReadApiException("node_not_found", $"Узел с id={nodeId} не найден.");

        var result = new List<Node>();
        int? cursor = nodeId;
        var safety = _graph.NodeCount + 1;
        while (safety-- > 0)
        {
            int? parentId;
            if (string.Equals(tree, Node.PathRefName, StringComparison.Ordinal))
            {
                if (cursor is not int cid) break;
                if (cid == Node.RootId) break;
                var c = _graph.GetById(cid);
                parentId = c?.ParentId;
            }
            else
            {
                if (cursor is not int cid) break;
                parentId = _graph.GetScopeParent(cid, tree);
            }

            if (parentId is not int pid) break;
            if (pid == Node.RootId)
            {
                // root присутствует в графе только концептуально (id=0); в _byId его нет —
                // не добавляем фантомный узел в результат, ancestors-цепочка path заканчивается прямо над ним.
                break;
            }
            var parent = _graph.GetById(pid);
            if (parent is null) break;
            result.Add(parent);
            cursor = pid;
        }
        return result;
    }

    private void ValidateTree(string tree)
    {
        if (string.IsNullOrEmpty(tree))
            throw new ReadApiException(
                "invalid_parameter",
                "Параметр 'tree' не должен быть пустым.",
                "Передай '--tree=<scope>' с именем дерева из get-schema.trees, либо опусти для дефолтного 'path'.");
        if (!_graph.KnownTrees.Contains(tree))
            throw new ReadApiException(
                "unknown_tree",
                $"Неизвестное дерево '{tree}'.",
                $"Доступные tree-scope'ы: {string.Join(", ", _graph.KnownTrees.OrderBy(x => x, StringComparer.Ordinal))}.");
    }

    /// <summary>
    /// Связи узла в обе стороны. Опциональный фильтр <paramref name="name"/>
    /// применяется к именам связей (одинаково для in и out). Включает связь
    /// <c>path</c> на равных правах с прочими.
    /// </summary>
    public RefSet GetRefs(int id, string? name = null)
    {
        if (_graph.GetById(id) is null)
            throw new ReadApiException("node_not_found", $"Узел с id={id} не найден.");

        var outRefs = _graph.GetOutRefs(id);
        var inRefs = _graph.GetInRefs(id);

        var outViews = new List<RefView>(outRefs.Count);
        foreach (var r in outRefs)
        {
            if (name is not null && !string.Equals(r.Name, name, StringComparison.Ordinal)) continue;
            outViews.Add(new RefView(r.Name, r.TargetId));
        }

        var inViews = new List<RefView>(inRefs.Count);
        foreach (var r in inRefs)
        {
            if (name is not null && !string.Equals(r.Name, name, StringComparison.Ordinal)) continue;
            inViews.Add(new RefView(r.Name, r.SourceId));
        }

        return new RefSet(inViews, outViews);
    }

    /// <summary>
    /// Только входящие связи; эквивалент <see cref="GetRefs"/>, но <see cref="RefSet.Out"/> пустой.
    /// </summary>
    public RefSet GetInRefs(int id, string? name = null)
    {
        var full = GetRefs(id, name);
        return new RefSet(full.In, Array.Empty<RefView>());
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
    /// Полнотекстовый поиск (case-insensitive substring) по полю <see cref="Node.Text"/>.
    /// Title и имена связей не индексируются.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query)
    {
        if (string.IsNullOrEmpty(query))
            throw new ReadApiException("invalid_query", "Параметр query не должен быть пустым.");

        var hits = new List<SearchHit>();
        foreach (var node in _graph.ById.Values.OrderBy(n => n.Id))
        {
            if (string.IsNullOrEmpty(node.Text)) continue;
            if (!Contains(node.Text, query)) continue;
            hits.Add(new SearchHit(node.Id, node.Title, node.TypeName, new[] { node.Text }));
        }
        return hits;
    }

    private static bool Contains(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

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
