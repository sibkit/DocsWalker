namespace DocsWalker.Core.Api;

/// <summary>
/// Машинно-читаемая meta-schema, отдаваемая на <c>select: "meta"</c>
/// (per api/read.md, раздел «Форма-строка: kernel-режимы»). Описывает
/// контракт data/event-узлов, link-identity, разрешённые направления,
/// набор селекторов, tx-операций и kernel-режимов. LLM использует её,
/// чтобы строить запросы без чтения markdown-спецификации.
///
/// Объект kernel-owned, версионируется с релизом DocsWalker, в hist не
/// фиксируется.
/// </summary>
internal static class MetaSchema
{
    public static IReadOnlyDictionary<string, object?> Build() => Cached;

    private static readonly IReadOnlyDictionary<string, object?> Cached = BuildOnce();

    private static IReadOnlyDictionary<string, object?> BuildOnce() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["version"] = "2",
        ["scopes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["main"]   = new Dictionary<string, object?>(StringComparer.Ordinal) { ["editable"] = true,  ["default"] = true,  ["temporal"] = true  },
            ["usage"]  = new Dictionary<string, object?>(StringComparer.Ordinal) { ["editable"] = true,  ["temporal"] = true  },
            ["scheme"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["editable"] = true,  ["temporal"] = true, ["breaking_check"] = true },
            ["hist"]   = new Dictionary<string, object?>(StringComparer.Ordinal) { ["editable"] = false, ["append_only"] = true },
        },
        ["node_classes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scopes"] = new object?[] { "main", "usage", "scheme" },
                ["fields"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"]            = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string", ["compact"] = true,  ["note"] = "opaque hex lower-case" },
                    ["path"]          = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string", ["compact"] = true },
                    ["title"]         = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string", ["compact"] = true,  ["regex"] = "^[\\p{L}\\p{Nd}._-]+$" },
                    ["map_bindings"]  = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "object", ["compact"] = true },
                    ["scope"]         = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string", ["compact"] = true,  ["note"] = "serialized only for non-main nodes" },
                    ["tokens"]        = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "integer", ["compact"] = true, ["note"] = "estimate of full-form cost" },
                    ["version"]       = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "integer", ["compact"] = true, ["note"] = "absent on at-reads" },
                    ["content"]       = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string", ["loadable"] = true },
                    ["links"]         = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "array",  ["loadable"] = true },
                },
            },
            ["event"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scopes"] = new object?[] { "hist" },
                ["fields"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"]           = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string",  ["compact"] = true,  ["note"] = "doubles as tx_id" },
                    ["title"]        = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string",  ["compact"] = true,  ["max_tokens"] = 100 },
                    ["date"]         = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string",  ["compact"] = true,  ["note"] = "ISO-8601 UTC" },
                    ["rollback_of"]  = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string",  ["compact"] = true,  ["note"] = "present on kernel-generated rollback tx" },
                    ["counts"]       = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "object",  ["compact"] = true },
                    ["tokens"]       = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "integer", ["compact"] = true },
                    ["description"]  = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "string",  ["loadable"] = true },
                    ["created"]      = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "object",  ["loadable"] = true },
                    ["changed"]      = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "object",  ["loadable"] = true },
                    ["deleted"]      = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "object",  ["loadable"] = true },
                },
            },
        },
        ["link"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["identity"] = new object?[] { "name", "from.id", "to.id" },
            ["allowed_directions"] = new object?[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = "main",  ["to"] = "main"  },
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = "usage", ["to"] = "usage" },
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = "usage", ["to"] = "main"  },
            },
        },
        ["data_selectors"] = new object?[]
        {
            "id", "path", "title", "map_bindings", "links", "match",
        },
        ["hist_selectors"] = new object?[]
        {
            "id", "title", "date", "description", "rollback_of", "tx_scope", "touches_node", "touches_link",
        },
        ["tx_ops"] = new object?[]
        {
            "create", "update", "move", "delete", "link", "unlink", "rollback",
        },
        ["read_ops"] = new object?[]
        {
            "select",
        },
        ["kernel_modes"] = new object?[]
        {
            "meta",
        },
        ["at_forms"] = new object?[]
        {
            "<tx_id>",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["before"] = "<tx_id>" },
        },
    };
}
