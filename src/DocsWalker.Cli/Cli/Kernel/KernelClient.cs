using System.Net.Http;
using System.Text.Json;
using DocsWalker.Core.Server;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Endpoint живого ядра DocsWalker: URL <c>http://127.0.0.1:&lt;port&gt;</c> и pid процесса.
/// Возвращается из <see cref="KernelClient.EnsureRunningAsync"/>.
/// </summary>
internal sealed record KernelEndpoint(string Url, int Pid);

/// <summary>
/// High-level client-helper: гарантирует, что per-user ядро DocsWalker запущено и
/// готово принимать запросы. Реализует discovery + spawn-race + health-handshake
/// (strategy.md «Принятые решения» #8, #9, #10).
/// <para>
/// Алгоритм (на каждый клиентский вызов):
/// </para>
/// <list type="number">
///   <item>Прочитать <c>kernel.json</c>; если pid жив + <c>GET /health</c> ok →
///   return endpoint.</item>
///   <item>Попытаться захватить <c>kernel.lock</c> (non-blocking).</item>
///   <item>Захватил → spawn ядра, ждать <c>/health</c> до 3 сек, прочитать обновлённый
///   kernel.json, отпустить lock, return.</item>
///   <item>Не захватил → poll kernel.json до 3 сек с интервалом 50мс; если winner
///   опубликовал — return; иначе следующая итерация (loser сам станет winner если
///   winner упал).</item>
///   <item>3 неуспешные итерации → <see cref="KernelStartException"/>.</item>
/// </list>
/// </summary>
internal static class KernelClient
{
    private static readonly TimeSpan HealthRetryTotal = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HealthRetryInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollLoserInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Возвращает endpoint живого ядра, спавня его при отсутствии. Печатает на stderr
    /// строку <c>kernel: spawned pid=X port=Y</c> при успешном spawn (auto-spawn —
    /// <b>не silent</b>, см. (#311) docs/DocsWalker.yml).
    /// </summary>
    /// <param name="kernelExePath">
    /// Путь к exe ядра (обычно <see cref="Environment.ProcessPath"/> — клиент сам и
    /// есть бинарь DocsWalker.Cli, который умеет команду <c>kernel</c>).
    /// </param>
    /// <param name="extraKernelArgs">Опц. флаги для kernel-команды (например, <c>--port=51234</c>).</param>
    /// <param name="httpClient">HttpClient для health-проверок. Можно переиспользовать.</param>
    /// <param name="ct">Cancellation для всей операции (включая retry-петлю).</param>
    public static async Task<KernelEndpoint> EnsureRunningAsync(
        string kernelExePath,
        IEnumerable<string>? extraKernelArgs,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // 1) Discovery: ядро уже живо?
            var existing = await TryGetLiveEndpointAsync(httpClient, ct);
            if (existing is not null) return existing;

            // 2) Захват lock'а — кто-то один из конкурентов выиграет.
            using var lockHandle = KernelLock.TryAcquireOnce();
            if (lockHandle is not null)
            {
                // 3) Winner: spawn + wait health.
                var pid = KernelSpawner.SpawnDetached(kernelExePath, extraKernelArgs ?? Array.Empty<string>());
                Console.Error.WriteLine($"kernel: spawned pid={pid}");

                var endpoint = await WaitHealthAsync(httpClient, HealthRetryTotal, ct);
                if (endpoint is not null)
                {
                    Console.Error.WriteLine($"kernel: ready url={endpoint.Url}");
                    return endpoint;
                }
                // Не дождались /health — продолжаем цикл; ядро могло упасть на старте.
            }
            else
            {
                // 4) Loser: ждём, что winner опубликует kernel.json.
                var endpoint = await WaitForKernelJsonAsync(httpClient, HealthRetryTotal, ct);
                if (endpoint is not null) return endpoint;
            }
        }

        throw new KernelStartException(
            "kernel_spawn_failed",
            "Не удалось запустить ядро DocsWalker за 3 попытки. Проверьте exe-путь, права и stderr ядра.");
    }

    /// <summary>
    /// Чтение kernel.json + health-check. Возвращает endpoint только если оба ОК.
    /// </summary>
    private static async Task<KernelEndpoint?> TryGetLiveEndpointAsync(
        HttpClient httpClient, CancellationToken ct)
    {
        var info = KernelInfoFile.TryRead();
        if (info is null) return null;
        if (!StalePidDetector.IsAlive(info.Pid, exePath: null)) return null;

        var url = $"http://127.0.0.1:{info.Port}";
        var alive = await HealthCheckAsync(httpClient, url, ct);
        return alive ? new KernelEndpoint(url, info.Pid) : null;
    }

    /// <summary>
    /// Polling-loop: ждём, пока в kernel.json появится живая запись (winner запустил ядро).
    /// </summary>
    private static async Task<KernelEndpoint?> WaitHealthAsync(
        HttpClient httpClient, TimeSpan total, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + total;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var endpoint = await TryGetLiveEndpointAsync(httpClient, ct);
            if (endpoint is not null) return endpoint;
            try { await Task.Delay(HealthRetryInterval, ct); }
            catch (TaskCanceledException) { throw new OperationCanceledException(ct); }
        }
        return null;
    }

    private static async Task<KernelEndpoint?> WaitForKernelJsonAsync(
        HttpClient httpClient, TimeSpan total, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + total;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var endpoint = await TryGetLiveEndpointAsync(httpClient, ct);
            if (endpoint is not null) return endpoint;
            try { await Task.Delay(PollLoserInterval, ct); }
            catch (TaskCanceledException) { throw new OperationCanceledException(ct); }
        }
        return null;
    }

    private static async Task<bool> HealthCheckAsync(HttpClient httpClient, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await httpClient.GetAsync($"{baseUrl}/health", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var health = JsonSerializer.Deserialize(json, KernelJsonContext.Default.HealthResponse);
            return health is { Ok: true };
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (JsonException) { return false; }
    }
}

/// <summary>
/// Не удалось привести ядро в живое состояние за серию попыток (spawn упал или
/// /health не отвечает). Содержит structured-код для CLI-обёртки.
/// </summary>
internal sealed class KernelStartException : Exception
{
    public string Code { get; }
    public KernelStartException(string code, string message) : base(message) { Code = code; }
}
