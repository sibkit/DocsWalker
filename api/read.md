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
        "include": ["content"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## Операция `select`

`select` принимает одну из двух форм:

- **Объект** — обычное чтение узлов по predicate-селектору.
- **Строка** — имя kernel-режима для чтения служебных данных
  (см. «Kernel-режимы» ниже).

Парсер различает формы по типу значения.

### Форма-объект: чтение узлов

- `selector` — обязательный. Predicate по полям узла (см.
  [selectors.md](selectors.md)).
- `include` — опциональный массив строк. Имена полей, которые kernel
  дополнительно подгрузит сверх compact-формы. Допустимый набор для
  каждого класса узла объявлен в meta-schema (`loadable` поля; см.
  [model.md](model.md)). Имя за пределами этого набора возвращает
  `invalid_request`. Пустой или отсутствующий — compact-форма.
- `max_tokens` — опциональный, положительное целое. Лимит токенов на
  выдачу операции. 0 или отрицательное возвращает `invalid_max_tokens`.
- `as` — опциональный alias (см. [selectors.md](selectors.md)).

### Форма-строка: kernel-режимы

```json
{ "ops": [ { "select": "meta" } ] }
```

Строка — имя kernel-режима. Реестр режимов:

- `"meta"` — содержимое meta-schema (контракт data-узла, event-узла,
  link, scheme-схемы и hist-предикатов). Доступно при любом `scope`
  запроса. Возвращает один объект с полем `meta` и `read_id`:

  ```json
  {
    "result": {
      "meta": { /* содержимое meta-schema */ },
      "read_id": "..."
    }
  }
  ```

Имя за пределами реестра возвращает `unknown_select_mode`. `selector`,
`include`, `max_tokens`, `as` в форме-строке не применимы — присутствие
этих полей рядом невозможно (значение `select` — целая строка, не
объект).

## Compact-форма data-узла

При пустом или отсутствующем `include` data-узел возвращается в
compact-форме:

```json
{
  "id": "2a",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "category": "documents/spec"
  },
  "tokens": 320,
  "read_id": "31bf"
}
```

Поле `scope` сериализуется в ответе только для узлов вне main
(`scope=usage`, `scope=scheme`); для main опускается.

`tokens` — оценка стоимости полного `content` узла. `read_id` в
compact-форме — **state** read_id.

При `include` с `"content"`, если content целиком влез в `max_tokens`, узел
возвращается с полем `content` и **content** read_id:

```json
{
  "id": "2a",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "category": "documents/spec"
  },
  "content": "...",
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

## Compact-форма event-узла (hist)

При отсутствии `include` event-узел возвращается с top-level полями и
счётчиками секций:

```json
{
  "id": "f4",
  "title": "selectors-section-add",
  "date": "2026-05-14",
  "rollback_of": "8b20",
  "counts": {
    "created": { "nodes": 1, "links": 1 },
    "changed": { "nodes": 1 },
    "deleted": { "nodes": 1, "links": 1 }
  },
  "tokens": 1200,
  "read_id": "31bf"
}
```

- `counts.<section>.<kind>` — число элементов в подсекции. Подсекции с
  нулём опускаются.
- `tokens` — оценка стоимости полной формы (со всеми loadable полями).
- `read_id` — state read_id event-узла.

При `include` с одной или несколькими секциями event-узел возвращается
с этими секциями раскрытыми. Если все запрошенные секции уложились в
`max_tokens` — возвращается **content** read_id (по аналогии с data-узлом
для полного content). Если часть секций обрезана — state read_id.

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
- `stopped_at` — `path` или `id` последнего возвращённого узла.
  Опционально.
- `omitted_count` — сколько узлов осталось за пределами выдачи.
- `items` — массив возвращённых узлов.

Truncated узел с запрошенным `content` / секцией, который не помещён,
получает только state read_id; content read_id выдаётся только для
полностью прочитанного content (или для полностью раскрытых секций
event-узла). Чтобы получить content read_id, LLM повторяет `read` с более
узким селектором или с увеличенным `max_tokens`.

## `read_id`

`read_id` — opaque hex-строка, которую kernel выдаёт при `read`.
Привязана к scope, id узла, версии состояния и scope-у чтения. LLM не
собирает и не модифицирует `read_id`.

Scope-ы `read_id`:

- **state** — подтверждает актуальную версию состояния узла. Для
  data-узла это `path`, `title`, `map_bindings` и incident links на
  момент чтения. Для event-узла это сам факт существования узла с этим
  `id` (event-узлы immutable — после записи kernel не правит их).
  State `read_id` используется как write precondition: если узел
  изменился после чтения, его актуальный state `read_id` другой и `tx`
  отклоняется.
- **content** — подтверждает, что смысл узла прочитан целиком, без
  truncation: для data-узла — `content`; для event-узла — все секции,
  заявленные в `include`. Content `read_id` одновременно удовлетворяет
  state precondition того же узла той же версии.

Compact read возвращает state `read_id`. Full read
(`include=["content"]` для data-узла / `include=["created","changed","deleted"]`
для event-узла, без truncation) возвращает content `read_id`. Truncated
full read возвращает state `read_id`.

При успешном изменении data-узла kernel выпускает для него новый state
и content `read_id`. Старые перестают соответствовать актуальной версии.
Создание или удаление link обновляет state `read_id` его source и
target узлов. Event-узлы не меняются после записи, их `read_id`-ы не
устаревают (кроме случая перестройки всей hist-схемы, что эквивалентно
смене версии kernel).

`read_id` передаётся LLM в `tx.read_ids` (см.
[read-gates.md](read-gates.md)). Применимость:

- state precondition для изменяемых project-узлов принимает state или
  content `read_id` той же версии.
- usage rule / map / link gate требует content `read_id`
  соответствующего usage-узла.
- project content gate (для `tx scope=main`) требует content `read_id`
  main-узла, если применимый rule содержит
  `requires_project_content_read=true`.

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
        "include": ["content", "links"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает узел `"2a"` целиком с content `read_id`. Этот `read_id` LLM
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
        "include": ["content", "links"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает rule-узел с content `read_id`. Этот `read_id` LLM передаёт в
`tx.read_ids` при затрагивающем main-узлы tx (см.
[read-gates.md](read-gates.md)).

### Чтение истории узла

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": { "touches_node": "2a" },
        "include": ["created", "changed", "deleted"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает все event-узлы `hist/transaction`, в секциях которых
фигурирует узел `"2a"` (в любой роли — создан, изменён или удалён),
во всех editable scope.
