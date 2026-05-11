# stg-0011 — code-mcp-project-split

## Цель

Выделить новый csproj `DocsWalker.Mcp` под MCP-stdio-адаптер. Перенести из `DocsWalker.Cli` только `McpWrapperHandler` (stdio↔HTTP bridge — единственное, что обслуживает команду `mcp-server`). `CommandsToTools` (MCP tool registration + inputSchema generator) — переехать в `DocsWalker.Kernel`, потому что использует его только kernel (RpcDispatcher.HandleListTools/HandleCallToolAsync), а Mcp.exe генерацию tool'ов вообще не дёргает: он просто форвардит JSON-RPC frames. Обновить `DocsWalker.slnx`, `.mcp.json` (новый путь к exe), `CLAUDE.md` (инструкции запуска).

## Файлы

- `src/DocsWalker.Mcp/DocsWalker.Mcp.csproj` — новый (AOT win-x64, ProjectReference → Core + Cli).
- `src/DocsWalker.Mcp/Program.cs` — новый entry point (UTF-8 console + вызов McpWrapperHandler.Run).
- `src/DocsWalker.Mcp/McpWrapperHandler.cs` — перенесён из `DocsWalker.Cli/Cli/Handlers/`.
- `src/DocsWalker.Kernel/CommandsToTools.cs` — перенесён из `DocsWalker.Cli/Mcp/`.
- `src/DocsWalker.Cli/DocsWalker.Cli.csproj` — добавить `InternalsVisibleTo Include="DocsWalker.Mcp"` (Mcp использует ClientConfig + Output как Kernel сейчас).
- `src/DocsWalker.Cli/Program.cs` — убрать ветку `cmd == "mcp-server"` из ранней маршрутизации.
- `src/DocsWalker.Cli/Cli/Commands.cs` — удалить регистрацию команды `mcp-server` (если есть).
- `src/DocsWalker.Cli/Program.cs::Dispatcher.Run` — убрать `case "mcp_server"`.
- `DocsWalker.slnx` — добавить новый проект.
- `.mcp.json` — обновить `command` и `args` (путь на `DocsWalker.Mcp.exe`).
- `CLAUDE.md` — обновить пути publish и команду запуска.

## Действия

1. Через `mcp__glider__find_code` — найти типы и файлы в `DocsWalker.Cli`, относящиеся к `mcp-server`.
2. Создать `DocsWalker.Mcp.csproj` (AOT-publish target `win-x64`, по образцу `DocsWalker.Cli.csproj`; ProjectReference → Core + Cli).
3. Перенести типы через `mcp__glider__move_type`:
   - `McpWrapperHandler` → `DocsWalker.Mcp` namespace.
   - `CommandsToTools` → `DocsWalker.Kernel` namespace.
4. Сделать новый `Program.cs` в `DocsWalker.Mcp` с одной точкой входа (UTF-8 console + парсинг `--quiet=true` + `McpWrapperHandler.Run`).
5. Удалить из `DocsWalker.Cli`: ветку `mcp-server` в `Program.cs`, регистрацию команды `mcp-server` в `Commands.cs` (если есть), кейс в `Dispatcher.Run`, пустую папку `Mcp/`.
6. Добавить `InternalsVisibleTo Include="DocsWalker.Mcp"` в `DocsWalker.Cli.csproj`.
7. Обновить `DocsWalker.slnx`.
8. `dotnet build` — проверить green. `dotnet test` — проверить 152 baseline.
9. `dotnet publish src/DocsWalker.Mcp/... -c Release -r win-x64`.
10. Обновить `.mcp.json` на новый exe-путь.
11. Обновить `CLAUDE.md` (секция «Запуск DocsWalker» / «MCP-интеграция» / «Один раз — собрать бинари»).
12. Проверить, что Claude Code видит обновлённые MCP tools после рестарта (внешним способом, не из этой сессии).

## Риски

Разрыв ссылок в тестах и `.mcp.json`. Перезапуск Claude Code требуется, чтобы новый mcp-server подцепился. Прогнать существующие тесты на CLI до и после — убедиться, что CLI остаётся рабочим.
