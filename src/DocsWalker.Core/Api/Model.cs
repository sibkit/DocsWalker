namespace DocsWalker.Core.Api;

/// <summary>
/// 4-scope-модель из api/model.md.
/// </summary>
public enum Scope
{
    Main = 0,
    Usage = 1,
    Scheme = 2,
    Hist = 3,
}

/// <summary>
/// Wire-имена scope (как в JSON). `main` в JSON не передаётся — это
/// default; явная передача — `invalid_scope`. Хелпер для двунаправленного
/// маппинга.
/// </summary>
public static class ScopeNames
{
    public const string Main = "main";
    public const string Usage = "usage";
    public const string Scheme = "scheme";
    public const string Hist = "hist";

    public static string ToWire(Scope scope) => scope switch
    {
        Scope.Main => Main,
        Scope.Usage => Usage,
        Scope.Scheme => Scheme,
        Scope.Hist => Hist,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

/// <summary>
/// Общие defaults запроса: `path_parent` (префикс для всех путей),
/// `map_bindings` (применяется к create.set.map_bindings).
/// </summary>
public sealed record Defaults(
    string? PathParent,
    IReadOnlyDictionary<string, string>? MapBindings);

/// <summary>
/// `selector.match` — regex-фильтр по текстовым полям. Список допустимых
/// `fields` зависит от класса узла (data: title|content; event: title|
/// description|date); парсер уже отфильтровал лишнее на стадии разбора.
/// </summary>
public sealed record MatchClause(
    string Regex,
    IReadOnlyList<string>? Fields,
    bool CaseSensitive);

/// <summary>
/// Ограничение data-узла по incident links: `selector.links`. От `Endpoint`
/// отличается набором допустимых форм (только id-строка или вложенный
/// DataSelector — без alias/ids).
/// </summary>
public sealed record LinkClause(
    string? Name,
    LinkEndpointClause? From,
    LinkEndpointClause? To);

/// <summary>
/// Эндпоинт в `selector.links.from/to`: либо id-строка, либо вложенный
/// DataSelector (один уровень вложения). Из двух полей всегда заполнено
/// ровно одно — гарантирует парсер.
/// </summary>
public sealed record LinkEndpointClause(
    string? Id,
    DataSelector? Selector);

/// <summary>
/// Общая база для двух типов селекторов: data (main/usage/scheme) и hist.
/// </summary>
public abstract record Selector;

/// <summary>
/// Селектор по data-узлу. Применим в read для main/usage/scheme и во всех
/// write-ops `tx`.
/// </summary>
public sealed record DataSelector(
    IReadOnlyList<string>? Ids,
    string? Path,
    string? Title,
    IReadOnlyDictionary<string, string>? MapBindings,
    MatchClause? Match,
    LinkClause? Links) : Selector;

/// <summary>
/// Identity link: tuple (name, from, to). Используется в hist-селекторе
/// `touches_link` (точное совпадение).
/// </summary>
public sealed record LinkIdentity(string Name, string From, string To);

/// <summary>
/// Селектор по event-узлу (hist). Применим только в `read scope=hist`.
/// Поля `Title`/`Date`/`Description` хранят либо exact-строку, либо
/// MatchClause (per-field short form для regex) — заполнено максимум одно
/// из двух парных полей.
/// </summary>
public sealed record HistSelector(
    IReadOnlyList<string>? Ids,
    string? Title,
    MatchClause? TitleMatch,
    string? Date,
    MatchClause? DateMatch,
    string? Description,
    MatchClause? DescriptionMatch,
    string? RollbackOf,
    string? TxScope,
    string? TouchesNodeId,
    LinkIdentity? TouchesLink) : Selector;

/// <summary>
/// Эндпоинт для `tx.link.from/to`, `tx.unlink.from/to`,
/// `create.set.links[].to`. 4 варианта (Id / Ids / Selector / Alias) —
/// dispatch по подтипу.
/// </summary>
public abstract record Endpoint;

public sealed record IdEndpoint(string Id) : Endpoint;
public sealed record IdsEndpoint(IReadOnlyList<string> Ids) : Endpoint;
public sealed record SelectorEndpoint(DataSelector Selector) : Endpoint;
public sealed record AliasEndpoint(string Alias) : Endpoint;

/// <summary>
/// `at` темпорального чтения. `Inclusive=true` для short form
/// (`at: "<tx_id>"`), `false` для explicit `{before: "<tx_id>"}`.
/// </summary>
public sealed record AtClause(string TxId, bool Inclusive);

/// <summary>
/// Базовый тип операции `read.ops[]`: единственный вариант сейчас —
/// `select`, в двух формах (по селектору / kernel-mode).
/// </summary>
public abstract record ReadOp;

/// <summary>
/// `select` в форме-объект: predicate-чтение узлов из выбранного scope.
/// </summary>
public sealed record SelectByPredicateOp(
    Selector Selector,
    IReadOnlyList<string>? Include,
    int? MaxTokens,
    string? Alias) : ReadOp;

/// <summary>
/// `select` в форме-строка: чтение kernel-mode данных. На сегодня
/// единственный режим — `"meta"`.
/// </summary>
public sealed record SelectKernelModeOp(string ModeName) : ReadOp;

/// <summary>
/// Аргументы вызова `read`.
/// </summary>
public sealed record ReadRequest(
    Scope Scope,
    Defaults? Defaults,
    AtClause? At,
    IReadOnlyList<ReadOp> Ops);

/// <summary>
/// `create.set` — полный начальный snapshot нового узла.
/// </summary>
public sealed record NodeSet(
    string? Title,
    string? Content,
    IReadOnlyDictionary<string, string>? MapBindings,
    IReadOnlyList<LinkCreate>? Links);

/// <summary>
/// Элемент `create.set.links[]`: исходящий link от создаваемого узла.
/// </summary>
public sealed record LinkCreate(string Name, Endpoint To);

/// <summary>
/// `update.set` — допускается только `title` и/или `content`.
/// </summary>
public sealed record UpdateSet(string? Title, string? Content);

/// <summary>
/// `move.to` — partial spec для bulk-move. `MapBindings` хранит
/// значение string (set/replace) или null (tombstone «снять») — отражает
/// partial-merge семантику из api/tx.md.
/// </summary>
public sealed record MoveTo(
    string? ParentPath,
    IReadOnlyDictionary<string, string?>? MapBindings);

/// <summary>
/// Базовый тип операции `tx.ops[]`: 7 вариантов (`create`/`update`/
/// `move`/`delete`/`link`/`unlink`/`rollback`).
/// </summary>
public abstract record TxOp;

public sealed record CreateOp(string Path, string? Alias, NodeSet Set) : TxOp;

public sealed record UpdateOp(string Id, long ExpectedVersion, UpdateSet Set) : TxOp;

public sealed record MoveOp(DataSelector Selector, MoveTo To, long ExpectedCount) : TxOp;

/// <summary>
/// `delete` — `Ids` XOR `Selector` (ровно одно), enforced парсером.
/// </summary>
public sealed record DeleteOp(
    IReadOnlyList<string>? Ids,
    DataSelector? Selector,
    long ExpectedCount) : TxOp;

public sealed record LinkOp(
    string Name,
    Endpoint From,
    Endpoint To,
    long ExpectedCount) : TxOp;

public sealed record UnlinkOp(
    string Name,
    Endpoint From,
    Endpoint To,
    long ExpectedCount) : TxOp;

public sealed record RollbackOp(string TxId) : TxOp;

/// <summary>
/// Аргументы вызова `tx`.
/// </summary>
public sealed record TxRequest(
    Scope Scope,
    string Title,
    string? Description,
    Defaults? Defaults,
    IReadOnlyList<TxOp> Ops);
