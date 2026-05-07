using System.IO.Pipes;

namespace DocsWalker.Core.Server.Ipc;

/// <summary>
/// IPC-канал на базе именованного pipe (Windows).
/// <para>
/// <see cref="Listen"/> создаёт первый server-instance pipe в режиме ожидания.
/// <see cref="AcceptAsync"/> ждёт подключения клиента к текущему instance,
/// одновременно создаёт следующий instance для принятия следующего клиента,
/// и возвращает подключённый stream. Это минимизирует окно, в которое новый
/// клиент получит ERROR_PIPE_NOT_FOUND.
/// </para>
/// <para>
/// ACL «только владелец»: требует System.IO.Pipes.AccessControl, который
/// не является AOT-совместимым в .NET 10. Не установлен.
/// TODO: добавить PipeSecurity, когда пакет получит trim-совместимость.
/// </para>
/// </summary>
public sealed class NamedPipeChannel : IIpcChannel
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _pendingPipe;
    private bool _disposed;

    internal NamedPipeChannel(string pipeName)
    {
        _pipeName = pipeName;
    }

    public string ChannelName => _pipeName;

    public void Listen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingPipe = CreateInstance();
    }

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPipe is null)
            throw new InvalidOperationException("Listen() должен быть вызван до AcceptAsync().");

        await _pendingPipe.WaitForConnectionAsync(cancellationToken);

        var connected = _pendingPipe;
        // Создаём следующий instance сразу после подключения, чтобы клиенты не
        // получали ERROR_PIPE_NOT_FOUND во время обработки текущего запроса.
        _pendingPipe = CreateInstance();
        return connected;
    }

    private NamedPipeServerStream CreateInstance() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingPipe?.Dispose();
        _pendingPipe = null;
    }
}
