using DocsWalker.Core.Sessions;

namespace DocsWalker.Core.Server;

/// <summary>
/// Ambient-контекст текущего запроса. Хранится в <see cref="AsyncLocal{T}"/>
/// и устанавливается ядром (<c>RpcDispatcher.ExecuteWithCaptureAsync</c>) до
/// вызова диспетчера, очищается после.
/// Несёт <see cref="SessionId"/> — UUID LLM-сессии (docs/DocsWalker.yml #322, #342) —
/// и ссылку на <see cref="SessionState"/> для read-handlers'ов: seen-фильтрация
/// (#344), reset на guide (#348), write-инвалидация (#358) — все дёргают эту пару из
/// <see cref="Current"/>.
/// </summary>
public sealed class RequestContext
{
    private static readonly AsyncLocal<RequestContext?> _current = new();

    public static RequestContext? Current => _current.Value;

    public string? SessionId { get; }

    /// <summary>
    /// Общий <see cref="SessionState"/> per-root (поднимает ядро). Null означает,
    /// что диспетчер вызван без поднятого ctx (тест, kernel pre-integration) —
    /// handlers пропускают seen-логику.
    /// </summary>
    public SessionState? Sessions { get; }

    private RequestContext(string? sessionId, SessionState? sessions)
    {
        SessionId = sessionId;
        Sessions = sessions;
    }

    /// <summary>
    /// Устанавливает контекст на текущий async-flow. Возвращает <see cref="IDisposable"/>,
    /// который восстанавливает предыдущее значение при <c>Dispose</c>. Вызывать обёрнутым
    /// в <c>using</c> вокруг каждого вызова диспетчера на сервере.
    /// </summary>
    public static IDisposable Push(string? sessionId, SessionState? sessions = null)
    {
        var prev = _current.Value;
        _current.Value = new RequestContext(sessionId, sessions);
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
