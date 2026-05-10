# step-02 — code-removal

**Статус:** [+]

## Цель

Удалить весь код server-side dedup (seen-set, session_id, persistence сессий, write-invalidation). Атомарный коммит. После этого шага `dotnet build` зелёная.

## Что сделано

### Удалены файлы целиком

- `src/DocsWalker.Core/Api/SeenScope.cs`
- `src/DocsWalker.Core/Sessions/SessionState.cs`
- `src/DocsWalker.Core/Sessions/SessionFile.cs`
- `src/DocsWalker.Core/Server/RequestContext.cs` (нёс только `SessionId`/`Sessions` — других потребителей нет)
- `src/DocsWalker.Cli/Cli/SessionId.cs`
- Папка `src/DocsWalker.Core/Sessions/` (опустошённая)

### Модифицированы файлы (`src/`)

- `src/DocsWalker.Kernel/RpcDispatcher.cs` — убраны `TryExtractSessionId`, генерация UUID, `RequestContext.Push`, параметр `sessionId` у `ExecuteWithCaptureAsync`, `using DocsWalker.Core.Server`.
- `src/DocsWalker.Kernel/Program.cs` — без правок (using оставлен, т.к. `StalePidDetector` живёт в той же namespace).
- `src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs` — убран вызов `SessionId.Resolve(argv)` и поле `session_id` в `arguments`.
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — убран параметр `noSeen`, вызовы `SeenScope.FromCurrentContext()`, `seen?.Commit()`, упрощены `GetNodes`/`GetByPath`/`GetSubtree`.
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — убран хук write-invalidation после `api.Apply`, `using DocsWalker.Core.Server`.
- `src/DocsWalker.Cli/Cli/Handlers/SchemaHandlers.cs` — убран `ResetSeen` на `get-usage-guide`, `using DocsWalker.Core.Server`.
- `src/DocsWalker.Cli/Cli/Handlers/ReplHandler.cs` — убрана генерация `sessionId`, подмешивание `--session-id=` в каждой строке REPL, упоминание session в баннере.
- `src/DocsWalker.Cli/Cli/Handlers/McpWrapperHandler.cs` — убрана генерация `sessionId`, подмешивание `session_id` в `tools/call.arguments`, `case "initialize"` (просто форвардится теперь), параметр `sessionId` у `ForwardOneAsync`.
- `src/DocsWalker.Cli/Cli/Commands.cs` — убран параметр `--no-seen` у `get-nodes` и `get-subtree`, переписаны `desc`/`examples`.
- `src/DocsWalker.Cli/Program.cs` — удалены `DispatchGetNodes`, `TryParseNoSeen`, упрощён `DispatchGetSubtree`, убран `"session-id"` из исключений валидации параметров.
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — переписан целиком: убраны `SeenScope?` и `bool noSeen` из всех перегрузок, удалены `AutoIncludeNodeToJson`, `SubtreeChildToJson`, `PlaceholderJson`, `AddAutoIncludesField` упрощён.
- `src/DocsWalker.Core/Api/WriteApi.cs` — убрано поле `TouchedIds` из `WriteResult`, упрощён возврат `Apply`.
- `src/DocsWalker.Core/Api/WriteState.cs` — убрано поле `_touchedIds` и метод `TouchedIds`, очищены `Add`/`Replace`/`Remove`.
- `src/DocsWalker.Cli/UsageGuide/UsageGuideText.cs` — переписан абзац про auto-include: убрано упоминание seen-фильтра/placeholder'ов, добавлен совет `fields=[title]+depth/tree`.

### Тесты (минимальные правки в этом шаге, остальное — step-03)

Чтобы `dotnet build` остался зелёным после удаления типов из `src/`, в этом же шаге пришлось:

- **Удалить**: `tests/DocsWalker.Tests/SeenScopeTests.cs`, `SessionsInfrastructureTests.cs`, `WriteInvalidationTests.cs` — эти файлы были в плане step-03, но без их удаления tests-проект не компилится после удаления типов. По духу страты атомарный шаг должен оставлять зелёную сборку, поэтому решение принято в step-02.
- **Поправить**: `tests/DocsWalker.Tests/AutoIncludeTests.cs` — удалены 2 метода с seen-сценариями (`AutoIncludeAlreadySeen_BecomesPlaceholder`, `NoSeenTrue_BypassesFilterForAutoIncludes`); остальные методы адаптированы под новые сигнатуры `ReadApiJson` (без `scope`/`noSeen`).
- **Поправить**: `tests/DocsWalker.Tests/McpArgvBuilderTests.cs` — заменён `--no-seen=false` в `BuildArgv_BooleanValue_TrueFalse` на `--quiet=false` (boolean-маршалинг по-прежнему проверяется).
- **Поправить**: `tests/DocsWalker.Tests/RedirectRefsTests.cs` — `createResult.TouchedIds.First(...)` заменено на `createResult.OpResults[0].Data["id"]!.GetValue<int>()` (`TouchedIds` удалён из публичного `WriteResult`).

### Сборка

```
dotnet build DocsWalker.slnx
> Сборка успешно завершена. Предупреждений: 0. Ошибок: 0.
```

## Расхождения со страт

- В step-02 пришлось затронуть тестовые файлы (что страт описывает как scope step-03). Альтернатива — нарушить criterion «`dotnet build` зелёная» в шаге. Финальная очистка тестов (если что-то осталось) — в step-03.
- `Kernel/Program.cs` правки `using` не понадобились — `StalePidDetector` в той же namespace `DocsWalker.Core.Server`, что и удалённый `RequestContext`. Using оставлен.

## Точка возобновления

После step-02 в коде нет ни одного упоминания seen/session_id/placeholder/--no-seen. `dotnet build` зелёная, `dotnet test` (step-03) ещё не запускался.
