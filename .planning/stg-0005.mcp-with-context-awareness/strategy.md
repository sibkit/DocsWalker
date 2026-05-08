# stg-0005 — mcp-with-context-awareness

**Статус:** текущая

## Задача
Дать LLM-агенту минимум шума в контексте при работе с DocsWalker: автоматически подтягивать концептуально неотъемлемые связи (auto-include), не дублировать выданные узлы между запросами одной сессии (seen-set по `session_id`), и добавить MCP-сервер как параллельный транспорт поверх стабилизированного ядра. Существующий CLI остаётся; dedup-семантика работает на любом транспорте — везде, где есть `session_id`.

## Шаги
- [+] (01) spec-in-docs
- [+] (02) session-state-core
- [+] session-id-handshake
- [*] read-dedup-placeholder
- [*] write-invalidation
- [*] auto-include
- [*] mcp-server
