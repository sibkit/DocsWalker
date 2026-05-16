# DocsWalker JSON API

`api/` — source of truth для описания JSON API DocsWalker. Если поведение
API, формат запроса, формат ответа, операция или код ошибки меняются,
сначала обновляется этот каталог, затем код и тесты.

Старый набор файлов до перехода на 4-scope модель архивирован в
[_archive/](_archive/). Файлы _archive — историческая справка, не
актуальная спецификация.

## Модель в одном абзаце

DocsWalker хранит граф знаний в одном каталоге `docs/`. Каталог разделён
на четыре scope: `main` (пользовательский контент), `usage` (инструкции
для LLM), `scheme` (редактируемые контракты Схемы) и `hist` (журнал
изменений editable scope, пишется kernel-ом). У каждого scope своя
schema, общая meta-schema (`docs/.docswalker/meta-schema.json`) задаёт
базовые поля узла (`id`, `path`, `title`, `value`, `map_bindings`) и
link (`name`, `source.id`, `target.id`, `target.scope`). Id-пространство
глобальное на весь каталог. Все id — opaque hex-строки lower-case. Все данные
хранятся как JSON.

Внешняя поверхность v1 состоит из двух методов:

- `read` — чтение узлов из выбранного scope по predicate-селекторам.
- `tx` — атомарное внесение изменений в editable scope.

Параметр `scope` опционален в обоих методах: отсутствие = `main`. Явное
указание `"main"` — ошибка. История пишется ядром и доступна только на
чтение (`read scope=hist`).

## Транспорт

Основной LLM-канал — MCP `tools/call` поверх kernel JSON-RPC. Метод
выбирается именем MCP tool в `params.name`. Внутри `params.arguments`
лежат только аргументы метода.

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "read",
    "arguments": {
      "ops": []
    }
  }
}
```

Успешный JSON-RPC ответ возвращает MCP `content`; внутри строкового
payload лежит JSON-результат конкретного метода. JSON-RPC ошибка
возвращает `error {code,message}`. Ошибка JSON API возвращается как
JSON с машинным `code` и `details`.

MCP tool descriptions содержат компактный quickstart. Подробные
инструкции, examples и schemas LLM читает через `read scope=usage` и
`read scope=scheme`.

## Файлы

- [model.md](model.md) — meta-schema, поля узла и link, 4 scope,
  глобальный id, defaults.
- [surface.md](surface.md) — два MCP-tools `read` и `tx`, параметр
  `scope`, envelope success/error.
- [selectors.md](selectors.md) — predicate-селекторы по полям
  meta-schema и schema scope, `selector.match.regex`, aliases и slots.
- [read.md](read.md) — метод `read`, `include`, `max_tokens`, truncation,
  `read_id` (state vs value).
- [tx.md](tx.md) — метод `tx`, шесть op-типов
  (`create`/`update`/`move`/`delete`/`link`/`unlink`), `expected_count`,
  rollback operation.
- [scheme-scope.md](scheme-scope.md) — scheme scope, breaking-change-check,
  add-then-remove migration workflow.
- [usage-scope.md](usage-scope.md) — usage scope, node contracts (rule,
  map, link, example, topic, method, field, error), cross-scope
  `usage → main` links.
- [hist-scope.md](hist-scope.md) — hist event log, шесть event-типов,
  target identity, replay restoration, rollback marker.
- [read-gates.md](read-gates.md) — state preconditions, usage rule / map
  / link gates, project value gates, `read_required` envelope.
- [errors.md](errors.md) — полный реестр кодов ошибок с `details` и
  подсказками.
