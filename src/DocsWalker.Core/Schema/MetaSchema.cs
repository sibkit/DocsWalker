namespace DocsWalker.Core.Schema;

public enum TypeKind
{
    Mapping,
    SingleKeyMapping,
    List,
    Primitive,
    RefType,
}

public enum TitleSourceKind
{
    Filename,
    InlineKey,
    Field,
}

public enum RefDirection
{
    ChildToParent,
    FromTo,
}

public abstract record TypeDefinition(
    string Name,
    TypeKind Kind,
    string? Description);

/// <summary>Тип kind ∈ {mapping, single_key_mapping, list}.</summary>
public sealed record NodeType(
    string Name,
    TypeKind Kind,
    string? Description,
    bool? Node,
    TitleSourceKind? TitleSource,
    string? TitleField,
    string? TitleFormat,
    IReadOnlyList<FieldDefinition>? Fields,
    string? KeyType,
    string? ValueType,
    string? Of,
    IReadOnlyList<BlockDefinition>? Blocks,
    IReadOnlyList<string>? Constraints)
    : TypeDefinition(Name, Kind, Description);

public sealed record RefType(
    string Name,
    RefDirection Direction,
    bool System,
    string? Description)
    : TypeDefinition(Name, TypeKind.RefType, Description);

public sealed record Primitive(
    string Name,
    string? Description,
    IReadOnlyList<string>? Constraints)
    : TypeDefinition(Name, TypeKind.Primitive, Description);

public sealed record FieldDefinition(
    string Name,
    string Type,
    string? Of,
    IReadOnlyList<string>? Values,
    bool Required,
    string? Default,
    string? Description);

public sealed record BlockDefinition(
    string Name,
    string Of,
    bool Required,
    string? Description);

/// <summary>
/// Мета-схема — описание формы schema-файла. Загружается из docs/.docswalker/meta-schema.yml.
/// </summary>
public sealed record MetaSchemaDocument(
    int Version,
    string Name,
    string Description,
    IReadOnlyList<string> PrimitiveTypes,
    IReadOnlyList<string> TypeKinds,
    NodeType SchemaRoot,
    NodeType TypeDefinitionSlot,
    NodeType FieldDefinitionSlot,
    NodeType BlockDefinitionSlot);
