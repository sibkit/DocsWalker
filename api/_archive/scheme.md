# Схема JSON API

`scheme` является read-only методом LLM JSON API для чтения и точечного описания
Схемы DocsWalker через JSON `ops[]`. Результат нужен LLM перед созданием или
обновлением узлов, когда требуется уточнить maps, link rules и constraints.

Схема - компактный документ, который человек настраивает один раз, а LLM читает
через `scheme`. Kernel применяет constraints Схемы при `create`, `update`,
`delete`, `move`, `link`, `unlink`, а затем повторяет финальную validation всего
результата `tx`.

## Schema Constraints

Схема задает:

- maps: имена карт классификации, обязательное описание, допустимые ветки и правила
  обязательности;
- conditional map bindings: обязательность map при совпадении других
  `map_bindings`;
- links: имя link, обязательное описание, source/target constraints,
  cardinality и `required_for`;
- cross constraints: правила link, зависящие от `map_bindings` source- и
  target-узлов.
- read gates: какие usage instructions или project values LLM обязана прочитать
  перед конкретной write-транзакцией.

Условия по `map_bindings` используют тот же wildcard-синтаксис, что
`selector.path`: exact value, `*` для одного сегмента и `**` для любой глубины.

Каждая map описывает отдельную классификационную ось. Внутри map ветки задаются
в `branches` как вложенный JSON object: ключи являются сегментами пути, а
значения являются объектами дочерних веток. Пустой object означает ветку без
дочерних веток. Строковый путь ветки получается соединением сегментов через
`/`.

Узел хранит выбор веток не в самой Схеме, а в своем поле `map_bindings`: ключ
является именем map, значение является строковым путем ветки внутри этой map. В
одном узле может быть не больше одной привязки к одной map.

Пример schema-фрагмента:

```json
{
  "maps": [
    {
      "name": "content",
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
    },
    {
      "name": "projects",
      "description": "Привязывает project document к проекту или продуктовой области, к которой он относится.",
      "branches": {
        "docswalker": {}
      },
      "required_when": {
        "map_bindings": {
          "content": "documents/project"
        }
      }
    }
  ],
  "links": [
    {
      "name": "depends_on",
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
    },
    {
      "name": "example_of",
      "description": "Связывает пример с узлом, который этот пример демонстрирует. Source всегда является примером, target - описываемым API или code node.",
      "source": {
        "map_bindings": {
          "content": "examples/csharp"
        }
      },
      "target": {
        "map_bindings": {
          "content": "code/csharp"
        }
      },
      "cardinality": "many_to_one",
      "required_for": ["source"]
    }
  ]
}
```

`cardinality` задает сколько links допустимо:

- `one_to_one` - один source может указывать на один target, и один target может
  иметь только один такой source.
- `one_to_many` - один source может указывать на много targets, каждый target
  имеет не больше одного такого source.
- `many_to_one` - много sources могут указывать на один target, каждый source
  имеет не больше одного такого target.
- `many_to_many` - много sources могут указывать на много targets.

`required_for` задает, для какой стороны link обязателен:

- `[]` - link не обязателен ни для source, ни для target.
- `["source"]` - каждый узел, подходящий под `source`, обязан иметь такой
  link на подходящий target.
- `["target"]` - каждый узел, подходящий под `target`, обязан иметь такой
  link от подходящего source.
- `["source","target"]` - link обязателен для обеих сторон.

В примере `example_of` каждый `examples/csharp` обязан ссылаться на
`code/csharp`, но `code/csharp` не обязан иметь examples.

`links[].description` обязателен. Он является LLM-facing инструкцией о смысле
связи: когда link уместен, какую сторону считать source, какую target и чем
этот link отличается от похожих связей. Технические `source`/`target`
constraints задают, где связь разрешена, но не заменяют описание семантики.

`maps[].description` обязателен. Он является LLM-facing инструкцией о смысле map:
какую классификационную ось она задает, когда привязка к этой map уместна и как
выбирать ветку внутри `branches`.

## Method Operations

`scheme operation` - операция внутри MCP tool `scheme`.

- `get` возвращает описание Схемы. `include` опционально принимает
  `description`, `maps`, `links` и `constraints`; по умолчанию возвращаются все
  секции. `map_names` и `link_names` опционально ограничивают выдачу. `get` не
  возвращает read ids.

Read ids не выдаются методом `scheme`. Для map/link/rule gates LLM читает
соответствующие instruction nodes обычным методом `usage`; full read такого
узла возвращает value `read_id`, который передается в `tx.read_ids`. Для
project state preconditions LLM читает соответствующие project nodes методом
`query`; compact или full read project node возвращает state `read_id`, который
тоже передается в `tx.read_ids`. Project value read ids нужны только для
schema-defined project value gates: тогда full read project node возвращает
value `read_id`.

Пример:

```json
{
  "ops": [
    {
      "get": {
        "include": ["maps", "links", "constraints"],
        "map_names": ["content", "subject", "projects"],
        "link_names": ["depends_on"]
      }
    }
  ]
}
```

## Required Schema Nodes

Usage graph должен содержать schema nodes для:

- project schema - основная schema project graph, который читает `query`;
- hist schema - schema hist graph, который читает `hist`;
- usage schema - schema этого instruction graph.

Эти schemas читаются обычным `usage select`, например:

```json
{
  "ops": [
    {
      "select": {
        "select": {
          "map_bindings": {
            "content": "usage/schema",
            "schema_name": "hist"
          }
        },
        "include": ["value", "links", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## Hist Schema

Hist graph использует отдельную schema `hist`.

### Maps

Hist schema использует строковые maps:

- `content` - `hist/transaction` или `hist/snapshot`.
- `tx_id` - id write-транзакции.
- `date` - ISO-8601 UTC date успешной транзакции.
- `index` - порядковый номер snapshot внутри транзакции.
- `op` - normalized resource change: `create`, `update`, `delete`, `move`,
  `link`, `unlink`.
- `resource` - `node` или `link`.
- `state` - `present` или `deleted`.

### Node Contracts

Transaction node:

- `map_bindings.content=hist/transaction`.
- Обязательные maps: `tx_id`, `date`.
- `value` обязателен и содержит `commit_message`.

Snapshot node:

- `map_bindings.content=hist/snapshot`.
- Обязательные maps: `tx_id`, `index`, `op`, `resource`, `state`.
- `value` обязателен и содержит compact JSON snapshot.

### Links

Hist schema определяет только links внутри hist graph:

- `has_snapshot`: transaction node -> snapshot node, `one_to_many`,
  `required_for=["source"]` для transactions with at least one project graph
  change.

Hist nodes не создают links на узлы основного project graph. Рабочие node ids,
paths и link tuples хранятся как данные внутри snapshot.

## Usage Schema

`usage` graph использует отдельную schema `usage`.

### Maps

Usage schema использует строковые maps:

- `content` - назначение узла usage.
- `subject` - тема: `query`, `hist`, `usage`, `tx`, `rollback`,
  `selector`, `error`, `schema`, `workflow`, `graph`.
- `method` - имя метода, если узел описывает метод.
- `field` - имя поля, если узел описывает поле.
- `error_code` - код ошибки, если узел описывает ошибку.
- `schema_name` - `project`, `hist` или `usage`, если узел описывает
  schema.
- `map_name` - имя map, если узел описывает map.
- `link_name` - имя link, если узел описывает link.

### Node Contracts

Topic node:

- `map_bindings.content=usage/topic`.
- Объясняет одну тему или workflow.
- `value` содержит инструкцию.

Method node:

- `map_bindings.content=usage/method`.
- Описывает метод API/MCP.
- `map_bindings.method` обязателен.
- `value` содержит назначение, входные поля и результат.

Field node:

- `map_bindings.content=usage/field`.
- Описывает поле request/response или selector.
- `map_bindings.field` обязателен.

Error node:

- `map_bindings.content=usage/error`.
- Описывает код ошибки и действие LLM.
- `map_bindings.error_code` обязателен.

Schema node:

- `map_bindings.content=usage/schema`.
- Описывает одну schema: project, hist или usage.
- `map_bindings.schema_name` обязателен.

Map node:

- `map_bindings.content=usage/map`.
- Описывает одну map из Схемы.
- `map_bindings.map_name` обязателен.
- `value` содержит description map, branches, required и required_when.

Link node:

- `map_bindings.content=usage/link`.
- Описывает один link из Схемы.
- `map_bindings.link_name` обязателен.
- `value` содержит description link, source/target constraints, cardinality и
  required_for.

Example node:

- `map_bindings.content=usage/example`.
- Содержит компактный JSON example.

Rule node:

- `map_bindings.content=usage/rule`.
- Описывает обязательную инструкцию, которую LLM должна прочитать перед
  подходящей write-транзакцией.
- `map_bindings.subject` задает тему rule.
- `map_bindings.method` задает метод, обычно `tx`.
- `value` содержит инструкцию для LLM. Если rule применяется только к части
  project graph, `value` начинается с compact JSON header с project selector.

Форма JSON header для rule:

```json
{
  "applies_to": {
    "path": "api/**",
    "map_bindings": {
      "subject": "api/**"
    },
    "links": {
      "name": "depends_on",
      "to": {
        "map_bindings": {
          "content": "api/**"
        }
      }
    }
  },
  "requires_project_value_read": false
}
```

`applies_to` использует обычный project selector. Он не выбирает usage nodes и
не создает links между usage graph и project graph. Он задает условие: если
`tx` затрагивает project nodes, подходящие под этот selector, перед записью
LLM обязана прочитать этот rule через `usage`.

`requires_project_value_read` опционален и по умолчанию равен `false`. Если он
равен `true`, этот rule дополнительно требует full read project nodes, которые
одновременно затронуты write-операцией и подходят под `applies_to`; полученные
project `read_id` передаются в `tx.read_ids`. Если поле отсутствует или равно
`false`, сам факт изменения подходящего project node не требует project value
`read_id`; state `read_id` для изменяемого project node все равно нужен как
write precondition.

Full read usage nodes возвращает value `read_id`, если `include`
содержит `value` и node не был truncated. Compact-ответ для usage nodes не
возвращает `read_id`.

### Links

`usage` schema определяет links только внутри usage graph:

- `explains`: topic/method/field/error/schema/map/link/rule ->
  topic/method/field/error/schema/map/link/rule.
- `example`: topic/method/field/error/schema/map/link/rule -> example.
- `schema_of`: schema -> topic/method/graph/map/link, где эта schema
  применяется.
- `related`: any -> any для навигации по близким темам.
- `requires`: topic/method/workflow -> topic/method/field/schema, которые надо
  понять перед применением.

Rule, map и link nodes могут быть связаны с examples, fields, methods и schemas
теми же links, что остальные usage nodes.
