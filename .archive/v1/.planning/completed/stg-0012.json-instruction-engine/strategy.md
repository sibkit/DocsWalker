# stg-0012 - json-instruction-engine

**Статус:** активна

## Состояние на сброс сессии

Спецификация LLM-facing JSON API создана в docs как отдельный документ:

- `DocsWalker-LLM JSON API`
- документ создан через DocsWalker MCP;
- `check-integrity` после записи: `ok=true`;
- git показывает штатные docs-изменения: новый
  `docs/DocsWalker-LLM JSON API.yml` и обновленный
  `docs/.docswalker/sequence.txt`.

Главное решение: новый API не заменяет внутренний low-level `transaction`, а
становится LLM-facing слоем над текущими read/write возможностями kernel-а.

## Handoff после шага 03

Текущий статус: шаги `(01)`, `(02)` и `(03)` завершены. Следующий шаг -
`(04) code-coordinate-resolver`.

Что уже сделано в коде:

- `src/DocsWalker.Core/Api/LlmJsonApiModel.cs` - DTO/AST и parser для request
  envelope, defaults, ops, selectors, targets, set, relation patches и
  response DTO.
- `src/DocsWalker.Core/Api/LlmJsonApiPathResolver.cs` - path-resolver для
  LLM-facing API: full/relative path через `defaults.path_parent`, exact lookup,
  wildcard `*`/`**`, ошибки `ambiguous_path_scope`, `not_found`,
  `ambiguous_selector`, resolved path для id.
- `tests/DocsWalker.Tests/LlmJsonApiModelTests.cs` - тесты parser/model.
- `tests/DocsWalker.Tests/LlmJsonApiPathResolverTests.cs` - тесты exact path,
  defaults, wildcard one/any depth, ambiguous single target, `not_found`,
  `id -> path`.
- `tests/DocsWalker.Tests/RootSynthesisTests.cs` - убран hardcode количества
  top-level docs-документов; тест теперь сравнивает root children с фактическими
  `path` inrefs.

Что важно для следующего шага:

- Path-resolver намеренно не фильтрует coordinates и не знает alias scope.
  Coordinates идут в `(04)`, alias ordering и `$alias` - в `(05)`.
- `coordinates.type` в docs утвержден как structural type/contract LLM API.
  На текущем первом инкременте наиболее прямой mapping - строка
  `coordinates.type` к существующему `Node.TypeName`; остальные coordinates
  должны резолвиться через classifier trees (`subject`, `subsystem`,
  `audience`, `csharp_structure`).
- Нужно проверить текущие C# типы `SchemaDocument`, `TreeDefinition`,
  `TypeDefinition`, `RefDef`, `Graph.GetScopeChildren/GetScopeParent` и
  существующие `ReadApi.Find` / `TreeFilter`, чтобы не дублировать уже готовую
  classifier-логику.

Последние проверки:

- `curl.exe http://127.0.0.1:18080/health` -> `ok=true`;
- DocsWalker MCP `check-integrity` -> `{"ok":true,"errors":[]}`;
- `dotnet test .\tests\DocsWalker.Tests\DocsWalker.Tests.csproj --no-restore --filter LlmJsonApi`
  -> 12/12 passed;
- `dotnet test .\DocsWalker.slnx --no-restore` -> 244/244 passed;
- Glider diagnostics -> 0.

Рабочее дерево содержит незакоммиченные изменения от этой стратегии и
предыдущих подготовительных шагов. Не откатывать чужие/предыдущие изменения:
продолжать поверх текущего состояния.

## Утвержденный LLM API v1

Внешняя поверхность состоит из трех методов:

- `hit` - безопасная проверка selector-ов или будущих write-ops без записи;
- `query` - чтение данных и контекста;
- `tx` - атомарное внесение изменений.

Форма запроса:

```json
{
  "method": "hit|query|tx",
  "defaults": {
    "path_parent": "...",
    "coordinates": {}
  },
  "ops": []
}
```

`ops` всегда массив, даже для одной операции.

## Термины v1

В LLM-facing API нет термина `address`.

- `path` - единственный уникальный человекочитаемый путь узла в v1.
- `coordinates` - классификационные оси, значения только строки.
- `coordinates.type` - структурный тип/контракт узла для LLM API.
- `relations` - именованные смысловые связи между узлами.

Storage tree и физическая материализация docs/ не фигурируют в JSON API. Это
внутренняя реализация DocsWalker.

## Defaults

`defaults` содержит только:

- `path_parent`;
- `coordinates`.

Если `defaults.path_parent` задан, `op.path` обязан быть относительным путем
внутри этого parent. Полный `op.path` вместе с `defaults.path_parent` дает ошибку
`ambiguous_path_scope`.

Если `defaults.path_parent` не задан, `op.path` должен быть полным path.

Per-op override для `path_parent` в v1 не делается.

## Допустимые ops

Для `hit`:

- `select`;
- write-ops для проверки без записи: `create`, `update`, `delete`, `move`,
  `link`, `unlink`.

Для `query`:

- только `select`.

Для `tx`:

- `select` для объявления alias;
- `create`;
- `update`;
- `delete`;
- `move`;
- `link`;
- `unlink`.

Операции с суффиксом `_many` в v1 не вводятся.

## Выбор целей

`select` может содержать:

- `path` exact или wildcard pattern с `*` / `**`;
- `coordinates`;
- `expect` для явной проверки cardinality.

Отдельного `select.type` нет. Тип фильтруется через
`select.coordinates.type`.

`as` разрешен у `select` и `create`. Alias доступен только последующим операциям
в порядке `ops[]`.

## Cardinality write-операций

`update` и `delete` выбирают цели через ровно одно из:

- `id`;
- `ids`;
- `path`;
- `target`;
- `select`.

Если `expected_count` не задан, выбор должен дать ровно один узел.

Если `expected_count` задан, выбор должен дать ровно `expected_count` узлов.
Несовпадение дает `count_mismatch`, `tx` ничего не применяет.

`ids` используется для явного перечисления нескольких целей и требует
`expected_count`.

`move`, `link` и `unlink` в v1 одиночные:

- `move` source должен резолвиться в один узел, `to` - exact path без wildcard;
- `link` создает одну relation между одиночными `from` и `to`;
- `unlink` удаляет одну relation между одиночными `from` и `to`;
- несколько связей выражаются несколькими `link`/`unlink` внутри одного `tx`.

## Write semantics

`create`:

- создает новый узел по exact path;
- содержимое задается только в `set`;
- `coordinates.type` обязателен в `set.coordinates` или
  `defaults.coordinates`;
- `title` не передается отдельно, kernel выводит его из последнего сегмента
  `path`;
- `relations` в `create.set` задают начальный набор связей и могут использовать
  короткую форму массива.

`update`:

- меняет `text`, `coordinates`, `relations`;
- не меняет `id`;
- не меняет `path`/`title`;
- `update.set.relations` использует только явные режимы `add`, `remove`,
  `replace`.

`move`:

- переносит или переименовывает один узел;
- не меняет `id`;
- `to` exact path, wildcard запрещен;
- отсутствующий parent дает `path_parent_not_found`.

Автосоздания в v1 нет:

- отсутствующий path parent дает `path_parent_not_found`;
- отсутствующая coordinate ветка дает `unknown_coordinate` без candidates;
- missing relation targets не создаются silently.

## Ответы

Все методы возвращают единый envelope.

Успех:

```json
{
  "ok": true,
  "method": "tx",
  "base_revision": 812,
  "summary": "...",
  "results": []
}
```

Ошибка:

```json
{
  "ok": false,
  "method": "tx",
  "code": "...",
  "message": "...",
  "details": {}
}
```

`tx` атомарен: если любая операция из `ops[]` не проходит resolve, schema
constraints или финальную validation-проверку графа, не применяется ничего.

Коды, уже зафиксированные в docs:

- `not_found`;
- `ambiguous_selector`;
- `count_mismatch`;
- `already_exists`;
- `unknown_coordinate`;
- `path_parent_not_found`;
- `ambiguous_path_scope`;
- `unknown_alias`.

## Implementation anchors

Glider solution load успешен: `DocsWalker.slnx`, 5 проектов.

Ключевые текущие файлы:

- `src/DocsWalker.Core/Api/WriteApi.cs` - текущие low-level write ops и
  атомарное применение.
- `src/DocsWalker.Core/Api/Transaction.cs` - парсер существующего
  low-level transaction JSON.
- `src/DocsWalker.Core/Api/ReadApi.cs` - чтение, tree/path/search/find API.
- `src/DocsWalker.Kernel/RpcDispatcher.cs` - JSON-RPC/MCP tools/call routing.
- `src/DocsWalker.Kernel/CommandsToTools.cs` - список MCP tools и inputSchema.
- `src/DocsWalker.Core/Mcp/McpArgvBuilder.cs` - конвертация MCP arguments в
  argv для существующих CLI-команд.
- `tests/DocsWalker.Tests/RpcDispatcherTests.cs` - тесты JSON-RPC routing.
- `tests/DocsWalker.Tests/*Write*`, `*Transaction*`, `*ReadApi*` - ближайшая
  зона тестирования.

Предпочтительная реализация: добавить новый LLM-facing слой в Core, который
парсит `hit/query/tx`, резолвит selectors/path/coordinates/aliases и
компилирует `tx` в существующие `WriteOp` / `WriteApi.Apply(...)`.

Не нужно сразу ломать существующие CLI/MCP commands. Новый API должен
сосуществовать с текущими низкоуровневыми командами.

## План внедрения

- [+] (01) docs-json-api-spec - создать отдельный docs-документ с контрактом
  `hit/query/tx`, полями запросов, ответами и safety rules.
- [+] (02) code-llm-model - добавить DTO/AST для request envelope, defaults,
  ops, selectors, targets, set, relation patches и responses.
- [+] (03) code-path-resolver - реализовать full/relative path resolution,
  `defaults.path_parent`, exact path lookup, wildcard `*`/`**`,
  `ambiguous_path_scope`, `not_found`, `ambiguous_selector`.
- [+] (04) code-coordinate-resolver - реализовать string-only coordinates,
  включая `coordinates.type` -> текущий schema node type / structural contract.
- [+] (05) code-alias-scope - реализовать порядок `ops[]`, `as` у `select` и
  `create`, ссылки `$alias`, ошибку `unknown_alias`.
- [+] (06) code-hit - реализовать `hit`: count, tokens, subtree_tokens,
  breakdown по `coordinates.type`, compact samples, validation,
  `would_change` для write-ops без записи.
- [+] (07) code-query - реализовать `query.select`: compact default и include
  для `text`, `relations`, `coordinates`, `ancestors`, `children`,
  `type_contract`, с `max_tokens`.
- [+] (08) code-tx-create - компилировать `create` в текущий `CreateNodeOp`:
  title из path, required refs из coordinates/relations, already_exists,
  path_parent_not_found.
- [+] (09) code-tx-update-delete - компилировать `update`/`delete` в текущие
  low-level ops, поддержать `expected_count`, `ids`, relation patches.
- [+] (10) code-tx-move - компилировать `move` в `MoveNodeOp` / rename path:
  source single-match, `to` exact, id не меняется.
- [+] (11) code-tx-link-unlink - компилировать одиночные `link`/`unlink` в
  `CreateRefOp` / `DeleteRefOp`.
- [+] (12) code-envelope-errors - единый envelope ответа и mapping ошибок
  LLM API.
- [+] (13) code-kernel-surface - добавить MCP/JSON-RPC tools `hit`, `query`,
  `tx` или один dispatcher path, в зависимости от минимального изменения в
  текущей архитектуре `CommandsToTools` / `RpcDispatcher`.
- [+] (14) tests-llm-json-api - покрыть request parsing, defaults,
  path wildcard, coordinates.type, aliases, expected_count, hit/query envelopes,
  tx atomicity и validator rejection.
- [+] (15) docs-sync-after-code - после реализации синхронизировать docs, если
  фактическое поведение отличается от документа.
- [+] (16) smoke - `curl /health`, `check-integrity`, targeted tests,
  `dotnet test .\DocsWalker.slnx --no-restore`, git status.

## Открытые инженерные вопросы перед кодом

- Как именно Схема объявит маппинг `coordinates.type` -> текущий schema node
  type: через существующие classifier trees или через новый schema-level alias?
- Нужно ли в первом инкременте поддерживать все include-поля `query`, или
  начать с compact + `text` + `relations` + `coordinates`?
- Делать `hit/query/tx` как три MCP tools или как один low-level kernel command
  с `method` внутри payload? В docs утверждены методы API, но transport surface
  можно выбрать минимально инвазивно.

## Definition of Done

- Docs-документ `DocsWalker-LLM JSON API` остается source of truth для v1.
- LLM может создать узел через `tx` без ручного подбора id: `path`,
  `coordinates.type`, `set.text`, `relations`.
- `hit` позволяет заранее оценить selectors/write-ops без полной загрузки
  текста.
- `query` возвращает compact по умолчанию и расширенный контекст по `include`.
- `tx` атомарно применяет `create`, `update`, `delete`, `move`, `link`,
  `unlink`.
- Wildcard path не применяется бесконтрольно: single-match без
  `expected_count`, exact count при `expected_count`.
- Все изменения проходят через существующий validator и не ломают текущие
  low-level команды.
