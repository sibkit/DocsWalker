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

        return new UnixSocketChannel(GetChannelName(rootHash));
    }

    /// <summary>
    /// Возвращает имя канала без создания серверного endpoint.
    /// Windows — имя pipe, POSIX — путь к .sock-файлу.
    /// </summary>
    public static string GetChannelName(string rootHash)
    {
        if (OperatingSystem.IsWindows())
            return $"docswalker-{rootHash}";

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDir)
            ? Path.Combine(runtimeDir, $"docswalker-{rootHash}.sock")
            : Path.Combine("/tmp", $"docswalker-{rootHash}.sock");
    }
}
