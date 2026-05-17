using System.Text.Json;
using DocsWalker.Core.Storage;
using DocsWalker.Kernel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Tests.Kernel;

public sealed class RpcDispatcherTests
{
    private const string Graph = "g1";

    [Fact]
    public async Task Initialize_ReturnsMcpHandshake()
    {
        await using var fix = NewFixture();
        var resp = await fix.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        var result = ParseResultObject(resp);
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("DocsWalker", result.GetProperty("serverInfo").GetProperty("name").GetString());
        var caps = result.GetProperty("capabilities").GetProperty("tools");
        Assert.False(caps.GetProperty("listChanged").GetBoolean());
    }

    [Fact]
    public async Task ToolsList_ReturnsReadAndTx()
    {
        await using var fix = NewFixture();
        var resp = await fix.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = ParseResultObject(resp).GetProperty("tools");
        var names = new HashSet<string>();
        foreach (var t in tools.EnumerateArray())
        {
            names.Add(t.GetProperty("name").GetString()!);
        }
        Assert.Equal(2, names.Count);
        Assert.Contains("read", names);
        Assert.Contains("tx", names);
    }

    [Fact]
    public async Task ToolsList_UnknownGraph_ReturnsRpcError()
    {
        await using var fix = NewFixture(graphName: "different");
        var resp = await fix.SendAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", graph: Graph);
        using var doc = JsonDocument.Parse(resp!);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, err.GetProperty("code").GetInt32());
        Assert.Contains("unknown_graph", err.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ToolsCall_Tx_Create_RoundTrip()
    {
        await using var fix = NewFixture();
        var txRequest = """
            {"jsonrpc":"2.0","id":10,"method":"tools/call",
             "params":{"name":"tx","arguments":{"title":"add x","ops":[{"create":{"path":"x"}}]}}}
            """;
        var resp = await fix.SendAsync(txRequest);
        var content = ParseToolText(resp);
        // tx envelope: { result: { id, ops: [{id}] } }
        Assert.True(content.TryGetProperty("result", out var result));
        Assert.False(string.IsNullOrEmpty(result.GetProperty("id").GetString()));
        var ops = result.GetProperty("ops");
        Assert.Equal(1, ops.GetArrayLength());
        var newId = ops[0].GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(newId));

        // Verify by reading back.
        var readRequest = """
            {"jsonrpc":"2.0","id":11,"method":"tools/call",
             "params":{"name":"read","arguments":{"ops":[{"select":{"selector":{"path":"x"}}}]}}}
            """;
        var readResp = await fix.SendAsync(readRequest);
        var readContent = ParseToolText(readResp);
        var items = readContent.GetProperty("result").GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("x", items[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsApiErrorAsContent()
    {
        await using var fix = NewFixture();
        var resp = await fix.SendAsync("""
            {"jsonrpc":"2.0","id":20,"method":"tools/call",
             "params":{"name":"bogus","arguments":{}}}
            """);
        using var outer = JsonDocument.Parse(resp!);
        var result = outer.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var inner = JsonDocument.Parse(text);
        Assert.Equal("unknown_method", inner.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ToolsCall_Tx_InvalidArgs_ReturnsApiErrorAsContent()
    {
        await using var fix = NewFixture();
        // tx без обязательного title → invalid_tx_title.
        var resp = await fix.SendAsync("""
            {"jsonrpc":"2.0","id":21,"method":"tools/call",
             "params":{"name":"tx","arguments":{"ops":[]}}}
            """);
        using var outer = JsonDocument.Parse(resp!);
        var result = outer.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var inner = JsonDocument.Parse(text);
        Assert.Equal("invalid_tx_title", inner.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Notification_NoIdResponseIsNull()
    {
        await using var fix = NewFixture();
        var resp = await fix.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Null(resp);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsRpcMethodNotFound()
    {
        await using var fix = NewFixture();
        var resp = await fix.SendAsync("""{"jsonrpc":"2.0","id":30,"method":"weird/thing"}""");
        using var doc = JsonDocument.Parse(resp!);
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, err.GetProperty("code").GetInt32());
    }

    // ---- helpers ----------------------------------------------------------

    private static JsonElement ParseResultObject(string? respJson)
    {
        Assert.NotNull(respJson);
        var doc = JsonDocument.Parse(respJson!);
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static JsonElement ParseToolText(string? respJson)
    {
        Assert.NotNull(respJson);
        using var doc = JsonDocument.Parse(respJson!);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString();
        Assert.NotNull(text);
        var inner = JsonDocument.Parse(text!);
        return inner.RootElement.Clone();
    }

    private static DispatcherFixture NewFixture(string graphName = Graph)
    {
        var dbName = "kerneldispatch_" + Guid.NewGuid().ToString("N");
        var store = SqliteStore.ForSharedInMemory(dbName);
        var conn = store.Open();
        SqliteStore.Bootstrap(conn);
        SqliteStore.EnsureGraphRegistered(conn, graphName);

        var registry = new GraphRegistry([graphName], store);
        var lifetime = new StubLifetime();
        var dispatcher = new RpcDispatcher(registry, lifetime);
        return new DispatcherFixture(conn, registry, dispatcher);
    }

    private sealed class DispatcherFixture(
        SqliteConnection seedConn,
        GraphRegistry registry,
        RpcDispatcher dispatcher) : IAsyncDisposable
    {
        public Task<string?> SendAsync(string json, string graph = Graph)
            => dispatcher.HandleMessageAsync(json, graph, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            registry.Dispose();
            seedConn.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
