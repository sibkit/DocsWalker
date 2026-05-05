# stg-0002 — describe-type

## Цель
Узкая read-команда `describe-type --name=<type>`: возвращает описание одного типа из Схемы (kind, fields, blocks, title_format, допустимые типы детей). Экономит токены LLM по сравнению с целиком `get-schema`.

## Файлы
`docs/DocsWalker.yml` — раздел «Операции чтения» (#17): новый пункт `describe_type`.
`docs/DocsWalker.yml` — раздел «CLI-интерфейс» (#35): пример.
`src/DocsWalker.Core/Api/ReadApi.cs` — метод `DescribeType(string name)`.
`src/DocsWalker.Core/Api/ReadApiJson.cs` — сериализация одного типа.
`src/DocsWalker.Cli/Cli/Commands.cs`, `Cli/Handlers/ReadHandlers.cs` или `SchemaHandlers.cs`, `Program.cs`.
`tests/DocsWalker.Tests/ReadApiTests.cs` — тесты.

## Действия
1. Зафиксировать команду в `docs/DocsWalker.yml`.
2. Реализовать `ReadApi.DescribeType`: найти тип в `SchemaDocument.Types`; вернуть структурированный объект (kind, поля и блоки с описаниями); если тип не найден — `read_api_exception` с кодом `type_not_found` и hint «вызови `get-schema` для списка типов».
3. Подключить в CLI.
4. Тесты: запрос существующего и несуществующего типа; для node-типа в ответе перечислены допустимые типы детей по блокам.

## Риски
Нет.
