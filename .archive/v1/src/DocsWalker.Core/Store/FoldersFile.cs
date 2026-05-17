using System.Globalization;
using System.Text;
using DocsWalker.Core.IO;
using DocsWalker.Core.Yaml;
using SharpYaml.Events;

namespace DocsWalker.Core.Store;

/// <summary>
/// Одна запись в <c>docs/.docswalker/folders.yml</c> — сопоставление
/// folder-узла графа физическому каталогу. Идентичность каталога
/// задаётся цепочкой <see cref="Title"/> от <see cref="ParentId"/> вверх до
/// <see cref="DocsWalker.Core.Graph.Node.RootId"/>; FS-имя каталога —
/// <see cref="Title"/>.
/// </summary>
public sealed record FolderRecord(int Id, int ParentId, string Title);

/// <summary>
/// Ошибка чтения/записи файла folders.yml. Несёт код, путь и
/// человекочитаемое сообщение — переоборачивается верхним loader-ом
/// в <see cref="DocsWalker.Core.Graph.GraphLoadException"/> при необходимости.
/// </summary>
public sealed class FoldersFileException : Exception
{
    public string Code { get; }
    public string? FilePath { get; }

    public FoldersFileException(string code, string? filePath, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        FilePath = filePath;
    }
}

/// <summary>
/// Чтение и сериализация <c>.docswalker/folders.yml</c>. Файл — primary-источник
/// folder-узлов в DocsWalker (см. R10). FS-каталоги в <c>docs/</c> производны:
/// расхождение «есть запись, нет каталога» / «есть каталог, нет записи»
/// диагностируется как структурированная ошибка целостности, но не
/// чинится автоматически.
///
/// Формат файла — плоский список mapping'ов:
/// <code>
/// - id: 17
///   path: 0
///   title: guides
/// - id: 42
///   path: 17
///   title: advanced
/// </code>
/// Отсутствующий или пустой файл = пустой список (проект без подкаталогов).
/// </summary>
public static class FoldersFile
{
    private const string IdField = "id";
    private const string PathField = "path";
    private const string TitleField = "title";

    /// <summary>
    /// Возвращает абсолютный путь к <c>folders.yml</c> для заданного <paramref name="docsRoot"/>.
    /// </summary>
    public static string AbsolutePath(string docsRoot) =>
        Path.Combine(docsRoot, ".docswalker", "folders.yml");

    /// <summary>
    /// Относительный путь от docs/ — для <see cref="DocsWalker.Core.Graph.Node.SourceFile"/>
    /// folder-узлов.
    /// </summary>
    public const string RelativePath = ".docswalker/folders.yml";

    /// <summary>
    /// Читает folders.yml. Если файла нет — возвращает пустой список (нормальный
    /// случай для проекта без подкаталогов). Любые синтаксические/структурные
    /// нарушения превращаются в <see cref="FoldersFileException"/>.
    /// </summary>
    public static IReadOnlyList<FolderRecord> Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        if (!File.Exists(filePath)) return Array.Empty<FolderRecord>();

        string content;
        try
        {
            content = Utf8File.ReadAllTextStrict(filePath);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FoldersFileException(
                "invalid_utf8",
                filePath,
                $"Файл '{filePath}' не является валидным UTF-8: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            throw new FoldersFileException(
                "folders_read_failed",
                filePath,
                $"Не удалось прочитать '{filePath}': {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<FolderRecord>();

        try
        {
            return ParseRecords(content, filePath);
        }
        catch (YamlReadException ex)
        {
            throw new FoldersFileException(ex.Code, filePath, ex.Message);
        }
    }

    /// <summary>
    /// Сериализует список записей в YAML-текст для атомарной записи. Формат —
    /// блочный список mapping'ов из трёх полей в порядке id/path/title.
    /// Текст оканчивается переводом строки.
    /// </summary>
    public static string Emit(IReadOnlyList<FolderRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var r in records)
        {
            sb.Append("- id: ").Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  path: ").Append(r.ParentId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  title: ").Append(Quoting.Format(r.Title)).Append('\n');
        }
        return sb.ToString();
    }

    private static IReadOnlyList<FolderRecord> ParseRecords(string content, string filePath)
    {
        using var reader = new StringReader(content);
        var r = new YamlReader(reader, filePath);

        r.Expect<StreamStart>();
        r.Expect<DocumentStart>();

        if (r.Peek() is null || r.Peek() is DocumentEnd)
        {
            r.Expect<DocumentEnd>();
            r.Expect<StreamEnd>();
            return Array.Empty<FolderRecord>();
        }

        if (r.Peek() is Scalar nullScalar && string.IsNullOrEmpty(nullScalar.Value))
        {
            r.Next();
            r.Expect<DocumentEnd>();
            r.Expect<StreamEnd>();
            return Array.Empty<FolderRecord>();
        }

        if (r.Peek() is not SequenceStart)
            throw new FoldersFileException(
                "invalid_folders_file",
                filePath,
                "Ожидался список folder-записей на верхнем уровне.");

        r.Expect<SequenceStart>();
        var records = new List<FolderRecord>();
        while (r.Peek() is MappingStart)
        {
            records.Add(ReadRecord(r, filePath));
        }
        r.Expect<SequenceEnd>();
        r.Expect<DocumentEnd>();
        r.Expect<StreamEnd>();
        return records;
    }

    private static FolderRecord ReadRecord(YamlReader r, string filePath)
    {
        r.Expect<MappingStart>();

        int? id = null;
        int? parentId = null;
        string? title = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case IdField:
                    id = ReadInt(r, IdField, filePath);
                    break;
                case PathField:
                    parentId = ReadInt(r, PathField, filePath);
                    break;
                case TitleField:
                    title = r.NextScalarValue();
                    break;
                default:
                    throw new FoldersFileException(
                        "invalid_folders_file",
                        filePath,
                        $"Неизвестное поле folder-записи: '{key}'. Допустимы только id, path, title.");
            }
        }
        r.Expect<MappingEnd>();

        if (id is null)
            throw new FoldersFileException(
                "invalid_folders_file",
                filePath,
                "В folder-записи отсутствует поле 'id'.");
        if (parentId is null)
            throw new FoldersFileException(
                "invalid_folders_file",
                filePath,
                $"В folder-записи (id={id}) отсутствует поле 'path'.");
        if (string.IsNullOrEmpty(title))
            throw new FoldersFileException(
                "invalid_folders_file",
                filePath,
                $"В folder-записи (id={id}) отсутствует или пуст 'title'.");

        return new FolderRecord(id.Value, parentId.Value, title);
    }

    private static int ReadInt(YamlReader r, string field, string filePath)
    {
        var raw = r.NextScalarValue();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FoldersFileException(
                "invalid_folders_file",
                filePath,
                $"Поле '{field}': ожидалось целое число, получено '{raw}'.");
        if (v < 0)
            throw new FoldersFileException(
                "invalid_folders_file",
                filePath,
                $"Поле '{field}': ожидалось неотрицательное значение, получено '{raw}'.");
        return v;
    }
}
