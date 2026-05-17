# Метод `read`

`read` — read-only метод для всех scope: `main` (default), `usage`,
`hist`, `scheme`. Принимает массив `ops[]` с операциями `select`.

```json
{
  "ops": [
    {
      "select": {
        "selector": {
          "id": ["2a", "11"]
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## Операция `select`

- `selector` — обязательный. Predicate по полям узла / link /
  hist-change (см. [selectors.md](selectors.md)).
- `include` — опциональный массив строк. Допустимые значения: `"value"`,
  `"links"`, `"map_bindings"`. Пустой или отсутствующий = compact-форма.
- `max_tokens` — опциональный, положительное целое. Лимит токенов на
  выдачу операции. 0 или отрицательное возвращает `invalid_max_tokens`.
- `as` — опциональный alias (см. [selectors.md](selectors.md)).

## Compact-форма и полные узлы

При пустом или отсутствующем `include` узлы возвращаются в
compact-форме:

```json
{
  "id": "2a",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "content": "documents/spec"
  },
  "tokens": 320,
  "read_id": "31bf"
}
```

Поле `scope` сериализуется в ответе только для узлов вне main
(`scope=usage`, `scope=hist`, `scope=scheme`); для main опускается.

`tokens` — оценка стоимости полного `value` узла. `read_id` в
compact-форме — **state** read_id.

При `include` с `"value"`, если value целиком влез в `max_tokens`, узел
возвращается с полем `value` и **value** read_id:

```json
{
  "id": "2a",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "content": "documents/spec"
  },
  "value": "...",
  "tokens": 320,
  "read_id": "4c8f"
}
```

При `include` с `"links"` к узлу добавляется список incident links.
Каждый элемент содержит либо `to` (исходящий link от текущего узла),
либо `from` (входящий link к текущему узлу); противоположный endpoint
— сам текущий узел и в записи не дублируется.

```json
{
  "links": [
    {
      "name": "depends_on",
      "to": {
        "id": "63",
        "path": "DocsWalker/api/selectors"
      }
    },
    {
      "name": "described_by",
      "from": {
        "id": "c8",
        "path": "examples/depends-on"
      }
    }
  ]
}
```

## Truncation

Если общий результат превышает `max_tokens`, выдача обрезается:

```json
{
  "count": 12,
  "truncated": true,
  "stopped_at": "DocsWalker/api/write-ops",
  "omitted_count": 5,
  "items": [
    { "id": "11" }
  ]
}
```

- `count` — общее число узлов, удовлетворяющих селектору (включая
  отрезанные).
- `truncated` — `true`, если что-то срезано.
- `stopped_at` — `path` последнего возвращённого узла. Опционально.
- `omitted_count` — сколько узлов осталось за пределами выдачи.
- `items` — массив возвращённых узлов.

Truncated узел с запрошенным `value`, который не помещён, получает
только state read_id; value read_id выдаётся только для полностью
прочитанного value. Чтобы получить value read_id, LLM повторяет `read`
с более узким селектором или с увеличенным `max_tokens`.

## `read_id`

`read_id` — opaque hex-строка, которую kernel выдаёт при `read`.
Привязана к scope, id узла, версии состояния и scope-у чтения. LLM не
собирает и не модифицирует `read_id`.

Scope-ы `read_id`:

- **state** — подтверждает актуальную версию состояния узла. Для node
  это `path`, `title`, `map_bindings` и incident links на момент чтения.
  State `read_id` используется как write precondition: если узел
  изменился после чтения, его актуальный state `read_id` другой и `tx`
  отклоняется.
- **value** — подтверждает, что `value` узла прочитан целиком, без
  truncation, и фиксирует версию value на момент чтения. Value `read_id`
  одновременно удовлетворяет state precondition того же узла той же
  версии.

Compact read возвращает state `read_id`. Full read (`include=["value"]`,
без truncation) возвращает value `read_id`. Truncated full read
возвращает state `read_id`.

При успешном изменении узла kernel выпускает для него новый state и
value `read_id`. Старые перестают соответствовать актуальной версии.
Создание или удаление link обновляет state `read_id` его source и target
узлов.

`read_id` передаётся LLM в `tx.read_ids` (см.
[read-gates.md](read-gates.md)). Применимость:

- state precondition для изменяемых project-узлов принимает state или
  value `read_id` той же версии.
- usage rule / map / link gate требует value `read_id`
  соответствующего usage-узла.
- project value gate (для `tx scope=main`) требует value `read_id`
  main-узла, если применимый rule содержит
  `requires_project_value_read=true`.

## Примеры

### Compact-обзор main под path

```json
{
  "ops": [
    {
      "select": {
        "selector": {
          "path": "DocsWalker/api/**"
        },
        "max_tokens": 2000
      }
    }
  ]
}
```

Возвращает main-узлы под `DocsWalker/api/` в compact-форме, с state
`read_id` каждого.

### Full read одного узла под изменение

```json
{
  "ops": [
    {
      "select": {
        "selector": {
          "id": "2a"
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает узел `"2a"` целиком с value `read_id`. Этот `read_id` LLM
передаёт в `tx.read_ids` при `update` / `delete` / `move` этого узла.

### Чтение usage rule перед tx scope=main

```json
{
  "scope": "usage",
  "ops": [
    {
      "select": {
        "selector": {
          "path": "rules/api/write"
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
          "target.node.id": "2a"
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает все hist-change-узлы, описывающие изменения узла `"2a"` во
всех editable scope.
