# stg-0002 — industrial-readiness

## Цель
Переписать write-слой DocsWalker.Core под refs-модель. Старые операции `add_ref_type`,
`update_node` с patch.fields/blocks/value, `move_node` с new_block_name — уходят;
вместо них единый набор: `create_node` принимает refs-карту со значениями всех required
out_refs (включая path), `move_node` правит только path-связь, `create_ref` / `delete_ref`
работают с любой объявленной в типе связью кроме path. SchemaEmitter удаляется как
неиспользуемый (правка Схемы — ручная, см. docs/DocsWalker.yml/«Расширение Схемы вручную»).
Промежуточный red-build допустим: `Api/ReadApi.cs` и `Api/ReadApiJson.cs` остаются на старой
модели и закроются в R8.

## Файлы
**Переписаны:**
- `src/DocsWalker.Core/Yaml/Emitter.cs` — emitter под refs-формат: документ как
  `id / text / <ref>:` с инлайн-детьми; section как single_key_mapping
  `"(#id) title":` со списком ref-блоков; атом — `"(#id) title": text`.
- `src/DocsWalker.Core/Api/WriteState.cs` — снимок узлов под Node с OutRefs;
  `MarkDocumentDirtyForNode` поднимается по path до узла с ParentId==RootId;
  `BuildGraph` добавляет документы первыми (для уникальности by-title).
- `src/DocsWalker.Core/Api/WriteApi.cs` — операции CreateNode (refs-карта),
  UpdateNode (только title/text), DeleteNode (запрет при path-детях и not-path
  in-refs), MoveNode (только new_parent_id), CreateRef/DeleteRef (любая
  объявленная связь, не path).
- `src/DocsWalker.Core/Api/Transaction.cs` — JSON-форма входа: `refs: {name: [ids]}`
  для create-node; убрана операция `add-ref-type`.

**Удалены:**
- `src/DocsWalker.Core/Yaml/SchemaEmitter.cs` — единственный потребитель был
  `add_ref_type`, операция уходит вместе с типом RefType.

**Не трогаются (R8/R9 scope):**
- `src/DocsWalker.Core/Api/ReadApi.cs`, `Api/ReadApiJson.cs` — на старой модели; R8.
- `src/DocsWalker.Cli/*` — на старой модели; R9.

## Принятые решения
1. **Удаление документов и переименование документов** в этой версии write-API не
   поддерживается — отдельные операции (`delete-document`, `rename-document`) появятся
   позже, при правке title документа меняется имя YAML-файла, что требует отдельного
   контракта. Сейчас — структурированные ошибки `delete_document_unsupported` /
   `rename_document_unsupported`.
2. **Каскадное удаление** не реализовано: `delete-node` отвергается при наличии
   path-детей. LLM удаляет поддерево вручную через серию `delete-node` (или одной
   transaction). Это явное решение в пользу простоты — каскад добавляется отдельным
   шагом, если станет реальным болевым местом.
3. **Создание folder-узлов** в этой версии запрещено — title_source=dirname требует
   создания каталога docs/, что относится к R10 (folders). Сейчас — структурированная
   ошибка `unsupported_type`.
4. **`new_block_name`** у move_node ушло: при переходе на refs-модель «контейнер» —
   это связь path с ровно одной целью; имя блока было артефактом старой модели.

## Действия
1. Yaml/Emitter: единый формат сериализации; решение «атом vs контейнер» — по контракту
   типа (`text_required && OutRefs.Count == 0` → атом).
2. Api/WriteState: модель snapshot с Schema read-only.
3. Api/WriteApi: операции и их предусловия (см. список выше).
4. Api/Transaction: парсер JSON-входа.
5. Удалить Yaml/SchemaEmitter.cs.
6. `dotnet build src/DocsWalker.Core` — допустимо до 11 ошибок в ReadApi/ReadApiJson;
   убедиться, что новые файлы и Yaml/Emitter компилируются.

## Риски
- Тесты в DocsWalker.Tests заведомо упадут — будут переписаны после R8/R9.
- `MoveNode` пока не поддерживает разные SourceFile у нового и старого родителя в
  rare-cases с обрывом path; при разрыве path до root — `cannot_move_root` /
  отсутствие dirty-mark; такие графы и так не валидны и до move_node не доживут.
- `CreateNode` не валидирует target_types для каждой переданной цели — это сделает
  `Validator.SchemaCheck`. Дублировать на write-пути нет смысла: ошибка одинаково
  превращается в `WriteValidationException`.
