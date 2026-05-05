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
/// Обходит docs/ и строит in-memory <see cref="Graph"/>. Файл Схема.yml и каталог
/// .docswalker/ исключаются — они валидируются отдельно (по мета-схеме / служебные).
/// Парсинг — через event-stream API SharpYaml (см. docs/Стек.yml/«YAML-парсер»).
/// На этапе загрузки выполняется лишь базовая структурная проверка; полный набор
/// проверок целостности — в graph-validator (отдельный шаг стратегии).
/// </summary>
public static class DocumentLoader
{
    private const string SchemaFileName = "Схема.yml";
    private const string DocsWalkerSubdir = ".docswalker";
    private const string ContentBlockName = "content";

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

        ValidateSectionTitlesUnique(graph);

        return new DocumentLoadResult { Graph = graph, LoadedFiles = loaded };
    }

    private static IEnumerable<string> EnumerateDocumentFiles(string docsRoot)
    {
        var paths = Directory.EnumerateFiles(docsRoot, "*.yml", SearchOption.AllDirectories);
        // Детерминированный порядок обхода — упрощает сравнение результатов в тестах.
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
        // title документа — путь относительно docs/ без расширения
        // (см. Правила оформления.yml/«Идентификация документа»).
        var title = rel.EndsWith(".yml", StringComparison.Ordinal)
            ? rel[..^4]
            : rel;

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
        r.Expect<MappingStart>();

        int? docId = null;
        string? description = null;
        bool sawContent = false;
        bool firstKey = true;
        var contentChildIds = new List<int>();

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();

            if (key == "id")
            {
                if (!firstKey)
                    throw new GraphLoadException(
                        "id_not_first",
                        sourceFile,
                        $"В документе '{title}' поле 'id' должно стоять первым в mapping.");
                docId = ReadIntScalar(r, "id", sourceFile);
            }
            else if (key == "description")
            {
                description = r.NextScalarValue();
            }
            else if (key == "content")
            {
                if (docId is null)
                    throw new GraphLoadException(
                        "id_not_first",
                        sourceFile,
                        $"В документе '{title}' поле 'id' обязано идти раньше 'content'.");
                sawContent = true;
                ReadContent(r, graph, idx, sourceFile, docId.Value, contentChildIds);
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
        if (description is null)
            throw new GraphLoadException("missing_field", sourceFile,
                $"Документ '{title}' без поля 'description'.");
        if (!sawContent)
            throw new GraphLoadException("missing_field", sourceFile,
                $"Документ '{title}' без поля 'content'.");

        var fields = new List<FieldValue>
        {
            new("id", docId.Value.ToString(CultureInfo.InvariantCulture), null),
            new("description", description, null),
        };

        graph.Add(new Node
        {
            Id = docId.Value,
            TypeName = "document",
            Title = title,
            ParentId = null,
            ParentBlockName = null,
            SourceFile = sourceFile,
            Fields = fields,
        });
    }

    private static void ReadContent(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, int parentId, List<int> outChildIds)
    {
        r.Expect<SequenceStart>();
        while (r.Peek() is MappingStart)
        {
            var sectionId = ReadSection(r, graph, idx, sourceFile, parentId);
            outChildIds.Add(sectionId);
        }
        r.Expect<SequenceEnd>();
    }

    private static int ReadSection(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, int parentId)
    {
        r.Expect<MappingStart>();
        var sectionType = idx.GetNodeType("section", sourceFile);
        if (sectionType.TitleFormat is null)
            throw new GraphLoadException("missing_title_format", sourceFile,
                "Тип 'section' в Схеме без поля title_format.");

        var rawKey = r.NextScalarValue();
        if (!TitleFormat.TryParse(sectionType.TitleFormat, rawKey, out var sectionId, out var sectionTitle))
            throw new GraphLoadException("invalid_title_format", sourceFile,
                $"Ключ section '{rawKey}' не соответствует title_format '{sectionType.TitleFormat}'.");

        var (blocks, explicitOutRefs) = ReadSectionBody(r, graph, idx, sourceFile, sectionType, sectionId);

        r.Expect<MappingEnd>();

        graph.Add(new Node
        {
            Id = sectionId,
            TypeName = "section",
            Title = sectionTitle,
            ParentId = parentId,
            ParentBlockName = ContentBlockName,
            SourceFile = sourceFile,
            Blocks = blocks,
            ExplicitOutRefs = explicitOutRefs,
        });
        return sectionId;
    }

    private static (List<NodeBlock> Blocks, IReadOnlyList<Ref>? ExplicitOutRefs) ReadSectionBody(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, NodeType sectionType, int sectionId)
    {
        r.Expect<SequenceStart>();
        var blocks = new List<NodeBlock>();
        IReadOnlyList<Ref>? explicitRefs = null;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        while (r.Peek() is MappingStart)
        {
            r.Expect<MappingStart>();
            var blockName = r.NextScalarValue();
            if (!seenNames.Add(blockName))
                throw new GraphLoadException("duplicate_block", sourceFile,
                    $"В section id={sectionId} блок '{blockName}' встречается дважды.");

            var blockDef = FindBlockDef(sectionType, blockName, sourceFile, sectionId);
            var (block, refs) = ReadBlockBody(r, graph, idx, sourceFile, sectionId, blockName, blockDef.Of);
            if (block is not null) blocks.Add(block);
            if (refs is not null) explicitRefs = refs;

            r.Expect<MappingEnd>();
        }
        r.Expect<SequenceEnd>();
        return (blocks, explicitRefs);
    }

    private static (NodeBlock? Block, IReadOnlyList<Ref>? Refs) ReadBlockBody(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile,
        int parentId, string blockName, string elementTypeName)
    {
        if (elementTypeName == "text")
        {
            var items = ReadScalarList(r);
            return (new TextBlock(blockName, items), null);
        }
        if (elementTypeName == "reference")
        {
            var refs = ReadOutRefs(r, parentId, sourceFile);
            return (new OutRefsBlock(blockName, refs), refs);
        }

        var elementType = idx.GetNodeType(elementTypeName, sourceFile);
        var ids = ReadChildNodes(r, graph, idx, sourceFile, parentId, blockName, elementType);
        return (new ChildrenBlock(blockName, ids), null);
    }

    private static IReadOnlyList<int> ReadChildNodes(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile,
        int parentId, string parentBlockName, NodeType elementType)
    {
        r.Expect<SequenceStart>();
        var ids = new List<int>();
        while (r.Peek() is MappingStart)
        {
            int childId = elementType.Kind switch
            {
                TypeKind.Mapping =>
                    ReadMappingNode(r, graph, sourceFile, parentId, parentBlockName, elementType),
                TypeKind.SingleKeyMapping =>
                    ReadSingleKeyMappingNode(r, graph, sourceFile, parentId, parentBlockName, elementType),
                _ => throw new GraphLoadException("invalid_kind", sourceFile,
                    $"Тип '{elementType.Name}' не может быть элементом блока: kind={elementType.Kind}."),
            };
            ids.Add(childId);
        }
        r.Expect<SequenceEnd>();
        return ids;
    }

    private static int ReadSingleKeyMappingNode(
        YamlReader r, Graph graph, string sourceFile,
        int parentId, string parentBlockName, NodeType type)
    {
        if (type.TitleFormat is null)
            throw new GraphLoadException("missing_title_format", sourceFile,
                $"У типа '{type.Name}' нет title_format в Схеме.");

        r.Expect<MappingStart>();
        var rawKey = r.NextScalarValue();
        if (!TitleFormat.TryParse(type.TitleFormat, rawKey, out var id, out var title))
            throw new GraphLoadException("invalid_title_format", sourceFile,
                $"Ключ '{rawKey}' не соответствует title_format '{type.TitleFormat}' для типа '{type.Name}'.");

        if (type.ValueType != "text")
            throw new GraphLoadException("unsupported_value_type", sourceFile,
                $"Тип '{type.Name}': value_type='{type.ValueType ?? "<null>"}' не поддерживается loader-ом " +
                $"для single_key_mapping-узлов на этом шаге.");

        var inlineValue = r.NextScalarValue();
        r.Expect<MappingEnd>();

        graph.Add(new Node
        {
            Id = id,
            TypeName = type.Name,
            Title = title,
            ParentId = parentId,
            ParentBlockName = parentBlockName,
            SourceFile = sourceFile,
            InlineValue = inlineValue,
        });
        return id;
    }

    private static int ReadMappingNode(
        YamlReader r, Graph graph, string sourceFile,
        int parentId, string parentBlockName, NodeType type)
    {
        r.Expect<MappingStart>();

        int? id = null;
        string? title = null;
        var fields = new List<FieldValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allowed = type.Fields ?? Array.Empty<FieldDefinition>();
        var fieldByName = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var f in allowed) fieldByName[f.Name] = f;
        bool firstKey = true;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();

            if (key == "id")
            {
                if (!firstKey)
                    throw new GraphLoadException("id_not_first", sourceFile,
                        $"В узле типа '{type.Name}' поле 'id' должно стоять первым.");
                id = ReadIntScalar(r, "id", sourceFile);
                fields.Add(new FieldValue("id", id.Value.ToString(CultureInfo.InvariantCulture), null));
                seen.Add("id");
                firstKey = false;
                continue;
            }
            firstKey = false;

            if (!fieldByName.TryGetValue(key, out var fieldDef))
                throw new GraphLoadException("unknown_field", sourceFile,
                    $"В узле типа '{type.Name}' неизвестное поле '{key}'.");
            if (!seen.Add(key))
                throw new GraphLoadException("duplicate_field", sourceFile,
                    $"В узле типа '{type.Name}' поле '{key}' встречается дважды.");

            FieldValue value;
            if (fieldDef.Type == "list")
            {
                var items = ReadScalarList(r);
                value = new FieldValue(key, null, items);
            }
            else
            {
                value = new FieldValue(key, r.NextScalarValue(), null);
            }
            fields.Add(value);

            if (type.TitleSource == TitleSourceKind.Field &&
                string.Equals(type.TitleField, key, StringComparison.Ordinal))
            {
                title = value.Scalar;
            }
        }

        r.Expect<MappingEnd>();

        if (id is null)
            throw new GraphLoadException("missing_id", sourceFile,
                $"Узел типа '{type.Name}' без поля 'id'.");
        if (type.TitleSource == TitleSourceKind.Field && title is null)
            throw new GraphLoadException("missing_field", sourceFile,
                $"Узел типа '{type.Name}': обязательное title-поле '{type.TitleField}' отсутствует.");

        graph.Add(new Node
        {
            Id = id.Value,
            TypeName = type.Name,
            Title = title ?? string.Empty,
            ParentId = parentId,
            ParentBlockName = parentBlockName,
            SourceFile = sourceFile,
            Fields = fields,
        });
        return id.Value;
    }

    private static IReadOnlyList<Ref> ReadOutRefs(YamlReader r, int fromId, string sourceFile)
    {
        r.Expect<SequenceStart>();
        var refs = new List<Ref>();
        while (r.Peek() is MappingStart)
        {
            r.Expect<MappingStart>();
            var refTypeName = r.NextScalarValue();
            var idStr = r.NextScalarValue();
            r.Expect<MappingEnd>();

            if (!int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var toId))
                throw new GraphLoadException("invalid_ref_target", sourceFile,
                    $"out_refs у id={fromId}: для типа связи '{refTypeName}' ожидался целочисленный id, получено '{idStr}'.");
            refs.Add(new Ref(fromId, refTypeName, toId, RefOrigin.Explicit));
        }
        r.Expect<SequenceEnd>();
        return refs;
    }

    private static IReadOnlyList<string> ReadScalarList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<string>();
        while (r.Peek() is Scalar)
        {
            list.Add(r.NextScalarValue());
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static int ReadIntScalar(YamlReader r, string field, string sourceFile)
    {
        var raw = r.NextScalarValue();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new GraphLoadException("invalid_field", sourceFile,
                $"Поле '{field}': ожидалось целое, получено '{raw}'.");
        if (v < 0)
            throw new GraphLoadException("invalid_field", sourceFile,
                $"Поле '{field}': ожидалось неотрицательное целое, получено '{raw}'.");
        return v;
    }

    private static BlockDefinition FindBlockDef(NodeType type, string blockName, string sourceFile, int parentId)
    {
        if (type.Blocks is not null)
        {
            foreach (var b in type.Blocks)
                if (string.Equals(b.Name, blockName, StringComparison.Ordinal)) return b;
        }
        throw new GraphLoadException("unknown_block", sourceFile,
            $"В узле типа '{type.Name}' (id={parentId}) неизвестный блок '{blockName}'.");
    }

    private static void ValidateSectionTitlesUnique(Graph graph)
    {
        var rootByNode = new Dictionary<int, int>();
        foreach (var node in graph.ById.Values)
        {
            rootByNode[node.Id] = FindRootDoc(graph, node);
        }
        var seen = new Dictionary<(int Doc, string Title), int>();
        foreach (var section in graph.GetByType("section"))
        {
            var doc = rootByNode[section.Id];
            var key = (doc, section.Title);
            if (seen.TryGetValue(key, out var prev))
                throw new GraphLoadException("duplicate_section_title", section.SourceFile,
                    $"В документе id={doc} секция с title='{section.Title}' встречается дважды (id={prev} и id={section.Id}).");
            seen[key] = section.Id;
        }
    }

    private static int FindRootDoc(Graph graph, Node node)
    {
        var current = node;
        while (current.ParentId is int pid)
        {
            current = graph.GetById(pid)!;
        }
        return current.Id;
    }
}

/// <summary>
/// Индекс типов из Схемы для быстрого доступа: имя → определение типа.
/// Различает три категории — node-типы (mapping/single_key_mapping/list),
/// типы связей (ref_type) и примитивы.
/// </summary>
internal sealed class TypeIndex
{
    private readonly Dictionary<string, NodeType> _nodeTypes;
    private readonly Dictionary<string, RefType> _refTypes;
    private readonly HashSet<string> _primitives;

    private TypeIndex(
        Dictionary<string, NodeType> nodeTypes,
        Dictionary<string, RefType> refTypes,
        HashSet<string> primitives)
    {
        _nodeTypes = nodeTypes;
        _refTypes = refTypes;
        _primitives = primitives;
    }

    public NodeType GetNodeType(string name, string sourceFile)
    {
        if (_nodeTypes.TryGetValue(name, out var t)) return t;
        throw new GraphLoadException("unknown_type", sourceFile,
            $"Тип '{name}' не объявлен в Схеме как mapping/single_key_mapping/list.");
    }

    public bool TryGetRefType(string name, out RefType type) => _refTypes.TryGetValue(name, out type!);

    public bool IsPrimitive(string name) => _primitives.Contains(name);

    public static TypeIndex Build(SchemaDocument schema)
    {
        var nodeTypes = new Dictionary<string, NodeType>(StringComparer.Ordinal);
        var refTypes = new Dictionary<string, RefType>(StringComparer.Ordinal);
        var primitives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
        {
            switch (t)
            {
                case NodeType nt: nodeTypes[nt.Name] = nt; break;
                case RefType rt: refTypes[rt.Name] = rt; break;
                case Primitive p: primitives.Add(p.Name); break;
            }
        }
        return new TypeIndex(nodeTypes, refTypes, primitives);
    }
}
