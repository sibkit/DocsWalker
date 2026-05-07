using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DocsWalker.Core.Server.Ipc;
using DocsWalker.Core.Store;

namespace DocsWalker.Core.Server;

/// <summary>
/// Управляет жизненным циклом серверного процесса DocsWalker:
/// захват эксклюзивного lock на docs/.docswalker/run.lock, ведение
/// run.pid, открытие платформенного IPC-канала и их корректное
/// освобождение при завершении.
/// <para>
/// Использование: <c>using var lifecycle = ServerLifecycle.Acquire(rootPath);</c>
/// </para>
/// </summary>
public sealed class ServerLifecycle : IDisposable
{
    private FileStream? _lockStream;
    private readonly string _pidPath;

    public IIpcChannel Channel { get; }
    public string RootHash { get; }

    public bool IsHeld => _lockStream is not null;

    private ServerLifecycle(FileStream lockStream, string pidPath, IIpcChannel channel, string rootHash)
    {
        _lockStream = lockStream;
        _pidPath    = pidPath;
        Channel     = channel;
        RootHash    = rootHash;
    }

    /// <summary>
    /// Захватывает lifecycle для <paramref name="rootPath"/>:
    /// <list type="number">
    ///   <item>берёт эксклюзивный lock на run.lock;</item>
    ///   <item>создаёт и открывает IPC-канал;</item>
    ///   <item>атомарно записывает run.pid.</item>
    /// </list>
    /// </summary>
    /// <exception cref="ServerAlreadyRunningException">
    /// Другой живой сервер уже держит lock на этот root.
    /// </exception>
    /// <exception cref="ServerStartException">
    /// Не удалось захватить lock за отведённое число попыток.
    /// </exception>
    public static ServerLifecycle Acquire(string rootPath)
    {
        var absRoot        = Path.GetFullPath(rootPath);
        var rootHash       = ComputeRootHash(absRoot);
        var docsWalkerDir  = Path.Combine(absRoot, "docs", ".docswalker");
        Directory.CreateDirectory(docsWalkerDir);

        var lockPath = Path.Combine(docsWalkerDir, "run.lock");
        var pidPath  = Path.Combine(docsWalkerDir, "run.pid");

        FileStream? lockStream = null;
        const int maxRetries = 6;
        int delayMs = 50;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                lockStream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                break;
            }
            catch (IOException)
            {
                var (pid, exePath) = TryReadPidFile(pidPath);
                if (pid.HasValue && StalePidDetector.IsAlive(pid.Value, exePath))
                    throw new ServerAlreadyRunningException(pid.Value);

                // pid мёртв или файл отсутствует — ждём, пока ОС отпустит lock
                if (attempt < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 400);
                }
            }
        }

        if (lockStream is null)
            throw new ServerStartException(
                "lock_failed",
                $"Не удалось захватить lock '{lockPath}' за {maxRetries} попыток.");

        var channel = IpcChannelFactory.Create(rootHash);
        try
        {
            channel.Listen();
        }
        catch
        {
            lockStream.Dispose();
            channel.Dispose();
            throw;
        }

        WritePidFile(pidPath);

        return new ServerLifecycle(lockStream, pidPath, channel, rootHash);
    }

    /// <summary>
    /// Закрывает IPC-канал, удаляет run.pid и отпускает lock.
    /// Безопасно вызывать повторно.
    /// </summary>
    public void Release()
    {
        var channel = Channel;
        var stream  = Interlocked.Exchange(ref _lockStream, null);
        if (stream is null) return;

        channel.Dispose();

        try { File.Delete(_pidPath); }
        catch { /* best-effort */ }

        stream.Dispose();
    }

    public void Dispose() => Release();

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// SHA-256 абсолютного пути root → первые 16 hex-символов в нижнем регистре.
    /// Используется для уникального именования pipe/socket при нескольких
    /// workspace'ах на одном хосте.
    /// </summary>
    public static string ComputeRootHash(string absRootPath)
    {
        var bytes = Encoding.UTF8.GetBytes(absRootPath);
        var hash  = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static void WritePidFile(string pidPath)
    {
        var pid     = Environment.ProcessId;
        var exePath = Environment.ProcessPath
                      ?? Process.GetCurrentProcess().MainModule?.FileName
                      ?? string.Empty;
        var content = $"{pid}\n{exePath}";
        AtomicWriter.WriteAll([new AtomicWriteTarget(pidPath, content)]);
    }

    private static (int? Pid, string? ExePath) TryReadPidFile(string pidPath)
    {
        try
        {
            var text  = File.ReadAllText(pidPath, Encoding.UTF8);
            var nl    = text.IndexOf('\n');
            var line0 = nl >= 0 ? text[..nl].Trim() : text.Trim();
            var line1 = nl >= 0 ? text[(nl + 1)..].Trim() : null;

            if (int.TryParse(line0, out var pid))
                return (pid, string.IsNullOrEmpty(line1) ? null : line1);
        }
        catch { /* ignore — отсутствие файла или ошибка чтения */ }

        return (null, null);
    }
}

/// <summary>
/// Другой живой экземпляр сервера уже держит lock на тот же root.
/// </summary>
public sealed class ServerAlreadyRunningException : Exception
{
    public int OtherPid { get; }

    public ServerAlreadyRunningException(int otherPid)
        : base($"Сервер уже запущен (pid={otherPid}).")
    {
        OtherPid = otherPid;
    }
}

/// <summary>
/// Не удалось запустить сервер по причине, отличной от конфликта с другим сервером.
/// </summary>
public sealed class ServerStartException : Exception
{
    public string Code { get; }

    public ServerStartException(string code, string message) : base(message)
    {
        Code = code;
    }
}
