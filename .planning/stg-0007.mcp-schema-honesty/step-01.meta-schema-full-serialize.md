# stg-0007 — MCP Schema Honesty

## Цель

`get-meta-schema` отдаёт полный документ мета-схемы (`schema_root`,
`tree_definition`, `type_definition`, `field_definition`, `ref_definition` и
прочие разделы реального `meta-schema.yml`), а не только верхушку
(`meta_schema_version`/`name`/`description`/`primitive_types`). Описание tool
обещает «полный текст мета-схемы» — реальный ответ должен этому
соответствовать.

## Файлы

- `docs/DocsWalker.yml` — уточнить/зафиксировать контракт tool
  `get-meta-schema`: какая форма JSON отдаётся, какие поля включаются.
- `src/DocsWalker.Core/Schema/MetaSchema.cs` — DTO `MetaSchemaDocument`
  пополнить полями (если разделы yml в DTO не парсятся).
- `src/DocsWalker.Core/Schema/SchemaLoader.cs` — парсинг недостающих полей в
  `LoadMetaSchema`; `SchemaJson.ToJson(MetaSchemaDocument)` сериализует все
  поля DTO.
- `tests/DocsWalker.Tests/Schema/...` — тест: загрузили проектный
  `meta-schema.yml`, прогнали через `LoadMetaSchema` + `SchemaJson.ToJson`,
  получили JSON с ключами всех разделов исходного yml.

## Действия

1. Прочитать `docs/.docswalker/meta-schema.yml` целиком, выписать все
   верхнеуровневые ключи.
2. Прочитать `MetaSchema.cs` и `SchemaLoader.LoadMetaSchema`. Установить,
   какие из разделов yml сейчас парсятся в DTO, какие игнорируются.
3. Через DocsWalker уточнить в `docs/DocsWalker.yml` контракт `get-meta-schema`:
   в каком виде отдаётся каждый раздел.
4. При необходимости — расширить DTO `MetaSchemaDocument` и парсер `LoadMetaSchema`.
5. Расширить `SchemaJson.ToJson(MetaSchemaDocument)` — сериализовать все
   поля DTO.
6. Добавить юнит-тест на полноту сериализации.
7. Сборка `dotnet publish ...` + smoke-test через `mcp-server`:
   `tools/call get-meta-schema` → проверить, что ответ содержит ожидаемые
   ключи.

## Риски

Объём DTO-расширения зависит от того, насколько `MetaSchemaDocument` сейчас
полон. Если парсер игнорирует большую часть — шаг разрастается. Установить
на шаге 2 и при значимой цене вынести trade-off в чат.
