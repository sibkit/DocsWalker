# stg-0010 — Kernel as Service + Addressable Trees

**Статус:** в работе

## Задача

Превратить kernel в standalone HTTP-сервис, полностью отвязать клиентов
(CLI / MCP / REPL) от файловой системы, ввести multi-graph через
named-graphs в URL. Заодно формализовать «addressable tree» как явное
свойство Схемы вместо неявного свойства имени `path`.

## Триггер

После stg-0009 (kill-seen) сделана ревизия архитектуры в чате. Найдены
два класса проблем:

**Концептуальные (модель процесса).** Текущая mental_model `«один kernel
на пользователя + auto-spawn + per-user kernel.json discovery»` не
соответствует тому, что реально хочется: kernel может быть локальный или
удалённый, может стоять отдельным сервисом, клиент должен явно знать
куда подключаться. Per-user привязка — артефакт раннего дизайна, не
архитектурная необходимость.

**Концептуальные (адресация).** В докe и в коде смешаны два смысла «path»:
fs-путь к корню проекта (`--root`) и tree-путь по графу (`--path` в
`get-by-path`). Path-tree объявлен «особым» (адресуем строкой), но
особенность фактически держится на external constraint OS (нельзя двух
файлов с одинаковым именем в одной папке) — а не на нашей модели. Любой
другой tree из Схемы (cardinality=one + required=true) формально tree, но
строкой не адресуем — мы это не отслеживаем.

## Принятые решения

### Блок 1 — Standalone kernel

1. **Kernel — отдельный standalone HTTP-сервер.** Может быть запущен где
   угодно: локально, в LAN, в контейнере, на удалённом хосте. Никакой
   привязки к OS-пользователю.
2. **Auto-spawn убирается полностью.** CLI/MCP/REPL не запускают ядро
   сами. Если ядра нет — клиент даёт явную ошибку `kernel_unreachable`
   с подсказкой «запусти kernel и проверь client-config».
3. **Per-user discovery убирается.** `kernel.json` в `%LOCALAPPDATA%\DocsWalker\`
   и `$XDG_RUNTIME_DIR/docswalker/` — удаляется. Клиент не «находит»
   kernel — он его знает по client-config.
4. **Bind по умолчанию `127.0.0.1`,** `--bind=0.0.0.0` явно для удалённого
   доступа.
5. **Auth — не делаем в этой страт.** Явно задокументировать «kernel —
   trusted network only; bind на 0.0.0.0 — на ответственность оператора».
   Token-based auth — отдельная страт после конкретного use-case.
6. **`kernel_already_running` проверка убирается.** Несколько kernel-ов
   на одной машине нормально (разные ports, разные kernel-config'и). За
   port collision отвечает Kestrel.

### Блок 2 — Named graphs + client-config

1. **Multi-graph через named graphs в kernel-config.** При старте kernel
   читает свой config:

   ```json
   {
     "bind": "127.0.0.1",
     "port": 8080,
     "graphs": {
       "docswalker": "/srv/projects/docswalker/docs",
       "another": "/data/another/docs"
     },
     "graph_idle_timeout": "10m"
   }
   ```

   Имя графа — alias, путь — внутренний storage path **прямо к
   docs-folder** (не к project-folder; см. Блок 2.4). Клиент про путь не
   знает.

2. **URL формат:** `http://<host>:<port>/db/<graph-name>/rpc` для RPC.
   `/health` остаётся (kernel-level, без graph). Текущий endpoint
   `/roots` переименовывается в `/db` — отдаёт массив объектов
   `{name, loaded, last_access}` (только декларированные в kernel-config
   имена, без путей). `name` — единственное, что клиент видит про graph.

3. **Client-config — JSON в `.dw/client.json` проектной папки.** Один
   способ конфигурации, никаких argv override / env / каскадов
   значений. По духу аналог `.claude/`, `.git/`, `.vscode/` — конфиг
   живёт **в проекте**, рядом с тем, что им управляется.

   ```json
   {
     "kernel": { "host": "127.0.0.1", "port": 8080 },
     "graph": "docswalker"
   }
   ```

   **Resolution:** клиент ищет `.dw/client.json` начиная от cwd, затем
   вверх по родителям до filesystem-root (как `git` ищет `.git/`).
   Первый найденный — используется. Если не найдено — ошибка
   `client_config_not_found` с подсказкой «создай `.dw/client.json` в
   корне проекта».

4. **Storage path в kernel-config — путь к docs-folder напрямую,**
   без угадывания подпапки `docs/`. Kernel ищет `<storage_path>/Схема.yml`,
   `<storage_path>/.docswalker/...`. Сохранять в репо удобно как
   `<repo>/docs/` — тогда в kernel-config писать
   `"docswalker": "/path/to/repo/docs"`. Никакой автоматики на стороне
   kernel про слово «docs» больше нет.

5. **Один client-config = один graph.** Жёсткая привязка. Override в
   запросе — не делаем.

6. **Multi-graph use-case** = N инстансов клиента, запущенных из N
   разных проектных папок (каждая со своим `.dw/client.json`). Под
   Claude Code — две записи в `.mcp.json` с разными `cwd` (или с
   командой `cd <project> && docswalker mcp-server`), указывающими на
   разные проектные папки → разные `.dw/`.

7. **`--root=` удаляется из всех команд.** CLI/MCP/REPL ничего про FS
   не знают. `Commands.cs`, `KernelHttpClient`, `McpWrapperHandler`,
   `ReplHandler`, `Program.cs` (CLI) — упрощаются.

8. **Kernel.exe не имеет CLI-обёртки** `docswalker kernel`. Запуск
   только напрямую `DocsWalker.Kernel.exe --config=<path>`. Команда
   `kernel` удаляется из реестра CLI-команд (`Commands.cs`).
   Kernel-config — argv (а не `.dw/`), потому что kernel — service,
   часто запускается systemd/NSSM/manually, а не «из проекта».
   Асимметрия с клиентским конфигом — оправдана разным контекстом.

9. **Клиентский конфиг не требуется для `--help` / `--version`.**
   Эти команды не делают RPC-вызов и работают всегда (для
   bootstrapping / debugging).

10. **Sole-writer гарантия — trust boundary.** Один graph пишется
    одним kernel-ом. Защиты от ошибки оператора (два kernel-а на одном
    storage) в этой страте **нет** — это его ответственность. Явный
    file-lock — отдельная страт, если появится конкретная нужда.

### Блок 3 — Addressable trees

1. **Расширение мета-схемы.** У tree-связи в Схеме появляется опциональное
   булево поле:

   ```yaml
   tree: path
   cardinality: one
   required: true
   unique_sibling_titles: true
   ```

   Семантика: «при write в этом tree title нового/обновляемого узла не
   должен совпадать с title уже существующих siblings под тем же parent».
   Default `false` — sibling-дубли разрешены.

2. **Addressable tree — производный термин для API/доки.** Tree называется
   *addressable*, если в Схеме у соответствующей tree-связи
   `unique_sibling_titles: true`. Не отдельный флаг, не новый concept в
   meta-схеме — следствие.

3. **`get-by-path --tree=<name>` generalization.** Параметр `--tree=`
   опциональный. Resolution default-а:
   - Если в meta-schema задано опциональное top-level поле
     `default_addressable_tree: <name>` — используется оно.
   - Иначе если в Схеме ровно один addressable tree — он default.
   - Иначе (addressable tree больше одного, поле не задано) — `--tree=`
     обязателен; ошибка `tree_required`.

   Если `--tree=<name>` указывает на non-addressable tree — ошибка
   `tree_not_addressable` с hint'ом «используй get-subtree
   --tree=<name> --id=<root> или get-refs».

4. **Валидация при write.** При `create-node` / `move-node` (если меняется
   parent в addressable tree) / `update-node` (если меняется title и
   узел сидит в каком-то addressable tree) — для каждого addressable
   tree, в котором узел участвует, kernel проверяет: title не дублирует
   существующих siblings нового parent. Конфликт → ошибка
   `duplicate_sibling_title` с указанием conflicting id и tree-name.

5. **FS-материализация — kernel-internal.** Не часть API-контракта.
   В текущей реализации kernel материализует один конкретный addressable
   tree (имя `path`) в FS-структуру. В будущем может стать configurable
   (`materialize_tree: <name>` в kernel-config) или вообще отвязано
   (JSON-storage).

6. **`default_addressable_tree` в meta-schema.** Опциональное top-level
   поле в meta-schema (рядом с уже существующими секциями). Семантика —
   см. п.3 выше. Не валидируется per-Схема (пустое поле = «нет default,
   действует автоматическая логика»).

## Что остаётся целым

- **Транзакционная семантика write** — atomic, all-or-nothing.
- **Auto-include механизм** — не зависит от FS / process-model.
- **Per-graph semaphore** (раньше per-root, теперь per-graph; имя меняется,
  семантика та же).
- **Per-graph idle eviction** (раньше per-root, теперь per-graph).
- **JSON-RPC 2.0** как протокол.
- **Sole-writer гарантия** на graph — kernel единственный пишет в свой
  storage; обеспечивается **разумным использованием** kernel-config'а
  (один storage path — в одном kernel-config'е). Защиты от ошибки
  оператора не делаем (см. Блок 2.10).
- **Все типы операций (read/write)** — те же, минус `--root=` параметр.
- **`get-usage-guide`, `describe-type`, `get-schema`, `get-meta-schema`**
  — структура та же; только убирается `--root=` из сигнатур.

## Шаги

- [+] (01) spec-rewrite — переписать `docs/DocsWalker.yml` под новую
  модель процесса (Блок 1+2) + meta-schema (`unique_sibling_titles`,
  опциональный `default_addressable_tree`) + переписать описание
  tree-связей (Блок 3) + удалить из спеки упоминания `--root=`,
  per-user kernel.json, auto-spawn, материализации `path` как
  системного. Шаг через DocsWalker `transaction` (или
  fallback-Edit на YAML, если CLI не поднимается). `dotnet build/test`
  не должны измениться (только docs).
- [+] (02) addressable-trees — расширить meta-schema parser, добавить
  `unique_sibling_titles` в `TreeRefDescriptor`, опциональное
  `default_addressable_tree` в `SchemaDocument`, реализовать валидацию
  `duplicate_sibling_title` в `WriteState`, расширить `get-by-path`
  параметром `--tree=` (default по новой логике), ошибки
  `tree_not_addressable` / `tree_required`. `dotnet test` зелёный
  (новые тесты на oба errors + sibling-collision на create/move/update).
- [+] (03) client-server-reshape — **атомарный шаг,** перестраивает
  весь client-server контракт. Промежуточно делить нельзя: kernel
  и клиент должны переходить на новый URL/протокол одновременно,
  иначе e2e ломается. Содержание:
  - **Kernel-side:** `KernelOptions` → `KernelConfig` (JSON-файл,
    `--config=<path>` argv), graphs map `<name> → <docs-folder-path>`,
    URL routing `/db/<name>/rpc` + `/db` (вместо `/roots`) + `/health`
    в `Program.cs` kernel-а, `RootRegistry` → `GraphRegistry` (lookup
    by name из config'а), `SchemaLoader` / `DocumentLoader` — путь
    напрямую к docs-folder (без подкаталога `docs/`),
    `graph_idle_timeout` из config'а.
  - **Client-side:** JSON-config-файл (`.dw/client.json`), поиск вверх
    по родителям от cwd до filesystem root, ошибки
    `client_config_not_found` / `client_config_invalid`,
    `kernel_unreachable` (если kernel offline),
    `KernelHttpClient` формирует URL из client-config'а
    (`http://host:port/db/<graph>/rpc`).
  - **CLI surface:** удалить `--root=` из всех команд (становится
    `unknown_parameter` автоматически), удалить `Read("kernel", ...)`
    запись из `Commands.cs`, `McpWrapperHandler` не подмешивает
    `root` в arguments + использует cwd для `.dw/` resolution,
    `ReplHandler` не принимает `--root=` + баннер показывает
    `graph=<name>, kernel=<host>:<port>`, `Program.cs` (CLI) —
    упрощается dispatcher.
  - **Help/version exempt:** не требуют client-config для запуска
    (не делают RPC).
  - **Тесты на `--root=`:** в этом же шаге обновляются на новую
    модель (mock client-config / kernel-config). Иначе после шага
    `dotnet test` красный — нарушение «зелёный после каждого шага».
    Атомарно с code-changes, как в stg-0009 step-02.
  - `dotnet build` + `dotnet test` оба зелёные.
- [+] (04) kill-auto-spawn — удалить `KernelInfoFile`,
  `KernelDiscovery`, часть `StalePidDetector` (auto-spawn race),
  `kernel_already_running` проверку из `Program.cs` kernel-а. Любая
  оставшаяся auto-spawn логика в `KernelHttpClient` — удалить. В
  этом же шаге удалить тесты, прямо ссылающиеся на эти типы
  (по тому же принципу атомарности). `dotnet build/test` зелёные.
- [ ] (05) error-case-tests — добавить тесты на новые error-coды
  (`client_config_not_found`, `client_config_invalid`,
  `kernel_unreachable`); добавить edge-cases для addressable-trees,
  если в шаге 02 что-то не покрыто (move-node между parent'ами с
  collision, update-node title с collision, transaction с collision
  внутри батча). `dotnet test` зелёный.
- [ ] (06) smoke — e2e на published binaries:
  - kernel запускается вручную из `publish/kernel/` с тестовым
    kernel-config (2 graph, `bind=127.0.0.1`).
  - 2 проектные папки, в каждой `.dw/client.json` указывает на свой
    graph.
  - `docswalker get-nodes --ids=1` из обеих папок — каждая видит свой
    graph.
  - `docswalker mcp-server` (запущенный с разным `cwd`) — отвечает на
    initialize + tools/call в правильном graph'е.
  - REPL — баннер показывает graph и kernel-endpoint.
  - `--root=...` → `unknown_parameter`.
  - `get-by-path --path="..."` без `--tree=` — работает (default из
    Схемы / автоматически).
  - `create-node` с дублирующим title в addressable tree → ошибка
    `duplicate_sibling_title`.
  - `get-by-path --tree=<non-addressable>` (если в Схеме появится
    такой) → `tree_not_addressable`.
  - kernel остановлен → клиент даёт `kernel_unreachable`.
  - Запуск без `.dw/client.json` → `client_config_not_found`.

## Решение по разрезу

Блок 3 (addressable trees) технически независим от блоков 1+2, и
теоретически выносится в отдельную страт `stg-0011.addressable-trees`.
**Решено в обсуждении: одна страт.** Атомарность сохраняется на уровне
шагов (каждый step — отдельный коммит), `dotnet build` зелёный после
каждого step'а. Разделение на две страт даёт overhead планирования,
не давая выигрыша по чистоте дифа.

## Точка возобновления (для новой сессии после сброса контекста)

Цель: standalone HTTP-сервис вместо per-user kernel + auto-spawn,
multi-graph через named graphs в URL, decoupling клиентов от FS,
формализация addressable tree через `unique_sibling_titles` в Схеме.

Старт новой сессии:

1. Прочитать проектный `CLAUDE.md` — общие правила (`docs/` через
   DocsWalker, atomic git, Glider для C#-навигации).
2. Прочитать этот `strategy.md` — особенно «Принятые решения».
3. Прочитать первый `[*]`-step и работать строго по нему. Step-файлы
   создаются перед началом каждого шага.
4. После завершения каждого шага — `[*] → [+]`, atomic git
   add / commit / push.

Что **не** надо пересматривать (зафиксировано в чате):

- Endpoint клиента — **только `.dw/client.json`** (поиск вверх по
  родителям, как `.git`). Никаких argv для endpoint, никаких ENV.
  Окончательно.
- Kernel запускается **только** через `DocsWalker.Kernel.exe --config=<path>`.
  Команда `docswalker kernel` удаляется. Окончательно.
- Auth — **в этой страт не делаем.** Окончательно.
- `--root=` уходит **полностью.** Клиент про FS не знает. Окончательно.
- Multi-graph — **named graphs в kernel-config + URL `/db/<name>/rpc`.**
  Не «один kernel = один graph», не «graph как опциональный override
  в запросе». Окончательно.
- Один client-config = один graph. Multi-graph use-case = N инстансов
  клиента, запускаемых из N разных проектных папок. Окончательно.
- Storage path в kernel-config = **path к docs-folder напрямую** (не
  к project-folder). Никакой автоматики «docs/» подпапки. Окончательно.
- Addressable tree — **производное свойство Схемы** (через
  `unique_sibling_titles`), не system-reserved имя `path`. Окончательно.
- Default tree для `get-by-path` — опциональное поле
  `default_addressable_tree` в meta-schema; иначе автоматический выбор
  если addressable tree один. Окончательно (если не возразишь по
  «всегда обязательное» — см. блок 3.6).
- FS-материализация — **kernel-internal,** не часть API-контракта.
  Окончательно.
- Sole-writer — **trust boundary,** защиту от ошибки оператора в этой
  страт не делаем. Окончательно.

## Замечание по нумерации

В `stg-0008/strategy.md` зарезервирована стадия
`stg-0009.storage-format-json`. Эта kill-seen (stg-0009) уже взяла
номер 9. JSON-storage переезжает на `stg-0011.storage-format-json`.
