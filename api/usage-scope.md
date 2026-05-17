# Scope `usage`

`usage` — editable scope для инструкций LLM: rules, examples,
topic-объяснения, описания методов и полей, описания ошибок, описания
map и link из схемы. LLM редактирует usage через `tx scope=usage`.

После чтения нужных usage-узлов LLM получает контекст для:

- чтения main через `read`;
- чтения hist через `read scope=hist`;
- чтения scheme через `read scope=scheme`;
- записи через `tx`;
- составления `title` и `description` транзакции;
- отката tx через `rollback`;
- обработки `rollback_conflict`;
- работы с селекторами, aliases, `expected_count`, `expected_version`;
- понимания полей узла (`title`, `content`, `map_bindings`) и
  envelope-форматов;
- понимания кодов ошибок.

## Node contracts

В schema usage заведены типы узлов через `map_bindings.category` со
значениями `usage/*`:

- `usage/topic` — общая тема или workflow.
- `usage/method` — описание метода API / MCP.
- `usage/field` — описание поля request / response / селектора.
- `usage/error` — описание кода ошибки и поведения LLM.
- `usage/schema` — описание одной schema (main или usage), читаемой
  через scheme scope.
- `usage/map` — описание одной map из main или usage схемы.
- `usage/link` — описание одного link из main или usage схемы.
- `usage/example` — компактный JSON-example.
- `usage/rule` — обязательная инструкция, привязанная к подмножеству
  main-узлов через `applies_to`.

Map `category` обязательна. Дополнительные map в usage schema:

- `subject` — тема: `read`, `tx`, `selector`, `error`, `schema`,
  `workflow`, `scope`.
- `method` — `read` / `tx`.
- `field` — имя поля.
- `error_code` — код ошибки.
- `schema_name` — `main` / `usage`.
- `map` — имя map (для `usage/map`).
- `link_name` — имя link (для `usage/link`).

## Формат `content` в usage

Поле `content` у data-узла — всегда строка на уровне API (см.
[model.md](model.md)). У usage-узлов с структурным контрактом
(`usage/rule`, `usage/map`, `usage/link`, `usage/example`) в `content`
кладётся escaped-JSON соответствующего shape; kernel парсит строку для
механизмов, которым нужен shape (rule applicability, gate computation).

Примеры ниже показывают `content` развёрнутым JSON-объектом для
читаемости. На уровне API он сериализуется как строка с escaped-JSON
внутри. У `usage/topic`, `usage/method`, `usage/field`, `usage/error`,
`usage/schema` `content` обычно markdown/plain text без структуры.

## Узел `usage/rule`

Rule — инструкция, которую LLM читает перед конкретной `tx scope=main`.
`applies_to` — JSON-header внутри `content`, описывает множество
main-узлов и операций, к которым rule относится.

```json
{
  "path": "rules/api/write",
  "title": "write",
  "map_bindings": {
    "category": "usage/rule",
    "subject": "tx",
    "method": "tx"
  },
  "content": {
    "applies_to": {
      "path": "DocsWalker/api/**",
      "map_bindings": {
        "audience": "llm-agent"
      },
      "links": {
        "name": "depends_on",
        "to": {
          "map_bindings": {
            "category": "documents/**"
          }
        }
      }
    },
    "instruction": "Перед изменением узлов под DocsWalker/api/** ... "
  }
}
```

- `applies_to` — обычный селектор по main-полям (см.
  [selectors.md](selectors.md)). Если `tx scope=main` затрагивает
  узлы под этот селектор, LLM **по дисциплине** читает rule через
  `read scope=usage` перед составлением `tx` и следует его
  `instruction`. Серверного gate-механизма kernel не накладывает.
- `instruction` — текст инструкции.

`applies_to` — pattern, который LLM использует при отборе применимых
rules; link на main-узлы из rule не создаётся.

## Узел `usage/map`

Описание одной map из main или usage schema: зачем эта map, как
выбирать ветку, какие сочетания осмысленны.

```json
{
  "path": "maps/category",
  "title": "category",
  "map_bindings": {
    "category": "usage/map",
    "map": "category",
    "schema_name": "main"
  },
  "content": {
    "description": "...",
    "branch_usage_notes": {
      "documents/spec": "...",
      "documents/project": "..."
    }
  }
}
```

При создании или изменении узла с `map_bindings.<map>=<branch>` LLM
**по дисциплине** предварительно читает соответствующий `usage/map`,
чтобы выбрать правильную ветку. Серверного gate-механизма kernel не
накладывает; ошибки выбора всплывут при `validation_failed` /
`unknown_map`.

## Узел `usage/link`

Описание одного link из main или usage schema.

```json
{
  "path": "links/depends_on",
  "title": "depends_on",
  "map_bindings": {
    "category": "usage/link",
    "link_name": "depends_on",
    "schema_name": "main"
  },
  "content": {
    "description": "...",
    "when_to_use": "...",
    "vs_similar_links": "..."
  }
}
```

Перед `link` в `tx` LLM **по дисциплине** читает соответствующий
`usage/link`, чтобы выбрать подходящее имя и направление. Серверного
gate-механизма kernel не накладывает; ошибки выбора всплывут при
`validation_failed` / `unknown_link` / `cross_scope_not_allowed`.

## Узел `usage/example`

Компактный JSON-example запроса или ответа. Примеры в MCP tool
descriptions не включаются — LLM явно читает их через `read scope=usage`.

```json
{
  "path": "examples/tx/move-with-expected-count",
  "title": "move-with-expected-count",
  "map_bindings": {
    "category": "usage/example",
    "subject": "tx",
    "method": "tx"
  },
  "content": {
    "request": {
      "title": "...",
      "ops": [{ "move": { "...": "..." } }]
    },
    "expected_response": {
      "result": { "id": "a3f1c2" }
    }
  }
}
```

## Cross-scope links `usage → main`

Usage-узел ссылается link-ом на main-узел. Канонический кейс —
`usage/example → main/method-node`:

```json
{
  "scope": "usage",
  "title": "example-link-describes-method",
  "ops": [
    {
      "link": {
        "name": "describes",
        "from": "c8",
        "to": "2a",
        "expected_count": 1
      }
    }
  ]
}
```

Поскольку id узла глобально уникален, scope target однозначно
выводится из `to` — отдельно `to.scope` указывать не нужно.

Удаление main-узла `"2a"` при наличии этого link возвращает
`delete_blocked_by_cross_scope_link`. LLM переключает link на другой
main-узел или удаляет сам usage-узел.

## Concurrency при `tx scope=usage`

Защита от lost update для `update` usage-узлов работает так же, как
для main: LLM передаёт `expected_version` в `tx.update`, при
расхождении kernel возвращает `version_mismatch` (см. [tx.md](tx.md),
раздел `update` и [errors.md](errors.md), раздел
`version_mismatch`). Для `move`, `delete`, `link`, `unlink`
concurrency-precondition отдельным полем не передаётся — конфликты
ловятся через `expected_count` для bulk-операций.
