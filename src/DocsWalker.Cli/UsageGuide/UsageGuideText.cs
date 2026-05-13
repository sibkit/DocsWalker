namespace DocsWalker.Cli.UsageGuide;

/// <summary>
/// Краткая ментальная модель DocsWalker для LLM-агента. Возвращается tool-ом
/// <c>get-usage-guide</c> в поле <c>mental_model</c>.
/// </summary>
internal static class UsageGuideText
{
    public const string MentalModel =
        """
        DocsWalker представляет docs/ как граф: узлы (units of meaning) + направленные именованные связи (out_refs). LLM работает только с узлами и связями через MCP tools поверх kernel JSON-RPC; имена файлов и каталогов наружу не торчат.

        Архитектура процессов: DocsWalker.Kernel.exe — фоновое ядро, HTTP+JSON-RPC 2.0 на 127.0.0.1:<port>, держит N графов в RAM (multi-graph). Canonical endpoint графа: POST /<graph>. Namespace /api/v0.4 зарезервирован под API/control plane DocsWalker, поэтому graph-name api запрещён. Имена графов и storage-paths объявлены в kernel-config.json; kernel запускается оператором заранее как DocsWalker.Kernel.exe --config=<path>.

        Основной LLM-канал — MCP: клиент вызывает tools/list и tools/call, wrapper читает .dw/client.json (kernel host/port + graph-name) поиском вверх от cwd и пересылает вызов в /<graph>. Auto-spawn отсутствует: если kernel не отвечает, клиент получает kernel_unreachable.

        Контракт kernel/MCP:
        - JSON-RPC request: {"jsonrpc":"2.0","id":<id>,"method":"tools/call","params":{"name":"<tool>","arguments":{...}}}.
        - Успех: JSON-RPC result с MCP content; внутри text лежит JSON-результат конкретного tool.
        - Ошибка JSON-RPC: error {code,message}. Ошибка DocsWalker tool: JSON с машинным code, message, path?, hint?, describe_type?.
        - Для LLM-facing batch-записи используй tool tx: он возвращает единый envelope с ok/method/base_revision/results.

        Связи объявлены в Схеме у типа узла-источника: имя, target_types, cardinality, required. Часть связей объединена в named-tree (tree-scope): path для физического размещения в хранилище и доменные классификаторы. Tree-связи всегда cardinality=one + required=true.

        Auto-include: связь с tree=null + required=true считается концептуально неотъемлемой. Read-tools get-nodes/get-tree/get-by-path транзитивно подтягивают цели таких связей в результат и помещают их в auto_includes. Для дешёвого обзора без auto-include используй fields=[title] + depth/tree. На текущей Схеме auto-include активен только для rule.examples.

        Корень графа — id=0, type=root. Обход всего хранилища: tools/call get-tree с arguments {"id":0,"tree":"path"}.

        Порядок работы перед записью:
        1. check-integrity — убедиться, что граф валиден.
        2. get-tree / get-refs — прочитать актуальное состояние затронутого участка.
        3. describe-type — уточнить контракт типа, если он незнаком.
        4. hit — проверить selectors/write-ops без записи.
        5. tx — атомарно применить ожидаемые изменения.

        Удаление — только delete-nodes явным списком ids; авто-каскада нет. Набор собирает LLM через get-tree по нужному tree-scope и path-children каждого узла. Ошибки path_orphans_left и dangling_refs перечисляют недостающее.

        Переподшивка узла в дереве — move-node с явным tree, если цель не path. Массовая переподшивка cross-refs — redirect-refs.

        Запреты:
        - Не править YAML / sequence.txt / folders.yml в обход kernel API: kernel — sole-writer, внешний edit ломает консистентность RAM-графа.
        - Схему менять только через update-schema: atomic-замена с server-side проверкой meta-schema и текущего графа.
        - Не использовать legacy CLI как рабочую поверхность LLM. Он остается внутренним/diagnostic слоем до выпила, но usage-guide и эксплуатационный путь — MCP/kernel-only.

        Per-graph idle eviction = graph_idle_timeout (default 10m, configurable в kernel-config.json): если граф не запрашивался дольше — выгружается из RAM, при следующем обращении re-load с диска.
        """;
}
