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
        Assert.DoesNotContain("repl", names);
        Assert.DoesNotContain("Примеры CLI", resp);

        var queryTool = tools.First(t => t.GetProperty("name").GetString() == "query");
        var schema = queryTool.GetProperty("inputSchema");
        var ops = schema.GetProperty("properties").GetProperty("ops");
        Assert.Equal("array", ops.GetProperty("type").GetString());
        Assert.Equal("object", ops.GetProperty("items").GetProperty("type").GetString());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "ops");
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
