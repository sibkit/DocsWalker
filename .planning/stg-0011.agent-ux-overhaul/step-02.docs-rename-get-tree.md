# stg-0011 — docs-rename-get-tree

## Цель

Переименовать definition `get_subtree` (id=302) в `get_tree` в `docs/`, добавить в `text` параметры `compact` и `max_tokens`. Обновить все текстовые упоминания `get-subtree` в других узлах документации (минимум 8 узлов, найденных через `search`).

## Файлы

`docs/` (через MCP):
- id=302 (`get_subtree` definition) — title и text.
- id=278 (definition `root`) — текст про обход.
- id=279 (example `Цепочка перед записью`).
- id=280 (rule `Read → describe → write`).
- id=285 (example `Удаление поддерева вручную`).
- id=286 (rule `delete-nodes без авто-каскада`).
- id=301 (rule `Опускаемые дефолты`).
- id=340 (rule `Auto-include`).
- id=388 (example `get-by-path с --tree=`).
- id=394 (rule `get-by-path только с addressable`).

## Действия

1. `update-node` id=302 — `title=get_tree`, `text` с описанием параметров (`id`, `tree`, `depth`, `fields`, `compact`, `max_tokens`), формат результата с `tokens` / `subtree_tokens` / `children`.
2. Sweep через `search --query=get-subtree` — выявить все упоминания. Для каждого — `update-node` с заменой `get-subtree` → `get-tree` и `get_subtree` → `get_tree`.
3. Контрольный повторный `search --query=get-subtree` — должен вернуть пустой массив.

## Риски

Пропуск редко упоминаемых узлов. Контрольный финальный `search` страхует.
