# Метод `tx`

`tx` атомарно применяет изменения к выбранному editable scope: `main`
(default), `usage` (`scope=usage`) или `scheme` (`scope=scheme`).

Перед записью kernel:

1. Резолвит селекторы, нормализует цели в id.
2. Проверяет state preconditions изменяемых узлов через `tx.read_ids`.
3. Проверяет schema-defined read gates (для `tx scope=main`,
   см. [read-gates.md](read-gates.md)).
4. Проверяет constraints Схемы для target scope.
5. Запускает финальную validation целостности.
6. Применяет изменения атомарно.
7. Пишет в hist одну `hist/transaction` + по одному `hist/change` на
   каждое изменение.
8. Возвращает `tx_id` и per-op данные.

Падение любого из шагов 1–5 возвращает envelope ошибки; hist остаётся
нетронутым.

## Аргументы

```json
{
  "commit_message": "...",
  "read_ids": [],
  "defaults": {},
  "ops": []
}
```

- `scope` — опциональный. `"usage"` или `"scheme"`. Отсутствие = `main`.
- `commit_message` — обязательный, не больше 100 токенов.
- `read_ids` — опциональный массив opaque-receipts.
- `defaults` — опциональный.
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

`expected_count` обязателен для `move`, `delete`, `link`, `unlink`.

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
          "to": { "id": "2a" }
        }
      ]
    }
  }
}
```

- `path` — полный либо относительный (если задан `defaults.path_parent`).
- `as` — опциональный alias.
- `set.title` — опциональный; при отсутствии выводится из последнего
  сегмента `path`.
- `set.value` — опциональный.
- `set.map_bindings` — опциональный объект; обязательность отдельных map
  задаёт Схема target scope.
- `set.links[]` — опциональный массив. Каждый link имеет `name` и `to`
  (id, селектор, alias). Cross-scope link допускается в направлении
  `usage → main`.

Результат операции:

```json
{
  "id": "c8"
}
```

`id` — hex-строка нового узла, выделенная kernel-ом. Этот же `id`
становится доступен через alias `as` для последующих ops в этой же tx.

## `update`

Точечная правка одного узла по id.

```json
{
  "update": {
    "id": "2a",
    "set": {
      "title": "selectors",
      "value": "..."
    }
  }
}
```

- `id` — обязательный hex-строка.
- `set` — обязательный, содержит хотя бы одно из `title`, `value`.

Изменение `title` пересчитывает `path` самого узла и path всех
path-потомков. Для затронутых узлов нужны state `read_id`-ы в
`tx.read_ids`. `update` не трогает `map_bindings`, `path` (через
parent), `links` — это область `move`, `link`, `unlink`.

## `move`

Bulk-операция изменения позиционирования (parent_path) и классификации
(map_bindings) для набора узлов.

```json
{
  "move": {
    "selector": {
      "path": "DocsWalker/api/old-section/**"
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
- `to.parent_path` — опциональный новый parent path. Новый `path` =
  `to.parent_path` + `/` + `title` узла. При отсутствии parent не
  меняется.
- `to.map_bindings` — опциональный объект, целиком заменяет привязки к
  указанным map для каждого затронутого узла.
  `defaults.map_bindings` к `move` не применяется.
- `expected_count` — обязательный.

Если `to.parent_path` совпадает с уже существующим path какого-то узла,
tx возвращает `already_exists`. Если `to.map_bindings` содержит ветку
вне Схемы — `unknown_map`.

## `delete`

Bulk-удаление узлов.

```json
{
  "delete": {
    "ids": ["11", "12", "13"],
    "expected_count": 3
  }
}
```

или:

```json
{
  "delete": {
    "selector": {
      "map_bindings": {
        "content": "documents/draft"
      }
    },
    "expected_count": 4
  }
}
```

- `ids` или `selector` — взаимоисключающие.
- `expected_count` — обязательный.

Incident links удаляемых узлов внутри scope удаляются вместе с узлом.
При наличии incoming `usage → main` link на удаляемый main-узел tx
возвращает `delete_blocked_by_cross_scope_link` со списком блокирующих
usage-узлов. LLM сначала отдельной `tx scope=usage` убирает или
переключает эти links, затем повторяет delete.

## `link`

Создание связей. Источник и цель задаются как селекторы / id / aliases;
итоговый набор links — cross-product source × target × name.

```json
{
  "link": {
    "name": "depends_on",
    "from": { "id": "2a" },
    "to": {
      "selector": {
        "map_bindings": {
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
- `to` — обязательный, по той же форме. Cross-scope target (`to.scope`)
  допускается в направлении `usage → main`.
- `expected_count` — обязательный, равен числу создаваемых links.

Отсутствие хотя бы одного источника / цели возвращает `not_found`. Link
с уже существующим tuple `(name, source.id, target.id, target.scope)`
возвращает `already_exists`. Несоответствие source / target constraints
Схемы — `validation_failed`. Иное направление cross-scope —
`cross_scope_not_allowed`.

## `unlink`

Удаление связей. Зеркально с `link`.

```json
{
  "unlink": {
    "name": "depends_on",
    "from": { "id": "2a" },
    "to": {
      "selector": {
        "path": "DocsWalker/api/old-section/**"
      }
    },
    "expected_count": 2
  }
}
```

Если для пары `(from, to)` link с указанным `name` отсутствует,
возвращается `not_found` для этой пары. Несовпадение фактического числа
удалённых links с `expected_count` — `count_mismatch`.

## `rollback`

Компенсирующая обратная tx.

```json
{
  "rollback": {
    "tx_id": "a3f1c2"
  }
}
```

- `tx_id` — обязательный hex-строка существующей tx в hist.

LLM передаёт только `tx_id`. Kernel:

1. Читает hist-changes указанной tx.
2. Для каждого вычисляет inverse: `create` → `delete`; `delete` →
   `create` с восстановлением исходного `id`; `update` → `update`
   со значениями prior из предыдущего hist-change того же id; `move`
   → `move` обратно; `link` → `unlink`; `unlink` → `link`.
3. Применяет inverse-ops как обычную tx (атомарно, с собственным
   `commit_message`, генерируемым kernel-ом в формате
   `"rollback of <commit_message_original>"`).
4. В hist создаёт новый `hist/transaction` с полем
   `rollback_of.tx_id = <исходный>`.
5. Возвращает новый `tx_id`.

Восстановление `id` после `create→delete-inverse` — kernel-уровневая
привилегия rollback.

При наличии последующих изменений, противоречащих inverse-ops, kernel
возвращает `rollback_conflict` с `details.conflicts[]`, где перечислены
блокирующие tx и затронутые ресурсы. LLM решает, делать ли новую
компенсирующую tx руками.

Отсутствие `tx_id` в hist — `rollback_not_found`. Уже выполненный
rollback (tx с `rollback_of.tx_id`, равным указанному) — `rollback_already_done`.

## Атомарность и hist-write

Атомарность tx распространяется на запись hist: изменения и
hist-записи применяются вместе. При падении записи hist kernel
откатывает применённые изменения и возвращает `hist_write_failed`.

## Примеры составной tx

### Создать узел и сразу слинковать его

```json
{
  "commit_message": "добавить раздел selectors и связать с write-ops",
  "read_ids": [
    "31bf",
    "7bc0",
    "7f3a",
    "22ab"
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
        "to": { "id": "11" },
        "expected_count": 1
      }
    }
  ]
}
```

Результат:

```json
{
  "result": {
    "tx_id": "b5e02d",
    "ops": [
      { "id": "c8" },
      {}
    ]
  }
}
```

### Массовая переклассификация

```json
{
  "commit_message": "пометить раздел selectors как llm-agent audience",
  "read_ids": [
    "22ab",
    "83f1",
    "7bc0",
    "5dea"
  ],
  "ops": [
    {
      "move": {
        "selector": {
          "path": "DocsWalker/api/selectors/**"
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
  "commit_message": "откатить ошибочную правку selectors",
  "ops": [
    {
      "rollback": {
        "tx_id": "a3f1c2"
      }
    }
  ]
}
```
