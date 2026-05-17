using System.Globalization;
using System.Text;
using System.Text.Json;
using DocsWalker.Core.Storage;
using DocsWalker.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// DocsWalker.Kernel V2 — отдельный exe. Хостит HTTP JSON-RPC 2.0 на
// 127.0.0.1:<port>, диспатчит MCP tools/call name=read|tx к
// ReadExecutor/TxExecutor поверх одной SQLite-БД. Конфигурация — в
// файле, путь через --config=<path>.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

string? configPath = null;
foreach (var arg in args)
{
    if (arg.StartsWith("--config=", StringComparison.Ordinal))
    {
        configPath = arg["--config=".Length..];
    }
    else if (arg == "--help" || arg == "-h")
    {
        Console.Error.WriteLine("DocsWalker.Kernel — HTTP+MCP server for DocsWalker V2");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  DocsWalker.Kernel.exe --config=<path-to-kernel-config.json>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Config (JSON):");
        Console.Error.WriteLine("  { \"bind\": \"127.0.0.1\", \"port\": 18080,");
        Console.Error.WriteLine("    \"db_path\": \"<абс. путь к .sqlite>\",");
        Console.Error.WriteLine("    \"graphs\": [\"docswalker\"] }");
        return 0;
    }
}
if (string.IsNullOrEmpty(configPath))
{
    Console.Error.WriteLine("DocsWalker.Kernel: --config=<path> обязателен (--help для деталей)");
    return 1;
}

KernelConfig config;
try
{
    config = KernelConfig.Read(configPath);
}
catch (KernelConfigException ex)
{
    Console.Error.WriteLine($"DocsWalker.Kernel: invalid_kernel_config: {ex.Message}");
    return 1;
}

return RunKernel(config);

static int RunKernel(KernelConfig config)
{
    const string KernelVersion = "2.0.0-dev";
    var pid = Environment.ProcessId;
    var startedAt = DateTimeOffset.UtcNow;

    // Bootstrap БД и регистрация графов. Падение → exit с диагностикой.
    SqliteStore store;
    try
    {
        var dir = Path.GetDirectoryName(config.DbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        store = SqliteStore.ForFile(config.DbPath);
        store.EnsureBootstrapped();
        using var bootstrapConn = store.Open();
        foreach (var name in config.Graphs)
        {
            SqliteStore.EnsureGraphRegistered(bootstrapConn, name);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"DocsWalker.Kernel: db_bootstrap_failed: {ex.Message}");
        return 1;
    }

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(config);

    var app = builder.Build();
    var bindUrl = $"http://{config.Bind}:{config.Port.ToString(CultureInfo.InvariantCulture)}";
    app.Urls.Clear();
    app.Urls.Add(bindUrl);

    var registry = new GraphRegistry(config.Graphs, store);
    var rpc = new RpcDispatcher(registry, app.Lifetime);

    RequestDelegate healthHandler = async ctx =>
    {
        var resp = new HealthResponse(
            Ok: true, Pid: pid, Version: KernelVersion, StartedAt: startedAt,
            DbPath: config.DbPath, Graphs: config.Graphs);
        var json = JsonSerializer.Serialize(resp, KernelJsonContext.Default.HealthResponse);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json, Encoding.UTF8);
    };
    RequestDelegate rpcHandler = async ctx =>
    {
        var graphName = ctx.Request.RouteValues["graph"] as string ?? string.Empty;
        await rpc.HandleAsync(ctx, graphName);
    };
    app.MapGet("/health", healthHandler);
    app.MapPost("/{graph}", rpcHandler);

    try
    {
        app.StartAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"DocsWalker.Kernel: failed to start: {ex.Message}");
        registry.Dispose();
        return 1;
    }

    var urls = string.Join(", ", app.Urls);
    Console.Error.WriteLine(
        $"DocsWalker.Kernel started: pid={pid.ToString(CultureInfo.InvariantCulture)}, " +
        $"url={urls}, {config.Format()}");

    try { app.WaitForShutdownAsync().GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { }

    registry.Dispose();
    Console.Error.WriteLine(
        $"DocsWalker.Kernel stopped: pid={pid.ToString(CultureInfo.InvariantCulture)}");
    return 0;
}
