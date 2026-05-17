# stg-0011 — docs-remove-get-map

## Цель

Удалить definition `get_map` (id=21) и example `Карта документации` (id=37) из `docs/`. Обновить упоминания `get-map` в правилах опускания дефолтов и в примере `Дефолты в JSON`.

## Файлы

`docs/` (через MCP):
- id=21 (`get_map` definition) — удалить.
- id=37 (example `Карта документации`) — удалить.
- id=300 (example `Дефолты в JSON`) — обновить text без упоминания get-map.
- id=301 (rule `Опускаемые дефолты`) — обновить text без get-map.

## Действия

1. Проверить через `get-in-refs` для id=21 и id=37: нет ли cross-refs на них. Если есть — `redirect-refs --unlink` или указать на замену.
2. `update-node` id=300 — text без get-map (оставить пример про describe-type).
3. `update-node` id=301 — text без get-map в условии опускания (оставить только get-tree).
4. `delete-nodes ids=21,37`.
5. `check-integrity` — убедиться, что нет dangling_refs и path_orphans_left.

## Риски

Cross-refs на удаляемые узлы из других правил/примеров. Шаг 1 действий — превентивная проверка.
