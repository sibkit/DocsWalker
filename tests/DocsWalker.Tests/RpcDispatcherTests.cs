using DocsWalker.Cli.Cli;
using DocsWalker.Kernel;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Tests;

/// <summary>
/// Multi-root /rpc roundtrip ядра <see cref="RpcDispatcher"/>: dispatch на разные
/// <c>arguments.root</c> создаёт независимые <see cref="RootEntry"/> в реестре,
/// валидирует обязательность <c>root</c>, корректно отвечает на initialize.
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
    public async Task ToolsCall_TwoDifferentRoots_BothInRegistry()
    {
        // root1 — реальный repo с docs/ (для успешного check-integrity).
        // root2 — temp-каталог с пустым docs/ (handler check-integrity упадёт на
        // загрузке, но dispatch-уровень всё равно отработает: routing решается до
        // вызова handler'а, второй RootEntry создаётся в реестре).
        var root1 = TestPaths.RepoRoot;
        var root2 = NewTempRoot();
        try
        {
            using var registry = new RootRegistry(rootIdleTimeout: TimeSpan.FromMinutes(10));
            var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

            await dispatcher.HandleMessageAsync(MakeCall(1, "check-integrity", root1), default);
            await dispatcher.HandleMessageAsync(MakeCall(2, "check-integrity", root2), default);

            var snap = registry.Snapshot();
            Assert.Equal(2, snap.Count);
            Assert.Contains(snap, r => SamePath(r.Root, root1));
            Assert.Contains(snap, r => SamePath(r.Root, root2));
        }
        finally { CleanUp(root2); }
    }

    [Fact]
    public async Task ToolsCall_MissingRoot_ReturnsInvalidParams()
    {
        using var registry = new RootRegistry(rootIdleTimeout: TimeSpan.FromMinutes(10));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"check-integrity","arguments":{}}}""",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"error\"", resp);
        Assert.Contains("root is required", resp);
        Assert.Empty(registry.Snapshot()); // root не зарегистрирован при отсутствующем arguments.root
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfoAndProtocolVersion()
    {
        using var registry = new RootRegistry(rootIdleTimeout: TimeSpan.FromMinutes(10));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
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
        using var registry = new RootRegistry(rootIdleTimeout: TimeSpan.FromMinutes(10));
        var dispatcher = new RpcDispatcher(registry, lifetime, Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":7,"method":"shutdown"}""",
            default);

        Assert.NotNull(resp);
        Assert.Contains("\"result\"", resp);
        Assert.True(lifetime.StopCalled);
    }

    [Fact]
    public async Task ParseError_OnMalformedJson_ReturnsParseError()
    {
        using var registry = new RootRegistry(rootIdleTimeout: TimeSpan.FromMinutes(10));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);

        var resp = await dispatcher.HandleMessageAsync("{not json at all", default);

        Assert.NotNull(resp);
        Assert.Contains("\"error\"", resp);
        Assert.Contains("-32700", resp); // ParseError
    }

    private static string MakeCall(int id, string toolName, string root)
    {
        // JSON-эскейп: backslash в Windows-путях → двойной backslash в JSON-литерале.
        var rootEsc = root.Replace("\\", "\\\\");
        return "{\"jsonrpc\":\"2.0\",\"id\":" + id +
               ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName +
               "\",\"arguments\":{\"root\":\"" + rootEsc + "\"}}}";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dw-rpc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        return dir;
    }

    private static void CleanUp(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
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
