# step-03 — tests-cleanup

**Статус:** [+]

## Цель

Привести набор тестов к зелёному `dotnet test` после code-removal (step-02). По
страт-плану шаг включал три удаления и два правки. Большая часть работы уже
выполнена в step-02 (см. «Расхождения со страт» в `step-02.code-removal.md`):
без этого `dotnet build` падал — атомарность step-02 нарушалась бы.

Поэтому реальная задача step-03:

1. Зафиксировать состояние тест-проекта (что осталось, что удалено, что
   подправлено) — для прозрачности диффа между страт-планом и реальностью.
2. Прогнать `dotnet test`. Все оставшиеся тесты должны быть зелёные.
3. Если что-то падает — диагностировать и починить (или удалить тест
   с обоснованием в этом файле).

## Сделано в step-02 (за scope step-03)

### Удалены полностью

- `tests/DocsWalker.Tests/SeenScopeTests.cs`
- `tests/DocsWalker.Tests/SessionsInfrastructureTests.cs`
- `tests/DocsWalker.Tests/WriteInvalidationTests.cs`

### Поправлены

- `tests/DocsWalker.Tests/AutoIncludeTests.cs` — удалены 2 метода с
  seen-сценариями (`AutoIncludeAlreadySeen_BecomesPlaceholder`,
  `NoSeenTrue_BypassesFilterForAutoIncludes`); сигнатуры `ReadApiJson` обновлены
  под новые (без `scope`/`noSeen`).
- `tests/DocsWalker.Tests/McpArgvBuilderTests.cs` — `--no-seen=false` →
  `--quiet=false` в `BuildArgv_BooleanValue_TrueFalse` (сохранён
  boolean-маршалинг как кейс).
- `tests/DocsWalker.Tests/RedirectRefsTests.cs` — `createResult.TouchedIds.First(...)`
  → `createResult.OpResults[0].Data["id"]!.GetValue<int>()` (`TouchedIds` удалён
  из `WriteResult`).

## Что сделано в этом шаге

Никаких code-правок. Step свёлся к одному прогону:

```
dotnet test DocsWalker.slnx --nologo --verbosity minimal
> Пройден!  : не пройдено  0, пройдено 152, пропущено 0, всего 152, 905 ms.
```

Чинить было нечего — все правки в тестах опережающим порядком уехали в step-02
ради зелёной сборки. Этот шаг подтверждает, что и тестовая семантика осталась
зелёной (не только компиляция).

## Расхождения со страт

- Страт описывает «удалить 3 теста + поправить 2 теста + запустить `dotnet test`».
  Удаления/правки уже сделаны в step-02 (документировано там же). Здесь
  остался только прогон.
- Решение оставить step-03 отдельным шагом, а не схлопнуть в step-02 —
  принято ради читаемости истории git и страт-плана: step-02 = «код мёртв,
  билд зелёный», step-03 = «тесты зелёные», step-04 = «smoke на опубликованных
  бинарях». Каждая точка верификации — отдельный коммит.

## Точка возобновления

После step-03 `dotnet build` и `dotnet test` оба зелёные. Готовность к step-04
(smoke на `dotnet publish` бинарях).
