# stg-0005 — read-dedup-placeholder

## Цель
На read-командах сервер заменяет уже-выданные узлы placeholder'ом `{"id":N,"seen":true}`. Прямые запросы по id всегда полные. `--no-seen=true` на `get-nodes`. Первый `get-usage-guide` в session_id сбрасывает seen.

## Файлы
`src/DocsWalker.Core/Api/ReadApi.cs` — обработчики `get-nodes`, `get-subtree`, `get-by-path`, `get-usage-guide`: фильтрация ответов по seen-state сессии.
`src/DocsWalker.Core/Serialization/NodeSerializer.cs` — placeholder-форма для seen-узлов.
`src/DocsWalker.Cli/Handlers/GetNodesHandler.cs` — флаг `--no-seen=true`.
`src/DocsWalker.Cli/Handlers/GetSubtreeHandler.cs`, `GetByPathHandler.cs` — отвергать `--no-seen` как `unknown_parameter`.

## Действия
1. После выполнения read-команды собрать список id всех узлов в payload (deep walk: `nodes[]` на верхнем уровне, `children` в subtree).
2. Разделить id на «прямо запрошенные» (значения параметров `--ids`, `--id`, корень subtree) и «транзитивно подтянутые» (children, auto-include-цели — на следующем шаге).
3. Для каждого транзитивного id: если он в `seen[session_id]` — заменить узел на placeholder `{"id":N,"seen":true}` без других полей.
4. Прямые id никогда не фильтруются — LLM явно просит.
5. Все id из payload (включая placeholder и прямые) добавить в `seen[session_id]`; пометить session как dirty.
6. Внутри одного ответа узел появляется полным только при первом упоминании в pre-order обходе; повторы — placeholder.
7. `--no-seen=true` на `get-nodes`: payload не фильтруется, но seen всё равно обновляется.
8. На `get-subtree` / `get-by-path` параметр `--no-seen` отвергается как `unknown_parameter` (не имеет смысла на дереве).
9. `get-usage-guide`: при вызове в session_id первым делом `ResetSeen(session_id)` (см. (#298) — guide вызывается как маркер начала логической сессии); затем обычная выдача guide.
10. Если session_id отсутствует (env пуст, нет `--session-id`): фильтрация не применяется — payload как сейчас.

## Риски
- После /compact LLM может потерять полную копию узла, но сервер этого не знает — placeholder вернётся «слепым». Override `--no-seen=true` на конкретный id покрывает; требует, чтобы LLM понимала, когда им пользоваться (документировать в LLM-секции).
