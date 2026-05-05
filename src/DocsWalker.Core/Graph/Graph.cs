namespace DocsWalker.Core.Graph;

/// <summary>
/// In-memory модель всего docs/. Узлы хранятся по id, индексы по типу и по родителю
/// строятся при добавлении. Default- и system-связи здесь не материализованы —
/// вычисляются на лету через <see cref="GetOutRefs"/> / <see cref="GetInRefs"/>
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
    /// "duplicate_id" при коллизии id.
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

    public IReadOnlyList<Node> GetChildren(int parentId) =>
        _byParent.TryGetValue(parentId, out var list) ? list : Array.Empty<Node>();

    public IReadOnlyList<Node> GetByType(string typeName) =>
        _byType.TryGetValue(typeName, out var list) ? list : Array.Empty<Node>();

    public Node? GetDocumentByTitle(string title) =>
        _documentByTitle.TryGetValue(title, out var doc) ? doc : null;

    public IReadOnlyList<Node> Documents => GetByType("document");

    /// <summary>
    /// Все исходящие связи узла: explicit (из YAML), default (parent → каждый ребёнок,
    /// тип = имя блока ребёнка в родителе), system path (этот узел → его родитель).
    /// </summary>
    public IReadOnlyList<Ref> GetOutRefs(int id)
    {
        if (!_byId.TryGetValue(id, out var node)) return Array.Empty<Ref>();
        var result = new List<Ref>();

        if (node.ExplicitOutRefs is not null)
        {
            foreach (var r in node.ExplicitOutRefs) result.Add(r);
        }

        // Default: parent → каждый child, тип = ParentBlockName ребёнка.
        foreach (var child in GetChildren(id))
        {
            if (child.ParentBlockName is null) continue;
            result.Add(new Ref(id, child.ParentBlockName, child.Id, RefOrigin.Default));
        }

        // System path: child → parent (присутствует у всех, кроме document).
        if (node.ParentId is int pid)
        {
            result.Add(new Ref(id, "path", pid, RefOrigin.System));
        }

        return result;
    }

    /// <summary>
    /// Все входящие связи на узел: explicit (где этот узел — to_id у других),
    /// default (от родителя по имени блока), system path (от каждого ребёнка).
    /// </summary>
    public IReadOnlyList<Ref> GetInRefs(int id)
    {
        if (!_byId.TryGetValue(id, out var node)) return Array.Empty<Ref>();
        var result = new List<Ref>();

        // Explicit: проходом по всем узлам — на этом шаге O(N), отдельный индекс
        // (если потребуется по производительности) — задача read-api / write-api.
        foreach (var src in _byId.Values)
        {
            if (src.ExplicitOutRefs is null) continue;
            foreach (var r in src.ExplicitOutRefs)
            {
                if (r.ToId == id) result.Add(r);
            }
        }

        // Default: от родителя по имени блока, в котором этот узел лежит.
        if (node.ParentId is int pid && node.ParentBlockName is not null)
        {
            result.Add(new Ref(pid, node.ParentBlockName, id, RefOrigin.Default));
        }

        // System path: каждый ребёнок → этот узел.
        foreach (var child in GetChildren(id))
        {
            result.Add(new Ref(child.Id, "path", id, RefOrigin.System));
        }

        return result;
    }
}
