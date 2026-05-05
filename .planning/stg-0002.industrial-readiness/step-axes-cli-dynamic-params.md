# stg-0002 — axes-cli-dynamic-params

## Цель
Сделать парсер CLI для `create-node` динамическим: имена параметров (`--path`, `--block`, `--next`...) берутся из Схемы по `--type`. Аналогично — для будущих write-операций, требующих значений осей.

## Файлы
`src/DocsWalker.Cli/Cli/Parsers/CreateNodeArgs.cs` (или эквивалент):
- Двух-фазный разбор: сперва `--type`, затем по схеме определить ожидаемый набор параметров и распарсить их.
- Ошибки: `unknown_parameter` (передан параметр, не описанный в must/may_have_axes для type), `missing_parameter` (не передано значение must_have_axis), `invalid_parameter` (значение оси не проходит target_types/cardinality).
`src/DocsWalker.Cli/Cli/Commands.cs` — описание команды `create-node` теперь подсвечивает «параметры зависят от --type, см. describe-type».
`src/DocsWalker.Cli/Program.cs` — wire-up.
`tests/DocsWalker.Tests/CliTests.cs` — кейсы: валидный create-node для разных type; пропуск must_have_axis → missing_parameter; лишний параметр → unknown_parameter.

## Действия
1. Реализовать двух-фазный парсер.
2. Адаптировать handler `create-node` — проброс `axisValues` в `WriteApi`.
3. Обновить help-вывод `create-node` (статичный — без перечисления параметров; рекомендация использовать `describe-type`).
4. Тесты CLI.

## Риски
Динамический набор параметров плохо помещается в стандартные CLI-парсеры (System.CommandLine и пр.) — реализация скорее ручная. Нужно аккуратно сохранить общий формат `--key=value`.
