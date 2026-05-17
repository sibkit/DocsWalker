namespace DocsWalker.Core.Api;

/// <summary>
/// Машинные коды ошибок из api/errors.md. Сериализуются в envelope
/// `{"code": "...", "details": {...}}` ровно этими строками.
/// </summary>
public static class ApiErrorCodes
{
    // Разбор запроса
    public const string InvalidJson = "invalid_json";
    public const string InvalidRequest = "invalid_request";
    public const string MissingRequiredField = "missing_required_field";
    public const string InvalidOp = "invalid_op";
    public const string UnknownOp = "unknown_op";
    public const string UnknownSelectMode = "unknown_select_mode";
    public const string InvalidScope = "invalid_scope";
    public const string UnknownScope = "unknown_scope";
    public const string AtNotApplicable = "at_not_applicable";
    public const string HistReadOnly = "hist_read_only";
    public const string InvalidTxTitle = "invalid_tx_title";
    public const string InvalidMaxTokens = "invalid_max_tokens";
    public const string InvalidMatchRegex = "invalid_match_regex";
    public const string InvalidMatchFields = "invalid_match_fields";
    public const string MatchTimeout = "match_timeout";
    public const string UnknownAlias = "unknown_alias";
    public const string AmbiguousPathBase = "ambiguous_path_base";
    public const string InvalidNodeTitle = "invalid_node_title";
    public const string InvalidMapBindingValue = "invalid_map_binding_value";

    // Resolve и selectors
    public const string NotFound = "not_found";
    public const string AmbiguousSelector = "ambiguous_selector";
    public const string CountMismatch = "count_mismatch";
    public const string PathParentNotFound = "path_parent_not_found";
    public const string AlreadyExists = "already_exists";
    public const string UnknownMap = "unknown_map";
    public const string UnknownLink = "unknown_link";

    // Cross-scope
    public const string CrossScopeNotAllowed = "cross_scope_not_allowed";
    public const string DeleteBlockedByCrossScopeLink = "delete_blocked_by_cross_scope_link";

    // Concurrency
    public const string VersionMismatch = "version_mismatch";

    // Schema
    public const string ValidationFailed = "validation_failed";
    public const string SchemaBreaksExistingData = "schema_breaks_existing_data";

    // Rollback
    public const string RollbackNotFound = "rollback_not_found";
    public const string RollbackConflict = "rollback_conflict";
    public const string RollbackFailed = "rollback_failed";
    public const string RollbackAlreadyDone = "rollback_already_done";

    // Hist write
    public const string HistWriteFailed = "hist_write_failed";

    // Method dispatch
    public const string UnknownMethod = "unknown_method";
}

/// <summary>
/// Содержимое поля `details` envelope-ошибки. `Path` — JSON-pointer-подобный
/// путь к месту ошибки в запросе (например, `$.ops[0].update.id`). `Extras`
/// — дополнительные ключи, специфичные для кода (например `reason`,
/// `expected`, `current`, `violations`).
/// </summary>
public sealed record ApiErrorDetails(
    string? Path = null,
    IReadOnlyDictionary<string, object?>? Extras = null);

/// <summary>
/// Машиночитаемая ошибка API. Соответствует JSON-envelope из
/// api/surface.md, раздел «Envelope ошибки».
/// </summary>
public sealed record ApiError(string Code, ApiErrorDetails Details);

/// <summary>
/// Прерывает парсинг (и любую другую стадию работы с запросом) с готовой
/// API-ошибкой. <see cref="Exception.Message"/> хранит код для удобства
/// логов; structured-форма — в <see cref="Code"/> и <see cref="Details"/>.
/// </summary>
public sealed class ApiException : Exception
{
    public string Code { get; }
    public ApiErrorDetails Details { get; }

    public ApiException(string code, string? path = null,
        IReadOnlyDictionary<string, object?>? extras = null)
        : base(code)
    {
        Code = code;
        Details = new ApiErrorDetails(path, extras);
    }

    public ApiError ToError() => new(Code, Details);
}
