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
  link и набор селекторов / tx-ops / kernel-режимов). Доступно при
  любом `scope` запроса. Возвращает один объект с полем `meta`:

  ```json
  {
    "result": {
      "meta": {
        "version": "2",
        "scopes": {
          "main":   { "editable": true,  "default": true,  "temporal": true },
          "usage":  { "editable": true,  "temporal": true },
          "scheme": { "editable": true,  "temporal": true, "breaking_check": true },
          "hist":   { "editable": false, "append_only": true }
        },
        "node_classes": {
          "data":  { "scopes": ["main","usage","scheme"], "fields": { "<name>": { "kind": "...", "compact|loadable": true, ... } } },
          "event": { "scopes": ["hist"],                  "fields": { "<name>": { ... } } }
        },
        "link": {
          "identity": ["name","from.id","to.id"],
          "allowed_directions": [
            { "from": "main",  "to": "main"  },
            { "from": "usage", "to": "usage" },
            { "from": "usage", "to": "main"  }
          ]
        },
        "data_selectors": ["id","path","title","map_bindings","links","match"],
        "hist_selectors": ["id","title","date","description","rollback_of","tx_scope","touches_node","touches_link"],
        "tx_ops":       ["create","update","move","delete","link","unlink","rollback"],
        "read_ops":     ["select"],
        "kernel_modes": ["meta"],
        "at_forms":     ["<tx_id>", { "before": "<tx_id>" }]
      }
    }
  }
  ```

  Помета `compact` у поля означает, что оно всегда возвращается в
  ответе `read` (включая compact-форму без `include`). Помета
  `loadable` — поле возвращается только если запрошено через `include`.
  Meta-schema kernel-owned, версионируется с релизом DocsWalker, не
  подчиняется concurrency-полю `version`.

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
    "категория": "документы/спека"
  },
  "tokens": 320,
  "version": 7
}
```

Поле `scope` сериализуется в ответе только для узлов вне main
(`scope=usage`, `scope=scheme`); для main опускается.

`tokens` — оценка стоимости полного `content` узла. `version` — текущая
ревизия узла; LLM передаёт её в `tx.update.expected_version` для
защиты от lost update (см. [tx.md](tx.md)).

При `include` с `"content"`, если content целиком влез в `max_tokens`, узел
возвращается с полем `content`:

```json
{
  "id": "2a",
  "path": "DocsWalker/api/write-ops",
  "title": "write-ops",
  "map_bindings": {
    "категория": "документы/спека"
  },
  "content": "...",
  "tokens": 320,
  "version": 7
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
      "name": "зависит-от",
      "to": {
        "id": "63",
        "path": "DocsWalker/api/selectors"
      }
    },
    {
      "name": "описывает",
      "from": {
        "id": "c8",
        "path": "examples/зависит-от"
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
  "tokens": 1200
}
```

- `counts.<section>.<kind>` — число элементов в подсекции. Подсекции с
  нулём опускаются.
- `tokens` — оценка стоимости полной формы (со всеми loadable полями).

При `include` с одной или несколькими секциями event-узел возвращается
с этими секциями раскрытыми. Event-узлы `version` не имеют (hist
append-only), в ответе это поле для них не возвращается.

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

Truncated data-узел возвращается с тем же `version`, что и непрерванный
— `version` отражает ревизию узла, а не полноту переданного `content`.
Если LLM нужен полный `content` (например, для семантического анализа
перед изменением), она повторяет `read` с более узким селектором или
с увеличенным `max_tokens`.

## `version`

`version` — целочисленный счётчик ревизии data-узла. Стартует с `1`
при `create`, инкрементируется при каждом изменении состояния узла
(см. [model.md](model.md), раздел «Служебные поля ответа `read`»).
Возвращается в compact-форме и full-форме (с `include`) для всех
data-узлов (`main`, `usage`, `scheme`). Event-узлы (`hist`) `version`
не имеют — hist append-only.

LLM использует `version` как координату состояния для optimistic
concurrency: после чтения и подготовки правки она передаёт ту же
ревизию обратно в `tx.update.expected_version`. Если узел изменился
между чтением и записью (например, конкурентным `tx`), kernel
возвращает `version_mismatch` с актуальным `current` — LLM перечитывает
узел и решает, как поступить с новой версией.

Поле `version` не привязано к полноте чтения `content`: truncated full
read возвращает ту же `version`, что и compact read той же ревизии.
`expected_version` достаточно по самому факту совпадения числа.

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

Возвращает main-узлы под `DocsWalker/api/` в compact-форме, с `version`
каждого.

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

Возвращает узел `"2a"` целиком с `version`. Эту ревизию LLM передаёт
в `tx.update.expected_version` при последующем изменении узла.

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

Возвращает rule-узел с `content` и `links`. Чтение rules перед
`tx scope=main` — дисциплина LLM (см. [usage-scope.md](usage-scope.md),
раздел «Узел `usage/rule`»), серверного gate-механизма kernel не
накладывает.

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

### Time-travel read

```json
{
  "at": "a3f1c2",
  "ops": [
    {
      "select": {
        "selector": { "path": "DocsWalker/api/**" }
      }
    }
  ]
}
```

Возвращает main-узлы под `DocsWalker/api/` в состоянии после
применения tx `a3f1c2`. Состояние перед применением:

```json
{
  "at": { "before": "a3f1c2" },
  "ops": [
    { "select": { "selector": { "path": "DocsWalker/api/**" } } }
  ]
}
```

Поле `version` в обоих ответах отсутствует — tx над прошлым моментом
не поддерживается, и concurrency-precondition в `at`-ответах смысла
не имеет. Подробности — [model.md](model.md), раздел «Темпоральные
чтения (`at`)».
