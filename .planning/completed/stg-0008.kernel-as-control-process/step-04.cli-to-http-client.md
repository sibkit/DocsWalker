# stg-0008 — step-04 — cli-to-http-client

## Цель

Заменить клиентский путь CLI с named-pipe IPC (`IpcClient.SendCommandAsync`) на HTTP+JSON-RPC к ядру через `KernelClient.EnsureRunningAsync` + `POST /rpc tools/call`.

**Архитектурная правка по ходу step-04:** ядро вынесено в отдельный проект `src/DocsWalker.Kernel/` (`Sdk=Microsoft.NET.Sdk.Web`, `OutputType=WinExe`, `PublishAot=true`). Без этого detached spawn на Windows ломается: child-процесс наследует console-handles parent CLI и падает на `Console.Error.WriteLine` после exit'а CLI. WinExe (windows subsystem) даёт child'у отсутствие консоли — детач «из коробки». На Linux/POSIX subsystem-понятия нет; ядро запускается обычным `setsid`/`nohup` wrapper'ом из `KernelSpawner.SpawnPosix`. Команда `docswalker kernel` из CLI убрана; CLI спавнит `DocsWalker.Kernel.exe` рядом со своим бинарём (`KernelSpawner.ResolveKernelExePath`).

Поведение для пользователя:
- `docswalker <command> --root=<path> [--key=value...]` авто-резолвит root, читает `kernel.json`, при отсутствии живого ядра — спавнит, затем отправляет команду через `tools/call` и печатает ответ.
- При ошибке spawn — `kernel_spawn_failed` с подсказкой.
- session_id из argv (`--session-id=<uuid>`) или env (`CLAUDE_CODE_SESSION_ID`) пробрасывается в `arguments.session_id`.

CLI-команды `run`, `mcp-server`, `kernel` остаются server-side (как и было).

## Файлы

- `src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs` (новый) — `SendCommandAsync(string[] argv, string rootPath, CancellationToken)`. Внутри: парсит argv → command + params; вызывает `KernelClient.EnsureRunningAsync`; собирает JSON-RPC `tools/call` с `arguments.root` (и опц. `session_id`); POSTит на `/rpc`; распаковывает `CallToolResult` → stdout/stderr + exit-code.
- `src/DocsWalker.Cli/Cli/SessionId.cs` (новый) — выносим логику `ResolveSessionId(argv)` из `IpcClient.cs` в общий helper (используется и `IpcClient` в legacy-пути, и `KernelHttpClient`).
- `src/DocsWalker.Cli/Program.cs` — заменить `IpcClient.SendCommandAsync(rootPath, args)` на `KernelHttpClient.SendCommandAsync(args, rootPath)`. Убрать предварительную проверку `PidFileReader.TryReadLivePid` (kernel auto-spawnится клиентом, отдельная проверка не нужна).
- `src/DocsWalker.Cli/Cli/IpcClient.cs` — обновить `ResolveSessionId` на `SessionId.Resolve` (общий helper).

## Действия

1. **`SessionId.Resolve(argv)`** — статический helper: ищет `--session-id=<uuid>` в argv, fallback на env `CLAUDE_CODE_SESSION_ID`. Пустое значение → null. (Контракт #342 docs/DocsWalker.yml.)
2. **`KernelHttpClient`**:
   - Конструктор с `HttpClient` (или статический helper, создающий per-call с reasonable timeout — например, 5 минут).
   - `SendCommandAsync`:
     - `ArgParser.Parse(argv)` → command-kebab + params dict (как в Dispatcher).
     - Если parse-error — печатаем error-envelope и exit 1.
     - Резолвим session_id через `SessionId.Resolve`.
     - `kernelExe = Environment.ProcessPath` (или fallback из `Process.GetCurrentProcess().MainModule.FileName`).
     - `KernelClient.EnsureRunningAsync(kernelExe, extraArgs:null, httpClient, ct)` → endpoint URL.
     - Собираем `arguments`: `{root: rootPath, session_id?: ...}` плюс все params из argv (как строки). Для CLI params, которые ядру не нужны (`session-id`), нормализуем в snake_case (`session_id`) или пропускаем.
     - `JsonRpcRequest{method:"tools/call", params:{name:command, arguments}}` → POST → `JsonRpcResponse`.
     - Если `Error` — печатаем как stderr-envelope, exit 1.
     - Если `Result` — десериализуем `CallToolResult`, печатаем `Content[0].Text` в stdout (если `IsError != true`) или stderr (если true), exit 0/1 соответственно.
3. **Program.cs**:
   - Удалить блок `if (!PidFileReader.TryReadLivePid(...)) { ... server_not_running ... return 1; }` — теперь kernel сам поднимется.
   - Заменить `return await IpcClient.SendCommandAsync(rootPath, args);` на `return await KernelHttpClient.SendCommandAsync(args, rootPath);`.
4. **Legacy `IpcClient`** — `SessionIdEnvVar`/`SessionIdFlagPrefix`/`ResolveSessionId` заменяем вызовом `SessionId.Resolve`. Сам класс остаётся в коде до step-07.

## Риски

- **Long-running команды (transaction большая)** — HttpClient default timeout = 100 сек. Поднимаем до 5 минут (или вообще `Timeout.InfiniteTimeSpan` для CLI use-case — мы сами CLI, нет внешнего ограничения).
- **Encoding ответа** — JSON-RPC ответ от ядра содержит `text` с CLI-output (UTF-8). Печатаем через `Console.Out` — на Windows уже выставлен UTF-8 в `Program.cs`.
- **stdin для interactive команд** — у нас нет таких; все команды одноразовые, stdin не используется.
- **Ошибки сетевого уровня** — IOException/TimeoutException/HttpRequestException → выводим как `kernel_unreachable` с hint про `kernel_spawn_failed`.
- **kernel_already_running при повторном spawn** — winner spawn'ит kernel, тот пишет kernel.json. Loser в полл-цикле видит kernel.json и подключается. При rare race оба клиента могут spawn'ить — второй увидит «kernel_already_running» от ядра и exit'нет. Loser-клиент же тогда не подключится. Mitigation: KernelClient уже делает 3 итерации с poll'ом — этого достаточно.

## Пост-проверки

1. `dotnet publish` — успех.
2. Удалить старый kernel.json (если есть). Запустить `docswalker get-nodes --root=. --ids=1` — kernel auto-spawn'ится в фоне, ответ возвращается, kernel.json появляется. Повторный вызов — мгновенно (используется существующее ядро).
3. `docswalker check-integrity --root=.` — то же.
4. `docswalker --help`-style ошибочный вызов — error-envelope, exit 1.
5. `docswalker get-nodes --root=. --ids=999999999` — kernel вернёт ошибку (`node_not_found`), CLI выведет stderr + exit 1.
6. Параллельный запуск двух клиентов через PowerShell — оба получают ответ через одно ядро.
