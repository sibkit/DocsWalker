# stg-0011 — docs-classifiers-model

## Цель

Описать в `docs/` модель multi-tree-классификаторов как абстрактную концепцию, до объявления конкретных деревьев. Зафиксировать: что такое classifier-tree, какова семантика узла `none` (first-class категория, не пустота), почему tree-связи классификаторов `required=true`, какое поведение у фильтра «выбор узла включает весь подграф потомков».

## Файлы

`docs/` (через MCP):
- Документ id=1 (`DocsWalker`) — новая section `Multi-tree классификаторы`.
- Внутри section — definitions и rules.
- Примеры в JSON-стиле.

## Действия

1. `create-node` type=section path=1 title=`Multi-tree классификаторы` text пустой (smysl в дочерних атомах).
2. Внутри:
   - definition `classifier_tree` — структура и роль.
   - definition `none_category` — first-class категория-сирота.
   - rule `Обязательность классификатора` — required tree-связь с возможностью указать `none`.
   - rule `Фильтр по узлу включает подграф` — семантика `--in-tree=name:<id>`.
   - examples под каждое правило (с required.examples).
3. `check-integrity` — нет ли нарушений required-связей.

## Риски

Концепция multi-tree уже частично описана в существующих узлах (mental_model упоминает tree-scope). Возможна избыточность — sweep по существующим узлам про tree-scopes до создания новых.
