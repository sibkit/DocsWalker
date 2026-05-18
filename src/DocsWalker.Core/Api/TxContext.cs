using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Изменяемое состояние одной выполняемой <c>tx</c>: соединение,
/// SQLite-транзакция, граф, scope, накапливаемые секции
/// <c>created</c>/<c>changed</c>/<c>deleted</c> и индексные списки
/// <c>tx_touches_node</c>/<c>tx_touches_link</c>, alias-биндинги от
/// <c>create.as</c>. Один экземпляр живёт от старта tx до её commit
/// или rollback.
/// </summary>
internal sealed class TxContext
{
    public TxContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string graphName,
        Scope scope,
        string utcDate,
        Defaults? defaults)
    {
        Connection = connection;
        Transaction = transaction;
        GraphName = graphName;
        Scope = scope;
        Date = utcDate;
        Defaults = defaults;
    }

    public SqliteConnection Connection { get; }
    public SqliteTransaction Transaction { get; }
    public string GraphName { get; }
    public Scope Scope { get; }
    public string Date { get; }
    public Defaults? Defaults { get; }

    /// <summary>
    /// Индекс текущей выполняемой <c>op</c> внутри <c>ops[]</c>. Используется
    /// при формировании <c>details.path</c> ошибок (<c>$.ops[N].op-name...</c>),
    /// чтобы в batch-tx было видно, какая именно op упала. Устанавливается
    /// <see cref="TxExecutor"/> перед каждым вызовом <c>Apply*</c>.
    /// </summary>
    public int OpIndex { get; set; }

    /// <summary>JSON-pointer-подобный префикс текущей op, готовый к конкатенации.</summary>
    public string OpPathPrefix => "$.ops[" + OpIndex + "]";

    /// <summary>alias → id единственного узла, выданного <c>create.as</c>.</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    public List<CreatedNode> CreatedNodes { get; } = [];
    public List<HistLink> CreatedLinks { get; } = [];

    /// <summary>
    /// Изменения по id. Несколько ops над одним узлом мерджатся в один
    /// итоговый <see cref="ChangedNode"/> с одним +1 у <c>version</c> на
    /// tx (per database-model/hist.md, раздел «Partial-merge для
    /// map_bindings»). Также сюда попадают каскадные потомки move/rename.
    /// </summary>
    public Dictionary<string, ChangedAccumulator> ChangedNodes { get; } = new(StringComparer.Ordinal);

    public List<DeletedNode> DeletedNodes { get; } = [];
    public List<HistLink> DeletedLinks { get; } = [];

    /// <summary>
    /// Отслеживает, у каких узлов <c>version</c> должен быть инкрементирован
    /// в самом конце tx (один +1 на узел независимо от числа изменений).
    /// </summary>
    public HashSet<string> VersionBump { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Выдаёт новый opaque hex id из <c>sequence</c> текущего графа.
    /// </summary>
    public string NextId()
    {
        using var cmd = NewCommand();
        cmd.CommandText = "UPDATE sequence SET next_id = next_id + 1 WHERE graph_name = @g RETURNING next_id - 1";
        cmd.Parameters.AddWithValue("@g", GraphName);
        var issued = cmd.ExecuteScalar();
        if (issued is null)
        {
            throw new InvalidOperationException($"sequence row missing for graph '{GraphName}'");
        }
        var n = Convert.ToInt64(issued, CultureInfo.InvariantCulture);
        return n.ToString("x", CultureInfo.InvariantCulture);
    }

    public SqliteCommand NewCommand()
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        return cmd;
    }

    public SqliteCommand NewCommandFromBuilder(SqlBuilder b)
    {
        var cmd = NewCommand();
        cmd.CommandText = b.Sql;
        for (int i = 0; i < b.Parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = b.Parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    /// <summary>
    /// Возвращает полный snapshot узла или null если узла нет в текущем графе.
    /// </summary>
    public NodeSnapshot? GetNode(string id)
    {
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT scope, path, title, content, version FROM node WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", GraphName);
        cmd.Parameters.AddWithValue("@id", id);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        return new NodeSnapshot(
            id,
            rdr.GetString(0),
            rdr.GetString(1),
            rdr.GetString(2),
            rdr.GetString(3),
            rdr.GetInt64(4));
    }

    /// <summary>
    /// Возвращает <c>map_bindings</c> узла (полный словарь).
    /// </summary>
    public Dictionary<string, string> GetMapBindings(string id)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT map_name, branch_path FROM node_map_binding WHERE graph_name = @g AND node_id = @id";
        cmd.Parameters.AddWithValue("@g", GraphName);
        cmd.Parameters.AddWithValue("@id", id);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            result[rdr.GetString(0)] = rdr.GetString(1);
        }
        return result;
    }

    /// <summary>
    /// Возвращает scope узла (<c>main</c>/<c>usage</c>/<c>scheme</c>),
    /// либо null если узла нет.
    /// </summary>
    public string? GetNodeScope(string id)
    {
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT scope FROM node WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", GraphName);
        cmd.Parameters.AddWithValue("@id", id);
        var s = cmd.ExecuteScalar();
        return s as string;
    }

    /// <summary>
    /// Возвращает id узлов, удовлетворяющих data-селектору, в указанном
    /// scope. Без сортировки.
    /// </summary>
    public List<string> FindNodes(Scope scope, DataSelector selector)
    {
        var b = new SqlBuilder();
        b.Append("SELECT n.id FROM node n WHERE n.graph_name = ").Param(GraphName);
        b.Append(" AND n.scope = ").Param(ScopeNames.ToWire(scope));
        SelectorSql.AppendDataPredicate(b, "n", selector);
        var result = new List<string>();
        using var cmd = NewCommandFromBuilder(b);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            result.Add(rdr.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// Запоминает, что у узла нужно поднять <c>version</c> в конце tx.
    /// </summary>
    public void BumpVersionOf(string id) => VersionBump.Add(id);

    /// <summary>
    /// Аккумулирует diff в секцию <c>changed.nodes</c>.
    /// </summary>
    public void RecordChange(string id, ChangedSet patch)
    {
        if (!ChangedNodes.TryGetValue(id, out var acc))
        {
            acc = new ChangedAccumulator();
            ChangedNodes[id] = acc;
        }
        acc.Apply(patch);
        BumpVersionOf(id);
    }

    /// <summary>
    /// Аккумулирует создание узла в <c>created.nodes</c>.
    /// </summary>
    public void RecordCreated(CreatedNode node)
    {
        CreatedNodes.Add(node);
    }

    public void RecordCreatedLink(HistLink link)
    {
        CreatedLinks.Add(link);
    }

    public void RecordDeleted(string id)
    {
        DeletedNodes.Add(new DeletedNode(id));
    }

    public void RecordDeletedLink(HistLink link)
    {
        DeletedLinks.Add(link);
    }

    /// <summary>
    /// Резолвит endpoint в массив id. <paramref name="role"/> — для
    /// диагностики (например, <c>"from"</c>/<c>"to"</c>).
    /// </summary>
    public List<string> ResolveEndpoint(Endpoint ep, string role)
    {
        return ep switch
        {
            IdEndpoint i => [i.Id],
            IdsEndpoint ii => ii.Ids.ToList(),
            AliasEndpoint a => ResolveAlias(a.Alias, role),
            SelectorEndpoint s => FindNodes(Scope, s.Selector),
            _ => throw new InvalidOperationException($"Unknown Endpoint {ep.GetType().Name}"),
        };
    }

    private List<string> ResolveAlias(string alias, string role)
    {
        if (!Aliases.TryGetValue(alias, out var id))
        {
            throw new ApiException(ApiErrorCodes.UnknownAlias, extras: new Dictionary<string, object?>
            {
                ["alias"] = alias,
                ["role"] = role,
            });
        }
        return [id];
    }

    /// <summary>
    /// Применяет ожидающие <c>+1</c> к <c>node.version</c>, пишет
    /// event-узел <c>tx_event</c>, индексные таблицы
    /// <c>tx_touches_node</c>/<c>tx_touches_link</c>. Возвращает
    /// <c>tx_id</c>.
    /// </summary>
    public string WriteHist(string title, string? description, string? txScopeOverride)
    {
        // 1) Apply version bumps.
        if (VersionBump.Count > 0)
        {
            var b = new SqlBuilder();
            b.Append("UPDATE node SET version = version + 1 WHERE graph_name = ").Param(GraphName);
            b.Append(" AND id IN (");
            int i = 0;
            foreach (var id in VersionBump)
            {
                if (i > 0) b.Append(", ");
                b.Param(id);
                i++;
            }
            b.Append(")");
            using var cmd = NewCommandFromBuilder(b);
            cmd.ExecuteNonQuery();
        }

        // 2) Issue tx_id and ordinal.
        var txId = NextId();
        long ordinal = NextOrdinal();
        var scopeWire = txScopeOverride ?? ScopeNames.ToWire(Scope);

        // 3) Build sections JSON.
        var sections = BuildSections();
        var sectionsJson = HistSectionsJson.Serialize(sections);

        // 4) Insert tx_event.
        using (var cmd = NewCommand())
        {
            cmd.CommandText = "INSERT INTO tx_event(graph_name, id, title, date, description, rollback_of, tx_scope, ordinal, sections_json) VALUES(@g, @id, @t, @d, @desc, @ro, @s, @o, @j)";
            cmd.Parameters.AddWithValue("@g", GraphName);
            cmd.Parameters.AddWithValue("@id", txId);
            cmd.Parameters.AddWithValue("@t", title);
            cmd.Parameters.AddWithValue("@d", Date);
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ro", (object?)RollbackOf ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", scopeWire);
            cmd.Parameters.AddWithValue("@o", ordinal);
            cmd.Parameters.AddWithValue("@j", sectionsJson);
            cmd.ExecuteNonQuery();
        }

        // 5) Insert tx_touches_node.
        var touchedNodes = CollectTouchedNodes();
        foreach (var (id, role) in touchedNodes)
        {
            using var cmd = NewCommand();
            cmd.CommandText = "INSERT INTO tx_touches_node(graph_name, tx_id, node_id, role) VALUES(@g, @t, @n, @r)";
            cmd.Parameters.AddWithValue("@g", GraphName);
            cmd.Parameters.AddWithValue("@t", txId);
            cmd.Parameters.AddWithValue("@n", id);
            cmd.Parameters.AddWithValue("@r", role);
            cmd.ExecuteNonQuery();
        }

        // 6) Insert tx_touches_link.
        foreach (var (link, role) in CollectTouchedLinks())
        {
            using var cmd = NewCommand();
            cmd.CommandText = "INSERT INTO tx_touches_link(graph_name, tx_id, link_name, from_id, to_id, role) VALUES(@g, @t, @n, @f, @to, @r)";
            cmd.Parameters.AddWithValue("@g", GraphName);
            cmd.Parameters.AddWithValue("@t", txId);
            cmd.Parameters.AddWithValue("@n", link.Name);
            cmd.Parameters.AddWithValue("@f", link.From);
            cmd.Parameters.AddWithValue("@to", link.To);
            cmd.Parameters.AddWithValue("@r", role);
            cmd.ExecuteNonQuery();
        }

        return txId;
    }

    /// <summary>
    /// Используется только для rollback-tx (см. TxRollback).
    /// </summary>
    public string? RollbackOf { get; set; }

    private long NextOrdinal()
    {
        using var cmd = NewCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(ordinal), 0) + 1 FROM tx_event WHERE graph_name = @g AND date = @d";
        cmd.Parameters.AddWithValue("@g", GraphName);
        cmd.Parameters.AddWithValue("@d", Date);
        return Convert.ToInt64(cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
    }

    private HistSections BuildSections()
    {
        CreatedSection? created = null;
        if (CreatedNodes.Count > 0 || CreatedLinks.Count > 0)
        {
            created = new CreatedSection(
                CreatedNodes.Count > 0 ? CreatedNodes.ToArray() : null,
                CreatedLinks.Count > 0 ? CreatedLinks.ToArray() : null);
        }
        ChangedSection? changed = null;
        var changedNodes = new List<ChangedNode>(ChangedNodes.Count);
        foreach (var kv in ChangedNodes)
        {
            if (kv.Value.IsEmpty) continue;
            changedNodes.Add(new ChangedNode(kv.Key, kv.Value.ToSet()));
        }
        if (changedNodes.Count > 0)
        {
            changed = new ChangedSection(changedNodes);
        }
        DeletedSection? deleted = null;
        if (DeletedNodes.Count > 0 || DeletedLinks.Count > 0)
        {
            deleted = new DeletedSection(
                DeletedNodes.Count > 0 ? DeletedNodes.ToArray() : null,
                DeletedLinks.Count > 0 ? DeletedLinks.ToArray() : null);
        }
        return new HistSections(created, changed, deleted);
    }

    private List<(string Id, string Role)> CollectTouchedNodes()
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<(string, string)>();
        foreach (var n in CreatedNodes)
        {
            if (seen.Add((n.Id, "created"))) result.Add((n.Id, "created"));
        }
        foreach (var n in ChangedNodes)
        {
            if (n.Value.IsEmpty) continue;
            if (seen.Add((n.Key, "changed"))) result.Add((n.Key, "changed"));
        }
        foreach (var n in DeletedNodes)
        {
            if (seen.Add((n.Id, "deleted"))) result.Add((n.Id, "deleted"));
        }
        return result;
    }

    private List<(HistLink Link, string Role)> CollectTouchedLinks()
    {
        var result = new List<(HistLink, string)>();
        foreach (var l in CreatedLinks)
        {
            result.Add((l, "created"));
        }
        foreach (var l in DeletedLinks)
        {
            result.Add((l, "deleted"));
        }
        return result;
    }
}

/// <summary>
/// Snapshot текущего состояния узла в БД (значения из колонок <c>node</c>).
/// Не включает <c>map_bindings</c> — те загружаются отдельно через
/// <see cref="TxContext.GetMapBindings(string)"/>.
/// </summary>
internal sealed record NodeSnapshot(
    string Id,
    string Scope,
    string Path,
    string Title,
    string Content,
    long Version);

/// <summary>
/// Накопитель forward-only diff одного узла в секции <c>changed</c>.
/// </summary>
internal sealed class ChangedAccumulator
{
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string?>? MapBindings { get; set; }

    public bool IsEmpty => Title is null && Content is null && Path is null &&
        (MapBindings is null || MapBindings.Count == 0);

    public void Apply(ChangedSet patch)
    {
        if (patch.Title is not null) Title = patch.Title;
        if (patch.Content is not null) Content = patch.Content;
        if (patch.Path is not null) Path = patch.Path;
        if (patch.MapBindings is not null)
        {
            MapBindings ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in patch.MapBindings)
            {
                MapBindings[kv.Key] = kv.Value;
            }
        }
    }

    public ChangedSet ToSet() => new(Title, Content, Path, MapBindings);
}
