using System.Net.Sockets;

namespace DocsWalker.Core.Server.Ipc;

/// <summary>
/// IPC-канал на базе Unix-domain socket (POSIX).
/// <para>
/// <see cref="Listen"/> удаляет потенциально oставшийся от предыдущего
/// (мёртвого) сервера .sock-файл, создаёт socket, bind и listen.
/// Стартовать <see cref="Listen"/> допустимо только после захвата run.lock —
/// в этот момент гарантировано, что предыдущий владелец уже мёртв.
/// </para>
/// <para>
/// <see cref="Dispose"/> удаляет .sock-файл (best-effort).
/// </para>
/// </summary>
public sealed class UnixSocketChannel : IIpcChannel
{
    private readonly string _socketPath;
    private Socket? _socket;
    private bool _disposed;

    internal UnixSocketChannel(string socketPath)
    {
        _socketPath = socketPath;
    }

    public string ChannelName => _socketPath;

    public void Listen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Удаляем stale-файл: мы уже держим run.lock, значит прежний сервер мёртв.
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _socket.Listen(128);
    }

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is null)
            throw new InvalidOperationException("Listen() должен быть вызван до AcceptAsync().");

        var clientSocket = await _socket.AcceptAsync(cancellationToken);
        return new NetworkStream(clientSocket, ownsSocket: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket?.Dispose();
        _socket = null;
        try { File.Delete(_socketPath); } catch { }
    }
}
