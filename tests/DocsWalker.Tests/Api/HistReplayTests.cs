using DocsWalker.Core.Api;
using DocsWalker.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Tests.Api;

public sealed class HistReplayTests
{
    private const string Graph = "g1";
    private static readonly DateTime FixedUtc = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Replay_RecreatesCreatedNodesAndBindings()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);
        tx.Execute(RequestParser.ParseTx("""
            {"title":"t","ops":[{"create":{"path":"a","set":{
                "content":"BODY","map_bindings":{"category":"documents/spec"}}}}]}
            """));

        // Wipe data tables and replay.
        ClearData(conn);
        HistReplay.Replay(conn, Graph);

        Assert.True(NodeExists(conn, "1"));
        Assert.Equal("BODY", GetNodeContent(conn, "1"));
        Assert.Equal("documents/spec", GetMapBinding(conn, "1", "category"));
    }

    [Fact]
    public void Replay_HandlesUpdateAndCascade()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);
        // Note: sequence выдаёт id и узлам, и tx_event (1=root, 2=tx_event1,
        // 3=root/child, 4=tx_event2). update нацелен на id="1" (root).
        tx.Execute(RequestParser.ParseTx("""{"title":"c1","ops":[{"create":{"path":"root"}}]}"""));
        tx.Execute(RequestParser.ParseTx("""{"title":"c2","ops":[{"create":{"path":"root/child"}}]}"""));
        tx.Execute(RequestParser.ParseTx("""
            {"title":"rename","ops":[{"update":{
                "id":"1","expected_version":1,"set":{"title":"renamed"}}}]}
            """));

        ClearData(conn);
        HistReplay.Replay(conn, Graph);

        Assert.Equal("renamed", GetNodeTitle(conn, "1"));
        Assert.Equal("renamed", GetNodePath(conn, "1"));
        Assert.Equal("renamed/child", GetNodePath(conn, "3"));
    }

    [Fact]
    public void Replay_HandlesMoveWithPartialMerge()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);
        tx.Execute(RequestParser.ParseTx("""
            {"title":"c","ops":[{"create":{"path":"x","set":{
                "map_bindings":{"category":"documents/draft","old":"drop"}}}}]}
            """));
        tx.Execute(RequestParser.ParseTx("""
            {"title":"mv","ops":[{"move":{
                "selector":{"id":"1"},
                "to":{"map_bindings":{"category":"documents/spec","old":null}},
                "expected_count":1}}]}
            """));

        ClearData(conn);
        HistReplay.Replay(conn, Graph);

        Assert.Equal("documents/spec", GetMapBinding(conn, "1", "category"));
        Assert.Null(GetMapBinding(conn, "1", "old"));
    }

    [Fact]
    public void Replay_HandlesDeleteAndLinks()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);
        // id-выдача: 1=a, 2=tx_event(a), 3=b, 4=tx_event(b), ...
        tx.Execute(RequestParser.ParseTx("""{"title":"a","ops":[{"create":{"path":"a"}}]}"""));
        tx.Execute(RequestParser.ParseTx("""{"title":"b","ops":[{"create":{"path":"b"}}]}"""));
        tx.Execute(RequestParser.ParseTx("""
            {"title":"l","ops":[{"link":{"name":"depends_on","from":"1","to":"3","expected_count":1}}]}
            """));
        tx.Execute(RequestParser.ParseTx("""
            {"title":"d","ops":[{"delete":{"ids":["1"],"expected_count":1}}]}
            """));

        ClearData(conn);
        HistReplay.Replay(conn, Graph);

        Assert.False(NodeExists(conn, "1"));
        Assert.True(NodeExists(conn, "3"));
        Assert.False(LinkExists(conn, "depends_on", "1", "3"));
    }

    [Fact]
    public void Replay_PreservesRollbackEffects()
    {
        using var conn = NewSeededGraph();
        var tx1 = new TxExecutor(conn, Graph, () => FixedUtc);
        var first = tx1.Execute(RequestParser.ParseTx("""{"title":"c","ops":[{"create":{"path":"a"}}]}"""));
        var tx2 = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1));
        tx2.Execute(RequestParser.ParseTx($$"""{"title":"rb","ops":[{"rollback":"{{first.Id}}"}]}"""));

        ClearData(conn);
        HistReplay.Replay(conn, Graph);

        Assert.False(NodeExists(conn, "1"));
    }

    [Fact]
    public void Replay_ResetsSequenceToMaxIdPlusOne()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);
        tx.Execute(RequestParser.ParseTx("""{"title":"c1","ops":[{"create":{"path":"a"}}]}"""));
        tx.Execute(RequestParser.ParseTx("""{"title":"c2","ops":[{"create":{"path":"b"}}]}"""));
        // After two creates + two events: ids 1..4 used. sequence.next_id = 5.

        ClearData(conn);
        HistReplay.Replay(conn, Graph);

        Assert.Equal(5L, GetSequence(conn));
        // Verify next create gets id 5.
        var tx3 = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1));
        var resp = tx3.Execute(RequestParser.ParseTx("""{"title":"c3","ops":[{"create":{"path":"c"}}]}"""));
        Assert.Equal("5", ((CreateOpResponse)resp.Ops[0]).Id);
    }

    [Fact]
    public void Replay_Idempotent()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);
        tx.Execute(RequestParser.ParseTx("""{"title":"c","ops":[{"create":{"path":"a"}}]}"""));

        HistReplay.Replay(conn, Graph);
        var pathAfterFirst = GetNodePath(conn, "1");
        HistReplay.Replay(conn, Graph);
        var pathAfterSecond = GetNodePath(conn, "1");

        Assert.Equal(pathAfterFirst, pathAfterSecond);
    }

    // ---------- helpers ----------

    private static SqliteConnection NewSeededGraph()
    {
        var name = "rpl_" + Guid.NewGuid().ToString("N");
        var store = SqliteStore.ForSharedInMemory(name);
        var conn = store.Open();
        SqliteStore.Bootstrap(conn);
        Exec(conn, "INSERT INTO graph(name) VALUES(@n)", ("@n", Graph));
        Exec(conn, "INSERT INTO sequence(graph_name, next_id) VALUES(@n, 1)", ("@n", Graph));
        return conn;
    }

    private static void ClearData(SqliteConnection conn)
    {
        Exec(conn, "DELETE FROM link WHERE graph_name = @g", ("@g", Graph));
        Exec(conn, "DELETE FROM node_map_binding WHERE graph_name = @g", ("@g", Graph));
        Exec(conn, "DELETE FROM node WHERE graph_name = @g", ("@g", Graph));
        Exec(conn, "UPDATE sequence SET next_id = 1 WHERE graph_name = @g", ("@g", Graph));
    }

    private static bool NodeExists(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM node WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", Graph);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static string GetNodePath(SqliteConnection conn, string id) =>
        (string)QueryScalar(conn, "SELECT path FROM node WHERE graph_name = @g AND id = @id", ("@id", id))!;

    private static string GetNodeTitle(SqliteConnection conn, string id) =>
        (string)QueryScalar(conn, "SELECT title FROM node WHERE graph_name = @g AND id = @id", ("@id", id))!;

    private static string GetNodeContent(SqliteConnection conn, string id) =>
        (string)QueryScalar(conn, "SELECT content FROM node WHERE graph_name = @g AND id = @id", ("@id", id))!;

    private static string? GetMapBinding(SqliteConnection conn, string id, string key) =>
        QueryScalar(conn, "SELECT branch_path FROM node_map_binding WHERE graph_name = @g AND node_id = @id AND map_name = @m",
            ("@id", id), ("@m", key)) as string;

    private static bool LinkExists(SqliteConnection conn, string name, string from, string to)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM link WHERE graph_name = @g AND name = @n AND from_id = @f AND to_id = @t";
        cmd.Parameters.AddWithValue("@g", Graph);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@f", from);
        cmd.Parameters.AddWithValue("@t", to);
        return cmd.ExecuteScalar() is not null;
    }

    private static long GetSequence(SqliteConnection conn) =>
        Convert.ToInt64(QueryScalar(conn, "SELECT next_id FROM sequence WHERE graph_name = @g")!);

    private static object? QueryScalar(SqliteConnection conn, string sql, params (string n, object v)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@g", Graph);
        foreach (var p in ps)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = p.n;
            prm.Value = p.v;
            cmd.Parameters.Add(prm);
        }
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection conn, string sql, params (string n, object v)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = p.n;
            prm.Value = p.v;
            cmd.Parameters.Add(prm);
        }
        cmd.ExecuteNonQuery();
    }
}
