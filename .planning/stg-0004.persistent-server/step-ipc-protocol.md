# stg-0004 — ipc-protocol

## Цель

Реализовать прикладной протокол поверх IPC-handle из шага `server-lifecycle`: newline-delimited JSON-frames, version handshake, серверная маршрутизация запроса в существующий `Dispatcher.Run`, клиентская функция отправки. После шага: байты ходят между сторонами правильно, серверная сторона умеет диспатчить запросы; команда `run` и переключение CLI — следующие шаги.

## Файлы

`src/DocsWalker.Core/Server/Protocol/Frame.cs` (новый) — encode/decode JSON-кадра (одна строка). Использует System.Text.Json source generator (per AOT-правилу проекта).

`src/DocsWalker.Core/Server/Protocol/Handshake.cs` (новый) — структуры `HandshakeRequest { ClientVersion, ProtocolVersion }`, `HandshakeResponse { ServerVersion, Accepted, Reason? }`. Логика проверки версий.

`src/DocsWalker.Core/Server/Protocol/Request.cs`, `Response.cs` (новые) — структуры запроса (`Cmd`, `Args[]`) и ответа (`Kind: "ok"|"error"`, `Payload: object`).

`src/DocsWalker.Core/Server/IpcServer.cs` (новый) — accept-loop: на каждый коннект — handshake, потом цикл `read frame → dispatch → write frame → ...` пока клиент не отключится. Серверная маршрутизация: парсит `Request.Args[]` → передаёт в существующий `Dispatcher.Run` → собирает stdout/stderr (или эквивалент) → формирует `Response`. Все запросы сериализуются глобально (`SemaphoreSlim(1,1)` на сервере).

`src/DocsWalker.Cli/Cli/IpcClient.cs` (новый) — клиентская функция `SendCommand(rootPath, args)`: коннект в pipe/socket, handshake, отправка запроса, чтение ответа, маршалинг в stdout/stderr + exit-code.

`src/DocsWalker.Core/Server/ProtocolVersion.cs` (новый) — константа текущей версии. Жёсткое равенство при handshake.

## Действия

1. Описать формат кадра: одна строка UTF-8 JSON, заканчивающаяся `\n`. Нет multi-line, нет binary.
2. Реализовать `Frame.WriteAsync` / `Frame.ReadLineAsync` поверх `Stream`. Учесть max-size limit (например, 16 MiB) — защита от runaway.
3. Реализовать handshake: сервер ждёт первое сообщение, парсит как `HandshakeRequest`, сверяет `ProtocolVersion` (жёсткое равенство), отвечает `HandshakeResponse`. При mismatch — `Accepted=false`, `Reason="version mismatch: server=X, client=Y, restart server"`, потом закрывает соединение.
4. Реализовать серверный accept-loop: на каждый принятый коннект — спавн обработчика (`Task.Run` либо async-loop). После handshake — цикл `request → response`. При закрытии клиента — выход из обработчика.
5. Внутри обработчика `Request` — взять `SemaphoreSlim(1,1)` (глобальный для сервера), вызвать `Dispatcher.Run(args)`, перехватить stdout/stderr — для этого обернуть `Console.Out`/`Console.Error` в `StringWriter` на время вызова (или модифицировать `Dispatcher` чтобы возвращал результат в виде объекта вместо записи в Console — это чище, обсудить во время реализации).
6. Реализовать клиента: коннект в pipe/socket по имени из pid-файла → handshake → отправка запроса → чтение ответа. Если `Kind=="ok"` — записать payload в `Console.Out`, exit 0. Если `Kind=="error"` — записать в `Console.Error`, exit 1.
7. Smoke-тест: поднять `IpcServer` в тесте, прогнать через `IpcClient` команду `get-types`, проверить что ответ совпадает с in-process вызовом. Тестируется до интеграции в `run` команду.
8. Проверить trim/AOT-совместимость: source generator для JSON в `Frame.cs`, никаких рефлексивных сериализаторов.

## Развилка для обсуждения по ходу реализации (не блокирующая старт шага)

**Stdout/stderr capture vs. Dispatcher refactor**: сейчас `Dispatcher.Run` пишет в `Console.Out`/`Console.Error` напрямую. Серверу удобнее, чтобы он возвращал `(string Stdout, string Stderr, int ExitCode)`. Можно либо обернуть Console (быстрее, грязнее) либо отрефакторить Dispatcher (чище, требует прохода по всем командам). Решить во время реализации; склоняюсь к рефактору — он один раз, потом `Dispatcher` чистый и тестируемый изолированно.

## Риски

- **Зависший клиент**: коннект открыт, запрос не идёт. Сервер блокируется на `ReadLineAsync` навсегда. Защита: timeout на handshake (например, 5s); после handshake — без timeout (REPL может молчать долго).
- **Большой ответ**: `get-subtree` на большом графе может дать сотни KB. Лимит 16 MiB должен покрыть; если упрёмся — увеличить или ввести streaming.
- **JSON source-gen для open-ended `Payload`**: payload — это `object`, что плохо дружит с source-gen'ом. Решение: сериализовать payload отдельным шагом — раз серверная сторона уже умеет сериализовать каждую конкретную команду в JSON-строку, кадр везёт `Payload` как уже-сериализованную строку (raw JSON), не объект. Frame.cs знает только про `kind` + `payload_raw`.
- **Console capture race**: если Dispatcher пишет в Console async — capture через `StringWriter` ловит часть. Поэтому рефактор Dispatcher предпочтительнее (см. развилку).
