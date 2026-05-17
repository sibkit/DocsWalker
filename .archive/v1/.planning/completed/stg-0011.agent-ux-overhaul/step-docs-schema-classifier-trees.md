# stg-0011 — docs-schema-classifier-trees

## Цель

Объявить в Схеме проекта `docs/` четыре классификатор-дерева: `subject`, `csharp_structure`, `subsystem`, `audience`. Создать соответствующие типы-категории (узлы дерева). Добавить на типах-атомах (`rule`, `statement`, `definition`, `example`, `note`, `may_rule`, `llm_hint`) required tree-связи в каждый из четырёх классификаторов.

## Файлы

Схема проекта (через MCP):
- Через update Схемы — добавить trees, types-категории, out_refs на атом-типах.

## Действия

1. Декомпозиция: для каждого классификатора решить структуру корня и иерархии (плоское дерево первых уровней — субъект/подсистема, для `csharp_structure` — иерархия solution → project → namespace → class).
2. Через `transaction` пакет:
   - Создание корневых узлов классификаторов.
   - Создание узлов `none` в каждом классификаторе как first-class категории.
   - Декларация tree-scopes в Схеме (имена, опции `addressable`).
   - Добавление out_refs `subject`, `csharp_structure`, `subsystem`, `audience` на типах-атомах с `tree=<имя>, cardinality=one, required=true`.
3. `check-integrity` — после правки Схемы все существующие 337 узлов окажутся нарушающими required (нет классификаторов). Это ожидаемо и закрывается шагом `migrate-classifiers-data`.

## Риски

После применения правок Схемы граф невалиден до шага миграции. До запуска этой правки — финальная проверка готовности шагов реализации (`code-classifier-validator`, `migrate-classifiers-data`). Возможно — делать в одной пачке с миграцией данных.
