# Read Gates

Read gate — server-side проверка перед `tx`, гарантирующая, что LLM
прочитала актуальное состояние затронутых узлов и обязательные
инструкции из usage. Цель: защита от lost updates (stale overwrite) и
от записи без подтверждения применимых правил.

LLM передаёт opaque `read_id`-ы, полученные из `read`, через
`tx.read_ids`. Если чего-то не хватает, kernel возвращает `read_required`
с готовыми `read`-вызовами для добора недостающего; устаревший или
неподходящий `read_id` — `invalid_read_id`.

## Матрица gates

| target scope | state precondition | usage rule | usage map | usage link | project value gate |
|--------------|--------------------|-----------|-----------|------------|-------------------|
| `main`       | да                 | да        | да        | да         | по rule           |
| `usage`      | да                 | нет       | нет       | нет        | нет               |
| `scheme`     | да                 | нет       | нет       | нет        | нет (плюс breaking-change-check, см. [scheme-scope.md](scheme-scope.md)) |

Rule / map / link gates действуют при `tx scope=main`. Для usage и
scheme применяются только state preconditions.

## State precondition

При `tx`, изменяющей существующий узел, в `tx.read_ids` должен быть
актуальный state или value `read_id` этого узла. Применимость:

- `update` — целевой узел `update.id` и path-потомки, если `set.title`
  меняет их `path`.
- `delete` — каждый удаляемый узел.
- `move` — каждый переносимый узел и каждый path-потомок, чей `path`
  меняется из-за переноса.
- `link` / `unlink` — существующие source / target endpoint узлы
  затронутой link-пары.

State `read_id` принимается любым из state или value scope (value
`read_id` той же версии узла удовлетворяет state precondition того же
node/version).

Узел, созданный в этой же tx и переданный в последующие ops через
alias, освобождён от state `read_id` — до этой tx его не существовало.

## Usage rule gate (tx scope=main)

Для каждого main-узла, который `tx` создаёт, изменяет, удаляет или
структурно затрагивает, kernel находит применимые rule-узлы из usage
scope (через `applies_to`-селектор в value rule, см.
[usage-scope.md](usage-scope.md)). Для каждого применимого rule LLM
передаёт value `read_id` этого rule-узла в `tx.read_ids`.

Применимость считается атомарно для всей tx: при изменении main-узлов
из разных под-областей набор применимых rules — объединение.

## Usage map gate (tx scope=main)

При operation, назначающей ветку map (`create.set.map_bindings.<map>=<branch>`,
либо `move.to.map_bindings.<map>=<branch>` с не-`null` значением), для
каждой назначаемой map LLM передаёт value `read_id` соответствующего
`usage/map`-узла (`map_bindings.content=usage/map` и
`map_bindings.map=<map>`).

Снятие привязки (`move.to.map_bindings.<map>=null`) gate не требует —
никакой новой классификации не вводится.

## Usage link gate (tx scope=main)

При operation `link` для каждого создаваемого link с именем `name`
LLM передаёт value `read_id` соответствующего `usage/link`-узла
(`map_bindings.content=usage/link`, `link_name=<name>`).

## Project value gate (tx scope=main, по rule)

Если применимый `usage/rule` содержит в value
`requires_project_value_read=true`, для каждого main-узла, одновременно
затронутого tx и попадающего под `applies_to` этого rule, LLM передаёт
value `read_id` этого main-узла. Чтобы получить value `read_id`, LLM
делает full read через `read` с `include=["value"]` и без truncation.

Если rule не содержит `requires_project_value_read=true`, узел требует
только state `read_id`.

## `read_required` envelope

При недостатке `read_id`-ов `tx` ничего не применяет и возвращает:

```json
{
  "code": "read_required",
  "details": {
    "required": [
      {
        "reason": "state_precondition",
        "read_scope": "state",
        "node": {
          "id": "2a",
          "path": "DocsWalker/api/write-ops"
        },
        "name": "read",
        "arguments": {
          "ops": [
            {
              "select": {
                "selector": { "id": "2a" }
              }
            }
          ]
        }
      },
      {
        "reason": "usage_rule",
        "scope": "usage",
        "read_scope": "value",
        "node": {
          "path": "rules/api/write"
        },
        "name": "read",
        "arguments": {
          "scope": "usage",
          "ops": [
            {
              "select": {
                "selector": { "path": "rules/api/write" },
                "include": ["value", "links"]
              }
            }
          ]
        }
      },
      {
        "reason": "usage_map",
        "scope": "usage",
        "read_scope": "value",
        "node": {
          "path": "maps/content"
        },
        "name": "read",
        "arguments": {
          "scope": "usage",
          "ops": [
            {
              "select": {
                "selector": {
                  "map_bindings": {
                    "content": "usage/map",
                    "map": "content"
                  }
                },
                "include": ["value"]
              }
            }
          ]
        }
      }
    ]
  }
}
```

- `required[]` — список missing reads, каждый с готовым `read`-вызовом.
- `reason` ∈ {`state_precondition`, `project_value`, `usage_rule`,
  `usage_map`, `usage_link`}.
- `read_scope` ∈ {`state`, `value`}.
- `name`, `arguments` — точная форма MCP-tool call для получения
  нужного `read_id`.

`read_required` не возвращает `read_id`. Получение `read_id` — через
выполнение указанного `read`-а.

После выполнения reads LLM повторяет `tx` с заполненным `tx.read_ids`.
При изменении состояния между read и tx kernel вернёт новый
`read_required` с актуальным списком, либо `invalid_read_id` для
переданных устаревших id.

## `invalid_read_id`

`details.reason`:

- `unknown` — `read_id` не выпускался kernel-ом или собран клиентом
  руками;
- `stale` — узел изменился после чтения;
- `scope_mismatch` — передан state `read_id` там, где требуется value;
- `schema_mismatch` — схема map / link изменилась после чтения.

При связке устаревшего id с конкретным узлом / gate `details.required`
содержит готовый `read`-вызов для чтения текущей версии. LLM
перечитывает узел, переоценивает изменение на новом состоянии и
отправляет новую `tx`.

## Полный пример workflow

1. `read` для нужного main-узла:
   ```json
   {
     "ops": [
       { "select": { "selector": { "id": "2a" },
         "include": ["value"] } }
     ]
   }
   ```
   получает узел `"2a"` с value `read_id`.
2. `read scope=usage` для применимого rule:
   ```json
   {
     "scope": "usage",
     "ops": [
       { "select": { "selector": { "path": "rules/api/write" },
         "include": ["value", "links"] } }
     ]
   }
   ```
   получает rule с value `read_id`.
3. `read scope=usage` для map / link, назначаемых или создаваемых tx;
   получает value `read_id`-ы.
4. `tx`:
   ```json
   {
     "title": "...",
     "read_ids": ["4c8f", "31bf", "7bc0"],
     "ops": [ { "update": { "id": "2a", "set": { "value": "..." } } } ]
   }
   ```
5. При недостающих `read_id` kernel возвращает `read_required` — LLM
   выполняет указанные `read`-ы, дополняет `tx.read_ids` и повторяет
   шаг 4.
