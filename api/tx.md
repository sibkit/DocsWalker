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
7. Пишет в hist один event-узел `hist/transaction` с секциями
   `created` / `changed` / `deleted` (см. [hist-scope.md](hist-scope.md)).
8. Возвращает `id` нового hist/transaction и per-op данные.

Падение любого из шагов 1–5 возвращает envelope ошибки; hist остаётся
нетронутым.

## Аргументы

```json
{
  "title": "...",
  "description": "...",
  "read_ids": [],
  "defaults": {},
  "ops": []
}
```

- `scope` — опциональный. `"usage"` или `"scheme"`. Отсутствие = `main`.
- `title` — обязательный. Свободный текст commit-сообщения, ≤ 100
  токенов. Без regex-ограничений.
- `description` — опциональный. Длинный текст с подробностями, без
  жёсткого лимита токенов.
- `read_ids` — опциональный массив opaque-receipts.
- `defaults` — опциональный.
- `ops[]` — обязательный.

## Допустимые операции

| op       | cardinality | что делает                                            |
|----------|-------------|------------------------------------------------------|
| `create` | single      | создание узла со всеми начальными полями              |
| `update` | single (по id) | изменение `title`, `value`                          |
| `move`   | bulk (по selector) | изменение `parent_path`, `map_bindings`         |
| `delete` | bulk (по ids/selector) | удаление узла(ов)                            |
| `link`   | bulk        | создание связи(ей)                                    |
| `unlink` | bulk        | удаление связи(ей)                                    |
| `rollback` | —         | компенсирующая обратная tx по id                      |

`expected_count` обязателен для `move`, `delete`, `link`, `unlink`.

Op `select` в `tx` отсутствует. Чтобы сослаться внутри tx на узел,
которого ещё нет, используется alias `create.as` (см. раздел `create`
ниже и [selectors.md](selectors.md), раздел «Aliases»). Если нужен
набор узлов для bulk-op, селектор задаётся прямо в этой op
(`move.selector`, `delete.selector`, `link.from`/`link.to` и т.д.) —
отдельная «резолверная» op не нужна.

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
        { "name": "depends_on", "to": "2a" }
      ]
    }
  }
}
```

- `path` — полный либо относительный (если задан `defaults.path_parent`).
- `as` — опциональный alias для нового узла. Единственный способ
  объявить alias в `tx`. Действует только в последующих ops той же tx
  и резолвится в `id`, выданный kernel-ом этой `create`-op.
- `set.title` — опциональный; при отсутствии выводится из последнего
  сегмента `path`.
- `set.value` — опциональный.
- `set.map_bindings` — опциональный объект; полный начальный набор
  привязок узла. Обязательность отдельных map задаёт Схема target scope.
  `null` в значении запрещён (нечего снимать у нового узла) — возвращает
  `invalid_map_binding_value`.
- `set.links[]` — опциональный массив. Каждый link имеет `name` и `to`
  (строка-id, либо объектная форма для селектора / alias / нескольких
  ids). Cross-scope link допускается в направлении `usage → main`.

Результат операции:

```json
{
  "id": "c8"
}
```

`id` — hex-строка нового узла, выделенная kernel-ом. Этот же `id`
становится доступен через alias `as` для последующих ops в этой же tx.

В hist новый узел попадает в `created.nodes` со своим полным
post-state, а каждый созданный link — в `created.links` с identity.

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

В hist изменение попадает в `changed.nodes` с `set`, содержащим только
фактически изменённые поля (forward-only diff). При изменении `title`
сам узел и каждый затронутый path-потомок получают **отдельный**
элемент в `changed.nodes` (у потомков `set.path = <новый>`; у самого
узла `set.title = <новый>` и/или `set.path = <новый>`).

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
- `to.map_bindings` — опциональный объект, partial-merge по ключам.
  Ключ → ветка: установить или перезаписать привязку узла к этой map.
  Ключ → `null`: снять привязку к этой map. Ключи, не упомянутые в
  объекте, не трогаются. Пустой объект `{}` — no-op для map_bindings.
  `defaults.map_bindings` к `move` не применяется.
- `expected_count` — обязательный, считает только узлы из selector
  (без каскадных path-потомков).

Если `to.parent_path` совпадает с уже существующим path какого-то узла,
tx возвращает `already_exists`. Если `to.map_bindings` содержит ветку
вне Схемы — `unknown_map`.

**Каскад на path-потомков.** Move переносимого узла автоматически
меняет `path` всех его path-потомков (последний сегмент остаётся,
parent меняется). Эти потомки тоже считаются затронутыми: их state
`read_id`-ы должны быть в `tx.read_ids`, и каждый из них получит
отдельный элемент в `hist/transaction.changed.nodes` с
`set.path = <новый>`. На `expected_count` потомки не влияют.

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

Incident links удаляемых узлов внутри scope удаляются вместе с узлом
(и попадают в `deleted.links` hist-узла). При наличии incoming
`usage → main` link на удаляемый main-узел tx возвращает
`delete_blocked_by_cross_scope_link` со списком блокирующих usage-узлов.
LLM сначала отдельной `tx scope=usage` убирает или переключает эти
links, затем повторяет delete.

В hist удалённые узлы попадают в `deleted.nodes` (только `{ id }`);
prior state для возможного rollback восстанавливается из предыдущей tx,
в которой узел фигурировал в `created.nodes` или `changed.nodes`.

## `link`

Создание связей. Источник и цель задаются как строка-id, объект с
селектором / id / ids / alias; итоговый набор links — cross-product
source × target × name.

```json
{
  "link": {
    "name": "depends_on",
    "from": "2a",
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
- `from` — обязательный, в формах endpoint (см.
  [selectors.md](selectors.md), раздел «Endpoint»). Короткая форма для
  single-id — строка `"2a"`.
- `to` — обязательный, по той же форме. Cross-scope `to` допускается в
  направлении `usage → main`; scope endpoint-узла определяется по `id`.
- `expected_count` — обязательный, равен числу создаваемых links.

Отсутствие хотя бы одного источника / цели возвращает `not_found`. Link
с уже существующим tuple `(name, from.id, to.id)` возвращает
`already_exists`. Несоответствие from / to constraints Схемы —
`validation_failed`. Иное направление cross-scope —
`cross_scope_not_allowed`.

В hist каждый созданный link попадает в `created.links` как
`{ "name": "...", "from": "<id>", "to": "<id>" }`.

## `unlink`

Удаление связей. Зеркально с `link`.

```json
{
  "unlink": {
    "name": "depends_on",
    "from": "2a",
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
возвращается `not_found` с указанием первой такой пары в `details`.
Несовпадение фактического числа удалённых links с `expected_count` —
`count_mismatch`.

В hist удалённый link попадает в `deleted.links` со своей identity.

## `rollback`

Компенсирующая обратная tx.

```json
{
  "rollback": "a3f1c2"
}
```

- Значение — строка-id существующей транзакции в hist (короткая форма).
  Допустима и длинная форма `{ "id": "a3f1c2" }`, но не рекомендуется.

LLM передаёт только id откатываемой tx. Сам `title` (commit-сообщение)
для компенсирующей tx передаётся на уровне всей `tx`, как обычно — это
свободный текст ("откатить ошибочную правку selectors" или подобное).
Kernel НЕ подменяет переданный LLM `title`; связь компенсирующей tx с
исходной фиксируется через поле `rollback_of` в новом event-узле.

Kernel при rollback:

1. Читает секции `created` / `changed` / `deleted` указанной tx.
2. Для каждого элемента вычисляет inverse:
   - `created.nodes[]` → inverse `delete`;
   - `created.links[]` → inverse `unlink`;
   - `changed.nodes[].set` → inverse `update` с возвратом полей к их
     значениям непосредственно перед откатываемой tx (реконструкция
     per-field по hist — см. [hist-scope.md](hist-scope.md), раздел
     «Реконструкция значения поля»);
   - `deleted.nodes[]` → inverse `create` с восстановлением исходного
     `id` и полного snapshot-а узла на момент перед откатываемой tx
     (тот же механизм реконструкции, применённый ко всем полям
     контракта);
   - `deleted.links[]` → inverse `link`.
3. Применяет inverse-ops как обычную tx атомарно.
4. Создаёт новый event-узел `hist/transaction` с
   `rollback_of: "<исходный id>"`.
5. Возвращает `id` нового event-узла.

Восстановление `id` после `create→delete-inverse` — kernel-уровневая
привилегия rollback.

При наличии последующих изменений, противоречащих inverse-ops, kernel
возвращает `rollback_conflict` с `details.conflicts[]`, где перечислены
блокирующие tx и затронутые ресурсы. LLM решает, делать ли новую
компенсирующую tx руками.

Отсутствие id в hist — `rollback_not_found`. Уже выполненный rollback
(существующая tx с `rollback_of`, равным указанному id) —
`rollback_already_done`.

## Атомарность и hist-write

Атомарность tx распространяется на запись hist: изменения и event-узел
hist применяются вместе. При падении записи event-узла kernel
откатывает применённые изменения и возвращает `hist_write_failed`.

## Примеры составной tx

### Создать узел и сразу слинковать его

```json
{
  "title": "selectors-section-and-link",
  "description": "Добавил раздел selectors и связал его с write-ops через depends_on.",
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
        "to": "11",
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
    "id": "b5e02d",
    "ops": [
      { "id": "c8" },
      {}
    ]
  }
}
```

В hist появится event-узел `id=b5e02d` с секциями:
- `created.nodes` — один элемент (полный snapshot узла `c8`);
- `created.links` — один элемент `{ "name": "depends_on", "from": "c8", "to": "11" }`.

### Массовая переклассификация

```json
{
  "title": "selectors-audience-llm-agent",
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
  "title": "rollback-selectors-fix",
  "description": "Откат правки selectors — повредила пример depends_on.",
  "ops": [
    { "rollback": "a3f1c2" }
  ]
}
```

В hist появится новый event-узел с `rollback_of: "a3f1c2"`. Содержимое
секций — inverse того, что было в исходной транзакции.
