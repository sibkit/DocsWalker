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

        tools/list и прямой tools/call ограничены компактной LLM-facing surface: hit, query, tx, get-overview, get-usage-guide, describe-type, get-schema. Остальные legacy read/write commands не являются MCP/kernel surface и должны возвращать unknown tool.

        Контракт kernel/MCP:
        - JSON-RPC request: {"jsonrpc":"2.0","id":<id>,"method":"tools/call","params":{"name":"<tool>","arguments":{...}}}.
        - Успех: JSON-RPC result с MCP content; внутри text лежит JSON-результат конкретного tool.
        - Ошибка JSON-RPC: error {code,message}. Ошибка DocsWalker tool: JSON с машинным code, message, path?, hint?, describe_type?.
        - Для LLM-facing batch-записи используй tx с intent и mode=apply_if_safe: kernel сначала запускает preview через hit, затем применяет tx и возвращает единый envelope с ok/method/base_revision/results.

        Связи объявлены в Схеме у типа узла-источника: имя, target_types, cardinality, required. Часть связей объединена в named-tree (tree-scope): path для физического размещения в хранилище и доменные классификаторы. Tree-связи всегда cardinality=one + required=true.

        Auto-include: связь с tree=null + required=true считается концептуально неотъемлемой. LLM-facing query возвращает связи и текст только по явному include. Для дешёвого обзора используй get-overview или query compact-формы. На текущей Схеме auto-include активен только для rule.examples.

        Корень графа — id=0, type=root. Стартовый snapshot всего хранилища даёт get-overview; точное чтение участка делай через query.

        Порядок работы перед записью:
        1. get-overview или query — прочитать актуальное состояние затронутого участка.
        2. describe-type — уточнить контракт типа, если он незнаком.
        3. hit — опционально проверить selectors/write-ops без записи, если нужна отдельная preview-картина.
        4. tx(intent, ops, mode=apply_if_safe) — атомарно применить ожидаемые изменения; kernel сам сверит intent и результат предварительной validation.

        Удаление в LLM-facing workflow — tx delete с id/ids/path/select и expected_count для массовых операций. Авто-каскада нет: LLM явно проверяет размер через hit/query и затем применяет tx. Ошибки path_orphans_left и dangling_refs перечисляют недостающее.

        Переподшивка узла и смысловых связей в LLM-facing workflow — tx move/link/unlink. Legacy move-node/redirect-refs не публикуются через MCP/kernel и остаются только внутренней diagnostic/CLI implementation detail до удаления.

        Запреты:
        - Не править YAML / sequence.txt / folders.yml в обход kernel API: kernel — sole-writer, внешний edit ломает консистентность RAM-графа.
        - LLM не меняет Схему через MCP surface. Схемные миграции остаются admin/diagnostic задачей вне LLM-facing tools и должны проходить server-side проверку meta-schema и текущего графа.
        - Не использовать legacy CLI как рабочую поверхность LLM. Он остаётся внутренним/diagnostic слоем до выпила, но usage-guide и эксплуатационный путь — MCP/kernel-only.

        Per-graph idle eviction = graph_idle_timeout (default 10m, configurable в kernel-config.json): если граф не запрашивался дольше — выгружается из RAM, при следующем обращении re-load с диска.
        """;
}
