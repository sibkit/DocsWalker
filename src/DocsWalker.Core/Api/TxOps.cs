using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Реализация семи tx-ops (per api/tx.md). Каждый хендлер:
/// <list type="number">
/// <item>валидирует input и резолвит цели (selector → ids, alias → id);</item>
/// <item>выполняет DML на <c>node</c>/<c>node_map_binding</c>/<c>link</c>;</item>
/// <item>аккумулирует секции <c>created</c>/<c>changed</c>/<c>deleted</c>
/// в <see cref="TxContext"/>.</item>
/// </list>
/// Запись <c>tx_event</c> и индексных таблиц делает <c>TxContext.WriteHist</c>
/// в конце <c>TxExecutor.Execute</c>.
/// </summary>
internal static class TxOps
{
    /// <summary>
    /// Regex из api/model.md, раздел «Поля data-узла» (поле <c>title</c>).
    /// Last segment пути и <c>update.set.title</c>.
    /// </summary>
    private static readonly Regex TitleRegex = new(
        @"^[\p{L}\p{Nd}._-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // -----------------------------------------------------------------
    // create
    // -----------------------------------------------------------------

    public static TxOpResponse Create(TxContext ctx, CreateOp op)
    {
        var rawPath = ApplyPathParent(ctx, op.Path);
        var (parentPath, title) = SplitPath(rawPath);
        ValidateTitle(title, "$.ops[].create.path");
        if (op.Set.Title is { } explicitTitle)
        {
            ValidateTitle(explicitTitle, "$.ops[].create.set.title");
            if (!string.Equals(explicitTitle, title, StringComparison.Ordinal))
            {
                throw new ApiException(ApiErrorCodes.InvalidNodeTitle, "$.ops[].create.set.title",
                    new Dictionary<string, object?> { ["reason"] = "set_title_must_equal_path_last_segment" });
            }
        }

        // Validate parent existence (only if multi-segment).
        if (!string.IsNullOrEmpty(parentPath))
        {
            if (!PathExists(ctx, parentPath))
            {
                throw new ApiException(ApiErrorCodes.PathParentNotFound, "$.ops[].create.path",
                    new Dictionary<string, object?> { ["parent_path"] = parentPath });
            }
        }

        var id = ctx.NextId();
        var content = op.Set.Content ?? string.Empty;

        // Insert node.
        try
        {
            using var cmd = ctx.NewCommand();
            cmd.CommandText = "INSERT INTO node(graph_name, id, scope, path, title, content, version) VALUES(@g, @id, @s, @p, @t, @c, 1)";
            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@s", ScopeNames.ToWire(ctx.Scope));
            cmd.Parameters.AddWithValue("@p", rawPath);
            cmd.Parameters.AddWithValue("@t", title);
            cmd.Parameters.AddWithValue("@c", content);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new ApiException(ApiErrorCodes.AlreadyExists, "$.ops[].create.path",
                new Dictionary<string, object?> { ["path"] = rawPath });
        }

        // Apply create.set.map_bindings (with defaults underlay).
        var bindings = MergeCreateBindings(ctx.Defaults?.MapBindings, op.Set.MapBindings);
        if (bindings.Count > 0)
        {
            foreach (var kv in bindings)
            {
                using var cmd = ctx.NewCommand();
                cmd.CommandText = "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v)";
                cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                cmd.Parameters.AddWithValue("@n", id);
                cmd.Parameters.AddWithValue("@m", kv.Key);
                cmd.Parameters.AddWithValue("@v", kv.Value);
                cmd.ExecuteNonQuery();
            }
        }

        // Record alias before processing links so links[] can reference it (not standard, but safe).
        if (op.Alias is { Length: > 0 } alias)
        {
            ctx.Aliases[alias] = id;
        }

        // Record created node into sections.
        ctx.RecordCreated(new CreatedNode(
            id, rawPath, title, content,
            bindings.Count > 0 ? bindings : null));

        // Apply set.links[] — outgoing links from the new node.
        if (op.Set.Links is { Count: > 0 } links)
        {
            foreach (var lc in links)
            {
                var toIds = ctx.ResolveEndpoint(lc.To, "to");
                foreach (var toId in toIds)
                {
                    CreateLinkRow(ctx, lc.Name, id, toId);
                }
            }
        }

        return new CreateOpResponse(id);
    }

    private static Dictionary<string, string> MergeCreateBindings(
        IReadOnlyDictionary<string, string>? defaults,
        IReadOnlyDictionary<string, string>? userValues)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (defaults is not null)
        {
            foreach (var kv in defaults) result[kv.Key] = kv.Value;
        }
        if (userValues is not null)
        {
            foreach (var kv in userValues) result[kv.Key] = kv.Value;
        }
        return result;
    }

    // -----------------------------------------------------------------
    // update
    // -----------------------------------------------------------------

    public static TxOpResponse Update(TxContext ctx, UpdateOp op)
    {
        var node = ctx.GetNode(op.Id) ?? throw new ApiException(
            ApiErrorCodes.NotFound, "$.ops[].update.id",
            new Dictionary<string, object?> { ["id"] = op.Id });
        if (node.Scope != ScopeNames.ToWire(ctx.Scope))
        {
            throw new ApiException(ApiErrorCodes.NotFound, "$.ops[].update.id",
                new Dictionary<string, object?> { ["id"] = op.Id, ["scope"] = node.Scope, ["expected_scope"] = ScopeNames.ToWire(ctx.Scope) });
        }
        if (node.Version != op.ExpectedVersion)
        {
            throw new ApiException(ApiErrorCodes.VersionMismatch, "$.ops[].update",
                new Dictionary<string, object?> { ["id"] = op.Id, ["expected"] = op.ExpectedVersion, ["current"] = node.Version });
        }

        string? newTitle = null;
        string? newPath = null;
        string? newContent = null;
        if (op.Set.Title is { } setTitle && !string.Equals(setTitle, node.Title, StringComparison.Ordinal))
        {
            ValidateTitle(setTitle, "$.ops[].update.set.title");
            newTitle = setTitle;
            var parent = ParentOf(node.Path);
            newPath = parent.Length == 0 ? setTitle : parent + "/" + setTitle;
        }
        if (op.Set.Content is { } setContent && !string.Equals(setContent, node.Content, StringComparison.Ordinal))
        {
            newContent = setContent;
        }
        if (newTitle is null && newContent is null)
        {
            // no-op
            return EmptyTxOpResponse.Instance;
        }

        // Apply UPDATE on the node itself.
        try
        {
            var b = new SqlBuilder();
            b.Append("UPDATE node SET ");
            bool first = true;
            if (newTitle is not null)
            {
                b.Append("title = ").Param(newTitle);
                first = false;
            }
            if (newPath is not null)
            {
                if (!first) b.Append(", ");
                b.Append("path = ").Param(newPath);
                first = false;
            }
            if (newContent is not null)
            {
                if (!first) b.Append(", ");
                b.Append("content = ").Param(newContent);
            }
            b.Append(" WHERE graph_name = ").Param(ctx.GraphName).Append(" AND id = ").Param(op.Id);
            using var cmd = ctx.NewCommandFromBuilder(b);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new ApiException(ApiErrorCodes.AlreadyExists, "$.ops[].update.set.title",
                new Dictionary<string, object?> { ["path"] = newPath });
        }

        // Cascade descendants if path changed.
        if (newPath is not null)
        {
            CascadePathChange(ctx, node.Path, newPath);
        }

        ctx.RecordChange(op.Id, new ChangedSet(newTitle, newContent, newPath, null));
        return EmptyTxOpResponse.Instance;
    }

    // -----------------------------------------------------------------
    // move
    // -----------------------------------------------------------------

    public static TxOpResponse Move(TxContext ctx, MoveOp op)
    {
        var selected = ctx.FindNodes(ctx.Scope, op.Selector);
        if (selected.Count != op.ExpectedCount)
        {
            throw new ApiException(ApiErrorCodes.CountMismatch, "$.ops[].move",
                new Dictionary<string, object?> { ["expected_count"] = op.ExpectedCount, ["actual_count"] = (long)selected.Count });
        }
        var rawParent = op.To.ParentPath is { Length: > 0 } pp ? ApplyPathParent(ctx, pp) : null;
        if (rawParent is not null && !PathExists(ctx, rawParent))
        {
            throw new ApiException(ApiErrorCodes.PathParentNotFound, "$.ops[].move.to.parent_path",
                new Dictionary<string, object?> { ["parent_path"] = rawParent });
        }

        foreach (var id in selected)
        {
            var node = ctx.GetNode(id)!;
            string? newPath = null;
            if (rawParent is not null)
            {
                var candidate = rawParent + "/" + node.Title;
                if (!string.Equals(candidate, node.Path, StringComparison.Ordinal))
                {
                    newPath = candidate;
                }
            }

            Dictionary<string, string?>? diff = null;
            if (op.To.MapBindings is { Count: > 0 } mb)
            {
                var current = ctx.GetMapBindings(id);
                diff = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var kv in mb)
                {
                    var currentValue = current.GetValueOrDefault(kv.Key);
                    if (kv.Value is null)
                    {
                        // tombstone — remove if exists.
                        if (currentValue is not null)
                        {
                            using var cmd = ctx.NewCommand();
                            cmd.CommandText = "DELETE FROM node_map_binding WHERE graph_name = @g AND node_id = @n AND map_name = @m";
                            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                            cmd.Parameters.AddWithValue("@n", id);
                            cmd.Parameters.AddWithValue("@m", kv.Key);
                            cmd.ExecuteNonQuery();
                            diff[kv.Key] = null;
                        }
                    }
                    else if (!string.Equals(currentValue, kv.Value, StringComparison.Ordinal))
                    {
                        using var cmd = ctx.NewCommand();
                        cmd.CommandText = "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v) ON CONFLICT(graph_name, node_id, map_name) DO UPDATE SET branch_path = excluded.branch_path";
                        cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                        cmd.Parameters.AddWithValue("@n", id);
                        cmd.Parameters.AddWithValue("@m", kv.Key);
                        cmd.Parameters.AddWithValue("@v", kv.Value);
                        cmd.ExecuteNonQuery();
                        diff[kv.Key] = kv.Value;
                    }
                }
                if (diff.Count == 0)
                {
                    diff = null;
                }
            }

            if (newPath is null && diff is null)
            {
                // no-op for this node
                continue;
            }

            if (newPath is not null)
            {
                try
                {
                    using var cmd = ctx.NewCommand();
                    cmd.CommandText = "UPDATE node SET path = @p WHERE graph_name = @g AND id = @id";
                    cmd.Parameters.AddWithValue("@p", newPath);
                    cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (IsUniqueViolation(ex))
                {
                    throw new ApiException(ApiErrorCodes.AlreadyExists, "$.ops[].move.to.parent_path",
                        new Dictionary<string, object?> { ["path"] = newPath });
                }
                CascadePathChange(ctx, node.Path, newPath);
            }

            ctx.RecordChange(id, new ChangedSet(null, null, newPath, diff));
        }
        return EmptyTxOpResponse.Instance;
    }

    // -----------------------------------------------------------------
    // delete
    // -----------------------------------------------------------------

    public static TxOpResponse Delete(TxContext ctx, DeleteOp op)
    {
        var ids = op.Ids is { } explicitIds ? new List<string>(explicitIds) : ctx.FindNodes(ctx.Scope, op.Selector!);
        if (ids.Count != op.ExpectedCount)
        {
            throw new ApiException(ApiErrorCodes.CountMismatch, "$.ops[].delete",
                new Dictionary<string, object?> { ["expected_count"] = op.ExpectedCount, ["actual_count"] = (long)ids.Count });
        }
        foreach (var id in ids)
        {
            var node = ctx.GetNode(id);
            if (node is null)
            {
                throw new ApiException(ApiErrorCodes.NotFound, "$.ops[].delete",
                    new Dictionary<string, object?> { ["id"] = id });
            }
            if (node.Scope != ScopeNames.ToWire(ctx.Scope))
            {
                throw new ApiException(ApiErrorCodes.NotFound, "$.ops[].delete",
                    new Dictionary<string, object?> { ["id"] = id, ["scope"] = node.Scope });
            }

            // Cross-scope blocker: main-узел c incoming usage→main links.
            if (node.Scope == ScopeNames.Main)
            {
                var blockers = new List<HistLink>();
                using var cmd = ctx.NewCommand();
                cmd.CommandText = "SELECT l.name, l.from_id FROM link l JOIN node src ON src.graph_name = l.graph_name AND src.id = l.from_id WHERE l.graph_name = @g AND l.to_id = @id AND src.scope = 'usage'";
                cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                cmd.Parameters.AddWithValue("@id", id);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    blockers.Add(new HistLink(rdr.GetString(0), rdr.GetString(1), id));
                }
                if (blockers.Count > 0)
                {
                    throw new ApiException(ApiErrorCodes.DeleteBlockedByCrossScopeLink, "$.ops[].delete",
                        new Dictionary<string, object?>
                        {
                            ["id"] = id,
                            ["blocking_links"] = blockers.Select(b => (object)new Dictionary<string, object?>
                            {
                                ["name"] = b.Name,
                                ["from"] = b.From,
                                ["to"] = b.To,
                            }).ToArray(),
                        });
                }
            }

            // Collect incident links and delete them.
            var incident = new List<HistLink>();
            using (var cmd = ctx.NewCommand())
            {
                cmd.CommandText = "SELECT name, from_id, to_id FROM link WHERE graph_name = @g AND (from_id = @id OR to_id = @id)";
                cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                cmd.Parameters.AddWithValue("@id", id);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    incident.Add(new HistLink(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
                }
            }
            using (var cmd = ctx.NewCommand())
            {
                cmd.CommandText = "DELETE FROM link WHERE graph_name = @g AND (from_id = @id OR to_id = @id)";
                cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            foreach (var l in incident)
            {
                ctx.RecordDeletedLink(l);
            }

            // Delete node (CASCADE clears node_map_binding).
            using (var cmd = ctx.NewCommand())
            {
                cmd.CommandText = "DELETE FROM node WHERE graph_name = @g AND id = @id";
                cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            ctx.RecordDeleted(id);
        }
        return EmptyTxOpResponse.Instance;
    }

    // -----------------------------------------------------------------
    // link / unlink
    // -----------------------------------------------------------------

    public static TxOpResponse Link(TxContext ctx, LinkOp op)
    {
        var fromIds = ctx.ResolveEndpoint(op.From, "from");
        var toIds = ctx.ResolveEndpoint(op.To, "to");
        if (fromIds.Count == 0 || toIds.Count == 0)
        {
            throw new ApiException(ApiErrorCodes.NotFound, "$.ops[].link",
                new Dictionary<string, object?>
                {
                    ["from_count"] = (long)fromIds.Count,
                    ["to_count"] = (long)toIds.Count,
                });
        }
        var product = fromIds.Count * toIds.Count;
        if (product != op.ExpectedCount)
        {
            throw new ApiException(ApiErrorCodes.CountMismatch, "$.ops[].link",
                new Dictionary<string, object?> { ["expected_count"] = op.ExpectedCount, ["actual_count"] = (long)product });
        }
        foreach (var f in fromIds)
        {
            foreach (var t in toIds)
            {
                CreateLinkRow(ctx, op.Name, f, t);
            }
        }
        return EmptyTxOpResponse.Instance;
    }

    public static TxOpResponse Unlink(TxContext ctx, UnlinkOp op)
    {
        var fromIds = ctx.ResolveEndpoint(op.From, "from");
        var toIds = ctx.ResolveEndpoint(op.To, "to");
        var product = fromIds.Count * toIds.Count;
        if (product != op.ExpectedCount)
        {
            throw new ApiException(ApiErrorCodes.CountMismatch, "$.ops[].unlink",
                new Dictionary<string, object?> { ["expected_count"] = op.ExpectedCount, ["actual_count"] = (long)product });
        }
        foreach (var f in fromIds)
        {
            foreach (var t in toIds)
            {
                int rows;
                using (var cmd = ctx.NewCommand())
                {
                    cmd.CommandText = "DELETE FROM link WHERE graph_name = @g AND name = @n AND from_id = @f AND to_id = @t";
                    cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                    cmd.Parameters.AddWithValue("@n", op.Name);
                    cmd.Parameters.AddWithValue("@f", f);
                    cmd.Parameters.AddWithValue("@t", t);
                    rows = cmd.ExecuteNonQuery();
                }
                if (rows == 0)
                {
                    throw new ApiException(ApiErrorCodes.NotFound, "$.ops[].unlink",
                        new Dictionary<string, object?> { ["name"] = op.Name, ["from"] = f, ["to"] = t });
                }
                ctx.RecordDeletedLink(new HistLink(op.Name, f, t));
            }
        }
        return EmptyTxOpResponse.Instance;
    }

    // -----------------------------------------------------------------
    // shared helpers
    // -----------------------------------------------------------------

    internal static void CreateLinkRow(TxContext ctx, string name, string fromId, string toId)
    {
        var fromScope = ctx.GetNodeScope(fromId) ?? throw new ApiException(
            ApiErrorCodes.NotFound, "$.ops[].link.from",
            new Dictionary<string, object?> { ["id"] = fromId });
        var toScope = ctx.GetNodeScope(toId) ?? throw new ApiException(
            ApiErrorCodes.NotFound, "$.ops[].link.to",
            new Dictionary<string, object?> { ["id"] = toId });
        if (!IsAllowedCrossScope(fromScope, toScope))
        {
            throw new ApiException(ApiErrorCodes.CrossScopeNotAllowed, "$.ops[].link",
                new Dictionary<string, object?>
                {
                    ["from_scope"] = fromScope,
                    ["to_scope"] = toScope,
                });
        }
        try
        {
            using var cmd = ctx.NewCommand();
            cmd.CommandText = "INSERT INTO link(graph_name, name, from_id, to_id) VALUES(@g, @n, @f, @t)";
            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@f", fromId);
            cmd.Parameters.AddWithValue("@t", toId);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new ApiException(ApiErrorCodes.AlreadyExists, "$.ops[].link",
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["from"] = fromId,
                    ["to"] = toId,
                });
        }
        ctx.RecordCreatedLink(new HistLink(name, fromId, toId));
    }

    /// <summary>
    /// Разрешённые направления per api/model.md, раздел «Cross-scope ссылки»:
    /// <c>main→main</c>, <c>usage→usage</c>, <c>usage→main</c>.
    /// </summary>
    private static bool IsAllowedCrossScope(string fromScope, string toScope)
    {
        if (fromScope == ScopeNames.Main && toScope == ScopeNames.Main) return true;
        if (fromScope == ScopeNames.Usage && toScope == ScopeNames.Usage) return true;
        if (fromScope == ScopeNames.Usage && toScope == ScopeNames.Main) return true;
        if (fromScope == ScopeNames.Scheme && toScope == ScopeNames.Scheme) return true;
        return false;
    }

    private static void ValidateTitle(string title, string path)
    {
        if (string.IsNullOrEmpty(title) || !TitleRegex.IsMatch(title))
        {
            throw new ApiException(ApiErrorCodes.InvalidNodeTitle, path,
                new Dictionary<string, object?> { ["title"] = title });
        }
    }

    private static bool PathExists(TxContext ctx, string path)
    {
        using var cmd = ctx.NewCommand();
        cmd.CommandText = "SELECT 1 FROM node WHERE graph_name = @g AND scope = @s AND LOWER(path) = LOWER(@p) LIMIT 1";
        cmd.Parameters.AddWithValue("@g", ctx.GraphName);
        cmd.Parameters.AddWithValue("@s", ScopeNames.ToWire(ctx.Scope));
        cmd.Parameters.AddWithValue("@p", path);
        return cmd.ExecuteScalar() is not null;
    }

    private static string ApplyPathParent(TxContext ctx, string path)
    {
        if (ctx.Defaults?.PathParent is not { Length: > 0 } parent)
        {
            return path;
        }
        return parent.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private static (string Parent, string Title) SplitPath(string path)
    {
        var idx = path.LastIndexOf('/');
        if (idx < 0)
        {
            return (string.Empty, path);
        }
        return (path[..idx], path[(idx + 1)..]);
    }

    private static string ParentOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? string.Empty : path[..idx];
    }

    /// <summary>
    /// Каскадно переименовывает path всех path-потомков узла, у которого
    /// path сменился с <paramref name="oldPath"/> на <paramref name="newPath"/>.
    /// Каждый потомок попадает в <c>changed.nodes</c> с собственным
    /// <c>set.path</c> и инкрементом <c>version</c> (per api/tx.md,
    /// раздел «Каскад на path-потомков»).
    /// </summary>
    private static void CascadePathChange(TxContext ctx, string oldPath, string newPath)
    {
        // Find descendants.
        var descendants = new List<(string Id, string OldPath)>();
        using (var cmd = ctx.NewCommand())
        {
            cmd.CommandText = "SELECT id, path FROM node WHERE graph_name = @g AND scope = @s AND path LIKE @p ESCAPE '\\'";
            cmd.Parameters.AddWithValue("@g", ctx.GraphName);
            cmd.Parameters.AddWithValue("@s", ScopeNames.ToWire(ctx.Scope));
            cmd.Parameters.AddWithValue("@p", EscapeLike(oldPath) + "/%");
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                descendants.Add((rdr.GetString(0), rdr.GetString(1)));
            }
        }
        foreach (var (id, oldChildPath) in descendants)
        {
            var newChildPath = newPath + oldChildPath[oldPath.Length..];
            try
            {
                using var cmd = ctx.NewCommand();
                cmd.CommandText = "UPDATE node SET path = @p WHERE graph_name = @g AND id = @id";
                cmd.Parameters.AddWithValue("@p", newChildPath);
                cmd.Parameters.AddWithValue("@g", ctx.GraphName);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (IsUniqueViolation(ex))
            {
                throw new ApiException(ApiErrorCodes.AlreadyExists, "$.ops[].cascade",
                    new Dictionary<string, object?> { ["path"] = newChildPath });
            }
            ctx.RecordChange(id, new ChangedSet(null, null, newChildPath, null));
        }
    }

    private static string EscapeLike(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '%' or '_')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    internal static bool IsUniqueViolation(SqliteException ex)
    {
        return ex.SqliteErrorCode == 19; // SQLITE_CONSTRAINT
    }
}
