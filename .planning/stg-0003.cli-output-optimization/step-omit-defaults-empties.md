# stg-0003 — omit-defaults-empties

## Цель
Сократить шум в JSON-выводе: не сериализовать значения, равные дефолтам, и пустые коллекции, которые LLM может вывести без явного присутствия в JSON. Все четыре правила — независимые, делаются в одной пачке, потому что каждая правка локальна.

## Файлы

- `src/DocsWalker.Core/Api/ReadApiJson.cs` (или иной слой сериализации `get-map`/`get-subtree`) — пропуск `children: []` и `subtree_tokens` если == `tokens`.
- `src/DocsWalker.Core/Api/SchemaSerializer.cs` (или иной слой сериализации `describe-type`/`get-schema`) — пропуск `cardinality:"many"` и `required:false` в RefDef.
- `src/DocsWalker.Cli/UsageGuide/CliUsageGuideSource.cs` — пропуск пустых `parameters` и `examples` в команде.
- `src/DocsWalker.Core/Api/UsageGuideCommand.cs` (или DTO в Core, где он определён) — поля `Parameters`/`Examples` сделать nullable, чтобы `JsonIgnoreCondition.WhenWritingNull` срабатывал; либо передавать null из `CliUsageGuideSource`.
- `src/DocsWalker.Core/Schema/*.cs` — парсер мета-схемы и схемы. Проверить, что отсутствие поля `cardinality:` в YAML трактуется как `Many`, отсутствие `required:` — как `false`. Если уже трактуется — править нечего; если требует явного значения — добавить дефолтную трактовку.
- Тесты — `tests/DocsWalker.Core.Tests/` и `tests/DocsWalker.Cli.Tests/`. Любой тест, сравнивающий JSON по строке и содержащий `\"children\":[]`, `\"subtree_tokens\"`, `\"cardinality\":\"many\"`, `\"required\":false`, `\"parameters\":[]`, `\"examples\":[]` — обновить.

## Действия

### Парсер дефолтов (предпосылка)

1. Через Glider найти парсер RefDef в схеме: `mcp__glider__search_symbols RefDef` либо `find_code cardinality intent=symbol` в проекте `DocsWalker.Core`.
2. Прочитать парсер. Убедиться, что:
   - отсутствие `cardinality:` в YAML → значение `Cardinality.Many`;
   - отсутствие `required:` в YAML → значение `false`.
3. Если оба условия выполняются — пропустить шаг к серии (а)–(d). Если хотя бы одно требует явного значения — поправить парсер: при отсутствии поля подставлять дефолт без ошибки.
4. На tree-refs `cardinality`/`required` уже сейчас не указываются (мета-схема запрещает их рядом с `tree:`). Это контекст; правило про дефолты не отменяет запрет на tree-refs.

### (a) Пропуск `cardinality:"many"` и `required:false` в выводе RefDef

1. Через Glider найти сериализатор RefDef для `describe-type` и `get-schema`. Скорее всего — общий метод в Core, отдающий `JsonObject`.
2. Условие пропуска:
   - если `cardinality == Cardinality.Many` — не добавлять поле `cardinality`;
   - если `required == false` — не добавлять поле `required`.
3. На tree-refs (где этих полей и сейчас нет) поведение не меняется.
4. Проверить вручную: `docswalker describe-type --name=section` — у `path` нет cardinality/required (как сейчас), у остальных out_refs нет `cardinality:"many"` и `required:false`. `docswalker get-schema` — то же.

### (b) Пропуск `children: []` в `get-map`/`get-subtree`

1. Через Glider найти сериализатор узлов с детьми: `find_code "children" intent=literalText` в `DocsWalker.Core/Api/`. Найти место, где собирается JsonArray детей.
2. Условие пропуска: если массив пуст — не добавлять ключ `children` в результат.
3. Проверить вручную: `docswalker get-map` — у листовых узлов нет `"children":[]`. `docswalker get-subtree --id=<leaf>` — у возвращённого узла нет `"children":[]`.

### (c) Пропуск `subtree_tokens`, равного `tokens`

1. Через Glider найти место, где `subtree_tokens` пишется. По идее — рядом с `tokens` в том же сериализаторе узлов для get-map/get-subtree.
2. Условие пропуска: `subtree_tokens == tokens` (это ровно случай узла без потомков в текущем поддереве — лист по контексту запроса). Не добавлять ключ.
3. Это правило уже зафиксировано в LLM-guide шагом `cli-output-spec` («отсутствие поля ⇒ узел листовой, `subtree_tokens = tokens`»). Шаг просто реализует.
4. Проверить вручную: `docswalker get-map` — у листов нет `subtree_tokens`. У не-листов он по-прежнему присутствует и ≠ `tokens`.

### (d) Пропуск `parameters: []` и `examples: []` в `get-usage-guide`

1. Прочитать `src/DocsWalker.Cli/UsageGuide/CliUsageGuideSource.cs`. Текущий код всегда передаёт пустой массив, даже когда параметров/примеров нет.
2. Найти DTO `UsageGuideCommand` в Core: `mcp__glider__search_symbols UsageGuideCommand kinds=Type`. Прочитать определение.
3. Сделать поля `Parameters`/`Examples` nullable (тип `IReadOnlyList<…>?`).
4. В `CliUsageGuideSource.GetCommands()`:
   - если `cmd.Params.Count == 0` — передавать `null` вместо пустого списка;
   - если `cmd.Examples` null или пустой — передавать `null`.
5. Проверить, что сериализация DTO опускает null благодаря `JsonIgnoreCondition.WhenWritingNull`. Если DTO сериализуется не через `CliJsonContext`, а через свой Core-контекст — добавить `WhenWritingNull` и в нём.
6. Проверить вручную: `docswalker get-usage-guide` — у команд без параметров (`get-meta-schema`, `get-schema`, `get-map`, `check-integrity`, `get-usage-guide`) нет ключа `parameters`. У команд без примеров (если такие есть) — нет ключа `examples`.

### Завершение

7. Прогнать `dotnet test`. Тесты, сравнивающие точную строку JSON, отвалятся в местах с удалёнными полями — переписать эталоны под новый формат.
8. Прогнать `docswalker check-integrity` — должна вернуть пустой `errors` (правка сериализации не должна влиять на содержимое графа).
9. Закоммитить + push. Если шаг катится в одной пачке с `trim-error-describe-type` — общий commit на оба.

## Риски

- **Round-trip Schema-парсер.** Если YAML-парсер мета-схемы или схемы при чтении требует явного `cardinality:`/`required:` — пропуск при сериализации сломает round-trip (записать `describe-type` ↔ прочитать YAML обратно). Защищаемся шагом (1) — парсер должен подставлять дефолты при отсутствии поля. Если парсер этого не делает и кейс round-trip есть в тестах — поправить парсер до сериализатора.
- **`subtree_tokens` для не-листов в краевых случаях.** Возможен случай, когда узел не лист, но все потомки исключены фильтром (например, `--depth=0`) — тогда `subtree_tokens == tokens`, и поле опустится. Это корректно по семантике «отсутствие = равенство», но если LLM рассчитывает на наличие `subtree_tokens` для всех не-листов — она ошибётся. Подтверждение, что это семантика по дизайну (а не баг) — фиксируется в LLM-guide шагом `cli-output-spec`.
- **`UsageGuideCommand` как record с required-полями.** Если поля `Parameters`/`Examples` объявлены как required — переход на nullable требует менять сигнатуру и все места конструирования. Это в скоупе шага. Если есть тесты, сравнивающие сериализованный JSON команды — переписать эталоны.
