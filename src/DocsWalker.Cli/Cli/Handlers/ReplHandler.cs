using System.Net.Http;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Cli.Cli.Repl;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Команда <c>repl</c> — интерактивный HTTP-клиент к ядру DocsWalker. На старте:
/// resolve kernel exe → <see cref="KernelClient.EnsureRunningAsync"/> (auto-spawn
/// при отсутствии). В цикле: <see cref="LineReader.ReadLine"/> → <see cref="ReplTokenizer.Tokenize"/>
/// → <see cref="KernelHttpClient.SendCommandAsync"/> с фиксированным <c>--root=</c>
/// и фиксированным <c>session_id</c> на всю REPL-сессию.
/// <para>
/// Strategy.md «Принятые решения» #11; step-06.
/// </para>
/// <para>
/// Старая команда <c>run</c> остаётся параллельно (server-mode + lock + IPC) до
/// step-07 cleanup-old-ipc.
/// </para>
/// </summary>
internal static class ReplHandler
{
    public static int Run(string root, IReadOnlyDictionary<string, string> args)
    {
        var quiet = ParseBool(args, "quiet");
        return RunImplAsync(root, quiet).GetAwaiter().GetResult();
    }

    private static async Task<int> RunImplAsync(string root, bool quiet)
    {
        var cliExe = Environment.ProcessPath
                     ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(cliExe))
        {
            Output.WriteError("cli_exe_not_found", path: null,
                "Не удалось определить путь к собственному exe.");
            return 1;
        }
        string kernelExe;
        try { kernelExe = KernelSpawner.ResolveKernelExePath(cliExe); }
        catch (KernelSpawnException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var cts  = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        KernelEndpoint endpoint;
        try
        {
            endpoint = await KernelClient.EnsureRunningAsync(kernelExe, http, cts.Token);
        }
        catch (KernelStartException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }
        catch (KernelSpawnException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        var sessionId = Guid.NewGuid().ToString();

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"DocsWalker REPL: root={root}, kernel={endpoint.Url}, session={sessionId}");
            Console.Error.WriteLine(
                "Команды — без префикса 'docswalker'. Выход: ':quit'/':exit'/Ctrl+D. Ctrl+C — отмена строки.");
        }

        // Каждая команда уходит как отдельный CLI-вызов через KernelHttpClient,
        // но с подмешанной --session-id (чтобы все команды одной REPL-сессии
        // делили seen-set когда ядро интегрирует SessionState — сейчас sessions=null
        // на kernel-стороне, эффект no-op до отдельного шага).
        while (!cts.Token.IsCancellationRequested)
        {
            Console.Out.Write("dw> ");
            Console.Out.Flush();

            string? line;
            try { line = LineReader.ReadLine(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line is null) break; // EOF

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed == ":quit" || trimmed == ":exit") break;

            var argv = ReplTokenizer.Tokenize(trimmed);
            if (argv.Length == 0) continue;

            // Подмешиваем --session-id и --root в argv — KernelHttpClient прочитает
            // и положит в JSON-RPC arguments. Если пользователь явно задал свой
            // --root в строке REPL, мы НЕ перезатираем (REPL-root — fallback,
            // не lockdown).
            var argvWithExtras = AppendIfMissing(argv, "--session-id=", sessionId);
            argvWithExtras = AppendIfMissing(argvWithExtras, "--root=", root);

            try
            {
                _ = await KernelHttpClient.SendCommandAsync(argvWithExtras, root, cts.Token);
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

    /// <summary>
    /// Добавляет <c>--key=value</c> в argv, если ни один токен не начинается с <c>--key=</c>.
    /// Используется чтобы не перезатереть пользовательский --root в REPL-строке.
    /// </summary>
    private static string[] AppendIfMissing(string[] argv, string keyPrefix, string value)
    {
        foreach (var t in argv)
            if (t.StartsWith(keyPrefix, StringComparison.Ordinal)) return argv;
        var next = new string[argv.Length + 1];
        Array.Copy(argv, next, argv.Length);
        next[^1] = $"{keyPrefix}{value}";
        return next;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var v)) return false;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
