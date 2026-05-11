using System.Text;

namespace DocsWalker.Core.Api;

/// <summary>
/// Извлечение snippet'а для search-команды: окно ±40 символов вокруг первого
/// совпадения с эллипсисами по краям, длина результата ≤ 160 символов
/// (см. docs/DocsWalker.yml/#26).
/// </summary>
internal static class SnippetExtractor
{
    private const int Window = 40;
    private const int MaxLength = 160;
    private const string Ellipsis = "…";

    /// <summary>
    /// Окно ±40 символов вокруг диапазона <c>[matchStart, matchEnd)</c> в
    /// <paramref name="text"/> с эллипсисами по краям, если фрагмент не упирается
    /// в начало/конец. Длина результата усекается до 160 символов.
    /// </summary>
    public static string Extract(string text, int matchStart, int matchEnd)
    {
        if (matchStart < 0) matchStart = 0;
        if (matchEnd > text.Length) matchEnd = text.Length;
        if (matchEnd < matchStart) matchEnd = matchStart;

        int start = Math.Max(0, matchStart - Window);
        int end = Math.Min(text.Length, matchEnd + Window);

        var sb = new StringBuilder();
        if (start > 0) sb.Append(Ellipsis);
        sb.Append(text.AsSpan(start, end - start));
        if (end < text.Length) sb.Append(Ellipsis);

        var snippet = sb.ToString();
        if (snippet.Length > MaxLength)
            snippet = snippet.Substring(0, MaxLength - 1) + Ellipsis;
        return snippet;
    }

    /// <summary>
    /// Ищет в <paramref name="text"/> первое (минимальное по позиции) вхождение
    /// любого из <paramref name="queryTokens"/> (case-insensitive) и возвращает
    /// snippet через <see cref="Extract"/>. Если ни один токен не найден — null.
    /// </summary>
    public static string? FindAndExtract(string? text, IReadOnlyList<string> queryTokens)
    {
        if (string.IsNullOrEmpty(text) || queryTokens.Count == 0) return null;

        int bestIdx = -1;
        int bestLen = 0;
        foreach (var tok in queryTokens)
        {
            if (string.IsNullOrEmpty(tok)) continue;
            int i = text.IndexOf(tok, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            if (bestIdx < 0 || i < bestIdx)
            {
                bestIdx = i;
                bestLen = tok.Length;
            }
        }
        if (bestIdx < 0) return null;
        return Extract(text, bestIdx, bestIdx + bestLen);
    }
}
