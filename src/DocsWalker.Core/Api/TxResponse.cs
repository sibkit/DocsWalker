namespace DocsWalker.Core.Api;

/// <summary>
/// Полный ответ на <c>tx</c>. <c>Id</c> — id вновь созданного
/// event-узла <c>hist/transaction</c> (одновременно tx_id, per
/// api/model.md, раздел «Поля event-узла»). <c>Ops</c> синхронизирован
/// один-в-один с <c>request.ops[]</c>.
/// </summary>
public sealed record TxResponse(string Id, IReadOnlyList<TxOpResponse> Ops);

public abstract record TxOpResponse;

/// <summary>
/// Результат <c>create</c>: <c>Id</c> нового узла, выделенный kernel-ом.
/// </summary>
public sealed record CreateOpResponse(string Id) : TxOpResponse;

/// <summary>
/// Пустой результат для всех op кроме <c>create</c>. По api/tx.md в
/// массиве результатов это отдаётся как <c>{}</c>.
/// </summary>
public sealed record EmptyTxOpResponse : TxOpResponse
{
    public static readonly EmptyTxOpResponse Instance = new();
}
