# stg-0002 — folders-rename-move

## Цель

Поддержать переименование и перенос folder-узлов в DocsWalker. После R11:

- `update-node --id=<folder> --title=<new>` переименовывает каталог в FS,
  правит запись в `.docswalker/folders.yml`, каскадно переписывает
  `SourceFile` у всех path-потомков (документы, секции, атомы).
- `move-node --id=<folder> --new-path=<parent>` переносит каталог под
  другой folder (или root), правит folder-запись и каскадно
  переписывает `SourceFile` потомков. Проверяет, что новый родитель
  допустим (`folder` или `root`) и не является потомком переносимого
  folder.
- `AtomicWriter` расширен FS-операцией `FsMoveDirectory` (применяется
  до фазы tmp+rename для файлов).
- В обоих случаях поведение симметрично R10:
  - запись в folders.yml — primary-источник, FS-операция выполняется
    атомарно вместе с правкой YAML-файлов;
  - YAML-файлы документов внутри переносимого/переименовываемого
    каталога не переписываются как dirty (содержание не менялось) —
    они переезжают вместе с каталогом через `FsMoveDirectory`,
    `Node.SourceFile` в графе обновляется, чтобы будущие операции
    адресовали правильный путь.

После R11 проект собирается без ошибок (Core+Cli зелёные).
Тесты остаются красными — отдельный долг от R6+.

## Файлы

**Переписаны:**

- `src/DocsWalker.Core/Store/AtomicWriter.cs`:
  - новая sealed-record `FsMoveDirectory(string SourceAbsolutePath, string DestinationAbsolutePath)`;
  - в `WriteAndApply` после фазы fs-pre/CreateDirectory добавляется
    фаза fs-pre/MoveDirectory (`Directory.Move`) — до фазы tmp+rename;
    это нужно, чтобы tmp-файл dirty-документа лёг в уже-перенесённый
    каталог. Ошибка применения → `AtomicWriteException("fs_move_directory_failed", …)`;
  - `DeleteEmptyDirectory` остаётся в фазе fs-post.

- `src/DocsWalker.Core/Api/WriteApi.cs`:
  - `ApplyUpdateNode` для folder: вместо `not_supported` —
    1. отдельная ветка для `op.NewTitle is not null && newTitle != node.Title`;
    2. проверка коллизии под тем же parent (другой folder с таким title);
    3. вычисление `oldRel = BuildFolderRelativePath(s, node.Id)` и
       `newRel` по `parent_rel + new_title`;
    4. проверка `Directory.Exists(newAbs) → fs_collision`
       (на регистр-нечувствительных FS вроде Windows коллизия
       чисто-регистровая возможна, но FS откажет на rename — пробросим
       обычной AtomicWriteException; явный рантайм-чек на регистр не
       вводим в R11);
    5. `s.Replace(folder с новым Title)`;
    6. `CascadeFolderSourceFile(s, folder.Id, oldRel, newRel)` —
       BFS по path-потомкам, для каждого узла, чей `SourceFile`
       начинается с `oldRel + "/"`, ставится новый префикс
       `newRel + "/"`;
    7. `s.AddFsOperation(new FsMoveDirectory(oldAbs, newAbs))`;
    8. `s.MarkFoldersDirty()`.
    Текст folder-узла не меняем (у folder text всегда пуст по контракту);
    обновление `op.NewText` для folder отвергаем как `invalid_field`,
    чтобы не плодить молчаливых эффектов.

  - `ApplyMoveNode` для folder: вместо `not_supported` —
    1. удаляется текущий early-throw для `node.TypeName == "folder"`;
    2. явный `invalid_path_target` если `newParent != root` и
       его тип не входит в `folder.path_targets`;
    3. остальные проверки (descendant cycle, no-effect)
       уже есть в общем теле метода — переиспользуем;
    4. `oldRel` и `newRel` вычисляются как
       `BuildFolderRelativePath(s, folder.Id)` (на текущем состоянии,
       до Replace) и `<parentRel> + "/" + folder.Title`;
    5. `s.Replace(folder с новым OutRefs[path])`;
    6. `CascadeFolderSourceFile(s, folder.Id, oldRel, newRel)`;
    7. `s.AddFsOperation(new FsMoveDirectory(oldAbs, newAbs))`;
    8. `s.MarkFoldersDirty()`.
    `MarkDocumentDirtyForNode(oldParentId)` / `MarkDocumentDirtyForNode(newParentId)`
    для folder не нужны: путь к старому/новому родителю — это folder
    или root, а не документ; folders.yml уже dirty.

  - новый приватный helper `CascadeFolderSourceFile(WriteState s, int folderId, string oldRel, string newRel)`:
    ```
    BFS по GetChildren(folderId);
    для каждого узла n:
        if (n.SourceFile.StartsWith(oldRel + "/", Ordinal))
            new SourceFile = newRel + "/" + n.SourceFile.Substring(oldRel.Length + 1);
            s.Replace(n с новым SourceFile);
    ```
    Узлы folder-типа имеют `SourceFile = ".docswalker/folders.yml"` —
    не попадают под условие префикса, поэтому корректно пропускаются.

  - `BuildFolderRelativePath` (уже есть) переиспользуется для всех
    rel-вычислений.

**Документы:**

- `docs/DocsWalker.yml` — синхронно обновляется правило `(#186)`:
  убрать «не поддержаны» для rename/move folder; описать, что rename =
  переименование dirname + правка folders.yml + cascade SourceFile
  потомков, move = всё то же между родителями. Обновить текст
  определения `(#72) move_node` — он уже упоминает cascade SourceFile
  для документов, после R11 это распространяется и на folder.

**Не трогаются:**

- `Graph/DocumentLoader.cs` — корректно выводит parent документа из
  его FS-пути через folders.yml; после rename/move новый load-цикл
  увидит новые имена и новые пути.
- `Validation/IntegrityCheck.cs` — проверки FS↔folders.yml,
  установленные в R10, остаются валидны.
- `tests/DocsWalker.Tests/*` — отдельный шаг.

## Принятые решения

1. **`FsMoveDirectory` в фазе fs-pre, до файлов.** Это нужно для
   корректности транзакций «rename folder X + update document внутри X»:
   tmp-файл документа должен лечь в уже-перенесённый каталог, иначе
   последующий rename файла упадёт.
2. **Folder-rename = только title.** `op.NewText` для folder отвергаем
   как `invalid_field` (контракт: text у folder пуст; разрешать
   изменение пустого text → не пустое — нарушит инвариант валидатора
   text_required=false и спутает diagnostics).
3. **Каскад только по `SourceFile` префиксу.** Альтернативы:
   - перепрошить SourceFile через resolve по новой цепочке title для
     каждого потомка — корректно, но требует знания, как для каждого
     типа узла собирать SourceFile из path-цепочки. Эта логика уже
     инкапсулирована тем, что для смысловых типов SourceFile = parent's
     SourceFile (transitively → root document's SourceFile). Поэтому
     префиксная замена эквивалентна resolve и проще.
   - префиксная замена на `oldRel + "/"`. Достаточно: все YAML-файлы,
     лежащие физически внутри подкаталога folder, имеют `SourceFile`,
     начинающийся с `oldRel + "/"`. Узлы внутри `.docswalker/folders.yml`
     (вложенные folder-узлы) — не имеют такого префикса и
     пропускаются.
4. **Документы под переносимым folder в `AffectedDocumentIds` не
   попадают.** Если их семантическое содержимое не менялось — их
   YAML-содержимое не нужно перезаписывать. `FsMoveDirectory`
   физически переносит файлы. `Node.SourceFile` в графе обновляется
   в памяти (важно для последующих операций в той же транзакции).
   Если в той же транзакции документ помечен dirty — его перезапись
   попадёт по новому пути (после move), что и ожидается.
5. **Регистр в title.** На Windows FS регистронезависим: rename
   `Guides → guides` Directory.Move отвергнет как «уже существует».
   Полагаемся на FS-проверку (исключение пробросится наверх как
   AtomicWriteException). Явный рантайм-нормализатор регистра в R11
   не вводим — не блокирующая фича.
6. **Перенос folder под document/смысловой узел запрещён.** В Схеме
   `folder.path_targets = [folder, root]`, поэтому новый parent должен
   быть root или folder. Проверяем явно в начале move-folder — UX
   лучше, чем рассчитывать на отложенный вердикт валидатора.
7. **Существующие проверки move-node переиспользуются.** Цикл
   («new_parent — потомок переносимого узла»), `parent_not_found`,
   `no_effect` (newParent == oldParent), `cannot_move_root` — общий
   код для всех типов; для folder ничего нового не нужно.

## Действия

1. Добавить `FsMoveDirectory` в `AtomicWriter.cs`. В `WriteAndApply`
   сразу после блока `FsCreateDirectory` пройти по
   `fsOperations`, применить `FsMoveDirectory` (`Directory.Move`).
   Ошибка → `AtomicWriteException("fs_move_directory_failed", …)`.
2. В `WriteApi.ApplyUpdateNode` найти ветку
   `node.TypeName == "folder" && op.NewTitle is not null && newTitle != node.Title`;
   заменить throw `not_supported` на полную реализацию rename
   (см. файлы выше).
3. В `WriteApi.ApplyMoveNode` убрать early-throw для folder. После
   общих проверок добавить ветку `if (node.TypeName == "folder")` до
   обычного обновления, выполнить move-folder-логику и завершить.
4. Вытащить `CascadeFolderSourceFile` в приватный helper рядом с
   `UpdateSubtreeSourceFile` (логика похожа, но префикс-замена, не
   плоская перезапись).
5. Обновить `docs/DocsWalker.yml`: правило `(#186)` и (опционально)
   определение `(#72) move_node`.
6. `dotnet build` — Core+Cli зелёные.
7. Закоммитить + запушить.

## Риски

- **Транзакция «rename + delete внутри renamed folder».** Сценарий:
  пользователь в одной пачке делает rename folder X → Y и
  delete-node атома внутри X. Атом находится в документе D,
  D.SourceFile сейчас «X/D.yml». После rename X→Y: SourceFile у D
  переписывается на `Y/D.yml`; AffectedDocumentIds содержит D
  (delete-node пометил document dirty); FsMoveDirectory переносит
  X→Y; затем AtomicWriter пишет tmp-файл в `Y/D.yml.tmp-…` и rename'ит
  в `Y/D.yml`. Корректно.
- **Транзакция «move folder + update внутри неё»** — симметрично.
  Никаких новых рисков, помимо общих рисков AtomicWriter.
- **Пересечение rename + move нескольких folder в одной транзакции.**
  Применяются в порядке регистрации `FsOperations`. Если один move
  ссылается на путь, изменённый предыдущим move в той же транзакции,
  будет рассинхронизация (rel-пути считаются от состояния после
  всех Replace, но применяются последовательно). Минимальный
  pragmatic подход: каждый folder-rename/move строит abs-пути
  на основе in-memory state на момент его обработки; если пользователь
  делает rename A→B + move C-внутри-A, операции вычисляются
  последовательно (rename A→B первым, move C-внутри-A смотрит на новое
  состояние). FS-операции применяются в том же порядке регистрации.
  Это работает корректно: после `Directory.Move(A, B)` каталог
  C-внутри-A находится по пути B/C, и следующий
  `Directory.Move(B/C, D)` найдёт его.
- **Тесты остаются красными.** Известный долг с R6.
