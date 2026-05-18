#!/usr/bin/env python3
"""Builds tx scope=scheme JSON with declared maps/links for V2 dogfood-config."""
import json
import sys


def map_op(owner_scope: str, name: str, description: str, branches: dict,
           required: bool = False) -> dict:
    content = {
        "description": description,
        "branches": branches,
        "required": required,
        "required_when": None,
    }
    return {
        "create": {
            "path": f"{owner_scope}/{name}",
            "set": {
                "title": name,
                "content": json.dumps(content, ensure_ascii=False),
                "map_bindings": {
                    "category": "map",
                    "owner_scope": owner_scope,
                    "map": name,
                },
            },
        }
    }


def link_op(owner_scope: str, name: str, description: str,
            from_constraint: dict | None = None,
            to_constraint: dict | None = None,
            cardinality: str = "many_to_many",
            required_for: list[str] | None = None) -> dict:
    content = {
        "description": description,
        "from": from_constraint if from_constraint is not None else {},
        "to": to_constraint if to_constraint is not None else {},
        "cardinality": cardinality,
        "required_for": required_for if required_for is not None else [],
    }
    return {
        "create": {
            "path": f"{owner_scope}/{name}",
            "set": {
                "title": name,
                "content": json.dumps(content, ensure_ascii=False),
                "map_bindings": {
                    "category": "link",
                    "owner_scope": owner_scope,
                    "link_name": name,
                },
            },
        }
    }


def flat_branches(*names: str) -> dict:
    return {n: {} for n in names}


# ============================================================
# Main scope maps (6)
# ============================================================
main_maps = [
    map_op("main", "категория",
           "Классификация main-узлов по типу: документы, задачи, "
           "заметки или legacy V1.",
           {
               "документы": flat_branches("спека", "решение", "инвариант", "гайд"),
               "задачи": flat_branches("бэклог", "запланирована", "активна",
                                      "заблокирована", "отложена", "сделана",
                                      "отменена"),
               "заметки": flat_branches("исследование", "вопрос", "чейнджлог", "идея"),
               "legacy": flat_branches("v1"),
           },
           required=False),
    map_op("main", "подсистема",
           "Принадлежность узла к подсистеме DocsWalker.",
           flat_branches("core", "kernel", "mcp", "cli", "tests", "spec",
                        "tooling", "infra")),
    map_op("main", "статус",
           "Статус жизненного цикла узла.",
           flat_branches("черновик", "действует", "устарел", "архив"),
           required=False),
    map_op("main", "адресат",
           "Целевая аудитория узла.",
           flat_branches("llm", "человек", "оба")),
    map_op("main", "строгость",
           "Степень обязательности рекомендации (применяется к документам).",
           flat_branches("обязательно", "рекомендуется", "опционально")),
    map_op("main", "приоритет",
           "Приоритет задачи.",
           flat_branches("критичный", "высокий", "средний", "низкий")),
]

# ============================================================
# Usage scope maps (8)
# ============================================================
usage_category_branches = {
    "usage": flat_branches("topic", "method", "field", "error", "schema",
                          "map", "link", "example", "rule"),
}

declared_map_names = [
    "категория", "подсистема", "статус", "адресат", "строгость", "приоритет",
    "тема", "метод", "поле", "код-ошибки", "имя-схемы", "имя-map", "имя-link",
]

declared_link_names = [
    "зависит-от", "заменяет", "опирается-на", "реализует", "связан-с",
    "упоминается-в",
    "использует-метод", "использует-поле", "возвращает-ошибку", "см-также",
    "описывает", "ссылается-на",
]

error_codes = [
    "invalid_json", "invalid_request", "missing_required_field", "invalid_op",
    "unknown_op", "unknown_select_mode", "invalid_scope", "unknown_scope",
    "at_not_applicable", "hist_read_only", "invalid_tx_title",
    "invalid_max_tokens", "invalid_match_regex", "invalid_match_fields",
    "match_timeout", "unknown_alias", "ambiguous_path_base",
    "invalid_node_title", "invalid_map_binding_value", "not_found",
    "ambiguous_selector", "count_mismatch", "path_parent_not_found",
    "already_exists", "unknown_map", "unknown_link", "cross_scope_not_allowed",
    "delete_blocked_by_cross_scope_link", "version_mismatch",
    "validation_failed", "schema_breaks_existing_data", "rollback_not_found",
    "rollback_conflict", "rollback_failed", "rollback_already_done",
    "hist_write_failed", "unknown_method",
]

api_fields = [
    "scope", "ops", "selector", "include", "max_tokens", "at",
    "title", "description", "defaults", "content", "map_bindings",
    "expected_count", "expected_version", "as", "set", "path",
    "id", "ids", "name", "from", "to", "alias", "links", "match",
]

usage_maps = [
    map_op("usage", "категория",
           "Тип usage-узла (rule, map, link, example, topic, method, field, "
           "error, schema).",
           usage_category_branches,
           required=True),
    map_op("usage", "тема",
           "Тематический разрез usage-узла.",
           flat_branches("read", "tx", "selector", "error", "schema",
                        "workflow", "scope")),
    map_op("usage", "метод",
           "Имя метода JSON API.",
           flat_branches("read", "tx")),
    map_op("usage", "поле",
           "Имя поля request/response/селектора.",
           flat_branches(*api_fields)),
    map_op("usage", "код-ошибки",
           "Код ошибки JSON API.",
           flat_branches(*error_codes)),
    map_op("usage", "имя-схемы",
           "Имя data-scope (main или usage).",
           flat_branches("main", "usage")),
    map_op("usage", "имя-map",
           "Имя map, описываемой через usage/map.",
           flat_branches(*declared_map_names)),
    map_op("usage", "имя-link",
           "Имя link, описываемого через usage/link.",
           flat_branches(*declared_link_names)),
]

# ============================================================
# Main scope links (6)
# ============================================================
main_links = [
    link_op("main", "зависит-от",
            "From-узел смыслово зависит от to-узла."),
    link_op("main", "заменяет",
            "From-узел заменяет to-узел (новая версия → устаревшая)."),
    link_op("main", "опирается-на",
            "Задача опирается на документ как контекст."),
    link_op("main", "реализует",
            "Задача реализует решение, инвариант или гайд."),
    link_op("main", "связан-с",
            "Мягкая ассоциация между двумя main-узлами."),
    link_op("main", "упоминается-в",
            "Узел упоминается в обсуждении или заметке."),
]

# ============================================================
# Usage scope links (4 within + 2 cross-scope)
# ============================================================
usage_links = [
    link_op("usage", "использует-метод",
            "Usage-узел упоминает указанный метод JSON API."),
    link_op("usage", "использует-поле",
            "Usage-узел упоминает указанное поле request/response."),
    link_op("usage", "возвращает-ошибку",
            "Usage-узел описывает ситуацию, в которой kernel возвращает "
            "указанный код ошибки."),
    link_op("usage", "см-также",
            "Мягкая ассоциация между двумя usage-узлами."),
    link_op("usage", "описывает",
            "Usage-узел описывает указанный main-узел (cross-scope "
            "usage → main)."),
    link_op("usage", "ссылается-на",
            "Usage-узел ссылается на указанный main-узел как пример или "
            "трассировку (cross-scope usage → main)."),
]

# ============================================================
# Root containers (создаются первыми, parent paths для map/link нод)
# ============================================================
roots = [
    {"create": {"path": "main", "set": {"title": "main"}}},
    {"create": {"path": "usage", "set": {"title": "usage"}}},
]

# ============================================================
# Build top-level tx
# ============================================================
tx = {
    "scope": "scheme",
    "title": "declare-maps-and-links",
    "description": (
        "Объявить полный контракт V2: 6 main-maps + 8 usage-maps + "
        "6 main-links + 6 usage-links (4 within + 2 cross-scope). "
        "Категория main без required, usage категория required=true "
        "(usage scope изначально пуст)."
    ),
    "ops": roots + main_maps + usage_maps + main_links + usage_links,
}

out_path = sys.argv[1] if len(sys.argv) > 1 else "-"
json_str = json.dumps(tx, ensure_ascii=False, indent=2)
if out_path == "-":
    sys.stdout.write(json_str)
else:
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(json_str)
    print(f"wrote {out_path}: {len(tx['ops'])} ops, {len(json_str)} bytes",
          file=sys.stderr)
