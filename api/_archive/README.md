# DocsWalker JSON API

`api/` является source of truth для описания JSON API DocsWalker. Если поведение
API, формат запроса, формат ответа, операция или код ошибки меняются, сначала
обновляется этот каталог, затем код и тесты.

Первичная версия этих файлов перенесена из docs-документа `DocsWalker-LLM JSON API`.
Для JSON API при конфликте актуален `api/`.

## Назначение

DocsWalker-LLM JSON API v1 описывает единый формат JSON-запросов, через который
LLM читает, проверяет и изменяет граф DocsWalker без ручной сборки
низкоуровневых операций хранения.

Внешняя поверхность v1 состоит из пяти методов:

- `query` - чтение данных по selector-ам.
- `hist` - чтение истории по selector-ам.
- `usage` - чтение инструкций и схем по selector-ам.
- `tx` - атомарное внесение изменений.
- `scheme` - read-only чтение контракта Схемы.

Все MCP tools этого JSON API принимают в `params.arguments` верхнеуровневый
массив `ops[]`. Каждый элемент `ops[]` является объектом с ровно одним ключом
операции, например `{ "select": { ... } }` или `{ "update": { ... } }`.

## Транспорт

Основной LLM-канал - MCP `tools/call` поверх kernel JSON-RPC.
Метод API выбирается именем MCP tool в `params.name`. Внутри
`params.arguments` поле `method` не передается: там лежат только аргументы
выбранного метода.

JSON-RPC request:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "query",
    "arguments": {
      "ops": []
    }
  }
}
```

Успешный JSON-RPC ответ возвращает MCP `content`; внутри строкового payload
лежит JSON результат конкретного tool. JSON-RPC ошибка возвращает
`error {code,message}`.
Ошибка LLM JSON API возвращается как JSON с машинным `code` и `details`.

LLM-facing JSON API surface включает методы `query`, `hist`, `usage`, `tx` и
`scheme`.

MCP tool descriptions содержат короткий quickstart и компактную карту методов.
Подробные инструкции читаются через `usage`.

## Файлы

- [model.md](model.md) - базовая модель v1, аргументы метода и defaults.
- [entry.md](entry.md) - вход LLM через MCP и `usage`.
- [methods.md](methods.md) - методы `query`, `hist`, `usage`, `tx`, `scheme` и allowed ops.
- [scheme.md](scheme.md) - контракт Схемы, метод `scheme`, hist schema и usage schema.
- [selectors.md](selectors.md) - Selector, ids, map bindings, links, wildcard path, selector slots, aliases и counts.
- [write-ops.md](write-ops.md) - `create`, `update`, `delete`, `move`, `link`, `unlink`, `rollback`.
- [transactions.md](transactions.md) - `tx_id`, rollback и conflicts.
- [hist.md](hist.md) - read-only hist graph, transaction log и snapshot value.
- [usage.md](usage.md) - read-only usage graph, instructions, examples и usage gates.
- [responses-and-errors.md](responses-and-errors.md) - общий envelope, atomic tx и коды ошибок.
- [workflow.md](workflow.md) - рекомендуемый LLM workflow.
