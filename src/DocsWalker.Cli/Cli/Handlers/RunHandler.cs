using DocsWalker.Cli.Cli.Repl;
using DocsWalker.Core.Server;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Обработчик команды <c>run</c>: захватывает <see cref="ServerLifecycle"/>,
/// запускает <see cref="IpcServer"/>, ветвится по TTY (REPL) / редирект stdin
/// (headless ожидание сигнала). Один процесс — ровно один сервер на root;
/// повторное вхождение из IPC-диспетчера (если кто-то прислал <c>run</c> в
/// уже работающий сервер) отсекается статическим флагом <see cref="_activeInProcess"/>
/// — иначе попытались бы взять новый lifecycle и подписать второй
/// <see cref="SignalHandler"/> в том же процессе.
/// </summary>
internal static class RunHandler
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
        string? modeOverride = args.TryGetValue("mode", out var m) ? m : null;
        if (modeOverride is not null && modeOverride != "tty" && modeOverride != "headless")
        {
            Output.WriteError(
                "invalid_parameter",
                path: null,
                $"Параметр '--mode': ожидается 'tty' или 'headless', получено '{modeOverride}'.");
            return 1;
        }

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
            var server = new IpcServer(lifecycle.Channel, Dispatcher.Run, lifecycle.Sessions);

            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"DocsWalker server started: root={root}, pid={Environment.ProcessId}, " +
                    $"channel={lifecycle.Channel.ChannelName}, protocol={ProtocolVersion.Current}");
            }

            var ipcTask = Task.Run(() => server.RunAsync(shutdown.Token));

            var useRepl = modeOverride switch
            {
                "tty"      => true,
                "headless" => false,
                _          => !Console.IsInputRedirected,
            };

            if (useRepl)
            {
                if (!quiet)
                {
                    Console.Error.WriteLine(
                        "REPL mode. Команды — как у одноразового CLI (без префикса 'docswalker'). " +
                        "Выход: ':quit' / ':exit' / Ctrl+D. Ctrl+C — отмена текущей строки.");
                }
                try { Repl.Repl.Run(server, shutdown.Token); }
                finally
                {
                    if (!shutdown.IsCancellationRequested) shutdown.Cancel();
                }
            }
            else
            {
                if (!quiet)
                    Console.Error.WriteLine("Headless mode: жду SIGINT / SIGTERM / Ctrl+C для graceful-shutdown.");
                shutdown.Token.WaitHandle.WaitOne();
            }

            try { ipcTask.GetAwaiter().GetResult(); }
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
