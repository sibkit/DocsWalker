# stg-0002 — delete-document

## Цель
Добавить write-операцию `delete-document`: удаление файла `docs/*.yml` со всеми узлами внутри. Снимает текущий запрет `delete_document_unsupported` в `WriteApi`.

## Файлы
`docs/DocsWalker.yml` — раздел «Операции записи» (#27): новый пункт `delete_document` с параметрами (`id`, `force?`) и правилами (без `force` — запрещён, если на любой узел документа есть входящая явная ref-связь извне документа; с `force` — удаляет и предупреждает, что входящие ссылки станут битыми → вылетит ошибка валидации `ref_target_not_found`, поэтому `force` имеет смысл только в transaction вместе с правкой источников ссылок).
`docs/DocsWalker.yml` — пункт `transaction` (#34): упомянуть.
`docs/DocsWalker.yml` — раздел «CLI-интерфейс» (#35): пример.
`src/DocsWalker.Core/Api/WriteApi.cs` — `DeleteDocumentOp`, `ApplyDeleteDocument`. Снять `delete_document_unsupported` для случая, когда `id` — корень документа.
`src/DocsWalker.Core/Store/AtomicWriter.cs` — поддержка удаления файла как одного из target-действий пачки.
`src/DocsWalker.Core/Api/Transaction.cs` — разбор `delete-document`.
`src/DocsWalker.Cli/Cli/Commands.cs`, `Cli/Handlers/WriteHandlers.cs`, `Program.cs`.
`tests/DocsWalker.Tests/WriteApiTests.cs`, `AtomicWriterTests.cs` — тесты.

## Действия
1. Зафиксировать операцию в `docs/DocsWalker.yml` (включая семантику `force`).
2. Реализовать `DeleteDocumentOp`: проверка, что узел — корневой (`document`); сбор всех потомков; проверка, что нет входящих явных ref-связей извне документа (если `force=false`); удаление поддерева из графа; пометка пути файла на физическое удаление.
3. Расширить `AtomicWriter`: новый тип target-а — удаление файла (с двух-фазной семантикой и rollback).
4. Поддержать в `Transaction.cs` и CLI.
5. Тесты: удаление пустого документа; запрет при входящих ссылках; `force=true` без правки источников → должна упасть валидация на `ref_target_not_found`; `force=true` в одной transaction с правкой источников → успех.

## Риски
`AtomicWriter` сейчас занимается записью; добавление операции удаления усложняет rollback. Возможно, нужно сначала переименовать в `.tmp-delete` и удалить только после успеха всех записей.
