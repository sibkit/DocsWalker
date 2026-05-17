using DocsWalker.Core.Api;

namespace DocsWalker.Tests.Api;

public sealed class RequestParserErrorsTests
{
    private static ApiException Read(string json) =>
        Assert.Throws<ApiException>(() => RequestParser.ParseRead(json));

    private static ApiException Tx(string json) =>
        Assert.Throws<ApiException>(() => RequestParser.ParseTx(json));

    // ---- JSON / структура верхнего уровня --------------------------------

    [Fact]
    public void InvalidJson_Read()
    {
        var ex = Read("not json");
        Assert.Equal(ApiErrorCodes.InvalidJson, ex.Code);
        Assert.Equal("$", ex.Details.Path);
    }

    [Fact]
    public void InvalidJson_Tx_Truncated()
    {
        var ex = Tx("{");
        Assert.Equal(ApiErrorCodes.InvalidJson, ex.Code);
    }

    [Fact]
    public void InvalidRequest_RootIsArray()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest, Read("[]").Code);
    }

    [Fact]
    public void MissingRequiredField_Ops_Read()
    {
        var ex = Read("{}");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops", ex.Details.Path);
    }

    [Fact]
    public void MissingRequiredField_Ops_Tx()
    {
        var ex = Tx("""{"title":"t"}""");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops", ex.Details.Path);
    }

    // ---- Op-level dispatch ------------------------------------------------

    [Fact]
    public void InvalidOp_NotObject()
    {
        var ex = Read("""{"ops":[123]}""");
        Assert.Equal(ApiErrorCodes.InvalidOp, ex.Code);
        Assert.Equal("$.ops[0]", ex.Details.Path);
    }

    [Fact]
    public void InvalidOp_TwoKeys()
    {
        var ex = Read("""{"ops":[{"select":{},"foo":1}]}""");
        Assert.Equal(ApiErrorCodes.InvalidOp, ex.Code);
    }

    [Fact]
    public void InvalidOp_EmptyObject()
    {
        var ex = Read("""{"ops":[{}]}""");
        Assert.Equal(ApiErrorCodes.InvalidOp, ex.Code);
    }

    [Fact]
    public void UnknownOp_Read_CreateNotAllowed()
    {
        Assert.Equal(ApiErrorCodes.UnknownOp,
            Read("""{"ops":[{"create":{}}]}""").Code);
    }

    [Fact]
    public void UnknownOp_Tx_FooNotAllowed()
    {
        Assert.Equal(ApiErrorCodes.UnknownOp,
            Tx("""{"title":"t","ops":[{"foo":{}}]}""").Code);
    }

    [Fact]
    public void UnknownSelectMode_ChunksNotSupported()
    {
        Assert.Equal(ApiErrorCodes.UnknownSelectMode,
            Read("""{"ops":[{"select":"chunks"}]}""").Code);
    }

    // ---- Scope ------------------------------------------------------------

    [Fact]
    public void InvalidScope_ExplicitMain_Read()
    {
        Assert.Equal(ApiErrorCodes.InvalidScope,
            Read("""{"scope":"main","ops":[]}""").Code);
    }

    [Fact]
    public void InvalidScope_ExplicitMain_Tx()
    {
        Assert.Equal(ApiErrorCodes.InvalidScope,
            Tx("""{"scope":"main","title":"t","ops":[]}""").Code);
    }

    [Fact]
    public void UnknownScope_Read()
    {
        Assert.Equal(ApiErrorCodes.UnknownScope,
            Read("""{"scope":"foo","ops":[]}""").Code);
    }

    [Fact]
    public void HistReadOnly_Tx()
    {
        Assert.Equal(ApiErrorCodes.HistReadOnly,
            Tx("""{"scope":"hist","title":"t","ops":[]}""").Code);
    }

    // ---- at_not_applicable ------------------------------------------------

    [Fact]
    public void AtNotApplicable_TxMethod()
    {
        var ex = Tx("""{"title":"t","at":"x","ops":[]}""");
        Assert.Equal(ApiErrorCodes.AtNotApplicable, ex.Code);
        Assert.Equal("tx_method", ex.Details.Extras!["reason"]);
    }

    [Fact]
    public void AtNotApplicable_HistScope()
    {
        var ex = Read("""{"scope":"hist","at":"x","ops":[]}""");
        Assert.Equal(ApiErrorCodes.AtNotApplicable, ex.Code);
        Assert.Equal("hist_scope", ex.Details.Extras!["reason"]);
    }

    [Fact]
    public void AtNotApplicable_MetaSelect()
    {
        var ex = Read("""{"at":"x","ops":[{"select":"meta"}]}""");
        Assert.Equal(ApiErrorCodes.AtNotApplicable, ex.Code);
        Assert.Equal("meta_select", ex.Details.Extras!["reason"]);
    }

    [Fact]
    public void At_BeforeForm_MissingBefore()
    {
        Assert.Equal(ApiErrorCodes.MissingRequiredField,
            Read("""{"at":{},"ops":[]}""").Code);
    }

    [Fact]
    public void At_BeforeForm_ExtraKey()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest,
            Read("""{"at":{"before":"x","extra":1},"ops":[]}""").Code);
    }

    // ---- Tx title ---------------------------------------------------------

    [Fact]
    public void InvalidTxTitle_Missing()
    {
        Assert.Equal(ApiErrorCodes.InvalidTxTitle, Tx("""{"ops":[]}""").Code);
    }

    [Fact]
    public void InvalidTxTitle_Empty()
    {
        Assert.Equal(ApiErrorCodes.InvalidTxTitle, Tx("""{"title":"","ops":[]}""").Code);
    }

    [Fact]
    public void InvalidTxTitle_WhitespaceOnly()
    {
        Assert.Equal(ApiErrorCodes.InvalidTxTitle, Tx("""{"title":"   ","ops":[]}""").Code);
    }

    // ---- max_tokens / match ----------------------------------------------

    [Fact]
    public void InvalidMaxTokens_Zero()
    {
        Assert.Equal(ApiErrorCodes.InvalidMaxTokens,
            Read("""{"ops":[{"select":{"selector":{},"max_tokens":0}}]}""").Code);
    }

    [Fact]
    public void InvalidMaxTokens_Negative()
    {
        Assert.Equal(ApiErrorCodes.InvalidMaxTokens,
            Read("""{"ops":[{"select":{"selector":{},"max_tokens":-1}}]}""").Code);
    }

    [Fact]
    public void InvalidMatchRegex_Empty()
    {
        Assert.Equal(ApiErrorCodes.InvalidMatchRegex,
            Read("""{"ops":[{"select":{"selector":{"match":{"regex":""}}}}]}""").Code);
    }

    [Fact]
    public void InvalidMatchRegex_Uncompilable()
    {
        Assert.Equal(ApiErrorCodes.InvalidMatchRegex,
            Read("""{"ops":[{"select":{"selector":{"match":{"regex":"["}}}}]}""").Code);
    }

    [Fact]
    public void InvalidMatchFields_DataContext_RejectsDescription()
    {
        Assert.Equal(ApiErrorCodes.InvalidMatchFields,
            Read("""{"ops":[{"select":{"selector":{"match":{"regex":"x","fields":["description"]}}}}]}""").Code);
    }

    [Fact]
    public void InvalidMatchFields_HistContext_RejectsContent()
    {
        Assert.Equal(ApiErrorCodes.InvalidMatchFields,
            Read("""{"scope":"hist","ops":[{"select":{"selector":{"title":{"match":{"regex":"x","fields":["content"]}}}}}]}""").Code);
    }

    // ---- node title regex -------------------------------------------------

    [Fact]
    public void InvalidNodeTitle_Create_PathWithSpace()
    {
        Assert.Equal(ApiErrorCodes.InvalidNodeTitle,
            Tx("""{"title":"t","ops":[{"create":{"path":"docs/with space"}}]}""").Code);
    }

    [Fact]
    public void InvalidNodeTitle_UpdateSetTitle()
    {
        Assert.Equal(ApiErrorCodes.InvalidNodeTitle,
            Tx("""{"title":"t","ops":[{"update":{"id":"1","expected_version":1,"set":{"title":"with space"}}}]}""").Code);
    }

    [Fact]
    public void InvalidNodeTitle_CreateSetTitle()
    {
        Assert.Equal(ApiErrorCodes.InvalidNodeTitle,
            Tx("""{"title":"t","ops":[{"create":{"path":"docs/x","set":{"title":"with space"}}}]}""").Code);
    }

    [Fact]
    public void ValidTitle_UnicodeLetters_Accepted()
    {
        var req = RequestParser.ParseTx(
            """{"title":"t","ops":[{"create":{"path":"Документы/раздел_1"}}]}""");
        Assert.Equal("Документы/раздел_1", ((CreateOp)req.Ops[0]).Path);
    }

    // ---- map_bindings semantics ------------------------------------------

    [Fact]
    public void InvalidMapBindingValue_NullInCreate()
    {
        Assert.Equal(ApiErrorCodes.InvalidMapBindingValue,
            Tx("""{"title":"t","ops":[{"create":{"path":"x","set":{"map_bindings":{"category":null}}}}]}""").Code);
    }

    [Fact]
    public void NullInMove_IsValidTombstone()
    {
        var req = RequestParser.ParseTx("""
            {"title":"t","ops":[{"move":{
                "selector":{"id":"1"},
                "to":{"map_bindings":{"audience":null}},
                "expected_count":1}}]}
            """);

        var op = (MoveOp)req.Ops[0];
        Assert.Null(op.To.MapBindings!["audience"]);
    }

    // ---- delete xor -------------------------------------------------------

    [Fact]
    public void Delete_BothIdsAndSelector_Invalid()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest,
            Tx("""{"title":"t","ops":[{"delete":{"ids":["1"],"selector":{},"expected_count":1}}]}""").Code);
    }

    [Fact]
    public void Delete_NeitherIdsNorSelector_Invalid()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest,
            Tx("""{"title":"t","ops":[{"delete":{"expected_count":1}}]}""").Code);
    }

    [Fact]
    public void Delete_EmptyIds_Invalid()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest,
            Tx("""{"title":"t","ops":[{"delete":{"ids":[],"expected_count":0}}]}""").Code);
    }

    // ---- update set ------------------------------------------------------

    [Fact]
    public void Update_SetEmpty_Invalid()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest,
            Tx("""{"title":"t","ops":[{"update":{"id":"1","expected_version":1,"set":{}}}]}""").Code);
    }

    [Fact]
    public void Update_SetUnknownField_Invalid()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest,
            Tx("""{"title":"t","ops":[{"update":{"id":"1","expected_version":1,"set":{"path":"x"}}}]}""").Code);
    }

    // ---- missing required fields per op ----------------------------------

    [Fact]
    public void Link_MissingFrom()
    {
        var ex = Tx("""{"title":"t","ops":[{"link":{"name":"x","to":"1","expected_count":1}}]}""");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops[0].link.from", ex.Details.Path);
    }

    [Fact]
    public void Update_MissingExpectedVersion()
    {
        var ex = Tx("""{"title":"t","ops":[{"update":{"id":"1","set":{"content":"x"}}}]}""");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops[0].update.expected_version", ex.Details.Path);
    }

    [Fact]
    public void Move_MissingExpectedCount()
    {
        var ex = Tx("""{"title":"t","ops":[{"move":{"selector":{"id":"1"},"to":{"parent_path":"new"}}}]}""");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops[0].move.expected_count", ex.Details.Path);
    }

    [Fact]
    public void Create_MissingPath()
    {
        var ex = Tx("""{"title":"t","ops":[{"create":{}}]}""");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops[0].create.path", ex.Details.Path);
    }

    // ---- select selector required ----------------------------------------

    [Fact]
    public void Select_MissingSelector()
    {
        var ex = Read("""{"ops":[{"select":{}}]}""");
        Assert.Equal(ApiErrorCodes.MissingRequiredField, ex.Code);
        Assert.Equal("$.ops[0].select.selector", ex.Details.Path);
    }

    [Fact]
    public void Select_UnknownExtraField()
    {
        var ex = Read("""{"ops":[{"select":{"selector":{},"foo":1}}]}""");
        Assert.Equal(ApiErrorCodes.InvalidRequest, ex.Code);
    }

    // ---- selector unknown field ------------------------------------------

    [Fact]
    public void Selector_UnknownField()
    {
        var ex = Read("""{"ops":[{"select":{"selector":{"foo":1}}}]}""");
        Assert.Equal(ApiErrorCodes.InvalidRequest, ex.Code);
    }
}
