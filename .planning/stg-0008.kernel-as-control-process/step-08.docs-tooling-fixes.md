# stg-0008 — step-08 — docs-tooling-fixes

## Цель

Закрыть четыре API-неудобства DocsWalker, накопленные за время dogfooding'а
stg-0008 (step-01..07). Каждое — самостоятельный мини-фикс, все вместе делают
выдачу `get-usage-guide` достаточной для LLM, чтобы не угадывать формат и не
получать `no_effect` на ровном месте.

## Под-задачи

### 1. Документ операций `transaction` в `get-usage-guide`

**Проблема:** для команды `transaction` в выдаче `get-usage-guide` сейчас стоит
описание «JSON-массив операций (см. формат в `TransactionParser`)». LLM ловит
ошибки `missing_field 'from_ids'` или `unknown_op` потому, что:

- JSON-формат операций — snake_case + массивы (`from_ids:[42], to_id:8, unlink:true`).
- Одноимённые CLI-команды — kebab-case + скаляры (`--from=42 --to=8 --unlink=true`).

LLM не видит маппинг между ними и пытается угадать.

**Решение:** добавить в выдачу `get-usage-guide` поле `transaction_operations` —
описание каждой операции (имена полей в JSON, типы, required/optional, маппинг
от CLI-флага к JSON-ключу). Поле живёт рядом с `commands`, не вместо него: LLM
заходит в guide один раз и получает оба контракта.

**Файлы:**
- `src/DocsWalker.Core/Api/UsageGuide.cs` — добавить record `UsageGuideTransactionOp`
  и поле `IReadOnlyList<UsageGuideTransactionOp> TransactionOperations` в `UsageGuideResponse`.
- `src/DocsWalker.Core/Api/IUsageGuideSource` — добавить метод `GetTransactionOperations()`.
- `src/DocsWalker.Cli/UsageGuide/CliUsageGuideSource.cs` — реализовать
  `GetTransactionOperations()`: hardcoded список из 7 op'ов
  (`create-node`, `update-node`, `delete-nodes`, `move-node`, `create-ref`,
  `delete-ref`, `redirect-refs`) с полями {name, json_key, json_type, required, cli_flag, description}.
- `src/DocsWalker.Core/Api/ReadApi.cs.GetUsageGuide` — пробросить
  TransactionOperations в `UsageGuideResponse`.
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — добавить сериализацию `transaction_operations`.

**Тесты:** `tests/DocsWalker.Tests/UsageGuideTransactionOperationsTests.cs` (новый):
- guide содержит 7 операций;
- у `create-node` поля type/title/text/refs;
- у `redirect-refs` поля from_ids/to_id/name/unlink + правильный required для
  unlink ⊕ to_id;
- маппинг CLI-флагов: `from-ids` (CLI) → `from_ids` (JSON).

### 2. Флаг `--no-seen=true` в `get-subtree`

**Проблема:** флаг сейчас есть только у `get-nodes`. Когда LLM хочет вытянуть
полный текст детей раздела через `get-subtree` после ранее сделанного
`get-nodes` — children приходят как `{id, seen:true}`-плейсхолдеры; чтобы
получить полный текст, приходится переспрашивать `get-nodes` пакетом. Лишний
roundtrip без причины.

**Решение:** добавить флаг `--no-seen=true` в `get-subtree`. Поведение
симметрично `get-nodes`: отключает фильтрацию seen-set при сериализации, но
seen всё равно обновляется (узлы помечаются как viewed).

**Файлы:**
- `src/DocsWalker.Cli/Cli/Commands.cs` — добавить `Opt("no-seen", ParamType.String, ...)`
  в spec `get_subtree`.
- `src/DocsWalker.Cli/Program.cs` — `Dispatcher.Run` switch для `get_subtree`
  парсит `no-seen` и передаёт `noSeen` параметр в `ReadHandlers.GetSubtree`.
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs.GetSubtree` — принимает `noSeen`,
  передаёт в `ReadApiJson.SubtreeToJson` (новый параметр или флаг к существующему).
- `src/DocsWalker.Core/Api/ReadApiJson.cs.SubtreeToJson` — пробрасывает `noSeen` в
  логику placeholder/seen.
- Тест в `tests/DocsWalker.Tests/SeenScopeTests.cs` или новом — проверить что
  с `noSeen=true` placeholder не подставляется, но seen обновляется.

### 3. Параметр `--command=<name>` в `get-usage-guide`

**Проблема:** сейчас выдаётся манифест на 26 команд (~5–10K токенов). Если LLM
нужна одна команда — overkill.

**Решение:** добавить optional `--command=<kebab-name>`. Если задан — выдача
содержит только описание этой команды (с примерами). Структура ответа та же
(`commands: [...]`), просто массив длины 1. Невалидное имя → exit 1 с
`unknown_command` (как у других read'ов).

`mental_model` / `trees` / `snapshot` остаются — их размер копеечный, и они
дают контекст для одной команды. Если в будущем понадобится — добавим
`--include=` whitelist; пока YAGNI.

**Файлы:**
- `src/DocsWalker.Cli/Cli/Commands.cs` — `Opt("command", ParamType.String, ...)` в
  spec `get_usage_guide`.
- `src/DocsWalker.Cli/Program.cs` — switch для `get_usage_guide` извлекает
  `params.GetValueOrDefault("command")` и передаёт в handler.
- `src/DocsWalker.Cli/Cli/Handlers/SchemaHandlers.cs.GetUsageGuide` —
  принимает `string? commandFilter`, после построения guide фильтрует
  `Commands` (или возвращает ошибку `unknown_command`).

**Тест:** `tests/DocsWalker.Tests/UsageGuideTests.cs` (или существующий) —
вызов с `--command=get-nodes` возвращает только `get-nodes` в commands, остальные
поля guide на месте.

### 4. Расхождение `get-in-refs` vs `redirect-refs`

**Проблема (по описанию в strategy.md):** `get-in-refs --id=368` возвращает
`{rules:[44]}`, но `redirect-refs --from=368` падает с `no_effect`. Гипотеза:
`get-in-refs` включает computed/path-derived связи, `redirect-refs` работает
только с physical (out_refs нодов).

**Сначала — расследование.** Прежде чем менять API, понять что реально
происходит: какой `id=368` в текущем `docs/`, что в его in-refs (проверить
через CLI), почему redirect-refs возвращает `no_effect`.

Гипотезы для `{rules:[44]}` исхода:
- (a) Граф буквально хранит `out_refs.rules: [368]` у некоторого узла 44 (и тогда
  redirect-refs должен срабатывать — поведение бага).
- (b) `Graph.GetInRefs` возвращает `path` (44.path → 368), и где-то по дороге
  имя `path` переписывается в `rules` (path-child synthesis в `ReadApiJson`?).
- (c) Это узел совершенно другого характера, и описание в strategy.md неточно.

**Действие:**
1. Воспроизвести: получить актуальный repo'шный id, прогнать `get-in-refs` и
   `redirect-refs` через `repl --root=.`. Разобрать ответ.
2. Если (a) — bug-фикс в `redirect-refs`.
3. Если (b) — добавить в выдачу `get-in-refs` секцию `physical:` и `computed:`
   (или добавить `kind` к каждой in-ref в map: `physical|computed`). LLM видит,
   на каких связях можно делать `redirect-refs`, на каких — нет (для computed —
   подсказать `move-node` либо `delete-nodes` источника).
4. Если (c) — обновить strategy.md (описание было неточным), сформулировать
   реальный баг или признать «нет проблемы».

Под-задача может вылиться в одну из трёх веток. Если оказывается, что баг
найден не в коде, а в формулировке strategy.md — фиксим описание; цель шага
закрыта.

## Порядок

1, 2, 3 — независимы; делаются по убыванию простоты: 2 → 3 → 1 → 4.
Под-задача 4 — последняя: возможно, окажется самой простой (правка docs), а
возможно — самой большой (новая семантика in-refs). Не лезем в неё, пока 1–3 не
готовы и закоммичены.

## Пост-проверки

- `dotnet build` + `dotnet test` зелёные после каждой под-задачи.
- `docswalker get-usage-guide --root=.` (через repl или одноразовый CLI) —
  содержит `transaction_operations`.
- `docswalker get-usage-guide --root=. --command=get-nodes` — массив `commands`
  длины 1, все остальные поля присутствуют.
- `docswalker get-subtree --id=42 --no-seen=true` — full text в children, seen
  обновляется (повторный get-nodes без --no-seen → placeholder).
- Под-задача 4 — конкретные пост-проверки определяются по итогу расследования.

## Риски

- **Поверхность get-usage-guide растёт.** `transaction_operations` — заметный
  блок. Mitigation: optional фильтр через `--command` (под-задача 3) уже
  снижает default-нагрузку для targeted-запросов; для guide-целиком +200–500
  токенов на operations — приемлемо за полное снятие класса ошибок «угадать
  формат transaction».
- **Под-задача 4 может вылиться в большое изменение семантики.** Mitigation:
  фиксированный порядок — расследование сначала, развилка по результату; если
  ветка (b) — реализуем минимально (две секции, без перекройки in-refs API).
- **Backward-compat выдачи `get-usage-guide`.** Добавляются новые поля
  (`transaction_operations`, фильтрация commands) — старые читатели должны
  игнорировать неизвестные поля. JSON-конвенция уже это допускает; теста на
  «не сломали старых» нет, ошибок не ожидаю.

## Завершение

После 4 под-задач: `dotnet test`; `git add -A` (без `bash.exe.stackdump`);
коммит `Implement stg-0008 step-08 docs-tooling-fixes`; `git push`. Каждая
git-команда — отдельный Bash-вызов (правило проектного `CLAUDE.md`).
Strategy: `[*] (08)` → `[+] (08)`.
