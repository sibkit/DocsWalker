namespace DocsWalker.Cli.UsageGuide;

/// <summary>
/// Краткая ментальная модель DocsWalker для LLM-агента (≤30 строк). Возвращается
/// командой <c>get-usage-guide</c> в поле <c>mental_model</c>. Полная версия
/// (атомарными узлами с примерами) — в docs/DocsWalker.yml/«Как LLM работает с DocsWalker».
/// </summary>
internal static class UsageGuideText
{
    public const string MentalModel =
        """
        DocsWalker представляет docs/ как граф: узлы (units of meaning) + направленные именованные связи (out_refs). LLM работает только с узлами и связями через CLI/MCP — имена файлов и каталогов наружу не торчат.

        Архитектура процессов: DocsWalker.Kernel.exe — фоновое ядро, HTTP+JSON-RPC 2.0 на 127.0.0.1:<port>, держит N графов в RAM (multi-graph). Все CLI-команды и MCP-вызовы идут к ядру; routing — через graph-name в URL: POST /db/<graph>/rpc. Имена графов и storage-paths объявлены в kernel-config.json (на стороне ядра, путь передаётся ему как DocsWalker.Kernel.exe --config=<path>). CLI/REPL/MCP-wrapper читают .dw/client.json (kernel host/port + graph-name) поиском вверх по родителям от cwd, как .git/. Auto-spawn убран: ядро запускается пользователем заранее; если ядро не отвечает — клиент возвращает kernel_unreachable.

        Контракт CLI (envelope-free):
        - Успех — exit 0, stdout — JSON-результат команды напрямую, без обёртки. Шейп — специфика команды (объект или массив).
        - Ошибка — exit ≠ 0, stderr — плоский JSON {code, message, path?, hint?, describe_type?}. stdout при ошибке пустой.
        - Дискриминатор — exit-code и поток (stdout vs stderr).
        - Для write-команд applied: true|false — top-level поле результата (true — записано на FS, false — dry-run). Для transaction — top-level массив, applied в каждом элементе.

        Связи объявлены в Схеме у типа узла-источника (имя, target_types, cardinality, required). Часть связей объединена в named-tree (tree-scope) — например, дерево 'path' (физическое размещение в FS, единственное материализованное) или доменное 'strategic'. Tree-связи всегда cardinality=one + required=true.

        Auto-include: связь с tree=null + required=true считается концептуально неотъемлемой — read-команды (get-nodes, get-subtree, get-by-path) транзитивно подтягивают цели таких связей в результат и помещают их в поле auto_includes (в плоский массив у get-nodes). Все узлы — полные, повторы между запросами и внутри одного ответа не фильтруются. Для дешёвого обзора без auto-include — fields=[title] + depth/tree в read-командах. На текущей Схеме auto-include активен только для rule.examples — единственная non-tree required связь.

        Корень — синглтон id=0, type=root. Любой обход начинается отсюда: get-subtree --id=0 --tree=path даёт всё дерево хранилища.

        Порядок работы перед записью:
        1. check-integrity — убедиться, что граф валиден.
        2. get-subtree / get-refs — прочитать актуальное состояние затронутого участка.
        3. describe-type --name=<type> — уточнить контракт типа (если незнакомый).
        4. На незнакомой задаче — write-команда с --dry-run=true (applied=false, без записи в FS).
        5. Если ответ ожидаемый — повтор без --dry-run.

        Удаление — только delete-nodes --ids= (явный список, без авто-каскада). Набор LLM собирает сама: get-subtree по нужному tree-scope + path-children каждого узла. Ошибки path_orphans_left и dangling_refs — обучающий сигнал, перечисляют недостающее.

        Переподшивка узла в дереве — move-node --tree=<scope> (по умолчанию 'path'). Массовая переподшивка cross-refs — redirect-refs.

        Запреты:
        - Прямая правка YAML / sequence.txt / folders.yml в обход API — ядро sole-writer; внешний edit ломает консистентность RAM-графа в ядре. Если правка действительно нужна — graceful kernel stop, edit, restart.
        - Изменение Схемы — задача человека, не LLM (нет API-команды).
        - move-node без --tree, если намерение — переподшить в доменном дереве: запустится реструктуризация хранилища.

        Команды по сценариям:
        - Одноразовый CLI-вызов: docswalker <команда> — читает .dw/client.json (вверх от cwd), форвардит в /db/<graph>/rpc ядра. Никакого --root в команде.
        - Интерактивный REPL: docswalker repl (HTTP-клиент к ядру; команды без префикса 'docswalker'; выход — :quit/:exit/Ctrl+D).
        - MCP-канал для Claude Code: docswalker mcp-server (тонкий stdio↔HTTP wrapper; обычно вызывается через .mcp.json, не вручную).
        - Запуск ядра: DocsWalker.Kernel.exe --config=<path-to-kernel-config.json> — отдельный exe (не подкоманда CLI). Слушает /db/<graph>/rpc для каждого графа из config'а.
        Per-graph idle eviction = graph_idle_timeout (default 10m, configurable в kernel-config.json): если граф не запрашивался дольше — выгружается из RAM, при следующем обращении re-load с диска.
        """;
}
