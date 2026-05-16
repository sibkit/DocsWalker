# Выбор Узлов

## Selector

Selector - строгий объект выбора узлов. Query-like операции и write-операции с
потенциально широким выбором используют selector slots.

В v1 selector может содержать только:

- `ids` как явное перечисление id узлов.
- `path` с exact path или wildcard pattern.
- `map_bindings` как фильтры по классификационным привязкам.
- `links` как выбор по именованному link.
- `match` как regex-фильтр по `title` и/или `value`.

Форма selector строгая: неизвестные поля являются `invalid_request`.

## Wildcard Path

В `selector.path` разрешены wildcard `*` и `**`. `create.path` и
`move.to.parent` являются exact path без wildcard.

- `foo/*` выбирает один сегмент ниже `foo`.
- `foo/**` выбирает любую глубину.
- `foo/**/bar` выбирает `bar` на любой глубине внутри `foo`.

## Match

`match` фильтрует уже выбранные узлы по регулярному выражению.
Если `ids`, `path`, `map_bindings` и `links` не заданы, `match` применяется ко
всем узлам graph-а.

```json
{
  "path": "DocsWalker-LLM JSON API/**",
  "match": {
    "regex": "validation_failed",
    "fields": ["title", "value"],
    "case_sensitive": false
  }
}
```

`regex` обязателен. `fields` опционально задает поля поиска и по умолчанию
равен `["title", "value"]`. Допустимые значения `fields`: `title`, `value`.
`case_sensitive` опционален и по умолчанию равен `false`.

## Map Bindings

`selector.map_bindings` выбирает узлы по привязкам к maps. Ключ задает имя map,
значение задает путь ветки внутри этой map. Значение может быть exact value или
wildcard pattern с теми же правилами `*` и `**`, что `selector.path`.

```json
{
  "map_bindings": {
    "content": "api/rule",
    "subject": "api/**"
  }
}
```

## Links

`selector.links` выбирает узлы по именованному link.

`links.name` задает имя link. Ровно одно из полей `from` или `to` задает
вторую сторону link:

- `from` выбирает target-узлы link-а, исходящего из выбранных source-узлов.
- `to` выбирает source-узлы link-а, входящего в выбранные target-узлы.

Поля `from` и `to` содержат вложенный selector object без `links`. Внешние
`map_bindings` и `match` дополнительно фильтруют узлы, выбранные через link.

```json
{
  "links": {
    "name": "depends_on",
    "to": {
      "ids": [42]
    }
  }
}
```

## Selector Slots

Selector slot - поле операции, принимающее inline selector object или alias,
объявленный ранее через `as`. Alias передается строкой с именем alias.

Selector slots в v1:

- `select.select` - selector внутри операции `select` для чтения или объявления alias.
- `move.source` - selector переносимых узлов.
- `link.source`, `link.target`, `unlink.source`, `unlink.target` - selector
  сторон link-а.
- `selector.links.from` и `selector.links.to` - вложенные selectors второй
  стороны link-фильтра.

Selector slots не смешивают selector с прямыми `ids`/`path` на уровне операции:
`ids`, `path`, `map_bindings`, `links` и `match` всегда находятся внутри
selector object. Операции с прямыми id описаны отдельно в write-ops.

## `expected_count`

`expected_count` обязателен для `move`, когда `move.source` резолвится больше
чем в один узел. Для `link` и `unlink` `expected_count` обязателен, когда
`source` x `target` дает больше одной link-пары.

Если фактическое число переносимых узлов или link-пар отличается от
`expected_count`, возвращается `count_mismatch`.

## `ids`

В `selector.ids` поле `ids` выбирает узлы по явному списку id.

В write-операциях `ids` используется внутри selector slot для `move`, `link` и
`unlink`, например `move.source.ids` или `link.source.ids`. Прямой список
удаления задается как `delete.ids`.

Kernel проверяет, что все ids существуют, не повторяются и соответствуют
constraints операции.

Для `move.source.ids` массив может содержать один или несколько узлов. Если
элементов больше одного, `expected_count` обязателен и должен быть равен
`ids.length`.

В `link.source`, `link.target`, `unlink.source` и `unlink.target` поле `ids`
может содержать один или несколько узлов; `expected_count` проверяет число
итоговых link-пар.

## Alias

Порядок `ops[]` значим. Alias, объявленный через `as`, доступен только
последующим операциям.

В v1 `as` разрешен у `select` и `create`. Ссылки на alias, объявленный позже,
являются ошибкой `unknown_alias`.
