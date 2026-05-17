using DocsWalker.Core.Api;

namespace DocsWalker.Tests.Api;

public sealed class RequestParserReadTests
{
    [Fact]
    public void Minimal_EmptyOps()
    {
        var req = RequestParser.ParseRead("""{"ops":[]}""");

        Assert.Equal(Scope.Main, req.Scope);
        Assert.Null(req.Defaults);
        Assert.Null(req.At);
        Assert.Empty(req.Ops);
    }

    [Theory]
    [InlineData("usage", Scope.Usage)]
    [InlineData("scheme", Scope.Scheme)]
    [InlineData("hist", Scope.Hist)]
    public void Scope_Parsed(string wire, Scope expected)
    {
        var req = RequestParser.ParseRead($$"""{"scope":"{{wire}}","ops":[]}""");

        Assert.Equal(expected, req.Scope);
    }

    [Fact]
    public void SelectByPredicate_PathOnly()
    {
        var req = RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"path":"DocsWalker/**"}}}]}""");

        var op = Assert.IsType<SelectByPredicateOp>(req.Ops[0]);
        var sel = Assert.IsType<DataSelector>(op.Selector);
        Assert.Equal("DocsWalker/**", sel.Path);
        Assert.Null(op.Include);
        Assert.Null(op.MaxTokens);
        Assert.Null(op.Alias);
    }

    [Fact]
    public void SelectByPredicate_FullFields()
    {
        var req = RequestParser.ParseRead("""
            {"ops":[{"select":{"selector":{"id":["2a","11"]},
                                "include":["content","links"],
                                "max_tokens":4000,
                                "as":"docs"}}]}
            """);

        var op = (SelectByPredicateOp)req.Ops[0];
        var sel = (DataSelector)op.Selector;
        Assert.Equal(new[] { "2a", "11" }, sel.Ids);
        Assert.Equal(new[] { "content", "links" }, op.Include);
        Assert.Equal(4000, op.MaxTokens);
        Assert.Equal("docs", op.Alias);
    }

    [Fact]
    public void Selector_IdAsString_NormalizedToList()
    {
        var req = RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"id":"2a"}}}]}""");

        var sel = (DataSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Equal(new[] { "2a" }, sel.Ids);
    }

    [Fact]
    public void SelectKernelMode_Meta()
    {
        var req = RequestParser.ParseRead("""{"ops":[{"select":"meta"}]}""");

        var op = Assert.IsType<SelectKernelModeOp>(req.Ops[0]);
        Assert.Equal("meta", op.ModeName);
    }

    [Fact]
    public void At_ShortForm_IsInclusive()
    {
        var req = RequestParser.ParseRead("""{"at":"a3f1c2","ops":[]}""");

        Assert.NotNull(req.At);
        Assert.Equal("a3f1c2", req.At!.TxId);
        Assert.True(req.At.Inclusive);
    }

    [Fact]
    public void At_BeforeForm_IsExclusive()
    {
        var req = RequestParser.ParseRead("""{"at":{"before":"a3f1c2"},"ops":[]}""");

        Assert.NotNull(req.At);
        Assert.Equal("a3f1c2", req.At!.TxId);
        Assert.False(req.At.Inclusive);
    }

    [Fact]
    public void HistSelector_TouchesNode_String()
    {
        var req = RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"touches_node":"2a"}}}]}""");

        var sel = Assert.IsType<HistSelector>(((SelectByPredicateOp)req.Ops[0]).Selector);
        Assert.Equal("2a", sel.TouchesNodeId);
    }

    [Fact]
    public void HistSelector_TouchesNode_Object()
    {
        var req = RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"touches_node":{"id":"2a"}}}}]}""");

        var sel = (HistSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Equal("2a", sel.TouchesNodeId);
    }

    [Fact]
    public void HistSelector_TouchesLink()
    {
        var req = RequestParser.ParseRead("""
            {"scope":"hist","ops":[{"select":{"selector":{
                "touches_link":{"name":"depends_on","from":"2a","to":"11"}}}}]}
            """);

        var sel = (HistSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.NotNull(sel.TouchesLink);
        Assert.Equal("depends_on", sel.TouchesLink!.Name);
        Assert.Equal("2a", sel.TouchesLink.From);
        Assert.Equal("11", sel.TouchesLink.To);
    }

    [Fact]
    public void HistSelector_DateShortFormMatch_AutoFieldsToDate()
    {
        var req = RequestParser.ParseRead("""
            {"scope":"hist","ops":[{"select":{"selector":{
                "date":{"match":{"regex":"^2026-05-1[0-7]$"}}}}}]}
            """);

        var sel = (HistSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Null(sel.Date);
        Assert.NotNull(sel.DateMatch);
        Assert.Equal("^2026-05-1[0-7]$", sel.DateMatch!.Regex);
        Assert.Equal(new[] { "date" }, sel.DateMatch.Fields);
    }

    [Fact]
    public void HistSelector_DateExact_NoMatch()
    {
        var req = RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"date":"2026-05-18"}}}]}""");

        var sel = (HistSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Equal("2026-05-18", sel.Date);
        Assert.Null(sel.DateMatch);
    }

    [Fact]
    public void HistSelector_TxScope_Parsed()
    {
        var req = RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"tx_scope":"scheme"}}}]}""");

        var sel = (HistSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Equal("scheme", sel.TxScope);
    }

    [Fact]
    public void HistSelector_RollbackOf()
    {
        var req = RequestParser.ParseRead(
            """{"scope":"hist","ops":[{"select":{"selector":{"rollback_of":"a3f1c2"}}}]}""");

        var sel = (HistSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Equal("a3f1c2", sel.RollbackOf);
    }

    [Fact]
    public void DataSelector_MatchFull()
    {
        var req = RequestParser.ParseRead("""
            {"ops":[{"select":{"selector":{"path":"x","match":{
                "regex":"foo","fields":["title","content"],"case_sensitive":true}}}}]}
            """);

        var sel = (DataSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.NotNull(sel.Match);
        Assert.Equal("foo", sel.Match!.Regex);
        Assert.Equal(new[] { "title", "content" }, sel.Match.Fields);
        Assert.True(sel.Match.CaseSensitive);
    }

    [Fact]
    public void DataSelector_LinksNested()
    {
        var req = RequestParser.ParseRead("""
            {"ops":[{"select":{"selector":{"links":{
                "name":"depends_on",
                "to":{"map_bindings":{"category":"documents/spec"}}}}}}]}
            """);

        var sel = (DataSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.NotNull(sel.Links);
        Assert.Equal("depends_on", sel.Links!.Name);
        Assert.NotNull(sel.Links.To);
        Assert.NotNull(sel.Links.To!.Selector);
        Assert.Null(sel.Links.To.Id);
        Assert.Equal("documents/spec", sel.Links.To.Selector!.MapBindings!["category"]);
    }

    [Fact]
    public void DataSelector_LinksFromId()
    {
        var req = RequestParser.ParseRead(
            """{"ops":[{"select":{"selector":{"links":{"name":"x","from":"2a"}}}}]}""");

        var sel = (DataSelector)((SelectByPredicateOp)req.Ops[0]).Selector;
        Assert.Equal("2a", sel.Links!.From!.Id);
        Assert.Null(sel.Links.From.Selector);
    }

    [Fact]
    public void Defaults_Parsed()
    {
        var req = RequestParser.ParseRead("""
            {"defaults":{"path_parent":"api/x","map_bindings":{"category":"documents/spec"}},
             "ops":[]}
            """);

        Assert.NotNull(req.Defaults);
        Assert.Equal("api/x", req.Defaults!.PathParent);
        Assert.Equal("documents/spec", req.Defaults.MapBindings!["category"]);
    }
}
