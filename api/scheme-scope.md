# Scope `scheme`

`scheme` — editable scope, в котором живут редактируемые контракты Схемы
для `main` и `usage`. Meta-schema и hist-schema лежат отдельно, в
`docs/.docswalker/meta-schema.json` (kernel-owned).

LLM читает scheme через `read scope=scheme` и меняет через
`tx scope=scheme`. Каждое изменение проходит breaking-change-check
относительно существующих данных в main / usage.

## Что лежит в scheme

В scheme scope живут узлы двух описаний:

- описание map для main или usage;
- описание link для main или usage.

Map node:

```json
{
  "path": "main/content",
  "title": "content",
  "map_bindings": {
    "kind": "map",
    "owner_scope": "main",
    "map": "content"
  },
  "value": {
    "description": "Классифицирует назначение main-узла по типу содержимого.",
    "branches": {
      "documents": {
        "project": {},
        "spec": {}
      },
      "examples": {
        "csharp": {}
      }
    },
    "required": true,
    "required_when": null
  }
}
```

- `map_bindings.kind` ∈ {`map`, `link`}.
- `map_bindings.owner_scope` ∈ {`main`, `usage`}.
- `map_bindings.map` или `map_bindings.link_name` — имя описываемого
  объекта.
- `value` содержит структурированное описание (branches, required,
  source / target constraints, cardinality и т.д.).

Link node:

```json
{
  "path": "main/depends_on",
  "title": "depends_on",
  "map_bindings": {
    "kind": "link",
    "owner_scope": "main",
    "link_name": "depends_on"
  },
  "value": {
    "description": "From-узел зависит от to-узла.",
    "from": {
      "map_bindings": {
        "content": "documents/**"
      }
    },
    "to": {
      "map_bindings": {
        "content": "documents/**"
      }
    },
    "cardinality": "many_to_many",
    "required_for": []
  }
}
```

`required_for` — массив `[]`, `["from"]`, `["to"]` или
`["from", "to"]`.

## Schema scope-а

Сам scheme scope имеет минимальную внутреннюю schema, описанную в
meta-schema. `tx scope=scheme` оперирует созданием / правкой / удалением
map-узлов и link-узлов.

`scope` для нового описания задаётся через `owner_scope`:

- `owner_scope=main` — описание относится к контракту main scope.
- `owner_scope=usage` — описание относится к контракту usage scope.

## Breaking-change-check

`tx scope=scheme` проходит проверку: после применения tx все
существующие узлы main и usage обязаны соответствовать новой схеме.
Нарушение возвращает:

```json
{
  "code": "schema_breaks_existing_data",
  "details": {
    "violations": [
      {
        "scope": "main",
        "id": "2a",
        "reason": "map_branch_unknown",
        "map": "content",
        "violating_value": "documents/legacy"
      }
    ]
  }
}
```

`tx` отклоняется атомарно. LLM подготавливает миграцию руками (см.
ниже).

## Add-then-remove migration workflow

Миграция Схемы делается последовательностью non-breaking шагов.
Канонический шаблон для переименования / реорганизации map или link:

1. **Добавить новую структуру** через `tx scope=scheme`. Новая map /
   link не required и не пересекается с существующими данными —
   additive change, tx проходит.
2. **Мигрировать данные** через одну или несколько `tx scope=main` /
   `tx scope=usage`: для каждого затронутого узла добавить новую
   привязку или новый link. Каждый шаг non-breaking.
3. **Переключить required / constraints** через `tx scope=scheme`:
   усилить новую структуру до required и/или ослабить старую до
   `deprecated`. При незаконченной миграции данных tx возвращает
   `schema_breaks_existing_data`; LLM доделывает шаг 2 на остальных
   узлах.
4. **Удалить старые привязки в данных** через `tx scope=main` /
   `tx scope=usage`.
5. **Удалить старую структуру в схеме** через `tx scope=scheme`.

Пример «переименовать map `subject` в `topic`»:

```
1. tx scope=scheme: создать map "topic" (с теми же branches, не required).
2. tx scope=main: для каждого узла добавить map_bindings.topic = old subject value.
3. tx scope=scheme: сделать "topic" required (если был required); сделать "subject" deprecated.
4. tx scope=main: для каждого узла удалить map_bindings.subject.
5. tx scope=scheme: удалить map "subject".
```

Каждый шаг — атомарная tx, каждый non-breaking сам по себе.

## Чтение схемы

```json
{
  "scope": "scheme",
  "ops": [
    {
      "select": {
        "selector": {
          "map_bindings": {
            "kind": "map",
            "owner_scope": "main"
          }
        },
        "include": ["value"],
        "max_tokens": 8000
      }
    }
  ]
}
```

Возвращает все map-описания main-схемы. Аналогично для link-описаний и
для usage-схемы.

Точечное чтение по имени:

```json
{
  "scope": "scheme",
  "ops": [
    {
      "select": {
        "selector": {
          "path": "main/content"
        },
        "include": ["value"]
      }
    }
  ]
}
```

## Meta-schema

Meta-schema — kernel-owned JSON-файл `docs/.docswalker/meta-schema.json`.
Описывает два класса узлов (см. [model.md](model.md)):

- **Data-узел** (main / usage / scheme): `id`, `path`, `title`, `value`,
  `map_bindings`.
- **Event-узел** (hist `hist/transaction`): `id`, `title`, `date`,
  `description?`, `rollback_of?`, секции `created` / `changed` /
  `deleted`.

А также:

- структуру link (`name`, `from.id`, `to.id`) для data-scope-ов;
- внутреннюю schema scheme scope (kind / owner_scope / map / link_name);
- hist-specific селекторные предикаты (`touches_node`, `touches_link`,
  `tx_scope`, `rollback_of`).

Meta-schema редактируется только kernel-ом, версионируется вместе с
релизами DocsWalker. При несовместимом изменении meta-schema оператор
мигрирует данные при старте kernel-а отдельным механизмом.

LLM читает meta-schema через отдельную форму `select` (строка вместо
объекта-селектора — см. [read.md](read.md), раздел «Форма-строка:
kernel-режимы»):

```json
{
  "ops": [
    { "select": "meta" }
  ]
}
```

Возвращает объект с полем `meta` (полное содержимое meta-schema) и
`read_id`. `scope` запроса для этой формы не важен — meta-schema живёт
вне scope-ов.

Hist-schema — раздел meta-schema; отдельной точки чтения нет.
