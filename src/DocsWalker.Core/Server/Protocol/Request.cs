namespace DocsWalker.Core.Server.Protocol;

/// <summary>
/// Запрос от клиента: полный argv (включая имя команды и параметры),
/// передаётся напрямую в Dispatcher.Run.
/// </summary>
public sealed record IpcRequest(string[] Args);
