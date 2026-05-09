# stg-0007 — MCP Schema Honesty

**Статус:** текущая

## Задача

Привести inputSchema MCP-tools и поведение сервера в соответствие с
описаниями: чтобы LLM-клиент получал то, что обещано схемой, без обходов
через escaped-strings, угадываний обязательных полей и несоответствий между
описанием tool и его реальным выводом.

## Шаги

- [+] (01) meta-schema-full-serialize
- [+] (02) transaction-array-passthrough
- [*] create-node-schema-from-types
