using System.Globalization;
using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Handler команды <c>docswalker kernel</c> — поднимает локальный HTTP-сервер
/// (Kestrel + minimal API) и обслуживает JSON-RPC 2.0 на <c>POST /rpc</c>,
/// плюс diagnostic-эндпойнты <c>GET /health</c> и <c>GET /roots</c>.
/// <para>
/// Архитектура — strategy.md stg-0008 «Принятые решения» #1, #2, #15: один kernel
/// на пользователя, multi-root в одном процессе, HTTP+JSON-RPC, embedded Kestrel,
/// <c>WebApplication.CreateSlimBuilder</c> (AOT-friendly).
/// </para>
/// <para>
/// Step-02 не пишет <c>kernel.json</c> / <c>kernel.lock</c> — это step-03
/// (discovery-and-spawn). Здесь — только startup-banner на stderr.
/// </para>
/// </summary>
internal static class KernelHandler
{
    private const string KernelVersion = "0.5.0-dev";

    public static int Run(IReadOnlyDictionary<string, string> args)
    {
        var options = KernelOptions.Parse(args, out var optError);
        if (optError is not null)
        {
            Output.WriteError("invalid_parameter", path: null, optError);
            return 1;
        }

        return RunImpl(options);
    }

    private static int RunImpl(KernelOptions options)
    {
        // Discovery: проверяем, не запущено ли уже ядро на этого пользователя.
        // Если kernel.json есть и pid жив — error и exit. Stale-файл (pid мёртв) —
        // молча перезатираем после успешного startup. Race между двумя одновременными
        // прямыми запусками `docswalker kernel` от пользователя — редкий случай;
        // mitigation на уровне клиентского spawn-race через kernel.lock (см. KernelClient).
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

        // CreateSlimBuilder — AOT-friendly minimal preset: без MVC, без статики, без
        // авторизации; только Kestrel + minimal API + JSON. Нам этого достаточно.
        var builder = WebApplication.CreateSlimBuilder();

        // Снимаем лишнюю болтовню Kestrel'а на startup (banner, request-логирование):
        // у нас свой startup-banner на stderr + сами JSON-RPC ответы.
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // Bind: <addr>:<port>. Port=0 → Kestrel выберет свободный, фактический
        // прочитаем после Start через app.Urls. Для localhost — две записи (v4+v6)
        // не нужны: дефолт 127.0.0.1 явно IPv4.
        var bindUrl = $"http://{options.Bind}:{options.Port.ToString(CultureInfo.InvariantCulture)}";
        app.Urls.Clear();
        app.Urls.Add(bindUrl);

        var registry = new RootRegistry(options.RootIdleTimeout);
        var rpc = new RpcDispatcher(registry, app.Lifetime, Dispatcher.Run);
        var startedAt = DateTimeOffset.UtcNow;
        var pid = Environment.ProcessId;

        // Explicit RequestDelegate-типы обходят RequestDelegateGenerator (RDG):
        // он генерит интерцепторы только для Delegate-overload'ов MapGet/MapPost,
        // и при AOT это вызывает ошибку signature-mismatch на async-лямбдах.
        // RequestDelegate-overload — нативный Map(string, RequestDelegate) — RDG не трогает.
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
        app.MapGet("/roots",  rootsHandler);
        app.MapPost("/rpc",   rpcHandler);

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DocsWalker kernel: failed to start: {ex.Message}");
            return 1;
        }

        // app.Urls наполняется фактическими адресами после Start (для Port=0
        // там реальный выбранный порт).
        var urls = string.Join(", ", app.Urls);
        var actualPort = ExtractPort(app.Urls) ?? options.Port;

        // Discovery: записываем kernel.json — клиенты прочитают и подключатся.
        // Best-effort: если запись не удалась, продолжаем работу (ядро функционирует),
        // но клиенты не смогут найти. Лог ошибки — в stderr.
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
            Console.Error.WriteLine(
                $"DocsWalker kernel: не удалось записать kernel.json: {ex.Message}. " +
                $"Клиенты не смогут авто-обнаружить ядро.");
        }

        Console.Error.WriteLine(
            $"DocsWalker kernel started: pid={pid.ToString(CultureInfo.InvariantCulture)}, " +
            $"url={urls}, {options.Format()}");

        // Блокирующее ожидание ApplicationStopping. Lifetime сам слушает SIGINT/SIGTERM
        // (Console.CancelKeyPress) и триггерит StopApplication. Также shutdown
        // триггерится JSON-RPC методом "shutdown" (см. RpcDispatcher).
        try
        {
            app.WaitForShutdownAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { /* normal на graceful */ }

        registry.Dispose();
        KernelInfoFile.DeleteIfExists();
        Console.Error.WriteLine($"DocsWalker kernel stopped: pid={pid.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    /// <summary>
    /// Извлекает фактический port из <c>app.Urls</c> (после <c>StartAsync</c>). Для
    /// dynamic-port (--port=0) Kestrel выбирает свободный, и URL приходит вида
    /// <c>http://127.0.0.1:51234</c> — парсим port-сегмент.
    /// </summary>
    private static int? ExtractPort(ICollection<string> urls)
    {
        foreach (var u in urls)
        {
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }
        return null;
    }
}
