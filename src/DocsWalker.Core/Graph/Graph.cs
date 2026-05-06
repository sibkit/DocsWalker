using DocsWalker.Core.Schema;

namespace DocsWalker.Core.Graph;

/// <summary>
/// In-memory модель всего docs/. Узлы хранятся по id, индексы по типу и по path-родителю
/// строятся при добавлении. Все связи (включая path) материализованы в Node.OutRefs;
/// in-refs вычисляются обратным проходом через <see cref="GetInRefs"/>
/// (см. docs/DocsWalker.yml/«Модель данных»).
/// Дополнительно поддерживает scope-индексы: для каждого объявленного дерева (tree-scope)
/// хранится мэппинг child→parent и parent→children, чтобы subtree- и ancestors-обходы
/// были O(1) на шаг (см. docs/DocsWalker.yml/«Tree-scopes»).
/// </summary>
public sealed class Graph
{
    private readonly Dictionary<int, Node> _byId = new();
    private readonly Dictionary<string, List<Node>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<Node>> _byParent = new();
    private readonly Dictionary<string, Node> _documentByTitle = new(StringComparer.Ordinal);

    /// <summary>
    /// Карта (тип → имя_дерева → имя_связи). Заполняется через <see cref="AttachSchema"/>
    /// перед/во время загрузки и используется для индексации scope-родителей.
    /// </summary>
    private Dictionary<string, Dictionary<string, string>> _scopeRefNamesByType =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Множество всех имён деревьев (включая <c>path</c>), объявленных в Схеме.
    /// </summary>
    private HashSet<string> _knownTrees = new(StringComparer.Ordinal) { Node.PathRefName };

    /// <summary>
    /// scope-индексы: имя дерева → (child→parent, parent→children).
    /// </summary>
    private readonly Dictionary<string, ScopeIndex> _byScope = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<int, Node> ById => _byId;

    public int NodeCount => _byId.Count;

    /// <summary>Имена всех известных деревьев (включая <c>path</c>).</summary>
    public IReadOnlyCollection<string> KnownTrees => _knownTrees;

    /// <summary>
    /// Привязывает Схему к графу: настраивает scope-индексы для всех объявленных деревьев.
    /// Должен вызываться до <see cref="Add"/> — иначе для уже добавленных узлов scope-индекс
    /// будет неполон (в текущем коде <see cref="DocumentLoader"/> вызывает это сразу
    /// после создания Graph).
    /// </summary>
    public void AttachSchema(SchemaDocument schema)
    {
        _knownTrees = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tr in schema.Trees) _knownTrees.Add(tr.Name);
        // path всегда известен — даже если в Схеме где-то не объявлен (валидация мета-схемы это поймает).
        _knownTrees.Add(Node.PathRefName);

        _scopeRefNamesByType = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
        {
            var byScope = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rd in t.OutRefs)
            {
                if (rd.Tree is null) continue;
                byScope[rd.Tree] = rd.Name;
            }
            _scopeRefNamesByType[t.Name] = byScope;
        }

        // Инициализируем все scope-индексы пустыми, чтобы lookup'ы не возвращали null.
        _byScope.Clear();
        foreach (var tree in _knownTrees) _byScope[tree] = new ScopeIndex();
    }

    /// <summary>
    /// Регистрирует узел в графе. Бросает <see cref="GraphLoadException"/> с кодом
    /// "duplicate_id" при коллизии id и "duplicate_document_title" при дубле title документа.
    /// </summary>
    public void Add(Node node)
    {
        if (!_byId.TryAdd(node.Id, node))
        {
            var existing = _byId[node.Id];
            throw new GraphLoadException(
                "duplicate_id",
                node.SourceFile,
                $"Узел id={node.Id} ('{node.TypeName}: {node.Title}' из '{node.SourceFile}') " +
                $"коллизирует с уже загруженным '{existing.TypeName}: {existing.Title}' из '{existing.SourceFile}'.");
        }

        if (!_byType.TryGetValue(node.TypeName, out var byType))
        {
            byType = new List<Node>();
            _byType[node.TypeName] = byType;
        }
        byType.Add(node);

        if (node.ParentId is int pid)
        {
            if (!_byParent.TryGetValue(pid, out var byParent))
            {
                byParent = new List<Node>();
                _byParent[pid] = byParent;
            }
            byParent.Add(node);
        }

        if (string.Equals(node.TypeName, "document", StringComparison.Ordinal))
        {
            if (!_documentByTitle.TryAdd(node.Title, node))
            {
                throw new GraphLoadException(
                    "duplicate_document_title",
                    node.SourceFile,
                    $"Документ с title='{node.Title}' уже зарегистрирован.");
            }
        }

        IndexScopeRefs(node);
    }

    /// <summary>
    /// Раскладывает все scope-связи узла по соответствующим <see cref="ScopeIndex"/>.
    /// Если AttachSchema не вызывался, scope-индексы пусты — индексируется только
    /// дефолтный path-scope, чтобы стандартные обходы продолжали работать.
    /// </summary>
    private void IndexScopeRefs(Node node)
    {
        if (_scopeRefNamesByType.TryGetValue(node.TypeName, out var byScope))
        {
            foreach (var (scope, refName) in byScope)
            {
                if (!node.OutRefs.TryGetValue(refName, out var targets) || targets.Count == 0)
                    continue;
                var idx = GetOrCreateScopeIndex(scope);
                idx.Set(node.Id, targets[0]);
            }
        }
        else if (node.OutRefs.TryGetValue(Node.PathRefName, out var pathTargets) && pathTargets.Count > 0)
        {
            // root либо тип без объявленной схемы — индексируем хотя бы path для обратной совместимости.
            var idx = GetOrCreateScopeIndex(Node.PathRefName);
            idx.Set(node.Id, pathTargets[0]);
        }
    }

    private ScopeIndex GetOrCreateScopeIndex(string scope)
    {
        if (!_byScope.TryGetValue(scope, out var idx))
        {
            idx = new ScopeIndex();
            _byScope[scope] = idx;
        }
        return idx;
    }

    public Node? GetById(int id) => _byId.TryGetValue(id, out var n) ? n : null;

    /// <summary>Возвращает прямых детей узла по обратному проходу out_refs[path] (alias к scope=path).</summary>
    public IReadOnlyList<Node> GetChildren(int parentId) =>
        _byParent.TryGetValue(parentId, out var list) ? list : Array.Empty<Node>();

    /// <summary>
    /// Возвращает прямых детей узла в scope <paramref name="scope"/>: id'ы узлов,
    /// чей scope-ref указывает на <paramref name="parentId"/>. Для scope=path
    /// эквивалентно <see cref="GetChildren(int)"/>, для прочих — отдаёт детей дерева.
    /// Неизвестное scope-имя → пустой список (бросать исключение это уровень API,
    /// не графа).
    /// </summary>
    public IReadOnlyList<int> GetScopeChildren(int parentId, string scope)
    {
        if (!_byScope.TryGetValue(scope, out var idx)) return Array.Empty<int>();
        return idx.GetChildren(parentId);
    }

    /// <summary>
    /// Возвращает id родителя узла в scope <paramref name="scope"/>, либо null если узел —
    /// корень дерева (нет scope-ref), либо неизвестное scope-имя.
    /// </summary>
    public int? GetScopeParent(int nodeId, string scope)
    {
        if (!_byScope.TryGetValue(scope, out var idx)) return null;
        return idx.GetParent(nodeId);
    }

    public IReadOnlyList<Node> GetByType(string typeName) =>
        _byType.TryGetValue(typeName, out var list) ? list : Array.Empty<Node>();

    public Node? GetDocumentByTitle(string title) =>
        _documentByTitle.TryGetValue(title, out var doc) ? doc : null;

    public IReadOnlyList<Node> Documents => GetByType("document");

    /// <summary>
    /// Все исходящие связи узла — flat-flatten его OutRefs. Связь path представлена
    /// как обычная пара (path, parent_id). Если узла нет — пустой список.
    /// </summary>
    public IReadOnlyList<OutRef> GetOutRefs(int id)
    {
        if (!_byId.TryGetValue(id, out var node)) return Array.Empty<OutRef>();
        var result = new List<OutRef>();
        foreach (var (name, targets) in node.OutRefs)
        {
            foreach (var t in targets) result.Add(new OutRef(name, t));
        }
        return result;
    }

    /// <summary>
    /// Все входящие связи на узел: проход по всем узлам, сбор тех, чьи OutRefs ссылаются на id.
    /// O(N) для одного запроса; при необходимости можно построить inverted-index.
    /// </summary>
    public IReadOnlyList<InRef> GetInRefs(int id)
    {
        if (!_byId.ContainsKey(id)) return Array.Empty<InRef>();
        var result = new List<InRef>();
        foreach (var src in _byId.Values)
        {
            foreach (var (name, targets) in src.OutRefs)
            {
                foreach (var t in targets)
                {
                    if (t == id) result.Add(new InRef(name, src.Id));
                }
            }
        }
        return result;
    }
}

/// <summary>
/// Двусторонний индекс одного дерева (tree-scope): child → parent (single) и
/// parent → children (list). Заполняется при <see cref="Graph.Add"/> по описанию из Схемы.
/// </summary>
internal sealed class ScopeIndex
{
    private readonly Dictionary<int, int> _parentOf = new();
    private readonly Dictionary<int, List<int>> _childrenOf = new();

    public int? GetParent(int childId) =>
        _parentOf.TryGetValue(childId, out var p) ? p : null;

    public IReadOnlyList<int> GetChildren(int parentId) =>
        _childrenOf.TryGetValue(parentId, out var list) ? list : Array.Empty<int>();

    public void Set(int childId, int parentId)
    {
        _parentOf[childId] = parentId;
        if (!_childrenOf.TryGetValue(parentId, out var list))
        {
            list = new List<int>();
            _childrenOf[parentId] = list;
        }
        list.Add(childId);
    }
}
