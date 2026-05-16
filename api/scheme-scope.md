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
    "map_name": "content"
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
- `map_bindings.map_name` или `map_bindings.link_name` — имя
  описываемого объекта.
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
    "description": "Source-узел зависит от target-узла.",
    "source": {
      "map_bindings": {
        "content": "documents/**"
      }
    },
    "target": {
      "map_bindings": {
        "content": "documents/**"
      }
    },
    "cardinality": "many_to_many",
    "required_for": []
  }
}
```

`required_for` — массив `[]`, `["source"]`, `["target"]` или
`["source", "target"]`.

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
        "map_name": "content",
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
        "include": ["value", "map_bindings"],
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
        "include": ["value", "map_bindings"]
      }
    }
  ]
}
```

## Meta-schema

Meta-schema — kernel-owned JSON-файл `docs/.docswalker/meta-schema.json`.
Описывает:

- структуру узла (`id`, `path`, `title`, `value`, `map_bindings`);
- структуру link (`name`, `source.id`, `target.id`, `target.scope`);
- внутреннюю schema scheme scope (kind / owner_scope / map_name /
  link_name);
- структуру hist-узлов (`hist/transaction`, `hist/change`, target поля).

Meta-schema редактируется только kernel-ом, версионируется вместе с
релизами DocsWalker. При несовместимом изменении meta-schema оператор
мигрирует данные при старте kernel-а отдельным механизмом.

LLM читает meta-schema через `read scope=scheme` со специальным
маркером:

```json
{
  "scope": "scheme",
  "ops": [
    {
      "select": {
        "selector": {
          "meta": true
        },
        "include": ["value"]
      }
    }
  ]
}
```

Селектор возвращает один узел с полным содержимым meta-schema
(сериализованным в `value`).

Hist-schema — раздел meta-schema; отдельной точки чтения нет.
