using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocsWalker.Core.Mcp;

/// <summary>
/// Source-gen JsonSerializerContext для всех типов MCP-канала. Native-AOT-совместим:
/// рефлексия не используется. JsonElement и JsonObject — встроенные типы STJ,
/// source-gen их обрабатывает напрямую.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ListToolsResult))]
[JsonSerializable(typeof(CallToolParams))]
[JsonSerializable(typeof(CallToolResult))]
[JsonSerializable(typeof(McpTool))]
[JsonSerializable(typeof(McpContentItem))]
[JsonSerializable(typeof(McpServerInfo))]
[JsonSerializable(typeof(McpServerCapabilities))]
[JsonSerializable(typeof(McpToolsCapability))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
public partial class McpJsonContext : JsonSerializerContext
{
}
