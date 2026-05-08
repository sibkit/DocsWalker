using DocsWalker.Core.Server;

namespace DocsWalker.Cli.Cli.Repl;

/// <summary>
/// REPL-цикл TTY-режима: prompt → ReadLine → tokenize → выполнение через
/// <see cref="IpcServer.ExecuteLocalAsync"/> (тот же семафор, что у IPC-клиентов).
/// Команды :quit / :exit и EOF (Ctrl+D / Ctrl+Z) завершают цикл; вызывающий
/// тригерит graceful-shutdown.
/// session_id REPL — UUID, генерируемый на старте; живёт до выхода из REPL
/// (docs/DocsWalker.yml #342). На <c>:exit</c> session-файл остаётся, GC
/// заберёт по TTL.
/// </summary>
internal static class Repl
{
    public static void Run(IpcServer server, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString();

        while (!ct.IsCancellationRequested)
        {
            Console.Out.Write("dw> ");
            Console.Out.Flush();

            string? line;
            try { line = LineReader.ReadLine(ct); }
            catch (OperationCanceledException) { break; }

            if (line is null) break; // EOF

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed == ":quit" || trimmed == ":exit") break;

            var argv = ReplTokenizer.Tokenize(trimmed);
            if (argv.Length == 0) continue;

            try
            {
                _ = server.ExecuteLocalAsync(argv, sessionId, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
