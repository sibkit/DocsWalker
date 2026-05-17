# stg-0011 — code-compact-and-tokens

## Цель

Реализовать в kernel флаг `compact` и параметр `max_tokens` с truncation-протоколом для команд `get-tree` и `get-nodes`. `compact=true` — alias для `fields=id,type,title`. `max_tokens` с default 50000 — бюджет ответа, при превышении срабатывает BFS-по-глубине усечение с возвратом полей `truncated`, `stopped_at`, `tokens_used`, `tokens_budget`.

## Файлы

Через `mcp__glider__find_code`:
- Kernel: `GetTreeHandler.cs` (после переименования из GetSubtree), `GetNodesHandler.cs`.
- Новый: `TruncationContext.cs` — accumulator для tokens_used и stopped_at.

## Действия

1. Расширить параметры handler'ов: `compact` (bool), `max_tokens` (int, default 50000).
2. `compact=true` → внутренне эквивалентно `fields=id,type,title`.
3. Реализовать BFS-по-глубине: собирать узлы уровень за уровнем, на каждом — проверять, что bucket токенов влезает целиком; если не влезает — сохранить остаток в `stopped_at`.
4. Включить поля `truncated`, `stopped_at` (массив `{parent_id, remaining_children, next_offset}`), `tokens_used`, `tokens_budget` в ответ только если усечение произошло.
5. Unit-тесты на маленьком корпусе с искусственно низким `max_tokens` (например 100).

## Риски

Алгоритм BFS-усечения может застрять на верхнем уровне, если он сам по себе больше `max_tokens`. В этом случае возвращать минимум — корень в compact-форме и `truncated=true` со списком всех его детей в `stopped_at`. Зафиксировать в тестах.
