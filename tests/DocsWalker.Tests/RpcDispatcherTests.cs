using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Kernel;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Tests;

/// <summary>
/// JSON-RPC roundtrip ядра <see cref="RpcDispatcher"/> в новой модели stg-0010
/// step-03: graph-name берётся из URL-сегмента (<c>HandleMessageAsync(json,
/// graphName, ct)</c>), а не из <c>arguments.root</c>; неизвестный граф —
/// <c>unknown_graph</c>; <c>arguments.root</c> в payload игнорируется фильтром
/// <see cref="DocsWalker.Core.Mcp.McpArgvBuilder"/>.
/// <para>
/// Серилизуется с <see cref="McpArgvBuilderTests"/> через collection
/// "ConsoleRedirect": <see cref="RpcDispatcher"/> внутри <c>tools/call</c>
/// делает <see cref="Console.SetOut"/> для перехвата stdout handler'ов;
/// настройка глобальная по процессу.
/// </para>
/// </summary>
[Collection("ConsoleRedirect")]
public class RpcDispatcherTests
{
    [Fact]
    public async Task ToolsCall_KnownGraph_RoutesAndExecutes()
    {
        // Реальный docs/ как storage; check-integrity отработает успешно.
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            MakeCall(1, "check-integrity"),
            graphName: "main",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"result\"", resp);
    }

    [Fact]
    public async Task ToolsList_IncludesLlmJsonApiTools()
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
        Assert.Contains("brief", names);
        Assert.Contains("checkpoint", names);
        Assert.Contains("resume", names);
        Assert.Contains("context-check", names);
        Assert.DoesNotContain("repl", names);
        Assert.DoesNotContain("Примеры CLI", resp);

        var queryTool = tools.First(t => t.GetProperty("name").GetString() == "query");
        var schema = queryTool.GetProperty("inputSchema");
        var ops = schema.GetProperty("properties").GetProperty("ops");
        Assert.Equal("array", ops.GetProperty("type").GetString());
        Assert.Equal("object", ops.GetProperty("items").GetProperty("type").GetString());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "ops");
        Assert.True(schema.GetProperty("properties").TryGetProperty("session_id", out _));

        var txTool = tools.First(t => t.GetProperty("name").GetString() == "tx");
        var txProperties = txTool.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(txProperties.TryGetProperty("session_id", out _));
        Assert.True(txProperties.TryGetProperty("intent", out _));
        Assert.True(txProperties.TryGetProperty("mode", out _));
    }

    [Fact]
    public async Task ToolsCall_SessionLifecycle_PersistsAndChecksWorkset()
    {
        using var env = new WriteTestEnvironment();
        using var registry = NewRegistry(("main", env.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var checkpoint = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 30,
              "method": "tools/call",
              "params": {
                "name": "checkpoint",
                "arguments": {
                  "session_id": "test-session",
                  "summary": "read node 1",
                  "touched_nodes": [1],
                  "decisions": ["session tools persist state"]
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(checkpoint);
        using (var rpcDoc = JsonDocument.Parse(checkpoint))
        using (var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc)))
        {
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("checkpoint", envelope.RootElement.GetProperty("method").GetString());
        }

        var resume = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 31,
              "method": "tools/call",
              "params": {
                "name": "resume",
                "arguments": { "session_id": "test-session" }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resume);
        using (var rpcDoc = JsonDocument.Parse(resume))
        using (var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc)))
        {
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("read node 1", envelope.RootElement
                .GetProperty("session")
                .GetProperty("summary")
                .GetString());
        }

        var okCheck = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 32,
              "method": "tools/call",
              "params": {
                "name": "context-check",
                "arguments": {
                  "session_id": "test-session",
                  "intent": "update node already in workset",
                  "write": { "ops": [{ "op": "update", "id": 1, "set": { "text": "..." } }] }
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(okCheck);
        using (var rpcDoc = JsonDocument.Parse(okCheck))
        using (var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc)))
        {
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Empty(envelope.RootElement.GetProperty("blockers").EnumerateArray());
        }

        var blockedCheck = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 33,
              "method": "tools/call",
              "params": {
                "name": "context-check",
                "arguments": {
                  "session_id": "test-session",
                  "intent": "update unread node",
                  "write": { "ops": [{ "op": "update", "id": 2, "set": { "text": "..." } }] }
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(blockedCheck);
        using (var rpcDoc = JsonDocument.Parse(blockedCheck))
        {
            Assert.True(rpcDoc.RootElement
                .GetProperty("result")
                .GetProperty("isError")
                .GetBoolean());
            using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
            Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(envelope.RootElement.GetProperty("blockers").EnumerateArray(),
                item => item.GetString() == "unread_target:2");
        }
    }

    [Fact]
    public async Task ToolsCall_Brief_ReturnsContextPack()
    {
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 34,
              "method": "tools/call",
              "params": {
                "name": "brief",
                "arguments": { "goal": "LLM work session context guard", "max_tokens": 1000 }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var rpcDoc = JsonDocument.Parse(resp);
        using var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc));
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("brief", envelope.RootElement.GetProperty("method").GetString());
        Assert.True(envelope.RootElement
            .GetProperty("pack")
            .GetProperty("relevant_nodes")
            .GetArrayLength() > 0);
    }

    [Fact]
    public async Task ToolsCall_QueryWithSessionId_AddsReturnedNodesToReadWorkset()
    {
        using var env = new WriteTestEnvironment();
        using var registry = NewRegistry(("main", env.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var query = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 36,
              "method": "tools/call",
              "params": {
                "name": "query",
                "arguments": {
                  "session_id": "query-workset",
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

        Assert.NotNull(query);
        int nodeId;
        using (var rpcDoc = JsonDocument.Parse(query))
        using (var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc)))
        {
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            nodeId = envelope.RootElement
                .GetProperty("results")[0]
                .GetProperty("data")
                .GetProperty("nodes")[0]
                .GetProperty("id")
                .GetInt32();
            Assert.True(envelope.RootElement
                .GetProperty("session")
                .GetProperty("persisted")
                .GetBoolean());
        }

        var resume = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 37,
              "method": "tools/call",
              "params": {
                "name": "resume",
                "arguments": { "session_id": "query-workset" }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resume);
        using (var rpcDoc = JsonDocument.Parse(resume))
        using (var envelope = JsonDocument.Parse(ExtractToolText(rpcDoc)))
        {
            Assert.Contains(
                envelope.RootElement
                    .GetProperty("session")
                    .GetProperty("read_workset")
                    .EnumerateArray(),
                item => item.GetInt32() == nodeId);
        }
    }

    [Fact]
    public async Task ToolsCall_TxApplyIfSafe_BlocksUnreadTarget()
    {
        using var env = new WriteTestEnvironment();
        using var registry = NewRegistry(("main", env.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);
        var targetId = await QueryLlmJsonApiDocumentId(dispatcher);

        await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 38,
              "method": "tools/call",
              "params": {
                "name": "checkpoint",
                "arguments": {
                  "session_id": "tx-block",
                  "summary": "empty workset",
                  "read_workset": [0]
                }
              }
            }
            """,
            graphName: "main",
            default);

        var tx = await dispatcher.HandleMessageAsync(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": 39,
              "method": "tools/call",
              "params": {
                "name": "tx",
                "arguments": {
                  "session_id": "tx-block",
                  "intent": "test blocked write",
                  "mode": "apply_if_safe",
                  "ops": [
                    { "op": "update", "id": {{targetId}}, "set": { "text": "blocked" } }
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
        Assert.Equal("session_guard_failed", envelope.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            envelope.RootElement
                .GetProperty("details")
                .GetProperty("blockers")
                .EnumerateArray(),
            item => item.GetString() == $"unread_target:{targetId}");
    }

    [Fact]
    public async Task ToolsCall_TxApplyIfSafe_AllowsReadTargetAndUpdatesSession()
    {
        using var env = new WriteTestEnvironment();
        using var registry = NewRegistry(("main", env.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);
        var targetId = await QueryLlmJsonApiDocumentId(dispatcher);

        await dispatcher.HandleMessageAsync(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": 40,
              "method": "tools/call",
              "params": {
                "name": "checkpoint",
                "arguments": {
                  "session_id": "tx-allow",
                  "summary": "read target",
                  "read_workset": [{{targetId}}]
                }
              }
            }
            """,
            graphName: "main",
            default);

        var tx = await dispatcher.HandleMessageAsync(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": 41,
              "method": "tools/call",
              "params": {
                "name": "tx",
                "arguments": {
                  "session_id": "tx-allow",
                  "intent": "test allowed write",
                  "mode": "apply_if_safe",
                  "ops": [
                    { "op": "update", "id": {{targetId}}, "set": { "text": "tx guard smoke" } }
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
        Assert.True(envelope.RootElement
            .GetProperty("session")
            .GetProperty("persisted")
            .GetBoolean());
    }

    [Fact]
    public async Task ToolsCall_TxPreview_UsesHitWithoutSessionGuard()
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

    [Fact]
    public async Task ToolsCall_CreateNode_AcceptsSnakeCaseClassifierRefFromMcp()
    {
        using var env = new WriteTestEnvironment();
        using var registry = NewRegistry(("main", env.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 35,
              "method": "tools/call",
              "params": {
                "name": "create-node",
                "arguments": {
                  "type": "example",
                  "title": "mcp snake classifier smoke",
                  "text": "smoke",
                  "path": 445,
                  "subject": 419,
                  "subsystem": 427,
                  "audience": 435,
                  "csharp_structure": 437,
                  "dry-run": true
                }
              }
            }
            """,
            graphName: "main",
            default);

        Assert.NotNull(resp);
        using var rpcDoc = JsonDocument.Parse(resp);
        Assert.False(rpcDoc.RootElement.GetProperty("result").TryGetProperty("isError", out _));
        var text = ExtractToolText(rpcDoc);
        Assert.Contains("\"applied\":false", text);
        Assert.DoesNotContain("missing_required_ref", text);
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
            MakeCall(1, "check-integrity"),
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
        // stg-0010 step-06: --root убран; silent-strip снят. Если LLM
        // передаст arguments.root, McpArgvBuilder пропустит ключ в argv,
        // Dispatcher.Run отвергнёт его с unknown_parameter (loud failure).
        // Storage-path продолжает инжектиться kernel'ом из kernel-config'а
        // и в user-input по-прежнему игнорируется (см. McpArgvBuilder.FilteredKeys).
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var bogusRoot = Path.Combine(Path.GetTempPath(), "should-be-rejected");
        var bogusEsc = bogusRoot.Replace("\\", "\\\\");
        var requestJson =
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"tools/call\"," +
            "\"params\":{\"name\":\"check-integrity\",\"arguments\":" +
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

    /// <summary>
    /// Минимальная заглушка <see cref="IHostApplicationLifetime"/>:
    /// <see cref="StopApplication"/> только взводит флаг <see cref="StopCalled"/>;
    /// CancellationToken'ы — default. Для unit-тестов RpcDispatcher этого достаточно.
    /// </summary>
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public bool StopCalled { get; private set; }
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => StopCalled = true;
    }
}
