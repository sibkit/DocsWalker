# stg-0002 — dry-run

## Цель
Общий флаг `--dry-run` для всех write-команд (одиночных и `transaction`): прогоняет pipeline до `AtomicWriter` включительно (резервирование id, применение операций, сборка графа, валидация), но не пишет на диск. Позволяет LLM проверить «сработает ли» перед реальной записью.

## Файлы
`docs/DocsWalker.yml` — раздел «CLI-интерфейс» (#35): описать общий флаг `--dry-run` и форму ответа (`applied: false`, `result` как при успехе либо `error` как при отказе).
`src/DocsWalker.Core/Api/WriteApi.cs` — параметр `dryRun` в `Apply` (по умолчанию `false`); при `true` — пропустить вызов `AtomicWriter.WriteAll` и шаг записи sequence.
`src/DocsWalker.Cli/Program.cs` — общий парсинг `--dry-run` (как `--root`).
`src/DocsWalker.Cli/Cli/Output.cs` — поле `applied` в success-ответе write-команд (всегда присутствует, по умолчанию `true`).
`src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — проброс `dryRun` во все обработчики.
`tests/DocsWalker.Tests/WriteApiTests.cs`, `TransactionTests.cs` — тесты: dry-run возвращает результат и не меняет файлы.

## Действия
1. Зафиксировать в `docs/DocsWalker.yml`: общий флаг и форма ответа.
2. Расширить `WriteApi.Apply` параметром `dryRun`. При `true` остановиться после валидации, вернуть `WriteResult` с пометкой.
3. Добавить общий разбор `--dry-run` в `Program.cs`, рядом с `--root`.
4. Включить поле `applied` в JSON success-ответа write-команд.
5. Тесты: одиночная команда с dry-run (граф и файлы не изменились, ответ корректный); transaction с dry-run; dry-run + ошибка валидации (возвращается ошибка как обычно).

## Риски
Read-команды флаг не принимают; CLI должен отвергать `--dry-run` для read-команд кодом `unknown_parameter`.
