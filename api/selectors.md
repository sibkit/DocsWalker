# Селекторы

Селектор — predicate-набор над полями узла или link, объявленными в
meta-schema и в schema выбранного scope. Селектор передаётся как
JSON-объект в поле `selector` операции `select` (для `read`), `move`,
`delete`, `link` и `unlink`. Селектор — это набор предикатов по тем же
полям, которые узел и link имеют в данных.

## Поля селектора

Базовые поля (meta-schema, работают во всех scope):

- `node.id` — hex-строка или массив hex-строк. Точное равенство или
  вхождение в массив.
- `node.path` — строка. Точный path либо pattern с wildcard `*` (один
  сегмент) и `**` (любая глубина).
- `node.title` — строка. Точное равенство (sibling-нечувствительность —
  через lower-case при матчинге).
- `node.map_bindings` — объект. Каждая пара ключ-значение задаёт
  ограничение «у узла есть привязка к map `key`, значение совпадает с
  указанным path-pattern». Path-pattern допускает wildcard `*` и `**`.
- `node.links` — объект-ограничение по incident links узла. См.
  «Ограничение по links» ниже.

Поля для link-операций и для селектора в hist-changes:

- `link.name` — строка.
- `link.source.id` — hex-строка.
- `link.target.id` — hex-строка.
- `link.target.scope` — строка (`"main"` / `"usage"` / `"scheme"`).

Поля hist-change (см. [hist-scope.md](hist-scope.md)):

- `target.node.id` — hex-строка. Узел, которого касается change.
- `target.link.name` / `target.link.source.id` / `target.link.target.id`
  / `target.link.target.scope` — для link-events.

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
ключ `from`. Допускается множественное вложение, без рекурсии (один
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

- `regex` — обязательный, .NET regex. Пустая строка возвращает
  `invalid_match_regex`.
- `fields` — опциональный, дефолт `["title", "value"]`. Допустимы
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

`alias` действует в текущем запросе, в selector slots последующих
операций. Ссылка на необъявленный alias возвращает `unknown_alias`.

В `tx` alias используется для передачи результата `select` или `create`
в последующие write-операции (например, `create` с alias, потом `link`,
ссылающийся на этот alias).

## Selector slots в write-операциях

Поведение:

- `move.selector` — bulk; обязателен `expected_count`.
- `delete` — `selector` или `ids`; bulk; обязателен `expected_count`.
- `link.from` и `link.to` — селекторы или `id` / `ids`; cross-product
  определяет создаваемые links; обязателен `expected_count` на сумму
  links.
- `unlink.from` и `unlink.to` — селекторы или `id` / `ids`; обязателен
  `expected_count`.
- `update` — только `id` одного узла.
- `create` — селектор не используется.

Пустой результат селектора в операции, требующей непустого набора,
возвращает `not_found`. Если результат содержит больше одного узла, а
операция требует единственной цели, возвращается `ambiguous_selector`.

## Counts

`expected_count` — целое неотрицательное число, обязательное для
bulk-ops в `tx`. Если фактическое число затронутых узлов или links
отличается от `expected_count`, tx возвращает `count_mismatch` и ничего
не применяет.

Перед bulk-операцией LLM через `read` явно смотрит размер выбранного
набора и затем указывает `expected_count` в `tx`.
