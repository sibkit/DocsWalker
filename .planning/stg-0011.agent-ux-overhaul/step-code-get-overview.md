# stg-0011 — code-get-overview

## Цель

Реализовать в kernel команду `get-overview`: возвращает global stat-картину графа. Поля ответа: `total_nodes`, `max_depth`, `total_tokens`, `trees` (массив объявленных tree-scopes с `addressable`/`default`), `schema.types_count`, `schema.top_types_by_count`, `root_children` с `subtree_tokens`, `hot_spots.largest_nodes` (top-5 по `subtree_tokens`), `hot_spots.most_connected_nodes` (top-5 по `in_refs + out_refs`).

## Файлы

Через `mcp__glider__find_code` — определить структуру kernel-handlers. Ожидаемые:
- Kernel: новый `GetOverviewHandler.cs`.
- Command registry.

## Действия

1. Спроектировать структуру ответа на основе спецификации из шага `docs-add-get-overview`.
2. Реализовать handler — обход графа за один проход с агрегацией метрик.
3. Зарегистрировать команду в dispatcher.
4. Unit-тест на handler.
5. `mcp__glider__get_diagnostics` — чистая компиляция.

## Риски

Подсчёт `total_tokens` требует прохода по всем узлам — для больших корпусов может быть медленным. Сейчас 337 узлов — не проблема, на будущее — кешировать или считать инкрементально (вне scope этого шага).
