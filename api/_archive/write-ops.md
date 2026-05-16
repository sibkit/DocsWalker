# Операции Записи

## `create`

`create` создает новый узел по exact path. Поле `path` обязательно напрямую или
собирается из `defaults.path_parent` и последнего сегмента операции.

`create` не требует state `read_id` для создаваемого узла, потому что до
транзакции он не существовал. Если созданный узел передается в последующие
операции того же `tx` через alias, эти операции тоже не требуют state `read_id`
для этого нового узла.

Содержимое нового узла задается только в `set`. Обязательные map bindings
определяются Схемой и передаются в `set.map_bindings` или
`defaults.map_bindings`.

Каждая map, назначенная через `set.map_bindings` или `defaults.map_bindings`,
требует matching read id в `tx.read_ids`. `read_id` можно получить только через
full read usage node этой map: `usage` select должен выбрать
`map_bindings.content=usage/map` и нужный `map_bindings.map_name`, а `include`
должен содержать `value`. Один `read_id` можно использовать для нескольких
назначений той же map в одном `tx`. Если read id отсутствует, возвращается
`read_required`; если он не подходит к map или текущей Схеме, возвращается
`invalid_read_id`.

`title` нового узла выводится kernel-ом из последнего сегмента `path`.
Переименование узла выполняется через `update.set.title`.
Новый `title` должен целиком соответствовать regex `^[\p{L}\p{Nd}._-]+$`.
Коллизия siblings проверяется по lower-case форме `title`.

В `create` поле `set` содержит `value` и `map_bindings`. Links для нового узла
создаются отдельными `link` операциями в том же `tx`; созданный узел можно
передать в `link.source` или `link.target` через alias.

## `update`

`update` меняет ровно один узел. Цель задается прямым полем `id`.
`tx.read_ids` должен содержать актуальный state или value `read_id` этого узла.
Если `set.title` меняет `path`, state read ids нужны также для каждого
path-потомка, чей `path` изменится из-за переименования.

Если применимый rule содержит `requires_project_value_read=true`, state
`read_id` недостаточен: нужен value `read_id`, полученный через full read
`query` с `include`, содержащим `value`. При отсутствии read id возвращается
`read_required`, при устаревшем, неподходящем или недостаточном scope -
`invalid_read_id`.

Изменения задаются только в `set`. В `set` допустимы:

- `value` - новое содержимое узла;
- `title` - новый последний сегмент `path`.

`set.title` должен целиком соответствовать regex `^[\p{L}\p{Nd}._-]+$`. При смене `title`
kernel пересчитывает `path` самого узла и всех его path-потомков. Если новый
title по lower-case форме конфликтует с существующим sibling, весь `tx`
отклоняется.

Parent path и map bindings меняются через `move`. Links меняются через `link` и
`unlink`. Массовые изменения нескольких узлов выражаются несколькими `update`
внутри одного `tx`.

Форма элемента `ops[]` для `update`:

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

Успешная write-транзакция возвращает только `tx_id`. Транзакцию можно откатить
через `tx` с `rollback` operation по этому `tx_id`, если откат не конфликтует с
последующими изменениями.

## `delete`

`delete` удаляет один узел или набор узлов.

Цели задаются прямым полем `ids`.
`tx.read_ids` должен содержать актуальный state или value `read_id` каждого
удаляемого узла. Если применимый rule содержит
`requires_project_value_read=true`, для подходящих под rule узлов нужен value
`read_id`.

Kernel проверяет, что все ids существуют и не повторяются. Если удаление
оставит path-потомков вне `ids` или dangling links на удаляемые узлы, `tx`
отклоняет весь запрос.

Перед удалением LLM должна прочитать выбранный набор через `query` и передать
конкретные ids.

Форма элемента `ops[]` для `delete`:

```json
{
  "delete": {
    "ids": [42, 43]
  }
}
```

## `move`

`move` меняет структурное положение узлов: parent path и/или map bindings. Source
задается через selector slot `source` и может резолвиться в один или несколько
узлов.

`tx.read_ids` должен содержать актуальные state или value `read_id` для каждого
переносимого узла. Если `to.parent` меняет `path`, state read ids нужны также
для каждого path-потомка, чей `path` изменится вместе с переносимым узлом. Если
применимый rule содержит `requires_project_value_read=true`, для подходящих под
rule узлов нужен value `read_id`.

Поле `to` обязательно и содержит хотя бы одно из полей:

- `parent` - новый parent path без wildcard;
- `map_bindings` - новые exact-привязки к maps.

Каждая map, назначенная через `to.map_bindings`, требует matching `read_id` в
`tx.read_ids`. `read_id` можно получить только через full read usage node этой
map. Один `read_id` можно использовать для нескольких назначений той же map в
одном `tx`.

При `to.parent` каждый переносимый узел сохраняет свой `title`. Если перенос
создает коллизию siblings по lower-case форме `title`, весь `tx` отклоняется.

Если `source` выбирает больше одного узла, `expected_count` обязателен и должен
совпасть с числом переносимых узлов. Массовая переклассификация по map bindings
выражается одним `move` с selector-набором и `expected_count`.

Форма элемента `ops[]` для `move`:

```json
{
  "move": {
    "source": {
      "path": "DocsWalker-LLM JSON API/**"
    },
    "to": {
      "parent": "DocsWalker/API",
      "map_bindings": {
        "audience": "llm-agent"
      }
    },
    "expected_count": 7
  }
}
```

## `link` и `unlink`

`link` создает один link между `source` и `target`; `unlink` удаляет один
link.

`name` задает имя link из Схемы. `source` и `target` являются selector slots и
могут резолвиться в один или несколько узлов.

`link` и `unlink` требуют актуальные state или value `read_id` существующих
source и target endpoint nodes каждой затронутой link-пары. Endpoint node,
созданный ранее в том же `tx` и переданный через alias, не требует state
`read_id`. Если применимый rule содержит `requires_project_value_read=true`,
для подходящих под rule endpoint nodes нужен value `read_id`.

Для `link` нужен matching read id в `tx.read_ids`. Его можно получить только
через full read usage node этого link-а: `usage` select должен выбрать
`map_bindings.content=usage/link` и нужный `map_bindings.link_name`, а
`include` должен содержать `value`. `read_id` подтверждает, что LLM прочитала
инструкцию о смысле связи перед созданием link-а. Один `read_id` можно
использовать для нескольких `link` операций с тем же `name` в одном `tx`. Если
read id отсутствует, возвращается `read_required`; если он не подходит к `name`
или текущей Схеме, возвращается `invalid_read_id`.

Для `unlink` не нужен usage read id link instruction node, потому что операция
удаляет уже существующий link tuple и не создает новую семантическую связь.

Link не имеет отдельного публичного `id`: его identity - tuple
`(name, source_id, target_id)`. `link` создает такой tuple, если он еще не
существует; повторное создание того же tuple отклоняется. `unlink` удаляет
существующий tuple, полученный из `name` и зарезолвленных `source`/`target`.

`link` и `unlink` применяются к декартову произведению `source` x `target`.
`expected_count` задает ожидаемое число итоговых link-пар. Если
`expected_count` не задан, операция должна дать ровно одну пару. Если одна из
сторон не найдена, возвращается `not_found`; если число link-пар отличается от
`expected_count`, возвращается `count_mismatch`.

Несколько links можно выразить одним `link`/`unlink` с selector-наборами и
`expected_count` либо несколькими `link`/`unlink` внутри одного `tx`.

Форма элемента `ops[]` для `link`:

```json
{
  "link": {
    "name": "depends_on",
    "source": { "ids": [1] },
    "target": { "ids": [2] }
  }
}
```

Форма элемента `ops[]` для `unlink`:

```json
{
  "unlink": {
    "name": "depends_on",
    "source": { "ids": [1] },
    "target": { "ids": [2] }
  }
}
```

## `rollback`

`rollback` внутри `tx` откатывает указанную ранее успешно примененную
транзакцию. Цель задается полем `tx_id`.

Rollback является обычной write-операцией: он меняет project graph, проходит
те же state preconditions, schema-defined read gates, schema constraints и
validation, а успешный `tx` получает новый `tx_id`.

Перед применением rollback kernel вычисляет project nodes, которые будут
изменены или удалены откатом, и требует их актуальные state read ids в
`tx.read_ids`. Если применимая Схема требует value read ids перед rollback,
kernel возвращает `read_required` с готовыми read requests; если переданные read
ids устарели, имеют недостаточный scope или не соответствуют нужному gate,
возвращается `invalid_read_id`.

Если последующие изменения мешают откату, весь `tx` отклоняется с
`rollback_conflict`, project graph не меняется.

Форма элемента `ops[]` для `rollback`:

```json
{
  "rollback": {
    "tx_id": "tx_20260514T101530123Z_7F3A91C2"
  }
}
```
