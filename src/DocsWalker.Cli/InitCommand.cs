using DocsWalker.Core.Storage;

namespace DocsWalker.Cli;

/// <summary>
/// <c>dw init &lt;db-path&gt; &lt;graph&gt;</c>: bootstrap SQLite-файла
/// + регистрация графа. Идемпотентно: повторный вызов на тот же файл
/// не ломает DB и не сбрасывает sequence.
/// </summary>
internal static class InitCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            throw new CliArgumentException("init: ожидается 2 аргумента: <db-path> <graph>");
        }
        var dbPath = Path.GetFullPath(args[0]);
        var graph = args[1];
        if (string.IsNullOrWhiteSpace(graph))
        {
            throw new CliArgumentException("init: <graph> не должен быть пустым");
        }
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var store = SqliteStore.ForFile(dbPath);
        store.EnsureBootstrapped();
        using var conn = store.Open();
        SqliteStore.EnsureGraphRegistered(conn, graph);
        Console.WriteLine($"OK: db={dbPath} graph={graph}");
        return 0;
    }
}
