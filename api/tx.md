# Метод `tx`

`tx` атомарно применяет изменения к выбранному editable scope: `main`
(default), `usage` (`scope=usage`) или `scheme` (`scope=scheme`). `hist`
запрещён.

Перед записью kernel:

1. Резолвит селекторы, нормализует цели в id.
2. Проверяет state preconditions изменяемых узлов через `tx.read_ids`.
3. Проверяет schema-defined read gates (только для `tx scope=main`,
   см. [read-gates.md](read-gates.md)).
4. Проверяет constraints Схемы для target scope.
5. Запускает финальную validation целостности.
6. Применяет изменения целиком и атомарно.
7. Пишет в hist одну `hist/transaction` + по одному `hist/change` на
   каждое изменение.
8. Возвращает `tx_id`.

Если любой из шагов 1–5 падает, tx возвращает envelope ошибки и ничего
не применяет (включая hist).

## Аргументы

```json
{
  "scope": "main",
  "commit_message": "...",
  "read_ids": [],
  "defaults": {},
  "ops": []
}
```

- `scope` — опциональный, `"usage"` или `"scheme"`. Отсутствие = `"main"`.
- `commit_message` — обязательный, не больше 100 токенов.
- `read_ids` — опциональный массив opaque-receipts.
- `defaults` — опциональный (см. [model.md](model.md)).
- `ops[]` — обязательный.

## Допустимые операции

| op       | cardinality | что делает                                            |
|----------|-------------|------------------------------------------------------|
| `select` | —           | объявление alias для использования в последующих ops  |
| `create` | single      | создание узла со всеми начальными полями              |
| `update` | single (по id) | изменение `title`, `value`                          |
| `move`   | bulk (по selector) | изменение `parent_path`, `map_bindings`         |
| `delete` | bulk (по ids/selector) | удаление узла(ов)                            |
| `link`   | bulk        | создание связи(ей)                                    |
| `unlink` | bulk        | удаление связи(ей)                                    |
| `rollback` | —         | компенсирующая обратная tx по `tx_id`                 |

`expected_count` обязателен для всех bulk-ops (`move`, `delete`, `link`,
`unlink`).

## `create`

Создаёт один узел.

```json
{
  "create": {
    "path": "DocsWalker/api/new-section",
    "as": "new_section",
    "set": {
      "title": "new-section",
      "value": "...",
      "map_bindings": {
        "content": "documents/spec"
      },
      "links": [
        {
          "name": "depends_on",
          "to": { "id": 42 }
        }
      ]
    }
  }
}
```

- `path` — полный или относительный (если задан `defaults.path_parent`).
- `as` — опциональный alias.
- `set.title` — опциональный; если не задан, выводится из последнего
  сегмента `path`.
- `set.value` — опциональный.
- `set.map_bindings` — опциональный объект; обязательность отдельных map
  задаёт Схема target scope.
- `set.links[]` — опциональный массив. Каждый link имеет `name` и `to`
  (id, селектор, alias). Cross-scope link допускается только для
  направления `usage → main` (см. [model.md](model.md)).

При успехе результат операции содержит `id` созданного узла и
`tx_id`-relative `read_id`-ы для последующих ops в том же tx.

## `update`

Точечная правка одного узла по id.

```json
{
  "update": {
    "id": 42,
    "set": {
      "title": "selectors",
      "value": "..."
    }
  }
}
```

- `id` — обязательный.
- `set` — обязательный, содержит хотя бы одно из: `title`, `value`.

Изменение `title` пересчитывает `node.path` самого узла и path всех
path-потомков. Для всех затронутых узлов нужны state `read_id`-ы в
`tx.read_ids`. `update` не меняет `map_bindings`, `path` (через parent)
и `links` — для этого используются `move`, `link`, `unlink`.

## `move`

Bulk-операция изменения позиционирования (parent_path) и
классификации (map_bindings) для набора узлов.

```json
{
  "move": {
    "selector": {
      "node.path": "DocsWalker/api/old-section/**"
    },
    "to": {
      "parent_path": "DocsWalker/api/new-section",
      "map_bindings": {
        "audience": "llm-agent"
      }
    },
    "expected_count": 5
  }
}
```

- `selector` — обязательный, набор переносимых узлов.
- `to.parent_path` — опциональный новый parent path. Для каждого узла
  новый `path` = `to.parent_path` + `/` + `title` узла. Если не задан,
  parent не меняется.
- `to.map_bindings` — опциональный объект, целиком заменяет привязки к
  указанным map для каждого затронутого узла. Default `defaults.map_bindings`
  не применяется к `move.to.map_bindings`.
- `expected_count` — обязательный. Считает узлы, реально затронутые
  изменением.

`move` не меняет `title` отдельных узлов и не меняет `value`. Если
`to.parent_path` совпадает с уже существующим path какого-то узла, tx
возвращает `already_exists`. Если `to.map_bindings` содержит ветку,
неизвестную Схеме, — `unknown_map`.

## `delete`

Bulk-удаление узлов.

```json
{
  "delete": {
    "ids": [17, 18, 19],
    "expected_count": 3
  }
}
```

или:

```json
{
  "delete": {
    "selector": {
      "node.map_bindings": {
        "content": "documents/draft"
      }
    },
    "expected_count": 4
  }
}
```

- `ids` или `selector` — взаимоисключающие.
- `expected_count` — обязательный.

Все incident links удаляемых узлов внутри scope удаляются автоматически.
Если на удаляемый main-узел есть incoming cross-scope link из usage, tx
возвращает `delete_blocked_by_cross_scope_link` со списком блокирующих
usage-узлов. LLM должна сначала отдельной `tx scope=usage` убрать или
переключить эти links.

## `link`

Создание связей. Источник и цель задаются как селекторы / id / aliases;
итоговый набор links — cross-product source × target × name.

```json
{
  "link": {
    "name": "depends_on",
    "from": { "id": 42 },
    "to": {
      "selector": {
        "node.map_bindings": {
          "content": "documents/spec"
        }
      }
    },
    "expected_count": 3
  }
}
```

- `name` — обязательный.
- `from` — обязательный, `id` / `ids` / `selector` / `alias`.
- `to` — обязательный, как `from`. Cross-scope target допускается только
  в направлении `usage → main` (поле `to.scope`, если нужно явно
  выйти из текущего scope).
- `expected_count` — обязательный, равен числу создаваемых links.

Если хотя бы один из источников/целей не найден, tx возвращает
`not_found`. Если link с tuple `(name, source.id, target.id, target.scope)`
уже существует, tx возвращает `already_exists`. Если source/target не
подходит под source/target constraints Схемы — `validation_failed`.

## `unlink`

Удаление связей. Зеркально с `link`.

```json
{
  "unlink": {
    "name": "depends_on",
    "from": { "id": 42 },
    "to": {
      "selector": {
        "node.path": "DocsWalker/api/old-section/**"
      }
    },
    "expected_count": 2
  }
}
```

Если для пары `(from, to)` link с указанным `name` не найден — tx
возвращает `not_found` для этой пары. Если `expected_count` не совпадает
с числом реально удалённых links — `count_mismatch`.

## `rollback`

Компенсирующая обратная tx.

```json
{
  "rollback": {
    "tx_id": "tx_20260514T101530123Z_7F3A91C2"
  }
}
```

- `tx_id` — обязательный, идентификатор существующей tx в hist.

LLM передаёт только `tx_id`. Kernel:

1. Читает hist-changes указанной tx.
2. Для каждого вычисляет inverse-op: для `create` → `delete`; для
   `delete` → `create` с восстановлением исходного `node.id`; для
   `update` → `update` со значениями prior из предыдущего hist-change
   того же id; для `move` → `move` обратно; для `link` → `unlink`; для
   `unlink` → `link`.
3. Применяет inverse-ops как обычную tx (атомарно, с собственным
   `commit_message`, который kernel генерирует автоматически в формате
   `"rollback of <commit_message_original>"`).
4. В hist создаёт новый `hist/transaction` с полем
   `rollback_of.tx_id = <исходный>`.
5. Возвращает новый `tx_id`.

`rollback` — kernel-генерируемая привилегия: восстановление исходного
`node.id` после `create→delete-inverse` запрещено пользовательским tx,
но разрешено kernel-у внутри rollback.

Если rollback не может быть применён (например, существующие после
оригинальной tx изменения противоречат inverse-операциям), kernel
возвращает `rollback_conflict` с `details.conflicts[]`, где перечислены
блокирующие tx и затронутые ресурсы. LLM решает, делать ли новую
компенсирующую tx руками.

Если `tx_id` не найден в hist — `rollback_not_found`. Если уже
существует tx с `rollback_of.tx_id` равным указанному — это значит, что
rollback уже выполнен; повторный возвращает `rollback_already_done`.

## Атомарность и hist-write

Атомарность tx распространяется на запись hist: либо изменения и
hist-записи применены вместе, либо ничего. Если запись hist падает (диск,
io), kernel откатывает применённые изменения и возвращает
`hist_write_failed`.

## Примеры составной tx

### Создать узел и сразу слинковать его

```json
{
  "scope": "main",
  "commit_message": "добавить раздел selectors и связать с write-ops",
  "read_ids": [
    "read_value_map_content_31BF23A0",
    "read_value_map_audience_7BC0E11D",
    "read_value_link_depends_on_7F3A91C2",
    "read_state_node17_22ABCDEF"
  ],
  "ops": [
    {
      "create": {
        "path": "DocsWalker/api/selectors",
        "as": "selectors",
        "set": {
          "value": "...",
          "map_bindings": {
            "content": "documents/spec",
            "audience": "llm-agent"
          }
        }
      }
    },
    {
      "link": {
        "name": "depends_on",
        "from": { "alias": "selectors" },
        "to": { "id": 17 },
        "expected_count": 1
      }
    }
  ]
}
```

### Массовая переклассификация

```json
{
  "scope": "main",
  "commit_message": "пометить раздел selectors как llm-agent audience",
  "read_ids": [
    "read_state_node17_22ABCDEF",
    "read_state_node18_83F1046B",
    "read_state_node19_7BC0E11D",
    "read_value_map_audience_7BC0E11D"
  ],
  "ops": [
    {
      "move": {
        "selector": {
          "node.path": "DocsWalker/api/selectors/**"
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

### Откатить транзакцию

```json
{
  "scope": "main",
  "commit_message": "откатить ошибочную правку selectors",
  "ops": [
    {
      "rollback": {
        "tx_id": "tx_20260514T101530123Z_7F3A91C2"
      }
    }
  ]
}
```
