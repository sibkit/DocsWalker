namespace DocsWalker.Core.Graph;

/// <summary>
/// Происхождение связи. Совпадает с пометкой origin в read-API DocsWalker
/// (см. docs/DocsWalker.yml/«Модель данных»).
/// </summary>
public enum RefOrigin
{
    /// <summary>Прикладная связь, явно записанная в блоке out_refs узла-источника.</summary>
    Explicit,

    /// <summary>Системная связь path: ребёнок → родитель. В YAML явно не хранится, выводится из вложенности.</summary>
    System,

    /// <summary>Связь по умолчанию: родитель → ребёнок по имени блока (definitions, examples, fields, content). В YAML не хранится.</summary>
    Default,
}

/// <summary>
/// Направленная типизированная связь между узлами. Тип — имя ref_type из Схемы
/// (для Explicit / System) или имя default-блока (для Default).
/// </summary>
public sealed record Ref(int FromId, string TypeName, int ToId, RefOrigin Origin);
