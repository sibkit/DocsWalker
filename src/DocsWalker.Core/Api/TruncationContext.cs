namespace DocsWalker.Core.Api;

/// <summary>
/// Точка обрыва BFS в truncation-протоколе read-команд (см. docs/DocsWalker.yml/#406).
/// <see cref="ParentId"/> — id узла, чьи children не дочитаны (для get-nodes —
/// синтетический маркер 0 плоского списка). <see cref="RemainingChildren"/> — сколько
/// ещё не включено. <see cref="NextOffset"/> — индекс первого недочитанного child'а
/// в исходном списке.
/// </summary>
public sealed record TruncationPoint(int ParentId, int RemainingChildren, int NextOffset);

/// <summary>
/// Accumulator для BFS-усечения read-ответов под бюджет токенов. Один экземпляр на
/// один call get-tree/get-nodes. Не thread-safe (нужен только в одном вызове).
/// </summary>
internal sealed class TruncationContext
{
    public int Budget { get; }
    public int TokensUsed { get; private set; }
    public List<TruncationPoint> StoppedAt { get; } = new();
    public bool Truncated => StoppedAt.Count > 0;

    public TruncationContext(int budget)
    {
        Budget = budget;
    }

    /// <summary>
    /// Пытается «потратить» <paramref name="tokens"/> из бюджета. true → потрачено
    /// (TokensUsed увеличен); false → не хватило (ничего не потрачено).
    /// </summary>
    public bool TryConsume(int tokens)
    {
        if (TokensUsed + tokens > Budget) return false;
        TokensUsed += tokens;
        return true;
    }

    /// <summary>
    /// Принудительно засчитывает <paramref name="tokens"/>, даже если выходит за
    /// бюджет — используется для «минимума» (корень в compact-форме), который мы
    /// возвращаем даже когда сам корень больше бюджета.
    /// </summary>
    public void ForceConsume(int tokens)
    {
        TokensUsed += tokens;
    }

    public void RecordStop(int parentId, int remainingChildren, int nextOffset)
    {
        StoppedAt.Add(new TruncationPoint(parentId, remainingChildren, nextOffset));
    }
}
