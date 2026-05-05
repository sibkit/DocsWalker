# stg-0002 — create-document

## Цель
Добавить write-операцию `create-document`: создание нового файла `docs/*.yml` через API. Закрывает пробел «единственная точка записи» — сейчас новые документы можно завести только руками.

## Файлы
`docs/DocsWalker.yml` — раздел «Операции записи» (#27): новый пункт `create_document` с параметрами (`filename`, `description`, `body?`) и правилами (имя файла без расширения уникально; путь относительный, без `..` и без `.docswalker/`).
`docs/DocsWalker.yml` — пункт `transaction` (#34): упомянуть в списке.
`docs/DocsWalker.yml` — раздел «CLI-интерфейс» (#35): пример.
`src/DocsWalker.Core/Api/WriteApi.cs` — `CreateDocumentOp`, `ApplyCreateDocument`: резервирует id, создаёт корневой узел типа `document`, помечает новый документ.
`src/DocsWalker.Core/Api/WriteState.cs` — поддержка регистрации нового документа в наборе изменённых.
`src/DocsWalker.Core/Store/AtomicWriter.cs` — убедиться, что путь к новому файлу создаётся (включая поддиректории).
`src/DocsWalker.Core/Api/Transaction.cs` — разбор `create-document`.
`src/DocsWalker.Cli/Cli/Commands.cs`, `Cli/Handlers/WriteHandlers.cs`, `Program.cs`.
`tests/DocsWalker.Tests/WriteApiTests.cs` — тесты.

## Действия
1. Зафиксировать операцию в `docs/DocsWalker.yml`.
2. Реализовать `CreateDocumentOp`: проверка, что файл с таким именем не существует и не запрещён (`.docswalker/`, абсолютный путь, `..`); резервирование id; построение `Node` типа `document` с обязательными полями (`id`, `description`); добавление в граф; помещение пути в список dirty.
3. Поддержать в `Transaction.cs` и CLI.
4. `AtomicWriter` должен корректно создать поддиректории при необходимости (проверить — возможно, уже работает).
5. Тесты: успешное создание; ошибка при дубликате имени; ошибка при попытке создать файл вне `docs/`; создание в подкаталоге (например, `lang/Синтаксис.yml`).

## Риски
Имя файла на русском с пробелами — должно корректно проходить через ОС API на Windows и Linux. Проверить путь относительно `docs/` и нормализацию слешей.
