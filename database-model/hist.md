# Hist: tx flow, replay, rollback

Как `tx` пишет в DB, как replay восстанавливает состояние, как
rollback вычисляет inverse. Соответствует
[../api/hist-scope.md](../api/hist-scope.md) и
[../api/tx.md](../api/tx.md).

## Анатомия успешной `tx`

Kernel принимает `tx`-запрос, проверяет (resolve, `expected_version`
для `update`, schema constraints, validation — см. ../api/tx.md), и
атомарно делает внутри одной `BEGIN ... COMMIT` транзакции SQLite:

1. **Issue tx_id.** `UPDATE sequence SET next_id = next_id + 1
   RETURNING next_id - 1`, форматирование в hex.
2. **Применяет ops** — DML на `node`, `node_map_binding`, `link`.
   Каждое изменение `node.*` инкрементирует `node.version`.
3. **Строит секции `created` / `changed` / `deleted`** — собирает
   list-ы из применённых ops:
   - `created.nodes[]` — полный post-state каждого нового узла.
   - `created.links[]` — identity каждого нового link.
   - `changed.nodes[].set` — forward-only diff фактически изменённых
     полей. Для скаляров (`title`, `content`, `path`) — полная замена;
     для `map_bindings` — partial diff с `null` для снятий (per
     ../api/hist-scope.md).
   - `deleted.nodes[]` — `{ id }`.
   - `deleted.links[]` — identity удалённых links.
4. **Insert `tx_event`** с `id`, `title`, `date`, `description`,
   `rollback_of` (null для обычной), `tx_scope` (вычисленный),
   `ordinal` (`max+1` за дату), `sections_json` (JSON секций).
5. **Insert `tx_touches_node`** — по одной строке на каждый
   `(tx_id, node_id, role)` из секций.
6. **Insert `tx_touches_link`** — по одной строке на каждый
   `(tx_id, link_name, from_id, to_id, role)`.
7. **COMMIT.**

Если падает любой шаг — `ROLLBACK`, ничего не применилось, hist пуст.
При успехе возвращает `tx_event.id` LLM-у.

## Каскад на path-потомков

При `update.set.title` или `move.to.parent_path` меняется `path` самого
узла и `path` всех его path-потомков (per ../api/tx.md).

Поиск descendants:

```sql
SELECT id, path FROM node
WHERE graph_name = ?
  AND scope      = ?
  AND path LIKE (? || '/%') ESCAPE '\';
```

(`?` — старый `path` цели.) Для каждого descendant:

1. `UPDATE node SET path = ?, version = version + 1 WHERE ...` —
   новый path (старый сегмент parent заменён на новый).
2. В `sections_json.changed.nodes[]` — отдельный элемент с
   `set.path = <новый>`.
3. В `tx_touches_node` — строка `(tx_id, descendant_id, 'changed')`.

`expected_count` (per ../api/selectors.md) — считает только узлы из
selector (без descendants); descendants попадают в hist вне счёта.

## Partial-merge для `map_bindings` в `move`

Per ../api/tx.md, `move.to.map_bindings` — partial-merge:

```sql
-- ключ → ветка
INSERT INTO node_map_binding (graph_name, node_id, map_name, branch_path)
VALUES (?, ?, ?, ?)
ON CONFLICT (graph_name, node_id, map_name)
  DO UPDATE SET branch_path = excluded.branch_path;

-- ключ → null
DELETE FROM node_map_binding
WHERE graph_name = ? AND node_id = ? AND map_name = ?;

-- ключи не упомянуты: не трогаются (нет DML)
```

В `sections_json.changed.nodes[id].set.map_bindings` записывается сам
diff (с `null` для снятий) — это форма из ../api/hist-scope.md, нужна
для реконструкции и rollback. Пустой `{}` в diff — no-op, в журнал
не пишется (per ../api/hist-scope.md).

После применения diff:
- `version` узла инкрементируется (одним +1 на всю tx по этому узлу,
  не по каждому изменению поля отдельно).
- В `tx_touches_node` — строка `(tx_id, node_id, 'changed')`.

## `delete` + incident links

При `delete` узла:

1. **Cross-scope check.** Если удаляется main-узел, ищем incoming
   `usage → main` links:

   ```sql
   SELECT l.name, l.from_id
   FROM link l
   JOIN node source ON source.id = l.from_id
                    AND source.graph_name = l.graph_name
   WHERE l.graph_name = ?
     AND l.to_id      = ?
     AND source.scope = 'usage';
   ```

   Если непусто → `delete_blocked_by_cross_scope_link`, rollback tx.

2. **Найти incident links** (внутри scope):

   ```sql
   SELECT name, from_id, to_id FROM link
   WHERE graph_name = ?
     AND (from_id = ? OR to_id = ?);
   ```

3. **Удалить links:**

   ```sql
   DELETE FROM link
   WHERE graph_name = ? AND (from_id = ? OR to_id = ?);
   ```

4. **Удалить узел:**

   ```sql
   DELETE FROM node WHERE graph_name = ? AND id = ?;
   ```

   `ON DELETE CASCADE` на `node_map_binding` снимает bindings.

5. **Секции:**
   - `deleted.nodes[]` += `{ id }`
   - `deleted.links[]` += `{ name, from, to }` по каждому удалённому
     link
   - `tx_touches_node` += `(tx_id, deleted_id, 'deleted')`
   - `tx_touches_link` += `(tx_id, name, from, to, 'deleted')`

Endpoint-узлы на другом конце удалённых incident links структурно не
меняются: их `version` не инкрементируется, в `tx_touches_node` они
не индексируются, в `changed.nodes` не попадают. Incident links в
state узла не входят (per [schema.md](schema.md), раздел «`node`»,
описание колонки `version`).

## Replay

Полное восстановление состояния scope-а (для миграции, recovery,
отладки):

```sql
SELECT id, sections_json, date, ordinal
FROM tx_event
WHERE graph_name = ?
  -- AND tx_scope = ?        -- опциональный фильтр по scope
ORDER BY date, ordinal;
```

Для каждого `tx_event` распарсить `sections_json` и применить в
порядке (per ../api/hist-scope.md):

1. `deleted.links[]` → `DELETE FROM link WHERE ...`
2. `deleted.nodes[]` → `DELETE FROM node WHERE ...`
3. `changed.nodes[]` → `UPDATE node SET <set-поля>` + DML на
   `node_map_binding` (partial-merge для `set.map_bindings`).
4. `created.nodes[]` → `INSERT INTO node` (с конкретным id из
   sections, не из sequence) + `INSERT INTO node_map_binding`.
5. `created.links[]` → `INSERT INTO link`.

Внутри одного event-а — одна SQLite-транзакция. Между событиями
коммитим, чтобы при сбое замигрировать с последнего успешно
применённого `tx_event.id`.

**Replay в чистый DB** (миграция со старого storage, recovery):
сначала очистить `node` / `node_map_binding` / `link` / `sequence` для
graph-а, оставить `tx_event` / `tx_touches_*` нетронутыми (они source
истины), потом прогнать алгоритм.

**`sequence.next_id` восстановление:** `SELECT max(CAST(id AS
INTEGER)) FROM (...union ids...) ` — но id хранится в hex. Проще:
kernel-side tracking max во время replay, в конце
`INSERT/UPDATE sequence SET next_id = max + 1`.

## Реконструкция значения поля

По ../api/hist-scope.md, kernel умеет восстановить любое поле любого
узла на любой момент. Используется для rollback и для точечных
запросов истории.

### Скалярные (`title`, `content`, `path`) на момент перед tx_X

```sql
SELECT e.id, e.sections_json
FROM tx_event e
JOIN tx_touches_node t
  ON t.graph_name = e.graph_name AND t.tx_id = e.id
WHERE e.graph_name = ?
  AND t.node_id    = ?
  AND t.role IN ('created', 'changed')
  AND (e.date < ?_target_date
       OR (e.date = ?_target_date AND e.ordinal < ?_target_ordinal))
ORDER BY e.date DESC, e.ordinal DESC;
```

Kernel-side: пройти результат сверху вниз, парсить `sections_json`,
искать `<id>` сначала в `changed.nodes[i].set.<field>`, затем в
`created.nodes[i].<field>`. Вернуть первое найденное значение. Если
ничего не найдено — поле не было задано (узел ещё не создан или поле
дефолтное).

### `map_bindings` на момент перед tx_X

Отдельный накопительный алгоритм (per ../api/hist-scope.md):

1. Тем же запросом найти все tx, тронувшие `<id>` в роли `created` /
   `changed`, отсортированные по убыванию.
2. Идти сверху вниз до первой `created.nodes[<id>]` — это snapshot
   `map_bindings` на момент создания.
3. Собрать все промежуточные `changed.nodes[<id>].set.map_bindings`
   (между snapshot-ом и tx_X).
4. Накатить snapshot + промежуточные diff'ы в **прямом**
   хронологическом порядке (от старого к новому): для каждого ключа
   diff'а — `null` удаляет ключ из аккумулятора, не-null
   устанавливает / перезаписывает.

### Полный snapshot узла

Применить оба алгоритма ко всем полям контракта data-узла:
скаляры (`title`, `content`, `path`) — алгоритмом для скаляров;
`map_bindings` — накопительным.

## Rollback

По ../api/tx.md, kernel:

1. **Прочитать секции исходного:**

   ```sql
   SELECT sections_json FROM tx_event
   WHERE graph_name = ? AND id = ?_target;
   ```

2. **Проверить, что нет уже выпущенного rollback:**

   ```sql
   SELECT 1 FROM tx_event
   WHERE graph_name = ? AND rollback_of = ?_target;
   ```

   Если есть — `rollback_already_done`.

3. **Вычислить inverse-ops:**
   - `created.nodes[]` → inverse `delete` этого id.
   - `created.links[]` → inverse `unlink`.
   - `changed.nodes[].set` → inverse `update` со значениями из
     реконструкции поля на момент перед `tx_target` (см. выше).
   - `deleted.nodes[]` → inverse `create` с восстановлением `id` и
     полного snapshot-а узла на момент перед `tx_target`
     (реконструкция всех полей).
   - `deleted.links[]` → inverse `link`.

4. **Применить inverse как обычную tx**, с проверкой что текущее
   состояние позволяет (иначе `rollback_conflict` с
   `details.conflicts[]` и `blocking_transactions[]`).

5. **INSERT новый `tx_event`** с `rollback_of = ?_target`,
   `sections_json` от inverse-ops, обычный `title` / `description`
   (LLM передаёт сам, kernel не подменяет).

6. **Заполнить touches** для нового event-а как обычно.

**Восстановление удалённого `id`:** при INSERT нового узла kernel
использует переданный явно `id` (не next из `sequence`).
`sequence.next_id` не сбрасывается — id остаётся уникальным даже
после восстановления (per ../api/model.md: «kernel-генерируемый
rollback восстанавливает удалённый id на тот же узел»).

## Атомарность hist-write

Запись `tx_event` + `tx_touches_node` + `tx_touches_link` идёт в той
же SQLite-транзакции, что и DML на `node` / `node_map_binding` /
`link`. SQLite атомарность транзакции гарантирует:
- либо все таблицы обновлены и event-узел записан,
- либо ничего, и возвращается `hist_write_failed` (per ../api/tx.md).

При падении IO (диск, journal) между BEGIN и COMMIT — SQLite
rollback'ит автоматически.

## Темпоральные чтения

`read` с `at` (per ../api/model.md, раздел «Темпоральные чтения
(`at`)») переиспользует механизмы этого файла: replay через hist для
многоузловых селекторов, точечная реконструкция поля для exact-id
селекторов. Алгоритмы и SQL — в [schema.md](schema.md), раздел
«Темпоральные чтения (`at`)».
