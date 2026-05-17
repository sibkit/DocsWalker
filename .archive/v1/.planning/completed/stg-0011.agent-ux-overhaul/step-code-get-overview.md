# stg-0011 — code-get-overview

## Цель

Реализовать в kernel команду `get-overview`: возвращает global stat-картину графа без параметров. Поля ответа:

- `total_nodes` — всего узлов в графе (без synthetic root).
- `max_depth` — максимальная глубина path-дерева (root=0, листья дают max).
- `total_tokens` — сумма tokens всех узлов (`TokenCounter.CountNode`).
- `trees` — массив `{name, description?}` объявленных tree-scopes из Схемы; `description` опускается при пустом (правило #301). Флаги `addressable`/`default` в этом шаге не выводятся — это часть `docs-schema-classifier-trees`.
- `schema.types_count` — число типов в Схеме.
- `schema.top_types_by_count` — массив `{type, count}`, топ-5 типов по числу узлов; тип `root` исключён.
- `root_children` — массив `{id, type, title, subtree_tokens}` детей synthetic root в path-дереве.
- `hot_spots.largest_nodes` — top-5 узлов по `tokens` отдельного узла (кандидаты на разбиение); поле `tokens`, не `subtree_tokens` — иначе верх занимают top-level документы, а не «жирные» атомы.
- `hot_spots.most_connected_nodes` — top-5 узлов по `count(in_cross_refs) + count(out_cross_refs)`; tree-refs (`path` и пр. tree-scopes из Схемы) исключены — иначе хаб-метрика искажается path-детьми. Поле — `refs_count`.

## Файлы

- `src/DocsWalker.Core/Api/OverviewResponse.cs` — records `OverviewResponse`, `TreeOverview`, `TypeCount`, `RootChildOverview`, `HotSpotByTokens`, `HotSpotByRefs`.
- `src/DocsWalker.Core/Api/ReadApi.cs` — instance-метод `GetOverview()`.
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — `OverviewToJson(OverviewResponse)`.
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — `GetOverview(storagePath)`.
- `src/DocsWalker.Cli/Cli/Commands.cs` — регистрация `get_overview` как Read-команды без параметров.
- `src/DocsWalker.Cli/Program.cs` — dispatch `"get_overview"`.

## Действия

1. Спроектировать структуру ответа по спецификации из docs/ (узлы 403 definition, 404 example).
2. Реализовать `GetOverview()` в `ReadApi`: один проход по `_graph.ById.Values` собирает `tokens`, `byTypeCount`, `cross-in/out refs`; рекурсивный проход по path-дереву считает `subtree_tokens` и `max_depth`.
3. Сериализатор `OverviewToJson` — порядок ключей и имена снейк-кейс по примеру #404.
4. CLI-handler `ReadHandlers.GetOverview` + регистрация в `Commands` + ветка в `Program.cs Dispatcher`.
5. Unit-тесты (`tests/DocsWalker.Tests/ReadApiTests.cs`): саnity-агрегаты на реальных docs/, исключение типа `root` из top_types, исключение tree-refs из `most_connected`.
6. `mcp__glider__get_diagnostics` — чистая компиляция; `dotnet test` — вся suite зелёная.

## Риски

Подсчёт `total_tokens` и `subtree_tokens` требует обхода всех узлов и path-дерева. На 337 узлах ≈ мгновенно; на больших корпусах нужно кешировать tokens по узлу (вне scope шага).

Truncation-протокол (`max_tokens`, правило #406) в этом шаге не реализован — response заведомо маленький (порядка сотен токенов). Поддержка `max_tokens` для всех read-команд — отдельный шаг `code-compact-and-tokens`.
