using System.Text;
using DocsWalker.Mcp;

// JSON-вывод DocsWalker — всегда UTF-8 без BOM. На Windows Console.Out по умолчанию
// использует кодовую страницу консоли (CP866/CP1251), что искажает кириллицу при
// прямом перехвате stdout/stderr (LLM, CI, файловый редирект). Устанавливаем явно.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// DocsWalker.Mcp.exe — тонкий stdio↔HTTP bridge между MCP-клиентом (Claude
// Code и т.п.) и DocsWalker.Kernel.exe. Запускается клиентом через
// mcpServers-запись в его конфиге. Из argv понимает один опциональный флаг
// --quiet=true|false (глушит баннер старта в stderr); прочие аргументы
// игнорируются — конфигурация транспорта берётся из .dw/client.json
// (поиск вверх от cwd).
var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var arg in args)
{
    if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
    var eq = arg.IndexOf('=', 2);
    if (eq < 0)
    {
        parsed[arg[2..]] = "true";
    }
    else
    {
        parsed[arg[2..eq]] = arg[(eq + 1)..];
    }
}

return McpWrapperHandler.Run(parsed);
