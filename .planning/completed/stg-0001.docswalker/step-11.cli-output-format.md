# stg-0001 — cli-output-format

## Цель
Сократить токены в stdout-выводе CLI для LLM-потребителя: убрать Unicode-escape кириллицы и убрать лишний уровень вложенности `{operations: [{op, data}]}` для одиночных write-команд. Шейп `transaction` оставить массивом — это семантическая особенность пачки.

## Файлы
`docs/DocsWalker.yml` — раздел `(#35) CLI-интерфейс`: уточнить правило формы успеха (`result` — структура, специфичная для команды) и добавить правило формы `transaction` (массив объектов с полем `op`).
`src/DocsWalker.Cli/Cli/Output.cs` — настроить `JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, прокинуть в source-gen контекст `CliJsonContext`.
`src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — для одиночных write-команд возвращать `op.Data` напрямую в `result`; для `transaction` собирать массив `{op, ...flat data}`.

## Действия
1. Обновить `(#35)` в `docs/DocsWalker.yml` через `update-node` CLI (write-API DocsWalker, без прямой правки YAML).
2. В `Output.cs`: создать `JsonSerializerOptions` поверх `CliJsonContext.Default.Options` с `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`; конструировать `CliJsonContext` от этих опций; использовать его при сериализации.
3. В `WriteHandlers.cs`: разделить пути `Run` (одиночная команда) и `RunMany`/`Transaction` (массив операций) — у одиночной `result = op.Data`, у `transaction` — массив `{op: <имя>, ...поля результата}`.
4. Прогнать сборку и существующие тесты — они работают на уровне `WriteApi` и не должны падать.
5. Smoke: запустить CLI-команду на изолированном `docs/`, убедиться, что вывод плоский и кириллица raw.

## Риски
- Шейп вывода — публичный контракт CLI. Изменение ломает любых потребителей, ориентированных на старую обёртку. Сейчас потребитель один — LLM в этой сессии, риск минимальный.
- `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` пропускает HTML-чувствительные символы (`<`, `>`, `&`, `'`) без escape. Для CLI-stdout (не HTML-контекст) это корректно и желаемо; «Unsafe» в названии — про HTML-injection, не про общую безопасность.
