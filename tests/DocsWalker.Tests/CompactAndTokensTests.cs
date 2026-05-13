using System.Text.Json;
using DocsWalker.Core.Mcp;

namespace DocsWalker.Tests;

[Collection("ConsoleRedirect")]
public class CompactAndTokensTests
{
    [Fact]
    public async Task GetNodes_RemovedFromMcpSurface_ReturnsUnknownTool()
    {
        var raw = await McpTestFixture.CallToolAsync("get-nodes", ("ids", "1"));

        using var doc = JsonDocument.Parse(raw);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.Equal("unknown tool: get-nodes", error.GetProperty("message").GetString());
    }
}
