using System.Data;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Исполнитель метода <c>read</c> (per api/read.md). Принимает разобранный
/// <see cref="ReadRequest"/>, выполняет <c>ops[]</c> внутри одной
/// SQLite-read-транзакции (BEGIN DEFERRED), возвращает структурированный
/// <see cref="ReadResponse"/>.
///
/// Темпоральные чтения (<c>at</c> ≠ now) реализуются через
/// <see cref="AtReplay"/> — полный replay <c>tx_event</c>-журнала до
/// upper bound, селекторы применяются к in-memory state через
/// <see cref="AtSelector"/>. Поле <c>version</c> в ответе при <c>at</c>
/// ≠ now не выдаётся (per api/model.md, раздел «Темпоральные чтения»).
///
/// Текущие ограничения v2:
/// <list type="bullet">
/// <item><c>select.as</c> в read-запросе принимается, но alias-ссылки
/// в последующих <c>links.to/from</c> ещё не реализованы.</item>
/// </list>
/// </summary>
public sealed class ReadExecutor
{
    private readonly SqliteConnection _connection;
    private readonly string _graphName;

    public ReadExecutor(SqliteConnection connection, string graphName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(graphName);
        _connection = connection;
        _graphName = graphName;
    }

    public ReadResponse Execute(ReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var tx = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        var aliasBag = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        AtState? atState = null;
        if (request.At is { } atClause)
        {
            atState = new AtReplay(_connection, _graphName).BuildState(atClause, tx);
        }
        var results = new List<ReadOpResponse>(request.Ops.Count);
        foreach (var op in request.Ops)
        {
            results.Add(ExecuteOp(op, request, aliasBag, atState, tx));
        }
        tx.Commit();
        return new ReadResponse(results);
    }

    private ReadOpResponse ExecuteOp(
        ReadOp op,
        ReadRequest request,
        Dictionary<string, IReadOnlyList<string>> aliases,
        AtState? atState,
        SqliteTransaction tx)
    {
        return op switch
        {
            SelectKernelModeOp k => ExecuteKernelMode(k),
            SelectByPredicateOp s when request.Scope == Scope.Hist => ExecuteHistSelect(s, tx),
            SelectByPredicateOp s when atState is not null =>
                ExecuteAtDataSelect(s, request.Scope, request.Defaults, aliases, atState),
            SelectByPredicateOp s => ExecuteDataSelect(s, request.Scope, request.Defaults, aliases, tx),
            _ => throw new InvalidOperationException($"Unsupported ReadOp: {op.GetType().Name}"),
        };
    }

    private SelectNodesResponse ExecuteAtDataSelect(
        SelectByPredicateOp op, Scope scope, Defaults? defaults,
        IReadOnlyDictionary<string, IReadOnlyList<string>> aliases, AtState state)
    {
        if (op.Selector is not DataSelector raw)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, extras: new Dictionary<string, object?>
            {
                ["reason"] = "data_scope_requires_data_selector",
            });
        }
        var selector = ApplyDefaults(raw, defaults);
        selector = ResolveLinkAliases(selector, aliases);
        var matched = AtSelector.SelectNodes(state, scope, selector);

        var include = op.Include ?? Array.Empty<string>();
        var wantContent = include.Contains("content", StringComparer.Ordinal);
        var wantLinks = include.Contains("links", StringComparer.Ordinal);

        var items = new List<NodeView>();
        var truncated = false;
        var omitted = 0;
        string? stoppedAt = null;
        var tokensUsed = 0;

        for (int i = 0; i < matched.Count; i++)
        {
            var snap = matched[i];
            IReadOnlyDictionary<string, string> mb = snap.MapBindings.Count > 0
                ? snap.MapBindings
                : EmptyMapBindings;
            IReadOnlyList<IncidentLinkView>? nodeLinks = wantLinks
                ? BuildIncidentLinksFromState(state, snap.Id)
                : null;
            var tokens = Tokens.Estimate(snap.Content);
            var weight = Tokens.EstimateCompactDataNode(
                snap.Id, snap.Path, snap.Title, mb,
                includeScope: snap.Scope != ScopeNames.Main);
            if (wantContent) weight += tokens;
            if (nodeLinks is { Count: > 0 }) weight += nodeLinks.Count * 8;
            if (op.MaxTokens is { } limit && items.Count > 0 && tokensUsed + weight > limit)
            {
                truncated = true;
                omitted = matched.Count - items.Count;
                stoppedAt = items[^1].Path;
                break;
            }
            tokensUsed += weight;
            items.Add(new NodeView(
                Id: snap.Id,
                Scope: snap.Scope == ScopeNames.Main ? null : snap.Scope,
                Path: snap.Path,
                Title: snap.Title,
                MapBindings: mb,
                Content: wantContent ? snap.Content : null,
                Links: nodeLinks,
                Tokens: tokens,
                Version: null));
        }

        return new SelectNodesResponse(matched.Count, truncated, omitted, stoppedAt, items);
    }

    private static IReadOnlyList<IncidentLinkView> BuildIncidentLinksFromState(AtState state, string ownerId)
    {
        var list = new List<IncidentLinkView>();
        foreach (var l in state.Links)
        {
            if (string.Equals(l.From, ownerId, StringComparison.Ordinal))
            {
                var path = state.Nodes.TryGetValue(l.To, out var target) ? target.Path : string.Empty;
                list.Add(new IncidentLinkView(l.Name, new LinkEndpointView(l.To, path), null));
            }
            else if (string.Equals(l.To, ownerId, StringComparison.Ordinal))
            {
                var path = state.Nodes.TryGetValue(l.From, out var source) ? source.Path : string.Empty;
                list.Add(new IncidentLinkView(l.Name, null, new LinkEndpointView(l.From, path)));
            }
        }
        return list;
    }

    private static SelectMetaResponse ExecuteKernelMode(SelectKernelModeOp op)
    {
        if (op.ModeName != "meta")
        {
            throw new ApiException(ApiErrorCodes.UnknownSelectMode, extras: new Dictionary<string, object?> { ["mode"] = op.ModeName });
        }
        return new SelectMetaResponse(MetaSchema.Build());
    }

    private SelectNodesResponse ExecuteDataSelect(
        SelectByPredicateOp op,
        Scope scope,
        Defaults? defaults,
        Dictionary<string, IReadOnlyList<string>> aliases,
        SqliteTransaction tx)
    {
        if (op.Selector is not DataSelector raw)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, extras: new Dictionary<string, object?>
            {
                ["reason"] = "data_scope_requires_data_selector",
            });
        }
        var selector = ApplyDefaults(raw, defaults);
        selector = ResolveLinkAliases(selector, aliases);

        var b = new SqlBuilder();
        b.Append("SELECT n.id, n.scope, n.path, n.title, n.content, n.version FROM node n WHERE n.graph_name = ").Param(_graphName);
        b.Append(" AND n.scope = ").Param(ScopeNames.ToWire(scope));
        SelectorSql.AppendDataPredicate(b, "n", selector);
        b.Append(" ORDER BY n.path");

        var rows = new List<DataRow>();
        using (var cmd = MakeCommand(b, tx))
        using (var rdr = cmd.ExecuteReader())
        {
            while (rdr.Read())
            {
                rows.Add(new DataRow(
                    rdr.GetString(0),
                    rdr.GetString(1),
                    rdr.GetString(2),
                    rdr.GetString(3),
                    rdr.GetString(4),
                    rdr.GetInt64(5)));
            }
        }

        if (op.Alias is { Length: > 0 } alias)
        {
            aliases[alias] = rows.Select(r => r.Id).ToArray();
        }

        var include = op.Include ?? Array.Empty<string>();
        var wantContent = include.Contains("content", StringComparer.Ordinal);
        var wantLinks = include.Contains("links", StringComparer.Ordinal);

        var nodeIds = rows.Select(r => r.Id).ToList();
        var bindings = LoadMapBindings(nodeIds, tx);
        var links = wantLinks ? LoadIncidentLinks(nodeIds, tx) : null;

        var items = new List<NodeView>();
        var truncated = false;
        var omitted = 0;
        string? stoppedAt = null;
        var tokensUsed = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var mb = bindings.GetValueOrDefault(r.Id) ?? EmptyMapBindings;
            IReadOnlyList<IncidentLinkView>? nodeLinks = wantLinks
                ? (links!.GetValueOrDefault(r.Id) ?? (IReadOnlyList<IncidentLinkView>)Array.Empty<IncidentLinkView>())
                : null;
            var tokens = Tokens.Estimate(r.Content);
            var weight = Tokens.EstimateCompactDataNode(
                r.Id, r.Path, r.Title, mb,
                includeScope: r.Scope != ScopeNames.Main);
            if (wantContent) weight += tokens;
            if (nodeLinks is { Count: > 0 }) weight += nodeLinks.Count * 8;
            if (op.MaxTokens is { } limit && items.Count > 0 && tokensUsed + weight > limit)
            {
                truncated = true;
                omitted = rows.Count - items.Count;
                stoppedAt = items[^1].Path;
                break;
            }
            tokensUsed += weight;
            items.Add(new NodeView(
                Id: r.Id,
                Scope: r.Scope == ScopeNames.Main ? null : r.Scope,
                Path: r.Path,
                Title: r.Title,
                MapBindings: mb,
                Content: wantContent ? r.Content : null,
                Links: nodeLinks,
                Tokens: tokens,
                Version: r.Version));
        }

        return new SelectNodesResponse(rows.Count, truncated, omitted, stoppedAt, items);
    }

    private SelectEventsResponse ExecuteHistSelect(SelectByPredicateOp op, SqliteTransaction tx)
    {
        if (op.Selector is not HistSelector hist)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, extras: new Dictionary<string, object?>
            {
                ["reason"] = "hist_scope_requires_hist_selector",
            });
        }

        var b = new SqlBuilder();
        b.Append("SELECT e.id, e.title, e.date, e.description, e.rollback_of, e.sections_json FROM tx_event e WHERE e.graph_name = ").Param(_graphName);
        SelectorSql.AppendHistPredicate(b, "e", hist);
        b.Append(" ORDER BY e.date, e.ordinal");

        var rows = new List<EventRow>();
        using (var cmd = MakeCommand(b, tx))
        using (var rdr = cmd.ExecuteReader())
        {
            while (rdr.Read())
            {
                rows.Add(new EventRow(
                    rdr.GetString(0),
                    rdr.GetString(1),
                    rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    rdr.GetString(5)));
            }
        }

        var include = op.Include ?? Array.Empty<string>();
        // description — compact-поле hist event-узла (per api/hist-scope.md,
        // раздел «Compact-форма event-узла»). Включается без include.
        var wantCreated = include.Contains("created", StringComparer.Ordinal);
        var wantChanged = include.Contains("changed", StringComparer.Ordinal);
        var wantDeleted = include.Contains("deleted", StringComparer.Ordinal);

        var items = new List<EventView>();
        var truncated = false;
        var omitted = 0;
        string? stoppedAt = null;
        var tokensUsed = 0;

        foreach (var r in rows)
        {
            var sections = HistSectionsJson.Deserialize(r.SectionsJson);
            var fullTokens = Tokens.Estimate(r.SectionsJson) + Tokens.Estimate(r.Description);
            var weight = Tokens.EstimateCompactEventNode(r.Id, r.Title, r.Date, r.RollbackOf);
            if (r.Description is not null) weight += Tokens.Estimate(r.Description);
            if (wantCreated || wantChanged || wantDeleted)
            {
                // Загружаемые секции стоят как полные sections-json; точное
                // разделение по секциям не делаем — пессимистическая оценка.
                weight += Tokens.Estimate(r.SectionsJson);
            }
            if (op.MaxTokens is { } limit && items.Count > 0 && tokensUsed + weight > limit)
            {
                truncated = true;
                omitted = rows.Count - items.Count;
                stoppedAt = items[^1].Id;
                break;
            }
            tokensUsed += weight;
            items.Add(new EventView(
                Id: r.Id,
                Title: r.Title,
                Date: r.Date,
                RollbackOf: r.RollbackOf,
                Description: r.Description,
                Counts: ComputeEventCounts(sections),
                Created: wantCreated ? sections.Created : null,
                Changed: wantChanged ? sections.Changed : null,
                Deleted: wantDeleted ? sections.Deleted : null,
                Tokens: fullTokens));
        }

        return new SelectEventsResponse(rows.Count, truncated, omitted, stoppedAt, items);
    }

    private static EventCountsView? ComputeEventCounts(HistSections sections)
    {
        SectionCountView? created = null;
        if (sections.Created is { } c)
        {
            int? nodes = c.Nodes is { Count: > 0 } ? c.Nodes.Count : null;
            int? links = c.Links is { Count: > 0 } ? c.Links.Count : null;
            if (nodes is not null || links is not null)
            {
                created = new SectionCountView(nodes, links);
            }
        }
        SectionCountView? changed = null;
        if (sections.Changed is { Nodes.Count: > 0 } cn)
        {
            changed = new SectionCountView(cn.Nodes.Count, null);
        }
        SectionCountView? deleted = null;
        if (sections.Deleted is { } d)
        {
            int? nodes = d.Nodes is { Count: > 0 } ? d.Nodes.Count : null;
            int? links = d.Links is { Count: > 0 } ? d.Links.Count : null;
            if (nodes is not null || links is not null)
            {
                deleted = new SectionCountView(nodes, links);
            }
        }
        if (created is null && changed is null && deleted is null)
        {
            return null;
        }
        return new EventCountsView(created, changed, deleted);
    }

    private static DataSelector ApplyDefaults(DataSelector selector, Defaults? defaults)
    {
        if (defaults?.PathParent is not { Length: > 0 } parent)
        {
            return selector;
        }
        if (selector.Path is not { Length: > 0 } path)
        {
            return selector;
        }
        var combined = path.StartsWith('/') ? path[1..] : path;
        var newPath = parent.TrimEnd('/') + "/" + combined;
        return selector with { Path = newPath };
    }

    /// <summary>
    /// Резолвит alias-endpoint'ы внутри <c>selector.links.from/to</c> в
    /// явные <c>Ids</c>-фильтры на основе ранее объявленных
    /// <c>select.as</c>. Alias-ссылка на несуществующее имя — <c>unknown_alias</c>.
    /// </summary>
    private static DataSelector ResolveLinkAliases(
        DataSelector selector, IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        if (selector.Links is not { } links) return selector;
        var newFrom = ResolveEndpoint(links.From, aliases);
        var newTo = ResolveEndpoint(links.To, aliases);
        if (ReferenceEquals(newFrom, links.From) && ReferenceEquals(newTo, links.To))
        {
            return selector;
        }
        return selector with { Links = new LinkClause(links.Name, newFrom, newTo) };
    }

    private static LinkEndpointClause? ResolveEndpoint(
        LinkEndpointClause? clause, IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        if (clause is null) return null;
        if (clause.Alias is not { Length: > 0 } alias) return clause;
        if (!aliases.TryGetValue(alias, out var ids))
        {
            throw new ApiException(ApiErrorCodes.UnknownAlias, extras: new Dictionary<string, object?>
            {
                ["alias"] = alias,
            });
        }
        var aliasSelector = new DataSelector(ids, null, null, null, null, null);
        return new LinkEndpointClause(null, null, aliasSelector);
    }

    private Dictionary<string, IReadOnlyDictionary<string, string>> LoadMapBindings(
        IReadOnlyList<string> nodeIds, SqliteTransaction tx)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (nodeIds.Count == 0) return result;

        var b = new SqlBuilder();
        b.Append("SELECT node_id, map_name, branch_path FROM node_map_binding WHERE graph_name = ").Param(_graphName);
        b.Append(" AND node_id IN (");
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (i > 0) b.Append(", ");
            b.Param(nodeIds[i]);
        }
        b.Append(")");

        var perNode = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        using var cmd = MakeCommand(b, tx);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var id = rdr.GetString(0);
            var name = rdr.GetString(1);
            var value = rdr.GetString(2);
            if (!perNode.TryGetValue(id, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                perNode[id] = dict;
            }
            dict[name] = value;
        }
        foreach (var kv in perNode)
        {
            result[kv.Key] = kv.Value;
        }
        return result;
    }

    private Dictionary<string, List<IncidentLinkView>> LoadIncidentLinks(
        IReadOnlyList<string> nodeIds, SqliteTransaction tx)
    {
        var result = new Dictionary<string, List<IncidentLinkView>>(StringComparer.Ordinal);
        if (nodeIds.Count == 0) return result;

        // Outgoing: for each from_id ∈ nodeIds.
        var bOut = new SqlBuilder();
        bOut.Append("SELECT l.from_id, l.name, l.to_id, target.path FROM link l JOIN node target ON target.graph_name = l.graph_name AND target.id = l.to_id WHERE l.graph_name = ").Param(_graphName);
        bOut.Append(" AND l.from_id IN (");
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (i > 0) bOut.Append(", ");
            bOut.Param(nodeIds[i]);
        }
        bOut.Append(")");
        using (var cmd = MakeCommand(bOut, tx))
        using (var rdr = cmd.ExecuteReader())
        {
            while (rdr.Read())
            {
                var owner = rdr.GetString(0);
                var name = rdr.GetString(1);
                var toId = rdr.GetString(2);
                var toPath = rdr.GetString(3);
                AddLink(result, owner, new IncidentLinkView(name, new LinkEndpointView(toId, toPath), null));
            }
        }

        // Incoming: for each to_id ∈ nodeIds.
        var bIn = new SqlBuilder();
        bIn.Append("SELECT l.to_id, l.name, l.from_id, source.path FROM link l JOIN node source ON source.graph_name = l.graph_name AND source.id = l.from_id WHERE l.graph_name = ").Param(_graphName);
        bIn.Append(" AND l.to_id IN (");
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (i > 0) bIn.Append(", ");
            bIn.Param(nodeIds[i]);
        }
        bIn.Append(")");
        using (var cmd = MakeCommand(bIn, tx))
        using (var rdr = cmd.ExecuteReader())
        {
            while (rdr.Read())
            {
                var owner = rdr.GetString(0);
                var name = rdr.GetString(1);
                var fromId = rdr.GetString(2);
                var fromPath = rdr.GetString(3);
                AddLink(result, owner, new IncidentLinkView(name, null, new LinkEndpointView(fromId, fromPath)));
            }
        }

        return result;
    }

    private static void AddLink(Dictionary<string, List<IncidentLinkView>> bucket, string owner, IncidentLinkView link)
    {
        if (!bucket.TryGetValue(owner, out var list))
        {
            list = [];
            bucket[owner] = list;
        }
        list.Add(link);
    }

    private SqliteCommand MakeCommand(SqlBuilder b, SqliteTransaction tx)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
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

    private static readonly IReadOnlyDictionary<string, string> EmptyMapBindings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record DataRow(string Id, string Scope, string Path, string Title, string Content, long Version);
    private sealed record EventRow(string Id, string Title, string Date, string? Description, string? RollbackOf, string SectionsJson);
}
