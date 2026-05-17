using System.Text;

namespace DocsWalker.Core.Api;

/// <summary>
/// Аккумулятор SQL-фрагмента + параметров. Параметры именуются
/// <c>@p0</c>, <c>@p1</c>, ... — индекс монотонный, привязан к порядку
/// добавления (соответствует порядку bind у SqliteCommand).
/// </summary>
public sealed class SqlBuilder
{
    private readonly StringBuilder _sb = new();
    private readonly List<object?> _parameters = [];

    public SqlBuilder Append(string text)
    {
        _sb.Append(text);
        return this;
    }

    /// <summary>
    /// Добавляет литеральный SQL-фрагмент (без параметризации).
    /// Допустимо только для трастовых строк (имена таблиц / колонок /
    /// зарезервированных значений), не для пользовательского input.
    /// </summary>
    public SqlBuilder Raw(string sql) => Append(sql);

    /// <summary>
    /// Регистрирует параметр и вставляет его имя в SQL.
    /// </summary>
    public SqlBuilder Param(object? value)
    {
        var name = "@p" + _parameters.Count;
        _parameters.Add(value ?? DBNull.Value);
        _sb.Append(name);
        return this;
    }

    public string Sql => _sb.ToString();
    public IReadOnlyList<object?> Parameters => _parameters;
}

/// <summary>
/// Компиляция API-селекторов из api/selectors.md в SQL-предикаты per
/// database-model/schema.md, раздел «Маппинг селекторов в SQL».
/// Метод <c>AppendDataPredicate</c> добавляет конъюнкты к WHERE по
/// data-узлу; <c>AppendHistPredicate</c> — по event-узлу.
/// </summary>
public static class SelectorSql
{
    /// <summary>
    /// Добавляет ` AND &lt;predicate&gt;` для каждого заполненного поля
    /// <paramref name="selector"/>. Caller передаёт уже открытый WHERE
    /// с обязательным <c>graph_name = ?</c> и <c>scope = ?</c>
    /// фильтром.
    /// </summary>
    public static void AppendDataPredicate(SqlBuilder b, string alias, DataSelector selector)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Ids is { Count: > 0 } ids)
        {
            b.Append(" AND ");
            AppendIdIn(b, alias, ids);
        }
        if (!string.IsNullOrEmpty(selector.Path))
        {
            b.Append(" AND ");
            AppendValuePattern(b, alias + ".path", selector.Path);
        }
        if (!string.IsNullOrEmpty(selector.Title))
        {
            b.Append(" AND LOWER(").Append(alias).Append(".title) = LOWER(").Param(selector.Title).Append(")");
        }
        if (selector.MapBindings is { Count: > 0 } mb)
        {
            foreach (var kv in mb)
            {
                b.Append(" AND EXISTS (SELECT 1 FROM node_map_binding b WHERE b.graph_name = ")
                    .Append(alias).Append(".graph_name AND b.node_id = ").Append(alias).Append(".id AND b.map_name = ")
                    .Param(kv.Key).Append(" AND ");
                AppendValuePattern(b, "b.branch_path", kv.Value);
                b.Append(")");
            }
        }
        if (selector.Match is { } match)
        {
            AppendMatchClause(b, alias, match, DataDefaultFields);
        }
        if (selector.Links is { } links)
        {
            b.Append(" AND ");
            AppendLinksExists(b, alias, links);
        }
    }

    /// <summary>
    /// Добавляет ` AND &lt;predicate&gt;` для каждого заполненного поля
    /// hist-селектора. Caller передаёт WHERE с уже зафиксированным
    /// <c>graph_name = ?</c>.
    /// </summary>
    public static void AppendHistPredicate(SqlBuilder b, string alias, HistSelector selector)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Ids is { Count: > 0 } ids)
        {
            b.Append(" AND ");
            AppendIdIn(b, alias, ids);
        }
        if (!string.IsNullOrEmpty(selector.Title))
        {
            b.Append(" AND ").Append(alias).Append(".title = ").Param(selector.Title);
        }
        if (selector.TitleMatch is { } titleMatch)
        {
            AppendMatchClause(b, alias, titleMatch, HistDefaultFields);
        }
        if (!string.IsNullOrEmpty(selector.Date))
        {
            b.Append(" AND ").Append(alias).Append(".date = ").Param(selector.Date);
        }
        if (selector.DateMatch is { } dateMatch)
        {
            AppendMatchClause(b, alias, dateMatch, HistDefaultFields);
        }
        if (!string.IsNullOrEmpty(selector.Description))
        {
            b.Append(" AND instr(").Append(alias).Append(".description, ").Param(selector.Description).Append(") > 0");
        }
        if (selector.DescriptionMatch is { } descMatch)
        {
            AppendMatchClause(b, alias, descMatch, HistDefaultFields);
        }
        if (!string.IsNullOrEmpty(selector.RollbackOf))
        {
            b.Append(" AND ").Append(alias).Append(".rollback_of = ").Param(selector.RollbackOf);
        }
        if (!string.IsNullOrEmpty(selector.TxScope))
        {
            b.Append(" AND ").Append(alias).Append(".tx_scope = ").Param(selector.TxScope);
        }
        if (!string.IsNullOrEmpty(selector.TouchesNodeId))
        {
            b.Append(" AND EXISTS (SELECT 1 FROM tx_touches_node tn WHERE tn.graph_name = ")
                .Append(alias).Append(".graph_name AND tn.tx_id = ").Append(alias).Append(".id AND tn.node_id = ")
                .Param(selector.TouchesNodeId).Append(")");
        }
        if (selector.TouchesLink is { } link)
        {
            b.Append(" AND EXISTS (SELECT 1 FROM tx_touches_link tl WHERE tl.graph_name = ")
                .Append(alias).Append(".graph_name AND tl.tx_id = ").Append(alias).Append(".id")
                .Append(" AND tl.link_name = ").Param(link.Name)
                .Append(" AND tl.from_id = ").Param(link.From)
                .Append(" AND tl.to_id = ").Param(link.To)
                .Append(")");
        }
    }

    /// <summary>
    /// Допустимые имена полей в <c>selector.match.fields</c> для data-узла.
    /// Соответствует api/selectors.md, раздел «Match по содержимому».
    /// </summary>
    public static readonly IReadOnlyList<string> DataDefaultFields = new[] { "title", "content" };

    /// <summary>
    /// Допустимые имена полей в <c>selector.match.fields</c> для event-узла.
    /// </summary>
    public static readonly IReadOnlyList<string> HistDefaultFields = new[] { "title", "description" };

    private static void AppendIdIn(SqlBuilder b, string alias, IReadOnlyList<string> ids)
    {
        b.Append(alias).Append(".id IN (");
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) b.Append(", ");
            b.Param(ids[i]);
        }
        b.Append(")");
    }

    /// <summary>
    /// Компилирует строку с возможными wildcard <c>*</c>/<c>**</c> в один
    /// из трёх вариантов:
    /// <list type="bullet">
    /// <item>exact (без wildcard) → <c>column = ?</c>.</item>
    /// <item><c>prefix/**</c> → <c>column LIKE 'prefix/%' ESCAPE '\\'</c>.</item>
    /// <item><c>prefix/*</c> → <c>LIKE 'prefix/%' ESCAPE '\\' AND instr(substr(column, len+1), '/') = 0</c>.</item>
    /// </list>
    /// Иные комбинации wildcard (в середине, отдельный <c>*</c>) в v1 не
    /// поддерживаются — возвращают <c>invalid_request</c>.
    /// </summary>
    private static void AppendValuePattern(SqlBuilder b, string columnSql, string pattern)
    {
        if (pattern.Length == 0)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, extras: new Dictionary<string, object?> { ["reason"] = "empty_pattern" });
        }
        var starIdx = pattern.IndexOf('*');
        if (starIdx < 0)
        {
            b.Append(columnSql).Append(" = ").Param(pattern);
            return;
        }
        if (pattern.EndsWith("/**", StringComparison.Ordinal) && pattern.IndexOf('*', 0, pattern.Length - 3) < 0)
        {
            var prefix = pattern[..^2];
            b.Append(columnSql).Append(" LIKE ").Param(EscapeLike(prefix) + "%").Append(" ESCAPE '\\'");
            return;
        }
        if (pattern.EndsWith("/*", StringComparison.Ordinal) && pattern.IndexOf('*', 0, pattern.Length - 2) < 0)
        {
            var prefix = pattern[..^1];
            b.Append("(").Append(columnSql).Append(" LIKE ").Param(EscapeLike(prefix) + "%").Append(" ESCAPE '\\'")
                .Append(" AND instr(substr(").Append(columnSql).Append(", ").Param((long)prefix.Length + 1).Append("), '/') = 0)");
            return;
        }
        throw new ApiException(ApiErrorCodes.InvalidRequest, extras: new Dictionary<string, object?>
        {
            ["reason"] = "unsupported_path_pattern",
            ["pattern"] = pattern,
        });
    }

    private static string EscapeLike(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '%' or '_')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AppendMatchClause(SqlBuilder b, string alias, MatchClause match, IReadOnlyList<string> defaults)
    {
        var fields = match.Fields ?? defaults;
        b.Append(" AND (");
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) b.Append(" OR ");
            b.Append("regex_match(").Append(alias).Append(".").Append(fields[i]).Append(", ").Param(match.Regex).Append(", ")
                .Param(match.CaseSensitive ? 1L : 0L).Append(")");
        }
        b.Append(")");
    }

    private static void AppendLinksExists(SqlBuilder b, string outerAlias, LinkClause links)
    {
        b.Append("EXISTS (SELECT 1 FROM link l WHERE l.graph_name = ").Append(outerAlias).Append(".graph_name");
        // Направление: ограничение от outer-узла. Если задано `to` — outer
        // выступает как from; если задано `from` — как to; если оба заданы —
        // ровно одна форма допустима (parser нормализует, но в общем случае
        // первое непустое определяет направление).
        if (links.To is { } toClause)
        {
            b.Append(" AND l.from_id = ").Append(outerAlias).Append(".id");
            if (!string.IsNullOrEmpty(links.Name))
            {
                b.Append(" AND l.name = ").Param(links.Name);
            }
            AppendLinkEndpointFilter(b, "l.to_id", outerAlias, toClause);
        }
        else if (links.From is { } fromClause)
        {
            b.Append(" AND l.to_id = ").Append(outerAlias).Append(".id");
            if (!string.IsNullOrEmpty(links.Name))
            {
                b.Append(" AND l.name = ").Param(links.Name);
            }
            AppendLinkEndpointFilter(b, "l.from_id", outerAlias, fromClause);
        }
        else
        {
            // Только name: любой incident link с указанным name.
            b.Append(" AND (l.from_id = ").Append(outerAlias).Append(".id OR l.to_id = ").Append(outerAlias).Append(".id)");
            if (!string.IsNullOrEmpty(links.Name))
            {
                b.Append(" AND l.name = ").Param(links.Name);
            }
        }
        b.Append(")");
    }

    private static void AppendLinkEndpointFilter(SqlBuilder b, string endpointColumn, string outerAlias, LinkEndpointClause clause)
    {
        if (!string.IsNullOrEmpty(clause.Id))
        {
            b.Append(" AND ").Append(endpointColumn).Append(" = ").Param(clause.Id);
            return;
        }
        if (clause.Selector is { } sel)
        {
            b.Append(" AND ").Append(endpointColumn).Append(" IN (SELECT t.id FROM node t WHERE t.graph_name = ")
                .Append(outerAlias).Append(".graph_name");
            AppendDataPredicate(b, "t", sel);
            b.Append(")");
        }
    }
}
