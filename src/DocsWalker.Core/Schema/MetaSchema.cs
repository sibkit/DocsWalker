namespace DocsWalker.Core.Schema;

/// <summary>
/// Источник, откуда узел типа берёт title.
/// (см. docs/.docswalker/meta-schema.yml/type_definition.title_source).
/// </summary>
public enum TitleSource
{
    /// <summary>Имя файла без расширения (для document).</summary>
    Filename,

    /// <summary>Имя каталога (для folder).</summary>
    Dirname,

    /// <summary>Inline-ключ mapping в YAML родителя (для смысловых узлов).</summary>
    InlineKey,
}

/// <summary>
/// Допустимое число целей связи у одного узла-источника.
/// (см. docs/.docswalker/meta-schema.yml/ref_def.cardinality).
/// </summary>
public enum Cardinality
{
    /// <summary>Ровно одна цель.</summary>
    One,

    /// <summary>Ноль и более целей.</summary>
    Many,
}

/// <summary>
/// Объявление одной исходящей связи в типе узла-источника.
/// (см. docs/.docswalker/meta-schema.yml/«ref_def»).
/// </summary>
public sealed record RefDef(
    string Name,
    IReadOnlyList<string> TargetTypes,
    Cardinality Cardinality,
    bool Required,
    string? Description);

/// <summary>
/// Описание типа узла. Тип задаёт, где узел может находиться (PathTargets),
/// какие исходящие связи у него допустимы (OutRefs) и требуется ли непустой text.
/// (см. docs/.docswalker/meta-schema.yml/«type_definition»).
/// </summary>
public sealed record TypeDefinition(
    string Name,
    string? Description,
    TitleSource TitleSource,
    bool TextRequired,
    IReadOnlyList<string> PathTargets,
    IReadOnlyList<RefDef> OutRefs);

/// <summary>
/// Мета-схема — описание формы schema-файла проекта. Загружается из docs/.docswalker/meta-schema.yml.
/// Текущая версия — 4 (refs-модель). Структура schema_root / type_definition / ref_def
/// фиксирована v4 и заложена в код валидатора; здесь хранятся только верхние поля.
/// </summary>
public sealed record MetaSchemaDocument(
    int Version,
    string Name,
    string Description,
    IReadOnlyList<string> PrimitiveTypes);
