# stg-0002 — axes-meta-schema

## Цель
Расширить мета-схему концептом **«ось»** (axis) — единым первичным отношением между узлами. Только описание формата schema-файла; правки самой Схемы проекта и кода — в следующих шагах.

## Файлы
`docs/.docswalker/meta-schema.yml` — корневая правка:
- Новый раздел `axis_definition` — форма одной оси (`name`, `direction`, `kind: system|default|explicit`, `cardinality: one|many`, `target_types?`, `description?`).
- В `schema_root.fields` добавить поле `axes` (list of `axis_definition`, required: false) рядом с `types`.
- В `type_definition.fields` добавить поля `must_have_axes` (list of axis_ref, required: false) и `may_have_axes` (list of axis_ref, required: false).
- `type_kinds` — оценить, остаётся ли `ref_type` отдельным kind или уходит (он становится `axis` верхнего уровня). Скорее всего, `ref_type` из `type_kinds` удаляется; типы переходят в `axes`.
- Удалить из `type_definition.fields` поля `direction` и `system` (они переезжают в `axis_definition`); подправить constraints.

## Действия
1. Сформулировать `axis_definition` блок (`fields` + `constraints`).
2. Добавить `axes` в `schema_root.fields`.
3. Добавить `must_have_axes` / `may_have_axes` в `type_definition.fields` со ссылкой на имя оси.
4. Убрать ref_type-специфичные поля из `type_definition` (direction, system) и сам `ref_type` из `type_kinds`.
5. Обновить `meta_schema_version` (поднять до 3).
6. Сверить с правилами оформления docs/ (одна строка на значение, snake_case имён полей).

## Риски
Мета-схема — корень системы валидации. Любая правка ломает все следующие шаги, если описана неполно. Контракт `axis_definition` нужно зафиксировать так, чтобы `path` (system, child_to_parent, cardinality=one) и `ref` (explicit, from_to, cardinality=many) описывались одной формой без спецкейсов.
