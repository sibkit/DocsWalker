namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Per-user discovery-локация ядра DocsWalker. Один путь на пользователя ОС
/// (multi-root внутри одного ядра — см. (#305) docs/DocsWalker.yml). Per-root
/// файлов в <c>&lt;root&gt;/.docswalker/</c> ядро НЕ создаёт (#314).
/// <para>
/// <b>Windows:</b> <c>%LOCALAPPDATA%\DocsWalker\kernel.json</c> +
/// <c>%LOCALAPPDATA%\DocsWalker\kernel.lock</c>. Каталог наследует ACL от
/// <c>%LOCALAPPDATA%</c> — per-user readable по дефолту.
/// </para>
/// <para>
/// <b>POSIX:</b> <c>${XDG_RUNTIME_DIR}/docswalker/</c> (если переменная задана) или
/// fallback <c>${HOME}/.cache/docswalker/</c>. Файлы — <c>chmod 600</c>, каталог — <c>chmod 700</c>.
/// </para>
/// </summary>
internal static class KernelDiscovery
{
    private const string AppDirName = "DocsWalker";
    private const string PosixDirName = "docswalker";

    /// <summary>
    /// Возвращает каталог per-user discovery-файлов. Каталог НЕ создаётся; для создания
    /// — <see cref="EnsureDirExists"/>.
    /// </summary>
    public static string GetKernelDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
                throw new InvalidOperationException("LOCALAPPDATA is not available");
            return Path.Combine(localAppData, AppDirName);
        }

        var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdgRuntime))
            return Path.Combine(xdgRuntime, PosixDirName);

        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            throw new InvalidOperationException("HOME is not available");
        return Path.Combine(home, ".cache", PosixDirName);
    }

    public static string GetKernelInfoPath() => Path.Combine(GetKernelDir(), "kernel.json");
    public static string GetKernelLockPath() => Path.Combine(GetKernelDir(), "kernel.lock");

    /// <summary>
    /// Создаёт каталог при отсутствии и (на POSIX) выставляет <c>0700</c>.
    /// На Windows наследование ACL от <c>%LOCALAPPDATA%</c> уже даёт per-user-only.
    /// </summary>
    public static void EnsureDirExists()
    {
        var dir = GetKernelDir();
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort: на некоторых FS chmod не поддержан */ }
        }
    }

    /// <summary>
    /// Выставляет owner-only mode (<c>0600</c>) на файл. На Windows — no-op (наследование ACL).
    /// </summary>
    public static void SetOwnerOnly(string filePath)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best-effort */ }
    }
}
