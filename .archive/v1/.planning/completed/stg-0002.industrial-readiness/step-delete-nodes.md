# stg-0002 — delete-nodes

## Цель
Заменить одиночный `delete-node` множественным `delete-nodes --ids=<csv>` с явным контрактом о намерении. Без авто-каскада: LLM сама собирает набор удаляемого (через `get_subtree` по любому scope). Движок валидирует path-замкнутость и отсутствие dangling cross-refs; нарушения возвращаются с детальным описанием.

## Файлы
- `src/DocsWalker.Core/Api/WriteApi.cs`:
  - Снять `DeleteNode(int id)`.
  - Добавить `DeleteNodes(IReadOnlyCollection<int> ids)`.
  - Алгоритм:
    1. Все id существуют, иначе `unknown_ids` со списком отсутствующих.
    2. **Path-замкнутость:** для каждого id в наборе через `Graph.GetChildren(id, "path")` собрать всех path-children; объединение должно быть подмножеством исходного набора. Иначе `path_orphans_left` со списком id, которых не хватает.
    3. **Dangling cross-refs:** найти все cross-refs (т.е. out_refs с `Tree == null` ИЛИ с `Tree != "path"` — последние тоже остаются висящими, потому что мы удаляем только по path-набору) от узлов вне набора в узлы внутри набора. Если есть — `dangling_refs` со списком (source_id, ref_name, target_id) и hint «зови redirect-refs или transaction с update-ref».
    4. Если чисто — атомарное удаление: убрать узлы из графа, обновить parent.out_refs[ref_name] (удалить ссылки), AtomicWriter сносит файлы/atom-записи.
- `src/DocsWalker.Core/Api/Transaction.cs` — операция `delete_nodes` (множественная). Снять одиночный `delete_node`.
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — снять `delete-node`, добавить `delete-nodes --ids=12,15,17`. Парсер `--ids` — split по запятой, trim, валидация что все парсятся в int.
- `src/DocsWalker.Cli/Cli/Commands.cs` — снять `delete-node`, добавить `delete-nodes`.
- `docs/DocsWalker.yml` — обновить раздел «Операции записи» (#…): убрать `delete_node`, добавить `delete_nodes` с описанием алгоритма (path-замкнутость, dangling cross-refs, отсутствие авто-каскада).

## Действия
1. Зафиксировать в `docs/DocsWalker.yml`.
2. Реализовать `WriteApi.DeleteNodes`.
3. Подключить в Transaction и CLI.
4. Снять одиночный `delete-node` из всех слоёв.
5. Тесты.

## Тесты
- `tests/.../WriteApiDeleteNodesTests.cs`:
  - Удаление одного листового узла — успех.
  - Удаление узла с path-children без указания детей — `path_orphans_left`, набор недостающих корректен.
  - Удаление набора, замкнутого по path, без dangling cross-refs — успех; узлы реально снесены, файлы обновлены.
  - Удаление набора с dangling cross-ref от узла вне набора — `dangling_refs`, список корректен.
  - Удаление с одним unknown id — `unknown_ids`.
  - Cascade-имитация по дереву strategic: get_subtree(strategy, "strategic") + дополнить path-замыканием каждого узла → передать в delete-nodes → успех.
- `tests/.../TransactionDeleteNodesTests.cs` — тот же набор внутри transaction.

## Риски
- Старые тесты, которые звали `DeleteNode` — обновить на `DeleteNodes([id])`.
- Атомарность: при сборке набора ошибка не должна оставить граф в полусостоянии. Реализация — сначала валидация на in-memory копии, потом коммит через AtomicWriter.
- Производительность: для большого набора path-замыкания и поиск dangling refs — O(размер набора + входящие refs). Индексы из R14 (scope-индекс по path) делают первое O(набор × children). Для входящих cross-refs нужен обратный индекс — добавить в R14 либо здесь.
