namespace DocsWalker.Core.Graph;

/// <summary>
/// Один блок внутри узла-section (или другого носителя блоков).
/// Имя блока соответствует BlockDefinition.Name из Схемы.
/// </summary>
public abstract record NodeBlock(string Name);

/// <summary>
/// Текстовый блок (statements, rules, may_rules, notes, llm) — список текстовых элементов.
/// </summary>
public sealed record TextBlock(string Name, IReadOnlyList<string> Items) : NodeBlock(Name);

/// <summary>
/// Блок дочерних узлов (definitions, examples, fields). Хранит список id —
/// сами узлы лежат в <see cref="Graph"/> по этим id.
/// </summary>
public sealed record ChildrenBlock(string Name, IReadOnlyList<int> ChildIds) : NodeBlock(Name);

/// <summary>
/// Блок out_refs: список явных перекрёстных связей.
/// </summary>
public sealed record OutRefsBlock(string Name, IReadOnlyList<Ref> Refs) : NodeBlock(Name);

/// <summary>
/// Значение поля mapping-узла (для типов document, field). Поля-списки скаляров
/// (например, values у field) хранятся в Items; обычные скалярные поля — в Scalar.
/// Поля, ссылающиеся на дочерние узлы (например, content у document), в Fields
/// не дублируются — они отражены в children-связях через ParentId.
/// </summary>
public sealed record FieldValue(string Name, string? Scalar, IReadOnlyList<string>? Items);

/// <summary>
/// Атомарная единица информации в DocsWalker. Имеет уникальный id; адресуется по id.
/// Title — человекочитаемая часть; в YAML id и title склеиваются по title_format
/// типа узла (см. <see cref="TitleFormat"/>).
/// </summary>
public sealed class Node
{
    /// <summary>Глобально уникальный sequence-id, выданный DocsWalker.</summary>
    public required int Id { get; init; }

    /// <summary>Имя типа узла из Схемы (document, section, definition, example, field).</summary>
    public required string TypeName { get; init; }

    /// <summary>Человекочитаемая часть. Для document — имя файла без расширения.</summary>
    public required string Title { get; init; }

    /// <summary>Id родителя или null для root-узла (document).</summary>
    public required int? ParentId { get; init; }

    /// <summary>
    /// Имя блока в родителе, в котором лежит этот узел. Для document = null.
    /// Используется для построения default-связи parent → child (имя связи = имя блока).
    /// </summary>
    public required string? ParentBlockName { get; init; }

    /// <summary>Относительный путь файла-источника от docs/ (например, "DocsWalker.yml").</summary>
    public required string SourceFile { get; init; }

    /// <summary>Поля mapping-узла (для document, field). null для single_key_mapping-узлов.</summary>
    public IReadOnlyList<FieldValue>? Fields { get; init; }

    /// <summary>Блоки single_key_mapping-узла (для section). null если у типа узла нет блоков.</summary>
    public IReadOnlyList<NodeBlock>? Blocks { get; init; }

    /// <summary>Inline-значение single_key_mapping-узла с value_type=text (для definition, example).</summary>
    public string? InlineValue { get; init; }

    /// <summary>
    /// Явные исходящие связи, прочитанные из блока out_refs YAML. null если у типа узла
    /// нет блока out_refs или он не задан в файле. Default- и system-связи здесь не дублируются —
    /// они вычисляются на лету из <see cref="Graph"/>.
    /// </summary>
    public IReadOnlyList<Ref>? ExplicitOutRefs { get; init; }
}
