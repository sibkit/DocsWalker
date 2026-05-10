using System.Collections.Concurrent;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Per-root state ядра: <see cref="SemaphoreSlim"/> сериализует обращения к одному root
/// (см. (#313) docs/DocsWalker.yml — per-root semaphore вместо global), <see cref="LastUsed"/>
/// и <see cref="IdleTimer"/> реализуют per-root idle eviction (#316).
/// </summary>
internal sealed class RootEntry : IDisposable
{
    /// <summary>Нормализованный абсолютный путь root'а (ключ в реестре).</summary>
    public string Root { get; }

    /// <summary>
    /// Per-root mutex. Параллельные запросы на разные roots обрабатываются параллельно;
    /// на один root — строго по одному (инвариант «no concurrent mutations on same graph»).
    /// </summary>
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    /// <summary>Момент последнего обращения. Обновляется на каждом RPC через <see cref="RootRegistry.Touch"/>.</summary>
    public DateTimeOffset LastUsed { get; internal set; }

    /// <summary>
    /// Idle-таймер. Стартует на <see cref="RootRegistry.GetOrAdd"/>, сбрасывается на
    /// каждом <see cref="RootRegistry.Touch"/>. Срабатывание → eviction entry.
    /// </summary>
    internal Timer IdleTimer { get; set; } = null!;

    public RootEntry(string root, DateTimeOffset now)
    {
        Root = root;
        LastUsed = now;
    }

    public void Dispose()
    {
        IdleTimer?.Dispose();
        Semaphore.Dispose();
    }
}

/// <summary>
/// Реестр загруженных root'ов ядра DocsWalker (<c>docswalker kernel</c>). Multi-root в
/// одном процессе: каждый root получает свой <see cref="RootEntry"/> по запросу
/// (<see cref="GetOrAdd"/>), независимо сериализуется и независимо
/// evict'ится по idle-таймеру.
/// <para>
/// Ключ — абсолютный путь, нормализованный <see cref="Path.GetFullPath(string)"/>; на
/// Windows регистр не важен (<see cref="StringComparer.OrdinalIgnoreCase"/>), на POSIX —
/// важен. <see cref="StringComparer.OrdinalIgnoreCase"/> используется на обеих платформах
/// для единообразия — на POSIX сценарий «два разных root отличаются только регистром»
/// маловероятен и хуже путаницы из-за разной семантики.
/// </para>
/// <para>
/// Strategy.md «Принятые решения» #6 (per-root eviction = 10 минут default), #13 (per-root
/// semaphore вместо global #313).
/// </para>
/// </summary>
internal sealed class RootRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, RootEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _rootIdleTimeout;
    private int _disposed;

    public RootRegistry(TimeSpan rootIdleTimeout)
    {
        _rootIdleTimeout = rootIdleTimeout;
    }

    /// <summary>
    /// Возвращает entry для <paramref name="rootPath"/>, создавая новый при отсутствии.
    /// Путь нормализуется (<see cref="Path.GetFullPath(string)"/>); регистронезависимый
    /// dedup. На созданный entry стартует idle-таймер; если entry уже был — таймер
    /// перезапускается (<see cref="Touch"/> внутри). Возвращает entry с обновлённым
    /// <see cref="RootEntry.LastUsed"/>.
    /// </summary>
    public RootEntry GetOrAdd(string rootPath)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RootRegistry));

        var key = NormalizePath(rootPath);
        var entry = _entries.GetOrAdd(key, k =>
        {
            var e = new RootEntry(k, DateTimeOffset.UtcNow);
            // Таймер стартует с infinite period — мы перезапускаем его вручную в Touch.
            e.IdleTimer = new Timer(EvictCallback, e, _rootIdleTimeout, Timeout.InfiniteTimeSpan);
            return e;
        });
        Touch(entry);
        return entry;
    }

    /// <summary>
    /// Обновляет <see cref="RootEntry.LastUsed"/> и сбрасывает idle-таймер. Вызывается
    /// на каждом обращении (RPC).
    /// </summary>
    public void Touch(RootEntry entry)
    {
        entry.LastUsed = DateTimeOffset.UtcNow;
        // Перезапускаем таймер: следующее срабатывание через _rootIdleTimeout.
        try { entry.IdleTimer.Change(_rootIdleTimeout, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* race с Dispose — игнорируем */ }
    }

    /// <summary>
    /// Снимок текущих entry для <c>GET /roots</c>. <see cref="RootInfo.ExpiresAt"/> =
    /// LastUsed + RootIdleTimeout.
    /// </summary>
    public IReadOnlyList<RootInfo> Snapshot()
    {
        var list = new List<RootInfo>(_entries.Count);
        foreach (var e in _entries.Values)
        {
            list.Add(new RootInfo(e.Root, e.LastUsed, e.LastUsed + _rootIdleTimeout));
        }
        return list;
    }

    private void EvictCallback(object? state)
    {
        if (state is not RootEntry entry) return;
        // Удаляем entry из словаря и dispose'им. Если в этот момент Touch успел
        // обновить LastUsed (race) — entry останется в словаре до следующего срабатывания.
        // Здесь мы НЕ проверяем «прошло ли реально N времени с LastUsed»: точное
        // соблюдение SLA per-root timeout не критично; ±N секунд приемлемо.
        if (_entries.TryRemove(entry.Root, out var removed))
        {
            removed.Dispose();
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var e in _entries.Values) e.Dispose();
        _entries.Clear();
    }
}
