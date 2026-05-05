# stg-0002 — industrial-readiness

**Статус:** текущая

## Задача
Сделать DocsWalker промышленно-пригодным для LLM-агентов — закрыть пробелы по полноте API, по проверкам целостности, по защите от ошибочных правок и по дискаверабельности.

В рамках стратегии также проводится унификация модели данных: введение концепта **«ось»** (axis) как единого первичного отношения между узлами. `path`, default-блоки (`definitions`/`examples`/`fields`/`content`) и прикладные `ref_type` сводятся к одному концепту с явно объявленным контрактом в Схеме. Это снимает спецкейс «документ не имеет parent» и делает поддержку папок (`folder`) бесплатной — без отдельных команд `create_document` / `delete_document`.

## Принятые решения по реализации axes-refactor
- **Стратегия рефакторинга — «чисто, в один проход» (без shim).** В шаге axes-core-graph `ParentId` / `OutRefs` у `Node` заменяются единой коллекцией `Axes` сразу. Компиляция сломается; восстанавливается послойно: Schema → Graph → Validator → Read API → Write API → CLI → Tests. Промежуточные коммиты с красной сборкой допустимы (мы в auto-режиме, обратная совместимость не требуется). Никакого переходного слоя — иначе риск, что shim где-то останется.
- **id папки — служебный файл `docs/.docswalker/folders.yml`** (вариант 1). Формат — список `{path: "Папка/Подпапка", id: 42}`. При rename папки правится одна строка, id сохраняется. При удалении строка убирается. Альтернативы (`.docswalker.yml` рядом с каждой папкой, `id = hash(path)`) отвергнуты.
- **default-оси объявляются явно в секции axes** Схемы (не выводятся неявно из блоков). Это даёт полный список осей в одном месте для get_schema и явный target_types.
- **`block_definition` имеет поле role** (`default_axis` | `refs_container`). `out_refs` имеет role=refs_container и не порождает default-оси.
- **root** — синглтон ядра DocsWalker с id=0, **не описывается типом в Схеме**. Не создаётся пользователем, не удаляется.
- **Параметр оси path в API** принимает либо id (целое), либо человекочитаемый путь (строка с разделителем `/`). Пустая строка `""` = root. Прочие explicit-оси принимают только id.
- **`update_node`** не меняет значения осей. Перенос по path — через `move_node`, правка explicit-осей — через `create_ref` / `delete_ref`.

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

## Точка возобновления (для новой сессии после сброса контекста)
Документация (`docs/.docswalker/meta-schema.yml`, `docs/Схема.yml`, `docs/DocsWalker.yml`, `docs/Правила оформления.yml`) **уже переписана** под концепт «ось» (коммиты `4baa335`, `0f59bd8`, `d7a0158`).

Следующий шаг — **axes-core-graph** (см. `step-axes-core-graph.md`). Стартовать с него.

Контекст для быстрого входа:
1. Прочитать `strategy.md` (этот файл) — раздел «Принятые решения по реализации axes-refactor».
2. Прочитать `step-axes-core-graph.md` — действия на этот шаг.
3. Загрузить C# воркспейс через `mcp__glider__load` (см. CLAUDE.md проекта).
4. Найти текущий `Node` (там есть `ParentId`, `OutRefs`) — заменить на единую коллекцию `Axes`.
5. По цепочке Schema → Graph → Validator → Read API → Write API → CLI → Tests привести код в соответствие с переписанной докой.

Никакого shim. Промежуточные коммиты с красной сборкой допустимы.
