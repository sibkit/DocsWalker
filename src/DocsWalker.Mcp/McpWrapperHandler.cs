using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Mcp;

/// <summary>
/// Handler команды <c>mcp-server</c> в новой архитектуре stg-0010: тонкий
/// stdio↔HTTP bridge между Claude Code (stdio JSON-RPC 2.0) и ядром
/// <c>DocsWalker.Kernel.exe</c> (<c>POST /{graph}</c>).
/// <para>
/// Никакой бизнес-логики:
/// </para>
/// <list type="bullet">
///   <item>Чтение <see cref="ClientConfig"/> поиском <c>.dw/client.json</c>
///   вверх от cwd.</item>
///   <item>Read frame из stdin → парс JSON-RPC envelope.</item>
///   <item>POST на <c>/{config.Graph}</c> ядра — пишем ответ в stdout
///   как-есть. Никакой инъекции <c>root</c> в arguments — сервер сам знает
///   по graph-name из URL.</item>
/// </list>
/// </summary>
internal static class McpWrapperHandler
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static int Run(IReadOnlyDictionary<string, string> args)
    {
        var quiet = ParseBool(args, "quiet");

        ClientConfig cfg;
        try { cfg = ClientConfig.Resolve(); }
        catch (ClientConfigException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        return RunImplAsync(cfg, quiet).GetAwaiter().GetResult();
    }

    private static async Task<int> RunImplAsync(ClientConfig cfg, bool quiet)
    {
        using var http = new HttpClient { Timeout = RequestTimeout };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var graphSegment = Uri.EscapeDataString(cfg.Graph);
        var rpcUrl = $"http://{cfg.KernelHost}:{cfg.KernelPort}/{graphSegment}";

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"DocsWalker MCP-wrapper started: graph={cfg.Graph}, kernel={cfg.KernelHost}:{cfg.KernelPort}, " +
                $"pid={Environment.ProcessId}");
        }

        using var stdin  = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        while (!cts.Token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Frame.ReadLineAsync(stdin, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (line is null) break;

            string? responseJson;
            try
            {
                responseJson = await ForwardOneAsync(line, http, rpcUrl, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                responseJson = MakeErrorEnvelopeFromRequest(line, ex.Message);
            }

            if (responseJson is null) continue;

            try { await Frame.WriteAsync(stdout, responseJson, cts.Token); }
            catch (IOException) { break; }
            catch (OperationCanceledException) { break; }
        }

        return 0;
    }

    /// <summary>
    /// Форвардит один входящий JSON-RPC запрос на <paramref name="rpcUrl"/>.
    /// Никаких подмен arguments (root/storage-path) — kernel получает имя
    /// графа из URL и инжектит storage-path сам. McpArgvBuilder на стороне
    /// kernel'а фильтрует root/storage_path из user-input как страховку.
    /// </summary>
    private static async Task<string?> ForwardOneAsync(
        string requestJson,
        HttpClient http,
        string rpcUrl,
        CancellationToken ct)
    {
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

        HttpResponseMessage httpResp;
        try
        {
            using var content = new StringContent(requestJson, Encoding.UTF8);
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
            if (httpResp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;

            var body = await httpResp.Content.ReadAsStringAsync(ct);
            if (!httpResp.IsSuccessStatusCode)
            {
                if (isNotification) return null;
                return MakeErrorEnvelope(idNode, -32603,
                    $"kernel http {(int)httpResp.StatusCode}: {body}");
            }

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
