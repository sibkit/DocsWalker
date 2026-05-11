# stg-0011 — code-api-v2-tests

## Цель

Покрыть новый API v2 тестами через MCP-канал (не CLI). Старые e2e через CLI оставляем как есть — они валидируют deprecated канал. Новые тесты — для нового MCP-канала с новыми именами команд и параметрами.

## Файлы

- `tests/DocsWalker.Tests/Mcp/Api/GetTreeTests.cs` — переименование и параметры.
- `tests/DocsWalker.Tests/Mcp/Api/GetOverviewTests.cs` — новая команда.
- `tests/DocsWalker.Tests/Mcp/Api/SearchV2Tests.cs` — BM25, фильтры, regex, snippet, score.
- `tests/DocsWalker.Tests/Mcp/Api/CompactAndTokensTests.cs` — флаг compact, max_tokens с truncation.
- `tests/DocsWalker.Tests/Mcp/Api/GetMapRemovedTests.cs` — `get-map` возвращает `unknown_command`.

## Действия

1. Завести test fixture с in-process kernel + MCP-bridge.
2. Тесты:
   - `get-tree` принимает все параметры (id, tree, depth, fields, compact, max_tokens).
   - `get-overview` возвращает все ожидаемые поля.
   - `search` ранжирует по BM25, boost'ит title-hit, фильтрует по type/tree/under/in, поддерживает regex и limit.
   - `compact=true` в `get-tree` и `get-nodes` урезает поля.
   - `max_tokens` с низким значением активирует truncation-протокол.
   - `get-map` отвергнут как `unknown_command`.
3. Прогон через CI.

## Риски

Setup MCP-канала в тестах может потребовать stdio-mocking. Использовать прямой in-process вызов kernel-handler'ов, минуя stdio, если bridge слишком тяжёл для unit-тестов.
