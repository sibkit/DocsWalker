using DocsWalker.Core.Api;
using DocsWalker.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Tests.Api;

public sealed class TxExecutorTests
{
    private const string Graph = "g1";
    private static readonly DateTime FixedUtc = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    // ---------- create ----------

    [Fact]
    public void Create_Minimal_ReturnsId_InsertsNode_WritesHist()
    {
        using var conn = NewSeededGraph();
        var tx = NewExecutor(conn);

        var req = ParseTx("""{"title":"add x","ops":[{"create":{"path":"x"}}]}""");
        var resp = tx.Execute(req);

        var createResp = Assert.IsType<CreateOpResponse>(resp.Ops[0]);
        Assert.Equal("1", createResp.Id);
        var node = GetNode(conn, "1");
        Assert.Equal("main", node.scope);
        Assert.Equal("x", node.path);
        Assert.Equal("x", node.title);
        Assert.Equal("", node.content);
        Assert.Equal(1L, node.version);

        var ev = GetEvent(conn, resp.Id);
        Assert.Equal("add x", ev.title);
        Assert.Equal("2026-05-18", ev.date);
        Assert.Equal("main", ev.txScope);
        var sections = HistSectionsJson.Deserialize(ev.sectionsJson);
        var created = Assert.Single(sections.Created!.Nodes!);
        Assert.Equal("1", created.Id);
        Assert.Equal("x", created.Path);
    }

    [Fact]
    public void Create_FullSet_StoresContentBindingsAndOutgoingLink()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "tgt/a", "a");
        var tx = NewExecutor(conn);

        var req = ParseTx("""
            {"title":"t","ops":[{"create":{
                "path":"DocsWalker/api/selectors",
                "set":{"content":"BODY",
                       "map_bindings":{"category":"documents/spec"},
                       "links":[{"name":"depends_on","to":"11"}]}}}]}
            """);
        SeedRawNode(conn, "_p1", "main", "DocsWalker", "DocsWalker");
        SeedRawNode(conn, "_p2", "main", "DocsWalker/api", "api");
        var resp = tx.Execute(req);

        var id = ((CreateOpResponse)resp.Ops[0]).Id;
        var node = GetNode(conn, id);
        Assert.Equal("DocsWalker/api/selectors", node.path);
        Assert.Equal("selectors", node.title);
        Assert.Equal("BODY", node.content);
        Assert.Equal("documents/spec", GetMapBinding(conn, id, "category"));
        Assert.True(LinkExists(conn, "depends_on", id, "11"));

        var ev = GetEvent(conn, resp.Id);
        var sections = HistSectionsJson.Deserialize(ev.sectionsJson);
        Assert.Single(sections.Created!.Nodes!);
        Assert.Single(sections.Created.Links!);
    }

    [Fact]
    public void Create_DuplicatePath_AlreadyExists()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "x", "x");
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(
            ParseTx("""{"title":"t","ops":[{"create":{"path":"x"}}]}""")));
        Assert.Equal(ApiErrorCodes.AlreadyExists, ex.Code);
    }

    [Fact]
    public void Create_ParentMissing_PathParentNotFound()
    {
        using var conn = NewSeededGraph();
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(
            ParseTx("""{"title":"t","ops":[{"create":{"path":"missing/child"}}]}""")));
        Assert.Equal(ApiErrorCodes.PathParentNotFound, ex.Code);
    }

    [Fact]
    public void Create_Alias_UsableInLaterLink()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "tgt", "tgt");
        var tx = NewExecutor(conn);

        var req = ParseTx("""
            {"title":"t","ops":[
                {"create":{"path":"src","as":"src"}},
                {"link":{"name":"depends_on","from":{"alias":"src"},"to":"11","expected_count":1}}
            ]}
            """);
        var resp = tx.Execute(req);

        var srcId = ((CreateOpResponse)resp.Ops[0]).Id;
        Assert.True(LinkExists(conn, "depends_on", srcId, "11"));
    }

    [Fact]
    public void Create_DefaultsMapBindings_UnderlayUserValues()
    {
        using var conn = NewSeededGraph();
        var tx = NewExecutor(conn);

        var req = ParseTx("""
            {"title":"t",
             "defaults":{"map_bindings":{"category":"documents/spec","audience":"all"}},
             "ops":[{"create":{"path":"x","set":{"map_bindings":{"audience":"llm-agent"}}}}]}
            """);
        var resp = tx.Execute(req);

        var id = ((CreateOpResponse)resp.Ops[0]).Id;
        Assert.Equal("documents/spec", GetMapBinding(conn, id, "category"));
        Assert.Equal("llm-agent", GetMapBinding(conn, id, "audience"));
    }

    // ---------- update ----------

    [Fact]
    public void Update_Title_BumpsVersion_RecordsPathChange()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "DocsWalker/old", "old");
        SeedRawNode(conn, "_p", "main", "DocsWalker", "DocsWalker");
        var tx = NewExecutor(conn);

        var resp = tx.Execute(ParseTx("""
            {"title":"rename","ops":[{"update":{
                "id":"11","expected_version":1,"set":{"title":"new"}}}]}
            """));

        var node = GetNode(conn, "11");
        Assert.Equal("new", node.title);
        Assert.Equal("DocsWalker/new", node.path);
        Assert.Equal(2L, node.version);

        var sections = HistSectionsJson.Deserialize(GetEvent(conn, resp.Id).sectionsJson);
        var change = Assert.Single(sections.Changed!.Nodes);
        Assert.Equal("11", change.Id);
        Assert.Equal("new", change.Set.Title);
        Assert.Equal("DocsWalker/new", change.Set.Path);
    }

    [Fact]
    public void Update_Title_CascadesDescendantPaths()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "p", "main", "root/parent", "parent");
        SeedRawNode(conn, "_r", "main", "root", "root");
        SeedRawNode(conn, "c1", "main", "root/parent/child", "child");
        SeedRawNode(conn, "c2", "main", "root/parent/child/inner", "inner");
        var tx = NewExecutor(conn);

        var resp = tx.Execute(ParseTx("""
            {"title":"rename","ops":[{"update":{
                "id":"p","expected_version":1,"set":{"title":"renamed"}}}]}
            """));

        Assert.Equal("root/renamed", GetNode(conn, "p").path);
        Assert.Equal("root/renamed/child", GetNode(conn, "c1").path);
        Assert.Equal("root/renamed/child/inner", GetNode(conn, "c2").path);
        Assert.Equal(2L, GetNode(conn, "c1").version);

        var sections = HistSectionsJson.Deserialize(GetEvent(conn, resp.Id).sectionsJson);
        Assert.Equal(3, sections.Changed!.Nodes.Count); // parent + 2 descendants
    }

    [Fact]
    public void Update_VersionMismatch()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "x", "x", version: 5);
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"update":{"id":"11","expected_version":3,"set":{"content":"y"}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.VersionMismatch, ex.Code);
        Assert.Equal(5L, ex.Details.Extras!["current"]);
    }

    [Fact]
    public void Update_NotFound()
    {
        using var conn = NewSeededGraph();
        var tx = NewExecutor(conn);
        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"update":{"id":"deadbeef","expected_version":1,"set":{"content":"y"}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void Update_SameValue_NoOp_NoVersionBump()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "x", "x", content: "same");
        var tx = NewExecutor(conn);

        var resp = tx.Execute(ParseTx("""
            {"title":"t","ops":[{"update":{"id":"11","expected_version":1,"set":{"content":"same"}}}]}
            """));

        Assert.Equal(1L, GetNode(conn, "11").version);
        var sections = HistSectionsJson.Deserialize(GetEvent(conn, resp.Id).sectionsJson);
        Assert.Null(sections.Changed); // no changes recorded
    }

    // ---------- move ----------

    [Fact]
    public void Move_Parent_UpdatesPath_CascadesDescendants()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "_o", "main", "old", "old");
        SeedRawNode(conn, "_n", "main", "new", "new");
        SeedRawNode(conn, "x", "main", "old/x", "x");
        SeedRawNode(conn, "c", "main", "old/x/child", "child");
        var tx = NewExecutor(conn);

        tx.Execute(ParseTx("""
            {"title":"mv","ops":[{"move":{
                "selector":{"id":"x"},
                "to":{"parent_path":"new"},
                "expected_count":1}}]}
            """));

        Assert.Equal("new/x", GetNode(conn, "x").path);
        Assert.Equal("new/x/child", GetNode(conn, "c").path);
    }

    [Fact]
    public void Move_MapBindings_PartialMerge_TombstoneAndSet()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "x", "main", "x", "x");
        SeedBinding(conn, "x", "category", "documents/draft");
        SeedBinding(conn, "x", "old", "drop");
        var tx = NewExecutor(conn);

        tx.Execute(ParseTx("""
            {"title":"mv","ops":[{"move":{
                "selector":{"id":"x"},
                "to":{"map_bindings":{"category":"documents/spec","old":null,"audience":"llm-agent"}},
                "expected_count":1}}]}
            """));

        Assert.Equal("documents/spec", GetMapBinding(conn, "x", "category"));
        Assert.Equal("llm-agent", GetMapBinding(conn, "x", "audience"));
        Assert.Null(GetMapBinding(conn, "x", "old"));
        Assert.Equal(2L, GetNode(conn, "x").version);
    }

    [Fact]
    public void Move_CountMismatch()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "x", "main", "a", "a");
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"move":{"selector":{"path":"a"},"to":{"map_bindings":{"k":"v"}},"expected_count":5}}]}
            """)));
        Assert.Equal(ApiErrorCodes.CountMismatch, ex.Code);
    }

    [Fact]
    public void Move_NoOp_DoesNotBumpVersion()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "x", "main", "p/x", "x");
        SeedRawNode(conn, "_p", "main", "p", "p");
        SeedBinding(conn, "x", "category", "documents/spec");
        var tx = NewExecutor(conn);

        var resp = tx.Execute(ParseTx("""
            {"title":"noop","ops":[{"move":{
                "selector":{"id":"x"},
                "to":{"parent_path":"p","map_bindings":{"category":"documents/spec"}},
                "expected_count":1}}]}
            """));

        Assert.Equal(1L, GetNode(conn, "x").version);
        var sections = HistSectionsJson.Deserialize(GetEvent(conn, resp.Id).sectionsJson);
        Assert.Null(sections.Changed);
    }

    // ---------- delete ----------

    [Fact]
    public void Delete_ById_RemovesNodeAndIncidentLinks()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "x", "main", "x", "x");
        SeedRawNode(conn, "y", "main", "y", "y");
        SeedLink(conn, "depends_on", "x", "y");
        var tx = NewExecutor(conn);

        var resp = tx.Execute(ParseTx("""
            {"title":"t","ops":[{"delete":{"ids":["x"],"expected_count":1}}]}
            """));

        Assert.False(NodeExists(conn, "x"));
        Assert.False(LinkExists(conn, "depends_on", "x", "y"));

        var sections = HistSectionsJson.Deserialize(GetEvent(conn, resp.Id).sectionsJson);
        Assert.Single(sections.Deleted!.Nodes!);
        Assert.Single(sections.Deleted.Links!);
    }

    [Fact]
    public void Delete_BlockedByUsageMainLink()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "x", "main", "x", "x");
        SeedRawNode(conn, "u", "usage", "rules/r", "r");
        SeedLink(conn, "applies_to", "u", "x");
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"delete":{"ids":["x"],"expected_count":1}}]}
            """)));
        Assert.Equal(ApiErrorCodes.DeleteBlockedByCrossScopeLink, ex.Code);
    }

    // ---------- link / unlink ----------

    [Fact]
    public void Link_CrossProduct_CreatesAll()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "a", "main", "a", "a");
        SeedRawNode(conn, "b", "main", "b", "b");
        SeedRawNode(conn, "c", "main", "c", "c");
        var tx = NewExecutor(conn);

        tx.Execute(ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"depends_on","from":{"ids":["a","b"]},"to":"c","expected_count":2}}]}
            """));

        Assert.True(LinkExists(conn, "depends_on", "a", "c"));
        Assert.True(LinkExists(conn, "depends_on", "b", "c"));
    }

    [Fact]
    public void Link_AlreadyExists()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "a", "main", "a", "a");
        SeedRawNode(conn, "b", "main", "b", "b");
        SeedLink(conn, "depends_on", "a", "b");
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"depends_on","from":"a","to":"b","expected_count":1}}]}
            """)));
        Assert.Equal(ApiErrorCodes.AlreadyExists, ex.Code);
    }

    [Fact]
    public void Link_CrossScope_MainToUsage_NotAllowed()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "a", "main", "a", "a");
        SeedRawNode(conn, "u", "usage", "rules/r", "r");
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"x","from":"a","to":"u","expected_count":1}}]}
            """)));
        Assert.Equal(ApiErrorCodes.CrossScopeNotAllowed, ex.Code);
    }

    [Fact]
    public void Link_UsageToMain_Allowed()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "a", "main", "a", "a");
        SeedRawNode(conn, "u", "usage", "rules/r", "r");
        var tx = NewExecutor(conn, Scope.Usage);

        tx.Execute(ParseTx("""
            {"scope":"usage","title":"t","ops":[{"link":{
                "name":"applies_to","from":"u","to":"a","expected_count":1}}]}
            """));

        Assert.True(LinkExists(conn, "applies_to", "u", "a"));
    }

    [Fact]
    public void Unlink_RemovesRow()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "a", "main", "a", "a");
        SeedRawNode(conn, "b", "main", "b", "b");
        SeedLink(conn, "x", "a", "b");
        var tx = NewExecutor(conn);

        tx.Execute(ParseTx("""
            {"title":"t","ops":[{"unlink":{
                "name":"x","from":"a","to":"b","expected_count":1}}]}
            """));

        Assert.False(LinkExists(conn, "x", "a", "b"));
    }

    [Fact]
    public void Unlink_NotFound()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "a", "main", "a", "a");
        SeedRawNode(conn, "b", "main", "b", "b");
        var tx = NewExecutor(conn);

        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[{"unlink":{
                "name":"x","from":"a","to":"b","expected_count":1}}]}
            """)));
        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    // ---------- rollback ----------

    [Fact]
    public void Rollback_OfCreate_DeletesNode()
    {
        using var conn = NewSeededGraph();
        var tx1 = NewExecutor(conn);
        var first = tx1.Execute(ParseTx("""{"title":"create","ops":[{"create":{"path":"x"}}]}"""));
        var createdId = ((CreateOpResponse)first.Ops[0]).Id;

        var tx2 = NewExecutor(conn, utc: FixedUtc.AddDays(1));
        var rb = tx2.Execute(ParseTx($$"""{"title":"rb","ops":[{"rollback":"{{first.Id}}"}]}"""));

        Assert.False(NodeExists(conn, createdId));
        Assert.Equal(first.Id, GetEvent(conn, rb.Id).rollbackOf);
    }

    [Fact]
    public void Rollback_OfUpdate_RestoresContent()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "x", "main", "x", "x", content: "before");
        // First, write a creation-tx into hist so reconstruction has a baseline.
        SeedHistCreatedNode(conn, txId: "t0", date: "2026-05-01", ordinal: 1, txScope: "main",
            node: new CreatedNode("x", "x", "x", "before", null));

        var tx1 = NewExecutor(conn, utc: FixedUtc);
        var first = tx1.Execute(ParseTx("""
            {"title":"upd","ops":[{"update":{"id":"x","expected_version":1,"set":{"content":"after"}}}]}
            """));
        Assert.Equal("after", GetNode(conn, "x").content);

        var tx2 = NewExecutor(conn, utc: FixedUtc.AddDays(1));
        tx2.Execute(ParseTx($$"""{"title":"rb","ops":[{"rollback":"{{first.Id}}"}]}"""));

        Assert.Equal("before", GetNode(conn, "x").content);
    }

    [Fact]
    public void Rollback_NotFound()
    {
        using var conn = NewSeededGraph();
        var tx = NewExecutor(conn);
        var ex = Assert.Throws<ApiException>(() => tx.Execute(ParseTx(
            """{"title":"t","ops":[{"rollback":"deadbeef"}]}""")));
        Assert.Equal(ApiErrorCodes.RollbackNotFound, ex.Code);
    }

    [Fact]
    public void Rollback_AlreadyDone()
    {
        using var conn = NewSeededGraph();
        var tx1 = NewExecutor(conn);
        var first = tx1.Execute(ParseTx("""{"title":"c","ops":[{"create":{"path":"x"}}]}"""));
        var tx2 = NewExecutor(conn, utc: FixedUtc.AddDays(1));
        tx2.Execute(ParseTx($$"""{"title":"rb","ops":[{"rollback":"{{first.Id}}"}]}"""));

        var tx3 = NewExecutor(conn, utc: FixedUtc.AddDays(2));
        var ex = Assert.Throws<ApiException>(() => tx3.Execute(ParseTx(
            $$"""{"title":"rb again","ops":[{"rollback":"{{first.Id}}"}]}""")));
        Assert.Equal(ApiErrorCodes.RollbackAlreadyDone, ex.Code);
    }

    // ---------- atomicity ----------

    [Fact]
    public void FailedOp_RollsBackEntireTx()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "11", "main", "existing", "existing");
        var tx = NewExecutor(conn);

        Assert.Throws<ApiException>(() => tx.Execute(ParseTx("""
            {"title":"t","ops":[
                {"create":{"path":"a"}},
                {"create":{"path":"existing"}}
            ]}
            """)));

        // 'a' should NOT exist — entire tx rolled back.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM node WHERE graph_name = @g AND path = 'a'";
        cmd.Parameters.AddWithValue("@g", Graph);
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        // No tx_event written.
        cmd.CommandText = "SELECT COUNT(*) FROM tx_event WHERE graph_name = @g";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    // ---------- hist scope rejection ----------

    [Fact]
    public void TxScopeHist_HistReadOnly()
    {
        using var conn = NewSeededGraph();
        var tx = NewExecutor(conn);
        // parser will throw hist_read_only before even reaching executor — verify via direct executor.
        var req = new TxRequest(Scope.Hist, "t", null, null, Array.Empty<TxOp>());
        var ex = Assert.Throws<ApiException>(() => tx.Execute(req));
        Assert.Equal(ApiErrorCodes.HistReadOnly, ex.Code);
    }

    // ---------- tx_touches_* ----------

    [Fact]
    public void Touches_NodesAndLinks_Indexed()
    {
        using var conn = NewSeededGraph();
        SeedRawNode(conn, "y", "main", "y", "y");
        var tx = NewExecutor(conn);

        var resp = tx.Execute(ParseTx("""
            {"title":"t","ops":[
                {"create":{"path":"x","as":"x","set":{"links":[{"name":"depends_on","to":"y"}]}}}
            ]}
            """));
        var newId = ((CreateOpResponse)resp.Ops[0]).Id;

        var touches = QueryStrings(conn,
            "SELECT node_id || ':' || role FROM tx_touches_node WHERE graph_name = @g AND tx_id = @t",
            ("@g", Graph), ("@t", resp.Id));
        Assert.Contains(newId + ":created", touches);

        var touchedLinks = QueryStrings(conn,
            "SELECT link_name || ':' || from_id || '->' || to_id || ':' || role FROM tx_touches_link WHERE graph_name = @g AND tx_id = @t",
            ("@g", Graph), ("@t", resp.Id));
        Assert.Contains("depends_on:" + newId + "->y:created", touchedLinks);
    }

    // ---------- helpers ----------

    private static SqliteConnection NewSeededGraph()
    {
        var name = "txt_" + Guid.NewGuid().ToString("N");
        var store = SqliteStore.ForSharedInMemory(name);
        var conn = store.Open();
        SqliteStore.Bootstrap(conn);
        Exec(conn, "INSERT INTO graph(name) VALUES(@n)", ("@n", Graph));
        Exec(conn, "INSERT INTO sequence(graph_name, next_id) VALUES(@n, 1)", ("@n", Graph));
        return conn;
    }

    private static TxExecutor NewExecutor(SqliteConnection conn, Scope scope = Scope.Main, DateTime? utc = null)
    {
        return new TxExecutor(conn, Graph, () => utc ?? FixedUtc);
    }

    private static TxRequest ParseTx(string json) => RequestParser.ParseTx(json);

    private static void SeedRawNode(SqliteConnection conn, string id, string scope, string path, string title,
        string content = "", long version = 1)
    {
        Exec(conn,
            "INSERT INTO node(graph_name, id, scope, path, title, content, version) VALUES(@g, @id, @s, @p, @t, @c, @v)",
            ("@g", Graph), ("@id", id), ("@s", scope), ("@p", path), ("@t", title), ("@c", content), ("@v", version));
    }

    private static void SeedBinding(SqliteConnection conn, string nodeId, string key, string value)
    {
        Exec(conn,
            "INSERT INTO node_map_binding(graph_name, node_id, map_name, branch_path) VALUES(@g, @n, @m, @v)",
            ("@g", Graph), ("@n", nodeId), ("@m", key), ("@v", value));
    }

    private static void SeedLink(SqliteConnection conn, string name, string from, string to)
    {
        Exec(conn,
            "INSERT INTO link(graph_name, name, from_id, to_id) VALUES(@g, @n, @f, @t)",
            ("@g", Graph), ("@n", name), ("@f", from), ("@t", to));
    }

    private static void SeedHistCreatedNode(SqliteConnection conn, string txId, string date, long ordinal,
        string txScope, CreatedNode node)
    {
        var sections = new HistSections(new CreatedSection(new[] { node }, null), null, null);
        var json = HistSectionsJson.Serialize(sections);
        Exec(conn,
            "INSERT INTO tx_event(graph_name, id, title, date, description, rollback_of, tx_scope, ordinal, sections_json) " +
            "VALUES(@g, @id, 'seed', @d, NULL, NULL, @s, @o, @j)",
            ("@g", Graph), ("@id", txId), ("@d", date), ("@s", txScope), ("@o", ordinal), ("@j", json));
        Exec(conn,
            "INSERT INTO tx_touches_node(graph_name, tx_id, node_id, role) VALUES(@g, @t, @n, 'created')",
            ("@g", Graph), ("@t", txId), ("@n", node.Id));
    }

    private record struct NodeRow(string scope, string path, string title, string content, long version);

    private static NodeRow GetNode(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope, path, title, content, version FROM node WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", Graph);
        cmd.Parameters.AddWithValue("@id", id);
        using var rdr = cmd.ExecuteReader();
        Assert.True(rdr.Read(), $"node '{id}' not found");
        return new NodeRow(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2), rdr.GetString(3), rdr.GetInt64(4));
    }

    private static bool NodeExists(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM node WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", Graph);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static string? GetMapBinding(SqliteConnection conn, string nodeId, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT branch_path FROM node_map_binding WHERE graph_name = @g AND node_id = @n AND map_name = @m";
        cmd.Parameters.AddWithValue("@g", Graph);
        cmd.Parameters.AddWithValue("@n", nodeId);
        cmd.Parameters.AddWithValue("@m", key);
        return cmd.ExecuteScalar() as string;
    }

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

    private record struct EventRow(string title, string date, string? description, string? rollbackOf, string txScope, string sectionsJson);

    private static EventRow GetEvent(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT title, date, description, rollback_of, tx_scope, sections_json FROM tx_event WHERE graph_name = @g AND id = @id";
        cmd.Parameters.AddWithValue("@g", Graph);
        cmd.Parameters.AddWithValue("@id", id);
        using var rdr = cmd.ExecuteReader();
        Assert.True(rdr.Read(), $"tx_event '{id}' not found");
        return new EventRow(
            rdr.GetString(0), rdr.GetString(1),
            rdr.IsDBNull(2) ? null : rdr.GetString(2),
            rdr.IsDBNull(3) ? null : rdr.GetString(3),
            rdr.GetString(4), rdr.GetString(5));
    }

    private static List<string> QueryStrings(SqliteConnection conn, string sql, params (string n, object v)[] ps)
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = p.n;
            prm.Value = p.v;
            cmd.Parameters.Add(prm);
        }
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(rdr.GetString(0));
        return result;
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
