# stg-0016 - active-session-wrapper

Цель: упростить рабочий протокол LLM без раздувания инструкций: `brief(goal)` создает session, MCP-wrapper держит ее active для текущего процесса и подставляет в session-aware tools.

## Шаги

[+] (01) spec-update - уточнить docs-модель active session и короткий протокол.
[+] (02) brief-session - `brief` без `session_id` выдает и persist-ит kernel-issued session.
[+] (03) wrapper-active - MCP-wrapper хранит active session per process и inject-ит `session_id` в `query`/`tx`/session tools.
[+] (04) usage-tests - обновить usage guide и покрыть brief/session wrapper.
[+] (05) verification - full test, publish/restart, live-smoke.

## Handoff

- Не добавлять новый `start-task`: используем существующий `brief`.
- Не делать active session глобальной на graph: active state живет в MCP-wrapper process.
- Kernel остается source of truth для persisted session и hard guard в `tx`.
