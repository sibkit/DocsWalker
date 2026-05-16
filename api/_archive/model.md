# Модель JSON API v1

## Path

`path` - уникальный человекочитаемый адрес узла в DocsWalker v1. По смыслу это
адресная иерархия, но в JSON API он остается отдельным полем: `path` уникален,
задает расположение узла и участвует в проверке path-конфликтов.

В `create.path` он задает полный адрес нового узла или относительный адрес
внутри `defaults.path_parent`. В selector slots `path` может быть точным путем
или pattern с wildcard `*` и `**`.

## Title

`title` - последний сегмент `path`. Это не произвольный label, а локальный
заголовок узла внутри parent path. `title` должен целиком соответствовать
regex `^[\p{L}\p{Nd}._-]+$`: разрешены Unicode-буквы любого регистра, Unicode
decimal digits, точка, тире и нижнее подчеркивание.

Оригинальный регистр `title` сохраняется в `title` и `path`, но уникальность
siblings проверяется по lower-case форме `title`: внутри одного parent path
нельзя иметь два sibling-узла, у которых lower-case `title` совпадает.

В read-ответах `title` выводится из `path`. В `update.set.title` поле меняет
последний сегмент `path`; kernel пересчитывает `path` самого узла и его
path-потомков.

## Value

`value` - содержимое узла. Для project graph это основной документируемый
контент узла. Для hist и usage graph `value` содержит данные этих graph-ов:
commit message, snapshot, инструкцию, schema или example.

## Map Bindings

`map_bindings` - классификационные привязки узла к картам. Ключ
`map_bindings` задает имя map из Схемы, значение задает ветку внутри этой map.
Узел может иметь не больше одной привязки к одной map и может быть привязан к
нескольким веткам только через разные maps.

```json
{
  "map_bindings": {
    "document_type": "manuals/guide",
    "product_map": "api/v3.5/scheme"
  }
}
```

Все значения `map_bindings` являются строками пути ветки внутри соответствующей
map. Несколько узлов могут иметь одинаковую привязку в одной или нескольких
maps. Обязательность map и допустимые пути веток задаются Схемой; в Схеме
ветки map описаны вложенным JSON object, а не плоским списком строк.

## Links

`links` - именованные node-to-node links. Link всегда имеет `name`,
`source_id` и `target_id`. Имена links, допустимые source/target constraints,
cardinality и обязательность задаются Схемой.

Link не имеет отдельного публичного `id`. Identity link-а - tuple
`(name, source_id, target_id)`. В одном graph-е не может существовать два link-а
с одинаковым tuple.

`path`, `map_bindings` и `links` образуют полный структурный контракт graph data:

- `path` задает адресную иерархию узлов.
- `map_bindings` задают классификационные привязки к maps.
- `links` задают явные node-to-node edges.

## Schema Constraints

Схема задает структурные constraints для maps, map bindings и links. Полный
контракт Схемы, операции метода `scheme`, cardinality, `required_for`, hist
schema и usage schema описаны в [scheme.md](scheme.md).

## Аргументы Метода

Каждый MCP `tools/call` выбирает метод через `params.name`. Внутри
`params.arguments` поле `method` отсутствует. Аргументы метода имеют форму:

```json
{
  "commit_message": "...",
  "read_ids": [],
  "defaults": {},
  "ops": []
}
```

Поле `ops` обязательно и всегда является массивом, даже если операция одна.
Каждый элемент `ops[]` является объектом с ровно одним ключом: имя ключа задает
операцию, а значение ключа содержит тело этой операции.

`commit_message` обязателен для `tx`; значение не должно превышать 100
токенов. `defaults` опционально и применяется к методам с selector/path:
`query`, `hist`, `usage` и `tx`.

`read_ids` опционально для `tx` и содержит opaque read ids, полученные через
read узлов. Kernel использует их как:

- state preconditions для существующих project nodes, которые write меняет,
  удаляет или структурно затрагивает;
- подтверждение, что LLM прочитала нужные instructions или project values,
  когда это требуется Схемой перед write.

Tool `hist` работает с hist graph. Tool `usage` работает с usage graph.
Tool `scheme` работает со Схемой.

## Read Ids

`read_id` - opaque receipt, который kernel возвращает при read узла.
`read_id` привязан к graph, id узла, версии состояния узла/Схемы и scope чтения.
LLM не собирает `read_id` самостоятельно. `read_id` не является permission или
auth token.

Scopes `read_id`:

- `state` - подтверждает актуальную версию состояния узла без обязательного
  чтения `value`. Для project node состояние включает `path`, `title`,
  `map_bindings` и incident links. State `read_id` используется как write
  precondition, чтобы write не перезаписал состояние, которое изменилось после
  чтения.
- `value` - подтверждает, что `value` узла был прочитан целиком, без truncation,
  и фиксирует актуальную версию этого value на момент read. Value `read_id`
  также удовлетворяет state precondition того же node/version.

Compact read project node возвращает state `read_id`. Если `include` содержит
`value` и value возвращен полностью, node response возвращает value `read_id`.
Если value truncated, response может вернуть только state `read_id`; такой id не
подходит для read gates, требующих прочитать `value`.

При каждом успешном изменении project node kernel выпускает для него новый
state `read_id`; старый state/value `read_id` больше не подтверждает актуальное
состояние этого узла. Создание или удаление link-а меняет state `read_id` его
source и target nodes.

Для записи существующих project nodes `tx.read_ids` должен содержать актуальный
state или value `read_id` каждого существующего узла, который операция меняет,
удаляет или структурно затрагивает. Это state precondition, а не требование
читать `value`.

Project state precondition применяется к:

- `update` - целевой узел `update.id` и path-потомки, если `set.title` меняет
  их `path`;
- `delete` - каждый удаляемый узел из `delete.ids`;
- `move` - каждый переносимый узел и каждый path-потомок, чей `path` меняется
  из-за переноса;
- `link` и `unlink` - существующие source/target endpoint nodes создаваемой или
  удаляемой link-пары.

Узел, созданный ранее в том же `tx` и переданный в последующие операции через
alias, не требует state `read_id`, потому что до этой транзакции он не
существовал.

Отдельно `read_ids` используются как read gates для usage rules, назначения
`map_bindings`, создания links и project value reads, если конкретный
`usage/rule` с `requires_project_value_read=true` явно требует прочитать value
перед записью. Один массив `tx.read_ids` содержит оба вида receipts: state
preconditions и schema-defined read gate confirmations.

```json
{
  "id": 31,
  "path": "maps/content",
  "map_bindings": {
    "content": "usage/map",
    "map_name": "content"
  },
  "read_id": "read_31BF23A0",
  "value": {
    "description": "..."
  }
}
```

## Defaults

`defaults` задает общие значения для запроса: `path_parent` и `map_bindings`.

Если операция возвращает resolved-данные, они строятся уже с учетом
`defaults`.

Если `defaults.path_parent` задан, `path` внутри `create`, `move.to.parent` и
selector slots обязан быть относительным путем внутри этого parent. Полный
`path` вместе с `defaults.path_parent` является ошибкой `ambiguous_path_base`.

При пустом `defaults.path_parent` `path` внутри `create`, `move.to.parent` и
selector slots обязан быть полным `path`.

`defaults.map_bindings` применяется к `create.set.map_bindings`. Для
`move.to.map_bindings` привязки задаются явно, чтобы массовая переклассификация
не происходила из-за общего default-а.

Пример:

```json
{
  "commit_message": "добавить описание create",
  "read_ids": [
    "read_content_31BF23A0",
    "read_subject_A22140CE",
    "read_subsystem_83F1046B",
    "read_audience_7BC0E11D"
  ],
  "defaults": {
    "path_parent": "DocsWalker-LLM JSON API/Операции записи",
    "map_bindings": {
      "content": "api/rule",
      "subject": "api/write",
      "subsystem": "kernel",
      "audience": "llm-agent"
    }
  },
  "ops": [
    {
      "create": {
        "path": "create",
        "set": {
          "value": "..."
        }
      }
    }
  ]
}
```

Resolved path: `DocsWalker-LLM JSON API/Операции записи/create`.
