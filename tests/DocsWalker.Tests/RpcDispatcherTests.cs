using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Core.Mcp;
using DocsWalker.Kernel;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Tests;

[Collection("ConsoleRedirect")]
public class RpcDispatcherTests
{
    [Fact]
    public async Task ToolsCall_KnownGraph_RoutesAndExecutes()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            MakeCall(1, "get-overview"),
            graphName: "main",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"result\"", resp);
    }

    [Fact]
    public async Task ToolsList_IncludesOnlyCurrentMcpSurface()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var doc = JsonDocument.Parse(resp);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToHashSet();

        Assert.Contains("hit", names);
        Assert.Contains("query", names);
        Assert.Contains("tx", names);
        Assert.Contains("get-overview", names);
        Assert.Contains("get-usage-guide", names);
        Assert.Contains("describe-type", names);
        Assert.Contains("get-schema", names);

        Assert.DoesNotContain("check-integrity", names);
        Assert.DoesNotContain("brief", names);
        Assert.DoesNotContain("checkpoint", names);
        Assert.DoesNotContain("resume", names);
        Assert.DoesNotContain("context-check", names);
        Assert.DoesNotContain("get-nodes", names);
        Assert.DoesNotContain("search", names);
        Assert.DoesNotContain("get-tree", names);
        Assert.DoesNotContain("get-refs", names);
        Assert.DoesNotContain("create-node", names);
        Assert.DoesNotContain("update-schema", names);

        var queryTool = tools.First(t => t.GetProperty("name").GetString() == "query");
        var schema = queryTool.GetProperty("inputSchema");
        var ops = schema.GetProperty("properties").GetProperty("ops");
        Assert.Equal("array", ops.GetProperty("type").GetString());
        Assert.Equal("object", ops.GetProperty("items").GetProperty("type").GetString());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "ops");
        Assert.False(schema.GetProperty("properties").TryGetProperty("session_id", out _));

        var txTool = tools.First(t => t.GetProperty("name").GetString() == "tx");
        var txProperties = txTool.GetProperty("inputSchema").GetProperty("properties");
        Assert.False(txProperties.TryGetProperty("session_id", out _));
        Assert.True(txProperties.TryGetProperty("intent", out _));
        Assert.True(txProperties.TryGetProperty("mode", out _));
    }

    [Fact]
    public async Task ToolsCall_TxApplyIfSafe_RequiresIntent()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var tx = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 39,
              "method": "tools/call",
              "params": {
                "name": "tx",
                "arguments": {
                  "mode": "apply_if_safe",
                  "ops": [
                    { "op": "update", "id": 1, "set": { "text": "blocked" } }
                  ]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(tx);
        using var rpcDoc = JsonDocument.Parse(tx);
        Assert.True(rpcDoc.RootElement
            .GetProperty("result")
            .GetProperty("isError")
            .GetBoolean());
        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("tx", envelope.RootElement.GetProperty("method").GetString());
        Assert.Equal("tx_guard_failed", envelope.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            envelope.RootElement
                .GetProperty("details")
                .GetProperty("blockers")
                .EnumerateArray(),
            item => item.GetString() == "missing_intent");
    }

    [Fact]
    public async Task ToolsCall_TxApplyIfSafe_AppliesWithoutSession()
    {
        using var env = new WriteTestEnvironment();
        using var registry = NewRegistry(("main", env.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);
        var targetId = await QueryLlmJsonApiDocumentId(dispatcher);

        var tx = await dispatcher.HandleMessageAsync(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": 41,
              "method": "tools/call",
              "params": {
                "name": "tx",
                "arguments": {
                  "intent": "test allowed write",
                  "mode": "apply_if_safe",
                  "ops": [
                    { "op": "update", "id": {{targetId}}, "set": { "text": "tx smoke" } }
                  ]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(tx);
        using var rpcDoc = JsonDocument.Parse(tx);
        Assert.False(rpcDoc.RootElement.GetProperty("result").TryGetProperty("isError", out _));
        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("tx", envelope.RootElement.GetProperty("method").GetString());
        Assert.False(envelope.RootElement.TryGetProperty("session", out _));
    }

    [Fact]
    public async Task ToolsCall_TxPreview_UsesHitWithoutIntentGuard()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var tx = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 42,
              "method": "tools/call",
              "params": {
                "name": "tx",
                "arguments": {
                  "mode": "preview",
                  "ops": [
                    {
                      "op": "select",
                      "select": { "path": "DocsWalker-LLM JSON API" }
                    }
                  ]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(tx);
        using var rpcDoc = JsonDocument.Parse(tx);
        Assert.False(rpcDoc.RootElement.GetProperty("result").TryGetProperty("isError", out _));
        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("tx", envelope.RootElement.GetProperty("method").GetString());
        Assert.Equal("preview", envelope.RootElement.GetProperty("mode").GetString());
        Assert.Equal("hit", envelope.RootElement.GetProperty("validated_by").GetString());
    }

    [Theory]
    [InlineData("brief")]
    [InlineData("checkpoint")]
    [InlineData("resume")]
    [InlineData("context-check")]
    [InlineData("check-integrity")]
    [InlineData("get-nodes")]
    [InlineData("search")]
    [InlineData("create-node")]
    [InlineData("update-node")]
    [InlineData("delete-nodes")]
    [InlineData("get-tree")]
    [InlineData("get-by-path")]
    [InlineData("get-refs")]
    [InlineData("find")]
    [InlineData("redirect-refs")]
    [InlineData("update-schema")]
    [InlineData("get-meta-schema")]
    public async Task ToolsCall_LegacyCommandSet_ReturnsUnknownTool(string toolName)
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": 35,
              "method": "tools/call",
              "params": {
                "name": "{{toolName}}",
                "arguments": {}
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var rpcDoc = JsonDocument.Parse(resp);
        var error = rpcDoc.RootElement.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.Equal($"unknown tool: {toolName}", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ToolsList_UnknownGraph_ReturnsInvalidParams()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            graphName: "no-such-graph",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"error\"", resp);
        Assert.Contains("unknown_graph", resp);
        Assert.Contains("no-such-graph", resp);
    }

    [Fact]
    public async Task ToolsCall_Query_RoutesThroughLlmJsonApiExecutor()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 3,
              "method": "tools/call",
              "params": {
                "name": "query",
                "arguments": {
                  "ops": [
                    {
                      "op": "select",
                      "select": { "path": "DocsWalker-LLM JSON API" },
                      "include": ["text"],
                      "max_tokens": 500
                    }
                  ]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var rpcDoc = JsonDocument.Parse(resp);
        var result = rpcDoc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out _));

        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("query", envelope.RootElement.GetProperty("method").GetString());
        Assert.True(envelope.RootElement.GetProperty("base_revision").TryGetInt64(out var revision));
        Assert.True(revision >= 0);
        Assert.Single(envelope.RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task ToolsCall_QueryFailure_ReturnsLlmEnvelopeAsToolError()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 4,
              "method": "tools/call",
              "params": {
                "name": "query",
                "arguments": {
                  "ops": [
                    {
                      "op": "select",
                      "select": { "path": "No such LLM JSON API path" }
                    }
                  ]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var rpcDoc = JsonDocument.Parse(resp);
        var result = rpcDoc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("query", envelope.RootElement.GetProperty("method").GetString());
        Assert.Equal("not_found", envelope.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ToolsCall_UnknownGraph_ReturnsInvalidParams()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            MakeCall(1, "get-overview"),
            graphName: "no-such-graph",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"error\"", resp);
        Assert.Contains("unknown_graph", resp);
        Assert.Contains("no-such-graph", resp);
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfoAndProtocolVersion()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            graphName: "main",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"result\"", resp);
        Assert.Contains("\"DocsWalker\"", resp);
        Assert.Contains("2024-11-05", resp);
    }

    [Fact]
    public async Task Shutdown_TriggersLifetimeStop()
    {
        var lifetime = new TestLifetime();
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, lifetime, Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":7,"method":"shutdown"}""",
            graphName: "main",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"result\"", resp);
        Assert.True(lifetime.StopCalled);
    }

    [Fact]
    public async Task ParseError_OnMalformedJson_ReturnsParseError()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            "{not json at all",
            graphName: "main",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"error\"", resp);
        Assert.Contains("-32700", resp);
    }

    [Fact]
    public async Task ToolsCall_RootInArguments_RejectedAsUnknownParameter()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var bogusRoot = Path.Combine(Path.GetTempPath(), "should-be-rejected");
        var bogusEsc = bogusRoot.Replace("\\", "\\\\");
        var requestJson =
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"tools/call\"," +
            "\"params\":{\"name\":\"get-overview\",\"arguments\":" +
            "{\"root\":\"" + bogusEsc + "\"}}}";

        var resp = await dispatcher.HandleMessageAsync(
            requestJson,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        Assert.Contains("unknown_parameter", resp);
    }

    private static GraphRegistry NewRegistry(params (string Name, string StoragePath)[] graphs)
    {
        var configs = graphs.Select(g => new KernelGraphConfig(g.Name, g.StoragePath));
        return new GraphRegistry(configs, TimeSpan.FromMinutes(10));
    }

    private static string MakeCall(int id, string toolName) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id +
        ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName +
        "\",\"arguments\":{}}}";

    private static string ExtractToolText(JsonDocument rpcDoc) =>
        rpcDoc.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;

    private static async Task<int> QueryLlmJsonApiDocumentId(RpcDispatcher dispatcher)
    {
        var resp = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 1001,
              "method": "tools/call",
              "params": {
                "name": "query",
                "arguments": {
                  "ops": [
                    {
                      "op": "select",
                      "select": { "path": "DocsWalker-LLM JSON API" },
                      "max_tokens": 500
                    }
                  ]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var rpcDoc = JsonDocument.Parse(resp);
        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        return envelope.RootElement
            .GetProperty("results")[0]
            .GetProperty("data")
            .GetProperty("nodes")[0]
            .GetProperty("id")
            .GetInt32();
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public bool StopCalled { get; private set; }
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => StopCalled = true;
    }
}
