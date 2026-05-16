# Scope `usage`

`usage` — editable scope, в котором живут инструкции для LLM: rules,
examples, topic-объяснения, описания методов и полей, описания ошибок и
любая другая read-side документация. LLM редактирует usage через
`tx scope=usage`.

Usage не дублирует данные main и не задаёт собственные правила записи в
main — этим занимается scheme. Usage именно про **как** работать,
**что** означает та или иная map / link / op, **почему** именно так.

## Зачем нужен

После чтения нужных usage-узлов LLM должна знать достаточно, чтобы:

- читать main через `read scope=main`;
- читать hist через `read scope=hist`;
- читать scheme через `read scope=scheme`;
- писать main через `tx`;
- писать `commit_message`;
- откатывать tx через `rollback`;
- понимать `rollback_conflict`;
- пользоваться селекторами, aliases, `expected_count`;
- читать main-узлы через `read` и передавать полученные state `read_id`
  в `tx.read_ids` для защиты от stale overwrite;
- full-read-ить main-узлы, когда применимый rule требует чтения value
  перед записью;
- full-read-ить usage-узлы с `map_bindings.content=usage/map` перед
  назначением map_bindings;
- full-read-ить usage-узлы с `map_bindings.content=usage/link` перед
  созданием link;
- понимать поля узла (`title`, `value`, `map_bindings`) и
  envelope-форматы;
- понимать коды ошибок.

## Node contracts

В schema usage заведены типы узлов через `map_bindings.content` со
значениями `usage/*`. Минимальный набор:

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

Map `content` обязательна; конкретное значение задаёт node contract.
Дополнительные map в usage schema:

- `subject` — тема: `read`, `tx`, `selector`, `error`, `schema`,
  `workflow`, `scope`.
- `method` — `read` / `tx` — если узел про метод.
- `field` — имя поля.
- `error_code` — код ошибки.
- `schema_name` — `main` / `usage` — если узел про конкретную схему.
- `map_name` — имя map, если узел `usage/map`.
- `link_name` — имя link, если узел `usage/link`.

## Узел `usage/rule`

Rule — обязательная инструкция, которую LLM должна прочитать перед
конкретной `tx scope=main`. У rule есть `applies_to` — JSON-header
внутри `value`, который описывает, к каким main-узлам и операциям rule
относится.

```json
{
  "node.path": "rules/api/write",
  "node.title": "write",
  "node.map_bindings": {
    "content": "usage/rule",
    "subject": "tx",
    "method": "tx"
  },
  "node.value": {
    "applies_to": {
      "node.path": "DocsWalker/api/**",
      "node.map_bindings": {
        "audience": "llm-agent"
      },
      "node.links": {
        "name": "depends_on",
        "to": {
          "node.map_bindings": {
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

- `applies_to` — обычный селектор по main-полям (см. [selectors.md](selectors.md)).
  Если `tx scope=main` затрагивает узлы, подходящие под этот селектор,
  LLM обязана прочитать rule через `usage` и передать его value
  `read_id` в `tx.read_ids`.
- `requires_project_value_read` — опциональный, default `false`. Если
  `true`, rule дополнительно требует full read main-узлов, попадающих
  под `applies_to` и одновременно затронутых tx (см.
  [read-gates.md](read-gates.md)).
- `instruction` — текст инструкции, который LLM применяет.

`applies_to` не создаёт link между usage-rule и main-узлами — это
pattern, который kernel вычисляет при подготовке tx.

## Узел `usage/map`

Описание одной map из main или usage schema, в человекочитаемой форме:
зачем эта map, как выбирать ветку, какие сочетания осмысленны, каких
комбинаций избегать.

```json
{
  "node.path": "maps/content",
  "node.title": "content",
  "node.map_bindings": {
    "content": "usage/map",
    "map_name": "content",
    "schema_name": "main"
  },
  "node.value": {
    "description": "...",
    "branch_usage_notes": {
      "documents/spec": "...",
      "documents/project": "..."
    }
  }
}
```

При создании или изменении узла с `map_bindings.<map>=<branch>` LLM
должна предварительно full-read-ить этот usage-узел и передать его
value `read_id` в `tx.read_ids`. См. [read-gates.md](read-gates.md).

## Узел `usage/link`

Описание одного link из main или usage schema.

```json
{
  "node.path": "links/depends_on",
  "node.title": "depends_on",
  "node.map_bindings": {
    "content": "usage/link",
    "link_name": "depends_on",
    "schema_name": "main"
  },
  "node.value": {
    "description": "...",
    "when_to_use": "...",
    "vs_similar_links": "..."
  }
}
```

Перед `link` в `tx` LLM должна full-read-ить этот узел и передать value
`read_id` в `tx.read_ids`.

## Узел `usage/example`

Компактный JSON-example запроса или ответа. Примеры не включаются в
MCP tool descriptions по умолчанию — LLM явно читает их через `usage`,
если нужна точная форма.

```json
{
  "node.path": "examples/tx/move-with-expected-count",
  "node.title": "move-with-expected-count",
  "node.map_bindings": {
    "content": "usage/example",
    "subject": "tx",
    "method": "tx"
  },
  "node.value": {
    "request": {
      "scope": "main",
      "commit_message": "...",
      "ops": [{ "move": { "...": "..." } }]
    },
    "expected_response": {
      "result": { "tx_id": "tx_..." }
    }
  }
}
```

## Cross-scope links `usage → main`

Усage-узел может ссылаться link-ом на main-узел. Канонический кейс —
`usage/example → main/method-node`:

```json
{
  "scope": "usage",
  "commit_message": "связать example с описанием метода в main",
  "read_ids": ["..."],
  "ops": [
    {
      "link": {
        "name": "describes",
        "from": { "id": 200 },
        "to": {
          "id": 42,
          "scope": "main"
        },
        "expected_count": 1
      }
    }
  ]
}
```

Если main-узел 42 удаляется, и на него есть этот link, `tx scope=main`
для удаления возвращает `delete_blocked_by_cross_scope_link` со списком
блокирующих usage-узлов. LLM либо переключает link на другой main-узел,
либо удаляет сам usage-узел.

Других направлений cross-scope нет (см. [model.md](model.md)).

## Read gates при `tx scope=usage`

Read gates в usage scope ограничены **state preconditions**. То есть
для изменения существующих usage-узлов нужен их state `read_id`, но
rule / map / link gates не действуют. Это сделано, чтобы избежать
самореференса (editing rule про editing rules — рекурсия без пользы).

Подробности — [read-gates.md](read-gates.md).
