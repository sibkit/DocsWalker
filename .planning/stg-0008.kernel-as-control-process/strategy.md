# stg-0008 — Kernel as Control Process

**Статус:** в работе

## Задача

Перевести DocsWalker на трёхслойную модель процессов:

1. **Ядро (`docswalker kernel`)** — отдельный долгоживущий процесс, **один на пользователя**. Слушает HTTP + JSON-RPC 2.0 на `127.0.0.1:<dynamic-port>`. Хранит N графов (один на каждый активный root), реализует все команды и MCP-протокол нативно. Запросы идут с **явным `root` параметром** — ядро делает routing.
2. **MCP-server (`docswalker mcp-server`)** — тонкий stdio↔HTTP-bridge. Stdio-процесс, который Claude поднимает через `.mcp.json` с `--root=<path>`. Форвардит JSON-RPC frames между stdin/stdout и `/rpc` ядра, **подставляя свой `root`** во все `tools/call`. Без бизнес-логики.
3. **CLI / REPL / будущий Web-backend** — HTTP-клиенты к ядру. Каждый запрос несёт явный `root`.

Ядро — **single writer**. Никаких внешних правок YAML «руками» вне ядра. Это упраздняет: file-lock на данные, checksum-инвалидацию sessions (#359), file-watcher, race на write-API. Конструкция гарантирует консистентность: что ядро записало — то и прочитает после re-load из RAM.

Уничтожает класс ошибки `server_not_running` (клиенты сами поднимают ядро). Снимает lock-конфликт `run` ↔ `mcp-server` (#367) — нет двух конкурирующих за lock серверов.

## Принятые решения

### 1. Транспорт ядра — HTTP + JSON-RPC 2.0

`POST /rpc` принимает JSON-RPC 2.0 envelope (методы `initialize`, `tools/list`, `tools/call`). `GET /health` для liveness-check. `GET /roots` для diagnostics (список загруженных roots, last-used). Один транспорт.

Кандидаты gRPC/WebSocket/TCP/named-pipe рассмотрены и отвергнуты:
- gRPC — heavy code-gen, awkward для будущего Web-backend (gRPC-Web bridging).
- WebSocket — frame-management overhead там, где request/response достаточно.
- TCP — пришлось бы изобретать свой framing/auth/logging; нет curl-debug.
- Named pipe / UDS — local-only, исключает Web-backend как класс.

HTTP даёт: тот же протокол что MCP (wrapper тривиален), curl/Postman диагностику, готовый middleware из Kestrel (auth/logging/rate-limit), интеграцию с любым backend в одну строку (`HttpClient`).

### 2. Один kernel на пользователя, multi-root внутри

Одно ядро на пользовательскую сессию обслуживает N разных docs-репозиториев. Routing — через явный `root` в каждом `tools/call`. Ядро держит словарь `root → загруженный граф`. Lazy load: первый запрос на новый root → ядро парсит YAML, кеширует в RAM.

Cross-user: разные пользователи на одной машине = разные ядра (см. discovery #4 — per-user локация).

### 3. Bind по умолчанию `127.0.0.1`

Local-only. Флаг `--bind=<addr>` для remote — с auth-token, который пишется в `kernel.json` с owner-only ACL.

### 4. Discovery — per-user, system-wide

- Windows: `%LOCALAPPDATA%\DocsWalker\kernel.json` + `kernel.lock`
- POSIX: `${XDG_RUNTIME_DIR}/docswalker/kernel.json` + `kernel.lock` (fallback `~/.cache/docswalker/`)

`kernel.json`:
```json
{
  "pid": 12345,
  "port": 51234,
  "version": "0.5.0",
  "started_at": "2026-05-10T...",
  "auth_token": null
}
```

Owner-only ACL/mode (0600 на POSIX, equivalent на Windows). Stale-detection: pid alive (`StalePidDetector`) **И** `GET /health` отвечает. Любой клиент читает файл, проверяет, подключается. **Ничего на per-root уровне в `<root>/.docswalker/`** — discovery полностью per-user.

### 5. Никакого file-lock на данные

Ядро — sole writer. Внешние правки YAML руками не предусмотрены: пользователь работает через CLI (или будущий Web UI), LLM работает через MCP. Это упраздняет:

- `docs/.docswalker/run.lock` (для данных).
- Большую часть `ServerLifecycle`.
- `#359` checksum-based session invalidation целиком.
- File-watcher (никогда не был, и не нужен).

Если когда-нибудь потребуется внешняя правка — сначала graceful kernel stop, затем ручной edit, затем restart. Это явный contract нарушение, не молчаливое допущение.

### 6. Per-root idle eviction = 10 минут

Если граф какого-то root'а не запрашивался > 10 минут — выгружаем из RAM. Cold re-load на следующем обращении (~50–200мс на типичных размерах). Конфигурируется через `--root-idle-timeout=<duration>`. Sole-writer гарантирует, что данные на диске = данные что мы записали сами; повторный load даёт идентичный граф.

### 7. Kernel always-running

**Без kernel-level idle-timeout.** Когда все roots выгружены, ядро = голый Kestrel (~30МБ). Не убиваем — нет смысла.

После reboot первый CLI/MCP-вызов спавнит новое ядро (одна холодная цена за boot). Опциональный `docswalker daemon install` (login-task на Windows / systemd --user на Linux) для auto-start при логине — отдельная фича на будущее, не в этой пачке.

Override для тестов/CI: `--idle-timeout=<duration>` или `--idle-timeout=0` (die-after-each-request).

### 8. Detached spawn клиентом

Если CLI/MCP-wrapper обнаружил, что ядра нет — спавнит:

- Windows: `Process.Start` с `UseShellExecute=false` + `CreateNoWindow=true` + `DETACHED_PROCESS` (через `ProcessStartInfo` + Win32 P/Invoke если нужно).
- POSIX: двойной fork через `Process.Start` обёрнутый в shell `nohup ... &` или явный double-fork через P/Invoke.

Спавнящий клиент дождался `GET /health` (~200мс с retry'ами), использовал `/rpc`, вышел. Ядро живёт автономно.

**Auto-spawn — не silent:** клиент пишет на stderr строку вида `kernel: spawned pid=X port=Y` (для CLI-режима) или эквивалентный лог-frame (для MCP-wrapper). Никаких фоновых процессов «откуда-то».

### 9. Spawn race — global file-lock

`kernel.lock` (per-user, не per-root). Логика клиента:

1. Прочитать `kernel.json`. Жив (pid + `/health`)? → подключиться, выход.
2. Попытаться захватить `kernel.lock` (non-blocking).
3. Захватил — winner: spawn ядра, дождался `/health` (ядро само пишет `kernel.json` на старте), отпустил lock, использует `/rpc`.
4. Не захватил — loser: poll `kernel.json` до 3 секунд с интервалом ~50мс. Если winner не поднял за 3 сек — следующая итерация (advisory lock освободился при exit процесса winner'а), loser сам пробует.

### 10. Никакого silent in-process fallback

Не подняли ядро — **ошибка с понятным сообщением**: `kernel_spawn_failed: <stderr ядра>`. Никаких «попробуем загрузить YAML in-process и сделать вид, что всё хорошо».

### 11. `run` → `repl`, HTTP-клиент

Команды `run` и старый `mcp-server` больше не «серверные». `repl` (бывший `run`) — интерактивный HTTP-клиент к ядру: readline → парс argv → `tools/call` с явным `root` (из `--root=` REPL'а) → печать ответа. **#367 уничтожен как класс** (нет двух процессов, конкурирующих за lock).

### 12. Sessions — per-root, на диске

`<root>/docs/.docswalker/sessions/<uuid>.yml` остаются как сейчас. Ядро load'ит sessions при первом обращении к root, держит в RAM пока root жив, persist при изменениях. После per-root eviction sessions пересоздаются с диска при re-load. Sole-writer гарантирует консистентность — ничего не нужно проверять, кроме самого факта существования файла.

### 13. Сериализация — per-root semaphore

Внутри ядра — `SemaphoreSlim` per-root. Параллельные запросы на разные roots обрабатываются параллельно; на один root — строго по одному. Сохраняет инвариант «no concurrent mutations on same graph», но снимает global serialization (старый #313 был global).

### 14. Storage остаётся YAML на этой стадии

YAML загрузчики/сериализаторы не трогаем. Переход на JSON — отдельная стадия `stg-0009.storage-format-json` после стабилизации kernel'а. Sole-writer + отсутствие #359 делают переход к JSON безопасным, но ортогональным текущей задаче.

### 15. Kestrel — embedded, без отдельного web-приложения

`WebApplication.CreateSlimBuilder` + `MapPost("/rpc", ...)` + `MapGet("/health", ...)` + `MapGet("/roots", ...)`. Минимальный набор middleware. Никаких view-engines, статики, Razor — чистый JSON endpoint. Зависимость `Microsoft.AspNetCore.App` (framework reference, не NuGet) — добавляется в `DocsWalker.Cli.csproj`.

## Шаги

- [+] (01) docs-rewrite
- [+] (02) kernel-host
- [*] (03) discovery-and-spawn
- [*] (04) cli-to-http-client
- [*] (05) mcp-wrapper
- [*] (06) repl-command
- [*] (07) cleanup-old-ipc
- [*] (08) docs-tooling-fixes
- [*] (09) smoke

## Итоговый порядок выполнения

1. **docs-rewrite** — переписать узлы `docs/DocsWalker.yml` под новую модель через DocsWalker `transaction`. Существенно затрагивает: #305 (модель процесса), #307 (CLI client-mode), #308 (выбрасываем lock на данные), #309 (multi-root внутри одного ядра, не отложен), #310 (HTTP вместо named pipe), #311 (auto-spawn разрешён, но не silent), #313 (per-root semaphore), #314 (StalePidDetector переезжает на kernel.json), #316, #317–#321, #322–#324, #359 (выбрасываем checksum invalidation), #367 (уничтожен как класс), #372 (MCP-wrapper). Добавить новые узлы: «Ядро (kernel)», «MCP-wrapper», «HTTP transport», «kernel.json discovery (per-user)», «spawn race (global lock)», «root routing», «per-root idle eviction», «kernel always-running». Шаг не трогает код.

2. **kernel-host** — реализация ядра: команда `docswalker kernel` (без `--root`, multi-root). ASP.NET Core minimal API (Kestrel, `WebApplication.CreateSlimBuilder`). Endpoints `POST /rpc` (JSON-RPC 2.0 с явным `root` в каждом `tools/call` → `Dispatcher.Run`), `GET /health`, `GET /roots`. `RootRegistry` (lazy load графов, словарь `root → graph + last-used`). Per-root `SemaphoreSlim`. Per-root idle-timer (10 мин default, configurable). Без kernel-level idle. Graceful shutdown через `IHostApplicationLifetime`.

3. **discovery-and-spawn** — per-user `kernel.json` (Windows `%LOCALAPPDATA%\DocsWalker\`, POSIX `$XDG_RUNTIME_DIR/docswalker/`) — запись ядром на старте, чтение клиентами. Global `kernel.lock` (winner-of-spawn-race, per-user не per-root). `StalePidDetector` (переиспользовать). Helper-функции для detached spawn (Windows DETACHED_PROCESS / POSIX double-fork). Health-handshake с retry. Без CLI-команд — только инфраструктура.

4. **cli-to-http-client** — переписать non-server CLI команды на `HttpClient` к `/rpc`. Logic: разрешить root → прочитать `kernel.json` → spawn если нет/stale → `tools/call` с явным `root` → распаковать ответ → exit. Старый `IpcClient` остаётся параллельно для `mcp-server` (удалится в шаге 7).

5. **mcp-wrapper** — переписать `mcp-server` команду на тонкий stdio↔HTTP bridge (~80–150 строк, без бизнес-логики). Читает JSON-RPC frame из stdin, **подставляет фиксированный `root` (из своего `--root=`)** во все `tools/call`, форвардит в `/rpc` ядра, пишет ответ в stdout. Spawn ядра при отсутствии. Старый `McpServer` класс пока живёт параллельно (удалится в шаге 7).

6. **repl-command** — переименовать `run` в `repl`. HTTP-клиент к ядру: readline → парс argv → `tools/call` с фиксированным `root` (из `--root=` REPL'а) → печать ответа. Spawn ядра при отсутствии. TTY/headless dual-mode из старого `RunHandler` выкидывается полностью.

7. **cleanup-old-ipc** — удалить старый IPC-стек: `IpcServer`, `IpcChannel`, `NamedPipeChannel`, `UnixSocketChannel`, старый `IpcClient`, большую часть `ServerLifecycle`, `RunHandler`, старый `McpServerHandler`. Удалить `#359` (Hash-detection / SHA256 checksum) код целиком. Переписать `mental_model` в `get-usage-guide` (`UsageGuideHandler`) — выкинуть упоминания `run --root=`, named pipe, `server_not_running`, IPC-сервера; добавить новую модель: kernel/auto-spawn/multi-root/per-root-eviction/JSON-RPC. Обновить описания команд `kernel`, `repl`, `mcp-server` в выдаче `get-usage-guide`. Обновить тесты — выкинуть тесты на named pipe/socket framing и checksum-invalidation, добавить тесты на multi-root `/rpc` roundtrip.

8. **docs-tooling-fixes** — закрыть API-неудобства DocsWalker, накопленные за step-01 (фиксировались как dogfooding-обратная связь):
   - **transaction format в `get-usage-guide`** — для команды `transaction` сейчас стоит «см. формат в TransactionParser». LLM ловит ошибку `missing_field 'from_ids'` на ровном месте, потому что транзакционные операции используют snake_case+массивы (`from_ids:[42], to_id:8, unlink:true`), а одноимённые CLI-команды — kebab-case+скаляры (`--from=42 --to=8 --unlink`). Описать каждую операцию в выдаче get-usage-guide (или отдельной выдаче `describe-transaction`): имена полей, типы, required/optional, маппинг от CLI-флага к JSON-ключу.
   - **`--no-seen=true` в `get-subtree`** — флаг сейчас есть только у `get-nodes`. Когда нужно вытянуть полный текст детей раздела через `get-subtree` после ранее сделанного `get-nodes` той же сессии — children приходят как `{id, seen:true}`-плейсхолдеры; приходится переспрашивать `get-nodes` пакетом. Добавить флаг (поведение симметрично `get-nodes`: отключает фильтрацию seen-set, seen всё равно обновляется).
   - **`--command=<name>` в `get-usage-guide`** — сейчас выдаётся весь манифест на 26 команд. Параметр `--command=<name>` отдаёт описание одной команды (с примерами). Альтернатива — переиспользовать `describe-type`-стиль, но команды и типы — разные сущности.
   - **Расхождение `get-in-refs` vs `redirect-refs`** — `get-in-refs --id=368` возвращает `{rules:[44]}`, но `redirect-refs --from=368` падает с `no_effect`. Похоже, `get-in-refs` включает computed-связи (collected по path-children с типом X), а `redirect-refs` работает только с физическими cross-refs. Расследовать: либо привести в синхрон (одна выдача, единый набор), либо подсветить computed-flag в выдаче in-refs (например, отдельные секции `physical:` и `computed:`), чтобы LLM знала, на каких связях нужно/можно делать redirect.

9. **smoke** — `dotnet publish` бинаря, прогнать на собственном `docs/`:
   - `docswalker kernel --root-idle-timeout=10s` — ядро поднимается, отвечает на `/health`, `/roots` пуст.
   - `docswalker get-nodes --root=. #1` — CLI поднимает/находит ядро, root load'ится, ответ корректный.
   - `docswalker get-nodes --root=<другой_root>` — параллельно второй root в том же ядре, оба видны в `/roots`.
   - `docswalker mcp-server --root=.` — MCP-wrapper форвардит `tools/list` и `tools/call` с подставлением root.
   - `docswalker repl --root=.` — REPL отвечает на команды.
   - Per-root eviction — root исчезает из `/roots` через 10 сек, следующий запрос успешно re-load'ит.
   - Параллельный spawn двумя CLI — winner+loser оба получают ответ через одно ядро.

Каждый шаг — отдельная пачка. Шаги связные (каждый стоит на предыдущем), параллелизация невозможна. После `[+]` каждой пачки — автокоммит/автопуш по правилу проектного `CLAUDE.md`.

## Точка возобновления (для новой сессии после сброса контекста)

Стратегия — переход DocsWalker на трёхслойную процессную модель: одно долгоживущее ядро **на пользователя** (HTTP + JSON-RPC 2.0 на `127.0.0.1:<dynamic-port>`) хранит N графов (multi-root через явный `root` параметр в каждом запросе); MCP-server — тонкий stdio↔HTTP wrapper для Claude (подставляет фиксированный root из `--root=`); CLI/REPL/будущий Web-backend — HTTP-клиенты. Ядро — single writer (никаких внешних правок YAML руками); это упраздняет file-lock на данные, #359 checksum-инвалидацию, file-watcher. Все принципиальные решения зафиксированы выше — пользователь подтвердил, новая сессия не пересогласовывает.

Старт новой сессии:

1. Прочитать проектный `CLAUDE.md` — общие правила (docs/ как первоисточник, atomic git-вызовы, Glider для C#-навигации, автокоммит/автопуш в конце пачки шагов).
2. Прочитать этот файл (`strategy.md`) полностью — особенно «Принятые решения» и «Итоговый порядок выполнения».
3. Прочитать первый `[*]`/`[.]`-step и работать строго по нему.
4. Дальше — по итоговому порядку, шаг за шагом, без пропусков.

Что **не** надо пересматривать:

- Транспорт ядра = HTTP + JSON-RPC 2.0. gRPC/WebSocket/TCP/named-pipe рассмотрены и отвергнуты. Окончательно.
- Один kernel на пользователя, multi-root внутри. Routing через явный `root` параметр в каждом запросе. Окончательно.
- Bind = `127.0.0.1` по умолчанию. Remote — opt-in с auth-token. Окончательно.
- Discovery в per-user локации (`%LOCALAPPDATA%`/`$XDG_RUNTIME_DIR`). Per-root discovery в `<root>/.docswalker/` НЕ используется. Окончательно.
- Никакого file-lock на данные. Ядро — sole writer. Внешний edit YAML = kernel stop → edit → restart, явный contract нарушения. Окончательно.
- `#359` checksum-invalidation — выбрасывается. Окончательно.
- Per-root eviction = 10 мин default, конфигурируется. Окончательно.
- Kernel-level idle-timeout = ОТСУТСТВУЕТ. Ядро всегда живёт пока процесс не убит / reboot. После reboot — auto-spawn первым клиентом. Окончательно.
- Detached spawn клиентом, **не silent**. Окончательно.
- Никакого silent in-process fallback. Окончательно.
- `run` → `repl`, HTTP-клиент. Окончательно. **#367 уничтожен.**
- Sessions per-root на диске, sole-writer гарантирует консистентность. Окончательно.
- Сериализация per-root через `SemaphoreSlim`. Окончательно.
- Storage в этой стадии остаётся YAML. JSON — отдельная стадия `stg-0009.storage-format-json`. Окончательно.

Реализация пачки шагов: первым tool-call в `strategy.md` всем шагам пачки `[*] → [.] (NN)` с присвоением последовательных NN и переименованием step-файлов; затем правки кода; в конце пачки — `[.] → [+]` плюс автокоммит/автопуш.

Размер пачек: каждый шаг укладывается в ~300K токенов сам по себе. Пачковать шаги между собой нельзя — каждый стоит на предыдущем; идём по одному.
