# stg-0007 — MCP Schema Honesty

## Цель

inputSchema `create-node` отражает обязательные поля контракта типа узла:
как минимум `path` (общий для всех типов в дереве path), и required-refs
конкретного типа. LLM-клиент получает actionable schema и не вынужден
ловить `missing_required_ref` методом проб.

Сейчас inputSchema содержит только `type`/`title`/`text`/`root`/`dry-run`;
required-refs (зависящие от значения `type`) не отражены, потому что
`DynamicParams=true` в `Commands.cs` означает «принимаем любой ключ», но в
schema это не пробрасывается.

## Файлы

- `docs/DocsWalker.yml` — зафиксировать контракт inputSchema для
  динамических tool: форма представления required-полей, зависящих от
  `type`. Развилка (см. ниже): `oneOf`-разворот по типам vs
  description-level enumeration.
- `src/DocsWalker.Cli/Mcp/CommandsToTools.cs` — генератор для `create-node`
  читает проектную Схему (`docs/Схема.yml` через `SchemaLoader.LoadSchema`),
  для каждого типа собирает required out_refs и вставляет в inputSchema по
  выбранной форме. Нужно решить, на каком этапе это происходит — статически
  на старте mcp-server (Schema уже загружена) или лениво.
- `src/DocsWalker.Core/Mcp/McpToolDescriptor.cs` — может потребоваться
  расширить descriptor (например, поле `RawInputSchema` или callback для
  построения), чтобы `create-node` мог дать собственную схему вместо
  стандартной из `BuildInputSchema`.
- `src/DocsWalker.Core/Mcp/McpServer.cs:332` (`BuildInputSchema`) — учесть
  кастомную схему descriptor'а, если она задана.
- `tests/DocsWalker.Tests/Mcp/...` — тест: для проектной Схемы
  inputSchema `create-node`-tool содержит ожидаемые required-refs (например,
  `path` присутствует во всех ветках, `examples` — в required для типа,
  у которого ref `examples` объявлен required).

## Действия

1. Прочитать `CommandsToTools.cs`, `McpToolDescriptor.cs`,
   `McpServer.BuildInputSchema`. Понять текущую сборку descriptor'ов.
2. Прочитать `Schema.cs`/`MetaSchema.cs`/`SchemaLoader.cs` — как достать
   required out_refs (`RefDef.Required` или подобное поле).
3. Развилка: `oneOf`-разворот по типам vs description-level enumeration
   (форма перечисления required в текстовом description). Вынести в чат на
   trade-off (`oneOf` строже, тяжелее парсится; description проще, менее
   formal). Выбрать.
4. Через DocsWalker уточнить в `docs/DocsWalker.yml` контракт inputSchema
   для динамических tool.
5. Реализовать выбранный вариант: расширить descriptor, поправить генератор.
6. Юнит-тест на собранную inputSchema против проектной Схемы.
7. Сборка + smoke-test через `mcp-server`: `tools/list` → проверить
   inputSchema `create-node` глазами и через парсер.

## Риски

`oneOf` корректнее и точнее, но раздувает schema (каждый тип = свой branch),
LLM-клиент может медленнее на нём строить call. Description-level короче,
но менее formal — LLM может его игнорировать. Развилка живая, решать на
шаге 3.
