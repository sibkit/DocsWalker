# Транзакции И Rollback

## `tx_id`

`tx_id` - opaque string успешной write-транзакции. Любой успешный `tx`
возвращает только новый `tx_id`, включая `tx` с `rollback` operation. Остальные
сведения о транзакции LLM читает через `hist`, если они нужны.

## `commit_message`

`commit_message` - краткое описание write-транзакции для hist log. Поле
обязательно для `tx` и не должно превышать 100 токенов.

LLM передает `commit_message` в write-запросе, но не пишет hist напрямую.
DocsWalker автоматически записывает `commit_message` в `value` transaction node
hist graph-а.

## Единый Hist Log

В hist graph попадают только успешно примененные write-транзакции. Успешный
rollback записывается в тот же hist graph как обычная transaction с собственным
`tx_id`, `commit_message` и snapshot nodes фактически примененного изменения.

DocsWalker пишет hist автоматически после успешного применения изменений
project graph-а. Успешная запись считается завершенной только после записи
соответствующих hist nodes и links. Если hist append не завершился успешно,
write-транзакция не считается успешно примененной.

## Rollback Operation

`rollback` operation внутри MCP tool `tx` откатывает указанную транзакцию по `tx_id`.
Rollback является обычной write-операцией: он меняет project graph, проходит
state preconditions, schema-defined read gates, validation, пишет hist и
возвращает новый `tx_id` как результат общего `tx`. Если read ids для
изменяемых project nodes или применимых gates отсутствуют, `tx` возвращает
`read_required` с готовыми read requests; если переданные read ids устарели,
имеют недостаточный scope или не соответствуют gate, возвращается
`invalid_read_id`.

Пример:

```json
{
  "commit_message": "откатить изменение API",
  "ops": [
    {
      "rollback": {
        "tx_id": "tx_20260514T101530123Z_7F3A91C2"
      }
    }
  ]
}
```

Успешный ответ:

```json
{
  "result": {
    "tx_id": "tx_rollback_20260514T101531012Z_22B7CA11"
  }
}
```

## Rollback Conflicts

Если последующие изменения мешают откату исходной транзакции, возвращается
`rollback_conflict` и ничего не меняется.

`rollback_conflict.details.conflicts[]` перечисляет конфликтующие ресурсы и
компактные summaries транзакций, которые изменили эти ресурсы после
откатываемой транзакции.

Пример:

```json
{
  "code": "rollback_conflict",
  "details": {
    "tx_id": "tx_1",
    "conflicts": [
      {
        "resource": "link",
        "link": "depends_on",
        "source_id": 42,
        "target_id": 77,
        "reason": "source_node_deleted",
        "blocking_transactions": [
          {
            "tx_id": "tx_2",
            "commit_message": "удалить устаревший узел"
          }
        ]
      }
    ]
  }
}
```

LLM не должна автоматически откатывать `blocking_transactions`. Она использует
эти summaries, затем решает: сделать новую компенсирующую `tx`, откатить
мешающую транзакцию через `rollback` operation или сообщить пользователю о конфликте.
