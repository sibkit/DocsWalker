# CLAUDE.md — DocsWalker

Мета-памятка ассистента. **Никаких решений по языку, программе, архитектуре,
стеку и синтаксису здесь нет и быть не должно** — они живут только в
[docs/](docs/).

## Единственный источник истины — `docs/`

Вся спецификация (язык, синтаксис, доменная модель, стек, архитектура,
принципы) лежит в [docs/](docs/) и **только в формате YAML**.

Если ответа на вопрос в docs нет — спроси пользователя и дополни
соответствующий YAML, не храни ответ нигде кроме docs.

В этом файле (CLAUDE.md) запрещено дублировать или фиксировать содержимое
docs — даже в виде краткой выжимки. Только ссылка.

## Порядок работы над любой фичей

1. Сначала описание/уточнение в соответствующем YAML в `docs/`.
2. Потом реализация в коде.
3. Поведение поменялось в процессе реализации — **синхронно** правится docs, не
   потом.

Так docs и код не расходятся.

## Язык общения и кода

- Диалог с пользователем, описания, комментарии в коде, тексты в docs — **русский**.
- Имена идентификаторов в коде — **английские** (стандартная конвенция).
- Доменные термины — как в docs, без перевода.

## Автокоммит и автопуш

Полная автономия по git: при завершении пачки шагов `/planning` (все шаги пачки в `[+]`/`[-]`) Claude **сам**:

1. Стейджит изменения по проекту (`git add -A`).
2. Создаёт коммит с однострочным message (English, imperative) — описывает суть пачки.
3. Пушит в `origin/master`.

Подтверждение пользователя перед коммитом и пушем не требуется. Hash коммита в чат не выписывается.

Исключения, при которых нужно остановиться и спросить:
- В индексе оказались файлы с признаками секретов (`.env`, `*.key`, `credentials*`, `*token*` в значениях) — диагностировать и спросить, прежде чем коммитить.
- `git push` упал не из-за устаревшей ветки (например, защищённый push, конфликт прав) — не делать `--force`, остановиться и сообщить.
- Конфликт слияния при попытке push после pull — не разрешать молча, дать пользователю выбрать стратегию.

## Инструменты работы с C#-кодом

**Glider** (MCP-сервер на Roslyn) — единственный канал для навигации и анализа C#-кода. Всё, что относится к структуре, символам и семантике C#, делается через `mcp__glider__*`. Текстовый поиск и прямое чтение `.cs` — только в исключениях, перечисленных ниже.

**В начале сессии** один раз вызвать `mcp__glider__load` с путём к `DocsWalker.slnx`. Без этого семантические инструменты не работают.

**Навигация и поиск (только Glider):**
- Поиск кода/символа — `mcp__glider__find_code`, `search_symbols`.
- Ссылки/реализации/наследники/иерархия — `find_references`, `find_implementations`, `find_overrides`, `get_type_hierarchy`, `get_derived_types`.
- Caller/callee — `find_callers`, `get_outgoing_calls`.
- Структура/outline файла — `get_structure`.
- Диагностики компилятора — `get_diagnostics`.

**Чтение C#-файлов (только Glider):**
- Целиком — `mcp__glider__get_file_contents`.
- Точечно — `get_method_source`, `get_type_source`.
- `Read` на `.cs` запрещён, кроме случая, когда Glider не смог открыть workspace или файл вне загруженного solution.

**Правки C#:**
- Семантические рефакторинги — `mcp__glider__rename_symbol`, `move_member`, `move_type`, `organize_usings`, `format_document`.
- Локальные правки строк — `Edit` допустим **после** того, как место найдено через Glider.
- Создание новых `.cs`-файлов — `Write`, сразу после — `mcp__glider__sync` или `reload`, чтобы Roslyn увидел файл.

**Текстовый поиск (`Grep`) по `.cs`:** только для строковых литералов и комментариев, которых нет в symbol table. Для поиска кода — Glider, не Grep.

**Не-C# файлы** (`.yml`, `.csproj`, `.md`, `.json`, `.txt`): `Read`/`Write`/`Edit` свободно.

## Работа с `docs/`

`docs/` — спецификация в YAML, навигируется графом DocsWalker. По мере возможности обращаться к docs **через DocsWalker** (MCP, когда сервер зарегистрирован в Claude Code; иначе CLI через Bash), а не прямым чтением YAML-файлов. Это и проверяет интерфейс самого продукта на собственной документации, и даёт доступ к разрешённым ссылкам/путям/auto-include — то, чего сырой YAML не показывает.

**Чтение через DocsWalker:**
- Узлы по id — `get-nodes`.
- По пути / поддерево / предки — `get-by-path`, `get-subtree`, `get-ancestors`.
- Связи — `get-refs`, `get-in-refs`.
- Структура и схема — `get-map`, `describe-type`, `get-schema`, `get-meta-schema`, `get-usage-guide`.
- Поиск — `search`.

**Запись через DocsWalker:**
- Узлы — `create-node`, `update-node`, `delete-nodes`, `move-node`.
- Связи — `create-ref`, `delete-ref`, `redirect-refs`.
- Атомарные пакеты правок — `transaction`.
- Прямой `Edit`/`Write` на YAML в `docs/` для контентных изменений — только если DocsWalker не запускается или у задачи специфический повод (диагностика загрузчика, ручное восстановление).

**Когда прямое чтение YAML оправдано:**
- Отладка загрузчика DocsWalker (граф не строится — нужно смотреть сырой файл).
- Служебные файлы (`.docswalker/meta-schema.yml`, `Схема.yml`, `sequence.txt`) — DocsWalker их использует, но не редактирует, ручная правка штатна.
- Поиск по содержимому, который не покрывает `search` (например, поиск точной строки в комментариях YAML).

## Обратная связь о DocsWalker

DocsWalker — это продукт, который мы и разрабатываем, и используем (dogfooding). Когда при работе через DocsWalker (CLI или MCP) встречаешь любое неудобство — **сразу говори в чате**, не умалчивай и не обходи костылём. К этому относится:

- Не хватает команды или параметра под нужный кейс.
- Ответ слишком шумный (лишние поля) или слишком скудный (приходится делать N запросов).
- Сообщение об ошибке непонятное / без подсказки, что делать.
- Неожиданное поведение: упало, зависло, ответило не то, что описано в docs.
- Транспорт MCP неудобен: handshake, форматы, контекст сессии, длина ответа.
- Расхождение между поведением и описанием в `docs/`.

Любая из этих ситуаций — отдельной заметкой в чате, прямо в момент столкновения. Решение (фикс / правка docs / добавление параметра) — отдельным шагом, после согласования.

## Правила взаимодействия (выжимка из глобального CLAUDE.md)

- Противоречие или пробел в инструкции/доках — **сразу уведомить пользователя**,
  не выбирать удобную трактовку молча.
- Развилка между «архитектурно честным» и «костылём» — по умолчанию
  предлагать честный вариант. При значимой цене — вынести trade-off и
  спросить.
- Максимум 3 блокирующих вопроса за раунд.
- Идеи и улучшения — в конце ответа, реализация только после подтверждения.

## Активная сессия

Снапшот для устойчивости к сбросу контекста. Удалить целиком, как только
новая сессия восстановит контекст и работа пойдёт дальше.

### Незаданные вопросы

1. **Default tree resolution — lax или strict?** Сейчас в meta-schema
   реализован **lax**: `default_addressable_tree` — опциональное поле,
   если не задано и addressable tree один — он default автоматически.
   Зафиксировано в `docs/.docswalker/meta-schema.yml` (step-01).
   Пользователь явного подтверждения lax не давал — если в новой сессии
   возразит, переключить на strict в step-02 (поле обязательно при
   наличии addressable tree, ошибка `default_tree_required` при load).

### Активная задача

**stg-0010.kernel-as-service** — 6 шагов; step-01 в `[+]`, остальные в `[ ]`.

**Где остановился:** step-01 (spec-rewrite) **завершён**. Правки в
docs/: `docs/.docswalker/meta-schema.yml` (новые поля
`unique_sibling_titles` в `ref_def`, `default_addressable_tree` в
`schema_root` + обновлённые constraints); `docs/Схема.yml`
(`unique_sibling_titles: true` к path-tree во всех 10 типах + бамп v5→v6
в description); `docs/DocsWalker.yml` (удалено 9 узлов про
auto-spawn/kernel.json/discovery/kernel.lock/CLI команду kernel/--root
пример; обновлено ~30 узлов под новую модель; создан section
«Addressable trees» с 1 def + 5 rules + 5 examples; добавлены rule
«Auto-spawn убран» + def «kernel-config» + def «client-config» +
3 examples в Модели процесса). check-integrity ok; search по старым
ключевым словам пусто; `dotnet build` + `dotnet test` 152/152 зелёные.

**Что следующее (step-02 addressable-trees):**

1. Создать `.planning/stg-0010.kernel-as-service/step-02.addressable-trees.md`.
2. Пометить step-02 как `[*]`.
3. Расширить парсер meta-schema (`SchemaLoader.cs:ReadRefDef` —
   читать `unique_sibling_titles`; добавить `default_addressable_tree`
   в `MetaSchemaDocument`/`SchemaDocument`); расширить runtime
   `RefDef`/`TreeRefDescriptor`. Реализовать `duplicate_sibling_title`
   в `WriteState` для create/move/update в addressable tree. Расширить
   `get-by-path` параметром `--tree=<name>` с resolution-логикой (lax).
   Тесты: новые errors (`tree_not_addressable`, `tree_required`,
   `duplicate_sibling_title`) + sibling-collision на create/move/update.
4. После step-02 — `[*] → [+]`, atomic git add/commit/push.

### Ключевые предположения

- **stg-0009 (kill-seen) полностью завершена** — 4/4 шага `[+]`. dotnet
  test зелёный (152/152) после stg-0009 и после stg-0010 step-01.
- **Kernel `pid=57588, port=61532`** запущен из `publish/kernel/`
  (старая модель — per-user kernel.json discovery + auto-spawn).
  Step-01 правил docs/ через этот же kernel — он принял transaction-ы
  без перезапуска. После step-03 (client-server-reshape) этот kernel
  будет полностью заменён через kernel-config.
- **MetaSchemaLoader / SchemaLoader парсер lenient** к unknown полям
  (`default: r.SkipValue()` в `ReadRefDef`/`ParseSchema`/`ParseMetaSchema`/
  `ReadType`/`ReadTree`). Поэтому правка meta-schema/Схема.yml в step-01
  не сломала kernel — он просто игнорирует новые поля. Step-02 расширяет
  парсер, чтобы поля начали учитываться.
- **stg-0010 — одна страт,** addressable-trees внутри (решение
  `23ac212`). JSON-storage отложен на `stg-0011`.
- **Атомарность:** каждый step stg-0010 должен оставлять `dotnet build`
  + `dotnet test` зелёными. Шаги 03 (client-server-reshape) и 04
  (kill-auto-spawn) — крупные, тесты обновляются/удаляются внутри
  того же step-а.
- **`.dw/client.json`** — новая локация client-config (аналог `.claude/`).
- **Storage path в kernel-config = path к docs-folder напрямую** (не
  к project-folder). SchemaLoader/DocumentLoader предстоит
  отрефакторить в step-03.
- **`bash.exe.stackdump`** в untracked — pre-existing артефакт от
  прошлых сессий. Не журналю как хвост, не коммичу.
- **Glider workspace** — в начале новой сессии нужен `mcp__glider__load`
  с `D:\Dev\cs\projects\DocsWalker\DocsWalker.slnx` + `workingDirectory:
  D:\Dev\cs\projects\DocsWalker`.
- **DocsWalker CLI** — `publish\cli\DocsWalker.Cli.exe` отвечает,
  kernel auto-spawn-ится при отсутствии (старая модель).
- **Новые id из step-01:** addressable trees section #386; def
  «addressable tree» #392; rules #393-397; examples #387-391;
  rule «Auto-spawn убран» #383; def «kernel-config» #384; def
  «client-config» #385; новые examples #379-382.

