# stg-0005 — session-state-core

## Цель
Новая подсистема ядра: in-memory `Map<UUID, SeenSet>`, persistence файлов sessions, GC по TTL, hash-detection ручной правки YAML. Без интеграции с командами — только инфраструктура.

## Файлы
`src/DocsWalker.Core/Sessions/SessionState.cs` — новый класс, держит мап в RAM, методы `EnsureSession`, `MarkSeen`, `Filter`, `RemoveFromAll`, `ResetSeen`, `EvictExpired`.
`src/DocsWalker.Core/Sessions/SessionFile.cs` — сериализация одной session в YAML (`created`, `last_used`, `seen`).
`src/DocsWalker.Core/Sessions/DocsChecksum.cs` — SHA256 от sorted-list YAML-файлов docs/, чтение/запись `docs/.docswalker/sessions/.checksum`.
`src/DocsWalker.Core/Server/Lifecycle.cs` (или эквивалент стартового кода сервера) — на startup load sessions с проверкой checksum, на shutdown flush + write checksum.
`.gitignore` — добавить `docs/.docswalker/sessions/`.

## Действия
1. Класс `SessionState` thread-safe (под общим семафором сервера, как все запросы); хранит `Dictionary<Guid, SeenSet>`, где `SeenSet = { HashSet<int> ids, DateTime created, DateTime lastUsed, bool dirty }`.
2. Persistence: один YAML-файл на session в `docs/.docswalker/sessions/<uuid>.yml`. Формат — обычный YAML mapping (через тот же emitter, что docs/). Папку создавать лениво при первой записи.
3. На startup сервера: вычислить SHA256 текущих docs/-файлов (исключая `docs/.docswalker/sessions/`); сравнить с записанным в `.checksum`. Mismatch или отсутствие checksum → стереть содержимое `sessions/` и не загружать. Совпало → загрузить session-файлы в map, отбросить с `last_used` старше TTL.
4. На graceful shutdown: flush sessions с `dirty=true`, записать актуальный checksum.
5. TTL=7d захардкожен константой; GC вызывается в startup (после load).
6. `EvictExpired` чистит из map и удаляет файлы.

## Риски
- Параллельная запись sessions в принципе не должна происходить (общий семафор сервера #313). Внешних правок sessions/ не предполагается — служебная папка.
