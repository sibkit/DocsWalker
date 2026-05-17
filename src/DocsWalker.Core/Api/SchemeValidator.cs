using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Breaking-change-check и data-against-scheme валидатор. Вызывается
/// <see cref="TxExecutor"/> после применения ops и до записи hist
/// (per api/scheme-scope.md, раздел «Breaking-change-check»; api/errors.md,
/// коды <c>schema_breaks_existing_data</c>, <c>validation_failed</c>).
///
/// Поведение для пустой схемы: если в графе нет scheme-узлов, валидатор
/// возвращает ничего — данных контракт не нарушают, потому что контракт
/// отсутствует. По мере наполнения <c>scheme</c> ограничения активируются.
///
/// V1-ограничения:
/// <list type="bullet">
/// <item>Для maps: проверяется <c>branch</c> exists и <c>required</c>
/// satisfied; <c>required_when</c> не реализован (нужен tokenizer для
/// rule applicability).</item>
/// <item>Для links: проверяется <c>from</c>/<c>to.map_bindings</c>
/// constraint и <c>required_for</c>; <c>cardinality</c> не реализован.</item>
/// </list>
/// </summary>
internal static class SchemeValidator
{
    public static void ValidateSchemeTx(SqliteConnection conn, SqliteTransaction tx, string graphName)
    {
        var store = SchemeLoader.Load(conn, tx, graphName);
        if (store.IsEmpty) return;
        var violations = new List<Dictionary<string, object?>>();
        foreach (var ownerScope in new[] { ScopeNames.Main, ScopeNames.Usage })
        {
            var nodes = LoadDataNodes(conn, tx, graphName, ownerScope);
            CheckMapsForScope(store, ownerScope, nodes, violations);
            var links = LoadDataLinks(conn, tx, graphName, ownerScope);
            CheckLinksForScope(store, ownerScope, nodes, links, violations);
        }
        if (violations.Count == 0) return;
        throw new ApiException(ApiErrorCodes.SchemaBreaksExistingData,
            extras: new Dictionary<string, object?>
            {
                ["violations"] = violations,
            });
    }

    public static void ValidateDataTx(SqliteConnection conn, SqliteTransaction tx,
        string graphName, Scope scope,
        IReadOnlyCollection<string> touchedNodeIds, IReadOnlyList<HistLink> touchedLinks)
    {
        var store = SchemeLoader.Load(conn, tx, graphName);
        if (store.IsEmpty) return;
        var ownerScope = ScopeNames.ToWire(scope);
        var errors = new List<Dictionary<string, object?>>();
        var maps = store.MapsFor(ownerScope);
        var linkDefs = store.LinksFor(ownerScope);

        foreach (var id in touchedNodeIds)
        {
            var snap = LoadSingleNode(conn, tx, graphName, id);
            if (snap is null) continue; // deleted in this tx — skip
            if (!string.Equals(snap.Scope, ownerScope, StringComparison.Ordinal)) continue;
            foreach (var kv in snap.MapBindings)
            {
                if (!maps.TryGetValue(kv.Key, out var def))
                {
                    errors.Add(new Dictionary<string, object?>
                    {
                        ["code"] = ApiErrorCodes.UnknownMap,
                        ["id"] = id,
                        ["map"] = kv.Key,
                    });
                    continue;
                }
                if (!def.IsBranchKnown(kv.Value))
                {
                    errors.Add(new Dictionary<string, object?>
                    {
                        ["code"] = ApiErrorCodes.UnknownMap,
                        ["id"] = id,
                        ["map"] = kv.Key,
                        ["branch"] = kv.Value,
                    });
                }
            }
        }

        foreach (var l in touchedLinks)
        {
            // Link принадлежит scope from-узла (per spec); фильтруем по нему.
            var fromScope = LoadNodeScope(conn, tx, graphName, l.From);
            if (!string.Equals(fromScope, ownerScope, StringComparison.Ordinal)) continue;
            if (!linkDefs.ContainsKey(l.Name))
            {
                errors.Add(new Dictionary<string, object?>
                {
                    ["code"] = ApiErrorCodes.UnknownLink,
                    ["link"] = l.Name,
                    ["from"] = l.From,
                    ["to"] = l.To,
                });
            }
        }
        if (errors.Count == 0) return;
        throw new ApiException(ApiErrorCodes.ValidationFailed,
            extras: new Dictionary<string, object?>
            {
                ["errors"] = errors,
            });
    }

    private static DataNodeRow? LoadSingleNode(SqliteConnection conn, SqliteTransaction tx,
        string graphName, string id)
    {
        DataNodeRow? row = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT scope FROM node WHERE graph_name = @g AND id = @id";
            cmd.Parameters.AddWithValue("@g", graphName);
            cmd.Parameters.AddWithValue("@id", id);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                row = new DataNodeRow(id) { Scope = rdr.GetString(0) };
            }
        }
        if (row is null) return null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT map_name, branch_path FROM node_map_binding WHERE graph_name = @g AND node_id = @id";
            cmd.Parameters.AddWithValue("@g", graphName);
            cmd.Parameters.AddWithValue("@id", id);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                row.MapBindings[rdr.GetString(0)] = rdr.GetString(1);
            }
        }
        return row;
    }

    private static string? LoadNodeScope(SqliteConnection conn, SqliteTransaction tx,
        string graphName, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT scope FROM node WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", graphName);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    private static void CheckMapsForScope(SchemeStore store, string ownerScope,
        List<DataNodeRow> nodes, List<Dictionary<string, object?>> violations)
    {
        var maps = store.MapsFor(ownerScope);
        foreach (var n in nodes)
        {
            foreach (var kv in n.MapBindings)
            {
                if (!maps.TryGetValue(kv.Key, out var def))
                {
                    violations.Add(new Dictionary<string, object?>
                    {
                        ["scope"] = ownerScope,
                        ["id"] = n.Id,
                        ["reason"] = "unknown_map",
                        ["map"] = kv.Key,
                    });
                    continue;
                }
                if (!def.IsBranchKnown(kv.Value))
                {
                    violations.Add(new Dictionary<string, object?>
                    {
                        ["scope"] = ownerScope,
                        ["id"] = n.Id,
                        ["reason"] = "map_branch_unknown",
                        ["map"] = kv.Key,
                        ["violating_value"] = kv.Value,
                    });
                }
            }
            // Required-checks: каждая required-map должна быть у каждого узла scope-а.
            foreach (var (mapName, def) in maps)
            {
                if (!def.Required) continue;
                if (!n.MapBindings.ContainsKey(mapName))
                {
                    violations.Add(new Dictionary<string, object?>
                    {
                        ["scope"] = ownerScope,
                        ["id"] = n.Id,
                        ["reason"] = "map_required_missing",
                        ["map"] = mapName,
                    });
                }
            }
        }
    }

    private static void CheckLinksForScope(SchemeStore store, string ownerScope,
        List<DataNodeRow> nodes, List<DataLinkRow> links,
        List<Dictionary<string, object?>> violations)
    {
        var linkDefs = store.LinksFor(ownerScope);
        var byId = new Dictionary<string, DataNodeRow>(StringComparer.Ordinal);
        foreach (var n in nodes) byId[n.Id] = n;

        foreach (var l in links)
        {
            if (!linkDefs.TryGetValue(l.Name, out var def))
            {
                violations.Add(new Dictionary<string, object?>
                {
                    ["scope"] = ownerScope,
                    ["link"] = l.Name,
                    ["from"] = l.From,
                    ["to"] = l.To,
                    ["reason"] = "unknown_link",
                });
                continue;
            }
            if (byId.TryGetValue(l.From, out var fromNode) && !def.From.Matches(fromNode.MapBindings))
            {
                violations.Add(new Dictionary<string, object?>
                {
                    ["scope"] = ownerScope,
                    ["link"] = l.Name,
                    ["from"] = l.From,
                    ["to"] = l.To,
                    ["reason"] = "link_from_constraint_unmet",
                });
            }
            if (byId.TryGetValue(l.To, out var toNode) && !def.To.Matches(toNode.MapBindings))
            {
                violations.Add(new Dictionary<string, object?>
                {
                    ["scope"] = ownerScope,
                    ["link"] = l.Name,
                    ["from"] = l.From,
                    ["to"] = l.To,
                    ["reason"] = "link_to_constraint_unmet",
                });
            }
        }

        // Required_for: для каждого link.required_for=["from"] каждый узел scope-а,
        // подходящий под constraint def.From, обязан быть from в хотя бы одном
        // link этого name; аналогично для "to".
        foreach (var (linkName, def) in linkDefs)
        {
            if (def.RequiredFor.Count == 0) continue;
            HashSet<string>? fromIds = null;
            HashSet<string>? toIds = null;
            foreach (var l in links)
            {
                if (!string.Equals(l.Name, linkName, StringComparison.Ordinal)) continue;
                (fromIds ??= new HashSet<string>(StringComparer.Ordinal)).Add(l.From);
                (toIds ??= new HashSet<string>(StringComparer.Ordinal)).Add(l.To);
            }
            fromIds ??= new HashSet<string>(StringComparer.Ordinal);
            toIds ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in nodes)
            {
                if (def.RequiredFor.Contains("from") && def.From.Matches(n.MapBindings)
                    && !fromIds.Contains(n.Id))
                {
                    violations.Add(new Dictionary<string, object?>
                    {
                        ["scope"] = ownerScope,
                        ["id"] = n.Id,
                        ["link"] = linkName,
                        ["reason"] = "link_required_from_missing",
                    });
                }
                if (def.RequiredFor.Contains("to") && def.To.Matches(n.MapBindings)
                    && !toIds.Contains(n.Id))
                {
                    violations.Add(new Dictionary<string, object?>
                    {
                        ["scope"] = ownerScope,
                        ["id"] = n.Id,
                        ["link"] = linkName,
                        ["reason"] = "link_required_to_missing",
                    });
                }
            }
        }
    }

    private static List<DataNodeRow> LoadDataNodes(SqliteConnection conn, SqliteTransaction tx,
        string graphName, string scope)
    {
        var rows = new List<DataNodeRow>();
        var byId = new Dictionary<string, DataNodeRow>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM node WHERE graph_name = @g AND scope = @s";
            cmd.Parameters.AddWithValue("@g", graphName);
            cmd.Parameters.AddWithValue("@s", scope);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var n = new DataNodeRow(rdr.GetString(0));
                rows.Add(n);
                byId[n.Id] = n;
            }
        }
        if (rows.Count == 0) return rows;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT b.node_id, b.map_name, b.branch_path FROM node_map_binding b " +
                "JOIN node n ON n.graph_name = b.graph_name AND n.id = b.node_id " +
                "WHERE n.graph_name = @g AND n.scope = @s";
            cmd.Parameters.AddWithValue("@g", graphName);
            cmd.Parameters.AddWithValue("@s", scope);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                if (byId.TryGetValue(rdr.GetString(0), out var node))
                {
                    node.MapBindings[rdr.GetString(1)] = rdr.GetString(2);
                }
            }
        }
        return rows;
    }

    private static List<DataLinkRow> LoadDataLinks(SqliteConnection conn, SqliteTransaction tx,
        string graphName, string scope)
    {
        var rows = new List<DataLinkRow>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT l.name, l.from_id, l.to_id FROM link l " +
            "JOIN node src ON src.graph_name = l.graph_name AND src.id = l.from_id " +
            "WHERE l.graph_name = @g AND src.scope = @s";
        cmd.Parameters.AddWithValue("@g", graphName);
        cmd.Parameters.AddWithValue("@s", scope);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new DataLinkRow(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
        }
        return rows;
    }

    private sealed class DataNodeRow
    {
        public string Id { get; }
        public string Scope { get; set; } = string.Empty;
        public Dictionary<string, string> MapBindings { get; } = new(StringComparer.Ordinal);

        public DataNodeRow(string id) { Id = id; }
    }

    private sealed record DataLinkRow(string Name, string From, string To);
}
