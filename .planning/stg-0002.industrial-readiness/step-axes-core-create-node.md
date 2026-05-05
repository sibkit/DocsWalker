# stg-0002 — axes-core-create-node

## Цель
Переработать write-операции под оси: `create_node` принимает значения обязательных осей, `move_node` — изменение значения оси `path`. Удалить `add_ref_type`, заменить на `add_axis`. Снять упоминание `create_document` / `delete_document` в коде (запрет `delete_document_unsupported` тоже снимается — теперь это обычный `delete_node` на узле типа `document`).

## Файлы
`src/DocsWalker.Core/Api/WriteApi.cs`:
- `CreateNodeOp` — параметры: `type`, `title`, `body?`, `axisValues: Map<axisName, AxisValue>`. Применение: для каждой `must_have_axes` типа проверить, что значение задано и валидно по контракту оси; вычислить `path`-родителя и `block`; зарезервировать id; вставить узел.
- `MoveNodeOp` — выразить как изменение значения оси `path` (target + block). Семантика прежняя: запрет на вход в собственное поддерево, обновление SourceFile при смене документа.
- Удалить `AddRefTypeOp`, добавить `AddAxisOp` (имя, direction, cardinality, target_types, description; запрет коллизии с system-осями и default-блоками сохраняется).
- Удалить шаги `delete_document_unsupported` (теперь `delete_node` на корневом узле документа удаляет файл).
`src/DocsWalker.Core/Api/Transaction.cs` — разбор обновлённых операций.
`src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs`, `Cli/Commands.cs` — частичная адаптация (полный динамический разбор — следующий шаг).
`tests/DocsWalker.Tests/WriteApiTests.cs` — миграция тестов.

## Действия
1. Переписать `CreateNodeOp` под `axisValues`.
2. Переписать `MoveNodeOp` через значение оси `path`.
3. Удалить `AddRefTypeOp`, добавить `AddAxisOp`.
4. Удалить `delete_document_unsupported`-ветку.
5. Обновить `Transaction` под новый набор операций.
6. Адаптировать тесты (включая move-node и delete на корневом document-узле).

## Риски
Переписывание move-node — недавно завершённый шаг. Нужно сохранить семантику cross-document subtree update, но выразить её через ось `path` (значение оси сменилось → SourceFile у поддерева пересчитан).
