using System.Text;

namespace DocsWalker.Core.Store;

/// <summary>
/// Описание одного файла, который нужно записать атомарно: целевой путь и его новое
/// содержимое в виде UTF-8 строки. Кодировка фиксирована — UTF-8 без BOM, как принято
/// для всех файлов docs/ (см. docs/Правила оформления.yml).
/// </summary>
public sealed record AtomicWriteTarget(string AbsolutePath, string Content);

/// <summary>
/// Ошибка атомарной записи. Несёт код, исходный путь и человекочитаемое сообщение.
/// На фазе подготовки временных файлов гарантирует, что временные файлы убраны.
/// </summary>
public sealed class AtomicWriteException : Exception
{
    public string Code { get; }
    public string? FilePath { get; }

    public AtomicWriteException(string code, string? filePath, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        FilePath = filePath;
    }
}

/// <summary>
/// Двухфазная атомарная запись пачки файлов:
///   1) для каждого target создаётся временный файл рядом с целевым (.tmp-uuid),
///      содержимое сбрасывается на диск с FileStream.Flush(true);
///   2) каждый временный файл переименовывается в целевой (File.Move с overwrite=true).
/// На Windows атомарность гарантирована только в пределах одного File.Move; для
/// нескольких файлов возможен «частично применённый» исход при сбое между переименованиями.
/// Это известное ограничение — фиксируется в спецификации write-api.
/// При ошибке на фазе 1 удаляются все уже созданные tmp-файлы.
/// </summary>
public static class AtomicWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAll(IReadOnlyList<AtomicWriteTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) return;

        var tempPaths = new List<string>(targets.Count);
        try
        {
            foreach (var target in targets)
            {
                var dir = Path.GetDirectoryName(target.AbsolutePath);
                if (string.IsNullOrEmpty(dir))
                    throw new AtomicWriteException(
                        "invalid_target_path",
                        target.AbsolutePath,
                        $"Целевой путь '{target.AbsolutePath}' не содержит каталога.");

                Directory.CreateDirectory(dir);

                var tmpPath = target.AbsolutePath + ".tmp-" + Guid.NewGuid().ToString("N");
                tempPaths.Add(tmpPath);

                try
                {
                    using var stream = new FileStream(
                        tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var bytes = Utf8NoBom.GetBytes(target.Content);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception ex)
                {
                    throw new AtomicWriteException(
                        "write_failed",
                        target.AbsolutePath,
                        $"Не удалось записать временный файл для '{target.AbsolutePath}': {ex.Message}",
                        ex);
                }
            }
        }
        catch
        {
            CleanupTempFiles(tempPaths);
            throw;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            try
            {
                File.Move(tempPaths[i], targets[i].AbsolutePath, overwrite: true);
            }
            catch (Exception ex)
            {
                throw new AtomicWriteException(
                    "rename_failed",
                    targets[i].AbsolutePath,
                    $"Не удалось переименовать временный файл в '{targets[i].AbsolutePath}': {ex.Message}. " +
                    $"Возможно частично применённое состояние: переименовано {i} из {targets.Count} файлов.",
                    ex);
            }
        }
    }

    private static void CleanupTempFiles(IReadOnlyList<string> tempPaths)
    {
        foreach (var path in tempPaths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Молчаливая уборка — основное сообщение уже сформировано исходным исключением.
            }
        }
    }
}
