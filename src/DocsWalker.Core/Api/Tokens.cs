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
}
