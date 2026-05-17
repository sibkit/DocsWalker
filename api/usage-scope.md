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
- работы с селекторами, aliases, `expected_count`;
- подбора state и value `read_id` для `tx.read_ids`;
- понимания полей узла (`title`, `value`, `map_bindings`) и
  envelope-форматов;
- понимания кодов ошибок.

## Node contracts

В schema usage заведены типы узлов через `map_bindings.content` со
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

Map `content` обязательна. Дополнительные map в usage schema:

- `subject` — тема: `read`, `tx`, `selector`, `error`, `schema`,
  `workflow`, `scope`.
- `method` — `read` / `tx`.
- `field` — имя поля.
- `error_code` — код ошибки.
- `schema_name` — `main` / `usage`.
- `map` — имя map (для `usage/map`).
- `link_name` — имя link (для `usage/link`).

## Узел `usage/rule`

Rule — инструкция, которую LLM читает перед конкретной `tx scope=main`.
`applies_to` — JSON-header внутри `value`, описывает множество
main-узлов и операций, к которым rule относится.

```json
{
  "path": "rules/api/write",
  "title": "write",
  "map_bindings": {
    "content": "usage/rule",
    "subject": "tx",
    "method": "tx"
  },
  "value": {
    "applies_to": {
      "path": "DocsWalker/api/**",
      "map_bindings": {
        "audience": "llm-agent"
      },
      "links": {
        "name": "depends_on",
        "to": {
          "map_bindings": {
            "content": "documents/**"
          }
        }
      }
    },
    "requires_project_value_read": false,
    "instruction": "Перед изменением узлов под DocsWalker/api/** ... "
  }
}
```

- `applies_to` — обычный селектор по main-полям (см.
  [selectors.md](selectors.md)). Если `tx scope=main` затрагивает
  узлы под этот селектор, LLM читает rule через `read scope=usage` и
  передаёт его value `read_id` в `tx.read_ids`.
- `requires_project_value_read` — опциональный, default `false`. При
  `true` rule дополнительно требует full read main-узлов под
  `applies_to`, попадающих в tx (см. [read-gates.md](read-gates.md)).
- `instruction` — текст инструкции.

`applies_to` — pattern, который kernel вычисляет при подготовке tx;
link на main-узлы из rule не создаётся.

## Узел `usage/map`

Описание одной map из main или usage schema: зачем эта map, как
выбирать ветку, какие сочетания осмысленны.

```json
{
  "path": "maps/content",
  "title": "content",
  "map_bindings": {
    "content": "usage/map",
    "map": "content",
    "schema_name": "main"
  },
  "value": {
    "description": "...",
    "branch_usage_notes": {
      "documents/spec": "...",
      "documents/project": "..."
    }
  }
}
```

При создании или изменении узла с `map_bindings.<map>=<branch>` LLM
предварительно делает full-read этого usage-узла и передаёт его
value `read_id` в `tx.read_ids` (см. [read-gates.md](read-gates.md)).

## Узел `usage/link`

Описание одного link из main или usage schema.

```json
{
  "path": "links/depends_on",
  "title": "depends_on",
  "map_bindings": {
    "content": "usage/link",
    "link_name": "depends_on",
    "schema_name": "main"
  },
  "value": {
    "description": "...",
    "when_to_use": "...",
    "vs_similar_links": "..."
  }
}
```

Перед `link` в `tx` LLM делает full-read этого узла и передаёт value
`read_id` в `tx.read_ids`.

## Узел `usage/example`

Компактный JSON-example запроса или ответа. Примеры в MCP tool
descriptions не включаются — LLM явно читает их через `read scope=usage`.

```json
{
  "path": "examples/tx/move-with-expected-count",
  "title": "move-with-expected-count",
  "map_bindings": {
    "content": "usage/example",
    "subject": "tx",
    "method": "tx"
  },
  "value": {
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

Усage-узел ссылается link-ом на main-узел. Канонический кейс —
`usage/example → main/method-node`:

```json
{
  "scope": "usage",
  "title": "example-link-describes-method",
  "read_ids": ["..."],
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

## Read gates при `tx scope=usage`

Read gates в usage scope сводятся к state preconditions: для
изменения существующих usage-узлов нужен их state `read_id`. Rule /
map / link gates действуют только при `tx scope=main`. Подробности —
[read-gates.md](read-gates.md).
