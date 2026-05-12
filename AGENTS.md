# AGENTS.md - DocsWalker

Инструкции для Codex в этом репозитории. Спецификация продукта живет только в
`docs/`; этот файл описывает рабочий протокол агента и локальную интеграцию
инструментов.

## Базовые правила

- Общение с пользователем, технические пояснения, docs-тексты и комментарии в
  коде - по-русски. Идентификаторы в коде - по-английски.
- `docs/` - single source of truth. Не фиксируй продуктовые решения в
  `AGENTS.md`, `CLAUDE.md` или временных заметках.
- Для любой продуктовой фичи сначала уточняется спецификация в `docs/`, затем
  меняется код. Если реализация изменила поведение, синхронно обновляй docs.
- Если инструкции или docs противоречат друг другу, остановись и явно сообщи
  пользователю. Не выбирай удобную трактовку молча.

## Работа с `docs/`

`docs/**/*.yml` нельзя читать, искать, редактировать или переписывать напрямую.
Доступ к графу идет через DocsWalker MCP tools или, если MCP еще не подключен,
через опубликованный CLI:

```powershell
src\DocsWalker.Cli\bin\Release\net10.0\win-x64\publish\DocsWalker.Cli.exe get-usage-guide
```

В начале сессии с docs:

1. Проверить kernel: `curl.exe http://127.0.0.1:18080/health`.
2. Вызвать `get-usage-guide`.
3. Вызвать `get-overview`.
4. Перед записью вызвать `check-integrity`.

Актуальные основные команды чтения: `get-nodes`, `get-tree`, `get-by-path`,
`get-ancestors`, `get-refs`, `get-in-refs`, `search`, `find`,
`describe-type`, `get-schema`, `get-meta-schema`, `get-overview`,
`get-usage-guide`.

Актуальные команды записи: `create-node`, `update-node`, `delete-nodes`,
`move-node`, `create-ref`, `delete-ref`, `redirect-refs`, `update-schema`,
`transaction`.

Исключения для прямого доступа к docs - только диагностика сломанной загрузки
графа или служебные файлы `docs/.docswalker/meta-schema.yml` и
`docs/.docswalker/sequence.txt`.

## C#-код

Если Glider MCP подключен, используй его для семантической навигации по C#:
load solution `DocsWalker.slnx`, затем `find_code`, `search_symbols`,
`find_references`, `get_structure`, `get_diagnostics`,
`get_file_contents`, `get_method_source`, `get_type_source`.

Если Glider недоступен, сообщи об этом как о деградации инструментария и
используй обычные shell-инструменты только настолько, насколько нужно для
диагностики или безопасной правки.

## Kernel и MCP

Локальный kernel ожидается на `127.0.0.1:18080`, конфиги:

- `kernel-config.json` - graph `docswalker` -> `D:/Dev/cs/projects/DocsWalker/docs`.
- `.dw/client.json` - host/port kernel и graph `docswalker`.

Запуск kernel:

```powershell
scripts\start-kernel.sh
```

Остановка kernel:

```powershell
scripts\stop-kernel.sh
```

Для Codex stdio MCP запускается через wrapper:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\docswalker-mcp.ps1
```

Wrapper делает `Set-Location` в корень репозитория, чтобы
`DocsWalker.Mcp.exe` нашел `.dw/client.json`.

## Планирование

Для крупных задач используй `.planning/`:

- одна активная стратегия за раз: `.planning/stg-{NNNN}.{slug}/strategy.md`;
- завершенные стратегии лежат в `.planning/completed/`;
- статусы шагов: `[ ]`, `[*]`, `[.]`, `[+]`, `[-]`;
- не оставляй активную стратегию или временные артефакты без явного статуса.

## Возобновление текущей работы

Если сессия сброшена, сначала открой активную стратегию
`.planning/stg-0012.json-instruction-engine/strategy.md`.

Текущий поток: `stg-0012 - json-instruction-engine`.

Стартовый протокол после сброса:

1. Проверить kernel: `curl.exe http://127.0.0.1:18080/health`.
2. Через DocsWalker MCP вызвать `get-usage-guide` и `get-overview`.
3. Через DocsWalker MCP перечитать docs-документ `DocsWalker-LLM JSON API`.
4. Через Glider убедиться, что загружен `DocsWalker.slnx`; если нет - загрузить.
5. Продолжить со следующего незавершенного шага в стратегии.

На момент последнего handoff завершены шаги `(01)`, `(02)` и `(03)`.
Следующий шаг: `(04) code-coordinate-resolver`.

Не переносить продуктовую спецификацию из стратегии в `AGENTS.md`: source of
truth для поведения остается в docs-документе `DocsWalker-LLM JSON API`, а
технический план и handoff - в `.planning/stg-0012.../strategy.md`.

## Git

Codex не делает автокоммит и автопуш по завершении задачи. Коммит и push
делаются только по явному запросу пользователя.

Из-за sandbox-пользователя git может потребовать `safe.directory`. Для разовых
команд можно использовать:

```powershell
git -c safe.directory=D:/Dev/cs/projects/DocsWalker status --short --branch
```

Не делай `git reset --hard`, force push или откат чужих изменений без прямого
запроса пользователя.

## Проверка

Минимальный smoke после миграционных или инфраструктурных изменений:

```powershell
curl.exe http://127.0.0.1:18080/health
src\DocsWalker.Cli\bin\Release\net10.0\win-x64\publish\DocsWalker.Cli.exe check-integrity
dotnet test .\DocsWalker.slnx --no-restore
git -c safe.directory=D:/Dev/cs/projects/DocsWalker status --short --branch
```
