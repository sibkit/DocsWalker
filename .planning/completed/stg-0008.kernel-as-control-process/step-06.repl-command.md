# stg-0008 — step-06 — repl-command

## Цель

Добавить команду `repl` — интерактивный HTTP-клиент к ядру. После старта:
- Spawn/reuse `DocsWalker.Kernel.exe` через `KernelClient.EnsureRunningAsync`.
- В цикле: `prompt → readline → ReplTokenizer → tools/call` (с фиксированным root из `--root=` и фиксированным session_id REPL-сессии) → печать `Content[0].Text` (или stderr при isError).
- Выход: `:quit` / `:exit` / Ctrl+D (EOF) / Ctrl+C.

Команда `run` пока остаётся (для backward-compat и для тех, кто привык; удалится в step-07 cleanup-old-ipc) — это server-side, не выкидывать сейчас.

## Файлы

- `src/DocsWalker.Cli/Cli/Commands.cs` — добавить spec `repl` (Required `--root`).
- `src/DocsWalker.Cli/Program.cs`:
  - в server-side gate (`if (cmd == "run" || cmd == "mcp-server")`) добавить `cmd == "repl"`,
  - в `Dispatcher.Run` switch — `"repl" → ReplHandler.Run(rootPath, parsed.Params)`.
- `src/DocsWalker.Cli/Cli/Handlers/ReplHandler.cs` (новый) — HTTP-REPL.

## Действия

1. **Spec `repl`** — Required `--root`, Optional `--quiet`. Description: «Интерактивный HTTP-REPL к ядру. Каждая команда уходит в kernel /rpc как tools/call с фиксированным root».
2. **Routing** — server-side gate включает `repl`; Dispatcher.Run switch вызывает `ReplHandler.Run`.
3. **`ReplHandler.Run`**:
   - Резолвим kernel exe (`KernelSpawner.ResolveKernelExePath`).
   - `HttpClient` 5min timeout.
   - `KernelClient.EnsureRunningAsync` → endpoint.
   - Banner на stderr: `DocsWalker REPL: root=<root>, kernel=<url>, session=<uuid>`.
   - Banner-help: `Команды — без префикса 'docswalker'. Выход — :quit/:exit/Ctrl+D.`
   - Loop:
     - prompt `dw> `
     - readline через `LineReader.ReadLine` (cancel via Ctrl+C)
     - на :quit/:exit/EOF — break
     - tokenize через `ReplTokenizer`
     - первый токен = command, остальные → params dict
     - сборка `tools/call` (как у `KernelHttpClient.SendCommandAsync`)
     - POST на /rpc → парс CallToolResult → печать stdout/stderr
4. **session_id** — генерим один на REPL-запуск (Guid). Подмешиваем в каждый `tools/call.arguments.session_id`. Сохраняем seen-state на стороне ядра (когда ядро будет интегрировать SessionState).

## Риски

- **Дубликаты с `KernelHttpClient`** — оба собирают одинаковую JSON-RPC envelope. Тоже вкладываем в KernelHttpClient или в helper `RpcCallBuilder`. На step-06 копируем inline; рефакторинг — отдельный микрошаг при необходимости.
- **Ctrl+C поведение в REPL** — старый `LineReader` корректно обрабатывал отмену строки vs выход. Переиспользуем как-есть.

## Пост-проверки

1. `dotnet publish` (Cli + Kernel) — успех.
2. `DocsWalker.Cli.exe repl --root=.` — REPL поднимается, kernel auto-spawn'ится.
3. В REPL: `check-integrity` → `{"ok":true,"errors":[]}`.
4. В REPL: `get-nodes --ids=1` → корректный JSON узла.
5. `:quit` → REPL выходит чисто; ядро остаётся живым (echo `curl /health`).
