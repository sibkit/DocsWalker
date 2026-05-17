using DocsWalker.Cli.Cli.Kernel;

namespace DocsWalker.Kernel;

/// <summary>
/// Per-graph state ядра: <see cref="SemaphoreSlim"/> сериализует обращения
/// к одному графу (а не к процессу целиком), <see cref="LastUsed"/> —
/// метка для API/control snapshot. <see cref="StoragePath"/> — путь к
/// папке <c>docs/</c>, по которому handlers читают/пишут граф; передаётся
/// клиентским argv-командам через инжект <c>--storage-path=</c> в
/// <see cref="RpcDispatcher"/>.
/// </summary>
internal sealed class GraphEntry : IDisposable
{
    public string Name { get; }
    public string StoragePath { get; }
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public DateTimeOffset LastUsed { get; internal set; }

    public GraphEntry(string name, string storagePath, DateTimeOffset now)
    {
        Name = name;
        StoragePath = storagePath;
        LastUsed = now;
    }

    public void Dispose()
    {
        Semaphore.Dispose();
    }
}

/// <summary>
/// Реестр графов ядра, известных из <see cref="KernelConfig"/>. Графы
/// статически заданы в конфиге — динамической регистрации нет: если
/// клиент дёргает URL <c>/&lt;unknown&gt;</c>, ядро отдаёт
/// <c>unknown_graph</c>.
/// <para>
/// Per-graph semaphore разрешает параллелизм запросов между графами и
/// сериализует внутри одного графа (handlers — sole-writer контракт,
/// reload файла YAML на каждом write — атомарность гарантируется только
/// при сериализации внутри графа).
/// </para>
/// </summary>
internal sealed class GraphRegistry : IDisposable
{
    private readonly Dictionary<string, GraphEntry> _entries =
        new(StringComparer.Ordinal);

    private readonly TimeSpan _graphIdleTimeout;
    private int _disposed;

    public GraphRegistry(IEnumerable<KernelGraphConfig> graphs, TimeSpan graphIdleTimeout)
    {
        _graphIdleTimeout = graphIdleTimeout;
        var now = DateTimeOffset.UtcNow;
        foreach (var g in graphs)
            _entries[g.Name] = new GraphEntry(g.Name, g.StoragePath, now);
    }

    public bool TryGet(string graphName, out GraphEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(GraphRegistry));

        if (_entries.TryGetValue(graphName, out var found))
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    public void Touch(GraphEntry entry)
    {
        entry.LastUsed = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<GraphInfo> Snapshot()
    {
        var list = new List<GraphInfo>(_entries.Count);
        foreach (var e in _entries.Values)
            list.Add(new GraphInfo(e.Name, e.StoragePath, e.LastUsed));
        return list;
    }

    public TimeSpan GraphIdleTimeout => _graphIdleTimeout;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var e in _entries.Values) e.Dispose();
        _entries.Clear();
    }
}
