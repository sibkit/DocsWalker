using DocsWalker.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Kernel;

/// <summary>
/// Registry открытых графов kernel-а. Каждый граф — это имя,
/// зарегистрированное в kernel-config и в таблицах SQLite-БД (одна БД
/// на kernel). Сейчас держит только set имён + per-graph semaphore для
/// сериализации tx (SQLite single-writer; параллельные write-tx без
/// сериализации идут в SQLITE_BUSY).
///
/// Connection pool строится через каждый запрос — Microsoft.Data.Sqlite
/// сама держит pooled-коннекты внутри драйвера. PRAGMA и regex_match
/// применяются в <see cref="SqliteStore.Open"/> на каждом open().
/// </summary>
internal sealed class GraphRegistry : IDisposable
{
    private readonly Dictionary<string, GraphEntry> _entries;

    public GraphRegistry(IReadOnlyList<string> graphNames, SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(graphNames);
        ArgumentNullException.ThrowIfNull(store);
        _entries = new Dictionary<string, GraphEntry>(StringComparer.Ordinal);
        foreach (var name in graphNames)
        {
            _entries[name] = new GraphEntry(name, store);
        }
    }

    public bool TryGet(string graphName, out GraphEntry entry)
        => _entries.TryGetValue(graphName, out entry!);

    public IReadOnlyList<string> Names => _entries.Keys.ToArray();

    public void Dispose()
    {
        foreach (var e in _entries.Values)
        {
            e.Dispose();
        }
        _entries.Clear();
    }
}

internal sealed class GraphEntry : IDisposable
{
    public GraphEntry(string name, SqliteStore store)
    {
        Name = name;
        Store = store;
        WriteLock = new SemaphoreSlim(1, 1);
    }

    public string Name { get; }
    public SqliteStore Store { get; }

    /// <summary>
    /// Сериализует write-tx по этому графу. Reads идут без захвата
    /// (SQLite WAL + BEGIN DEFERRED — concurrent-readers OK).
    /// </summary>
    public SemaphoreSlim WriteLock { get; }

    public SqliteConnection OpenConnection() => Store.Open();

    public void Dispose() => WriteLock.Dispose();
}
