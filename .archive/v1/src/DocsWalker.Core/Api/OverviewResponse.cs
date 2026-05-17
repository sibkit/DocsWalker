namespace DocsWalker.Core.Api;

/// <summary>
/// Один объявленный tree-scope в обзоре графа.
/// </summary>
public sealed record TreeOverview(string Name, string? Description);

/// <summary>
/// Тип Схемы + число узлов этого типа в графе. Используется в hot-spots
/// поля <see cref="OverviewResponse.TopTypesByCount"/>.
/// </summary>
public sealed record TypeCount(string TypeName, int Count);

/// <summary>
/// Верхнеуровневый ребёнок root в path-дереве с tokens поддерева.
/// </summary>
public sealed record RootChildOverview(int Id, string TypeName, string Title, int SubtreeTokens);

/// <summary>
/// Крупный одиночный узел — кандидат на разбиение. Сортировка по
/// <see cref="Tokens"/> (CountNode), не по subtree_tokens: иначе на верх
/// всегда выходят top-level документы.
/// </summary>
public sealed record HotSpotByTokens(int Id, string Title, int Tokens);

/// <summary>
/// «Хаб» графа — узел с большим числом cross-refs (in+out, без tree-refs).
/// Tree-refs (path и др. tree-scopes из Схемы) исключены: иначе верх
/// выигрывают document/section с многочисленными path-детьми.
/// </summary>
public sealed record HotSpotByRefs(int Id, string Title, int RefsCount);

/// <summary>
/// Глобальный snapshot хранилища для команды get-overview.
/// </summary>
public sealed record OverviewResponse(
    int TotalNodes,
    int MaxDepth,
    int TotalTokens,
    IReadOnlyList<TreeOverview> Trees,
    int TypesCount,
    IReadOnlyList<TypeCount> TopTypesByCount,
    IReadOnlyList<RootChildOverview> RootChildren,
    IReadOnlyList<HotSpotByTokens> LargestNodes,
    IReadOnlyList<HotSpotByRefs> MostConnectedNodes);
