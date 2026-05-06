using System.Globalization;
using System.Text;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
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
        graph.AttachSchema(schema);
        var loaded = new List<string>();

        // R10: загружаем folder-узлы из .docswalker/folders.yml. folders.yml —
        // primary-источник; FS-каталоги в docs/ должны симметрично соответствовать
        // записям. Расхождение → структурированная GraphLoadException.
        var folders = LoadFolders(graph, idx, docsRoot);

        foreach (var file in EnumerateDocumentFiles(docsRoot))
        {
            loaded.Add(file);
            LoadFile(graph, idx, docsRoot, file, folders);
        }

        return new DocumentLoadResult { Graph = graph, LoadedFiles = loaded };
    }

    /// <summary>
    /// Индекс folder-узлов проекта: сопоставление относительного пути каталога
    /// (например, <c>"guides/advanced"</c>) идентификатору folder-узла. Корень
    /// docs/ присутствует как пустая строка → <see cref="Node.RootId"/>.
    /// </summary>
    private sealed class FoldersIndex
    {
        public Dictionary<string, int> ByRelPath { get; } = new(StringComparer.Ordinal);

        public int Resolve(string relDirPath)
        {
            if (relDirPath.Length == 0) return Node.RootId;
            return ByRelPath.TryGetValue(relDirPath, out var id) ? id : -1;
        }
    }

    /// <summary>
    /// Читает folders.yml, валидирует FS↔folders.yml-симметрию и добавляет
    /// folder-узлы в граф. Порядок добавления — от root к листьям, чтобы
    /// path-ссылки уже разрешались. У всех folder-узлов
    /// <see cref="Node.SourceFile"/> = <see cref="FoldersFile.RelativePath"/>.
    /// </summary>
    private static FoldersIndex LoadFolders(Graph graph, TypeIndex idx, string docsRoot)
    {
        // Тип folder обязан быть объявлен в Схеме (ошибка типизации для инвариантности).
        idx.GetType("folder", FoldersFile.RelativePath);

        var foldersPath = FoldersFile.AbsolutePath(docsRoot);
        IReadOnlyList<FolderRecord> records;
        try
        {
            records = FoldersFile.Read(foldersPath);
        }
        catch (FoldersFileException ex)
        {
            throw new GraphLoadException(ex.Code, ex.FilePath, ex.Message);
        }

        var index = new FoldersIndex();

        // Уникальность id и (parent, title).
        var byId = new Dictionary<int, FolderRecord>();
        var byParentTitle = new Dictionary<(int Parent, string Title), int>();
        foreach (var rec in records)
        {
            if (!byId.TryAdd(rec.Id, rec))
                throw new GraphLoadException(
                    "duplicate_folder_id",
                    foldersPath,
                    $"folders.yml: id={rec.Id} встречается дважды.");
            var key = (rec.ParentId, rec.Title);
            if (!byParentTitle.TryAdd(key, rec.Id))
                throw new GraphLoadException(
                    "duplicate_folder_name",
                    foldersPath,
                    $"folders.yml: под parent_id={rec.ParentId} имя '{rec.Title}' встречается дважды (id={byParentTitle[key]} и id={rec.Id}).");
        }

        // Топологическая сортировка от root: BFS.
        var byParent = new Dictionary<int, List<FolderRecord>>();
        foreach (var rec in records)
        {
            if (!byParent.TryGetValue(rec.ParentId, out var list))
                byParent[rec.ParentId] = list = new List<FolderRecord>();
            list.Add(rec);
        }

        var ordered = new List<FolderRecord>(records.Count);
        var queue = new Queue<int>();
        if (byParent.TryGetValue(Node.RootId, out var rootChildren))
        {
            foreach (var ch in rootChildren) queue.Enqueue(ch.Id);
        }
        var visited = new HashSet<int>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            ordered.Add(byId[id]);
            if (byParent.TryGetValue(id, out var children))
            {
                foreach (var ch in children) queue.Enqueue(ch.Id);
            }
        }
        if (visited.Count != records.Count)
        {
            // Все непосещённые — это записи, чей parent-id неизвестен или вне root-достижимости.
            var orphan = records.FirstOrDefault(r => !visited.Contains(r.Id));
            throw new GraphLoadException(
                "folder_orphan",
                foldersPath,
                orphan is null
                    ? "folders.yml: найдены записи с недостижимым parent (цикл или висячая ссылка)."
                    : $"folders.yml: запись id={orphan.Id} '{orphan.Title}' имеет parent_id={orphan.ParentId}, не достижимый от root.");
        }

        // Реляр-путь FS-каталога для каждой записи (по цепочке title до root).
        var relPathById = new Dictionary<int, string>();
        foreach (var rec in ordered)
        {
            var parentRel = rec.ParentId == Node.RootId ? string.Empty : relPathById[rec.ParentId];
            var rel = parentRel.Length == 0 ? rec.Title : parentRel + "/" + rec.Title;
            relPathById[rec.Id] = rel;
            index.ByRelPath[rel] = rec.Id;
        }

        // Симметрия FS↔folders.yml.
        var fsRelDirs = EnumerateFsRelDirs(docsRoot);
        foreach (var rec in ordered)
        {
            var expected = relPathById[rec.Id];
            if (!fsRelDirs.Contains(expected))
                throw new GraphLoadException(
                    "folder_dir_missing",
                    foldersPath,
                    $"folders.yml: запись id={rec.Id} '{rec.Title}' указывает на каталог 'docs/{expected}', которого нет на FS.");
        }
        foreach (var dir in fsRelDirs)
        {
            if (!index.ByRelPath.ContainsKey(dir))
                throw new GraphLoadException(
                    "folder_record_missing",
                    foldersPath,
                    $"Каталог 'docs/{dir}' существует на FS, но в .docswalker/folders.yml нет соответствующей записи.");
        }

        // Добавление folder-узлов в граф.
        foreach (var rec in ordered)
        {
            var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                [Node.PathRefName] = new[] { rec.ParentId },
            };
            graph.Add(new Node
            {
                Id = rec.Id,
                TypeName = "folder",
                Title = rec.Title,
                Text = string.Empty,
                OutRefs = outRefs,
                SourceFile = FoldersFile.RelativePath,
            });
        }

        return index;
    }

    /// <summary>
    /// Перечисляет все подкаталоги <paramref name="docsRoot"/> (рекурсивно)
    /// в виде относительных путей с / разделителями. Служебный
    /// <c>.docswalker/</c> и его потомки исключены.
    /// </summary>
    private static HashSet<string> EnumerateFsRelDirs(string docsRoot)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(docsRoot, "*", SearchOption.AllDirectories))
        {
            var rel = NormalizeRel(Path.GetRelativePath(docsRoot, dir));
            if (rel.Equals(DocsWalkerSubdir, StringComparison.Ordinal) ||
                rel.StartsWith(DocsWalkerSubdir + "/", StringComparison.Ordinal))
                continue;
            result.Add(rel);
        }
        return result;
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

    private static void LoadFile(Graph graph, TypeIndex idx, string docsRoot, string path, FoldersIndex folders)
    {
        var rel = NormalizeRel(Path.GetRelativePath(docsRoot, path));
        // title документа = имя файла без расширения (без каталога-префикса);
        // каталог-префикс хранится в graph как цепочка folder-узлов.
        var fileName = Path.GetFileName(rel);
        var title = fileName.EndsWith(".yml", StringComparison.Ordinal) ? fileName[..^4] : fileName;
        var dirRel = NormalizeRel(Path.GetDirectoryName(rel) ?? string.Empty);
        var parentId = folders.Resolve(dirRel);
        if (parentId < 0)
        {
            // Невозможно после симметрии-чека LoadFolders, но пробрасываем явно.
            throw new GraphLoadException(
                "folder_record_missing",
                rel,
                $"Каталог 'docs/{dirRel}' содержит документ '{fileName}', но не имеет записи в .docswalker/folders.yml.");
        }

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var r = new YamlReader(reader, path);

        try
        {
            r.Expect<StreamStart>();
            r.Expect<DocumentStart>();
            ReadDocument(r, graph, idx, rel, title, parentId);
            r.Expect<DocumentEnd>();
            r.Expect<StreamEnd>();
        }
        catch (YamlReadException ex)
        {
            throw new GraphLoadException(ex.Code, ex.FilePath, ex.Message);
        }
    }

    private static void ReadDocument(
        YamlReader r, Graph graph, TypeIndex idx, string sourceFile, string title, int parentId)
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
            [Node.PathRefName] = new[] { parentId },
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
            if (string.Equals(blockName, Node.PathRefName, StringComparison.Ordinal))
                throw new GraphLoadException(
                    "reserved_ref_name",
                    sourceFile,
                    $"В узле id={id} ('{type.Name}') встречен блок 'path' — связь path управляется только структурой docs/, не записывается в YAML.");
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
