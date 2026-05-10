# stg-0010 — step-05 — error-case-tests

## Цель

Закрыть тестовые пробелы по новым error-codes из step-02 и step-03.

К моменту step-05 валидатор уже умеет emit'ить `duplicate_sibling_title`,
client-config даёт `client_config_not_found`/`client_config_invalid`,
а `KernelHttpClient` — `kernel_unreachable` при недоступном ядре. Часть
этого покрыта (см. ниже), но **e2e write-path** для addressable-tree
collision и **e2e network-path** для `kernel_unreachable` тестами не
охвачены — добавляем их.

Шаг — **только тесты,** без правок production-кода. После шага
`dotnet build` + `dotnet test` — зелёные.

## Что уже покрыто (не трогаем)

- **`client_config_not_found` / `client_config_invalid`** — полное покрытие
  в `tests/DocsWalker.Tests/ClientConfigTests.cs` (file-not-found,
  malformed JSON, missing kernel, missing graph, invalid port,
  upward-search resolution).
- **`tree_required` / `tree_not_addressable` / `unknown_tree_scope` /
  `default_addressable_tree`** — покрыто в
  `tests/DocsWalker.Tests/AddressableTreeTests.cs` (read-side: построение
  Схемы в памяти + `ReadApi.GetByPath`).
- **`duplicate_sibling_title` на уровне Validator** — покрыто в
  `tests/DocsWalker.Tests/ValidatorTests.cs/Validate_SiblingTitleCollisionInPathTree_Reports_DuplicateSiblingTitle`.

## Что добавляется

### 1. `tests/DocsWalker.Tests/KernelHttpClientTests.cs` (новый файл)

`kernel_unreachable` e2e: kernel offline → клиент даёт корректный
error-code и exit≠0.

- `SendCommandAsync_KernelOffline_Reports_KernelUnreachable` —
  занимает свободный порт через `TcpListener(IPAddress.Loopback, 0)`,
  немедленно закрывает его, конструирует `ClientConfig` указывающий на
  этот порт, перенаправляет `Console.Error` в `StringWriter`, вызывает
  `KernelHttpClient.SendCommandAsync(["get-nodes", "--ids=1"], cfg)`,
  проверяет: возвращён `1`, в stderr `"code":"kernel_unreachable"`.

`KernelHttpClient` — `internal static`, доступ через
`InternalsVisibleTo("DocsWalker.Tests")` в `DocsWalker.Cli.csproj`
(уже сконфигурировано).

### 2. `tests/DocsWalker.Tests/AddressableTreeWriteTests.cs` (новый файл)

E2e write-path для addressable-tree collision через `WriteApi.Apply` /
`ApplyOne`. Использует `WriteTestEnvironment` (изолированный клон
реального `docs/`) и реальную Схему DocsWalker'а, где `path` —
единственный addressable tree.

- `CreateNode_DuplicateSiblingTitle_Reports_DuplicateSiblingTitle` —
  два create-node под одним path-родителем с одинаковым title;
  второй вызов бросает `WriteValidationException` с error-code
  `duplicate_sibling_title`.
- `UpdateNode_TitleCollidesWithSibling_Reports_DuplicateSiblingTitle` —
  создаёт два узла с разными title, затем update-node на второй,
  меняя title на title первого; ожидается
  `WriteValidationException` / `duplicate_sibling_title`.
- `MoveNode_NewParentHasSiblingWithSameTitle_Reports_DuplicateSiblingTitle` —
  создаёт два узла с одинаковым title под РАЗНЫМИ path-родителями;
  затем move-node одного из них в parent другого; ожидается
  collision.
- `Transaction_BatchCreatesCollision_Reports_DuplicateSiblingTitle` —
  `Apply` с двумя `CreateNodeOp` в одной пачке, оба с одинаковым
  title под одним parent; вся транзакция бракуется атомарно.

## Действия (упорядоченные)

1. `Write` `step-05.error-case-tests.md` (этот файл).
2. `Edit` `strategy.md` — `[ ] (05)` → `[*] (05)`.
3. `Write` `tests/DocsWalker.Tests/KernelHttpClientTests.cs`.
4. `Write` `tests/DocsWalker.Tests/AddressableTreeWriteTests.cs`.
5. `mcp__glider__sync` — Roslyn видит новые файлы.
6. `dotnet build DocsWalker.slnx` — зелёный.
7. `dotnet test DocsWalker.slnx` — зелёный (178 + 5 = 183).
8. `Edit` `strategy.md` — `[*] (05)` → `[+] (05)`.
9. `Edit` проектного `CLAUDE.md` — обновить «Активная сессия» под
   step-06 (smoke).
10. Atomic git: `add` / `commit` / `push` (3 отдельных Bash-вызова).

## Риски

- **Race-condition с TcpListener-portgrab.** Между `Stop()` и попыткой
  клиента открыть TCP-соединение порт может быть занят случайным
  процессом. Вероятность ≈ 0 на dev-машине; если будет flake —
  переключиться на гарантированно-неоткрытый порт (например, 1) или
  использовать DNS-имя, которое не резолвится.
- **InternalsVisibleTo + AOT.** В `DocsWalker.Cli.csproj` стоит
  `PublishAot=true`, но тесты собираются как обычная сборка (xunit,
  reflection-based) и `InternalsVisibleTo` уже работает (см.
  `McpArgvBuilderTests` обращается к `Cli.Mcp` internals). Никаких
  доп.правок csproj не требуется.
- **Конкуренция за `WriteTestEnvironment`.** Каждый тест получает
  собственный временный каталог; никаких side-effect'ов между тестами.
- **Сообщение `duplicate_sibling_title` упоминает tree-name.**
  Проверяем error-code, не текст — текст в спеке зафиксирован, но
  тест по тексту дал бы false-positive flake при правках месседжа.

## Сверка со страт

- Блок 3.4 (валидация при write): тесты подтверждают, что
  `WriteApi.Apply` для `create-node`/`update-node`/`move-node`/
  `transaction` действительно бракует sibling-collision через
  `WriteValidationException` с code `duplicate_sibling_title`.
- Блок 1.2 (`kernel_unreachable`): тест подтверждает, что при kernel
  offline клиент возвращает exit=1 и пишет JSON с
  `"code":"kernel_unreachable"` в stderr.
- step-05 завершает test-coverage stg-0010; в step-06 остаётся только
  smoke на published binaries.
