namespace DocsWalker.Core.Server;

/// <summary>
/// Ambient-контекст текущего запроса сервера. Хранится в <see cref="AsyncLocal{T}"/>
/// и устанавливается <see cref="IpcServer"/> до вызова диспетчера, очищается после.
/// Сейчас несёт только <see cref="SessionId"/> — UUID LLM-сессии (docs/DocsWalker.yml
/// #322, #342); следующие шаги стратегии stg-0005 будут читать его из обработчиков
/// для seen-фильтрации, write-инвалидации, auto-include.
/// </summary>
public sealed class RequestContext
{
    private static readonly AsyncLocal<RequestContext?> _current = new();

    public static RequestContext? Current => _current.Value;

    public string? SessionId { get; }

    private RequestContext(string? sessionId)
    {
        SessionId = sessionId;
    }

    /// <summary>
    /// Устанавливает контекст на текущий async-flow. Возвращает <see cref="IDisposable"/>,
    /// который восстанавливает предыдущее значение при <c>Dispose</c>. Вызывать обёрнутым
    /// в <c>using</c> вокруг каждого вызова диспетчера на сервере.
    /// </summary>
    public static IDisposable Push(string? sessionId)
    {
        var prev = _current.Value;
        _current.Value = new RequestContext(sessionId);
        return new Scope(prev);
    }

    private sealed class Scope : IDisposable
    {
        private readonly RequestContext? _prev;
        private bool _disposed;

        public Scope(RequestContext? prev) { _prev = prev; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = _prev;
        }
    }
}
