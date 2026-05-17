namespace DocsWalker.Core.Api;

/// <summary>
/// Полный ответ на <c>read</c>. <c>Ops</c> синхронизирован один-в-один
/// с <c>request.ops[]</c> по позиции.
/// </summary>
public sealed record ReadResponse(IReadOnlyList<ReadOpResponse> Ops);

public abstract record ReadOpResponse;

/// <summary>
/// Ответ на <c>select</c> в форме-объект. <c>Items</c> — выдача
/// (compact или full в зависимости от <c>include</c>), <c>Count</c> —
/// общее число совпадений селектора. При срабатывании <c>max_tokens</c>:
/// <c>Truncated=true</c>, <c>OmittedCount</c> &gt; 0, <c>StoppedAt</c>
/// — <c>path</c> или <c>id</c> последнего возвращённого узла.
/// </summary>
public sealed record SelectNodesResponse(
    int Count,
    bool Truncated,
    int OmittedCount,
    string? StoppedAt,
    IReadOnlyList<NodeView> Items) : ReadOpResponse;

/// <summary>
/// Ответ на <c>select</c> в hist scope. Структура зеркальная с
/// <see cref="SelectNodesResponse"/>, но с event-узлами.
/// </summary>
public sealed record SelectEventsResponse(
    int Count,
    bool Truncated,
    int OmittedCount,
    string? StoppedAt,
    IReadOnlyList<EventView> Items) : ReadOpResponse;

/// <summary>
/// Ответ на <c>select: "meta"</c>. На v2 meta-schema ещё не зафиксирована —
/// возвращается пустой объект-заглушка.
/// </summary>
public sealed record SelectMetaResponse(IReadOnlyDictionary<string, object?> Meta) : ReadOpResponse;

/// <summary>
/// Compact/full форма data-узла. <c>Scope</c> — null для main (per
/// api/read.md: «Поле scope сериализуется в ответе только для узлов вне
/// main»). <c>Content</c> и <c>Links</c> — null если не запрошены через
/// <c>include</c>. <c>Version</c> — null при <c>at</c> ≠ now (per
/// api/model.md, раздел «Темпоральные чтения»).
/// </summary>
public sealed record NodeView(
    string Id,
    string? Scope,
    string Path,
    string Title,
    IReadOnlyDictionary<string, string> MapBindings,
    string? Content,
    IReadOnlyList<IncidentLinkView>? Links,
    int Tokens,
    long? Version);

/// <summary>
/// Запись в массиве <c>links</c> compact/full ответа. Ровно одно из
/// <c>To</c>/<c>From</c> заполнено: <c>To</c> — исходящий link от
/// текущего узла, <c>From</c> — входящий.
/// </summary>
public sealed record IncidentLinkView(
    string Name,
    LinkEndpointView? To,
    LinkEndpointView? From);

public sealed record LinkEndpointView(string Id, string Path);

/// <summary>
/// Compact/full форма event-узла. <c>Description</c>/<c>Created</c>/
/// <c>Changed</c>/<c>Deleted</c> — null если не запрошены через
/// <c>include</c>. <c>Counts</c> заполнен в compact-форме (когда хотя бы
/// одна из created/changed/deleted секций НЕ возвращена раскрытой);
/// в full-форме (<c>include</c> со всеми тремя секциями) <c>Counts</c>
/// также возвращается для удобства.
/// </summary>
public sealed record EventView(
    string Id,
    string Title,
    string Date,
    string? RollbackOf,
    string? Description,
    EventCountsView? Counts,
    CreatedSection? Created,
    ChangedSection? Changed,
    DeletedSection? Deleted,
    int Tokens);

/// <summary>
/// Подсекции с нулём опускаются (per api/read.md): <c>Created.Links</c>
/// = null означает, что в созданных секцию <c>links</c> не входит.
/// </summary>
public sealed record EventCountsView(
    SectionCountView? Created,
    SectionCountView? Changed,
    SectionCountView? Deleted);

public sealed record SectionCountView(int? Nodes, int? Links);
