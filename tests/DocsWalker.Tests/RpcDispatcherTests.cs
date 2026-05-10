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
    public async Task ToolsCall_RootInArguments_FilteredAndIgnored()
    {
        // Защита: даже если клиент пытается перенаправить kernel через
        // arguments.root, McpArgvBuilder фильтрует это. Для check-integrity
        // нет других обязательных параметров — kernel использует свой
        // зарегистрированный storage-path. Если бы фильтра не было,
        // arguments.root попал бы в --root=... и Dispatcher.Run упал бы
        // на unknown_parameter.
        using var registry = NewRegistry(("main", TestPaths.DocsRoot));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var bogusRoot = Path.Combine(Path.GetTempPath(), "should-be-ignored");
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
        Assert.Contains("\"result\"", resp);
        // Не должно быть unknown_parameter (значит фильтр сработал).
        Assert.DoesNotContain("unknown_parameter", resp);
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
