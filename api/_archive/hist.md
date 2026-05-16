# Hist Graph

Hist graph - отдельный read-only graph для истории изменений project-а.

Адрес:

```
localhost/{project}/hist
```

Данные hist graph не смешиваются с данными основного project graph. История
читается методом `hist`, который работает так же, как `query`, но всегда
использует hist graph и hist schema. LLM не может создавать, менять, удалять или
откатывать hist nodes: история формируется только DocsWalker автоматически после
успешных write-транзакций.

Метод `hist` принимает `select` operations и возвращает nodes из hist graph.
Форма `arguments` совпадает с `query`:

```json
{
  "ops": [
    {
      "select": {
        "select": {
          "map_bindings": {
            "content": "hist/transaction"
          }
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## Log And Show

Hist работает как graph log.

Для просмотра журнала LLM читает transaction nodes через `hist`. Compact-форма
transaction node дает `path`, `title`, `map_bindings` и tokens; при
`include=["value","map_bindings"]`
LLM получает `commit_message`, `tx_id`, `date` и `read_id`, если value не был
truncated.

Для просмотра подробностей конкретной транзакции LLM явно читает snapshot nodes
этой транзакции. Snapshot nodes показывают, какие ресурсы появились,
изменились или были удалены этой транзакцией, и хранят только состояние после
транзакции.

## Paths

Transaction node создается по path:

```
transactions/{yyyy}/{MM}/{dd}/{tx_id}
```

Snapshot nodes создаются под transaction node:

```
transactions/{yyyy}/{MM}/{dd}/{tx_id}/snapshots/{index}
```

`index` - стабильный порядковый номер snapshot внутри транзакции.

## Hist Schema

Hist graph использует отдельную schema `hist`. Maps, node contracts и links
hist schema описаны в [scheme.md](scheme.md).

## Snapshot Value

`snapshot.value` хранит compact JSON snapshot после транзакции. Snapshot
описывает только затронутый ресурс. Для сравнения состояний LLM читает snapshot
нужной транзакции и предыдущий snapshot того же resource identity через `hist`.

Форма node snapshot для `create`, `update` и `move`:

```json
{
  "resource": "node",
  "op": "update",
  "state": "present",
  "node": {
    "id": 42,
    "path": "DocsWalker/API/selectors",
    "title": "selectors",
    "map_bindings": {
      "subject": "api/selectors"
    },
    "value": "new value"
  }
}
```

Форма node snapshot для `delete`:

```json
{
  "resource": "node",
  "op": "delete",
  "state": "deleted",
  "node": {
    "id": 42
  }
}
```

Форма link snapshot для `link`:

```json
{
  "resource": "link",
  "op": "link",
  "state": "present",
  "link": {
    "name": "depends_on",
    "source_id": 1,
    "target_id": 2
  }
}
```

Форма link snapshot для `unlink`:

```json
{
  "resource": "link",
  "op": "unlink",
  "state": "deleted",
  "link": {
    "name": "depends_on",
    "source_id": 1,
    "target_id": 2
  }
}
```

## Operation Mapping

- `create`: `state=present`, `node` содержит созданный node snapshot.
- `update`: `state=present`, `node` содержит node snapshot после изменения.
- `delete`: `state=deleted`, `node` содержит id удаленного node.
- `move`: `state=present`, `node` содержит node snapshot после переноса или
  изменения map bindings.
- `link`: `state=present`, `link` содержит link tuple.
- `unlink`: `state=deleted`, `link` содержит link tuple.
- rollback transaction содержит обычный `tx_id`, `date`, `commit_message` и
  snapshot nodes фактически примененных resource changes.
