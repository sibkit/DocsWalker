namespace DocsWalker.Core.Api;

/// <summary>
/// Оценка стоимости текста в токенах. V1-эвристика: 4 символа ≈ 1 токен.
/// Возвращается LLM как поле <c>tokens</c> в ответе <c>read</c> (per
/// api/read.md, раздел «Compact-форма data-узла»).
/// </summary>
internal static class Tokens
{
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        return (text.Length + 3) / 4;
    }

    /// <summary>
    /// Оценка стоимости сериализованного compact data-узла в JSON-ответе
    /// (id+path+title+map_bindings+scope?+tokens+version + JSON-обвязка).
    /// Используется при truncation, чтобы compact-форма имела ненулевой
    /// budget (per api/read.md, раздел «Truncation»).
    /// </summary>
    public static int EstimateCompactDataNode(
        string id, string path, string title,
        IReadOnlyDictionary<string, string> mapBindings,
        bool includeScope)
    {
        // Базовая JSON-обвязка: ключи id/path/title/map_bindings/tokens/version
        // и скобки. Эмпирически ≈ 60 символов.
        var chars = 60;
        chars += id.Length + path.Length + title.Length;
        if (includeScope) chars += "scope".Length + 8; // ,"scope":"usage"|"scheme"
        chars += "map_bindings".Length + 4;
        foreach (var kv in mapBindings)
        {
            chars += kv.Key.Length + kv.Value.Length + 6; // "k":"v",
        }
        return (chars + 3) / 4;
    }

    /// <summary>
    /// Оценка стоимости сериализованного compact event-узла hist в JSON
    /// (id+title+date+rollback_of?+counts+tokens + JSON-обвязка).
    /// </summary>
    public static int EstimateCompactEventNode(
        string id, string title, string date, string? rollbackOf)
    {
        var chars = 60; // обвязка + counts + tokens
        chars += id.Length + title.Length + date.Length;
        if (rollbackOf is not null) chars += "rollback_of".Length + rollbackOf.Length + 6;
        return (chars + 3) / 4;
    }
}
