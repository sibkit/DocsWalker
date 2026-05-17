using System.Data;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// In-memory snapshot одного data-узла, реконструированный на момент
/// <c>at</c>. Используется <see cref="AtReplay"/> и
/// <see cref="ReadExecutor"/> для темпоральных чтений
/// (per api/model.md, раздел «Темпоральные чтения (<c>at</c>)»).
/// </summary>
internal sealed class AtNodeSnapshot
{
    public string Id { get; }
    public string Scope { get; set; }
    public string Path { get; set; }
    public string Title { get; set; }
    public string Content { get; set; }
    public Dictionary<string, string> MapBindings { get; }

    public AtNodeSnapshot(string id, string scope, string path, string title, string content)
    {
        Id = id;
        Scope = scope;
        Path = path;
        Title = title;
        Content = content;
        MapBindings = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

/// <summary>
/// Реконструированное состояние графа на момент <c>at</c>. Все узлы
/// (всех scope) и все links — общий снимок. Фильтрация по scope запроса
/// делается на стадии выдачи (per database-model/schema.md, раздел
/// «Темпоральные чтения (<c>at</c>)»).
/// </summary>
internal sealed class AtState
{
    public Dictionary<string, AtNodeSnapshot> Nodes { get; } =
        new(StringComparer.Ordinal);

    public HashSet<AtLinkKey> Links { get; } = new();
}

internal readonly record struct AtLinkKey(string Name, string From, string To);

/// <summary>
/// Реконструирует in-memory state одного графа на момент <c>at</c>
/// через full replay <c>tx_event</c>-журнала до upper bound. Алгоритм v1
/// — replay без snapshot'ов (per database-model/schema.md, раздел
/// «Темпоральные чтения (<c>at</c>)», подраздел «Реконструкция scope
/// (полный replay)»).
/// </summary>
internal sealed class AtReplay
{
    private readonly SqliteConnection _connection;
    private readonly string _graphName;

    public AtReplay(SqliteConnection connection, string graphName)
    {
        _connection = connection;
        _graphName = graphName;
    }

    public AtState BuildState(AtClause at, SqliteTransaction tx)
    {
        var (boundDate, boundOrdinal) = ResolveBound(at, tx);

        var state = new AtState();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // Inclusive=true (short form `at: "<tx_id>"`) → upper bound включает
        // указанную tx; Inclusive=false (`{before: "<tx_id>"}`) — исключает.
        var op = at.Inclusive ? "<=" : "<";
        cmd.CommandText =
            "SELECT id, sections_json, tx_scope FROM tx_event " +
            "WHERE graph_name = @g " +
            "AND (date < @d OR (date = @d AND ordinal " + op + " @o)) " +
            "ORDER BY date, ordinal";
        cmd.Parameters.AddWithValue("@g", _graphName);
        cmd.Parameters.AddWithValue("@d", boundDate);
        cmd.Parameters.AddWithValue("@o", boundOrdinal);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var sections = HistSectionsJson.Deserialize(rdr.GetString(1));
            var txScope = rdr.GetString(2);
            ApplySections(state, sections, txScope);
        }
        return state;
    }

    private (string Date, long Ordinal) ResolveBound(AtClause at, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT date, ordinal FROM tx_event WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", _graphName);
        cmd.Parameters.AddWithValue("@id", at.TxId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read())
        {
            throw new ApiException(ApiErrorCodes.NotFound, "$.at");
        }
        return (rdr.GetString(0), rdr.GetInt64(1));
    }

    private static void ApplySections(AtState state, HistSections s, string txScope)
    {
        // Фиксированный порядок per api/hist-scope.md, раздел «Replay
        // restoration». Тот же, что в HistReplay.
        if (s.Deleted?.Links is { } deletedLinks)
        {
            foreach (var l in deletedLinks)
            {
                state.Links.Remove(new AtLinkKey(l.Name, l.From, l.To));
            }
        }
        if (s.Deleted?.Nodes is { } deletedNodes)
        {
            foreach (var n in deletedNodes)
            {
                state.Nodes.Remove(n.Id);
            }
        }
        if (s.Changed?.Nodes is { } changedNodes)
        {
            foreach (var c in changedNodes)
            {
                if (!state.Nodes.TryGetValue(c.Id, out var snap))
                {
                    // Sections-журнал нарушен (changed для удалённого/несозданного
                    // узла). На live-DB не должно случаться — пропускаем тихо.
                    continue;
                }
                if (c.Set.Title is { } title) snap.Title = title;
                if (c.Set.Content is { } content) snap.Content = content;
                if (c.Set.Path is { } path) snap.Path = path;
                if (c.Set.MapBindings is { } mb)
                {
                    foreach (var kv in mb)
                    {
                        if (kv.Value is null) snap.MapBindings.Remove(kv.Key);
                        else snap.MapBindings[kv.Key] = kv.Value;
                    }
                }
            }
        }
        if (s.Created?.Nodes is { } createdNodes)
        {
            foreach (var n in createdNodes)
            {
                var snap = new AtNodeSnapshot(n.Id, txScope, n.Path, n.Title, n.Content);
                if (n.MapBindings is { } mb)
                {
                    foreach (var kv in mb)
                    {
                        snap.MapBindings[kv.Key] = kv.Value;
                    }
                }
                state.Nodes[n.Id] = snap;
            }
        }
        if (s.Created?.Links is { } createdLinks)
        {
            foreach (var l in createdLinks)
            {
                state.Links.Add(new AtLinkKey(l.Name, l.From, l.To));
            }
        }
    }
}
