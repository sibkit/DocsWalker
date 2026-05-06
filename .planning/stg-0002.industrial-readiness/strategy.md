# stg-0002 — industrial-readiness

**Статус:** текущая

## Задача
Сделать DocsWalker промышленно-пригодным для LLM-агентов — закрыть пробелы по полноте API, по проверкам целостности, по защите от ошибочных правок и по дискаверабельности.

В рамках стратегии проводится корневой пересмотр модели данных: переход на **атомарную refs-модель**. Каждая «единица смысла» из docs/ становится отдельным узлом со своим id; узлы связаны направленными исходящими связями (`out_refs`). Концепты «ось» (axis), default/system/explicit-axis, mapping-fields, blocks-в-Node, тип `field` — упраздняются как избыточные.

## Цели рефакторинга

1. **Структура хранится в YAML-файлах docs/.** Включая каждый текстовый атом (бывший bullet-блок) — у него свой узел в YAML.
2. **LLM собирает из графа полный контекст для решения задачи, не забивая контекст ненужным.** Атомарность узлов даёт фильтрацию «по одному правилу / одному определению» без подтягивания всей секции; уход «осей»/спецкейсов даёт LLM единый интерфейс — `get_node` и `get_refs` хватает на любую навигацию.

## Принятые решения

### Модель Node
Узел имеет ровно **5 полей**; других нет.

- `id: int` — глобально уникальный sequence-id, выдаётся ядром.
- `type: string` — имя типа из Схемы. Тип определяет: где узел может находиться (target_types для встроенной связи `path`); какие исходящие связи допустимы у этого узла (по имени и target_types); требуется ли непустой `text`.
- `title: string` — короткая человекочитаемая часть. Используется в path-навигации и для display.
- `text: string` — единица смысла; полезная нагрузка узла. Может быть пустым у структурных типов (root, folder, section), требуется у смысловых (statement, rule, definition, …). Всегда одна строка (запрещены `\n`, `\r`, `\t` — то же правило, что и сейчас).
- `out_refs: Map<name, List<int>>` — все исходящие связи узла; ключ — имя связи, значение — список id целей. Только исходящие; обратные направления (`in_refs`) — вычисляемое представление, обратным проходом по графу. Связь `path` — обязательная встроенная (есть у каждого узла кроме root); остальные связи объявляются типом узла-источника.

### Терминология
- «Связь» / `ref`. Не «ось», не `axis`. Слова `axis`, `axis_kind`, `default_axis`, `system_axis`, `explicit_axis` — уходят из документации, кода, CLI.
- `out_refs` — исходящие связи (то, что хранится в узле). Аналогия: `<ProjectReference Include="OtherProject.csproj"/>` в csproj — это исходящая зависимость *этого* проекта; связь хранится в источнике, направление наружу.
- `in_refs` — вычисляемое представление: «кто на меня ссылается». Получается обратным проходом по графу, в файлах не хранится.
- `path` — встроенная связь ядра. У каждого узла кроме root — обязательная, cardinality=one. Управляется ядром через структурные операции (создание/удаление/перенос); в out_refs хранится явно как `out_refs["path"] = [parent_id]`.

### Тип в Схеме
Описание типа сводится к:
- `name`
- `title_source` (`filename` / `dirname` / `inline_key` — `field` уходит вместе с типом field)
- `text_required: bool`
- `path_targets: List<string>` — какие типы могут быть на конце встроенной связи `path` у узла этого типа
- `out_refs: List<RefDef>` — объявление допустимых исходящих связей (кроме path). Каждая `RefDef`:
  - `name`
  - `target_types: List<string>`
  - `cardinality: one | many`
  - `required: bool`
  - `description?`

Конструкции `fields`, `blocks`, `key_type`, `value_type`, `direction`, `system` в типах — уходят. Различие system/default/explicit на уровне Схемы исчезает: связь либо объявлена в типе, либо нет.

### Title как path-сегмент и формат YAML смысловых узлов
`title` — это сжатие `text` до 1–2 слов, path-сегмент узла, а не отдельная сущность и не display-name. Используется в path-навигации (`/Document/Section/Atom-title`) и для дискаверабельности через имя, а не только id. Если для bullet нет естественного title — это сигнал, что bullet шумный и должен слиться с соседом или быть удалён; «нет title» как нормальный кейс не существует.

Дихотомия типов:
- **Структурные** (`root`, `folder`, `document`) — `title_source` ∈ `{filename, dirname}`; узел физически = файл или каталог.
- **Смысловые** (`section`, `definition`, `example`, `statement`, `rule`, `may_rule`, `note`, `llm_hint`) — `title_source = inline_key`; узел физически = mapping-ключ в YAML родителя.

`title_source` enum остаётся `{filename, dirname, inline_key}` — без новых значений, без исключений.

Единый формат сериализации смысловых узлов — одна YAML-запись `"(#id) Title": value`. Форма `value` диктуется контрактом типа: при `text_required=true` без `out_refs` value = строка с текстом узла; при наличии `out_refs` value = mapping с сериализованными исходящими связями. Этот формат уже существует у `definition`/`example` в текущих docs; атомизация распространяет его на ex-bullets `statements`/`rules`/`may_rules`/`notes`/`llm`. Конкретный пример формата — в DocsWalker.yml (R4).

### Атомизация bullet-блоков
Текущие text-блоки section (`statements`, `rules`, `may_rules`, `notes`, `llm`) разворачиваются в новые node-типы:
- `statement`, `rule`, `may_rule`, `note`, `llm_hint` — каждый node-тип с `text_required=true`, `path_targets=[section]`, `title_source=inline_key`.
- В `section` вместо блоков появляются исходящие связи `statements`, `rules`, `may_rules`, `notes`, `llm`; каждая связь cardinality=many, target_types=[<соответствующий тип>].
- Каждый bullet старого YAML получает свой id (sequence пересчитывается под максимальный) и 1–2-словный title; сериализуется по формату `"(#id) Title": text` (см. раздел про title).

### Уход типа field
Тип `field` из текущей Схемы уходит. Контракт типа полностью описывается через объявление связей в самом `type_definition` — описывать «field» как отдельный node-тип не нужно. Все field-узлы исчезают после миграции.

### Cross-refs (отказ от generic `ref`)
Generic-тип связи `ref` (текущий `kind: ref_type, direction: from_to`) уходит. Каждая связь — это named контракт, объявленный в типе узла-источника. На R3 у `section`/`document`/прочих смысловых типов **никакие cross-refs не объявляются** — оставляем минимальный набор связей под атомизацию. На R5 каждое существующее `{ref: id}` в живых docs смотрится по одному: либо переименование в семантически точный named ref (с декларацией в Схеме того же шага), либо удаление как избыточный — то есть cross-refs появляются спросом, а не сразу.

### Что остаётся как есть
- YAML — единственная форма хранения.
- `sequence.txt` — id-счётчик ядра (будет пересчитан при миграции под максимальный id новой нумерации).
- Folder как тип (см. шаг R10), root как синглтон с id=0.
- Стиль: одна строка на значение, snake_case, единый emitter.

## Шаги (новый список под refs-модель)

Старые `step-axes-*.md` устарели. Удаление — первое действие новой сессии.

- [+] (R1) revert-docs-axes-rewrite — откатить правки docs/{.docswalker/meta-schema.yml, Схема.yml, DocsWalker.yml, Правила оформления.yml} от коммитов `4baa335` / `0f59bd8` / `d7a0158` (axes-* шаги). Цель — вернуться к версии до axes-refactor для чистой переработки под refs-модель. Способ — `git checkout ce3f05e^ -- <files>` или явная правка под содержимое до axes (на выбор реализатора).
- [+] (R2) refs-model-meta-schema — переписать `docs/.docswalker/meta-schema.yml` под refs-модель: `type_definition` имеет `name, title_source, text_required, path_targets, out_refs[]`; раздел `axis_definition` уходит; `field_definition` / `block_definition` уходят; `ref_kind` / `ref_direction` уходят. Поднять `meta_schema_version` до 4.
- [+] (R3) refs-model-schema — переписать `docs/Схема.yml`: убрать тип `field`; добавить типы `statement` / `rule` / `may_rule` / `note` / `llm_hint`; описать out_refs-контракты для `document` / `section` / `definition` / `example` / `folder`; убрать blocks/fields/value_type у типов; убрать раздел axes (если был добавлен).
- [*] (R4) refs-model-docswalker-doc — переписать `docs/DocsWalker.yml` под новую терминологию (refs/out_refs, без axis); описание API под новую модель; описание атомизации bullets.
- [*] (R5) migrate-docs-to-atoms — переписать существующие `docs/*.yml` (DocsWalker.yml, Стек.yml, Правила оформления.yml) под атомарную модель: каждый bullet → отдельный узел с id; sequence.txt пересчитывается.
- [*] (R6) refs-model-core-graph — переписать ядро (`Schema/*`, `Graph/Node`, `Graph/DocumentLoader`, `Validation/*`) под refs-модель. Node = `{id, type, title, text, out_refs}`. Удалить `NodeBlock`/`TextBlock`/`ChildrenBlock`/`OutRefsBlock`, `Ref`/`RefOrigin` (или переименовать `Ref` в простую запись `(name, targetId)`). Промежуточные коммиты с красной сборкой допустимы (auto-режим, без обратной совместимости). Никакого shim.
- [*] (R7) refs-model-write-api — переписать `Api/WriteApi.cs` и `Api/Transaction.cs` под refs-модель. Операции: `create_node` (параметры: `type`, `title`, `text?`, плюс значения всех `required` out_refs из контракта типа), `update_node` (правка title/text), `delete_node`, `move_node` (изменение значения `out_refs["path"]`), `create_ref` (от любой объявленной в типе связи, не только `ref`), `delete_ref`. `add_ref_type` / `add_axis` — уходит (типы и refs-контракты редактируются на уровне Схемы; эта операция, если нужна, появится отдельно как `add_type` / `add_ref_def`).
- [*] (R8) refs-model-read-api — переписать `Api/ReadApi.cs` под refs-модель. `get_nodes` возвращает `id, type, title, text, out_refs`. `get_refs` возвращает `in[]` и `out[]` — единый формат `{name, target_id}`. `get_subtree`, `get_by_path`, `search` — без axis-фильтров.
- [*] (R9) refs-model-cli — CLI-handlers и динамический парсер `create-node`: имена параметров берутся из `out_refs`-контракта типа в Схеме (`--<ref-name>` для каждой required-связи).
- [*] (R10) folders — поддержка типа `folder`, чтения подкаталогов docs/, `.docswalker/folders.yml`. Аналог шага step-axes-folders, но под refs-модель.

После завершения refs-модели — оставшиеся шаги исходной стратегии:
- [+] (01) error-hints — выполнено.
- [+] (02) extra-integrity-checks — выполнено.
- [+] (03) check-integrity-command — выполнено.
- [+] move-node — выполнено (придётся пересмотреть в R7).
- [*] dry-run
- [*] describe-type
- [*] usage-guide
- [*] docs-llm-guide
- [*] cross-process-lock

## Точка возобновления (для новой сессии после сброса контекста)

Прошлая итерация ушла в концепцию «осей» (axes); это направление **пересмотрено и отброшено**. Решено: модель **не axes, а атомарная refs-модель** с Node = `{id, type, title, text, out_refs}`.

Старт новой сессии:

1. Прочитать `strategy.md` (этот файл) полностью — особенно разделы «Цели рефакторинга», «Принятые решения», «Шаги».
2. Удалить устаревшие `step-axes-*.md` (`step-axes-cli-dynamic-params.md`, `step-axes-core-create-node.md`, `step-axes-core-graph.md`, `step-axes-docswalker-yml.md`, `step-axes-folders.md`, `step-axes-meta-schema.md`, `step-axes-migrate-docs.md`, `step-axes-schema.md`). Закоммитить как «Drop obsolete axes step plans, refs-model supersedes». Старые axes-шаги в gitlog сохранятся, восстановить можно.
3. Создать первый файл нового шага `step-R1-revert-docs.md` (точное имя на выбор) и начать с него.
4. Дальше по списку R1 → R2 → … → R10 → unfinished шаги исходной стратегии.

Принципы:
- Промежуточные коммиты с красной сборкой допустимы (auto-режим, обратная совместимость не требуется).
- Никакого shim, никакого переходного слоя.
- Всё, что описывается в коде, синхронно отражается в `docs/` (правило из проектного `CLAUDE.md`).
