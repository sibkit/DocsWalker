# stg-0007 — MCP Schema Honesty

## Цель

`transaction.operations` принимает JSON-массив через MCP `arguments`
напрямую (как обещает description). Сейчас работает только escaped-string —
если LLM шлёт `operations: [{...},{...}]`, сервер склеивает массив через
`string.Join(",", ...)` без скобок, CLI получает невалидный JSON и падает с
`invalid_parameter`. Плюс inputSchema объявляет `operations` как
`type: object`, что противоречит description «JSON-массив операций».

## Файлы

- `docs/DocsWalker.yml` — зафиксировать контракт `operations`: тип JSON-array
  через MCP arguments, форма передачи (raw passthrough), inputSchema
  `type: array`.
- `src/DocsWalker.Cli/Cli/Commands.cs` — `CommandParam` либо новый
  `ParamType.JsonArray`, либо флаг `JsonPassthrough` на параметре
  (форма принимается на шаге 2 step'а в чате).
- `src/DocsWalker.Core/Mcp/McpServer.cs:321` (`JsonValueToCliString`) —
  `JsonValueKind.Array` для passthrough-параметра возвращает `value.GetRawText()`
  (raw JSON со скобками), а не `string.Join`.
- `src/DocsWalker.Cli/Mcp/CommandsToTools.cs` —  генератор inputSchema:
  для passthrough-параметра-массива выдаёт `{type:"array", items:{type:"object"}}`.
- `src/DocsWalker.Core/Mcp/McpToolDescriptor.cs` — пробросить признак
  passthrough в descriptor, если он не выводится из текущей структуры.
- `tests/DocsWalker.Tests/Mcp/...` — тест: `BuildArgvFromArguments` для
  `transaction` с `operations`-массивом возвращает корректный CLI-аргумент
  `--operations=[{...},{...}]`.

## Действия

1. Прочитать `Commands.cs`, `CommandsToTools.cs`, `McpToolDescriptor.cs`,
   `McpServer.cs` — текущую структуру `ParamType` и сборки tool-descriptor.
2. Развилка: `ParamType.JsonArray` (новый тип) vs `JsonPassthrough`
   (флаг на `CommandParam`). Вынести в чат, выбрать.
3. Через DocsWalker уточнить в `docs/DocsWalker.yml` контракт `operations`.
4. Реализовать выбранный вариант: правка `Commands.cs`, генератора schema,
   `JsonValueToCliString`.
5. Юнит-тест на конвертацию arguments → argv.
6. Сборка + smoke-test через `mcp-server`: `tools/call transaction
   {operations:[{...}]}` без escape — должен пройти до семантической
   валидации (а не упасть на JSON-parse).

## Риски

`Json` параметры есть и в других командах — нужно не сломать существующее
поведение для object-формы. Покрыть тестом обе формы: object (как сейчас) и
array (новое). Решение по форме (новый тип vs флаг) влияет на объём правок —
оценить на шаге 2.
