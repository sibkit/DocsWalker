# step-04 — smoke

**Статус:** [+]

## Цель

E2E-прогон на опубликованных бинарях — проверить, что после удаления seen
ничего не сломалось в реальном пайплайне (CLI ↔ kernel ↔ HTTP-RPC ↔ MCP-wrapper
↔ REPL). Bug-кейсы: повтор узла должен быть полным, `--no-seen`/`--session-id`
должны давать `unknown_parameter`, `get-usage-guide` не должен содержать
seen/session_id.

## План проверок (из strategy.md)

1. `dotnet publish` обоих exe (Cli + Kernel) в `publish/` рядом с проектами.
2. Поднять kernel из publish-сборки.
3. CLI checks через published `docswalker.exe`:
   - `get-nodes --root=. --ids=1` — полный узел.
   - `get-subtree --root=. --id=1 --fields=title --depth=3` — компактный обзор.
   - Повтор `get-nodes --ids=1` — снова полный узел.
   - `get-nodes --ids=1 --no-seen=true` → `unknown_parameter`.
   - `get-nodes --ids=1 --session-id=test-uuid` → `unknown_parameter`.
   - `get-usage-guide` — текст без `seen`/`session_id`, с `fields=[title]`.
4. MCP-wrapper: запустить `docswalker mcp-server --root=.`, отправить
   `initialize` → `tools/list` → `tools/call(get-nodes,ids=1)` через stdin,
   убедиться, что `tools/call.arguments` без `session_id`.
5. REPL: запустить `docswalker repl --root=.`, выполнить пару команд,
   убедиться, что баннер не упоминает session/UUID.

## Прогон

Бинари опубликованы AOT-self-contained:

- `publish/cli/DocsWalker.Cli.exe` (8.5 MB, native).
- `publish/kernel/DocsWalker.Kernel.exe` (16 MB, native).

Старое dev-ядро остановлено через `taskkill /PID 58292 /F` (с разрешения
пользователя; classifier блокировал автоматически). Новое поднято из
`publish/kernel/`.

### CLI checks (через `publish/cli/DocsWalker.Cli.exe --root=.`)

| # | Команда | Ожидание | Результат |
|---|---------|----------|-----------|
| 1 | `get-nodes --ids=1` | Полный узел (text + out_refs) | ✅ |
| 2 | `get-subtree --id=1 --fields=title --depth=3` | Компактный обзор только с title | ✅ |
| 3 | Повтор `get-nodes --ids=1` | Снова полный, никаких placeholder | ✅ |
| 4 | `get-nodes --ids=1 --no-seen=true` | `{"code":"unknown_parameter",...}` | ✅ |
| 5 | `get-nodes --ids=1 --session-id=test-uuid` | `{"code":"unknown_parameter",...}` | ✅ |
| 6 | `get-usage-guide` | Без `seen`/`session_id`/`--no-seen`/`placeholder`, с `fields=[title]` | ✅ (после фикса, см. ниже) |

### MCP-wrapper

```
{"jsonrpc":"2.0","method":"initialize",...}  → ok (serverInfo: DocsWalker 0.1)
{"jsonrpc":"2.0","method":"tools/call","params":{"name":"get-nodes","arguments":{"ids":"1"}}}
  → result.content[0].text = JSON со полным узлом id=1
```

Никаких `session_id` в `tools/call.arguments` (гарантировано удалением кода
в step-02).

### REPL

```
DocsWalker REPL: root=., kernel=http://127.0.0.1:61532
Команды — без префикса 'docswalker'. Выход: ':quit'/':exit'/Ctrl+D. Ctrl+C — отмена строки.
dw> DocsWalker REPL: bye
```

Баннер не упоминает session/UUID. Команды форвардятся.

## Расхождения со страт

step-04 был задуман как чистый smoke-прогон без code-правок. Фактически
обнаружились **3 утечки seen/session_id из step-02**, которые пришлось
дочистить здесь:

1. **`src/DocsWalker.Cli/Cli/Commands.cs:158`** — desc `mcp_server` команды
   содержал «подмешивает фиксированный root и session_id» — пережиток
   из stg-0008. Поправлено: убрано «и session_id».
2. **`src/DocsWalker.Cli/Cli/Commands.cs:142-144`** — комментарий выше
   `Read("repl", ...)` упоминал «общим session_id». Поправлено.
3. **`src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs:38`** — комментарий
   «(без seen-фильтрации)» — позитивное упоминание killed mechanism.
   Заменён на «для дешёвого обзора без полного text — см. get-subtree
   с fields=title.»

После правок CLI и Kernel опубликованы заново, ядро перезапущено из
`publish/kernel/`, проверка #6 повторена и стала зелёной.

`Grep` на `session_id|--no-seen|seen-set|placeholder|\bseen\b` по
финальному `get-usage-guide` payload: 0 совпадений.

В `WriteApi.cs` локальная переменная dedup в `redirect-refs` (защита из
дублей при маппинге `from_ids→to_id`) переименована `seen → dedupSet` —
чтобы слова `seen` в исходниках не было совсем.

## Точка возобновления

Страт `stg-0009.kill-seen` закрыта целиком: code-removal атомарный, тесты
зелёные (152/152), e2e-smoke зелёный. server-side dedup (`seen-set`,
`session_id`-протокол, persistence сессий, `--no-seen` флаг,
write-invalidation) удалён из кода и спецификации без следов.

Текущий kernel: `publish/kernel/DocsWalker.Kernel.exe` (запущен при step-04,
оставлен в работе как штатное per-user ядро). Старое dev-ядро (pid=58292)
остановлено в начале step-04.
