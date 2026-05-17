# Селекторы

Селектор — predicate-набор над полями узла (data или event) или
link-identity. Селектор передаётся как JSON-объект в поле `selector`
операции `select` (для `read`), `move`, `delete`, `link` и `unlink`.

Если запрос `read` содержит `at` (см. [model.md](model.md), раздел
«Темпоральные чтения (`at`)»), селектор интерпретируется в координатах
состояния scope на момент `at` — `path`, `title`, `map_bindings`,
incident links сверяются с реконструированным состоянием, а не с now.
В остальных контекстах селектор интерпретируется на текущем состоянии.

Набор допустимых полей селектора зависит от scope чтения:

- В `read` без `scope` (= `main`) и в `read scope=usage`/`scope=scheme`,
  а также во всех write-ops `tx` — селектор работает по полям
  data-узла.
- В `read scope=hist` — селектор работает по полям event-узла и
  hist-специфичным virtual-предикатам.

Чтение kernel-owned данных (meta-schema) — не селектор, а отдельная
форма `select` со строковым значением (например, `"select": "meta"`).
См. [read.md](read.md), раздел «Форма-строка: kernel-режимы». Поля
вроде `meta:true` внутри селектора не существуют.

## Поля селектора по data-узлу

Применяются в main / usage / scheme и во всех селекторах внутри `tx`
(независимо от target scope записи).

- `id` — hex-строка или массив hex-строк. Точное равенство или
  вхождение в массив.
- `path` — строка. Точный path либо pattern с wildcard `*` (один
  сегмент) и `**` (любая глубина).
- `title` — строка. Точное равенство (sibling-нечувствительность —
  через lower-case при матчинге).
- `map_bindings` — объект. Каждая пара ключ-значение задаёт
  ограничение «у узла есть привязка к map `key`, значение совпадает с
  указанным path-pattern». Path-pattern допускает wildcard `*` и `**`.
- `links` — объект-ограничение по incident links узла. См.
  «Ограничение по links» ниже.

Идентификационные поля link (используются в `links`-вложении и в
hist-предикате `touches_link`; в верхнем уровне селектора по data-узлу
link не выбирается — links индуцируются cross-product `from` × `to` в
операциях `link` / `unlink`):

- `name` — строка.
- `from` — id или вложенный селектор по узлу-источнику.
- `to` — id или вложенный селектор по узлу-цели.

## Поля селектора по event-узлу (hist)

Применяются только в `read scope=hist`.

- `id` — hex-строка или массив hex-строк. Точное равенство.
- `title` — строка либо `{ "match": { "regex": "...", ... } }` для
  regex-поиска по свободному тексту.
- `date` — строка (точное равенство) либо
  `{ "match": { "regex": "..." } }` для диапазонов и шаблонов.
- `description` — строка-подстрока либо `{ "match": { "regex": "..." } }`.
- `rollback_of` — строка-id. Точное равенство id исходной транзакции.
  Находит kernel-сгенерированные компенсирующие rollback-tx для
  заданной.
- `tx_scope` — virtual-предикат, `"main"` / `"usage"` / `"scheme"`.
  Kernel вычисляет принадлежность транзакции к scope из содержимого её
  секций.
- `touches_node` — строка-id (короткая форма) или объект
  `{ "id": "..." }`. Транзакции, в которых указанный data-узел
  фигурирует в любой секции (`created`/`changed`/`deleted`).
- `touches_link` — объект `{ "name": "...", "from": "<id>", "to": "<id>" }`.
  Транзакции, тронувшие указанный link.

В hist селектор не использует `path`, `content`, `map_bindings`, `links`,
incident-link-вложение — этих полей у event-узлов нет.

## Пример простого селектора

```json
{
  "selector": {
    "path": "DocsWalker/**",
    "map_bindings": {
      "category": "documents/spec"
    }
  }
}
```

Все main-узлы под `DocsWalker/`, привязанные к ветке `documents/spec`
карты `category`.

## Ограничение по links

Поле `links` фильтрует data-узлы по их incident links.

```json
{
  "selector": {
    "links": {
      "name": "depends_on",
      "to": {
        "map_bindings": {
          "category": "documents/spec"
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

В hist `links`-вложение неприменимо.

## Match по содержимому

`selector.match` добавляет regex-фильтр по текстовым полям узла.

```json
{
  "selector": {
    "path": "DocsWalker/**",
    "match": {
      "regex": "validation_failed",
      "fields": ["title", "content"],
      "case_sensitive": false
    }
  }
}
```

- `regex` — обязательный, .NET regex. Пустая строка возвращает
  `invalid_match_regex`.
- `fields` — опциональный. Допустимые поля зависят от класса узла:
  - data-узел: `title`, `content`. Default `["title", "content"]`.
  - event-узел (hist): `title`, `description`, `date`. Default
    `["title", "description"]`.
- `case_sensitive` — опциональный, дефолт `false`.

Регэкс имеет bounded timeout на стороне kernel. Превышение — ошибка
`match_timeout` с подсказкой сузить `path` / `map_bindings` (или
hist-предикат) либо упростить выражение.

Удобная короткая форма для одного поля — указать `match` прямо в
значении поля (вместо явного перечисления `fields`):

```json
{
  "selector": {
    "date": { "match": { "regex": "^2026-05-1[0-7]$" } }
  }
}
```

Эквивалент: `match.fields = ["date"]`.

## Aliases

Alias — именованная ссылка на узел (или набор узлов), объявленная
ранее в этом же запросе. Удобна, когда последующей op нужно сослаться
на результат предыдущей без повторения селектора. Ссылка на alias —
объект `{ "alias": "<name>" }`; действует только до конца текущего
запроса; ссылка на необъявленный alias — `unknown_alias`.

Объявление alias зависит от метода:

- В `read` alias объявляется на операции `select` через поле `as` и
  ссылается на **набор узлов**, выбранных селектором.
- В `tx` alias объявляется на операции `create` через поле `as` и
  ссылается на **ровно один новый узел**, чей `id` выдан kernel-ом
  внутри этой tx. Других способов объявить alias в `tx` нет — op
  `select` в `tx` не существует.

### Alias в `read`

```json
{
  "ops": [
    {
      "select": {
        "as": "rules",
        "selector": {
          "map_bindings": {
            "category": "usage/rule"
          }
        }
      }
    },
    {
      "select": {
        "selector": {
          "links": {
            "name": "example",
            "to": { "alias": "rules" }
          }
        }
      }
    }
  ]
}
```

`as: "rules"` биндит набор найденных узлов; следующая op подставляет
этот набор в `links.to` второго селектора.

### Alias в `tx`

```json
{
  "ops": [
    {
      "create": {
        "path": "DocsWalker/api/selectors",
        "as": "selectors",
        "set": { "content": "..." }
      }
    },
    {
      "link": {
        "name": "depends_on",
        "from": { "alias": "selectors" },
        "to": "11",
        "expected_count": 1
      }
    }
  ]
}
```

`as: "selectors"` биндит `id` только что созданного узла; следующая op
использует его как endpoint в `link.from`. Если в одной `tx` нужно
сослаться на набор существующих узлов из нескольких bulk-ops, селектор
задаётся в каждой op напрямую — отдельной «резолверной» op нет.

## Endpoint в link / unlink / create.set.links[]

Поля `from` и `to` в `tx.link`, `tx.unlink`, `tx.create.set.links[]`
адресуют endpoint узла. Допустимые формы:

- `"2a"` — строка-id (короткая форма, когда endpoint = единственный
  узел по id).
- `{ "id": "2a" }` — длинная форма (равнозначна строке-id; не
  рекомендуется, но допустима для единообразия).
- `{ "ids": ["2a", "2b"] }` — несколько endpoints по id.
- `{ "selector": { ... } }` — endpoint по селектору (bulk).
- `{ "alias": "..." }` — endpoint по alias из предыдущего `create`
  этого же `tx` (alias в `tx` объявляется только на `create.as`).

При `link` / `unlink` cross-product `from` × `to` × `name` определяет
набор затрагиваемых links.

## Selector slots в write-операциях

Поведение:

- `move.selector` — bulk; обязателен `expected_count`.
- `delete` — `selector` или `ids`; bulk; обязателен `expected_count`.
- `link.from` и `link.to` — id-строка / объектная форма (см. выше);
  cross-product определяет создаваемые links; обязателен
  `expected_count` на сумму links.
- `unlink.from` и `unlink.to` — то же, что у `link`.
- `update` — только `id` одного узла.
- `create` — селектор не используется.

Пустой результат селектора в операции, требующей непустого набора,
возвращает `not_found`. Исключение — bulk-op с `expected_count = 0`,
где пустой результат легитимен (LLM явно проверяет, что под селектор
ничего не подпадает; см. «Counts» ниже). Если результат содержит
больше одного узла, а операция требует единственной цели, возвращается
`ambiguous_selector`.

## Counts

`expected_count` — целое неотрицательное число, обязательное для
bulk-ops в `tx`. Если фактическое число затронутых узлов или links
отличается от `expected_count`, tx возвращает `count_mismatch` и ничего
не применяет.

`expected_count = 0` — легитимное значение. Op с пустым селектором и
`expected_count = 0` успешна (это «проверка отсутствия», узлы не
изменяются); `not_found` за пустой селектор в этом случае не
возвращается.

Перед bulk-операцией LLM через `read` явно смотрит размер выбранного
набора и затем указывает `expected_count` в `tx`.
