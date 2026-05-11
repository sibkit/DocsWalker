# stg-0011 — docs-search-spec

## Цель

Переписать definition узла `search` (id=26) в `docs/` под расширенный контракт: новые параметры, BM25-ранжирование, поля результата. Добавить примеры использования. Это первый шаг этапа спецификации API v2 в `docs/`.

## Файлы

`docs/` (через MCP `update-node` и `create-node`) — затрагиваемые узлы:
- id=26 (`search` definition) — переписать `text`.
- Section id=17 (`Операции чтения`) — место для новых example-узлов.
- Section id=35 (`CLI-интерфейс`) — альтернативное место для примеров.

## Действия

1. `update-node` id=26 — переписать `text`: перечисление параметров (`query`, `in`, `type`, `tree`, `under`, `regex`, `limit`, `compact`), описание BM25-ранжирования с boost по title-hit ×3, формат результата (поля `id`, `type`, `title`, `score`, `snippet`).
2. `create-node` — новые `example`-узлы под сценарии: BM25-релевантность, фильтр `in=title`, фильтр `type=rule`, фильтр `tree=path` + `under=<id>`, regex-режим. Примеры — в JSON-стиле (`<tool-name> {arguments}`), формат закрепится в шаге `docs-examples-json-migration`.
3. Sweep по узлам, которые могут устаревать по упоминанию старого поведения `search`, через `search --query=search`. Точечные `update-node`.

## Риски

Старые упоминания `search` в text других узлов (CLI-секция, mental-model). Sweep через `search` нивелирует.
