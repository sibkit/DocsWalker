# Модель JSON API v1

DocsWalker хранит граф знаний в одном каталоге `docs/`. Каталог разделён
на четыре scope: `main`, `usage`, `scheme`, `hist`. Meta-schema описывает
два класса узлов: data-узлы (main / usage / scheme) с одним контрактом и
event-узлы (hist) с другим. Поверх meta-schema у data-scope-ов есть
editable Схема (`scheme`-scope) с описанием maps и links.

## Каталог

```
docs/
  main/                       пользовательский контент (project data)
  usage/                      инструкции для LLM
  scheme/                     редактируемые схемы main и usage
  hist/                       журнал изменений editable scope
  .docswalker/
    meta-schema.json          kernel-owned: контракт узла, link и hist-формат
    sequence.txt              служебный счётчик и порядок
```

Хранение в JSON.

## 4 Scope

- `main` — пользовательский контент. Editable через `tx` без `scope`.
- `usage` — инструкции для LLM (rules, examples, topics, methods,
  fields, errors, map descriptions, link descriptions). Editable через
  `tx scope=usage`.
- `scheme` — редактируемые контракты Схемы main и usage. Editable через
  `tx scope=scheme` с обязательным breaking-change-check.
- `hist` — kernel-журнал изменений editable scope. Read-only для LLM.

`scope` в `read` и `tx` опционален. Отсутствие = `main`. Явное
указание `"main"` — ошибка `invalid_scope`. Допустимые значения в `read`:
`"usage"`, `"hist"`, `"scheme"`. Допустимые значения в `tx`: `"usage"`,
`"scheme"`. `tx scope=hist` отклоняется кодом `hist_read_only`.

## Meta-schema поля

Meta-schema (`.docswalker/meta-schema.json`) задаёт два класса узлов:

- **Data-узел** — узлы scope `main`, `usage`, `scheme`. Контракт:
  `id` + `path` + `title` + `value` + `map_bindings`.
- **Event-узел** — узлы scope `hist`. Контракт: `id` + `title` + `date`
  + опционально `description` + опционально `rollback_of` + три секции
  `created` / `changed` / `deleted`.

Link (как сущность с tuple-identity) применим только внутри data-scope.
В hist изменения links хранятся как элементы массивов внутри секций
`hist/transaction`, без отдельной identity.

LLM читает meta-schema через отдельную форму `select` со строковым
значением `"meta"` — см. [read.md](read.md), раздел «Форма-строка:
kernel-режимы».

### Поля data-узла

Помета `compact` — поле всегда возвращается в ответе `read` (включая
compact-форму без `include`). Помета `loadable` — поле возвращается
только если запрошено через `include` (см. [read.md](read.md)).

- `id` (`compact`) — opaque hex-строка lower-case (например `"2a"`,
  `"4c8f"`). Глобально уникальна на весь каталог. После удаления узла
  id остаётся историческим идентификатором в hist и не выдаётся новым
  узлам; исключение — kernel-генерируемый rollback восстанавливает
  удалённый id на тот же узел (см. [tx.md](tx.md)).
- `path` (`compact`) — уникальный иерархический address внутри scope.
  Сегменты разделены `/`. Последний сегмент задаёт `title` узла.
- `title` (`compact`) — последний сегмент `path`. Соответствует regex
  `^[\p{L}\p{Nd}._-]+$` (Unicode-буквы, decimal digits, точка, тире,
  underscore). Уникальность siblings проверяется по lower-case форме
  `title` внутри одного parent path.
- `map_bindings` (`compact`) — классификационные привязки узла к maps,
  объявленным в schema scope-а. Ключ — имя map, значение — путь ветки
  в branches этой map. У одного узла одна привязка к одной map.
  В `read` всегда возвращается полный словарь активных привязок узла.
  В `tx` меняется только через `move.to.map_bindings` с partial-семантикой
  по ключам (ключ → ветка: установить или перезаписать; ключ → `null`:
  снять; не упомянутые ключи не трогаются — см. [tx.md](tx.md)). При
  `create.set.map_bindings` задаётся полный начальный словарь, `null` в
  значении запрещён.
- `value` (`loadable`) — содержимое узла. Для main — документируемый
  контент; для usage — инструкция / example / topic; для scheme —
  описание map / link / constraint.
- `links` (`loadable`) — incident links узла (исходящие и входящие).
  Поле виртуальное: identity link хранится отдельно, но в ответе `read`
  выдаётся как поле узла.

### Поля event-узла

Помета `compact` / `loadable` — см. описание выше для data-узла.

- `id` (`compact`) — opaque hex-строка lower-case, из того же
  глобального id-пространства каталога. Одновременно играет роль
  `tx_id` транзакции (отдельного поля `tx_id` у event-узла нет).
- `title` (`compact`) — свободный текст commit-сообщения. Без
  regex-ограничений и без требования уникальности. Ограничение длины:
  не больше 100 токенов.
- `date` (`compact`) — ISO-8601 UTC дата применения транзакции.
- `rollback_of` (`compact`) — опциональная строка-id ссылка на исходную
  транзакцию, если данный event-узел — kernel-генерируемая
  компенсирующая rollback-tx. Отсутствие поля = обычная LLM-tx.
- `description` (`loadable`) — опциональный длинный текст с
  подробностями транзакции. Без жёсткого лимита токенов.
- `created` / `changed` / `deleted` (`loadable`) — три секции с
  описанием того, что произошло в транзакции. Детали — в
  [hist-scope.md](hist-scope.md).

У event-узла нет полей `path`, `value`, `map_bindings`, `title`-regex.
Event-узлы идентифицируются только по `id`, иерархии под hist нет.

### Поля link (data-scope)

- `name` — имя link, объявленное в schema scope.
- `from.id` — id узла-источника.
- `to.id` — id узла-цели.

Identity link — tuple `(name, from.id, to.id)`. Tuple уникален
глобально (id узлов сквозные на весь каталог, поэтому пара
`(from, to)` однозначно определяет endpoints без указания scope).

В запросах `tx` (`link.from`, `link.to`, `unlink.from`, `unlink.to`,
`create.set.links[].to`) endpoint, заданный единственным полем `id`,
записывается как строка-id вместо объекта `{ "id": "..." }`. Объектная
форма остаётся для остальных способов адресации (`{ "selector": ... }`,
`{ "alias": "..." }`, `{ "ids": [...] }`).

## Глобальный id

Один счётчик kernel-а на весь каталог. main-узел, usage-узел,
scheme-узел, hist/transaction (event-узел) — все получают id из одного
пространства. Это упрощает ссылочную идентификацию (особенно в hist,
где id узла транзакции одновременно играет роль её `tx_id`).

Все id — opaque hex-строки lower-case переменной длины: `id` узла,
`from.id` / `to.id` у link, `read_id`. Префиксы и суффиксы внутри id
не используются — kernel выдаёт компактные hex.

`(scope, id)` имеет смысл в селекторах, когда нужно ограничить выборку
конкретным scope. Сам `id` однозначно адресует узел.

## Cross-scope ссылки

Разрешённые направления link:

| from scope | to scope |
|------------|----------|
| `main`     | `main`   |
| `usage`    | `usage`  |
| `usage`    | `main`   |

Иное направление возвращает `cross_scope_not_allowed` при `link` и при
`create.set.links[]`. Удаление main-узла, на который ссылается
incoming `usage → main` link, возвращает
`delete_blocked_by_cross_scope_link` со списком блокирующих usage-узлов.

Scope endpoint-узла определяется kernel-ом по `id` — отдельное поле в
link identity не требуется.

## Defaults

`defaults` — опциональный блок аргументов `read` и `tx`, задающий общие
значения для всех ops запроса.

```json
{
  "defaults": {
    "path_parent": "api/write-ops",
    "map_bindings": {
      "content": "documents/spec"
    }
  }
}
```

- `defaults.path_parent` — если задан, `path` внутри `create`,
  `move.to.parent_path` и selector slots интерпретируется как
  относительный внутри этого parent. Абсолютный `path` вместе с
  `defaults.path_parent` возвращает `ambiguous_path_base`.
- `defaults.map_bindings` — применяется к `create.set.map_bindings`. Для
  `move.to.map_bindings` привязки задаются явно — общий default к
  `move` не применяется.

## Аргументы методов

Каждый MCP `tools/call` выбирает метод по `params.name` (`read` или
`tx`). Внутри `params.arguments` поле `method` отсутствует. Минимальная
форма аргументов:

```json
{
  "ops": []
}
```

С опциональным `scope` и `defaults`:

```json
{
  "scope": "usage",
  "defaults": {},
  "ops": []
}
```

Для `tx` добавляются:

```json
{
  "title": "...",
  "description": "...",
  "read_ids": []
}
```

`ops` обязательно и всегда массив, даже если операция одна. Каждый
элемент `ops[]` — объект с ровно одним ключом операции; значение ключа
— тело операции. Допустимый набор ключей зависит от метода:

- `read` — только `select`.
- `tx` — `create`, `update`, `move`, `delete`, `link`, `unlink`,
  `rollback`. Op `select` в `tx` отсутствует; alias в `tx` объявляется
  только через `create.as` (см. [selectors.md](selectors.md), раздел
  «Aliases»).

`title` обязателен для `tx`, не больше 100 токенов. Свободный текст,
без regex-ограничений (это title event-узла, не data-узла).
`description` опционален, без жёсткого лимита токенов — туда уходит
подробное описание, если нужно.
`read_ids` опционален и содержит opaque receipts, полученные через
предыдущие `read` (см. [read-gates.md](read-gates.md)).
