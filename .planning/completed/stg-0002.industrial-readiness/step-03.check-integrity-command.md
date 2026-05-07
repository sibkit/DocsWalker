# stg-0002 — check-integrity-command

## Цель
Добавить read-команду `check-integrity`: прогон полного `Validator` на текущем состоянии `docs/` без записи. Используется для аудита файлов после ручной правки или миграции и как инструмент LLM-агента «проверь, что граф цел, прежде чем писать».

## Файлы
`docs/DocsWalker.yml` — раздел «Операции чтения» (#17): новый пункт `check_integrity` с описанием формы ответа.
`docs/DocsWalker.yml` — раздел «CLI-интерфейс» (#35): пример вызова `docswalker check-integrity`.
`src/DocsWalker.Core/Api/ReadApi.cs` — метод `CheckIntegrity` (возвращает `ValidationResult`).
`src/DocsWalker.Core/Api/ReadApiJson.cs` — сериализация результата (массив ошибок с кодом, путём, сообщением, hint, id узла).
`src/DocsWalker.Cli/Cli/Commands.cs` — регистрация команды.
`src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — обработчик.
`src/DocsWalker.Cli/Program.cs` — диспатч.
`tests/DocsWalker.Tests/ReadApiTests.cs` — тесты: чистый граф → `ok=true, errors=[]`; граф с ошибкой → `ok=true, result.errors` непустой.

## Действия
1. Зафиксировать новую команду в `docs/DocsWalker.yml`.
2. Реализовать `ReadApi.CheckIntegrity`: загрузить мета-схему, схему, граф, sequence; запустить `Validator.Validate`; вернуть `ValidationResult`.
3. Сериализовать результат в JSON с массивом ошибок (форма та же, что в write-ошибках, но в `result`, не в `error` — операция чтения завершилась успешно, отчёт это её данные).
4. Подключить команду в CLI.
5. Тесты на сценарии «всё чисто» и «есть нарушение».

## Риски
Семантика exit-code: команда сама не упала, exit=0; ошибки графа — данные ответа. Это отличается от write-команд, где валидационная ошибка → exit≠0. Зафиксировать это явно в `docs/DocsWalker.yml`.
