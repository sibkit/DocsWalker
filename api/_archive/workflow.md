# Workflow LLM

Рекомендуемый LLM workflow:

1. Сначала прочитать quickstart и карту методов из MCP tool descriptions.
2. Затем `usage select` для чтения нужных инструкций, examples и schemas.
3. Затем `query select` для чтения нужного project-контекста.
4. Для существующих project nodes, которые write меняет, удаляет или структурно
   затрагивает, сохранить state `read_id` и передать его в `tx.read_ids`.
5. Если применимый usage rule содержит `requires_project_value_read=true`,
   выполнить full read соответствующих project nodes и сохранить value
   `read_id`.
6. Перед назначением `map_bindings` прочитать через `usage` map nodes для каждой
   назначаемой map с `include=["value"]` и сохранить полученные `read_id`.
7. Перед созданием link прочитать через `usage` link node для нужного link name
   с `include=["value"]` и сохранить полученный `read_id`.
8. Затем `tx` для записи с `commit_message`.
9. Если `tx` вернул `read_required`, выполнить указанные reads, получить
   `read_id`, применить инструкции и повторить или перестроить `tx` с
   `read_ids`.
10. Если `tx` вернул `invalid_read_id`, LLM должна заново прочитать
   указанные узлы, переоценить изменение на новом состоянии и отправить новый
   `tx`.
11. Если после ответа `tx` LLM видит ошибку в примененном изменении, она
   вызывает новый `tx` с `rollback` operation, полученным `tx_id` и новым
   `commit_message`.
12. Если `tx` с `rollback` operation вернул `rollback_conflict`, LLM читает
   `details.conflicts[]` и решает, делать ли новую компенсирующую `tx`.
13. Для просмотра истории и подробностей транзакций LLM вызывает `hist`.

Перед `tx` с wildcard `move`, `link` или `unlink` LLM должна через `query` явно
понять размер выбранного набора и нужные ids. Перед `update` LLM должна знать
один точный `id`; перед `delete` - точный список `ids`.

Перед `update`, `delete`, `move`, `link` и `unlink` LLM должна иметь state
`read_id` существующих узлов, которые операция меняет или структурно
затрагивает. LLM не должна full-read-ить project value только ради записи, если
применимый rule не содержит `requires_project_value_read=true`. Если value gate
есть, LLM должна выполнить full read через `query` или отправить `tx` и
выполнить готовые read requests из `read_required`.

Перед массовым `move`, `link` или `unlink` LLM должна явно понимать размер
изменения и указывать `expected_count`. Если модель сомневается в размере
выборки или token budget, она должна сузить `query`.

Перед любым `create` или `move`, который назначает `map_bindings`, LLM должна
через `usage` прочитать `usage/map` node для каждой назначаемой map, понять
description, ветки и required rules, затем передать полученные ids в
`tx.read_ids`. `read_id` возвращается только при full read, когда `include`
содержит `value`.

Перед любым `link` LLM должна через `usage` прочитать `usage/link` node для
этого `name`, понять `description`, source/target направление и constraints,
затем передать полученный `read_id` в `tx.read_ids`.

`rollback` operation внутри `tx` предназначена для немедленного отката указанной
транзакции по полученному `tx_id`, если LLM после `tx` видит ошибку в
примененном изменении. Перед записью все равно применяются `expected_count` для
массовых операций, state preconditions, schema-defined read gates и server-side
validation.
