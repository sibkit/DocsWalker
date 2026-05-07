# stg-0002 — R16 tree-scopes-write-api

## Цель
Обобщить write-API под tree-scopes:
- `move-node` принимает параметр `tree` (default `"path"`); семантика для path-scope полностью сохраняется (cascade SourceFile-rewrite у потомков), для других scope'ов — атомарная правка одного scope-ref'а у узла.
- `create-node` для типов с несколькими tree-refs (path + домен) принимает значения каждого scope-родителя как отдельные required-параметры (имя параметра = имя ref'а).

## Файлы
- `src/DocsWalker.Core/Api/WriteApi.cs`:
  - `MoveNode(int nodeId, int newParentId, string tree = "path")` — диспетчирует:
    - `tree == "path"` → существующая логика (через AtomicWriter, cascade SourceFile).
    - `tree != "path"` → одна операция `update_ref` на scope-ref узла, без FS-операций.
  - `CreateNode(...)` — расширить: помимо `path` принимать значения других tree-refs из контракта типа. Все они required по контракту tree-ref. Имя параметра = имя ref'а.
- `src/DocsWalker.Core/Api/Transaction.cs` — операции `move_node` и `create_node` принимают аргумент `tree` (для move) и map значений всех tree-refs (для create).
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs`:
  - `move-node --id=<n> --to=<n> [--tree=<scope>]` — default path.
  - `create-node` — динамические параметры по контракту типа: для каждой tree-ref своё `--<ref-name>=<id>`.
- `src/DocsWalker.Core/Validation/IntegrityValidator.cs` — после move в не-path scope: проверить, что новое значение scope-ref'а не создаёт цикл в дереве (cycle-check именно для этого scope, до коммита).

## Действия
1. Расширить WriteApi.MoveNode параметром tree, диспетчер на path vs другие.
2. Для не-path move: pre-check цикла, валидация target_types (целевой узел совместим с target_types scope-ref'а), запись через атомарный update_ref.
3. CreateNode: цикл по всем RefDef типа с `Tree != null` (включая path) — каждый требует значение в параметрах.
4. CLI: parse `--tree` для move-node; для create-node динамические параметры собираются в момент сборки команды (как уже делает R9).
5. Обработка ошибок: `unknown_tree`, `tree_cycle`, `target_type_mismatch`.

## Тесты
- `tests/.../WriteApiMoveTreeTests.cs` — move в не-path scope меняет один ref, не трогает SourceFile у потомков; move в path-scope сохраняет старое поведение.
- `tests/.../WriteApiMoveTreeCycleTests.cs` — попытка move, образующая цикл в scope, отклоняется.
- `tests/.../WriteApiCreateNodeMultiTreeTests.cs` — create узла в схеме с двумя tree-ref'ами требует оба значения.
- Существующие move-node тесты (из R7/R11) проходят без `--tree` (default path).

## Риски
- Перенос «каскад rewrite SourceFile» с path-only — не должен случайно сработать на не-path move. Изоляция веток должна быть жёсткой.
- Если в будущем появятся «scope, которые тоже физически материализуются» — сейчас неизвестно; одновременно нет таких сценариев. Не тащим обобщение преждевременно.
