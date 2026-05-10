# stg-0010 — step-01 — spec-rewrite

## Цель

Переписать `docs/DocsWalker.yml` под новую модель процесса (Блоки 1+2 страт-файла) и формализовать addressable trees (Блок 3) в meta-schema. Шаг **docs-only**: код не трогаем; `dotnet build` и `dotnet test` после шага должны быть в том же состоянии, что и до — изменения в `docs/` не зацепляют код.

Все нодные правки в `docs/DocsWalker.yml` — через DocsWalker `transaction`. Мета-схема (`docs/.docswalker/meta-schema.yml`) — ручной `Edit`, потому что DocsWalker не редактирует свой собственный мета-формат.

## Файлы

- `docs/DocsWalker.yml` — главные правки (через DocsWalker `transaction`).
- `docs/.docswalker/meta-schema.yml` — расширение мета-схемы (`Edit` напрямую).
- `docs/Схема.yml` — добавить `unique_sibling_titles: true` к tree-связи `name=path` во всех типах. Без этого `path`-дерево перестаёт быть addressable, и step-02 (`get-by-path` под новой моделью) сломается.

## Изменения в meta-schema (Блок 3)

1. **`schema_root` — новое опциональное поле `default_addressable_tree`:**

   ```yaml
   - name: default_addressable_tree
     type: string
     required: false
     description: |
       Имя дерева, используемого как default в API/CLI (`get-by-path` без --tree=).
       Если не задано и в проекте ровно один addressable tree — он default
       автоматически. Если addressable trees больше одного и поле не задано —
       --tree= обязателен (ошибка tree_required).
   ```

2. **`ref_def` — новое опциональное поле `unique_sibling_titles`:**

   ```yaml
   - name: unique_sibling_titles
     type: bool
     required: false
     description: |
       Только для tree-связей. true — title узла должен быть уникальным
       среди siblings под одним parent в этом дереве. false (default) —
       sibling-дубли разрешены. Tree-связь с unique_sibling_titles=true
       называется addressable; такие деревья поддерживают get-by-path,
       проверку duplicate_sibling_title при write.
   ```

3. **`tree_definition`** — не меняется. Addressable — свойство ref-связи в типе-источнике, не дерева как такового.

4. **`schema_root.constraints` — обновить пункт про `path`:**

   - Сейчас: «Дерево с именем `path` обязательно присутствует в `trees`: оно соответствует встроенному scope хранилища ядра.»
   - Заменить на: «Имя `path` — конвенция для дерева, материализуемого ядром в FS-структуру (текущая реализация). Жёсткого требования наличия именно `path` в `trees` мета-схема не накладывает.»
   - Обоснование: страт Блок 3.5 — «FS-материализация — kernel-internal, не часть API-контракта», meta-schema не должна жёстко требовать именно `path`.

5. **`schema_root.constraints` — добавить пункт про `default_addressable_tree`:**

   - «`default_addressable_tree` (если задан) обязан ссылаться на имя дерева из `trees`, у которого в типах-источниках хотя бы одна tree-связь имеет `unique_sibling_titles: true`.»

6. **`ref_def.constraints` — добавить пункт про `unique_sibling_titles`:**

   - «`unique_sibling_titles` допустим только при заданном `tree`. Для horizontal-связи (без `tree`) поле запрещено.»

## Изменения в `docs/Схема.yml`

После step-08 stg-0008 в Схеме у каждого типа (кроме `root`) есть обязательная tree-связь `name=path` с `tree=path`. К ним надо дописать `unique_sibling_titles: true`. Это удерживает `path`-дерево как addressable в новой модели.

Узлы, которые надо тронуть: все `type_definition` с tree-связью `name=path`. Точный список — после `Read docs/Схема.yml`.

## Изменения в `docs/DocsWalker.yml` (Блоки 1+2)

### Раздел «Модель процесса»

Текущее состояние (после stg-0008/0009): per-user kernel, multi-root в одном ядре, per-user discovery (`%LOCALAPPDATA%\DocsWalker\kernel.json` / `$XDG_RUNTIME_DIR/docswalker/`), auto-spawn разрешён (не silent), per-root idle eviction, MCP-wrapper подмешивает `--root=`, HTTP+JSON-RPC 2.0, per-root semaphore, sole-writer без file-lock.

**Что меняем:**

1. **Multi-root → multi-graph (named).** «Roots» как термин уходит везде; вместо этого — «graphs», объявленные по имени в kernel-config.
2. **Per-user kernel → standalone HTTP-сервер.** Узлы про «один kernel на пользователя» — переписать. Kernel может быть локальным или удалённым, запускается явно, без привязки к OS-пользователю.
3. **Per-user discovery — удалить.** Все узлы про `%LOCALAPPDATA%\DocsWalker\kernel.json` и `$XDG_RUNTIME_DIR/docswalker/kernel.json` — `delete_nodes`.
4. **Auto-spawn — удалить.** Узлы про спавн kernel из CLI/MCP/REPL — `delete_nodes`. Kernel запускается только вручную через `DocsWalker.Kernel.exe --config=<path>`.
5. **Idle eviction:** per-root → per-graph. Формулировка обновляется (`graph_idle_timeout` из kernel-config, default 10 мин — оставляем).
6. **MCP-wrapper:** не подмешивает `--root=` в RPC-аргументы. Использует cwd для resolve `.dw/client.json`.
7. **Per-root semaphore → per-graph semaphore.** Семантика та же, имя меняется.
8. **Sole-writer:** trust boundary. Явная формулировка «один storage-path обязан быть в одном kernel-config'е; защита от ошибки оператора (два kernel-а на одном storage) в этой страт не делается».
9. **Bind по умолчанию `127.0.0.1`,** `--bind=0.0.0.0` явно для удалённого доступа. Auth — не делаем в этой страт; явно «kernel — trusted network only».
10. **`kernel_already_running`-проверка убрана.** Несколько kernel-ов на одной машине нормально (разные ports, разные kernel-config'и); collision на port — на ответственности Kestrel.

### Раздел «URL и транспорт»

1. **`/health`** — оставляем, kernel-level, без graph-name.
2. **`/roots` → `/db`.** Список graphs, элементы `{name, loaded, last_access}` (только декларированные имена, без storage path).
3. **`/rpc` → `/db/<graph-name>/rpc`** — все RPC-вызовы.
4. **JSON-RPC 2.0** — не меняется.

### Раздел «Конфигурация»

Новый блок узлов (либо расширение существующего раздела про конфигурацию, если такой есть).

1. **Kernel-config** — JSON-файл, путь через `--config=<path>` argv. Формат:

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

   Storage path = path к docs-folder напрямую (не к project-folder; kernel ищет `<storage>/Схема.yml`, `<storage>/.docswalker/...`).

2. **Client-config** — JSON-файл `.dw/client.json` в проектной папке. Формат:

   ```json
   {
     "kernel": { "host": "127.0.0.1", "port": 8080 },
     "graph": "docswalker"
   }
   ```

   Resolution: поиск вверх от cwd до filesystem-root (как `git` ищет `.git/`). Первый найденный — используется. Не найдено → `client_config_not_found`.

3. **Один client-config = один graph.** Override в запросе не делаем.
4. **`--help` / `--version`** не требуют client-config (не делают RPC).
5. **Multi-graph use-case** — N инстансов клиента из N разных проектных папок (каждая со своим `.dw/client.json`).

### Раздел «Команды и параметры»

1. **`--root=`** — удалить из всех описаний команд: `get-nodes`, `get-by-path`, `get-subtree`, `get-refs`, `get-in-refs`, `get-ancestors`, `create-node`, `update-node`, `delete-nodes`, `move-node`, `create-ref`, `delete-ref`, `redirect-refs`, `transaction`, `search`, `check-integrity`, `describe-type`, `get-schema`, `get-meta-schema`, `get-usage-guide`, `get-map`. Точный список — по inventory.
2. **`get-by-path`** — добавить опциональный параметр `--tree=<name>`. Resolution default-а — Блок 3.3 страта.
3. **Команда `kernel`** в реестре CLI-команд — удалить (узел про неё). Kernel запускается только через `DocsWalker.Kernel.exe --config=<path>`.
4. **REPL** — баннер показывает `graph=<name>, kernel=<host>:<port>` (вместо `root=<path>`).
5. **MCP-сервер** — узлы про подмешивание `--root=` удалить; добавить «использует cwd для `.dw/` resolution».

### Раздел «Ошибки»

1. **Новые коды:**
   - `client_config_not_found` — `.dw/client.json` не найден от cwd до filesystem-root.
   - `client_config_invalid` — JSON не парсится / отсутствуют обязательные поля.
   - `kernel_unreachable` — TCP-соединение с kernel-ом не установилось.
   - `tree_required` — `get-by-path` без `--tree=`, addressable trees > 1, `default_addressable_tree` не задан.
   - `tree_not_addressable` — `--tree=<name>` указывает на не-addressable tree.
   - `duplicate_sibling_title` — попытка `create-node` / `move-node` (с change of parent в addressable tree) / `update-node` (с change of title в addressable tree) создаёт sibling-collision.

2. **Удалённые / переименованные коды (по факту inventory):**
   - `server_not_running` (если есть) — заменён на `kernel_unreachable`.
   - `kernel_already_running` (если есть) — удалить (по решению Блок 1.6).
   - `root_not_found` (если есть) — заменён на `graph_not_found` (если такая ошибка нужна; если в текущем графе её нет — пропускаем).

### Раздел «Addressable trees» (новый, Блок 3)

1. **Definition.** Tree называется *addressable*, если у его tree-связи в Схеме `unique_sibling_titles: true` (свойство derived, не отдельный концепт).
2. **API.** `get-by-path` работает только с addressable trees. Не-addressable деревья навигируются через `get-subtree`, `get-refs`.
3. **Default tree.** Resolution `--tree=<name>` параметра в `get-by-path` (Блок 3.3 страта).
4. **Валидация при write.** Конфликт title в siblings → `duplicate_sibling_title`.
5. **FS-материализация** — kernel-internal, не часть API-контракта. В текущей реализации материализуется конкретно `path`-дерево.

## Действия

1. **Sanity-check CLI / kernel.**
   - `publish\cli\DocsWalker.Cli.exe get-usage-guide --root=<repo>` (старый `--root=`, текущая модель ещё работает) — должен ответить.
   - Если не отвечает: `dotnet publish` обоих exe (`DocsWalker.Cli`, `DocsWalker.Kernel`) и поднять kernel вручную (текущая модель: per-user `kernel.json` discovery).

2. **Inventory узлов.**
   - `get-meta-schema` — текущее представление мета-схемы.
   - `get-subtree --id=<root-id-Модель-процесса> --depth=99` — поддерево «Модель процесса».
   - `search --query=root` — все упоминания `--root=` (фильтровать по релевантности; будут совпадения и на «корневой узел»).
   - `search --query=kernel.json` — узлы про per-user discovery.
   - `search --query=auto-spawn` — узлы про авто-спавн.
   - `search --query=материализ` — узлы про материализацию `path`.
   - `search --query=session_id` — должно быть пусто (после stg-0009).
   - `get-by-path --path=<...>` для разделов «URL», «Команды», «Ошибки».

3. **Inbound-refs analysis.** Для каждого удаляемого узла — `get-in-refs --id=<N>`. Inbound решаются `delete-ref` / `redirect-refs` / `update-node`.

4. **Карта правок.** На каждый затронутый узел: `delete` / `update` / `redirect-refs` / `delete-ref` / `create`. Список новых узлов: kernel-config, client-config, /db endpoint, /db/<name>/rpc endpoint, новые ошибки, addressable trees, named graphs, default_addressable_tree.

5. **Meta-schema правки.** `Edit docs/.docswalker/meta-schema.yml` по списку из «Изменения в meta-schema».

6. **`docs/Схема.yml` правки.** `Read` файл, найти все `name: path` tree-связи, дописать `unique_sibling_titles: true`.

7. **Транзакция.** Атомарная `transaction` (массив операций: `delete_nodes`, `update_node`, `create_node`, `create_ref`, `delete_ref`, `redirect_refs`) на `docs/DocsWalker.yml`. Применить через CLI `transaction --root=<repo>`. Атомарность гарантирует rollback при ошибке.

8. **Пост-проверки:**
   - `check-integrity` — граф консистентен.
   - `search --query=--root=` → пусто (или только в исторических контекстах, явно объяснённых).
   - `search --query=auto-spawn` → пусто.
   - `search --query=kernel.json` → пусто.
   - `search --query=root_not_found` → пусто (если был).
   - `get-by-path --path=<...>` для новых разделов («Конфигурация», «Addressable trees») — узлы есть.
   - `get-meta-schema` — отражает обновления (новые поля, обновлённые constraints).
   - `dotnet build` — зелёный (docs-only не должно зацепить код).
   - `dotnet test` — зелёный (152/152 как было после stg-0009).

9. **Сверка со страта.** Пробежать «Принятые решения» (Блоки 1, 2, 3) — каждое decision должно отразиться в одном или нескольких узлах docs.

## Риски

- **Большой объём правок.** 30+ узлов под удаление/правку, плюс 10+ новых. Высока вероятность пропустить ссылку или конфликт. Mitigation: атомарная `transaction` (rollback при ошибке) + post-search по ключевым словам.
- **`Схема.yml` забыт.** Если не дописать `unique_sibling_titles: true` к `path`-tree, шаг 02 (addressable-trees) сломается на тестах `get-by-path`. Mitigation: отдельная задача (#6 в TaskList) и явный пункт в «Файлы».
- **Meta-schema через `Edit`.** DocsWalker не пишет в свою собственную meta-schema. Ручная правка YAML — единственный способ. Это не fallback, это нормальный workflow для meta-schema.
- **Kernel-config / client-config — JSON, не граф docs.** Описываются как `note`/`example` узлы с JSON в text-поле. Новые типы в Схему не добавляем — это step-03+ работа.
- **Dogfooding риск.** Текущий CLI (`publish/cli/`) собран под старую модель и принимает `--root=`. Описание в docs новой модели не ломает текущее поведение CLI — код будет переписан в шаге 03. Если CLI/kernel упадёт по другой причине — fallback на `Edit` YAML с явной отметкой в чате (нарушение приоритета DocsWalker, но единственный способ продолжить шаг).
- **`get-meta-schema` не отражает правки в YAML до перезапуска kernel.** Возможно, kernel кеширует meta-schema. Mitigation: после правки meta-schema — рестарт kernel или ждать idle-eviction; пост-проверки `get-meta-schema` после рестарта.
- **Kernel `pid=57588`** (текущий, со старой моделью) — не вижу проблемы для этого шага: правки docs и meta-schema kernel прочитает при следующем `get-meta-schema`/`get-schema` запросе (или после рестарта). После завершения шага этот kernel останется работающим в старой модели; в шаге 03 он будет полностью пересобран и перезапущен через kernel-config.
