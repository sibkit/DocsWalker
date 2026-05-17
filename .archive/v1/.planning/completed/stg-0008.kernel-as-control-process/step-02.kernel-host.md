# stg-0008 — step-02 — kernel-host

## Цель

Реализовать ядро DocsWalker как HTTP-сервер на ASP.NET Core minimal API + Kestrel. Команда `docswalker kernel` (без `--root`, multi-root) поднимает локальный HTTP-listener на динамическом порту и обслуживает JSON-RPC 2.0 поверх HTTP.

Эндпойнты:
- `POST /rpc` — JSON-RPC 2.0 (`initialize`, `notifications/initialized`, `tools/list`, `tools/call`, `shutdown`). В `tools/call` — `arguments.root` обязателен (kernel multi-root).
- `GET /health` — liveness check (`{ok:true,pid,started_at,version}`).
- `GET /roots` — `[{root, last_used, expires_at}]` для диагностики.

Ядро **не пишет ещё** `kernel.json` / `kernel.lock` — это step-03 (discovery-and-spawn). Для step-02 достаточно вывода `pid` и `url` на stderr на старте.

## Файлы

- `src/DocsWalker.Cli/DocsWalker.Cli.csproj` — добавить `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. Это включает AspNetCore без NuGet, AOT-совместимо.
- `src/DocsWalker.Cli/Cli/Commands.cs` — spec команды `kernel` (read, optional params: `--bind`, `--port`, `--root-idle-timeout`).
- `src/DocsWalker.Cli/Program.cs` — в `Dispatcher.Run` шунт «kernel» до `TryResolveRoot` (kernel — без --root).
- `src/DocsWalker.Cli/Cli/Handlers/KernelHandler.cs` (новый) — точка входа: builder, listener, маршруты, graceful shutdown.
- `src/DocsWalker.Cli/Cli/Kernel/RootRegistry.cs` (новый) — `ConcurrentDictionary<string, RootEntry>`; per-root semaphore + last_used + idleTimer.
- `src/DocsWalker.Cli/Cli/Kernel/KernelOptions.cs` (новый) — парс bind/port/root-idle-timeout с дефолтами.
- `src/DocsWalker.Cli/Cli/Kernel/RpcDispatcher.cs` (новый) — JSON-RPC layer: парсит envelope, валидирует, вытягивает root, держит per-root semaphore, маршалит `tools/call` в `Dispatcher.Run`, упаковывает результат.
- `src/DocsWalker.Cli/Cli/Kernel/KernelJsonContext.cs` (новый) — source-gen context для AOT-совместимой сериализации `HealthResponse`/`RootsResponse`/`RootInfo`.

## Действия

1. **csproj** — добавить FrameworkReference на AspNetCore.
2. **Spec команды `kernel`** — read-команда (без побочного эффекта на docs/), optional params, без `--root`.
3. **Routing в Dispatcher.Run** — проверка `parsed.CommandKebab == "kernel"` сразу после spec lookup и перед `TryResolveRoot`. Вызывает `KernelHandler.Run(parsed.Params)`.
4. **`KernelOptions`** — `bind` (default "127.0.0.1"), `port` (default 0 = dynamic), `root-idle-timeout` (default 10m, парс `ParseDuration("10m"|"30s"|"1h"|"500ms")`).
5. **`RootRegistry`** — `ConcurrentDictionary<string, RootEntry>`. `GetOrAdd(root)` → entry. `Touch(entry)` обновляет `LastUsed`, перезапускает `IdleTimer`. `RootEntry` несёт `SemaphoreSlim(1,1)`, `LastUsed`, `IdleTimer`. Eviction-callback удаляет entry из словаря и dispose'ит таймер/семафор. `Snapshot()` для `/roots`. `DisposeAll()` на shutdown.
6. **`KernelHandler.Run`** — `WebApplication.CreateSlimBuilder` (AOT slim), `app.Urls.Add("http://{bind}:{port}")`, регистрирует source-gen JsonContext в `ConfigureHttpJsonOptions`, `MapPost("/rpc", ...)`, `MapGet("/health", ...)`, `MapGet("/roots", ...)`. После `app.StartAsync()` — печать на stderr `DocsWalker kernel started: pid=X, url=http://...:..., root_idle_timeout=10m`. Ждёт `IHostApplicationLifetime.ApplicationStopping` или Ctrl-C. На shutdown — `registry.DisposeAll()`.
7. **`RpcDispatcher.HandleAsync(HttpContext)`** — читает body, deserialize `JsonRpcRequest` через `McpJsonContext`. Method:
   - `initialize` → `InitializeResult` (как у `McpServer`).
   - `notifications/initialized`, `notifications/cancelled` → notification.
   - `tools/list` → `CommandsToTools.Build(schema)`. Optional `params.root`: если есть — грузим Схему этого root'а для динамической schema у `create-node`; если нет — базовая schema без oneOf.
   - `tools/call` → `DispatchToolCall`:
     - валидирует name + arguments;
     - извлекает `root` из arguments — если нет → InvalidParams «root is required for kernel HTTP transport»;
     - `RootRegistry.GetOrAdd(root).Semaphore.WaitAsync()`;
     - `BuildArgvFromArguments` (тот же что у MCP) + `RequestContext.Push(session_id)` + `Dispatcher.Run` + capture stdout/stderr;
     - возвращает `CallToolResult` (text content, `isError` если exit != 0).
   - `shutdown` → пустой ответ + триггер graceful shutdown через `IHostApplicationLifetime.StopApplication()`.
   - другое → `MethodNotFound`.
8. **Capture stdout/stderr** — глобальная `_globalCaptureLock` (SemaphoreSlim(1,1)). Per-root semaphore остаётся в registry для будущей интеграции (когда handlers будут принимать TextWriter параметром в step-04 или позже). Console.SetOut/SetError — process-global; иначе параллельные роуты на разные roots будут писать друг другу в stdout. Risk-mitigation в комментарии.
9. **`KernelJsonContext`** — source-gen для `HealthResponse(string Version, int Pid, DateTimeOffset StartedAt, bool Ok)`, `RootInfo(string Root, DateTimeOffset LastUsed, DateTimeOffset ExpiresAt)`, `RootsResponse(IReadOnlyList<RootInfo> Roots)`.
10. **Graceful shutdown** — `Console.CancelKeyPress` → `app.Lifetime.StopApplication()`. На stopping → `registry.DisposeAll()` (все таймеры/семафоры). Дожидаемся `ApplicationStopped`.

## Риски

- **AOT и ASP.NET Core minimal API** — AOT-only пути требуют source-gen JSON. `WebApplication.CreateSlimBuilder` AOT-friendly. `MapPost`/`MapGet` с `(HttpContext)` AOT-совместимы. Регистрируем `ConfigureHttpJsonOptions` с нашими source-gen контекстами.
- **Per-request stdout capture** — `Console.SetOut/SetError` process-global. Глобальная сериализация capture'а — temporary; полное per-root concurrency требует TextWriter-параметра в handlers (вне scope step-02). Per-root semaphore в registry уже на месте — переход к настоящему concurrency пройдёт без новой инфраструктуры.
- **Idle timer в AOT** — `System.Threading.Timer` AOT-совместим. OK.
- **Graph/Schema cache в registry** — strategy.md упоминает `RootRegistry (lazy load графов, словарь root → graph + last-used)`. Для step-02 RootRegistry хранит только semaphore+last_used+idleTimer; cache графа/Схемы добавится либо в step-04 (cli-to-http-client), либо отдельным микро-шагом — **handlers пока загружают через `WithApi`, что ОК благодаря sole-writer (повторный load даёт идентичный граф)**.
- **TryResolveRoot для других команд** — kernel не использует TryResolveRoot, но handlers внутри `Dispatcher.Run` — да. argv будет содержать `--root=<from arguments>`, resolver просто примет.
- **Тесты не пишем** — step-02 заканчивается на «бинарь собирается, kernel запускается, /health отвечает 200, /rpc initialize отвечает корректным envelope». Полноценный smoke — step-09.

## Пост-проверки

1. `dotnet publish src/DocsWalker.Cli` — успех.
2. `kernel` — выводит на stderr строку `DocsWalker kernel started: pid=X, url=http://127.0.0.1:Y, root_idle_timeout=10m`.
3. `curl http://127.0.0.1:Y/health` → 200, json `{"ok":true, "pid":X, ...}`.
4. `curl -X POST http://127.0.0.1:Y/rpc -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'` — 200, корректный JSON-RPC envelope.
5. `curl http://127.0.0.1:Y/roots` → `{"roots":[]}`.
6. Ctrl+C → graceful exit, `DocsWalker kernel stopped: pid=X` на stderr.
