# stg-0005 — spec-in-docs

## Цель
Зафиксировать всю модель context-aware-loading в `docs/DocsWalker.yml` до начала кодирования. Без этого код и спека разойдутся, а правило проекта (docs/ — единственный источник правды) будет нарушено.

## Файлы
`docs/DocsWalker.yml` — добавление новых section и расширение существующих.

## Действия
1. Добавить section «Контекст-aware-выдача» с правилами:
   - auto-include для non-tree required cross-refs читается транзитивно при любой read-команде, без отдельного флага в Схеме;
   - концепция `session_id` — универсальный идентификатор LLM-сессии, по которому сервер ведёт seen-set;
   - источники session_id: env `CLAUDE_CODE_SESSION_ID` для CLI-клиента, MCP-protocol context для MCP, генерация UUID на старт TTY-REPL, `--session-id=<uuid>` override;
   - семантика placeholder: уже-выданный узел в текущей session возвращается как `{"id":N,"seen":true}` без других полей; прямо запрошенные через `--ids`/`--id` всегда полные;
   - первый `get-usage-guide` в session_id сбрасывает seen этой сессии (маркер /clear);
   - параметр `--no-seen=true` допустим только на `get-nodes` (single-id), на subtree-командах отвергается как `unknown_parameter`;
   - persistence seen-state в `docs/.docswalker/sessions/<uuid>.yml`, формат файла (`created`, `last_used`, `seen: [ids]`);
   - TTL=7d, GC при старте сервера;
   - hash-detection ручной правки YAML: сервер пишет SHA256 от docs/-файлов на graceful shutdown в `docs/.docswalker/sessions/.checksum`, сверяет на startup; mismatch → invalidate всех sessions.
2. Расширить раздел «Модель процесса»: request-frame и handshake расширяются полем `session_id`; описать поведение клиента-CLI (читает env, поддерживает override). Bump `protocol_version`.
3. Добавить раздел «MCP-интерфейс» (поверх существующего notes-only варианта в #44): MCP — параллельный транспорт поверх ядра, JSON-RPC 2.0 stdio, инициализация через MCP-init, регистрация всех команд API как MCP-tools, session_id из MCP-context, dedup и auto-include работают одинаково на CLI и MCP. Дополнительно зафиксировать:
   - инстанс MCP-сервера обслуживает ровно один root (как и `run`); single-root-per-process сохраняется;
   - multi-root в одной Claude Code сессии = несколько `mcpServers`-entries в `.mcp.json` с разными `--root` (Claude Code запускает их как независимые процессы; tools видны с разными префиксами);
   - session_id MCP-сессии берётся из MCP-`initialize`-handshake (Claude Code не пробрасывает `CLAUDE_CODE_SESSION_ID` в MCP-stdio-процессы); живёт от `initialize` до закрытия канала;
   - `/clear` в Claude Code не закрывает MCP-канал — маркер-сброс seen-set остаётся прежним: первый `get-usage-guide` в этой MCP-сессии.
4. Расширить write-команды описанием invalidation: после успешного write затронутые id удаляются из seen-set всех активных sessions.
5. Добавить examples ко всем новым правилам (по правилу проекта — у каждого rule минимум один example).
6. Прогон `docswalker check-integrity` после правок; убедиться, что sequence.txt покрывает все новые id.

## Риски
- Объём правки `DocsWalker.yml` большой (несколько новых section + атомов); ошибиться в id-нумерации легко. Перед коммитом — `check-integrity`.
