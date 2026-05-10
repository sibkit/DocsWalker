# stg-0009 — Kill Seen

**Статус:** выполнена

## Задача

Полностью удалить server-side dedup-механизм (`seen-set` + `session_id`-протокол + persistence сессий + `--no-seen` флаг + reset на guide + write-invalidation сессий), вернуть read-команды к простой модели «всегда полный узел». Для дешёвого обзора (вместо placeholder'ов на повторах) — эксплицитный `fields=[title]` + `depth` + `tree`, которые в API уже есть.

Триггер: текущая модель `seen` построена на ложном допущении «сервер отправил клиенту = у LLM есть в контексте». Допущение не выполняется в большинстве реальных сценариев — `/compact`, tool-result truncation, длинный диалог, сабагент с делёным `session_id`. Результат — false-negative placeholder'ы, которые LLM трактует как «уже видела» и галлюцинирует ответ. Цена удаления — небольшой рост токенов на повторах в одной выдаче (на текущей Схеме auto-include активен только для `rule.examples` (#341), повторов мало). Выигрыш — резкое упрощение архитектуры и гарантированная честность ответов.

## Принятые решения

### 1. `session_id` убирается из протокола целиком

Из `RequestFrame`/`RequestContext`, из CLI (`--session-id=` флаг, `CLAUDE_CODE_SESSION_ID` env), из MCP-handshake. Узлы `docs/DocsWalker.yml` #342 (Источники session_id), #370 (session_id из MCP-handshake) — удаляются. Аргумент: после удаления seen-механизма session_id никем не используется. Хранить мёртвый параметр «на случай audit/rate-limit» — это и есть тот mess, от которого избавляемся. Если потребуется — добавим обратно с конкретной мотивацией.

### 2. `--no-seen=true` → жёсткий `unknown_parameter`

Не deprecated, не silently ignored — отвергается как любой левый ключ. Аргумент: DocsWalker в активной разработке, нет внешних установок, на которые мы можем сломаться. Скрипты с `--no-seen` — в худшем случае наши же. Чисто, без legacy-кладбища.

### 3. Session-files на диске не трогаем

Папка `<root>/docs/.docswalker/sessions/<uuid>.yml` остаётся как есть после апгрейда — пользователь сам уберёт, если хочет. Сервер новой версии **никогда** туда не пишет и не читает; код, который её обслуживал, удаляется. Аргумент: это **временные данные** под старую модель; делать миграционный шаг ради их удаления — лишняя ответственность сервера. Имя папки очевидное, ручная зачистка — одна команда.

### 4. Что остаётся целым

- **Auto-include (#363)** — отдельный механизм (транзитивная подтяжка `tree==null && required==true` связей), не зависит от seen. Не трогаем.
- **MCP-сервер** — остаётся как stdio↔HTTP wrapper (stg-0008). Просто перестаёт нести `session_id` в payload `tools/call`.
- **`fields`, `tree`, `depth`** в `get-subtree` / `get-by-path` / `get-nodes` — главные рычаги дешёвого обзора. Это и были архитектурно правильным способом; seen был лишним слоем магии сверху.
- **`get-map`, `search`, `describe-type`** — навигация без полного текста. Не трогаем.
- **Все write-команды** — упрощаются (нет hook'а на write-invalidation seen-set), но семантика не меняется.

### 5. Замена в usage-guide

`get-usage-guide` явно прописывает: «повторы узлов в одном ответе и между запросами всегда полные; для дешёвого обзора используй `fields=[title]` + ограниченный `depth`/`tree`». LLM получает эксплицитный контракт вместо скрытой магии.

## Шаги

- [+] (01) spec-rewrite
- [+] (02) code-removal
- [+] (03) tests-cleanup
- [+] (04) smoke

## Итоговый порядок выполнения

1. **spec-rewrite** — переписать `docs/DocsWalker.yml`. Удалить узлы (через DocsWalker `transaction` — обращаемся к docs только через продукт, не прямым YAML-edit, см. проектный `CLAUDE.md`):
   - В разделе #338 удалить: rules #342 (Источники session_id), #344 (Placeholder для seen), #346 (Прямые id всегда полные), #348 (Reset на guide), #350 (--no-seen), #352 (Persistence сессий), #354 (TTL=7d), #358 (Write-invalidation); definitions #360 (session_id), #361 (seen-set), #362 (placeholder); examples #343/#345/#347/#349/#351/#353/#355/#359.
   - В API-секции удалить: rule #373 (Write-invalidation, дублирует #358) с примером #374; rule #370 (session_id из MCP-handshake) с примером #371.
   - **Переименовать** #338 «Контекст-aware-выдача» → «Auto-include» (после удаления остаётся только rule #340 + definition #363 + example #341).
   - **Переписать** #339 (Цель раздела): «Auto-include автоматически подтягивает концептуально-неотъемлемые связи (`tree==null && required==true`). Для дешёвого обзора без auto-include — `fields=[title]` + `depth`/`tree` в read-командах.»
   - **Править** #340 (Auto-include text): убрать «Подтянутые узлы проходят через тот же seen-фильтр, что и children»; «массив узлов или placeholder ов» → «массив узлов».
   - **Править** #330 (Кадры запроса/ответа): убрать `"session_id":"3f1a-..."` из JSON-примера запроса.
   - Замечания о session-id в описаниях команд (`get-nodes`, `get-subtree`, `get-by-path`) — точные узлы определятся при инвентаризации `search session_id`.
   - В описаниях read-команд явно сказать: «повторы — всегда полные; для дешёвого обзора — `fields=[title]` + `depth`/`tree`».
   - Проверить, что #363 (auto-include) и #341 остаются нетронутыми (это другой механизм).
   Шаг не трогает код. Если CLI к kernel недоступен (kernel требует свой отдельный exe в Debug-сборке) — публикуем `dotnet publish` минимально и используем вышедшие бинари; в крайнем случае прямой `Edit` на YAML (с явной отметкой что это фоллбэк). Финал шага — `transaction`-payload для git diff.

2. **code-removal** — удаление кода **атомарным коммитом** (пока seen хоть где-то жив, всё связано — частичная работоспособность невозможна):
   - **Удалить файлы целиком**: `src/DocsWalker.Core/Api/SeenScope.cs`, `src/DocsWalker.Core/Sessions/SessionState.cs`, `src/DocsWalker.Core/Sessions/SessionFile.cs`, `src/DocsWalker.Cli/Cli/SessionId.cs`. Если папка `src/DocsWalker.Core/Sessions/` пуста после — удалить и её.
   - **`src/DocsWalker.Core/Server/RequestContext.cs`** — определить судьбу: если кроме `SessionId`/`Sessions` ничего не несёт, удалить целиком; если есть другие свойства — оставить, убрать только два упомянутых (нужно убедиться чтением).
   - **`src/DocsWalker.Kernel/RpcDispatcher.cs`** — удалить `TryExtractSessionId`, генерацию UUID на отсутствующий session_id, `RequestContext.Push(...)` если он больше не нужен. Method не должен принимать/использовать session_id.
   - **`src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs`** — убрать поле `session_id` из `arguments` payload, убрать `SessionId.Resolve(argv)` вызов.
   - **CLI handlers**: `ReadHandlers.cs` (параметр `noSeen`, передача в JSON-сериализацию), `WriteHandlers.cs` (write-invalidation хуки), `SchemaHandlers.cs` (reset seen на guide), `ReplHandler.cs` (TTY UUID-генерация), `McpWrapperHandler.cs` (session_id в handshake/`tools/call`).
   - **`src/DocsWalker.Cli/Cli/Commands.cs`** — параметры `--no-seen`, `--session-id` в реестре команд → убрать. После удаления параметр станет автоматически unknown_parameter (см. решение #2).
   - **`src/DocsWalker.Cli/Program.cs`** — убрать резолв session_id, передачу его в kernel client.
   - **`src/DocsWalker.Core/Api/ReadApiJson.cs`** — убрать placeholder-сериализацию (`{"id":N,"seen":true}`), убрать параметры `seen` и `noSeen` из публичных методов. Узлы всегда сериализуются полностью.
   - **`src/DocsWalker.Core/Api/WriteApi.cs`, `WriteState.cs`** — убрать вызовы invalidate seen-set после write.
   - **`src/DocsWalker.Cli/UsageGuide/UsageGuideText.cs`** — переписать раздел про context-awareness: убрать упоминания seen/placeholder/--no-seen/session_id/reset. Добавить параграф про `fields=[title]` + `depth`/`tree` как способ дешёвого обзора.
   - **Сборка**: `dotnet build` зелёная, никаких unused using/symbol warnings.

3. **tests-cleanup** — после code-removal в тестах остаются классы, ссылающиеся на удалённые типы — компиляция упадёт:
   - **Удалить целиком**: `tests/DocsWalker.Tests/SeenScopeTests.cs`, `tests/DocsWalker.Tests/SessionsInfrastructureTests.cs`, `tests/DocsWalker.Tests/WriteInvalidationTests.cs`.
   - **Поправить**: `tests/DocsWalker.Tests/AutoIncludeTests.cs` (отрезать seen-сценарии, оставить чисто auto-include), `tests/DocsWalker.Tests/McpArgvBuilderTests.cs` (убрать ожидание session_id в payload).
   - Полная прогонка: `dotnet test` — должно остаться меньше тестов, все зелёные.

4. **smoke** — `dotnet publish` обоих exe (Cli + Kernel), e2e-прогон на собственных `docs/`:
   - `docswalker get-nodes --root=. --ids=1` — полный узел в payload.
   - `docswalker get-subtree --root=. --id=1 --fields=title --depth=3` — дешёвый обзор работает.
   - Повтор `get-nodes --ids=1` через секунду — снова полный узел (никаких placeholder'ов).
   - `docswalker get-nodes --root=. --ids=1 --no-seen=true` — `unknown_parameter` (по решению #2).
   - `docswalker get-nodes --root=. --ids=1 --session-id=test-uuid` — `unknown_parameter`.
   - `docswalker get-usage-guide --root=.` — текст без упоминаний seen/session_id, есть упоминание `fields=[title]` (этот post-check перенесён сюда из step-01: текст guide хардкоден в `UsageGuideText.cs`, правится в step-02, e2e-проверка корректна только здесь).
   - MCP-wrapper: `docswalker mcp-server --root=.` — `tools/call` без поля `session_id` в payload, форвард работает.
   - REPL: `docswalker repl --root=.` — отвечает на команды, нет UUID-генерации на старте.

## Ссылки на step-файлы

- `step-01.spec-rewrite.md` — детали правок `docs/DocsWalker.yml`.
- `step-02.code-removal.md` — создаётся перед началом step-02.
- `step-03.tests-cleanup.md` — создаётся перед началом step-03.
- `step-04.smoke.md` — создаётся перед началом step-04.

## Точка возобновления (для новой сессии после сброса контекста)

Стратегия — удалить server-side dedup-механизм seen целиком. Включает: `SeenScope`/`SessionState`/`SessionFile`/`SessionId.cs` файлы (удалить), `--no-seen` параметр (станет unknown), `session_id` поле в request payload (убрать из всех клиентов и kernel), session-files в `<root>/docs/.docswalker/sessions/` (на диске не трогать, но код для них убрать), узлы `docs/DocsWalker.yml` #342, #344-#362, #370, #371, #374 (удалить через DocsWalker `transaction`). Что остаётся: auto-include (#363), MCP-сервер (без session_id), `fields`/`depth`/`tree` параметры, все read/write команды.

Старт новой сессии:

1. Прочитать проектный `CLAUDE.md` — общие правила (`docs/` через DocsWalker, atomic git, Glider для C#-навигации, автокоммит после `[+]`).
2. Прочитать этот `strategy.md` — особенно «Принятые решения».
3. Прочитать первый `[*]`-step и работать строго по нему. Step-файлы создаются перед началом каждого шага (кроме step-01, который существует с самого начала).
4. После завершения каждого шага — `[*] → [+]`, atomic git add/commit/push.

Что **не** надо пересматривать:

- session_id убирается **целиком**, не remains as opaque-marker. Окончательно.
- `--no-seen` → жёсткий `unknown_parameter`, не deprecated. Окончательно.
- Session-files на диске не трогаем — ни миграции, ни автоудаления. Окончательно.
- Auto-include (#363), MCP-server, `fields`/`depth`/`tree` — **остаются**. Окончательно.
- Атомарность code-removal: один шаг = один коммит, частичная работоспособность не предусмотрена. Окончательно.

Реализация пачки шагов: первым tool-call в `strategy.md` пометить шаг `[*] (NN)`; по завершению — `[*] → [+]` плюс атомарный git add/commit/push.

## Замечание по нумерации

В `stg-0008/strategy.md` упоминается зарезервированная стадия `stg-0009.storage-format-json`. Эта kill-seen-страта **выходит вперёд** по приоритету (упрощает API → последующий переход на JSON будет дешевле). JSON-storage переезжает на следующий свободный номер (вероятно `stg-0010.storage-format-json`); упоминание в stg-0008 устаревает, но переписывать завершённую страту не будем — это исторический snapshot решений на момент работы.
