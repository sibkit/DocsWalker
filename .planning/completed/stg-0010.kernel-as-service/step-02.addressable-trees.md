# stg-0010 — step-02 — addressable-trees

## Цель

Расширить парсер мета-схемы и runtime-модель так, чтобы поле
`unique_sibling_titles` (на tree-связях) и `default_addressable_tree`
(на корне Схемы), добавленные в step-01, начали учитываться. Реализовать
валидацию `duplicate_sibling_title` для всех addressable trees (а не
только `path`), расширить `get-by-path` параметром `--tree=<name>` с
lax-resolution дефолта. Шаг **только код + тесты**: docs/ не трогаем,
`dotnet build` и `dotnet test` после шага должны быть зелёные.

Step-03 (client-server-reshape) опирается на runtime-флаги из этого
шага: `KernelHttpClient` должен уметь отдавать дефолт-tree клиенту, а
LLM — получать ошибку `duplicate_sibling_title` с tree-name (а не
с type-name, как было в старой модели).

## Файлы под правку

- `src/DocsWalker.Core/Schema/MetaSchema.cs` — добавить поле
  `UniqueSiblingTitles` в `RefDef`; добавить вычисляемое свойство
  `IsAddressable` (синтаксический сахар над `Tree != null &&
  UniqueSiblingTitles`).
- `src/DocsWalker.Core/Schema/Schema.cs` — добавить поле
  `DefaultAddressableTree` в `SchemaDocument`.
- `src/DocsWalker.Core/Schema/SchemaLoader.cs` —
  - `ReadRefDef`: читать `unique_sibling_titles` (только при `tree`).
  - `ParseSchema`: читать `default_addressable_tree`.
  - `SchemaJson.RefDefToJson`: эмитить `unique_sibling_titles: true`,
    если задано (для round-trip get-schema).
  - `SchemaJson.ToJson(SchemaDocument)`: эмитить
    `default_addressable_tree`, если задан.
- `src/DocsWalker.Core/Validation/UniqueCheck.cs` — переписать ключ
  uniqueness: вместо `(parent_id, type_name, title)` использовать
  `(tree_name, parent_in_tree, title)`, итерировать по всем addressable
  trees (по `RefDef.UniqueSiblingTitles=true`). Сообщение и код ошибки
  (`duplicate_sibling_title`) сохраняются.
- `src/DocsWalker.Core/Validation/MetaSchemaCheck.cs` — валидировать
  Схему: `default_addressable_tree` (если задан) ссылается на
  существующее имя дерева, у которого хотя бы одна tree-связь
  `unique_sibling_titles=true`; `unique_sibling_titles=true` стоит
  только на tree-связи (без `tree` — invalid_meta_schema).
- `src/DocsWalker.Core/Api/ReadApi.cs` — `GetByPath`:
  - Принять опциональный параметр `tree`.
  - Resolution дефолта (lax):
    - Если задан `tree=<name>`:
      - неизвестное имя → `ReadApiException("unknown_tree_scope", …)`;
      - не-addressable → `ReadApiException("tree_not_addressable", …)`;
      - addressable → используется.
    - Если не задан:
      - есть `SchemaDocument.DefaultAddressableTree` → берётся он;
      - иначе если ровно один addressable tree → берётся он;
      - иначе → `ReadApiException("tree_required", …)`.
  - Поддержать корни в любом addressable tree: вместо
    `Graph.GetDocumentByTitle` обобщить — найти узел `target_types`
    которого включает `root` и `parent_in_tree == RootId` с указанным
    title (т. е. «top-level в этом tree»).
  - Spuck по детям через `Graph.GetScopeChildren(tree, parentId)` для
    non-path tree; для path — текущая логика `GetChildren`.
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — `GetByPath`
  принять `--tree=<name>` и пробросить в `ReadApi.GetByPath(path, tree)`.
- `tests/DocsWalker.Tests/ReadApiTests.cs` — тесты на
  `tree_not_addressable`, `tree_required` (через искусственную Схему с
  двумя addressable trees), `unknown_tree_scope`,
  `default_addressable_tree` (явный default берётся).
- `tests/DocsWalker.Tests/WriteApiTests.cs` (или новый
  `AddressableTreeTests.cs`) — тесты на sibling-collision в
  `create-node`, `update-node` (change of title) и `move-node`
  (change of parent).

## Действия

1. **MetaSchema.cs.** Добавить `bool UniqueSiblingTitles = false` в
   primary-конструктор `RefDef`. Добавить:
   ```csharp
   public bool IsAddressable => Tree is not null && UniqueSiblingTitles;
   ```
2. **Schema.cs.** Добавить `string? DefaultAddressableTree = null` в
   primary-конструктор `SchemaDocument` (опциональное поле).
3. **SchemaLoader.cs:ReadRefDef.** Добавить локальную переменную
   `bool? uniqueSiblingTitles = null;` и case `unique_sibling_titles`.
   В блоке `if (tree is not null)` пробросить значение в `RefDef`. Если
   `tree is null && uniqueSiblingTitles is not null` — кинуть
   `invalid_schema` («unique_sibling_titles допустим только при tree»).
4. **SchemaLoader.cs:ParseSchema.** Добавить case
   `default_addressable_tree`. Прокинуть в `SchemaDocument`.
5. **SchemaLoader.cs:SchemaJson.RefDefToJson.** Если
   `rd.UniqueSiblingTitles` — добавить `obj["unique_sibling_titles"] = true`.
6. **SchemaLoader.cs:SchemaJson.ToJson(SchemaDocument).** Если
   `doc.DefaultAddressableTree is not null` —
   `obj["default_addressable_tree"] = doc.DefaultAddressableTree`.
7. **MetaSchemaCheck.cs.** Добавить два правила:
   - `unique_sibling_titles=true` на ref без `tree` →
     `invalid_meta_schema` («unique_sibling_titles только при tree»).
     (Это уже отлавливается в `ReadRefDef`, но дублируем как defence-
     in-depth — Схема.yml на load даёт SchemaLoadException, но
     MetaSchemaCheck читает `SchemaDocument`, и здесь можно дать более
     дружественное сообщение для случая, когда Схема загружена через
     иной путь — например, тестовый.)
   - `default_addressable_tree`, если задан, ссылается на:
     - существующее имя дерева (иначе `unknown_tree_scope`);
     - дерево с хотя бы одной addressable tree-связью (иначе
       `default_tree_not_addressable`).
8. **UniqueCheck.cs.** Переписать вторую часть `Run` (логика
   `seenTitles`):
   - Собрать список addressable trees из `schema.Types`:
     `(tree_name, ref_name)` для каждого `RefDef` с
     `UniqueSiblingTitles=true`.
   - Для каждого addressable tree — итерировать по узлам, у которых
     `OutRefs[ref_name]` непуст (= узлы, участвующие в этом дереве),
     взять parent (target tree-ref'а), составить ключ
     `(tree_name, parent_id, title)`, найти дубли. Дубль —
     `duplicate_sibling_title` с сообщением, упоминающим tree-name.
   - Сигнатура `UniqueCheck.Run(GraphModel graph, List<ValidationError>
     errors)` → `UniqueCheck.Run(GraphModel graph, SchemaDocument
     schema, List<ValidationError> errors)`. Обновить вызов в
     `Validator.cs`.
9. **ReadApi.cs:GetByPath.** Сигнатура:
   `public NodeSubtree GetByPath(string path, string? tree = null)`.
   Резолвинг tree вынести в helper `ResolveAddressableTree(string?
   requested) -> string` (возвращает имя или кидает). Вызвать в начале.
   Затем для path: текущая логика; для не-path: новая логика через
   `Graph.GetScopeChildren`.

   Выяснить, есть ли в Graph метод поиска узла по title в произвольном
   scope. Если нет — добавить помощник вида `FindRootInTree(string
   tree, string title)` (ищет узел, у которого tree-ref-ом является root
   и title совпадает). Документ-как-корень дерева (path) — частный
   случай, обрабатывается через `_graph.GetDocumentByTitle`.

10. **ReadHandlers.cs:GetByPath.** Прочитать `--tree=` через стандартный
    `ParameterReader`/`ArgsReader` (где это делает `--root=`). Передать
    в `ReadApi.GetByPath`. Если параметр пустой — null.

11. **Тесты.** Минимум:
    - `GetByPath_TreeRequired_WhenMultipleAddressable` — Схема с двумя
      addressable trees (искусственная), `tree=null` → `tree_required`.
    - `GetByPath_TreeNotAddressable` — Схема, `tree="path"` без
      `unique_sibling_titles` (искусственная) → `tree_not_addressable`.
    - `GetByPath_DefaultAddressableTree_Honored` — Схема с
      `default_addressable_tree=path`, `tree=null` → работает.
    - `GetByPath_SinglyAddressable_AutoDefault` — Схема с одним
      addressable tree, `tree=null` → работает (текущее поведение).
    - `WriteApi_CreateNode_DuplicateSiblingTitle` — создаём узел с
      title, дублирующим existing sibling в addressable tree → ошибка.
    - `WriteApi_UpdateNode_TitleCollidesSibling` — переименовываем →
      ошибка.
    - `WriteApi_MoveNode_TargetParentHasSiblingWithTitle` → ошибка.

12. **`dotnet build`** + `dotnet test` — оба зелёные.

13. **Сверка со страт.** Блок 3.1, 3.2, 3.3, 3.4, 3.6 — каждое decision
    отражено в коде; Блок 3.5 (FS-материализация — kernel-internal) —
    не часть step-а, но логика `GetByPath` для tree=path остаётся
    backward-compatible.

## Риски

- **UniqueCheck сдвиг с `(parent, type, title)` на `(tree, parent,
  title)`** — потенциально breaking для существующих docs/, если под
  одним path-родителем есть siblings разных типов с одинаковым title.
  Mitigation: текущие `docs/` — inline_key атомы внутри одного YAML
  mapping (ключи уникальны автоматически), folder/document на одном
  FS-уровне (имена уникальны OS-конвенцией). Шанс конфликта мал;
  `dotnet test` покажет регрессию.
- **`MetaSchemaCheck` или `UniqueCheck` нужно поменять сигнатуру** —
  `UniqueCheck.Run(graph, errors)` → `UniqueCheck.Run(graph, schema,
  errors)`. Обновить `Validator.cs`. Тесты, вызывающие `Validator.Run`
  напрямую — обновить.
- **`get-by-path --tree=<non-path>`** — на текущих docs/ нет других
  addressable trees (`path` единственный), так что новая логика
  непокрытая боевыми данными. Тесты на искусственной Схеме покрывают
  unit-уровень; полное e2e проверится в step-06 (smoke).
- **Graph.GetScopeChildren** — нужно убедиться, что он есть и работает
  для произвольного scope. Если нет — добавить (через индексирование
  обратных рефов). Может оказаться большим изменением — если такое,
  выделим в отдельный sub-task.
- **`describe-type` / `get-schema` round-trip.** После добавления
  `unique_sibling_titles` в JSON-эмиттер, parser должен принимать его
  обратно — это test и будет.
- **Lax vs strict default tree.** strategy.md фиксирует lax (default
  field optional, auto-fallback при ровно одном addressable tree).
  Если пользователь возразит позже — переключаемся; пока lax.
- **Kernel pid=57588** — старый kernel со старой моделью, продолжает
  обслуживать docs/ через текущий протокол. После step-02 он на
  следующем `get-meta-schema`/`get-schema` запросе НЕ начнёт парсить
  новые поля автоматически — он работает на published-binaries из
  `publish/kernel/`, которые скомпилированы со старым SchemaLoader.
  Чтобы его обновить, нужно `dotnet publish` обоих exe заново и
  перезапустить kernel. Этот шаг — не часть step-02 (step-03/04
  пересоберут kernel полностью под новую модель). Если в ходе step-02
  понадобится `get-schema` через CLI с новыми полями — собрать
  dev-kernel-а отдельно.
