# stg-0002 — R13 tree-scopes-schema

## Цель
Переписать `docs/Схема.yml` под мета-схему v5 из R12: добавить декларацию `trees:` и перевести все path-связи на формат `tree: path` (вместо снятого `path_targets`). Заодно — заложить обязательную связь `examples` у типа `rule` (в рамках следующего шага миграция docs, но в Схеме декларация уже здесь).

## Правки docs/Схема.yml

**На верхнем уровне** добавить:
```yaml
trees:
  - name: path
    description: |
      Дерево хранилища. Каждый узел кроме root имеет ровно один path-ref,
      указывающий на его родителя. Только это дерево используется ядром
      для физического размещения узлов в файловой системе и для каскадных
      проверок при удалении.
```

**Для каждого типа** (folder, document, section, statement, rule, may_rule, note, definition, example, llm_hint):
- Удалить поле `path_targets`.
- В `out_refs` (создать список, если его нет) добавить **первой записью** связь `path`:
  ```yaml
  - name: path
    tree: path
    target_types: [<те же значения, что были в path_targets>]
    description: Размещение узла в дереве хранилища.
  ```
  Без `cardinality` и `required` — они подразумеваются `tree: path`.

**Для типа `rule`** дополнительно добавить в `out_refs`:
```yaml
- name: examples
  target_types: [example]
  cardinality: many
  required: true
  description: Примеры применения правила. Минимум один.
```

Эта связь — обычная горизонтальная (без `tree:`), потому что example — самостоятельный атом секции, может иллюстрировать несколько правил.

## Действия
1. Открыть `docs/Схема.yml`.
2. Добавить декларацию `trees:` сразу после `description:`.
3. По каждому из 10 типов — заменить `path_targets` на запись `path` в `out_refs` с тем же набором target_types.
4. У `rule` — добавить `examples` ref как описано.
5. Сверить: для каждого типа кроме root в out_refs есть запись `name: path, tree: path`.

## Сборка/тесты
Шаг — только правка YAML. Код всё ещё ломается (валидатор не понимает `tree:`/`trees:`), R14 это исправит.

## Риски
- Если в Схеме где-то осталось упоминание `path_targets`, валидатор мета-схемы v5 ругнётся. Простой grep по файлу после правки.
- Связь `examples → example` у `rule` ещё не выполнена в живых docs (там есть rule без examples). Шаг `rule-requires-example` отдельный — он мигрирует docs. До тех пор `check-integrity` будет падать на этих rule.
