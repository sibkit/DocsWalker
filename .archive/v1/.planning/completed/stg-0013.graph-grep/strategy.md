# stg-0013 - graph-grep

**Статус:** активна

## Цель

Добавить в LLM JSON API детерминированный graph-grep: точный поиск по title/text
узлов графа без прямого доступа к `docs/**/*.yml` и без загрузки полных узлов.

Graph-grep дополняет существующий BM25 `search`: `search` нужен для retrieval по
релевантности, `grep` нужен для точного поиска контрактных строк, error codes,
имен операций и полей API.

## Предварительное решение

- Внешний method не добавляется.
- Новая операция живет внутри `method=query` как `op=grep`.
- Операция read-only, не объявляет alias и не участвует в `tx`.
- Scope задается через уже существующие `select.path` и `select.coordinates`.
- Ответ возвращает matches со snippet-ами, а не полный text узлов.
- Результат управляется `limit`, `context_chars`, `max_tokens`, `regex`,
  `case_sensitive` и `in=title|text|both`.

## План

- [+] (01) docs-grep-spec - расширить `DocsWalker-LLM JSON API`: описать
  `query op=grep`, параметры, ответ, отличие от `search`, truncation и safety.
- [+] (02) code-grep-model - добавить DTO/parser для `op=grep` в
  `LlmJsonApiModel`.
- [+] (03) code-grep-executor - реализовать graph-grep в
  `LlmJsonApiQueryExecutor` поверх in-memory graph, path/coordinate scope и
  token budget.
- [+] (04) tests-grep - покрыть literal, case sensitivity, regex, scope,
  limit/max_tokens/truncated и invalid inputs.
- [+] (05) smoke - `check-integrity`, focused tests, full tests, live
  `tools/call query op=grep`, git status.

## Definition of Done

- LLM может сделать точный lexical scan через `query op=grep` без чтения YAML.
- По умолчанию ответ компактен: path/title/snippet/field, без полного text.
- Regex ограничен безопасным timeout или другим bounded-механизмом.
- При обрезании ответа API явно возвращает `truncated`, `returned`,
  `tokens_used`, `tokens_budget` и `stopped_at`.
- Существующие `hit/query/tx` сценарии не ломаются.
