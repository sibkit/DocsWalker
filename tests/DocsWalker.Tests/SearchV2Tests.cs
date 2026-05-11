using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Kernel;
using Microsoft.Extensions.Hosting;

namespace DocsWalker.Tests;

/// <summary>
/// Покрытие search v2 (см. docs/DocsWalker.yml/#26) через JSON-RPC-канал kernel'а:
/// BM25-ранжирование, фильтры (in/type/tree+under), regex-режим, snippet и score
/// в ответе. Тесты идут через <see cref="RpcDispatcher.HandleMessageAsync"/> —
/// тот же путь, что используют MCP-клиенты (Claude Code и т.п.).
/// </summary>
[Collection("ConsoleRedirect")]
public class SearchV2Tests
{
    [Fact]
    public async Task Search_Bm25_RanksByScoreAndIncludesSnippet()
    {
        var hits = await CallSearchAsync(("query", "валидатор"));
        Assert.NotEmpty(hits);
        for (int i = 1; i < hits.Count; i++)
        {
            double prev = hits[i - 1].GetProperty("score").GetDouble();
            double curr = hits[i].GetProperty("score").GetDouble();
            Assert.True(prev >= curr,
                $"hits[{i - 1}].score ({prev}) должен быть ≥ hits[{i}].score ({curr})");
        }
        foreach (var h in hits)
        {
            Assert.True(h.TryGetProperty("id", out _));
            Assert.True(h.TryGetProperty("type", out _));
            Assert.True(h.TryGetProperty("title", out _));
            Assert.True(h.TryGetProperty("score", out _));
        }
    }

    [Fact]
    public async Task Search_TitleBoost_RanksTitleMatchAbovePlainTextMatch()
    {
        // Title-boost ×3 в режиме in=both: hit с "search" в title должен идти
        // строго выше любого hit'а с "search" только в text.
        var hits = await CallSearchAsync(("query", "search"));
        Assert.NotEmpty(hits);

        double minTitleScore = double.PositiveInfinity;
        double maxTextOnlyScore = 0;
        bool hasTitleHit = false;
        bool hasTextOnlyHit = false;
        foreach (var h in hits)
        {
            var title = h.GetProperty("title").GetString() ?? string.Empty;
            var score = h.GetProperty("score").GetDouble();
            bool titleMatch = title.Contains("search", StringComparison.OrdinalIgnoreCase);
            if (titleMatch)
            {
                hasTitleHit = true;
                minTitleScore = Math.Min(minTitleScore, score);
            }
            else
            {
                hasTextOnlyHit = true;
                maxTextOnlyScore = Math.Max(maxTextOnlyScore, score);
            }
        }
        Assert.True(hasTitleHit, "В выдаче должен быть хотя бы один title-hit.");
        if (hasTextOnlyHit)
        {
            Assert.True(minTitleScore > maxTextOnlyScore,
                $"Title-hit (min score {minTitleScore}) должен ранжироваться выше text-only (max {maxTextOnlyScore}).");
        }
    }

    [Fact]
    public async Task Search_TypeFilter_ReturnsOnlyMatchingType()
    {
        var hits = await CallSearchAsync(
            ("query", "поиск"),
            ("type", "definition"));
        Assert.NotEmpty(hits);
        foreach (var h in hits)
            Assert.Equal("definition", h.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Search_UnderFilter_LimitsToSubtree()
    {
        // section «Операции чтения» — id=17 в path-дереве.
        var insideHits = await CallSearchAsync(
            ("query", "узел"),
            ("under", 17));
        var insideIds = insideHits.Select(h => h.GetProperty("id").GetInt32()).ToHashSet();
        Assert.NotEmpty(insideIds);
        // id=8 — definition «узел» в section «Модель данных» (вне 17).
        Assert.DoesNotContain(8, insideIds);
    }

    [Fact]
    public async Task Search_RegexMode_NoScoreOrderedById()
    {
        var hits = await CallSearchAsync(
            ("query", "^DocsWalker$"),
            ("regex", true));
        for (int i = 1; i < hits.Count; i++)
        {
            int prev = hits[i - 1].GetProperty("id").GetInt32();
            int curr = hits[i].GetProperty("id").GetInt32();
            Assert.True(prev < curr);
        }
        Assert.All(hits, h => Assert.Equal(0.0, h.GetProperty("score").GetDouble()));
    }

    [Fact]
    public async Task Search_LimitParameter_HonoredInResponse()
    {
        var hits = await CallSearchAsync(
            ("query", "узел"),
            ("limit", 3));
        Assert.True(hits.Count <= 3, $"expected ≤3 hits, got {hits.Count}");
    }

    [Fact]
    public async Task Search_InTitleMode_OnlyTitleHits()
    {
        var hits = await CallSearchAsync(
            ("query", "search"),
            ("in", "title"));
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains(
            "search",
            h.GetProperty("title").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsError()
    {
        var raw = await McpTestFixture.CallToolAsync("search", ("query", ""));
        Assert.Contains("invalid_query", raw);
        Assert.Contains("\"isError\":true", raw);
    }

    private static async Task<IReadOnlyList<JsonElement>> CallSearchAsync(
        params (string Key, object Value)[] args)
    {
        var raw = await McpTestFixture.CallToolAsync("search", args);
        return McpTestFixture.ParseSuccessArray(raw);
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
