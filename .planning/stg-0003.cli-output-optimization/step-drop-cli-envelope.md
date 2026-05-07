# stg-0003 — drop-cli-envelope

## Цель
Убрать success/error-конверт CLI: stdout получает чистый result-объект, stderr — плоский error без вложенного `error: {…}`. exit-code и поток (stdout vs stderr) — единственный дискриминатор успех/ошибка. Параллельно — синхронизировать `UsageGuideText.MentalModel` под новый контракт.

## Файлы

- `src/DocsWalker.Cli/Cli/Output.cs` — основная правка. Удалить типы `SuccessEnvelope` и `ErrorEnvelope` как обёртки; `ErrorBody` — поля переходят в плоскую структуру. `Output.WriteSuccess(JsonNode?)` пишет результат напрямую в stdout. `Output.WriteSuccess(JsonNode?, bool applied)` подмешивает `applied` в сам result-объект (требует `JsonObject` — см. риски). `Output.WriteError(...)` пишет плоский объект `{code, message, path?, hint?, describe_type?}` в stderr.
- `src/DocsWalker.Cli/UsageGuide/UsageGuideText.cs` — переписать константу `MentalModel` под новый контракт (envelope-free, exit-code дискриминатор, плоская ошибка).
- `src/DocsWalker.Cli/Cli/Handlers/SchemaHandlers.cs`, `ReadHandlers.cs`, `WriteHandlers.cs` — проверить вызовы `Output.WriteSuccess`/`WriteError`. Read- и Schema-handler'ы зовут одну перегрузку `WriteSuccess(JsonNode?)` — структурно не меняются. Write-handler'ы зовут `WriteSuccess(result, applied)` — проверить, что `result` всегда `JsonObject`, иначе `applied` некуда положить (см. риски).
- `src/DocsWalker.Cli/Program.cs` — `Dispatcher.Run` маршрутизирует ошибки через `Output.WriteError` и возвращает 1. Структурно не меняется; проверить, что сигнатуры вызовов согласуются с новой формой.
- Тесты `tests/DocsWalker.Cli.Tests/` (если есть) и тесты Core, которые парсят вывод CLI — обновить эталоны под новый формат. Локализовать через Grep по подстрокам `\"ok\":true` / `\"ok\":false` / `\"result\"` в файлах под `tests/`.

## Действия

1. Прочитать `Output.cs` целиком, удалить типы `SuccessEnvelope`, `ErrorEnvelope`. Тип `ErrorBody` — снять, его поля становятся прямыми членами JSON-объекта в `WriteError`.

2. В `CliJsonContext` (атрибут `JsonSourceGenerationOptions` + `JsonSerializable`) убрать ссылки на удалённые типы. Оставить `JsonNode`, `JsonObject`, `JsonArray`, `JsonValue` — они нужны для прямой сериализации произвольного результата. Опции (snake-case, `WhenWritingNull`, encoder unsafe-relaxed) — без изменений.

3. Реализовать `Output.WriteSuccess(JsonNode? result)`:
   - сериализовать `result` напрямую через `JsonSerializer.Serialize(result, Options)`;
   - если `result` null — писать `{}` (пустой объект). null в stdout читать LLM сложнее. Все Read- и Schema-handler'ы возвращают не-null результат, так что фактически ветка не должна срабатывать; защита от регрессии.
   - вывод в `Console.Out.WriteLine(json)`.

4. Реализовать `Output.WriteSuccess(JsonNode? result, bool applied)` для write-команд:
   - требовать `result` типа `JsonObject` (cast через `as`; на null/не-объект — бросать `InvalidOperationException` с понятным сообщением «write-команда должна возвращать JsonObject»);
   - подмешать поле `applied` в сам объект: `obj["applied"] = JsonValue.Create(applied);`
   - сериализовать и вывести.

5. Реализовать `Output.WriteError(string code, string? path, string message, string? hint = null, JsonNode? describeType = null)`:
   - построить `JsonObject` напрямую с полями `code`, `message`, опционально `path` (через `DocumentPath.NormalizeForLlm`), `hint`, `describe_type` (snake-case ключ). null-поля не добавлять (вместо опоры на `WhenWritingNull` — просто не добавляем поле, так нагляднее);
   - сериализовать через тот же `Options`-инстанс;
   - вывод в `Console.Error.WriteLine(json)`.

6. Переписать `UsageGuideText.MentalModel` (const string) под новый контракт. Текст должен явно зафиксировать:
   - успех — exit 0, stdout — JSON-результат команды напрямую;
   - ошибка — exit ≠ 0, stderr — JSON `{code, message, path?, hint?, describe_type?}`;
   - для write-команд `applied: true|false` присутствует как top-level поле результата;
   - дискриминатор — exit-code.
   - Не упоминать `ok:true`/`ok:false`/`result:`/`error:` как имена полей-обёрток.

7. В `WriteHandlers.cs` — если какой-то handler формирует результат не как `JsonObject` (например, возвращает массив), перевести его на `JsonObject`-результат. Поле `applied` принципиально лежит на верхнем уровне рядом с другими полями результата.

8. Прогнать `dotnet build` — должно собраться. Прогнать `dotnet test` — тесты, которые сравнивают вывод по строке, отвалятся; переписать эталоны под новый формат.

9. Прогнать вручную репрезентативный набор:
   - `docswalker get-map` — stdout должен начинаться сразу с массива, без `{"ok":true,"result":…}`;
   - `docswalker get-nodes --ids=1,2` — stdout — массив объектов узлов;
   - `docswalker get-refs --id=1` — stdout — объект `{in: […], out: […]}` (старая плоская форма, переход на map-form — следующий шаг);
   - `docswalker create-node --type=section --title=Foo --path=1 --dry-run=true` — stdout — объект `{id:…, type:…, title:…, applied:false}`;
   - `docswalker get-nodes` (без `--ids`) — stderr — `{code:"missing_parameter", message:…}`, exit 1;
   - `docswalker create-node --type=section --title=Foo` (без `--path`) — stderr — `{code:"missing_required_ref", message:…, hint:…, describe_type:{…}}`, exit 1.

10. Закоммитить + push (атомарные git-вызовы по правилу проекта).

## Риски

- **`WriteSuccess(result, applied)` ожидает `JsonObject`.** Если хотя бы один write-handler возвращает массив или скаляр — `applied` некуда положить. Локализация: пройтись по всем вызовам `WriteSuccess(_, true|false)` в `WriteHandlers.cs` (через Glider `find_callers WriteSuccess`), убедиться, что каждый строит `JsonObject`. Если нет — поправить handler до основной правки `Output.cs`.
- **Read-команды на верхнем уровне могут возвращать массив** (как `get-map` и `get-nodes` сейчас). Для read-команд `applied` не нужен, и текущая форма «массив на верхнем уровне» — валидный JSON, LLM нормально парсит. Опасности нет; просто проверить, что `Output.WriteSuccess(JsonNode?)` корректно сериализует и `JsonObject`, и `JsonArray`.
- **Тесты, прибитые к точному JSON.** Все эталонные строки с `{"ok":true,...` отвалятся одновременно. Решение: разовая массовая правка эталонов; в дальнейшем тесты лучше сравнивать через `JsonNode.DeepEquals` или подобное, но это отдельная задача — не в скоупе шага.
- **`UsageGuideText.MentalModel` уже на этом шаге описывает новый контракт, хотя `get-refs` map-form, defaults/empties и trim describe_type ещё не реализованы.** Решение: формулировать MentalModel сразу под итоговый контракт всей стратегии. Пользователь увидит расхождение «mental model говорит X, а команда отдаёт Y» только в окне между шагами 2 и 5 — приемлемо, тем более что промежуточные шаги катятся быстро, и LLM, если успеет столкнуться, увидит расхождение как обучающий сигнал «формат ещё не везде синхронизирован».
