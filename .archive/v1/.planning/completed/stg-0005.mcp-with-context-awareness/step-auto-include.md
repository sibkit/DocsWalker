# stg-0005 — auto-include

## Цель
При read-командах сервер транзитивно подгружает в результат цели всех non-tree required cross-refs узла (auto-include = `tree == null && required == true`). Подтянутые узлы проходят через dedup-фильтр шага read-dedup-placeholder.

## Файлы
`src/DocsWalker.Core/Api/ReadApi.cs` — добавление транзитивного обхода в результат `get-nodes`, `get-subtree`, `get-by-path`.
`src/DocsWalker.Core/Schema/RefDef.cs` — helper `IsAutoInclude` (= `tree == null && required == true`).
`src/DocsWalker.Core/Api/UsageGuideText.cs` — упомянуть в манифесте, какие связи на текущей Схеме являются auto-include.

## Действия
1. После сбора прямого результата команды: для каждого узла обойти его out_refs; для каждой связи с `IsAutoInclude == true` добавить целевые узлы в результат.
2. Обход транзитивный: подтянутые auto-include-узлы тоже обходятся. До фикс-точки. Использовать local seen-set обхода для предотвращения циклов внутри одного ответа.
3. Auto-include-узлы помечаются как «транзитивно подтянутые» — проходят через dedup-фильтр шага read-dedup-placeholder (могут стать placeholder).
4. На текущей Схеме auto-include активирует только `rule.examples` — единственный non-tree required cross-ref.
5. `--no-seen=true` на `get-nodes` отключает dedup-фильтр для прямых id и их auto-include-целей; auto-include как таковой остаётся.
6. В `UsageGuideText.MentalModel` упомянуть правило auto-include.

## Риски
- Если в будущем кто-то объявит non-tree required связь на массивную цель (например, `document.sections`), один get-nodes вернёт большой кусок графа. На текущей Схеме безопасно; контроль остаётся за тем, кто правит Схему руками.
