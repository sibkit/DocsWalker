using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Tokens;
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
/// Связи узла, сгруппированные по имени связи: для каждого имени — отсортированный
/// по возрастанию список id противоположных узлов («цель» для исходящих,
/// «источник» для входящих). Совпадает по форме с <c>out_refs</c> в get-nodes
/// и describe-type — единый контракт «связь → список целей» по всему API.
/// </summary>
public sealed record RefSet(
    IReadOnlyDictionary<string, IReadOnlyList<int>> In,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Out);

/// <summary>
/// Полное поддерево узла: сам узел плюс дочерние поддеревья (по выбранному tree-scope).
/// <see cref="Tokens"/> — BPE-счёт самого узла (с учётом whitelist <see cref="ReadApi.GetTree(int, string, int?)"/>:
/// если <c>fields</c> отрезает text/out_refs, эти куски не попадают и в подсчёт);
/// <see cref="SubtreeTokens"/> — агрегат по поддереву с учётом ограничения <c>depth</c>.
/// </summary>
public sealed record NodeSubtree(
    Node Node,
    int Tokens,
    int SubtreeTokens,
    IReadOnlyList<NodeSubtree> Children);

public sealed record SearchHit(
    int Id,
    string Title,
    string TypeName,
    IReadOnlyList<string> Fragments);

/// <summary>
/// FS-агностичное описание одного типа узла из Схемы. В отличие от <see cref="TypeDefinition"/>,
/// не выдаёт <c>title_source</c> наружу: это контракт «движок ↔ docs/», LLM его не должна знать.
/// Возвращается операцией <c>describe-type</c>; экономит токены LLM по сравнению с
/// полным <c>get-schema</c>.
/// </summary>
public sealed record TypeDescription(
    string Name,
    string? Description,
    bool TextRequired,
    IReadOnlyList<TypeRefDescription> OutRefs);

/// <summary>
/// Описание одной исходящей связи в DTO <see cref="TypeDescription"/>.
/// Для tree-refs <see cref="Cardinality"/> и <see cref="Required"/> равны <c>null</c>
/// (подразумеваются <c>one</c>+<c>true</c> по контракту мета-схемы), для остальных —
/// заполнены реальными значениями.
/// </summary>
public sealed record TypeRefDescription(
    string Name,
    string? Tree,
    Cardinality? Cardinality,
    bool? Required,
    IReadOnlyList<string> TargetTypes,
    string? Description);

/// <summary>
/// Read-API DocsWalker (см. docs/DocsWalker.yml/«Операции чтения»).
/// Все операции read-only, без побочных эффектов; работают по уже загруженному
/// <see cref="Graph"/>. Состояние не кэшируется внутри — каждый вызов идёт по графу
/// напрямую. Ошибки разрешения id/path/фильтров — через <see cref="ReadApiException"/>.
/// </summary>
public sealed class ReadApi
{
    private readonly GraphModel _graph;
    private readonly SchemaDocument? _schema;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _autoIncludeRefsByType;

    public ReadApi(GraphModel graph) : this(graph, schema: null)
    {
    }

    /// <summary>
    /// <paramref name="schema"/> — опционально. Передана → read-команды могут
    /// строить транзитивный auto-include (#340) через
    /// <see cref="CollectAutoIncludes"/>. Не передана → auto-include выключен,
    /// API не зависит от Схемы (используется в тестах и в местах, где Схема
    /// ещё не загружена).
    /// </summary>
    public ReadApi(GraphModel graph, SchemaDocument? schema)
    {
        _graph = graph;
        _schema = schema;
        _autoIncludeRefsByType = BuildAutoIncludeIndex(schema);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildAutoIncludeIndex(SchemaDocument? schema)
    {
        if (schema is null) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var index = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
        {
            List<string>? names = null;
            foreach (var rd in t.OutRefs)
            {
                if (!rd.IsAutoInclude) continue;
                names ??= new List<string>();
                names.Add(rd.Name);
            }
            if (names is not null) index[t.Name] = names;
        }
        return index;
    }

    /// <summary>
    /// Транзитивный обход auto-include-связей (#340) от <paramref name="seeds"/>.
    /// Возвращает плоский список целей в порядке BFS-открытия; сами seed-узлы
    /// в результат не входят. Цикл-защита: каждый id посещается не более одного
    /// раза в одном вызове. Если Схема не была передана в <see cref="ReadApi(Graph,SchemaDocument?)"/>,
    /// возвращает пустой список.
    /// </summary>
    public IReadOnlyList<Node> CollectAutoIncludes(IReadOnlyList<Node> seeds)
    {
        if (_autoIncludeRefsByType.Count == 0 || seeds.Count == 0)
            return Array.Empty<Node>();

        var visited = new HashSet<int>();
        foreach (var s in seeds) visited.Add(s.Id);

        var queue = new Queue<Node>(seeds);
        var collected = new List<Node>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!_autoIncludeRefsByType.TryGetValue(node.TypeName, out var refNames)) continue;
            foreach (var refName in refNames)
            {
                if (!node.OutRefs.TryGetValue(refName, out var targetIds)) continue;
                foreach (var tid in targetIds)
                {
                    if (!visited.Add(tid)) continue;
                    var target = _graph.GetById(tid);
                    if (target is null) continue;
                    collected.Add(target);
                    queue.Enqueue(target);
                }
            }
        }
        return collected;
    }

    /// <summary>
    /// Удобный аналог <see cref="CollectAutoIncludes(IReadOnlyList{Node})"/> для subtree:
    /// заполняет seed-список всеми узлами поддерева (root + transitive children) и
    /// делегирует. Полезен для get-tree / get-by-path.
    /// </summary>
    public IReadOnlyList<Node> CollectAutoIncludes(NodeSubtree subtree)
    {
        var seeds = new List<Node>();
        FlattenSubtree(subtree, seeds);
        return CollectAutoIncludes(seeds);
    }

    private static void FlattenSubtree(NodeSubtree st, List<Node> sink)
    {
        sink.Add(st.Node);
        foreach (var c in st.Children) FlattenSubtree(c, sink);
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
    /// Поддерево по человекочитаемому пути вида "Документ/Раздел/Подраздел" в
    /// заданном *addressable* дереве. Разделитель — '/'.
    /// <para>
    /// Параметр <paramref name="tree"/> опциональный: если null, дефолт берётся
    /// из <see cref="SchemaDocument.DefaultAddressableTree"/>; если поле не задано,
    /// но в Схеме ровно один addressable tree — он default; иначе ошибка
    /// <c>tree_required</c>.
    /// </para>
    /// <para>
    /// Для <c>tree="path"</c> имя документа может содержать '/' (если файл лежит
    /// в подкаталоге docs/) — берётся самый длинный префикс пути, совпадающий
    /// с title какого-либо документа. Для прочих addressable деревьев первый
    /// segment пути матчится с title top-level узла дерева (узла, у которого
    /// scope-родитель = root).
    /// </para>
    /// <para>
    /// Запрос <paramref name="tree"/>=&lt;non-addressable&gt; → <c>tree_not_addressable</c>;
    /// запрос дерева, не объявленного в Схеме → <c>unknown_tree_scope</c>.
    /// </para>
    /// </summary>
    public NodeSubtree GetByPath(string path, string? tree = null)
    {
        if (string.IsNullOrEmpty(path))
            throw new ReadApiException("invalid_path", "Путь не должен быть пустым.");

        var resolvedTree = ResolveAddressableTree(tree);
        var segments = path.Split('/');
        var isPathTree = string.Equals(resolvedTree, Node.PathRefName, StringComparison.Ordinal);

        Node? root = null;
        int prefixLen;

        if (isPathTree)
        {
            // path-tree: longest-prefix match по document.Title (имя файла может содержать '/').
            prefixLen = 0;
            for (int k = segments.Length; k >= 1; k--)
            {
                var candidate = string.Join('/', segments, 0, k);
                var match = _graph.GetDocumentByTitle(candidate);
                if (match is not null)
                {
                    root = match;
                    prefixLen = k;
                    break;
                }
            }
            if (root is null)
                throw new ReadApiException(
                    "path_not_found",
                    $"В docs/ нет документа, совпадающего с префиксом пути '{path}'.");
        }
        else
        {
            // Прочее addressable дерево: top-level узел = scope-child от RootId.
            var topLevelIds = _graph.GetScopeChildren(Node.RootId, resolvedTree);
            var firstSegment = segments[0];
            foreach (var id in topLevelIds)
            {
                var n = _graph.GetById(id);
                if (n is null) continue;
                if (string.Equals(n.Title, firstSegment, StringComparison.Ordinal))
                {
                    root = n;
                    break;
                }
            }
            if (root is null)
                throw new ReadApiException(
                    "path_not_found",
                    $"В дереве '{resolvedTree}' нет top-level узла с title='{firstSegment}'.");
            prefixLen = 1;
        }

        var current = root;
        for (int i = prefixLen; i < segments.Length; i++)
        {
            var title = segments[i];
            Node? next = null;
            if (isPathTree)
            {
                foreach (var child in _graph.GetChildren(current.Id))
                {
                    if (string.Equals(child.Title, title, StringComparison.Ordinal))
                    {
                        next = child;
                        break;
                    }
                }
            }
            else
            {
                foreach (var id in _graph.GetScopeChildren(current.Id, resolvedTree))
                {
                    var child = _graph.GetById(id);
                    if (child is null) continue;
                    if (string.Equals(child.Title, title, StringComparison.Ordinal))
                    {
                        next = child;
                        break;
                    }
                }
            }
            if (next is null)
                throw new ReadApiException(
                    "path_not_found",
                    $"У узла '{current.Title}' (id={current.Id}) нет дочернего узла в дереве '{resolvedTree}' с title='{title}'.");
            current = next;
        }

        return isPathTree ? BuildSubtree(current) : BuildSubtreeByScope(current, resolvedTree, remainingDepth: null);
    }

    /// <summary>
    /// Резолвит имя addressable дерева для <see cref="GetByPath"/>:
    ///   запрошено явно → проверяется существование и addressable-флаг;
    ///   null + есть <see cref="SchemaDocument.DefaultAddressableTree"/> → берётся он;
    ///   null + ровно один addressable tree → он default;
    ///   null + 0 либо &gt;1 addressable trees → <c>tree_required</c>.
    /// При <c>_schema == null</c> (legacy: ReadApi без схемы) — поддерживается
    /// только <c>path</c> (требование было всегда; LLM получает понятную ошибку).
    /// </summary>
    private string ResolveAddressableTree(string? requested)
    {
        if (requested is not null)
        {
            if (_schema is null)
            {
                if (!string.Equals(requested, Node.PathRefName, StringComparison.Ordinal))
                    throw new ReadApiException(
                        "tree_required",
                        "Schema не передана в ReadApi; без неё единственный доступный tree — 'path'.");
                return requested;
            }

            var declared = false;
            foreach (var tr in _schema.Trees)
            {
                if (string.Equals(tr.Name, requested, StringComparison.Ordinal))
                {
                    declared = true;
                    break;
                }
            }
            if (!declared)
                throw new ReadApiException(
                    "unknown_tree_scope",
                    $"Дерево '{requested}' не объявлено в schema.trees.",
                    "Сверь имя дерева со списком из get-schema.trees.");

            if (!IsAddressableTree(_schema, requested))
                throw new ReadApiException(
                    "tree_not_addressable",
                    $"Дерево '{requested}' не является addressable (нет tree-связи с unique_sibling_titles=true).",
                    "Используй get-tree --tree=<name> --id=<root> для обхода не-addressable деревьев.");

            return requested;
        }

        if (_schema is null) return Node.PathRefName;

        if (_schema.DefaultAddressableTree is not null)
            return _schema.DefaultAddressableTree;

        var addressables = CollectAddressableTrees(_schema);
        if (addressables.Count == 1) return addressables[0];

        if (addressables.Count == 0)
            throw new ReadApiException(
                "tree_required",
                "В Схеме нет ни одного addressable tree (с unique_sibling_titles=true). get-by-path неприменим.",
                "Используй get-tree --tree=<name> --id=<root> для обхода произвольного дерева.");

        throw new ReadApiException(
            "tree_required",
            $"В Схеме несколько addressable trees ({string.Join(", ", addressables)}); параметр --tree= обязателен.",
            "Задай default_addressable_tree в schema_root либо передай --tree=<name>.");
    }

    private static bool IsAddressableTree(SchemaDocument schema, string treeName)
    {
        foreach (var t in schema.Types)
        {
            foreach (var rd in t.OutRefs)
            {
                if (rd.IsAddressable && string.Equals(rd.Tree, treeName, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static List<string> CollectAddressableTrees(SchemaDocument schema)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var t in schema.Types)
        {
            foreach (var rd in t.OutRefs)
            {
                if (rd.IsAddressable && rd.Tree is not null && seen.Add(rd.Tree))
                    ordered.Add(rd.Tree);
            }
        }
        return ordered;
    }

    private NodeSubtree BuildSubtree(Node node)
    {
        var children = _graph.GetChildren(node.Id);
        var sub = new List<NodeSubtree>(children.Count);
        foreach (var c in children) sub.Add(BuildSubtree(c));
        return WithTokens(node, sub);
    }

    /// <summary>
    /// Полное поддерево узла в указанном дереве (tree-scope). По умолчанию <c>"path"</c> —
    /// эквивалент <see cref="GetByPath"/> по id, но без разрешения текстового пути.
    /// Для <c>tree="path"</c> используется обратный проход path-родителей (<see cref="Graph.GetChildren(int)"/>);
    /// для прочих scope'ов — обход через scope-индекс <see cref="Graph.GetScopeChildren"/>.
    /// <paramref name="depth"/> ограничивает глубину обхода: 0 — только корень без детей;
    /// 1 — корень + один уровень; null — без ограничения. <see cref="NodeSubtree.SubtreeTokens"/>
    /// учитывает только включённые узлы, поэтому при урезании depth эта метрика —
    /// «сколько ты получишь сейчас», а не «сколько стоит весь оригинальный subtree».
    /// </summary>
    public NodeSubtree GetTree(int rootId, string tree = Node.PathRefName, int? depth = null)
    {
        ValidateTree(tree);
        if (depth is < 0)
            throw new ReadApiException(
                "invalid_parameter",
                "Параметр 'depth' не может быть отрицательным.",
                "0 — только корень без детей; 1 — корень + один уровень; опусти параметр для неограниченного обхода.");

        var node = _graph.GetById(rootId)
            ?? throw new ReadApiException("node_not_found", $"Узел с id={rootId} не найден.");
        return BuildSubtreeByScope(node, tree, depth);
    }

    private NodeSubtree BuildSubtreeByScope(Node node, string tree, int? remainingDepth)
    {
        var sub = new List<NodeSubtree>();
        if (remainingDepth is null || remainingDepth.Value > 0)
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
            int? nextDepth = remainingDepth is int d ? d - 1 : null;
            foreach (var c in children) sub.Add(BuildSubtreeByScope(c, tree, nextDepth));
        }
        return WithTokens(node, sub);
    }

    private static NodeSubtree WithTokens(Node node, List<NodeSubtree> children)
    {
        var tokens = TokenCounter.CountNode(node);
        var subtreeTokens = tokens;
        foreach (var c in children) subtreeTokens += c.SubtreeTokens;
        return new NodeSubtree(node, tokens, subtreeTokens, children);
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

        return new RefSet(
            BuildRefMap(inRefs, name, r => r.Name, r => r.SourceId),
            BuildRefMap(outRefs, name, r => r.Name, r => r.TargetId));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> BuildRefMap<T>(
        IReadOnlyList<T> refs,
        string? nameFilter,
        Func<T, string> getName,
        Func<T, int> getId)
    {
        var grouped = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            var n = getName(r);
            if (nameFilter is not null && !string.Equals(n, nameFilter, StringComparison.Ordinal)) continue;
            if (!grouped.TryGetValue(n, out var ids))
            {
                ids = new List<int>();
                grouped[n] = ids;
            }
            ids.Add(getId(r));
        }

        var result = new Dictionary<string, IReadOnlyList<int>>(grouped.Count, StringComparer.Ordinal);
        foreach (var (k, ids) in grouped)
            result[k] = ids;
        return result;
    }

    /// <summary>
    /// Только входящие связи в форме map &lt;имя_связи → source-ids&gt; (без обёртки in/out,
    /// поскольку направление и так фиксировано). Совпадает по форме с <c>out_refs</c>
    /// в get-nodes и describe-type.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> GetInRefs(int id, string? name = null)
        => GetRefs(id, name).In;

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
            // root в человекочитаемом пути не участвует — путь начинается с документа,
            // ходовая форма для get-by-path: 'Документ/Раздел/...'.
            if (current.Id == Node.RootId) break;
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

    /// <summary>
    /// Manifest для LLM-агента: ментальная модель + декларация деревьев + список команд +
    /// слепок графа. Источник команд и текста ментальной модели — внешний (CLI или MCP),
    /// внедряется через <see cref="IUsageGuideSource"/>; сам Core их не знает.
    /// Метод статический — оперирует переданными аргументами, состояния не имеет.
    /// </summary>
    public static UsageGuideResponse GetUsageGuide(
        IUsageGuideSource source,
        SchemaDocument schema,
        GraphModel graph)
    {
        var rootChildrenNodes = graph.GetChildren(Node.RootId);
        var rootChildren = new List<RootChild>(rootChildrenNodes.Count);
        foreach (var n in rootChildrenNodes)
            rootChildren.Add(new RootChild(n.Id, n.TypeName, n.Title));

        var snapshot = new GraphSnapshot(
            TotalNodes: graph.NodeCount,
            RootChildren: rootChildren,
            SchemaTypesCount: schema.Types.Count);

        return new UsageGuideResponse(
            MentalModel: source.GetMentalModel(),
            Trees: schema.Trees,
            Commands: source.GetCommands(),
            Snapshot: snapshot,
            TransactionOperations: source.GetTransactionOperations());
    }

    /// <summary>
    /// Узкая операция: описание одного типа из Схемы в FS-агностичной форме.
    /// Используется LLM для уточнения контракта типа (например, перед <c>create-node</c>),
    /// чтобы не качать всю Схему. Если тип не найден — <see cref="ReadApiException"/>
    /// с кодом <c>type_not_found</c> и hint со списком доступных имён.
    /// Метод статический: оперирует только переданной Схемой, к графу не обращается.
    /// </summary>
    public static TypeDescription DescribeType(SchemaDocument schema, string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ReadApiException("invalid_parameter", "Параметр 'name' не должен быть пустым.");

        foreach (var t in schema.Types)
        {
            if (!string.Equals(t.Name, name, StringComparison.Ordinal)) continue;
            var refs = new List<TypeRefDescription>(t.OutRefs.Count);
            foreach (var rd in t.OutRefs)
            {
                refs.Add(new TypeRefDescription(
                    rd.Name,
                    rd.Tree,
                    rd.Tree is null ? rd.Cardinality : null,
                    rd.Tree is null ? rd.Required : null,
                    rd.TargetTypes,
                    rd.Description));
            }
            return new TypeDescription(t.Name, t.Description, t.TextRequired, refs);
        }

        var available = string.Join(", ", schema.Types.Select(x => x.Name));
        throw new ReadApiException(
            "type_not_found",
            $"Тип '{name}' не найден в Схеме.",
            $"Доступные типы: {available}.");
    }
}
