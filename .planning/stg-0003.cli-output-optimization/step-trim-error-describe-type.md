# stg-0003 — trim-error-describe-type

## Цель
Сократить токены в ошибках записи: при ошибках с локализацией на конкретной связи (`missing_required_ref`, `invalid_ref_value` и аналогах) embedded `describe_type` отдаёт только проблемный ref в `out_refs`, а не весь контракт типа. Шапка типа (`name`, `description`, `text_required`) остаётся.

## Файлы

- `src/DocsWalker.Cli/Cli/ErrorEnrichment.cs` — текущий `TryDescribeType(rootPath, typeName)` отдаёт полный describe-type. Расширить сигнатурой `TryDescribeType(rootPath, typeName, focusRefName?)` — при заданном focus отфильтровать `out_refs` JSON-результата до записи с этим именем.
- `src/DocsWalker.Cli/Program.cs` — `Dispatcher.Run` зовёт `ErrorEnrichment.TryDescribeType` в `TryValidateParams`-ветке (строки около 45). Эта ветка — про валидацию параметров CLI, не локализована конкретной связью; здесь focus-ref остаётся null, форма прежняя (полный describe_type).
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — все write-handler'ы, которые перехватывают Core-ошибки и зовут `Output.WriteError` с embedded describe_type. Передавать туда `focusRefName`, если ошибка несёт это имя.
- `src/DocsWalker.Core/Api/WriteApi.cs` (или место, где Core формирует ошибки `missing_required_ref`/`invalid_ref_value`) — проверить, что в Core-Result/Exception доступно имя проблемной связи как структурное поле (а не только в message). Если нет — добавить.
- Тесты `tests/DocsWalker.Cli.Tests/` или Core — обновить эталоны под trim'нутый describe_type в этих ошибках.

## Действия

1. Через Glider найти все места формирования `missing_required_ref` в Core: `mcp__glider__find_code "missing_required_ref" intent=literalText`. Аналогично для `invalid_ref_value`. Понять, какие ещё ref-локализованные коды ошибок существуют (по списку: `missing_required_ref`, `invalid_ref_value`, потенциально `unknown_ref`, `dangling_ref` — точный список — по результату исследования).

2. Проверить структуру Core-ошибки. Если используется `Result<T, Error>` или подобное — посмотреть `Error`-тип. У ref-локализованных ошибок должно быть поле `RefName` (или эквивалентное). Если поля нет:
   - добавить в `Error`-тип опциональное поле `RefName`;
   - в местах формирования ошибки заполнить его.
   - Это часть write-API контракта; расширение, не breaking.

3. В `ErrorEnrichment.TryDescribeType` добавить опциональный параметр `string? focusRefName = null`:
   - текущая логика — построить describe_type-объект через тот же Core-API, что и команда `describe-type`;
   - после построения, если `focusRefName != null` — в полученном объекте отфильтровать `out_refs`-массив до записей, у которых поле `name == focusRefName`. Если такая запись не найдена — оставить пустой `out_refs: []`. Шапку типа не трогать.

4. В `WriteHandlers.cs` для каждого handler'а:
   - на каждой write-команде, при перехвате Core-ошибки, если у ошибки есть `RefName` — передавать его в `TryDescribeType`;
   - результат класть в `describeType` параметр `Output.WriteError`;
   - если `RefName` нет (ошибка не ref-локализована) — текущее поведение, полный describe_type.

5. Тесты — для каждого error-case с ref-локализацией проверить, что `error.describe_type.out_refs` содержит ровно одну запись (с именем из `error.message`/контекста). Конкретные команды:
   - `create-node --type=section --title=Foo` (без `--path`) — `error.describe_type.out_refs` = только запись с `name: "path"`;
   - `create-node --type=rule --title=Foo --text=bar --path=42` (без `--examples`, у `rule` есть обязательная связь `examples`) — `out_refs` = только запись `examples`;
   - `create-ref --from-id=42 --name=examples --to-id=99999` (если `99999` не существует) — `error.describe_type.out_refs` = только запись `examples`;
   - `delete-ref --from-id=42 --name=path --to-id=1` (если path — required tree-ref, нельзя удалить последний) — `out_refs` = только запись `path`.

6. Прогнать вручную те же сценарии в реальном CLI, проверить размер JSON в stderr — должен заметно уменьшиться по сравнению с прежним полным describe_type.

7. Закоммитить + push. Если шаг катится в одной пачке с `omit-defaults-empties` — общий commit на оба.

## Риски

- **Core-ошибка не несёт `RefName` структурно.** Тогда trim сделать нельзя без правки Core-контракта. Решение: добавить поле в Core-Error-тип. Это не breaking — просто новое опциональное поле; внешний консьюмер увидит его как extra-данные.
- **Множественные ref-ошибки в одной операции.** Теоретически в `transaction` или batch-валидации может быть ошибка про несколько ref'ов сразу. Решение: один error-объект, один focus-ref — берём первый из перечня. Multi-ref-trim — на следующую итерацию, если кейс встретится. В скоупе шага не делаем.
- **`unknown_ref` (имя ref'а не объявлено в типе).** Если LLM передала `--<wrong_name>=<id>` — тип-то известен, имя — нет. После trim'а `out_refs` будет пустой массив. Это сигнал «такой связи у типа нет», что и так выражено в `error.message` и `error.code`. Допустимо.
- **Шапка типа в trim-режиме.** Поля `name`, `description`, `text_required` остаются всегда. `text_required` иногда будет нерелевантен (ошибка про ref, не про text), но он дешёвый по токенам и помогает LLM вспомнить контекст типа без отдельного вызова `describe-type`.
