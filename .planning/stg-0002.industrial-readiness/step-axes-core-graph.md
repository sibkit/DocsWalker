# stg-0002 — axes-core-graph

## Цель
Реализовать в `DocsWalker.Core` модель данных под оси: убрать спецкейсы `parent_id` / `parent_block` / `out_refs[ref_type]`, заменить единой коллекцией значений по осям.

## Файлы
`src/DocsWalker.Core/Schema/Axis.cs` — новый тип `Axis` (имя, направление, kind, cardinality, target_types).
`src/DocsWalker.Core/Schema/SchemaModel.cs` — добавить `IReadOnlyList<Axis> Axes`; в `TypeDefinition` — `MustHaveAxes` / `MayHaveAxes`.
`src/DocsWalker.Core/Schema/SchemaLoader.cs` — парсинг секции `axes` и полей `must_have_axes` / `may_have_axes` у типа.
`src/DocsWalker.Core/Graph/Node.cs` — заменить `ParentId` + `OutRefs` единой структурой `IReadOnlyDictionary<string, IReadOnlyList<AxisValue>>` (или симметричный `Axes`-член), где ключ — имя оси.
`src/DocsWalker.Core/Graph/AxisValue.cs` — новый record (target_id + опц. block/cardinality-параметры).
`src/DocsWalker.Core/Documents/DocumentLoader.cs` — при загрузке YAML строить `Axes`: ось `path` — из родительского узла, ось `default` (`definitions`/`examples`/`fields`/`content`) — из имени child-block, `out_refs` блок — в значения соответствующей explicit-оси.
`src/DocsWalker.Core/Validation/Validator.cs` — переписать проверки `refs` и `parent_block` под единое «значения обязательных осей валидны по контракту». Сохранить коды ошибок где возможно (`unique`, `sequence`).
`tests/DocsWalker.Tests/SchemaTests.cs`, `GraphTests.cs`, `ValidatorTests.cs` — миграция тестов.

## Действия
1. Завести `Axis`/`AxisValue` модели; обновить `SchemaModel`.
2. Переработать `SchemaLoader` под секцию `axes`.
3. Переработать `Node` — единая `Axes`-коллекция вместо `ParentId`/`OutRefs`.
4. Переработать `DocumentLoader` — при чтении YAML заполнять `Axes` из иерархии и блоков.
5. Обновить `Validator` — единая проверка `axis_value_invalid`.
6. Адаптировать тесты.

## Риски
Ломается весь Read API (get_nodes возвращает out_refs, get_refs — direction/type). Эти изменения покрываются шагами axes-core-create-node и axes-cli-dynamic-params; в этом шаге компиляция Read API уже сломается — это **ожидаемое промежуточное состояние** (см. strategy.md, «Стратегия рефакторинга — без shim»). Никакого переходного слоя не вводим.
