using System.Text.Json;
using System.Text.Json.Nodes;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Mcp;
using DocsWalker.Core.Mcp;
using DocsWalker.Core.Sessions;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение MCP-сервера (#364, #366, #370): JSON-RPC 2.0 поверх stdio,
/// initialize/tools/list/tools/call, маршалинг argv и проброс session_id
/// в RequestContext. Тестируем через <see cref="McpServer.HandleMessageAsync"/>
/// — точка вне stdio-loop'а, не требует файлового ввода-вывода.
/// <para>
/// Серилизуется с <see cref="IpcSmokeTests"/> через collection "ConsoleRedirect":
/// оба класса заворачивают <see cref="Console.SetOut"/> вокруг диспатчера, а
/// настройка глобальная по процессу — параллельный запуск этих двух классов
/// рассинхронизирует stdout-перехват и валит обе сборки. Внутри одного class'а
/// xUnit и так серилизует тесты.
/// </para>
/// </summary>
[Collection("ConsoleRedirect")]
public class McpServerTests
{
    private static McpServer NewServer(SessionState? sessions = null)
    {
        var tools = CommandsToTools.Build();
        return new McpServer(
            input:      Stream.Null,
            output:     Stream.Null,
            dispatcher: Dispatcher.Run,
            tools:      tools,
            sessions:   sessions);
    }

    private static JsonElement Send(McpServer server, string json)
    {
        var resp = server.HandleMessageAsync(json, default).GetAwaiter().GetResult();
        Assert.NotNull(resp);
        using var doc = JsonDocument.Parse(resp!);
        return doc.RootElement.Clone();
    }

    private static string CallToolJson(int id, string toolName, object args)
    {
        var argsNode = JsonNode.Parse(JsonSerializer.Serialize(args));
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = argsNode,
            },
        };
        return req.ToJsonString();
    }

    // ── BuildArgvFromArguments ───────────────────────────────────────────────

    [Fact]
    public void BuildArgv_StringValue_ProducesKvFlag()
    {
        using var doc = JsonDocument.Parse(@"{""query"":""validator""}");
        var argv = McpServer.BuildArgvFromArguments("search", doc.RootElement);
        Assert.Equal(new[] { "search", "--query=validator" }, argv);
    }

    [Fact]
    public void BuildArgv_IntegerValue_ProducesKvFlag()
    {
        using var doc = JsonDocument.Parse(@"{""id"":42,""depth"":2}");
        var argv = McpServer.BuildArgvFromArguments("get-subtree", doc.RootElement);
        Assert.Contains("--id=42", argv);
        Assert.Contains("--depth=2", argv);
    }

    [Fact]
    public void BuildArgv_ArrayValue_ProducesCsvIdList()
    {
        using var doc = JsonDocument.Parse(@"{""ids"":[1,8,42]}");
        var argv = McpServer.BuildArgvFromArguments("get-nodes", doc.RootElement);
        Assert.Equal(new[] { "get-nodes", "--ids=1,8,42" }, argv);
    }

    [Fact]
    public void BuildArgv_BooleanValue_TrueFalse()
    {
        using var doc = JsonDocument.Parse(@"{""dry-run"":true,""no-seen"":false}");
        var argv = McpServer.BuildArgvFromArguments("create-node", doc.RootElement);
        Assert.Contains("--dry-run=true", argv);
        Assert.Contains("--no-seen=false", argv);
    }

    [Fact]
    public void BuildArgv_ObjectValue_PassedAsRawJson()
    {
        using var doc = JsonDocument.Parse(
            @"{""operations"":[{""op"":""create-node"",""type"":""section""}]}");
        var argv = McpServer.BuildArgvFromArguments("transaction", doc.RootElement);
        Assert.Equal(2, argv.Length);
        Assert.Equal("transaction", argv[0]);
        Assert.StartsWith("--operations=", argv[1]);
        // RawText сохраняется один-в-один.
        Assert.Contains("\"op\":\"create-node\"", argv[1]);
    }

    [Fact]
    public void BuildArgv_SnakeCaseKeys_NormalizedToKebab()
    {
        using var doc = JsonDocument.Parse(@"{""from_id"":42,""to_id"":8,""name"":""rel""}");
        var argv = McpServer.BuildArgvFromArguments("create-ref", doc.RootElement);
        Assert.Contains("--from-id=42", argv);
        Assert.Contains("--to-id=8", argv);
        Assert.Contains("--name=rel", argv);
    }

    [Fact]
    public void BuildArgv_NoArguments_ReturnsToolNameOnly()
    {
        var argv = McpServer.BuildArgvFromArguments("get-map", null);
        Assert.Equal(new[] { "get-map" }, argv);
    }

    // ── initialize ───────────────────────────────────────────────────────────

    [Fact]
    public void Initialize_ReturnsServerInfoAndProtocolVersion()
    {
        var server = NewServer();
        var resp = Send(server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");

        Assert.Equal("2.0", resp.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, resp.GetProperty("id").GetInt32());
        var result = resp.GetProperty("result");
        Assert.Equal(McpServer.McpProtocolVersion, result.GetProperty("protocolVersion").GetString());
        Assert.Equal("DocsWalker", result.GetProperty("serverInfo").GetProperty("name").GetString());
        var caps = result.GetProperty("capabilities");
        Assert.False(caps.GetProperty("tools").GetProperty("listChanged").GetBoolean());
    }

    [Fact]
    public void Initialize_RegeneratesSessionIdEachTime()
    {
        var server = NewServer();
        Send(server, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        var sid1 = server.CurrentSessionId;
        Send(server, """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{}}""");
        var sid2 = server.CurrentSessionId;

        Assert.NotEqual(sid1, sid2);
        Assert.True(Guid.TryParse(sid1, out _));
        Assert.True(Guid.TryParse(sid2, out _));
    }

    // ── tools/list ───────────────────────────────────────────────────────────

    [Fact]
    public void ListTools_ContainsAllReadAndWriteCommands_ButNotRunOrMcpServer()
    {
        var server = NewServer();
        var resp = Send(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = resp.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToHashSet();

        Assert.Contains("get-nodes", tools);
        Assert.Contains("get-usage-guide", tools);
        Assert.Contains("transaction", tools);
        Assert.Contains("create-node", tools);

        // Серверные команды не выставляются как MCP-tools — их вызывает оператор.
        Assert.DoesNotContain("run", tools);
        Assert.DoesNotContain("mcp-server", tools);
    }

    [Fact]
    public void ListTools_GetNodes_HasInputSchemaWithIdsArrayAndOptionalNoSeen()
    {
        var server = NewServer();
        var resp = Send(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var getNodes = resp.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "get-nodes");

        var schema = getNodes.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.Equal("array", props.GetProperty("ids").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("ids").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("no-seen").GetProperty("type").GetString());

        var required = schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Contains("ids", required);
        Assert.DoesNotContain("no-seen", required);
        // root всегда optional.
        Assert.DoesNotContain("root", required);
    }

    [Fact]
    public void ListTools_WriteCommand_HasDryRunOptionalParam()
    {
        var server = NewServer();
        var resp = Send(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var createNode = resp.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "create-node");
        var props = createNode.GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal("boolean", props.GetProperty("dry-run").GetProperty("type").GetString());
    }

    [Fact]
    public void ListTools_ReadCommand_HasNoDryRunParam()
    {
        var server = NewServer();
        var resp = Send(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var getMap = resp.GetProperty("result").GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "get-map");
        var props = getMap.GetProperty("inputSchema").GetProperty("properties");
        Assert.False(props.TryGetProperty("dry-run", out _));
    }

    // ── tools/call ───────────────────────────────────────────────────────────

    [Fact]
    public void CallTool_GetUsageGuide_ReturnsTextContent()
    {
        var server = NewServer();
        var resp = Send(server, CallToolJson(1, "get-usage-guide", new { root = TestPaths.RepoRoot }));

        Assert.False(resp.TryGetProperty("error", out _));
        var content = resp.GetProperty("result").GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        var item = content[0];
        Assert.Equal("text", item.GetProperty("type").GetString());
        var text = item.GetProperty("text").GetString()!;
        Assert.Contains("mental_model", text);
        Assert.Contains("commands", text);
    }

    [Fact]
    public void CallTool_GetNodes_ValidIds_ReturnsNodesArray()
    {
        var server = NewServer();
        Send(server, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}""");
        var resp = Send(server, CallToolJson(1, "get-nodes",
            new { ids = new[] { 1 }, root = TestPaths.RepoRoot }));

        Assert.False(resp.TryGetProperty("error", out _));
        var result = resp.GetProperty("result");
        // isError либо отсутствует (success), либо false.
        if (result.TryGetProperty("isError", out var isError))
            Assert.False(isError.GetBoolean());

        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var nodesDoc = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Array, nodesDoc.RootElement.ValueKind);
        Assert.True(nodesDoc.RootElement.GetArrayLength() >= 1);
        Assert.Equal(1, nodesDoc.RootElement[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void CallTool_GetNodes_PropagatesSessionIdToSeenSet()
    {
        var sessions = new SessionState();
        var server = NewServer(sessions);
        Send(server, """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}""");
        var sid = Guid.Parse(server.CurrentSessionId);

        Send(server, CallToolJson(1, "get-nodes",
            new { ids = new[] { 1 }, root = TestPaths.RepoRoot }));

        // Read-handler отметил id=1 как seen в сессии McpServer.CurrentSessionId.
        Assert.True(sessions.Sessions.ContainsKey(sid));
        Assert.Contains(1, sessions.Sessions[sid].Ids);
    }

    [Fact]
    public void CallTool_UnknownTool_ReturnsInvalidParamsError()
    {
        var server = NewServer();
        var resp = Send(server, CallToolJson(1, "no-such-tool", new { x = 1 }));
        var error = resp.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.Contains("unknown tool", error.GetProperty("message").GetString());
    }

    [Fact]
    public void CallTool_DispatcherErrorPath_ReturnsCallToolResultWithIsErrorTrue()
    {
        var server = NewServer();
        // get-nodes без --ids — dispatcher вернёт exit≠0 + stderr с error envelope.
        // По контракту MCP-tool это не protocol-error, а tool-result с isError=true.
        var resp = Send(server, CallToolJson(1, "get-nodes",
            new { root = TestPaths.RepoRoot }));

        Assert.False(resp.TryGetProperty("error", out _));
        var result = resp.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("missing_parameter", text);
    }

    // ── general protocol ──────────────────────────────────────────────────────

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        var server = NewServer();
        var resp = Send(server,
            """{"jsonrpc":"2.0","id":1,"method":"foo/bar","params":{}}""");
        var error = resp.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void ParseError_OnMalformedJson_ReturnsParseError()
    {
        var server = NewServer();
        var resp = Send(server, "{not json at all");
        var error = resp.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.ParseError, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Notification_NoId_ReturnsNullResponse()
    {
        var server = NewServer();
        var raw = await server.HandleMessageAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""",
            default);
        Assert.Null(raw);
    }

    [Fact]
    public void Shutdown_ReturnsEmptyResultOk()
    {
        var server = NewServer();
        var resp = Send(server, """{"jsonrpc":"2.0","id":7,"method":"shutdown"}""");
        Assert.Equal(7, resp.GetProperty("id").GetInt32());
        Assert.False(resp.TryGetProperty("error", out _));
        Assert.True(resp.TryGetProperty("result", out _));
    }

    [Fact]
    public void IdRoundtrip_PreservesStringId()
    {
        var server = NewServer();
        var resp = Send(server,
            """{"jsonrpc":"2.0","id":"abc-123","method":"tools/list"}""");
        Assert.Equal("abc-123", resp.GetProperty("id").GetString());
    }
}
