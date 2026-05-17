# stg-0002 — refs-model-read-api

## Цель
Закрыть R7-промежуточный red-build: переписать `Api/ReadApi.cs` и
`Api/ReadApiJson.cs` под refs-модель. Read-API возвращает узлы в форме
5 концептуальных полей (`id`, `type`, `title`, `text`, `out_refs`) и единый
формат связей `{name, target_id}` без axis/origin-фильтров. Дополнительно
чинится R6-остаток в `Validation/MetaSchemaCheck.cs` — отсутствует
`using DocsWalker.Core.Graph;` для имени `Node`. После R8 проект
`DocsWalker.Core` собирается без ошибок.

## Файлы

**Переписаны:**
- `src/DocsWalker.Core/Api/ReadApi.cs` — Node-видимая часть = 5 полей. Запись
  `RefView` = `{name, target_id}`. `GetRefs(id, name?)` без origin/type-фильтра
  старого вида. `Search` идёт только по `Text`. Удалены
  `TryParseOrigin` / `OriginToString`.
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — JSON-форма узла: ровно
  `id, type, title, text, out_refs`. `RefSet` → `{in: [{name,target_id}], out: [...]}`.
  Удалены `FieldsToJson` / `BlocksToJson` / origin-сериализация.

**Минорно:**
- `src/DocsWalker.Core/Validation/MetaSchemaCheck.cs` — добавить
  `using DocsWalker.Core.Graph;` (R6-остаток, без него имя `Node` не разрешается).

**Не трогаются (R9/далее):**
- `src/DocsWalker.Cli/*` — handlers и парсер на старой read-API; R9.
- `tests/DocsWalker.Tests/*` — переписываются после R9 отдельным шагом.

## Принятые решения

1. **`parent_id` не дублируется в JSON узла.** Он эквивалентен
   `out_refs["path"][0]` — явное поле было бы избыточным и расходилось бы с
   принципом «5 полей».
2. **Origin-фильтр уходит.** В refs-модели понятий explicit/system/default
   нет. `GetRefs` принимает опциональный фильтр по имени связи (`string? name`).
3. **`Search` индексирует только `Text`.** `Title` — path-сегмент 1-2 слова
   и в индексацию не попадает (как и раньше для структурных ключей).
4. **`MapNode` / `NodeSubtree` остаются.** Это удобные view'хи поверх
   path-связи (дерево по `path`); не пересекаются с raw-полями узла и нужны
   CLI-операциям `get-map` / `get-by-path`.
5. **`out_refs` в JSON — объект `{name: [ids]}`.** Зеркалит in-memory
   `IReadOnlyDictionary<string, IReadOnlyList<int>>`. Связь `path` тоже видна —
   как обычная пара ключа/массива.

## Действия

1. Добавить `using DocsWalker.Core.Graph;` в `Validation/MetaSchemaCheck.cs`.
2. Переписать `Api/ReadApi.cs`:
   - `RefView(string Name, int TargetId)`;
   - `GetRefs(int id, string? name = null)` — итерация `node.OutRefs` для out
     и `_graph.GetInRefs(id)` для in;
   - `Search` — substring (case-insensitive) по `node.Text`;
   - убрать `TryParseOrigin` / `OriginToString`.
3. Переписать `Api/ReadApiJson.cs`:
   - `NodeToJson` → `{id, type, title, text, out_refs}`;
   - `out_refs` → `JsonObject` с массивом id'ов под каждым именем связи;
   - `SubtreeToJson` — то же + `children`;
   - `RefSetToJson` → `{in:[{name,target_id}], out:[{name,target_id}]}`;
   - удалить `FieldsToJson` / `BlocksToJson` / sub-helpers.
4. `dotnet build src/DocsWalker.Core` — должен быть зелёным.
   `DocsWalker.Cli` и `DocsWalker.Tests` остаются красными — это R9 и
   последующий шаг по тестам.

## Риски

- Тесты, использующие старые поля JSON-узла (`parent_id`, `blocks`, `fields`,
  `inline_value`), упадут громче — это отложенная стоимость R8, закрывается
  на этапе тестов (отдельный шаг после R10).
- `MapNode.Children` и `GetChildren` опираются на индекс по path-родителю
  (`Graph._byParent`), уже построенный в R6; никаких новых требований к графу
  R8 не добавляет.
