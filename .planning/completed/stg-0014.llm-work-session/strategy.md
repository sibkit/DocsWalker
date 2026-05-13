# stg-0014 - llm-work-session

Цель: сделать так, чтобы LLM могла восстанавливать рабочий контекст при работе с DocsWalker через внешний, проверяемый state, а не через память модели.

## Шаги

[+] (01) spec-llm-work-session - описать в docs модель briefing/resume/checkpoint/context-check.
[+] (02) api-shape - определить минимальный MCP/kernel-facing контракт без привязки к legacy CLI.
[+] (03) core-skeleton - добавить минимальную реализацию или явно зафиксировать границу первой итерации.
[+] (04) verification - покрыть тестами и прогнать smoke.

## Handoff

- Рабочий канал LLM остается MCP tools поверх kernel JSON-RPC.
- Source of truth для поведения - docs-граф, не этот файл.
- Целевая проблема: после context reset LLM должна вызвать resume/briefing tool и получить компактный пакет текущего состояния, а перед записью kernel должен уметь проверить, что изменение не делается вслепую.
- Итог: добавлены tools brief/checkpoint/resume/context-check, checkpoint хранится в `.dw/sessions/<graph>/`, context-check проверяет intent/workset/revision/selector guards.
- Проверено: `dotnet test .\DocsWalker.slnx --no-restore` = 326/326; live kernel pid=21116 видит новые tools; check-integrity ok.
