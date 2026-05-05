# stg-0002 — axes-schema

## Цель
Переписать `docs/Схема.yml` под новый концепт «ось» (см. axes-meta-schema). Ввести типы `folder` и системный псевдо-узел `root`. Унифицировать default-блоки как параметризованные оси `path`.

## Файлы
`docs/Схема.yml` — широкая правка:
- Корневой объект — добавить раздел `axes`.
- `axes` — содержит:
  - `path` (system, child_to_parent, cardinality=one, target=любой node-тип; в YAML не хранится).
  - `ref` (explicit, from_to, cardinality=many, target=любой node-тип; хранится в `out_refs` источника).
- В `types` добавить:
  - `folder` (kind=mapping, node=true, title_source=dirname, без `description`-поля по решению пользователя). `must_have_axes: [path]` (path → folder | root).
  - `root` — системный псевдо-узел (id=0). Описать как `kind: system_root`, `node: true` с пометкой singleton; не создаётся пользователем.
- Из `types` удалить отдельные определения `path` и `ref` (kind=ref_type) — они переехали в `axes`.
- Каждому node-типу проставить `must_have_axes` (минимум `[path]`) и `may_have_axes` (если применимо).
- default-блоки (`definitions`/`examples`/`fields`/`content`) выразить как параметризованные значения оси `path` (через поле `block` у значения оси — параметр имени child-block родителя). В Схеме это означает: сами блоки описаны там же, где сейчас (внутри `section.blocks`), но в spec пометить, что попадание узла в блок = значение оси `path` с `block=<имя>`.

## Действия
1. Добавить корневую секцию `axes` со списком из двух осей.
2. Описать тип `folder` и `root`.
3. Удалить отдельные `path` и `ref` из `types`.
4. Добавить `must_have_axes`/`may_have_axes` ко всем node-типам:
   - `document` — `must_have_axes: [path]` (target: folder | root).
   - `folder` — `must_have_axes: [path]` (target: folder | root).
   - `section` — `must_have_axes: [path]` (target: document | section).
   - `definition`/`example`/`field` — `must_have_axes: [path]` (target: section).
5. Проверить, что `check-integrity` после правки даст список нарушений (он будет красный до завершения axes-core-graph) — это ожидаемо, фиксируется в комменте к шагу axes-migrate-docs.

## Риски
Переписывание корневого `docs/Схема.yml` ломает существующий валидатор. До завершения axes-core-graph и axes-migrate-docs `check-integrity` будет давать ошибки — это нормальное промежуточное состояние, ловить его не пытаемся.
