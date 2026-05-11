namespace DocsWalker.Cli.Cli;

internal enum ParamType
{
    String,
    Integer,
    IdList,
    /// <summary>
    /// JSON-объект, передаётся CLI как raw JSON-текст (со скобками <c>{...}</c>).
    /// В MCP-схеме маппится в <c>type=object</c>.
    /// </summary>
    Json,
    /// <summary>
    /// JSON-массив объектов, передаётся CLI как raw JSON-текст (со скобками <c>[{...},...]</c>).
    /// В MCP-схеме маппится в <c>type=array, items.type=object</c>. Используется для
    /// transaction.operations: контракт явно требует массив операций (см.
    /// docs/DocsWalker.yml/«(#34) transaction»), и MCP-клиент должен иметь возможность
    /// прислать array через arguments напрямую без escape-string-обхода.
    /// </summary>
    JsonArray,
    /// <summary>
    /// Булев флаг. В CLI передаётся как <c>true</c>/<c>false</c> (case-insensitive)
    /// либо <c>1</c>/<c>0</c>; в MCP-схеме — <c>type=boolean</c>. McpArgvBuilder
    /// автоматически конвертирует JSON-boolean в строку.
    /// </summary>
    Boolean,
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
            Read("get_nodes",
                desc: "Полные узлы по списку id. Возвращает объект {nodes:[...], truncated?, stopped_at?, tokens_used?, tokens_budget?}. Truncation-протокол (#406): включаются узлы по порядку, пока влезает max_tokens; default 50000.",
                examples: new[]
                {
                    "docswalker get-nodes --ids=1,8,42",
                    "docswalker get-nodes --ids=1,8,42 --compact=true",
                    "docswalker get-nodes --ids=1,8,42 --max-tokens=500",
                },
                Req("ids",        ParamType.IdList,  "Один id или список id через запятую."),
                Opt("fields",     ParamType.String,  "Whitelist полей через запятую: id,type,title,text,out_refs,tokens,subtree_tokens. Без параметра — все поля. id всегда."),
                Opt("compact",    ParamType.Boolean, "true → alias для fields=id,type,title; default false. Явные fields имеют приоритет."),
                Opt("max_tokens", ParamType.Integer, "Бюджет токенов на ответ; default 50000. См. truncation-протокол #406.")),
            Read("get_by_path",
                desc: "Полное поддерево узла по человекочитаемому пути 'Документ/Раздел/...' в указанном addressable дереве. По умолчанию tree берётся из schema.default_addressable_tree, либо автоматически если в Схеме ровно один addressable tree.",
                examples: new[]
                {
                    "docswalker get-by-path --path=\"DocsWalker/Операции чтения\"",
                    "docswalker get-by-path --path=\"DocsWalker/Операции чтения\" --tree=path",
                },
                Req("path", ParamType.String, "Путь, разделитель '/'."),
                Opt("tree", ParamType.String, "Имя addressable дерева. По умолчанию — default_addressable_tree из Схемы либо единственный addressable tree.")),
            Read("get_tree",
                desc: "Поддерево узла в указанном tree-scope с бюджетом токенов. По умолчанию tree=path, depth — без ограничения, fields — все поля, max_tokens=50000. Каждый узел несёт tokens / subtree_tokens. При превышении max_tokens — BFS-усечение; ответ дополняется полями truncated/stopped_at/tokens_used/tokens_budget (правило #301, #406).",
                examples: new[]
                {
                    "docswalker get-tree --id=0",
                    "docswalker get-tree --id=42 --depth=2",
                    "docswalker get-tree --id=0 --fields=id,type,title,tokens",
                    "docswalker get-tree --id=PROJECT --tree=strategic",
                    "docswalker get-tree --id=0 --compact=true --max-tokens=200",
                },
                Req("id",         ParamType.Integer, "id корня поддерева."),
                Opt("tree",       ParamType.String,  "Имя дерева (tree-scope). По умолчанию 'path'."),
                Opt("depth",      ParamType.Integer, "Максимальная глубина обхода: 0 — только корень, 1 — корень + один уровень. Без параметра — без ограничения."),
                Opt("fields",     ParamType.String,  "Whitelist полей через запятую: id,type,title,text,out_refs,tokens,subtree_tokens. Без параметра — все поля. Поле id присутствует всегда."),
                Opt("compact",    ParamType.Boolean, "true → alias для fields=id,type,title; default false. Явные fields имеют приоритет."),
                Opt("max_tokens", ParamType.Integer, "Бюджет токенов на ответ; default 50000. См. truncation-протокол #406.")),
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
                desc: "Полнотекстовый поиск с BM25-ранжированием по title и text узлов. Title-hit получает boost ×3 в режиме in=both. Сортировка: score desc, id asc. Снимок одного hit'а — {id, type, title, score, snippet}.",
                examples: new[]
                {
                    "docswalker search --query=валидатор",
                    "docswalker search --query=get-tree --in=title",
                    "docswalker search --query=^stg --regex=true --type=definition",
                    "docswalker search --query=поиск --tree=path --under=17 --limit=10",
                },
                Req("query",   ParamType.String,  "Substring (или regex, если --regex=true). Не пустой."),
                Opt("in",      ParamType.String,  "Где искать: title|text|both. По умолчанию both."),
                Opt("type",    ParamType.String,  "Фильтр по типу узла (TypeName из Схемы)."),
                Opt("tree",    ParamType.String,  "Tree-scope для --under. По умолчанию path."),
                Opt("under",   ParamType.Integer, "id узла; искать только в его поддереве в указанном tree."),
                Opt("regex",   ParamType.Boolean, "true → query как .NET-regex (без BM25, сортировка по id asc). По умолчанию false."),
                Opt("limit",   ParamType.Integer, "Максимум hit'ов; default 20."),
                Opt("compact", ParamType.Boolean, "true → alias для whitelist полей id,type,title,score,snippet. По умолчанию false (на сейчас функционально no-op).")),
            Read("check_integrity",
                desc: "Полный прогон валидатора на текущем docs/ без записи. Возвращает {ok, errors[]}; exit code всегда 0.",
                examples: new[] { "docswalker check-integrity" }),
            Read("get_overview",
                desc: "Глобальный snapshot хранилища: total_nodes, max_depth, total_tokens, trees, schema.types_count/top_types_by_count, root_children, hot_spots (largest_nodes по tokens — кандидаты на разбиение; most_connected_nodes по числу cross-refs, in+out без tree-refs). Зови первым в сессии — оценить размер и центры графа.",
                examples: new[] { "docswalker get-overview" }),
            Read("get_usage_guide",
                desc: "Manifest всех команд + ментальная модель + перечень tree-scope'ов + слепок графа. Зови в начале сессии. Опциональный --command=<kebab-name> отдаёт описание одной команды (mental_model/trees/snapshot остаются).",
                examples: new[]
                {
                    "docswalker get-usage-guide",
                    "docswalker get-usage-guide --command=get-nodes",
                },
                Opt("command", ParamType.String, "Kebab-имя команды для targeted-выдачи. Без параметра — манифест всех команд.")),

            // REPL поверх ядра — интерактивный HTTP-клиент к DocsWalker.Kernel.exe.
            // Читает .dw/client.json (kernel host/port + graph) поиском вверх от cwd.
            // Каждая введённая команда уходит как tools/call на /db/{graph}/rpc.
            Read("repl",
                desc: "Интерактивный REPL-клиент DocsWalker. Читает .dw/client.json (host/port/graph kernel'а) поиском вверх от cwd. Каждая введённая команда (без префикса 'docswalker') уходит в kernel /db/{graph}/rpc как tools/call. Kernel должен быть запущен заранее (auto-spawn убран в stg-0010).",
                examples: new[]
                {
                    "docswalker repl",
                    "docswalker repl --quiet=true",
                },
                Opt("quiet", ParamType.String, "Глушит баннер старта и приветствие в stderr (true/false). По умолчанию false.")),

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
                desc: "Удалить узлы явным списком --ids=. Авто-каскада нет: набор собирается LLM (get-tree + path-children каждого).",
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
                Req("operations", ParamType.JsonArray, "JSON-массив операций (см. формат в TransactionParser). Принимается через MCP arguments напрямую (array of object) — серверный конвертер передаст raw JSON со скобками в CLI.")),
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
