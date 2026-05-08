using DocsWalker.Core.Server;
using DocsWalker.Core.Sessions;

namespace DocsWalker.Core.Api;

/// <summary>
/// Per-request скоуп фильтрации seen-узлов в read-ответе. Создаётся один раз
/// в начале сериализации одного payload, передаётся в <see cref="ReadApiJson"/>,
/// после сборки JSON — <see cref="Commit"/> добавляет все эмитнутые id в
/// общий <see cref="SessionState"/>.
/// <para>
/// Различает два класса узлов в одном ответе:
/// <list type="bullet">
///   <item>прямые id (root subtree, элементы --ids) — никогда не фильтруются по
///         seen-set между запросами (#346); внутри одного ответа это уже
///         неактуально, так как каждый прямой id эмитится один раз;</item>
///   <item>транзитивные узлы (children поддерева, auto-include-цели) — фильтруются
///         через <see cref="ShouldHideTransitive"/> и при попадании в seen-set
///         или повторном упоминании в одном ответе становятся placeholder'ом
///         (#344).</item>
/// </list>
/// </para>
/// <para>
/// Скоуп консистентен на всё время одного запроса: <see cref="ShouldHideTransitive"/>
/// сравнивает с снимком seen-set, сделанным в конструкторе. Это гарантирует, что
/// узел, эмитнутый полным в первом упоминании, в этом же ответе не вернётся как
/// «уже видел» из глобального state — повторы рулятся локальным
/// <c>_emittedThisResponse</c>.
/// </para>
/// </summary>
public sealed class SeenScope
{
    private readonly SessionState _sessions;
    private readonly Guid _sessionId;
    private readonly HashSet<int> _previouslySeen;
    private readonly HashSet<int> _emittedThisResponse = new();

    private SeenScope(SessionState sessions, Guid sessionId, HashSet<int> previouslySeen)
    {
        _sessions = sessions;
        _sessionId = sessionId;
        _previouslySeen = previouslySeen;
    }

    /// <summary>
    /// Создаёт скоуп из ambient <see cref="RequestContext"/>. Возвращает null,
    /// если в контексте нет валидного <see cref="RequestContext.SessionId"/>
    /// или нет <see cref="RequestContext.Sessions"/> — это «фильтрация не
    /// применяется», payload как до контекст-aware-фичи (#342).
    /// </summary>
    public static SeenScope? FromCurrentContext()
    {
        var ctx = RequestContext.Current;
        if (ctx is null) return null;
        if (string.IsNullOrEmpty(ctx.SessionId)) return null;
        if (ctx.Sessions is null) return null;
        if (!Guid.TryParse(ctx.SessionId, out var sid)) return null;

        var previously = ctx.Sessions.Sessions.TryGetValue(sid, out var set)
            ? new HashSet<int>(set.Ids)
            : new HashSet<int>();
        return new SeenScope(ctx.Sessions, sid, previously);
    }

    /// <summary>
    /// Прямой ручной конструктор для тестов и внутренних вызовов, минующих
    /// ambient <see cref="RequestContext"/>. В рантайме предпочтительно
    /// использовать <see cref="FromCurrentContext"/>.
    /// </summary>
    public static SeenScope Create(SessionState sessions, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var previously = sessions.Sessions.TryGetValue(sessionId, out var set)
            ? new HashSet<int>(set.Ids)
            : new HashSet<int>();
        return new SeenScope(sessions, sessionId, previously);
    }

    /// <summary>
    /// Должен ли транзитивно подтянутый узел стать placeholder'ом? Возвращает
    /// true, если id уже был в seen-set до начала запроса либо уже эмитнут
    /// (полным или placeholder'ом) в этом же ответе.
    /// </summary>
    public bool ShouldHideTransitive(int id) =>
        _previouslySeen.Contains(id) || _emittedThisResponse.Contains(id);

    /// <summary>
    /// Регистрирует id как эмитнутый в этом ответе (полным узлом или placeholder'ом —
    /// неважно, оба идут в seen-set). Должен вызываться сериализатором после
    /// добавления каждого узла в payload.
    /// </summary>
    public void Mark(int id) => _emittedThisResponse.Add(id);

    /// <summary>
    /// В конце сборки payload — записывает все эмитнутые id в общий
    /// <see cref="SessionState"/>. Сессия становится dirty (флэшнётся на
    /// graceful shutdown).
    /// </summary>
    public void Commit(DateTime now) =>
        _sessions.MarkSeen(_sessionId, _emittedThisResponse, now);
}
