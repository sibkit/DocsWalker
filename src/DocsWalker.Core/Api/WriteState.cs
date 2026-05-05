using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Изменяемый снимок состояния DocsWalker для одной транзакции записи. Хранит:
///   - словарь узлов (мутируется по операциям);
///   - текущее состояние Схемы (заменяется только операцией add_ref_type);
///   - резервирование sequence-id (через <see cref="ReserveId"/>);
///   - множество id-документов, чьи YAML-файлы должны быть перезаписаны.
///
/// На входе в транзакцию состояние клонируется из исходного <see cref="GraphModel"/>;
/// на выходе <see cref="BuildGraph"/> воссоздаёт обычный <see cref="GraphModel"/>
/// для прогона валидатора и эмиттера.
/// </summary>
internal sealed class WriteState
{
    private readonly Dictionary<int, Node> _nodes;
    private readonly HashSet<int> _affectedDocs = new();

    public string DocsRoot { get; }
    public SchemaDocument Schema { get; set; }
    public bool SchemaModified { get; set; }
    public int SequenceBase { get; }
    public int IdsConsumed { get; private set; }

    public IReadOnlyCollection<int> AffectedDocumentIds => _affectedDocs;

    public WriteState(string docsRoot, SchemaDocument schema, GraphModel original, int sequenceBase)
    {
        DocsRoot = docsRoot;
        Schema = schema;
        SequenceBase = sequenceBase;
        _nodes = new Dictionary<int, Node>(original.ById);
    }

    public Node? GetNode(int id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public NodeType ResolveNodeType(string name)
    {
        var t = Schema.Types.OfType<NodeType>().FirstOrDefault(t => t.Name == name);
        if (t is null)
            throw new WriteApiException(
                "unknown_type",
                $"Тип '{name}' не объявлен в Схеме как mapping/single_key_mapping/list.",
                "Сверь имя типа со списком из get-schema; уточни структуру через describe-type.");
        return t;
    }

    public RefType ResolveRefType(string name)
    {
        var t = Schema.Types.OfType<RefType>().FirstOrDefault(t => t.Name == name);
        if (t is null)
            throw new WriteApiException(
                "unknown_ref_type",
                $"Тип связи '{name}' не объявлен в Схеме как ref_type.",
                "Объяви новый ref_type через add-ref-type перед create-ref, либо сверь существующие имена через get-schema.");
        return t;
    }

    public int ReserveId()
    {
        IdsConsumed++;
        return SequenceBase + IdsConsumed;
    }

    public void Add(Node node)
    {
        if (_nodes.ContainsKey(node.Id))
            throw new WriteApiException(
                "duplicate_id",
                $"Узел id={node.Id} уже существует.");
        _nodes[node.Id] = node;
    }

    public void Replace(Node node)
    {
        if (!_nodes.ContainsKey(node.Id))
            throw new WriteApiException(
                "node_not_found",
                $"Узел id={node.Id} не найден для замены.");
        _nodes[node.Id] = node;
    }

    public void Remove(int id)
    {
        if (!_nodes.Remove(id))
            throw new WriteApiException(
                "node_not_found",
                $"Узел id={id} не найден для удаления.");
    }

    public IEnumerable<Ref> ListIncomingExplicitRefs(int id)
    {
        foreach (var n in _nodes.Values)
        {
            if (n.ExplicitOutRefs is null) continue;
            foreach (var r in n.ExplicitOutRefs)
                if (r.ToId == id) yield return r;
        }
    }

    /// <summary>Помечает документ <paramref name="documentId"/> к перезаписи.</summary>
    public void MarkDocumentDirty(int documentId) => _affectedDocs.Add(documentId);

    /// <summary>
    /// Помечает документ, в файле которого лежит узел <paramref name="anyNodeId"/>:
    /// поднимается по path-связям до root-узла-документа и помечает его id.
    /// </summary>
    public void MarkDocumentDirtyForNode(int anyNodeId)
    {
        if (!_nodes.TryGetValue(anyNodeId, out var node)) return;
        var current = node;
        var safety = _nodes.Count + 1;
        while (current.ParentId is int pid && safety-- > 0)
        {
            if (!_nodes.TryGetValue(pid, out var parent)) break;
            current = parent;
        }
        _affectedDocs.Add(current.Id);
    }

    public GraphModel BuildGraph()
    {
        var g = new GraphModel();
        // Documents (parent_id=null) сначала — Graph.Add проверяет дубль title документа.
        foreach (var n in _nodes.Values.Where(n => n.ParentId is null).OrderBy(n => n.Id))
            g.Add(n);
        foreach (var n in _nodes.Values.Where(n => n.ParentId is not null).OrderBy(n => n.Id))
            g.Add(n);
        return g;
    }
}
