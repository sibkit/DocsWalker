# Методы API

## Read Methods

`query`, `hist` и `usage` являются query-like read-only методами. Они
принимают одинаковые `select` operations для получения узлов с управлением
`include`, `max_tokens`, выбором по `ids`, `path`, `map_bindings`, `links` и
regex-фильтром в `selector.match`.

- `query` читает основной project graph по project schema.
- `hist` читает hist graph по hist schema.
- `usage` читает usage graph по usage schema.

JSON-примеры в этом файле показывают `params.arguments` соответствующего MCP
tool; имя метода задается снаружи через `params.name`.

При пустом `include` ответ возвращается в compact-форме:
`id`, `path`, `title`, `map_bindings` и `tokens`.

Поля `value` и `links` возвращаются только при явном `include` и в рамках
`max_tokens`. Compact project node response содержит state `read_id`. Если
`include` содержит `value` и value узла возвращен полностью, node response
содержит value `read_id`. При truncation ответ содержит `truncated=true`,
`stopped_at` и `omitted_count`; truncated node не получает value `read_id`, но
может получить state `read_id`.

Пример `query select`:

```json
{
  "ops": [
    {
      "select": {
        "select": {
          "path": "DocsWalker-LLM JSON API/**",
          "map_bindings": {
            "content": "api/rule"
          },
          "match": {
            "regex": "validation_failed",
            "fields": ["title", "value"]
          }
        },
        "include": [
          "value",
          "links",
          "map_bindings"
        ],
        "max_tokens": 12000
      }
    }
  ]
}
```

Пример `hist select`:

```json
{
  "ops": [
    {
      "select": {
        "select": {
          "map_bindings": {
            "content": "hist/transaction"
          }
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Пример `usage select`:

```json
{
  "ops": [
    {
      "select": {
        "select": {
          "map_bindings": {
            "content": "usage/schema"
          }
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## `tx`

`tx` вносит изменения в project graph как атомарная транзакция. Перед записью
kernel каждый раз самостоятельно резолвит запрос, нормализует цели в ids,
проверяет state preconditions для изменяемых project nodes, schema-defined read
gates, constraints Схемы, запускает валидатор и применяет изменения целиком.

`tx.commit_message` задает краткое описание изменения для hist log и
обязателен для вызова `tx`. Значение не должно превышать 100 токенов.
`tx.read_ids` опционально содержит read ids, которые LLM получила через read
узлов. Для изменяемых project nodes это state precondition: если затронутый
узел изменился после чтения, его текущий state `read_id` уже другой и `tx`
отклоняется. Для schema-defined read gates kernel требует value `read_id`
соответствующих usage или project nodes.

Для защиты от случайной широкой записи массовые `move`, `link` и `unlink`
используют `expected_count`. Если фактическое число целей или link-пар
отличается от `expected_count`, `tx` возвращает `count_mismatch` и ничего не
применяет. `update` всегда меняет один узел по `id`; `delete` удаляет явный
список `ids`.

Каждая операция, назначающая `map_bindings`, требует, чтобы `tx.read_ids`
содержал read ids usage nodes с `map_bindings.content=usage/map` для каждой
затронутой map.

Каждая операция `link` требует, чтобы `tx.read_ids` содержал read id usage node
с `map_bindings.content=usage/link` для того же link name. Без этого
подтверждения `tx` не создает link.

Каждая операция, изменяющая существующие project nodes, требует, чтобы
`tx.read_ids` содержал актуальные state или value read ids этих узлов: `update`
требует read id целевого узла и path-потомков с изменяемым `path`, `delete` -
всех удаляемых узлов, `move` - переносимых узлов и path-потомков с изменяемым
`path`, `link` и `unlink` - существующих endpoint nodes затронутых link-пар.

Если затронутые project nodes требуют обязательных usage rules, write
затрагивает map/link gates, не хватает state read ids изменяемых project nodes
или применим rule с `requires_project_value_read=true`, которые LLM еще не
подтвердила через `read_ids`, `tx` возвращает `read_required` и ничего не
применяет. Если переданный `read_id` устарел, не относится к нужному node/gate,
имеет недостаточный scope или не соответствует текущей Схеме, `tx` возвращает
`invalid_read_id`.

После успешной записи `tx` возвращает только `tx_id`. `tx_id` является opaque
string. Если LLM нужны детали примененной транзакции, она читает их через
`hist`.

Пример успешного ответа `tx`:

```json
{
  "result": {
    "tx_id": "tx_20260514T101530123Z_7F3A91C2"
  }
}
```

## `scheme`

`scheme` является read-only методом LLM JSON API для чтения и точечного описания
Схемы DocsWalker через JSON `ops[]`. Результат нужен LLM перед созданием или
обновлением узлов, когда требуется уточнить maps, link rules и constraints.

Операции `scheme`, формат ответа и примеры описаны в [scheme.md](scheme.md).

## Allowed Ops

Допустимые `ops` зависят от имени MCP tool:

- `query` принимает `select`.
- `hist` принимает `select`.
- `usage` принимает `select`.
- `tx` принимает `select` для объявления alias и write-ops `create`, `update`,
  `delete`, `move`, `link`, `unlink`, `rollback`.
- `scheme` принимает read-only операцию `get` для чтения Схемы. Read ids для
  map/link/rule gates выдаются через full read usage nodes методом `usage`.
  State read ids для project state preconditions выдаются через compact или full
  read project nodes методом `query`; value read ids для project value gates
  выдаются через full read project nodes методом `query`.

Пример `tx` для одиночного `update`:

```json
{
  "commit_message": "уточнить правило selectors",
  "read_ids": [
    "read_node_42_state_31BF23A0",
    "read_rule_api_write_9AA1020B"
  ],
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

Пример `tx` для массового `move` по map bindings:

```json
{
  "commit_message": "назначить аудиторию правилам API",
  "read_ids": [
    "read_node_17_state_A22140CE",
    "read_node_18_state_83F1046B",
    "read_node_19_state_7BC0E11D",
    "read_rule_api_write_9AA1020B",
    "read_audience_7BC0E11D"
  ],
  "ops": [
    {
      "move": {
        "source": {
          "path": "DocsWalker-LLM JSON API/**"
        },
        "to": {
          "map_bindings": {
            "audience": "llm-agent"
          }
        },
        "expected_count": 3
      }
    }
  ]
}
```

При совпадении count `tx` применяет запись атомарно. При mismatch возвращается
envelope ошибки с `code=count_mismatch`.

Пример `tx` для rollback:

```json
{
  "commit_message": "откатить изменение API",
  "ops": [
    {
      "rollback": {
        "tx_id": "tx_20260514T101530123Z_7F3A91C2"
      }
    }
  ]
}
```

Успешный rollback является обычной `tx`: он применяет изменения к project graph,
пишет transaction node и snapshot nodes в `hist`, затем возвращает новый
`tx_id`.
