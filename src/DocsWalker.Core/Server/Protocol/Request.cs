namespace DocsWalker.Core.Server.Protocol;

/// <summary>
/// Запрос от клиента: полный argv (включая имя команды и параметры),
/// передаётся напрямую в Dispatcher.Run. Опциональный <see cref="SessionId"/> —
/// UUID LLM-сессии для context-aware-фильтрации (docs/DocsWalker.yml #322, #342).
/// null/отсутствует — сервер не ведёт seen-set для этого запроса.
/// </summary>
public sealed record IpcRequest(string[] Args, string? SessionId = null);
