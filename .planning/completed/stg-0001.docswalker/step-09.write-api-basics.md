# stg-0001 — write-api-basics

## Цель
Реализовать базовые write-операции: `create-node`, `update-node`, `delete-node`, `create-ref`, `delete-ref`, `add-ref-type`. Атомарная запись (temp-файлы + rename), валидация перед коммитом.

## Файлы
`src/DocsWalker.Core/Api/WriteApi.cs` — реализация операций (типизированные `WriteOp`, общий `Apply` через снимок графа)
`src/DocsWalker.Core/Api/WriteState.cs` — изменяемый снимок графа и Схемы для одной транзакции
`src/DocsWalker.Core/Store/AtomicWriter.cs` — единый механизм атомарной записи нескольких файлов: подготовить временные файлы, проверить, переименовать
`src/DocsWalker.Core/Yaml/SchemaEmitter.cs` — сериализатор `SchemaDocument` обратно в `Схема.yml` (нужен `add-ref-type`)
`src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — обработчики write- и transaction-команд CLI
`src/DocsWalker.Cli/Program.cs` — диспетчер команд: подключение write- и transaction-команд

## Действия
1. Каждая операция работает на копии графа: применить изменения, прогнать `Validator`, при успехе сериализовать через `Emitter` и записать атомарно через `AtomicWriter`.
2. `create-node`: получить id из `SequenceCounter`, создать узел с указанным `parent_id`, `type`, `title`/`name`, `body`. Авто-создать path-связь.
3. `update-node`: применить patch к узлу (новый title, изменения описания и блоков). Родителя не меняет — перенос делается через `transaction`.
4. `delete-node`: запретить, если есть входящие явные связи. Удалить узел и его исходящие явные/системные связи.
5. `create-ref` / `delete-ref`: только для ref_type c `system=false`. Тип должен быть в Схеме.
6. `add-ref-type`: добавить новый ref_type в Схему (`Схема.yml`). Запретить коллизии с системными именами и именами default-блоков (`definitions`, `examples`, `fields`, `content`).
7. `AtomicWriter`: подготовить временные файлы для всех затронутых, выполнить `FileStream.Flush(true)`, переименовать (по одному файлу — `File.Replace`/`File.Move`); при ошибке — удалить temp-файлы.

## Риски
- На Windows атомарность гарантируется только в пределах одного файла. Для пачки файлов используется двухфазный подход: сначала записать все temp-файлы, потом по одному переименовать. Если падение между переименованиями — состояние «частично применено». Документировать как известное ограничение и для редких случаев решать через `transaction` + watchdog.
- `add-ref-type` правит `Схема.yml` — это тоже атомарная запись, и она проходит валидатор `MetaSchema`.
