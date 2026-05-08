using DocsWalker.Cli.Mcp;
using DocsWalker.Core.Mcp;
using DocsWalker.Core.Server;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Обработчик команды <c>mcp-server</c>: захватывает <see cref="ServerLifecycle"/>
/// (общий lock с <c>run</c> — single-root-per-process (#309), (#367)), строит
/// манифест tools из <see cref="CommandsToTools"/>, запускает <see cref="McpServer"/>
/// поверх stdin/stdout. Завершается при EOF на stdin (MCP-клиент закрыл канал)
/// или по сигналу.
/// </summary>
internal static class McpServerHandler
{
    private static int _activeInProcess;

    public static int Run(string root, IReadOnlyDictionary<string, string> args)
    {
        if (Interlocked.CompareExchange(ref _activeInProcess, 1, 0) != 0)
        {
            Output.WriteError(
                "server_already_running",
                path: null,
                $"Сервер DocsWalker уже инициализирован в этом процессе (pid={Environment.ProcessId}).");
            return 1;
        }

        try
        {
            return RunImpl(root, args);
        }
        finally
        {
            Volatile.Write(ref _activeInProcess, 0);
        }
    }

    private static int RunImpl(string root, IReadOnlyDictionary<string, string> args)
    {
        var quiet = ParseBool(args, "quiet");

        ServerLifecycle lifecycle;
        try
        {
            lifecycle = ServerLifecycle.Acquire(root);
        }
        catch (ServerAlreadyRunningException ex)
        {
            Output.WriteError(
                "server_already_running",
                path: null,
                $"Сервер DocsWalker уже запущен (pid={ex.OtherPid}).",
                hint: $"Завершите процесс {ex.OtherPid} перед повторным запуском.");
            return 1;
        }
        catch (ServerStartException ex)
        {
            Output.WriteError(ex.Code, path: null, ex.Message);
            return 1;
        }

        using (lifecycle)
        using (var signals = new SignalHandler())
        using (var shutdown = CancellationTokenSource.CreateLinkedTokenSource(signals.Token))
        {
            var tools = CommandsToTools.Build();
            // stdin/stdout — raw streams; Console.Out не используем для протокола,
            // т.к. handlers редиректятся в StringWriter на каждый вызов.
            using var stdin  = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            var server = new McpServer(stdin, stdout, Dispatcher.Run, tools, lifecycle.Sessions);

            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"DocsWalker MCP server started: root={root}, pid={Environment.ProcessId}, " +
                    $"protocol={McpServer.McpProtocolVersion}, tools={tools.Count}");
            }

            try { server.RunAsync(shutdown.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (AggregateException ae)
                when (ae.InnerExceptions.All(static e => e is OperationCanceledException))
            { }

            return 0;
        }
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var v)) return false;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }
}
