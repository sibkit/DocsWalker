# stg-0002 — cross-process-lock

## Цель
Реализовать межпроцессную сериализацию write-операций через lock-файл `docs/.docswalker/.lock` (`FileShare.None`). Закрывает пункт #69 в `docs/DocsWalker.yml`, отложенный из MVP.

## Файлы
`docs/DocsWalker.yml` — раздел #69 «Многопроцессная запись»: убрать пометку «не реализован в первом MVP», обновить описание реализации.
`src/DocsWalker.Core/Store/CrossProcessLock.cs` (новый) — `IDisposable`-обёртка вокруг `FileStream` с `FileShare.None`. Создаёт каталог `.docswalker/` при необходимости. Поддерживает таймаут ожидания.
`src/DocsWalker.Core/Api/WriteApi.cs` — обернуть тело `Apply` в `using var lockFile = new CrossProcessLock(...)` после in-process lock.
`tests/DocsWalker.Tests/CrossProcessLockTests.cs` — тест: два потока (или два процесса) пытаются взять lock одновременно — второй ждёт; третий с таймаутом 0 — получает структурированную ошибку.

## Действия
1. Обновить `docs/DocsWalker.yml` #69.
2. Реализовать `CrossProcessLock`: открытие файла с `FileMode.OpenOrCreate`, `FileShare.None`, таймаут через цикл с задержкой (или `FileSystemWatcher` — посмотреть на этапе реализации).
3. Подключить в `WriteApi.Apply`: in-process lock → cross-process lock → существующее тело.
4. На неуспех взятия lock'а в течение таймаута — `WriteApiException("lock_timeout", ...)` с hint «другой процесс DocsWalker сейчас пишет в этот docs/».
5. Тест: два параллельных вызова, оба завершились успешно, итоговая запись непротиворечива (id не пересеклись, оба коммита видны).

## Риски
На Windows `FileShare.None` достаточно. На Linux `FileShare` через `flock` работает иначе; проверить, что `FileStream` с `FileShare.None` корректно блокирует на linux .NET 10. AOT — отдельных ограничений не накладывает, но проверить на этапе.
