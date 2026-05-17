using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocsWalker.Core.Api;

/// <summary>
/// Структурный парсер JSON-аргументов методов `read` и `tx` в DTO из
/// <see cref="Model"/>. Покрывает разбор формы запроса и базовую
/// семантическую валидацию (коды `invalid_*`, `missing_required_field`,
/// `invalid_op`, `unknown_op`, `unknown_select_mode`, `invalid_scope`,
/// `unknown_scope`, `hist_read_only`, `at_not_applicable`,
/// `invalid_match_regex`, `invalid_match_fields`, `invalid_max_tokens`,
/// `invalid_tx_title`, `invalid_node_title`, `invalid_map_binding_value`).
/// Resolve селекторов, concurrency и schema-проверки — на executor-е.
/// </summary>
public static class RequestParser
{
    private const string RootPath = "$";

    private static readonly HashSet<string> ReadScopes = new(StringComparer.Ordinal)
        { ScopeNames.Usage, ScopeNames.Scheme, ScopeNames.Hist };
    private static readonly HashSet<string> TxScopes = new(StringComparer.Ordinal)
        { ScopeNames.Usage, ScopeNames.Scheme };

    private static readonly HashSet<string> DataMatchFields = new(StringComparer.Ordinal)
        { "title", "content" };
    private static readonly HashSet<string> HistMatchFields = new(StringComparer.Ordinal)
        { "title", "description", "date" };

    private static readonly HashSet<string> KernelModes = new(StringComparer.Ordinal) { "meta" };

    private static readonly HashSet<string> TxScopeWireValues = new(StringComparer.Ordinal)
        { ScopeNames.Main, ScopeNames.Usage, ScopeNames.Scheme };

    private static readonly Regex NodeTitleRegex = new(@"^[\p{L}\p{Nd}._-]+$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // ---- Public entry points -----------------------------------------------

    public static ReadRequest ParseRead(string json)
    {
        using var doc = ParseJsonOrThrow(json);
        return ParseRead(doc.RootElement);
    }

    public static TxRequest ParseTx(string json)
    {
        using var doc = ParseJsonOrThrow(json);
        return ParseTx(doc.RootElement);
    }

    public static ReadRequest ParseRead(JsonElement root)
    {
        RequireObject(root, RootPath);

        var scope = ParseScope(root, RootPath, ReadScopes, txMethod: false);
        var defaults = ParseDefaults(root, RootPath);
        var at = ParseAt(root, RootPath, scope, txMethod: false);
        var ops = ParseReadOps(root, scope, at is not null, RootPath);

        return new ReadRequest(scope, defaults, at, ops);
    }

    public static TxRequest ParseTx(JsonElement root)
    {
        RequireObject(root, RootPath);

        var scope = ParseScope(root, RootPath, TxScopes, txMethod: true);
        // `at` в tx запрещён — фиксируется здесь с reason=tx_method.
        ParseAt(root, RootPath, scope, txMethod: true);
        var title = ParseTxTitle(root, RootPath);
        var description = OptionalString(root, "description", RootPath);
        var defaults = ParseDefaults(root, RootPath);
        var ops = ParseTxOps(root, RootPath);

        return new TxRequest(scope, title, description, defaults, ops);
    }

    // ---- Scope / at / defaults / title ------------------------------------

    private static Scope ParseScope(JsonElement root, string path,
        HashSet<string> allowed, bool txMethod)
    {
        if (!TryGetField(root, "scope", out var scopeElem))
        {
            return Scope.Main;
        }
        if (scopeElem.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, "scope"));
        }
        var raw = scopeElem.GetString()!;

        if (string.Equals(raw, ScopeNames.Main, StringComparison.Ordinal))
        {
            throw new ApiException(ApiErrorCodes.InvalidScope, JoinKey(path, "scope"));
        }
        if (txMethod && string.Equals(raw, ScopeNames.Hist, StringComparison.Ordinal))
        {
            throw new ApiException(ApiErrorCodes.HistReadOnly, JoinKey(path, "scope"));
        }
        if (!allowed.Contains(raw))
        {
            throw new ApiException(ApiErrorCodes.UnknownScope, JoinKey(path, "scope"));
        }

        return raw switch
        {
            ScopeNames.Usage => Scope.Usage,
            ScopeNames.Scheme => Scope.Scheme,
            ScopeNames.Hist => Scope.Hist,
            _ => Scope.Main,
        };
    }

    private static AtClause? ParseAt(JsonElement root, string path,
        Scope scope, bool txMethod)
    {
        if (!TryGetField(root, "at", out var atElem))
        {
            return null;
        }
        var atPath = JoinKey(path, "at");

        if (txMethod)
        {
            throw new ApiException(ApiErrorCodes.AtNotApplicable, atPath,
                new Dictionary<string, object?> { ["reason"] = "tx_method" });
        }
        if (scope == Scope.Hist)
        {
            throw new ApiException(ApiErrorCodes.AtNotApplicable, atPath,
                new Dictionary<string, object?> { ["reason"] = "hist_scope" });
        }

        return atElem.ValueKind switch
        {
            JsonValueKind.String => new AtClause(atElem.GetString()!, Inclusive: true),
            JsonValueKind.Object => ParseAtBeforeObject(atElem, atPath),
            _ => throw new ApiException(ApiErrorCodes.InvalidRequest, atPath),
        };
    }

    private static AtClause ParseAtBeforeObject(JsonElement atElem, string atPath)
    {
        // Объект формы `{ "before": "<tx_id>" }` — ровно один ключ.
        var beforeKeyPath = JoinKey(atPath, "before");
        if (!atElem.TryGetProperty("before", out var beforeElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, beforeKeyPath);
        }
        if (beforeElem.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, beforeKeyPath);
        }
        // Лишние ключи рядом с `before` — invalid_request.
        foreach (var prop in atElem.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "before", StringComparison.Ordinal))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(atPath, prop.Name));
            }
        }
        return new AtClause(beforeElem.GetString()!, Inclusive: false);
    }

    private static Defaults? ParseDefaults(JsonElement root, string path)
    {
        if (!TryGetField(root, "defaults", out var elem))
        {
            return null;
        }
        var dPath = JoinKey(path, "defaults");
        RequireObject(elem, dPath);

        var pathParent = OptionalString(elem, "path_parent", dPath);
        IReadOnlyDictionary<string, string>? mapBindings = null;
        if (TryGetField(elem, "map_bindings", out var mbElem))
        {
            mapBindings = ParseMapBindingsStringValues(mbElem, JoinKey(dPath, "map_bindings"));
        }
        return new Defaults(pathParent, mapBindings);
    }

    private static string ParseTxTitle(JsonElement root, string path)
    {
        if (!TryGetField(root, "title", out var titleElem))
        {
            throw new ApiException(ApiErrorCodes.InvalidTxTitle, JoinKey(path, "title"));
        }
        if (titleElem.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiErrorCodes.InvalidTxTitle, JoinKey(path, "title"));
        }
        var title = titleElem.GetString() ?? string.Empty;
        if (title.Length == 0 || title.AsSpan().Trim().IsEmpty)
        {
            throw new ApiException(ApiErrorCodes.InvalidTxTitle, JoinKey(path, "title"));
        }
        // Лимит «≤ 100 токенов» — семантика executor-а (tokenizer ещё нет).
        return title;
    }

    // ---- Read ops ---------------------------------------------------------

    private static IReadOnlyList<ReadOp> ParseReadOps(JsonElement root, Scope scope,
        bool atSet, string path)
    {
        var arr = RequireArray(root, "ops", path);
        var opsPath = JoinKey(path, "ops");
        var list = new List<ReadOp>();
        var i = 0;
        foreach (var opElem in arr.EnumerateArray())
        {
            var opPath = JoinIndex(opsPath, i);
            list.Add(ParseReadOp(opElem, scope, atSet, opPath));
            i++;
        }
        return list;
    }

    private static ReadOp ParseReadOp(JsonElement op, Scope scope, bool atSet, string opPath)
    {
        if (op.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidOp, opPath);
        }
        var keys = CollectOpKeys(op);
        if (keys.Count != 1)
        {
            throw new ApiException(ApiErrorCodes.InvalidOp, opPath);
        }
        var key = keys[0];
        if (!string.Equals(key, "select", StringComparison.Ordinal))
        {
            throw new ApiException(ApiErrorCodes.UnknownOp, JoinKey(opPath, key));
        }
        var selectElem = op.GetProperty("select");
        var selectPath = JoinKey(opPath, "select");

        if (selectElem.ValueKind == JsonValueKind.String)
        {
            var modeName = selectElem.GetString()!;
            if (!KernelModes.Contains(modeName))
            {
                throw new ApiException(ApiErrorCodes.UnknownSelectMode, selectPath);
            }
            if (atSet)
            {
                // `at` + `select: "meta"` → at_not_applicable reason=meta_select.
                throw new ApiException(ApiErrorCodes.AtNotApplicable, JoinKey(RootPath, "at"),
                    new Dictionary<string, object?> { ["reason"] = "meta_select" });
            }
            return new SelectKernelModeOp(modeName);
        }
        if (selectElem.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, selectPath);
        }
        return ParseSelectByPredicate(selectElem, scope, selectPath);
    }

    private static SelectByPredicateOp ParseSelectByPredicate(JsonElement select,
        Scope scope, string path)
    {
        // selector — required
        if (!TryGetField(select, "selector", out var selElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, "selector"));
        }
        var selPath = JoinKey(path, "selector");
        var selector = scope == Scope.Hist
            ? (Selector)ParseHistSelector(selElem, selPath)
            : ParseDataSelector(selElem, selPath);

        int? maxTokens = null;
        if (TryGetField(select, "max_tokens", out var mtElem))
        {
            var mtPath = JoinKey(path, "max_tokens");
            if (mtElem.ValueKind != JsonValueKind.Number || !mtElem.TryGetInt32(out var mt))
            {
                throw new ApiException(ApiErrorCodes.InvalidMaxTokens, mtPath);
            }
            if (mt <= 0)
            {
                throw new ApiException(ApiErrorCodes.InvalidMaxTokens, mtPath);
            }
            maxTokens = mt;
        }

        IReadOnlyList<string>? include = null;
        if (TryGetField(select, "include", out var incElem))
        {
            include = ParseStringArray(incElem, JoinKey(path, "include"));
        }

        string? alias = OptionalString(select, "as", path);

        // Лишние ключи внутри select → invalid_request.
        foreach (var prop in select.EnumerateObject())
        {
            if (prop.Name is not ("selector" or "include" or "max_tokens" or "as"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest,
                    JoinKey(path, prop.Name));
            }
        }

        return new SelectByPredicateOp(selector, include, maxTokens, alias);
    }

    // ---- Tx ops -----------------------------------------------------------

    private static IReadOnlyList<TxOp> ParseTxOps(JsonElement root, string path)
    {
        var arr = RequireArray(root, "ops", path);
        var opsPath = JoinKey(path, "ops");
        var list = new List<TxOp>();
        var i = 0;
        foreach (var opElem in arr.EnumerateArray())
        {
            var opPath = JoinIndex(opsPath, i);
            list.Add(ParseTxOp(opElem, opPath));
            i++;
        }
        return list;
    }

    private static TxOp ParseTxOp(JsonElement op, string opPath)
    {
        if (op.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidOp, opPath);
        }
        var keys = CollectOpKeys(op);
        if (keys.Count != 1)
        {
            throw new ApiException(ApiErrorCodes.InvalidOp, opPath);
        }
        var key = keys[0];
        var body = op.GetProperty(key);
        var bodyPath = JoinKey(opPath, key);

        return key switch
        {
            "create" => ParseCreate(body, bodyPath),
            "update" => ParseUpdate(body, bodyPath),
            "move" => ParseMove(body, bodyPath),
            "delete" => ParseDelete(body, bodyPath),
            "link" => ParseLink(body, bodyPath),
            "unlink" => ParseUnlink(body, bodyPath),
            "rollback" => ParseRollback(body, bodyPath),
            _ => throw new ApiException(ApiErrorCodes.UnknownOp, bodyPath),
        };
    }

    private static CreateOp ParseCreate(JsonElement body, string path)
    {
        RequireObject(body, path);

        var rawPath = RequireString(body, "path", path);
        var pathField = JoinKey(path, "path");
        ValidateTitleFromPath(rawPath, pathField);

        string? alias = OptionalString(body, "as", path);

        NodeSet set;
        if (TryGetField(body, "set", out var setElem))
        {
            set = ParseNodeSet(setElem, JoinKey(path, "set"));
        }
        else
        {
            set = new NodeSet(null, null, null, null);
        }

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is not ("path" or "as" or "set"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new CreateOp(rawPath, alias, set);
    }

    private static UpdateOp ParseUpdate(JsonElement body, string path)
    {
        RequireObject(body, path);

        var id = RequireString(body, "id", path);
        var expectedVersion = RequireLong(body, "expected_version", path);
        if (!TryGetField(body, "set", out var setElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, "set"));
        }
        var setPath = JoinKey(path, "set");
        RequireObject(setElem, setPath);

        string? title = OptionalString(setElem, "title", setPath);
        string? content = OptionalString(setElem, "content", setPath);
        // Хотя бы одно поле обязательно.
        if (title is null && content is null)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, setPath);
        }
        if (title is not null)
        {
            ValidateBareTitle(title, JoinKey(setPath, "title"));
        }
        foreach (var prop in setElem.EnumerateObject())
        {
            if (prop.Name is not ("title" or "content"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(setPath, prop.Name));
            }
        }
        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is not ("id" or "expected_version" or "set"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new UpdateOp(id, expectedVersion, new UpdateSet(title, content));
    }

    private static MoveOp ParseMove(JsonElement body, string path)
    {
        RequireObject(body, path);
        if (!TryGetField(body, "selector", out var selElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, "selector"));
        }
        var selector = ParseDataSelector(selElem, JoinKey(path, "selector"));

        if (!TryGetField(body, "to", out var toElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, "to"));
        }
        var to = ParseMoveTo(toElem, JoinKey(path, "to"));
        var expectedCount = RequireLong(body, "expected_count", path);

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is not ("selector" or "to" or "expected_count"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new MoveOp(selector, to, expectedCount);
    }

    private static DeleteOp ParseDelete(JsonElement body, string path)
    {
        RequireObject(body, path);

        var hasIds = TryGetField(body, "ids", out var idsElem);
        var hasSel = TryGetField(body, "selector", out var selElem);
        if (hasIds == hasSel)
        {
            // Оба заданы или оба отсутствуют → invalid_request.
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }

        IReadOnlyList<string>? ids = null;
        DataSelector? sel = null;
        if (hasIds)
        {
            ids = ParseStringArray(idsElem, JoinKey(path, "ids"));
            if (ids.Count == 0)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, "ids"));
            }
        }
        else
        {
            sel = ParseDataSelector(selElem, JoinKey(path, "selector"));
        }
        var expectedCount = RequireLong(body, "expected_count", path);

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is not ("ids" or "selector" or "expected_count"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new DeleteOp(ids, sel, expectedCount);
    }

    private static LinkOp ParseLink(JsonElement body, string path)
    {
        var (name, from, to, count) = ParseLinkPayload(body, path);
        return new LinkOp(name, from, to, count);
    }

    private static UnlinkOp ParseUnlink(JsonElement body, string path)
    {
        var (name, from, to, count) = ParseLinkPayload(body, path);
        return new UnlinkOp(name, from, to, count);
    }

    private static (string Name, Endpoint From, Endpoint To, long Count) ParseLinkPayload(
        JsonElement body, string path)
    {
        RequireObject(body, path);

        var name = RequireString(body, "name", path);
        if (!TryGetField(body, "from", out var fromElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, "from"));
        }
        if (!TryGetField(body, "to", out var toElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, "to"));
        }
        var from = ParseEndpoint(fromElem, JoinKey(path, "from"));
        var to = ParseEndpoint(toElem, JoinKey(path, "to"));
        var expectedCount = RequireLong(body, "expected_count", path);

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is not ("name" or "from" or "to" or "expected_count"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return (name, from, to, expectedCount);
    }

    private static RollbackOp ParseRollback(JsonElement body, string path)
    {
        return body.ValueKind switch
        {
            JsonValueKind.String => new RollbackOp(body.GetString()!),
            JsonValueKind.Object => ParseRollbackObject(body, path),
            _ => throw new ApiException(ApiErrorCodes.InvalidRequest, path),
        };
    }

    private static RollbackOp ParseRollbackObject(JsonElement body, string path)
    {
        var id = RequireString(body, "id", path);
        foreach (var prop in body.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "id", StringComparison.Ordinal))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new RollbackOp(id);
    }

    // ---- NodeSet / UpdateSet / MoveTo / LinkCreate ------------------------

    private static NodeSet ParseNodeSet(JsonElement set, string path)
    {
        RequireObject(set, path);

        string? title = OptionalString(set, "title", path);
        if (title is not null)
        {
            ValidateBareTitle(title, JoinKey(path, "title"));
        }
        string? content = OptionalString(set, "content", path);

        IReadOnlyDictionary<string, string>? mapBindings = null;
        if (TryGetField(set, "map_bindings", out var mbElem))
        {
            mapBindings = ParseCreateMapBindings(mbElem, JoinKey(path, "map_bindings"));
        }

        IReadOnlyList<LinkCreate>? links = null;
        if (TryGetField(set, "links", out var linksElem))
        {
            links = ParseLinkCreateArray(linksElem, JoinKey(path, "links"));
        }

        foreach (var prop in set.EnumerateObject())
        {
            if (prop.Name is not ("title" or "content" or "map_bindings" or "links"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new NodeSet(title, content, mapBindings, links);
    }

    private static IReadOnlyList<LinkCreate> ParseLinkCreateArray(JsonElement elem, string path)
    {
        if (elem.ValueKind != JsonValueKind.Array)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }
        var list = new List<LinkCreate>();
        var i = 0;
        foreach (var item in elem.EnumerateArray())
        {
            var itemPath = JoinIndex(path, i);
            RequireObject(item, itemPath);
            var name = RequireString(item, "name", itemPath);
            if (!TryGetField(item, "to", out var toElem))
            {
                throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(itemPath, "to"));
            }
            var to = ParseEndpoint(toElem, JoinKey(itemPath, "to"));
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name is not ("name" or "to"))
                {
                    throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(itemPath, prop.Name));
                }
            }
            list.Add(new LinkCreate(name, to));
            i++;
        }
        return list;
    }

    private static MoveTo ParseMoveTo(JsonElement elem, string path)
    {
        RequireObject(elem, path);

        string? parentPath = OptionalString(elem, "parent_path", path);

        IReadOnlyDictionary<string, string?>? mapBindings = null;
        if (TryGetField(elem, "map_bindings", out var mbElem))
        {
            mapBindings = ParseMoveMapBindings(mbElem, JoinKey(path, "map_bindings"));
        }

        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name is not ("parent_path" or "map_bindings"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new MoveTo(parentPath, mapBindings);
    }

    // ---- Selectors --------------------------------------------------------

    private static DataSelector ParseDataSelector(JsonElement elem, string path)
    {
        RequireObject(elem, path);

        IReadOnlyList<string>? ids = null;
        if (TryGetField(elem, "id", out var idElem))
        {
            ids = ParseIdOrIdArray(idElem, JoinKey(path, "id"));
        }
        string? pathPattern = OptionalString(elem, "path", path);
        string? title = OptionalString(elem, "title", path);

        IReadOnlyDictionary<string, string>? mapBindings = null;
        if (TryGetField(elem, "map_bindings", out var mbElem))
        {
            mapBindings = ParseMapBindingsStringValues(mbElem, JoinKey(path, "map_bindings"));
        }

        MatchClause? match = null;
        if (TryGetField(elem, "match", out var matchElem))
        {
            match = ParseMatchClause(matchElem, JoinKey(path, "match"), isHist: false);
        }

        LinkClause? links = null;
        if (TryGetField(elem, "links", out var linksElem))
        {
            links = ParseLinkClause(linksElem, JoinKey(path, "links"));
        }

        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name is not ("id" or "path" or "title" or "map_bindings" or "match" or "links"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new DataSelector(ids, pathPattern, title, mapBindings, match, links);
    }

    private static HistSelector ParseHistSelector(JsonElement elem, string path)
    {
        RequireObject(elem, path);

        IReadOnlyList<string>? ids = null;
        if (TryGetField(elem, "id", out var idElem))
        {
            ids = ParseIdOrIdArray(idElem, JoinKey(path, "id"));
        }

        var (title, titleMatch) = ParseExactOrShortFormMatch(elem, "title", path, "title", isHist: true);
        var (date, dateMatch) = ParseExactOrShortFormMatch(elem, "date", path, "date", isHist: true);
        var (descr, descrMatch) = ParseExactOrShortFormMatch(elem, "description", path, "description", isHist: true);

        string? rollbackOf = OptionalString(elem, "rollback_of", path);
        string? txScope = null;
        if (TryGetField(elem, "tx_scope", out var txScopeElem))
        {
            var txScopePath = JoinKey(path, "tx_scope");
            if (txScopeElem.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, txScopePath);
            }
            var raw = txScopeElem.GetString()!;
            if (!TxScopeWireValues.Contains(raw))
            {
                throw new ApiException(ApiErrorCodes.UnknownScope, txScopePath);
            }
            txScope = raw;
        }

        string? touchesNodeId = null;
        if (TryGetField(elem, "touches_node", out var tnElem))
        {
            touchesNodeId = ParseTouchesNode(tnElem, JoinKey(path, "touches_node"));
        }

        LinkIdentity? touchesLink = null;
        if (TryGetField(elem, "touches_link", out var tlElem))
        {
            touchesLink = ParseTouchesLink(tlElem, JoinKey(path, "touches_link"));
        }

        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name is not ("id" or "title" or "date" or "description"
                or "rollback_of" or "tx_scope" or "touches_node" or "touches_link"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new HistSelector(ids, title, titleMatch, date, dateMatch,
            descr, descrMatch, rollbackOf, txScope, touchesNodeId, touchesLink);
    }

    private static (string? Exact, MatchClause? Match) ParseExactOrShortFormMatch(
        JsonElement parent, string key, string parentPath, string fieldName, bool isHist)
    {
        if (!TryGetField(parent, key, out var v))
        {
            return (null, null);
        }
        var vPath = JoinKey(parentPath, key);
        if (v.ValueKind == JsonValueKind.String)
        {
            return (v.GetString(), null);
        }
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, vPath);
        }
        // short-form `{ match: { ... } }` — ровно один ключ `match`.
        if (!v.TryGetProperty("match", out var matchElem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(vPath, "match"));
        }
        foreach (var prop in v.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "match", StringComparison.Ordinal))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(vPath, prop.Name));
            }
        }
        var parsed = ParseMatchClause(matchElem, JoinKey(vPath, "match"), isHist);
        if (parsed.Fields is null)
        {
            parsed = parsed with { Fields = new[] { fieldName } };
        }
        return (null, parsed);
    }

    private static string ParseTouchesNode(JsonElement elem, string path)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString()!,
            JsonValueKind.Object => RequireString(elem, "id", path),
            _ => throw new ApiException(ApiErrorCodes.InvalidRequest, path),
        };
    }

    private static LinkIdentity ParseTouchesLink(JsonElement elem, string path)
    {
        RequireObject(elem, path);
        var name = RequireString(elem, "name", path);
        var from = RequireString(elem, "from", path);
        var to = RequireString(elem, "to", path);
        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name is not ("name" or "from" or "to"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new LinkIdentity(name, from, to);
    }

    // ---- Endpoint / LinkClause / Match -----------------------------------

    private static Endpoint ParseEndpoint(JsonElement elem, string path)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return new IdEndpoint(elem.GetString()!);
        }
        if (elem.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }
        // Объект должен иметь ровно один из ключей: id / ids / selector / alias.
        var key = SingleEndpointKey(elem, path);
        return key switch
        {
            "id" => new IdEndpoint(RequireString(elem, "id", path)),
            "ids" => new IdsEndpoint(ParseStringArray(elem.GetProperty("ids"), JoinKey(path, "ids"))),
            "selector" => new SelectorEndpoint(
                ParseDataSelector(elem.GetProperty("selector"), JoinKey(path, "selector"))),
            "alias" => new AliasEndpoint(RequireString(elem, "alias", path)),
            _ => throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, key)),
        };
    }

    private static string SingleEndpointKey(JsonElement elem, string path)
    {
        var found = (string?)null;
        var count = 0;
        foreach (var prop in elem.EnumerateObject())
        {
            count++;
            found = prop.Name;
            if (count > 1)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, path);
            }
        }
        if (count == 0)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }
        return found!;
    }

    private static LinkClause ParseLinkClause(JsonElement elem, string path)
    {
        RequireObject(elem, path);
        string? name = OptionalString(elem, "name", path);
        LinkEndpointClause? from = null;
        LinkEndpointClause? to = null;
        if (TryGetField(elem, "from", out var fromElem))
        {
            from = ParseLinkEndpointClause(fromElem, JoinKey(path, "from"));
        }
        if (TryGetField(elem, "to", out var toElem))
        {
            to = ParseLinkEndpointClause(toElem, JoinKey(path, "to"));
        }
        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name is not ("name" or "from" or "to"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new LinkClause(name, from, to);
    }

    private static LinkEndpointClause ParseLinkEndpointClause(JsonElement elem, string path)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return new LinkEndpointClause(elem.GetString()!, null);
        }
        if (elem.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }
        return new LinkEndpointClause(null, ParseDataSelector(elem, path));
    }

    private static MatchClause ParseMatchClause(JsonElement elem, string path, bool isHist)
    {
        RequireObject(elem, path);
        var regex = RequireString(elem, "regex", path);
        var regexPath = JoinKey(path, "regex");
        if (regex.Length == 0)
        {
            throw new ApiException(ApiErrorCodes.InvalidMatchRegex, regexPath);
        }
        try
        {
            _ = new Regex(regex, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            throw new ApiException(ApiErrorCodes.InvalidMatchRegex, regexPath);
        }

        IReadOnlyList<string>? fields = null;
        if (TryGetField(elem, "fields", out var fieldsElem))
        {
            var allowed = isHist ? HistMatchFields : DataMatchFields;
            if (fieldsElem.ValueKind != JsonValueKind.Array)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, "fields"));
            }
            var list = new List<string>();
            var i = 0;
            foreach (var item in fieldsElem.EnumerateArray())
            {
                var itemPath = JoinIndex(JoinKey(path, "fields"), i);
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ApiException(ApiErrorCodes.InvalidMatchFields, itemPath);
                }
                var s = item.GetString()!;
                if (!allowed.Contains(s))
                {
                    throw new ApiException(ApiErrorCodes.InvalidMatchFields, itemPath);
                }
                list.Add(s);
                i++;
            }
            fields = list;
        }

        var caseSensitive = false;
        if (TryGetField(elem, "case_sensitive", out var csElem))
        {
            var csPath = JoinKey(path, "case_sensitive");
            caseSensitive = csElem.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ApiException(ApiErrorCodes.InvalidRequest, csPath),
            };
        }

        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name is not ("regex" or "fields" or "case_sensitive"))
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, prop.Name));
            }
        }
        return new MatchClause(regex, fields, caseSensitive);
    }

    // ---- map_bindings / id arrays / string arrays ------------------------

    private static IReadOnlyDictionary<string, string> ParseMapBindingsStringValues(
        JsonElement elem, string path)
    {
        RequireObject(elem, path);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in elem.EnumerateObject())
        {
            var vPath = JoinKey(path, prop.Name);
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, vPath);
            }
            dict[prop.Name] = prop.Value.GetString()!;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, string> ParseCreateMapBindings(
        JsonElement elem, string path)
    {
        RequireObject(elem, path);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in elem.EnumerateObject())
        {
            var vPath = JoinKey(path, prop.Name);
            // create.set.map_bindings: null запрещён (invalid_map_binding_value).
            if (prop.Value.ValueKind == JsonValueKind.Null)
            {
                throw new ApiException(ApiErrorCodes.InvalidMapBindingValue, vPath);
            }
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, vPath);
            }
            dict[prop.Name] = prop.Value.GetString()!;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, string?> ParseMoveMapBindings(
        JsonElement elem, string path)
    {
        RequireObject(elem, path);
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var prop in elem.EnumerateObject())
        {
            var vPath = JoinKey(path, prop.Name);
            if (prop.Value.ValueKind == JsonValueKind.Null)
            {
                dict[prop.Name] = null;
                continue;
            }
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, vPath);
            }
            dict[prop.Name] = prop.Value.GetString();
        }
        return dict;
    }

    private static IReadOnlyList<string> ParseIdOrIdArray(JsonElement elem, string path)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return new[] { elem.GetString()! };
        }
        if (elem.ValueKind == JsonValueKind.Array)
        {
            return ParseStringArray(elem, path);
        }
        throw new ApiException(ApiErrorCodes.InvalidRequest, path);
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement elem, string path)
    {
        if (elem.ValueKind != JsonValueKind.Array)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }
        var list = new List<string>();
        var i = 0;
        foreach (var item in elem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiErrorCodes.InvalidRequest, JoinIndex(path, i));
            }
            list.Add(item.GetString()!);
            i++;
        }
        return list;
    }

    // ---- Path-title validation -------------------------------------------

    private static void ValidateTitleFromPath(string fullPath, string pathField)
    {
        // Последний сегмент path должен подходить под NodeTitleRegex.
        if (fullPath.Length == 0)
        {
            throw new ApiException(ApiErrorCodes.InvalidNodeTitle, pathField);
        }
        var lastSlash = fullPath.LastIndexOf('/');
        var title = lastSlash >= 0 ? fullPath[(lastSlash + 1)..] : fullPath;
        ValidateBareTitle(title, pathField);
    }

    private static void ValidateBareTitle(string title, string path)
    {
        if (title.Length == 0 || !NodeTitleRegex.IsMatch(title))
        {
            throw new ApiException(ApiErrorCodes.InvalidNodeTitle, path);
        }
    }

    // ---- Low-level helpers -----------------------------------------------

    private static JsonDocument ParseJsonOrThrow(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ApiException(ApiErrorCodes.InvalidJson, RootPath);
        }
    }

    private static void RequireObject(JsonElement elem, string path)
    {
        if (elem.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, path);
        }
    }

    private static JsonElement RequireArray(JsonElement root, string key, string path)
    {
        if (!TryGetField(root, key, out var elem))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(path, key));
        }
        if (elem.ValueKind != JsonValueKind.Array)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(path, key));
        }
        return elem;
    }

    private static bool TryGetField(JsonElement root, string key, out JsonElement value)
    {
        if (!root.TryGetProperty(key, out value))
        {
            return false;
        }
        // Null трактуем как отсутствие — упрощает optional-разбор.
        if (value.ValueKind == JsonValueKind.Null)
        {
            value = default;
            return false;
        }
        return true;
    }

    private static string RequireString(JsonElement parent, string key, string parentPath)
    {
        if (!TryGetField(parent, key, out var v))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(parentPath, key));
        }
        if (v.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(parentPath, key));
        }
        return v.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string key, string parentPath)
    {
        if (!TryGetField(parent, key, out var v))
        {
            return null;
        }
        if (v.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(parentPath, key));
        }
        return v.GetString();
    }

    private static long RequireLong(JsonElement parent, string key, string parentPath)
    {
        if (!TryGetField(parent, key, out var v))
        {
            throw new ApiException(ApiErrorCodes.MissingRequiredField, JoinKey(parentPath, key));
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
        {
            throw new ApiException(ApiErrorCodes.InvalidRequest, JoinKey(parentPath, key));
        }
        return n;
    }

    private static List<string> CollectOpKeys(JsonElement op)
    {
        var keys = new List<string>(1);
        foreach (var prop in op.EnumerateObject())
        {
            keys.Add(prop.Name);
        }
        return keys;
    }

    private static string JoinKey(string parent, string key)
        => string.Concat(parent, ".", key);

    private static string JoinIndex(string parent, int i)
        => string.Concat(parent, "[", i.ToString(CultureInfo.InvariantCulture), "]");
}
