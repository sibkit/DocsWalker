# Ответы И Ошибки

## Envelope

Все методы `query`, `hist`, `usage`, `tx` и `scheme` возвращают компактный
envelope.

Успех:

```json
{
  "result": "ok"
}
```

Если операция возвращает полезную нагрузку, `result` содержит данные этой
операции напрямую. Если в запросе несколько `ops[]`, данные ops-метода являются
массивом в порядке исходных операций. Успешный `tx` возвращает только `tx_id`;
подробности транзакции читаются через `hist`.

Ошибка:

```json
{
  "code": "...",
  "details": {}
}
```

Validation-ошибки внутри `query`, `hist`, `usage`, `tx` и `scheme`
поднимаются в общий envelope ошибки. Ошибки `rollback` operation возвращаются как
ошибки `tx`.

## Atomic Tx

`tx` является атомарным. Если любая операция из `ops[]` не проходит resolve,
schema constraints или финальную validation-проверку графа, весь запрос
возвращает envelope ошибки.

## Коды Ошибок

`not_found` означает, что `id`, `ids` или selector slot не нашли узлы там, где
требовалась одиночная цель или непустой набор.

`ambiguous_selector` означает, что одиночная операция получила больше одного
узла из selector slot.

`count_mismatch` означает, что массовый `move` нашел число целей, отличающееся
от `expected_count`, либо число link-пар в `link`/`unlink` отличается от
`expected_count`.

`already_exists` означает, что `create`, `update.set.title` или
`move.to.parent` пытается получить `path`, который уже занят существующим
sibling, либо получить sibling `title`, lower-case форма которого совпадает с
существующим sibling. Для `link` этот код означает попытку создать link с
tuple `(name, source_id, target_id)`, который уже существует.

`unknown_map` означает, что указанная map или указанная в `map_bindings` ветка
не существует.

`path_parent_not_found` означает, что parent для `create` или `move.to.parent`
не существует.

`ambiguous_path_base` означает, что запрос смешал `defaults.path_parent` с
полным `path` внутри `create`, `move.to.parent` или selector slot. При
`defaults.path_parent` все такие `path` должны быть относительными.

`invalid_title` означает, что последний сегмент `create.path`, `move.to` при
переименовании либо `update.set.title` не соответствует regex
`^[\p{L}\p{Nd}._-]+$` и поэтому не может быть использован как `title`.

`invalid_json` означает, что запрос LLM JSON API не может быть разобран до
аргументов метода.

`invalid_request` означает, что JSON разобран, но нарушает форму LLM JSON API:
например, содержит неизвестное поле selector object или недопустимое сочетание
полей.

`missing_required_field` означает, что в запросе отсутствует обязательное поле;
`details.path` указывает путь к нему.

`validation_failed` означает, что `tx` дошел до финальной validation-проверки
графа или write-layer validation и был отклонен.

`read_required` означает, что `tx` требует read ids, которых нет в
`tx.read_ids`: например, state read ids изменяемых project nodes, обязательные
usage rules, map instruction nodes для назначаемых `map_bindings`, link
instruction nodes для создаваемых links или project value reads, требуемые rule
с `requires_project_value_read=true`. `details.required[]` содержит готовые
requests для недостающих reads. Для project state preconditions это compact или
full `query`, для project value gates - full `query`, для usage instructions -
full `usage`. Ошибка не возвращает `read_id`; получить его можно только
выполнив указанный read.

`invalid_read_id` означает, что один из `tx.read_ids` был собран клиентом
вручную, устарел относительно текущей версии graph/Schema или не соответствует
read gate, для которого используется. Value gate не принимает state `read_id`;
state precondition принимает state или value `read_id` той же версии узла. Если
kernel может связать устаревший или неподходящий read id с конкретным node/gate,
`details.required[]` содержит готовый request для чтения текущей версии:
`query` для project state/value gate или `usage` для usage instruction node.
LLM должна перечитать узел и переоценить write, а не автоматически повторять
старый `tx`.

`unknown_alias` означает, что selector slot ссылается на alias, который не был
объявлен предыдущей операцией `ops[]` в текущем запросе.

`invalid_match_regex` означает, что `selector.match.regex` пустой или содержит
некорректное regex-выражение.

`match_timeout` означает, что regex-фильтр `selector.match` превысил bounded
timeout. LLM должна сузить `path` или `map_bindings` либо упростить regex.

`invalid_match_fields` означает, что `selector.match.fields` содержит значение
вне допустимого набора `title`, `value`.

`invalid_max_tokens` означает, что read `select` operation получил
`max_tokens` меньше или равный нулю. LLM должна передать положительный token
budget или убрать поле.

`invalid_commit_message` означает, что обязательный `commit_message` отсутствует,
пустой или превышает 100 токенов.

`hist_read_only` означает, что запрос пытается изменить hist graph. История
создается только DocsWalker автоматически.

`hist_write_failed` означает, что изменения project graph-а были готовы к
применению, но DocsWalker не смог атомарно записать соответствующие hist nodes
и links. Write-транзакция не считается успешно завершенной.

`rollback_not_found` означает, что `rollback.tx_id` не найден в hist graph.

`rollback_conflict` означает, что `rollback` operation не может быть применена к
project graph-у без затрагивания последующих изменений.
`details.conflicts[]` содержит конфликтующие ресурсы, reason и
`blocking_transactions` с компактными summaries блокирующих транзакций.

`rollback_failed` означает, что rollback-операция не завершилась успешно.
