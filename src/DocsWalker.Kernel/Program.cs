using System.Globalization;
using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Core.Server;
using DocsWalker.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// DocsWalker.Kernel — отдельный exe (Microsoft.NET.Sdk.Web, OutputType=WinExe).
// На Windows это означает «windows subsystem» — child-процесс не наследует
// console handles parent'а, что снимает проблему detached spawn'а через
// CLI-wrapper (см. stg-0008 step-04 history). На Linux subsystem-понятия нет;
// поведение идентично обычному exe.
//
// Командная строка: --bind=<addr> --port=<int> --root-idle-timeout=<duration>.
// Аргументы парсятся <see cref="KernelOptions.ParseArgv"/>; для совместимости с
// прошлыми вызовами `docswalker kernel ...` дополнительный токен `kernel` в argv
// (если он есть первым) тихо игнорируется.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var argvNoCmd = (args.Length > 0 && args[0] == "kernel") ? args[1..] : args;

var options = KernelOptions.ParseArgv(argvNoCmd, out var optError);
if (optError is not null)
{
    Output.WriteError("invalid_parameter", path: null, optError);
    return 1;
}

return RunKernel(options);

static int RunKernel(KernelOptions options)
{
    const string KernelVersion = "0.5.0-dev";

    // Discovery: уже запущенное ядро?
    var existing = KernelInfoFile.TryRead();
    if (existing is not null && StalePidDetector.IsAlive(existing.Pid, exePath: null))
    {
        Output.WriteError(
            "kernel_already_running",
            path: null,
            $"DocsWalker kernel уже запущен (pid={existing.Pid}, port={existing.Port}).",
            hint: $"Подключайтесь к http://127.0.0.1:{existing.Port} или завершите процесс {existing.Pid} перед повторным запуском.");
        return 1;
    }

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    var app = builder.Build();

    var bindUrl = $"http://{options.Bind}:{options.Port.ToString(CultureInfo.InvariantCulture)}";
    app.Urls.Clear();
    app.Urls.Add(bindUrl);

    var registry = new RootRegistry(options.RootIdleTimeout);
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
    RequestDelegate rootsHandler = async ctx =>
    {
        var resp = new RootsResponse(registry.Snapshot());
        var json = JsonSerializer.Serialize(resp, KernelJsonContext.Default.RootsResponse);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json, Encoding.UTF8);
    };
    RequestDelegate rpcHandler = ctx => rpc.HandleAsync(ctx);

    app.MapGet("/health", healthHandler);
    app.MapGet("/roots", rootsHandler);
    app.MapPost("/rpc", rpcHandler);

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
    var actualPort = ExtractPort(app.Urls) ?? options.Port;

    try
    {
        KernelInfoFile.Write(new KernelInfo(
            Pid: pid,
            Port: actualPort,
            Version: KernelVersion,
            StartedAt: startedAt,
            AuthToken: null));
    }
    catch (Exception ex)
    {
        SafeWriteStderr(
            $"DocsWalker kernel: не удалось записать kernel.json: {ex.Message}. " +
            $"Клиенты не смогут авто-обнаружить ядро.");
    }

    SafeWriteStderr(
        $"DocsWalker kernel started: pid={pid.ToString(CultureInfo.InvariantCulture)}, " +
        $"url={urls}, {options.Format()}");

    try { app.WaitForShutdownAsync().GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { }

    registry.Dispose();
    KernelInfoFile.DeleteIfExists();
    SafeWriteStderr($"DocsWalker kernel stopped: pid={pid.ToString(CultureInfo.InvariantCulture)}");
    return 0;
}

static void SafeWriteStderr(string message)
{
    try { Console.Error.WriteLine(message); }
    catch { /* stderr недоступен — продолжаем */ }
}

static int? ExtractPort(ICollection<string> urls)
{
    foreach (var u in urls)
    {
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Port > 0)
            return uri.Port;
    }
    return null;
}
