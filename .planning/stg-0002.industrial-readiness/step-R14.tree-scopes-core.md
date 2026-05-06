# stg-0002 — R14 tree-scopes-core

## Цель
Расширить ядро (`Schema/*`, `Validation/*`, `Graph/*`) поддержкой tree-scopes из мета-схемы v5: парсинг, валидация, индексация для эффективных subtree-обходов. После этого шага сборка снова собирается и `check-integrity` проходит на R13-схеме.

## Файлы
- `src/DocsWalker.Core/Schema/MetaSchemaDocument.cs` (или эквивалент) — парсинг новой версии мета-схемы (v5): секция `tree_definition`, поле `trees:` в schema_root, поле `tree?` в ref_def, удалённое `path_targets` у type_definition.
- `src/DocsWalker.Core/Schema/SchemaDocument.cs` — модель: `IReadOnlyList<TreeDefinition> Trees`. Тип `TypeDefinition` теряет `PathTargets`. `RefDef` получает `string? Tree`. Field-валидация: при `Tree != null` поля `Cardinality`/`Required` не сериализуются и не парсятся (или принудительно фиксируются как `One`/`true`).
- `src/DocsWalker.Core/Schema/SchemaValidator.cs` (или где живёт валидация Схемы по мета-схеме):
  - Каждый `tree:` в RefDef ↦ существует в `Trees` (иначе `unknown_tree_scope`).
  - Дерево `path` обязано присутствовать в `Trees` (иначе `path_tree_missing`).
  - Каждый type кроме root имеет в `out_refs` ровно одну запись с `tree: path` (иначе `missing_path_ref`).
  - При `tree:` запрещены `cardinality`/`required` (иначе `redundant_cardinality_with_tree`).
  - Имя `path` в RefDef допустимо только при `tree: path`.
- `src/DocsWalker.Core/Validation/IntegrityValidator.cs` (или эквивалент):
  - Per-scope no-cycle: для каждого `tree` собрать граф scope-edges, проверить отсутствие циклов; нарушение — `tree_cycle` с указанием scope-name и id-цепочки.
  - Для дерева `path` — связность: каждый не-root узел достижим от root по path-edges; нарушение — `path_disconnected`.
  - Каждый узел кроме root имеет ровно один out_ref с `tree: path` (path-cardinality=1, обязательность); нарушение — `path_missing_or_duplicated`.
- `src/DocsWalker.Core/Graph/Graph.cs` (или эквивалент):
  - Индекс по scope: `IDictionary<string, ScopeIndex> _byScope`, где `ScopeIndex` содержит `parentOf[childId] -> parentId` и `childrenOf[parentId] -> List<int>` для данного scope. Заполняется при загрузке и обновляется write-операциями.
  - Метод `IEnumerable<int> GetChildren(int nodeId, string scope)` — O(1) лук-ап.
  - Метод `int? GetParent(int nodeId, string scope)` — O(1).
- `src/DocsWalker.Core/Graph/DocumentLoader.cs` — учесть, что target_types path-связи теперь читаются из RefDef[name=path].target_types, а не из TypeDefinition.path_targets.

## Действия
1. Расширить парсер мета-схемы под v5.
2. Обновить SchemaDocument-модель (Trees, RefDef.Tree, удаление PathTargets).
3. SchemaValidator: новые проверки согласованности.
4. IntegrityValidator: per-scope cycle + path connectivity + path uniqueness.
5. Graph: scope-индексы.
6. DocumentLoader: миграция чтения path-target_types.
7. Прогнать `check-integrity` на R13-схеме — должен пройти зелёным (живые docs ещё могут быть невалидны из-за rule-without-example, это R-rule-requires-example).

## Тесты
- `tests/.../SchemaParserTests.cs` — мета-схема v5 парсится, v4 отвергается с понятной ошибкой.
- `tests/.../SchemaValidatorTests.cs` — все новые проверки (unknown_tree_scope, path_tree_missing, missing_path_ref, redundant_cardinality_with_tree).
- `tests/.../IntegrityValidatorTests.cs` — искусственный цикл в scope (path и не-path), path-disconnected, два path-ref у одного узла.
- `tests/.../GraphScopeIndexTests.cs` — индекс корректно строится, GetChildren/GetParent отдают правильные значения.

## Риски
- Сборка краснеет на R12+R13 без R14 — это спланировано, R14 закрывает.
- Запутанность с тем, что RefDef для `tree:` не имеет cardinality/required: модель должна явно отвечать «истинная семантика» (через computed-свойства `EffectiveCardinality` / `EffectiveRequired`), чтобы вышестоящий код (write-API, валидаторы integrity, describe-type) обращался к ним без if'ов.
- Существующая логика, которая жёстко завязана на «path — встроенная связь, не в out_refs» — теперь path **в** out_refs. Внимательно: WriteApi/ReadApi могут иметь ветки `if (refName == "path")` — их нужно ревизовать.
