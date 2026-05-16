# Read Gates

Read gate — server-side проверка перед `tx`, гарантирующая, что LLM
прочитала актуальное состояние затронутых узлов и обязательные
инструкции из usage. Цель: защита от lost updates (stale overwrite) и
от записи без понимания применимых правил.

Read gates применяются по матрице ниже в зависимости от `tx.scope`. LLM
передаёт opaque `read_id`-ы, полученные из `read`, через `tx.read_ids`.
Если чего-то не хватает, kernel возвращает `read_required` с готовыми
`read`-вызовами для добора недостающего; если `read_id` устарел или не
подходит — `invalid_read_id`.

## Матрица gates

| target scope | state precondition | usage rule | usage map | usage link | project value gate |
|--------------|--------------------|-----------|-----------|------------|-------------------|
| `main`       | да                 | да        | да        | да         | по rule           |
| `usage`      | да                 | нет       | нет       | нет        | нет               |
| `scheme`     | да                 | нет       | нет       | нет        | нет (плюс breaking-change-check, см. [scheme-scope.md](scheme-scope.md)) |

Rule / map / link gates действуют только при `tx scope=main`. Для usage и
scheme достаточно state preconditions: rule-gates на этих scope создали
бы рекурсию (rule про rules) без пользы, а scheme дополнительно защищён
breaking-change-check'ом.

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
node/version). Если узел изменился после чтения, его актуальный
`read_id` уже другой и tx отклоняется.

Узел, созданный в этой же tx и переданный в последующие ops через
alias, не требует state `read_id` (до этой tx его не существовало).

## Usage rule gate (только tx scope=main)

Для каждого main-узла, который `tx` создаёт, изменяет, удаляет или
структурно затрагивает, kernel находит **применимые** rule-узлы из
usage scope (через `applies_to`-селектор в value rule, см.
[usage-scope.md](usage-scope.md)). Для каждого применимого rule LLM
обязана передать value `read_id` этого rule-узла в `tx.read_ids`.

Применимость считается атомарно для всей tx: если в одном tx
изменяются main-узлы из разных под-областей, набор применимых rules —
объединение.

## Usage map gate (только tx scope=main)

При каждой operation, назначающей `map_bindings` (`create`, `move` с
`to.map_bindings`), для каждой назначаемой map LLM обязана передать
value `read_id` соответствующего `usage/map`-узла (`map_bindings.content=
usage/map`, `map_name=<map>`).

Map gate не действует при `tx scope=usage` или `tx scope=scheme`.

## Usage link gate (только tx scope=main)

При операции `link` для каждого создаваемого link с именем `name` LLM
обязана передать value `read_id` соответствующего `usage/link`-узла
(`map_bindings.content=usage/link`, `link_name=<name>`).

Link gate не действует при `tx scope=usage` или `tx scope=scheme`.

## Project value gate (только tx scope=main, по rule)

Если применимый `usage/rule` содержит в value
`requires_project_value_read=true`, для каждого main-узла, который
одновременно затронут tx и попадает под `applies_to` этого rule, LLM
обязана передать **value** `read_id` этого main-узла. Чтобы получить
value `read_id`, LLM делает full read через `read scope=main` с
`include=["value"]` и без truncation.

Если rule не содержит `requires_project_value_read=true`, тот же узел
по-прежнему требует state `read_id` (state precondition), но
value `read_id` не нужен.

## `read_required` envelope

Если `tx` обнаруживает недостаток `read_id`-ов, она ничего не применяет
и возвращает:

```json
{
  "code": "read_required",
  "details": {
    "required": [
      {
        "reason": "state_precondition",
        "scope": "main",
        "read_scope": "state",
        "node": {
          "id": 42,
          "path": "DocsWalker/api/write-ops"
        },
        "name": "read",
        "arguments": {
          "scope": "main",
          "ops": [
            {
              "select": {
                "selector": { "node.id": 42 },
                "include": ["map_bindings"]
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
                "selector": { "node.path": "rules/api/write" },
                "include": ["value", "links", "map_bindings"]
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
                  "node.map_bindings": {
                    "content": "usage/map",
                    "map_name": "content"
                  }
                },
                "include": ["value", "map_bindings"]
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
- `reason` ∈ {`state_precondition`, `value_gate`, `usage_rule`,
  `usage_map`, `usage_link`}.
- `read_scope` ∈ {`state`, `value`}.
- `name`, `arguments` — точная форма MCP-tool call, который LLM должна
  выполнить, чтобы получить нужный `read_id`.

`read_required` не возвращает `read_id`. Единственный способ получить
`read_id` — выполнить указанный `read`.

После выполнения всех reads LLM повторяет `tx` с заполненным
`tx.read_ids`. Если за время между read и tx что-то изменилось, kernel
вернёт новый `read_required` с актуальным списком (или `invalid_read_id`,
если переданный id устарел).

## `invalid_read_id`

Если хотя бы один `read_id` в `tx.read_ids`:

- собран клиентом руками или не выпускался kernel-ом — `invalid_read_id`
  с `details.reason=unknown`;
- устарел (узел изменился после чтения) — `invalid_read_id` с
  `details.reason=stale`;
- не подходит read_scope-у (например, передан state `read_id` там, где
  требуется value) — `invalid_read_id` с `details.reason=scope_mismatch`;
- не соответствует текущей Схеме (например, схема map изменилась после
  чтения) — `invalid_read_id` с `details.reason=schema_mismatch`.

Если kernel может связать устаревший id с конкретным узлом / gate,
`details.required` содержит готовый `read`-вызов для чтения текущей
версии. LLM выполняет, переоценивает изменение на новом состоянии и
отправляет новую `tx` (не повторяет старую с переданным новым id —
данные могли существенно отличаться).

## Полный пример workflow

1. LLM вызывает `read scope=main` для нужного куска main:
   ```json
   {
     "scope": "main",
     "ops": [
       { "select": { "selector": { "node.id": 42 },
         "include": ["value", "map_bindings"] } }
     ]
   }
   ```
   получает узел 42 с value `read_id`.
2. LLM вызывает `read scope=usage` для рул, относящихся к этому узлу:
   ```json
   {
     "scope": "usage",
     "ops": [
       { "select": { "selector": { "node.path": "rules/api/write" },
         "include": ["value", "links", "map_bindings"] } }
     ]
   }
   ```
   получает rule с value `read_id`.
3. LLM вызывает `read scope=usage` для map / link, которые tx будет
   назначать или создавать; получает value `read_id`-ы.
4. LLM вызывает `tx`:
   ```json
   {
     "scope": "main",
     "commit_message": "...",
     "read_ids": ["read_value_node42_...", "read_value_rule_...", "..."],
     "ops": [ { "update": { "id": 42, "set": { "value": "..." } } } ]
   }
   ```
5. Если kernel считает, что чего-то не хватает, возвращает
   `read_required` — LLM выполняет указанные `read`-ы, дополняет
   `tx.read_ids` и повторяет шаг 4.
