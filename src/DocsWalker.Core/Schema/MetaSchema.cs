using System.Text.Json.Nodes;

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
/// Объявление именованного дерева (tree-scope) поверх графа.
/// Дерево образовано всеми <see cref="RefDef"/> с <c>Tree=Name</c> в любых типах
/// (см. docs/.docswalker/meta-schema.yml/«tree_definition»).
/// </summary>
public sealed record TreeDefinition(string Name, string? Description)
{
    /// <summary>Имя зарезервированного встроенного дерева хранилища.</summary>
    public const string PathTreeName = "path";
}

/// <summary>
/// Объявление одной исходящей связи в типе узла-источника.
/// (см. docs/.docswalker/meta-schema.yml/«ref_def»).
/// При <see cref="Tree"/> != null связь участвует в дереве scope-имени Tree;
/// в этом случае <see cref="Cardinality"/>=One и <see cref="Required"/>=true
/// подразумеваются (в YAML мета-схема запрещает их указывать рядом с tree).
/// </summary>
public sealed record RefDef(
    string Name,
    IReadOnlyList<string> TargetTypes,
    string? Tree,
    Cardinality Cardinality,
    bool Required,
    string? Description)
{
    /// <summary>
    /// Связь является auto-include: <c>tree == null</c> и <c>required == true</c>.
    /// Read-команды транзитивно подтягивают цели таких связей в payload
    /// (см. docs/DocsWalker.yml/«(#340) Auto-include», (#363) auto-include).
    /// </summary>
    public bool IsAutoInclude => Tree is null && Required;
}

/// <summary>
/// Описание типа узла. Тип задаёт допустимые исходящие связи (<see cref="OutRefs"/>)
/// и требуется ли непустой text. Размещение узла в дереве хранилища задаётся
/// обязательной (для всех типов кроме root) связью <c>name=path</c> с <c>tree=path</c>
/// в <see cref="OutRefs"/> (см. docs/.docswalker/meta-schema.yml/«type_definition»).
/// </summary>
public sealed record TypeDefinition(
    string Name,
    string? Description,
    TitleSource TitleSource,
    bool TextRequired,
    IReadOnlyList<RefDef> OutRefs)
{
    /// <summary>
    /// Возвращает встроенную связь <c>name=path</c> (с <c>tree=path</c>), если она объявлена.
    /// У типа <c>root</c> path-ref отсутствует по контракту мета-схемы v6 (root —
    /// корневой синглгон без родителя в дереве хранилища); для остальных типов лукап
    /// всегда даёт результат в валидной Схеме.
    /// </summary>
    public RefDef? FindPathRef()
    {
        foreach (var rd in OutRefs)
        {
            if (string.Equals(rd.Name, "path", StringComparison.Ordinal)) return rd;
        }
        return null;
    }

    /// <summary>
    /// Допустимые типы родителя в дереве хранилища (target_types у связи <c>path</c>).
    /// Пустой список — ошибка валидации схемы; вызывающий код может полагаться на наличие
    /// path-связи у любого type_definition в валидной Схеме.
    /// </summary>
    public IReadOnlyList<string> PathTargets =>
        FindPathRef()?.TargetTypes ?? Array.Empty<string>();
}

/// <summary>
/// Мета-схема — описание формы schema-файла проекта. Загружается из docs/.docswalker/meta-schema.yml.
/// Текущая версия — 6 (refs-модель + tree-scopes + root-as-declared-type). Структура
/// schema_root / type_definition / ref_def / tree_definition фиксирована v6 и заложена
/// в код валидатора — типизированные поля (Version/Name/Description/PrimitiveTypes) валидатор
/// читает напрямую. Остальные верхнеуровневые ключи мета-схемы попадают в <see cref="Sections"/>
/// в форме generic JsonNode (mapping → JsonObject, sequence → JsonArray, скаляр → JsonValue
/// с эвристикой по форме: true/false → bool, целое → long, иначе → string). Используется
/// сериализатором <see cref="SchemaJson.ToJson(MetaSchemaDocument)"/>: контракт tool
/// <c>get-meta-schema</c> — отдать LLM полный текст meta-schema.yml в JSON-форме (см.
/// docs/DocsWalker.yml/«(#19) get_meta_schema»).
/// </summary>
public sealed record MetaSchemaDocument(
    int Version,
    string Name,
    string Description,
    IReadOnlyList<string> PrimitiveTypes,
    IReadOnlyDictionary<string, JsonNode> Sections);
