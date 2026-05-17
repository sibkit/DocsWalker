using DocsWalker.Core.Api;
using DocsWalker.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Tests.Api;

public sealed class SchemeValidatorTests
{
    private const string Graph = "g1";
    private static readonly DateTime FixedUtc = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoScheme_DataTxPasses()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);

        // Никакой схемы — data tx не должен валидироваться.
        var resp = tx.Execute(RequestParser.ParseTx("""
            {"title":"c","ops":[{"create":{"path":"a","set":{
                "map_bindings":{"unknown":"value/x"}}}}]}
            """));

        Assert.Single(resp.Ops);
    }

    [Fact]
    public void NoScheme_SchemeTxPasses()
    {
        using var conn = NewSeededGraph();
        var tx = new TxExecutor(conn, Graph, () => FixedUtc);

        // Добавление первой map в пустую scheme — никаких data-нарушений.
        var resp = tx.Execute(RequestParser.ParseTx("""
            {"scope":"scheme","title":"add map","ops":[{"create":{"path":"cat_main","set":{
                "content":"{\"branches\":{\"documents\":{\"spec\":{}}},\"required\":false}",
                "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
            """));

        Assert.Single(resp.Ops);
    }

    [Fact]
    public void SchemeTx_AddRequiredMap_FailsWhenDataMissingBinding()
    {
        using var conn = NewSeededGraph();
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""{"title":"m","ops":[{"create":{"path":"a"}}]}"""));

        var tx = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1));
        var ex = Assert.Throws<ApiException>(() => tx.Execute(RequestParser.ParseTx("""
            {"scope":"scheme","title":"req","ops":[{"create":{"path":"cat_main","set":{
                "content":"{\"branches\":{\"documents\":{\"spec\":{}}},\"required\":true}",
                "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.SchemaBreaksExistingData, ex.Code);
        var violations = (List<Dictionary<string, object?>>)ex.Details.Extras!["violations"]!;
        Assert.Contains(violations, v =>
            (string?)v["reason"] == "map_required_missing" && (string?)v["id"] == "1");
    }

    [Fact]
    public void SchemeTx_AddRequiredMap_PassesWhenAllDataHaveBinding()
    {
        using var conn = NewSeededGraph();
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"title":"m","ops":[{"create":{"path":"a","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));

        var resp = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1))
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"req","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}},\"required\":true}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));
        Assert.Single(resp.Ops);
    }

    [Fact]
    public void SchemeTx_RemoveBranch_FailsWhenDataUsesBranch()
    {
        using var conn = NewSeededGraph();
        // Сначала scheme с двумя ветками.
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"init","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{},\"legacy\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));
        // Узел с веткой legacy.
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1))
            .Execute(RequestParser.ParseTx("""
                {"title":"m","ops":[{"create":{"path":"a","set":{
                    "map_bindings":{"category":"documents/legacy"}}}}]}
                """));

        // Сужаем схему — убираем legacy.
        var tx = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(2));
        var ex = Assert.Throws<ApiException>(() => tx.Execute(RequestParser.ParseTx("""
            {"scope":"scheme","title":"shrink","ops":[{"update":{"id":"1","expected_version":1,"set":{
                "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}"}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.SchemaBreaksExistingData, ex.Code);
        var violations = (List<Dictionary<string, object?>>)ex.Details.Extras!["violations"]!;
        Assert.Contains(violations, v =>
            (string?)v["reason"] == "map_branch_unknown"
            && (string?)v["violating_value"] == "documents/legacy");
    }

    [Fact]
    public void SchemeTx_AddLinkRequiredFor_FailsWhenSourceMissingLink()
    {
        using var conn = NewSeededGraph();
        // Map "category" и узел документ.
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"m","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1))
            .Execute(RequestParser.ParseTx("""
                {"title":"a","ops":[{"create":{"path":"a","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));

        // Добавляем required_for=["from"] link, но у узла нет ни одного link.
        var tx = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(2));
        var ex = Assert.Throws<ApiException>(() => tx.Execute(RequestParser.ParseTx("""
            {"scope":"scheme","title":"link","ops":[{"create":{"path":"link_main","set":{
                "content":"{\"from\":{\"map_bindings\":{\"category\":\"documents/**\"}},\"to\":{\"map_bindings\":{\"category\":\"documents/**\"}},\"required_for\":[\"from\"]}",
                "map_bindings":{"category":"link","owner_scope":"main","link_name":"depends_on"}}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.SchemaBreaksExistingData, ex.Code);
        var violations = (List<Dictionary<string, object?>>)ex.Details.Extras!["violations"]!;
        Assert.Contains(violations, v =>
            (string?)v["reason"] == "link_required_from_missing"
            && (string?)v["link"] == "depends_on");
    }

    [Fact]
    public void DataTx_UnknownMap_FailsValidationFailed()
    {
        using var conn = NewSeededGraph();
        // Создаём схему с единственной map.
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"m","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));

        var tx = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1));
        var ex = Assert.Throws<ApiException>(() => tx.Execute(RequestParser.ParseTx("""
            {"title":"a","ops":[{"create":{"path":"a","set":{
                "map_bindings":{"category":"documents/spec","other":"x"}}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        var errors = (List<Dictionary<string, object?>>)ex.Details.Extras!["errors"]!;
        Assert.Contains(errors, e => (string?)e["code"] == ApiErrorCodes.UnknownMap
            && (string?)e["map"] == "other");
    }

    [Fact]
    public void DataTx_UnknownBranch_FailsValidationFailed()
    {
        using var conn = NewSeededGraph();
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"m","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));

        var tx = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1));
        var ex = Assert.Throws<ApiException>(() => tx.Execute(RequestParser.ParseTx("""
            {"title":"a","ops":[{"create":{"path":"a","set":{
                "map_bindings":{"category":"unknown/branch"}}}}]}
            """)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        var errors = (List<Dictionary<string, object?>>)ex.Details.Extras!["errors"]!;
        Assert.Contains(errors, e => (string?)e["code"] == ApiErrorCodes.UnknownMap
            && (string?)e["branch"] == "unknown/branch");
    }

    [Fact]
    public void DataTx_KnownMapAndBranch_Passes()
    {
        using var conn = NewSeededGraph();
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"m","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));

        var resp = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1))
            .Execute(RequestParser.ParseTx("""
                {"title":"a","ops":[{"create":{"path":"a","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));
        Assert.Single(resp.Ops);
    }

    [Fact]
    public void DataTx_UnknownLink_FailsValidationFailed()
    {
        using var conn = NewSeededGraph();
        // scheme: только map (без link). create узлы и попытаться слинковать.
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"m","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1))
            .Execute(RequestParser.ParseTx("""
                {"title":"a","ops":[{"create":{"path":"a","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(2))
            .Execute(RequestParser.ParseTx("""
                {"title":"b","ops":[{"create":{"path":"b","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));

        var tx = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(3));
        var ex = Assert.Throws<ApiException>(() => tx.Execute(RequestParser.ParseTx("""
            {"title":"l","ops":[{"link":{"name":"depends_on","from":"3","to":"5","expected_count":1}}]}
            """)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        var errors = (List<Dictionary<string, object?>>)ex.Details.Extras!["errors"]!;
        Assert.Contains(errors, e => (string?)e["code"] == ApiErrorCodes.UnknownLink
            && (string?)e["link"] == "depends_on");
    }

    [Fact]
    public void DataTx_KnownLink_Passes()
    {
        using var conn = NewSeededGraph();
        new TxExecutor(conn, Graph, () => FixedUtc)
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"m","ops":[{"create":{"path":"cat_main","set":{
                    "content":"{\"branches\":{\"documents\":{\"spec\":{}}}}",
                    "map_bindings":{"category":"map","owner_scope":"main","map":"category"}}}}]}
                """));
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(1))
            .Execute(RequestParser.ParseTx("""
                {"scope":"scheme","title":"l","ops":[{"create":{"path":"link_main","set":{
                    "content":"{\"from\":{\"map_bindings\":{\"category\":\"documents/**\"}},\"to\":{\"map_bindings\":{\"category\":\"documents/**\"}},\"required_for\":[]}",
                    "map_bindings":{"category":"link","owner_scope":"main","link_name":"depends_on"}}}}]}
                """));
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(2))
            .Execute(RequestParser.ParseTx("""
                {"title":"a","ops":[{"create":{"path":"a","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));
        new TxExecutor(conn, Graph, () => FixedUtc.AddDays(3))
            .Execute(RequestParser.ParseTx("""
                {"title":"b","ops":[{"create":{"path":"b","set":{
                    "map_bindings":{"category":"documents/spec"}}}}]}
                """));

        // a=id5, b=id7 (1=map, 2=tx_event, 3=link, 4=tx_event, 5=a, 6=tx_event, 7=b, 8=tx_event).
        var resp = new TxExecutor(conn, Graph, () => FixedUtc.AddDays(4))
            .Execute(RequestParser.ParseTx("""
                {"title":"link","ops":[{"link":{"name":"depends_on","from":"5","to":"7","expected_count":1}}]}
                """));
        Assert.Single(resp.Ops);
    }

    // ---------- helpers ----------

    private static SqliteConnection NewSeededGraph()
    {
        var name = "sv_" + Guid.NewGuid().ToString("N");
        var store = SqliteStore.ForSharedInMemory(name);
        var conn = store.Open();
        SqliteStore.Bootstrap(conn);
        Exec(conn, "INSERT INTO graph(name) VALUES(@n)", ("@n", Graph));
        Exec(conn, "INSERT INTO sequence(graph_name, next_id) VALUES(@n, 1)", ("@n", Graph));
        return conn;
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
