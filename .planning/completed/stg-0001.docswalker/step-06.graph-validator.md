# stg-0001 — graph-validator

## Цель
Реализовать полный набор проверок целостности графа из `docs/DocsWalker.yml`/«Контракт валидации». Запись применяется только если результирующее состояние проходит валидатор.

## Файлы
`src/DocsWalker.Core/Validation/Validator.cs` — общий вход `Validate(graph) -> ValidationError[]`
`src/DocsWalker.Core/Validation/MetaSchemaCheck.cs` — соответствие `Схема.yml` мета-схеме
`src/DocsWalker.Core/Validation/SchemaCheck.cs` — соответствие документов схеме (типы узлов, обязательные поля, перечисления)
`src/DocsWalker.Core/Validation/RefsCheck.cs` — тип каждой явной связи объявлен как ref_type, цель существует, цикл по path невозможен
`src/DocsWalker.Core/Validation/UniqueCheck.cs` — id уникален в `docs/`, title section уникален в пределах document
`src/DocsWalker.Core/Validation/StyleCheck.cs` — snake_case структурных ключей, отсутствие пустых строк, формат склейки `(#id) title`, явный запрет YAML-конструкций `\|`, `>`, `&`, `*`, `!`, `%` (если YAML-библиотека их пропускает)

## Действия
1. Спроектировать `ValidationError`: код, путь к месту в графе/файле, человекочитаемое сообщение.
2. Реализовать пять проверок (`MetaSchema`, `Schema`, `Refs`, `Unique`, `Style`) как независимые классы; общий `Validator` собирает ошибки в один список.
3. `StyleCheck` включает явное отвержение запрещённых YAML-конструкций — проходом по сырому тексту файла, если AST-уровень их пропускает. Простая state-machine, отделяющая «внутри кавычек» от остального.
4. Интегрировать валидатор как обязательный шаг для всех операций записи (см. `write-api-basics`, `write-api-transaction`).

## Риски
- Поиск запрещённых YAML-конструкций по сырому тексту чувствителен к ложным срабатываниям внутри строк-литералов в кавычках — отсюда state-machine.
- Цикл по path формально невозможен (path — производная YAML-вложенности), но защититься от него стоит на случай ошибок построения графа.
