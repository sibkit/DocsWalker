# stg-0002 — R12 tree-scopes-meta-schema

## Цель
Добавить в мета-схему понятие именованных деревьев (tree-scopes). Связь может объявлять `tree: <scope_name>` — тогда она участвует в named-дереве с автоматическими инвариантами `cardinality=one + required=true` (не указываются явно). `path` перестаёт быть «встроенной связью с особым полем `path_targets`» — становится обычной tree-связью со scope-name `path`.

## Правки docs/.docswalker/meta-schema.yml

**Поднять `meta_schema_version` до 5.**

**На уровне `schema_root.fields`** — добавить поле:
```yaml
- name: trees
  type: list
  of: tree_definition
  required: true
  description: |
    Декларация именованных деревьев scope'а поверх графа. Каждое дерево
    образовано связями с `tree: <name>` в любых типах. Дерево `path`
    обязательно объявляется и используется ядром как физическое размещение
    узлов.
```

В `schema_root.constraints` добавить:
- `'Дерево с именем path обязательно присутствует в trees: оно соответствует встроенному scope хранилища.'`

**Новый блок `tree_definition`** (на одном уровне с `type_definition`/`ref_def`):
```yaml
tree_definition:
  description: Объявление именованного дерева. Дерево образовано всеми ref_def с tree=<name>.
  fields:
    - name: name
      type: string
      required: true
      description: Имя scope'а (snake_case). Используется в ref_def.tree и в API (get_subtree, move_node, …).
    - name: description
      type: text
      required: false
      description: Назначение дерева.
  constraints:
    - 'Имя ''path'' зарезервировано за встроенным scope хранилища.'
    - Имена деревьев уникальны в пределах schema-файла.
```

**В `type_definition`:**
- **Удалить** поле `path_targets` целиком (включая constraint о его пустоте). Допустимые цели path-связи теперь описываются через target_types у самой связи `path` в `out_refs`.
- В `out_refs` теперь допустима связь с `name: path` — снять constraint `'Имя ''path'' зарезервировано — не используется в out_refs.'` и заменить на: `'Связь name=path обязана иметь tree=path; присутствует у каждого type_definition кроме типа root.'`

**В `ref_def`:**
- Добавить поле:
  ```yaml
  - name: tree
    type: string
    required: false
    description: |
      Имя дерева (scope) из schema_root.trees, в котором участвует эта связь.
      Если задан — связь автоматически cardinality=one + required=true; конвенция
      направления: child → parent.
  ```
- `cardinality` и `required` сделать `required: false` на уровне поля (раньше были обязательны). Добавить constraints:
  - `'Если задан tree — указывать cardinality и required запрещено: они подразумеваются (cardinality=one, required=true).'`
  - `'Если tree не задан — cardinality и required обязательны.'`

В `ref_def.constraints` добавить:
- `'Поле tree (если задано) ссылается на существующее имя в schema_root.trees.'`

## Действия
1. Открыть `docs/.docswalker/meta-schema.yml`, поднять версию.
2. Добавить блок `tree_definition` и поле `trees` в schema_root.
3. Удалить `path_targets` из type_definition; обновить constraints типа.
4. Добавить поле `tree` в ref_def; перевести `cardinality`/`required` в условно-обязательные; добавить constraints согласованности.
5. Прогнать линтер мета-схемы (если есть) или вручную проверить, что YAML валиден и нет дублирующих полей.

## Сборка/тесты
Этот шаг — только правка YAML мета-схемы. Код пока ломается, потому что ядро ещё парсит старый формат — это будет исправлено в R14. Промежуточная красная сборка допустима (auto-режим, без обратной совместимости).

## Риски
- Существующая `docs/Схема.yml` валидируется по старой мета-схеме v4. После этого шага она тоже невалидна — будет переписана в R13. До завершения R13 любая попытка `load` падает на валидации схемы. Это ожидаемо.
- `ref_def.cardinality/required` становятся условно-обязательными. Реализация валидатора в R14 должна явно проверять обе ветки (с tree / без tree), иначе регрессия.
