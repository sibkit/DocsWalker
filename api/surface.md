# Поверхность API

DocsWalker JSON API имеет **два** MCP-tools: `read` и `tx`. Любая работа
LLM с графом проходит через один из них.

## `read`

Read-only метод. Читает узлы из выбранного scope по predicate-селекторам.

Аргументы:

```json
{
  "scope": "main",
  "defaults": {},
  "ops": [
    {
      "select": {
        "selector": {
          "node.path": "DocsWalker/**"
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

- `scope` — обязательный. `"main"` / `"usage"` / `"hist"` / `"scheme"`.
- `defaults` — опциональный (см. [model.md](model.md)).
- `ops[]` — обязательный массив, каждый элемент с ровно одним ключом
  операции. Допустима только операция `select`.

Подробности — [read.md](read.md) и [selectors.md](selectors.md).

## `tx`

Write-метод. Атомарно применяет набор изменений к выбранному editable
scope.

Аргументы:

```json
{
  "scope": "main",
  "commit_message": "обновить раздел selectors",
  "read_ids": [],
  "defaults": {},
  "ops": [
    {
      "update": {
        "id": 42,
        "set": {
          "title": "selectors",
          "value": "..."
        }
      }
    }
  ]
}
```

- `scope` — опциональный. `"usage"` или `"scheme"`. Отсутствие = `"main"`.
  `"hist"` отклоняется как `hist_read_only`.
- `commit_message` — обязательный. Краткое описание изменения для hist
  log. Не больше 100 токенов.
- `read_ids` — опциональный массив opaque-receipts, см.
  [read-gates.md](read-gates.md).
- `defaults` — опциональный (см. [model.md](model.md)).
- `ops[]` — обязательный массив. Каждый элемент содержит ровно один ключ
  из: `select` (для объявления alias), `create`, `update`, `move`,
  `delete`, `link`, `unlink`, `rollback`.

Подробности — [tx.md](tx.md).

## Транспорт

Поверх kernel JSON-RPC через MCP `tools/call`:

```json
{
  "jsonrpc": "2.0",
  "id": 42,
  "method": "tools/call",
  "params": {
    "name": "tx",
    "arguments": {
      "scope": "usage",
      "commit_message": "добавить usage/example для tx-link",
      "ops": []
    }
  }
}
```

`params.name` выбирает метод (`read` или `tx`). Внутри `params.arguments`
поле `method` отсутствует.

## Envelope успеха

Успешный ответ имеет одну форму для всех методов:

```json
{
  "result": "ok"
}
```

Если операции возвращают полезные данные, `result` содержит их напрямую:

- При одной операции `ops[0]` с непустым результатом `result` — объект
  данных этой операции.
- При нескольких операциях `result` — массив объектов в порядке `ops`.
- При пустых результатах всех операций `result = "ok"`.

Для read это означает результат `select` (count, samples, ids, или
полные узлы — в зависимости от `include`). Для tx успешный результат
содержит `tx_id`:

```json
{
  "result": {
    "tx_id": "tx_20260514T101530123Z_7F3A91C2"
  }
}
```

`tx_id` — opaque-string. Если LLM нужны детали применённой транзакции,
она читает их через `read scope=hist` с фильтром по этому `tx_id`.

## Envelope ошибки

Любая ошибка возвращается единым форматом:

```json
{
  "code": "not_found",
  "details": {
    "path": "$.ops[0].update.id"
  }
}
```

- `code` — машинная строка из набора, описанного в [errors.md](errors.md).
- `details` — объект с подробностями. Структура зависит от кода, но всегда
  включает либо `path` (JSON-pointer-подобный путь к месту ошибки в
  запросе), либо тематические поля кода.

Ошибка любой операции `tx` приводит к атомарному отказу всей tx — ни
одно изменение не применяется, hist не пишется.

## Атомарность tx

Если хотя бы одна операция в `tx.ops[]` не проходит resolve, schema
constraints, read gates или финальную validation, **весь запрос**
возвращает envelope ошибки. Журнал hist не получает ни одной записи
этой попытки.

Успешная tx атомарно:

1. Применяет все ops к target scope.
2. Записывает в hist одну `hist/transaction`-узел + по одному `hist/change`
   на каждое изменение.
3. Возвращает `tx_id` верхнего уровня.

Если запись в hist падает (например, диск переполнен), tx считается
не примененной — изменения откатываются, возвращается `hist_write_failed`.
