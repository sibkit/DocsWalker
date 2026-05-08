# step-02 — root-synthesis

**Статус:** [*]

## Цель

`Graph.GetById(0)` начинает возвращать синтезированный Node-объект; существующие read-команды (`get-nodes`, `get-refs`, `get-in-refs`, `get-subtree`, `get-ancestors`) подхватывают это автоматически. Write-команды отбивают любые операции над id=0 единым кодом `cannot_modify_root`. `UsageGuideText` упоминает id=0 как точку входа. Тесты обновляются.

## Конкретные правки

### `src/DocsWalker.Core/Graph/Graph.cs`

`GetById(int id)` — спец-кейс на 0:

```csharp
public Node? GetById(int id)
{
    if (id == Node.RootId)
    {
        return new Node(
            id: Node.RootId,
            typeName: "root",
            title: "docs",
            text: "Корень docs/. Briefing — get-usage-guide.",
            outRefs: ImmutableDictionary<string, ImmutableList<int>>.Empty,
            sourceFile: null,
            parentId: null);
    }
    return _byId.TryGetValue(id, out var n) ? n : null;
}
```

(Сигнатура Node — по факту в коде; `sourceFile`/`parentId` — null/none.)

### `src/DocsWalker.Core/Api/WriteApi.cs`

Найти все спец-кейсы по `Node.RootId` в write-операциях; привести к единому коду ошибки:

- `delete-nodes`: текущая ошибка «Корневой узел id=0 удалить нельзя» (~`WriteApi.cs:858`) → код `cannot_modify_root`, message «root (id=0) — синглтон, изменению не подлежит».
- `update-node`: добавить guard на старте — id=0 → `cannot_modify_root`.
- `move-node`: guard на `op.Id == 0` → `cannot_modify_root`.
- `create-ref` / `delete-ref`: guard на `from_id == 0` → `cannot_modify_root` (target_id == 0 валиден — это путь child→root в out_refs.path; ничего не блокируем).

### `src/DocsWalker.Cli/Cli/UsageGuideText.cs` (или где собирается текст guide)

Добавить в mental-model одну строку про id=0:
> «Граф начинается с id=0 (тип `root`, title `docs`). Каждый top-level узел имеет `out_refs.path=[0]`.»

### Тесты

- `tests/DocsWalker.Tests/...` — найти тесты, которые завязаны на `GetById(0) == null` (в т.ч. `WriteApiTests`, `ReadApiTests`, `ValidationTests`); обновить под новое поведение.
- Новый тест: `GetNodes_RootId_ReturnsSyntheticNode` — проверка структуры.
- Новый тест: `GetInRefs_RootId_PathName_ReturnsAllTopLevel` — обратный индекс работает.
- Новый тест: write-операции на id=0 → `cannot_modify_root`.

## Acceptance

- `docswalker get-nodes --ids=0` отдаёт `[{id:0, type:"root", title:"docs", text:"…", out_refs:{}}]`.
- `docswalker get-in-refs --id=0 --name=path` возвращает массив top-level документов и folders.
- `docswalker update-node --id=0 --title=foo` → exit ≠ 0, `{"code":"cannot_modify_root", ...}`.
- `dotnet test` — все тесты зелёные (162 текущих + новые).
- `docswalker check-integrity` — `{"ok":true,"errors":[]}`.

## Что не делается на этом шаге

- `get-map` остаётся плоским списком документов (см. решение #3 в strategy.md).
- Обратной совместимости не делаем — поведение `GetById(0)` меняется one-shot.
