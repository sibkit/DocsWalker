using System.Net.Sockets;
using System.Text.Json;
using DocsWalker.Core.Server;
using DocsWalker.Core.Server.Ipc;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Cli.Cli;

/// <summary>
/// Клиентская сторона IPC. Подключается к запущенному серверу,
/// выполняет handshake, отправляет команду, проксирует stdout/stderr + exit-code.
/// session_id берётся из <c>--session-id=&lt;uuid&gt;</c> в argv (если задан) либо из
/// env <c>CLAUDE_CODE_SESSION_ID</c> (docs/DocsWalker.yml #342). Оба пустые →
/// в frame отправляется null, сервер не ведёт seen-set для этого запроса.
/// </summary>
internal static class IpcClient
{
    private const string SessionIdEnvVar = "CLAUDE_CODE_SESSION_ID";
    private const string SessionIdFlagPrefix = "--session-id=";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> SendCommandAsync(string rootPath, string[] args)
    {
        var sessionId = ResolveSessionId(args);
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

            var request = new IpcRequest(args, sessionId);
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

    /// <summary>
    /// Резолвит <c>session_id</c> по правилам docs/DocsWalker.yml #342: явный
    /// <c>--session-id=&lt;uuid&gt;</c> в argv перебивает env <c>CLAUDE_CODE_SESSION_ID</c>;
    /// оба пустые — null. Пустое значение трактуется как отсутствие.
    /// </summary>
    private static string? ResolveSessionId(string[] argv)
    {
        foreach (var token in argv)
        {
            if (token.StartsWith(SessionIdFlagPrefix, StringComparison.Ordinal))
            {
                var value = token[SessionIdFlagPrefix.Length..];
                return value.Length == 0 ? null : value;
            }
        }
        var fromEnv = Environment.GetEnvironmentVariable(SessionIdEnvVar);
        return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
    }
}
