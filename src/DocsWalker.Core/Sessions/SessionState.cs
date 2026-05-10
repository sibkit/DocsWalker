namespace DocsWalker.Core.Sessions;

/// <summary>
/// In-memory состояние seen-set всех активных LLM-сессий per-root. Не блокирующий
/// контейнер: thread-safety обеспечивается per-root семафором ядра, который
/// сериализует обработку запросов одного root. Persistence — через
/// <see cref="SessionFile"/>. Внешняя checksum-инвалидация ушла вместе с (#359):
/// ядро sole-writer, RAM-граф = диск.
/// </summary>
public sealed class SessionState
{
    /// <summary>
    /// TTL для seen-set: после этого срока с last_used сессия удаляется при GC
    /// в startup сервера. Зафиксировано в спецификации
    /// (<c>docs/DocsWalker.yml</c> §Контекст-aware-выдача, rule «TTL=7d»).
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromDays(7);

    private readonly Dictionary<Guid, SeenSet> _sessions = new();

    /// <summary>Все известные сессии (для persistence и наблюдения).</summary>
    public IReadOnlyDictionary<Guid, SeenSet> Sessions => _sessions;

    /// <summary>
    /// Вернуть существующую или создать новую <see cref="SeenSet"/> для
    /// <paramref name="sessionId"/>. Обновляет <c>last_used</c> и помечает
    /// сессию dirty (требует записи на shutdown).
    /// </summary>
    public SeenSet EnsureSession(Guid sessionId, DateTime now)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            existing.LastUsed = now;
            existing.Dirty = true;
            return existing;
        }
        var set = new SeenSet(created: now, lastUsed: now) { Dirty = true };
        _sessions[sessionId] = set;
        return set;
    }

    /// <summary>
    /// Добавить <paramref name="ids"/> в seen-set сессии. Сессия создаётся
    /// при необходимости. Если хоть один id новый — сессия dirty.
    /// </summary>
    public void MarkSeen(Guid sessionId, IEnumerable<int> ids, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var set = EnsureSession(sessionId, now);
        foreach (var id in ids) set.Ids.Add(id);
    }

    /// <summary>
    /// Разделить <paramref name="ids"/> на уже виденные сессией и новые.
    /// Не модифицирует state. Если сессия не известна — все id попадают в unseen.
    /// </summary>
    public (List<int> Seen, List<int> Unseen) Filter(Guid sessionId, IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var seen = new List<int>();
        var unseen = new List<int>();
        if (!_sessions.TryGetValue(sessionId, out var set))
        {
            unseen.AddRange(ids);
            return (seen, unseen);
        }
        foreach (var id in ids)
        {
            if (set.Ids.Contains(id)) seen.Add(id);
            else unseen.Add(id);
        }
        return (seen, unseen);
    }

    /// <summary>
    /// Удалить <paramref name="ids"/> из seen-set всех известных сессий
    /// (write-invalidation: запись/удаление узла снимает его «seen»-флаг везде).
    /// Каждая затронутая сессия помечается dirty.
    /// </summary>
    public void RemoveFromAll(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idsList = ids as IReadOnlyCollection<int> ?? ids.ToArray();
        if (idsList.Count == 0) return;
        foreach (var (_, set) in _sessions)
        {
            bool changed = false;
            foreach (var id in idsList)
            {
                if (set.Ids.Remove(id)) changed = true;
            }
            if (changed) set.Dirty = true;
        }
    }

    /// <summary>
    /// Сбросить seen-set сессии без её удаления (маркер /clear: первый
    /// <c>get-usage-guide</c> в session_id обнуляет «уже видел»).
    /// Если сессия неизвестна — создаётся пустой dirty SeenSet.
    /// </summary>
    public void ResetSeen(Guid sessionId, DateTime now)
    {
        if (!_sessions.TryGetValue(sessionId, out var set))
        {
            _sessions[sessionId] = new SeenSet(created: now, lastUsed: now) { Dirty = true };
            return;
        }
        set.Ids.Clear();
        set.LastUsed = now;
        set.Dirty = true;
    }

    /// <summary>
    /// Удалить из map все сессии, у которых <c>last_used</c> старше
    /// <paramref name="now"/> − <paramref name="ttl"/>. Возвращает список
    /// удалённых session_id — потребитель удалит соответствующие файлы.
    /// </summary>
    public List<Guid> EvictExpired(TimeSpan ttl, DateTime now)
    {
        var threshold = now - ttl;
        var evicted = new List<Guid>();
        foreach (var (id, set) in _sessions)
        {
            if (set.LastUsed < threshold) evicted.Add(id);
        }
        foreach (var id in evicted) _sessions.Remove(id);
        return evicted;
    }

    /// <summary>
    /// Восстановить сессию из persistence. Отличается от <see cref="EnsureSession"/>
    /// тем, что не помечает dirty (загруженное состояние уже соответствует диску).
    /// Если сессия с таким id уже была — перезаписывается.
    /// </summary>
    public void RestoreSession(Guid sessionId, SeenSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        set.Dirty = false;
        _sessions[sessionId] = set;
    }
}

/// <summary>
/// Состояние одной LLM-сессии: набор уже выданных id, отметки времени,
/// флаг «нужно записать на shutdown».
/// </summary>
public sealed class SeenSet
{
    public HashSet<int> Ids { get; }
    public DateTime Created { get; set; }
    public DateTime LastUsed { get; set; }
    public bool Dirty { get; set; }

    public SeenSet(IEnumerable<int>? ids = null, DateTime? created = null, DateTime? lastUsed = null)
    {
        Ids = ids is null ? new HashSet<int>() : new HashSet<int>(ids);
        var c = created ?? DateTime.UtcNow;
        Created = c;
        LastUsed = lastUsed ?? c;
        Dirty = false;
    }
}
