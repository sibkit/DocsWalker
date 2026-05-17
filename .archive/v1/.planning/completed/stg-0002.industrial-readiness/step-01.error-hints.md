# stg-0002 — error-hints

## Цель
Добавить поле `hint` в структурированные ошибки `ValidationError` и `WriteApiException` — чтобы LLM получала готовую подсказку «как починить», а не только код и сообщение. Проброс `hint` в CLI JSON.

## Файлы
`src/DocsWalker.Core/Validation/ValidationError.cs` — поле `Hint`, конструктор с подсказкой.
`src/DocsWalker.Core/Api/WriteApi.cs` — поле `Hint` в `WriteApiException`, перегрузка конструктора.
`src/DocsWalker.Core/Validation/SchemaCheck.cs`, `RefsCheck.cs`, `UniqueCheck.cs`, `StyleCheck.cs`, `MetaSchemaCheck.cs` — проставить `hint` для каждой ошибки, где подсказка осмысленна (например, `unknown_ref_type` → «вызови `add-ref-type` перед операцией»).
`src/DocsWalker.Core/Api/WriteApi.cs` — проставить `hint` в исключениях с типичными ошибками вызова.
`src/DocsWalker.Cli/Cli/Output.cs` — пробросить `hint` в JSON-ответ ошибки.
`src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs`, `WriteHandlers.cs`, `SchemaHandlers.cs` — пробросить `Hint` из исключений в `Output.WriteError`.
`docs/DocsWalker.yml` — раздел «Контракт валидации» / «CLI-интерфейс»: упомянуть поле `hint` в форме ответа об ошибке.
`tests/DocsWalker.Tests/ValidatorTests.cs`, `WriteApiTests.cs` — проверка наличия `hint` в типичных ошибках.

## Действия
1. Обновить `docs/DocsWalker.yml`: в описание ошибки CLI и контракта валидации добавить поле `hint` (опциональное).
2. Расширить `ValidationError` и `WriteApiException` полем `Hint` (string, опционально).
3. Пройти все места создания этих ошибок и добавить осмысленные подсказки. Где подсказка очевидно бесполезна — оставить `null`.
4. Обновить `Output.WriteError` и сигнатуры обработчиков CLI, чтобы пробрасывать `hint` в JSON.
5. Обновить тесты: типичные сценарии ошибок должны возвращать непустой `hint`.

## Риски
Поле опционально, но потребители уже могут разбирать JSON ошибки — добавление нового поля совместимо. Следить, чтобы в success-ответе поля `hint` не было.
