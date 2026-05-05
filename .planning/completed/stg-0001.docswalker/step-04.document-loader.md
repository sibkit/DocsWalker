# stg-0001 — document-loader

## Цель
Загрузить все документы `docs/*.yml` в in-memory модель графа узлов и связей. Парсить ключи single_key_mapping по `title_format` соответствующего типа узла. Генерировать системные path-связи и default-связи по принадлежности к блокам.

## Файлы
`src/DocsWalker.Core/Graph/Node.cs` — структура узла (id, type, title, parent_id, raw-блоки, out_refs)
`src/DocsWalker.Core/Graph/Ref.cs` — структура связи (from_id, type, to_id, origin: explicit/system/default)
`src/DocsWalker.Core/Graph/Graph.cs` — корень in-memory модели: индексы по id, по типу, по parent
`src/DocsWalker.Core/Graph/TitleFormat.cs` — парсинг и сборка ключа по `title_format` (например, `(#{id}) {title}` ↔ `(id, title)`)
`src/DocsWalker.Core/Graph/DocumentLoader.cs` — обход `docs/`, парсинг каждого `*.yml`, разбор узлов по типу из Схемы

## Действия
1. Зафиксировать модель `Node`, `Ref`, `Graph`. Хранить body узла в форме, удобной для read-API (не сырой YAML, а уже разобранные блоки).
2. Парсер `title_format`: общий шаблон `(#{id}) {title}` плюс варианты, объявленные в Схеме на тип узла. Прямой и обратный.
3. `DocumentLoader`:
   - обход `docs/*.yml` (исключая `.docswalker/`);
   - парсинг каждого файла через SharpYaml на уровне event-stream API (без reflection-based Serializer — несовместим с AOT);
   - корневой узел — документ (title = имя файла без расширения);
   - рекурсивный обход по типу узла из Схемы;
   - создание системных path-связей (родитель → ребёнок) и default-связей (по принадлежности к блокам `definitions`, `examples`, `fields`, `content`);
   - разбор out_refs-блока в Ref-объекты с origin=explicit.
4. Базовая проверка на этапе загрузки: каждый id уникален в пределах docs/, каждый title section уникален в пределах document. Полная проверка — в `graph-validator`.

## Риски
- На event-stream уровне SharpYaml single_key_mapping — это пара событий `MappingStart` + один ключ + одно значение + `MappingEnd`. Диспетчеризация по типу узла из Схемы должна работать с этой формой.
- Узлы со сложными ключами (`(#42) текст`) требуют, чтобы `title_format` парсился до того, как мы дёргаем title — иначе `title` сольётся с `id` в строке.
