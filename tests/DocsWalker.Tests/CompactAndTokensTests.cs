using System.Text.Json;

namespace DocsWalker.Tests;

/// <summary>
/// Покрытие compact-флага и truncation-протокола (см. docs/DocsWalker.yml/#406)
/// для get-tree и get-nodes через kernel JSON-RPC.
/// </summary>
[Collection("ConsoleRedirect")]
public class CompactAndTokensTests
{
    [Fact]
    public async Task GetTree_Compact_DropsTextAndOutRefs()
    {
        var raw = await McpTestFixture.CallToolAsync(
            "get-tree",
            ("id", 0),
            ("depth", 1),
            ("compact", true));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        var root = obj.GetProperty("root");
        // Root в compact-форме: только id, type, title (+ children).
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("out_refs", out _));
        Assert.False(root.TryGetProperty("tokens", out _));
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task GetTree_LowMaxTokens_TriggersTruncationProtocol()
    {
        // С низким бюджетом BFS не дочитает дерево. Должны вернуться поля
        // truncated/stopped_at/tokens_used/tokens_budget (правило #406).
        var raw = await McpTestFixture.CallToolAsync(
            "get-tree",
            ("id", 0),
            ("max_tokens", 200));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.True(obj.TryGetProperty("truncated", out var truncated));
        Assert.True(truncated.GetBoolean());
        Assert.True(obj.TryGetProperty("stopped_at", out var stoppedAt));
        Assert.True(stoppedAt.ValueKind == JsonValueKind.Array);
        Assert.True(stoppedAt.GetArrayLength() > 0);
        var first = stoppedAt[0];
        Assert.True(first.TryGetProperty("parent_id", out _));
        Assert.True(first.TryGetProperty("remaining_children", out _));
        Assert.True(first.TryGetProperty("next_offset", out _));
        Assert.True(obj.TryGetProperty("tokens_used", out var used));
        Assert.True(used.GetInt32() <= 200);
        Assert.True(obj.TryGetProperty("tokens_budget", out var budget));
        Assert.Equal(200, budget.GetInt32());
    }

    [Fact]
    public async Task GetTree_DefaultMaxTokens_NoTruncationOnSmallSubtree()
    {
        // depth=0 — только корень. С default бюджетом 50000 не должно быть truncation.
        var raw = await McpTestFixture.CallToolAsync(
            "get-tree",
            ("id", 0),
            ("depth", 0));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.False(obj.TryGetProperty("truncated", out _),
            "При непереполненном бюджете поле truncated должно быть опущено (правило #301).");
    }

    [Fact]
    public async Task GetByPath_CompactDepth_DropsTextAndOutRefs()
    {
        var raw = await McpTestFixture.CallToolAsync(
            "get-by-path",
            ("path", "DocsWalker"),
            ("tree", "path"),
            ("depth", 1),
            ("compact", true));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.Equal("path", obj.GetProperty("tree").GetString());

        var root = obj.GetProperty("root");
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("out_refs", out _));
        Assert.True(root.TryGetProperty("children", out var children));
        Assert.True(children.GetArrayLength() > 0);
        foreach (var child in children.EnumerateArray())
            Assert.False(child.TryGetProperty("children", out _));
    }

    [Fact]
    public async Task GetByPath_LowMaxTokens_TriggersTruncationProtocol()
    {
        var raw = await McpTestFixture.CallToolAsync(
            "get-by-path",
            ("path", "DocsWalker"),
            ("tree", "path"),
            ("max_tokens", 200));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.True(obj.GetProperty("truncated").GetBoolean());
        Assert.True(obj.GetProperty("stopped_at").GetArrayLength() > 0);
        Assert.True(obj.GetProperty("tokens_used").GetInt32() <= 200);
        Assert.Equal(200, obj.GetProperty("tokens_budget").GetInt32());
    }

    [Fact]
    public async Task GetNodes_Compact_DropsTextAndOutRefs()
    {
        var raw = await McpTestFixture.CallToolAsync(
            "get-nodes",
            ("ids", "1,8"),
            ("compact", true));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        var nodes = obj.GetProperty("nodes");
        Assert.True(nodes.GetArrayLength() > 0);
        foreach (var n in nodes.EnumerateArray())
        {
            Assert.False(n.TryGetProperty("text", out _));
            Assert.False(n.TryGetProperty("out_refs", out _));
            Assert.True(n.TryGetProperty("id", out _));
            Assert.True(n.TryGetProperty("type", out _));
        }
    }

    [Fact]
    public async Task GetNodes_LowMaxTokens_TruncatesWithFlatStoppedAt()
    {
        var raw = await McpTestFixture.CallToolAsync(
            "get-nodes",
            ("ids", "1,17,8"),
            ("max_tokens", 30));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.True(obj.TryGetProperty("truncated", out var truncated));
        Assert.True(truncated.GetBoolean());
        Assert.True(obj.TryGetProperty("stopped_at", out var stoppedAt));
        var first = stoppedAt[0];
        // Для get-nodes parent_id=0 — синтетический маркер плоского списка.
        Assert.Equal(0, first.GetProperty("parent_id").GetInt32());
        Assert.True(first.GetProperty("remaining_children").GetInt32() > 0);
    }

    [Fact]
    public async Task GetNodes_HasNodesField_Always()
    {
        // Шейп ответа всегда объект {nodes: [...]}.
        var raw = await McpTestFixture.CallToolAsync("get-nodes", ("ids", "1"));
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.True(obj.TryGetProperty("nodes", out var nodes));
        Assert.Equal(JsonValueKind.Array, nodes.ValueKind);
    }
}
