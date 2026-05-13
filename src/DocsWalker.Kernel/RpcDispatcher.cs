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

    private static readonly HashSet<string> SessionToolNames = new(StringComparer.Ordinal)
    {
        "brief",
        "checkpoint",
        "resume",
        "context-check",
    };

    private static readonly UTF8Encoding StrictUtf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

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
            var parseErrorJson = SerializeResponse(MakeError(
                id: null,
                code: JsonRpcErrorCodes.ParseError,
                message: "JSON body is not valid UTF-8."));
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(parseErrorJson, Encoding.UTF8, ctx.RequestAborted);
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

        if (IsSessionTool(callParams.Name))
        {
            return await HandleSessionToolAsync(
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
                var args = NormalizeArguments(arguments);
                var executeMethod = toolName;
                var txMode = string.Empty;

                if (toolName == "query" &&
                    TryGetString(args, "session_id", out var querySessionId) &&
                    !string.IsNullOrWhiteSpace(querySessionId) &&
                    !IsSafeSessionId(querySessionId))
                {
                    envelope = BuildLlmJsonApiInfrastructureError(
                        toolName,
                        "invalid_session_id",
                        "session_id may contain only letters, digits, '.', '_' and '-'.",
                        null);
                    return MakeToolTextResponse(
                        requestId,
                        envelope.ToJsonString(RelaxedCtx.Options),
                        isError: true);
                }

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

                if (toolName == "tx")
                {
                    txMode = ReadTxMode(args);
                    if (!IsKnownTxMode(txMode))
                    {
                        envelope = BuildInvalidTxModeError(txMode);
                        return MakeToolTextResponse(
                            requestId,
                            envelope.ToJsonString(RelaxedCtx.Options),
                            isError: true);
                    }

                    if (txMode == "preview")
                    {
                        executeMethod = "hit";
                    }
                    else
                    {
                        var hitRequest = BuildLlmJsonApiRequest("hit", args, expectedMethod: "tx");
                        var hitEnvelope = executor.Execute(hitRequest);
                        if (IsLlmJsonApiError(hitEnvelope))
                        {
                            envelope = RewriteTxPreviewEnvelope(hitEnvelope, txMode);
                            return MakeToolTextResponse(
                                requestId,
                                envelope.ToJsonString(RelaxedCtx.Options),
                                isError: true);
                        }

                        var previewTargetIds = CollectIdsFromJson(hitEnvelope)
                            .Where(id => id >= 0)
                            .ToHashSet();
                        foreach (var id in CollectIdsFromJson(CloneJson(args)).Where(id => id >= 0))
                            previewTargetIds.Add(id);
                        var guardError = BuildTxGuardError(entry, args, txMode, baseRevision, previewTargetIds);
                        if (guardError is not null)
                        {
                            envelope = guardError;
                            return MakeToolTextResponse(
                                requestId,
                                envelope.ToJsonString(RelaxedCtx.Options),
                                isError: true);
                        }
                    }
                }

                var request = BuildLlmJsonApiRequest(executeMethod, args, expectedMethod: toolName);
                envelope = executor.Execute(request);
                if (toolName == "tx" && txMode == "preview")
                    envelope = RewriteTxPreviewEnvelope(envelope, txMode);
                if (toolName == "query" && !IsLlmJsonApiError(envelope))
                    TryUpdateQuerySession(entry, args, envelope, baseRevision);
                if (toolName == "tx" && txMode is "apply_if_safe" or "apply" && !IsLlmJsonApiError(envelope))
                    TryUpdateTxSession(entry, args, envelope, ctx);
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

    private static bool IsSessionTool(string name) =>
        SessionToolNames.Contains(name);

    private static JsonObject BuildLlmJsonApiRequest(
        string method,
        JsonElement? arguments,
        string? expectedMethod = null)
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
                var requiredMethod = expectedMethod ?? method;
                if (prop.Value.ValueKind != JsonValueKind.String ||
                    !string.Equals(prop.Value.GetString(), requiredMethod, StringComparison.Ordinal))
                {
                    throw new ArgumentException("arguments.method must match tool name");
                }
                continue;
            }

            request[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        return request;
    }

    private static string ReadTxMode(JsonElement args)
    {
        if (!TryGetString(args, "mode", out var mode) || string.IsNullOrWhiteSpace(mode))
            return "apply_if_safe";
        return mode.Trim();
    }

    private static bool IsKnownTxMode(string mode) =>
        mode is "preview" or "apply_if_safe" or "apply";

    private static JsonObject BuildInvalidTxModeError(string mode) =>
        BuildLlmJsonApiInfrastructureError(
            "tx",
            "invalid_mode",
            "tx.mode must be one of: preview, apply_if_safe, apply.",
            new JsonObject
            {
                ["mode"] = mode,
                ["allowed"] = JsonStringArray("preview", "apply_if_safe", "apply"),
            });

    private static JsonObject RewriteTxPreviewEnvelope(JsonObject envelope, string mode)
    {
        var result = envelope.DeepClone().AsObject();
        result["method"] = "tx";
        result["mode"] = mode;
        result["validated_by"] = "hit";
        return result;
    }

    private static JsonObject? BuildTxGuardError(
        GraphEntry entry,
        JsonElement args,
        string mode,
        long baseRevision,
        HashSet<int> targetIds)
    {
        var blockers = new JsonArray();
        var worksetCount = 0;

        if (!TryGetString(args, "intent", out var intent) || string.IsNullOrWhiteSpace(intent))
            blockers.Add((JsonNode?)JsonValue.Create("missing_intent"));

        if (mode == "apply_if_safe")
        {
            if (!TryGetString(args, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            {
                blockers.Add((JsonNode?)JsonValue.Create("missing_session_id"));
            }
            else if (!IsSafeSessionId(sessionId))
            {
                blockers.Add((JsonNode?)JsonValue.Create("invalid_session_id"));
            }
            else
            {
                var path = GetSessionFilePath(entry, sessionId, createDirectory: false);
                if (!File.Exists(path))
                {
                    blockers.Add((JsonNode?)JsonValue.Create("session_not_found"));
                }
                else if (!TryReadSession(path, out var session))
                {
                    blockers.Add((JsonNode?)JsonValue.Create("malformed_session"));
                }
                else
                {
                    var workset = CollectSessionNodeIds(session);
                    worksetCount = workset.Count;
                    if (TryGetLong(session, "graph_revision", out var sessionRevision) &&
                        sessionRevision < baseRevision)
                    {
                        blockers.Add((JsonNode?)JsonValue.Create("stale_revision"));
                    }

                    foreach (var id in targetIds
                                 .Where(id => id != Node.RootId && !workset.Contains(id))
                                 .Distinct()
                                 .OrderBy(id => id))
                    {
                        blockers.Add((JsonNode?)JsonValue.Create($"unread_target:{id}"));
                    }
                }
            }
        }
        else if (TryGetString(args, "session_id", out var optionalSessionId) &&
                 !string.IsNullOrWhiteSpace(optionalSessionId) &&
                 !IsSafeSessionId(optionalSessionId))
        {
            blockers.Add((JsonNode?)JsonValue.Create("invalid_session_id"));
        }

        if (blockers.Count == 0)
            return null;

        return new JsonObject
        {
            ["ok"] = false,
            ["method"] = "tx",
            ["code"] = "session_guard_failed",
            ["message"] = "tx session guard blocked write.",
            ["details"] = new JsonObject
            {
                ["mode"] = mode,
                ["base_revision"] = baseRevision,
                ["target_ids"] = BuildIdArray(targetIds),
                ["workset_node_count"] = worksetCount,
                ["blockers"] = blockers,
            },
        };
    }

    private static void TryUpdateQuerySession(
        GraphEntry entry,
        JsonElement args,
        JsonObject envelope,
        long baseRevision)
    {
        if (!TryGetString(args, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var ids = CollectIdsFromJson(envelope).Where(id => id >= 0).ToArray();
            var session = LoadOrCreateSession(entry, sessionId, baseRevision, "auto-created by query");
            var added = AddUniqueIds(session, "read_workset", ids);
            TouchSession(session, baseRevision);
            session["last_query_at"] = DateTimeOffset.UtcNow.ToString("O");
            SaveSession(entry, sessionId, session);

            envelope["session"] = new JsonObject
            {
                ["session_id"] = sessionId,
                ["read_workset_added"] = added,
                ["read_workset_count"] = CollectSessionNodeIds(session).Count,
                ["graph_revision"] = baseRevision,
                ["persisted"] = true,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            envelope["session"] = BuildSessionWarning(sessionId, ex);
        }
    }

    private static void TryUpdateTxSession(
        GraphEntry entry,
        JsonElement args,
        JsonObject envelope,
        WriteContext ctx)
    {
        if (!TryGetString(args, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var revision = new SequenceCounter(ctx.SequencePath).Read();
            var ids = CollectIdsFromJson(envelope).Where(id => id >= 0).ToArray();
            var session = LoadOrCreateSession(entry, sessionId, revision, "auto-created by tx");
            var added = AddUniqueIds(session, "touched_nodes", ids);
            TouchSession(session, revision);
            if (TryGetString(args, "intent", out var intent) && !string.IsNullOrWhiteSpace(intent))
                session["last_intent"] = intent;
            session["last_tx_at"] = DateTimeOffset.UtcNow.ToString("O");
            SaveSession(entry, sessionId, session);

            envelope["session"] = new JsonObject
            {
                ["session_id"] = sessionId,
                ["touched_added"] = added,
                ["touched_count"] = CollectSessionNodeIds(session).Count,
                ["graph_revision"] = revision,
                ["persisted"] = true,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            envelope["session"] = BuildSessionWarning(sessionId, ex);
        }
    }

    private static JsonObject BuildSessionWarning(string sessionId, Exception ex) =>
        new()
        {
            ["session_id"] = sessionId,
            ["persisted"] = false,
            ["warning"] = "session_update_failed",
            ["message"] = ex.Message,
        };

    private static JsonObject LoadOrCreateSession(
        GraphEntry entry,
        string sessionId,
        long baseRevision,
        string summary)
    {
        var path = GetSessionFilePath(entry, sessionId, createDirectory: true);
        var session = File.Exists(path) && TryReadSession(path, out var existing)
            ? existing
            : new JsonObject();

        session["schema_version"] = 1;
        session["graph"] = entry.Name;
        session["session_id"] = sessionId;
        if (!session.TryGetPropertyValue("summary", out _))
            session["summary"] = summary;
        TouchSession(session, baseRevision);
        return session;
    }

    private static bool TryReadSession(string path, out JsonObject session)
    {
        try
        {
            session = JsonNode.Parse(File.ReadAllText(path, StrictUtf8NoBom)) as JsonObject
                ?? new JsonObject();
            return true;
        }
        catch (JsonException)
        {
            session = new JsonObject();
            return false;
        }
    }

    private static void SaveSession(GraphEntry entry, string sessionId, JsonObject session)
    {
        var path = GetSessionFilePath(entry, sessionId, createDirectory: true);
        File.WriteAllText(path, session.ToJsonString(), StrictUtf8NoBom);
    }

    private static void TouchSession(JsonObject session, long baseRevision)
    {
        session["graph_revision"] = baseRevision;
        session["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
    }

    private static int AddUniqueIds(JsonObject session, string propertyName, IEnumerable<int> ids)
    {
        var unique = new HashSet<int>();
        AddIds(session[propertyName], unique);
        var before = unique.Count;
        foreach (var id in ids.Where(id => id >= 0))
            unique.Add(id);

        session[propertyName] = BuildIdArray(unique);
        return unique.Count - before;
    }

    private static JsonArray BuildIdArray(IEnumerable<int> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids.Distinct().OrderBy(id => id))
            array.Add((JsonNode?)JsonValue.Create(id));
        return array;
    }

    private static HashSet<int> CollectIdsFromJson(JsonNode? node)
    {
        var ids = new HashSet<int>();
        CollectIdsFromJson(node, propertyName: null, ids);
        return ids;
    }

    private static void CollectIdsFromJson(JsonNode? node, string? propertyName, HashSet<int> ids)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var prop in obj)
                    CollectIdsFromJson(prop.Value, prop.Key, ids);
                break;

            case JsonArray array:
                if (IsIdArrayProperty(propertyName))
                {
                    foreach (var item in array)
                    {
                        if (item is JsonValue itemValue && itemValue.TryGetValue<int>(out var itemId))
                            ids.Add(itemId);
                    }
                }
                foreach (var item in array)
                    CollectIdsFromJson(item, propertyName: null, ids);
                break;

            case JsonValue value when IsIdScalarProperty(propertyName) && value.TryGetValue<int>(out var id):
                ids.Add(id);
                break;
        }
    }

    private static bool IsIdScalarProperty(string? propertyName) =>
        propertyName is "id" || (propertyName?.EndsWith("_id", StringComparison.Ordinal) == true);

    private static bool IsIdArrayProperty(string? propertyName) =>
        propertyName is "ids" || (propertyName?.EndsWith("_ids", StringComparison.Ordinal) == true);

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

    private async Task<JsonRpcResponse> HandleSessionToolAsync(
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
            envelope = toolName switch
            {
                "brief" => HandleBrief(entry, arguments),
                "checkpoint" => HandleCheckpoint(entry, arguments),
                "resume" => HandleResume(entry, arguments),
                "context-check" => HandleContextCheck(entry, arguments),
                _ => BuildSessionError(toolName, "unknown_tool", $"unknown session tool: {toolName}")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            envelope = BuildSessionError(toolName, "internal_error", ex.Message);
        }
        finally
        {
            entry.Semaphore.Release();
        }

        var ok = envelope["ok"]?.GetValue<bool>() == true;
        return MakeToolTextResponse(requestId, envelope.ToJsonString(), isError: !ok);
    }

    private static JsonObject HandleBrief(GraphEntry entry, JsonElement? arguments)
    {
        var args = NormalizeArguments(arguments);
        if (!TryGetString(args, "goal", out var goal) || string.IsNullOrWhiteSpace(goal))
            return BuildSessionError("brief", "missing_goal", "brief requires non-empty goal.");

        var (api, baseRevision) = LoadReadApi(entry);
        var hits = ReadApiJson.SearchToJson(api.Search(goal, limit: 8, compact: true));
        var overview = ReadApiJson.OverviewToJson(api.GetOverview());

        var pack = new JsonObject
        {
            ["overview"] = overview,
            ["relevant_nodes"] = hits,
            ["must_read"] = JsonStringArray("get-usage-guide", "get-overview"),
            ["suggested_next"] = JsonStringArray(
                "query/get-nodes для дочитывания relevant_nodes",
                "checkpoint после первого устойчивого решения",
                "context-check перед tx")
        };

        var response = new JsonObject
        {
            ["ok"] = true,
            ["method"] = "brief",
            ["graph"] = entry.Name,
            ["base_revision"] = baseRevision,
            ["goal"] = goal,
            ["pack"] = pack
        };
        if (TryGetString(args, "session_id", out var sessionId) && !string.IsNullOrWhiteSpace(sessionId))
            response["session_id"] = sessionId;
        if (args.TryGetProperty("scope", out var scope))
            response["scope"] = CloneJson(scope);

        return response;
    }

    private static JsonObject HandleCheckpoint(GraphEntry entry, JsonElement? arguments)
    {
        var args = NormalizeArguments(arguments);
        if (!TryGetString(args, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            return BuildSessionError("checkpoint", "missing_session_id", "checkpoint requires session_id.");
        if (!IsSafeSessionId(sessionId))
            return BuildSessionError("checkpoint", "invalid_session_id", "session_id may contain only letters, digits, '.', '_' and '-'.");
        if (!TryGetString(args, "summary", out var summary) || string.IsNullOrWhiteSpace(summary))
            return BuildSessionError("checkpoint", "missing_summary", "checkpoint requires non-empty summary.");

        var (_, baseRevision) = LoadReadApi(entry);
        var session = new JsonObject
        {
            ["schema_version"] = 1,
            ["graph"] = entry.Name,
            ["session_id"] = sessionId,
            ["summary"] = summary,
            ["graph_revision"] = baseRevision,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("O")
        };

        foreach (var prop in args.EnumerateObject())
        {
            if (prop.Name is "session_id" or "summary") continue;
            session[prop.Name] = CloneJson(prop.Value);
        }

        var path = GetSessionFilePath(entry, sessionId, createDirectory: true);
        File.WriteAllText(path, session.ToJsonString(), StrictUtf8NoBom);

        return new JsonObject
        {
            ["ok"] = true,
            ["method"] = "checkpoint",
            ["graph"] = entry.Name,
            ["session_id"] = sessionId,
            ["graph_revision"] = baseRevision,
            ["persisted"] = true
        };
    }

    private static JsonObject HandleResume(GraphEntry entry, JsonElement? arguments)
    {
        var args = NormalizeArguments(arguments);
        if (!TryGetString(args, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            return BuildSessionError("resume", "missing_session_id", "resume requires session_id.");
        if (!IsSafeSessionId(sessionId))
            return BuildSessionError("resume", "invalid_session_id", "session_id may contain only letters, digits, '.', '_' and '-'.");

        var path = GetSessionFilePath(entry, sessionId, createDirectory: false);
        if (!File.Exists(path))
            return BuildSessionError("resume", "session_not_found", $"work_session '{sessionId}' not found.");

        var session = JsonNode.Parse(File.ReadAllText(path, StrictUtf8NoBom)) as JsonObject
            ?? new JsonObject();
        var (_, baseRevision) = LoadReadApi(entry);

        return new JsonObject
        {
            ["ok"] = true,
            ["method"] = "resume",
            ["graph"] = entry.Name,
            ["session_id"] = sessionId,
            ["base_revision"] = baseRevision,
            ["session"] = session
        };
    }

    private static JsonObject HandleContextCheck(GraphEntry entry, JsonElement? arguments)
    {
        var args = NormalizeArguments(arguments);
        if (!TryGetString(args, "session_id", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            return BuildSessionError("context-check", "missing_session_id", "context-check requires session_id.");
        if (!IsSafeSessionId(sessionId))
            return BuildSessionError("context-check", "invalid_session_id", "session_id may contain only letters, digits, '.', '_' and '-'.");
        if (!args.TryGetProperty("write", out var write) || write.ValueKind != JsonValueKind.Object)
            return BuildSessionError("context-check", "missing_write", "context-check requires write object.");

        var path = GetSessionFilePath(entry, sessionId, createDirectory: false);
        if (!File.Exists(path))
            return BuildSessionError("context-check", "session_not_found", $"work_session '{sessionId}' not found.");

        var session = JsonNode.Parse(File.ReadAllText(path, StrictUtf8NoBom)) as JsonObject
            ?? new JsonObject();
        var (_, baseRevision) = LoadReadApi(entry);
        var workset = CollectSessionNodeIds(session);
        var (targetIds, hasSelector, hasMissingExpectedCount) = CollectWriteTargets(write);
        var blockers = new JsonArray();

        if (!TryGetString(args, "intent", out var intent) || string.IsNullOrWhiteSpace(intent))
            blockers.Add((JsonNode?)JsonValue.Create("missing_intent"));
        if (TryGetLong(session, "graph_revision", out var sessionRevision) && sessionRevision < baseRevision)
            blockers.Add((JsonNode?)JsonValue.Create("stale_revision"));
        foreach (var id in targetIds.Where(id => !workset.Contains(id)).Distinct().OrderBy(id => id))
            blockers.Add((JsonNode?)JsonValue.Create($"unread_target:{id}"));
        if (hasSelector)
            blockers.Add((JsonNode?)JsonValue.Create("selector_requires_hit"));
        if (hasMissingExpectedCount)
            blockers.Add((JsonNode?)JsonValue.Create("missing_expected_count"));

        var idsJson = new JsonArray();
        foreach (var id in targetIds.Distinct().OrderBy(id => id))
            idsJson.Add((JsonNode?)JsonValue.Create(id));

        var ok = blockers.Count == 0;
        return new JsonObject
        {
            ["ok"] = ok,
            ["method"] = "context-check",
            ["graph"] = entry.Name,
            ["session_id"] = sessionId,
            ["base_revision"] = baseRevision,
            ["target_ids"] = idsJson,
            ["workset_node_count"] = workset.Count,
            ["blockers"] = blockers
        };
    }

    private static (ReadApi Api, long BaseRevision) LoadReadApi(GraphEntry entry)
    {
        var ctx = WriteContext.FromStoragePath(entry.StoragePath);
        var schema = SchemaLoader.LoadSchema(ctx.SchemaPath);
        var loaded = DocumentLoader.Load(ctx.DocsRoot, schema);
        var baseRevision = new SequenceCounter(ctx.SequencePath).Read();
        return (new ReadApi(loaded.Graph, schema), baseRevision);
    }

    private static JsonElement NormalizeArguments(JsonElement? arguments)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
        return arguments.Value.Clone();
    }

    private static bool TryGetString(JsonElement args, string name, out string value)
    {
        value = string.Empty;
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString() ?? string.Empty;
        return true;
    }

    private static JsonNode? CloneJson(JsonElement element) =>
        JsonNode.Parse(element.GetRawText());

    private static JsonArray JsonStringArray(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }

    private static bool IsSafeSessionId(string sessionId)
    {
        if (sessionId.Length is 0 or > 80) return false;
        foreach (var ch in sessionId)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
                continue;
            return false;
        }
        return true;
    }

    private static string GetSessionFilePath(GraphEntry entry, string sessionId, bool createDirectory)
    {
        var docsRoot = Path.GetFullPath(entry.StoragePath);
        var workspaceRoot = Directory.GetParent(docsRoot)?.FullName ?? docsRoot;
        var dir = Path.Combine(workspaceRoot, ".dw", "sessions", entry.Name);
        if (createDirectory) Directory.CreateDirectory(dir);
        return Path.Combine(dir, sessionId + ".json");
    }

    private static HashSet<int> CollectSessionNodeIds(JsonObject session)
    {
        var ids = new HashSet<int>();
        AddIds(session["touched_nodes"], ids);
        AddIds(session["read_workset"], ids);
        AddIds(session["node_ids"], ids);
        if (session["read_workset"] is JsonObject workset)
            AddIds(workset["node_ids"], ids);
        return ids;
    }

    private static void AddIds(JsonNode? node, HashSet<int> ids)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var id))
        {
            ids.Add(id);
            return;
        }
        if (node is not JsonArray array) return;
        foreach (var item in array)
        {
            if (item is JsonValue itemValue && itemValue.TryGetValue<int>(out var itemId))
                ids.Add(itemId);
        }
    }

    private static (HashSet<int> TargetIds, bool HasSelector, bool HasMissingExpectedCount) CollectWriteTargets(JsonElement write)
    {
        var ids = new HashSet<int>();
        var hasSelector = false;
        var missingExpectedCount = false;

        if (!write.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array)
            return (ids, hasSelector, missingExpectedCount);

        foreach (var op in ops.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object) continue;
            AddElementIds(op, "id", ids);
            AddElementIds(op, "ids", ids);
            AddTargetElementIds(op, "source", ids);
            AddTargetElementIds(op, "from", ids);
            AddTargetElementIds(op, "to", ids);
            if (op.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
            {
                AddElementIds(target, "id", ids);
                AddElementIds(target, "ids", ids);
            }
            if (op.TryGetProperty("select", out var select) && select.ValueKind == JsonValueKind.Object)
            {
                hasSelector = true;
                if (!op.TryGetProperty("expected_count", out _))
                    missingExpectedCount = true;
            }
            if (op.TryGetProperty("path", out var path) &&
                path.ValueKind == JsonValueKind.String &&
                path.GetString() is { } rawPath &&
                rawPath.Contains('*', StringComparison.Ordinal) &&
                !op.TryGetProperty("expected_count", out _))
            {
                missingExpectedCount = true;
            }
        }

        return (ids, hasSelector, missingExpectedCount);
    }

    private static void AddTargetElementIds(JsonElement obj, string propertyName, HashSet<int> ids)
    {
        if (!obj.TryGetProperty(propertyName, out var target) || target.ValueKind != JsonValueKind.Object)
            return;
        AddElementIds(target, "id", ids);
        AddElementIds(target, "ids", ids);
    }

    private static void AddElementIds(JsonElement obj, string propertyName, HashSet<int> ids)
    {
        if (!obj.TryGetProperty(propertyName, out var prop)) return;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var id))
        {
            ids.Add(id);
            return;
        }
        if (prop.ValueKind != JsonValueKind.Array) return;
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var itemId))
                ids.Add(itemId);
        }
    }

    private static bool TryGetLong(JsonObject obj, string name, out long value)
    {
        value = 0;
        return obj[name] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static JsonObject BuildSessionError(string method, string code, string message) =>
        new()
        {
            ["ok"] = false,
            ["method"] = method,
            ["code"] = code,
            ["message"] = message
        };

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
