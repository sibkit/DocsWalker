# stg-0011 — code-mcp-tools-update

## Цель

Обновить MCP tool registrations в `DocsWalker.Mcp.exe`: отразить переименование `get-subtree` → `get-tree`, удаление `get-map`, добавление `get-overview`, новые параметры в `search` / `get-tree` / `get-nodes`.

## Файлы

Через `mcp__glider__find_code`:
- `src/DocsWalker.Mcp/Tools/*.cs` (или эквивалент после выноса в шаге `code-mcp-project-split`).

## Действия

1. Снять регистрацию `get-subtree`, добавить регистрацию `get-tree`.
2. Снять регистрацию `get-map`.
3. Добавить регистрацию `get-overview` (без параметров).
4. Обновить inputSchema для `search`: добавить `in`, `type`, `tree`, `under`, `regex`, `limit`, `compact`.
5. Обновить inputSchema для `get-tree`: добавить `compact`, `max_tokens`.
6. Обновить inputSchema для `get-nodes`: добавить `compact`, `max_tokens`.
7. Проверить через рестарт Claude Code, что новые tools видны.

## Риски

Несоответствие inputSchema реальному поведению handler'ов → MCP-ошибки. Сверять с реализацией шагов `code-search-v2-bm25` и `code-compact-and-tokens`.
