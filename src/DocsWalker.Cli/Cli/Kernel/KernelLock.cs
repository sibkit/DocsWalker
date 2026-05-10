namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Per-user file-lock на <c>kernel.lock</c>. Используется как winner-of-spawn-race
/// (см. strategy.md «Принятые решения» #9): клиент, захвативший lock, отвечает за
/// запуск ядра; loser ждёт и читает <c>kernel.json</c>.
/// <para>
/// Реализация — <see cref="FileStream"/> с <see cref="FileShare.None"/>: ОС
/// гарантирует эксклюзивный доступ, lock освобождается на <see cref="Dispose"/>
/// или при exit процесса (если он крашнулся не закрыв stream).
/// </para>
/// <para>
/// На Windows и POSIX (Linux/macOS) семантика идентична через .NET runtime.
/// </para>
/// </summary>
internal sealed class KernelLock : IDisposable
{
    private FileStream? _stream;

    public string Path { get; }

    private KernelLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>
    /// Не-блокирующая попытка захватить lock. Возвращает <see cref="KernelLock"/> при
    /// успехе или null если занят другим процессом. Каталог создаётся при отсутствии.
    /// </summary>
    public static KernelLock? TryAcquireOnce()
    {
        KernelDiscovery.EnsureDirExists();
        var path = KernelDiscovery.GetKernelLockPath();
        try
        {
            var stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new KernelLock(path, stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Блокирующая попытка захватить lock с retry'ями. Использует
    /// <see cref="TryAcquireOnce"/> в цикле с экспоненциальным backoff (50мс → 400мс).
    /// Возвращает null если за <paramref name="timeout"/> lock не освободился.
    /// </summary>
    public static KernelLock? TryAcquire(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int delayMs = 50;
        while (true)
        {
            var l = TryAcquireOnce();
            if (l is not null) return l;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(delayMs);
            delayMs = Math.Min(delayMs * 2, 400);
        }
    }

    public void Dispose()
    {
        var s = Interlocked.Exchange(ref _stream, null);
        s?.Dispose();
    }
}
