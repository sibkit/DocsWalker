# Модель JSON API v1

DocsWalker хранит граф знаний в одном каталоге `docs/`. Каталог разделён
на четыре scope: `main`, `usage`, `scheme`, `hist`. Узлы и связи во всех
scope подчиняются единой meta-schema, поверх которой каждый scope имеет
свою editable или kernel-owned schema.

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

Meta-schema (`.docswalker/meta-schema.json`) задаёт обязательные поля
узла и link для всех scope. LLM читает meta-schema через
`read scope=scheme` со специальным маркером `meta=true` в селекторе.

### Поля узла

- `node.id` — opaque hex-строка lower-case (например `"2a"`, `"4c8f"`).
  Глобально уникальна на весь каталог. После удаления узла id остаётся
  историческим идентификатором в hist и не выдаётся новым узлам;
  исключение — kernel-генерируемый rollback восстанавливает удалённый
  id на тот же узел (см. [tx.md](tx.md)).
- `node.path` — уникальный иерархический address внутри scope. Сегменты
  разделены `/`. Последний сегмент задаёт `title` узла.
- `node.title` — последний сегмент `node.path`. Соответствует regex
  `^[\p{L}\p{Nd}._-]+$` (Unicode-буквы, decimal digits, точка, тире,
  underscore). Уникальность siblings проверяется по lower-case форме
  `title` внутри одного parent path.
- `node.value` — содержимое узла. Для main — документируемый контент;
  для usage — инструкция / example / topic; для scheme — описание map
  / link / constraint; для hist — payload event-а.
- `node.map_bindings` — классификационные привязки узла к maps,
  объявленным в schema scope-а. Ключ — имя map, значение — путь ветки
  в branches этой map. У одного узла одна привязка к одной map.

### Поля link

- `link.name` — имя link, объявленное в schema scope.
- `link.source.id` — id узла-источника.
- `link.target.id` — id узла-цели.
- `link.target.scope` — опциональное. Default — scope узла-источника.

Identity link — tuple `(name, source.id, target.id, target.scope)`.
Tuple уникален в пределах scope.

## Глобальный id

Один счётчик kernel-а на весь каталог. main-узел, usage-узел,
scheme-узел, hist-change-узел — все получают id из одного пространства.
Это упрощает ссылочную идентификацию (особенно в hist).

Все id — opaque hex-строки lower-case переменной длины: `node.id`,
`link.source.id`, `link.target.id`, `tx_id`, `read_id`. Префиксы и
суффиксы внутри id не используются — kernel выдаёт компактные hex.

`(scope, id)` имеет смысл в селекторах, когда нужно ограничить выборку
конкретным scope. Сам `id` однозначно адресует узел.

## Cross-scope ссылки

Разрешённые направления link:

| source scope | target scope |
|--------------|--------------|
| `main`       | `main`       |
| `usage`      | `usage`      |
| `usage`      | `main`       |

Иное направление возвращает `cross_scope_not_allowed` при `link` и при
`create.set.links[]`. Удаление main-узла, на который ссылается
incoming `usage → main` link, возвращает
`delete_blocked_by_cross_scope_link` со списком блокирующих usage-узлов.

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
  "commit_message": "...",
  "read_ids": []
}
```

`ops` обязательно и всегда массив, даже если операция одна. Каждый
элемент `ops[]` — объект с ровно одним ключом операции (`select`,
`create`, `update`, `move`, `delete`, `link`, `unlink`, `rollback`);
значение ключа — тело операции.

`commit_message` обязателен для `tx`, не больше 100 токенов.
`read_ids` опционален и содержит opaque receipts, полученные через
предыдущие `read` (см. [read-gates.md](read-gates.md)).
