# Database Model

SQLite-схема хранения DocsWalker для следующей версии. Source of truth
для физического слоя — как `../api/` для LLM-facing JSON API.

API контракт лежит в [../api/](../api/) и описывает поведение методов
`read` и `tx`. Эта папка описывает, **как** это поведение реализуется
поверх SQLite.

## Принципы

- **Гибрид relational + JSON.** Скалярные/часто-фильтруемые поля
  (`id`, `scope`, `path`, `title`, `version`) — столбцы.
  `content` — `TEXT` (per [../api/model.md](../api/model.md) — строка,
  возможно с escaped-JSON для структуры). `map_bindings` — отдельная
  индексируемая таблица. Секции hist (`created` / `changed` /
  `deleted`) — JSON-blob в `tx_event.sections_json` для replay /
  rollback **плюс** sub-таблицы `tx_touches_node` /
  `tx_touches_link` для индексируемых селекторов `touches_*`.
- **Один DB на kernel.** Все графы (graphs из `kernel-config.json`)
  живут в одном `.sqlite` файле. Изоляция между graphs — через
  колонку `graph_name` в каждой таблице, первой в составных индексах.
- **Optimistic concurrency через `node.version`.** Колонка
  `node.version` инкрементируется при каждом изменении состояния
  узла. Kernel возвращает это значение в ответе `read` как поле
  `version`; LLM передаёт его обратно в `tx.update.expected_version`.
  При расхождении — `version_mismatch` (см.
  [../api/errors.md](../api/errors.md)). Никаких HMAC-секретов и
  signed receipts.

## Что не в DB

- **Meta-schema** (`docs/.docswalker/meta-schema.json`) — kernel-owned,
  версионируется вместе с релизом DocsWalker, не меняется в runtime.
  Остаётся файлом.
- **Kernel-config** — конфигурация развёртывания (порт, путь к DB,
  реестр graphs), не данные. Остаётся файлом. Изменение по сравнению
  с текущей версией: вместо `graphs: { name → folder_path }` теперь
  `db_path: "..."` + `graphs: [name1, name2]`. Имя графа — ключ в
  таблицах.

## SQLite PRAGMA

Kernel при открытии DB обязательно выставляет:

```sql
PRAGMA journal_mode = WAL;          -- читатели не блокируют писателя
PRAGMA synchronous = NORMAL;        -- баланс надёжности и скорости
PRAGMA foreign_keys = ON;           -- enforced FK на binding/touches
PRAGMA case_sensitive_like = ON;    -- LIKE по path — case-sensitive
```

Минимальная версия SQLite — 3.38 (для JSON1 операторов и expression
indexes).

## Файлы

- [schema.md](schema.md) — полный DDL всех таблиц с индексами,
  объяснение каждой колонки, маппинг API-селекторов в SQL,
  compact / full форма выдачи.
- [hist.md](hist.md) — как `tx` пишет в `tx_event` + touches таблицы,
  алгоритм replay, rollback, реконструкция значения поля.

## Миграция

Переход с текущего JSON-file storage на SQLite — отдельная задача,
описывается в самом kernel-е (одноразовая миграция при первом старте
новой версии: парсит существующий `docs/`, перекладывает в таблицы,
один tx-event с `title="initial-import"`). В этой папке пока не
специфицируется.
