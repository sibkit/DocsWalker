using System.Text.Json;
using DocsWalker.Core.Api;

namespace DocsWalker.Tests.Api;

public sealed class WireFormatTests
{
    [Fact]
    public void Read_SingleOp_UnwrapsResultIntoObject()
    {
        var op = new SelectNodesResponse(
            Count: 1, Truncated: false, OmittedCount: 0, StoppedAt: null,
            Items: [new NodeView(
                Id: "2a", Scope: null, Path: "root/child", Title: "child",
                MapBindings: new Dictionary<string, string>(),
                Content: null, Links: null, Tokens: 12, Version: 7)]);
        var response = new ReadResponse([op]);

        var json = WireFormat.SerializeRead(response);
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("result");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        var items = result.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        var first = items[0];
        Assert.Equal("2a", first.GetProperty("id").GetString());
        // scope=null опускается, как и map_bindings={}, content=null, links=null.
        Assert.False(first.TryGetProperty("scope", out _));
        Assert.False(first.TryGetProperty("map_bindings", out _));
        Assert.False(first.TryGetProperty("content", out _));
        Assert.False(first.TryGetProperty("links", out _));
        Assert.Equal(12, first.GetProperty("tokens").GetInt32());
        Assert.Equal(7, first.GetProperty("version").GetInt64());
    }

    [Fact]
    public void Read_MultipleOps_WrapsResultIntoArray()
    {
        var op1 = new SelectNodesResponse(0, false, 0, null, Array.Empty<NodeView>());
        var op2 = new SelectMetaResponse(new Dictionary<string, object?> { ["v"] = 1L });
        var response = new ReadResponse([op1, op2]);

        var json = WireFormat.SerializeRead(response);
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("result");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal(0, result[0].GetProperty("count").GetInt32());
        Assert.Equal(1, result[1].GetProperty("meta").GetProperty("v").GetInt64());
    }

    [Fact]
    public void Read_NonMainScope_PreservesScopeField()
    {
        var op = new SelectNodesResponse(1, false, 0, null,
            [new NodeView("a", "usage", "rules/x", "x",
                new Dictionary<string, string> { ["category"] = "cat/a" },
                Content: "body", Links: null, Tokens: 5, Version: 1)]);
        var json = WireFormat.SerializeRead(new ReadResponse([op]));
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("result").GetProperty("items")[0];
        Assert.Equal("usage", node.GetProperty("scope").GetString());
        Assert.Equal("cat/a", node.GetProperty("map_bindings").GetProperty("category").GetString());
        Assert.Equal("body", node.GetProperty("content").GetString());
    }

    [Fact]
    public void Tx_AlwaysWrapsResultWithIdAndOps()
    {
        var resp = new TxResponse("abc123",
            [new CreateOpResponse("ff"), EmptyTxOpResponse.Instance]);
        var json = WireFormat.SerializeTx(resp);
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("abc123", result.GetProperty("id").GetString());
        var ops = result.GetProperty("ops");
        Assert.Equal(2, ops.GetArrayLength());
        Assert.Equal("ff", ops[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, ops[1].ValueKind);
        Assert.Empty(ops[1].EnumerateObject());
    }

    [Fact]
    public void Error_SerializesCodeAndDetails()
    {
        var error = new ApiError(ApiErrorCodes.NotFound,
            new ApiErrorDetails(
                Path: "$.ops[0].update.id",
                Extras: new Dictionary<string, object?>
                {
                    ["id"] = "ff",
                    ["count"] = 0,
                }));
        var json = WireFormat.SerializeError(error);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        var details = doc.RootElement.GetProperty("details");
        Assert.Equal("$.ops[0].update.id", details.GetProperty("path").GetString());
        Assert.Equal("ff", details.GetProperty("id").GetString());
        Assert.Equal(0, details.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Error_DetailsWithoutPath_StillEmitsDetailsObject()
    {
        var error = new ApiError("internal_error", new ApiErrorDetails());
        var json = WireFormat.SerializeError(error);
        using var doc = JsonDocument.Parse(json);
        var details = doc.RootElement.GetProperty("details");
        Assert.Equal(JsonValueKind.Object, details.ValueKind);
    }

    [Fact]
    public void Read_Hist_EventCountsAndSectionsRoundtrip()
    {
        var ev = new EventView(
            Id: "ev1", Title: "demo", Date: "2026-05-18", RollbackOf: null,
            Description: "details",
            Counts: new EventCountsView(
                Created: new SectionCountView(Nodes: 2, Links: null),
                Changed: new SectionCountView(Nodes: 1, Links: null),
                Deleted: null),
            Created: new CreatedSection(
                Nodes: [new CreatedNode("a", "p/a", "a", "body", null)],
                Links: null),
            Changed: null,
            Deleted: null,
            Tokens: 33);
        var op = new SelectEventsResponse(1, false, 0, null, [ev]);
        var json = WireFormat.SerializeRead(new ReadResponse([op]));
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("result").GetProperty("items")[0];
        Assert.Equal("demo", item.GetProperty("title").GetString());
        Assert.Equal("details", item.GetProperty("description").GetString());
        Assert.Equal(2, item.GetProperty("counts").GetProperty("created").GetProperty("nodes").GetInt32());
        Assert.Equal("a", item.GetProperty("created").GetProperty("nodes")[0].GetProperty("id").GetString());
        Assert.False(item.TryGetProperty("changed", out _));
        Assert.False(item.TryGetProperty("deleted", out _));
    }
}
