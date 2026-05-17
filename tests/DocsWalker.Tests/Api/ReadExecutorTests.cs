using DocsWalker.Core.Api;
using DocsWalker.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Tests.Api;

public sealed class ReadExecutorTests
{
    private const string Graph = "g1";

    [Fact]
    public void EmptyOps_ReturnsEmptyResponse()
    {
        using var conn = NewSeededGraph();
        var rx = new ReadExecutor(conn, Graph);
        var req = RequestParser.ParseRead("""{"ops":[]}""");

        var resp = rx.Execute(req);

        Assert.Empty(resp.Ops);
    }

    [Fact]
    public void SelectById_ReturnsNode()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "2a", "main", "DocsWalker/api/read", "read", content: "BODY");
        var rx = new ReadExecutor(conn, Graph);
        var req = RequestParser.ParseRead("""{"ops":[{"select":{"selector":{"id":"2a"}}}]}""");

        var resp = rx.Execute(req);

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(1, op.Count);
        var n = Assert.Single(op.Items);
        Assert.Equal("2a", n.Id);
        Assert.Null(n.Scope);
        Assert.Equal("DocsWalker/api/read", n.Path);
        Assert.Equal("read", n.Title);
        Assert.Null(n.Content);
        Assert.Null(n.Links);
        Assert.Equal(1L, n.Version);
    }

    [Fact]
    public void SelectByPath_DoubleStarPattern()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "DocsWalker/api/read", "read");
        InsertNode(conn, "2", "main", "DocsWalker/api/tx", "tx");
        InsertNode(conn, "3", "main", "DocsWalker/api/sub/inner", "inner");
        InsertNode(conn, "4", "main", "OtherTree/x", "x");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"path":"DocsWalker/api/**"}}}]}"""));

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(3, op.Count);
        Assert.Equal(new[] { "DocsWalker/api/read", "DocsWalker/api/sub/inner", "DocsWalker/api/tx" },
            op.Items.Select(i => i.Path).Order().ToArray());
    }

    [Fact]
    public void SelectByPath_SingleStarPattern_OneSegmentOnly()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "DocsWalker/api/read", "read");
        InsertNode(conn, "2", "main", "DocsWalker/api/tx", "tx");
        InsertNode(conn, "3", "main", "DocsWalker/api/sub/inner", "inner");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"path":"DocsWalker/api/*"}}}]}"""));

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(2, op.Count);
        Assert.DoesNotContain(op.Items, i => i.Path.Contains("sub/inner"));
    }

    [Fact]
    public void SelectByTitle_CaseInsensitive()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "DocsWalker/api/Read", "Read");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"title":"read"}}}]}"""));

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(1, op.Count);
        Assert.Equal("Read", op.Items[0].Title);
    }

    [Fact]
    public void SelectByMapBinding_Exact()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "a", "a", binding: ("category", "documents/spec"));
        InsertNode(conn, "2", "main", "b", "b", binding: ("category", "documents/draft"));
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"map_bindings":{"category":"documents/spec"}}}}]}"""));

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(1, op.Count);
        Assert.Equal("a", op.Items[0].Title);
        Assert.Equal("documents/spec", op.Items[0].MapBindings["category"]);
    }

    [Fact]
    public void SelectByMapBinding_PatternDoubleStar()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "a", "a", binding: ("category", "documents/spec"));
        InsertNode(conn, "2", "main", "b", "b", binding: ("category", "documents/draft"));
        InsertNode(conn, "3", "main", "c", "c", binding: ("category", "examples/sample"));
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"map_bindings":{"category":"documents/**"}}}}]}"""));

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(2, op.Count);
    }

    [Fact]
    public void SelectMatch_RegexOverContent()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "a", "a", content: "talks about validation_failed semantics");
        InsertNode(conn, "2", "main", "b", "b", content: "unrelated");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"match":{"regex":"validation","fields":["content"]}}}}]}"""));

        var op = Assert.IsType<SelectNodesResponse>(resp.Ops[0]);
        Assert.Equal(1, op.Count);
        Assert.Equal("a", op.Items[0].Title);
    }

    [Fact]
    public void IncludeContent_PopulatesContent()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "a", "a", content: "BODY");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"id":"1"},"include":["content"]}}]}"""));

        var op = (SelectNodesResponse)resp.Ops[0];
        Assert.Equal("BODY", op.Items[0].Content);
        Assert.Equal((4 + 3) / 4, op.Items[0].Tokens);
    }

    [Fact]
    public void IncludeLinks_PopulatesOutgoingAndIncoming()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "a", "a");
        InsertNode(conn, "2", "main", "b", "b");
        InsertNode(conn, "3", "main", "c", "c");
        InsertLink(conn, "depends_on", "1", "2");
        InsertLink(conn, "described_by", "3", "1");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"id":"1"},"include":["links"]}}]}"""));

        var op = (SelectNodesResponse)resp.Ops[0];
        var node = op.Items[0];
        Assert.NotNull(node.Links);
        Assert.Equal(2, node.Links!.Count);
        var outgoing = node.Links.Single(l => l.To is not null);
        Assert.Equal("depends_on", outgoing.Name);
        Assert.Equal("2", outgoing.To!.Id);
        Assert.Equal("b", outgoing.To.Path);
        var incoming = node.Links.Single(l => l.From is not null);
        Assert.Equal("described_by", incoming.Name);
        Assert.Equal("3", incoming.From!.Id);
    }

    [Fact]
    public void Scope_Main_NotSerialized_OtherScopesSerialized()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "a", "a");
        InsertNode(conn, "2", "usage", "rules/x", "x");
        var rx = new ReadExecutor(conn, Graph);

        var mainResp = rx.Execute(RequestParser.ParseRead("""{"ops":[{"select":{"selector":{"id":"1"}}}]}"""));
        var usageResp = rx.Execute(RequestParser.ParseRead("""{"scope":"usage","ops":[{"select":{"selector":{"id":"2"}}}]}"""));

        var main = (SelectNodesResponse)mainResp.Ops[0];
        var usage = (SelectNodesResponse)usageResp.Ops[0];
        Assert.Null(main.Items[0].Scope);
        Assert.Equal("usage", usage.Items[0].Scope);
    }

    [Fact]
    public void MaxTokens_TruncatesAfterFirstOverflow()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "1", "main", "p1", "p1", content: new string('x', 100));
        InsertNode(conn, "2", "main", "p2", "p2", content: new string('x', 100));
        InsertNode(conn, "3", "main", "p3", "p3", content: new string('x', 100));
        var rx = new ReadExecutor(conn, Graph);

        // Каждый узел ~25 токенов, лимит 30 → второй уже превышает.
        var resp = rx.Execute(RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"id":["1","2","3"]},"include":["content"],"max_tokens":30}}]}"""));

        var op = (SelectNodesResponse)resp.Ops[0];
        Assert.Equal(3, op.Count);
        Assert.True(op.Truncated);
        Assert.Single(op.Items);
        Assert.Equal(2, op.OmittedCount);
        Assert.Equal("p1", op.StoppedAt);
    }

    [Fact]
    public void SelectMeta_ReturnsStub()
    {
        using var conn = NewSeededGraph();
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead("""{"ops":[{"select":"meta"}]}"""));

        var meta = Assert.IsType<SelectMetaResponse>(resp.Ops[0]);
        Assert.Empty(meta.Meta);
    }

    [Fact]
    public void At_TemporalRead_Throws_NotSupported()
    {
        using var conn = NewSeededGraph();
        var rx = new ReadExecutor(conn, Graph);
        var req = RequestParser.ParseRead("""{"at":"deadbeef","ops":[]}""");

        Assert.Throws<NotSupportedException>(() => rx.Execute(req));
    }

    [Fact]
    public void HistSelect_TouchesNode_ReturnsCompactWithCounts()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "n1", "main", "a", "a");
        InsertTxEvent(conn, "t1", "first", "2026-05-10", 1, "main",
            sectionsJson: """{"created":{"nodes":[{"id":"n1","path":"a","title":"a","content":""}]}}""");
        InsertTxTouches(conn, "t1", "n1", "created");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"touches_node":"n1"}}}]}"""));

        var op = Assert.IsType<SelectEventsResponse>(resp.Ops[0]);
        Assert.Equal(1, op.Count);
        var e = op.Items[0];
        Assert.Equal("t1", e.Id);
        Assert.Equal("first", e.Title);
        Assert.Equal("2026-05-10", e.Date);
        Assert.Null(e.Description);
        Assert.Null(e.Created);
        Assert.NotNull(e.Counts);
        Assert.Equal(1, e.Counts!.Created!.Nodes);
        Assert.Null(e.Counts.Created.Links);
        Assert.Null(e.Counts.Changed);
        Assert.Null(e.Counts.Deleted);
    }

    [Fact]
    public void HistSelect_IncludeCreated_ReturnsSection()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "n1", "main", "a", "a");
        InsertTxEvent(conn, "t1", "first", "2026-05-10", 1, "main",
            sectionsJson: """{"created":{"nodes":[{"id":"n1","path":"a","title":"a","content":""}]}}""");
        InsertTxTouches(conn, "t1", "n1", "created");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"id":"t1"},"include":["created"]}}]}"""));

        var op = (SelectEventsResponse)resp.Ops[0];
        Assert.NotNull(op.Items[0].Created);
        Assert.NotNull(op.Items[0].Created!.Nodes);
        Assert.Equal("n1", op.Items[0].Created!.Nodes![0].Id);
    }

    [Fact]
    public void HistSelect_DateRegex_Matches()
    {
        using var conn = NewSeededGraph();
        InsertTxEvent(conn, "t1", "x", "2026-05-10", 1, "main", sectionsJson: """{}""");
        InsertTxEvent(conn, "t2", "y", "2026-05-15", 1, "main", sectionsJson: """{}""");
        InsertTxEvent(conn, "t3", "z", "2026-06-01", 1, "main", sectionsJson: """{}""");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"date":{"match":{"regex":"^2026-05-1[0-7]$"}}}}}]}"""));

        var op = (SelectEventsResponse)resp.Ops[0];
        Assert.Equal(2, op.Count);
    }

    [Fact]
    public void HistSelect_RollbackOf()
    {
        using var conn = NewSeededGraph();
        InsertTxEvent(conn, "t1", "src", "2026-05-10", 1, "main", sectionsJson: """{}""");
        InsertTxEvent(conn, "t2", "comp", "2026-05-11", 1, "main", sectionsJson: """{}""", rollbackOf: "t1");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"rollback_of":"t1"}}}]}"""));

        var op = (SelectEventsResponse)resp.Ops[0];
        Assert.Equal(1, op.Count);
        Assert.Equal("t2", op.Items[0].Id);
        Assert.Equal("t1", op.Items[0].RollbackOf);
    }

    [Fact]
    public void NestedLinksSelector_ReturnsByOutgoingTarget()
    {
        using var conn = NewSeededGraph();
        InsertNode(conn, "a", "main", "src/a", "a");
        InsertNode(conn, "b", "main", "tgt/b", "b", binding: ("category", "documents/spec"));
        InsertNode(conn, "c", "main", "tgt/c", "c", binding: ("category", "documents/draft"));
        InsertLink(conn, "depends_on", "a", "b");
        var rx = new ReadExecutor(conn, Graph);

        var resp = rx.Execute(RequestParser.ParseRead("""
            {"ops":[{"select":{"selector":{"links":{
                "name":"depends_on",
                "to":{"map_bindings":{"category":"documents/spec"}}}}}}]}
            """));

        var op = (SelectNodesResponse)resp.Ops[0];
        Assert.Equal(1, op.Count);
        Assert.Equal("a", op.Items[0].Title);
    }

    // ---------- seed helpers ----------

    private static SqliteConnection NewSeededGraph()
    {
        var name = "rxt_" + Guid.NewGuid().ToString("N");
        var store = SqliteStore.ForSharedInMemory(name);
        var conn = store.Open();
        SqliteStore.Bootstrap(conn);
        InsertGraph(conn, Graph);
        return conn;
    }

    private static void InsertGraph(SqliteConnection conn, string name)
    {
        Exec(conn, "INSERT INTO graph(name) VALUES(@n)", ("@n", name));
        Exec(conn, "INSERT INTO sequence(graph_name, next_id) VALUES(@n, 1)", ("@n", name));
    }

    private static void InsertNode(SqliteConnection conn, string id, string scope, string path, string title,
        string content = "", long version = 1, (string key, string value)? binding = null)
    {
        Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title, content, version) VALUES(@g, @id, @s, @p, @t, @c, @v)",
            ("@g", Graph), ("@id", id), ("@s", scope), ("@p", path), ("@t", title), ("@c", content), ("@v", version));
        if (binding is { } b)
        {
            Exec(conn,
                "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v)",
                ("@g", Graph), ("@n", id), ("@m", b.key), ("@v", b.value));
        }
    }

    private static void InsertLink(SqliteConnection conn, string name, string from, string to)
    {
        Exec(conn,
            "INSERT INTO link(graph_name, name, from_id, to_id) VALUES(@g, @n, @f, @t)",
            ("@g", Graph), ("@n", name), ("@f", from), ("@t", to));
    }

    private static void InsertTxEvent(SqliteConnection conn, string id, string title, string date,
        long ordinal, string txScope, string sectionsJson, string? description = null, string? rollbackOf = null)
    {
        Exec(conn,
            "INSERT INTO tx_event(graph_name, id, title, date, description, rollback_of, tx_scope, ordinal, sections_json) " +
            "VALUES(@g, @id, @t, @d, @desc, @ro, @s, @o, @j)",
            ("@g", Graph), ("@id", id), ("@t", title), ("@d", date),
            ("@desc", (object?)description ?? DBNull.Value),
            ("@ro", (object?)rollbackOf ?? DBNull.Value),
            ("@s", txScope), ("@o", ordinal), ("@j", sectionsJson));
    }

    private static void InsertTxTouches(SqliteConnection conn, string txId, string nodeId, string role)
    {
        Exec(conn,
            "INSERT INTO tx_touches_node(graph_name, tx_id, node_id, role) VALUES(@g, @t, @n, @r)",
            ("@g", Graph), ("@t", txId), ("@n", nodeId), ("@r", role));
    }

    private static void Exec(SqliteConnection conn, string sql, params (string name, object value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = p.name;
            prm.Value = p.value;
            cmd.Parameters.Add(prm);
        }
        cmd.ExecuteNonQuery();
    }
}
