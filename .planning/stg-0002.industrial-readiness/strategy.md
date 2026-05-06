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

### Tree-scopes — обобщение «дерева» поверх графа (refs-model phase 2)

Принято после R11 при проектировании оставшихся read/write-команд. Граф = узлы + named-связи; **некоторые связи объединяются в named-scope деревья**. Дерево — это именованная область поверх графа, образованная одной или несколькими связями, опционально на разных типах. Связь принадлежит одному дереву (не нескольким); это объявляется через `tree: <scope_name>` в RefDef.

**Правила tree-scope:**
- Связь с `tree: X` всегда `cardinality=one + required=true` — эти поля **не указываются** рядом с `tree:` (запрещено мета-схемой), они подразумеваются. Конвенция направления: tree-refs идут child → parent.
- Несколько RefDef с одинаковым `tree: X` (на разных типах, с разными именами) **вместе** образуют дерево. Пример: `project.strategy → strategy`, `task.project → project`, `subtask.parent_task → task` — все три с `tree: strategic`, вместе дают дерево «стратегия → проект → задача → подзадача».
- Узел — корень дерева scope X, если его тип не объявляет ни одной out_ref с `tree: X`. Лист — если на узел нет входящих refs scope X. Forest допустим (несколько корней).
- Все деревья scope-имена декларируются на верхнем уровне Схемы в секции `trees: [{name, description}]`. Каждое `tree: X` в RefDef валидируется по этой декларации.
- Валидатор по каждому объявленному дереву: нет циклов в графе scope'а. По дереву `path` дополнительно — связность (каждый не-root достижим из root).

**`path` теперь — обычный scope.** В мета-схеме path-связи описываются так же, как любые tree-refs: `name: path, tree: path, target_types: [...]`. Никакого специального поля `path:` или `path_targets:` (последнее уходит из мета-схемы — заменяется на target_types у самой связи). Особенность path только семантическая: `tree: path` — единственное обязательное на каждом не-root узле дерево, физически задающее размещение в хранилище. Имя `path` — зарезервировано (нельзя объявить свою связь с этим именем).

**API:**
- `get_subtree(node_id, tree="path")` — обобщённая subtree-операция. По умолчанию path. Внутри scope X: обход вниз по входящим refs scope X на каждом уровне.
- `get_ancestors(node_id, tree="path")` — обход вверх по исходящему scope-ref'у узла.
- `move_node(node_id, new_parent_id, tree="path")` — переподшивка scope-ref'а узла. Для path = реструктуризация хранилища (с каскадным rewrite SourceFile у потомков). Для других scope'ов = просто правка одного ref'а.
- `describe-type` отдаёт out_refs с полем `tree?: <scope>` (отсутствует у не-tree связей).

### Удаление узлов: универсальный delete-nodes с контрактом о намерении

Принято после R11 при проектировании delete-семантики. Одиночный `delete-node` снимается, заменяется множественным `delete-nodes --ids=<csv>` с явным списком id. Cascade ни по какому дереву **не зашит** — LLM сама собирает набор удаляемого через `get_subtree` (по любому scope), движок валидирует.

**Алгоритм:**
1. **Path-замкнутость:** для каждого id в наборе все path-children тоже в наборе. Иначе ошибка `path_orphans_left` со списком недостающих id и hint «добавь их или ограничь набор».
2. **Нет dangling cross-refs:** ни одной входящей cross-ref на узел внутри набора от узла снаружи. Иначе ошибка `dangling_refs` с перечислением (source_id, ref_name, target_id) — LLM перенаправляет/удаляет связи и ретраит.
3. Если чисто — атомарное удаление всех узлов в наборе.

Это даёт «universal cascade»: если LLM хочет удалить проект со всеми задачами по дереву `strategic`, она зовёт `get_subtree(project_id, tree="strategic")`, добавляет path-children для каждого узла набора (если есть) и передаёт всё в `delete-nodes`. Движок не делает магии — LLM явно собирает набор, движок проверяет целостность. Ошибки — обучающий сигнал: LLM осваивает структуру, а галлюцинации ловятся жёстко.

### Redirect-refs — массовая переподшивка cross-refs

Принято там же. Удобная операция перед удалением: перенаправить все входящие cross-refs (или их подмножество) с одного узла на другой, либо разорвать.

Формы:
- `redirect-refs --from=<src_id> --to=<dst_id> [--name=<ref_name>]` — все входящие cross-refs в src_id (опционально только с указанным именем) → в dst_id.
- `redirect-refs --from-subtree=<root_id> --to=<dst_id>` — все входящие cross-refs в любой узел path-subtree → в dst_id.
- `redirect-refs --from=<src_id> --unlink [--name=<ref_name>]` — разрыв вместо переноса.

Без `redirect-refs` тот же результат достигается серией `update-ref`'ов в transaction'е, но требует от LLM руками собрать список источников через `get-refs` — шумно и подверженно ошибкам.

### Schema-правило: `rule` обязан иметь `example`

Принято при проектировании tree-scopes. У типа `rule` появляется обязательная горизонтальная связь:

```yaml
- name: rule
  out_refs:
    - name: examples
      target_types: [example]
      cardinality: many
      required: true
      description: Примеры применения правила. Минимум один.
```

Не tree-scope — обычная cross-ref. `example.path` остаётся `[section]` (вариант A): example — самостоятельный атом секции, может иллюстрировать несколько правил. Существующие правила в живых docs мигрируются: для каждого rule подбирается/создаётся example из соседних узлов или из формулировки правила.

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
- [+] (R4) refs-model-docswalker-doc — переписать `docs/DocsWalker.yml` под новую терминологию (refs/out_refs, без axis); описание API под новую модель; описание атомизации bullets.
- [+] (R5) migrate-docs-to-atoms — переписать существующие `docs/*.yml` (DocsWalker.yml, Стек.yml, Правила оформления.yml) под атомарную модель: каждый bullet → отдельный узел с id; sequence.txt пересчитывается.
- [+] (R6) refs-model-core-graph — переписать ядро (`Schema/*`, `Graph/Node`, `Graph/DocumentLoader`, `Validation/*`) под refs-модель. Node = `{id, type, title, text, out_refs}`. Удалить `NodeBlock`/`TextBlock`/`ChildrenBlock`/`OutRefsBlock`, `Ref`/`RefOrigin` (или переименовать `Ref` в простую запись `(name, targetId)`). Промежуточные коммиты с красной сборкой допустимы (auto-режим, без обратной совместимости). Никакого shim.
- [+] (R7) refs-model-write-api — переписать `Api/WriteApi.cs` и `Api/Transaction.cs` под refs-модель. Операции: `create_node` (параметры: `type`, `title`, `text?`, плюс значения всех `required` out_refs из контракта типа), `update_node` (правка title/text), `delete_node`, `move_node` (изменение значения `out_refs["path"]`), `create_ref` (от любой объявленной в типе связи, не только `ref`), `delete_ref`. `add_ref_type` / `add_axis` — уходит (типы и refs-контракты редактируются на уровне Схемы; эта операция, если нужна, появится отдельно как `add_type` / `add_ref_def`).
- [+] (R8) refs-model-read-api — переписать `Api/ReadApi.cs` под refs-модель. `get_nodes` возвращает `id, type, title, text, out_refs`. `get_refs` возвращает `in[]` и `out[]` — единый формат `{name, target_id}`. `get_subtree`, `get_by_path`, `search` — без axis-фильтров.
- [+] (R9) refs-model-cli — CLI-handlers и динамический парсер `create-node`: имена параметров берутся из `out_refs`-контракта типа в Схеме (`--<ref-name>` для каждой required-связи).
- [+] (R10) folders-read-create — поддержка типа `folder` на чтение и создание/удаление: рекурсивный обход подкаталогов в DocumentLoader, `.docswalker/folders.yml` как primary-источник folder-узлов (FS-расхождение → integrity_failed без авто-починки), `create-node type=folder` создаёт каталог + запись, `delete-node` для folder требует пустой каталог. Update/move folder сюда **не входят** (отдельный шаг R11) — title/parent у folder в этой итерации не правятся, чтобы не тащить в R10 cascade-rewrite SourceFile у потомков и FS-machine для переноса каталогов.
- [+] (R11) folders-rename-move — поддержка update-node (rename folder = переименование dirname + правка folders.yml + cascade-rewrite SourceFile у потомков) и move-node (перенос каталога между родителями + всё то же). Расширение AtomicWriter под FS-операции `MoveDirectory` / `DeleteEmptyDirectory`. Отдельным шагом, чтобы границы R10 оставались узкими.

### Refs-model phase 2 — tree-scopes и универсальное удаление

- [+] (R12) tree-scopes-meta-schema — добавить в `docs/.docswalker/meta-schema.yml` секцию `trees: [{name, description}]` на верхнем уровне Схемы; в `ref_definition` — поле `tree?: string`. Запретить указывать `cardinality`/`required` рядом с `tree:`. Снять `path_targets` из `type_definition` (заменяется на target_types у самой path-связи). Поднять `meta_schema_version` до 5.
- [+] (R13) tree-scopes-schema — переписать `docs/Схема.yml`: добавить декларацию `trees: [{name: path, description: ...}]`; для каждого типа кроме root объявить связь `path` через `tree: path` (вместо текущего `path_targets`). Заодно: добавить в `rule.out_refs` обязательную связь `examples → example` (см. раздел «Schema-правило: rule обязан иметь example»).
- [+] (R14) tree-scopes-core — расширить ядро (Schema parser, валидатор, Graph). Парсер мета-схемы понимает `trees:` и `tree:`. Валидатор: per-scope no-cycle, для `path` — связность; запрещает cardinality/required рядом с tree. Graph хранит индекс «по scope → entries», чтобы subtree-операции были эффективны.
- [+] (R15) tree-scopes-read-api — обобщить `get_subtree`/`get_ancestors` параметром `tree: string` (default `"path"`). Внутри scope X: subtree = обход вниз по входящим refs scope X; ancestors = вверх по исходящему scope-ref'у. ListDocuments снимается (граф достаётся обходом от root по `tree: path`).
- [+] (R16) tree-scopes-write-api — `move-node` получает параметр `tree: string` (default `"path"`). Для `tree=path` сохраняется текущая семантика (переподшивка path + cascade-rewrite SourceFile). Для других scope'ов — атомарная правка одного scope-ref'а. `create-node` для нескольких tree-refs принимает значение каждого scope-родителя как required-параметр.

### Read/write API completion

- [*] delete-nodes — заменить одиночный `delete-node` множественным `delete-nodes --ids=<csv>`. Алгоритм валидации: path-замкнутость + dangling cross-refs check. Без авто-каскада. См. раздел «Удаление узлов».
- [*] redirect-refs — новая команда. Формы `--from/--to`, `--from-subtree/--to`, `--unlink`. См. раздел «Redirect-refs».
- [*] rule-requires-example — миграция живых docs: каждое существующее `rule` в docs/*.yml получает обязательный `examples` ref ≥1 элемент. Где example нет — создаётся минимальный example рядом или формулируется из текста правила. См. раздел «Schema-правило».

### Завершающие шаги исходной стратегии

- [+] (01) error-hints — выполнено.
- [+] (02) extra-integrity-checks — выполнено.
- [+] (03) check-integrity-command — выполнено.
- [+] move-node — выполнено (пересмотрено в R7, расширяется в R16).
- [*] dry-run
- [*] cross-process-lock
- [*] describe-type — спецификация переписывается под refs-model + tree-scopes.
- [*] docs-llm-guide — спецификация переписывается под модель «граф + tree-scopes».
- [*] usage-guide — спецификация переписывается под актуальный manifest команд.

### Итоговый порядок выполнения

```
R12 → R13 → R14 → R15 → R16
→ delete-nodes → redirect-refs → rule-requires-example
→ dry-run → cross-process-lock
→ describe-type → docs-llm-guide → usage-guide
```

Перед стартом реализации каждый из спорных step-файлов (`describe-type`, `docs-llm-guide`, `usage-guide`) переписывается под актуальный дизайн — чтобы спецификация и код не расходились к моменту шага.

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
