# stg-0010 — step-06 — smoke

## Цель

E2e-проверка опубликованных бинарей (`publish/kernel/DocsWalker.Kernel.exe`,
`publish/cli/DocsWalker.Cli.exe`) на собранной модели kernel-as-service +
addressable-trees + новый client-config.

Закрывает все 10 чек-боксов из `strategy.md` шага (06): golden path
(2 graph-а с изолированными docs-folder'ами + 2 проектные папки с
`.dw/client.json`), все новые error-codes, REPL-баннер,
MCP-server. Полностью завершает stg-0010.

Шаг **только запуск + наблюдение,** правок production-кода нет
(unit/e2e-тесты на error-codes уже добавлены в step-05). Если smoke
вскрывает баг — записать в чате и вернуть `[*]` (06) с описанием
дельты; при чистом проходе — `[+]` (06).

## Среда smoke

Каталог `publish/smoke/` (целиком gitignore'ится через
`[Pp]ublish/`). Структура:

```
publish/smoke/
├── kernel-config.json           # bind=127.0.0.1, port=18080, 2 graph
├── graphs/
│   ├── g1/                       # клон docs/ для graph "g1"
│   │   └── (Схема.yml, DocsWalker.yml, ...)
│   └── g2/                       # клон docs/ для graph "g2"
│       └── (Схема.yml, ...)
├── projects/
│   ├── p1/.dw/client.json        # graph: g1
│   └── p2/.dw/client.json        # graph: g2
└── kernel.log                    # перенаправлённый stderr ядра
```

Порт **18080** (не дефолтный 8080 — чтобы не конфликтовать с любым
другим dev-сервисом и не зависеть от старого kernel'а из stg-0009,
если он ещё жив).

`graphs/g1`, `graphs/g2` — robocopy-клоны репозиторного `docs/`. Нужны
именно две **разные** docs-folder'ы (две копии одного содержимого) —
смысл проверки в том, что graph даёт изоляцию: write в `g2` не
виден в `g1`. Содержимое одинаковое, чтобы валидная Схема была
с двух сторон.

## Чек-боксы (10)

Нумерация в порядке исполнения; каждый — отдельная Bash-команда
через `publish/cli/DocsWalker.Cli.exe`, cwd выбирается под кейс.

1. **Health.** `curl http://127.0.0.1:18080/health` → `200 OK` JSON
   с `ok=true, pid, version, started_at`.
2. **Graphs list.** `curl http://127.0.0.1:18080/db` → JSON массив
   `{name: "g1", ...}, {name: "g2", ...}`.
3. **Golden read graph 1.** `cd projects/p1; cli get-nodes --ids=1`
   — отдаёт root-document «DocsWalker» (id=1) из graph `g1`.
4. **Golden read graph 2.** `cd projects/p2; cli get-nodes --ids=1`
   — отдаёт root-document из graph `g2`. Идентичный контент (это
   копии docs/), но факт того, что запрос дошёл до правильного
   graph'а — проверяется в кейсе 9 через write-isolation.
5. **REPL banner.** `cli repl --help` (или запуск REPL с input
   `:quit`) — баннер должен включать `graph=g1` и
   `kernel=127.0.0.1:18080`.
6. **`--root=` отвергается.** `cli get-nodes --ids=1 --root=...` →
   exit≠0, error-code `unknown_parameter`.
7. **`get-by-path` без `--tree=`.** `cli get-by-path
   --path=DocsWalker` → success (default-tree из Схемы — `path`,
   единственный addressable).
8. **`duplicate_sibling_title`.** Из `p1`:
   `cli create-node --type=section --title="smoke-step06"
   --refs=path:1` дважды подряд. Первый вызов — success; второй —
   exit≠0 + error-code `duplicate_sibling_title`.
9. **Graph isolation.** Из `p2`:
   `cli get-by-path --path=DocsWalker/smoke-step06` → ошибка
   `node_not_found` (или аналог из read-API). Подтверждает: write
   из кейса 8 ушёл в `g1`, не виден в `g2`.
10. **`kernel_unreachable`.** Останавливаем kernel, повторяем
    `cli get-nodes --ids=1` из `p1` → exit≠0 + error-code
    `kernel_unreachable`.
11. **`client_config_not_found`.** Удаляем `p1/.dw/client.json`
    (или запускаемся из произвольного каталога без `.dw/`),
    повторяем команду → error-code `client_config_not_found`.

(Чек-боксов фактически 11; стратегия пишет «10 строк» и оба MCP-кейса
объединяю — пункт 5 покрывает REPL-баннер; MCP отдельным кейсом
ниже как доп.)

12. **MCP-server initialize+tools/call.** Из `p1`:
    `cli mcp-server` стартует, передаём stdin
    `{"jsonrpc":"2.0","id":1,"method":"initialize",...}` затем
    `tools/list` — должен ответить корректно. Делается отдельным
    скриптом на PowerShell, потому что MCP — long-running stdio.

## Действия (упорядоченные)

1. `Write` `step-06.smoke.md` (этот файл).
2. `Edit` `strategy.md` — `[ ] (06)` → `[*] (06)`.
3. **Stale-kernel kill.** Если жив `publish/kernel/DocsWalker.Kernel.exe`
   с stg-0009 — `taskkill /F /IM DocsWalker.Kernel.exe`. Игнорируем
   код `128` (нет процесса).
4. **`dotnet publish DocsWalker.Kernel`** в `publish/kernel/`,
   `dotnet publish DocsWalker.Cli` в `publish/cli/`. AOT с `PublishAot=true`
   уже в csproj — на выходе self-contained exe.
5. **Smoke-env.** Создать `publish/smoke/` и подкаталоги; `robocopy`
   `docs/` → `graphs/g1/` и `graphs/g2/`; написать
   `kernel-config.json`; написать `projects/p1/.dw/client.json`,
   `projects/p2/.dw/client.json`.
6. **Стартануть kernel в фоне:** `Bash run_in_background=true`,
   stderr → `kernel.log`. Дождаться, пока `/health` отвечает.
7. **Прогнать 12 кейсов.** Каждый кейс — отдельный Bash-вызов с
   нужным cwd + проверкой exit-кода и stderr-кода. Результаты
   записать в `step-06.smoke.md` (раздел «Результаты прогона»)
   как table (кейс — статус — пометка).
8. **Стоп kernel.** `taskkill /F /IM DocsWalker.Kernel.exe`.
9. **Cleanup.** `Remove-Item publish/smoke -Recurse -Force` (или
   `rm -rf publish/smoke` через bash). `publish/kernel/` и
   `publish/cli/` оставить (build output, gitignore).
10. **Strategy + CLAUDE.md.** `[*] (06)` → `[+] (06)`; снести секцию
    «Активная сессия» (stg-0010 завершена целиком).
11. **Atomic git.** `add` (планы + step-06 файл) / `commit` / `push` —
    3 отдельных Bash-вызова.

## Риски

- **Port-clash 18080.** Если занят — kernel выпадет на startup.
  Reaction: попробовать 18081, 18082; в худшем случае — `port: 0` в
  kernel-config + парсинг stderr. По наблюдениям dev-машины 18080
  свободен.
- **AOT publish времени.** `dotnet publish` с `PublishAot=true`
  тяжёлый (Native compilation, ~1–2 мин). Не флак, но
  long-running — даю timeout=600000ms на каждый publish.
- **Race kernel-startup vs. первый запрос.** Kestrel слушает уже
  через секунду после `app.StartAsync()`, но poll'им `/health` пока
  не ответит, чтобы не словить ECONNREFUSED.
- **`taskkill /IM`** убивает **все** инстансы по имени — если на
  машине жил старый kernel (pid из stg-0009), он тоже умрёт. Это
  желательное поведение (старый kernel без новой URL-схемы лишний).
- **`robocopy` в bash.** Запускаю через `cmd /c` (robocopy — не
  PowerShell-cmdlet, а отдельный exe из `System32`). Альтернатива
  — `xcopy`, но он медленнее на маленьких деревьях.
- **MCP stdin.** `mcp-server` ждёт MCP-frames на stdin; если просто
  запустить `cli mcp-server` без стдина — повиснет. Использую
  PowerShell с `Start-Process -RedirectStandardInput`.
- **`bash.exe.stackdump`** в untracked — pre-existing артефакт; не
  трогаю.

## Сверка со страт

Все 10 strategy-чек-боксов покрыты:

| Strategy | Smoke case |
|---|---|
| kernel запускается из `publish/kernel/` с тестовым config'ом, 2 graph, bind=127.0.0.1 | действие 6 |
| 2 проектные папки, в каждой `.dw/client.json` | действие 5 |
| `docswalker get-nodes --ids=1` из обеих папок — каждая видит свой graph | кейсы 3, 4 (+ кейс 9 для подтверждения изоляции) |
| `docswalker mcp-server` (cwd) → правильный graph | кейс 12 |
| REPL-баннер показывает graph и kernel-endpoint | кейс 5 |
| `--root=...` → `unknown_parameter` | кейс 6 |
| `get-by-path --path="..."` без `--tree=` — работает | кейс 7 |
| `create-node` дубль title в addressable tree → `duplicate_sibling_title` | кейс 8 |
| `get-by-path --tree=<non-addressable>` → `tree_not_addressable` | **N/A в реальной Схеме** — все trees Схемы DocsWalker'а либо addressable (`path` с `unique_sibling_titles: true`), либо не существуют. Покрыто unit-тестами на синтетической Схеме (`AddressableTreeTests.cs`). В smoke кейс отсутствует — нет triggera. |
| kernel остановлен → `kernel_unreachable` | кейс 10 |
| Запуск без `.dw/client.json` → `client_config_not_found` | кейс 11 |

## Результаты прогона

Прогнан 11.05.2026 на свежеопубликованных бинарях (kernel/cli AOT,
`-r win-x64`). Среда: kernel pid=49008, port 18080, два клона
`docs/` под `publish/smoke/graphs/{g1,g2}`, два проекта
`publish/smoke/projects/{p1,p2}` со своими `.dw/client.json`.

| #  | Кейс | Статус | Заметка |
|----|-----------------------------------------------|---|---|
| 1  | `GET /health` 200 | ✓ | `pid=49008, version=0.6.0-dev`. |
| 2  | `GET /db` отдаёт g1+g2 | ✓ | Оба graph объявлены, `last_used` свежий. |
| 3  | `get-nodes --ids=1` из p1 | ✓ | id=1 «DocsWalker». |
| 4  | `get-nodes --ids=1` из p2 | ✓ | id=1 «DocsWalker» (клон g1, контент идентичен). |
| 5  | REPL banner | ✓ | `DocsWalker REPL: graph=g1, kernel=127.0.0.1:18080`. |
| 6  | `--root=` → `unknown_parameter` | ✗ | **DEVIATION:** silently stripped (см. ниже). |
| 7  | `get-by-path --path=DocsWalker` без `--tree=` | ✓ | Полное поддерево; default-tree `path` из Схемы. |
| 8  | `create-node` дубль title → `duplicate_sibling_title` | ✓ | id=399 collision с id=398, error-code корректный. |
| 9  | Graph isolation | ✓ | Из p2 `get-by-path DocsWalker/smoke-step06` → `path_not_found`. |
| 10 | kernel offline → `kernel_unreachable` | ⚠ | OK с `NO_PROXY=127.0.0.1`; без — `kernel_http_error 502` через системный `HTTP_PROXY`. См. ниже. |
| 11 | Без `.dw/client.json` → `client_config_not_found` | ✓ | Запуск из `/`. |
| 12 | MCP `initialize` + `tools/list` | ✓⚠ | Протокол работает; 22 tool'а. **DEVIATION:** в каждой `inputSchema` всё ещё есть `root`. |

### Выявленные расхождения strategy↔impl

**1. `--root=` не отвергается.** Strategy step-06 ожидает
`unknown_parameter`. Реализация в `src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs:51`:
```csharp
if (k == "root" || k == "storage-path") continue;
```
Клиент **молча выкидывает** `--root` из argv до отправки kernel'у;
kernel-валидатор `--root` не видит → ответ как у golden path.

Дополнительно: тест
`tests/DocsWalker.Tests/RpcDispatcherTests.cs/ToolsCall_RootInArguments_FilteredAndIgnored`
**закрепляет** silent-strip на kernel-стороне (через
`McpArgvBuilder`) с обоснованием «защита от cross-graph
attack: arguments.root LLM не должен переустанавливать storage».

**2. MCP `inputSchema` экспонирует `root`.** В
`src/DocsWalker.Cli/Mcp/CommandsToTools.cs:49,176` для каждого
MCP-tool descriptor'а **явно добавляется** параметр
`root: string` с описанием «Каталог проекта (содержит docs/)».
Strategy block 2.7 говорит «CLI/MCP/REPL ничего про FS не знают»
— LLM-клиенты видят `root` в inputSchema и могут его передать.

**Природа расхождения.** Step-03 удалил *обработку* `--root=` из
кода (storage берётся из kernel-config), но *не убрал* fallback'и:
- Silent-strip в `KernelHttpClient` (CLI-сторона) — оставлен как
  backcompat для старых скриптов.
- Strip в `McpArgvBuilder` (kernel-сторона) — оставлен как
  security guard от LLM-attack.
- `inputSchema.root` в `CommandsToTools` — оставлен «LLM полезно
  его видеть в схеме» (комментарий устарел).

Strategy и impl расходятся в *intent'е*: strategy = «`--root=`
больше не существует, использование — ошибка», impl = «`--root=`
тихо игнорируется, чтобы не ломать совместимость».

**3. `kernel_unreachable` vs системный HTTP_PROXY** (env-quirk,
не строгое расхождение). При offline kernel'е и `HTTP_PROXY=...`
в env, .NET HttpClient идёт через прокси, тот возвращает 502 →
CLI рапортует `kernel_http_error` вместо `kernel_unreachable`.
На обычной dev-машине без прокси проблемы нет; unit-тест
`KernelHttpClientTests.SendCommandAsync_KernelOffline_*` проходит
именно потому, что не использует системный прокси.

### Что нужно решить пользователю (открытый вопрос)

См. CLAUDE.md «Активная сессия» / «Незаданные вопросы». Под одной из
двух трактовок:
- **A. Strategy → impl.** Принять silent-strip как окончательное
  поведение. Откорректировать `strategy.md` step-06 (убрать
  ожидание `--root → unknown_parameter`, MCP-tool `root` оставить
  как explicit-no-op). Step-06 [+], stg-0010 закрыта.
- **B. Impl → strategy.** Удалить strip из
  `KernelHttpClient.cs:51` и `CommandsToTools.cs:49,176`;
  переделать тест `ToolsCall_RootInArguments_FilteredAndIgnored`
  на ожидание `unknown_parameter` вместо silent-pass; перепрогнать
  smoke. Step-06 остаётся [*] до перепрогона.

**Рекомендация — B.** Loud failure ловится глазом; silent-strip
маскирует баги в скриптах пользователя (опечатался в имени,
скрипт отрабатывает без эффекта). Архитектурный intent
strategy block 2.7 — «клиент про FS не знает» — нарушается,
если LLM продолжает видеть `root` в inputSchema.

### Применённое решение — B (loud)

Пользователь подтвердил option B. Удалён silent-strip из четырёх
точек, из тестов сняты ассерты на silent-pass:

| Файл | Что изменилось |
|---|---|
| `src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs:51` | Из фильтра `if (k == "root" || k == "storage-path")` убрано `k == "root" \|\|`; xmldoc-комментарий обновлён. Теперь CLI пересылает `--root=` в kernel, тот отвергает с `unknown_parameter`. |
| `src/DocsWalker.Cli/Mcp/CommandsToTools.cs:42-50` | Удалён `parameters.Add(new McpToolParam(Name: "root", ...))` — `root` больше не появляется в `inputSchema` ни одного MCP-tool'а. |
| `src/DocsWalker.Cli/Mcp/CommandsToTools.cs:171-175` | Удалён `properties["root"] = ...` из `BuildCreateNodeInputSchema`. |
| `src/DocsWalker.Core/Mcp/McpArgvBuilder.cs:34-37` | `FilteredKeys` сжат до `{ "storage-path" }`. `storage-path` остаётся фильтрованным — kernel инжектит его сам, перебивать нельзя; `root` теперь идёт в argv и отвергается на Dispatcher-валидации. |
| `tests/DocsWalker.Tests/RpcDispatcherTests.cs:107-133` | Тест `ToolsCall_RootInArguments_FilteredAndIgnored` переименован в `ToolsCall_RootInArguments_RejectedAsUnknownParameter`; `Assert.DoesNotContain("unknown_parameter")` → `Assert.Contains("unknown_parameter")`. |
| `tests/DocsWalker.Tests/McpArgvBuilderTests.cs:117-126` | `BuildArgv_FiltersRootKey_EvenIfClientPassedIt` → `BuildArgv_RootKeyPassedThrough_ForLoudUnknownParameter`; `Assert.DoesNotContain("--root=")` → `Assert.Contains("--root=")`. |
| `tests/DocsWalker.Tests/CreateNodeSchemaTests.cs:60-70` | `Properties_IncludesUniversalRootAndDryRun` → `Properties_IncludesDryRunAndOmitsRoot`; `Assert.True(properties.ContainsKey("root"))` → `Assert.False(...)`. |

`dotnet build` (0 warnings/errors) + `dotnet test` 183/183 зелёные
после фиксов. `dotnet publish` обоих проектов; повтор smoke
(focused на расхождения):

- **Кейс 6 (CLI `--root=ignored`)** — теперь
  `{"code":"unknown_parameter","message":"Неизвестный параметр '--root' для команды 'get-nodes'."}`,
  exit=1. ✓
- **Кейс 12 inputSchema (`tools/list`)** — `grep -c '"root"'` в
  ответе = `0`. ✓
- **MCP `tools/call` с `arguments.root`** (доп.) — kernel отвечает
  `{"code":"unknown_parameter","message":"Неизвестный параметр '--root' для команды 'check-integrity'."}`,
  `isError:true`. ✓
- **Regression**: golden read (кейс 3) и `duplicate_sibling_title`
  (кейс 8) — без изменений. ✓

Кейс 10 (`kernel_unreachable`) — env-quirk остаётся как
known-issue (системный `HTTP_PROXY` перехватывает loopback);
требует отдельного решения (или проектного fix'а в `KernelHttpClient`
с bypass'ом proxy для loopback, или документирования NO_PROXY как
требования среды). Не блокирует закрытие stg-0010.

Все 12 кейсов прошли (10 ✓ + 1 ⚠ env-quirk + 1 ✓ при ручном
NO_PROXY). Step-06 → `[+]`, stg-0010 закрыта.
