using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Полный replay tx_event-журнала для одного графа: пересоздаёт
/// data-таблицы (<c>node</c>, <c>node_map_binding</c>, <c>link</c>) из
/// секций <c>tx_event.sections_json</c> в хронологическом порядке (per
/// database-model/hist.md, раздел «Replay»). Используется для recovery
/// и для миграционных утилит. Источник истины — таблицы
/// <c>tx_event</c>/<c>tx_touches_*</c>, они не очищаются.
///
/// После replay-а sequence.next_id выставляется в (max(id) + 1) по всем
/// встреченным id (узлы + tx_event), чтобы новые tx не конфликтовали с
/// восстановленным id-пространством.
/// </summary>
public static class HistReplay
{
    /// <summary>
    /// Replays all hist events for the given graph, reconstructing data tables.
    /// Caller должен заранее создать строку в <c>graph</c> с именем
    /// <paramref name="graphName"/>; данные в <c>node</c>/<c>link</c>/
    /// <c>node_map_binding</c>/<c>sequence</c> для графа будут стёрты.
    /// </summary>
    public static void Replay(SqliteConnection connection, string graphName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(graphName);

        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            // Wipe data tables for this graph.
            ExecInTx(connection, tx, "DELETE FROM link WHERE graph_name = @g", graphName);
            ExecInTx(connection, tx, "DELETE FROM node_map_binding WHERE graph_name = @g", graphName);
            ExecInTx(connection, tx, "DELETE FROM node WHERE graph_name = @g", graphName);

            long maxIdSeen = 0;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id, sections_json, tx_scope FROM tx_event WHERE graph_name = @g ORDER BY date, ordinal";
                cmd.Parameters.AddWithValue("@g", graphName);
                var events = new List<(string Id, string Json, string Scope)>();
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        events.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
                    }
                }
                foreach (var ev in events)
                {
                    var sections = HistSectionsJson.Deserialize(ev.Json);
                    ApplySections(connection, tx, graphName, ev.Scope, sections);
                    maxIdSeen = Math.Max(maxIdSeen, ParseHex(ev.Id));
                    if (sections.Created?.Nodes is { } created)
                    {
                        foreach (var n in created)
                        {
                            maxIdSeen = Math.Max(maxIdSeen, ParseHex(n.Id));
                        }
                    }
                }
            }

            // Reset sequence.next_id to max + 1.
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE sequence SET next_id = @v WHERE graph_name = @g";
                cmd.Parameters.AddWithValue("@v", maxIdSeen + 1);
                cmd.Parameters.AddWithValue("@g", graphName);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            throw;
        }
    }

    private static void ApplySections(
        SqliteConnection conn, SqliteTransaction tx,
        string graphName, string txScope, HistSections s)
    {
        // Fixed order per api/hist-scope.md, раздел «Replay restoration».
        if (s.Deleted?.Links is { } deletedLinks)
        {
            foreach (var l in deletedLinks)
            {
                ExecInTx(conn, tx, "DELETE FROM link WHERE graph_name = @g AND name = @n AND from_id = @f AND to_id = @t",
                    graphName, ("@n", l.Name), ("@f", l.From), ("@t", l.To));
            }
        }
        if (s.Deleted?.Nodes is { } deletedNodes)
        {
            foreach (var n in deletedNodes)
            {
                ExecInTx(conn, tx, "DELETE FROM node WHERE graph_name = @g AND id = @id",
                    graphName, ("@id", n.Id));
            }
        }
        if (s.Changed?.Nodes is { } changedNodes)
        {
            foreach (var c in changedNodes)
            {
                ApplyChange(conn, tx, graphName, c);
            }
        }
        if (s.Created?.Nodes is { } createdNodes)
        {
            foreach (var n in createdNodes)
            {
                CreateNode(conn, tx, graphName, txScope, n);
            }
        }
        if (s.Created?.Links is { } createdLinks)
        {
            foreach (var l in createdLinks)
            {
                ExecInTx(conn, tx, "INSERT INTO link(graph_name, name, from_id, to_id) VALUES(@g, @n, @f, @t)",
                    graphName, ("@n", l.Name), ("@f", l.From), ("@t", l.To));
            }
        }
    }

    private static void ApplyChange(SqliteConnection conn, SqliteTransaction tx, string graphName, ChangedNode c)
    {
        // Scalar fields: full replace per api/hist-scope.md.
        if (c.Set.Title is not null || c.Set.Content is not null || c.Set.Path is not null)
        {
            var b = new SqlBuilder();
            b.Append("UPDATE node SET version = version + 1");
            if (c.Set.Title is not null) b.Append(", title = ").Param(c.Set.Title);
            if (c.Set.Content is not null) b.Append(", content = ").Param(c.Set.Content);
            if (c.Set.Path is not null) b.Append(", path = ").Param(c.Set.Path);
            b.Append(" WHERE graph_name = ").Param(graphName).Append(" AND id = ").Param(c.Id);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = b.Sql;
            for (int i = 0; i < b.Parameters.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + i;
                p.Value = b.Parameters[i] ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            cmd.ExecuteNonQuery();
        }
        else
        {
            // Only map_bindings changed — still bump version (per api/tx.md, раздел move).
            ExecInTx(conn, tx, "UPDATE node SET version = version + 1 WHERE graph_name = @g AND id = @id",
                graphName, ("@id", c.Id));
        }
        if (c.Set.MapBindings is { Count: > 0 } mb)
        {
            foreach (var kv in mb)
            {
                if (kv.Value is null)
                {
                    ExecInTx(conn, tx, "DELETE FROM node_map_binding WHERE graph_name = @g AND node_id = @n AND map_name = @m",
                        graphName, ("@n", c.Id), ("@m", kv.Key));
                }
                else
                {
                    ExecInTx(conn, tx, "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v) ON CONFLICT(graph_name, node_id, map_name) DO UPDATE SET branch_path = excluded.branch_path",
                        graphName, ("@n", c.Id), ("@m", kv.Key), ("@v", kv.Value));
                }
            }
        }
    }

    private static void CreateNode(SqliteConnection conn, SqliteTransaction tx, string graphName, string txScope, CreatedNode n)
    {
        ExecInTx(conn, tx,
            "INSERT INTO node(graph_name, id, scope, path, title, content, version) VALUES(@g, @id, @s, @p, @t, @c, 1)",
            graphName, ("@id", n.Id), ("@s", txScope), ("@p", n.Path), ("@t", n.Title), ("@c", n.Content));
        if (n.MapBindings is { Count: > 0 } mb)
        {
            foreach (var kv in mb)
            {
                ExecInTx(conn, tx,
                    "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v)",
                    graphName, ("@n", n.Id), ("@m", kv.Key), ("@v", kv.Value));
            }
        }
    }

    private static void ExecInTx(SqliteConnection conn, SqliteTransaction tx, string sql, string graphName, params (string name, object value)[] extra)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@g", graphName);
        foreach (var p in extra)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = p.name;
            prm.Value = p.value;
            cmd.Parameters.Add(prm);
        }
        cmd.ExecuteNonQuery();
    }

    private static long ParseHex(string id) => long.Parse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
