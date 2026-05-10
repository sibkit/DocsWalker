# stg-0008 — step-05 — mcp-wrapper

## Цель

Переписать команду `mcp-server` как тонкий **stdio↔HTTP bridge** к ядру `DocsWalker.Kernel.exe`. Никакой бизнес-логики; pure forwarding с подстановкой фиксированного `root` (из `--root=` аргумента wrapper'а) во все `tools/call`.

Архитектура (strategy.md decision #11, step-05):
- Claude Code запускает `docswalker mcp-server --root=<path>` через `.mcp.json`.
- Wrapper читает JSON-RPC frames из stdin, форвардит на `POST /rpc` ядра.
- Для `tools/call` — подмешивает в `arguments` ключ `root` (если LLM не задал) и `session_id` (генерится при `initialize`).
- Ответ ядра пишется в stdout как-есть.
- Если ядро не запущено — wrapper auto-spawn'ит `DocsWalker.Kernel.exe` через тот же `KernelClient.EnsureRunningAsync`, что и CLI.

Старый `McpServer` класс (Core) **остаётся** до step-07 (cleanup-old-ipc).

## Файлы

- `src/DocsWalker.Cli/Cli/Handlers/McpWrapperHandler.cs` (новый) — main entry для команды `mcp-server`.
- `src/DocsWalker.Cli/Program.cs` — Dispatcher.Run: для `mcp_server` вызывать `McpWrapperHandler.Run` вместо старого `McpServerHandler.Run`.
- `src/DocsWalker.Cli/Cli/Handlers/McpServerHandler.cs` — пока остаётся, но не вызывается. Удалится в step-07.
- `src/DocsWalker.Core/Server/Protocol/Frame.cs` — переиспользуется (newline-delimited JSON-RPC frames).

## Действия

1. **`McpWrapperHandler.Run(rootPath, args)`**:
   - Парс `quiet` флаг (как у старого McpServerHandler).
   - Резолв kernel exe через `KernelSpawner.ResolveKernelExePath(Environment.ProcessPath)`.
   - `HttpClient` с RequestTimeout=5min.
   - `KernelClient.EnsureRunningAsync(kernelExe, http, ct)` → endpoint URL. Если упало — на stderr строку и exit 1.
   - Stderr-banner (если не quiet): `DocsWalker MCP-wrapper started: root=<root>, kernel=<url>, pid=<own pid>`.
   - Генерим `session_id = Guid.NewGuid().ToString()` при первом `initialize` (или сразу).
   - Loop: `Frame.ReadLineAsync(stdin)` → парс JSON → если `tools/call`: подмешать `root` и `session_id` → POST на `<kernelUrl>/rpc` → `Frame.WriteAsync(stdout, responseJson)`. Notifications (без id) — не возвращаем ответ.
   - Поддерживаемые методы прокидываются 1:1: `initialize`, `notifications/initialized`, `tools/list`, `tools/call`, `shutdown`, `notifications/cancelled`. Любой другой метод тоже форвардится — ядро ответит `MethodNotFound`.
   - Цикл живёт до EOF на stdin (Claude Code закрыл pipe) либо SIGINT.
   - На exit — graceful close `HttpClient`. Ядро НЕ останавливаем (оно живёт независимо для других клиентов).
2. **Подмешивание `root` и `session_id`**:
   - `initialize` — фиксируем `session_id = Guid.NewGuid()`. Ядру params не подмешиваем (там нет root).
   - `tools/call` — в `params.arguments` прописываем `root: <wrapper's --root>` (overwrite если LLM передал свой) и `session_id: <wrapper's session>`. Ключи в snake_case (как ожидает RpcDispatcher).
   - `tools/list` — опц. подмешиваем `params.root` чтобы ядро вернуло корректную inputSchema для create-node (динамические схемы из проектной Схемы).
   - Прочие — без модификации.
3. **Routing в Program.cs Dispatcher.Run**: `mcp_server` → `McpWrapperHandler.Run(rootPath, parsed.Params)` вместо старого `McpServerHandler.Run`.
4. **Тест**: запустить wrapper из терминала, ввести вручную JSON-RPC `initialize`, `tools/list`, `tools/call check-integrity` — увидеть корректные ответы. Убедиться, что ядро видит запрос с подмешанным root.

## Риски

- **Race на initialize** — Claude Code сначала шлёт `initialize`, потом первый `tools/call`. Если wrapper генерит session_id в response на initialize, у первого tools/call session_id уже есть. OK.
- **Ядро упало посреди сессии** — wrapper словит HttpRequestException на POST /rpc. Простейший fallback: вернуть `JsonRpcError(InternalError, "kernel unreachable: <msg>")`. LLM получит ошибку, retry или сдастся. Полноценный re-spawn посреди сессии — over-engineering для step-05; добавим если потребуется.
- **stdin EOF до initialize** — Claude Code может закрыть pipe сразу. Loop корректно exit'ится.
- **session_id collision** — Guid v4 коллизий нет.
- **Кодировка** — JSON-RPC frames newline-delimited UTF-8. `Frame.WriteAsync` это делает. OK.

## Пост-проверки

1. `dotnet publish` — обе сборки (Cli, Kernel) успех.
2. `DocsWalker.Cli.exe mcp-server --root=. --quiet=true` — wrapper запускается, ждёт stdin.
3. Echo через wrapper:
   ```
   {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
   ```
   → корректный envelope с capabilities.
4. `tools/list` через wrapper — список tools (идентичен прямому /rpc).
5. `tools/call` с `arguments={ids:"1"}` без root — wrapper подмешивает root, ядро отвечает.
6. Через `.mcp.json` Claude Code: tool работает.
