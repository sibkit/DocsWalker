using System.Text.RegularExpressions;

namespace DocsWalker.Core.Api;

/// <summary>
/// Применяет <see cref="DataSelector"/> к in-memory <see cref="AtState"/>
/// (per database-model/schema.md, раздел «Темпоральные чтения
/// (<c>at</c>)»). Используется только <see cref="ReadExecutor"/> при
/// <c>at</c> ≠ now — `now` идёт прямо в SQL через
/// <see cref="SelectorSql"/>. Поведение фильтров обязано совпадать с
/// SQL-аналогами (per api/selectors.md), кроме <c>links</c>: здесь —
/// in-memory обход <see cref="AtState.Links"/>.
/// </summary>
internal static class AtSelector
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

    public static List<AtNodeSnapshot> SelectNodes(
        AtState state, Scope scope, DataSelector selector)
    {
        var scopeWire = ScopeNames.ToWire(scope);
        var result = new List<AtNodeSnapshot>();
        foreach (var snap in state.Nodes.Values)
        {
            if (!string.Equals(snap.Scope, scopeWire, StringComparison.Ordinal)) continue;
            if (!Matches(snap, selector, state)) continue;
            result.Add(snap);
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    private static bool Matches(AtNodeSnapshot snap, DataSelector sel, AtState state)
    {
        if (sel.Ids is { Count: > 0 } ids)
        {
            var hit = false;
            foreach (var id in ids)
            {
                if (string.Equals(id, snap.Id, StringComparison.Ordinal)) { hit = true; break; }
            }
            if (!hit) return false;
        }
        if (sel.Path is { Length: > 0 } pattern)
        {
            if (!PathMatches(snap.Path, pattern)) return false;
        }
        if (sel.Title is { Length: > 0 } title)
        {
            if (!string.Equals(title, snap.Title, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (sel.MapBindings is { Count: > 0 } mb)
        {
            foreach (var kv in mb)
            {
                if (!snap.MapBindings.TryGetValue(kv.Key, out var value)) return false;
                if (!BranchMatches(value, kv.Value)) return false;
            }
        }
        if (sel.Match is { } m)
        {
            if (!MatchRegex(snap, m)) return false;
        }
        if (sel.Links is { } lc)
        {
            if (!LinksMatch(snap, lc, state)) return false;
        }
        return true;
    }

    private static bool PathMatches(string actual, string pattern)
    {
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = pattern[..^3];
            // "**" — весь поддерево, включая сам prefix не входит (per селектора `prefix/**`).
            return actual.StartsWith(prefix + "/", StringComparison.Ordinal);
        }
        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            if (!actual.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
            var tail = actual.AsSpan(prefix.Length + 1);
            return tail.IndexOf('/') < 0;
        }
        if (pattern.Contains('*'))
        {
            // Intra-segment glob не поддерживается (per api/selectors.md).
            throw new ApiException(ApiErrorCodes.InvalidRequest, extras:
                new Dictionary<string, object?> { ["reason"] = "intra_segment_glob_unsupported" });
        }
        return string.Equals(actual, pattern, StringComparison.Ordinal);
    }

    private static bool BranchMatches(string actualBranch, string pattern)
    {
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = pattern[..^3];
            return actualBranch.StartsWith(prefix + "/", StringComparison.Ordinal)
                || string.Equals(actualBranch, prefix, StringComparison.Ordinal);
        }
        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            if (!actualBranch.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
            return actualBranch.AsSpan(prefix.Length + 1).IndexOf('/') < 0;
        }
        return string.Equals(actualBranch, pattern, StringComparison.Ordinal);
    }

    private static bool MatchRegex(AtNodeSnapshot snap, MatchClause m)
    {
        var fields = m.Fields ?? new[] { "content" };
        var opts = RegexOptions.CultureInvariant;
        if (!m.CaseSensitive) opts |= RegexOptions.IgnoreCase;
        Regex regex;
        try
        {
            regex = new Regex(m.Regex, opts, MatchTimeout);
        }
        catch (ArgumentException)
        {
            throw new ApiException(ApiErrorCodes.InvalidMatchRegex);
        }
        foreach (var field in fields)
        {
            string text = field switch
            {
                "title" => snap.Title,
                "content" => snap.Content,
                _ => string.Empty,
            };
            try
            {
                if (regex.IsMatch(text)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ApiException(ApiErrorCodes.MatchTimeout);
            }
        }
        return false;
    }

    private static bool LinksMatch(AtNodeSnapshot snap, LinkClause lc, AtState state)
    {
        foreach (var l in state.Links)
        {
            if (lc.Name is { Length: > 0 } n &&
                !string.Equals(n, l.Name, StringComparison.Ordinal)) continue;
            // Узел может выступать как from или to (per api/selectors.md, раздел «links»).
            if (lc.From is null && lc.To is null)
            {
                if (string.Equals(l.From, snap.Id, StringComparison.Ordinal) ||
                    string.Equals(l.To, snap.Id, StringComparison.Ordinal))
                {
                    return true;
                }
                continue;
            }
            // Если задано `from` — текущий узел = to, link приходит к нему ОТ from-кандидата.
            if (lc.From is { } from)
            {
                if (!string.Equals(l.To, snap.Id, StringComparison.Ordinal)) continue;
                if (!MatchesEndpoint(l.From, from, state)) continue;
            }
            // Если задано `to` — текущий узел = from, link уходит ОТ него К to-кандидату.
            if (lc.To is { } to)
            {
                if (!string.Equals(l.From, snap.Id, StringComparison.Ordinal)) continue;
                if (!MatchesEndpoint(l.To, to, state)) continue;
            }
            return true;
        }
        return false;
    }

    private static bool MatchesEndpoint(string nodeId, LinkEndpointClause ep, AtState state)
    {
        if (ep.Id is { Length: > 0 } id)
        {
            return string.Equals(id, nodeId, StringComparison.Ordinal);
        }
        if (ep.Selector is { } nested)
        {
            if (!state.Nodes.TryGetValue(nodeId, out var snap)) return false;
            return Matches(snap, nested, state);
        }
        return false;
    }
}
