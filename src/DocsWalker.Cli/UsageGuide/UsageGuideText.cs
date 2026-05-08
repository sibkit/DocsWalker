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

        ВАЖНО — серверная модель: все команды (кроме `run`) работают только через локальный IPC к запущенному серверу. Перед любой командой должен быть запущен `docswalker run --root=<path>`. CI-pipeline: `docswalker run --root=. &` → команды → kill. Если сервер не запущен — любая команда вернёт exit 1 и {"code":"server_not_running","hint":"docswalker run --root=<path>"}.

        Контракт CLI (envelope-free):
        - Успех — exit 0, stdout — JSON-результат команды напрямую, без обёртки. Шейп — специфика команды (объект или массив).
        - Ошибка — exit ≠ 0, stderr — плоский JSON {code, message, path?, hint?, describe_type?}. stdout при ошибке пустой.
        - Дискриминатор — exit-code и поток (stdout vs stderr).
        - Для write-команд applied: true|false — top-level поле результата (true — записано на FS, false — dry-run). Для transaction — top-level массив, applied в каждом элементе.

        Связи объявлены в Схеме у типа узла-источника (имя, target_types, cardinality, required). Часть связей объединена в named-tree (tree-scope) — например, дерево 'path' (физическое размещение в FS, единственное материализованное) или доменное 'strategic'. Tree-связи всегда cardinality=one + required=true.

        Auto-include: связь с tree=null + required=true считается концептуально неотъемлемой — read-команды (get-nodes, get-subtree, get-by-path) транзитивно подтягивают цели таких связей в результат и помещают их в поле auto_includes (в плоский массив у get-nodes). Подтянутые цели проходят через тот же seen-фильтр, что и children: повторное транзитивное появление в той же session_id заменяется placeholder'ом {id, seen:true}. На текущей Схеме auto-include активен только для rule.examples — единственная non-tree required связь.

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
        - Прямая правка YAML / sequence.txt / folders.yml в обход API — теряются sequence-инвариант, целостность графа, атомарность.
        - Изменение Схемы — задача человека, не LLM (нет API-команды).
        - move-node без --tree, если намерение — переподшить в доменном дереве: запустится реструктуризация хранилища.

        Серверный режим (особая команда `run`): docswalker run --root=<path> запускает long-lived сервер для одного docs/-root — захватывает file-lock на docs/.docswalker/run.lock, открывает локальный IPC-канал, держит граф в RAM до выхода. В TTY открывает REPL-prompt, при редиректе stdin — блокируется на сигнал. Опции: --quiet=true глушит баннер старта в stderr; --mode=tty|headless даёт явный override автодетекта. Корректное завершение — :quit / Ctrl+D в REPL либо SIGINT/SIGTERM/Ctrl+C в headless. `run` сама не вызывается LLM; её запускает оператор/CI.
        """;
}
