using DocsWalker.Cli.Migration;
using DocsWalker.Core.Api;
using DocsWalker.Core.Storage;

namespace DocsWalker.Cli;

/// <summary>
/// <c>dw migrate-v1 &lt;v1-docs-path&gt; &lt;db-path&gt; &lt;graph&gt;</c>:
/// одноразовый импорт V1 YAML-графа в новый V2 SQLite. Создаёт
/// scope=main узлы из иерархии .yml файлов под указанной папкой и
/// записывает их одной tx с <c>title="initial-import"</c>.
///
/// <para>
/// Преconditions: граф должен быть зарегистрирован (<c>dw init</c>) И
/// быть пустым (в нём нет ни одного main-узла). Запуск на не-пустом
/// графе отказывается, чтобы не делать тихий повторный импорт с
/// дублирующимися путями.
/// </para>
///
/// <para>
/// Возвращает stdout-сводку: число файлов, узлов, пропущенных ref-ов,
/// разрешённых path-конфликтов. Exit 0 — успех, 1 — API ошибка
/// (например, validation failed), 2 — argv-ошибка.
/// </para>
/// </summary>
internal static class MigrateV1Command
{
    public static int Run(string[] args)
    {
        if (args.Length != 3)
        {
            throw new CliArgumentException(
                "migrate-v1: ожидается <v1-docs-path> <db-path> <graph>");
        }
        var v1Path = Path.GetFullPath(args[0]);
        var dbPath = Path.GetFullPath(args[1]);
        var graph = args[2];

        var store = SqliteStore.ForFile(dbPath);
        store.EnsureBootstrapped();
        using var conn = store.Open();
        SqliteStore.EnsureGraphRegistered(conn, graph);

        if (CountMainNodes(conn, graph) > 0)
        {
            Console.Error.WriteLine(
                $"migrate-v1: граф '{graph}' уже содержит main-узлы. " +
                "Импорт требует пустого графа.");
            return 1;
        }

        var importer = new V1Importer(Console.Error);
        importer.ImportFolder(v1Path);
        if (importer.CollectedOps.Count == 0)
        {
            Console.Error.WriteLine($"migrate-v1: в '{v1Path}' не найдено импортируемых .yml файлов");
            return 1;
        }
        var txRequest = new TxRequest(
            Scope: Scope.Main,
            Title: "initial-import",
            Description:
                $"Импорт V1 YAML-graph из '{v1Path}' " +
                $"({importer.FilesProcessed} файлов, {importer.NodesCreated} узлов, " +
                $"refs-skipped={importer.RefsSkipped}, " +
                $"collisions-resolved={importer.CollisionsResolved}).",
            Defaults: null,
            Ops: importer.CollectedOps);

        try
        {
            var exec = new TxExecutor(conn, graph);
            var response = exec.Execute(txRequest);
            Console.WriteLine(WireFormat.SerializeTx(response));
            Console.Error.WriteLine(
                $"migrate-v1: ok. files={importer.FilesProcessed} " +
                $"nodes={importer.NodesCreated} refs-skipped={importer.RefsSkipped} " +
                $"collisions-resolved={importer.CollisionsResolved} " +
                $"tx_id={response.Id}");
            return 0;
        }
        catch (ApiException ex)
        {
            Console.Out.WriteLine(WireFormat.SerializeError(ex));
            return 1;
        }
    }

    private static long CountMainNodes(Microsoft.Data.Sqlite.SqliteConnection conn, string graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM node WHERE graph_name = @g AND scope = 'main'";
        cmd.Parameters.AddWithValue("@g", graph);
        var v = cmd.ExecuteScalar();
        return v is null ? 0 : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }
}
