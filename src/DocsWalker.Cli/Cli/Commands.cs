namespace DocsWalker.Cli.Cli;

internal enum ParamType
{
    String,
    Integer,
    IdList,
    Json,
}

internal sealed record CommandParam(string KebabName, ParamType Type, bool Required);

/// <summary>
/// Спецификация CLI-команды.
///
/// <para><c>DynamicParams=true</c> означает: набор параметров командой не
/// зафиксирован полностью — фиксированные параметры всё равно валидируются
/// (тип значения, наличие у required), но любые сверх них не отвергаются
/// как "unknown_parameter". Используется для <c>create-node</c>, у которого
/// имена out_refs-параметров берутся из контракта типа в Схеме (см.
/// docs/DocsWalker.yml/#159).</para>
/// </summary>
internal sealed record CommandSpec(
    string SnakeName,
    string KebabName,
    IReadOnlyList<CommandParam> Params,
    bool DynamicParams = false);

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
            Cmd("get_meta_schema"),
            Cmd("get_schema"),
            Cmd("get_map"),
            Cmd("get_nodes",
                Req("ids", ParamType.IdList)),
            Cmd("get_by_path",
                Req("path", ParamType.String)),
            Cmd("get_subtree",
                Req("id",   ParamType.Integer),
                Opt("tree", ParamType.String)),
            Cmd("get_ancestors",
                Req("id",   ParamType.Integer),
                Opt("tree", ParamType.String)),
            Cmd("get_refs",
                Req("id",   ParamType.Integer),
                Opt("name", ParamType.String)),
            Cmd("get_in_refs",
                Req("id",   ParamType.Integer),
                Opt("name", ParamType.String)),
            Cmd("search",
                Req("query", ParamType.String)),
            Cmd("check_integrity"),

            // Запись
            DynamicCmd("create_node",
                Req("type",  ParamType.String),
                Req("title", ParamType.String),
                Opt("text",  ParamType.String)),
            Cmd("update_node",
                Req("id",    ParamType.Integer),
                Opt("title", ParamType.String),
                Opt("text",  ParamType.String)),
            Cmd("delete_nodes",
                Req("ids", ParamType.IdList)),
            Cmd("move_node",
                Req("id",   ParamType.Integer),
                Req("to",   ParamType.Integer),
                Opt("tree", ParamType.String)),
            Cmd("create_ref",
                Req("from_id", ParamType.Integer),
                Req("name",    ParamType.String),
                Req("to_id",   ParamType.Integer)),
            Cmd("delete_ref",
                Req("from_id", ParamType.Integer),
                Req("name",    ParamType.String),
                Req("to_id",   ParamType.Integer)),
            // redirect-refs принимает либо --from=<id>, либо --from-subtree=<root_id>
            // (взаимоисключающие; ровно один обязателен — handler разбирается).
            // Действие — либо --to=<dst_id>, либо --unlink (тоже взаимоисключающие).
            // --name=<ref-name> опционально фильтрует переподшивку по имени связи.
            Cmd("redirect_refs",
                Opt("from",         ParamType.Integer),
                Opt("from_subtree", ParamType.Integer),
                Opt("to",           ParamType.Integer),
                Opt("name",         ParamType.String),
                Opt("unlink",       ParamType.String)),
            Cmd("transaction",
                Req("operations", ParamType.Json)),
        };
    }

    private static CommandSpec Cmd(string snakeName, params CommandParam[] parameters) =>
        new(snakeName, snakeName.Replace('_', '-'), parameters);

    private static CommandSpec DynamicCmd(string snakeName, params CommandParam[] parameters) =>
        new(snakeName, snakeName.Replace('_', '-'), parameters, DynamicParams: true);

    private static CommandParam Req(string snakeName, ParamType type) =>
        new(snakeName.Replace('_', '-'), type, Required: true);

    private static CommandParam Opt(string snakeName, ParamType type) =>
        new(snakeName.Replace('_', '-'), type, Required: false);
}
