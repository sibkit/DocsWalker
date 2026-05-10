# stg-0008 — step-03 — discovery-and-spawn

## Цель

Закрыть инфраструктуру discovery ядра и detached spawn для клиентов:

- **Per-user discovery** (`kernel.json`/`kernel.lock`):
  - Windows: `%LOCALAPPDATA%\DocsWalker\kernel.json` и `kernel.lock`.
  - POSIX: `${XDG_RUNTIME_DIR}/docswalker/kernel.json` (fallback `${HOME}/.cache/docswalker/`).
- **Ядро на старте** — захватывает `kernel.lock`, пишет `kernel.json` с pid/port/started_at; на shutdown — удаляет `kernel.json`, освобождает lock.
- **Stale-detection** — pid alive (`StalePidDetector`) **И** `GET /health` отвечает.
- **Detached spawn** — кросс-платформенный helper: Windows `CreateNoWindow=true`, POSIX `setsid`/`nohup`.
- **Health-handshake** — после spawn клиент ждёт `/health` 200 OK с retry (~50мс × 60 = ~3 сек total).
- **Owner-only ACL** — POSIX `chmod 600` через `File.SetUnixFileMode`. Windows — наследование ACL от `%LOCALAPPDATA%` (per-user по дефолту).
- **Spawn-race** (decision #9): winner захватывает `kernel.lock`; loser polls `kernel.json` до 3 сек; если winner не поднял — следующая итерация.

CLI-команд **не добавляем** — только инфраструктура для step-04/step-05.

## Файлы

- `src/DocsWalker.Cli/Cli/Kernel/KernelDiscovery.cs` (новый) — пути per-OS, owner-only ACL helper.
- `src/DocsWalker.Cli/Cli/Kernel/KernelInfoFile.cs` (новый) — DTO `KernelInfo(pid, port, version, started_at, auth_token)` + read/write через source-gen JSON; добавить `[JsonSerializable(typeof(KernelInfo))]` в `KernelJsonContext`.
- `src/DocsWalker.Cli/Cli/Kernel/KernelLock.cs` (новый) — `IDisposable`-обёртка над FileStream + FileShare.None на `kernel.lock`. `TryAcquire(timeout)` для blocking, `TryAcquireOnce()` для non-blocking (winner-of-race).
- `src/DocsWalker.Cli/Cli/Kernel/KernelSpawner.cs` (новый) — detached spawn: Windows ProcessStartInfo + CreateNoWindow=true; POSIX `Process.Start("/bin/sh", "-c", "setsid ... </dev/null >/dev/null 2>&1 &")` (fallback nohup).
- `src/DocsWalker.Cli/Cli/Kernel/KernelClient.cs` (новый) — high-level `EnsureRunningAsync`: read kernel.json → alive? → return; если нет — try lock → spawn → wait /health → return; loser → poll kernel.json до 3 сек.
- `src/DocsWalker.Cli/Cli/Handlers/KernelHandler.cs` — расширить: на startup acquire `kernel.lock` (если занят живым ядром — exit `kernel_already_running`), записать `kernel.json`; на shutdown — удалить `kernel.json`. Lock освобождается через Dispose.
- `src/DocsWalker.Cli/Cli/Kernel/KernelJsonContext.cs` — добавить `KernelInfo` в source-gen.

## Действия

1. **`KernelDiscovery`** — статический класс. `GetKernelDir()`: Windows `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "DocsWalker")`; POSIX через `Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")`/`HOME/.cache`. `EnsureKernelDirExists()` — создаёт каталог если нет. `SetOwnerOnly(path)` — POSIX `File.SetUnixFileMode(0600 для файлов, 0700 для каталогов)`; Windows — no-op (наследование).
2. **`KernelInfo` + sourcegen** — record с props `pid:int`, `port:int`, `version:string`, `started_at:DateTimeOffset`, `auth_token:string?`. JsonPropertyName snake_case. Добавить в KernelJsonContext.
3. **`KernelLock`** — `Acquire(path)` создаёт FileStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None). `TryAcquireOnce(path)` — non-blocking, возвращает `KernelLock?` (null если занят). `TryAcquire(path, timeout)` — с retry'ями (50мс с capped exponential backoff). Dispose закрывает stream → ОС освобождает lock.
4. **`KernelSpawner.SpawnDetachedAsync(exePath, args)`** — возвращает `Process` (или просто success/failure). Windows: ProcessStartInfo CreateNoWindow=true, UseShellExecute=false, RedirectStandardInput/Output/Error=true (закрываем сразу после Start, чтобы освободить pipe handles в child). POSIX: `Process.Start("/bin/sh", new[]{"-c", $"setsid {exe} {args} </dev/null >/dev/null 2>&1 &"})` — child detached от group/session; родитель shell сразу exit.
5. **`KernelClient.EnsureRunningAsync(httpClient, ct)`** — итеративный алгоритм:
   - read kernel.json: если есть и pid жив (StalePidDetector) и GET /health ok → return endpoint.
   - try `KernelLock.TryAcquireOnce(kernel.lock)`:
     - захватил → spawn (KernelSpawner), wait /health с retry до 3 сек (50мс × 60), считать kernel.json (мог обновиться), release lock, return endpoint.
     - не захватил → poll kernel.json до 3 сек с интервалом 50мс; если появился alive — return endpoint; иначе следующая итерация цикла.
   - после 3 итераций без успеха — exception `kernel_spawn_failed` с подробностями.
6. **`KernelHandler` — startup-расширение:**
   - Acquire `kernel.lock` через `KernelLock.TryAcquireOnce`. Если занят — прочитать kernel.json, проверить, жив ли указанный pid: жив → exit `kernel_already_running pid=X port=Y`; мёртв → подождать ~200мс и повторить захват lock; всё ещё занят → fatal.
   - После Kestrel `app.StartAsync()` (когда port известен через `app.Urls`):
     - извлечь port из URL'а;
     - сериализовать `KernelInfo` в kernel.json через atomic-write (tmp + rename), set owner-only ACL;
     - продолжить (banner на stderr остаётся).
   - На shutdown (после `WaitForShutdownAsync`):
     - удалить kernel.json best-effort;
     - lock освободится через Dispose.
7. **Stale-detection** — переиспользовать существующий `DocsWalker.Core.Server.StalePidDetector.IsAlive(pid, exePath?)`. exePath берём из `Environment.ProcessPath` ядра (записываем в `KernelInfo` опционально или сравниваем по basename).

## Риски

- **POSIX detached spawn** — `setsid` не везде есть (минимальные docker images могут не иметь). Fallback: `nohup`. Если оба нет — fail с `spawn_helper_missing`. Документируем требование.
- **Race на kernel.lock между acquire и kernel.json чтение** — клиент мог успеть прочитать kernel.json от только-что умершего ядра. Mitigation: после acquire ещё раз прочитать kernel.json (теперь без блокировки писателем) и проверить /health.
- **`kernel.json` partial write на crash** — pre-emptive crash во время записи оставит файл с мусором. Mitigation: atomic write через tmp + rename (как `AtomicWriter` в Core).
- **Windows process detachment** — `CreateNoWindow=true` достаточно для отвязки от console group, но если родитель CLI был запущен из job object (cmd /c), child может тоже попасть в job. Низкая вероятность в наших юзкейсах; docs упомянуть.
- **Ports & race** — между `app.Urls`-чтением (после StartAsync) и записью kernel.json есть микросекунды, в которые «другой клиент» может неправильно полить port. Mitigation: client ждёт kernel.json через poll → видит запись только когда уже атомарно записана.
- **AOT и `File.SetUnixFileMode`** — API доступен в .NET 7+; в AOT работает (P/Invoke в libc). OK.

## Пост-проверки

1. `dotnet publish` — успех.
2. `kernel` — startup создаёт `%LOCALAPPDATA%\DocsWalker\kernel.json` с корректным pid/port/started_at.
3. Повторный запуск `kernel` параллельно с уже живым → ошибка `kernel_already_running pid=X port=Y`.
4. После `Ctrl-C` или `shutdown` — `kernel.json` исчезает, `kernel.lock` освобождается.
5. Killed kernel (SIGKILL/`taskkill /F`) — `kernel.json` остаётся stale; `KernelClient.EnsureRunningAsync` переиспользует pid-check + /health и спавнит новое ядро (если ему дать вызваться через test-utility — пока без CLI-команды, через unit-тест или smoke в step-09).
6. Спавн на голую машину (нет файла) — `EnsureRunningAsync` поднимает kernel, kernel.json появляется, /health отвечает.
