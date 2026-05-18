# CLAUDE.md — DocsWalker

Мета-памятка ассистента.

## Активная сессия

> Снапшот перед рестартом Claude Code для подключения MCP server
> `docswalker`. Удалить, когда smoke через `mcp__docswalker__*` пройдёт.

**Активная задача.** Проверить, что после рестарта Claude Code MCP server
`docswalker` подцепился из `.mcp.json` и инструменты
`mcp__docswalker__read` / `mcp__docswalker__tx` работают.

**Checkpoint.** Bootstrap V2 dogfood-config полностью завершён и
запушен (commit `9a48bdc`). БД, scheme, kernel, MCP wrapper — все
готовы. Осталось только smoke-test через MCP-инструменты.

**Следующий шаг после рестарта:** позвать `mcp__docswalker__read` со
`scope=scheme` (пустой селектор) — ожидается 28 узлов: 2 root + 14 maps
(6 main + 8 usage) + 12 links (6 main + 6 usage).

**Ключевые предположения:**

- Kernel запущен в фоне на `127.0.0.1:18080`, pid 18844. Если процесс
  выжил рестарт — `curl http://127.0.0.1:18080/health` отдаст `ok`.
  Если упал — `bash scripts/start-kernel.sh`.
- БД: `.dw/docswalker.sqlite`, graph `docswalker`. 417 узлов в main с
  `категория=legacy/v1` + 28 в scheme.

**Открытое (не блокирующее):** `статус` map оставлен `required=false`.
Поднять можно после фактической пере-классификации legacy-узлов в их
реальные категории (`документы/*`, `задачи/*`, `заметки/*`).

> **Статус (2026-05-18): V2 dogfood-config запущен.** Core (Read/Tx
> executors, hist replay, at time-travel, scheme validation), Kernel
> (HTTP+MCP JSON-RPC), Mcp (stdio↔HTTP bridge), Cli (init / exec / repl /
> health / migrate-v1) реализованы и покрыты тестами. БД
> `.dw/docswalker.sqlite` содержит 417 импортированных узлов V1 с
> разметкой `категория=legacy/v1` (миграция через `dw migrate-v1` + один
> tx scope=main) и полный декларированный контракт V2 в scheme scope:
> 6 main-maps + 8 usage-maps + 6 main-links + 6 usage-links (4 within +
> 2 cross-scope). Main `категория` required=true; `статус` остаётся
> non-required до фактической пере-классификации legacy-узлов. Скрипты
> bootstrap-а (Python + JSON tx) лежат в `.planning/` для исторической
> справки. MCP server `docswalker` объявлен в `.mcp.json`; рестарт
> Claude Code подключает его автоматически.

## Единственный источник истины — `api/` и `database-model/`

Спецификация V2 живёт в двух markdown-каталогах:

- [`api/`](api/) — LLM-facing JSON API (два MCP tool'а `read` и `tx`,
  селекторы, 4-scope модель, hist event-log, коды ошибок).
- [`database-model/`](database-model/) — физический слой (SQLite-DDL,
  индексы, алгоритмы replay/rollback, маппинг селекторов в SQL).

`api/` описывает поведение, `database-model/` — реализацию. Это markdown,
не граф — обычные `Read`, `Edit`, `Write`, `Grep`, `Glob` разрешены.

Если ответа на вопрос нет в `api/` или `database-model/` — спроси
пользователя и допиши в соответствующий файл. Не храни решения нигде
кроме этих двух каталогов.

В этом файле (CLAUDE.md) запрещено дублировать содержимое `api/` или
`database-model/` — даже краткой выжимкой. Только ссылка.

Старый источник истины (YAML-граф V1 в `docs/`) заархивирован в
`.archive/v1/docs/` и служит референсом для разовой миграции в
SQLite-storage V2.

## Порядок работы над любой фичей

1. Сначала описание/уточнение в соответствующем `.md` в `api/` или
   `database-model/`.
2. Потом реализация в коде.
3. Поведение поменялось в процессе реализации — **синхронно** правится
   спека, не потом.

Так спека и код не расходятся.

## Язык общения и кода

- Диалог с пользователем, описания, комментарии в коде, тексты в docs — **русский**.
- Имена идентификаторов в коде — **английские** (стандартная конвенция).
- Доменные термины — как в docs, без перевода.

## Автокоммит и автопуш

Полная автономия по git: при завершении пачки шагов `/planning` (все шаги пачки в `[+]`/`[-]`) Claude **сам**:

1. Стейджит изменения по проекту (`git add -A`).
2. Создаёт коммит с однострочным message (English, imperative) — описывает суть пачки.
3. Пушит в `origin/master`.

Подтверждение пользователя перед коммитом и пушем не требуется. Hash коммита в чат не выписывается.

Исключения, при которых нужно остановиться и спросить:
- В индексе оказались файлы с признаками секретов (`.env`, `*.key`, `credentials*`, `*token*` в значениях) — диагностировать и спросить, прежде чем коммитить.
- `git push` упал не из-за устаревшей ветки (например, защищённый push, конфликт прав) — не делать `--force`, остановиться и сообщить.
- Конфликт слияния при попытке push после pull — не разрешать молча, дать пользователю выбрать стратегию.

## Инструменты работы с C#-кодом

**Glider** (MCP-сервер на Roslyn) — единственный канал для навигации и анализа C#-кода. Всё, что относится к структуре, символам и семантике C#, делается через `mcp__glider__*`. Текстовый поиск и прямое чтение `.cs` — только в исключениях, перечисленных ниже.

**В начале сессии** один раз вызвать `mcp__glider__load` с путём к `DocsWalker.slnx`. Без этого семантические инструменты не работают.

**Навигация и поиск (только Glider):**
- Поиск кода/символа — `mcp__glider__find_code`, `search_symbols`.
- Ссылки/реализации/наследники/иерархия — `find_references`, `find_implementations`, `find_overrides`, `get_type_hierarchy`, `get_derived_types`.
- Caller/callee — `find_callers`, `get_outgoing_calls`.
- Структура/outline файла — `get_structure`.
- Диагностики компилятора — `get_diagnostics`.

**Чтение C#-файлов (только Glider):**
- Целиком — `mcp__glider__get_file_contents`.
- Точечно — `get_method_source`, `get_type_source`.
- `Read` на `.cs` запрещён, кроме случая, когда Glider не смог открыть workspace или файл вне загруженного solution.

**Правки C#:**
- Семантические рефакторинги — `mcp__glider__rename_symbol`, `move_member`, `move_type`, `organize_usings`, `format_document`.
- Локальные правки строк — `Edit` допустим **после** того, как место найдено через Glider.
- Создание новых `.cs`-файлов — `Write`, сразу после — `mcp__glider__sync` или `reload`, чтобы Roslyn увидел файл.

**Текстовый поиск (`Grep`) по `.cs`:** только для строковых литералов и комментариев, которых нет в symbol table. Для поиска кода — Glider, не Grep.

**Не-C# файлы** (`.yml`, `.csproj`, `.md`, `.json`, `.txt`): `Read`/`Write`/`Edit` свободно.

## Работа с данными графа

V2 хранит граф в SQLite-файле; LLM ходит туда через два инструмента
`read` и `tx` (см. [`api/`](api/)). Прямая правка SQLite или его дампа
запрещена так же, как в V1 запрещалась правка YAML — целостность
держит kernel.

Каналы:

- **MCP-клиент → `DocsWalker.Mcp.exe` → kernel.** Production-путь для
  Claude Code, Codex и других MCP-агентов. Wrapper читает
  `.dw/client.json` (host/port kernel-а + имя графа), форвардит
  JSON-RPC через `POST /{graph}` kernel-а.
- **`dw exec` / `dw repl`.** Diagnostic-инструмент, открывает SQLite
  напрямую без kernel-а — для smoke-тестов, разовых правок, repro в
  shell-скриптах.

V1 YAML-граф остался в `.archive/v1/docs/` как историческая референс-копия.
Импортируется в V2 одноразово через `dw migrate-v1` (см. ниже).

## Запуск DocsWalker

### Сборка и публикация

```powershell
dotnet build DocsWalker.slnx
dotnet test  DocsWalker.slnx --no-restore
dotnet publish src/DocsWalker.Kernel -c Release -r win-x64
dotnet publish src/DocsWalker.Mcp    -c Release -r win-x64
# CLI можно гонять через `dotnet run` — публикация необязательна.
```

### Создание и заполнение БД

```powershell
# Пустая БД (создаётся при первом обращении, граф регистрируется).
dotnet run --project src/DocsWalker.Cli -- init .dw/docswalker.sqlite docswalker

# Импорт V1 YAML-графа (один раз; падает, если main scope уже не пустой).
dotnet run --project src/DocsWalker.Cli -- migrate-v1 `
    .archive/v1/docs .dw/docswalker.sqlite docswalker
```

### Запуск kernel (HTTP+MCP transport)

Конфигурация — `kernel-config.json` (bind/port/db_path/graphs[]).
Старт в фоне через `scripts/start-kernel.sh` (Start-Process в
скрытом окне, stderr → `kernel.log`, stdout → `kernel.stdout.log`).
Остановка — `scripts/stop-kernel.sh` (taskkill) или
`POST /<graph>` с `{"jsonrpc":"2.0","id":1,"method":"shutdown"}`.

Проверка здоровья:

```powershell
curl.exe http://127.0.0.1:18080/health
# или
dotnet run --project src/DocsWalker.Cli -- health
```

### MCP wrapper (для LLM-агентов)

`scripts/docswalker-mcp.ps1` запускает `DocsWalker.Mcp.exe` из
publish/. MCP-клиент конфигурируется как:

```
"docswalker": {
  "command": "powershell.exe",
  "args": ["-NoProfile", "-ExecutionPolicy", "Bypass",
           "-File", "scripts\\docswalker-mcp.ps1"]
}
```

Wrapper читает `.dw/client.json` (host/port + graph) поиском вверх
от cwd.

### Smoke

```powershell
# tools/list через прямой HTTP
curl.exe -X POST http://127.0.0.1:18080/docswalker `
  -H "Content-Type: application/json" `
  -d '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}'

# tools/call read через MCP wrapper
echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"ops":[{"select":{"selector":{"path":"DocsWalker"}}}]}}}' `
  | dotnet run --project src/DocsWalker.Mcp -- --quiet=true
```

## Обратная связь о DocsWalker

DocsWalker — это продукт, который мы и разрабатываем, и используем (dogfooding). Когда при работе через DocsWalker (CLI или MCP) встречаешь любое неудобство — **сразу говори в чате**, не умалчивай и не обходи костылём. К этому относится:

- Не хватает команды или параметра под нужный кейс.
- Ответ слишком шумный (лишние поля) или слишком скудный (приходится делать N запросов).
- Сообщение об ошибке непонятное / без подсказки, что делать.
- Неожиданное поведение: упало, зависло, ответило не то, что описано в docs.
- Транспорт MCP неудобен: handshake, форматы, контекст сессии, длина ответа.
- Расхождение между поведением и спецификацией в `api/` /
  `database-model/`.

Любая из этих ситуаций — отдельной заметкой в чате, прямо в момент столкновения. Решение (фикс / правка спеки / добавление параметра) — отдельным шагом, после согласования.

## Правила взаимодействия (выжимка из глобального CLAUDE.md)

- Противоречие или пробел в инструкции/доках — **сразу уведомить пользователя**,
  не выбирать удобную трактовку молча.
- Развилка между «архитектурно честным» и «костылём» — по умолчанию
  предлагать честный вариант. При значимой цене — вынести trade-off и
  спросить.
- Максимум 3 блокирующих вопроса за раунд.
- Идеи и улучшения — в конце ответа, реализация только после подтверждения.
