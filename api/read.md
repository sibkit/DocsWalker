# Метод `read`

`read` — read-only метод для всех scope: `main`, `usage`, `hist`,
`scheme`. Принимает массив `ops[]` с операциями `select`. Никаких других
ops у `read` нет.

```json
{
  "scope": "main",
  "ops": [
    {
      "select": {
        "selector": {
          "node.id": [42, 17]
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## Операция `select`

- `selector` — обязательный. Predicate по полям узла / link / hist-change
  (см. [selectors.md](selectors.md)).
- `include` — опциональный массив строк. Допустимые значения: `"value"`,
  `"links"`, `"map_bindings"`. Пустой или отсутствующий = compact-форма.
- `max_tokens` — опциональный, положительное целое. Лимит токенов на
  выдачу всей операции. 0 или отрицательное — `invalid_max_tokens`.
- `as` — опциональный alias (см. [selectors.md](selectors.md)).

## Compact-форма и полные узлы

Если `include` пуст или отсутствует, узлы возвращаются в compact-форме:

```json
{
  "id": 42,
  "scope": "main",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "content": "documents/spec"
  },
  "tokens": 320,
  "read_id": "read_state_node42_31BF23A0"
}
```

`tokens` — оценка стоимости полного `value` узла (читается отдельной
операцией с `include=["value"]`). `read_id` в compact-форме — **state**
read_id (см. ниже).

Если `include` содержит `"value"` и value целиком влез в `max_tokens`,
узел возвращается с полем `value` и **value** `read_id`:

```json
{
  "id": 42,
  "scope": "main",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "content": "documents/spec"
  },
  "value": "...",
  "tokens": 320,
  "read_id": "read_value_node42_31BF23A0"
}
```

Если `include` содержит `"links"`, к узлу добавляется список incident
links:

```json
{
  "links": [
    {
      "name": "depends_on",
      "direction": "out",
      "target": {
        "id": 99,
        "scope": "main",
        "path": "DocsWalker/api/selectors"
      }
    }
  ]
}
```

## Truncation

Если общий результат превышает `max_tokens`, выдача обрезается.

```json
{
  "count": 12,
  "truncated": true,
  "stopped_at": "DocsWalker/api/write-ops",
  "omitted_count": 5,
  "items": [
    { "id": 17, "...": "..." }
  ]
}
```

- `count` — общее число узлов, удовлетворяющих селектору (включая
  отрезанные).
- `truncated` — `true`, если что-то срезано.
- `stopped_at` — `path` последнего возвращённого узла. Опционально.
- `omitted_count` — сколько узлов не попало в ответ.
- `items` — массив возвращённых узлов.

Truncated узел, у которого `value` был запрошен, но не помещён — получает
только state `read_id`, value `read_id` не выдаётся. Чтобы получить value
`read_id` для truncated-узла, LLM делает повторный `read` с более узким
селектором или с увеличенным `max_tokens`.

## `read_id`

`read_id` — opaque-receipt, который kernel выдаёт при `read`. Привязан
к scope, id узла, версии состояния и scope-у чтения. LLM не собирает и
не модифицирует `read_id`. Это не permission и не auth token.

Scope-ы `read_id`:

- **state** — подтверждает актуальную версию состояния узла без
  обязательного чтения `value`. Для node это `path`, `title`,
  `map_bindings` и incident links на момент чтения. State `read_id`
  используется как write precondition: если узел изменился после
  чтения, его текущий state `read_id` уже другой и `tx` отклоняется.
- **value** — подтверждает, что `value` узла прочитан целиком, без
  truncation, и фиксирует версию value на момент чтения. Value `read_id`
  одновременно удовлетворяет state precondition того же узла той же
  версии.

Compact read возвращает state `read_id`. Full read (`include=["value"]`,
не truncated) возвращает value `read_id`. Truncated full read возвращает
только state `read_id`.

При каждом успешном изменении узла kernel выпускает для него **новый**
state и value `read_id`-ы — старые перестают соответствовать актуальной
версии. Создание или удаление link меняет state `read_id` его source и
target узлов.

`read_id` передаётся LLM в `tx.read_ids` (см.
[read-gates.md](read-gates.md)). Применимость:

- state precondition для изменяемых project-узлов — принимает state
  или value `read_id` той же версии.
- usage rule / map / link gate — требует **value** `read_id` соответствующего
  usage-узла.
- project value gate (для main-tx) — требует **value** `read_id` main-узла,
  если применимый rule содержит `requires_project_value_read=true`.

## Примеры

### Compact-обзор main под path

```json
{
  "scope": "main",
  "ops": [
    {
      "select": {
        "selector": {
          "node.path": "DocsWalker/api/**"
        },
        "max_tokens": 2000
      }
    }
  ]
}
```

Возвращает все main-узлы под `DocsWalker/api/`, в compact-форме, с state
`read_id` каждого.

### Full read одного узла под изменение

```json
{
  "scope": "main",
  "ops": [
    {
      "select": {
        "selector": {
          "node.id": 42
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает узел 42 целиком с value `read_id`. Этот `read_id` LLM передаёт
в `tx.read_ids` при `update` / `delete` / `move` этого узла.

### Чтение usage rule перед tx scope=main

```json
{
  "scope": "usage",
  "ops": [
    {
      "select": {
        "selector": {
          "node.path": "rules/api/write"
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает rule-узел с value `read_id`. Этот `read_id` LLM передаёт в
`tx.read_ids` при затрагивающем main-узлы tx (см.
[read-gates.md](read-gates.md)).

### Чтение истории узла

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "target.node.id": 42
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает все hist-change-узлы, описывающие изменения узла 42 во всех
editable scope.
