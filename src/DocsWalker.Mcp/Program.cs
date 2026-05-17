using System.Text;
using DocsWalker.Mcp;

// DocsWalker.Mcp V2 — тонкий stdio↔HTTP bridge. Запускается MCP-клиентом
// (Claude Code и т.п.) через mcpServers-конфиг. Конфигурация транспорта —
// в .dw/client.json (поиск вверх от cwd).
//
// Argv-флаги:
//   --quiet=true|false   глушит баннер старта в stderr
//   --config=<path>      явный путь к client.json (минует поиск .dw/)
//   --help               показать справку

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

bool quiet = false;
string? explicitConfig = null;
foreach (var arg in args)
{
    if (arg is "--help" or "-h")
    {
        Console.Error.WriteLine("DocsWalker.Mcp — stdio↔HTTP bridge to DocsWalker.Kernel");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  DocsWalker.Mcp.exe [--quiet=true|false] [--config=<path>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Без --config ищет '.dw/client.json' вверх от cwd.");
        return 0;
    }
    if (arg.StartsWith("--quiet=", StringComparison.Ordinal))
    {
        var v = arg["--quiet=".Length..];
        quiet = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
    else if (arg.StartsWith("--config=", StringComparison.Ordinal))
    {
        explicitConfig = arg["--config=".Length..];
    }
}

ClientConfig cfg;
try
{
    cfg = explicitConfig is null ? ClientConfig.Resolve() : ClientConfig.Read(explicitConfig);
}
catch (ClientConfigException ex)
{
    Console.Error.WriteLine($"DocsWalker.Mcp: {ex.Code}: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await StdioBridge.RunAsync(cfg, quiet, cts.Token);
