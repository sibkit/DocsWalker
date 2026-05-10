# stg-0010 — step-04 — kill-auto-spawn

## Цель

Удалить из репозитория весь код, ставший unreferenced после step-03:
auto-spawn pipeline (CLI запускал ядро сам), per-user kernel.json
discovery (`%LOCALAPPDATA%\DocsWalker\` / `$XDG_RUNTIME_DIR/docswalker/`),
spawn-race file-lock и stale-pid детектор. После step-04 в коде нет
ни одного места, где клиент пытался бы запустить kernel или искать
его через side-channel.

Шаг — **чистая зачистка**. API-контракты, тесты по фичам, поведение
RPC — не меняются. `dotnet build` и `dotnet test` должны быть зелёными
после шага.

## Что удаляется

### CLI side — целиком (dead-code файлы)

- `src/DocsWalker.Cli/Cli/Kernel/KernelClient.cs` — entry point
  auto-spawn (`EnsureRunningAsync`, `KernelEndpoint`, `KernelStartException`).
- `src/DocsWalker.Cli/Cli/Kernel/KernelSpawner.cs` — детач child-процесса
  `DocsWalker.Kernel.exe` (Windows + POSIX). Содержит
  `ResolveKernelExePath`, `KernelSpawnException`.
- `src/DocsWalker.Cli/Cli/Kernel/KernelLock.cs` — per-user file-lock на
  `kernel.lock` (winner-of-spawn-race).
- `src/DocsWalker.Cli/Cli/Kernel/KernelDiscovery.cs` — пути per-user
  discovery (`%LOCALAPPDATA%\DocsWalker\`, `$XDG_RUNTIME_DIR/docswalker/`).
- `src/DocsWalker.Cli/Cli/Kernel/KernelInfoFile.cs` — read/write/delete
  `kernel.json` discovery-файла.

### Core side — целиком

- `src/DocsWalker.Core/Server/StalePidDetector.cs` — единственный потребитель
  был `KernelClient.TryGetLiveEndpointAsync`. После удаления auto-spawn
  становится dead. Папка `src/DocsWalker.Core/Server/` после этого пуста.

### KernelJsonContext.cs (CLI) — частичная зачистка

`src/DocsWalker.Cli/Cli/Kernel/KernelJsonContext.cs`:
- Удалить `record KernelInfo(...)` — описывала формат `kernel.json`,
  теперь не используется.
- Удалить `[JsonSerializable(typeof(KernelInfo))]`.
- Удалить doc-комментарии про discovery-файл, kernel.json,
  `bind=127.0.0.1` / remote-bind.

Оставить: `HealthResponse`, `GraphInfo`, `GraphsResponse`,
`[JsonSerializable]` на них, заголовок класса `KernelJsonContext`.

### Kernel/Program.cs — мелкая зачистка

`src/DocsWalker.Kernel/Program.cs`:
- Убрать `using DocsWalker.Core.Server;` (после удаления `StalePidDetector`
  namespace опустеет; в файле никто из Server-namespace и так не
  используется).
- `kernel_already_running` проверка уже удалена в step-03 (substep 2).
  В step-04 — финальный аудит, что строка не встречается ни в src/, ни
  в tests/.

## Что НЕ удаляется

- `src/DocsWalker.Cli/Cli/Kernel/ClientConfig.cs` — текущий канал
  endpoint resolution.
- `src/DocsWalker.Cli/Cli/Kernel/KernelHttpClient.cs` — путь client→kernel
  через `/db/{graph}/rpc`.
- `src/DocsWalker.Cli/Cli/Kernel/KernelJsonContext.cs` сам файл —
  нужен под `HealthResponse` / `GraphInfo` / `GraphsResponse`.
- Никаких тестов на удаляемые типы нет (Grep по `tests/` — 0 matches);
  step-04 не трогает тестовую базу по своей основной задаче.

## Действия (упорядоченные)

1. `git rm` пятёрки в `src/DocsWalker.Cli/Cli/Kernel/`:
   `KernelClient.cs`, `KernelSpawner.cs`, `KernelLock.cs`,
   `KernelDiscovery.cs`, `KernelInfoFile.cs`.
2. `git rm src/DocsWalker.Core/Server/StalePidDetector.cs`. Папка
   `src/DocsWalker.Core/Server/` остаётся пустой локально — git её
   не отслеживает, но на FS подчистится отдельно (no-op для commit'а).
3. `Edit` `KernelJsonContext.cs` (CLI) — убрать `KernelInfo` record +
   JsonSerializable + связанные doc-комментарии.
4. `Edit` `kernel/Program.cs` — убрать `using DocsWalker.Core.Server;`.
5. `mcp__glider__sync` — Roslyn видит удалённые/изменённые файлы.
6. `dotnet build DocsWalker.slnx` — зелёный.
7. `dotnet test DocsWalker.slnx` — зелёный (178/178).
8. Atomic git: `add` / `commit` / `push` (3 отдельных Bash-вызова, как
   в CLAUDE.md).
9. `[*] → [+]` в `strategy.md` для step-04. Обновить `CLAUDE.md`
   «Активная сессия» под step-05.

## Риски

- **Старый orphan `kernel.json` в `%LOCALAPPDATA%\DocsWalker\`.** После
  step-03 ни kernel-exe не пишет туда, ни клиент не читает. Файл
  остаётся на пользовательском диске как orphan. Не удаляем
  программно — это user-data, и удаление при build/install было бы
  side-effect'ом не по адресу. Пользователь сам почистит, либо это
  делается во время smoke (step-06) при подготовке окружения.
- **Сборка после удаления KernelInfo.** `KernelJsonContext` —
  source-gen serializer; если `[JsonSerializable(typeof(KernelInfo))]`
  оставить, а `record KernelInfo` удалить — компиляция падает на
  unresolved type. Удаляем атрибут и record одной правкой; build/test
  верифицируют.
- **`using DocsWalker.Core.Server;` в kernel/Program.cs.** Текущий
  `Program.cs` уже не использует ничего из этого namespace; using —
  unused. После удаления StalePidDetector namespace в проекте
  `DocsWalker.Core` пустеет; компилятор-warning CS8019 (unused using)
  не блокирует сборку, но смотрится грязно. Убираем.
- **`#pragma warning disable` локальных мест.** В step-03 рисках
  допускалось оставить unused-warning'и до step-04. Сейчас всё
  удаляется одним пакетом — проверять отдельно не нужно.
- **Нет тестов на удаляемые типы.** Подтверждено Grep'ом по `tests/`
  (0 matches на `KernelClient|KernelSpawner|KernelLock|KernelDiscovery|KernelInfoFile|StalePidDetector`).
  Если кто-то внёс тест за время сессии — `dotnet test` поймает
  failure на компиляции. Не блокирующий риск.

## Сверка со страт

- Блок 1.2 (auto-spawn убирается полностью): `KernelClient`,
  `KernelSpawner`, `KernelLock` удалены. После step-04 в репо нет ни
  одной строки, где CLI/MCP/REPL запускали бы kernel сами.
- Блок 1.3 (per-user discovery убирается): `KernelDiscovery`,
  `KernelInfoFile`, запись `KernelInfo` удалены. Никаких ссылок на
  `%LOCALAPPDATA%\DocsWalker\` / `$XDG_RUNTIME_DIR/docswalker/` в коде
  больше нет.
- Блок 1.6 (`kernel_already_running` убирается): уже сделано в step-03;
  в step-04 — финальная проверка отсутствия строки.
