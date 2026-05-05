# stg-0002 — industrial-readiness

**Статус:** текущая

## Задача
Сделать DocsWalker промышленно-пригодным для LLM-агентов — закрыть пробелы по полноте API, по проверкам целостности, по защите от ошибочных правок и по дискаверабельности.

В рамках стратегии также проводится унификация модели данных: введение концепта **«ось»** (axis) как единого первичного отношения между узлами. `path`, default-блоки (`definitions`/`examples`/`fields`/`content`) и прикладные `ref_type` сводятся к одному концепту с явно объявленным контрактом в Схеме. Это снимает спецкейс «документ не имеет parent» и делает поддержку папок (`folder`) бесплатной — без отдельных команд `create_document` / `delete_document`.

## Шаги
- [+] (01) error-hints
- [+] (02) extra-integrity-checks
- [+] (03) check-integrity-command
- [+] move-node
- [+] axes-meta-schema
- [+] axes-schema
- [+] axes-docswalker-yml
- [*] axes-core-graph
- [*] axes-core-create-node
- [*] axes-cli-dynamic-params
- [*] axes-folders
- [*] axes-migrate-docs
- [*] dry-run
- [*] describe-type
- [*] usage-guide
- [*] docs-llm-guide
- [*] cross-process-lock
