# stg-0002 — R15 tree-scopes-read-api

## Цель
Обобщить read-API под tree-scopes: `get_subtree` / `get_ancestors` принимают параметр `tree`. По умолчанию `path` — обратная совместимость с текущими вызовами. Для других scope'ов работают по тому же индексу из R14.

## Файлы
- `src/DocsWalker.Core/Api/ReadApi.cs`:
  - `GetSubtree(int rootId, string tree = "path")` — обход вниз через `Graph.GetChildren(_, tree)`. Возвращает плоский список или вложенную структуру (см. ниже).
  - `GetAncestors(int nodeId, string tree = "path")` — обход вверх через `Graph.GetParent(_, tree)`. Возвращает список от ближайшего родителя до корня.
  - `GetByPath(string path)` — без изменений (он специфичен для path-tree, остаётся как есть, имя оставить).
  - `ListDocuments()` — **снять** (документы достаются обходом `GetSubtree(0, tree="path")` с фильтром по типу `document`). Если кому-то всё ещё нужно — добавить в usage-guide рецепт.
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — сериализация `tree` параметра в ответах get_subtree / get_ancestors (поле `tree` в результате для подтверждения, по какому scope обходили).
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — добавить `--tree=<scope>` опциональным параметром в `get-subtree` и `get-ancestors` CLI-команды; снять команду `list-documents`.
- `src/DocsWalker.Cli/Cli/Commands.cs` — снять `list-documents`, обновить описания get-subtree/get-ancestors.

## Действия
1. Расширить методы ReadApi параметром `tree`. Default `"path"`.
2. Если `tree` неизвестен (нет в Schema.Trees) — `read_api_exception` с кодом `unknown_tree` и hint «доступные scope'ы: <list>».
3. Снять `ListDocuments` из ReadApi и CLI.
4. Обновить ReadApiJson: формат ответа get_subtree содержит поле `tree` (по какому обходили) + дерево узлов (вложенный или плоский — оставить как сейчас, не менять форму).
5. CLI: `--tree=<name>` опционально; без него — path.

## Тесты
- `tests/.../ReadApiSubtreeTests.cs` — get_subtree с явным tree=path и tree=<other> на синтетической схеме с двумя scope'ами.
- `tests/.../ReadApiAncestorsTests.cs` — то же для ancestors.
- `tests/.../ReadApiUnknownTreeTests.cs` — несуществующий scope → ошибка с правильным кодом.
- Все существующие тесты get_subtree/get_ancestors без явного tree — должны проходить (default `"path"`).

## Риски
- Снятие `list-documents` ломает зовущих; в этой стратегии auto-режим, без shim, но проверить, что check-integrity / другие read-команды не зовут ListDocuments внутри.
- В синтетических тестах нужна возможность объявить второй scope. Можно сделать в `tests/Fixtures/` отдельную минимальную Схему с двумя деревьями и грузить её.
