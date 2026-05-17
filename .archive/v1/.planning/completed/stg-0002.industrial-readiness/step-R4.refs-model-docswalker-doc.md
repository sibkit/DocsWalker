# stg-0002 — industrial-readiness

## Цель
Переписать содержимое `docs/DocsWalker.yml` под refs-модель и новую терминологию (out_refs, без axis, без ref_type, без default-блоков). Описать: модель данных (Node = 5 полей), API чтения и записи под новую модель, формат YAML смысловых узлов и атомизацию bullet-блоков. CLI-примеры обновляются под новую сигнатуру операций.

YAML-формат файла на этом шаге **остаётся пре-axes** (statements/rules/notes как голые строки, definitions/examples как single_key_mapping). Перенос файла в атомарный формат выполняется на R5.

## Файлы
`docs/DocsWalker.yml` — содержимое переписывается по разделам.
`docs/.docswalker/sequence.txt` — поднять до значения максимального нового id.

## Действия
1. **Раздел «Модель данных» (#7).** Переписать definitions: (#8) узел — 5 полей (id, type, title, text, out_refs); (#9) связь — пара (имя_связи, target_id), хранится в out_refs источника. Удалить definitions (#10) тип связи, (#11) системный тип, (#12) прикладной тип, (#13) связь по умолчанию. Добавить новые definitions: out_refs, in_refs (вычисляемое), path (встроенная связь), root (синглтон id=0). Переписать rules под новую модель.
2. **Раздел «Уровни схемы» (#4).** Поднять упоминание meta-schema до v4.
3. **Раздел «Идентификация и адресация» (#14).** Уточнить формат YAML смысловых узлов (`"(#id) title": value`); путь = цепочка path-связей.
4. **Раздел «Контракт валидации» (#16).** Переписать список проверок: meta_schema, schema (path_targets, out_refs контракт типа, cardinality, required), refs (имя связи объявлена; target существует; target_types соблюдается; нет циклов по path), unique (id глобально; title уникален среди siblings одного типа), sequence, style. Убрать упоминания parent_block/ChildrenBlock.
5. **Раздел «Операции чтения» (#17).** Переписать definitions:
   - (#18) list_documents — список documents и folders.
   - (#22) get_nodes — возвращает {id, type, title, text, out_refs}.
   - (#24) get_refs — возвращает in[]/out[] с единым форматом {name, target_id} (или {name, source_id} для in).
   - (#25) get_in_refs — то же без out.
   - Удалить упоминания origin (explicit/system/default).
6. **Раздел «Операции записи» (#27).** Переписать definitions:
   - (#28) create_node — параметры type, title, text? (если text_required), значения всех required out_refs (включая path).
   - (#29) update_node — правка title/text. Связи меняются через create_ref/delete_ref.
   - (#31) create_ref — from_id, name (любая объявленная в типе источника), to_id.
   - (#32) delete_ref — то же удаляет.
   - (#72) move_node — меняет значение out_refs[path] узла.
   - Удалить (#33) add_ref_type. Расширение Схемы (новый тип, новый named ref) делается правкой docs/Схема.yml напрямую — отдельной API-команды нет (на R7 решим, нужна ли add_type / add_ref_def).
7. **Раздел «CLI-интерфейс» (#35).** Переписать examples под новую сигнатуру: create-node с --path и параметрами других required связей; create-ref --from-id --name --to-id; move-node --id --new-path. Удалить пример (#42) add-ref-type. Обновить (#43) create-ref на конкретное named-имя (без generic ref).
8. **Новый раздел «Атомизация и формат YAML смысловых узлов» (id 78).** Описать: дихотомия структурные/смысловые типы; формат `"(#id) title": value`; форма value диктуется контрактом типа (text vs mapping с out_refs); title — 1–2-словное сжатие text. Добавить definitions с этой семантикой и examples (section с атомами; leaf-атом).
9. **sequence.txt** — поднять до 86 (или фактического max нового id после правки).
10. Grep'ом проверить отсутствие ref_type, axis, default_axis, system_axis, parent_block, ChildrenBlock в файле.

## Риски
- ids удалённых definitions (#10, #11, #12, #13, #33, #42) становятся «дырами» в sequence. R5 при ренумерации это устранит.
- Файл остаётся в pre-axes YAML-формате (bare-string bullets) до R5. Validator в момент R6+R8 будет валидировать его уже под новой схемой — на R5 файл должен быть переписан в атомарный формат до того, как ядро начнёт строго валидировать.
- Новые id (78–86) живут в sequence.txt бок о бок со старыми (1–73). R5 пересчитает счётчик под новый max.
