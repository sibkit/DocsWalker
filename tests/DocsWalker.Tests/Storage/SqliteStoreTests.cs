using DocsWalker.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Tests.Storage;

public sealed class SqliteStoreTests
{
    [Fact]
    public void Bootstrap_CreatesAllTables()
    {
        using var conn = OpenFresh();

        var tables = ListMaster(conn, "table");

        Assert.Contains("graph", tables);
        Assert.Contains("sequence", tables);
        Assert.Contains("node", tables);
        Assert.Contains("node_map_binding", tables);
        Assert.Contains("link", tables);
        Assert.Contains("tx_event", tables);
        Assert.Contains("tx_touches_node", tables);
        Assert.Contains("tx_touches_link", tables);
    }

    [Fact]
    public void Bootstrap_CreatesAllIndexes()
    {
        using var conn = OpenFresh();

        var indexes = ListMaster(conn, "index");

        Assert.Contains("node_path", indexes);
        Assert.Contains("node_path_lower", indexes);
        Assert.Contains("node_scope", indexes);
        Assert.Contains("node_map_binding_by_map", indexes);
        Assert.Contains("link_by_from", indexes);
        Assert.Contains("link_by_to", indexes);
        Assert.Contains("tx_event_date", indexes);
        Assert.Contains("tx_event_rollback_of", indexes);
        Assert.Contains("tx_event_tx_scope", indexes);
        Assert.Contains("tx_event_date_ordinal", indexes);
        Assert.Contains("tx_touches_node_by_node", indexes);
        Assert.Contains("tx_touches_link_by_link", indexes);
    }

    [Fact]
    public void Pragmas_AppliedOnOpen()
    {
        using var conn = OpenFresh();

        Assert.Equal(1L, Scalar(conn, "PRAGMA foreign_keys"));
        // case_sensitive_like write-only — проверяем через поведение LIKE.
        Assert.Equal(0L, Scalar(conn, "SELECT 'A' LIKE 'a'"));
        Assert.Equal(1L, Scalar(conn, "SELECT 'a' LIKE 'a'"));
    }

    [Fact]
    public void Bootstrap_IsIdempotent()
    {
        using var conn = OpenFresh();

        SqliteStore.Bootstrap(conn);
        SqliteStore.Bootstrap(conn);

        var tables = ListMaster(conn, "table");
        Assert.Contains("node", tables);
    }

    [Fact]
    public void RegexMatch_RegisteredAndMatchesCaseInsensitive()
    {
        using var conn = OpenFresh();

        Assert.Equal(1L, Scalar(conn, "SELECT regex_match('Hello World', 'hello', 0)"));
        Assert.Equal(0L, Scalar(conn, "SELECT regex_match('Hello World', 'hello', 1)"));
        Assert.Equal(1L, Scalar(conn, "SELECT regex_match('abc123', '^[a-z]+\\d+$', 1)"));
        Assert.Equal(0L, Scalar(conn, "SELECT regex_match('abc', '^xyz$', 0)"));
    }

    [Fact]
    public void RegexMatch_NullArgumentsReturnNull()
    {
        using var conn = OpenFresh();

        Assert.Equal(DBNull.Value, ScalarRaw(conn, "SELECT regex_match(NULL, 'foo', 0)"));
        Assert.Equal(DBNull.Value, ScalarRaw(conn, "SELECT regex_match('foo', NULL, 0)"));
    }

    [Fact]
    public void NodePathLower_EnforcesCaseInsensitiveUniqueness()
    {
        using var conn = OpenFresh();

        Exec(conn, "INSERT INTO graph(name) VALUES('g')");
        Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title) " +
            "VALUES('g', '1', 'main', 'A/B/c', 'c')");

        var ex = Assert.Throws<SqliteException>(() => Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title) " +
            "VALUES('g', '2', 'main', 'a/b/c', 'c')"));
        Assert.Contains("node_path_lower", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodeScopeCheck_RejectsInvalidScope()
    {
        using var conn = OpenFresh();

        Exec(conn, "INSERT INTO graph(name) VALUES('g')");

        Assert.Throws<SqliteException>(() => Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title) " +
            "VALUES('g', '1', 'hist', 'x', 'x')"));
    }

    [Fact]
    public void ForeignKey_NodeRequiresGraph()
    {
        using var conn = OpenFresh();

        var ex = Assert.Throws<SqliteException>(() => Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title) " +
            "VALUES('missing-graph', '1', 'main', 'x', 'x')"));
        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodeMapBinding_CascadesOnNodeDelete()
    {
        using var conn = OpenFresh();

        Exec(conn, "INSERT INTO graph(name) VALUES('g')");
        Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title) " +
            "VALUES('g', '1', 'main', 'x', 'x')");
        Exec(conn,
            "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) " +
            "VALUES('g', '1', 'category', 'documents/spec')");

        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM node_map_binding"));

        Exec(conn, "DELETE FROM node WHERE id='1'");

        Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM node_map_binding"));
    }

    [Fact]
    public void TxEventDateOrdinal_IsUnique()
    {
        using var conn = OpenFresh();

        Exec(conn, "INSERT INTO graph(name) VALUES('g')");
        Exec(conn,
            "INSERT INTO tx_event(graph_name, id, title, date, tx_scope, ordinal, sections_json) " +
            "VALUES('g', 'a', 't', '2026-05-18', 'main', 1, '{}')");

        Assert.Throws<SqliteException>(() => Exec(conn,
            "INSERT INTO tx_event(graph_name, id, title, date, tx_scope, ordinal, sections_json) " +
            "VALUES('g', 'b', 't', '2026-05-18', 'main', 1, '{}')"));
    }

    private static SqliteConnection OpenFresh()
    {
        var store = SqliteStore.ForSharedInMemory("test-" + Guid.NewGuid().ToString("N"));
        var conn = store.Open();
        SqliteStore.Bootstrap(conn);
        return conn;
    }

    private static IReadOnlyList<string> ListMaster(SqliteConnection conn, string type)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%'";
        cmd.Parameters.AddWithValue("$type", type);
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        var result = ScalarRaw(conn, sql);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? ScalarRaw(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
