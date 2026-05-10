using System.Collections.Concurrent;
using DocsWalker.Cli.Cli.Kernel;

namespace DocsWalker.Kernel;

/// <summary>
/// Per-root state ядра: <see cref="SemaphoreSlim"/> сериализует обращения к одному root
/// (см. (#313) docs/DocsWalker.yml — per-root semaphore вместо global), <see cref="LastUsed"/>
/// и <see cref="IdleTimer"/> реализуют per-root idle eviction (#316).
/// </summary>
internal sealed class RootEntry : IDisposable
{
    public string Root { get; }
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public DateTimeOffset LastUsed { get; internal set; }
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
/// Реестр загруженных root'ов ядра DocsWalker. Multi-root в одном процессе: каждый
/// root получает свой <see cref="RootEntry"/> по запросу (<see cref="GetOrAdd"/>),
/// независимо сериализуется и независимо evict'ится по idle-таймеру.
/// <para>
/// Strategy.md «Принятые решения» #6 (per-root eviction = 10 минут default), #13
/// (per-root semaphore вместо global #313).
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

    public RootEntry GetOrAdd(string rootPath)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RootRegistry));

        var key = NormalizePath(rootPath);
        var entry = _entries.GetOrAdd(key, k =>
        {
            var e = new RootEntry(k, DateTimeOffset.UtcNow);
            e.IdleTimer = new Timer(EvictCallback, e, _rootIdleTimeout, Timeout.InfiniteTimeSpan);
            return e;
        });
        Touch(entry);
        return entry;
    }

    public void Touch(RootEntry entry)
    {
        entry.LastUsed = DateTimeOffset.UtcNow;
        try { entry.IdleTimer.Change(_rootIdleTimeout, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* race с Dispose — игнорируем */ }
    }

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
