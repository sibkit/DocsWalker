using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Core.Server;
using DocsWalker.Core.Server.Ipc;
using DocsWalker.Core.Server.Protocol;

namespace DocsWalker.Tests;

public class IpcSmokeTests
{
    // Создаём уникальный temp-root для каждого теста: IpcClient.SendCommandAsync вычисляет
    // имя канала из rootPath, сервер должен слушать тот же хэш.
    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dw-ipc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task RoundTrip_ValidCommand_ReturnsExitZeroAndJson()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var rootHash = ServerLifecycle.ComputeRootHash(tempRoot);
            using var channel = IpcChannelFactory.Create(rootHash);
            channel.Listen();

            using var cts = new CancellationTokenSource();
            var server = new IpcServer(channel, Dispatcher.Run);
            var serverTask = server.RunAsync(cts.Token);

            // --root указывает на реальный repo с docs/; канал именован по tempRoot
            var args = new[] { "get-usage-guide", $"--root={TestPaths.RepoRoot}" };
            var exitCode = await IpcClient.SendCommandAsync(tempRoot, args);

            cts.Cancel();
            await serverTask;

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: false);
        }
    }

    [Fact]
    public async Task RoundTrip_InvalidCommand_ReturnsExitOne()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var rootHash = ServerLifecycle.ComputeRootHash(tempRoot);
            using var channel = IpcChannelFactory.Create(rootHash);
            channel.Listen();

            using var cts = new CancellationTokenSource();
            var server = new IpcServer(channel, Dispatcher.Run);
            var serverTask = server.RunAsync(cts.Token);

            var args = new[] { "no-such-command", $"--root={TestPaths.RepoRoot}" };
            var exitCode = await IpcClient.SendCommandAsync(tempRoot, args);

            cts.Cancel();
            await serverTask;

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: false);
        }
    }

    [Fact]
    public async Task Handshake_WrongProtocol_RejectsConnection()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var rootHash = ServerLifecycle.ComputeRootHash(tempRoot);
            using var channel = IpcChannelFactory.Create(rootHash);
            channel.Listen();

            using var cts = new CancellationTokenSource();
            var server = new IpcServer(channel, Dispatcher.Run);
            var serverTask = server.RunAsync(cts.Token);

            var channelName = IpcChannelFactory.GetChannelName(rootHash);
            using var stream = await IpcClientConnector.ConnectAsync(channelName, CancellationToken.None);

            var badHs = new HandshakeRequest(ProtocolVersion.Current, "999");
            await Frame.WriteAsync(stream,
                JsonSerializer.Serialize(badHs, ProtocolJsonContext.Default.HandshakeRequest));

            var respJson = await Frame.ReadLineAsync(stream);
            var resp = JsonSerializer.Deserialize(respJson!, ProtocolJsonContext.Default.HandshakeResponse);

            cts.Cancel();
            await serverTask;

            Assert.NotNull(resp);
            Assert.False(resp.Accepted);
            Assert.Contains("version mismatch", resp.Reason);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: false);
        }
    }
}
