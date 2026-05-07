namespace DocsWalker.Cli.Cli;

internal enum ParamType
{
    String,
    Integer,
    IdList,
    Json,
}

internal enum CommandKind
{
    /// <summary>Read-операция: без побочного эффекта, без applied=, не принимает --dry-run.</summary>
    Read,

    /// <summary>Write-операция: меняет docs/ (или валидируется dry-run'ом), принимает --dry-run.</summary>
    Write,
}

/// <summary>
/// Описание одного параметра CLI-команды. <see cref="Description"/> — короткая
/// фраза для usage-guide; null означает «без отдельного описания» (имя+тип+required
/// уже несут смысл).
/// </summary>
internal sealed record CommandParam(
    string KebabName,
    ParamType Type,
    bool Required,
    string? Description = null);

/// <summary>
/// Спецификация CLI-команды.
///
/// <para><c>DynamicParams=true</c> означает: набор параметров командой не
/// зафиксирован полностью — фиксированные параметры всё равно валидируются
/// (тип значения, наличие у required), но любые сверх них не отвергаются
/// как "unknown_parameter". Используется для <c>create-node</c>, у которого
/// имена out_refs-параметров берутся из контракта типа в Схеме (см.
/// docs/DocsWalker.yml/#159).</para>
///
/// <para><see cref="Description"/> и <see cref="Examples"/> используются
/// командой <c>get-usage-guide</c> для генерации manifest для LLM. Допускают
/// быть пустыми/null, тогда команда попадёт в manifest без описания/примеров.</para>
/// </summary>
internal sealed record CommandSpec(
    string SnakeName,
    string KebabName,
    IReadOnlyList<CommandParam> Params,
    CommandKind Kind = CommandKind.Read,
    bool DynamicParams = false,
    string? Description = null,
    IReadOnlyList<string>? Examples = null);

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
            Read("get_meta_schema",
                desc: "Полный текст мета-схемы (форма schema-файла); токенов много, нужно редко.",
                examples: new[] { "docswalker get-meta-schema" }),
            Read("get_schema",
                desc: "Полная Схема проекта: все типы, их out_refs, declared trees. Если нужен один тип — describe-type экономит токены.",
                examples: new[] { "docswalker get-schema" }),
            Read("describe_type",
                desc: "Описание одного типа в FS-агностичной форме: text_required и список out_refs (с tree/cardinality/required, target_types). Не выдаёт title_source.",
                examples: new[] { "docswalker describe-type --name=section", "docswalker describe-type --name=rule" },
                Req("name", ParamType.String, "Имя типа из Схемы.")),
            Read("get_map",
                desc: "Карта всех узлов docs/ — оглавление в форме path-дерева (id, type, title; без text и связей).",
                examples: new[] { "docswalker get-map" }),
            Read("get_nodes",
                desc: "Полные узлы по списку id. Возвращает 5 полей каждого узла: id, type, title, text, out_refs.",
                examples: new[] { "docswalker get-nodes --ids=1,8,42" },
                Req("ids", ParamType.IdList, "Один id или список id через запятую.")),
            Read("get_by_path",
                desc: "Полное поддерево узла по человекочитаемому пути 'Документ/Раздел/...'.",
                examples: new[] { "docswalker get-by-path --path=\"DocsWalker/Операции чтения\"" },
                Req("path", ParamType.String, "Путь, разделитель '/'.")),
            Read("get_subtree",
                desc: "Поддерево узла в указанном tree-scope. По умолчанию tree=path, depth — без ограничения, fields — все поля включая токены. Каждый узел несёт tokens (только сам узел) и subtree_tokens (узел + потомки в результате) — для бюджет-планирования.",
                examples: new[]
                {
                    "docswalker get-subtree --id=0",
                    "docswalker get-subtree --id=42 --depth=2",
                    "docswalker get-subtree --id=0 --fields=id,type,title,tokens",
                    "docswalker get-subtree --id=PROJECT --tree=strategic",
                },
                Req("id",     ParamType.Integer, "id корня поддерева."),
                Opt("tree",   ParamType.String,  "Имя дерева (tree-scope). По умолчанию 'path'."),
                Opt("depth",  ParamType.Integer, "Максимальная глубина обхода: 0 — только корень, 1 — корень + один уровень. Без параметра — без ограничения."),
                Opt("fields", ParamType.String,  "Whitelist полей через запятую: id,type,title,text,out_refs,tokens,subtree_tokens. Без параметра — все поля. Поле id присутствует всегда.")),
            Read("get_ancestors",
                desc: "Цепочка родителей в указанном tree-scope (от ближайшего к корню дерева).",
                examples: new[] { "docswalker get-ancestors --id=42", "docswalker get-ancestors --id=42 --tree=strategic" },
                Req("id",   ParamType.Integer, "id узла."),
                Opt("tree", ParamType.String,  "Имя дерева. По умолчанию 'path'.")),
            Read("get_refs",
                desc: "Все связи узла в обе стороны (in[], out[]). Опциональный фильтр по имени связи.",
                examples: new[] { "docswalker get-refs --id=42", "docswalker get-refs --id=42 --name=examples" },
                Req("id",   ParamType.Integer, "id узла."),
                Opt("name", ParamType.String,  "Имя связи; без параметра — все связи.")),
            Read("get_in_refs",
                desc: "Только входящие связи на узел (in[]).",
                examples: new[] { "docswalker get-in-refs --id=42" },
                Req("id",   ParamType.Integer, "id узла."),
                Opt("name", ParamType.String,  "Имя связи; без параметра — все.")),
            Read("search",
                desc: "Полнотекстовый поиск (case-insensitive substring) по text узлов.",
                examples: new[] { "docswalker search --query=валидатор" },
                Req("query", ParamType.String, "Подстрока поиска.")),
            Read("check_integrity",
                desc: "Полный прогон валидатора на текущем docs/ без записи. Возвращает {ok, errors[]}; exit code всегда 0.",
                examples: new[] { "docswalker check-integrity" }),
            Read("get_usage_guide",
                desc: "Manifest всех команд + ментальная модель + перечень tree-scope'ов + слепок графа. Зови в начале сессии.",
                examples: new[] { "docswalker get-usage-guide" }),

            // Запись
            DynamicWrite("create_node",
                desc: "Создать узел: type+title+text плюс значения всех required-связей контракта типа (--<имя_связи>=<id|csv>). Имя обязательного path-параметра — --path.",
                examples: new[] { "docswalker create-node --type=section --title=Новая --path=1", "docswalker create-node --type=rule --title=foo --text=bar --path=42 --examples=99" },
                Req("type",  ParamType.String, "Имя типа из Схемы."),
                Req("title", ParamType.String, "Path-сегмент (1–2 слова)."),
                Opt("text",  ParamType.String, "Текст; обязателен для типов с text_required=true.")),
            Write("update_node",
                desc: "Сменить title и/или text узла. Связи этой командой не меняются — для них create-ref/delete-ref/move-node.",
                examples: new[] { "docswalker update-node --id=42 --title=Новое", "docswalker update-node --id=42 --text='Новый текст'" },
                Req("id",    ParamType.Integer, "id узла."),
                Opt("title", ParamType.String,  "Новый title."),
                Opt("text",  ParamType.String,  "Новый text.")),
            Write("delete_nodes",
                desc: "Удалить узлы явным списком --ids=. Авто-каскада нет: набор собирается LLM (get-subtree + path-children каждого).",
                examples: new[] { "docswalker delete-nodes --ids=42,43" },
                Req("ids", ParamType.IdList, "Список id через запятую.")),
            Write("move_node",
                desc: "Переподшить узел в указанном tree-scope (по умолчанию path → реструктуризация хранилища).",
                examples: new[] { "docswalker move-node --id=42 --to=8", "docswalker move-node --id=SUBTASK --to=NEW_TASK --tree=strategic" },
                Req("id",   ParamType.Integer, "id переносимого узла."),
                Req("to",   ParamType.Integer, "id нового parent в указанном дереве."),
                Opt("tree", ParamType.String,  "Имя дерева. По умолчанию 'path'.")),
            Write("create_ref",
                desc: "Создать одну исходящую связь from→to с именем name. Tree-refs (path и др.) этой командой не редактируются — для них move-node.",
                examples: new[] { "docswalker create-ref --from-id=42 --name=related --to-id=8" },
                Req("from_id", ParamType.Integer, "id узла-источника."),
                Req("name",    ParamType.String,  "Имя связи (объявленной в out_refs типа источника)."),
                Req("to_id",   ParamType.Integer, "id узла-цели.")),
            Write("delete_ref",
                desc: "Удалить одну исходящую связь from→to с именем name. Запрещено для required-связей, если это последняя цель.",
                examples: new[] { "docswalker delete-ref --from-id=42 --name=related --to-id=8" },
                Req("from_id", ParamType.Integer, "id узла-источника."),
                Req("name",    ParamType.String,  "Имя связи."),
                Req("to_id",   ParamType.Integer, "id узла-цели.")),
            // redirect-refs принимает либо --from=<id>, либо --from-subtree=<root_id>
            // (взаимоисключающие; ровно один обязателен — handler разбирается).
            // Действие — либо --to=<dst_id>, либо --unlink (тоже взаимоисключающие).
            // --name=<ref-name> опционально фильтрует переподшивку по имени связи.
            Write("redirect_refs",
                desc: "Массовая переподшивка входящих cross-refs: с одного узла (или его path-поддерева) на другой узел, либо разрыв (--unlink). Tree-refs не трогает.",
                examples: new[] { "docswalker redirect-refs --from=42 --to=8", "docswalker redirect-refs --from-subtree=42 --to=8", "docswalker redirect-refs --from=42 --unlink" },
                Opt("from",         ParamType.Integer, "id узла-источника входящих cross-refs."),
                Opt("from_subtree", ParamType.Integer, "id корня path-поддерева; альтернатива --from."),
                Opt("to",           ParamType.Integer, "id узла-приёмника; альтернатива --unlink."),
                Opt("name",         ParamType.String,  "Фильтр по имени связи."),
                Opt("unlink",       ParamType.String,  "true → разрыв вместо переноса.")),
            Write("transaction",
                desc: "Атомарная пачка write-операций. Применяется целиком; результат — массив элементов {op, ...поля}.",
                examples: new[] { "docswalker transaction --operations='[{\"op\":\"create-node\",...},{\"op\":\"create-ref\",...}]'" },
                Req("operations", ParamType.Json, "JSON-массив операций (см. формат в TransactionParser).")),
        };
    }

    private static CommandSpec Read(
        string snakeName,
        string desc,
        IReadOnlyList<string>? examples = null,
        params CommandParam[] parameters) =>
        new(snakeName, snakeName.Replace('_', '-'), parameters,
            Kind: CommandKind.Read, DynamicParams: false,
            Description: desc, Examples: examples ?? Array.Empty<string>());

    private static CommandSpec Write(
        string snakeName,
        string desc,
        IReadOnlyList<string>? examples = null,
        params CommandParam[] parameters) =>
        new(snakeName, snakeName.Replace('_', '-'), parameters,
            Kind: CommandKind.Write, DynamicParams: false,
            Description: desc, Examples: examples ?? Array.Empty<string>());

    private static CommandSpec DynamicWrite(
        string snakeName,
        string desc,
        IReadOnlyList<string>? examples = null,
        params CommandParam[] parameters) =>
        new(snakeName, snakeName.Replace('_', '-'), parameters,
            Kind: CommandKind.Write, DynamicParams: true,
            Description: desc, Examples: examples ?? Array.Empty<string>());

    private static CommandParam Req(string snakeName, ParamType type, string? description = null) =>
        new(snakeName.Replace('_', '-'), type, Required: true, Description: description);

    private static CommandParam Opt(string snakeName, ParamType type, string? description = null) =>
        new(snakeName.Replace('_', '-'), type, Required: false, Description: description);
}
