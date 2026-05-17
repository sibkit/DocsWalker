using System.Text;
using DocsWalker.Cli;

// DocsWalker.Cli V2 — diagnostic-инструмент над Core. Открывает
// SQLite-файл напрямую (без kernel-а) для смоук-тестов, миграции,
// быстрой проверки запросов. Production-путь LLM остаётся через
// kernel HTTP + MCP stdio bridge.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length == 0)
{
    CliHelp.WriteFullUsage(Console.Error);
    return 1;
}

var subcommand = args[0];
var rest = args.Skip(1).ToArray();

try
{
    return subcommand switch
    {
        "init"       => InitCommand.Run(rest),
        "exec"       => ExecCommand.Run(rest),
        "repl"       => ReplCommand.Run(rest),
        "health"     => await HealthCommand.RunAsync(rest),
        "migrate-v1" => MigrateV1Command.Run(rest),
        "-h" or "--help" or "help" => CliReturn(CliHelp.WriteFullUsage(Console.Out)),
        _ => UnknownSubcommand(subcommand),
    };
}
catch (CliArgumentException ex)
{
    Console.Error.WriteLine($"DocsWalker.Cli {subcommand}: {ex.Message}");
    return 2;
}

static int CliReturn(int code) => code;

static int UnknownSubcommand(string name)
{
    Console.Error.WriteLine($"DocsWalker.Cli: неизвестная команда '{name}'");
    Console.Error.WriteLine();
    CliHelp.WriteFullUsage(Console.Error);
    return 1;
}
