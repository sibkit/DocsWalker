namespace DocsWalker.Core.Api;

/// <summary>
/// Реализация op <c>rollback</c> (per api/tx.md, раздел <c>rollback</c>):
/// читает секции исходного event-узла, вычисляет inverse-ops через
/// per-field реконструкцию по hist (см. <see cref="HistReconstruction"/>),
/// применяет их к текущему состоянию через DML <see cref="TxContext"/>.
/// Возвращает <c>tx_scope</c> исходной tx — для записи нового event-узла
/// с правильным <c>tx_scope</c> и <c>rollback_of</c>.
/// </summary>
internal static class TxRollback
{
    public static (TxOpResponse Response, string TxScope) Apply(TxContext ctx, RollbackOp op)
    {
        // 1) Read source event.
        string? sourceSectionsJson = null;
        string? sourceScope = null;
        string sourceDate = string.Empty;
        long sourceOrdinal = 0;
        using (var cmd = ctx.NewCommand())
        {
            cmd.CommandText = "SELECT sections_json, tx_scope, date, ordinal FROM tx_event WHERE graph_name = @g AND id = @id";
            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
            cmd.Parameters.AddWithValue("@id", op.TxId);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                sourceSectionsJson = rdr.GetString(0);
                sourceScope = rdr.GetString(1);
                sourceDate = rdr.GetString(2);
                sourceOrdinal = rdr.GetInt64(3);
            }
        }
        if (sourceSectionsJson is null)
        {
            throw new ApiException(ApiErrorCodes.RollbackNotFound, ctx.OpPathPrefix + ".rollback",
                new Dictionary<string, object?> { ["id"] = op.TxId });
        }

        // 2) Check no existing compensating rollback for this source.
        using (var cmd = ctx.NewCommand())
        {
            cmd.CommandText = "SELECT id FROM tx_event WHERE graph_name = @g AND rollback_of = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
            cmd.Parameters.AddWithValue("@id", op.TxId);
            var existing = cmd.ExecuteScalar();
            if (existing is not null)
            {
                throw new ApiException(ApiErrorCodes.RollbackAlreadyDone, ctx.OpPathPrefix + ".rollback",
                    new Dictionary<string, object?> { ["id"] = op.TxId, ["rollback_tx_id"] = (string)existing });
            }
        }

        if (sourceScope != ScopeNames.ToWire(ctx.Scope))
        {
            throw new ApiException(ApiErrorCodes.RollbackConflict, ctx.OpPathPrefix + ".rollback",
                new Dictionary<string, object?>
                {
                    ["reason"] = "scope_mismatch",
                    ["source_scope"] = sourceScope,
                    ["tx_scope"] = ScopeNames.ToWire(ctx.Scope),
                });
        }

        var sections = HistSectionsJson.Deserialize(sourceSectionsJson);
        var recon = new HistReconstruction(ctx, sourceDate, sourceOrdinal);

        // 3) Compute and apply inverse-ops.
        // 3a) created.links → delete now (inverse unlink).
        if (sections.Created?.Links is { } createdLinks)
        {
            foreach (var l in createdLinks)
            {
                int rows;
                using (var cmd = ctx.NewCommand())
                {
                    cmd.CommandText = "DELETE FROM link WHERE graph_name = @g AND name = @n AND from_id = @f AND to_id = @t";
                    cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                    cmd.Parameters.AddWithValue("@n", l.Name);
                    cmd.Parameters.AddWithValue("@f", l.From);
                    cmd.Parameters.AddWithValue("@t", l.To);
                    rows = cmd.ExecuteNonQuery();
                }
                if (rows == 0)
                {
                    throw new ApiException(ApiErrorCodes.RollbackConflict, ctx.OpPathPrefix + ".rollback",
                        new Dictionary<string, object?>
                        {
                            ["reason"] = "link_already_removed",
                            ["name"] = l.Name,
                            ["from"] = l.From,
                            ["to"] = l.To,
                        });
                }
                ctx.RecordDeletedLink(l);
            }
        }
        // 3b) created.nodes → delete now (inverse create).
        if (sections.Created?.Nodes is { } createdNodes)
        {
            foreach (var n in createdNodes)
            {
                // Drop incident links if any (should not be present unless someone added).
                using (var cmd = ctx.NewCommand())
                {
                    cmd.CommandText = "DELETE FROM link WHERE graph_name = @g AND (from_id = @id OR to_id = @id)";
                    cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                    cmd.Parameters.AddWithValue("@id", n.Id);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = ctx.NewCommand())
                {
                    cmd.CommandText = "DELETE FROM node WHERE graph_name = @g AND id = @id";
                    cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                    cmd.Parameters.AddWithValue("@id", n.Id);
                    var rows = cmd.ExecuteNonQuery();
                    if (rows == 0)
                    {
                        throw new ApiException(ApiErrorCodes.RollbackConflict, ctx.OpPathPrefix + ".rollback",
                            new Dictionary<string, object?> { ["reason"] = "node_already_removed", ["id"] = n.Id });
                    }
                }
                ctx.RecordDeleted(n.Id);
            }
        }
        // 3c) changed.nodes → restore previous values (inverse update / move).
        if (sections.Changed?.Nodes is { } changedNodes)
        {
            foreach (var cn in changedNodes)
            {
                string? priorTitle = null, priorContent = null, priorPath = null;
                Dictionary<string, string?>? priorMapDiff = null;
                if (cn.Set.Title is not null)
                {
                    priorTitle = recon.ReconstructScalar(cn.Id, "title");
                }
                if (cn.Set.Content is not null)
                {
                    priorContent = recon.ReconstructScalar(cn.Id, "content") ?? string.Empty;
                }
                if (cn.Set.Path is not null)
                {
                    priorPath = recon.ReconstructScalar(cn.Id, "path");
                }
                if (cn.Set.MapBindings is not null)
                {
                    var priorMap = recon.ReconstructMapBindings(cn.Id);
                    priorMapDiff = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var kv in cn.Set.MapBindings)
                    {
                        string? priorValue = null;
                        if (priorMap is not null) priorMap.TryGetValue(kv.Key, out priorValue);
                        priorMapDiff[kv.Key] = priorValue; // null = tombstone (key didn't exist before)
                    }
                }

                // Apply node scalar restore.
                if (priorTitle is not null || priorContent is not null || priorPath is not null)
                {
                    var b = new SqlBuilder();
                    b.Append("UPDATE node SET ");
                    bool first = true;
                    if (priorTitle is not null)
                    {
                        b.Append("title = ").Param(priorTitle);
                        first = false;
                    }
                    if (priorPath is not null)
                    {
                        if (!first) b.Append(", ");
                        b.Append("path = ").Param(priorPath);
                        first = false;
                    }
                    if (priorContent is not null)
                    {
                        if (!first) b.Append(", ");
                        b.Append("content = ").Param(priorContent);
                    }
                    b.Append(" WHERE graph_name = ").Param(ctx.GraphName).Append(" AND id = ").Param(cn.Id);
                    using var cmd = ctx.NewCommandFromBuilder(b);
                    cmd.ExecuteNonQuery();
                }

                // Apply map_binding restore.
                if (priorMapDiff is not null)
                {
                    foreach (var kv in priorMapDiff)
                    {
                        if (kv.Value is null)
                        {
                            using var cmd = ctx.NewCommand();
                            cmd.CommandText = "DELETE FROM node_map_binding WHERE graph_name = @g AND node_id = @n AND map_name = @m";
                            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                            cmd.Parameters.AddWithValue("@n", cn.Id);
                            cmd.Parameters.AddWithValue("@m", kv.Key);
                            cmd.ExecuteNonQuery();
                        }
                        else
                        {
                            using var cmd = ctx.NewCommand();
                            cmd.CommandText = "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v) ON CONFLICT(graph_name, node_id, map_name) DO UPDATE SET branch_path = excluded.branch_path";
                            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                            cmd.Parameters.AddWithValue("@n", cn.Id);
                            cmd.Parameters.AddWithValue("@m", kv.Key);
                            cmd.Parameters.AddWithValue("@v", kv.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                ctx.RecordChange(cn.Id, new ChangedSet(priorTitle, priorContent, priorPath, priorMapDiff));
            }
        }
        // 3d) deleted.nodes → recreate with full snapshot.
        if (sections.Deleted?.Nodes is { } deletedNodes)
        {
            foreach (var dn in deletedNodes)
            {
                var snap = recon.ReconstructFullNode(dn.Id);
                if (snap is null)
                {
                    throw new ApiException(ApiErrorCodes.RollbackFailed, ctx.OpPathPrefix + ".rollback",
                        new Dictionary<string, object?>
                        {
                            ["reason"] = "cannot_reconstruct_deleted_node",
                            ["id"] = dn.Id,
                        });
                }
                using (var cmd = ctx.NewCommand())
                {
                    cmd.CommandText = "INSERT INTO node(graph_name, id, scope, path, title, content, version) VALUES(@g, @id, @s, @p, @t, @c, 1)";
                    cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                    cmd.Parameters.AddWithValue("@id", snap.Id);
                    cmd.Parameters.AddWithValue("@s", ScopeNames.ToWire(ctx.Scope));
                    cmd.Parameters.AddWithValue("@p", snap.Path);
                    cmd.Parameters.AddWithValue("@t", snap.Title);
                    cmd.Parameters.AddWithValue("@c", snap.Content);
                    cmd.ExecuteNonQuery();
                }
                if (snap.MapBindings is { Count: > 0 } mb)
                {
                    foreach (var kv in mb)
                    {
                        using var cmd = ctx.NewCommand();
                        cmd.CommandText = "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v)";
                        cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                        cmd.Parameters.AddWithValue("@n", snap.Id);
                        cmd.Parameters.AddWithValue("@m", kv.Key);
                        cmd.Parameters.AddWithValue("@v", kv.Value);
                        cmd.ExecuteNonQuery();
                    }
                }
                ctx.RecordCreated(new CreatedNode(snap.Id, snap.Path, snap.Title, snap.Content, snap.MapBindings));
            }
        }
        // 3e) deleted.links → recreate.
        if (sections.Deleted?.Links is { } deletedLinks)
        {
            foreach (var l in deletedLinks)
            {
                TxOps.CreateLinkRow(ctx, l.Name, l.From, l.To);
            }
        }

        // 4) Record rollback_of for hist write.
        ctx.RollbackOf = op.TxId;
        return (EmptyTxOpResponse.Instance, sourceScope);
    }
}

/// <summary>
/// Per-field реконструкция значений data-узла из hist (per
/// api/hist-scope.md и database-model/hist.md, раздел «Реконструкция
/// значения поля»). Используется rollback-ом и темпоральными чтениями
/// (step 6).
/// </summary>
internal sealed class HistReconstruction
{
    private readonly TxContext _ctx;
    private readonly string _beforeDate;
    private readonly long _beforeOrdinal;

    public HistReconstruction(TxContext ctx, string beforeDate, long beforeOrdinal)
    {
        _ctx = ctx;
        _beforeDate = beforeDate;
        _beforeOrdinal = beforeOrdinal;
    }

    /// <summary>
    /// Возвращает значение скалярного поля (<c>title</c>/<c>content</c>/
    /// <c>path</c>) на момент непосредственно перед tx с (<c>_beforeDate</c>,
    /// <c>_beforeOrdinal</c>). Идёт в обратном порядке по hist, возвращает
    /// первое найденное значение. null — если поле никогда не задавалось.
    /// </summary>
    public string? ReconstructScalar(string nodeId, string field)
    {
        var sections = LoadTouchingSections(nodeId, descending: true);
        foreach (var s in sections)
        {
            if (s.Changed?.Nodes is { } changed)
            {
                foreach (var c in changed)
                {
                    if (c.Id != nodeId) continue;
                    var v = ReadField(c.Set, field);
                    if (v is not null) return v;
                }
            }
            if (s.Created?.Nodes is { } created)
            {
                foreach (var n in created)
                {
                    if (n.Id != nodeId) continue;
                    return field switch
                    {
                        "title" => n.Title,
                        "content" => n.Content,
                        "path" => n.Path,
                        _ => null,
                    };
                }
            }
        }
        return null;
    }

    public Dictionary<string, string>? ReconstructMapBindings(string nodeId)
    {
        var sections = LoadTouchingSections(nodeId, descending: false);
        Dictionary<string, string>? acc = null;
        foreach (var s in sections)
        {
            if (s.Created?.Nodes is { } created)
            {
                foreach (var n in created)
                {
                    if (n.Id != nodeId) continue;
                    acc = n.MapBindings is null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(n.MapBindings, StringComparer.Ordinal);
                    break;
                }
            }
            if (acc is null) continue;
            if (s.Changed?.Nodes is { } changed)
            {
                foreach (var c in changed)
                {
                    if (c.Id != nodeId) continue;
                    if (c.Set.MapBindings is not { } diff) continue;
                    foreach (var kv in diff)
                    {
                        if (kv.Value is null) acc.Remove(kv.Key);
                        else acc[kv.Key] = kv.Value;
                    }
                }
            }
        }
        return acc;
    }

    public CreatedNode? ReconstructFullNode(string nodeId)
    {
        var title = ReconstructScalar(nodeId, "title");
        var path = ReconstructScalar(nodeId, "path");
        var content = ReconstructScalar(nodeId, "content") ?? string.Empty;
        var map = ReconstructMapBindings(nodeId);
        if (title is null || path is null) return null;
        return new CreatedNode(nodeId, path, title, content, map);
    }

    private static string? ReadField(ChangedSet set, string field) => field switch
    {
        "title" => set.Title,
        "content" => set.Content,
        "path" => set.Path,
        _ => null,
    };

    private List<HistSections> LoadTouchingSections(string nodeId, bool descending)
    {
        var sql = "SELECT e.sections_json FROM tx_event e JOIN tx_touches_node t ON t.graph_name = e.graph_name AND t.tx_id = e.id WHERE e.graph_name = @g AND t.node_id = @id AND t.role IN ('created', 'changed') AND (e.date < @date OR (e.date = @date AND e.ordinal < @ord)) ORDER BY e.date " + (descending ? "DESC" : "ASC") + ", e.ordinal " + (descending ? "DESC" : "ASC");
        var result = new List<HistSections>();
        using var cmd = _ctx.NewCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@g", _ctx.GraphName);
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@date", _beforeDate);
        cmd.Parameters.AddWithValue("@ord", _beforeOrdinal);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            result.Add(HistSectionsJson.Deserialize(rdr.GetString(0)));
        }
        return result;
    }
}
