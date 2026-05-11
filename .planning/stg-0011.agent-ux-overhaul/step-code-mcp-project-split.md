# stg-0011 — code-mcp-project-split

## Цель

Выделить новый csproj `DocsWalker.Mcp` под MCP-сервер. Перенести из `DocsWalker.Cli` весь код, относящийся к команде `mcp-server` (entry point, stdio↔HTTP мост, MCP tool registration, inputSchema generator). Обновить `DocsWalker.slnx`, `.mcp.json` (новый путь к exe), `CLAUDE.md` (инструкции запуска).

## Файлы

- `src/DocsWalker.Mcp/DocsWalker.Mcp.csproj` — новый.
- `src/DocsWalker.Mcp/Program.cs` — новый entry point.
- `src/DocsWalker.Mcp/...` — перенесённый код mcp-server.
- `src/DocsWalker.Cli/...` — удалённый mcp-связанный код.
- `DocsWalker.slnx` — добавить новый проект.
- `.mcp.json` — обновить `command` и `args`.
- `CLAUDE.md` — обновить пути publish и команду запуска.

## Действия

1. Через `mcp__glider__find_code` — найти типы и файлы в `DocsWalker.Cli`, относящиеся к `mcp-server`.
2. Создать `DocsWalker.Mcp.csproj` (AOT-publish target `win-x64`, по образцу `DocsWalker.Cli.csproj`).
3. Перенести типы через `mcp__glider__move_type` в новый namespace `DocsWalker.Mcp`.
4. Сделать новый `Program.cs` с одной точкой входа (без поддержки команд CLI — только mcp-server).
5. Удалить из `DocsWalker.Cli` все mcp-связанные регистрации команд и файлы.
6. Обновить `DocsWalker.slnx`.
7. `dotnet publish src/DocsWalker.Mcp/... -c Release -r win-x64`.
8. Обновить `.mcp.json` на новый exe-путь.
9. Обновить `CLAUDE.md` (секция «Запуск DocsWalker» / «MCP-интеграция»).
10. Проверить, что Claude Code видит обновлённые MCP tools после рестарта (внешним способом, не из этой сессии).

## Риски

Разрыв ссылок в тестах и `.mcp.json`. Перезапуск Claude Code требуется, чтобы новый mcp-server подцепился. Прогнать существующие тесты на CLI до и после — убедиться, что CLI остаётся рабочим.
