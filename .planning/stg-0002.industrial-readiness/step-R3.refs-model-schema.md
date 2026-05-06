# stg-0002 — industrial-readiness

## Цель
Переписать `docs/Схема.yml` под refs-модель. Зафиксировать набор типов узлов проекта, их `path_targets` и `out_refs`. Убрать тип `field`, тип `reference`, оба `kind: ref_type` (`path`, `ref`), все примитивы как user-types, конструкции `kind`/`fields`/`blocks`/`key_type`/`value_type`/`direction`/`system`/`title_format`. Cross-refs не объявлять (вариант A из обсуждения — named refs появятся на R5 по факту).

## Файлы
`docs/Схема.yml` — полная замена.

## Действия
1. Переписать файл согласно решению «Title как path-сегмент» из стратегии. Список типов:
   - `folder` — `title_source=dirname`, `text_required=false`, `path_targets=[root, folder]`, без `out_refs`.
   - `document` — `title_source=filename`, `text_required=true` (text = однострочное описание документа), `path_targets=[root, folder]`, `out_refs={sections: many of section}`.
   - `section` — `title_source=inline_key`, `text_required=false`, `path_targets=[document]`, `out_refs={statements, rules, may_rules, notes, definitions, examples, llm}` — каждая cardinality=many, target_types — соответствующий атом.
   - `statement`, `rule`, `may_rule`, `note`, `definition`, `example`, `llm_hint` — `title_source=inline_key`, `text_required=true`, `path_targets=[section]`, без `out_refs`.
2. У `definition` title — определяемый термин, text — содержание; у `example` title — подпись, text — содержимое примера; у атомов (statement/rule/may_rule/note/llm_hint) title — 1–2-словное сжатие text. Это фиксируется в `description` каждого type_definition.
3. Проверить grep'ом отсутствие axis-, field- и kind-терминологии в файле (`axis`, `kind:`, `fields:` на уровне типов, `blocks:`, `direction:`, `system:`, `node:`, `key_type`, `value_type`, `title_format`, `ref_type`, `primitive`).

## Риски
- Соответствие `Схема.yml` ↔ существующих `docs/*.yml` после R3 будет нарушено (старый формат блоков). Это устраняется на R5 (миграция docs к атомам).
- В Схеме нет cross-refs. Если на R5 окажется, что какой-то живой `{ref: id}` имеет смысл — добавить named ref в Схему придётся отдельной правкой того же шага.
- В Схеме нет `path`-типа отдельно: `path` — встроенная связь ядра, контролируется только через `path_targets` каждого типа.
