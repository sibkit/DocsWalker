# stg-0005 — mcp-server

## Цель
MCP-сервер как параллельный транспорт поверх ядра: JSON-RPC 2.0 над stdio, регистрация всех read/write команд как MCP-tools, session_id из MCP-protocol context. CLI остаётся живым (тот же бинарь, разные точки входа).

## Файлы
`src/DocsWalker.Core/Mcp/JsonRpcServer.cs` — минимальный JSON-RPC 2.0 движок (request, response, error), AOT-совместимый, на System.Text.Json source-генераторах.
`src/DocsWalker.Core/Mcp/McpProtocol.cs` — MCP-конкретика: `initialize`, `tools/list`, `tools/call`, `shutdown`.
`src/DocsWalker.Core/Mcp/ToolRegistry.cs` — статическая регистрация всех команд из CLI Dispatcher как MCP-tools с JSON-Schema-описанием параметров.
`src/DocsWalker.Cli/Program.cs` — новая точка входа `docswalker mcp-server` (отдельная команда, ветвится в McpServer вместо Dispatcher).
`docs/DocsWalker.yml` — финализация описания MCP-tools (если расходятся с CLI-командами; идеально — 1:1).

## Действия
1. Минимальный JSON-RPC 2.0 поверх stdio: чтение newline-delimited JSON из stdin, ответы в stdout. Source-gen JsonSerializerContext для всех типов ((#173)).
2. MCP-handshake: `initialize` запрос с параметрами клиента; ответ с серверными capabilities. session_id берётся из initialize-параметра (MCP-клиент шлёт) или из per-call meta — финал решается в шаге spec-in-docs.
3. `tools/list` — отдаёт manifest всех команд из текущего Dispatcher: имя tool = kebab-case CLI-команды, описание + JSON-Schema параметров (та же информация, что в `get-usage-guide`).
4. `tools/call` — приходит `{name, arguments}` → собирается тот же argv, что у CLI → проходит через тот же `Dispatcher.Run` → ответ маршалится в MCP-формат (success-объект CLI или MCP-error для ошибки).
5. session_id из MCP-context идёт в RequestContext так же, как у CLI.
6. `shutdown` — graceful release lock, save sessions, exit.
7. Команда запуска: `docswalker mcp-server --root=<path>` — новая команда верхнего уровня, не идёт через client-mode (как и `run`). Захватывает тот же lock на docs/, но слушает stdio вместо named pipe / Unix socket.
8. Параллельный запуск `run` и `mcp-server` на одном root: оба берут lock, второй упадёт с `server_already_running` (single-root-per-process (#309)). Это feature.

## Риски
- AOT-совместимость JSON-RPC поверх stdio. System.Text.Json source-gen покрывают сериализацию; рефлексия не используется. Если попадётся MCP-feature, требующая dynamic JSON, — придётся вводить минимальный wrapper.
- Два long-lived сервера на одном host'е (CLI-`run` для одного workspace, `mcp-server` для другого) — поддерживается; ресурсы на root-hash namespace'ятся.
