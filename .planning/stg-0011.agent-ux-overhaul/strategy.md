# stg-0011 — agent-ux-overhaul

**Статус:** текущая

## Задача

Радикальное улучшение UX DocsWalker для LLM-агента: расширение read-API (новые команды, BM25-ранжирование в `search`, бюджеты с truncation-протоколом), вынос MCP-сервера в отдельный exe и deprecation CLI, ввод multi-tree-классификаторов как второго измерения навигации, миграция всех примеров в `docs/` на JSON-формат.

## Шаги

- [+] (01) docs-search-spec
- [+] (02) docs-rename-get-tree
- [+] (03) docs-add-get-overview
- [+] (04) docs-remove-get-map
- [+] (05) docs-truncation-protocol
- [+] (06) docs-examples-json-migration
- [*] code-mcp-project-split
- [*] code-api-rename-and-remove
- [*] code-get-overview
- [*] code-search-v2-bm25
- [*] code-compact-and-tokens
- [*] code-mcp-tools-update
- [*] code-api-v2-tests
- [*] docs-cli-deprecate
- [*] docs-classifiers-model
- [*] docs-meta-schema-classifiers
- [*] docs-schema-classifier-trees
- [*] code-classifier-validator
- [*] code-find-command
- [*] code-search-classifier-filter
- [*] migrate-classifiers-data
