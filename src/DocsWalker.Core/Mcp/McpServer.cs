using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Core.Server;
using DocsWalker.Core.Server.Protocol;
using DocsWalker.Core.Sessions;

namespace DocsWalker.Core.Mcp;

/// <summary>
/// MCP-сервер поверх stdio. Принимает newline-delimited JSON-RPC 2.0 (см. (#364)
/// docs/DocsWalker.yml), маршрутизирует <c>initialize</c> / <c>tools/list</c> /
/// <c>tools/call</c> / <c>shutdown</c>; всё остальное — <c>methodNotFound</c>.
/// <see cref="HandleMessageAsync"/> — точка для unit-тестов: принимает JSON-строку
/// и возвращает строку-ответ (или null для notification).
/// </summary>
public sealed class McpServer
{
    /// <summary>
    /// Версия MCP-протокола, которую сервер декларирует в <c>initialize</c>.
    /// Совместимая со стабильным MCP-клиентом Claude Code (#370).
    /// </summary>
    public const string McpProtocolVersion = "2024-11-05";

    private const string ServerName = "DocsWalker";
    private const string ServerVersion = "0.1";

    /// <summary>
    /// Source-gen-context с relaxed-encoder: не экранирует не-ASCII (кириллицу) в JSON.
    /// Соответствует правилу (#221) docs/DocsWalker.yml — описания инструментов и
    /// stdout-payload'ы хендлеров идут наружу читаемыми, без \uXXXX-эскейпов.
    /// </summary>
    private static readonly McpJsonContext RelaxedCtx = new(
        new JsonSerializerOptions(McpJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<string[], int> _dispatcher;
    private readonly IReadOnlyList<McpToolDescriptor> _tools;
    private readonly IReadOnlyDictionary<string, McpToolDescriptor> _toolsByName;
    private readonly SessionState? _sessions;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string _sessionId = Guid.NewGuid().ToString();

    public McpServer(
        Stream input,
        Stream output,
        Func<string[], int> dispatcher,
        IReadOnlyList<McpToolDescriptor> tools,
        SessionState? sessions = null)
    {
        _input      = input;
        _output     = output;
        _dispatcher = dispatcher;
        _tools      = tools;
        _toolsByName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _sessions   = sessions;
    }

    /// <summary>
    /// Текущий session_id MCP-сессии. Перевыставляется при каждом <c>initialize</c>.
    /// </summary>
    public string CurrentSessionId => _sessionId;

    /// <summary>
    /// Цикл stdio: читает кадр за кадром до EOF / отмены, диспатчит, пишет ответ.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Frame.ReadLineAsync(_input, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (line is null) break; // EOF

            string? responseJson;
            try
            {
                responseJson = await HandleMessageAsync(line, ct);
            }
            catch (Exception ex)
            {
                responseJson = SerializeResponse(MakeError(
                    id: null,
                    code: JsonRpcErrorCodes.InternalError,
                    message: $"internal: {ex.Message}"));
            }

            if (responseJson is not null)
            {
                try { await Frame.WriteAsync(_output, responseJson, ct); }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Обрабатывает одно входящее сообщение. Возвращает JSON-ответ или null,
    /// если сообщение — notification (нет id).
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

        var response = await DispatchAsync(request, ct);
        if (isNotification) return null;
        return SerializeResponse(response);
    }

    private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case "initialize":             return HandleInitialize(request);
            // Notifications/initialized — клиентский ack на initialize.
            // Содержательной обработки не требует: до получения этой нотификации
            // tools/list/tools/call всё равно работают (мы не блокируем их).
            case "notifications/initialized": return EmptyOk(request.Id);
            case "tools/list":             return HandleListTools(request);
            case "tools/call":             return await HandleCallToolAsync(request, ct);
            case "shutdown":               return EmptyOk(request.Id);
            // Прочие notifications: cancelled, log, и т.п. — тихо принимаем.
            case "notifications/cancelled": return EmptyOk(request.Id);
            default:
                return MakeError(
                    request.Id,
                    JsonRpcErrorCodes.MethodNotFound,
                    $"method not found: {request.Method}");
        }
    }

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        // Генерируем свежий session_id на каждый initialize: один MCP-канал = одна
        // сессия (#370). Перезатираем default из конструктора.
        _sessionId = Guid.NewGuid().ToString();

        var result = new InitializeResult(
            ProtocolVersion: McpProtocolVersion,
            ServerInfo: new McpServerInfo(ServerName, ServerVersion),
            Capabilities: new McpServerCapabilities(
                Tools: new McpToolsCapability(ListChanged: false)));

        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.InitializeResult));
    }

    private JsonRpcResponse HandleListTools(JsonRpcRequest request)
    {
        var tools = new List<McpTool>(_tools.Count);
        foreach (var t in _tools)
            tools.Add(new McpTool(t.Name, t.Description, BuildInputSchema(t)));

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
        if (!_toolsByName.ContainsKey(callParams.Name))
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams,
                $"unknown tool: {callParams.Name}");
        }

        string[] argv;
        try
        {
            argv = BuildArgvFromArguments(callParams.Name, callParams.Arguments);
        }
        catch (ArgumentException ex)
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams, ex.Message);
        }

        var (exitCode, stdout, stderr) = await ExecuteWithCaptureAsync(argv, ct);

        // CLI-контракт: успех → stdout с JSON-payload (exit==0); ошибка → stderr
        // с ошибочным envelope (exit≠0). Маршалим то и другое в text-content и
        // выставляем isError=true для ошибок (#365).
        var text = exitCode == 0
            ? (string.IsNullOrEmpty(stdout) ? string.Empty : stdout)
            : (string.IsNullOrEmpty(stderr) ? stdout ?? string.Empty : stderr);

        var result = new CallToolResult(
            Content: new[] { new McpContentItem("text", text) },
            IsError: exitCode != 0 ? true : (bool?)null);

        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.CallToolResult));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteWithCaptureAsync(
        string[] argv, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var stdoutWriter = new StringWriter();
            var stderrWriter = new StringWriter();
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);
            int exitCode;
            using var _ = RequestContext.Push(_sessionId, _sessions);
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
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Преобразует MCP arguments-объект в CLI argv: первый элемент — имя tool,
    /// далее <c>--key=value</c> для каждого ключа.
    /// </summary>
    public static string[] BuildArgvFromArguments(string toolName, JsonElement? arguments)
    {
        var argv = new List<string>(8) { toolName };
        if (!arguments.HasValue) return argv.ToArray();
        var args = arguments.Value;
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            return argv.ToArray();
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments must be an object");

        foreach (var prop in args.EnumerateObject())
        {
            var key = prop.Name.Replace('_', '-');
            var value = JsonValueToCliString(prop.Value);
            argv.Add($"--{key}={value}");
        }
        return argv.ToArray();
    }

    private static string JsonValueToCliString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String  => value.GetString() ?? string.Empty,
        JsonValueKind.Number  => value.GetRawText(),
        JsonValueKind.True    => "true",
        JsonValueKind.False   => "false",
        JsonValueKind.Null    => string.Empty,
        // Массив — IdList: 1,2,3 (запятая-разделитель совпадает с CLI-форматом).
        JsonValueKind.Array   => string.Join(",", value.EnumerateArray().Select(JsonValueToCliString)),
        // Объект — Json-параметр (например, transaction.operations): сырой JSON-текст.
        JsonValueKind.Object  => value.GetRawText(),
        _ => throw new ArgumentException($"unsupported argument value kind: {value.ValueKind}")
    };

    /// <summary>
    /// Строит JSON-Schema для inputSchema MCP-tool. Маппинг:
    /// String → string, Integer → integer, IdList → array of integer,
    /// Json → object. Required-параметры собираются в required[].
    /// </summary>
    private static JsonObject BuildInputSchema(McpToolDescriptor tool)
    {
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
            // Add((JsonNode?)…) выбирает нерефлексионный overload и не тянет
            // IL2026/IL3050 при AOT — generic Add<T>(T) для string небезопасен.
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

    // ── helpers ──────────────────────────────────────────────────────────────

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
