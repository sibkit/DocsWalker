# stg-0001 — schema-loader

## Цель
Прочитать и распарсить `docs/.docswalker/meta-schema.yml` и `docs/Схема.yml` в типизированные C#-модели. Реализовать команды `get-meta-schema` и `get-schema`.

## Файлы
`src/DocsWalker.Core/Schema/MetaSchema.cs` — record-типы мета-схемы (`TypeKind`, `TypeDefinition`, `FieldDefinition`, `BlockDefinition`, `RefType`, `NodeType`, `Primitive`)
`src/DocsWalker.Core/Schema/Schema.cs` — record-типы загруженной схемы (узлы, типы связей, примитивы)
`src/DocsWalker.Core/Schema/SchemaLoader.cs` — загрузка обоих YAML-файлов через SharpYaml event-stream API, упрощённая структурная валидация
`src/DocsWalker.Cli/Cli/Handlers/SchemaHandlers.cs` — обработчики `get-meta-schema`, `get-schema`

## Действия
1. Описать record-типы под мета-схему: поля из `meta-schema.yml`, включая `node`, `title_source`, `title_format`, `direction`, `system`. Для тегированных вариантов (`mapping` vs `single_key_mapping`) использовать sum-подход — общий базовый record + дискриминатор.
2. Распарсить `meta-schema.yml` через SharpYaml на уровне event-stream API (`Parser` / `EventReader` / `YamlStream`), без reflection-based Serializer. Разложить вручную в record-типы мета-схемы. Если документ не соответствует ожидаемой форме — структурированная ошибка.
3. Распарсить `Схема.yml` через тот же SharpYaml event-stream API. Разложить по типам узлов / типам связей / примитивов вручную.
4. Реализовать `get-meta-schema` и `get-schema` — JSON-сериализация распарсенных структур в stdout (через source-сгенерированный `JsonSerializerContext`).

## Риски
- Различие между `mapping`-узлами и `single_key_mapping`-узлами требует осторожной модели данных. Sum-тип / discriminator должен быть зафиксирован в одном месте и применяться единообразно.
- Поле `blocks` встречается у узла-`single_key_mapping` (например, `section`) — обе формы должны парситься одной моделью.
