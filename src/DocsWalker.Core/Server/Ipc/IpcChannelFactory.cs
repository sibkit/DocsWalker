namespace DocsWalker.Core.Server.Ipc;

public static class IpcChannelFactory
{
    /// <summary>
    /// Создаёт платформенную реализацию IPC-канала для заданного rootHash.
    /// Windows → именованный канал; POSIX → Unix-domain socket.
    /// </summary>
    public static IIpcChannel Create(string rootHash)
    {
        if (OperatingSystem.IsWindows())
            return new NamedPipeChannel($"docswalker-{rootHash}");

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var socketPath = !string.IsNullOrEmpty(runtimeDir)
            ? Path.Combine(runtimeDir, $"docswalker-{rootHash}.sock")
            : Path.Combine("/tmp", $"docswalker-{rootHash}.sock");

        return new UnixSocketChannel(socketPath);
    }
}
