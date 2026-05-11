# CLAUDE.md — DocsWalker

Мета-памятка ассистента. **Никаких решений по языку, программе, архитектуре,
стеку и синтаксису здесь нет и быть не должно** — они живут только в
[docs/](docs/).

## Единственный источник истины — `docs/`

Вся спецификация (язык, синтаксис, доменная модель, стек, архитектура,
принципы) лежит в [docs/](docs/) и **только в формате YAML**.

Если ответа на вопрос в docs нет — спроси пользователя и дополни
соответствующий YAML, не храни ответ нигде кроме docs.

В этом файле (CLAUDE.md) запрещено дублировать или фиксировать содержимое
docs — даже в виде краткой выжимки. Только ссылка.

## Порядок работы над любой фичей

1. Сначала описание/уточнение в соответствующем YAML в `docs/`.
2. Потом реализация в коде.
3. Поведение поменялось в процессе реализации — **синхронно** правится docs, не
   потом.

Так docs и код не расходятся.

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

## Работа с `docs/` — только через DocsWalker

`docs/` — спецификация в YAML, навигируется графом DocsWalker. **Жёсткое правило:** к `docs/**/*.yml` запрещены `Read`, `Grep`, `Glob`, `Edit`, `Write`, `mcp__glider__get_file_contents` и любой другой прямой доступ к содержимому. Доступ к docs идёт **только** через MCP-tools `mcp__docswalker__*` (или CLI `docswalker <команда>` через `Bash`, если MCP не поднят).

Зачем строго: dogfood — мы используем продукт ровно так, как используют его LLM-агенты у внешних пользователей. Любая лазейка через сырой YAML маскирует пробелы в API DocsWalker и не даёт вылавливать проблемы UX. Если задачу нельзя решить через DocsWalker — это **симптом нехватки команды/параметра**, и она фиксируется как обратная связь о продукте (см. ниже), а не обходится `Read`.

**Чтение через DocsWalker:**
- Узлы по id — `get-nodes`.
- По пути / поддерево / предки — `get-by-path`, `get-subtree`, `get-ancestors`.
- Связи — `get-refs`, `get-in-refs`.
- Структура и схема — `get-map`, `describe-type`, `get-schema`, `get-meta-schema`, `get-usage-guide`.
- Поиск — `search`.

**Запись через DocsWalker:**
- Узлы — `create-node`, `update-node`, `delete-nodes`, `move-node`.
- Связи — `create-ref`, `delete-ref`, `redirect-refs`.
- Атомарные пакеты правок — `transaction`.

**Узкие исключения, когда сырой файл всё-таки можно читать/писать (только эти, ничего больше):**
- `docs/.docswalker/meta-schema.yml`, `docs/.docswalker/sequence.txt` — служебные файлы, DocsWalker их использует, но не редактирует через API; ручная правка штатна.
- DocsWalker сломан и не загружает граф (диагностика самого загрузчика — единственный путь увидеть сырой YAML, которого ядро ещё не «понимает»). При этом нужно сначала убедиться, что kernel вообще не поднимается (см. секцию «Запуск DocsWalker»), и сообщить пользователю, что переходим в диагностический режим.

Все остальные сценарии — через DocsWalker. Если соблазн «гляну в YAML быстрее» — это сигнал, что нужно либо поднять DocsWalker, либо подсветить недостающую команду как обратную связь.

## Запуск DocsWalker

Перед сессией с `docs/` нужно убедиться, что DocsWalker MCP-tools доступны. Это значит: kernel живой, `.dw/client.json` валидный, `.mcp.json` подцепился Claude Code'ом.

### Один раз — собрать бинари

AOT-публикация даёт self-contained `.exe`'шники под Windows. Делается один раз; повторять при изменениях в `src/` соответствующего проекта.

```powershell
dotnet publish src/DocsWalker.Kernel/DocsWalker.Kernel.csproj -c Release -r win-x64
dotnet publish src/DocsWalker.Cli/DocsWalker.Cli.csproj       -c Release -r win-x64
dotnet publish src/DocsWalker.Mcp/DocsWalker.Mcp.csproj       -c Release -r win-x64
```

После — на диске:
- `src\DocsWalker.Kernel\bin\Release\net10.0\win-x64\publish\DocsWalker.Kernel.exe` — HTTP-сервис (управляющий процесс).
- `src\DocsWalker.Cli\bin\Release\net10.0\win-x64\publish\DocsWalker.Cli.exe` — клиент-режим (форвард к kernel'у) + REPL.
- `src\DocsWalker.Mcp\bin\Release\net10.0\win-x64\publish\DocsWalker.Mcp.exe` — stdio↔HTTP bridge для MCP-клиента (Claude Code и т.п.).

**Пересборка при изменениях в `src/`.** При правках в `src/DocsWalker.Core/`,
`src/DocsWalker.Kernel/`, `src/DocsWalker.Cli/` нужна повторная публикация Kernel и
Cli (Core линкуется внутрь обоих). `DocsWalker.Mcp.exe` — тонкий stdio↔HTTP-bridge
без бизнес-логики; его публикация повторяется **только** при правках в самом
`src/DocsWalker.Mcp/`. Дополнительная причина не трогать Mcp без необходимости:
активная MCP-сессия Claude Code удерживает `DocsWalker.Mcp.exe` запущенным, и
`dotnet publish` падает с file-lock (MSB3027) до завершения сессии.

### Один раз — конфиги

**`kernel-config.json`** в корне проекта (gitignore'ить отдельно, если хочешь — он dev-локальный):

```json
{
  "bind": "127.0.0.1",
  "port": 18080,
  "graphs": {
    "docswalker": "D:/Dev/cs/projects/DocsWalker/docs"
  },
  "graph_idle_timeout": "10m"
}
```

- `bind` — `127.0.0.1` (local-only).
- `port` — фиксированный (клиенту в `.dw/client.json` нужен тот же).
- `graphs` — словарь `graph_name → storage_path`. `storage_path` указывает на папку `docs/`, не на корень репо.

**`.dw/client.json`** в корне проекта:

```json
{
  "kernel": {
    "host": "127.0.0.1",
    "port": 18080
  },
  "graph": "docswalker"
}
```

`graph` обязан совпадать с ключом из `graphs` в `kernel-config.json`.

### Запуск kernel

Из корня проекта в фоновом окне (kernel — windows-subsystem exe, без stdout у parent'а):

```powershell
Start-Process -FilePath "src\DocsWalker.Kernel\bin\Release\net10.0\win-x64\publish\DocsWalker.Kernel.exe" `
  -ArgumentList "--config=kernel-config.json" `
  -RedirectStandardError "kernel.log"
```

Sanity-check (важно: на машинах с системным `HTTP_PROXY` proxy перехватывает loopback и вернёт 502 — выставь `NO_PROXY="127.0.0.1,localhost"` для своего терминала):

```powershell
curl http://127.0.0.1:18080/health
# {"ok":true,"pid":...,"version":"0.6.0-dev","started_at":"..."}
```

### MCP-интеграция с Claude Code

`.mcp.json` в корне проекта уже настроен — он зовёт `DocsWalker.Mcp.exe --quiet=true`, который читает `.dw/client.json` поиском вверх от cwd и форвардит JSON-RPC frames к kernel'у. Никаких других args быть не должно (Mcp.exe понимает только `--quiet=true|false`; конфигурация транспорта целиком в `.dw/client.json`).

С stg-0011 (code-mcp-project-split) MCP-сервер вынесен в отдельный exe `DocsWalker.Mcp.exe` — раньше тот же мост жил как `DocsWalker.Cli.exe mcp-server`. Если в `.mcp.json` остаётся старая команда — обновить путь и убрать аргумент `mcp-server`.

Чтобы Claude Code увидел tool'ы — открыть проект из корня (где лежит `.mcp.json`) и перезапустить сессию. После этого должны появиться tool'ы `mcp__docswalker__get-nodes`, `mcp__docswalker__get-map` и так далее.

### Остановка kernel

```powershell
Stop-Process -Name DocsWalker.Kernel -Force
```

### Что делать, если что-то не работает

- `client_config_not_found` от CLI — нет `.dw/client.json` на пути от cwd вверх.
- `unknown_graph` от kernel'а — `graph` в `.dw/client.json` не совпадает с ключом в `kernel-config.json`.
- `kernel_unreachable` — kernel не поднят, либо порт/host в client.json не совпадает с kernel-config.
- `kernel_http_error 502` на запросе к `127.0.0.1` — почти наверняка системный `HTTP_PROXY` перехватывает loopback, см. выше про `NO_PROXY`.
- MCP-tool'ы не появились в Claude Code — посмотреть `kernel.log` и stderr mcp-server'а; типовая причина — устаревший `.mcp.json` с args-флагами, которых уже нет в команде.

## Обратная связь о DocsWalker

DocsWalker — это продукт, который мы и разрабатываем, и используем (dogfooding). Когда при работе через DocsWalker (CLI или MCP) встречаешь любое неудобство — **сразу говори в чате**, не умалчивай и не обходи костылём. К этому относится:

- Не хватает команды или параметра под нужный кейс.
- Ответ слишком шумный (лишние поля) или слишком скудный (приходится делать N запросов).
- Сообщение об ошибке непонятное / без подсказки, что делать.
- Неожиданное поведение: упало, зависло, ответило не то, что описано в docs.
- Транспорт MCP неудобен: handshake, форматы, контекст сессии, длина ответа.
- Расхождение между поведением и описанием в `docs/`.

Любая из этих ситуаций — отдельной заметкой в чате, прямо в момент столкновения. Решение (фикс / правка docs / добавление параметра) — отдельным шагом, после согласования.

## Правила взаимодействия (выжимка из глобального CLAUDE.md)

- Противоречие или пробел в инструкции/доках — **сразу уведомить пользователя**,
  не выбирать удобную трактовку молча.
- Развилка между «архитектурно честным» и «костылём» — по умолчанию
  предлагать честный вариант. При значимой цене — вынести trade-off и
  спросить.
- Максимум 3 блокирующих вопроса за раунд.
- Идеи и улучшения — в конце ответа, реализация только после подтверждения.
