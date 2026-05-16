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
  hist/                       журнал всех изменений editable scope
  .docswalker/
    meta-schema.json          kernel-owned: контракт узла, link и hist-формат
    sequence.txt              служебный счётчик и порядок
```

Хранение в JSON. Никакого YAML на диске не используется.

## 4 Scope

- `main` — пользовательский контент. Editable через `tx` без явного `scope`
  (default).
- `usage` — инструкции для LLM (rules, examples, topics, methods, fields,
  errors, map descriptions, link descriptions). Editable через
  `tx scope=usage`.
- `scheme` — редактируемые контракты Схемы main и usage. Editable через
  `tx scope=scheme` с обязательным breaking-change-check; meta-schema и
  hist-schema в scheme scope не лежат, они kernel-owned.
- `hist` — kernel-журнал всех изменений editable scope. Read-only для LLM;
  `tx scope=hist` возвращает `hist_read_only`.

LLM выбирает scope для read обязательным параметром `scope` на каждом
вызове `read`. Для write LLM передаёт опциональный `scope` в `tx`; при его
отсутствии цель — `main`.

## Meta-schema поля

Meta-schema (`.docswalker/meta-schema.json`) задаёт обязательные поля узла
и link для всех scope. LLM читает meta-schema через
`read scope=scheme` со специальным маркером `meta=true` в селекторе.

### Поля узла

- `node.id` — целое число, глобально уникально на весь каталог. Не
  переиспользуется после удаления (kernel-генерируемое исключение —
  rollback, см. [tx.md](tx.md)).
- `node.path` — уникальный иерархический address внутри scope. Сегменты
  разделены `/`. Последний сегмент задаёт `title` узла.
- `node.title` — последний сегмент `node.path`. Обязан соответствовать
  regex `^[\p{L}\p{Nd}._-]+$` (Unicode-буквы, decimal digits, точка, тире,
  underscore). Уникальность siblings проверяется по lower-case форме
  `title` внутри одного parent path.
- `node.value` — содержимое узла. Для main это документируемый контент;
  для usage — инструкция/example/topic; для scheme — описание map/link/
  constraint; для hist — payload event-а.
- `node.map_bindings` — классификационные привязки узла к maps,
  объявленным в schema scope-а. Ключ — имя map, значение — путь ветки в
  branches этой map. У одного узла не больше одной привязки к одной map.

### Поля link

- `link.name` — имя link, объявленное в schema scope.
- `link.source.id` — id узла-источника.
- `link.target.id` — id узла-цели.
- `link.target.scope` — опциональное; если присутствует и отличается от
  scope узла-источника, это cross-scope link. Default — same scope as
  source.

Identity link — tuple `(name, source.id, target.id, target.scope)`. Двух
links с одинаковым tuple в одном scope быть не может.

## Глобальный id

Один счётчик kernel-а на весь каталог. main-узел, usage-узел, scheme-узел,
hist-change-узел — все получают id из одного пространства. Это упрощает
ссылочную идентификацию (особенно в hist) и устраняет необходимость
таскать `(scope, id)` как составной key.

`(scope, id)` имеет смысл только в селекторах, когда LLM хочет ограничить
выборку конкретным scope. Сам `id` уже однозначно адресует узел.

## Cross-scope ссылки

Разрешённые направления link:

| source scope | target scope | разрешено |
|--------------|--------------|-----------|
| `main`       | `main`       | да        |
| `usage`      | `usage`      | да        |
| `usage`      | `main`       | да        |
| `main`       | `usage`      | нет       |
| `main`       | `hist`       | нет       |
| `usage`      | `hist`       | нет       |
| `hist`       | любой        | нет (links в hist отсутствуют) |
| `scheme`     | любой scope, кроме `scheme` | нет |

Контракт: main не зависит от своей документации (`main → usage`
запрещён); hist не имеет исходящих links вовсе (идентификация затронутых
узлов — через структурные поля `target.*`, см. [hist-scope.md](hist-scope.md));
scheme живёт изолированно от данных. Cross-scope link допустим только в
направлении `usage → main` — например `usage/example → main/method-узел`.

При попытке удалить main-узел, на который есть incoming `usage → main`
link, `tx` возвращает `delete_blocked_by_cross_scope_link` со списком
блокирующих usage-узлов. LLM должна сначала убрать или переключить эти
links отдельной `tx scope=usage`, затем повторить delete.

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

- `defaults.path_parent` — если задан, `path` внутри `create`, `move.to`
  и selector slots должен быть относительным внутри этого parent. Полный
  `path` вместе с `defaults.path_parent` даёт `ambiguous_path_base`.
- `defaults.map_bindings` — применяется к `create.set.map_bindings`. Для
  `move.to.map_bindings` привязки задаются явно — общий default не
  применяется к bulk-переклассификации, чтобы случайное wildcard-move
  не переписывало map_bindings разом.

## Аргументы методов

Каждый MCP `tools/call` выбирает метод по `params.name` (`read` или `tx`).
Внутри `params.arguments` поле `method` отсутствует. Минимальная форма
аргументов:

```json
{
  "scope": "main",
  "defaults": {},
  "ops": []
}
```

Для `tx` к этой форме добавляются:

```json
{
  "commit_message": "...",
  "read_ids": []
}
```

`ops` обязательно и всегда массив, даже если операция одна. Каждый элемент
`ops[]` — объект с ровно одним ключом операции (`select`, `create`,
`update`, `move`, `delete`, `link`, `unlink`, `rollback`), и значение
ключа содержит тело операции.

`scope` в `read` обязателен и принимает `"main"`, `"usage"`, `"hist"` или
`"scheme"`. `scope` в `tx` опционален и принимает `"usage"` или
`"scheme"`; отсутствие = `"main"`; `"hist"` отклоняется как
`hist_read_only`.

`commit_message` обязателен для `tx`. Значение не должно превышать 100
токенов. `read_ids` опционален и содержит opaque receipts, полученные
через предыдущие `read` (см. [read-gates.md](read-gates.md)).
