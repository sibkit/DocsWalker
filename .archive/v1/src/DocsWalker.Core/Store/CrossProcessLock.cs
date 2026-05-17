namespace DocsWalker.Core.Store;

/// <summary>
/// Межпроцессная блокировка через эксклюзивно открытый файл. Используется
/// <see cref="Api.WriteApi"/> для сериализации write-операций между разными процессами
/// DocsWalker над одним <c>docs/</c> (например, CLI из IDE и MCP-сервер, либо несколько
/// LLM-агентов). Внутри одного процесса остаётся также <c>in-process</c> lock на
/// <see cref="Api.WriteApi"/> — настоящий объект только закрывает межпроцессный кейс.
///
/// Реализация — <see cref="FileStream"/> с <see cref="FileShare.None"/>: на Windows
/// и Linux .NET 10 это даёт эксклюзивный доступ к файлу до его закрытия. Каталог
/// <c>.docswalker/</c> создаётся при необходимости.
///
/// Ожидание — циклом с экспоненциальной задержкой (50 → 500 мс). По истечении
/// <see cref="TimeSpan"/> таймаута бросается <see cref="CrossProcessLockTimeoutException"/>;
/// вызывающая сторона переводит её в структурированную CLI-ошибку <c>lock_timeout</c>.
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    private FileStream? _stream;

    public string Path { get; }

    private CrossProcessLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>
    /// Берёт эксклюзивный lock на файл <paramref name="lockFilePath"/> с ожиданием не
    /// дольше <paramref name="timeout"/>. На таймаут — <see cref="CrossProcessLockTimeoutException"/>.
    /// Каталог lock-файла создаётся автоматически.
    /// </summary>
    public static CrossProcessLock Acquire(string lockFilePath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockFilePath);

        var dir = System.IO.Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var deadline = DateTime.UtcNow + timeout;
        int delayMs = 50;
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new CrossProcessLock(lockFilePath, stream);
            }
            catch (IOException)
            {
                // Файл занят другим процессом — ждём и пробуем снова.
                if (DateTime.UtcNow >= deadline)
                    throw new CrossProcessLockTimeoutException(lockFilePath, timeout);
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 500);
            }
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>
/// Не удалось взять межпроцессный lock за отведённое время. Несёт путь lock-файла
/// и значение таймаута, чтобы вызывающий мог собрать осмысленное сообщение для LLM.
/// </summary>
public sealed class CrossProcessLockTimeoutException : Exception
{
    public string LockPath { get; }
    public TimeSpan Timeout { get; }

    public CrossProcessLockTimeoutException(string lockPath, TimeSpan timeout)
        : base($"Не удалось взять межпроцессный lock '{lockPath}' за {timeout.TotalSeconds:0.#} сек.")
    {
        LockPath = lockPath;
        Timeout = timeout;
    }
}
