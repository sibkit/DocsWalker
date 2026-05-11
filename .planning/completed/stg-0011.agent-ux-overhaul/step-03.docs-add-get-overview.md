# stg-0011 — docs-add-get-overview

## Цель

Создать в `docs/` definition новой команды `get_overview` и пример её использования. Команда — глобальная stat-картина хранилища: `total_nodes`, `max_depth`, `total_tokens`, `trees`, `schema.types_count`, `schema.top_types_by_count`, `root_children` с `subtree_tokens`, `hot_spots.largest_nodes`, `hot_spots.most_connected_nodes`.

## Файлы

`docs/` (через MCP):
- Section id=17 (`Операции чтения`) — новый узел type=definition с title `get_overview`.
- Section id=35 (`CLI-интерфейс`) — новый узел type=example с примером в JSON-формате.

## Действия

1. `create-node` type=definition path=17 title=`get_overview` — text с описанием формата ответа и use case (первый вызов в сессии, до `get_usage_guide`).
2. `create-node` type=example path=35 title=`get-overview на старте сессии` — text с примером arguments-JSON и форматом ответа.
3. Проверить через `check-integrity` отсутствие новых ошибок.

## Риски

Нет — чистое добавление, не затрагивает существующие узлы.
