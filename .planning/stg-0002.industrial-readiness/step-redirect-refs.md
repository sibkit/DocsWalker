# stg-0002 — redirect-refs

## Цель
Удобная массовая операция переподшивки или разрыва входящих cross-refs одного узла (или path-subtree). Закрывает разрыв «LLM хочет удалить X, но на X висят входящие refs от 5 источников» — позволяет одной командой переподшить все источники на новый таргет или разорвать связи, без перечисления вручную.

## Файлы
- `src/DocsWalker.Core/Api/WriteApi.cs`:
  - `RedirectRefs(int fromId, int? toId, string? refName, bool fromSubtree, bool unlink)` — единый метод, диспетчирует по комбинации параметров:
    - `fromSubtree=false`: ищем входящие cross-refs в `fromId`.
    - `fromSubtree=true`: ищем входящие cross-refs во **все** узлы path-subtree(`fromId`) от **узлов снаружи** subtree.
    - `refName != null`: фильтр только по этому имени связи.
    - `unlink=true`: удаление refs, `toId` игнорируется.
    - иначе: переподшивка на `toId`.
  - Возвращает список затронутых правок: `[{source_id, ref_name, old_target, new_target | null}]`.
  - Tree-refs (`Tree != null`) **исключены** из переподшивки этой командой — для них есть `move_node` со своей семантикой (per-scope, с проверкой циклов). RedirectRefs работает только с обычными cross-refs.
- `src/DocsWalker.Core/Api/Transaction.cs` — операция `redirect_refs` с теми же параметрами.
- `src/DocsWalker.Cli/Cli/Handlers/WriteHandlers.cs` — handler `redirect-refs`. Параметры:
  - `--from=<id>` — обязателен (один из режимов).
  - `--from-subtree=<id>` — альтернатива `--from`. Ровно один из двух.
  - `--to=<id>` — обязателен, если не задан `--unlink`.
  - `--unlink` — флаг, разрыв связей вместо переноса.
  - `--name=<ref_name>` — опциональный фильтр.
- `src/DocsWalker.Cli/Cli/Commands.cs` — регистрация `redirect-refs`.
- `docs/DocsWalker.yml` — раздел «Операции записи»: новая команда `redirect_refs` с описанием режимов.

## Действия
1. Зафиксировать команду в `docs/DocsWalker.yml`.
2. Реализовать `WriteApi.RedirectRefs`. Внутри:
   - Валидация: `fromId` существует; `toId` существует (если не unlink); `toId` совместим по target_types с каждой переподшиваемой ref'ой (если несовместим — ошибка `target_type_mismatch` с указанием конкретной ref).
   - Сбор источников: обратный индекс входящих cross-refs (нужен из R14/delete-nodes — переиспользовать).
   - Атомарное применение: сборка набора правок update-ref, прогон через AtomicWriter.
3. Подключить в Transaction и CLI.
4. Тесты.

## Тесты
- `tests/.../WriteApiRedirectRefsTests.cs`:
  - `--from --to` с одной входящей cross-ref — переподшита, источник обновлён в YAML.
  - `--from --to` с пятью входящими разных имён — все переподшиты.
  - `--from --to --name=foo` — переподшиты только refs с именем foo, остальные не тронуты.
  - `--from-subtree --to` — переподшиты входящие refs от узлов снаружи subtree (не внутренние, чтобы не плодить циклы внутри subtree).
  - `--from --unlink` — refs удалены из источников.
  - `--to` несовместим по target_types — `target_type_mismatch`.
  - Tree-refs не трогаются — путь к этому: создать узел с `tree:strategic` ref на `from`, позвать redirect-refs --from --to, убедиться, что tree-ref остался прежним.
- `tests/.../TransactionRedirectRefsTests.cs` — то же внутри transaction.

## Риски
- При переподшивке нужно проверить, что не создаются дубликаты в out_refs (если у источника уже была ref на `to` с тем же именем) — что делать: оставить дубликат, дедуплицировать, ошибка? Решение: **дедуплицировать молча**, в результате-отчёте указать `dedup: true` для таких записей.
- `--from-subtree` дорогой при больших subtree — приемлемо, такая операция редкая.
- Совместимость с tree-refs: явно исключаем — задокументировать в hint, если LLM попробует `--name=path`, отказ с hint «используй move-node для tree-refs».
