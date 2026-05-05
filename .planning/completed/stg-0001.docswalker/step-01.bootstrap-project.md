# stg-0001 — bootstrap-project

## Цель
Создать каркас репозитория DocsWalker на C# / .NET 10 с обязательной публикацией через Native AOT: solution с тремя проектами (Core-библиотека, CLI-хост, тесты), подключить SharpYaml, прогнать обязательный smoke-тест парсинга нашего подмножества YAML, зафиксировать конкретную версию .NET в `docs/`.

## Файлы
`global.json` — пин SDK-версии .NET 10
`Directory.Build.props` — общие свойства всех проектов: Nullable, LangVersion=latest, TreatWarningsAsErrors
`DocsWalker.sln` — solution с тремя проектами
`src/DocsWalker.Core/DocsWalker.Core.csproj` — class library, `IsAotCompatible=true`, NuGet-зависимость на SharpYaml (xoofx/SharpYaml)
`src/DocsWalker.Cli/DocsWalker.Cli.csproj` — exe, `PublishAot=true`, ссылка на Core
`src/DocsWalker.Cli/Program.cs` — минимальная точка входа, печатает версию из `Assembly`
`tests/DocsWalker.Tests/DocsWalker.Tests.csproj` — xUnit, ссылка на Core (без AOT)
`tests/DocsWalker.Tests/SharpYamlSmokeTests.cs` — обязательный smoke-тест: SharpYaml парсит всё наше подмножество
`docs/Стек.yml` — раздел «Версия .NET» уточнить точную версию SDK (например, 10.0.x)

## Действия
1. `global.json` с пином .NET 10 SDK (конкретная версия — актуальная на момент бутстрапа).
2. `Directory.Build.props` в корне репозитория с общими свойствами:
   - `<Nullable>enable</Nullable>`,
   - `<LangVersion>latest</LangVersion>`,
   - `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`,
   - `<ImplicitUsings>enable</ImplicitUsings>`.
3. Создать `DocsWalker.sln` и три проекта:
   - `DocsWalker.Core` — class library, `<TargetFramework>net10.0</TargetFramework>`, `<IsAotCompatible>true</IsAotCompatible>`, NuGet `SharpYaml` последней стабильной версии;
   - `DocsWalker.Cli` — exe, `<TargetFramework>net10.0</TargetFramework>`, `<PublishAot>true</PublishAot>`, `<RootNamespace>DocsWalker.Cli</RootNamespace>`, `ProjectReference` на Core;
   - `DocsWalker.Tests` — xUnit, `<TargetFramework>net10.0</TargetFramework>`, `ProjectReference` на Core (без AOT — xUnit с Native AOT не совместим).
4. Минимальный `Program.cs` в `DocsWalker.Cli`: печатает версию через `Assembly.GetExecutingAssembly().GetName().Version`, выходит с кодом 0.
5. **Обязательный smoke-тест SharpYaml** в `DocsWalker.Tests/SharpYamlSmokeTests.cs`. Подать на парсер минимальный YAML, покрывающий всё подмножество из `docs/DocsWalker.yml`/«Стек реализации»:
   - block-mapping и block-sequence;
   - flow-sequence `[a, b, c]`;
   - **flow-mapping `{k: v}`** (критично проверить — частая дыра в YAML-парсерах);
   - скаляры unquoted, single-quoted, double-quoted;
   - integer, true/false;
   - комментарии `#`.
   Каждая конструкция проверяется тестом, что SharpYaml парсит её без ошибки и с ожидаемой структурой событий event-stream API. Если хотя бы одна часть не проходит — остановиться, не двигаться дальше, вернуть пользователю развилку.
6. `dotnet build`, `dotnet test` — оба зелёные.
7. `dotnet publish src/DocsWalker.Cli -c Release -r win-x64` (или другой подходящий native RID) — AOT-публикация проходит без AOT-warning'ов (`IL2026`, `IL3050`, `IL3051`). Если SharpYaml даёт AOT-предупреждения — изолировать reflection-зону через `[RequiresUnreferencedCode]`/trimming-аннотации либо подавить конкретные коды в csproj. Если AOT принципиально не получается — остановиться, вернуть пользователю развилку.
8. Обновить `docs/Стек.yml`/«Версия .NET», вписать точную версию SDK рядом с упоминанием LTS.

## Риски
- SharpYaml может не покрыть какой-то пункт подмножества (особенно flow-mapping `{k: v}`). Smoke-тест из п. 5 — обязательный гейт.
- AOT-публикация может выдать предупреждения из-за reflection в SharpYaml. План реакции — в п. 7.
- `TreatWarningsAsErrors=true` иногда ловит предупреждения в коде SharpYaml. Глушить точечно через `<NoWarn>` для конкретных кодов в csproj Core, не общим выключением.
