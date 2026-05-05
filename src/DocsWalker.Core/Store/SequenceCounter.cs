using System.Globalization;
using System.Text;

namespace DocsWalker.Core.Store;

/// <summary>
/// Ошибка работы со sequence-счётчиком id (docs/.docswalker/sequence.txt):
/// файл повреждён, недоступен, содержит не-целое значение и т. п.
/// </summary>
public sealed class SequenceCounterException : Exception
{
    public string Code { get; }
    public string? FilePath { get; }

    public SequenceCounterException(string code, string? filePath, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        FilePath = filePath;
    }
}

/// <summary>
/// Sequence-счётчик id узлов. Хранится в одном файле строкой десятичного числа без
/// иных полей (см. docs/DocsWalker.yml/«Sequence-счётчик id»). При первом обращении
/// создаётся со значением 0. Каждый <see cref="Next"/> читает текущее, увеличивает на 1,
/// записывает обратно и возвращает новое значение.
///
/// Безопасность по гонкам:
///   - в пределах одного процесса — внутренний lock-объект сериализует Next();
///   - между процессами — операция выполняется с FileShare.None в одном FileStream,
///     второй процесс получит IOException и обязан отступить (write-api делает один Next
///     на операцию, retry политика на этом шаге не вводится).
/// </summary>
public sealed class SequenceCounter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly object _lock = new();

    public SequenceCounter(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Текущее значение счётчика. Если файла нет — возвращает 0 и создаёт файл.
    /// </summary>
    public int Read()
    {
        lock (_lock)
        {
            EnsureInitialized();
            return ReadCurrent();
        }
    }

    /// <summary>
    /// Атомарно увеличивает счётчик на 1 и возвращает новое значение.
    /// </summary>
    public int Next()
    {
        lock (_lock)
        {
            EnsureInitialized();
            var current = ReadCurrent();
            checked
            {
                var next = current + 1;
                WriteValue(next);
                return next;
            }
        }
    }

    private void EnsureInitialized()
    {
        if (File.Exists(_filePath)) return;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WriteValue(0);
    }

    private int ReadCurrent()
    {
        string raw;
        try
        {
            raw = File.ReadAllText(_filePath, Utf8NoBom).Trim();
        }
        catch (Exception ex)
        {
            throw new SequenceCounterException(
                "sequence_read_failed",
                _filePath,
                $"Не удалось прочитать sequence-файл '{_filePath}': {ex.Message}",
                ex);
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new SequenceCounterException(
                "sequence_invalid_value",
                _filePath,
                $"Файл '{_filePath}' содержит '{raw}', ожидалось целое десятичное число.");
        if (value < 0)
            throw new SequenceCounterException(
                "sequence_invalid_value",
                _filePath,
                $"Файл '{_filePath}' содержит отрицательное значение '{raw}'.");
        return value;
    }

    private void WriteValue(int value)
    {
        var content = value.ToString(CultureInfo.InvariantCulture) + "\n";
        var bytes = Utf8NoBom.GetBytes(content);
        try
        {
            using var stream = new FileStream(
                _filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            throw new SequenceCounterException(
                "sequence_write_failed",
                _filePath,
                $"Не удалось записать sequence-файл '{_filePath}': {ex.Message}",
                ex);
        }
    }
}
