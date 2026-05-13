using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Mcp;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Kernel;

/// <summary>
/// JSON-RPC 2.0 endpoint ядра DocsWalker. Принимает canonical <c>POST /{graph}</c>,
/// диспатчит <c>initialize</c> / <c>tools/list</c> / <c>tools/call</c> /
/// <c>shutdown</c>.
/// <para>
/// Контракт: имя графа лежит в URL-сегменте, а не в <c>arguments.root</c>.
/// Kernel по имени графа находит storage-path в <see cref="GraphRegistry"/> и
/// инжектит <c>--storage-path=&lt;path&gt;</c> в argv команды перед вызовом
/// <see cref="DocsWalker.Cli.Cli.Dispatcher.Run"/>. Клиент storage-path не
/// передаёт и не видит — это server-side контракт.
/// </para>
/// </summary>
internal sealed class RpcDispatcher
{
    private const string ServerName = "DocsWalker";
    private const string ServerVersion = "0.1";
    private const string McpProtocolVersion = "2024-11-05";
    private static readonly HashSet<string> LlmJsonApiToolNames = new(StringComparer.Ordinal)
    {
        "hit",
        "query",
        "tx",
    };

    private static readonly McpJsonContext RelaxedCtx = new(
        new JsonSerializerOptions(McpJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// Глобальный mutex на capture stdout/stderr. <see cref="Console.SetOut(TextWriter)"/>
    /// process-global; пока handlers пишут в Console.Out, параллельные вызовы
    /// разных графов переписали бы друг другу буфер. Per-graph semaphore из
    /// <see cref="GraphRegistry"/> блокирует параллельность только на одном графе —
    /// между разными графами она существует. До тех пор, пока handlers не примут
    /// TextWriter явным параметром, capture-фаза сериализуется глобально.
    /// </summary>
    private readonly SemaphoreSlim _globalCaptureLock = new(1, 1);

    private readonly GraphRegistry _registry;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Func<string[], int> _dispatcher;

    public RpcDispatcher(
        GraphRegistry registry,
        IHostApplicationLifetime lifetime,
        Func<string[], int> dispatcher)
    {
        _registry = registry;
        _lifetime = lifetime;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Точка входа для <c>MapPost("/{graph}", ...)</c>. Читает body,
    /// дёргает <see cref="HandleMessageAsync"/>, пишет ответ как application/json.
    /// </summary>
    public async Task HandleAsync(HttpContext ctx, string graphName)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ctx.RequestAborted);
        }

        var responseJson = await HandleMessageAsync(body, graphName, ctx.RequestAborted);

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
    /// Обрабатывает одно входящее JSON-RPC-сообщение. Возвращает строку-ответ
    /// или null для notification (request без id).
    /// </summary>
    public async Task<string?> HandleMessageAsync(string requestJson, string graphName, CancellationToken ct)
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
            response = await DispatchAsync(request, graphName, ct);
        }
        catch (Exception ex)
        {
            response = MakeError(request.Id, JsonRpcErrorCodes.InternalError,
                $"internal: {ex.Message}");
        }

        if (isNotification) return null;
        return SerializeResponse(response);
    }

    private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, string graphName, CancellationToken ct)
    {
        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "notifications/initialized" => EmptyOk(request.Id),
            "notifications/cancelled" => EmptyOk(request.Id),
            "tools/list" => HandleListTools(request, graphName),
            "tools/call" => await HandleCallToolAsync(request, graphName, ct),
            "shutdown" => HandleShutdown(request),
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
    /// <c>tools/list</c> — грузим Схему графа по имени из URL для динамической
    /// inputSchema у <c>create-node</c>. Неизвестный граф падает сразу, чтобы
    /// неверный endpoint не выглядел рабочим до первого <c>tools/call</c>.
    /// </summary>
    private JsonRpcResponse HandleListTools(JsonRpcRequest request, string graphName)
    {
        if (!_registry.TryGet(graphName, out var entry))
            return MakeUnknownGraphError(request.Id, graphName);

        var schema = TryLoadSchema(entry.StoragePath);
        var descriptors = CommandsToTools.Build(schema);
        var tools = new List<McpTool>(descriptors.Count);
        foreach (var d in descriptors)
            tools.Add(new McpTool(d.Name, d.Description, BuildInputSchema(d)));

        var result = new ListToolsResult(tools);
        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.ListToolsResult));
    }

    private async Task<JsonRpcResponse> HandleCallToolAsync(JsonRpcRequest request, string graphName, CancellationToken ct)
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

        // Routing по имени графа из URL. Неизвестный граф → unknown_graph.
        if (!_registry.TryGet(graphName, out var entry))
            return MakeUnknownGraphError(request.Id, graphName);

        if (IsLlmJsonApiTool(callParams.Name))
        {
            return await HandleLlmJsonApiToolAsync(
                request.Id,
                entry,
                callParams.Name,
                callParams.Arguments,
                ct);
        }

        // Schema для динамических tool'ов (create-node) грузим лениво на каждый
        // вызов (handlers — sole-writer контракт, переключаются на reload).
        var schema = TryLoadSchema(entry.StoragePath);
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
            argv = McpArgvBuilder.BuildArgvFromArguments(callParams.Name, callParams.Arguments, paramByName);
        }
        catch (ArgumentException ex)
        {
            return MakeError(request.Id, JsonRpcErrorCodes.InvalidParams, ex.Message);
        }

        // Kernel инжектит --storage-path=<docs-folder> в argv. McpArgvBuilder
        // фильтрует root/storage_path из user-input, так что коллизий нет.
        var argvWithStorage = new string[argv.Length + 1];
        Array.Copy(argv, argvWithStorage, argv.Length);
        argvWithStorage[^1] = $"--storage-path={entry.StoragePath}";

        var (exitCode, stdout, stderr) = await ExecuteWithCaptureAsync(entry, argvWithStorage, ct);

        var text = exitCode == 0
            ? (string.IsNullOrEmpty(stdout) ? string.Empty : stdout)
            : (string.IsNullOrEmpty(stderr) ? stdout ?? string.Empty : stderr);

        var result = new CallToolResult(
            Content: new[] { new McpContentItem("text", text) },
            IsError: exitCode != 0 ? true : (bool?)null);

        return MakeOk(request.Id, JsonSerializer.SerializeToElement(result, RelaxedCtx.CallToolResult));
    }

    private async Task<JsonRpcResponse> HandleLlmJsonApiToolAsync(
        JsonElement? requestId,
        GraphEntry entry,
        string toolName,
        JsonElement? arguments,
        CancellationToken ct)
    {
        if (arguments.HasValue &&
            arguments.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return MakeError(requestId, JsonRpcErrorCodes.InvalidParams,
                "arguments must be an object");
        }

        JsonObject envelope;
        await entry.Semaphore.WaitAsync(ct);
        try
        {
            _registry.Touch(entry);
            try
            {
                var request = BuildLlmJsonApiRequest(toolName, arguments);
                var ctx = WriteContext.FromStoragePath(entry.StoragePath);
                var schema = SchemaLoader.LoadSchema(ctx.SchemaPath);
                var loaded = DocumentLoader.Load(ctx.DocsRoot, schema);
                var baseRevision = new SequenceCounter(ctx.SequencePath).Read();
                var writeApi = new WriteApi(ctx);
                var executor = new LlmJsonApiExecutor(
                    loaded.Graph,
                    schema,
                    writeApi,
                    () => baseRevision);

                envelope = executor.Execute(request);
            }
            catch (ArgumentException ex)
            {
                return MakeError(requestId, JsonRpcErrorCodes.InvalidParams, ex.Message);
            }
            catch (SchemaLoadException ex)
            {
                envelope = BuildLlmJsonApiInfrastructureError(
                    toolName,
                    ex.Code,
                    ex.Message,
                    BuildFileDetails(ex.FilePath));
            }
            catch (GraphLoadException ex)
            {
                envelope = BuildLlmJsonApiInfrastructureError(
                    toolName,
                    ex.Code,
                    ex.Message,
                    BuildGraphLoadDetails(ex));
            }
            catch (SequenceCounterException ex)
            {
                envelope = BuildLlmJsonApiInfrastructureError(
                    toolName,
                    ex.Code,
                    ex.Message,
                    BuildFileDetails(ex.FilePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                envelope = BuildLlmJsonApiInfrastructureError(
                    toolName,
                    "internal_error",
                    ex.Message,
                    null);
            }
        }
        finally
        {
            entry.Semaphore.Release();
        }

        return MakeToolTextResponse(
            requestId,
            envelope.ToJsonString(RelaxedCtx.Options),
            IsLlmJsonApiError(envelope));
    }

    private static bool IsLlmJsonApiTool(string name) =>
        LlmJsonApiToolNames.Contains(name);

    private static JsonObject BuildLlmJsonApiRequest(string method, JsonElement? arguments)
    {
        var request = new JsonObject { ["method"] = method };
        if (!arguments.HasValue ||
            arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return request;
        }

        foreach (var prop in arguments.Value.EnumerateObject())
        {
            if (string.Equals(prop.Name, "method", StringComparison.Ordinal))
            {
                if (prop.Value.ValueKind != JsonValueKind.String ||
                    !string.Equals(prop.Value.GetString(), method, StringComparison.Ordinal))
                {
                    throw new ArgumentException("arguments.method must match tool name");
                }
                continue;
            }

            request[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        return request;
    }

    private static JsonObject BuildLlmJsonApiInfrastructureError(
        string method,
        string code,
        string message,
        JsonObject? details) =>
        new()
        {
            ["ok"] = false,
            ["method"] = method,
            ["code"] = code,
            ["message"] = message,
            ["details"] = details ?? new JsonObject(),
        };

    private static JsonObject BuildFileDetails(string? filePath)
    {
        var details = new JsonObject();
        if (!string.IsNullOrEmpty(filePath))
            details["file"] = filePath;
        return details;
    }

    private static JsonObject BuildGraphLoadDetails(GraphLoadException ex)
    {
        var details = BuildFileDetails(ex.FilePath);
        if (!string.IsNullOrEmpty(ex.NodePath))
            details["path"] = ex.NodePath;
        return details;
    }

    private static bool IsLlmJsonApiError(JsonObject envelope)
    {
        if (!envelope.TryGetPropertyValue("ok", out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<bool>(out var ok))
        {
            return true;
        }

        return !ok;
    }

    private static JsonRpcResponse MakeToolTextResponse(JsonElement? id, string text, bool isError)
    {
        var result = new CallToolResult(
            Content: new[] { new McpContentItem("text", text) },
            IsError: isError ? true : (bool?)null);

        return MakeOk(id, JsonSerializer.SerializeToElement(result, RelaxedCtx.CallToolResult));
    }

    private JsonRpcResponse HandleShutdown(JsonRpcRequest request)
    {
        // Триггер graceful shutdown — KernelHandler ждёт ApplicationStopping.
        // Ответ возвращаем сразу (до фактической остановки), чтобы клиент получил OK.
        _lifetime.StopApplication();
        return EmptyOk(request.Id);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteWithCaptureAsync(
        GraphEntry entry, string[] argv, CancellationToken ct)
    {
        // Двойная сериализация: per-graph semaphore (заявленный инвариант) + global
        // capture-lock (вынужденный костыль из-за process-global Console.SetOut).
        // global снимется в более позднем шаге, когда handlers начнут принимать TextWriter.
        await entry.Semaphore.WaitAsync(ct);
        try
        {
            _registry.Touch(entry);
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

    private static SchemaDocument? TryLoadSchema(string storagePath)
    {
        var schemaPath = Path.Combine(storagePath, "Схема.yml");
        if (!File.Exists(schemaPath)) return null;
        try { return SchemaLoader.LoadSchema(schemaPath); }
        catch { return null; }
    }

    /// <summary>
    /// Калька с <see cref="DocsWalker.Core.Mcp.McpServer.BuildInputSchema"/> —
    /// JSON-Schema для inputSchema MCP-tool. Скопировано (а не вынесено в общий
    /// helper), чтобы не плодить публичный API в Core для одного потребителя на
    /// этой стадии.
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

    private static JsonRpcResponse MakeUnknownGraphError(JsonElement? id, string graphName) =>
        MakeError(id, JsonRpcErrorCodes.InvalidParams,
            $"unknown_graph: '{graphName}' (kernel-config содержит только статически зарегистрированные графы)");

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
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
