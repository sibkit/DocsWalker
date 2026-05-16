# Usage Graph

`usage` - read-only instruction graph для LLM. Он хранит не project data и
не hist data, а инструкции, schemas, workflows и examples по работе с
DocsWalker.

Метод `usage` работает так же, как `query`: принимает `select` operations,
`include`, `max_tokens`, selector links и `selector.match`. Метод всегда читает
usage graph и usage schema.

LLM читает короткий quickstart из MCP tool descriptions, а подробности получает
через `usage`.

## Graph

Usage graph имеет адрес:

```
localhost/{project}/usage
```

Данные usage graph не смешиваются с project graph и hist graph.
LLM не может изменять usage: он формируется DocsWalker.

## Required Knowledge

После чтения нужных узлов `usage` у LLM должно быть достаточно контекста,
чтобы работать с DocsWalker:

- читать project graph через `query`;
- читать hist graph через `hist`;
- писать изменения через `tx`;
- писать `commit_message`;
- откатывать транзакции по полученному `tx_id` через `tx` с `rollback` operation;
- понимать `rollback_conflict`;
- пользоваться selectors, selector slots и `expected_count` для `move`, `link`
  и `unlink`;
- читать project nodes через `query` и передавать полученные state `read_id` в
  `tx.read_ids` для защиты write от stale overwrite;
- full-read-ить project nodes через `query` и передавать полученные value
  `read_id` в `tx.read_ids`, когда применимый rule явно требует чтения project
  value перед записью;
- full-read-ить usage nodes с `map_bindings.content=usage/map` и передавать
  полученные `read_id` в `tx.read_ids` при назначении `map_bindings`;
- full-read-ить usage nodes с `map_bindings.content=usage/link` и передавать
  полученные `read_id` в `tx.read_ids` при создании link;
- понимать поля узла `title`, `value` и `map_bindings`;
- понимать request/response envelopes и ошибки;
- понимать schemas project, hist и usage.

## Usage Schema

`usage` graph использует отдельную schema `usage`. Maps, node contracts, links
и обязательные schema nodes описаны в [scheme.md](scheme.md).

## Read Gates

Read gate - server-side проверка перед `tx`. Kernel резолвит будущую
write-транзакцию, определяет state preconditions и schema-defined nodes, которые
LLM должна была прочитать, и сравнивает их с `tx.read_ids`.

Read gate применяется как минимум к:

- существующим project nodes, которые write меняет, удаляет или структурно
  затрагивает; для них достаточно state `read_id`, если Схема не требует value;
- обязательным `usage/rule`, подходящим под затронутые project nodes;
- `usage/map` для maps, назначаемых через `map_bindings`;
- `usage/link` для links, создаваемых через `link`.

Project value read gate появляется только если применимый `usage/rule` содержит
`requires_project_value_read=true`. Тогда kernel требует full read project nodes,
которые одновременно затронуты write-операцией и подходят под `applies_to` этого
rule. Если value узла не нужен по Схеме, write все равно требует state
`read_id` этого узла, но не требует value `read_id`.

Если обязательные read ids отсутствуют в `tx.read_ids`, `tx` не применяется и
возвращает `read_required`. В ответе kernel передает готовые MCP tool calls для
недостающих reads: `query` compact/full read для project state preconditions,
`query` full read для project value gates и `usage` full read для usage nodes.
Ответ `read_required` не возвращает `read_id`; единственный способ получить
`read_id` - выполнить указанный read.

Пример ошибки:

```json
{
  "code": "read_required",
  "details": {
    "required": [
      {
        "reason": "project_state_precondition",
        "graph": "project",
        "read_scope": "state",
        "node": {
          "id": 42,
          "path": "DocsWalker-LLM JSON API/write-ops"
        },
        "name": "query",
        "arguments": {
          "ops": [
            {
              "select": {
                "select": {
                  "ids": [42]
                },
                "include": ["map_bindings"],
                "max_tokens": 1000
              }
            }
          ]
        }
      },
      {
        "reason": "usage_rule",
        "graph": "usage",
        "read_scope": "value",
        "name": "usage",
        "arguments": {
          "ops": [
            {
              "select": {
                "select": {
                  "path": "rules/api/write"
                },
                "include": ["value", "links", "map_bindings"],
                "max_tokens": 4000
              }
            }
          ]
        }
      }
    ]
  }
}
```

Full read rule node возвращает `read_id`:

```json
{
  "id": 17,
  "path": "rules/api/write",
  "map_bindings": {
    "content": "usage/rule"
  },
  "read_id": "read_rule_api_write_9AA1020B",
  "value": "..."
}
```

Full read map node возвращает `read_id`. Для этого LLM читает map node обычным
`usage select`:

```json
{
  "ops": [
    {
      "select": {
        "select": {
          "map_bindings": {
            "content": "usage/map",
            "map_name": "content"
          }
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Пример ответа:

```json
{
  "id": 31,
  "path": "maps/content",
  "map_bindings": {
    "content": "usage/map",
    "map_name": "content"
  },
  "read_id": "read_map_content_31BF23A0",
  "value": {
    "description": "Классифицирует назначение узла по типу содержимого. Используется для выбора node contract и базовых workflow по содержимому.",
    "branches": {
      "documents": {
        "project": {},
        "spec": {}
      },
      "examples": {
        "csharp": {}
      }
    },
    "required": true
  }
}
```

Compact-ответ для `usage/map` не возвращает `read_id`. Чтобы получить
`read_id`, `usage select` должен явно включать `value`.

Full read link node возвращает `read_id` тем же механизмом:

```json
{
  "id": 32,
  "path": "links/depends_on",
  "map_bindings": {
    "content": "usage/link",
    "link_name": "depends_on"
  },
  "read_id": "read_link_depends_on_7F3A91C2",
  "value": {
    "description": "Показывает, что source-узел зависит от target-узла. Используется для актуальных рабочих зависимостей, а не для исторических или похожих по теме связей.",
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

LLM должна выполнить указанные MCP tool calls, применить инструкции, сверить
актуальное состояние project nodes и повторить или перестроить `tx`, передав
прочитанные ids в `read_ids`.

```json
{
  "commit_message": "...",
  "read_ids": [
    "read_node_42_state_31BF23A0",
    "read_rule_api_write_9AA1020B",
    "read_map_content_31BF23A0",
    "read_link_depends_on_7F3A91C2"
  ],
  "ops": []
}
```

Read gate дополняет `expected_count` для `move`, `link`, `unlink`, schema
constraints и финальную validation.

## Examples

Examples являются обычными usage nodes с
`map_bindings.content=usage/example`. Методы, fields, errors и workflows
связываются с examples link `example`.

LLM должна читать examples явно, когда ей нужна точная JSON-форма запроса или
ответа. Examples не включаются в MCP tool descriptions по умолчанию.
