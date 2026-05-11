# stg-0011 — code-api-rename-and-remove

## Цель

В коде ядра DocsWalker.Kernel: переименовать команду `get-subtree` → `get-tree` (включая внутренний handler-класс, имя API-метода, registration в dispatcher). Удалить команду `get-map` и все её следы. Сохранить старый алиас `get-subtree` на одну итерацию ради обратной совместимости НЕ требуется — пользователь явно ушёл от CLI, новый MCP сразу использует новые имена.

## Файлы

Через `mcp__glider__find_code` определить точно. Ожидаемые:
- Kernel handler `GetSubtreeHandler.cs` (или подобное) → переименовать.
- Kernel handler `GetMapHandler.cs` → удалить.
- Command registry / dispatcher — registration.

## Действия

1. `mcp__glider__find_code` запросом `GetSubtree` — найти все вхождения.
2. `mcp__glider__rename_symbol` — переименовать класс/метод GetSubtree → GetTree.
3. `mcp__glider__find_code` запросом `GetMap` — найти GetMap-handler и его registration.
4. Удалить GetMap-handler и unregister.
5. `mcp__glider__get_diagnostics` — убедиться, что компиляция чистая.
6. Старые e2e CLI-тесты не трогать — они гоняются через CLI, который пока живёт. Если есть kernel unit-тесты на GetMap — удалить.

## Риски

Тесты на kernel-уровне с прямыми вызовами GetMap — упадут. Удалить или обновить.
