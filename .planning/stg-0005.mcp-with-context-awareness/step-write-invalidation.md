# stg-0005 — write-invalidation

## Цель
Write-команды после применения изменений удаляют затронутые id из seen-set всех активных sessions. Без этого LLM может видеть устаревший узел через placeholder.

## Файлы
`src/DocsWalker.Core/Api/WriteApi.cs` — обработчики `create-node`, `update-node`, `delete-nodes`, `move-node`, `create-ref`, `delete-ref`, `redirect-refs`.
`src/DocsWalker.Core/Api/Transaction.cs` — после применения всех op'ов одним проходом инвалидировать все touched id.
`src/DocsWalker.Core/Sessions/SessionState.cs` — метод `RemoveFromAll(ids)` (объявлен в шаге session-state-core, здесь — реальное использование).

## Действия
1. Каждая write-команда после успешного `Commit` собирает список затронутых id:
   - `create-node` — новый id (может пригодиться, если до сессии его уже видели через get-nodes по другому id-параметру);
   - `update-node` — id обновляемого узла;
   - `delete-nodes` — все удалённые id;
   - `move-node` — id переносимого узла (его out_refs.path меняется);
   - `create-ref` / `delete-ref` — id источника (его out_refs меняется);
   - `redirect-refs` — id всех источников переподшитых cross-refs (их out_refs меняются).
2. Вызов `sessionState.RemoveFromAll(ids)` — проходит по всем sessions in-memory, чистит touched id, помечает затронутые sessions как dirty. Flush на disk — лениво, на следующем shutdown или при явной операции flush.
3. `transaction` накапливает touched id всех вложенных op'ов и инвалидирует одним проходом по sessions в конце транзакции.
4. Read-команды seen-set через invalidation не модифицируют (только через MarkSeen при заполнении).

## Риски
- Лень flush'а: если сервер crash'нет до graceful shutdown, persisted sessions могут содержать удалённые id. Безопасно — на следующем startup hash-detection заметит изменения docs/ (если они были сохранены) и выкинет всё; если нет — старый seen вернётся, но invalidated id всё равно станут просто id, которых нет в графе → следующий read получит `node_not_found`, LLM поправится.
