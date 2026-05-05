namespace DocsWalker.Core.Schema;

/// <summary>
/// Схема проекта — типы узлов, типы связей и формы значений. Загружается из docs/Схема.yml.
/// SchemaName может быть null, если в файле отсутствует поле "schema:" (это диагностируется
/// валидатором, не загрузчиком).
/// </summary>
public sealed record SchemaDocument(
    string? SchemaName,
    string Description,
    IReadOnlyList<TypeDefinition> Types);
