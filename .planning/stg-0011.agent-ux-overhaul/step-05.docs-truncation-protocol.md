# stg-0011 — docs-truncation-protocol

## Цель

Создать в `docs/` правило с описанием truncation-протокола для read-команд при превышении `max_tokens`. Зафиксировать поля ответа: `truncated`, `stopped_at` (массив `{parent_id, remaining_children, next_offset}`), `tokens_used`, `tokens_budget`. Default `max_tokens=50000`. Приложить пример усечённого ответа.

## Файлы

`docs/` (через MCP):
- Новый узел type=rule в section id=17 (`Операции чтения`) или id=35 (`CLI-интерфейс`).
- Новый узел type=example — иллюстрация усечённого ответа.

## Действия

1. Решить, где правилу место — `Операции чтения` (рядом с definition'ами get-tree/get-nodes) ближе по смыслу.
2. `create-node` type=rule title=`Truncation-протокол` text=описание полей, алгоритма (BFS по depth, верхние уровни целиком, нижние пока влезают), default 50000.
3. `create-node` type=example title=`Усечённый ответ get-tree` text=пример JSON-ответа с полями truncated/stopped_at/tokens_used/tokens_budget.
4. Если в Схеме у rule есть required `examples` — `create-ref` от rule к example.

## Риски

Нарушение required-связи rule.examples — устраняется шагом 4.
