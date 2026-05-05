# stg-0001 — read-api

## Цель
Реализовать все read-операции по `docs/DocsWalker.yml`/«Операции чтения» поверх загруженного графа.

## Файлы
`src/DocsWalker.Core/Api/ReadApi.cs` — реализация: `ListDocuments`, `GetMap`, `GetNodes`, `GetByPath`, `GetRefs`, `GetInRefs`, `Search`
`src/DocsWalker.Cli/Cli/Handlers/ReadHandlers.cs` — обработчики этих команд (заменяют заглушки из `cli-skeleton`)

## Действия
1. `list-documents` — пары `(id, title)` корневых узлов документов.
2. `get-map` — дерево узлов в форме path-связей; для каждого узла id, title, type, без описаний и блоков.
3. `get-nodes` — по списку id вернуть полный узел: id, title, type, описание, блоки, out_refs (с origin = explicit / system / default), parent_id отдельным полем.
4. `get-by-path` — по строке вида `Документ/Раздел/Подраздел` вернуть полное поддерево (если path указывает на документ — всё содержимое файла в структурированной форме).
5. `get-refs` — все связи узла в обе стороны: in/out, фильтр по `type` и `origin`. Для каждой связи — `direction`, `type`, `origin`, id противоположного узла, его title и path.
6. `get-in-refs` — только входящие; эквивалент `get-refs` с `direction=in`. Включает явные и default-связи.
7. `search` — полнотекст по описаниям и блокам узлов (case-insensitive substring); результат — id найденных узлов и фрагменты совпадений.

## Риски
- `get-by-path` требует, чтобы парсер path-строки корректно работал с разделителем `/`. Если в title встречается `/` — проверить правила оформления и/или экранировать.
- `search` — на этом шаге простой substring; индексирование (если потребуется) — отдельная задача, не сейчас.
