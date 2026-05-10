using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Cli.Mcp;
using DocsWalker.Core.Mcp;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Kernel;

/// <summary>
/// JSON-RPC 2.0 endpoint ядра DocsWalker. Принимает <c>POST /rpc</c>, диспатчит
/// <c>initialize</c> / <c>tools/list</c> / <c>tools/call</c> / <c>shutdown</c>.
/// <para>
/// Отличия от <see cref="McpServer"/>:
/// </para>
/// <list type="bullet">
///   <item>Транспорт — HTTP, не stdio (frame-on-newline).</item>
///   <item><c>tools/call</c> требует <c>arguments.root</c> — ядро multi-root
///   (см. (#305) docs/DocsWalker.yml), routing решается per-call.</item>
///   <item>Per-root semaphore из <see cref="RootRegistry"/> (вместо global #313).</item>
///   <item>Capture stdout/stderr — пока через глобальный mutex (см. комментарий
///   в <see cref="ExecuteWithCaptureAsync"/>); per-root concurrency на capture
///   требует TextWriter-параметра в handlers — отдельный шаг.</item>
/// </list>
/// </summary>
internal sealed class RpcDispatcher
{
    private const string ServerName = "DocsWalker";
    private const string ServerVersion = "0.1";
    private const string McpProtocolVersion = "2024-11-05";

    /// <summary>
    /// Source-gen-context с relaxed-encoder: не экранирует не-ASCII (кириллицу) в JSON.
    /// Соответствует правилу (#221) docs/DocsWalker.yml.
    /// </summary>
    private static readonly McpJsonContext RelaxedCtx = new(
        new JsonSerializerOptions(McpJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// Глобальный mutex на capture stdout/stderr. <see cref="Console.SetOut(TextWriter)"/>
    /// process-global; пока handlers пишут в Console.Out, параллельные вызовы
    /// разных roots переписали бы друг другу буфер. Per-root semaphore из
    /// <see cref="RootRegistry"/> блокирует параллельность только на одном root —
    /// между разными roots она существует. До тех пор, пока handlers не примут
    /// TextWriter явным параметром, capture-фаза сериализуется глобально.
    /// </summary>
    private readonly SemaphoreSlim _globalCaptureLock = new(1, 1);

    private readonly RootRegistry _registry;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Func<string[], int> _dispatcher;

    public RpcDispatcher(
        RootRegistry registry,
        IHostApplicationLifetime lifetime,
        Func<string[], int> dispatcher)
    {
        _registry = registry;
        _lifetime = lifetime;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Точка входа для <c>MapPost("/rpc", ...)</c>. Читает body, дёргает
    /// <see cref="HandleMessageAsync"/>, пишет ответ как application/json.
    /// </summary>
    public async Task HandleAsync(HttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ctx.RequestAborted);
        }

        var responseJson = await HandleMessageAsync(body, ctx.RequestAborted);

        // notification (responseJson==null) — HTTP 204 No Content; протокол не запрещает.
        if (responseJson is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(responseJson, Encoding.UTF8, ctx.RequestAborted);
    }

    /// <summary>
    /// Обрабатывает одно входящее JSON-RPC-сообщение. Возвращает строку-ответ или null
    /// для notification (request без id).
    /// </summary>
    public async Task<string?> HandleMessageAsync(string requestJson, CancellationToken ct)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(requestJson, McpJsonContext.Default.JsonRpcRequest);
        }
        catch (JsonException)
        {
            return SerializeResponse(MakeError(
                id: null,
                code: JsonRpcErrorCodes.ParseError,
                message: "JSON parse error"));
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
        {
            return SerializeResponse(MakeError(
                id: request?.Id,
                code: JsonRpcErrorCodes.InvalidRequest,
                message: "invalid request"));
        }

        var isNotification = !request.Id.HasValue
            || request.Id.Value.ValueKind == JsonValueKind.Null
            || request.Id.Value.ValueKind == JsonValueKind.Undefined;

        JsonRpcResponse response;
        try
        {
            response = await DispatchAsync(request, ct);
        }
        catch (Exception ex)
        {
            response = MakeError(request.Id, JsonRpcErrorCodes.InternalError,
                $"internal: {ex.Message}");
        }

        if (isNotification) return null;
        return SerializeResponse(response);
    }

    private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, CancellationToken ct)
    {
        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "notifications/initialized" => EmptyOk(request.Id),
            "notifications/cancelled"   => EmptyOk(request.Id),
            "tools/list" => HandleListTools(request),
            "tools/call" => await HandleCallToolAsync(request, ct),
            "shutdown"   => HandleShutdown(request),
            _ => MakeError(request.Id, JsonRpcErrorCodes.MethodNotFound,
                $"method not found: {request.Method}"),
        };
    }

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        var result = new InitializeResult(
            ProtocolVersion: McpProtocolVersion,
            ServerInfo: new McpServerInfo(ServerName, ServerVersion),
            Capabilities: new McpServerCapabilities(
                Tools: new McpToolsCapability(ListChanged: false)));

        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.InitializeResult));
    }

    /// <summary>
    /// <c>tools/list</c> — необязательный <c>params.root</c>: если указан, грузим
    /// проектную Схему этого root'а для динамической inputSchema у <c>create-node</c>.
    /// Без <c>root</c> — отдаём базовую schema (без oneOf-ветвей по типам). LLM-клиент
    /// в реальности всегда знает root (через MCP-wrapper или CLI), так что отсутствие
    /// root в <c>tools/list</c> — диагностический сценарий (curl).
    /// </summary>
    private JsonRpcResponse HandleListTools(JsonRpcRequest request)
    {
        SchemaDocument? schema = null;
        if (TryExtractRoot(request.Params, out var root, error: out _))
        {
            schema = TryLoadSchema(root);
        }

        var descriptors = CommandsToTools.Build(schema);
        var tools = new List<McpTool>(descriptors.Count);
        foreach (var d in descriptors)
            tools.Add(new McpTool(d.Name, d.Description, BuildInputSchema(d)));

        var result = new ListToolsResult(tools);
        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.ListToolsResult));
    }

    private async Task<JsonRpcResponse> HandleCallToolAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (!request.Params.HasValue || request.Params.Value.ValueKind != JsonValueKind.Object)
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams,
                "params must be an object {name, arguments}");
        }

        CallToolParams? callParams;
        try
        {
            callParams = JsonSerializer.Deserialize(
                request.Params.Value.GetRawText(),
                McpJsonContext.Default.CallToolParams);
        }
        catch (JsonException ex)
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams,
                $"invalid params: {ex.Message}");
        }
        if (callParams is null || string.IsNullOrEmpty(callParams.Name))
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams,
                "params.name is required");
        }

        // Multi-root routing: arguments.root — обязателен. Без него мы не знаем,
        // куда направить запрос (см. (#305) — ядро multi-root, routing per-call).
        if (!TryExtractRoot(callParams.Arguments, out var root, out var rootError))
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams, rootError ?? "root is required");
        }

        // session_id — опциональный, из arguments.session_id (МCP-wrapper / REPL
        // подставляют свой). Без него — генерируем per-call: seen-фильтрация просто
        // не накапливает state между вызовами.
        var sessionId = TryExtractSessionId(callParams.Arguments) ?? Guid.NewGuid().ToString();

        // Schema для динамических tool'ов (create-node) грузим лениво на root,
        // чтобы валидация JsonValueToCliString правильно различала array-of-object
        // от IdList. Грузим лучше один раз, но в step-02 пока — на каждый вызов
        // (handlers сами reload — sole-writer гарантирует консистентность).
        var schema = TryLoadSchema(root);
        var descriptors = CommandsToTools.Build(schema);
        var toolByName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);
        if (!toolByName.TryGetValue(callParams.Name, out var tool))
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams,
                $"unknown tool: {callParams.Name}");
        }

        string[] argv;
        try
        {
            var paramByName = tool.Params.ToDictionary(p => p.Name, StringComparer.Ordinal);
            argv = McpServer.BuildArgvFromArguments(callParams.Name, callParams.Arguments, paramByName);
        }
        catch (ArgumentException ex)
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams, ex.Message);
        }

        var entry = _registry.GetOrAdd(root);

        var (exitCode, stdout, stderr) = await ExecuteWithCaptureAsync(entry, argv, sessionId, ct);

        var text = exitCode == 0
            ? (string.IsNullOrEmpty(stdout) ? string.Empty : stdout)
            : (string.IsNullOrEmpty(stderr) ? stdout ?? string.Empty : stderr);

        var result = new CallToolResult(
            Content: new[] { new McpContentItem("text", text) },
            IsError: exitCode != 0 ? true : (bool?)null);

        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.CallToolResult));
    }

    private JsonRpcResponse HandleShutdown(JsonRpcRequest request)
    {
        // Триггер graceful shutdown — KernelHandler ждёт ApplicationStopping.
        // Ответ возвращаем сразу (до фактической остановки), чтобы клиент получил OK.
        _lifetime.StopApplication();
        return EmptyOk(request.Id);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteWithCaptureAsync(
        RootEntry entry, string[] argv, string sessionId, CancellationToken ct)
    {
        // Двойная сериализация: per-root semaphore (заявленный инвариант) + global
        // capture-lock (вынужденный костыль из-за process-global Console.SetOut).
        // global снимется в более позднем шаге, когда handlers начнут принимать TextWriter.
        await entry.Semaphore.WaitAsync(ct);
        try
        {
            await _globalCaptureLock.WaitAsync(ct);
            try
            {
                var stdoutWriter = new StringWriter();
                var stderrWriter = new StringWriter();
                var oldOut = Console.Out;
                var oldErr = Console.Error;
                Console.SetOut(stdoutWriter);
                Console.SetError(stderrWriter);
                int exitCode;
                using var _ = RequestContext.Push(sessionId, sessions: null);
                try
                {
                    exitCode = _dispatcher(argv);
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    stderrWriter.WriteLine(
                        $"{{\"code\":\"internal_error\",\"message\":\"{EscapeJson(ex.Message)}\"}}");
                }
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldErr);
                }

                return (
                    exitCode,
                    stdoutWriter.ToString().TrimEnd('\r', '\n'),
                    stderrWriter.ToString().TrimEnd('\r', '\n'));
            }
            finally
            {
                _globalCaptureLock.Release();
            }
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    /// <summary>
    /// Извлекает <c>root</c> из arguments-объекта. Принимаем оба варианта ключа: <c>root</c>
    /// (CLI-стандарт) и <c>"root"</c> (явный override). Если arguments == null или поле
    /// отсутствует — error.
    /// </summary>
    private static bool TryExtractRoot(JsonElement? arguments, out string root, out string? error)
    {
        root = string.Empty;
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
        {
            error = "arguments.root is required (kernel multi-root, no auto-detect)";
            return false;
        }
        if (!arguments.Value.TryGetProperty("root", out var rootElem)
            || rootElem.ValueKind != JsonValueKind.String)
        {
            error = "arguments.root is required (kernel multi-root, no auto-detect)";
            return false;
        }
        var raw = rootElem.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "arguments.root must be non-empty string";
            return false;
        }
        root = raw;
        error = null;
        return true;
    }

    private static string? TryExtractSessionId(JsonElement? arguments)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object) return null;
        if (!arguments.Value.TryGetProperty("session_id", out var elem)
            && !arguments.Value.TryGetProperty("session-id", out elem))
            return null;
        return elem.ValueKind == JsonValueKind.String ? elem.GetString() : null;
    }

    private static SchemaDocument? TryLoadSchema(string root)
    {
        var schemaPath = Path.Combine(root, "docs", "Схема.yml");
        if (!File.Exists(schemaPath)) return null;
        try { return SchemaLoader.LoadSchema(schemaPath); }
        catch { return null; }
    }

    /// <summary>
    /// Калька с <see cref="McpServer.BuildInputSchema"/> — JSON-Schema для inputSchema MCP-tool.
    /// Скопировано (а не вынесено в общий helper), чтобы не плодить публичный API в Core
    /// для одного потребителя на этой стадии. После step-04/05 при необходимости
    /// пере-фактории — обоих перенесём в один helper.
    /// </summary>
    private static JsonObject BuildInputSchema(McpToolDescriptor tool)
    {
        if (tool.RawInputSchema is { } raw)
            return (JsonObject)raw.DeepClone();

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in tool.Params)
        {
            var prop = new JsonObject { ["type"] = p.JsonType };
            if (p.JsonType == "array" && !string.IsNullOrEmpty(p.ItemsJsonType))
                prop["items"] = new JsonObject { ["type"] = p.ItemsJsonType };
            if (!string.IsNullOrEmpty(p.Description))
                prop["description"] = p.Description;
            properties[p.Name] = prop;
            if (p.Required) required.Add((JsonNode?)p.Name);
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static JsonRpcResponse MakeOk(JsonElement? id, JsonElement result) =>
        new("2.0", id ?? JsonDocument.Parse("null").RootElement, result, null);

    private static JsonRpcResponse MakeError(JsonElement? id, int code, string message) =>
        new("2.0", id ?? JsonDocument.Parse("null").RootElement, null,
            new JsonRpcError(code, message, null));

    private static JsonRpcResponse EmptyOk(JsonElement? id) =>
        MakeOk(id, JsonDocument.Parse("{}").RootElement);

    private static string SerializeResponse(JsonRpcResponse response) =>
        JsonSerializer.Serialize(response, RelaxedCtx.JsonRpcResponse);

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
