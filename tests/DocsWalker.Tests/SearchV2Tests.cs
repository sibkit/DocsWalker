using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Core.Mcp;
using DocsWalker.Kernel;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Tests;

[Collection("ConsoleRedirect")]
public class SearchV2Tests
{
    [Fact]
    public async Task Search_RemovedFromMcpSurface_ReturnsUnknownTool()
    {
        var raw = await McpTestFixture.CallToolAsync("search", ("query", "validator"));

        using var doc = JsonDocument.Parse(raw);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.Equal("unknown tool: search", error.GetProperty("message").GetString());
    }
}

/// <summary>
/// Общая инфраструктура для MCP-тестов: in-process <see cref="RpcDispatcher"/>
/// поверх реальных <c>docs/</c>. Сборка argv-объекта в JSON — вручную,
/// не через рефлексию JsonSerializer'а, чтобы избежать AOT-сюрпризов.
/// </summary>
internal static class McpTestFixture
{
    public static async Task<string> CallToolAsync(
        string toolName,
        params (string Key, object Value)[] args)
    {
        using var registry = new GraphRegistry(
            new[] { new KernelGraphConfig("main", TestPaths.DocsRoot) },
            TimeSpan.FromMinutes(10));
        var dispatcher = new RpcDispatcher(registry, new TestLifetime(), Dispatcher.Run);
        var requestJson = BuildToolsCallJson(1, toolName, args);
        var resp = await dispatcher.HandleMessageAsync(requestJson, "main", default);
        Assert.NotNull(resp);
        return resp!;
    }

    public static IReadOnlyList<JsonElement> ParseSuccessArray(string responseJson)
    {
        var text = ExtractContentText(responseJson);
        using var innerDoc = JsonDocument.Parse(text);
        var arr = new List<JsonElement>();
        foreach (var item in innerDoc.RootElement.EnumerateArray())
            arr.Add(item.Clone());
        return arr;
    }

    public static JsonElement ParseSuccessObject(string responseJson)
    {
        var text = ExtractContentText(responseJson);
        using var innerDoc = JsonDocument.Parse(text);
        return innerDoc.RootElement.Clone();
    }

    private static string ExtractContentText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var content = doc.RootElement.GetProperty("result").GetProperty("content");
        var text = content[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        return text!;
    }

    private static string BuildToolsCallJson(int id, string toolName, (string Key, object Value)[] args)
    {
        var argsBody = BuildArgsObject(args);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{argsBody}}}}}";
    }

    private static string BuildArgsObject((string Key, object Value)[] args)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in args)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(EscapeJsonString(k)).Append("\":");
            AppendJsonValue(sb, v);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendJsonValue(StringBuilder sb, object v)
    {
        switch (v)
        {
            case string s: sb.Append('"').Append(EscapeJsonString(s)).Append('"'); break;
            case int i: sb.Append(i); break;
            case long l: sb.Append(l); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case null: sb.Append("null"); break;
            default: throw new ArgumentException($"unsupported arg type {v.GetType()}");
        }
    }

    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

internal sealed class TestLifetime : IHostApplicationLifetime
{
    public bool StopCalled { get; private set; }
    public CancellationToken ApplicationStarted => default;
    public CancellationToken ApplicationStopping => default;
    public CancellationToken ApplicationStopped => default;
    public void StopApplication() => StopCalled = true;
}
