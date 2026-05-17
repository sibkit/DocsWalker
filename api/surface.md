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
        "include": ["content", "links"],
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
  "title": "selectors-update",
  "description": "Обновил раздел selectors — добавил пример match.regex.",
  "defaults": {},
  "ops": [
    {
      "update": {
        "id": "2a",
        "expected_version": 7,
        "set": {
          "title": "selectors",
          "content": "..."
        }
      }
    }
  ]
}
```

- `scope` — опциональный. `"usage"` или `"scheme"`. Отсутствие = `main`.
- `title` — обязательный. Свободный текст commit-сообщения, ≤ 100
  токенов. Уходит в `title` создаваемого hist-узла.
- `description` — опциональный. Длинный текст с подробностями (если
  100 токенов на title мало). Без жёсткого лимита.
- `defaults` — опциональный.
- `ops[]` — обязательный массив. Каждый элемент с одним ключом из:
  `create`, `update`, `move`, `delete`, `link`, `unlink`, `rollback`.

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
      "title": "tx-link-example",
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

Для `tx` верхнего уровня успех содержит `id` созданного event-узла
hist:

```json
{
  "result": {
    "id": "a3f1c2",
    "ops": [
      { "id": "c8" }
    ]
  }
}
```

`ops[]` в результате `tx` — массив per-op данных в порядке исходного
запроса. Каждый элемент содержит то, что вернула конкретная операция
(например, `id` от `create`, или пустой объект от `update` без
дополнительных данных). Верхнеуровневый `id` — opaque hex-строка
event-узла `hist/transaction` (он же `tx_id` транзакции в неформальной
терминологии). Если LLM нужны детали применённой транзакции, она
читает их через `read scope=hist` с селектором `{ "id": "<значение>" }`
или `{ "touches_node": "..." }`.

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

Если хотя бы одна операция в `tx.ops[]` не проходит resolve,
concurrency-check (`expected_version`, `expected_count`), schema
constraints или финальную validation, весь запрос возвращает envelope
ошибки. Журнал hist остаётся нетронутым.

Успешная tx атомарно:

1. Применяет все ops к target scope.
2. Записывает в hist один event-узел `hist/transaction` с секциями
   `created` / `changed` / `deleted`, отражающими произошедшее в tx
   (см. [hist-scope.md](hist-scope.md)).
3. Возвращает результат верхнего уровня с `id` нового event-узла и
   per-op данными.

Если запись hist падает (диск, io), tx откатывается, возвращается
`hist_write_failed`.
