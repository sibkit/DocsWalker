using System.Security.Cryptography;
using System.Text;
using DocsWalker.Core.Store;

namespace DocsWalker.Core.Sessions;

/// <summary>
/// Хэш-снимок состава и содержимого docs/-файлов. Используется для
/// детекции внешней (ручной) правки YAML между сессиями сервера: при
/// shutdown пишем текущий хэш в <c>docs/.docswalker/sessions/.checksum</c>,
/// при startup сверяем — mismatch инвалидирует все seen-state
/// (см. <c>docs/DocsWalker.yml</c> §Контекст-aware-выдача,
/// rule «Hash-detection ручной правки»).
///
/// Алгоритм: для всех <c>*.yml</c> рекурсивно под <paramref name="docsDir"/>,
/// исключая поддерево <paramref name="excludeRelativePath"/> (служебная
/// папка sessions/), сортируем по relative-path в Ordinal-порядке;
/// по каждому файлу формируем буфер
/// <c>UTF-8(rel) + 0x00 + SHA256(content) + 0x00</c>;
/// финальный хэш — SHA256 от конкатенации всех буферов в hex (нижний регистр).
/// </summary>
public static class DocsChecksum
{
    /// <summary>
    /// Посчитать текущий хэш docs/. <paramref name="excludeRelativePath"/> —
    /// относительный путь поддерева, которое исключается (обычно
    /// <c>.docswalker/sessions</c>).
    /// </summary>
    public static string ComputeForDocs(string docsDir, string excludeRelativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(docsDir);
        ArgumentException.ThrowIfNullOrEmpty(excludeRelativePath);

        var docsAbs = Path.GetFullPath(docsDir);
        var excludeAbs = Path.GetFullPath(Path.Combine(docsAbs, excludeRelativePath));
        var excludePrefix = excludeAbs
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var files = Directory.EnumerateFiles(docsAbs, "*.yml", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(p => !p.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetRelativePath(docsAbs, p).Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();

        using var ms = new MemoryStream();
        foreach (var path in files)
        {
            var rel = Path.GetRelativePath(docsAbs, path).Replace('\\', '/');
            var relBytes = Encoding.UTF8.GetBytes(rel);
            ms.Write(relBytes, 0, relBytes.Length);
            ms.WriteByte(0);

            byte[] fileHash;
            using (var fs = File.OpenRead(path))
                fileHash = SHA256.HashData(fs);
            ms.Write(fileHash, 0, fileHash.Length);
            ms.WriteByte(0);
        }

        ms.Position = 0;
        var totalHash = SHA256.HashData(ms);
        return Convert.ToHexString(totalHash).ToLowerInvariant();
    }

    /// <summary>
    /// Прочитать сохранённый хэш. Возвращает <c>null</c>, если файла нет или
    /// его не удалось прочитать (отсутствие — нормально для первого старта).
    /// </summary>
    public static string? ReadStored(string checksumPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(checksumPath);
        if (!File.Exists(checksumPath)) return null;
        try
        {
            var text = File.ReadAllText(checksumPath, Encoding.UTF8).Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }

    /// <summary>
    /// Записать хэш атомарно через <see cref="AtomicWriter"/>. Каталог
    /// создаётся при необходимости.
    /// </summary>
    public static void WriteStored(string checksumPath, string hexChecksum)
    {
        ArgumentException.ThrowIfNullOrEmpty(checksumPath);
        ArgumentException.ThrowIfNullOrEmpty(hexChecksum);
        var dir = Path.GetDirectoryName(checksumPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        AtomicWriter.WriteAll([new AtomicWriteTarget(checksumPath, hexChecksum + "\n")]);
    }
}
