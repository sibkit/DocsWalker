# stg-0011 — docs-examples-json-migration

## Цель

Переписать `text` всех `example`-узлов в `docs/`, у которых сейчас CLI-формат (`docswalker X --a=b`), на JSON-формат вида `<tool-name> {arguments-json}`. Это финальный шаг этапа спецификации API: примеры начинают отражать актуальный канал (MCP), а не deprecated CLI.

## Файлы

`docs/` (через MCP) — все узлы type=example, чей text содержит `docswalker `.

## Действия

1. `search --query=docswalker ` — собрать список example-узлов с CLI-командами.
2. Для каждого узла — определить эквивалент в JSON-формате: имя tool в kebab-case + объект arguments. Пример: `docswalker get-nodes --ids=1,8,42` → `get-nodes {"ids": [1, 8, 42]}`.
3. Пакетный `transaction` с массивом `update-node` для всех затрагиваемых узлов.
4. Финальный `search --query=docswalker ` — должен вернуть пустой массив, если все примеры мигрированы.

## Риски

Большой объём (десятки узлов). Семантические нюансы CLI-команд (boolean-флаги типа `--dry-run=true` → `"dry_run": true`, csv-параметры в массивы). Делать через `transaction` с `--dry-run=true` сначала, потом без.
