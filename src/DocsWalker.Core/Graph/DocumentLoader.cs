using System.Globalization;
using System.Text;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Yaml;
using SharpYaml.Events;

namespace DocsWalker.Core.Graph;

/// <summary>
/// Результат загрузки docs/: построенный граф плюс список абсолютных путей
/// фактически загруженных файлов (для диагностики и тестов).
/// </summary>
public sealed class DocumentLoadResult
{
    public required Graph Graph { get; init; }
    public required IReadOnlyList<string> LoadedFiles { get; init; }
}

/// <summary>
/// Обходит docs/ и строит in-memory <see cref="Graph"/> по refs-модели.
/// Файл Схема.yml и каталог .docswalker/ исключаются — они валидируются отдельно.
/// Парсинг — через event-stream API SharpYaml (см. docs/Стек.yml/«YAML-парсер»).
/// На этапе загрузки выполняются базовые структурные проверки; полные проверки
/// целостности — в graph-validator (отдельный шаг стратегии).
/// </summary>
public static class DocumentLoader
{
    private const string SchemaFileName = "Схема.yml";
    private const string DocsWalkerSubdir = ".docswalker";

    /// <summary>Имя поля документа: id.</summary>
    public const string IdFieldName = "id";

    /// <summary>Имя поля документа: text (бывший description).</summary>
    public const string TextFieldName = "text";

    /// <summary>Имя поля документа: sections (бывший content).</summary>
    public const string SectionsFieldName = "sections";

    /// <summary>Универсальный формат склейки id+title для смысловых узлов (title_source=inline_key).</summary>
    public const string InlineKeyFormat = "(#{id}) {title}";

    public static DocumentLoadResult Load(string docsRoot, SchemaDocument schema)
    {
        if (!Directory.Exists(docsRoot))
            throw new GraphLoadException(
                "docs_not_found",
                docsRoot,
                $"Каталог '{docsRoot}' не существует.");

        var idx = TypeIndex.Build(schema);
        var graph = new Graph();
        var loaded = new List<string>();

        foreach (var file in EnumerateDocumentFiles(docsRoot))
        {
            loaded.Add(file);
            LoadFile(graph, idx, docsRoot, file);
        }

        return new DocumentLoadResult { Graph = graph, LoadedFiles = loaded };
    }

    private static IEnumerable<string> EnumerateDocumentFiles(string docsRoot)
    {
        var paths = Directory.EnumerateFiles(docsRoot, "*.yml", SearchOption.AllDirectories);
        var ordered = paths.OrderBy(p => p, StringComparer.Ordinal);
        foreach (var path in ordered)
        {
            var rel = NormalizeRel(Path.GetRelativePath(docsRoot, path));
            if (rel.Equals(DocsWalkerSubdir, StringComparison.Ordinal) ||
                rel.StartsWith(DocsWalkerSubdir + "/", StringComparison.Ordinal))
                continue;
            if (rel.Equals(SchemaFileName, StringComparison.Ordinal))
                continue;
            yield return path;
        }
    }

    private static string NormalizeRel(string rel) => rel.Replace('\\', '/');

    private static void LoadFile(Graph graph, TypeIndex idx, string docsRoot, string path)
    {
        var rel = NormalizeRel(Path.GetRelativePath(docsRoot, path));
        var title = rel.EndsWith(".yml", StringComparison.Ordinal) ? rel[..^4] : rel;

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var r = new YamlReader(reader, path);

        try
        {
            r.Expect<StreamStart>();
            r.Expect<DocumentStart>();
            ReadDocument(r, graph, idx, rel, title);
            r.Expect<DocumentEnd>();
            r.Expect<StreamEnd>();
        }
        catch (YamlReadException ex)
        {
            throw new GraphLoadException(ex.Code, ex.FilePath, ex.Message);
        }
    }

    private static void ReadDocument(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, string title)
    {
        // document — структурный тип; здесь интересна сама форма (id/text/sections),
        // typeName объявлен в Схеме как "document".
        idx.GetType("document", sourceFile);

        r.Expect<MappingStart>();

        int? docId = null;
        string? text = null;
        bool sawSections = false;
        var sectionIds = new List<int>();
        bool firstKey = true;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();

            if (key == IdFieldName)
            {
                if (!firstKey)
                    throw new GraphLoadException(
                        "id_not_first",
                        sourceFile,
                        $"В документе '{title}' поле 'id' должно стоять первым в mapping.");
                docId = ReadIntScalar(r, "id", sourceFile);
            }
            else if (key == TextFieldName)
            {
                text = r.NextScalarValue();
            }
            else if (key == SectionsFieldName)
            {
                if (docId is null)
                    throw new GraphLoadException(
                        "id_not_first",
                        sourceFile,
                        $"В документе '{title}' поле 'id' обязано идти раньше '{SectionsFieldName}'.");
                sawSections = true;
                ReadSections(r, graph, idx, sourceFile, docId.Value, sectionIds);
            }
            else
            {
                throw new GraphLoadException(
                    "unknown_field",
                    sourceFile,
                    $"В документе '{title}' неизвестное поле '{key}'.");
            }

            firstKey = false;
        }

        r.Expect<MappingEnd>();

        if (docId is null)
            throw new GraphLoadException("missing_id", sourceFile,
                $"Документ '{title}' без поля 'id'.");
        if (text is null)
            throw new GraphLoadException("missing_field", sourceFile,
                $"Документ '{title}' без поля 'text'.");

        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [Node.PathRefName] = new[] { Node.RootId },
        };
        if (sawSections && sectionIds.Count > 0)
        {
            outRefs[SectionsFieldName] = sectionIds.ToArray();
        }

        graph.Add(new Node
        {
            Id = docId.Value,
            TypeName = "document",
            Title = title,
            Text = text,
            OutRefs = outRefs,
            SourceFile = sourceFile,
        });
    }

    private static void ReadSections(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, int parentId, List<int> outSectionIds)
    {
        r.Expect<SequenceStart>();
        while (r.Peek() is MappingStart)
        {
            var sectionId = ReadSemanticContainer(r, graph, idx, sourceFile, parentId, "section");
            outSectionIds.Add(sectionId);
        }
        r.Expect<SequenceEnd>();
    }

    /// <summary>
    /// Читает узел смыслового container-типа (section и подобные). Формат —
    /// single_key_mapping `"(#id) title": [list of blocks]`. Внутри каждый блок —
    /// single_key_mapping с именем связи, объявленной в типе как RefDef, и списком атомов.
    /// </summary>
    private static int ReadSemanticContainer(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, int parentId, string typeName)
    {
        var type = idx.GetType(typeName, sourceFile);
        r.Expect<MappingStart>();

        var rawKey = r.NextScalarValue();
        if (!TitleFormat.TryParse(InlineKeyFormat, rawKey, out var id, out var title))
            throw new GraphLoadException(
                "invalid_title_format",
                sourceFile,
                $"Ключ '{rawKey}' не соответствует формату '(#id) title' для типа '{typeName}'.");

        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [Node.PathRefName] = new[] { parentId },
        };

        r.Expect<SequenceStart>();
        var seenBlocks = new HashSet<string>(StringComparer.Ordinal);
        while (r.Peek() is MappingStart)
        {
            r.Expect<MappingStart>();
            var blockName = r.NextScalarValue();
            if (!seenBlocks.Add(blockName))
                throw new GraphLoadException(
                    "duplicate_block",
                    sourceFile,
                    $"В узле id={id} ('{type.Name}') блок '{blockName}' встречается дважды.");

            var refDef = FindRefDef(type, blockName, sourceFile, id);
            var atomIds = ReadAtoms(r, graph, idx, sourceFile, id, refDef);
            outRefs[blockName] = atomIds;

            r.Expect<MappingEnd>();
        }
        r.Expect<SequenceEnd>();
        r.Expect<MappingEnd>();

        graph.Add(new Node
        {
            Id = id,
            TypeName = type.Name,
            Title = title,
            Text = string.Empty,
            OutRefs = outRefs,
            SourceFile = sourceFile,
        });
        return id;
    }

    private static RefDef FindRefDef(TypeDefinition type, string name, string sourceFile, int parentId)
    {
        foreach (var rd in type.OutRefs)
        {
            if (string.Equals(rd.Name, name, StringComparison.Ordinal)) return rd;
        }
        throw new GraphLoadException(
            "unknown_ref",
            sourceFile,
            $"В узле типа '{type.Name}' (id={parentId}) неизвестная связь '{name}'.");
    }

    private static IReadOnlyList<int> ReadAtoms(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, int parentId, RefDef refDef)
    {
        if (refDef.TargetTypes.Count != 1)
            throw new GraphLoadException(
                "ambiguous_target_type",
                sourceFile,
                $"Связь '{refDef.Name}' имеет несколько target_types ({string.Join(", ", refDef.TargetTypes)}); " +
                "loader на этом шаге поддерживает только однотипные блоки атомов.");

        var atomTypeName = refDef.TargetTypes[0];
        var atomType = idx.GetType(atomTypeName, sourceFile);

        r.Expect<SequenceStart>();
        var ids = new List<int>();
        while (r.Peek() is MappingStart)
        {
            ids.Add(ReadAtom(r, graph, sourceFile, parentId, atomType));
        }
        r.Expect<SequenceEnd>();
        return ids;
    }

    /// <summary>
    /// Читает атом-лист — single_key_mapping `"(#id) title": text`.
    /// Применимо к смысловым типам с text_required=true и пустым OutRefs (statement, rule, definition, …).
    /// </summary>
    private static int ReadAtom(
        YamlReader r, Graph graph, string sourceFile, int parentId, TypeDefinition atomType)
    {
        if (atomType.TitleSource != TitleSource.InlineKey)
            throw new GraphLoadException(
                "unsupported_atom_type",
                sourceFile,
                $"Тип '{atomType.Name}' имеет title_source={atomType.TitleSource}; loader атомов поддерживает только inline_key.");

        r.Expect<MappingStart>();
        var rawKey = r.NextScalarValue();
        if (!TitleFormat.TryParse(InlineKeyFormat, rawKey, out var id, out var title))
            throw new GraphLoadException(
                "invalid_title_format",
                sourceFile,
                $"Ключ '{rawKey}' не соответствует формату '(#id) title' для атома типа '{atomType.Name}'.");

        var text = r.NextScalarValue();
        r.Expect<MappingEnd>();

        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [Node.PathRefName] = new[] { parentId },
        };

        graph.Add(new Node
        {
            Id = id,
            TypeName = atomType.Name,
            Title = title,
            Text = text,
            OutRefs = outRefs,
            SourceFile = sourceFile,
        });
        return id;
    }

    private static int ReadIntScalar(YamlReader r, string field, string sourceFile)
    {
        var raw = r.NextScalarValue();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new GraphLoadException(
                "invalid_field",
                sourceFile,
                $"Поле '{field}': ожидалось целое, получено '{raw}'.");
        if (v < 0)
            throw new GraphLoadException(
                "invalid_field",
                sourceFile,
                $"Поле '{field}': ожидалось неотрицательное целое, получено '{raw}'.");
        return v;
    }
}

/// <summary>
/// Индекс типов из Схемы для быстрого доступа: имя → определение типа.
/// Все типы — node-типы под refs-модель; ref_type/primitive как пользовательские типы упразднены.
/// </summary>
internal sealed class TypeIndex
{
    private readonly Dictionary<string, TypeDefinition> _byName;

    private TypeIndex(Dictionary<string, TypeDefinition> byName)
    {
        _byName = byName;
    }

    public TypeDefinition GetType(string name, string sourceFile)
    {
        if (_byName.TryGetValue(name, out var t)) return t;
        throw new GraphLoadException(
            "unknown_type",
            sourceFile,
            $"Тип '{name}' не объявлен в Схеме.");
    }

    public bool TryGetType(string name, out TypeDefinition type) => _byName.TryGetValue(name, out type!);

    public IEnumerable<TypeDefinition> AllTypes => _byName.Values;

    public static TypeIndex Build(SchemaDocument schema)
    {
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) byName[t.Name] = t;
        return new TypeIndex(byName);
    }
}
