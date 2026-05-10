using System.Globalization;
using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// DocsWalker.Kernel — отдельный exe (Microsoft.NET.Sdk.Web, OutputType=WinExe).
// На Windows это означает «windows subsystem» — child-процесс не наследует
// console handles parent'а. Запускается пользователем вручную или
// process-supervisor'ом (systemd/Windows Service); auto-spawn клиентом
// убран в stg-0010 step-04.
//
// Командная строка: --config=<path-to-kernel-config.json>. Все остальные
// параметры (bind/port/graphs/idle-timeout) живут внутри JSON-файла.
// См. <see cref="KernelConfig"/>.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var options = KernelOptions.ParseArgv(args, out var optError);
if (optError is not null)
{
    Output.WriteError("invalid_parameter", path: null, optError);
    return 1;
}

KernelConfig config;
try
{
    config = KernelConfig.Read(options.ConfigPath);
}
catch (KernelConfigException ex)
{
    Output.WriteError("invalid_kernel_config", path: options.ConfigPath, ex.Message);
    return 1;
}

return RunKernel(options, config);

static int RunKernel(KernelOptions options, KernelConfig config)
{
    const string KernelVersion = "0.6.0-dev";

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    var app = builder.Build();

    var bindUrl = $"http://{config.Bind}:{config.Port.ToString(CultureInfo.InvariantCulture)}";
    app.Urls.Clear();
    app.Urls.Add(bindUrl);

    var registry = new GraphRegistry(config.Graphs, config.GraphIdleTimeout);
    var rpc = new RpcDispatcher(registry, app.Lifetime, Dispatcher.Run);
    var startedAt = DateTimeOffset.UtcNow;
    var pid = Environment.ProcessId;

    RequestDelegate healthHandler = async ctx =>
    {
        var resp = new HealthResponse(Ok: true, Pid: pid, Version: KernelVersion, StartedAt: startedAt);
        var json = JsonSerializer.Serialize(resp, KernelJsonContext.Default.HealthResponse);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json, Encoding.UTF8);
    };
    RequestDelegate graphsHandler = async ctx =>
    {
        var resp = new GraphsResponse(registry.Snapshot());
        var json = JsonSerializer.Serialize(resp, KernelJsonContext.Default.GraphsResponse);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json, Encoding.UTF8);
    };
    RequestDelegate rpcHandler = async ctx =>
    {
        var graphName = ctx.Request.RouteValues["graph"] as string ?? string.Empty;
        await rpc.HandleAsync(ctx, graphName);
    };

    app.MapGet("/health", healthHandler);
    app.MapGet("/db", graphsHandler);
    app.MapPost("/db/{graph}/rpc", rpcHandler);

    try
    {
        app.StartAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        SafeWriteStderr($"DocsWalker kernel: failed to start: {ex.Message}");
        return 1;
    }

    var urls = string.Join(", ", app.Urls);
    SafeWriteStderr(
        $"DocsWalker kernel started: pid={pid.ToString(CultureInfo.InvariantCulture)}, " +
        $"url={urls}, {config.Format()}");

    try { app.WaitForShutdownAsync().GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { }

    registry.Dispose();
    SafeWriteStderr($"DocsWalker kernel stopped: pid={pid.ToString(CultureInfo.InvariantCulture)}");
    return 0;
}

static void SafeWriteStderr(string message)
{
    try { Console.Error.WriteLine(message); }
    catch { /* stderr недоступен — продолжаем */ }
}
