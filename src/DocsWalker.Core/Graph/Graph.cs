namespace DocsWalker.Core.Graph;

/// <summary>
/// In-memory модель всего docs/. Узлы хранятся по id, индексы по типу и по path-родителю
/// строятся при добавлении. Все связи (включая path) материализованы в Node.OutRefs;
/// in-refs вычисляются обратным проходом через <see cref="GetInRefs"/>
/// (см. docs/DocsWalker.yml/«Модель данных»).
/// </summary>
public sealed class Graph
{
    private readonly Dictionary<int, Node> _byId = new();
    private readonly Dictionary<string, List<Node>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<Node>> _byParent = new();
    private readonly Dictionary<string, Node> _documentByTitle = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<int, Node> ById => _byId;

    public int NodeCount => _byId.Count;

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
    }

    public Node? GetById(int id) => _byId.TryGetValue(id, out var n) ? n : null;

    /// <summary>Возвращает прямых детей узла по обратному проходу out_refs[path].</summary>
    public IReadOnlyList<Node> GetChildren(int parentId) =>
        _byParent.TryGetValue(parentId, out var list) ? list : Array.Empty<Node>();

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
