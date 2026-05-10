using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Handler команды <c>mcp-server</c> в новой архитектуре stg-0008: тонкий
/// stdio↔HTTP bridge между Claude Code (stdio JSON-RPC 2.0) и ядром
/// <c>DocsWalker.Kernel.exe</c> (<c>POST /rpc</c>).
/// <para>
/// Никакой бизнес-логики. Только:
/// </para>
/// <list type="bullet">
///   <item>Резолв пути к ядру (<see cref="KernelSpawner.ResolveKernelExePath"/>).</item>
///   <item>Auto-spawn ядра через <see cref="KernelClient.EnsureRunningAsync"/>, если оно не запущено.</item>
///   <item>Read frame из stdin → парс JSON-RPC envelope.</item>
///   <item>Если <c>tools/call</c> или <c>tools/list</c> — подмешиваем фиксированный
///   <c>root</c> (из <c>--root=</c> wrapper'а).</item>
///   <item>POST на <c>/rpc</c> ядра — пишем ответ в stdout как-есть.</item>
/// </list>
/// <para>
/// strategy.md «Принятые решения» #1, #4, #11; step-05.
/// </para>
/// </summary>
internal static class McpWrapperHandler
{
    /// <summary>
    /// Read-side таймаут на запрос к ядру: длинные операции (search, transaction)
    /// могут занимать секунды-десятки секунд. 5 минут с запасом.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static int Run(string rootPath, IReadOnlyDictionary<string, string> args)
    {
        var quiet = ParseBool(args, "quiet");

        var cliExe = Environment.ProcessPath
                     ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(cliExe))
        {
            Output.WriteError("cli_exe_not_found", path: null,
                "Не удалось определить путь к собственному exe.");
            return 1;
        }

        string kernelExe;
        try { kernelExe = KernelSpawner.ResolveKernelExePath(cliExe); }
        catch (KernelSpawnException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        return RunImplAsync(rootPath, quiet, kernelExe).GetAwaiter().GetResult();
    }

    private static async Task<int> RunImplAsync(string rootPath, bool quiet, string kernelExe)
    {
        using var http = new HttpClient { Timeout = RequestTimeout };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        KernelEndpoint endpoint;
        try
        {
            endpoint = await KernelClient.EnsureRunningAsync(kernelExe, http, cts.Token);
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

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"DocsWalker MCP-wrapper started: root={rootPath}, kernel={endpoint.Url}, " +
                $"pid={Environment.ProcessId}");
        }

        using var stdin  = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var rpcUrl = $"{endpoint.Url}/rpc";

        while (!cts.Token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Frame.ReadLineAsync(stdin, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (line is null) break; // EOF — Claude Code закрыл pipe

            string? responseJson;
            try
            {
                responseJson = await ForwardOneAsync(line, rootPath, http, rpcUrl, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                responseJson = MakeErrorEnvelopeFromRequest(line, ex.Message);
            }

            if (responseJson is null) continue; // notification — без ответа

            try { await Frame.WriteAsync(stdout, responseJson, cts.Token); }
            catch (IOException) { break; }
            catch (OperationCanceledException) { break; }
        }

        return 0;
    }

    /// <summary>
    /// Форвардит один входящий JSON-RPC запрос на <paramref name="rpcUrl"/>, подмешивая
    /// <paramref name="root"/> в <c>tools/call.arguments</c> и <c>tools/list.params</c>.
    /// Возвращает строку ответа или null для notification (без id или id=null).
    /// </summary>
    private static async Task<string?> ForwardOneAsync(
        string requestJson,
        string root,
        HttpClient http,
        string rpcUrl,
        CancellationToken ct)
    {
        // Разбираем входящий JSON в JsonNode — нужна mutable модификация.
        JsonNode? requestNode;
        try { requestNode = JsonNode.Parse(requestJson); }
        catch (JsonException ex)
        {
            return MakeErrorEnvelope(id: null, code: -32700, message: $"JSON parse error: {ex.Message}");
        }
        if (requestNode is not JsonObject req)
        {
            return MakeErrorEnvelope(id: null, code: -32600, message: "request is not a JSON object");
        }

        var method = req["method"]?.GetValue<string>();
        var idNode = req["id"];
        var isNotification = idNode is null || idNode is JsonValue v && v.GetValueKind() == JsonValueKind.Null;

        // shutdown — не форвардим. В MCP это «клиент закрывает сессию», в нашей
        // HTTP-архитектуре kernel живёт независимо для других MCP/CLI/REPL клиентов.
        // Отвечаем локально OK; реальный kernel.shutdown — отдельная админская команда
        // (curl напрямую на /rpc или будущий `docswalker kernel-stop`).
        if (method == "shutdown")
        {
            if (isNotification) return null;
            var ok = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode?.DeepClone(),
                ["result"] = new JsonObject(),
            };
            return ok.ToJsonString();
        }

        // Подмешиваем root для нужных методов.
        switch (method)
        {
            case "tools/list":
                {
                    var p = req["params"] as JsonObject ?? new JsonObject();
                    p["root"] = root;
                    req["params"] = p;
                    break;
                }
            case "tools/call":
                {
                    var p = req["params"] as JsonObject;
                    if (p is null)
                    {
                        // Сервер ответит InvalidParams; форвардим как есть.
                        break;
                    }
                    var argsNode = p["arguments"] as JsonObject;
                    if (argsNode is null)
                    {
                        argsNode = new JsonObject();
                        p["arguments"] = argsNode;
                    }
                    // Перезатираем root явным wrapper'овым (single-root invariant
                    // MCP-сессии — #11 strategy.md).
                    argsNode["root"] = root;
                    break;
                }
            // Остальные методы (initialize, notifications/initialized, shutdown, ...) — без модификаций.
        }

        var modifiedJson = req.ToJsonString();

        HttpResponseMessage httpResp;
        try
        {
            using var content = new StringContent(modifiedJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            httpResp = await http.PostAsync(rpcUrl, content, ct);
        }
        catch (HttpRequestException ex)
        {
            if (isNotification) return null;
            return MakeErrorEnvelope(idNode, -32603, $"kernel unreachable: {ex.Message}");
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException ex)
        {
            if (isNotification) return null;
            return MakeErrorEnvelope(idNode, -32603, $"kernel timeout: {ex.Message}");
        }

        using (httpResp)
        {
            // 204 на notification — без ответа.
            if (httpResp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;

            var body = await httpResp.Content.ReadAsStringAsync(ct);
            if (!httpResp.IsSuccessStatusCode)
            {
                if (isNotification) return null;
                return MakeErrorEnvelope(idNode, -32603,
                    $"kernel http {(int)httpResp.StatusCode}: {body}");
            }

            // Тело ответа — уже валидный JSON-RPC envelope от ядра. Возвращаем как есть.
            return body;
        }
    }

    private static string MakeErrorEnvelope(JsonNode? id, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? null,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return envelope.ToJsonString();
    }

    /// <summary>
    /// Best-effort извлечение id из неразобранного запроса для error-envelope при
    /// внешнем исключении (вне ForwardOneAsync). Если не парсится — id=null.
    /// </summary>
    private static string MakeErrorEnvelopeFromRequest(string requestJson, string message)
    {
        JsonNode? id = null;
        try
        {
            var node = JsonNode.Parse(requestJson);
            if (node is JsonObject obj) id = obj["id"];
        }
        catch { }
        return MakeErrorEnvelope(id, -32603, $"internal: {message}");
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var v)) return false;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
