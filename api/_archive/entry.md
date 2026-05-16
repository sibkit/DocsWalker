# MCP Entry

## Вход LLM в API

MCP tool descriptions содержат короткий quickstart и карту LLM-facing API.
Отдельного bootstrap tool нет: подробные инструкции, examples и schemas LLM
читает через метод `usage`.

Минимальный quickstart в MCP descriptions:

1. `usage` - прочитать нужные инструкции, examples и schemas.
2. `query` - прочитать project-контекст.
3. `tx.read_ids` - передать state read ids изменяемых project nodes и read ids
   обязательных read gates; если ids не хватает, выполнить готовые requests из
   `read_required`.
4. `usage` - перед назначением `map_bindings` full-read map node с описанием и
   получить `read_id`.
5. `usage` - перед созданием link full-read link node с описанием и получить
   `read_id`.
6. `tx` - записать изменения с `commit_message` и `expected_count` для
   массовых `move`, `link` и `unlink`.
7. `tx` с `rollback` operation - откатить транзакцию по `tx_id`.
8. `hist` - прочитать историю и подробности транзакций.
9. `scheme` - уточнить maps, links и constraints.

## `usage`

`usage` - read-only метод для чтения подробной инструкции. Он работает так же,
как `query`, но читает usage graph.
