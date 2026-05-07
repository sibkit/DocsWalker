# stg-0002 — industrial-readiness

## Цель
Переписать `docs/.docswalker/meta-schema.yml` под refs-модель. Тип задаётся через `name, title_source, text_required, path_targets, out_refs[]`; конструкции `axis_definition`, `field_definition`, `block_definition` удаляются; концепт `kind`, `direction`, `system`, `node`, `key_type`, `value_type` — уходит. Поднять `meta_schema_version` до 4.

## Файлы
`docs/.docswalker/meta-schema.yml` — полная замена.

## Действия
1. Переписать файл. Структура:
   - `meta_schema_version: 4`.
   - `primitive_types`: оставить только реально используемые в полях meta-schema (string, text, bool, enum, list).
   - `schema_root`: поля `description`, `types`. Constraints: уникальность имён типов, идентификация schema-файла по имени, зарезервированное имя `root`, встроенная связь `path`.
   - `type_definition`: поля `name`, `description?`, `title_source` (enum: filename/dirname/inline_key), `text_required` (bool), `path_targets` (list of string), `out_refs` (list of ref_def, optional).
   - `ref_def`: поля `name`, `target_types` (list of string), `cardinality` (enum: one/many), `required` (bool), `description?`.
2. Проверить grep'ом отсутствие axis-терминологии и старых конструкций (`axis`, `kind`, `fields`, `blocks`, `direction`, `system`, `node:`, `key_type`, `value_type`, `field_definition`, `block_definition`, `axis_definition`).

## Риски
- Соответствие `docs/Схема.yml` новой мета-схеме на этом шаге не проверяется — это R3 (Схема.yml после R1 в pre-axes состоянии и под v2 не валидна относительно v4; временное расхождение допустимо до R3).
- Поднятие meta_schema_version ломает любого читателя v2/v3. Допустимо: цепочка R6–R9 переписывает ядро.
