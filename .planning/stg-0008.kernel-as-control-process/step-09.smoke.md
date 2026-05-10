# stg-0008 — step-09 — smoke

## Цель

Финальная проверка stg-0008 на собственном `docs/` репозитория. Подтверждаем,
что после step-01..08 трёхслойная модель (kernel + CLI HTTP-клиент +
mcp-server stdio-bridge + repl) работает целиком, на реальной публикации
обоих exe (`DocsWalker.Cli.exe`, `DocsWalker.Kernel.exe`), а не только
в `dotnet test`.

В рамках smoke в коде допустимы только мелкие фиксы под найденные регрессии
(до commit'а step-09); существенный объём — возврат на нужный шаг. Фактически
обнаружены и закрыты в рамках step-09:

1. **Diag-вывод в `DocsWalker.Kernel/Program.cs`** — две строки на старте
   ядра: `kernel_info_path=<полный путь>` и `kernel_info_written exists=<bool>`.
   Без них тяжело диагностировать «discovery-файл не пишется» сценарии (под
   AOT, под не-стандартные shell-окружения и т.п.); при включённом ядре
   стоимость — 2 строки на startup, незаметно.
2. **Non-TTY fallback в `DocsWalker.Cli/Cli/Repl/LineReader.cs`** — при
   `Console.IsInputRedirected` делегируем чтение в `Console.In.ReadLine()`
   (TTY-only API `TreatControlCAsInput`/`KeyAvailable`/`ReadKey` иначе бросают
   `IOException` на closed-handle). Это нужно для headless/CI/MCP-pipe
   сценариев REPL — в smoke воспроизвелось при подаче команд через pipe.

## Pre-conditions

1. Все юнит-тесты зелёные:
   `dotnet test tests/DocsWalker.Tests/DocsWalker.Tests.csproj -c Release` → 176/176.
2. На машине нет работающих ранее `DocsWalker.Kernel.exe` процессов и
   `%LOCALAPPDATA%\DocsWalker\kernel.json` отсутствует (либо stale). Очистка
   в шаге 0 ниже.

## Действия

Среда — Git-Bash (MSYS2) под Windows. `/tmp` уже = `C:\Users\sibkit\AppData\Local\Temp`,
а `/cygdrive/c/...` — НЕ маппинг (буквальная подпапка), поэтому используем `/c/`
и `/tmp/` для путей. Windows-style операции (taskkill, cmd, dir) — через `cmd //c '…'`.

### Шаг 0 — приготовиться

```bash
# 0.1. Прибить любые старые kernel-процессы.
taskkill //F //IM DocsWalker.Kernel.exe 2>&1 || true

# 0.2. Удалить stale kernel.json/kernel.lock dir.
cmd //c 'rmdir /s /q C:\Users\sibkit\AppData\Local\DocsWalker' 2>&1 || true
```

### Шаг 1 — publish обоих exe

```bash
dotnet publish src/DocsWalker.Cli/DocsWalker.Cli.csproj       -c Release -r win-x64 --self-contained true
dotnet publish src/DocsWalker.Kernel/DocsWalker.Kernel.csproj -c Release -r win-x64 --self-contained true
```

Pass-критерий: оба `dotnet publish` завершаются без ошибок и без `IL2026/IL3050`
warning'ов (Kernel помечен `PublishAot=true`).

Стейджим оба exe рядом (spawn-логика ищет `DocsWalker.Kernel.exe` в каталоге CLI):

```bash
SMOKE='/tmp/docswalker-smoke-bin'
rm -rf "$SMOKE"
mkdir -p "$SMOKE"
cp -r src/DocsWalker.Cli/bin/Release/net10.0/win-x64/publish/*    "$SMOKE/"
cp -r src/DocsWalker.Kernel/bin/Release/net10.0/win-x64/publish/* "$SMOKE/"
CLI="$SMOKE/DocsWalker.Cli.exe"
KERNEL="$SMOKE/DocsWalker.Kernel.exe"
```

### Шаг 2 — kernel поднимается, отвечает на /health и /roots

Сценарий из strategy.md #9.1.

```bash
cd "$SMOKE"
./DocsWalker.Kernel.exe --root-idle-timeout=10s > kernel.out 2> kernel.err &
sleep 2
cat kernel.err
PORT=$(cmd //c 'type C:\Users\sibkit\AppData\Local\DocsWalker\kernel.json' \
       | grep -oE '"port":[0-9]+' | grep -oE '[0-9]+')
echo "PORT=$PORT"
curl -s "http://127.0.0.1:$PORT/health"
curl -s "http://127.0.0.1:$PORT/roots"
```

Pass-критерий:
- `kernel.err` содержит три строки: `kernel_info_path=…`, `kernel_info_written exists=True`,
  `DocsWalker kernel started: pid=…, url=http://127.0.0.1:…, …`.
- `/health` возвращает `{"ok":true,"pid":…,"version":"0.5.0-dev","started_at":…}`.
- `/roots` возвращает `{"roots":[]}` (пусто до первого вызова).

### Шаг 3 — CLI auto-spawn + одиночная команда

Сценарий из strategy.md #9.2.

```bash
taskkill //F //IM DocsWalker.Kernel.exe 2>&1 || true
sleep 1
cmd //c 'rmdir /s /q C:\Users\sibkit\AppData\Local\DocsWalker' 2>&1 || true

cd D:/Dev/cs/projects/DocsWalker
"$CLI" get-nodes --root=. --ids=1 > /tmp/cli.out 2> /tmp/cli.err
echo "exit=$?"
cat /tmp/cli.err
head -c 200 /tmp/cli.out
```

Pass-критерий:
- exit code 0;
- stdout содержит JSON-массив с одним узлом id=1 (поля `type`, `title`, `text`, `out_refs`);
- stderr содержит две строки: `kernel: spawned pid=…` и `kernel: ready url=http://127.0.0.1:…`
  (auto-spawn — не silent);
- ядро живо после exit'а CLI (`tasklist | grep DocsWalker.Kernel` находит один процесс).

### Шаг 4 — multi-root в одном ядре

Сценарий из strategy.md #9.3. Второй root — копия `docs/`.

```bash
ROOT2='/tmp/docswalker-smoke-root2'
ROOT2_W='C:\Users\sibkit\AppData\Local\Temp\docswalker-smoke-root2'
rm -rf "$ROOT2"
mkdir -p "$ROOT2"
cp -r docs "$ROOT2/"

"$CLI" get-map --root="$ROOT2_W" > /tmp/root2.out
PORT=$(cmd //c 'type C:\Users\sibkit\AppData\Local\DocsWalker\kernel.json' \
       | grep -oE '"port":[0-9]+' | grep -oE '[0-9]+')
curl -s "http://127.0.0.1:$PORT/roots"
```

Pass-критерий: `/roots.roots` массив длиной 2; оба пути присутствуют
(основной `D:\Dev\cs\projects\DocsWalker` + временный `…\docswalker-smoke-root2`),
`last_used` свежий, `expires_at` = last_used + idle.

### Шаг 5 — mcp-server stdio↔HTTP wrapper

Сценарий из strategy.md #9.4.

```bash
cat > /tmp/mcp-frames.json << 'EOF'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get-map","arguments":{}}}
EOF
"$CLI" mcp-server --root=. --quiet=true < /tmp/mcp-frames.json > /tmp/mcp.out 2> /tmp/mcp.err
echo "exit=$?"
wc -l /tmp/mcp.out
```

Pass-критерий:
- exit 0;
- 3 строки в stdout (по числу запросов);
- каждая строка — JSON-RPC envelope с `id` (1/2/3) и `result` без `error`;
- (без `--quiet=true`) в stderr строка `DocsWalker MCP-wrapper started: root=…, kernel=…`.

### Шаг 6 — repl интерактивный

Сценарий из strategy.md #9.5.

```bash
cat > /tmp/repl-in.txt << 'EOF'
check-integrity
get-map
:quit
EOF
"$CLI" repl --root=. --quiet=true < /tmp/repl-in.txt > /tmp/repl.out 2> /tmp/repl.err
echo "exit=$?"
head -c 300 /tmp/repl.out
```

Pass-критерий:
- exit 0;
- `repl.out` содержит две JSON-строки после `dw> ` prompt'а (ответ на `check-integrity`
  и `get-map`);
- `:quit` корректно завершает REPL.

(Если падает с `IOException: Неверный дескриптор` на `Console.TreatControlCAsInput` —
не применён non-TTY fallback в `LineReader.cs`. Это часть step-09 кода; см.
секцию «Цель».)

### Шаг 7 — per-root eviction

Сценарий из strategy.md #9.6. Idle = 10s, проверяем переход 1→0→1.

```bash
taskkill //F //IM DocsWalker.Kernel.exe 2>&1 || true
sleep 1
cmd //c 'rmdir /s /q C:\Users\sibkit\AppData\Local\DocsWalker' 2>&1 || true

cd "$SMOKE"
./DocsWalker.Kernel.exe --root-idle-timeout=10s > kernel.out 2> kernel.err &
sleep 2
PORT=$(cmd //c 'type C:\Users\sibkit\AppData\Local\DocsWalker\kernel.json' \
       | grep -oE '"port":[0-9]+' | grep -oE '[0-9]+')

cd D:/Dev/cs/projects/DocsWalker
"$CLI" get-map --root=. > /dev/null
curl -s "http://127.0.0.1:$PORT/roots"   # roots count=1
sleep 12
curl -s "http://127.0.0.1:$PORT/roots"   # roots count=0
"$CLI" get-map --root=. > /dev/null
curl -s "http://127.0.0.1:$PORT/roots"   # roots count=1
```

Pass-критерий: после паузы 12s (>10s idle) `/roots.roots` пуст; следующий
CLI-запрос успешно работает (exit 0); `/roots.roots` снова длиной 1.

### Шаг 8 — параллельный spawn

Сценарий из strategy.md #9.7.

```bash
taskkill //F //IM DocsWalker.Kernel.exe 2>&1 || true
sleep 1
cmd //c 'rmdir /s /q C:\Users\sibkit\AppData\Local\DocsWalker' 2>&1 || true

cd D:/Dev/cs/projects/DocsWalker
"$CLI" get-map --root=. > /tmp/race1.out 2> /tmp/race1.err &
P1=$!
"$CLI" get-map --root=. > /tmp/race2.out 2> /tmp/race2.err &
P2=$!
wait $P1; EXIT1=$?
wait $P2; EXIT2=$?
echo "exit1=$EXIT1 exit2=$EXIT2"
cat /tmp/race1.err; echo '---'; cat /tmp/race2.err
tasklist 2>&1 | grep -i DocsWalker.Kernel
```

Pass-критерий:
- оба `$EXIT1`/`$EXIT2` = 0;
- оба `/tmp/raceN.out` содержат валидный JSON-ответ (одинакового размера —
  оба get-map к одному графу);
- ровно один процесс `DocsWalker.Kernel.exe` (winner поднял, loser
  присоединился);
- В stderr одного из CLI — `kernel: spawned …` (winner), у другого — пусто (loser).

### Шаг 9 — финальная уборка

```bash
taskkill //F //IM DocsWalker.Kernel.exe 2>&1 || true
cmd //c 'rmdir /s /q C:\Users\sibkit\AppData\Local\DocsWalker' 2>&1 || true
rm -rf /tmp/docswalker-smoke-bin /tmp/docswalker-smoke-root2
rm -f  /tmp/cli.out /tmp/cli.err /tmp/race*.out /tmp/race*.err \
       /tmp/repl-in.txt /tmp/repl.out /tmp/repl.err \
       /tmp/mcp-frames.json /tmp/mcp.out /tmp/mcp.err \
       /tmp/root2.out /tmp/root2.err
```

## Завершение

После прохождения всех 7 сценариев:

1. `strategy.md`: `[*] (09) smoke` → `[+] (09) smoke`.
2. Из `CLAUDE.md` удалить секции `## Временные артефакты сессии` (все строки
   step-09 убраны после шага 9) и `## Активная сессия` (снапшот неактуален —
   stg-0008 закрыт).
3. Atomic git: `git add` (точечно — `.planning`, `CLAUDE.md`, `src/`;
   `bash.exe.stackdump` в корне игнорируем), `git commit -m "Implement
   stg-0008 step-09 smoke"`, `git push origin master`. Каждая команда —
   отдельный Bash-вызов.

## Риски

- **Cygwin/Git-Bash mount table.** Mount table `/c on / type ntfs` ≠
  `/cygdrive/c/...` (literal-path в Git-Bash). Использовать `/c/` или `/tmp/`
  для Windows-путей; для проверок через cmd — `cmd //c 'dir C:\...'`.
- **Auto-spawn ядра в неинтерактивной shell.** Решено `OutputType=WinExe` на
  Kernel — windows-subsystem не наследует console-handles, detached spawn
  работает. Если падает — диагностика: запустить kernel вручную (шаг 2),
  убедиться, что CLI находит уже работающее ядро.
- **Idle eviction может срабатывать раньше при сильно скошенных часах или
  спорадичности background-таска.** Mitigation: 10s idle + 12s sleep даёт
  буфер; «следующий запрос re-load'ит» — самодостаточная проверка, не зависит
  от точного момента eviction.
- **Spawn race** — sole-winner гарантируется advisory-lock семантикой
  `kernel.lock`. Если воспроизвелся double-spawn (два DocsWalker.Kernel.exe в
  tasklist) — это блокер step-09.
- **`bash.exe.stackdump` в корне репо** — Cygwin crash dump, не от этой
  работы. Mitigation: targeted `git add .planning CLAUDE.md src` вместо
  `git add -A`.

## Точка возобновления (если step-09 прервётся)

Этот шаг — последний в stg-0008. Прервался — значит, остался один из 7 сценариев
(шаги 2..8). Restart с того сценария, на котором сорвалось; pre-conditions
(шаг 0) и publish (шаг 1) переделывать не нужно, если бинарники свежие.
