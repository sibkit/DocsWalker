namespace DocsWalker.Cli.Cli;

internal enum ParamType
{
    String,
    Integer,
    IdList,
    Json,
}

internal sealed record CommandParam(string KebabName, ParamType Type, bool Required);

internal sealed record CommandSpec(string SnakeName, string KebabName, IReadOnlyList<CommandParam> Params);

internal static class Commands
{
    public static IReadOnlyList<CommandSpec> All { get; } = Build();
    public static IReadOnlyDictionary<string, CommandSpec> ByKebab { get; } =
        All.ToDictionary(c => c.KebabName, StringComparer.Ordinal);

    private static List<CommandSpec> Build()
    {
        return new List<CommandSpec>
        {
            // Чтение
            Cmd("list_documents"),
            Cmd("get_meta_schema"),
            Cmd("get_schema"),
            Cmd("get_map"),
            Cmd("get_nodes",
                Req("ids", ParamType.IdList)),
            Cmd("get_by_path",
                Req("path", ParamType.String)),
            Cmd("get_refs",
                Req("id", ParamType.Integer),
                Opt("type", ParamType.String),
                Opt("origin", ParamType.String)),
            Cmd("get_in_refs",
                Req("id", ParamType.Integer),
                Opt("type", ParamType.String),
                Opt("origin", ParamType.String)),
            Cmd("search",
                Req("query", ParamType.String)),
            Cmd("check_integrity"),

            // Запись
            Cmd("create_node",
                Req("parent_id", ParamType.Integer),
                Req("type",      ParamType.String),
                Opt("title",     ParamType.String),
                Opt("name",      ParamType.String),
                Opt("body",      ParamType.Json)),
            Cmd("update_node",
                Req("id",    ParamType.Integer),
                Req("patch", ParamType.Json)),
            Cmd("delete_node",
                Req("id", ParamType.Integer)),
            Cmd("move_node",
                Req("id",             ParamType.Integer),
                Req("new_parent_id",  ParamType.Integer),
                Opt("new_block_name", ParamType.String)),
            Cmd("create_ref",
                Req("from_id", ParamType.Integer),
                Req("type",    ParamType.String),
                Req("to_id",   ParamType.Integer)),
            Cmd("delete_ref",
                Req("from_id", ParamType.Integer),
                Req("type",    ParamType.String),
                Req("to_id",   ParamType.Integer)),
            Cmd("add_ref_type",
                Req("name",        ParamType.String),
                Req("direction",   ParamType.String),
                Req("description", ParamType.String)),
            Cmd("transaction",
                Req("operations", ParamType.Json)),
        };
    }

    private static CommandSpec Cmd(string snakeName, params CommandParam[] parameters) =>
        new(snakeName, snakeName.Replace('_', '-'), parameters);

    private static CommandParam Req(string snakeName, ParamType type) =>
        new(snakeName.Replace('_', '-'), type, Required: true);

    private static CommandParam Opt(string snakeName, ParamType type) =>
        new(snakeName.Replace('_', '-'), type, Required: false);
}
