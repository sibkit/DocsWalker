# stg-0002 — industrial-readiness

## Цель
Перевести три живых docs-файла в атомарный формат refs-модели. Каждый bare-string bullet (statement, rule, may_rule, note, llm) становится отдельным узлом с id, type, title, text — серилизованным как `"(#id) Title": text`. На уровне document — переименовать `description: → text:` и `content: → sections:` (под Схему R3). Удалить устаревшие cross-refs `{ref: id}` (Q2 вариант A — перевод их в named refs не нужен, так как ни один из живых refs не несёт критической семантики, не выраженной соседним текстом). Поднять `sequence.txt` до нового максимального id.

## Файлы
`docs/Стек.yml` — переименование document-полей + атомизация 9 bullets.
`docs/Правила оформления.yml` — переименование document-полей + атомизация ~30 bullets + удаление устаревшего: пример (#56) Field, целая section (#58) Перекрёстные ссылки (формат описывается в DocsWalker.yml; в Правилах он избыточен после атомизации), все три блока `out_refs: [{ref: ...}]`. Обновить рулу про структурные ключи под новую вокабулярию (без `fields`, `content`; с `text`, `sections`).
`docs/DocsWalker.yml` — переименование document-полей + атомизация ~60 bullets.
`docs/.docswalker/sequence.txt` — поднять до фактического max id после миграции.

## Действия
1. **Стек.yml.** Заменить `description:`/`content:` на `text:`/`sections:`. В каждом блоке `rules:` сменить bare-string элементы на `"(#id) Title": text` (Title — 1–2-словное сжатие text, id берётся последовательно от 87).
2. **Правила оформления.yml.**
   - Заменить `description:`/`content:` на `text:`/`sections:`.
   - Удалить пример (#56) Field.
   - Удалить целиком section (#58) Перекрёстные ссылки — её содержимое (формат сериализации refs) уже описано в DocsWalker.yml/(#78) Атомизация.
   - Удалить все три блока `out_refs: [{ref: ...}]` (в (#47), (#58 — уже удалится с секцией), (#62)).
   - В (#51) Язык ключей обновить список структурных ключей: убрать `description, content, fields, may_rules` (часть стала именами блоков в out_refs section, часть исчезла); добавить `text, sections, target_types, cardinality, path_targets, text_required` (вокабулярия refs-модели и Схемы).
   - В (#57) Порядок полей mapping-узлов: убрать упоминание типа `field` (его нет), оставить только document.
   - Атомизировать все statements/rules с id от 96.
3. **DocsWalker.yml.** Заменить `description:`/`content:` на `text:`/`sections:`. Атомизировать все bare-string bullets (statements/rules/notes) с id от очередного значения после Правил.
4. **sequence.txt** — записать фактический максимальный id после миграции.
5. Grep'ом проверить отсутствие `ref_type`, `add_ref_type`, `axis`, `field_definition`, `block_definition`, `parent_block`, `ChildrenBlock` в трёх мигрированных файлах. И что нет голых bullets (статей в `statements:`/`rules:`/`notes:`/`may_rules:`/`llm:` без id+title).

## Риски
- Title для атомов придумываются «на глаз» — где-то получится тяжеловато, понадобится редактирование. Допустимо: в R6+ при работе ядра title-сегменты могут быть пересмотрены отдельно.
- Удаление section (#58) убирает явное описание cross-refs в Правилах. Описание остаётся в DocsWalker.yml/(#78). Если в будущем понадобятся reusable cross-refs — добавляются вместе с named refs в Схеме.
- ids существующих sections/definitions/examples сохраняются; ids удалённых definitions/examples становятся пробелами в sequence (это допустимо — sequence монотонна).
- После R5 файлы валидируются по Схеме R3 (контракты типов). Полная валидация будет работать только после R6 (новое ядро); до тех пор расхождения видны только глазами.
