# stg-0002 — extra-integrity-checks

## Цель
Расширить `Validator` тремя проверками, которые сейчас не выполняются и при которых граф может тихо разъехаться: согласованность `parent_id` с `ChildrenBlock` родителя, покрытие sequence-счётчика максимальным id графа, отсутствие дубликатов id в одном `ChildrenBlock`.

## Файлы
`docs/DocsWalker.yml` — раздел «Контракт валидации» (#16): добавить три новых правила и коды ошибок (`parent_block_inconsistent`, `sequence_underflow`, `duplicate_child_in_block`).
`src/DocsWalker.Core/Validation/ParentBlockCheck.cs` — новый файл: проверка, что у каждого узла с `ParentBlockName` есть встречная запись в `ChildrenBlock` родителя, и наоборот.
`src/DocsWalker.Core/Validation/SequenceCheck.cs` — новый файл: проверка `sequence ≥ max(id)`.
`src/DocsWalker.Core/Validation/UniqueCheck.cs` — добавить проверку `duplicate_child_in_block`.
`src/DocsWalker.Core/Validation/Validator.cs` — подключить новые проверки в `Validate`; передать значение sequence в `SequenceCheck` (требуется новый параметр или загрузка через `WriteContext`-подобный объект).
`src/DocsWalker.Core/Api/WriteApi.cs` — передать текущее значение sequence в валидатор.
`tests/DocsWalker.Tests/ValidatorTests.cs` — тесты на каждый новый код ошибки.

## Действия
1. В `docs/DocsWalker.yml` зафиксировать три новых правила и коды ошибок.
2. Реализовать `ParentBlockCheck`: проход по всем узлам, для каждого с `ParentId` и `ParentBlockName` — проверка, что родитель содержит этот id в указанном блоке; обратная проверка по всем `ChildrenBlock`.
3. Реализовать `SequenceCheck`: чтение sequence-значения, сравнение с max(id) графа; ошибка, если sequence меньше.
4. Расширить `UniqueCheck`: для каждого `ChildrenBlock` — проверка уникальности id внутри списка.
5. Пробросить sequence в `Validator` (новый параметр конструктора или метода `Validate`).
6. Покрыть тестами: построить in-memory графы с расхождениями всех трёх типов.

## Риски
`SequenceCheck` требует, чтобы валидатор знал значение sequence — это новый параметр. Старые вызовы валидатора (если есть в тестах без sequence) надо обновить. На write-пути значение уже доступно в `WriteApi`.
