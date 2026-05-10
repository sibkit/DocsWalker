# stg-0008 — step-07 — cleanup-old-ipc

## Цель

Удалить весь старый IPC-стек, который заменён HTTP-ядром (`DocsWalker.Kernel`).
После шага в коде не остаётся:

- named-pipe / Unix-socket каналов и handshake-протокола;
- in-process серверного режима `run`;
- старого in-process `McpServer` поверх stdio (его место занял `McpWrapperHandler` + ядро);
- SHA-256 docs-checksum инвалидации сессий (#359);
- упоминаний `run --root=`, `server_not_running`, named pipe в выдаче `get-usage-guide`.

После шага все CLI-вызовы (включая `repl`, `mcp-server`) идут единственным путём:
`HttpClient → POST /rpc → DocsWalker.Kernel.exe`.

## Файлы

### Удалить целиком (Core/Server)

- `src/DocsWalker.Core/Server/IpcServer.cs`
- `src/DocsWalker.Core/Server/ServerLifecycle.cs` (вместе с `ServerAlreadyRunningException`, `ServerStartException`)
- `src/DocsWalker.Core/Server/SignalHandler.cs` (вместе с `IShutdownToken`)
- `src/DocsWalker.Core/Server/Ipc/IIpcChannel.cs`
- `src/DocsWalker.Core/Server/Ipc/NamedPipeChannel.cs`
- `src/DocsWalker.Core/Server/Ipc/UnixSocketChannel.cs`
- `src/DocsWalker.Core/Server/Ipc/IpcChannelFactory.cs`
- `src/DocsWalker.Core/Server/Ipc/IpcClientConnector.cs`
- `src/DocsWalker.Core/Server/Protocol/Handshake.cs` (`HandshakeRequest/Response`)
- `src/DocsWalker.Core/Server/Protocol/Request.cs` (`IpcRequest`)
- `src/DocsWalker.Core/Server/Protocol/Response.cs` (`IpcResponse`)
- `src/DocsWalker.Core/Server/Protocol/ProtocolVersion.cs`
- `src/DocsWalker.Core/Server/Protocol/ProtocolJsonContext.cs`

### Оставить (Core/Server)

- `src/DocsWalker.Core/Server/StalePidDetector.cs` — переиспользуется `KernelClient` и Kernel/Program.cs.
- `src/DocsWalker.Core/Server/RequestContext.cs` — поднимает session_id для read/write-handlers (используется `SeenScope`, `SchemaHandlers`, `WriteHandlers`); в Kernel пушится `RpcDispatcher.ExecuteWithCaptureAsync`.
- `src/DocsWalker.Core/Server/Protocol/Frame.cs` — newline-delimited JSON-frame, нужен `McpWrapperHandler` для stdio↔HTTP-bridge.

### Удалить (Core/Mcp)

- `src/DocsWalker.Core/Mcp/McpServer.cs` — целиком. Старый stdio-server.
  Static-helpers `BuildArgvFromArguments` / `JsonValueToCliString` / `IsObjectArray`
  переезжают в новый файл `src/DocsWalker.Core/Mcp/McpArgvBuilder.cs` (см. Действия).

### Оставить (Core/Mcp)

- `McpToolDescriptor.cs`, `McpTypes.cs`, `JsonRpcTypes.cs`, `McpJsonContext.cs` — потребители: `RpcDispatcher`, `CommandsToTools`, `McpWrapperHandler`.

### Удалить (Core/Sessions)

- `src/DocsWalker.Core/Sessions/DocsChecksum.cs` — целиком (#359, sole-writer ядра делает checksum-инвалидацию ненужной).

### Оставить (Core/Sessions)

- `SessionState.cs`, `SessionFile.cs` — sessions per-root на диске остаются (decision #12 strategy.md). Сейчас kernel пушит `sessions=null`; интеграция — отдельный шаг.

### Удалить (Cli)

- `src/DocsWalker.Cli/Cli/IpcClient.cs` — клиент к старому IpcServer.
- `src/DocsWalker.Cli/Cli/PidFileReader.cs` — читал старый `docs/.docswalker/run.pid`. Новый discovery — через `KernelInfoFile`.
- `src/DocsWalker.Cli/Cli/Handlers/RunHandler.cs` — старая команда `run` (server-mode).
- `src/DocsWalker.Cli/Cli/Handlers/McpServerHandler.cs` — старый stdio-server поверх `ServerLifecycle`.
- `src/DocsWalker.Cli/Cli/Repl/Repl.cs` — старый in-process REPL (вызывал `IpcServer.ExecuteLocalAsync`).

### Оставить (Cli)

- `SessionId.cs` — `Resolve(argv)` используется в `KernelHttpClient.SendCommandAsync` (env+флаг подхватывает, прокидывает в `tools/call.arguments.session_id`).
- `Cli/Repl/LineReader.cs`, `Cli/Repl/ReplTokenizer.cs` — переиспользуются в `ReplHandler`.

### Править (Cli)

- `src/DocsWalker.Cli/Program.cs`:
  - В верхнем gate (после parsing argv) убрать `cmd == "run"` из условия. Остаются `mcp-server` и `repl`.
  - В `Dispatcher.Run` switch удалить `"run" → RunHandler.Run(...)` ветку.
- `src/DocsWalker.Cli/Cli/Commands.cs`:
  - Удалить `Read("run", ..., Req("root", ...), Opt("quiet", ...), Opt("mode", ...))` целиком (CommandSpec вместе с примерами).
- `src/DocsWalker.Cli/UsageGuide/UsageGuideText.cs`:
  - Полностью переписать `MentalModel` под новую модель (см. Действия).

### Править (Kernel)

- `src/DocsWalker.Kernel/RpcDispatcher.cs`:
  - Заменить `McpServer.BuildArgvFromArguments(...)` → `McpArgvBuilder.BuildArgvFromArguments(...)` (после переноса).

### Тесты

- `tests/DocsWalker.Tests/IpcSmokeTests.cs` — удалить целиком (named pipe / handshake / framing).
- `tests/DocsWalker.Tests/McpServerTests.cs`:
  - Оставить только тесты `BuildArgv_*` (8 штук); они становятся `McpArgvBuilderTests`. Файл переименовать в `McpArgvBuilderTests.cs`, класс `McpServerTests` → `McpArgvBuilderTests`, вызовы — на `McpArgvBuilder.BuildArgvFromArguments`.
  - Остальные тесты (`Initialize_*`, `ListTools_*`, `CallTool_*`, `UnknownMethod_*`, `ParseError_*`, `Notification_*`, `Shutdown_*`, `IdRoundtrip_*`) — удалить. Их заменит `RpcDispatcherTests` (см. ниже).
  - Атрибут `[Collection("ConsoleRedirect")]` на классе `BuildArgv` не нужен — эти тесты не дёргают `Console.SetOut`. Снять.
- `tests/DocsWalker.Tests/SessionsInfrastructureTests.cs`:
  - Удалить секцию `// ── DocsChecksum ──` (5 тестов: `ComputeForDocs_DeterministicForSameContent`, `ComputeForDocs_FileEdited_HashChanges`, `ComputeForDocs_ExcludesSessionsSubtree`, `Stored_RoundtripsThroughDisk`, `Stored_MissingFile_ReturnsNull`). Удалить `using DocsWalker.Core.Sessions;`-зависимый кусок XML-doc summary (упоминание `DocsChecksum`).
  - SessionState / SessionFile тесты оставить.
- `tests/DocsWalker.Tests/RpcDispatcherTests.cs` (новый файл) — multi-root /rpc roundtrip:
  - Добавить `ProjectReference` на `DocsWalker.Kernel.csproj` в `DocsWalker.Tests.csproj`.
  - В `DocsWalker.Kernel.csproj` добавить `<InternalsVisibleTo Include="DocsWalker.Tests" />` (через `<ItemGroup>` с `AssemblyAttribute` или через свойство).
  - Тест `MultiRoot_GetUsageGuide_TwoRootsInSameKernel`: создаёт два временных root'а с минимальным `docs/Схема.yml` и `docs/.docswalker/sequence.txt` (или копирует `TestPaths.RepoRoot` для обоих с разной правкой), конструирует `RootRegistry`, мок `IHostApplicationLifetime`, `Dispatcher.Run`, гоняет `tools/call get-usage-guide` с `arguments.root=root1`, потом с `arguments.root=root2`, оба возвращают `isError != true`.
  - Серилизация с `IpcSmokeTests`/`McpServerTests` через `[Collection("ConsoleRedirect")]` (по той же причине: `RpcDispatcher` глобально перехватывает `Console.SetOut`).

## Действия

1. **`McpArgvBuilder`** — создать `src/DocsWalker.Core/Mcp/McpArgvBuilder.cs`:
   - Public static class. Перенести `BuildArgvFromArguments`, `JsonValueToCliString`, `IsObjectArray` из `McpServer` 1:1. Сохранить XML-doc.
2. **RpcDispatcher** — заменить вызов `McpServer.BuildArgvFromArguments` на `McpArgvBuilder.BuildArgvFromArguments`.
3. **Удалить файлы** из списка выше через `git rm` (или удаление через bash + sync). После каждой пачки удалений — `mcp__glider__reload`, чтобы Roslyn перестроил workspace и не путался.
4. **Program.cs / Commands.cs / UsageGuideText.cs** — правки по списку.
5. **MentalModel** — переписать под новую модель. Опорные точки:
   - DocsWalker.Kernel.exe — фоновое ядро, один процесс на пользователя; держит N графов (multi-root, routing через `--root=` в каждом запросе).
   - Все CLI-команды идут к ядру через локальный HTTP+JSON-RPC 2.0 на `127.0.0.1:<port>`. Discovery — `%LOCALAPPDATA%\DocsWalker\kernel.json` (Windows) / `$XDG_RUNTIME_DIR/docswalker/kernel.json` (POSIX).
   - Если ядра нет — клиент авто-spawn'ит `DocsWalker.Kernel.exe` (DETACHED), пишет на stderr `kernel: spawned pid=… port=…`. Никаких silent fallback'ов.
   - Per-root idle eviction = 10 мин, kernel-level idle отсутствует. После reboot первый клиент поднимет ядро.
   - Команды по сценариям: `kernel` (ручной запуск/диагностика, обычно не нужен), `repl --root=` (интерактивный HTTP-клиент к ядру), `mcp-server --root=` (stdio↔HTTP wrapper, поднимает Claude Code). Для одноразового вызова — просто `docswalker <команда> --root=…`.
   - Удалить упоминания named pipe, `server_not_running`, `docs/.docswalker/run.lock`, в каком-либо контексте — больше не существует.
6. **Тесты** — правки по списку. После — `dotnet test` локально (быстрее `dotnet test --filter` по новому файлу для проверки).
7. **`mcp__glider__reload`** ещё раз по итогу — workspace должен зелёный.
8. **strategy.md**: `[*] (07) cleanup-old-ipc` → `[+] (07) cleanup-old-ipc`.
9. **Atomic git**: `git add -A`; `git commit -m "Implement stg-0008 step-07 cleanup-old-ipc"`; `git push origin master`. Каждая команда — отдельный Bash-вызов.

## Риски

- **Удаление `McpServer.cs` ломает `RpcDispatcher`.** Mitigation: сначала создать `McpArgvBuilder.cs`, заменить вызов в RpcDispatcher, прогнать `dotnet build`, потом удалять `McpServer.cs`. Не наоборот.
- **`PidFileReader` / `SessionId` / `LineReader` неожиданно используются ещё где-то.** Mitigation: `mcp__glider__find_references` перед удалением; перепроверить grep'ом.
- **`Frame.ReadLineAsync` — единственная зависимость `McpWrapperHandler` от старого пространства имён `DocsWalker.Core.Server.Protocol`.** После удаления остальных файлов этого namespace останется единственный класс. Это нормально — переименовывать пространство не требуется (ассёт идёт в одном `.cs`-файле).
- **Тесты `McpServerTests` тестировали `HandleMessageAsync` — initialize/tools/list/error path.** Эти ветки в `RpcDispatcher` идентичны (копи-паста). На step-07 мы покрываем только smoke-тестом roundtrip; полное JSON-RPC unit-покрытие RpcDispatcher — отдельный шаг при необходимости.
- **AOT/source-gen для удалённых типов `ProtocolJsonContext`.** STJ source-generator перестроит контексты; никаких ручных правок не нужно. После `dotnet build` — проверить, что нет warning'ов IL2026/IL3050.

## Пост-проверки

1. `dotnet build DocsWalker.slnx -c Release` — успех, 0 warnings/errors.
2. `dotnet test tests/DocsWalker.Tests/DocsWalker.Tests.csproj -c Release` — все тесты зелёные. Из удалённых наборов остаются только `BuildArgv_*` + `RpcDispatcher_MultiRoot` + базовые SessionState/SessionFile.
3. `dotnet publish src/DocsWalker.Cli/DocsWalker.Cli.csproj -c Release -r win-x64` и аналогично Kernel — оба проекта собираются.
4. Smoke вручную: убить любой ранее запущенный `DocsWalker.Kernel.exe`, запустить `DocsWalker.Cli.exe get-usage-guide --root=.` — kernel auto-spawn, ответ корректный.
5. `DocsWalker.Cli.exe repl --root=.` — REPL поднимается, простая команда `check-integrity` отвечает.
6. `grep -r "IpcClient\|IpcServer\|IpcChannel\|ServerLifecycle\|HandshakeRequest\|HandshakeResponse\|IpcRequest\|IpcResponse\|DocsChecksum" src tests` — пусто (кроме новых файлов, если такие случайно).
7. `get-usage-guide` (через CLI) — в выдаче `mental_model` нет слов `run --root=`, `server_not_running`, `named pipe`, `IPC`, `run.lock`.
