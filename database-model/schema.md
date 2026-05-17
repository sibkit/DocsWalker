# Schema

DDL для SQLite-storage DocsWalker. Все DDL даны как `CREATE TABLE` /
`CREATE INDEX`, готовые к выполнению. Версия SQLite — 3.38+.

## Общие правила

- Каждая таблица содержит `graph_name TEXT NOT NULL` первой колонкой
  составного PK / индекса (для изоляции graphs в одном DB).
- Все id узлов и event-узлов — opaque hex-строки lower-case (`TEXT`).
- FK c `ON DELETE CASCADE` — для индексных таблиц (`node_map_binding`,
  `tx_touches_node`, `tx_touches_link`), чтобы каскадно убирать
  индексные строки при удалении родителя.
- FK c `ON DELETE RESTRICT` (default) — для `link` на `node`; инвариант
  поддерживается kernel-ом (incident links удаляются раньше узла).
- Datetime — ISO-8601 UTC `TEXT`.
- **`read` оборачивается в одну SQLite read-транзакцию** (`BEGIN
  DEFERRED ... COMMIT`), чтобы все `ops[]` запроса видели единый
  snapshot состояния. WAL обеспечивает это без блокировки параллельных
  writer-ов.
- **`tx` оборачивается в одну SQLite write-транзакцию** (`BEGIN
  IMMEDIATE ... COMMIT`) — резолв, concurrency-check, DML на
  `node`/`link`/`node_map_binding` и запись `tx_event` +
  `tx_touches_*` происходят атомарно. Любая ошибка → `ROLLBACK`.

## `graph`

Реестр graphs внутри одного DB.

```sql
CREATE TABLE graph (
  name TEXT PRIMARY KEY
);
```

Заполняется при старте kernel-а по списку из `kernel-config.json`. На
любое появление нового graph_name kernel сначала вставляет строку
сюда, потом начинает работать с его таблицами.

## `sequence`

Per-graph счётчик id. На запрос нового id kernel инкрементирует
`next_id` и форматирует в hex lower-case (per
[../api/model.md](../api/model.md), раздел «Глобальный id»).

```sql
CREATE TABLE sequence (
  graph_name TEXT PRIMARY KEY,
  next_id INTEGER NOT NULL,
  FOREIGN KEY (graph_name) REFERENCES graph(name)
);
```

Атомарная выдача id внутри транзакции:

```sql
UPDATE sequence SET next_id = next_id + 1
WHERE graph_name = ?
RETURNING next_id - 1 AS issued_id;
```

Затем `printf('%x', issued_id)` → opaque hex.

## `node`

Data-узлы (scope `main`, `usage`, `scheme`). Event-узлы (scope `hist`)
— отдельная таблица `tx_event`.

```sql
CREATE TABLE node (
  graph_name TEXT NOT NULL,
  id         TEXT NOT NULL,
  scope      TEXT NOT NULL CHECK (scope IN ('main', 'usage', 'scheme')),
  path       TEXT NOT NULL,
  title      TEXT NOT NULL,
  content    TEXT NOT NULL DEFAULT '',
  version    INTEGER NOT NULL DEFAULT 1,
  PRIMARY KEY (graph_name, id),
  FOREIGN KEY (graph_name) REFERENCES graph(name)
);

CREATE INDEX node_path
  ON node (graph_name, scope, path);

CREATE UNIQUE INDEX node_path_lower
  ON node (graph_name, scope, LOWER(path));

CREATE INDEX node_scope
  ON node (graph_name, scope);
```

- `id` — opaque hex (per API). PK `(graph_name, id)` гарантирует
  глобальную уникальность id внутри графа.
- `scope` — где живёт узел. `hist` исключён (event-узлы в `tx_event`).
- `path` — иерархический адрес. Уникальность siblings проверяется по
  lower-case форме (per ../api/model.md) → expression index
  `node_path_lower`. Оригинальный регистр сохраняется в колонке.
- `title` — последний сегмент `path`, денормализован для быстрого
  доступа без парсинга `path`.
- `content` — строка (per ../api/model.md). `''` если поле логически
  не задано (избегаем NULL для упрощения LIKE/regex).
- `version` — монотонный счётчик, инкрементируется на каждое
  state-изменение самого узла (изменение `path`, `title`, `content`,
  `map_bindings`; для path-потомков move/rename — тоже +1). Стартует
  с `1` на `create`. Создание/удаление incident links endpoint-узлов
  `version` НЕ бьёт — incident links в state узла не входят. Колонка
  возвращается LLM как поле `version` в ответе `read` (per
  [../api/read.md](../api/read.md), раздел `version`) и сверяется с
  `tx.update.expected_version` — при расхождении kernel возвращает
  `version_mismatch`.

Индексы:
- `node_path` (non-unique) — prefix-сканы для селектора `path:
  "DocsWalker/api/**"` через `LIKE 'DocsWalker/api/%'`.
- `node_path_lower` (unique) — полная path-уникальность по lower-case
  (per ../api/model.md, раздел «Поля data-узла», описание `title`):
  `A/B/c` и `a/b/c` считаются одним адресом и не могут сосуществовать.
- `node_scope` — селектор по scope без path.

## `node_map_binding`

Индексируемые `map_bindings` узла. Источник истины для маппинга
`{map_name → branch_path}`.

```sql
CREATE TABLE node_map_binding (
  graph_name  TEXT NOT NULL,
  node_id     TEXT NOT NULL,
  map_name    TEXT NOT NULL,
  branch_path TEXT NOT NULL,
  PRIMARY KEY (graph_name, node_id, map_name),
  FOREIGN KEY (graph_name, node_id)
    REFERENCES node(graph_name, id) ON DELETE CASCADE
);

CREATE INDEX node_map_binding_by_map
  ON node_map_binding (graph_name, map_name, branch_path);
```

- PK `(graph_name, node_id, map_name)` — один узел имеет максимум
  одну привязку к одной map (per ../api/model.md).
- `node_map_binding_by_map` — селектор `map_bindings: { category:
  "documents/spec" }` через `WHERE map_name='category' AND
  branch_path = 'documents/spec'` (или `LIKE` для wildcard
  `'documents/%'`).
- `ON DELETE CASCADE` — при удалении узла bindings снимаются.

## `link`

Data-scope links. Tuple identity `(name, from_id, to_id)`, глобально
уникален в пределах графа.

```sql
CREATE TABLE link (
  graph_name TEXT NOT NULL,
  name       TEXT NOT NULL,
  from_id    TEXT NOT NULL,
  to_id      TEXT NOT NULL,
  PRIMARY KEY (graph_name, name, from_id, to_id),
  FOREIGN KEY (graph_name, from_id)
    REFERENCES node(graph_name, id),
  FOREIGN KEY (graph_name, to_id)
    REFERENCES node(graph_name, id)
);

CREATE INDEX link_by_from ON link (graph_name, from_id, name);
CREATE INDEX link_by_to   ON link (graph_name, to_id,   name);
```

- PK — tuple. `already_exists` (per ../api/errors.md) при повторной
  вставке — нативный SQL conflict.
- `link_by_from` / `link_by_to` — обход incident links для `include:
  ["links"]` и для селектора `links: { name, to: {...} }`.
- Cross-scope правила (main→main, usage→usage, usage→main) — kernel
  валидирует перед insert через JOIN с node для определения scope
  endpoint-ов. В DB не enforced.

## `tx_event`

Hist event-узлы (scope `hist`). Каждое успешное `tx` создаёт ровно
один event-узел `hist/transaction`.

```sql
CREATE TABLE tx_event (
  graph_name    TEXT NOT NULL,
  id            TEXT NOT NULL,
  title         TEXT NOT NULL,
  date          TEXT NOT NULL,
  description   TEXT,
  rollback_of   TEXT,
  tx_scope      TEXT NOT NULL CHECK (tx_scope IN ('main', 'usage', 'scheme')),
  ordinal       INTEGER NOT NULL,
  sections_json TEXT NOT NULL,
  PRIMARY KEY (graph_name, id),
  FOREIGN KEY (graph_name) REFERENCES graph(name),
  FOREIGN KEY (graph_name, rollback_of)
    REFERENCES tx_event(graph_name, id)
);

CREATE INDEX tx_event_date
  ON tx_event (graph_name, date, ordinal);

CREATE INDEX tx_event_rollback_of
  ON tx_event (graph_name, rollback_of);

CREATE INDEX tx_event_tx_scope
  ON tx_event (graph_name, tx_scope);

CREATE UNIQUE INDEX tx_event_date_ordinal
  ON tx_event (graph_name, date, ordinal);
```

- `id` — opaque hex из того же sequence, что и data-узлы (per
  ../api/model.md). Одновременно tx_id.
- `title` — свободный текст ≤ 100 токенов (kernel валидирует).
- `date` — ISO-8601 UTC.
- `description` — опциональный длинный текст.
- `rollback_of` — nullable; если задан, этот event — kernel-генерируемая
  компенсирующая rollback-tx; ссылается на исходный `tx_event.id`.
- `tx_scope` — virtual поле per ../api/hist-scope.md, вычисляется
  kernel-ом из содержимого секций. Денормализован для индекса.
- `ordinal` — порядковый номер внутри одной даты для устойчивой
  хронологии. Kernel генерирует как `max(ordinal)+1` по этой дате.
- `sections_json` — целые секции `created` / `changed` / `deleted` в
  JSON (per ../api/hist-scope.md). Источник истины для replay и
  rollback.

Индексы:
- `tx_event_date` — `read scope=hist` с regex/exact по дате.
- `tx_event_rollback_of` — селектор `rollback_of: "<id>"`.
- `tx_event_tx_scope` — селектор `tx_scope: "main"`.
- `tx_event_date_ordinal` (unique) — устойчивая хронология: для одной
  даты ordinal'ы уникальны, повторная выдача того же `(date, ordinal)`
  ловится как SQL-конфликт даже при гипотетической ошибке kernel-side
  генерации.

## `tx_touches_node`

Денормализованный индекс «какие узлы тронуты в какой tx». Заполняется
kernel-ом при insert event-а на основе содержимого секций. Поддерживает
селектор `touches_node`.

```sql
CREATE TABLE tx_touches_node (
  graph_name TEXT NOT NULL,
  tx_id      TEXT NOT NULL,
  node_id    TEXT NOT NULL,
  role       TEXT NOT NULL CHECK (role IN ('created', 'changed', 'deleted')),
  PRIMARY KEY (graph_name, tx_id, node_id, role),
  FOREIGN KEY (graph_name, tx_id)
    REFERENCES tx_event(graph_name, id) ON DELETE CASCADE
);

CREATE INDEX tx_touches_node_by_node
  ON tx_touches_node (graph_name, node_id);
```

- `role` — в какой секции узел встречается.
- `tx_touches_node_by_node` — селектор `touches_node: "<id>"`
  возвращает все tx, в которых узел фигурирует в любой роли.

## `tx_touches_link`

Денормализованный индекс «какие links тронуты в какой tx». Для
селектора `touches_link`.

```sql
CREATE TABLE tx_touches_link (
  graph_name TEXT NOT NULL,
  tx_id      TEXT NOT NULL,
  link_name  TEXT NOT NULL,
  from_id    TEXT NOT NULL,
  to_id      TEXT NOT NULL,
  role       TEXT NOT NULL CHECK (role IN ('created', 'deleted')),
  PRIMARY KEY (graph_name, tx_id, link_name, from_id, to_id, role),
  FOREIGN KEY (graph_name, tx_id)
    REFERENCES tx_event(graph_name, id) ON DELETE CASCADE
);

CREATE INDEX tx_touches_link_by_link
  ON tx_touches_link (graph_name, link_name, from_id, to_id);
```

- `role` — `created` или `deleted` (links не имеют `changed`).
- Индекс — для `touches_link: { name, from, to }` через exact match.

## Optimistic concurrency (`node.version`)

Защита от lost update — только через числовое сравнение колонки
`node.version` с `tx.update.expected_version`. Никаких receipts, HMAC
и секретов.

### Валидация в `tx.update`

1. `SELECT version FROM node WHERE graph_name = ? AND id = ?` →
   `current_version`. Не нашёл узел → `not_found` с
   `details.path = "$.ops[i].update.id"`.
2. Если `current_version != expected_version` → `version_mismatch`
   с `details: { id, expected, current }` (per
   [../api/errors.md](../api/errors.md), раздел `version_mismatch`).
3. Иначе применяем `update` (см. [hist.md](hist.md), раздел «Анатомия
   успешной `tx`»).

Для `move`, `delete`, `link`, `unlink` отдельной concurrency-проверки
по `version` нет: bulk-операции защищены `expected_count` (per
[../api/selectors.md](../api/selectors.md), раздел «Counts»). Изменение
конкретного узла, попавшего в bulk-набор, в параллельной tx между
read и write не отлавливается этим механизмом — отлов происходит на
следующей правке узла через его собственный `version_mismatch`.

## Маппинг селекторов в SQL

Селектор API (per [../api/selectors.md](../api/selectors.md)) → конкретный SQL.

### `selector.id`

```sql
WHERE node.id = ?
-- или
WHERE node.id IN (?, ?, ?)
```

### `selector.path` exact

```sql
WHERE node.path = ?
```

### `selector.path` pattern с `*` / `**`

```sql
-- "DocsWalker/api/**"
WHERE node.path LIKE 'DocsWalker/api/%' ESCAPE '\'

-- "DocsWalker/api/*" — ровно один сегмент после
WHERE node.path LIKE 'DocsWalker/api/%' ESCAPE '\'
  AND instr(substr(node.path, length('DocsWalker/api/') + 1), '/') = 0
```

Kernel компилирует pattern в LIKE-строку (escape `%`, `_`, `\`).
`**` → чистый prefix. `*` → prefix + post-filter по отсутствию `/`
в хвосте.

### `selector.title` exact

```sql
WHERE LOWER(node.title) = LOWER(?)
```

Sibling-уникальность по lower-case.

### `selector.map_bindings: { category: "documents/spec" }`

```sql
WHERE EXISTS (
  SELECT 1 FROM node_map_binding b
  WHERE b.graph_name = node.graph_name
    AND b.node_id    = node.id
    AND b.map_name    = 'category'
    AND b.branch_path = 'documents/spec'
)
```

Wildcards в `branch_path` (`documents/**`) — `LIKE 'documents/%'`.

### `selector.links: { name, to: {...} }`

```sql
WHERE EXISTS (
  SELECT 1 FROM link l
  JOIN node target
    ON target.graph_name = l.graph_name AND target.id = l.to_id
  WHERE l.graph_name = node.graph_name
    AND l.from_id    = node.id
    AND l.name       = 'depends_on'
    AND <вложенный селектор по target>
)
```

Симметрично для `from`. Один уровень вложения (per ../api/selectors.md).

### `selector.match.regex` по `content` / `title`

```sql
WHERE regex_match(node.content, ?, ?case_sensitive?)
```

`regex_match(text, pattern, case_sensitive)` — кастомный SQLite
function, регистрируется kernel-ом (.NET `Regex` с bounded timeout).
Timeout → `match_timeout`.

### `selector.touches_node`

```sql
WHERE tx_event.id IN (
  SELECT tx_id FROM tx_touches_node
  WHERE graph_name = ? AND node_id = ?
)
```

### `selector.touches_link`

```sql
WHERE tx_event.id IN (
  SELECT tx_id FROM tx_touches_link
  WHERE graph_name = ?
    AND link_name  = ?
    AND from_id    = ?
    AND to_id      = ?
)
```

### `selector.tx_scope`, `selector.date`, `selector.rollback_of`

```sql
WHERE tx_event.tx_scope    = ?
WHERE tx_event.date        = ?      -- exact
WHERE regex_match(tx_event.date, ?, FALSE)   -- regex
WHERE tx_event.rollback_of = ?
```

## Compact-форма выдачи

`read` без `include` возвращает поля per [../api/read.md](../api/read.md)
(id, scope, path, title, map_bindings, tokens, version).

```sql
SELECT
  n.id,
  n.scope,
  n.path,
  n.title,
  (SELECT json_group_object(b.map_name, b.branch_path)
     FROM node_map_binding b
     WHERE b.graph_name = n.graph_name AND b.node_id = n.id) AS map_bindings_json,
  n.version
FROM node n
WHERE <селектор>;
```

`tokens` оценивается kernel-ом по конкретному tokenizer-у (не
SQL-функция); входной сигнал — `length(content)` или прочитанный
`content`. `version` отдаётся LLM как есть из колонки `n.version` и
используется в `tx.update.expected_version`.

## Full-форма `include: ["content"]`

Добавить `n.content` в SELECT.

## Full-форма `include: ["links"]`

```sql
-- Outgoing
SELECT 'out' AS dir, l.name, l.to_id AS endpoint_id,
       target.path AS endpoint_path
FROM link l
JOIN node target
  ON target.graph_name = l.graph_name AND target.id = l.to_id
WHERE l.graph_name = ? AND l.from_id = ?

UNION ALL

-- Incoming
SELECT 'in' AS dir, l.name, l.from_id AS endpoint_id,
       source.path AS endpoint_path
FROM link l
JOIN node source
  ON source.graph_name = l.graph_name AND source.id = l.from_id
WHERE l.graph_name = ? AND l.to_id = ?;
```

Kernel формирует response: `{ name, to: {id, path} }` для `dir='out'`,
`{ name, from: {id, path} }` для `dir='in'` (per ../api/read.md).

## Event-узел compact-форма

`read scope=hist` без `include`:

```sql
SELECT
  e.id, e.title, e.date, e.rollback_of,
  -- counts собираются JSON-агрегацией из sections_json:
  json_object(
    'created', json_object(
      'nodes', json_array_length(json_extract(e.sections_json, '$.created.nodes')),
      'links', json_array_length(json_extract(e.sections_json, '$.created.links'))
    ),
    'changed', json_object(
      'nodes', json_array_length(json_extract(e.sections_json, '$.changed.nodes'))
    ),
    'deleted', json_object(
      'nodes', json_array_length(json_extract(e.sections_json, '$.deleted.nodes')),
      'links', json_array_length(json_extract(e.sections_json, '$.deleted.links'))
    )
  ) AS counts_json
FROM tx_event e
WHERE <селектор>;
```

`json_array_length` возвращает `0` или `NULL` для отсутствующих
секций; kernel-side обрезает подсекции с нулём (per ../api/read.md).
`tokens` оценивает kernel.

## Event-узел full-форма

`include: ["created", "changed", "deleted"]`:

```sql
SELECT e.id, e.title, e.date, e.description, e.rollback_of,
       e.sections_json
FROM tx_event e
WHERE <селектор>;
```

Kernel парсит `sections_json` и возвращает запрошенные секции
(всё / часть, per `include`).

## Темпоральные чтения (`at`)

При `read` с непустым `at` (per [../api/model.md](../api/model.md),
раздел «Темпоральные чтения (`at`)») kernel реконструирует состояние
scope в указанной точке и применяет к нему селектор. Алгоритм v1 —
replay из hist на каждый at-запрос, без снэпшотов.

### Резолв `at` в момент времени

`at` принимает строку-`tx_id` или объект `{ "before": tx_id }`. Kernel
ищет указанную tx:

```sql
SELECT date, ordinal FROM tx_event
WHERE graph_name = ? AND id = ?_at_tx_id;
```

Не нашёл → `not_found` с `details.path = "$.at"`.

Полученная пара `(date, ordinal)` плюс inclusive-флаг задаёт upper
bound для replay:

- short form (`at: "<tx_id>"`) → upper bound включает указанную tx.
- before form (`at: { "before": "<tx_id>" }`) → upper bound исключает
  указанную tx.

### Реконструкция scope (полный replay)

Используется когда селектор не сводится к exact-id-match (поиск по
path, map_bindings, links, match.regex и т.п.).

1. Выбрать все `hist/transaction` до upper bound:

   ```sql
   SELECT id, sections_json, date, ordinal
   FROM tx_event
   WHERE graph_name = ?
     AND (date < ?_bound_date
          OR (date = ?_bound_date AND ordinal <= ?_bound_ordinal))
   ORDER BY date, ordinal;
   ```

   (`<` для before-формы, `<=` для after-формы — отражает inclusive
   bit `at`.)

2. Прогнать секции каждой tx по фиксированному порядку (replay из
   [hist.md](hist.md), раздел «Replay») против in-memory структур
   `{node_id → {scope, path, title, content, map_bindings}}` и
   `{(name, from_id, to_id) → exists}`. Фильтрация по `scope` запроса
   делается на уровне созданных/изменённых/удалённых узлов: остаются
   только те, чей scope соответствует запросу.

3. Применить селектор запроса к реконструированному состоянию
   in-memory.

4. Вернуть отфильтрованные узлы. Поле `version` в ответе **не
   добавляется** (per [../api/model.md](../api/model.md), раздел
   «Темпоральные чтения»). `tokens` оценивается kernel-ом по
   реконструированному `content`.

### Точечная реконструкция (оптимизация для одиночного id)

Если селектор — exact `id` или `ids`, kernel переиспользует алгоритм
реконструкции поля из [hist.md](hist.md), раздел «Реконструкция
значения поля», и обходит полный replay:

1. Для каждого `id` через `tx_touches_node` достать tx, тронувшие
   узел до upper bound:

   ```sql
   SELECT e.id, e.sections_json, e.date, e.ordinal
   FROM tx_event e
   JOIN tx_touches_node t
     ON t.graph_name = e.graph_name AND t.tx_id = e.id
   WHERE e.graph_name = ?
     AND t.node_id    = ?_id
     AND (e.date < ?_bound_date
          OR (e.date = ?_bound_date AND e.ordinal <= ?_bound_ordinal))
   ORDER BY e.date, e.ordinal;
   ```

2. Скаляры (`title`, `content`, `path`) — вернуть последнее значение
   в `created.nodes` / `changed.nodes.set` (обратный проход).

3. `map_bindings` — найти ближайший `created.nodes[<id>]`, накатить все
   промежуточные `changed.nodes[<id>].set.map_bindings` в прямом
   порядке (snapshot + diff'ы; `null` снимает ключ).

4. Если ближайший `created.nodes[<id>]` не найден до upper bound — узел
   в этой точке не существовал, kernel возвращает пустой результат
   (без `not_found` — это легитимный empty match селектора).

5. Incident links узла при `include: ["links"]` — фильтр из
   `tx_touches_link` тем же bound: links с `role=created` без
   последующего `role=deleted` существуют на момент `at`.

### Ограничения и инварианты

- Замечание о scheme: `at` ≠ now интерпретирует селекторы по
  `map_bindings` относительно схемы на момент `at`. Ветка map, не
  существовавшая тогда — пустой результат, без `unknown_map`.
- При `at` ≠ now поле `version` не выдаётся: concurrency-precondition
  применим только к текущему состоянию.
- `at` запрещён в `scope=hist`, `tx`, `select: "meta"` —
  `at_not_applicable` соответствующего `reason`.

### Snapshots (future, v1 не реализуется)

Periodic snapshots ускоряют replay для долгих графов. Предполагаемая
схема:

```sql
CREATE TABLE scope_snapshot (
  graph_name  TEXT NOT NULL,
  scope       TEXT NOT NULL CHECK (scope IN ('main', 'usage', 'scheme')),
  at_tx_id    TEXT NOT NULL,
  at_date     TEXT NOT NULL,
  at_ordinal  INTEGER NOT NULL,
  state_json  TEXT NOT NULL,
  PRIMARY KEY (graph_name, scope, at_tx_id),
  FOREIGN KEY (graph_name, at_tx_id)
    REFERENCES tx_event(graph_name, id) ON DELETE CASCADE
);
```

Replay тогда стартует не с пустого scope, а с ближайшего snapshot до
upper bound, и накатывает только tx между snapshot и `at`. Стратегия
снэпшотирования (фиксированный интервал в N tx, после крупных миграций
schema, manual trigger) — решается отдельно. На v1 не реализуется,
описание здесь даётся для контекста проектирования.
