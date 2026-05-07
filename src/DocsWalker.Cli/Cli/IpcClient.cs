using System.Net.Sockets;
using System.Text.Json;
using DocsWalker.Core.Server;
using DocsWalker.Core.Server.Ipc;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Cli.Cli;

/// <summary>
/// Клиентская сторона IPC. Подключается к запущенному серверу,
/// выполняет handshake, отправляет команду, проксирует stdout/stderr + exit-code.
/// </summary>
internal static class IpcClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> SendCommandAsync(string rootPath, string[] args)
    {
        var rootHash = ServerLifecycle.ComputeRootHash(rootPath);
        var channelName = IpcChannelFactory.GetChannelName(rootHash);

        Stream stream;
        try
        {
            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            stream = await IpcClientConnector.ConnectAsync(channelName, connectCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            Output.WriteError(
                "server_not_running",
                path: null,
                "Сервер DocsWalker не запущен или не отвечает.",
                hint: $"docswalker run --root={rootPath}");
            return 1;
        }

        using (stream)
        {
            var ct = CancellationToken.None;

            var hsReq = new HandshakeRequest(ProtocolVersion.Current, ProtocolVersion.Current);
            await Frame.WriteAsync(stream,
                JsonSerializer.Serialize(hsReq, ProtocolJsonContext.Default.HandshakeRequest), ct);

            var hsJson = await Frame.ReadLineAsync(stream, ct);
            if (hsJson is null)
            {
                Output.WriteError("server_disconnected", path: null, "Сервер закрыл соединение во время handshake.");
                return 1;
            }

            var hsResp = JsonSerializer.Deserialize(hsJson, ProtocolJsonContext.Default.HandshakeResponse);
            if (hsResp is null || !hsResp.Accepted)
            {
                Output.WriteError(
                    "version_mismatch",
                    path: null,
                    hsResp?.Reason ?? "version mismatch: restart server");
                return 1;
            }

            var request = new IpcRequest(args);
            await Frame.WriteAsync(stream,
                JsonSerializer.Serialize(request, ProtocolJsonContext.Default.IpcRequest), ct);

            var respJson = await Frame.ReadLineAsync(stream, ct);
            if (respJson is null)
            {
                Output.WriteError("server_disconnected", path: null, "Сервер закрыл соединение до отправки ответа.");
                return 1;
            }

            var response = JsonSerializer.Deserialize(respJson, ProtocolJsonContext.Default.IpcResponse);
            if (response is null)
            {
                Output.WriteError("bad_response", path: null, "Сервер прислал некорректный ответ.");
                return 1;
            }

            if (response.Stdout is not null) Console.Out.WriteLine(response.Stdout);
            if (response.Stderr is not null) Console.Error.WriteLine(response.Stderr);
            return response.ExitCode;
        }
    }
}
