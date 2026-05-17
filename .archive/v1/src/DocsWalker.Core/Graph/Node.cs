namespace DocsWalker.Core.Graph;

/// <summary>
/// Атомарная единица в DocsWalker. Имеет ровно 5 концептуальных полей:
/// Id, TypeName, Title, Text, OutRefs (см. docs/DocsWalker.yml/«узел»).
/// SourceFile — engineering-metadata (откуда узел был загружен), не часть
/// «5 полей» refs-модели; используется для диагностики и трекинга.
/// </summary>
public sealed class Node
{
    /// <summary>Глобально уникальный sequence-id, выданный DocsWalker.</summary>
    public required int Id { get; init; }

    /// <summary>Имя типа узла из Схемы (document, section, statement, rule, …).</summary>
    public required string TypeName { get; init; }

    /// <summary>Title — 1–2-словный path-сегмент, человекочитаемая часть.</summary>
    public required string Title { get; init; }

    /// <summary>Text — единица смысла, всегда одна строка. Пустая строка для типов с text_required=false.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Все исходящие связи узла как Map&lt;имя, list&lt;target_id&gt;&gt;. Связь path
    /// присутствует у каждого узла кроме root (cardinality=one). Прочие связи
    /// объявляются типом узла-источника. Default- и system-shadow-категорий нет.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<int>> OutRefs { get; init; }

    /// <summary>Относительный путь файла-источника от docs/ (например, "DocsWalker.yml"). Engineering-metadata, не входит в концептуальные 5 полей.</summary>
    public required string SourceFile { get; init; }

    /// <summary>Имя встроенной связи path (зарезервировано).</summary>
    public const string PathRefName = "path";

    /// <summary>Зарезервированное имя корневого синглтона (id=0).</summary>
    public const string RootTypeName = "root";

    /// <summary>Идентификатор корневого синглтона.</summary>
    public const int RootId = 0;

    /// <summary>Возвращает id родителя по out_refs[path], или null если связь отсутствует (только у root).</summary>
    public int? ParentId
    {
        get
        {
            if (!OutRefs.TryGetValue(PathRefName, out var list)) return null;
            return list.Count == 0 ? null : list[0];
        }
    }
}
