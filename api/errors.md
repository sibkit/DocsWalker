# Коды ошибок

Все ошибки возвращаются единым envelope:

```json
{
  "code": "...",
  "details": {}
}
```

`code` — машинная строка; `details` зависит от кода, но всегда содержит
либо `path` (JSON-pointer-подобный путь к месту ошибки в запросе), либо
тематические поля.

Ниже — полный список кодов, сгруппированных по областям.

## Разбор запроса

### `invalid_json`

JSON-payload не разбирается до аргументов метода.

```json
{ "code": "invalid_json", "details": { "path": "$" } }
```

### `invalid_request`

JSON разобран, но нарушает форму API (например, неизвестное поле
селектора, неверный тип значения).

### `missing_required_field`

В запросе отсутствует обязательное поле; `details.path` указывает на
него.

### `invalid_op`

Элемент `ops[]` не объект, либо у объекта нет ровно одного ключа op.

### `unknown_op`

Имя op не из допустимого набора для текущего метода (`select` для
`read`; `select`/`create`/`update`/`move`/`delete`/`link`/`unlink`/`rollback`
для `tx`).

### `unknown_scope`

`scope` не из допустимого набора (`main`/`usage`/`hist`/`scheme` для
`read`; `main`/`usage`/`scheme` для `tx`).

### `hist_read_only`

`tx` указал `scope=hist`. История пишется только kernel-ом.

### `invalid_commit_message`

`commit_message` отсутствует, пустой или превышает 100 токенов.

### `invalid_max_tokens`

`select.max_tokens` ≤ 0.

### `invalid_match_regex`

`selector.match.regex` пустой или некомпилируемый regex.

### `invalid_match_fields`

`selector.match.fields` содержит значение вне допустимого набора
(`title`, `value`).

### `match_timeout`

Regex-фильтр `selector.match` превысил bounded timeout. Подсказка:
сузить `path` / `map_bindings` или упростить regex.

### `unknown_alias`

Селектор ссылается на alias, не объявленный предыдущей `select`-op.

### `ambiguous_path_base`

`defaults.path_parent` задан, но `path` внутри `create`, `move.to` или
selector slot — абсолютный. Должен быть относительный.

### `invalid_title`

Последний сегмент `create.path`, `move.to.parent_path` при переносе
либо `update.set.title` не соответствует regex `^[\p{L}\p{Nd}._-]+$`.

## Resolve и selectors

### `not_found`

`id`, `ids` или selector slot не нашли узлы там, где требовалась
одиночная цель или непустой набор.

### `ambiguous_selector`

Одиночная операция получила больше одного узла из selector slot.

### `count_mismatch`

Bulk-операция (`move`, `delete`, `link`, `unlink`) нашла число
затронутых узлов / links, отличающееся от `expected_count`.
`details.expected_count` и `details.actual_count` присутствуют.

### `path_parent_not_found`

Parent для `create` или `move.to.parent_path` не существует.

### `already_exists`

`create`, `update.set.title` или `move.to.parent_path` пытается получить
`path`, который уже занят существующим sibling, либо `title`, lower-case
форма которого совпадает с существующим sibling. Для `link` — попытка
создать link с tuple `(name, source.id, target.id, target.scope)`,
который уже существует.

### `unknown_map`

Указанная map или указанная ветка не существует в схеме target scope.

### `unknown_link`

Указанный link `name` не существует в схеме target scope.

## Cross-scope

### `cross_scope_not_allowed`

`link` пытается создать связь в запрещённом направлении (например,
`main → usage`). См. матрицу в [model.md](model.md).

### `delete_blocked_by_cross_scope_link`

`delete` main-узла, на который есть incoming `usage → main` link.
`details.blocking_links[]` перечисляет блокирующие links и usage-узлы.

## Read gates

### `read_required`

Tx требует `read_id`-ов, которых нет в `tx.read_ids`. `details.required[]`
содержит готовые `read`-вызовы для добора. См. [read-gates.md](read-gates.md).

### `invalid_read_id`

Один из `read_id` в `tx.read_ids` устарел, не выпускался kernel-ом, не
подходит read_scope-у или не соответствует текущей Схеме.
`details.reason` ∈ {`unknown`, `stale`, `scope_mismatch`,
`schema_mismatch`}. Если kernel может связать id с конкретным узлом /
gate, `details.required` содержит готовый `read`-вызов.

## Schema

### `validation_failed`

Tx прошла resolve и read gates, но финальная validation графа
отклонила результат. `details.errors[]` — массив объектов
`{code, node.id, path, ref}` с подробностями каждого нарушения.

### `schema_breaks_existing_data`

`tx scope=scheme` ввёл бы breaking change для существующих узлов main /
usage. `details.violations[]` перечисляет нарушающие узлы.

## Rollback

### `rollback_not_found`

`rollback.tx_id` не найден в hist.

### `rollback_conflict`

`rollback` не может быть применён без затрагивания последующих
изменений. `details.conflicts[]` содержит конфликтующие ресурсы, причину
(`reason`) и `blocking_transactions[]` с компактными summary блокирующих
tx.

### `rollback_failed`

Rollback-операция начата, но не завершилась успешно. Состояние не
изменено (атомарность tx).

### `rollback_already_done`

В hist уже существует tx с `rollback_of.tx_id`, равным указанному.

## Hist write

### `hist_write_failed`

Изменения готовы к применению, но запись соответствующих hist-узлов
не удалась. Tx считается не применённой; состояние main / usage /
scheme не изменилось.

## Method dispatch

### `unknown_method`

`params.name` в MCP `tools/call` не из допустимого набора (`read`,
`tx`).
