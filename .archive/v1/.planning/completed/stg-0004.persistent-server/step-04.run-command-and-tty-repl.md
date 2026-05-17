# stg-0004 — run-command-and-tty-repl

## Цель

Реализовать команду `docswalker run --root=...`: интегрировать `ServerLifecycle` (захват lock + IPC) и `IpcServer` (accept-loop), завести её в `Commands.cs` и `UsageGuideText`. По `Console.IsInputRedirected` ветвление: TTY → REPL-prompt поверх того же диспетчера; редирект/headless → блокирующее ожидание сигнала на завершение.

## Файлы

`src/DocsWalker.Cli/Cli/Commands.cs` — добавить регистрацию команды `run` (Req `--root`, Opt `--quiet`).

`src/DocsWalker.Cli/Cli/Handlers/RunHandler.cs` (новый) — handler команды `run`. Захват `ServerLifecycle`, старт `IpcServer`, ветка TTY vs headless, graceful shutdown.

`src/DocsWalker.Cli/Cli/Repl/Repl.cs` (новый) — REPL-loop: читает строку → парсит как argv существующим парсером (или Tokenizer'ом из `Commands`) → вызывает тот же путь, что обрабатывает IPC-запросы (через сериализованную через `SemaphoreSlim` точку входа). Печатает ответ в Console.Out/Error.

`src/DocsWalker.Cli/Cli/Repl/LineReader.cs` (новый) — нативный line-edit на консольных API. Минимум — backspace, стрелки influence (если есть), Ctrl+C → текущая строка отменяется (а не процесс), Ctrl+D / `:quit` → выход из REPL и завершение сервера.

`src/DocsWalker.Core/UsageGuide/UsageGuideText.cs` — добавить описание команды `run` в usage-guide.

`docs/DocsWalker.yml` — добавить inline-пример `docswalker run --root=docs/` в нужное место (если оно есть в текущей структуре).

## Действия

1. Зарегистрировать команду `run` в `Commands.cs` рядом с другими.
2. В `RunHandler.Handle(args)`:
   - `Acquire(root)` через `ServerLifecycle`. При уже работающем сервере (server_already_running) — печать ошибки + exit 1.
   - Если `--quiet=false` (default) — баннер «DocsWalker server started, root=…, pid=…, pipe=…, ProtocolVersion=…».
   - Запустить `IpcServer.StartAsync(channel)` — accept-loop в фоне.
   - Создать общий `CancellationTokenSource` `shutdown`, прокинуть в `IpcServer` и в REPL.
3. Ветвление TTY:
   - `Console.IsInputRedirected == false` → запустить `Repl.RunAsync(shutdownToken)`. По выходу из REPL (Ctrl+D / `:quit` / Ctrl+C дважды) — `shutdown.Cancel()`.
   - Иначе → `await shutdown.Token.WaitAsync()` (ждём сигнала).
4. По `shutdown` — остановить `IpcServer` (graceful: дождаться текущего запроса), `Lifecycle.Release()`, exit 0.
5. REPL-loop:
   - Print `dw> `, прочитать строку через `LineReader`.
   - Если строка пустая / пробелы → следующая итерация.
   - Если `:quit`, `:exit`, EOF → break loop.
   - Иначе: парсить как argv (тот же Tokenizer что для IPC-запросов), вызвать сериализованную через `SemaphoreSlim` точку обработки, напечатать `Response.Payload` в Console.Out (если `Kind=="ok"`) или Console.Error (`error`).
6. Описать команду `run` в `UsageGuideText`: параметры, поведение TTY/headless, как её правильно гасить.
7. Добавить inline-пример в YAML (если архитектурный шаг 1 уже выделил место).
8. Glider-load workspace в начале сессии. Тестирование вручную: запуск из `pwsh` (TTY) — увидеть REPL; запуск через `docswalker run --root=. </dev/null` (редирект) — увидеть headless-ожидание.

## Риски

- **`Console.IsInputRedirected` на Windows + Claude Code**: Claude Code запускает CLI через pty или через CreateProcess с inherited handles. Поведение `IsInputRedirected` нужно проверить эмпирически — возможно, оно скажет «не TTY» в условиях, где пользователь ожидает REPL. Защита: разрешить явный override через `--mode=tty|headless`.
- **REPL line-edit complexity**: «нативный readline» в .NET — это `Console.ReadKey` + ручная сборка строки. Для MVP достаточно простого — backspace + Enter + Ctrl+C. Не делать autocomplete, не делать историю.
- **Ctrl+C двойная семантика**: «отмени текущую строку» vs «выйди из REPL» — общая UX-проблема. Решение: одиночный Ctrl+C — отмена строки; `:quit` или Ctrl+D — выход.
- **Single-instance per root**: если пользователь запустит второй `docswalker run --root=docs/` — должен получить понятный отказ (`server_already_running, pid=X`), не повисший процесс.
- **Зависший серверный handler**: один долгий запрос блокирует семафор → REPL и IPC-клиенты ждут. Это ОЖИДАЕМО (см. принятое решение #8 — сериализация). Просто убедиться, что не deadlock.
