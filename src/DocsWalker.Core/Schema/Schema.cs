namespace DocsWalker.Core.Schema;

/// <summary>
/// Схема проекта — типы узлов, типы связей и формы значений. Загружается из docs/Схема.yml.
/// Идентификация schema-файла — по имени файла; внутреннего поля schema/document
/// мета-схема не предусматривает (см. docs/Правила оформления.yml/«Идентификация документа»).
/// </summary>
public sealed record SchemaDocument(
    string Description,
    IReadOnlyList<TypeDefinition> Types);
