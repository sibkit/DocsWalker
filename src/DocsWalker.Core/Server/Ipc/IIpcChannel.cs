namespace DocsWalker.Core.Server.Ipc;

/// <summary>
/// Платформонезависимый IPC-канал. Открывает endpoint, принимает подключения
/// и возвращает stream для каждого клиента. Реализации: именованный канал
/// (Windows) и Unix-domain socket (POSIX).
/// </summary>
public interface IIpcChannel : IDisposable
{
    /// <summary>
    /// Имя канала: имя pipe без префикса (Windows) или путь к .sock-файлу (POSIX).
    /// Используется для передачи клиенту через run.pid / derive из rootHash.
    /// </summary>
    string ChannelName { get; }

    /// <summary>
    /// Открывает endpoint и переводит канал в режим ожидания подключений.
    /// Должен вызываться ровно один раз до <see cref="AcceptAsync"/>.
    /// </summary>
    void Listen();

    /// <summary>
    /// Ожидает следующего подключения и возвращает двунаправленный
    /// <see cref="Stream"/> для обмена данными с клиентом.
    /// Вызывается в цикле сервером; поток следует закрыть после обработки запроса.
    /// </summary>
    Task<Stream> AcceptAsync(CancellationToken cancellationToken);
}
