namespace DocsWalker.Core.Schema;

/// <summary>
/// Схема проекта — типы узлов, объявленные деревья (tree-scopes) и контракты связей.
/// Загружается из docs/Схема.yml. Идентификация schema-файла — по имени файла; внутреннего
/// поля schema/document мета-схема не предусматривает (см. docs/Правила оформления.yml/
/// «Идентификация документа»).
/// <para>
/// <see cref="DefaultAddressableTree"/> — опциональное имя дерева, используемого как
/// default в API <c>get-by-path</c> без явного <c>--tree=</c>. Если не задано и в Схеме
/// ровно один addressable tree (с <c>unique_sibling_titles=true</c>) — он default
/// автоматически. Если addressable trees больше одного и поле не задано — <c>--tree=</c>
/// обязателен (ошибка <c>tree_required</c>).
/// </para>
/// </summary>
public sealed record SchemaDocument(
    string Description,
    IReadOnlyList<TreeDefinition> Trees,
    IReadOnlyList<TypeDefinition> Types,
    string? DefaultAddressableTree = null);
