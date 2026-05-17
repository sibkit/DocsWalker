using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocsWalker.Mcp;

/// <summary>
/// Тонкий stdio↔HTTP bridge между MCP-клиентом (Claude Code и т.п.) и
/// <c>DocsWalker.Kernel.exe</c>. Никакой бизнес-логики:
/// <list type="bullet">
///   <item>Читает line-delimited JSON-RPC envelopes с stdin
///     (MCP stdio framing).</item>
///   <item>Форвардит как POST <c>/{graph}</c> на kernel HTTP.</item>
///   <item>Пишет ответ kernel-а в stdout как-есть (одной строкой,
///     заканчивающейся <c>\n</c>).</item>
/// </list>
/// Имя графа — из <see cref="ClientConfig"/>, не из payload.
/// </summary>
internal static class StdioBridge
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync(ClientConfig cfg, bool quiet, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = RequestTimeout };
        var graphSegment = Uri.EscapeDataString(cfg.Graph);
        var rpcUrl = $"http://{cfg.KernelHost}:{cfg.KernelPort}/{graphSegment}";

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"DocsWalker MCP-wrapper started: graph={cfg.Graph}, " +
                $"kernel={cfg.KernelHost}:{cfg.KernelPort}, pid={Environment.ProcessId}");
        }

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (line is null) break;
            if (line.Length == 0) continue;

            string? response;
            try
            {
                response = await ForwardOneAsync(line, http, rpcUrl, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                response = MakeErrorEnvelopeFromRequest(line, ex.Message);
            }
            if (response is null) continue;
            try
            {
                await writer.WriteLineAsync(response.AsMemory(), ct);
            }
            catch (IOException) { break; }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    private static async Task<string?> ForwardOneAsync(
        string requestJson, HttpClient http, string rpcUrl, CancellationToken ct)
    {
        JsonNode? requestNode;
        try
        {
            requestNode = JsonNode.Parse(requestJson);
        }
        catch (JsonException ex)
        {
            return MakeErrorEnvelope(id: null, code: -32700, message: $"JSON parse error: {ex.Message}");
        }
        if (requestNode is not JsonObject req)
        {
            return MakeErrorEnvelope(id: null, code: -32600, message: "request is not a JSON object");
        }
        var idNode = req["id"];
        var isNotification = idNode is null ||
            (idNode is JsonValue v && v.GetValueKind() == JsonValueKind.Null);

        using var content = new StringContent(requestJson, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        HttpResponseMessage httpResp;
        try
        {
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
            if (httpResp.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }
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
        catch
        {
            // Ничего не делаем — упали выше JSON-парса; id не известен.
        }
        return MakeErrorEnvelope(id, -32603, $"internal: {message}");
    }
}
