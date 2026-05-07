namespace DocsWalker.Core.Server.Protocol;

/// <summary>
/// Ответ сервера: stdout и stderr перехвачены у Dispatcher.Run и упакованы сюда.
/// Клиент пишет их в свои Console.Out / Console.Error.
/// </summary>
public sealed record IpcResponse(string Kind, string? Stdout, string? Stderr, int ExitCode);
