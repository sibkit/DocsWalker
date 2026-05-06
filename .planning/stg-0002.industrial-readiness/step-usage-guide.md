# stg-0002 — usage-guide

## Цель
Read-команда `get-usage-guide`: единый manifest, который LLM-агент дёргает в начале сессии. Содержит:
- Краткую ментальную модель (граф + tree-scopes), отсылку на доменный раздел в `docs/DocsWalker.yml`.
- Список деревьев (из `Schema.Trees`).
- Manifest всех команд (имя, краткая семантика, параметры с типами, типичные ошибки, 1-2 примера).
- Слепок состояния графа: общее число узлов, перечень корневых узлов первого уровня (от root), число типов в Схеме.

## Форма ответа

```yaml
mental_model: <русский текст из UsageGuideText, краткая выжимка модели>
trees:                        # из Schema.Trees
  - name: path
    description: ...
  - name: <other>
    description: ...
commands:
  - name: <command>
    kind: read | write | structural
    description: <одна-две фразы>
    parameters:
      - name: <param>
        type: int | string | bool | csv<int> | enum<...>
        required: bool
        description: ...
    errors:
      - code: <error_code>
        when: <короткое описание условия>
    examples:
      - <текст команды>
graph_snapshot:
  total_nodes: <int>
  root_children:
    - id: <int>
      type: <type>
      title: <title>
  schema_types_count: <int>
```

## Файлы
- `docs/DocsWalker.yml` — раздел «Операции чтения»: новый пункт `get_usage_guide` с описанием формы ответа.
- `src/DocsWalker.Cli/UsageGuide/UsageGuideText.cs` (новый) — статическая константа с инструкцией для LLM (русский текст: модель, do/don't, типичные паттерны). Дублирует базовое из docs-llm-guide, но в краткой форме (выжимка).
- `src/DocsWalker.Core/Api/IUsageGuideSource.cs` (новый) — интерфейс, через который ядро получает manifest команд. CLI реализует, передаёт в Core при старте.
- `src/DocsWalker.Core/Api/ReadApi.cs` — метод `GetUsageGuide()`, собирает ответ из:
  - `_usageGuideSource.GetMentalModel()` → текст
  - `Schema.Trees` → блок trees
  - `_usageGuideSource.GetCommands()` → manifest команд
  - `Graph.Snapshot()` → graph_snapshot
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — сериализация ответа.
- `src/DocsWalker.Cli/Cli/Commands.cs` — реализация `IUsageGuideSource`: возвращает manifest команд по `Commands.All`. Описание команд — короткая фраза + параметры из их же определений + 1-2 примера (захардкожены в Commands.cs рядом с описанием каждой команды).
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs`, `Program.cs` — регистрация и DI `IUsageGuideSource` → `ReadApi`.
- `tests/DocsWalker.Tests/ReadApiUsageGuideTests.cs` — тесты.

## Действия
1. Зафиксировать команду в `docs/DocsWalker.yml`.
2. Завести `IUsageGuideSource` интерфейс в Core.
3. Завести `UsageGuideText.cs` в CLI с краткой выжимкой ментальной модели (≤30 строк).
4. Расширить определения команд в `Commands.cs` полями `Description`, `Examples`, `Errors` (если их там ещё нет).
5. Реализовать `ReadApi.GetUsageGuide` через `IUsageGuideSource` + `Schema.Trees` + `Graph` snapshot.
6. Подключить в CLI.
7. Тесты: ответ содержит все ожидаемые секции; все актуальные команды (delete-nodes, redirect-refs, move-node с --tree, get-subtree с --tree, describe-type, dry-run flag и т.д.) присутствуют; trees содержит как минимум path; graph_snapshot непуст.

## Риски
- Дублирование описаний команд между Commands.cs и `docs/DocsWalker.yml` — при расхождении LLM получает одно, валидатор integrity другое. Можно подумать про единый источник (например, описание в YAML рядом с типом, парсится Schema), но это отдельный шаг — пока два источника, синхронизация ручная и проверяется при ревью.
- `IUsageGuideSource` — DI проброс: при первом запуске CLI из тестов (без полного wiring) UsageGuide может быть недоступен. Тесты должны мокать или собирать минимальный manifest.
- При появлении новых команд в будущем — добавлять описание в Commands.cs обязательно, иначе LLM их «не увидит».
