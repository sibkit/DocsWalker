using System.Text.Json;

namespace DocsWalker.Tests;

/// <summary>
/// Точечное покрытие MCP-facing read API: get-overview остаётся рабочей командой,
/// старые tree/map tools убраны из MCP surface.
/// </summary>
[Collection("ConsoleRedirect")]
public class ApiV2MiscTests
{
    [Fact]
    public async Task GetTree_RemovedFromMcpSurface_ReturnsUnknownTool()
    {
        var raw = await McpTestFixture.CallToolAsync("get-tree", ("id", 0));
        Assert.Contains("unknown tool: get-tree", raw);
    }

    [Fact]
    public async Task GetSubtree_RemovedFromRegistry_ReturnsUnknownTool()
    {
        // Старое имя должно быть удалено из MCP-tools registry.
        var raw = await McpTestFixture.CallToolAsync("get-subtree", ("id", 0));
        Assert.Contains("unknown tool", raw);
    }

    [Fact]
    public async Task GetMap_RemovedFromRegistry_ReturnsUnknownTool()
    {
        var raw = await McpTestFixture.CallToolAsync("get-map");
        Assert.Contains("unknown tool", raw);
    }

    [Fact]
    public async Task GetOverview_ReturnsExpectedFields()
    {
        var raw = await McpTestFixture.CallToolAsync("get-overview");
        var obj = McpTestFixture.ParseSuccessObject(raw);
        Assert.True(obj.TryGetProperty("total_nodes", out var total));
        Assert.True(total.GetInt32() > 0);
        Assert.True(obj.TryGetProperty("max_depth", out _));
        Assert.True(obj.TryGetProperty("total_tokens", out _));
        Assert.True(obj.TryGetProperty("trees", out var trees));
        Assert.Equal(JsonValueKind.Array, trees.ValueKind);
        Assert.True(obj.TryGetProperty("schema", out var schema));
        Assert.True(schema.TryGetProperty("types_count", out _));
        Assert.True(obj.TryGetProperty("root_children", out _));
        Assert.True(obj.TryGetProperty("hot_spots", out var hot));
        Assert.True(hot.TryGetProperty("largest_nodes", out _));
        Assert.True(hot.TryGetProperty("most_connected_nodes", out _));
    }
}
