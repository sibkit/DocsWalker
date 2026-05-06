# stg-0002 — folders-read-create

## Цель
Поддержать тип `folder` в DocsWalker на чтение и на структурные операции
create/delete. После R10:
- DocumentLoader рекурсивно обходит подкаталоги `docs/`, читает
  `.docswalker/folders.yml` как primary-источник folder-узлов и сшивает
  каждый документ с правильным folder-родителем (вместо текущего
  «всем path = root»);
- расхождение FS ↔ `folders.yml` приводит к структурированной
  `integrity_failed`-ошибке без авто-починки;
- `create-node --type=folder --title=<dirname> --path=<parent_id>`
  одновременно создаёт каталог в FS и добавляет запись в
  `folders.yml`;
- `delete-node` для folder допустим только если каталог пуст
  (нет документов и подкаталогов под ним), удаляет каталог в FS и
  запись в `folders.yml`;
- `update-node` и `move-node` для folder в этой итерации **не
  поддерживаются** — отдельный шаг R11. На попытку — структурированная
  `not_supported`-ошибка с подсказкой.

После R10 проект собирается без ошибок; CLI-команды
`create-node type=folder`, `delete-node`, чтение через `get-map`,
`get-by-path`, `get-nodes` корректно работают для подкаталогов.

## Файлы

**Новые:**
- `src/DocsWalker.Core/Store/FoldersFile.cs` — read/write
  `.docswalker/folders.yml`. Формат:
  ```yaml
  - id: 17
    path: 0
    title: guides
  - id: 42
    path: 17
    title: advanced
  ```
  (плоский список, элементы — mapping с тремя полями: `id` (int),
  `path` (int — id родителя; 0 = root), `title` (string — dirname).
  Файл может отсутствовать целиком — это эквивалент пустого списка
  (проект без подкаталогов).
- `src/DocsWalker.Core/Store/FoldersFileException.cs` — структурированная
  ошибка чтения/записи folders.yml (можно положить рядом с
  `SequenceCounter` в одном файле, если так удобнее).

**Переписаны:**
- `src/DocsWalker.Core/Graph/DocumentLoader.cs`:
  - перед сканом `*.yml` загружает `folders.yml` (если есть);
  - для каждой записи `{id, path, title}` создаёт folder-узел с
    `OutRefs[path]=[parent_id]`, `Title=title`,
    `SourceFile=".docswalker/folders.yml"` (folder-узлы все
    "лежат" в одном служебном файле);
  - сверяет FS-каталоги и записи: для каждой записи нужен каталог
    по цепочке title до root; для каждого FS-каталога (кроме
    `.docswalker/`) — запись. Расхождение → `folder_dir_missing` /
    `folder_record_missing`;
  - при чтении `*.yml` определяет parent документа по его
    физическому пути: первый сегмент относительного пути → folder с
    title-цепочкой, последний — имя документа. Документу ставится
    `OutRefs[path]=[<id-folder-родителя>]` вместо текущего
    `[Node.RootId]`.
- `src/DocsWalker.Core/Api/WriteApi.cs`:
  - `ResolveSourceFile` — для `title_source=dirname` (folder)
    возвращает `.docswalker/folders.yml`;
  - `ApplyCreateNode` для type=folder: проверяет, что parent —
    либо root, либо folder-узел; формирует FS-путь нового каталога
    как цепочку title-сегментов до root; добавляет
    fs-операцию `CreateDirectory` и помечает folders.yml dirty;
  - `ApplyDeleteNode` для type=folder: проверяет, что у узла нет
    path-детей (ни folder, ни document); добавляет fs-операцию
    `DeleteEmptyDirectory` и помечает folders.yml dirty;
  - `ApplyUpdateNode` / `ApplyMoveNode` для type=folder бросают
    `WriteApiException("not_supported", …)` с подсказкой
    «rename/move folder появятся в R11».
- `src/DocsWalker.Core/Api/WriteState.cs` — добавить
  `bool _foldersDirty` и метод `MarkFoldersDirty()`. Добавить
  список fs-операций (`AddFsOperation(FsOp op)`) и
  `IReadOnlyList<FsOp> FsOperations`.
- `src/DocsWalker.Core/Store/AtomicWriter.cs` — расширить:
  `AtomicWriteTarget` остаётся для записи файлов; добавить
  отдельный мини-API `FsOp` (sealed-record-иерархия:
  `FsCreateDirectory(path)`, `FsDeleteEmptyDirectory(path)`).
  В рамках R10 не вводим `MoveDirectory` — это для R11.
  Появляется метод `AtomicWriter.WriteAllAndApplyFs(targets, ops)`:
  сначала фаза tmp + rename для файлов, затем последовательно
  применяются FS-операции. Частично-применённое состояние возможно,
  как и в текущей версии, и зафиксировано в комментарии.
- `src/DocsWalker.Core/Validation/IntegrityCheck.cs` (или где сейчас
  `ReadApi.CheckIntegrity` живёт) — добавить FS-симметрию:
  каждый folder-узел из folders.yml должен иметь существующий
  каталог; каждый каталог в `docs/` (кроме `.docswalker/`) должен
  иметь folder-узел.

**Документы:**
- `docs/DocsWalker.yml` — добавить:
  - в `(#7) Модель данных`: упоминание folder-типа в духе «структурный
    тип, физически = каталог в docs/, идентифицируется записью в
    `.docswalker/folders.yml`»;
  - в `(#15) Sequence-счётчик id` или новой секции — описание
    `.docswalker/folders.yml` (формат, симметрия с FS).
  - в `(#27) Операции записи`: что create/delete для folder
    поддержаны; rename/move появятся отдельно (отметить как «не
    поддержано в текущей версии»).

**Не трогаются:**
- `tests/DocsWalker.Tests/*` — отдельный шаг после R11/общего
  переписывания тестов под refs-модель.

## Принятые решения

1. **`folders.yml` — primary, FS — производное.** Согласовано:
   loader не правит FS при load. Расхождение → `integrity_failed`
   (или соответствующий код в graph-load). Симметрия с
   `sequence.txt`.
2. **Запись в `folders.yml` — `{id, path, title}`.** id и parent_id
   живут в записи, FS-имя выводится по title. Переименование =
   правка одной строки + переименование каталога. Это договоренность
   на потом (R11).
3. **`SourceFile` для folder = `.docswalker/folders.yml`.** Все
   folder-узлы шарят один SourceFile. dirty-маркировка
   folders.yml — отдельный флаг, не через `AffectedDocumentIds`.
4. **`create-node folder` создаёт каталог сразу.** Симметрия с
   document (создание `*.yml` сразу). LLM получает «создал →
   значит, на FS оно есть».
5. **`update-node` / `move-node` для folder — `not_supported` в R10.**
   Cascade-rewrite SourceFile у потомков и FS-перенос каталогов —
   отдельный шаг (R11). В R10 явный отказ с понятным сообщением,
   а не молчаливое no-op.
6. **`AtomicWriter` расширяется минимально.** В R10 — только
   `CreateDirectory` и `DeleteEmptyDirectory` (никаких rename/move
   каталога). FS-операции применяются после успешного завершения
   фазы tmp+rename для файлов.

## Действия

1. Создать `Store/FoldersFile.cs` — read/write списка
   `FolderRecord(int Id, int ParentId, string Title)`. Чтение через
   тот же `YamlReader` (event-stream), запись через явный YAML-emit
   с UTF-8 без BOM. Пустой/отсутствующий файл = пустой список.
2. Расширить `AtomicWriter` под `FsOp`-операции. Контракт —
   именно «применить после файлов»; ошибка применения — exception
   `AtomicWriteException("fs_apply_failed", path, …)`.
3. Расширить `WriteState`: `_foldersDirty`, `MarkFoldersDirty`,
   `_fsOps`, `AddFsOperation`. `BuildGraph` остаётся прежним.
4. Переписать `DocumentLoader.Load`:
   - сначала `FoldersFile.Read(docsRoot)`;
   - вытащить мапу `path-cepочка-title → folderId`;
   - сверить FS-каталоги (рекурсивно через `EnumerateDirectories`,
     исключая `.docswalker/`) с записями;
   - сделать folder-узлы (порядок — от root к листьям, чтобы
     parent уже существовал);
   - в `LoadFile` определять parent документа по его относительному
     пути (`Path.GetDirectoryName(rel)` → ищем folder-узел с такой
     цепочкой title; если каталог = `""`, parent = root).
5. Расширить `WriteApi.ApplyCreateNode`:
   - если type.Name == "folder":
     - валидировать, что parent — root или folder;
     - построить путь FS-каталога: `BuildFolderPath(state, parentId, title)`
       (рекурсия по title-цепочке);
     - проверить, что каталог ещё не существует и нет коллизии по
       title под тем же parent;
     - добавить `FsOp.CreateDirectory(absolutePath)`;
     - вызвать `state.MarkFoldersDirty()`;
     - SourceFile для нового узла = `.docswalker/folders.yml`.
6. Расширить `WriteApi.ApplyDeleteNode`:
   - если node.TypeName == "folder":
     - проверить, что нет path-детей (`state.GetChildren(id)` пуст);
     - добавить `FsOp.DeleteEmptyDirectory(absolutePath)`;
     - `state.MarkFoldersDirty()`.
7. Расширить `WriteApi.ApplyUpdateNode` и `ApplyMoveNode`:
   - если node.TypeName == "folder":
     - бросить `WriteApiException("not_supported",
       "Rename/move folder в текущей версии не поддержан.",
       "Появится в шаге R11.")`.
8. В `WriteApi.ApplyLocked` — после построения targets для документов
   добавить target для folders.yml, если `_foldersDirty=true`,
   и применить FS-операции через расширенный AtomicWriter.
9. В `ReadApi.CheckIntegrity` — добавить проверки FS↔folders.yml.
10. Обновить `docs/DocsWalker.yml` синхронно с реализацией
    (раздел «Модель данных» и «Операции записи» — добавить folder,
    зафиксировать ограничения R10).
11. `dotnet build` — Core+Cli зелёные. Tests остаются красными
    (отдельный шаг).

## Риски

- **Cascade SourceFile у документов внутри подкаталога.**
  В R10 не переименовывается ни folder, ни перенос — поэтому
  cascade не нужен. Но при обычной записи документов внутри
  подкаталога (`update-node` атома, `create-node` нового section и т. п.)
  emitter должен уметь писать `*.yml` по правильному относительному
  пути. Это уже должно работать, т. к. SourceFile документа
  включает каталог-префикс — но если loader сейчас всем документам
  ставит SourceFile = голое имя файла, нужно проверить и поправить.
- **Конкуренция `folders.yml` ↔ FS.** Если между чтением и записью
  процесс был убит, возможно расхождение. Это смягчается тем, что
  AtomicWriter применяет FS-операции после успешной записи всех
  файлов; folders.yml обновляется в фазе файлов, FS-операции — после.
  Сценарий «есть запись, нет каталога» возможен при сбое; решается
  диагностикой через integrity_check, что и зафиксировано в R10.
- **Тесты остаются красными.** Это уже зафиксированный долг
  R6/R7/R8/R9 — R10 его не уменьшает и не увеличивает.
- **Объём шага.** R10 без update/move folder всё ещё значительный
  (FoldersFile, рекурсивный loader, FS-симметрия, расширение
  AtomicWriter, интеграция в Persist). Если в процессе всплывёт
  блокирующее проектное решение (например, смена представления
  SourceFile у folder-узлов), шаг можно остановить, выпустить
  частичный коммит и спросить.
