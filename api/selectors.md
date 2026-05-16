# Селекторы

Селектор — predicate-набор над полями узла или link, объявленными в
meta-schema и в schema выбранного scope. Селектор передаётся как
JSON-объект в поле `selector` операции `select` (для `read`), `update`
(если bulk-расширение, не используется в v1), `move`, `delete`, `link` и
`unlink`. Селектор не специальный язык — это просто набор предикатов
по тем же полям, которые узел и link имеют в данных.

## Поля селектора

Базовые поля (meta-schema, работают во всех scope):

- `node.id` — целое число или массив целых чисел. Точное равенство по id
  или вхождение в массив.
- `node.path` — строка. Может быть точным path, либо pattern с
  wildcard-сегментами `*` (один сегмент) и `**` (любая глубина).
- `node.title` — строка. Точное равенство (sibling-нечувствительность —
  через lower-case при матчинге).
- `node.map_bindings` — объект. Каждая пара ключ-значение задаёт
  ограничение «у узла есть привязка к map `key`, и значение этой
  привязки совпадает с указанным path-pattern». Path-pattern допускает
  wildcard `*` и `**`.
- `node.links` — объект-ограничение по incident links узла. См.
  «Ограничение по links» ниже.

Поля для link-операций и для селектора в hist-changes:

- `link.name` — строка.
- `link.source.id` — целое.
- `link.target.id` — целое.
- `link.target.scope` — строка (`"main"` / `"usage"` / `"scheme"`).

Поля hist-change (см. [hist-scope.md](hist-scope.md)):

- `target.node.id` — целое. Узел, которого касается change.
- `target.link.name` / `target.link.source.id` / `target.link.target.id` /
  `target.link.target.scope` — для link-events.

## Пример простого селектора

```json
{
  "selector": {
    "node.path": "DocsWalker/**",
    "node.map_bindings": {
      "content": "documents/spec"
    }
  }
}
```

Все main-узлы под `DocsWalker/`, привязанные к ветке `documents/spec`
карты `content`.

## Ограничение по links

Поле `node.links` фильтрует узлы по их incident links.

```json
{
  "selector": {
    "node.links": {
      "name": "depends_on",
      "to": {
        "node.map_bindings": {
          "content": "documents/spec"
        }
      }
    }
  }
}
```

Возвращает узлы, у которых есть исходящий link `depends_on` на узел,
подходящий под вложенный селектор `to`. Для входящих links используется
ключ `from`. Допускается множественное вложение, но без рекурсии (один
уровень).

## Match по содержимому

`selector.match` добавляет regex-фильтр по текстовым полям.

```json
{
  "selector": {
    "node.path": "DocsWalker/**",
    "match": {
      "regex": "validation_failed",
      "fields": ["title", "value"],
      "case_sensitive": false
    }
  }
}
```

- `regex` — обязательный, .NET regex. Пустая строка — `invalid_match_regex`.
- `fields` — опциональный, дефолт `["title", "value"]`. Допустимы только
  `title` и `value`.
- `case_sensitive` — опциональный, дефолт `false`.

Regex имеет bounded timeout на стороне kernel. Превышение — ошибка
`match_timeout` с подсказкой сузить `path` / `map_bindings` или упростить
выражение.

## Aliases

В рамках одного запроса операция `select` может объявить alias через
поле `as`:

```json
{
  "ops": [
    {
      "select": {
        "as": "rules",
        "selector": {
          "node.map_bindings": {
            "content": "usage/rule"
          }
        }
      }
    },
    {
      "select": {
        "selector": {
          "node.links": {
            "name": "example",
            "to": { "alias": "rules" }
          }
        }
      }
    }
  ]
}
```

`alias` действует только в текущем запросе и только в селектор slots
последующих операций. Ссылка на необъявленный alias — `unknown_alias`.

В `tx` alias используется для передачи результата `select` в
последующие write-операции (например, `create` с alias, потом `link`,
ссылающийся на этот alias).

## Selector slots в write-операциях

В `move`, `delete`, `link` и `unlink` `selector` определяет набор целей.
Поведение:

- `move.source` — селектор; bulk; обязателен `expected_count`.
- `delete` — селектор или `ids`; bulk; обязателен `expected_count`.
- `link.from` и `link.to` — селекторы или `id` / `ids`; tuple
  cross-product определяет создаваемые links; обязателен `expected_count`
  на сумму links.
- `unlink.from` и `unlink.to` — селекторы или `id` / `ids`;
  обязателен `expected_count`.
- `update` — только `id` одного узла; селектор не используется.
- `create` — селектор не используется.

Если результат селектора пустой, а операция требует непустого набора —
ошибка `not_found`. Если результат селектора содержит больше одного
узла, а операция требует единственной цели (`update`) — это
не применимо (`update` принимает только `id`).

## Counts

`expected_count` — целое неотрицательное число, обязательное для bulk-ops
в `tx`. Если фактическое число затронутых узлов или links отличается от
`expected_count`, tx возвращает `count_mismatch` и ничего не применяет.

Перед bulk-операцией LLM должна через `read` явно посмотреть размер
выбранного набора, и только потом указывать `expected_count` в `tx`.
