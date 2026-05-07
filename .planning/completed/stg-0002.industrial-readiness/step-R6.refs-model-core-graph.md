# stg-0002 — industrial-readiness

## Цель
Переписать ядро DocsWalker.Core под refs-модель: Schema/, Graph/Node + Graph/Ref + Graph/Graph + Graph/DocumentLoader + Graph/TitleFormat, Validation/. Старая модель (kind, ref_type, system/default-блоки, ParentBlockName, Fields/Blocks/InlineValue, RefOrigin) удаляется без shim. Node — ровно 5 полей плюс sourceFile-метаданные. Промежуточные коммиты с красной сборкой допустимы (R7/R8 закроют API после R6).

## Файлы
**Новые / переписываемые:**
- `src/DocsWalker.Core/Schema/MetaSchema.cs` — `MetaSchemaDocument` + `TypeDefinition` + `RefDef` + `TitleSource`/`Cardinality` enums.
- `src/DocsWalker.Core/Schema/Schema.cs` — `SchemaDocument` record (description + types).
- `src/DocsWalker.Core/Schema/SchemaLoader.cs` — парсер новой Схемы.
- `src/DocsWalker.Core/Graph/Node.cs` — 5 полей (Id, TypeName, Title, Text, OutRefs) + SourceFile metadata.
- `src/DocsWalker.Core/Graph/Ref.cs` — простая запись (Name, TargetId), без `RefOrigin`. NodeBlock/TextBlock/ChildrenBlock/OutRefsBlock/FieldValue удаляются.
- `src/DocsWalker.Core/Graph/Graph.cs` — индексы byId/byType, GetOutRefs (из Node.OutRefs), GetInRefs (обход графа), GetChildren (через path).
- `src/DocsWalker.Core/Graph/DocumentLoader.cs` — парсер нового YAML: id/text/sections в корне; `"(#id) title": value` для смысловых; out_refs-блоки section как key→list-of-atoms.
- `src/DocsWalker.Core/Graph/TitleFormat.cs` — единый формат `"(#{id}) {title}"` для inline_key-типов.
- `src/DocsWalker.Core/Graph/GraphLoadException.cs` — без изменений (или мелкие, если нужны новые коды).
- `src/DocsWalker.Core/Validation/SchemaCheck.cs` — проверка path_targets, out_refs контракта типа, cardinality, required, text_required.
- `src/DocsWalker.Core/Validation/RefsCheck.cs` — имя ref объявлено в типе или = path; цель существует; target_types соблюдается; нет циклов по path.
- `src/DocsWalker.Core/Validation/UniqueCheck.cs` — id уникален; title уникален среди siblings одного типа.
- `src/DocsWalker.Core/Validation/StyleCheck.cs` — только однострочные значения (без \n/\r/\t в title/text/refs).
- `src/DocsWalker.Core/Validation/MetaSchemaCheck.cs` — Схема соответствует мета-схеме (фиксированные ожидания).
- `src/DocsWalker.Core/Validation/SequenceCheck.cs` — sequence ≥ max id (мелкая правка).
- `src/DocsWalker.Core/Validation/Validator.cs` — orchestrator без ParentBlockCheck.

**Удаляемые:**
- `src/DocsWalker.Core/Validation/ParentBlockCheck.cs` — концепт parent_block ушёл вместе с ParentBlockName.

**Не трогаются (R7/R8):**
- `src/DocsWalker.Core/Api/*` — сломаются, починятся в R7/R8.
- `src/DocsWalker.Core/Yaml/Emitter.cs`, `Yaml/SchemaEmitter.cs` — сломаются, починятся в R7 (нужны для записи).
- `src/DocsWalker.Core/Yaml/YamlReader.cs`, `Yaml/Quoting.cs` — низкоуровневые, не зависят от модели, останутся.
- `src/DocsWalker.Core/Store/*` — не зависят от модели, остаются.
- `src/DocsWalker.Cli/*` — сломаются, починятся в R9.

## Действия
1. **Schema:** переписать MetaSchema/Schema/SchemaLoader. Загрузка docs/Схема.yml v4 формата; полная замена `TypeDefinition` на новый. Удалить `NodeType`/`RefType`/`Primitive`/`FieldDefinition`/`BlockDefinition`. Удалить enums `TypeKind`, `RefDirection`. Сохранить `TitleSourceKind` (с переименованием значения `Field` → `Dirname`).
2. **Graph:** переписать Node (5 полей + SourceFile), Ref (Name, TargetId), Graph (byId/byType/byParent через path/documentByTitle; GetOutRefs/GetInRefs).
3. **DocumentLoader:** переписать целиком. Новый код парсит: document root mapping {id, text, sections[]}; section single_key_mapping `"(#id) title": [list-of-blocks]`; block name → list-of-atoms; atom single_key_mapping `"(#id) title": text`. TypeIndex упрощается (только NodeType-аналог; refType/primitive ушли).
4. **TitleFormat:** упростить до universal-формата. Метод TryParse оставить (для парсера), метод Compose добавить (для emitter, R7).
5. **Validation:** удалить ParentBlockCheck. Переписать пять других проверок и Validator под refs-модель.
6. **Sanity:** запустить `dotnet build src/DocsWalker.Core` или Glider get_diagnostics. Ожидаются ошибки в Api/Yaml/Cli — это фиксируется в R7/R8/R9. Core под Schema/Graph/Validation должен быть компилируем.

## Риски
- В Validator.cs могут быть ссылки из Api/* — компиляция Core упадёт. Допустимо.
- Удаление NodeBlock/TextBlock/etc. сломает Yaml/Emitter.cs. Допустимо до R7.
- Sequence-счётчик и AtomicWriter не трогаем — они на уровне Store, не зависят от модели.
- TitleSourceKind: переименование Field→Dirname затронет SchemaLoader. Нет внешних потребителей.
- Тесты в DocsWalker.Tests заведомо упадут (зависят от старой модели). Их починка — в отдельной задаче после R7-R9.
