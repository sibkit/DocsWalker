# stg-0002 — move-node

## Цель
Добавить write-операцию `move-node`: атомарный перенос узла к новому родителю с сохранением `id` и всех входящих/исходящих ссылок. Отдельная операция, не расширение `update-node`.

## Файлы
`docs/DocsWalker.yml` — раздел «Операции записи» (#27): новый пункт `move_node` с параметрами (`id`, `new_parent_id`, `new_block_name?`) и правилами (id сохраняется; новый родитель должен принимать тип ребёнка; целевой блок задаётся при многозначности).
`docs/DocsWalker.yml` — пункт `transaction` (#34): упомянуть `move_node` среди допустимых операций пачки.
`docs/DocsWalker.yml` — раздел «CLI-интерфейс» (#35): пример вызова.
`src/DocsWalker.Core/Api/WriteApi.cs` — `MoveNodeOp`, `ApplyMoveNode`.
`src/DocsWalker.Core/Api/Transaction.cs` — разбор операции `move-node` из JSON.
`src/DocsWalker.Cli/Cli/Commands.cs` — регистрация.
`src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — обработчик.
`src/DocsWalker.Cli/Program.cs` — диспатч.
`tests/DocsWalker.Tests/WriteApiTests.cs`, `TransactionTests.cs` — тесты.

## Действия
1. Описать `move_node` в `docs/DocsWalker.yml` (Операции записи + transaction + CLI).
2. Реализовать `MoveNodeOp` и `ApplyMoveNode`: убрать id из старого `ChildrenBlock`, добавить в новый, обновить `ParentId` и `ParentBlockName`, при необходимости сменить `SourceFile` (если старый и новый родитель в разных документах). Пометить оба документа dirty.
3. Поддержать `move-node` в `Transaction.cs`.
4. Подключить в CLI: команда + параметры + диспатч.
5. Тесты: простой перенос внутри документа, перенос между документами, ошибка при несовместимом типе ребёнка для нового родителя, неоднозначный `new_block_name`, целевой блок не существует.

## Риски
Перенос между документами требует обновить `SourceFile` у самого узла и у всех его потомков (поддерево целиком переезжает). Учесть это явно в реализации и в тестах.
