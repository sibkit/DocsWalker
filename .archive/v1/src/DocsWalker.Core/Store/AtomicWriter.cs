using System.Text;

namespace DocsWalker.Core.Store;

/// <summary>
/// Описание одного файла, который нужно записать атомарно: целевой путь и его новое
/// содержимое в виде UTF-8 строки. Кодировка фиксирована — UTF-8 без BOM, как принято
/// для всех файлов docs/ (см. docs/Правила оформления.yml).
/// </summary>
public sealed record AtomicWriteTarget(string AbsolutePath, string Content);

/// <summary>
/// Базовый тип FS-операций, применяемых в составе атомарной пачки записи
/// (см. <see cref="AtomicWriter.WriteAndApply"/>). Поддержаны: создание каталога,
/// удаление пустого каталога, перенос (rename/move) каталога. Этого достаточно
/// для create/delete/rename/move folder.
/// </summary>
public abstract record FsOperation;

/// <summary>Создать каталог по абсолютному пути (идемпотентно).</summary>
public sealed record FsCreateDirectory(string AbsolutePath) : FsOperation;

/// <summary>Удалить пустой каталог. Если каталог не пуст — операция падает.</summary>
public sealed record FsDeleteEmptyDirectory(string AbsolutePath) : FsOperation;

/// <summary>
/// Переместить (rename / move) каталог из <paramref name="SourceAbsolutePath"/> в
/// <paramref name="DestinationAbsolutePath"/> через <see cref="Directory.Move"/>.
/// Применяется в фазе fs-pre до записи tmp-файлов — это нужно, чтобы dirty-документы
/// внутри переносимого каталога легли уже на новый путь.
/// </summary>
public sealed record FsMoveDirectory(string SourceAbsolutePath, string DestinationAbsolutePath) : FsOperation;

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
///
/// R10: дополнительно поддержаны простые FS-операции (создание / удаление пустого
/// каталога) через <see cref="WriteAndApply"/>. CreateDirectory применяется до
/// фазы tmp (так что tmp-файл может лечь в новый каталог); DeleteEmptyDirectory —
/// после rename.
///
/// R11: добавлен <see cref="FsMoveDirectory"/> (rename/move каталога). Применяется
/// в фазе fs-pre сразу после CreateDirectory и до фазы tmp — это позволяет
/// dirty-документу внутри переносимого каталога лечь на новый путь, а не на
/// несуществующий старый. FS-операции одного типа применяются в порядке
/// регистрации в <see cref="WriteState"/>.
/// </summary>
public static class AtomicWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAll(IReadOnlyList<AtomicWriteTarget> targets) =>
        WriteAndApply(targets, Array.Empty<FsOperation>());

    /// <summary>
    /// Атомарно записывает <paramref name="targets"/> и применяет
    /// <paramref name="fsOperations"/>. Порядок:
    ///   1) <see cref="FsCreateDirectory"/> — все, в порядке поступления;
    ///   2) <see cref="FsMoveDirectory"/> — все, в порядке поступления;
    ///   3) фаза tmp + rename для targets;
    ///   4) <see cref="FsDeleteEmptyDirectory"/> — все, в порядке поступления.
    /// При ошибке на фазе 1–2 — ничего не записано (но каталоги, созданные/
    /// перенесённые до ошибки, остаются как есть; обратного отката нет).
    /// </summary>
    public static void WriteAndApply(
        IReadOnlyList<AtomicWriteTarget> targets,
        IReadOnlyList<FsOperation> fsOperations)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(fsOperations);
        if (targets.Count == 0 && fsOperations.Count == 0) return;

        // Фаза fs-pre/1: CreateDirectory.
        foreach (var op in fsOperations)
        {
            if (op is not FsCreateDirectory create) continue;
            try
            {
                Directory.CreateDirectory(create.AbsolutePath);
            }
            catch (Exception ex)
            {
                throw new AtomicWriteException(
                    "fs_create_directory_failed",
                    create.AbsolutePath,
                    $"Не удалось создать каталог '{create.AbsolutePath}': {ex.Message}",
                    ex);
            }
        }

        // Фаза fs-pre/2: MoveDirectory. Выполняется до tmp-фазы, чтобы dirty-документ
        // внутри переносимого каталога лёг по новому пути.
        foreach (var op in fsOperations)
        {
            if (op is not FsMoveDirectory move) continue;
            try
            {
                Directory.Move(move.SourceAbsolutePath, move.DestinationAbsolutePath);
            }
            catch (Exception ex)
            {
                throw new AtomicWriteException(
                    "fs_move_directory_failed",
                    move.SourceAbsolutePath,
                    $"Не удалось перенести каталог '{move.SourceAbsolutePath}' → '{move.DestinationAbsolutePath}': {ex.Message}",
                    ex);
            }
        }

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

        // Фаза fs-post: DeleteEmptyDirectory.
        foreach (var op in fsOperations)
        {
            if (op is not FsDeleteEmptyDirectory del) continue;
            try
            {
                if (Directory.Exists(del.AbsolutePath))
                    Directory.Delete(del.AbsolutePath, recursive: false);
            }
            catch (Exception ex)
            {
                throw new AtomicWriteException(
                    "fs_delete_directory_failed",
                    del.AbsolutePath,
                    $"Не удалось удалить каталог '{del.AbsolutePath}': {ex.Message}",
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
