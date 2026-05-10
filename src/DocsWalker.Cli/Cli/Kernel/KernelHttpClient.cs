using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Cli.Cli;
using DocsWalker.Core.Mcp;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Клиентский путь CLI поверх HTTP+JSON-RPC. Все non-server CLI команды идут через
/// <c>POST /rpc</c> ядра, поднятого <see cref="KernelClient.EnsureRunningAsync"/>.
/// <para>
/// Алгоритм:
/// </para>
/// <list type="number">
///   <item><see cref="ArgParser.Parse"/> — argv → command + params dict.</item>
///   <item><see cref="KernelClient.EnsureRunningAsync"/> — discovery + spawn.</item>
///   <item>Сборка JSON-RPC <c>tools/call</c>: <c>name</c> = command, <c>arguments</c> =
///   <c>{root, session_id?, ...params}</c>.</item>
///   <item>POST на <c>/rpc</c>; разбор <see cref="JsonRpcResponse"/>.</item>
///   <item>Печать <c>content[0].text</c> в stdout (или stderr при <c>isError</c>) +
///   exit-code 0/1.</item>
/// </list>
/// </summary>
internal static class KernelHttpClient
{
    /// <summary>
    /// CLI-запросы могут быть длительными (большая transaction, search по большому
    /// docs/), но не бесконечными. 5 минут — комфортный потолок без surprise-зависаний.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int> SendCommandAsync(string[] argv, string rootPath, CancellationToken ct = default)
    {
        var (parsed, parseError) = ArgParser.Parse(argv);
        if (parsed is null)
        {
            Output.WriteError(parseError!.Code, path: null, parseError.Message);
            return 1;
        }

        // Один HttpClient на всю операцию: и для health-handshake в KernelClient,
        // и для самого /rpc-вызова. Disposable, but RequestTimeout — глобальный.
        using var http = new HttpClient { Timeout = RequestTimeout };

        var cliExe = Environment.ProcessPath
                     ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(cliExe))
        {
            Output.WriteError("cli_exe_not_found", path: null,
                "Не удалось определить путь к собственному exe (Environment.ProcessPath null).");
            return 1;
        }

        string kernelExe;
        try { kernelExe = KernelSpawner.ResolveKernelExePath(cliExe); }
        catch (KernelSpawnException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        KernelEndpoint endpoint;
        try
        {
            endpoint = await KernelClient.EnsureRunningAsync(kernelExe, http, ct);
        }
        catch (KernelStartException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }
        catch (KernelSpawnException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        var sessionId = SessionId.Resolve(argv);

        // Собираем arguments: все params из argv (как строки) + явный root + опц. session_id.
        // session-id в argv — пропускаем (мы уже извлекли в session_id), чтобы не дублировать.
        var arguments = new JsonObject { ["root"] = rootPath };
        if (sessionId is not null) arguments["session_id"] = sessionId;
        foreach (var (k, v) in parsed.Params)
        {
            if (k == "session-id" || k == "root") continue;
            // Все значения — как строки. Server'овский McpServer.JsonValueToCliString
            // (вызывается RpcDispatcher) принимает string и кладёт в --key=value as-is.
            arguments[k.Replace('-', '_')] = v;
        }

        var paramsObj = new JsonObject
        {
            ["name"] = parsed.CommandKebab,
            ["arguments"] = arguments,
        };

        var requestObj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = paramsObj,
        };
        var requestJson = requestObj.ToJsonString();

        HttpResponseMessage httpResp;
        try
        {
            using var content = new StringContent(requestJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            httpResp = await http.PostAsync($"{endpoint.Url}/rpc", content, ct);
        }
        catch (HttpRequestException ex)
        {
            Output.WriteError("kernel_unreachable", path: null,
                $"Не удалось дозвониться до ядра ({endpoint.Url}): {ex.Message}",
                hint: "Проверьте, не убито ли ядро. Удалите kernel.json и повторите вызов.");
            return 1;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            Output.WriteError("kernel_timeout", path: null,
                $"Запрос к ядру превысил таймаут {RequestTimeout.TotalMinutes} мин: {ex.Message}");
            return 1;
        }

        using (httpResp)
        {
            var respText = await httpResp.Content.ReadAsStringAsync(ct);
            if (!httpResp.IsSuccessStatusCode)
            {
                Output.WriteError("kernel_http_error", path: null,
                    $"Ядро вернуло {(int)httpResp.StatusCode} {httpResp.ReasonPhrase}: {respText}");
                return 1;
            }

            JsonRpcResponse? rpcResp;
            try
            {
                rpcResp = JsonSerializer.Deserialize(respText, McpJsonContext.Default.JsonRpcResponse);
            }
            catch (JsonException ex)
            {
                Output.WriteError("kernel_bad_response", path: null,
                    $"Не удалось разобрать ответ ядра: {ex.Message}");
                return 1;
            }
            if (rpcResp is null)
            {
                Output.WriteError("kernel_bad_response", path: null, "Пустой ответ ядра.");
                return 1;
            }

            if (rpcResp.Error is not null)
            {
                Output.WriteError("kernel_rpc_error", path: null,
                    $"JSON-RPC error {rpcResp.Error.Code}: {rpcResp.Error.Message}");
                return 1;
            }

            if (!rpcResp.Result.HasValue || rpcResp.Result.Value.ValueKind != JsonValueKind.Object)
            {
                Output.WriteError("kernel_bad_response", path: null,
                    "result в ответе отсутствует или не объект.");
                return 1;
            }

            CallToolResult? toolResult;
            try
            {
                toolResult = JsonSerializer.Deserialize(
                    rpcResp.Result.Value.GetRawText(),
                    McpJsonContext.Default.CallToolResult);
            }
            catch (JsonException ex)
            {
                Output.WriteError("kernel_bad_response", path: null,
                    $"Не удалось разобрать CallToolResult: {ex.Message}");
                return 1;
            }
            if (toolResult is null || toolResult.Content.Count == 0)
            {
                // Пустой результат — успех без вывода. Exit 0.
                return 0;
            }

            var text = toolResult.Content[0].Text ?? string.Empty;
            var isError = toolResult.IsError == true;
            if (isError)
            {
                if (text.Length > 0) Console.Error.WriteLine(text);
                return 1;
            }
            if (text.Length > 0) Console.Out.WriteLine(text);
            return 0;
        }
    }
}
