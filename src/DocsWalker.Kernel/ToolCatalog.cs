using System.Text.Json;

namespace DocsWalker.Kernel;

/// <summary>
/// Список MCP-tools, отдаваемый kernel-ом через <c>tools/list</c>: два
/// инструмента <c>read</c> и <c>tx</c> (per api/surface.md). Описание
/// каждого — компактный quickstart-абзац; подробности LLM подгружает
/// через <c>read scope=usage</c>.
///
/// <para>
/// <c>inputSchema</c> возвращается как короткий JSON-Schema object с
/// одним полем <c>ops</c> типа array. Полная схема (со всеми вариантами
/// селекторов, ops, scope) не выводится в tools/list — она живёт в
/// api/*.md и в meta-schema (через <c>read scope=usage</c>).
/// </para>
/// </summary>
internal static class ToolCatalog
{
    private const string ReadDescription =
        "Read graph nodes by predicate-selectors. Read-only, all scopes (main/usage/scheme/hist). " +
        "Args: { scope?, defaults?, at?, ops: [{ select: { selector, include?, max_tokens?, as? } | \"meta\" }] }. " +
        "Use 'read scope=usage' to load full documentation, 'read scope=scheme' for editable schema.";

    private const string TxDescription =
        "Atomic transaction over editable scope (main/usage/scheme). " +
        "Args: { scope?, title, description?, defaults?, ops: [{create|update|move|delete|link|unlink|rollback}] }. " +
        "Title is required (≤100 tokens), describes the change. Failure of any op rolls back the entire tx.";

    public static IReadOnlyList<McpTool> BuildMcpTools()
    {
        return
        [
            new McpTool("read", ReadDescription, BuildOpsSchema()),
            new McpTool("tx", TxDescription, BuildOpsSchema(requireTitle: true)),
        ];
    }

    private static JsonElement BuildOpsSchema(bool requireTitle = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append("\"type\":\"object\",");
        sb.Append("\"properties\":{");
        sb.Append("\"scope\":{\"type\":\"string\",\"enum\":[\"usage\",\"scheme\"");
        if (!requireTitle) sb.Append(",\"hist\"");
        sb.Append("]},");
        if (requireTitle)
        {
            sb.Append("\"title\":{\"type\":\"string\"},");
            sb.Append("\"description\":{\"type\":\"string\"},");
        }
        else
        {
            sb.Append("\"at\":{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"object\"}]},");
        }
        sb.Append("\"defaults\":{\"type\":\"object\"},");
        sb.Append("\"ops\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}");
        sb.Append("},\"required\":[\"ops\"");
        if (requireTitle) sb.Append(",\"title\"");
        sb.Append("]}");
        using var doc = JsonDocument.Parse(sb.ToString());
        return doc.RootElement.Clone();
    }
}
