# step-01 — spec-in-docs

**Статус:** [+]

## Цель

Атомарно сдвинуть мета-уровень: bump мета-схемы v5 → v6, объявить тип `root` в Схеме, описать семантику id=0 в `DocsWalker.yml`. Минимальные правки парсера, чтобы новая версия мета-схемы загружалась.

## Конкретные правки

### `docs/.docswalker/meta-schema.yml`

1. `meta_schema_version: 5` → `meta_schema_version: 6`.
2. Constraint строки 35 переписать:
   - **Старый:** `'Имя ''root'' зарезервировано: обозначает корневой синглтон ядра DocsWalker (узел с id=0). Не объявляется как тип, но допустимо в target_types.'`
   - **Новый:** `'Имя ''root'' зарезервировано за корневым синглтоном ядра DocsWalker (узел с id=0). Объявляется как обычный type_definition с особенностью: отсутствует обязательная связь name=path (см. constraint типа).'`
3. Constraint строки 82 (`'Связь name=path обязана иметь tree=path; присутствует у каждого type_definition кроме типа root.'`) — без изменений, теперь самосогласован.

### `docs/Схема.yml`

В начало списка `types:` добавить:

```yaml
  - name: root
    description: Корневой синглтон графа (id=0). Точка входа для LLM. Не персистится в YAML — синтезируется ядром на лету. title — имя FS-папки docs/ (берётся как dirname).
    title_source: dirname
    text_required: false
    out_refs: []
```

### Парсер мета-схемы и валидатор Схемы

- Найти константу/проверку «supported meta_schema_version» — поднять до 6.
- Найти спецкейс «root cannot be declared as type» в SchemaLoader/валидаторе — снять.
- Constraint «у type_definition есть `name=path` ref» — должен уже исключать root по name; убедиться, что после декларации root проходит без ошибок.

(Это минимальные правки, без которых обновлённый YAML не загрузится. Полная синтез-логика и поведение — в step-02.)

### `docs/DocsWalker.yml` — узлы про root

Через `docswalker create-node` (после правки парсера, чтобы Схема загружалась) добавить 2–3 узла:

1. **statement** про id=0 как синглтон: «Граф docs/ имеет единственный root — id=0, тип root. Каждый top-level узел (document или folder) имеет out_refs.path=[0].»
2. **statement** про синтез: «Root не персистится в YAML — генерируется ядром на лету.»
3. **rule** про точку входа: «LLM, наткнувшись на id=0 в out_refs.path, может зафетчить узел через get-nodes ids=[0] и получить pointer на get-usage-guide для briefing.»

Точная секция для размещения — в существующих секциях DocsWalker.yml; кандидаты: «Уровни схемы» или «Граф» (если такая есть). Конкретный текст может быть скорректирован при размещении под стиль соседних узлов.

## Способ внесения

- **meta-schema.yml, Схема.yml, парсер C#** — прямой `Edit` (нет API на правку Схемы / мета-схемы).
- **DocsWalker.yml** — через `docswalker create-node` (dogfooding). Подразумевает старт `docswalker run` в фоне после того, как парсер пропускает v6.

## Acceptance

- `dotnet build` — зелёный.
- `docswalker check-integrity` — `{"ok":true,"errors":[]}`.
- `docswalker describe-type --name=root` возвращает контракт нового типа.
- В `docs/DocsWalker.yml` появились узлы с семантикой id=0.

## Что не делается на этом шаге

- `Graph.GetById(0)` пока продолжает возвращать null — поведение синтеза в step-02. Тесты, которые на это завязаны, тоже остаются как есть до step-02.
- `UsageGuideText.cs` — step-02.
- Write-API гарды — step-02 (ничего нового по поводу root.id=0 здесь не вносится, поведение существующих гардов прежнее).
