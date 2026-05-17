using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocsWalker.Core.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Kernel;

/// <summary>
/// JSON-RPC 2.0 endpoint kernel-а: один POST per <c>/{graph}</c> →
/// один MCP-message → один JSON-RPC response.
///
/// <para>
/// Поддерживаемые методы:
/// <list type="bullet">
///   <item><c>initialize</c> — MCP handshake.</item>
///   <item><c>notifications/initialized</c>, <c>notifications/cancelled</c>
///     — notification ack (без ответа).</item>
///   <item><c>tools/list</c> — список из двух tool-ов <c>read</c> и
///     <c>tx</c> с inputSchema.</item>
///   <item><c>tools/call</c> name=read|tx — парсит arguments через
///     <see cref="RequestParser"/>, исполняет через
///     <see cref="ReadExecutor"/>/<see cref="TxExecutor"/>, сериализует
///     ответ через <see cref="WireFormat"/> и кладёт в
///     <c>content[0].text</c>.</item>
///   <item><c>shutdown</c> — graceful stop хоста.</item>
/// </list>
/// </para>
///
/// <para>
/// Контракт routing: имя графа — в URL-сегменте, не в
/// <c>arguments</c>. Неизвестный граф → JSON-RPC error
/// <c>InvalidParams</c> с message <c>unknown_graph</c>.
/// </para>
///
/// <para>
/// Контракт ошибок:
/// <list type="bullet">
///   <item>Транспортные ошибки (JSON parse, неверный envelope,
///     неизвестный метод/граф) → JSON-RPC <c>error</c> на верхнем
///     уровне ответа.</item>
///   <item>API-ошибки (<see cref="ApiException"/>) → MCP envelope
///     <c>{content:[{type:"text",text:"&lt;json-error&gt;"}], isError:true}</c>
///     с телом, сериализованным через <see cref="WireFormat.SerializeError(ApiException)"/>.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class RpcDispatcher
{
    private const string ServerName = "DocsWalker";
    private const string ServerVersion = "2.0.0-dev";
    private const string McpProtocolVersion = "2024-11-05";

    private static readonly UTF8Encoding StrictUtf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions RelaxedOptions = new(KernelJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly KernelJsonContext RelaxedCtx = new(RelaxedOptions);

    private readonly GraphRegistry _registry;
    private readonly IHostApplicationLifetime _lifetime;

    public RpcDispatcher(GraphRegistry registry, IHostApplicationLifetime lifetime)
    {
        _registry = registry;
        _lifetime = lifetime;
    }

    public async Task HandleAsync(HttpContext ctx, string graphName)
    {
        string body;
        try
        {
            using var reader = new StreamReader(
                ctx.Request.Body,
                StrictUtf8NoBom,
                detectEncodingFromByteOrderMarks: false);
            body = await reader.ReadToEndAsync(ctx.RequestAborted);
        }
        catch (DecoderFallbackException)
        {
            await WriteResponseAsync(ctx, MakeError(
                id: null,
                code: JsonRpcErrorCodes.ParseError,
                message: "JSON body is not valid UTF-8."));
            return;
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

    private static async Task WriteResponseAsync(HttpContext ctx, JsonRpcResponse resp)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(Serialize(resp), Encoding.UTF8, ctx.RequestAborted);
    }

    public async Task<string?> HandleMessageAsync(string requestJson, string graphName, CancellationToken ct)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(requestJson, KernelJsonContext.Default.JsonRpcRequest);
        }
        catch (JsonException)
        {
            return Serialize(MakeError(
                id: null,
                code: JsonRpcErrorCodes.ParseError,
                message: "JSON parse error"));
        }
        if (request is null || string.IsNullOrEmpty(request.Method))
        {
            return Serialize(MakeError(
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
        return Serialize(response);
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

    private static JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        var result = new InitializeResult(
            ProtocolVersion: McpProtocolVersion,
            ServerInfo: new McpServerInfo(ServerName, ServerVersion),
            Capabilities: new McpServerCapabilities(
                Tools: new McpToolsCapability(ListChanged: false)));
        return MakeOk(request.Id,
            JsonSerializer.SerializeToElement(result, RelaxedCtx.InitializeResult));
    }

    private JsonRpcResponse HandleListTools(JsonRpcRequest request, string graphName)
    {
        if (!_registry.TryGet(graphName, out _))
        {
            return MakeUnknownGraphError(request.Id, graphName);
        }
        var tools = ToolCatalog.BuildMcpTools();
        var result = new ListToolsResult(tools);
        return MakeOk(request.Id,
            JsonSerializer.SerializeToElement(result, RelaxedCtx.ListToolsResult));
    }

    private async Task<JsonRpcResponse> HandleCallToolAsync(
        JsonRpcRequest request, string graphName, CancellationToken ct)
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
                KernelJsonContext.Default.CallToolParams);
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
        if (!_registry.TryGet(graphName, out var entry))
        {
            return MakeUnknownGraphError(request.Id, graphName);
        }

        var argumentsJson = ExtractArgumentsJson(callParams.Arguments);
        var (text, isError) = await Task.Run(
            () => InvokeTool(entry, callParams.Name!, argumentsJson, ct),
            ct);

        var result = new CallToolResult(
            Content: [new McpContentItem("text", text)],
            IsError: isError ? true : (bool?)null);
        return MakeOk(request.Id,
            JsonSerializer.SerializeToElement(result, RelaxedCtx.CallToolResult));
    }

    private static string ExtractArgumentsJson(JsonElement? arguments)
    {
        if (!arguments.HasValue ||
            arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "{}";
        }
        return arguments.Value.GetRawText();
    }

    private static (string Text, bool IsError) InvokeTool(
        GraphEntry entry, string toolName, string argumentsJson, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "read" => (ExecuteRead(entry, argumentsJson), false),
                "tx" => (ExecuteTx(entry, argumentsJson, ct), false),
                _ => (
                    WireFormat.SerializeError(
                        new ApiError(ApiErrorCodes.UnknownMethod,
                            new ApiErrorDetails(Path: null,
                                Extras: new Dictionary<string, object?> { ["tool"] = toolName }))),
                    true),
            };
        }
        catch (ApiException ex)
        {
            return (WireFormat.SerializeError(ex), true);
        }
        catch (Exception ex)
        {
            var error = new ApiError("internal_error",
                new ApiErrorDetails(Path: null,
                    Extras: new Dictionary<string, object?> { ["message"] = ex.Message }));
            return (WireFormat.SerializeError(error), true);
        }
    }

    private static string ExecuteRead(GraphEntry entry, string argumentsJson)
    {
        var request = RequestParser.ParseRead(argumentsJson);
        using var conn = entry.OpenConnection();
        var exec = new ReadExecutor(conn, entry.Name);
        var response = exec.Execute(request);
        return WireFormat.SerializeRead(response);
    }

    private static string ExecuteTx(GraphEntry entry, string argumentsJson, CancellationToken ct)
    {
        var request = RequestParser.ParseTx(argumentsJson);
        // SQLite single-writer: tx-ы по одному графу сериализуются через
        // WriteLock. Без него параллельные write-tx натыкаются на SQLITE_BUSY.
        entry.WriteLock.Wait(ct);
        try
        {
            using var conn = entry.OpenConnection();
            var exec = new TxExecutor(conn, entry.Name);
            var response = exec.Execute(request);
            return WireFormat.SerializeTx(response);
        }
        finally
        {
            entry.WriteLock.Release();
        }
    }

    private JsonRpcResponse HandleShutdown(JsonRpcRequest request)
    {
        _lifetime.StopApplication();
        return EmptyOk(request.Id);
    }

    // ---- envelope helpers -------------------------------------------------

    private static JsonRpcResponse MakeOk(JsonElement? id, JsonElement result) =>
        new("2.0", id ?? NullId, result, null);

    private static JsonRpcResponse MakeError(JsonElement? id, int code, string message) =>
        new("2.0", id ?? NullId, null, new JsonRpcError(code, message, null));

    private static JsonRpcResponse MakeUnknownGraphError(JsonElement? id, string graphName) =>
        MakeError(id, JsonRpcErrorCodes.InvalidParams,
            $"unknown_graph: '{graphName}'");

    private static JsonRpcResponse EmptyOk(JsonElement? id)
    {
        using var doc = JsonDocument.Parse("{}");
        return MakeOk(id, doc.RootElement.Clone());
    }

    private static readonly JsonElement NullId = ParseNullId();

    private static JsonElement ParseNullId()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }

    private static string Serialize(JsonRpcResponse response) =>
        JsonSerializer.Serialize(response, RelaxedCtx.JsonRpcResponse);
}
