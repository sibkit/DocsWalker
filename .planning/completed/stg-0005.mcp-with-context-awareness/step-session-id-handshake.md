# stg-0005 — session-id-handshake

## Цель
Расширить IPC-протокол текущего CLI клиента-сервера: добавить `session_id` в каждый запрос, научить клиента брать его из env / `--session-id`, генерировать UUID на старт TTY-REPL.

## Файлы
`src/DocsWalker.Core/Ipc/RequestFrame.cs` — добавление поля `session_id?: string`.
`src/DocsWalker.Core/Ipc/Handshake.cs` — bump `protocol_version`.
`src/DocsWalker.Cli/IpcClient.cs` — чтение `CLAUDE_CODE_SESSION_ID` из env, override через `--session-id`, проброс в каждый запрос.
`src/DocsWalker.Cli/Repl.cs` (или эквивалент TTY-REPL) — генерация UUID при старте, использование для всех команд REPL.
`src/DocsWalker.Core/Server/Dispatcher.cs` — приём `session_id` в каждом запросе, проброс в обработчики команд через `RequestContext`.

## Действия
1. Добавить `session_id` в request-frame (опциональное поле). Если null — сервер просто не ведёт seen для этого запроса.
2. CLI-клиент при старте читает `CLAUDE_CODE_SESSION_ID` (env). Если задан `--session-id=<uuid>` — перебивает env. Оба пустые — null в frame.
3. Регистрация общего параметра CLI `--session-id=<uuid>` (как `--root`, `--dry-run`).
4. REPL: при старте генерирует `Guid.NewGuid().ToString()`, использует для всех команд этой REPL-сессии до `:exit`. На `:exit` — session-файл остаётся, GC заберёт по TTL.
5. Сервер передаёт `session_id` в `Dispatcher.Run` через `RequestContext` (или эквивалент). Хранение/чтение seen-state — следующие шаги.
6. Bump `protocol_version` в `Handshake.cs`. Несовместимость со старым клиентом — feature (`(#312)`).

## Риски
- Bump protocol_version ломает совместимость с уже запущенным сервером прошлой версии. По правилу проекта — клиент и сервер деплоятся одним артефактом, рассинхрон явная ошибка пользователя.
