using DocsWalker.Core.Api;
using DocsWalker.Core.Storage;

namespace DocsWalker.Cli;

/// <summary>
/// <c>dw exec &lt;db&gt; &lt;graph&gt; &lt;tool&gt; [&lt;json-file&gt;|-]</c>:
/// один tool-call поверх локальной SQLite-БД. Полезно для smoke,
/// regression-репро и shell-скриптов вокруг V2 API.
///
/// <para>
/// Аргументы:
/// <list type="bullet">
///   <item><c>db</c> — путь к SQLite-файлу (создаётся при отсутствии).</item>
///   <item><c>graph</c> — имя графа (должен быть зарегистрирован, иначе
///     получите ошибку executor-а; используйте <c>dw init</c> заранее).</item>
///   <item><c>tool</c> — <c>read</c> или <c>tx</c>.</item>
///   <item><c>json-file</c> — путь к файлу с arguments-JSON (как в
///     MCP <c>tools/call</c>). <c>-</c> или отсутствие = stdin.</item>
/// </list>
/// </para>
///
/// <para>
/// Возвращает JSON-envelope результата в stdout. Exit-code:
/// <c>0</c> для success, <c>1</c> для API-ошибки, <c>2</c> для CLI argv-ошибки.
/// </para>
/// </summary>
internal static class ExecCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 3 || args.Length > 4)
        {
            throw new CliArgumentException(
                "exec: ожидается <db-path> <graph> <read|tx> [<json-file>|-]");
        }
        var dbPath = Path.GetFullPath(args[0]);
        var graph = args[1];
        var tool = args[2];
        var source = args.Length == 4 ? args[3] : "-";

        if (tool is not ("read" or "tx"))
        {
            throw new CliArgumentException(
                $"exec: tool должен быть 'read' или 'tx', получен '{tool}'");
        }
        var json = source == "-" ? Console.In.ReadToEnd() : File.ReadAllText(source);

        var store = SqliteStore.ForFile(dbPath);
        store.EnsureBootstrapped();
        using var conn = store.Open();
        // Граф должен быть уже зарегистрирован. exec не создаёт его сам,
        // чтобы typo в имени не сделал тихо новый пустой граф.
        if (!IsGraphRegistered(conn, graph))
        {
            throw new CliArgumentException(
                $"exec: граф '{graph}' не зарегистрирован в '{dbPath}' " +
                "(используйте `dw init` заранее)");
        }
        var output = ToolRunner.Run(tool, graph, conn, json);
        Console.Out.WriteLine(output.Text);
        return output.IsError ? 1 : 0;
    }

    private static bool IsGraphRegistered(Microsoft.Data.Sqlite.SqliteConnection conn, string graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM graph WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", graph);
        return cmd.ExecuteScalar() is not null;
    }
}

internal static class ToolRunner
{
    public readonly record struct ToolResult(string Text, bool IsError);

    public static ToolResult Run(string tool, string graph, Microsoft.Data.Sqlite.SqliteConnection conn, string json)
    {
        try
        {
            return tool switch
            {
                "read" => RunRead(graph, conn, json),
                "tx" => RunTx(graph, conn, json),
                _ => new ToolResult(
                    WireFormat.SerializeError(new ApiError(ApiErrorCodes.UnknownMethod,
                        new ApiErrorDetails(Path: null,
                            Extras: new Dictionary<string, object?> { ["tool"] = tool }))),
                    IsError: true),
            };
        }
        catch (ApiException ex)
        {
            return new ToolResult(WireFormat.SerializeError(ex), IsError: true);
        }
    }

    private static ToolResult RunRead(string graph, Microsoft.Data.Sqlite.SqliteConnection conn, string json)
    {
        var request = RequestParser.ParseRead(json);
        var exec = new ReadExecutor(conn, graph);
        var response = exec.Execute(request);
        return new ToolResult(WireFormat.SerializeRead(response), IsError: false);
    }

    private static ToolResult RunTx(string graph, Microsoft.Data.Sqlite.SqliteConnection conn, string json)
    {
        var request = RequestParser.ParseTx(json);
        var exec = new TxExecutor(conn, graph);
        var response = exec.Execute(request);
        return new ToolResult(WireFormat.SerializeTx(response), IsError: false);
    }
}
