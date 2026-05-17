using DocsWalker.Core.Api;

namespace DocsWalker.Tests.Api;

public sealed class RequestParserTxTests
{
    [Fact]
    public void Minimal_TitleAndEmptyOps()
    {
        var req = RequestParser.ParseTx("""{"title":"t","ops":[]}""");

        Assert.Equal(Scope.Main, req.Scope);
        Assert.Equal("t", req.Title);
        Assert.Null(req.Description);
        Assert.Null(req.Defaults);
        Assert.Empty(req.Ops);
    }

    [Theory]
    [InlineData("usage", Scope.Usage)]
    [InlineData("scheme", Scope.Scheme)]
    public void Scope_Parsed(string wire, Scope expected)
    {
        var req = RequestParser.ParseTx($$"""{"scope":"{{wire}}","title":"t","ops":[]}""");

        Assert.Equal(expected, req.Scope);
    }

    [Fact]
    public void Description_Parsed()
    {
        var req = RequestParser.ParseTx("""{"title":"t","description":"long","ops":[]}""");

        Assert.Equal("long", req.Description);
    }

    [Fact]
    public void Create_Minimal()
    {
        var req = RequestParser.ParseTx(
            """{"title":"t","ops":[{"create":{"path":"docs/x"}}]}""");

        var op = Assert.IsType<CreateOp>(req.Ops[0]);
        Assert.Equal("docs/x", op.Path);
        Assert.Null(op.Alias);
        Assert.Null(op.Set.Title);
        Assert.Null(op.Set.Content);
        Assert.Null(op.Set.MapBindings);
        Assert.Null(op.Set.Links);
    }

    [Fact]
    public void Create_FullSet()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"create":{
                "path":"docs/x",
                "as":"x_alias",
                "set":{"title":"x","content":"body",
                       "map_bindings":{"category":"documents/spec"},
                       "links":[{"name":"depends_on","to":"2a"}]}}}]}
            """);

        var op = (CreateOp)req.Ops[0];
        Assert.Equal("x_alias", op.Alias);
        Assert.Equal("x", op.Set.Title);
        Assert.Equal("body", op.Set.Content);
        Assert.Equal("documents/spec", op.Set.MapBindings!["category"]);
        Assert.Single(op.Set.Links!);
        var link = op.Set.Links![0];
        Assert.Equal("depends_on", link.Name);
        var to = Assert.IsType<IdEndpoint>(link.To);
        Assert.Equal("2a", to.Id);
    }

    [Fact]
    public void Update_Full()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"update":{
                "id":"2a","expected_version":7,
                "set":{"title":"selectors","content":"body"}}}]}
            """);

        var op = Assert.IsType<UpdateOp>(req.Ops[0]);
        Assert.Equal("2a", op.Id);
        Assert.Equal(7L, op.ExpectedVersion);
        Assert.Equal("selectors", op.Set.Title);
        Assert.Equal("body", op.Set.Content);
    }

    [Fact]
    public void Update_ContentOnly()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"update":{
                "id":"2a","expected_version":1,"set":{"content":"x"}}}]}
            """);

        var op = (UpdateOp)req.Ops[0];
        Assert.Null(op.Set.Title);
        Assert.Equal("x", op.Set.Content);
    }

    [Fact]
    public void Move_WithTombstoneMapBinding()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"move":{
                "selector":{"path":"old/**"},
                "to":{"parent_path":"new",
                      "map_bindings":{"audience":"llm-agent","old_audience":null}},
                "expected_count":5}}]}
            """);

        var op = Assert.IsType<MoveOp>(req.Ops[0]);
        Assert.Equal("old/**", op.Selector.Path);
        Assert.Equal("new", op.To.ParentPath);
        Assert.Equal("llm-agent", op.To.MapBindings!["audience"]);
        Assert.Null(op.To.MapBindings["old_audience"]);
        Assert.Equal(5L, op.ExpectedCount);
    }

    [Fact]
    public void Move_OnlyParent_NoMapBindings()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"move":{
                "selector":{"id":"1"},
                "to":{"parent_path":"new"},
                "expected_count":1}}]}
            """);

        var op = (MoveOp)req.Ops[0];
        Assert.Equal("new", op.To.ParentPath);
        Assert.Null(op.To.MapBindings);
    }

    [Fact]
    public void Delete_Ids()
    {
        var req = RequestParser.ParseTx(
            """{"title":"t","ops":[{"delete":{"ids":["11","12"],"expected_count":2}}]}""");

        var op = Assert.IsType<DeleteOp>(req.Ops[0]);
        Assert.Equal(new[] { "11", "12" }, op.Ids);
        Assert.Null(op.Selector);
        Assert.Equal(2L, op.ExpectedCount);
    }

    [Fact]
    public void Delete_Selector()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"delete":{
                "selector":{"map_bindings":{"category":"documents/draft"}},
                "expected_count":4}}]}
            """);

        var op = (DeleteOp)req.Ops[0];
        Assert.Null(op.Ids);
        Assert.NotNull(op.Selector);
        Assert.Equal("documents/draft", op.Selector!.MapBindings!["category"]);
    }

    [Fact]
    public void Link_FromIdToSelector()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"depends_on",
                "from":"2a",
                "to":{"selector":{"map_bindings":{"category":"documents/spec"}}},
                "expected_count":3}}]}
            """);

        var op = Assert.IsType<LinkOp>(req.Ops[0]);
        Assert.Equal("depends_on", op.Name);
        Assert.Equal("2a", Assert.IsType<IdEndpoint>(op.From).Id);
        var to = Assert.IsType<SelectorEndpoint>(op.To);
        Assert.Equal("documents/spec", to.Selector.MapBindings!["category"]);
        Assert.Equal(3L, op.ExpectedCount);
    }

    [Fact]
    public void Link_Endpoint_AliasForm()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"x","from":{"alias":"new_node"},"to":"11","expected_count":1}}]}
            """);

        var op = (LinkOp)req.Ops[0];
        Assert.Equal("new_node", Assert.IsType<AliasEndpoint>(op.From).Alias);
    }

    [Fact]
    public void Link_Endpoint_IdsForm()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"x","from":{"ids":["a","b"]},"to":"c","expected_count":2}}]}
            """);

        var op = (LinkOp)req.Ops[0];
        var from = Assert.IsType<IdsEndpoint>(op.From);
        Assert.Equal(new[] { "a", "b" }, from.Ids);
    }

    [Fact]
    public void Link_Endpoint_IdObjectForm()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"link":{
                "name":"x","from":{"id":"a"},"to":"b","expected_count":1}}]}
            """);

        var op = (LinkOp)req.Ops[0];
        Assert.Equal("a", Assert.IsType<IdEndpoint>(op.From).Id);
    }

    [Fact]
    public void Unlink_Symmetric()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"unlink":{
                "name":"x","from":"a","to":"b","expected_count":1}}]}
            """);

        Assert.IsType<UnlinkOp>(req.Ops[0]);
    }

    [Fact]
    public void Rollback_StringForm()
    {
        var req = RequestParser.ParseTx(
            """{"title":"t","ops":[{"rollback":"a3f1c2"}]}""");

        var op = Assert.IsType<RollbackOp>(req.Ops[0]);
        Assert.Equal("a3f1c2", op.TxId);
    }

    [Fact]
    public void Rollback_ObjectForm()
    {
        var req = RequestParser.ParseTx(
            """{"title":"t","ops":[{"rollback":{"id":"a3f1c2"}}]}""");

        var op = (RollbackOp)req.Ops[0];
        Assert.Equal("a3f1c2", op.TxId);
    }

    [Fact]
    public void Composite_CreateThenLink()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[
                {"create":{"path":"docs/x","as":"x"}},
                {"link":{"name":"depends_on","from":{"alias":"x"},"to":"11","expected_count":1}}
            ]}
            """);

        Assert.Equal(2, req.Ops.Count);
        Assert.Equal("x", ((CreateOp)req.Ops[0]).Alias);
        Assert.Equal("x", Assert.IsType<AliasEndpoint>(((LinkOp)req.Ops[1]).From).Alias);
    }
}
