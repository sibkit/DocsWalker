# stg-0002 — refs-model-cli

## Цель
Закрыть R7/R8-промежуточный red-build на стороне CLI: переписать
`DocsWalker.Cli` (Commands, Dispatcher, ReadHandlers, WriteHandlers,
ArgParser-валидация) под refs-модель. После R9 проект `DocsWalker.Cli`
собирается без ошибок, CLI-команды один-в-один соответствуют новому API
из `DocsWalker.Core` (R6/R7/R8) и описанию в `docs/DocsWalker.yml`
(секция `(#27) Операции записи` / `(#35) CLI-интерфейс`).

## Файлы

**Переписаны:**
- `src/DocsWalker.Cli/Cli/Commands.cs` — фиксированные спецификации команд
  под новое API. `add_ref_type` удалён. `update_node`: вместо `--patch`
  поля `--title?` / `--text?`. `move_node`: вместо `--new-parent-id`
  / `--new-block-name` поле `--new-path`. `create_ref` / `delete_ref`:
  параметр `type` переименован в `name`. `get_refs` / `get_in_refs`:
  параметр `origin` удалён, `type` переименован в `name`. `create_node` —
  динамическая команда (флаг `DynamicParams=true`), фиксированы только
  `type`, `title`, `text`; имена связей принимаются как произвольные
  `--<ref-name>=<id-list>` без декларации в `Commands.cs`.
- `src/DocsWalker.Cli/Program.cs` (Dispatcher) — снят case `add_ref_type`;
  `create-node` маршрутизируется на dynamic-handler, который сам разбирает
  refs-параметры. Generic-валидатор пропускает unknown-keys для
  динамических команд. `move-node` использует `--new-path` (имя поля API
  тоже `new_path` в docs).
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — новые сигнатуры
  операций:
  - `CreateNode(root, args)` — берёт `type`, `title`, `text?`, остальные
    `--<key>=<id-list>` парсит как `IReadOnlyList<int>` и кладёт в
    `Refs: Map<name, List<int>>`. Ничего не валидирует против Схемы —
    эту работу делает `WriteApi.ApplyCreateNode` (unknown_ref / cardinality
    / missing_required_ref / parent_not_found).
  - `UpdateNode(root, args)` — `UpdateNodeOp(Id, NewTitle, NewText)`.
    Хотя бы одно из `title`/`text` должно быть передано (иначе
    `invalid_parameter`).
  - `MoveNode(root, args)` — `MoveNodeOp(Id, NewParentId)`,
    `NewParentId` берётся из `--new-path`.
  - `CreateRef` / `DeleteRef` — `Name` берётся из `--name`.
  - `AddRefType` — удалён вместе с case.
- `src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — новые сигнатуры:
  - `GetRefs(root, id, name?)` / `GetInRefs(root, id, name?)` — без
    origin. Удалены `TryParseOrigin` и приватная запись `OriginError`.
  - `GetNodes` / `GetByPath` — вызовы `ReadApiJson.NodesToJson(nodes)` и
    `ReadApiJson.SubtreeToJson(subtree)` с одним аргументом (R8 убрал
    второй).

**Не трогаются:**
- `src/DocsWalker.Cli/Cli/ArgParser.cs` — текущий парсер не хранит
  знаний о наборе параметров и работает корректно. Валидация типов и
  unknown-keys остаётся в Dispatcher.
- `src/DocsWalker.Cli/Cli/Output.cs`, `SchemaHandlers.cs` — не зависят
  от refs-модели.
- `tests/DocsWalker.Tests/*` — отдельный шаг после R10/обновления
  фикстур.

## Принятые решения

1. **Динамическая команда `create-node` без декларации refs.**
   Имена out_refs живут в Схеме проекта; декларировать их повторно в
   `Commands.cs` — дубликат, который будет рассыпаться при правке
   Схемы. Поэтому `Commands.cs` фиксирует только `type`, `title`,
   `text`, а Dispatcher для команд с `DynamicParams=true` пропускает
   проверку unknown-параметра. Любой `--<x>=<y>` вне фиксированных и
   `--root` уходит в `WriteHandlers.CreateNode`, который кладёт его в
   `Refs[x] = parse(y)`. Валидацию имени и значений целей делает
   `WriteApi`. Совпадает с правилом #159 в `docs/DocsWalker.yml`:
   «для create-node имена параметров required-связей берутся из
   контракта типа в Схеме».

2. **`update-node` принимает `title?` / `text?`, не `patch`.**
   Новый `UpdateNodeOp` имеет `NewTitle?, NewText?`, JSON-объекта
   нет. Параметр `--patch` уходит как несовместимый с новым контрактом.

3. **`move-node` — параметр `--new-path`, не `--new-parent-id`.**
   В `docs/DocsWalker.yml/#73` пример: `move-node --id=42 --new-path=8`.
   В коде имя параметра — `new_path`/`--new-path`; в `MoveNodeOp` поле
   называется `NewParentId` (внутреннее имя ядра, не CLI-имя).
   Пользователю CLI и докам в Схеме видно `path` — то же имя, что у
   встроенной связи.

4. **Параметр `origin` уходит из get-refs / get-in-refs.**
   В refs-модели нет explicit/system/default различения; единственный
   фильтр — имя связи (string). Чтобы не тащить старое имя `type`
   (которое в новом API занято — это тип узла, а связи имеют `name`),
   переименовываем CLI-параметр в `--name`.

5. **`add-ref-type` удалена целиком.**
   Расширение Схемы — прямая правка `docs/Схема.yml` (см. правило
   #155 в `docs/DocsWalker.yml`). Отдельной API/CLI-команды для этого
   нет и не будет в текущей итерации.

6. **CLI не дублирует валидацию Core.**
   Дополнительные проверки cardinality / target_types / unknown_ref /
   missing_required_ref остаются в `WriteApi`. CLI-handler формирует
   операцию и пробрасывает `WriteApiException` через существующий
   error-конверт. Это сохраняет «валидация ровно в одном месте».

## Действия

1. Переписать `Cli/Commands.cs` под новый набор команд. Добавить флаг
   `bool DynamicParams` в `CommandSpec`. У `create_node` —
   `DynamicParams=true`, фиксированные параметры `type` (req, String),
   `title` (req, String), `text` (opt, String).
2. В `Program.cs` (Dispatcher) → `TryValidateParams`: для команд с
   `DynamicParams=true` пропускать ветку «unknown_parameter». Required
   и type-validation для фиксированных параметров остаются. Снять case
   `add_ref_type`. `create_node` маршрутизировать сразу в
   `WriteHandlers.CreateNode(rootPath, parsed.Params)`.
3. Переписать `Cli/Handlers/WriteHandlers.cs`:
   - `CreateNode` извлекает `type`, `title`, `text?`, отбрасывает
     служебные ключи (`root`), остальные — парсит как ID-список;
     невалидные — `invalid_parameter` с подсказкой формата.
   - `UpdateNode` строит `UpdateNodeOp(Id, NewTitle, NewText)` из
     `--title?` / `--text?`. Если оба null — `invalid_parameter`
     («нужно указать хотя бы --title или --text»).
   - `MoveNode` строит `MoveNodeOp(Id, NewParentId)` из `--id` и
     `--new-path`.
   - `CreateRef` / `DeleteRef` берут `Name` из `--name`.
   - Удалить `AddRefType` целиком.
4. Переписать `Cli/Handlers/ReadHandlers.cs`:
   - `GetRefs(root, int id, string? name)` / `GetInRefs(...)` —
     удалить origin-плечо, удалить приватный `TryParseOrigin` /
     `OriginError`.
   - В `GetNodes` / `GetByPath` обновить вызовы `NodesToJson` /
     `SubtreeToJson` под новые сигнатуры (один аргумент).
5. `dotnet build src/DocsWalker.Cli` — должен быть зелёным; одновременно
   `DocsWalker.Core` остаётся зелёным (R8 уже закрыт).
6. Прогон `docs/Схема.yml` / `docs/DocsWalker.yml` на предмет
   рассогласования с CLI после правок: имена параметров
   (`--new-path`, `--name`, динамические `--<ref-name>`) уже описаны
   в правилах #154/#159. Доработка docs не требуется.

## Риски

- `DocsWalker.Tests` остаются красными — они опираются на старые
  сигнатуры (CreateNode принимал `parent_id`, RefSet содержал
  `origin`, и т. п.). Перенос тестов — отдельный шаг после R10.
- LLM-агенты, использовавшие старые имена параметров (`--type` для
  create-ref, `--patch` для update-node, `--origin` для get-refs),
  получат `unknown_parameter`. Обратной совместимости в рамках
  refs-rewrite нет (auto-mode, без shim).
- Динамическая команда `create-node` ослабляет CLI-side проверку имён
  параметров: опечатка вроде `--paht=1` (вместо `--path`) пройдёт
  парсер и упадёт в `WriteApi.ApplyCreateNode` как
  `unknown_ref: Тип 'X' не объявляет связь 'paht'`. Это приемлемо:
  ошибка структурированная, code/message информативны, дубликат
  валидации в CLI был бы хрупким (Схема единственный источник
  правды о связях).
