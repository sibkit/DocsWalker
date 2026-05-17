# Коды ошибок

Все ошибки возвращаются единым envelope:

```json
{
  "code": "...",
  "details": {}
}
```

`code` — машинная строка; `details` зависит от кода и содержит либо
`path` (JSON-pointer-подобный путь к месту ошибки в запросе), либо
тематические поля.

Ниже — полный реестр кодов, сгруппированный по областям.

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

Имя op за пределами допустимого набора для текущего метода (`select`
для `read`; `create` / `update` / `move` / `delete` / `link` /
`unlink` / `rollback` для `tx`).

### `unknown_select_mode`

Строковое значение `select` за пределами реестра kernel-режимов (см.
[read.md](read.md), «Форма-строка»). На сегодня допустимо только
`"meta"`.

### `invalid_scope`

Указан `scope="main"` — явное указание main scope в API запрещено. Для
работы с main scope параметр `scope` опускается.

### `unknown_scope`

Значение `scope` за пределами допустимого набора. В `read` допустимы
`"usage"`, `"hist"`, `"scheme"`. В `tx` допустимы `"usage"`, `"scheme"`.

### `hist_read_only`

`tx` указал `scope=hist`. История пишется только kernel-ом.

### `invalid_tx_title`

`tx.title` отсутствует, пустой или превышает 100 токенов.

### `invalid_max_tokens`

`select.max_tokens` ≤ 0.

### `invalid_match_regex`

`selector.match.regex` пустой или некомпилируемый regex.

### `invalid_match_fields`

`selector.match.fields` содержит значение за пределами набора,
допустимого для класса узла:

- data-узел: `title`, `content`.
- event-узел (hist): `title`, `description`, `date`.

### `match_timeout`

Regex-фильтр `selector.match` превысил bounded timeout. Подсказка:
сузить `path` / `map_bindings` / hist-предикат либо упростить regex.

### `unknown_alias`

Селектор ссылается на alias за пределами объявленных в этом запросе.

### `ambiguous_path_base`

`defaults.path_parent` задан, и `path` внутри `create`,
`move.to.parent_path` или selector slot — абсолютный.

### `invalid_node_title`

Последний сегмент `create.path`, `move.to.parent_path` при переносе
либо `update.set.title` за пределами regex `^[\p{L}\p{Nd}._-]+$`.
Применяется только к data-узлам; title event-узлов regex не подчиняется.

### `invalid_map_binding_value`

`create.set.map_bindings.<map>=null`. У нового узла нет существующих
привязок — снимать нечего; `null` допустим только в
`move.to.map_bindings` как tombstone «снять ветку».

## Resolve и selectors

### `not_found`

`id`, `ids` или selector slot вернули пустой результат там, где
требовалась одиночная цель или непустой набор.

### `ambiguous_selector`

Одиночная операция получила больше одного узла из selector slot.

### `count_mismatch`

Bulk-операция (`move`, `delete`, `link`, `unlink`) обнаружила число
затронутых узлов / links, отличающееся от `expected_count`.
`details.expected_count` и `details.actual_count` присутствуют.

### `path_parent_not_found`

Parent для `create` или `move.to.parent_path` отсутствует в графе.

### `already_exists`

`create`, `update.set.title` или `move.to.parent_path` дают `path`,
который уже занят существующим sibling, либо `title`, lower-case форма
которого совпадает с существующим sibling. Для `link` — попытка
создать link с уже существующим tuple `(name, from.id, to.id)`.

### `unknown_map`

Указанная map или указанная ветка отсутствуют в схеме target scope.

### `unknown_link`

Указанный link `name` отсутствует в схеме target scope.

## Cross-scope

### `cross_scope_not_allowed`

`link` или `create.set.links[]` пытается создать связь в направлении за
пределами разрешённой матрицы (см. [model.md](model.md)). Разрешённые
направления: `main → main`, `usage → usage`, `usage → main`.

### `delete_blocked_by_cross_scope_link`

`delete` main-узла, на который ссылается incoming `usage → main` link.
`details.blocking_links[]` перечисляет блокирующие links и usage-узлы.

## Read gates

### `read_required`

`tx` требует `read_id`-ов, которых нет в `tx.read_ids`.
`details.required[]` содержит готовые `read`-вызовы для добора. См.
[read-gates.md](read-gates.md).

### `invalid_read_id`

Один из `read_id` в `tx.read_ids` устарел, не выпускался kernel-ом, не
подходит read_scope-у либо не соответствует текущей Схеме.
`details.reason` ∈ {`unknown`, `stale`, `scope_mismatch`,
`schema_mismatch`}. При связке id с конкретным узлом / gate
`details.required` содержит готовый `read`-вызов.

## Schema

### `validation_failed`

Tx прошла resolve и read gates, финальная validation графа отклонила
результат. `details.errors[]` — массив объектов
`{code, id, path, ref}` с подробностями каждого нарушения.

### `schema_breaks_existing_data`

`tx scope=scheme` дал бы breaking change для существующих узлов main /
usage. `details.violations[]` перечисляет нарушающие узлы.

## Rollback

### `rollback_not_found`

`rollback` указал id, которого нет в hist (нет event-узла с таким `id`).

### `rollback_conflict`

`rollback` затрагивает последующие изменения. `details.conflicts[]`
содержит конфликтующие ресурсы, `reason` и `blocking_transactions[]` с
компактными summary блокирующих tx (`id`, `title`, `date`).

### `rollback_failed`

Rollback-операция начата и не завершилась успешно. Состояние
не изменено (атомарность tx).

### `rollback_already_done`

В hist уже существует event-узел с `rollback_of`, равным указанному id.

## Hist write

### `hist_write_failed`

Изменения готовы к применению, запись соответствующего event-узла
`hist/transaction` неуспешна. Tx считается не применённой; состояние
main / usage / scheme остаётся прежним.

## Method dispatch

### `unknown_method`

`params.name` в MCP `tools/call` за пределами набора (`read`, `tx`).
