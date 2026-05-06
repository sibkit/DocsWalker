namespace DocsWalker.Core.Schema;

/// <summary>
/// Схема проекта — типы узлов, объявленные деревья (tree-scopes) и контракты связей.
/// Загружается из docs/Схема.yml. Идентификация schema-файла — по имени файла; внутреннего
/// поля schema/document мета-схема не предусматривает (см. docs/Правила оформления.yml/
/// «Идентификация документа»).
/// </summary>
public sealed record SchemaDocument(
    string Description,
    IReadOnlyList<TreeDefinition> Trees,
    IReadOnlyList<TypeDefinition> Types);
