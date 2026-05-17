using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocsWalker.Core.Api;
using YamlDotNet.RepresentationModel;

namespace DocsWalker.Cli.Migration;

/// <summary>
/// Импортёр V1 YAML-графа (структура из <c>.archive/v1/docs/*.yml</c>)
/// в V2 модель: scope=main, hex-id, partitioned path. Делает один проход
/// по входным файлам и собирает массив <see cref="CreateOp"/> для одной
/// initial-import tx.
///
/// <para>
/// Что мапится:
/// <list type="bullet">
///   <item>Файл <c>X.yml</c> → корневой узел <c>path="X"</c> (sanitized),
///     <c>content</c> — корневое поле <c>text</c> (если есть).</item>
///   <item>Каждая nested-секция/rule/statement/example/etc.
///     (<c>"(#NNN) title": [...]</c>) → дочерний узел.</item>
///   <item><c>title</c> и <c>path</c>-сегмент — sanitized
///     (пробелы → <c>-</c>, недопустимые символы выкинуты, дедупликация
///     суффиксом <c>_v1NNN</c>).</item>
///   <item><c>content</c> — текстовое поле <c>text</c> узла.</item>
///   <item><c>map_bindings</c>: <c>v1_id</c> (исходный номер) и
///     <c>v1_kind</c> (имя containing-секции: section/rule/statement/...).</item>
/// </list>
/// </para>
///
/// <para>
/// Что НЕ мапится (на v2 за рамками одноразового импорта):
/// <list type="bullet">
///   <item>Кросс-ссылки (<c>subject: [425]</c>, <c>subsystem: [433]</c> и т.п.)
///     — это V1 ref-id, у нас в V2 нет соответствующих узлов.
///     Импортер пишет их в stderr как warning и пропускает.</item>
///   <item>V1 meta-schema (<c>.docswalker/meta-schema.yml</c>) — V2
///     ведёт свою meta-schema в kernel, файл не переносится.</item>
///   <item>V1 <c>sequence.txt</c> — V2 берёт свой next_id из таблицы
///     <c>sequence</c> (стартует с 1; импорт занимает первые N id-ов).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class V1Importer
{
    private static readonly Regex InvalidPathChars = new(@"[^\p{L}\p{Nd}._-]",
        RegexOptions.CultureInvariant);
    private static readonly Regex V1KeyPrefix = new(@"^\(#(\d+)\)\s+(.+)$",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SkippedRefKeys = new(StringComparer.Ordinal)
    {
        "subject", "subsystem", "audience", "csharp_structure", "examples", "rules",
        "statements", "definitions", "notes",
    };

    private readonly List<CreateOp> _ops = [];
    private readonly HashSet<string> _usedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextWriter _stderr;

    public V1Importer(TextWriter stderr)
    {
        _stderr = stderr;
    }

    public IReadOnlyList<CreateOp> CollectedOps => _ops;

    public int FilesProcessed { get; private set; }
    public int NodesCreated => _ops.Count;
    public int RefsSkipped { get; private set; }
    public int CollisionsResolved { get; private set; }

    public void ImportFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"V1 docs folder не найден: {folder}");
        }
        // .docswalker/ — служебная папка V1 (meta-schema, sequence). В V2
        // не нужна.
        foreach (var file in Directory.EnumerateFiles(folder, "*.yml", SearchOption.TopDirectoryOnly)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            ImportFile(file);
        }
    }

    public void ImportFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilesProcessed++;
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var rootTitle = SanitizeTitle(fileName);
        if (rootTitle.Length == 0)
        {
            _stderr.WriteLine($"[import] WARN: file '{filePath}' имя нельзя нормализовать в title — пропуск");
            return;
        }
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0)
        {
            _stderr.WriteLine($"[import] WARN: file '{filePath}' пустой — пропуск");
            return;
        }
        var rootNode = yaml.Documents[0].RootNode;
        if (rootNode is not YamlMappingNode root)
        {
            _stderr.WriteLine($"[import] WARN: file '{filePath}' корень не mapping — пропуск");
            return;
        }
        var rootId = TryString(root, "id");
        var rootText = TryString(root, "text");
        var rootPath = AllocatePath(parentPath: null, candidateTitle: rootTitle, v1IdForDedup: rootId);
        var mb = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1_source"] = Path.GetFileName(filePath),
            ["v1_kind"] = "document",
        };
        if (rootId is not null) mb["v1_id"] = rootId;
        _ops.Add(new CreateOp(
            Path: rootPath,
            Alias: null,
            Set: new NodeSet(
                Title: ExtractTitleFromPath(rootPath),
                Content: rootText,
                MapBindings: mb,
                Links: null)));

        if (root.Children.TryGetValue(new YamlScalarNode("sections"), out var sections))
        {
            ImportContainer(rootPath, "section", sections);
        }
    }

    /// <summary>
    /// Импорт списка элементов какого-то типа (sections, rules, examples,
    /// statements, …). Каждый элемент — mapping с одной парой
    /// <c>"(#NNN) title": [ { ... }, { ... } ]</c>.
    /// </summary>
    private void ImportContainer(string parentPath, string kind, YamlNode container)
    {
        if (container is not YamlSequenceNode seq)
        {
            return;
        }
        foreach (var item in seq.Children)
        {
            if (item is not YamlMappingNode itemMap || itemMap.Children.Count == 0)
            {
                continue;
            }
            // V1 элемент — ровно один key, например `"(#125) Запись через API"`.
            var firstPair = itemMap.Children.First();
            var rawKey = (firstPair.Key as YamlScalarNode)?.Value ?? string.Empty;
            var (v1Id, title) = ParseV1Key(rawKey);
            var safeTitle = SanitizeTitle(title);
            if (safeTitle.Length == 0)
            {
                _stderr.WriteLine($"[import] WARN: '{parentPath}' пропуск элемента с пустым title (raw='{rawKey}')");
                continue;
            }
            var childPath = AllocatePath(parentPath, safeTitle, v1Id);
            var content = ExtractTextField(firstPair.Value);
            var mb = new Dictionary<string, string>(StringComparer.Ordinal) { ["v1_kind"] = kind };
            if (v1Id is not null) mb["v1_id"] = v1Id;
            _ops.Add(new CreateOp(
                Path: childPath,
                Alias: null,
                Set: new NodeSet(
                    Title: ExtractTitleFromPath(childPath),
                    Content: content,
                    MapBindings: mb,
                    Links: null)));

            // Спускаемся в nested containers того же шаблона.
            if (firstPair.Value is YamlSequenceNode childSeq)
            {
                foreach (var subItem in childSeq.Children)
                {
                    if (subItem is not YamlMappingNode subMap) continue;
                    foreach (var pair in subMap.Children)
                    {
                        var subKey = (pair.Key as YamlScalarNode)?.Value ?? string.Empty;
                        if (subKey is "text") continue;
                        if (subKey is "sections" or "rules" or "examples" or "statements" or "definitions" or "notes")
                        {
                            ImportContainer(childPath, NormaliseChildKind(subKey), pair.Value);
                        }
                        else if (SkippedRefKeys.Contains(subKey))
                        {
                            RefsSkipped++;
                        }
                        else if (subKey != "text")
                        {
                            // Прочие named-ref-ы из V1 — тоже пропускаем (без crash-а).
                            RefsSkipped++;
                        }
                    }
                }
            }
        }
    }

    private static string NormaliseChildKind(string yamlKey) => yamlKey switch
    {
        "sections" => "section",
        "rules" => "rule",
        "examples" => "example",
        "statements" => "statement",
        "definitions" => "definition",
        "notes" => "note",
        _ => yamlKey,
    };

    private string AllocatePath(string? parentPath, string candidateTitle, string? v1IdForDedup)
    {
        var basePath = parentPath is null ? candidateTitle : $"{parentPath}/{candidateTitle}";
        if (_usedPaths.Add(basePath))
        {
            return basePath;
        }
        // Конфликт: добавляем V1 id, если есть; иначе численный суффикс.
        var suffix = v1IdForDedup is not null ? $"_v1{v1IdForDedup}" : "_dup";
        var attempt = $"{basePath}{suffix}";
        var counter = 2;
        while (!_usedPaths.Add(attempt))
        {
            attempt = $"{basePath}{suffix}{counter.ToString(CultureInfo.InvariantCulture)}";
            counter++;
        }
        CollisionsResolved++;
        return attempt;
    }

    private static (string? Id, string Title) ParseV1Key(string key)
    {
        var match = V1KeyPrefix.Match(key);
        if (!match.Success)
        {
            return (null, key.Trim());
        }
        return (match.Groups[1].Value, match.Groups[2].Value.Trim());
    }

    private static string SanitizeTitle(string raw)
    {
        var trimmed = raw.Trim();
        // Спецсимволы (пробелы, кавычки, скобки) → '-'.
        var dashes = trimmed
            .Replace(' ', '-')
            .Replace('\t', '-')
            .Replace('/', '-')
            .Replace('\\', '-');
        var stripped = InvalidPathChars.Replace(dashes, string.Empty);
        // Свернуть подряд идущие '-'.
        while (stripped.Contains("--", StringComparison.Ordinal))
        {
            stripped = stripped.Replace("--", "-", StringComparison.Ordinal);
        }
        stripped = stripped.Trim('-', '.', '_');
        return stripped;
    }

    private static string ExtractTitleFromPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string? TryString(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var v))
        {
            return null;
        }
        return (v as YamlScalarNode)?.Value;
    }

    private static string? ExtractTextField(YamlNode item)
    {
        if (item is not YamlSequenceNode seq) return null;
        foreach (var entry in seq.Children)
        {
            if (entry is not YamlMappingNode em) continue;
            if (em.Children.TryGetValue(new YamlScalarNode("text"), out var textNode) &&
                textNode is YamlScalarNode scalar)
            {
                return scalar.Value;
            }
        }
        return null;
    }
}
