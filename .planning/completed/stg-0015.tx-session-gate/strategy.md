# stg-0015 - tx-session-gate

Цель: оставить `hit` отдельным preview-инструментом, но сделать `tx` самодостаточным commit gate, который не пропускает мусор в docs даже если LLM не вызвала `hit`.

## Шаги

[+] (01) spec-update - уточнить docs-модель: `tx` принимает `session_id`, `intent`, `mode`; `query` может пополнять workset.
[+] (02) kernel-gate - добавить session-aware guard в kernel path для `query`/`tx`, не дублируя core write validation.
[+] (03) tests - покрыть `query` workset auto-register и `tx` blockers/apply modes.
[+] (04) verification - full test, publish/restart, live-smoke.

## Handoff

- `hit` остается самостоятельным read-only preview.
- `tx` должен сам делать hard safety gate: отсутствие `hit` не должно открывать путь записи мусора.
- Guard относится к session/workset/intent/revision/mass-selector policy; schema/ref/text validation остается в Core write path.
