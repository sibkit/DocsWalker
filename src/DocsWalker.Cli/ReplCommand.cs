using System.Text;
using DocsWalker.Core.Storage;

namespace DocsWalker.Cli;

/// <summary>
/// <c>dw repl &lt;db&gt; &lt;graph&gt;</c>: интерактивная REPL поверх
/// локальной SQLite-БД. Поддерживает многострочный JSON: первая
/// строка-команда (<c>read</c> / <c>tx</c>) опционально содержит начало
/// JSON, дальше парсер дочитывает до сбалансированной скобки.
///
/// <para>
/// Служебные команды:
/// <list type="bullet">
///   <item><c>:help</c> — короткая справка.</item>
///   <item><c>:quit</c> / <c>:q</c> / Ctrl+D — выход.</item>
///   <item><c>:graph &lt;name&gt;</c> — переключить текущий граф (DB та же).</item>
/// </list>
/// </para>
/// </summary>
internal static class ReplCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            throw new CliArgumentException("repl: ожидается <db-path> <graph>");
        }
        var dbPath = Path.GetFullPath(args[0]);
        var graph = args[1];

        var store = SqliteStore.ForFile(dbPath);
        store.EnsureBootstrapped();
        using var conn = store.Open();
        if (!IsGraphRegistered(conn, graph))
        {
            Console.Error.WriteLine(
                $"repl: граф '{graph}' не зарегистрирован в '{dbPath}' " +
                "(используйте `dw init` заранее)");
            return 1;
        }

        Console.Error.WriteLine($"DocsWalker REPL: db={dbPath} graph={graph}");
        Console.Error.WriteLine("Команды: `read <json>`, `tx <json>`, `:help`, `:quit`.");

        while (true)
        {
            Console.Error.Write($"{graph}> ");
            Console.Error.Flush();
            var line = Console.In.ReadLine();
            if (line is null)
            {
                Console.Error.WriteLine();
                return 0;
            }
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith(':'))
            {
                var (exit, newGraph) = HandleMeta(line, graph);
                if (exit) return 0;
                if (newGraph is not null)
                {
                    if (!IsGraphRegistered(conn, newGraph))
                    {
                        Console.Error.WriteLine($"граф '{newGraph}' не зарегистрирован — оставляем '{graph}'");
                    }
                    else
                    {
                        graph = newGraph;
                    }
                }
                continue;
            }

            var (cmd, head) = SplitFirstWord(line);
            if (cmd is not ("read" or "tx"))
            {
                Console.Error.WriteLine($"неизвестная команда '{cmd}' (нужно read / tx / :help)");
                continue;
            }
            string json;
            try
            {
                json = ReadJsonContinuing(head);
            }
            catch (IOException)
            {
                return 0;
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                Console.Error.WriteLine("пустой JSON — пропуск");
                continue;
            }
            var output = ToolRunner.Run(cmd, graph, conn, json);
            Console.Out.WriteLine(output.Text);
        }
    }

    private static (bool Exit, string? NewGraph) HandleMeta(string line, string currentGraph)
    {
        switch (line)
        {
            case ":quit" or ":q" or ":exit":
                return (true, null);
            case ":help":
                Console.Error.WriteLine("Команды:");
                Console.Error.WriteLine("  read <json>     read-запрос; JSON может занимать несколько строк");
                Console.Error.WriteLine("  tx <json>       tx-запрос");
                Console.Error.WriteLine("  :graph <name>   переключить current graph");
                Console.Error.WriteLine("  :quit           выход (alias: :q, Ctrl+D)");
                return (false, null);
        }
        if (line.StartsWith(":graph ", StringComparison.Ordinal))
        {
            var name = line[":graph ".Length..].Trim();
            if (name.Length == 0)
            {
                Console.Error.WriteLine(":graph: имя графа не указано");
                return (false, null);
            }
            return (false, name);
        }
        Console.Error.WriteLine($"неизвестная служебная команда '{line}' (см. :help)");
        return (false, null);
    }

    private static (string Command, string TailJson) SplitFirstWord(string line)
    {
        var spaceIdx = line.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return (line, string.Empty);
        }
        return (line[..spaceIdx], line[(spaceIdx + 1)..]);
    }

    private static string ReadJsonContinuing(string head)
    {
        // Простейший балансер скобок: сохраняем читаемые строки, пока
        // суммарный JSON не станет валидным (тестируем парсингом).
        var buf = new StringBuilder();
        if (!string.IsNullOrEmpty(head)) buf.Append(head);
        while (true)
        {
            var candidate = buf.ToString().Trim();
            if (candidate.Length > 0 && IsValidJson(candidate))
            {
                return candidate;
            }
            Console.Error.Write("... ");
            Console.Error.Flush();
            var next = Console.In.ReadLine();
            if (next is null) throw new IOException("EOF");
            buf.Append('\n').Append(next);
        }
    }

    private static bool IsValidJson(string s)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(s);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsGraphRegistered(Microsoft.Data.Sqlite.SqliteConnection conn, string graph)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM graph WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", graph);
        return cmd.ExecuteScalar() is not null;
    }
}
