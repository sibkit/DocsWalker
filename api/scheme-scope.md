# Scope `scheme`

`scheme` — editable scope, в котором живут **редактируемые** контракты
Схемы для `main` и `usage`. Meta-schema и hist-schema в scheme scope
не лежат; они kernel-owned (см. ниже).

LLM читает scheme через `read scope=scheme` и меняет через
`tx scope=scheme`. Любое изменение проходит обязательный
breaking-change-check относительно существующих данных в main / usage.

## Что лежит в scheme

В scheme scope живут узлы двух описаний:

- описание map для main или usage,
- описание link для main или usage.

Map node:

```json
{
  "node.path": "main/content",
  "node.title": "content",
  "node.map_bindings": {
    "kind": "map",
    "owner_scope": "main",
    "map_name": "content"
  },
  "node.value": {
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
- `map_bindings.owner_scope` ∈ {`main`, `usage`} — какой scope использует
  эту map / link.
- `map_bindings.map_name` или `map_bindings.link_name` — имя описываемого
  объекта.
- `value` содержит структурированное описание (branches, required,
  source / target constraints, cardinality и т.д.).

Link node:

```json
{
  "node.path": "main/depends_on",
  "node.title": "depends_on",
  "node.map_bindings": {
    "kind": "link",
    "owner_scope": "main",
    "link_name": "depends_on"
  },
  "node.value": {
    "description": "Source-узел зависит от target-узла.",
    "source": {
      "node.map_bindings": {
        "content": "documents/**"
      }
    },
    "target": {
      "node.map_bindings": {
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
meta-schema. Эта схема жёстко зафиксирована и не редактируется через
`tx scope=scheme`. Изменения в scheme scope = добавление / правка /
удаление map-узлов и link-узлов.

`scope` для нового map / link определяется через `owner_scope`:

- `owner_scope=main` — описание относится к контракту main scope.
- `owner_scope=usage` — описание относится к контракту usage scope.

Описаний с `owner_scope=hist` или `owner_scope=scheme` в редактируемой
scheme не бывает: hist-формат — часть meta-schema, scheme-формат — часть
meta-schema.

## Breaking-change-check

Любая `tx scope=scheme` проходит проверку: после применения tx
все существующие узлы main и usage обязаны соответствовать новой
схеме. Если хотя бы один узел нарушает (например, у него есть привязка
к map, которую tx удаляет; или есть link, для которого tx сужает
constraints так, что данный link становится недопустимым), tx
возвращает:

```json
{
  "code": "schema_breaks_existing_data",
  "details": {
    "violations": [
      {
        "scope": "main",
        "node.id": 42,
        "reason": "map_branch_unknown",
        "map_name": "content",
        "violating_value": "documents/legacy"
      }
    ]
  }
}
```

`tx` отклоняется атомарно. LLM подготовит миграцию руками (см. ниже).

## Add-then-remove migration workflow

Поскольку breaking change отклоняется жёстко, миграция Схемы делается
**последовательностью** non-breaking шагов. Канонический шаблон для
переименования / реорганизации map или link:

1. **Добавить новую структуру** через `tx scope=scheme`. Новая map / link
   не required и не пересекается с существующими данными — это additive
   non-breaking change, tx проходит.
2. **Мигрировать данные** через одну или несколько `tx scope=main` /
   `tx scope=usage`: для каждого затронутого узла добавить новую
   привязку или новый link. Каждый шаг non-breaking, tx проходит.
3. **Переключить required / constraints** через `tx scope=scheme`:
   усилить новую структуру до required и/или ослабить старую до
   `deprecated`. Если данные уже мигрированы (шаг 2), tx проходит; если
   что-то пропущено, tx возвращает `schema_breaks_existing_data`, и LLM
   доделывает шаг 2 на остальных узлах.
4. **Удалить старые привязки в данных** через `tx scope=main` /
   `tx scope=usage`.
5. **Удалить старую структуру в схеме** через `tx scope=scheme`. Теперь
   данных, ссылающихся на неё, нет — tx проходит.

Пример «переименовать map `subject` в `topic`»:

```
1. tx scope=scheme: create map "topic" (с теми же branches, не required).
2. tx scope=main: для каждого узла добавить map_bindings.topic = old subject value.
3. tx scope=scheme: сделать "topic" required (если был required); сделать "subject" deprecated.
4. tx scope=main: для каждого узла удалить map_bindings.subject.
5. tx scope=scheme: удалить map "subject".
```

Каждый из пяти шагов — атомарная tx, каждый non-breaking сам по себе.

## Чтение схемы

LLM читает scheme scope обычным `read scope=scheme`:

```json
{
  "scope": "scheme",
  "ops": [
    {
      "select": {
        "selector": {
          "node.map_bindings": {
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

Альтернативно — точечно по имени:

```json
{
  "scope": "scheme",
  "ops": [
    {
      "select": {
        "selector": {
          "node.path": "main/content"
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

- структуру узла (`node.id`, `node.path`, `node.title`, `node.value`,
  `node.map_bindings`);
- структуру link (`link.name`, `link.source.id`, `link.target.id`,
  `link.target.scope`);
- внутреннюю schema scheme scope (kind / owner_scope / map_name /
  link_name);
- структуру hist-узлов (`hist/transaction`, `hist/change`, target поля).

LLM не редактирует meta-schema через tx. Она версионируется вместе с
релизами DocsWalker. Когда новая версия kernel вводит несовместимое
изменение meta-schema, оператор мигрирует данные при старте kernel-а
отдельным механизмом (вне LLM-facing API).

LLM может **прочитать** meta-schema через `read scope=scheme` со
специальным маркером:

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

Этот селектор возвращает один узел с полным содержимым meta-schema
(сериализованным в `value`).

Hist-schema не имеет отдельной точки чтения: её описание — часть
meta-schema (раздел про hist-узлы).
