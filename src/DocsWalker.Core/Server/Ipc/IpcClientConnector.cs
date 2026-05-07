using System.IO.Pipes;
using System.Net.Sockets;

namespace DocsWalker.Core.Server.Ipc;

/// <summary>
/// Клиентское подключение к IPC-каналу сервера. Платформонезависимо.
/// </summary>
public static class IpcClientConnector
{
    /// <summary>
    /// Подключается к серверному каналу по имени и возвращает двунаправленный stream.
    /// Windows — NamedPipeClientStream, POSIX — Unix-domain socket.
    /// </summary>
    public static async Task<Stream> ConnectAsync(string channelName, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", channelName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ct);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(channelName), ct);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
