# stg-0011 — code-update-schema-command

## Цель

Добавить в kernel MCP-команду `update-schema(yaml_text)` — atomic replacement Схемы проекта. До этого Схема правилась только вручную (через файл `docs/Схема.yml`), что нарушало правило проектного CLAUDE.md «доступ к `docs/**/*.yml` только через DocsWalker MCP-tools». Команда даёт LLM-агенту единственный официальный канал управления Схемой.

## Контракт

Параметры:
- `yaml_text` (string, required) — полный YAML-текст Схемы (заменяет содержимое файла целиком).
- `dry-run` (boolean, optional, default false) — валидирует, но не пишет.

Серверная валидация (в указанном порядке, ошибка → rollback):
1. YAML парсится (синтаксис).
2. `SchemaLoader.LoadSchema` — соответствие meta-schema (типы/ref-defs/trees правильно объявлены).
3. `MetaSchemaCheck` — финальный structural-check.
4. `check-integrity` на текущем графе с новой Схемой — все существующие узлы валидны (если нет — ошибка `schema_breaks_graph` с перечислением узлов, которые сломаются).

Atomic-write через `File.WriteAllText` + reload Схемы в graph (`AttachSchema`). Если step 4 фейлит — Схема на диск не пишется, graph остаётся со старой Схемой.

Ответ (success): `{applied: true|false, schema_summary: {types_count, trees_count}}`.
Ответ (error): обычный error-envelope с кодом и hint.

## Файлы

- `src/DocsWalker.Core/Api/WriteApi.cs` — новый метод `UpdateSchema(string yamlText, bool dryRun)`.
- `src/DocsWalker.Core/Api/WriteState.cs` — если требуется, поддержка swap Схемы в transaction-state.
- `src/DocsWalker.Cli/Cli/Commands.cs` — регистрация команды `update_schema`.
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — handler.
- `src/DocsWalker.Cli/Program.cs` — dispatch.
- `tests/DocsWalker.Tests/UpdateSchemaTests.cs` — unit-тесты.

## Действия

1. Реализовать `WriteApi.UpdateSchema`:
   - Принять yaml_text.
   - Распарсить через `SchemaLoader.LoadSchemaFromString` (если есть; иначе через временный stream).
   - Прогнать `MetaSchemaCheck`.
   - Прогнать `Validator.Validate` с новой Схемой на текущем графе.
   - Если ok и не dry-run: `File.WriteAllText(SchemaPath, yamlText)`, `AttachSchema(graph, newSchema)`.
2. Зарегистрировать `update_schema` в `Commands.All` как `Write`-команду.
3. Dispatch + Handler.
4. Тесты:
   - Корректный YAML с trees+types → applied=true, файл и graph обновлены.
   - Невалидный YAML (sytnax) → ошибка `invalid_yaml`.
   - Schema не соответствует meta-schema → `schema_validation_failed`.
   - Schema ломает существующий graph → `schema_breaks_graph` + перечисление узлов.
   - dry-run → applied=false, файл не изменён.

## Риски

- Сериализация Схемы в YAML на стороне LLM — потенциально хрупка (LLM может потерять форматирование). Atomic-update требует, чтобы LLM генерировал валидный YAML. Mitigation — пользоваться `get-schema` + локальный merge у LLM, либо позже добавить granular API.
- Hot-swap Схемы при работающем graph: если новая Схема убирает type, существующие узлы этого типа → ошибки. Команда обязана это поймать на шаге 4 валидации.

## Влияние на стратегию

Шаги `docs-schema-classifier-trees` и `migrate-classifiers-data` зависят от этой команды — после её появления LLM может через MCP объявить classifier-trees и продолжить миграцию данных в той же сессии.
