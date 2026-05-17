# stg-0004 — client-mode-default

## Цель

Перевести **все** существующие CLI-команды (кроме `run`) на единый клиент-режим: бинарь стартует → читает `run.pid` → коннектится в IPC → проксирует запрос/ответ. Старый in-process Dispatcher-путь для не-`run` команд удаляется полностью. Финал: новая модель становится единственной, нет двух режимов работы.

## Файлы

`src/DocsWalker.Cli/Program.cs` (или эквивалент main entrypoint) — изменить main code-path.

`src/DocsWalker.Cli/Cli/Dispatcher.cs` — почистить от прямого вызова in-process команд для не-`run` cmd. Оставить только серверный путь (для `run`).

`src/DocsWalker.Cli/Cli/IpcClient.cs` — финализация (подключение из шага 3 уже на месте).

`src/DocsWalker.Cli/Cli/PidFileReader.cs` (новый) — чтение `run.pid` + проверка живости через `StalePidDetector`.

`docs/DocsWalker.yml` — обновить inline-примеры команд если что-то стало неактуальным; обновить описание поведения «сервер не запущен» если по факту разошлось со спекой из шага 1.

## Действия

1. В `Program.Main`:
   - Парсить argv до cmd.
   - Если cmd == `run` → серверный путь (через `RunHandler`, шаг 4).
   - Иначе → клиент-режим: 
     - Получить `--root` из argv (обязательный для всех команд) либо взять CWD.
     - Прочитать `{root}/.docswalker/run.pid`. Если файла нет / pid мёртв → плоская ошибка `server_not_running` + хинт «запусти `docswalker run --root=…`» + exit 1.
     - Подключиться к pipe/socket из metadata pid-файла, вызвать `IpcClient.SendCommand(args)`.
     - Вывести ответ в Console.Out/Error по `Kind`, exit с code из ответа.
2. Удалить старый in-process Dispatcher-путь для не-`run` команд: код, который сейчас на каждом старте парсит YAML и обрабатывает команду в том же процессе. Этого пути больше нет.
3. Обновить `UsageGuideText.MentalModel`: подчеркнуть, что любая команда требует запущенного сервера; CI делает явный pipeline `run &` → команды → kill.
4. Прогнать integration тест:
   - Запустить `docswalker run --root=docs/` в фоне.
   - Прогнать `get-types`, `get-nodes --ids=1`, `describe-type --name=section`, `check-integrity` — все должны работать через IPC.
   - Прогнать без сервера — все должны падать с `server_not_running`.
   - Запустить два `run` подряд — второй должен падать с `server_already_running`.
   - Killовать серверный процесс — следующий клиентский вызов должен падать со `server_not_running` (после чистки stale pid).
5. Обновить inline-примеры в `docs/DocsWalker.yml` если архитектурный шаг 1 что-то упустил по поводу новой модели запуска.
6. Сборка Release-бинаря, прогон финального integration-теста на нём.
7. Glider-load workspace в начале сессии.

## Риски

- **`check-integrity` в CI**: после этого шага он работает только через сервер. CI-pipeline нужно обновить: либо запускать `run &` → `check-integrity` → kill, либо договориться о том, что CI просто собирает binary (без integrity-проверки). Это not-blocking для шага, но нужно зафиксировать в docs/инструкциях.
- **Stale pid + race**: клиент прочитал pid → сервер за это время умер → клиент коннектится в мёртвый pipe. Защита: на mismatch / connection-failed — повторно читаем pid; если уже отсутствует — `server_not_running`; если ещё там, но коннект не идёт — `server_died` + exit 1.
- **Dispatcher refactor пересечение**: если в шаге 3 был выбран рефактор `Dispatcher.Run` под возврат структурированного результата — здесь нужно убедиться, что клиентская сторона корректно маршалит payload в Console.
- **Удалённый старый код**: `git rm` неактуальных файлов — проверить через grep, что нет «зомби»-точек входа (например, какой-нибудь интеграционный тест ходит мимо main entrypoint в Dispatcher напрямую).
- **AOT-trim после удаления кода**: после чистки прогнать `dotnet publish -r win-x64` с проверкой trim warnings — должно быть 0.
