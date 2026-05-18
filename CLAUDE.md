# CLAUDE.md — DocsWalker

Мета-памятка ассистента.

## Активная сессия

> Временная секция-снапшот для возобновления после сброса контекста.
> Удалить, когда работа по dogfood-конфигу V2 закрыта.

### Активная задача

**Dogfood V2 для Claude Code.** Объявить maps и links для main/usage scope
через `tx scope=scheme`, мигрировать V1-импорт под классификацию
`категория=legacy/v1`, подключить V2 kernel в `.mcp.json` + allow-rules
в `.claude/settings.local.json`, протестировать через Claude Code.

**Статус:** все продуктовые решения по схеме приняты, к реализации не
приступал. Последний шаг сессии — согласование набора maps/links
(см. ниже).

### Согласованные решения

1. **Язык.** Переводим всё на русский (main scope, usage scope, примеры
   в `api/`). **Исключение: meta-schema остаётся английской** —
   kernel-owned, версионируется с релизом DocsWalker. Соответственно
   service-maps scheme scope (`category`/`owner_scope`/`map`/`link_name`)
   тоже **остаются английскими** (они описаны в meta-schema, см.
   `api/scheme-scope.md` раздел «Schema scope-а»).

2. **Объём maps на старте.** Полный набор — 6 main-maps + 8 usage-maps.

3. **Разделитель в именах link.** Без `_`, через `-`. То есть
   `зависит-от`, а не `зависит_от`. Действует на все link-имена в
   data-scope (main, usage, cross-scope). Не действует на service-maps
   scheme и meta-schema (они английские, без правок).

4. **V1-импорт.** Add-then-remove migration. План:
   - tx scope=scheme: объявить полный набор maps, `категория` без
     `required`;
   - tx scope=main: массовая `move`-операция, проставляющая
     `категория=legacy/v1` всем 417 импортированным узлам;
   - tx scope=scheme: поднять `категория` до `required: true`.

### Финальный набор maps

**Main scope (русские имена и ветки):**

- `категория` (required после миграции):
  - `документы/{спека, решение, инвариант, гайд}`
  - `задачи/{бэклог, запланирована, активна, заблокирована, отложена,
    сделана, отменена}`
  - `заметки/{исследование, вопрос, чейнджлог, идея}`
  - `legacy/{v1}`
- `подсистема` (не required): `core, kernel, mcp, cli, tests, spec,
  tooling, infra` (имена проектов C# — английские)
- `статус` (required): `черновик, действует, устарел, архив`
- `адресат` (не required): `llm, человек, оба`
- `строгость` (не required, для документов): `обязательно,
  рекомендуется, опционально`
- `приоритет` (не required, для задач): `критичный, высокий, средний,
  низкий`

**Usage scope (имя map русское, ветки см. ниже — часть остаётся
английской, см. «Открытый вопрос»):**

- `категория` (required) — типы usage-узлов
- `тема` (раньше `subject`)
- `метод` (раньше `method`) — `read, tx` (имена JSON-методов API,
  английские)
- `поле` (раньше `field`) — имена полей JSON-API (английские)
- `код-ошибки` (раньше `error_code`) — все коды из `api/errors.md`
  (английские, потому что это `enum`-значения в коде)
- `имя-схемы` (раньше `schema_name`): `main, usage`
- `имя-map` (раньше `map`) — имена объявленных map (включают и русские
  имена main-scope, и английские usage/service)
- `имя-link` (раньше `link_name`) — имена объявленных link

### Финальный набор links

**Main:**
- `зависит-от` — смысловая зависимость
- `заменяет` — устаревший узел
- `опирается-на` — задача использует документ как контекст
- `реализует` — задача воплощает решение/инвариант
- `связан-с` — мягкая ассоциация
- `упоминается-в` — трассировка обсуждений

**Не заводим `блокирует`** — блокировка видна из `категория=задачи/
заблокирована` + `зависит-от`. Отдельный link дублирует семантику.

**Usage (переводим):**
- `использует-метод` (раньше `uses_method`)
- `использует-поле` (раньше `uses_field`)
- `возвращает-ошибку` (раньше `returns_error`)
- `см-также` (раньше `see_also`)

**Cross-scope usage → main:**
- `описывает` (раньше `describes`)
- `ссылается-на` (раньше `references`)

### Открытые вопросы (не блокирующие — задать в начале следующей сессии)

1. **Переводим ли префикс `usage/` в ветках `категория` usage scope?**
   Сейчас в спеке: `usage/topic`, `usage/method`, `usage/field`,
   `usage/error`, `usage/schema`, `usage/map`, `usage/link`,
   `usage/example`, `usage/rule`. Префикс совпадает с именем scope.
   Если переводить — что-то вроде `справка/тема`, `справка/правило`,
   …, но тогда префикс расходится с именем scope-а (`usage`). Лучший
   вариант пока не очевиден.

2. **Ветки `тема` usage scope.** Сейчас: `read, tx, selector, error,
   schema, workflow, scope`. `read`/`tx` — имена JSON-методов
   (английские), остальное — концепции. Переводить всё (`селектор,
   ошибка, схема, …`) или оставить английскими?

3. **Service-map имена scheme scope.** Сейчас (английские, из
   meta-schema): `category`, `owner_scope`, `map`, `link_name`. По
   текущему решению — не трогаем. Но если хочешь полную
   консистентность — придётся менять meta-schema (kernel-owned), что
   уже инфраструктурная правка. По умолчанию: **не меняем**.

### План следующих шагов (после сброса)

1. Правка `api/usage-scope.md`, `api/scheme-scope.md`, `api/read.md`,
   `api/tx.md` — перевод имён maps/links и веток (с учётом ответов на
   открытые вопросы 1-2).
2. Составить `tx scope=scheme` JSON с объявлением всех maps + links
   (без `required` для `категория`). Применить через `dw exec`.
3. `tx scope=main` — массовая разметка `категория=legacy/v1` для
   импортированных узлов.
4. `tx scope=scheme` — поднять `категория` до `required: true`.
5. `.mcp.json` + `.claude/settings.local.json` allow-rules под V2
   kernel.
6. Smoke: рестарт Claude Code → `read scope=scheme` через MCP видит
   весь объявленный контракт.

### Ключевые предположения

- Кириллица работает в title (`\p{L}` пропускает её), в content,
  именах map/link, ветках — везде. Никаких правок regex не требуется.
- Текущий V1-импорт (417 узлов) считается ценным — сохраняем через
  add-then-remove, не удаляем.
- Pre-condition для шагов 5-6: kernel запущен, БД содержит размеченный
  граф (после шагов 1-4).

> **Статус (2026-05-18): V2 запускается.** Core (Read/Tx executors,
> hist replay, at time-travel, scheme validation), Kernel (HTTP+MCP
> JSON-RPC), Mcp (stdio↔HTTP bridge), Cli (init / exec / repl / health
> / migrate-v1) реализованы и покрыты тестами (192 теста). V1 архив
> остался под `.archive/v1/`; миграция V1 → V2 — через
> `dw migrate-v1`. Раздел «Запуск DocsWalker» ниже описывает V2-runtime.

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
