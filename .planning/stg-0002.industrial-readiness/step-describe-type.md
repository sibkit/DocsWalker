# stg-0002 — describe-type

## Цель
Узкая read-команда `describe-type --name=<type>`: возвращает API-описание одного типа из Схемы. Экономит токены LLM по сравнению с целиком `get-schema`. Описание **FS-агностичное**: ни `title_source`, ни имена файлов/каталогов наружу не торчат — они контракт «движок ↔ docs/», LLM их не должна знать.

## Форма ответа

```yaml
name: <type_name>
description: <type description>
text_required: bool
out_refs:
  - name: <ref_name>
    tree?: <scope>           # отсутствует у не-tree refs
    cardinality: one|many    # отсутствует у tree-refs (подразумевается one)
    required: bool           # отсутствует у tree-refs (подразумевается true)
    target_types: [<type>, ...]
    description?: <ref description>
```

Что **не возвращается** в API:
- `title_source` (filename/dirname/inline_key) — внутренний контракт хранилища.
- Имена файлов, каталогов, путей в FS.

Что возвращается **как часть out_refs** (не отдельным полем):
- `path` — рядовая запись в out_refs у каждого типа кроме root, помечена `tree: path`. Никакого выделенного поля.

## Файлы
- `docs/DocsWalker.yml` — раздел «Операции чтения»: новый пункт `describe_type` с примером ответа в новой форме.
- `docs/DocsWalker.yml` — раздел «CLI-интерфейс»: пример `describe-type --name=section`.
- `src/DocsWalker.Core/Api/ReadApi.cs` — метод `DescribeType(string name)`. Внутри: найти `TypeDefinition` в `SchemaDocument.Types`; преобразовать в API-DTO, **не показывая** title_source; для каждой RefDef собрать запись в out_refs с условным включением полей в зависимости от Tree.
- `src/DocsWalker.Core/Api/ReadApiJson.cs` — сериализация TypeDescription DTO.
- `src/DocsWalker.Cli/Cli/Commands.cs`, `Cli/Handlers/ReadHandlers.cs` или `SchemaHandlers.cs`, `Program.cs`.
- `tests/DocsWalker.Tests/ReadApiDescribeTypeTests.cs` — тесты.

## Действия
1. Зафиксировать команду и форму ответа в `docs/DocsWalker.yml`.
2. Реализовать `ReadApi.DescribeType`:
   - Если тип не найден — `read_api_exception` с кодом `type_not_found` и hint «вызови `get-schema` для списка типов».
   - Сборка out_refs: для каждой RefDef условно включаем `tree`, `cardinality`, `required` (по правилам выше).
3. Подключить в CLI.
4. Тесты: запрос существующего типа, несуществующего, типа с tree-ref'ами и обычными cross-refs (проверка корректного формата каждой), типа `root` (нет path в out_refs).

## Риски
- При реализации не утечь title_source/path_targets — выделить отдельный DTO, не сериализовать SchemaDocument напрямую.
