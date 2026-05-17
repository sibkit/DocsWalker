using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Cli.Cli.Repl;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Команда <c>repl</c> — интерактивный HTTP-клиент к ядру DocsWalker. На старте
/// читает <see cref="ClientConfig"/> поиском <c>.dw/client.json</c> вверх от
/// cwd. В цикле: <see cref="LineReader.ReadLine"/> → <see cref="ReplTokenizer.Tokenize"/>
/// → <see cref="KernelHttpClient.SendCommandAsync"/> с фиксированным <c>config</c>.
/// Никакого <c>--root=</c> или <c>--storage-path=</c> в argv не подмешиваем —
/// kernel знает graph по имени из URL и инжектит storage-path сам.
/// </summary>
internal static class ReplHandler
{
    public static int Run(IReadOnlyDictionary<string, string> args)
    {
        var quiet = ParseBool(args, "quiet");

        ClientConfig cfg;
        try { cfg = ClientConfig.Resolve(); }
        catch (ClientConfigException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        return RunImplAsync(cfg, quiet).GetAwaiter().GetResult();
    }

    private static async Task<int> RunImplAsync(ClientConfig cfg, bool quiet)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"DocsWalker REPL: graph={cfg.Graph}, kernel={cfg.KernelHost}:{cfg.KernelPort}");
            Console.Error.WriteLine(
                "Команды — без префикса 'docswalker'. Выход: ':quit'/':exit'/Ctrl+D. Ctrl+C — отмена строки.");
        }

        while (!cts.Token.IsCancellationRequested)
        {
            Console.Out.Write("dw> ");
            Console.Out.Flush();

            string? line;
            try { line = LineReader.ReadLine(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed == ":quit" || trimmed == ":exit") break;

            var argv = ReplTokenizer.Tokenize(trimmed);
            if (argv.Length == 0) continue;

            try
            {
                _ = await KernelHttpClient.SendCommandAsync(argv, cfg, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"repl: внутренняя ошибка вызова — {ex.Message}");
            }
        }

        if (!quiet) Console.Error.WriteLine("DocsWalker REPL: bye");
        return 0;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var v)) return false;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
