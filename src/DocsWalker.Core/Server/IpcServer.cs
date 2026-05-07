using System.Text.Json;
using DocsWalker.Core.Server.Ipc;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Core.Server;

/// <summary>
/// Accept-loop поверх IIpcChannel. Обрабатывает входящие подключения,
/// выполняет handshake, принимает запросы и диспатчит через переданный делегат.
/// Все запросы сериализуются глобально через SemaphoreSlim(1,1).
/// </summary>
public sealed class IpcServer
{
    private readonly IIpcChannel _channel;
    private readonly Func<string[], int> _dispatcher;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public IpcServer(IIpcChannel channel, Func<string[], int> dispatcher)
    {
        _channel = channel;
        _dispatcher = dispatcher;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Stream stream;
            try { stream = await _channel.AcceptAsync(ct); }
            catch (OperationCanceledException) { break; }

            _ = HandleConnectionAsync(stream, ct);
        }
    }

    /// <summary>
    /// Выполняет команду через тот же глобальный семафор, что и IPC-запросы,
    /// но БЕЗ перехвата stdout/stderr — вывод идёт в текущий
    /// <see cref="Console.Out"/>/<see cref="Console.Error"/>. Используется
    /// REPL-локалью: REPL и удалённые IPC-клиенты сериализуются через единую
    /// очередь, обеспечивая правило (#313) — обработка запросов строго по одному.
    /// </summary>
    public async Task<int> ExecuteLocalAsync(string[] args, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            try
            {
                return _dispatcher(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"{{\"code\":\"internal_error\",\"message\":\"{ex.Message.Replace("\"", "\\\"")}\"}}");
                return 1;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken serverCt)
    {
        using (stream)
        {
            try
            {
                using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt, handshakeCts.Token);

                var hsJson = await Frame.ReadLineAsync(stream, linkedCts.Token);
                if (hsJson is null) return;

                var hsReq = JsonSerializer.Deserialize(hsJson, ProtocolJsonContext.Default.HandshakeRequest);
                if (hsReq is null) return;

                if (hsReq.ProtocolVersion != ProtocolVersion.Current)
                {
                    var denied = new HandshakeResponse(
                        ProtocolVersion.Current, false,
                        $"version mismatch: server={ProtocolVersion.Current}, client={hsReq.ProtocolVersion}, restart server");
                    await Frame.WriteAsync(stream,
                        JsonSerializer.Serialize(denied, ProtocolJsonContext.Default.HandshakeResponse),
                        serverCt);
                    return;
                }

                var accepted = new HandshakeResponse(ProtocolVersion.Current, true);
                await Frame.WriteAsync(stream,
                    JsonSerializer.Serialize(accepted, ProtocolJsonContext.Default.HandshakeResponse),
                    serverCt);

                while (true)
                {
                    var reqJson = await Frame.ReadLineAsync(stream, serverCt);
                    if (reqJson is null) break;

                    var request = JsonSerializer.Deserialize(reqJson, ProtocolJsonContext.Default.IpcRequest);
                    if (request is null) break;

                    var response = await DispatchAsync(request, serverCt);
                    await Frame.WriteAsync(stream,
                        JsonSerializer.Serialize(response, ProtocolJsonContext.Default.IpcResponse),
                        serverCt);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var stdoutWriter = new StringWriter();
            var stderrWriter = new StringWriter();
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);
            int exitCode;
            try
            {
                exitCode = _dispatcher(request.Args);
            }
            catch (Exception ex)
            {
                exitCode = 1;
                stderrWriter.WriteLine($"{{\"code\":\"internal_error\",\"message\":\"{ex.Message.Replace("\"", "\\\"")}\"}}");
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }

            var stdout = stdoutWriter.ToString().TrimEnd('\r', '\n');
            var stderr = stderrWriter.ToString().TrimEnd('\r', '\n');
            return new IpcResponse(
                exitCode == 0 ? "ok" : "error",
                string.IsNullOrEmpty(stdout) ? null : stdout,
                string.IsNullOrEmpty(stderr) ? null : stderr,
                exitCode);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
