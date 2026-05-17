# Scope `hist`

`hist` — kernel-журнал всех изменений editable scope (`main`, `usage`,
`scheme`). Запись производит kernel при успешной `tx`. `tx scope=hist`
возвращает `hist_read_only`.

`read scope=hist` не принимает `at` (см. [model.md](model.md), раздел
«Темпоральные чтения (`at`)»): hist уже хронологический журнал, и
фильтрация по моменту времени делается через селектор `date` или `id`.
Передача `at` возвращает `at_not_applicable` с
`details.reason = "hist_scope"`.

Hist — pure event log из event-узлов. Каждый успешный `tx` создаёт
ровно один event-узел `hist/transaction`, который описывает шапку
транзакции и три секции изменений (`created`, `changed`, `deleted`).
Никаких других типов узлов и никаких link с identity в hist нет.

Полное состояние графа в произвольный момент восстанавливается
replay'ем всех `hist/transaction`-узлов в хронологическом порядке.

## Функции

- Аудит: «когда создан этот узел / эта связь, когда изменён, когда
  удалён» — селектор `touches_node` / `touches_link` по hist scope.
- Rollback: kernel читает секции исходной транзакции, вычисляет
  inverse-операции, применяет компенсирующую tx (см. [tx.md](tx.md)).
- Restoration: полное состояние main / usage / scheme пересобирается
  из hist replay'ем секций каждого `hist/transaction`.

## Контракт event-узла

Event-узел подчиняется отдельной части meta-schema (см.
[model.md](model.md), раздел «Поля event-узла»). У него нет `path`,
`content`, `map_bindings`. Идентификация — только по `id`. Иерархии под
hist нет.

```json
{
  "id": "f4",
  "title": "добавить раздел selectors и связать с write-ops",
  "date": "2026-05-14",
  "description": "Раздел selectors появился как следствие правки api/. Связан с write-ops через depends_on.",
  "rollback_of": "8b20",
  "created": {
    "nodes": [
      {
        "id": "c8",
        "path": "DocsWalker/api/selectors",
        "title": "selectors",
        "content": "...",
        "map_bindings": {
          "category": "documents/spec"
        }
      }
    ],
    "links": [
      { "name": "depends_on", "from": "c8", "to": "11" }
    ]
  },
  "changed": {
    "nodes": [
      { "id": "2a", "set": { "content": "..." } }
    ]
  },
  "deleted": {
    "nodes": [ { "id": "5a" } ],
    "links": [
      { "name": "old_link", "from": "...", "to": "..." }
    ]
  }
}
```

Поля top-level:

- `id` — opaque hex-id event-узла. Одновременно играет роль `tx_id`:
  именно этим значением другие транзакции ссылаются на эту через
  `rollback_of`, и именно его возвращает `tx` в результате как `id`.
- `title` — свободный текст commit-сообщения (≤ 100 токенов).
- `date` — ISO-8601 UTC дата применения tx.
- `description` — опциональный длинный текст с подробностями.
- `rollback_of` — опциональная строка-id исходной транзакции, если
  этот узел — kernel-генерируемая компенсирующая rollback-tx. У обычных
  LLM-tx поле отсутствует.
- `created`, `changed`, `deleted` — три секции; см. ниже.

Любая из трёх секций может отсутствовать или быть пустым объектом, если
данных нет. Подсекции (`nodes`, `links`) внутри секций тоже опциональны.

## Секция `created`

Объекты, появившиеся в этой транзакции.

```json
"created": {
  "nodes": [
    {
      "id": "c8",
      "path": "DocsWalker/api/selectors",
      "title": "selectors",
      "content": "...",
      "map_bindings": { "category": "documents/spec" }
    }
  ],
  "links": [
    { "name": "depends_on", "from": "c8", "to": "11" }
  ]
}
```

- `created.nodes[]` — каждый элемент содержит **полный post-state**
  созданного data-узла (id + все обязательные поля по схеме его
  scope-а). Используется replay для точного воссоздания.
- `created.links[]` — identity созданных links: `name`, `from`
  (строка-id), `to` (строка-id). Других полей у link нет.

## Секция `changed`

Изменения существующих data-узлов (links не меняются — они либо есть,
либо нет, поэтому `changed.links` физически не бывает).

```json
"changed": {
  "nodes": [
    { "id": "2a", "set": { "content": "..." } },
    { "id": "2b", "set": { "path": "DocsWalker/api/new-parent/child1" } }
  ]
}
```

- `id` — id затронутого узла.
- `set` — те же ключи, что и в `tx.update.set` / `tx.move.to`
  (поля `title`, `content`, `path`, `map_bindings`). Заполнены **только
  те, что фактически поменялись** (forward-only diff, без prior
  значений).
  - Для скалярных полей (`title`, `content`, `path`) replay применяет
    `set.<field>` как полную замену поля.
  - Для `map_bindings` действует partial-семантика: ключ → ветка
    устанавливает/перезаписывает привязку узла к этой map; ключ → `null`
    снимает привязку; не упомянутые ключи не трогаются. Пустого объекта
    `set.map_bindings = {}` в журнале не бывает (no-op в `move.to` в
    `changed.nodes` не пишется).

Move-эффект на path-потомков: если транзакция переименовала родителя,
то у каждого затронутого потомка path меняется, и каждый такой потомок
получает **отдельный** элемент в `changed.nodes` с `set.path = <новый>`.
Один move узла с N потомками → N+1 элементов в `changed.nodes` (сам
переносимый узел + N потомков).

## Секция `deleted`

Удалённые объекты — только identity, без post-state.

```json
"deleted": {
  "nodes": [ { "id": "5a" } ],
  "links": [
    { "name": "old_link", "from": "...", "to": "..." }
  ]
}
```

- `deleted.nodes[]` — `{ id }`. State на момент удаления (если нужен
  для rollback) восстанавливается через предыдущую транзакцию, в
  которой этот id фигурировал в `created.nodes` или `changed.nodes`.
- `deleted.links[]` — identity удалённых links (по аналогии с
  `created.links`).

## Селекторы hist

`read scope=hist` поддерживает hist-специфичный набор предикатов:

- `id` — точное равенство или массив id event-узлов.
- `title` — selector.match.regex по полю title (свободный текст).
- `date` — точное равенство, либо regex через `match.fields=["date"]`.
- `description` — regex match.
- `rollback_of` — точная строка-id исходной транзакции (находит
  компенсирующие rollback-tx для заданной).
- `tx_scope` — virtual-предикат, kernel вычисляет принадлежность tx к
  scope (`main` / `usage` / `scheme`) из содержимого её секций.
- `touches_node` — `"id-строка"` либо `{ "id": "..." }`. Находит все
  транзакции, в которых указанный data-узел фигурирует в любой из
  секций (`created`/`changed`/`deleted`).
- `touches_link` — `{ "name": "...", "from": "<id>", "to": "<id>" }`.
  Находит транзакции, тронувшие указанный link.

Селекторы `path`, `content`, `map_bindings`, `links` в hist неприменимы —
этих полей у event-узлов нет. Подробности по селекторам — в
[selectors.md](selectors.md).

## Чтение hist

«Все изменения узла `2a`»:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": { "touches_node": "2a" },
        "include": ["created", "changed", "deleted"],
        "max_tokens": 4000
      }
    }
  ]
}
```

«История link `(depends_on, 2a, 11)`»:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "touches_link": {
            "name": "depends_on",
            "from": "2a",
            "to": "11"
          }
        },
        "include": ["created", "deleted"],
        "max_tokens": 2000
      }
    }
  ]
}
```

«Все scheme-транзакции этой недели»:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "tx_scope": "scheme",
          "date": { "match": { "regex": "^2026-05-1[0-7]$" } }
        },
        "include": ["created", "changed", "deleted"],
        "max_tokens": 4000
      }
    }
  ]
}
```

«Найти rollback-tx для заданной исходной`»:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": { "rollback_of": "a3f1c2" },
        "include": ["created", "changed", "deleted"]
      }
    }
  ]
}
```

## Compact-форма event-узла

При отсутствии `include` `read scope=hist` возвращает compact-форму
event-узла:

```json
{
  "id": "f4",
  "title": "...",
  "date": "2026-05-14",
  "rollback_of": "8b20",
  "counts": {
    "created": { "nodes": 1, "links": 1 },
    "changed": { "nodes": 1 },
    "deleted": { "nodes": 1, "links": 1 }
  },
  "tokens": 1200
}
```

`counts` — счётчики элементов в каждой подсекции. Подсекции с нулём
опускаются. `tokens` — оценка стоимости полной формы (со всеми loadable
полями). Loadable поля event-узла (`description`, `created`, `changed`,
`deleted`) в compact-форму не входят и запрашиваются через `include`.
Event-узлы поля `version` не имеют — hist append-only, concurrency-
precondition для них не нужен; `rollback` принимает id event-узла
напрямую.

## Replay restoration

Полное состояние main / usage / scheme на любой момент
восстанавливается:

1. Берётся пустой scope.
2. По хронологическому порядку (`date` + порядок записи в пределах
   одной даты) применяются все `hist/transaction`-узлы, относящиеся к
   соответствующему scope (kernel определяет `tx_scope` по содержимому
   секций). id event-узлов в сортировке не участвуют — они opaque
   переменной длины и лексикографически не упорядочены по времени
   выпуска.
3. Внутри одной транзакции изменения применяются в фиксированном
   порядке:
   1. `deleted.links` — удалить tuples.
   2. `deleted.nodes` — удалить узлы.
   3. `changed.nodes` — применить `set` к каждому узлу: для скалярных
      полей (`title`, `content`, `path`) — полная замена поля; для
      `map_bindings` — partial-merge (ключ → ветка: установить или
      перезаписать; ключ → `null`: снять; не упомянутые ключи без
      изменений).
   4. `created.nodes` — создать узлы с указанным `id` и состоянием.
   5. `created.links` — создать tuples.

Этот порядок гарантирует, что path-конфликты внутри одной tx (когда
удаляется узел `a/x` и тут же создаётся новый узел с тем же path)
разрешаются корректно: сначала освобождается старый адрес, потом
занимается новый.

Replay гарантирует консистентность: id-пространство восстанавливается
точно (включая удалённые id, поскольку каждый created-узел приходит с
конкретным id из исходной транзакции); порядок tx сохраняется;
scheme-changes применяются раньше зависящих от них data-changes
(потому что в hist они идут в исходном хронологическом порядке).

## Реконструкция значения поля

Kernel умеет восстановить любое значение любого поля любого data-узла
на любой момент времени, не храня никаких дополнительных snapshot'ов:
вся информация уже лежит в `hist`.

### Скалярные поля (`title`, `content`, `path`)

Алгоритм для скалярного поля `<id>.<field>` на момент перед заданной
транзакцией tx_X:

1. Идти по `hist/transaction`-узлам в обратном хронологическом порядке,
   начиная с tx непосредственно перед tx_X.
2. Вернуть значение из первой же tx, где `<id>` фигурирует с `<field>`:
   - в `created.nodes[<id>].<field>`,
   - либо в `changed.nodes[<id>].set.<field>`.
3. Если такой tx нет — поле на тот момент не было задано (узел ещё не
   существовал или поле было пустым по схеме).

### Поле `map_bindings`

`map_bindings` хранится в hist как partial-diff с null-tombstone, поэтому
для него нужен накопительный проход от snapshot'а вперёд.

Алгоритм для `<id>.map_bindings` на момент перед tx_X:

1. Идти по `hist/transaction`-узлам в обратном хронологическом порядке,
   начиная с tx непосредственно перед tx_X. Найти ближайший
   `created.nodes[<id>]` — это базовый snapshot (полный
   `map_bindings`-словарь на момент создания узла; если поле в snapshot'е
   отсутствует — base = `{}`).
2. Собрать все `changed.nodes[<id>].set.map_bindings` из tx, лежащих
   между snapshot'ом и tx_X (не включая tx_X).
3. Накатить собранные diff'ы в **прямом** хронологическом порядке поверх
   base: для каждого ключа diff'а — если значение `null`, удалить ключ
   из аккумулятора; иначе записать (создать или перезаписать).
4. Если ближайшего `created.nodes[<id>]` нет (узел не создавался до
   tx_X) — `map_bindings` на тот момент не существовал.

### Полный snapshot узла

Полный snapshot data-узла на момент перед tx_X собирается применением
обоих алгоритмов к полям контракта data-узла: скалярные (`title`,
`content`, `path`) — алгоритмом для скалярных полей; `map_bindings` —
отдельным накопительным алгоритмом.

На этом механизме построены:

- rollback (см. ниже и [tx.md](tx.md), раздел `rollback`);
- точечные запросы об истории конкретного поля / узла из hist.

Replay restoration (см. выше) — это применение того же принципа целиком
к графу: forward-проход от первой tx, накапливающий состояние всех
узлов. Реконструкция значения поля — обратный точечный запрос по
одному `(id, field)`.

## Rollback и hist

При rollback (см. [tx.md](tx.md)):

- kernel читает секции исходной транзакции по её `id`;
- для каждого элемента секций вычисляет inverse-операцию:
  - элемент из `created.nodes` → inverse `delete` этого узла;
  - элемент из `created.links` → inverse `unlink`;
  - элемент из `changed.nodes` → inverse `update` с возвратом полей к
    их значениям непосредственно перед откатываемой tx (реконструкция
    per-field — см. раздел «Реконструкция значения поля» выше);
  - элемент из `deleted.nodes` → inverse `create` с восстановлением
    исходного `id` и полного snapshot-а узла на момент перед
    откатываемой tx (тот же механизм реконструкции, применённый ко
    всем полям контракта);
  - элемент из `deleted.links` → inverse `link`;
- inverse-ops применяются как обычная транзакция с собственным `id`
  (новым); этот новый event-узел получает поле
  `rollback_of: "<исходный id>"`;
- `title` компенсирующей tx — обычный обязательный `title` запроса
  `tx`, LLM передаёт его свободным текстом. Kernel этот title не
  подменяет; связь с исходной транзакцией фиксируется отдельным полем
  `rollback_of`. См. [tx.md](tx.md) для деталей.

Поиск rollback-tx для заданной исходной — селектор `rollback_of` (см.
выше).

## Атомарность hist-write

Hist-write атомарен с применением tx: создание event-узла происходит
одновременно с применением изменений. При падении записи event-узла
kernel откатывает применённые изменения и возвращает
`hist_write_failed`.

## Hist-schema

Hist-schema — раздел meta-schema (`docs/.docswalker/meta-schema.json`),
описывает контракт event-узла:

- top-level поля event-узла (`id`, `title`, `date`, `description?`,
  `rollback_of?`, `created?`, `changed?`, `deleted?`);
- структуру `created.nodes[]`, `created.links[]`, `changed.nodes[]`,
  `deleted.nodes[]`, `deleted.links[]`;
- first-class селекторные предикаты (`touches_node`, `touches_link`,
  `tx_scope`, `rollback_of`).

Hist-schema редактируется только kernel-ом, версионируется вместе с
релизами DocsWalker.
