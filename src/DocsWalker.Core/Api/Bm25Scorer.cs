using System.Text.RegularExpressions;

namespace DocsWalker.Core.Api;

/// <summary>
/// BM25-ранжирование для search-команды (см. docs/DocsWalker.yml/#26).
/// Используется один scorer на запрос: токенизуем query и documents,
/// строим per-doc tf-словари только по терминам запроса, считаем idf и
/// final score. k1=1.2, b=0.75 — стандартные значения, на 337-узловом
/// корпусе разница с упрощёнными метриками неотличима.
/// </summary>
internal static class Bm25Scorer
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var matches = TokenRegex.Matches(text);
        if (matches.Count == 0) return Array.Empty<string>();
        var tokens = new List<string>(matches.Count);
        foreach (Match m in matches) tokens.Add(m.Value.ToLowerInvariant());
        return tokens;
    }

    /// <summary>
    /// Считает BM25-scores для каждого doc'а в <paramref name="docTokens"/> по
    /// уникальным терминам <paramref name="queryTokens"/>. Возвращает массив
    /// scores в том же порядке, что docs.
    /// </summary>
    public static double[] Score(IReadOnlyList<IReadOnlyList<string>> docTokens, IReadOnlyList<string> queryTokens)
    {
        int n = docTokens.Count;
        var scores = new double[n];
        if (n == 0 || queryTokens.Count == 0) return scores;

        var qSet = new HashSet<string>(queryTokens, StringComparer.Ordinal);
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in qSet) df[q] = 0;

        var docTf = new Dictionary<string, int>[n];
        var docLen = new int[n];
        long totalLen = 0;

        for (int i = 0; i < n; i++)
        {
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            var toks = docTokens[i];
            docLen[i] = toks.Count;
            totalLen += toks.Count;
            foreach (var tok in toks)
            {
                if (!qSet.Contains(tok)) continue;
                tf[tok] = tf.GetValueOrDefault(tok) + 1;
            }
            docTf[i] = tf;
            foreach (var term in tf.Keys) df[term]++;
        }

        double avgdl = n > 0 ? (double)totalLen / n : 0;
        if (avgdl <= 0) return scores;

        for (int i = 0; i < n; i++)
        {
            double s = 0;
            foreach (var kv in docTf[i])
            {
                int dfn = df[kv.Key];
                double idf = Math.Log(((n - dfn + 0.5) / (dfn + 0.5)) + 1);
                int tf = kv.Value;
                double norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * docLen[i] / avgdl));
                s += idf * norm;
            }
            scores[i] = s;
        }
        return scores;
    }
}
