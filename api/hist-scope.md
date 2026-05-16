# Scope `hist`

`hist` — kernel-журнал всех изменений editable scope (`main`, `usage`,
`scheme`). Запись делает только kernel при успешной `tx`; LLM пишет
в hist не может (`tx scope=hist` отклоняется как `hist_read_only`).

Hist — **pure event log**. Каждый узел `hist/change` хранит только
**новое состояние** затронутого объекта (после применения op).
Полное состояние графа в произвольный момент восстанавливается
**replay'ем** всех hist-changes с начала до этой точки.

## Зачем такой

- Аудит: «когда создан этот узел / эта связь, когда изменён, когда
  удалён» — точечный read по hist scope.
- Rollback: kernel читает hist-changes исходной tx, вычисляет
  inverse-ops, применяет компенсирующую tx (см. [tx.md](tx.md)).
- Restoration: при необходимости полное состояние main / usage / scheme
  пересобирается из hist replay'ем.

## Hist self-contained относительно editable scope

Узлы hist не имеют исходящих links наружу. Затронутый объект
идентифицируется **структурными полями** `target.*`, описанными в
meta-schema. Это автоматически решает проблему «висячих» ссылок: при
удалении main-узла его id остаётся в hist как identity историчесих
событий, но никаких link-edges на удалённый узел в hist нет.

## Структура `hist/transaction`

Один узел `hist/transaction` на одну успешную tx.

```json
{
  "node.id": 200,
  "node.path": "tx/2026-05-14/tx_20260514T101530123Z_7F3A91C2",
  "node.value": {
    "commit_message": "...",
    "initiator": "llm"
  },
  "node.map_bindings": {
    "content": "hist/transaction",
    "tx_id": "tx_20260514T101530123Z_7F3A91C2",
    "date": "2026-05-14",
    "scope": "main"
  },
  "rollback_of": {
    "tx_id": "tx_20260513T185922401Z_AB7011FF"
  }
}
```

- `value.commit_message` — `commit_message` исходной tx (для kernel-
  генерируемой rollback здесь автоматический текст
  `"rollback of <original_commit_message>"`).
- `value.initiator` ∈ {`llm`, `kernel`} — кто инициировал tx. `kernel`
  стоит для компенсирующих rollback-tx.
- `map_bindings.tx_id` — opaque-id транзакции; повторяется во всех её
  hist-changes.
- `map_bindings.date` — ISO-8601 UTC date успешного применения.
- `map_bindings.scope` ∈ {`main`, `usage`, `scheme`} — какой scope
  изменён транзакцией (одна tx меняет ровно один scope).
- `rollback_of.tx_id` — опциональное поле верхнего уровня. Присутствует
  только если эта tx — компенсирующая обратная для указанной. LLM
  использует это поле, чтобы понимать связь между tx и её откатом.

## Структура `hist/change`

По одному узлу `hist/change` на каждое изменение объекта в tx.
`hist/transaction` связан с своими changes через link `has_change`
(внутри hist scope), `cardinality=one_to_many`, `required_for=["source"]`
для tx, изменивших хоть один объект.

### Node-change (create / update / move / delete)

```json
{
  "node.id": 201,
  "node.path": "tx/2026-05-14/tx_.../change-1",
  "node.value": {
    "title": "selectors",
    "value": "...",
    "path": "DocsWalker/api/selectors",
    "map_bindings": {
      "content": "documents/spec"
    }
  },
  "node.map_bindings": {
    "content": "hist/change",
    "op": "create",
    "scope": "main",
    "tx_id": "tx_...",
    "date": "2026-05-14",
    "state": "present"
  },
  "target": {
    "node.id": 42
  }
}
```

- `value` — **новое состояние** узла после применения op (для `delete`
  пустой объект).
- `map_bindings.op` ∈ {`create`, `update`, `move`, `delete`}.
- `map_bindings.scope` — где произошло изменение.
- `map_bindings.tx_id` — fk на транзакцию.
- `map_bindings.date` — повтор `transaction.date`.
- `map_bindings.state` ∈ {`present`, `deleted`} — итоговое состояние
  затронутого узла.
- `target.node.id` — id затронутого узла (структурное поле,
  определено в meta-schema, поддерживается селектором `read scope=hist`
  как first-class predicate).

### Link-change (link / unlink)

```json
{
  "node.id": 202,
  "node.path": "tx/2026-05-14/tx_.../change-2",
  "node.value": {},
  "node.map_bindings": {
    "content": "hist/change",
    "op": "link",
    "scope": "main",
    "tx_id": "tx_...",
    "date": "2026-05-14",
    "state": "present"
  },
  "target": {
    "link": {
      "name": "depends_on",
      "source.id": 42,
      "target.id": 17,
      "target.scope": "main"
    }
  }
}
```

- `value` для link / unlink — пустой объект (link не имеет своего
  body-состояния; всё описывается через target).
- `target.link.*` — structural fields, определённые в meta-schema.
  Селектор `read scope=hist` поддерживает их как first-class predicates.

## Schema hist scope

Hist-schema — часть meta-schema (`docs/.docswalker/meta-schema.json`).
Она не редактируется через `tx scope=scheme` — это runtime-протокол
kernel-а, версионируется с релизами DocsWalker.

Hist-schema задаёт:

- map `content` со значениями `hist/transaction` и `hist/change`;
- map `op` со значениями `create`, `update`, `move`, `delete`, `link`,
  `unlink`;
- map `scope` со значениями `main`, `usage`, `scheme`;
- map `tx_id`, `date`, `state` — для фильтра событий;
- link `has_change`: `hist/transaction` → `hist/change`, one_to_many,
  `required_for=["source"]` для транзакций с хотя бы одним change;
- структурные поля `target.node.id`, `target.link.name`,
  `target.link.source.id`, `target.link.target.id`,
  `target.link.target.scope`;
- опциональное поле `rollback_of.tx_id` у `hist/transaction`.

## Чтение hist

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "target.node.id": 42
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

Возвращает все hist-changes, описывающие изменения узла 42 во всех
scope. Чтобы сузить — добавляем `map_bindings.scope` или `map_bindings.op`.

«История link `(depends_on, 42, 17)`»:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "target.link.name": "depends_on",
          "target.link.source.id": 42,
          "target.link.target.id": 17
        },
        "include": ["map_bindings"],
        "max_tokens": 2000
      }
    }
  ]
}
```

«Все транзакции этой недели по scheme»:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "node.map_bindings": {
            "content": "hist/transaction",
            "scope": "scheme"
          },
          "match": {
            "regex": "^2026-05-1[0-7]$",
            "fields": ["map_bindings.date"]
          }
        },
        "include": ["value", "map_bindings"],
        "max_tokens": 4000
      }
    }
  ]
}
```

## Replay restoration

Полное состояние main / usage / scheme на любой момент восстанавливается:

1. Берётся пустой scope.
2. По порядку `tx_id` (хронологически) применяются hist-changes,
   относящиеся к этому scope.
3. Для каждого `op`:
   - `create` — создать узел с `target.node.id` и state из `value`.
   - `update` — обновить узел `target.node.id` до state из `value`.
   - `move` — обновить `path` и/или `map_bindings` узла
     `target.node.id` до state из `value`.
   - `delete` — удалить узел `target.node.id`.
   - `link` — создать link с tuple из `target.link.*`.
   - `unlink` — удалить link с tuple из `target.link.*`.

Replay гарантирует консистентность: id-пространство восстанавливается
точно (включая удалённые id), порядок tx сохраняется, scheme-changes
применяются раньше зависящих от них data-changes (потому что в hist
они идут в исходном хронологическом порядке).

## Rollback и hist

При rollback:

- kernel читает hist-changes исходной tx;
- для каждого вычисляет inverse;
- prior state берётся из **предыдущего** hist-change того же
  `target.node.id` (или из соответствующего link-change для link-events);
- если нет предыдущего change (узел был создан в исходной tx) — inverse
  это `delete`;
- inverse-ops применяются как обычная tx с собственным `tx_id` и своим
  набором hist-changes;
- новая `hist/transaction` помечается `rollback_of.tx_id = <исходный>`;
- исходные hist-changes остаются нетронутыми (история неприкосновенна).

LLM, чтобы найти rollback-тx для данной исходной:

```json
{
  "scope": "hist",
  "ops": [
    {
      "select": {
        "selector": {
          "node.map_bindings": {
            "content": "hist/transaction"
          },
          "node.value.rollback_of.tx_id": "tx_20260514T101530123Z_7F3A91C2"
        },
        "include": ["value", "map_bindings"]
      }
    }
  ]
}
```

(Селектор по `node.value.rollback_of.tx_id` поддерживается hist-схемой
как first-class фильтр — это плоское поле, не structured value query.)
