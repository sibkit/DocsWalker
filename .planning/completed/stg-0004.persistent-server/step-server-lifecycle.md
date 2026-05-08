# stg-0004 — server-lifecycle

## Цель

Реализовать низкоуровневый каркас серверного процесса: захват эксклюзивного lock'а на `docs/`, ведение pid-файла со stale-detection, открытие/закрытие платформенно-зависимого IPC-handle (named pipe Win / Unix socket POSIX), graceful shutdown на SIGINT/SIGTERM/Ctrl+C. Без бизнес-логики команд — только «процесс взял ресурсы, держит, корректно отпустил».

## Файлы

`src/DocsWalker.Core/Server/Lifecycle.cs` (новый) — класс `ServerLifecycle` с методами `Acquire(rootPath)`, `Release()`, `IsHeld`. Внутри: file-lock через `FileStream` с `FileShare.None` (или `FileShare.Read` если нужно) либо платформенно-зависимый OS lock; pid-файл с metadata (`pid`, `root_hash`, `pipe_name`, `started_at`, `client_version`).

`src/DocsWalker.Core/Server/Ipc/IpcChannel.cs` (новый) — abstract base class или interface: `Listen()`, `AcceptAsync()`, `Dispose()`.

`src/DocsWalker.Core/Server/Ipc/NamedPipeChannel.cs` (новый, Windows-only код в `#if WINDOWS` или через runtime-detection) — реализация на `NamedPipeServerStream`.

`src/DocsWalker.Core/Server/Ipc/UnixSocketChannel.cs` (новый, POSIX-only) — реализация на `UnixDomainSocketEndPoint` + `Socket`.

`src/DocsWalker.Core/Server/Ipc/IpcChannelFactory.cs` (новый) — `Create(rootHash)`: возвращает нужную реализацию по `OperatingSystem.IsWindows()`.

`src/DocsWalker.Core/Server/StalePidDetector.cs` (новый) — функция «жив ли процесс с таким pid и наш ли он» (на Win — `Process.GetProcessById` + проверка имени; на POSIX — `kill(pid, 0)` + чтение `/proc/{pid}/comm` или эквивалент).

`src/DocsWalker.Core/Server/SignalHandler.cs` (новый) — подписка на `Console.CancelKeyPress`, `AppDomain.ProcessExit`, POSIX-сигналы (если есть API). Триггерит `IShutdownToken`.

## Действия

1. Реализовать `Lifecycle.Acquire(rootPath)`:
   - Вычислить `rootHash` (SHA-256 от absolute path → первые N hex-символов).
   - Открыть `{root}/.docswalker/run.lock` с эксклюзивным режимом. Если другой процесс держит lock — попытаться прочитать `run.pid`, через `StalePidDetector` проверить живость; если жив — вернуть ошибку `ServerAlreadyRunning(otherPid)`; если мёртв — перезаписать pid и забрать lock (с гонкой между проверкой и записью разобраться через retry-loop с small back-off).
   - Создать `IpcChannel` через factory.
   - Записать `run.pid` (атомарно: temp + rename).
2. Реализовать `Lifecycle.Release()`:
   - Закрыть `IpcChannel`.
   - Удалить `run.pid` (best-effort; если не удалось — лог-варнинг).
   - Закрыть lock-файл (lock отпускается автоматически).
3. Реализовать `IpcChannel` — Listen/Accept/Dispose. Конкретные реализации тестируются изолированно (минимум — smoke test на «открылось / можно подключиться / закрылось»).
4. Реализовать `SignalHandler`: при первом сигнале — триггерим `CancellationToken`; при повторном — force-exit (защита от зависшего shutdown). Не интегрировать в `Lifecycle` напрямую — `Lifecycle` принимает `CancellationToken` снаружи.
5. Glider-load workspace в начале сессии (mandatory per project CLAUDE.md). Все навигация по C# — через Glider tools.

## Риски

- **Гонка при stale-pid takeover**: между проверкой «pid мёртв» и нашим `OpenWrite(run.pid)` другой процесс может уже забрать lock. Защита: lock-файл берётся первым; запись pid'а — после успешного lock'а.
- **`FileShare.None` lock на Linux**: .NET file-lock на Linux реализован через `flock`/`fcntl`, поведение между процессами надёжно, но между потоками одного процесса — нет. Проверить: захват ровно одним fd, не двумя.
- **Named pipe ACL на Windows**: по умолчанию pipe доступен слишком широко. Указать ACL «owner only» через `PipeSecurity` явно.
- **Unix socket file leftover**: при кривом завершении (kill -9) `.sock`-файл остаётся. При следующем `Listen()` через `bind()` будет `EADDRINUSE`. Защита: перед `bind` проверить — если есть pid-файл и pid мёртв, удалить `.sock` и продолжить; иначе ошибка.
- **Slim runtime AOT-флаги**: `NamedPipeServerStream` и `UnixDomainSocketEndPoint` должны работать в native-AOT. Проверить `dotnet publish -c Release -r win-x64 --self-contained` и аналогично на Linux после реализации; trim warnings — нулевые.
