#!/usr/bin/env python3
"""Builds tx scope=scheme JSON to raise main/категория to required=true."""
import json
import sys

content = {
    "description": (
        "Классификация main-узлов по типу: документы, задачи, заметки или "
        "legacy V1."
    ),
    "branches": {
        "документы": {"спека": {}, "решение": {}, "инвариант": {}, "гайд": {}},
        "задачи": {
            "бэклог": {}, "запланирована": {}, "активна": {},
            "заблокирована": {}, "отложена": {}, "сделана": {}, "отменена": {},
        },
        "заметки": {
            "исследование": {}, "вопрос": {}, "чейнджлог": {}, "идея": {},
        },
        "legacy": {"v1": {}},
    },
    "required": True,
    "required_when": None,
}

tx = {
    "scope": "scheme",
    "title": "raise-категория-required",
    "description": (
        "Поднять main категория до required=true после массовой "
        "разметки legacy/v1."
    ),
    "ops": [
        {
            "update": {
                "id": "1a6",
                "expected_version": 1,
                "set": {
                    "content": json.dumps(content, ensure_ascii=False),
                },
            }
        }
    ],
}

out_path = sys.argv[1] if len(sys.argv) > 1 else "-"
json_str = json.dumps(tx, ensure_ascii=False, indent=2)
if out_path == "-":
    sys.stdout.write(json_str)
else:
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(json_str)
    print(f"wrote {out_path}: {len(json_str)} bytes", file=sys.stderr)
