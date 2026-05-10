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
       "docswalker": "/srv/docs/myproject",
       "another": "/data/another"
     },
     "graph_idle_timeout": "10m"
   }
   ```

   Имя графа — alias, путь — internal storage path. Клиент про путь не знает.

2. **URL формат:** `http://<host>:<port>/db/<graph-name>/rpc` для RPC.
   `/health` остаётся (kernel-level, без graph). `/db` (если оставляем) —
   список граф-имён (без путей).

3. **Client-config — JSON, обязательный.** Один способ конфигурации,
   никаких argv override / env / каскадов.

   ```json
   {
     "kernel": { "host": "127.0.0.1", "port": 8080 },
     "graph": "docswalker"
   }
   ```

   Расположение: `%APPDATA%\DocsWalker\client.json` (Windows) /
   `$XDG_CONFIG_HOME/docswalker/client.json` или `~/.config/docswalker/client.json`
   (POSIX). Если файла нет / невалидный — fail с `client_config_missing`
   или `client_config_invalid`.

4. **Один client-config = один graph.** Жёсткая привязка. Override в
   запросе — не делаем.

5. **Multi-graph use-case** = N MCP/CLI инстансов с N разных
   client-config'ов. Под Claude Code — две записи в `.mcp.json` с
   разными ENV/args, указывающими на разные client-config'и.

6. **`--root=` удаляется из всех команд.** CLI/MCP/REPL ничего про FS
   не знают. `Commands.cs`, `KernelHttpClient`, `McpWrapperHandler`,
   `ReplHandler`, `Program.cs` (CLI) — упрощаются.

7. **Kernel.exe не имеет CLI-обёртки** `docswalker kernel`. Запуск
   только напрямую `DocsWalker.Kernel.exe --config=<path>`.

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
   опциональный. Default tree выбирается **Схемой** (для DocsWalker
   convention — `path`), не системой. Если `--tree=` указывает на
   non-addressable tree — ошибка `tree_not_addressable` с hint'ом
   «используй get-subtree --tree=<name> --id=<root> или get-refs».

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

## Что остаётся целым

- **Транзакционная семантика write** — atomic, all-or-nothing.
- **Auto-include механизм** — не зависит от FS / process-model.
- **Per-graph semaphore** (раньше per-root, теперь per-graph; имя меняется,
  семантика та же).
- **Per-graph idle eviction** (раньше per-root, теперь per-graph).
- **JSON-RPC 2.0** как протокол.
- **Sole-writer гарантия** на graph — kernel единственный пишет в свой
  storage, теперь это его внутреннее свойство (один kernel на graph
  обеспечивается — на уровне Schema/конфигурации kernel-а).
- **Все типы операций (read/write)** — те же, минус `--root=` параметр.

## Шаги

- [ ] (01) spec-rewrite — переписать `docs/DocsWalker.yml` под новую
  модель процесса + ввести `unique_sibling_titles` в meta-schema +
  переписать описание tree-связей.
- [ ] (02) addressable-trees — расширить meta-schema parser, добавить
  `unique_sibling_titles` в `TreeRefDescriptor`, реализовать валидацию
  `duplicate_sibling_title` в `WriteState`, расширить `get-by-path`
  параметром `--tree=`, ошибка `tree_not_addressable`.
- [ ] (03) named-graphs — kernel-config (`KernelOptions` → `KernelConfig`
  с graphs map), URL routing `/db/<name>/rpc` в `Program.cs` kernel-а,
  переход с `RootRegistry` на `GraphRegistry` (named lookup).
- [ ] (04) client-config — JSON-config-файл для клиента, чтение endpoint
  + graph-name, ошибки `client_config_missing` / `client_config_invalid`.
- [ ] (05) kill-root-param — удалить `--root=` из всех CLI команд,
  обновить `Commands.cs`, `KernelHttpClient` (URL формирует из
  client-config), `McpWrapperHandler` (не подмешивает `root` в
  arguments), `ReplHandler` (не принимает `--root=`).
- [ ] (06) kill-auto-spawn — удалить `KernelInfoFile`, `KernelDiscovery`,
  часть `StalePidDetector` (которая обслуживала auto-spawn race),
  `kernel_already_running` проверку из `Program.cs` kernel-а.
- [ ] (07) tests-cleanup — обновить тесты под новую модель.
- [ ] (08) smoke — e2e на published binaries: вручную запущенный kernel
  с config'ом, два client-config (на два graph), CLI / MCP / REPL
  отрабатывают, addressable-trees валидация работает, `tree_not_addressable`
  возвращается корректно.

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

- Endpoint клиента — **только client-config JSON.** Никаких argv / env /
  каскадов. Окончательно.
- Auth — **в этой страт не делаем.** Окончательно.
- `--root=` уходит **полностью.** Клиент про FS не знает. Окончательно.
- Multi-graph — **named graphs в kernel-config + URL `/db/<name>/rpc`.**
  Не «один kernel = один graph», не «graph как опциональный override
  в запросе». Окончательно.
- Один client-config = один graph. Multi-graph use-case = N инстансов
  клиента. Окончательно.
- Addressable tree — **производное свойство Схемы** (через
  `unique_sibling_titles`), не system-reserved имя `path`. Окончательно.
- FS-материализация — **kernel-internal,** не часть API-контракта.
  Окончательно.

## Замечание по нумерации

В `stg-0008/strategy.md` зарезервирована стадия
`stg-0009.storage-format-json`. Эта kill-seen (stg-0009) уже взяла
номер 9. JSON-storage переезжает на `stg-0011.storage-format-json`.
