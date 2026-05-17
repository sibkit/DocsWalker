namespace DocsWalker.Cli;

/// <summary>
/// Help-текст CLI. Печатается при <c>--help</c>, без аргументов или при
/// неизвестной subcommand. Один источник, два output-stream (stdout
/// для запрошенного help, stderr для ошибочной ситуации).
/// </summary>
internal static class CliHelp
{
    public static int WriteFullUsage(TextWriter w)
    {
        w.WriteLine("DocsWalker.Cli — diagnostic tool for DocsWalker V2 (SQLite + JSON API).");
        w.WriteLine();
        w.WriteLine("Subcommands:");
        w.WriteLine("  init       <db-path> <graph>");
        w.WriteLine("             Создать SQLite-файл и зарегистрировать пустой граф.");
        w.WriteLine();
        w.WriteLine("  exec       <db-path> <graph> <tool> [<json-file>|-]");
        w.WriteLine("             Однократно вызвать tool=read|tx с JSON-аргументами");
        w.WriteLine("             из файла или stdin (-). Возвращает JSON-envelope");
        w.WriteLine("             в stdout (exit-code 0 = ok, 1 = api error).");
        w.WriteLine();
        w.WriteLine("  repl       <db-path> <graph>");
        w.WriteLine("             Интерактивный REPL: каждая строка — `read <json>`,");
        w.WriteLine("             `tx <json>` или служебная (`:help`, `:quit`).");
        w.WriteLine();
        w.WriteLine("  health     [<kernel-url>]");
        w.WriteLine("             Запросить /health у kernel-а (default: http://127.0.0.1:18080).");
        w.WriteLine();
        w.WriteLine("  migrate-v1 <v1-docs-path> <db-path> <graph>");
        w.WriteLine("             Одноразовый импорт V1 YAML-graph в новый V2 SQLite.");
        w.WriteLine("             Создаёт scope=main узлы из иерархии .yml файлов,");
        w.WriteLine("             одной tx с title='initial-import'.");
        w.WriteLine();
        w.WriteLine("Не модифицирует kernel-config.json или client.json.");
        return 0;
    }
}

/// <summary>
/// Бросается при невалидных argv. Ловится в <c>Program.Main</c>, печатает
/// сообщение и возвращает exit-code 2.
/// </summary>
internal sealed class CliArgumentException : Exception
{
    public CliArgumentException(string message) : base(message) { }
}
