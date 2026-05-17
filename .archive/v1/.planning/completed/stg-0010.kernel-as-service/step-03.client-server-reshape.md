# stg-0010 — step-03 — client-server-reshape

## Цель

Атомарно перестроить весь client-server контракт под новую модель
процесса. До шага: per-user kernel + auto-spawn + `arguments.root` в
каждом RPC. После шага: standalone kernel-сервис c JSON-config'ом и
named graphs в URL; CLI/MCP/REPL знают только endpoint и graph-name из
`.dw/client.json`; никакой FS-логики на клиентской стороне; никакого
`--root=` в API.

Шаг **атомарный**: kernel-side и client-side переходят на новый URL и
протокол одновременно. Промежуточно делить нельзя — e2e ломается.
`dotnet build` + `dotnet test` могут быть красными между подшагами,
но **должны быть зелёными после полного завершения step-а** (как в
stg-0009 step-02).

После step-03 типы `KernelInfoFile`, `KernelDiscovery`, `KernelLock`,
`KernelClient`, `KernelSpawner`, `StalePidDetector` становятся
unreferenced (auto-spawn / per-user discovery больше не используются).
Их удаляет **step-04** (kill-auto-spawn) — это позволяет step-03
пройти ревью без шума «удалили половину client/Kernel/».

## Принятые решения внутри step-а

1. **Канал kernel→Dispatcher: argv injection (Вариант A).** Kernel
   подмешивает `--storage-path=<docs-folder>` в argv перед вызовом
   `_dispatcher(argv)`. Dispatcher.Run читает значение через
   `TryResolveStoragePath`, передаёт handlers'ам как `storagePath`.
   Handlers больше не делают `Path.Combine(root, "docs", ...)` — путь
   указывает прямо на docs-folder.
   - `--storage-path=` — internal kernel-only ключ. Если клиент
     пришлёт его через `arguments` — `McpArgvBuilder` обязан
     отфильтровать (security: иначе клиент перенаправит kernel на
     чужой docs-folder).
   - В `Dispatcher.TryValidateParams` `--storage-path` — universal
     ключ (whitelist рядом с `--dry-run`), не отвергается как
     `unknown_parameter`.
   - Архитектурный долг (DispatchContext / kernel-service слой) —
     задача **на потом**, не входит в stg-0010.

2. **kernel-config JSON.** Path передаётся argv `--config=<path>`. Файл:
   ```json
   {
     "bind": "127.0.0.1",
     "port": 8080,
     "graphs": {
       "docswalker": "D:/Dev/cs/projects/DocsWalker/docs",
       "another": "/data/another/docs"
     },
     "graph_idle_timeout": "10m"
   }
   ```
   - `bind` — обязательное.
   - `port` — обязательное (фиксированное; client'у нужно знать его в
     своём `.dw/client.json`).
   - `graphs` — обязательное, ≥1 запись. Имя — alias, путь — прямо к
     docs-folder.
   - `graph_idle_timeout` — опциональное, default `10m`. Формат
     `Ns/Nm/Nh/Nms` (как был в `KernelOptions`).

3. **client-config JSON (`.dw/client.json`).** Поиск вверх от cwd до
   filesystem-root (как `git` ищет `.git/`). Файл:
   ```json
   {
     "kernel": { "host": "127.0.0.1", "port": 8080 },
     "graph": "docswalker"
   }
   ```
   - Все поля обязательные.

4. **URL routing:**
   - `GET /health` — kernel-level, без graph.
   - `GET /db` — listing зарегистрированных graphs (`{name, loaded,
     last_access}` без storage-path'ов).
   - `POST /db/{graph}/rpc` — JSON-RPC dispatch внутри graph'а.

5. **Error codes:**
   - Kernel-side: `invalid_kernel_config` (JSON invalid / missing
     fields), `unknown_graph` (graph-name не в config'е),
     `graph_storage_not_found` (storage-path не существует — detect at
     startup).
   - Client-side: `client_config_not_found`, `client_config_invalid`,
     `kernel_unreachable`. Существующие `kernel_http_error`,
     `kernel_timeout`, `kernel_bad_response`, `kernel_rpc_error`
     остаются.

6. **`--help` / `--version` exempt.** Не требуют client-config: работают
   из коробки для bootstrapping/debugging. Special-cased в CLI
   `Program.cs` ДО `ClientConfig.Resolve()`.

7. **`docswalker kernel` в Commands.cs нет (удалена в stg-0008
   step-04).** В step-03 проверить, что её действительно нет — иначе
   удалить. Запуск только через `DocsWalker.Kernel.exe --config=<path>`.

## Файлы под правку

### Kernel-side (новые)

- `src/DocsWalker.Kernel/KernelConfig.cs` — новый. `record KernelConfig(string Bind, int Port, IReadOnlyDictionary<string,string> Graphs, TimeSpan GraphIdleTimeout)`. Метод `Read(string path)` парсит JSON, валидирует (bind/port/graphs обязательны, ≥1 graph, каждый storage-path существует). Бросает `KernelConfigException(code, message)` с кодами `invalid_kernel_config`, `graph_storage_not_found`.

### Kernel-side (переименование/правки)

- `src/DocsWalker.Kernel/RootRegistry.cs` → `GraphRegistry.cs`. Имя класса `RootRegistry` → `GraphRegistry`, `RootEntry` → `GraphEntry`. Конструктор принимает `KernelConfig` (или просто `IReadOnlyDictionary<string,string> graphs` + `TimeSpan idleTimeout`); `GetOrAdd` ищет по graph-name; `Snapshot()` отдаёт `IReadOnlyList<GraphInfo>` (без storage-path'ов).
- `src/DocsWalker.Kernel/KernelOptions.cs` — упростить: только парс `--config=<path>` argv. Удалить поля `Bind`, `Port`, `RootIdleTimeout`, методы `TryParseDuration`, `FormatDuration`, `Format`. `TryParseDuration` нужно перенести в `KernelConfig` (для парсинга `graph_idle_timeout`).
- `src/DocsWalker.Kernel/Program.cs` — переписать main:
  - Argv: `--config=<path>` обязательно. Если нет — `invalid_parameter` с подсказкой.
  - `KernelConfig.Read(configPath)` → ошибки `invalid_kernel_config` / `graph_storage_not_found` → exit 1.
  - `WebApplication.CreateSlimBuilder()`, bind URL `http://{config.Bind}:{config.Port}`.
  - `var registry = new GraphRegistry(config.Graphs, config.GraphIdleTimeout);`.
  - `var rpc = new RpcDispatcher(registry, app.Lifetime, Dispatcher.Run);`.
  - URL routes:
    - `MapGet("/health", healthHandler)`.
    - `MapGet("/db", graphsHandler)` — отдаёт `GraphsResponse`.
    - `MapPost("/db/{graph}/rpc", async (HttpContext ctx, string graph) => await rpc.HandleAsync(ctx, graph))`.
  - Удалить блок discovery (`KernelInfoFile.TryRead` / `kernel_already_running`).
  - Удалить блок `KernelInfoFile.Write` + `KernelInfoFile.DeleteIfExists`.
  - Удалить ignore токена `kernel` в начале argv.
- `src/DocsWalker.Kernel/RpcDispatcher.cs`:
  - Сигнатура `HandleAsync(HttpContext ctx)` → `HandleAsync(HttpContext ctx, string graphName)`.
  - `HandleMessageAsync(string requestJson, CancellationToken ct)` → `HandleMessageAsync(string requestJson, string graphName, CancellationToken ct)`.
  - Удалить `TryExtractRoot`. В `HandleListTools` и `HandleCallToolAsync` `root` больше не извлекается из arguments — он подменяется на `graphName + storagePath` из `GraphRegistry`.
  - Lookup: `var entry = _registry.GetOrAdd(graphName);` — если graph неизвестен (нет в config'е) — JSON-RPC error `unknown_graph` с code `JsonRpcErrorCodes.InvalidParams` (или новый код).
  - `HandleListTools.TryLoadSchema(graph)` — заменить на `TryLoadSchema(entry.StoragePath)` (storagePath из GraphEntry, не root).
  - `HandleCallToolAsync`:
    - Удалить `TryExtractRoot` + `root is required` ошибку.
    - Schema грузится через `TryLoadSchema(entry.StoragePath)`.
    - `McpArgvBuilder.BuildArgvFromArguments(...)` — фильтрует `arguments.root` И `arguments.storage_path` из user-input (см. ниже).
    - В argv инжектится `--storage-path={entry.StoragePath}` после билда.
- `src/DocsWalker.Kernel/KernelJsonContext.cs`:
  - Удалить `RootInfo`, `RootsResponse` (если уезжают в Cli/Cli/Kernel — пересмотреть размещение; они там и сейчас, но переезжают на `GraphInfo`/`GraphsResponse`). Решение: переместить kernel-side records (`HealthResponse`, `GraphInfo`, `GraphsResponse`, `KernelConfig`-DTO) в `src/DocsWalker.Kernel/KernelJsonContext.cs` (новый файл, отдельный от `Cli.Cli.Kernel.KernelJsonContext` — последний удаляется в step-04).
  - В step-03 проще: добавить `GraphInfo`/`GraphsResponse` рядом с существующими record'ами в `Cli/Cli/Kernel/KernelJsonContext.cs`, удалить `RootInfo`/`RootsResponse`. `KernelInfo` оставить (используется до step-04).

### Cli-side (новые)

- `src/DocsWalker.Cli/Cli/Kernel/ClientConfig.cs` — новый. Содержит:
  - `record KernelEndpointSpec(string Host, int Port)`.
  - `record ClientConfig(KernelEndpointSpec Kernel, string Graph)`.
  - `static ClientConfig Resolve(string? cwd = null)` — поиск `.dw/client.json` начиная от cwd, далее `Parent` до filesystem-root. Бросает `ClientConfigException(code, message)` с кодами `client_config_not_found`, `client_config_invalid`.
  - JSON-парсер через source-gen context (см. ниже про `KernelJsonContext`).
- `src/DocsWalker.Cli/Cli/Kernel/ClientConfigException.cs` — новый (или внутри `ClientConfig.cs`). `Code`, `Message`.

### Cli-side (правки)

- `src/DocsWalker.Cli/Cli/Kernel/KernelJsonContext.cs` — добавить
  записи `ClientConfigJson` (JSON-shape с `kernel: {host, port}`,
  `graph`), `KernelConfigJson` (storage-path map). Удалить `RootInfo`,
  `RootsResponse` → заменить на `GraphInfo`, `GraphsResponse`.
- `src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs` — переписать:
  - Сигнатура: `SendCommandAsync(string[] argv, ClientConfig config, CancellationToken ct = default)`. Без `rootPath`.
  - URL: `http://{config.Kernel.Host}:{config.Kernel.Port}/db/{config.Graph}/rpc`.
  - Удалить блок `cliExe = Environment.ProcessPath` + `KernelSpawner.ResolveKernelExePath` + `KernelClient.EnsureRunningAsync`. Сразу POST на URL.
  - `arguments` — только `parsed.Params` (без подмешивания `root` и без `storage_path`).
  - Catch `HttpRequestException` → `kernel_unreachable` (текст: «не удалось дозвониться до ядра ({url}). Запустите kernel и проверьте `.dw/client.json`.»). Hint про auto-spawn — удалить.
- `src/DocsWalker.Cli/Cli/Handlers/McpWrapperHandler.cs` — переписать:
  - `Run()` сигнатура: больше не принимает `rootPath`. Параметр — `IReadOnlyDictionary<string,string> args` (как было) + читает `ClientConfig.Resolve()` внутри.
  - Удалить `KernelSpawner.ResolveKernelExePath` + `KernelClient.EnsureRunningAsync`. Endpoint строится из ClientConfig.
  - `rpcUrl = $"http://{cfg.Kernel.Host}:{cfg.Kernel.Port}/db/{cfg.Graph}/rpc"`.
  - В `tools/list.params` — **не** подмешивать `root`. Удалить блок case `"tools/list"`. (Если для backward-compat нужна schema — kernel сам грузит её для graph'а из config'а.)
  - В `tools/call.arguments` — **не** подмешивать `root`. Удалить блок `argsNode["root"] = root;`.
  - На `kernel_unreachable` для intermediate forward — `MakeErrorEnvelope(idNode, -32603, "kernel unreachable: ...")`.
  - Banner: `MCP-wrapper started: graph={cfg.Graph}, kernel={cfg.Kernel.Host}:{cfg.Kernel.Port}, pid={pid}`.
  - Catch `ClientConfigException` в начале → `Output.WriteError(ex.Code, ...)` + return 1.
- `src/DocsWalker.Cli/Cli/Handlers/ReplHandler.cs` — переписать аналогично:
  - Не принимает `root`. Читает ClientConfig.
  - Удалить auto-spawn.
  - Banner: `REPL: graph={cfg.Graph}, kernel={cfg.Kernel.Host}:{cfg.Kernel.Port}`.
  - В REPL-вводе пользователь больше не пишет `--root=` — kernel-side validation отвергнет (это правильное поведение, single-source-of-truth).
  - `KernelHttpClient.SendCommandAsync(argv, cfg, ct)`.
  - Удалить `AppendIfMissing(argv, "--root=", root)` целиком.
- `src/DocsWalker.Cli/Cli/Commands.cs`:
  - У `repl`: удалить `Req("root", ...)`.
  - У `mcp_server`: удалить `Req("root", ...)`.
  - Описания (`desc`/`examples`) — обновить: убрать `--root=` из примеров.
  - В `get-by-path` пример `--root=` — нет (там path/tree). Проверить остальные команды.
- `src/DocsWalker.Cli/Program.cs` (top-level):
  - Удалить `TryResolveClientRoot` целиком.
  - Удалить ветку `if (!TryResolveClientRoot(args, out var rootPath)) {...}`.
  - Special-case `--help` / `--version`: если `args[0] == "--help"` или `args[0] == "--version"` — `return Dispatcher.Run(args)` (без ClientConfig).
  - Special-case `mcp-server` / `repl`: `return Dispatcher.Run(args)` — handlers сами читают ClientConfig.
  - Все остальные:
    ```csharp
    ClientConfig cfg;
    try { cfg = ClientConfig.Resolve(); }
    catch (ClientConfigException ex) {
        Output.WriteError(ex.Code, path: null, ex.Message);
        return 1;
    }
    return await KernelHttpClient.SendCommandAsync(args, cfg);
    ```
- `src/DocsWalker.Cli/Program.cs` (`Dispatcher.Run`):
  - `TryResolveRoot(...)` → `TryResolveStoragePath(...)`. Читает `--storage-path=` из args, если нет — ошибка `missing_storage_path` с подсказкой «kernel должен инжектить --storage-path; этот код только под kernel-mode». Этот путь срабатывает только когда Dispatcher.Run вызван kernel-ом через RpcDispatcher; CLI-mode (mcp-server/repl/--help/--version) — special-cased выше и не идут сюда.
  - Special-case в Dispatcher.Run для команд `mcp_server` / `repl` / `--help` / `--version`: storagePath им не нужен; resolution skipped.
  - В `TryValidateParams`: `--storage-path` добавить в whitelist рядом с `--root` (старый whitelist) и `--dry-run`. На самом деле `--root` нужно **удалить** из whitelist (теперь он `unknown_parameter`), а `--storage-path` добавить.
- `src/DocsWalker.Cli/Cli/Handlers/SchemaHandlers.cs`:
  - Сигнатуры `GetMetaSchema(string root)`, `GetSchema(string root)`, `DescribeType(string root, string name)`, `GetUsageGuide(string root, string?)` → `string storagePath`.
  - `Path.Combine(root, "docs", ".docswalker", "meta-schema.yml")` → `Path.Combine(storagePath, ".docswalker", "meta-schema.yml")`.
  - `Path.Combine(root, "docs", "Схема.yml")` → `Path.Combine(storagePath, "Схема.yml")`.
  - `var docsRoot = Path.Combine(root, "docs")` → `var docsRoot = storagePath` (оставляем имя `docsRoot` локальной переменной — она передаётся в `DocumentLoader.Load(docsRoot, schema)`, и DocumentLoader корректно работает с любым путём, который содержит `Схема.yml` рядом).
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs`:
  - Все методы `string root` → `string storagePath`.
  - `WithApi(root, ...)` → `WithApi(storagePath, ...)`. Внутри `Path.Combine(storagePath, "Схема.yml")`, `DocumentLoader.Load(storagePath, schema)`.
  - `CheckIntegrity`: `var docsRoot = storagePath`, остальное как сейчас.
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — same (read через Glider, чтобы не пропустить).
- `src/DocsWalker.Cli/Mcp/McpArgvBuilder.cs` — добавить фильтрацию: при сборке argv из `arguments`-объекта, **игнорировать ключи `root` и `storage_path`** (как user-input). Kernel сам инжектит `--storage-path=` после билда. Это критично для безопасности (Вариант A risk).

### Tests (правки)

- `tests/DocsWalker.Tests/RpcDispatcherTests.cs` — переписать:
  - Конструктор `RootRegistry(timeout)` → `GraphRegistry(graphs, timeout)`. Тестовые graphs:
    ```csharp
    var graphs = new Dictionary<string, string>
    {
        ["docswalker"] = TestPaths.DocsRoot,
        ["empty"] = NewTempDocsFolder(),
    };
    ```
  - `MakeCall(id, name, root)` → `MakeCall(id, name)` без root в arguments.
  - `dispatcher.HandleMessageAsync(msg, default)` → `HandleMessageAsync(msg, "docswalker", default)`.
  - Тест `ToolsCall_TwoDifferentRoots_BothInRegistry` → `ToolsCall_TwoDifferentGraphs_BothInRegistry` — 2 вызова с разными graphName, проверить registry.Snapshot().
  - Тест `ToolsCall_MissingRoot_ReturnsInvalidParams` → `ToolsCall_UnknownGraph_ReturnsError` — graphName не в config'е, проверить error `unknown_graph`.
  - Тест `Initialize_ReturnsServerInfoAndProtocolVersion` — graphName не релевантен для initialize, но сигнатура требует. Передать `"docswalker"`.
- `tests/DocsWalker.Tests/WriteTestEnvironment.cs` — без правок (DocsRoot уже = storage-path в новой модели).
- `tests/DocsWalker.Tests/TestPaths.cs` — без правок (DocsRoot = `<repo>/docs` уже подходит как storagePath).
- `tests/DocsWalker.Tests/McpArgvBuilderTests.cs` — добавить тест: arguments `{"root": "...", "type": "section"}` → argv не содержит `--root=`, содержит `--type=section`. Аналогично для `storage_path`.
- Новые файлы:
  - `tests/DocsWalker.Tests/KernelConfigTests.cs` — Read валидного / invalid JSON / missing fields / non-existent storage-path.
  - `tests/DocsWalker.Tests/ClientConfigTests.cs` — Resolve находит вверх по дереву / `client_config_not_found` если не существует / `client_config_invalid` для битого JSON.

## Действия (упорядоченные)

1. **GraphRegistry + KernelConfig + KernelOptions.** Создать
   `KernelConfig.cs` (parse + validate). Переименовать `RootRegistry` →
   `GraphRegistry` (через `mcp__glider__rename_symbol`), сменить
   модель «root path → entry» на «graph-name → entry { name,
   storagePath, ... }». Упростить `KernelOptions` до `--config=`.
   Перенести `TryParseDuration` в `KernelConfig`. Записи `KernelInfo`
   и kernel-side `RootInfo`/`RootsResponse` пока трогаем минимально
   (KernelInfo нужен step-04).
2. **Kernel/Program.cs.** Переписать main: argv `--config=`,
   `KernelConfig.Read`, URL routes `/health`, `/db`,
   `/db/{graph}/rpc`. Удалить блок discovery + `kernel_already_running` +
   `KernelInfoFile.Write/DeleteIfExists`. `RpcDispatcher` принимает
   `GraphRegistry`.
3. **RpcDispatcher.cs.** `HandleAsync(ctx, graphName)`,
   `HandleMessageAsync(json, graphName, ct)`, удаление
   `TryExtractRoot`, lookup graph через `_registry.GetOrAdd(graphName)`
   (в registry — `GraphEntry { GraphName, StoragePath, Semaphore,
   IdleTimer, ... }`); `unknown_graph` error если нет.
4. **McpArgvBuilder.cs (фильтрация).** При сборке argv из arguments
   игнорировать `root` и `storage_path`. После билда argv в
   `HandleCallToolAsync` инжектить `--storage-path=<storagePath>`.
5. **Dispatcher.Run + handlers.** `TryResolveRoot` →
   `TryResolveStoragePath`. Все handler-методы переименовывают
   параметр `string root` → `string storagePath`. Все
   `Path.Combine(root, "docs", ...)` → `Path.Combine(storagePath, ...)`.
   Special-case `mcp_server`/`repl`/`--help`/`--version` — без storagePath.
   `Commands.cs` — удалить `Req("root", ...)` из `repl`/`mcp_server`;
   валидация unknown_parameter обновляется.
6. **ClientConfig.cs.** Создать класс + exception + JSON-records +
   resolver. Tests — параллельно.
7. **KernelHttpClient.cs / McpWrapperHandler.cs / ReplHandler.cs.**
   Переписать на ClientConfig + URL `/db/{graph}/rpc` + удаление
   auto-spawn. arguments больше не модифицируется.
8. **Program.cs (CLI top-level).** Удалить `TryResolveClientRoot`;
   special-case `--help`/`--version`/`mcp-server`/`repl`; для остальных
   — `ClientConfig.Resolve()` + `KernelHttpClient.SendCommandAsync(args,
   cfg)`.
9. **Tests.** Обновить `RpcDispatcherTests`. Добавить новые
   `KernelConfigTests`, `ClientConfigTests`, расширить
   `McpArgvBuilderTests` (фильтрация). Убедиться, что
   `WriteTestEnvironment` / `TestPaths` не требуют правок.
10. **`dotnet build` + `dotnet test`** — оба зелёные.
11. **Git.** atomic commit + push (одна пачка step-03).
12. **`[*] → [+]`** в `strategy.md`.

## Риски

- **Атомарность шага.** Между подшагами 1–9 проект не компилируется
  и тесты красные. Это нормально для атомарного step-а; main цель —
  green после п.10. Если выясняется, что шаг разваливается — откат
  целиком (через `git checkout -- .` или branch).
- **`--storage-path=` security.** Если `McpArgvBuilder` забудет
  фильтровать `arguments.storage_path` от user-input — клиент сможет
  перенаправить kernel на чужой docs-folder. Тест в
  `McpArgvBuilderTests` критичен; проверять отдельно.
- **Auto-spawn типы → dead code.** `KernelClient`, `KernelSpawner`,
  `KernelLock`, `KernelDiscovery`, `KernelInfoFile`,
  `StalePidDetector` после step-03 unreferenced. Компиляция проходит,
  но IDE/Roslyn могут предупреждать unused. **Не удалять** в step-03
  (scope step-04). Если warning'и блокируют сборку — добавить
  `#pragma warning disable` локально с TODO step-04.
- **`KernelInfo` запись `kernel.json`.** Step-03 удаляет вызов
  `KernelInfoFile.Write` из kernel'ного `Program.cs`. Старый запущенный
  kernel (pid=57588) больше не валиден после пересборки + рестарта;
  пользовательский `%LOCALAPPDATA%\DocsWalker\kernel.json` остаётся
  как orphan. Это не блокирует step-03 (никто его не читает после
  правки `KernelHttpClient`); зачистка дискrovery-файлов — step-04.
- **REPL `--root=` в строке.** Если пользователь введёт `get-nodes
  --root=foo --ids=1`, токен `--root=` уйдёт в kernel argv через
  `KernelHttpClient.SendCommandAsync` → `arguments` (без фильтрации
  `McpArgvBuilder` ниже, потому что КЛИЕНТСКАЯ сторона). Kernel
  получит его в `tools/call.arguments.root`, McpArgvBuilder
  отфильтрует, kernel инжектит `--storage-path=...`. То есть `--root=`
  тихо игнорируется в REPL — не идеально, но не страшно. Идеально —
  поймать на CLI-стороне в `KernelHttpClient` и кинуть
  `unknown_parameter`. Но это требует знать spec'и команд клиенту.
  Решение в step-03 — **передавать как есть, фильтровать на kernel**.
  Если фидбек по UX появится — в step-05 (error-case-tests) добавить
  client-side warning.
- **MCP `tools/list` без graph.** До step-03 `tools/list.params`
  принимал `root` как опциональный hint для динамической схемы
  `create-node`. После step-03 graph выбирается URL'ом, kernel сам
  знает; `tools/list.params` больше не нужно подмешивать. Но MCP
  `initialize` — без graph (URL-mux уже выбрал graph). Это норма:
  любой запрос на `/db/{graph}/rpc` уже в контексте graph'а, включая
  initialize.
- **Round-trip get-schema / describe-type.** Не затронут — Schema
  читается из storagePath, формат тот же.
- **`McpArgvBuilder`-фильтрация и обратная совместимость.** До step-03
  arguments.root был required; user-arguments всегда содержали его.
  После step-03 он неожидан; если client (старая версия CLI) пришлёт
  его — мы тихо игнорируем (фильтр). Это даёт мягкий graceful-degrade
  при mismatch версий: user видит ошибку конкретной команды (если
  storagePath kernel-side инжектится корректно), а не protocol-level
  сбой.
- **`dotnet publish`.** После step-03 — `publish/cli/` и `publish/kernel/`
  устарели. Тестовый kernel из `publish/kernel/` всё ещё пишет
  kernel.json, не понимает `/db/{graph}/rpc`. Перед smoke (step-06)
  обязательно пересобрать оба exe и удалить orphan-discovery-файлы.

## Сверка со страт

- Блок 1.1 (standalone HTTP-сервер): kernel запускается через
  `--config=<path>` argv, KernelOptions упрощён.
- Блок 1.2 (auto-spawn убирается): `KernelHttpClient` /
  `McpWrapperHandler` / `ReplHandler` больше не вызывают
  `KernelClient.EnsureRunningAsync`. Сами типы удаляются в step-04 —
  атомарно с зачисткой тестов.
- Блок 1.3 (per-user discovery убирается): `KernelInfoFile.Write`
  убран из kernel'ного `Program.cs`; clients больше не читают
  `kernel.json`. Удаление KernelDiscovery/KernelInfoFile-типов —
  step-04.
- Блок 1.4 (bind 127.0.0.1 default): `KernelConfig.Bind` обязателен;
  пример в страт показывает `127.0.0.1`. Default'а в KernelConfig нет
  (явность).
- Блок 1.5 (auth не делаем): без изменений, как было.
- Блок 1.6 (kernel_already_running убирается): удаляется из
  kernel'ного `Program.cs` — step-03.
- Блок 2.1 (named graphs в kernel-config): `KernelConfig.Graphs`,
  `GraphRegistry`.
- Блок 2.2 (URL `/db/<name>/rpc`, `/db`, `/health`): новые route'ы.
- Блок 2.3 (client-config `.dw/client.json`): `ClientConfig.Resolve()`
  ищет вверх; ошибки `client_config_not_found` /
  `client_config_invalid`.
- Блок 2.4 (storage path = path к docs-folder напрямую): handlers
  принимают `storagePath`, не делают `Path.Combine(root, "docs", ...)`.
- Блок 2.5 (один client-config = один graph): `ClientConfig.Graph` —
  одно значение, без override.
- Блок 2.6 (multi-graph use-case = N инстансов клиента): не меняется
  в коде.
- Блок 2.7 (`--root=` удаляется из всех команд): через `Commands.cs` +
  `Dispatcher.TryValidateParams` отвергает как `unknown_parameter`.
- Блок 2.8 (kernel.exe без CLI-обёртки): уже сделано в stg-0008
  step-04, проверить отсутствие в `Commands.cs`.
- Блок 2.9 (`--help`/`--version` exempt): special-case в CLI
  `Program.cs`.
- Блок 2.10 (sole-writer = trust boundary): защиты нет, как и в страт.
