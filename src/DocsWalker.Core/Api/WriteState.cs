using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Изменяемый снимок состояния DocsWalker для одной транзакции записи под refs-модель.
/// Хранит:
///   - словарь узлов (мутируется по операциям);
///   - актуальную Схему (read-only — Схема правится только вручную, см.
///     docs/DocsWalker.yml/«Расширение Схемы вручную»);
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
    private readonly HashSet<int> _touchedIds = new();
    private readonly List<FsOperation> _fsOperations = new();
    private bool _foldersDirty;

    public string DocsRoot { get; }
    public SchemaDocument Schema { get; }
    public int SequenceBase { get; }
    public int IdsConsumed { get; private set; }

    public IReadOnlyCollection<int> AffectedDocumentIds => _affectedDocs;

    /// <summary>
    /// Все id узлов, изменённых, созданных или удалённых в рамках транзакции.
    /// Заполняется автоматически в <see cref="Add"/>/<see cref="Replace"/>/<see cref="Remove"/>.
    /// Используется write-invalidation'ом (#358): после успешного commit'а сервер
    /// чистит эти id из seen-set всех активных sessions.
    /// </summary>
    public IReadOnlyCollection<int> TouchedIds => _touchedIds;

    public IReadOnlyList<FsOperation> FsOperations => _fsOperations;

    public bool FoldersDirty => _foldersDirty;

    public IEnumerable<Node> AllNodes => _nodes.Values;

    public WriteState(string docsRoot, SchemaDocument schema, GraphModel original, int sequenceBase)
    {
        DocsRoot = docsRoot;
        Schema = schema;
        SequenceBase = sequenceBase;
        _nodes = new Dictionary<int, Node>(original.ById);
    }

    public Node? GetNode(int id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public TypeDefinition ResolveType(string name)
    {
        var t = Schema.Types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (t is null)
            throw new WriteApiException(
                "unknown_type",
                $"Тип '{name}' не объявлен в Схеме.",
                "Сверь имя типа со списком из get-schema; уточни структуру через describe-type.");
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
        _touchedIds.Add(node.Id);
    }

    public void Replace(Node node)
    {
        if (!_nodes.ContainsKey(node.Id))
            throw new WriteApiException(
                "node_not_found",
                $"Узел id={node.Id} не найден для замены.");
        _nodes[node.Id] = node;
        _touchedIds.Add(node.Id);
    }

    public void Remove(int id)
    {
        if (!_nodes.Remove(id))
            throw new WriteApiException(
                "node_not_found",
                $"Узел id={id} не найден для удаления.");
        _touchedIds.Add(id);
    }

    /// <summary>
    /// Перечисляет входящие связи на узел <paramref name="id"/>: пары (источник, имя_связи).
    /// При <paramref name="includePath"/>=false (по умолчанию) связь path исключается —
    /// path-обратки находятся через <see cref="GetChildren"/>.
    /// </summary>
    public IEnumerable<(int SourceId, string Name)> ListIncomingRefs(int id, bool includePath = false)
    {
        foreach (var n in _nodes.Values)
        {
            foreach (var (name, targets) in n.OutRefs)
            {
                if (!includePath && string.Equals(name, Node.PathRefName, StringComparison.Ordinal))
                    continue;
                foreach (var t in targets)
                    if (t == id) yield return (n.Id, name);
            }
        }
    }

    /// <summary>
    /// Перечисляет path-детей узла (узлы, у которых OutRefs[path][0] == <paramref name="parentId"/>).
    /// </summary>
    public IEnumerable<Node> GetChildren(int parentId)
    {
        foreach (var n in _nodes.Values)
            if (n.ParentId == parentId) yield return n;
    }

    /// <summary>Помечает документ <paramref name="documentId"/> к перезаписи.</summary>
    public void MarkDocumentDirty(int documentId) => _affectedDocs.Add(documentId);

    /// <summary>Помечает <c>.docswalker/folders.yml</c> к перезаписи.</summary>
    public void MarkFoldersDirty() => _foldersDirty = true;

    /// <summary>Регистрирует FS-операцию (создание/удаление каталога), применяемую при <see cref="WriteApi.Apply"/>.</summary>
    public void AddFsOperation(FsOperation op) => _fsOperations.Add(op);

    /// <summary>
    /// Поднимается по path до document-узла (узла с ParentId == <see cref="Node.RootId"/>)
    /// и помечает его как dirty. Если path оборвана — ничего не помечает.
    /// </summary>
    public void MarkDocumentDirtyForNode(int anyNodeId)
    {
        if (!_nodes.TryGetValue(anyNodeId, out var node)) return;
        var current = node;
        var safety = _nodes.Count + 1;
        while (current.ParentId is int pid && pid != Node.RootId && safety-- > 0)
        {
            if (!_nodes.TryGetValue(pid, out var parent)) return;
            current = parent;
        }
        if (current.ParentId == Node.RootId)
            _affectedDocs.Add(current.Id);
    }

    public GraphModel BuildGraph()
    {
        var g = new GraphModel();
        foreach (var n in _nodes.Values
            .Where(n => n.ParentId == Node.RootId)
            .OrderBy(n => n.Id))
        {
            g.Add(n);
        }
        foreach (var n in _nodes.Values
            .Where(n => n.ParentId is null || n.ParentId != Node.RootId)
            .OrderBy(n => n.Id))
        {
            g.Add(n);
        }
        return g;
    }
}
