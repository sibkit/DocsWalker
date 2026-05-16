# Поверхность API

DocsWalker JSON API имеет два MCP-tools: `read` и `tx`. Любая работа
LLM с графом проходит через один из них.

## `read`

Read-only метод. Читает узлы из выбранного scope по predicate-селекторам.

Аргументы:

```json
{
  "scope": "usage",
  "defaults": {},
  "ops": [
    {
      "select": {
        "selector": {
          "path": "rules/api/**"
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

- `scope` — опциональный. Допустимые значения: `"usage"`, `"hist"`,
  `"scheme"`. Отсутствие = `main`.
- `defaults` — опциональный (см. [model.md](model.md)).
- `ops[]` — обязательный массив. Каждый элемент с одним ключом `select`.

Подробности — [read.md](read.md) и [selectors.md](selectors.md).

## `tx`

Write-метод. Атомарно применяет набор изменений к выбранному editable
scope.

Аргументы:

```json
{
  "commit_message": "обновить раздел selectors",
  "read_ids": [],
  "defaults": {},
  "ops": [
    {
      "update": {
        "id": "2a",
        "set": {
          "title": "selectors",
          "value": "..."
        }
      }
    }
  ]
}
```

- `scope` — опциональный. `"usage"` или `"scheme"`. Отсутствие = `main`.
- `commit_message` — обязательный. Краткое описание изменения для hist
  log. Не больше 100 токенов.
- `read_ids` — опциональный массив opaque-receipts, см.
  [read-gates.md](read-gates.md).
- `defaults` — опциональный.
- `ops[]` — обязательный массив. Каждый элемент с одним ключом из:
  `select` (для объявления alias), `create`, `update`, `move`, `delete`,
  `link`, `unlink`, `rollback`.

Подробности — [tx.md](tx.md).

## Транспорт

Поверх kernel JSON-RPC через MCP `tools/call`:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
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

Если операция возвращает полезные данные, `result` содержит их напрямую.
Для одной операции `result` — её объект данных. Для нескольких `result`
— массив объектов в порядке `ops[]`. Если все операции возвращают
пустые объекты, `result = "ok"`.

Для `read.select` — счётчик, список узлов и (при truncation) маркеры
обрезания. Для `tx.create` — id созданного узла:

```json
{
  "result": {
    "id": "c8"
  }
}
```

Для `tx` верхнего уровня успех содержит `tx_id`:

```json
{
  "result": {
    "tx_id": "a3f1c2",
    "ops": [
      { "id": "c8" }
    ]
  }
}
```

`ops[]` в результате `tx` — массив per-op данных в порядке исходного
запроса. Каждый элемент содержит то, что вернула конкретная операция
(например, `id` от `create`, или пустой объект от `update` без
дополнительных данных). `tx_id` — opaque hex-строка. Если LLM нужны
детали применённой транзакции, она читает их через
`read scope=hist` с фильтром по этому `tx_id`.

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

- `code` — машинная строка из реестра в [errors.md](errors.md).
- `details` — объект с подробностями. Структура зависит от кода и
  включает либо `path` (JSON-pointer-подобный путь к месту ошибки в
  запросе), либо тематические поля кода.

Ошибка любой операции `tx` приводит к атомарному отказу всей tx: ни
одно изменение не применяется, hist-запись не создаётся.

## Атомарность tx

Если хотя бы одна операция в `tx.ops[]` не проходит resolve, schema
constraints, read gates или финальную validation, весь запрос
возвращает envelope ошибки. Журнал hist остаётся нетронутым.

Успешная tx атомарно:

1. Применяет все ops к target scope.
2. Записывает в hist одну `hist/transaction`-узел + по одному
   `hist/change` на каждое изменение.
3. Возвращает результат верхнего уровня с `tx_id` и per-op данными.

Если запись hist падает (диск, io), tx откатывается, возвращается
`hist_write_failed`.
