using System.Globalization;
using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
using DocsWalker.Core.Validation;
using DocsWalker.Core.Yaml;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Ошибка write-операции, возникшая до прогона валидатора: несуществующие id,
/// неизвестный тип, неверная форма входа, нарушение системных запретов
/// (path управляется только структурно, root неперемещаем). Содержательные нарушения,
/// обнаруженные валидатором, передаются через <see cref="WriteValidationException"/>.
/// </summary>
public sealed class WriteApiException : Exception
{
    public string Code { get; }
    public string? Hint { get; }

    public WriteApiException(string code, string message, string? hint = null) : base(message)
    {
        Code = code;
        Hint = hint;
    }
}

/// <summary>
/// Ошибка валидации после применения write-операций. Содержит весь список
/// <see cref="ValidationError"/>, найденных <see cref="Validator"/>; CLI/MCP должны
/// сериализовать список целиком, не теряя ни одной записи.
/// </summary>
public sealed class WriteValidationException : Exception
{
    public IReadOnlyList<ValidationError> Errors { get; }

    public WriteValidationException(IReadOnlyList<ValidationError> errors)
        : base($"Запись отклонена валидатором: {errors.Count} ошибк(а/и).")
    {
        Errors = errors;
    }
}

/// <summary>
/// Описание одной операции в составе транзакции под refs-модель. Перечисление имён
/// (<see cref="Type"/>) — стабильный контракт CLI/MCP.
/// </summary>
public abstract record WriteOp(string Type);

/// <summary>
/// Создание узла. Параметры:
///   <paramref name="TypeName"/>, <paramref name="Title"/>, опц. <paramref name="Text"/>
///   и <paramref name="Refs"/> — карта &lt;имя_связи, list&lt;target_id&gt;&gt; для всех
///   required out_refs контракта типа (включая встроенную <c>path</c> для всех типов
///   с непустым path_targets, кроме root).
/// </summary>
public sealed record CreateNodeOp(
    string TypeName,
    string Title,
    string? Text,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Refs) : WriteOp("create-node");

public sealed record UpdateNodeOp(int Id, string? NewTitle, string? NewText) : WriteOp("update-node");

public sealed record DeleteNodeOp(int Id) : WriteOp("delete-node");

public sealed record MoveNodeOp(int Id, int NewParentId) : WriteOp("move-node");

public sealed record CreateRefOp(int FromId, string Name, int ToId) : WriteOp("create-ref");

public sealed record DeleteRefOp(int FromId, string Name, int ToId) : WriteOp("delete-ref");

/// <summary>
/// Результат одной операции. Поле <c>Type</c> совпадает с именем входной операции,
/// поле <c>Data</c> содержит данные, специфичные для команды.
/// </summary>
public sealed record WriteOpResult(string Type, JsonObject Data);

public sealed record WriteResult(IReadOnlyList<WriteOpResult> OpResults);

/// <summary>
/// Параметры окружения write-операций: пути к docs/, Схема.yml, sequence.txt и
/// мета-схема (для прогона <see cref="Validator"/>). Используется как контейнер
/// зависимостей; не хранит изменяемого состояния.
/// </summary>
public sealed class WriteContext
{
    public string DocsRoot { get; }
    public string SchemaPath { get; }
    public string SequencePath { get; }
    public string FoldersPath { get; }
    public MetaSchemaDocument MetaSchema { get; }

    public WriteContext(
        string docsRoot, string schemaPath, string sequencePath, string foldersPath,
        MetaSchemaDocument metaSchema)
    {
        DocsRoot = docsRoot;
        SchemaPath = schemaPath;
        SequencePath = sequencePath;
        FoldersPath = foldersPath;
        MetaSchema = metaSchema;
    }

    public static WriteContext FromRoot(string root)
    {
        var docsRoot = Path.Combine(root, "docs");
        var schemaPath = Path.Combine(docsRoot, "Схема.yml");
        var metaSchemaPath = Path.Combine(docsRoot, ".docswalker", "meta-schema.yml");
        var sequencePath = Path.Combine(docsRoot, ".docswalker", "sequence.txt");
        var foldersPath = FoldersFile.AbsolutePath(docsRoot);
        var meta = SchemaLoader.LoadMetaSchema(metaSchemaPath);
        return new WriteContext(docsRoot, schemaPath, sequencePath, foldersPath, meta);
    }
}

/// <summary>
/// Write-API DocsWalker под refs-модель. Все операции применяются на снимке состояния
/// (граф + схема), после чего весь снимок прогоняется через <see cref="Validator"/>;
/// при успехе затронутые YAML-файлы перезаписываются <see cref="AtomicWriter"/>'ом
/// в одной пачке с новым значением sequence.txt. При ошибке (валидация / IO / конфликт)
/// ничего не записывается, ошибка возвращается структурированно.
///
/// Изменение Схемы — отдельная транзакция, выполняется ручной правкой docs/Схема.yml
/// (см. docs/DocsWalker.yml/«Расширение Схемы вручную»). Через write-API Схема не правится.
/// </summary>
public sealed class WriteApi
{
    private readonly WriteContext _ctx;
    private readonly object _processLock = new();

    public WriteApi(WriteContext ctx)
    {
        _ctx = ctx;
    }

    public WriteResult Apply(IReadOnlyList<WriteOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (ops.Count == 0)
            throw new WriteApiException(
                "empty_transaction",
                "Список операций пуст — пачка должна содержать хотя бы одну операцию.");

        // Один in-process lock на всё время операции — sequence-файл и YAML-файлы пишутся
        // непротиворечиво. Cross-process safety — отдельный шаг (см. step-cross-process-lock).
        lock (_processLock)
        {
            return ApplyLocked(ops);
        }
    }

    public WriteResult ApplyOne(WriteOp op) => Apply(new[] { op });

    private WriteResult ApplyLocked(IReadOnlyList<WriteOp> ops)
    {
        var schema = SchemaLoader.LoadSchema(_ctx.SchemaPath);
        var loaded = DocumentLoader.Load(_ctx.DocsRoot, schema);
        var counter = new SequenceCounter(_ctx.SequencePath);
        var sequenceBase = counter.Read();

        var state = new WriteState(_ctx.DocsRoot, schema, loaded.Graph, sequenceBase);

        var opResults = new List<WriteOpResult>(ops.Count);
        for (int i = 0; i < ops.Count; i++)
        {
            try
            {
                opResults.Add(ApplyOp(state, ops[i]));
            }
            catch (WriteApiException ex)
            {
                throw new WriteApiException(ex.Code, $"Операция #{i} ({ops[i].Type}): {ex.Message}", ex.Hint);
            }
        }

        var newGraph = state.BuildGraph();

        var validator = new Validator(_ctx.MetaSchema, schema);
        var newSequence = state.SequenceBase + state.IdsConsumed;
        var validation = validator.Validate(newGraph, newSequence);
        if (!validation.IsValid)
            throw new WriteValidationException(validation.Errors);

        var targets = new List<AtomicWriteTarget>();
        foreach (var rootId in state.AffectedDocumentIds)
        {
            var docNode = newGraph.GetById(rootId)
                ?? throw new WriteApiException(
                    "internal_inconsistency",
                    $"Документ id={rootId} помечен как изменённый, но отсутствует в новом графе.");
            if (!string.Equals(docNode.TypeName, "document", StringComparison.Ordinal))
                throw new WriteApiException(
                    "internal_inconsistency",
                    $"Узел id={rootId} помечен как dirty document, но имеет тип '{docNode.TypeName}'.");
            var yaml = Emitter.EmitDocument(newGraph, schema, docNode);
            var absolutePath = Path.Combine(_ctx.DocsRoot,
                docNode.SourceFile.Replace('/', Path.DirectorySeparatorChar));
            targets.Add(new AtomicWriteTarget(absolutePath, yaml));
        }

        if (state.IdsConsumed > 0)
        {
            var content = newSequence.ToString(CultureInfo.InvariantCulture) + "\n";
            targets.Add(new AtomicWriteTarget(_ctx.SequencePath, content));
        }

        if (state.FoldersDirty)
        {
            var records = state.AllNodes
                .Where(n => string.Equals(n.TypeName, "folder", StringComparison.Ordinal))
                .OrderBy(n => n.Id)
                .Select(n => new FolderRecord(n.Id, n.ParentId ?? Node.RootId, n.Title))
                .ToList();
            targets.Add(new AtomicWriteTarget(_ctx.FoldersPath, FoldersFile.Emit(records)));
        }

        if (targets.Count == 0 && state.FsOperations.Count == 0)
            throw new WriteApiException(
                "no_effect",
                "Пачка операций не приводит ни к каким изменениям в файлах.");

        AtomicWriter.WriteAndApply(targets, state.FsOperations);

        return new WriteResult(opResults);
    }

    private static WriteOpResult ApplyOp(WriteState s, WriteOp op) => op switch
    {
        CreateNodeOp c => ApplyCreateNode(s, c),
        UpdateNodeOp u => ApplyUpdateNode(s, u),
        DeleteNodeOp d => ApplyDeleteNode(s, d),
        MoveNodeOp m => ApplyMoveNode(s, m),
        CreateRefOp cr => ApplyCreateRef(s, cr),
        DeleteRefOp dr => ApplyDeleteRef(s, dr),
        _ => throw new WriteApiException(
            "unknown_op",
            $"Неизвестный тип операции '{op.Type}'."),
    };

    private static WriteOpResult ApplyCreateNode(WriteState s, CreateNodeOp op)
    {
        var type = s.ResolveType(op.TypeName);
        if (string.IsNullOrEmpty(op.Title))
            throw new WriteApiException(
                "missing_parameter",
                $"Для типа '{op.TypeName}' требуется параметр 'title'.");

        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

        // path — обязателен у всех типов с непустым path_targets (всё кроме root,
        // который через write-API не создаётся).
        bool needsPath = type.PathTargets.Count > 0;
        if (needsPath)
        {
            if (!op.Refs.TryGetValue(Node.PathRefName, out var pathTargets) || pathTargets.Count == 0)
                throw new WriteApiException(
                    "missing_required_ref",
                    $"Для типа '{op.TypeName}' требуется значение связи 'path' (id родителя).",
                    "Передай --path=<parent_id> или укажи refs.path в JSON.");
            if (pathTargets.Count > 1)
                throw new WriteApiException(
                    "invalid_cardinality",
                    $"Связь 'path' должна иметь ровно одну цель, передано {pathTargets.Count}.");
            int parentId = pathTargets[0];
            if (parentId != Node.RootId && s.GetNode(parentId) is null)
                throw new WriteApiException(
                    "parent_not_found",
                    $"Родитель id={parentId} не найден.",
                    "Сверь parent_id с актуальным графом через get-map.");
            // Тип родителя должен входить в path_targets создаваемого узла (root — особое имя).
            var parentTypeName = parentId == Node.RootId
                ? Node.RootTypeName
                : s.GetNode(parentId)!.TypeName;
            if (!type.PathTargets.Any(p => string.Equals(p, parentTypeName, StringComparison.Ordinal)))
                throw new WriteApiException(
                    "invalid_path_target",
                    $"Тип '{type.Name}' не допускает родителя типа '{parentTypeName}'.",
                    $"Допустимые типы родителя: {string.Join(", ", type.PathTargets)}.");
            outRefs[Node.PathRefName] = new[] { parentId };
        }

        // Для каждой объявленной связи типа: если переданы цели — кладём их;
        // если связь required и целей нет — отвергаем.
        foreach (var rd in type.OutRefs)
        {
            if (op.Refs.TryGetValue(rd.Name, out var targets) && targets.Count > 0)
            {
                if (rd.Cardinality == Cardinality.One && targets.Count > 1)
                    throw new WriteApiException(
                        "invalid_cardinality",
                        $"Связь '{rd.Name}' имеет cardinality=one; передано {targets.Count} целей.");
                foreach (var tid in targets)
                {
                    if (tid != Node.RootId && s.GetNode(tid) is null)
                        throw new WriteApiException(
                            "ref_target_not_found",
                            $"Связь '{rd.Name}': цель id={tid} не найдена.");
                }
                outRefs[rd.Name] = targets.ToArray();
            }
            else if (rd.Required)
            {
                throw new WriteApiException(
                    "missing_required_ref",
                    $"Для типа '{op.TypeName}' требуется значение связи '{rd.Name}'.",
                    $"Передай --{rd.Name}=<id[,id,...]>; допустимые типы цели: {string.Join(", ", rd.TargetTypes)}.");
            }
        }

        // Имена связей, не объявленные ни как path, ни в типе — отвергаем сразу.
        var declaredNames = new HashSet<string>(StringComparer.Ordinal) { Node.PathRefName };
        foreach (var rd in type.OutRefs) declaredNames.Add(rd.Name);
        foreach (var name in op.Refs.Keys)
        {
            if (!declaredNames.Contains(name))
                throw new WriteApiException(
                    "unknown_ref",
                    $"Тип '{op.TypeName}' не объявляет связь '{name}'.",
                    "Допустимые имена связей смотри в get-schema или describe-type.");
        }

        // R10: создание folder идёт по отдельной ветке — у folder нет
        // SourceFile-документа, путь хранится в .docswalker/folders.yml,
        // а на FS нужно создать каталог одной FS-операцией.
        if (string.Equals(type.Name, "folder", StringComparison.Ordinal))
        {
            return ApplyCreateFolder(s, op, outRefs);
        }

        var sourceFile = ResolveSourceFile(s, type, op.Title, outRefs);

        var id = s.ReserveId();
        var newNode = new Node
        {
            Id = id,
            TypeName = type.Name,
            Title = op.Title,
            Text = op.Text ?? string.Empty,
            OutRefs = outRefs,
            SourceFile = sourceFile,
        };
        s.Add(newNode);

        if (string.Equals(type.Name, "document", StringComparison.Ordinal))
            s.MarkDocumentDirty(id);
        else
            s.MarkDocumentDirtyForNode(id);

        return new WriteOpResult("create-node", new JsonObject
        {
            ["id"] = id,
            ["type"] = type.Name,
            ["title"] = op.Title,
        });
    }

    /// <summary>
    /// Создание folder-узла: проверяет коллизию имени под тем же родителем,
    /// строит FS-путь по цепочке title до root, регистрирует
    /// <see cref="FsCreateDirectory"/> и помечает folders.yml как dirty.
    /// SourceFile у folder-узлов — общий <see cref="FoldersFile.RelativePath"/>.
    /// </summary>
    private static WriteOpResult ApplyCreateFolder(
        WriteState s, CreateNodeOp op,
        Dictionary<string, IReadOnlyList<int>> outRefs)
    {
        var parentId = outRefs[Node.PathRefName][0];

        // Коллизия: уже есть folder с тем же title под этим parent.
        foreach (var sibling in s.GetChildren(parentId))
        {
            if (string.Equals(sibling.TypeName, "folder", StringComparison.Ordinal)
                && string.Equals(sibling.Title, op.Title, StringComparison.Ordinal))
            {
                throw new WriteApiException(
                    "duplicate_folder_name",
                    $"Под parent id={parentId} уже существует folder с title '{op.Title}' (id={sibling.Id}).",
                    "Выбери другое имя или используй существующий folder.");
            }
        }

        // Полный FS-путь нового каталога.
        var parentRel = BuildFolderRelativePath(s, parentId);
        var newRel = parentRel.Length == 0 ? op.Title : parentRel + "/" + op.Title;
        var absolutePath = Path.Combine(s.DocsRoot,
            newRel.Replace('/', Path.DirectorySeparatorChar));

        // Защита от рассинхронизации: если каталог уже существует на FS, но
        // записи в графе под ним нет — это симптом ручной правки docs/.
        if (Directory.Exists(absolutePath))
            throw new WriteApiException(
                "fs_collision",
                $"На FS уже существует каталог '{absolutePath}', но в .docswalker/folders.yml нет соответствующей записи.",
                "Это может быть остаточный каталог от ручной правки; убери вручную или восстанови запись в folders.yml.");

        var id = s.ReserveId();
        s.Add(new Node
        {
            Id = id,
            TypeName = "folder",
            Title = op.Title,
            Text = string.Empty,
            OutRefs = outRefs,
            SourceFile = FoldersFile.RelativePath,
        });

        s.AddFsOperation(new FsCreateDirectory(absolutePath));
        s.MarkFoldersDirty();

        return new WriteOpResult("create-node", new JsonObject
        {
            ["id"] = id,
            ["type"] = "folder",
            ["title"] = op.Title,
        });
    }

    /// <summary>
    /// Восстанавливает относительный путь FS-каталога folder-узла по цепочке title до root.
    /// Для root возвращает пустую строку.
    /// </summary>
    private static string BuildFolderRelativePath(WriteState s, int folderId)
    {
        if (folderId == Node.RootId) return string.Empty;
        var node = s.GetNode(folderId)
            ?? throw new WriteApiException(
                "parent_not_found",
                $"Folder id={folderId} не найден.");
        if (!string.Equals(node.TypeName, "folder", StringComparison.Ordinal))
            throw new WriteApiException(
                "invalid_path_target",
                $"id={folderId} имеет тип '{node.TypeName}', ожидается folder.");
        var parentId = node.ParentId ?? Node.RootId;
        var parentRel = BuildFolderRelativePath(s, parentId);
        return parentRel.Length == 0 ? node.Title : parentRel + "/" + node.Title;
    }

    /// <summary>
    /// Определяет SourceFile для нового узла:
    /// title_source=filename → "{folder_rel_path}/{title}.yml" (для document);
    /// title_source=inline_key → SourceFile path-родителя.
    /// title_source=dirname (folder) обрабатывается отдельно в <see cref="ApplyCreateFolder"/>;
    /// сюда не должно попадать.
    /// </summary>
    private static string ResolveSourceFile(
        WriteState s, TypeDefinition type, string title,
        IReadOnlyDictionary<string, IReadOnlyList<int>> outRefs)
    {
        if (!outRefs.TryGetValue(Node.PathRefName, out var pathTargets) || pathTargets.Count == 0)
            throw new WriteApiException(
                "internal_inconsistency",
                $"У узла типа '{type.Name}' отсутствует связь path после её резервирования.");
        var parentId = pathTargets[0];

        if (type.TitleSource == TitleSource.Filename)
        {
            // Документ может лежать под root или под folder; SourceFile = <rel>/title.yml.
            var parentRel = BuildFolderRelativePath(s, parentId);
            return parentRel.Length == 0 ? title + ".yml" : parentRel + "/" + title + ".yml";
        }
        if (type.TitleSource == TitleSource.Dirname)
            throw new WriteApiException(
                "internal_inconsistency",
                $"Тип '{type.Name}' (title_source=dirname) должен обрабатываться отдельной веткой ApplyCreateFolder.");

        if (parentId == Node.RootId)
            throw new WriteApiException(
                "invalid_path_target",
                $"Тип '{type.Name}' (title_source=inline_key) не может иметь root в качестве родителя.");
        var parent = s.GetNode(parentId)
            ?? throw new WriteApiException(
                "parent_not_found",
                $"Родитель id={parentId} не найден.");
        return parent.SourceFile;
    }

    private static WriteOpResult ApplyUpdateNode(WriteState s, UpdateNodeOp op)
    {
        var node = s.GetNode(op.Id)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел id={op.Id} не найден.",
                "Сверь id через get-map / get-nodes.");

        if (op.NewTitle is null && op.NewText is null)
            throw new WriteApiException(
                "no_effect",
                "Update-node без полей 'title' и 'text' не вносит изменений.",
                "Передай хотя бы одно из --title / --text.");

        var newTitle = op.NewTitle ?? node.Title;
        var newText = op.NewText ?? node.Text;

        // Title документа = filename. Переименование документа правит SourceFile —
        // это меняет имя YAML-файла; текущая итерация write-API такое не поддерживает,
        // отвергаем.
        if (string.Equals(node.TypeName, "document", StringComparison.Ordinal)
            && op.NewTitle is not null
            && !string.Equals(op.NewTitle, node.Title, StringComparison.Ordinal))
        {
            throw new WriteApiException(
                "rename_document_unsupported",
                $"Переименование документа id={op.Id} ('{node.Title}') в этой версии write-API не поддерживается.",
                "Title документа = имя файла; переименование требует переименования YAML-файла, появится в отдельной команде.");
        }

        // R10: rename folder требует FS-операций и каскадного rewrite SourceFile у потомков —
        // выполняется в отдельном шаге R11.
        if (string.Equals(node.TypeName, "folder", StringComparison.Ordinal)
            && op.NewTitle is not null
            && !string.Equals(op.NewTitle, node.Title, StringComparison.Ordinal))
        {
            throw new WriteApiException(
                "not_supported",
                $"Переименование folder id={op.Id} ('{node.Title}') в текущей версии не поддержано.",
                "Появится в шаге R11. Для смены имени каталога временно используй прямую правку folders.yml + FS.");
        }

        var updated = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = newTitle,
            Text = newText,
            OutRefs = node.OutRefs,
            SourceFile = node.SourceFile,
        };
        s.Replace(updated);
        s.MarkDocumentDirtyForNode(node.Id);

        return new WriteOpResult("update-node", new JsonObject
        {
            ["id"] = node.Id,
            ["title"] = newTitle,
        });
    }

    private static WriteOpResult ApplyDeleteNode(WriteState s, DeleteNodeOp op)
    {
        var node = s.GetNode(op.Id)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел id={op.Id} не найден.",
                "Сверь id через get-map / get-nodes.");

        if (string.Equals(node.TypeName, "document", StringComparison.Ordinal))
            throw new WriteApiException(
                "delete_document_unsupported",
                $"Удаление документа id={op.Id} ('{node.Title}') в этой версии write-API не поддерживается.",
                "Для удаления документа используй отдельную команду delete-document.");

        var children = s.GetChildren(op.Id).ToList();
        if (children.Count > 0)
            throw new WriteApiException(
                "has_children",
                $"Узел id={op.Id} имеет {children.Count} path-детей; каскадное удаление не поддерживается.",
                "Сначала удали или перенеси детей через delete-node / move-node, затем повтори delete-node.");

        var inRefs = s.ListIncomingRefs(op.Id, includePath: false).ToList();
        if (inRefs.Count > 0)
            throw new WriteApiException(
                "incoming_refs",
                $"Узел id={op.Id} имеет {inRefs.Count} входящих связ(и/ей); удаление запрещено.",
                "Сначала удали входящие связи через delete-ref, затем повтори delete-node.");

        // R10: удаление folder требует отдельной FS-операции и dirty folders.yml.
        if (string.Equals(node.TypeName, "folder", StringComparison.Ordinal))
        {
            var rel = BuildFolderRelativePath(s, node.Id);
            var absolutePath = Path.Combine(s.DocsRoot,
                rel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(absolutePath))
            {
                bool isEmpty = !Directory.EnumerateFileSystemEntries(absolutePath).Any();
                if (!isEmpty)
                    throw new WriteApiException(
                        "folder_not_empty",
                        $"Каталог '{absolutePath}' не пуст; удаление folder допустимо только для пустого каталога.",
                        "Сначала удали все документы и подкаталоги внутри.");
            }
            s.AddFsOperation(new FsDeleteEmptyDirectory(absolutePath));
            s.MarkFoldersDirty();
            s.Remove(op.Id);
            return new WriteOpResult("delete-node", new JsonObject
            {
                ["id"] = op.Id,
            });
        }

        s.MarkDocumentDirtyForNode(op.Id);
        s.Remove(op.Id);

        return new WriteOpResult("delete-node", new JsonObject
        {
            ["id"] = op.Id,
        });
    }

    private static WriteOpResult ApplyMoveNode(WriteState s, MoveNodeOp op)
    {
        var node = s.GetNode(op.Id)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел id={op.Id} не найден.",
                "Сверь id через get-map / get-nodes.");

        if (string.Equals(node.TypeName, "document", StringComparison.Ordinal))
            throw new WriteApiException(
                "cannot_move_document",
                $"Узел id={op.Id} ('{node.Title}') — документ; перенос документов не поддерживается.",
                "Документ нельзя перенести как узел; используй create-document/delete-document для смены файла.");

        // R10: перенос folder требует переноса каталога на FS и каскадного rewrite SourceFile —
        // выполняется в отдельном шаге R11.
        if (string.Equals(node.TypeName, "folder", StringComparison.Ordinal))
            throw new WriteApiException(
                "not_supported",
                $"Перенос folder id={op.Id} ('{node.Title}') в текущей версии не поддержан.",
                "Появится в шаге R11.");

        if (op.NewParentId == op.Id)
            throw new WriteApiException(
                "invalid_move",
                $"Узел id={op.Id} нельзя сделать собственным родителем.");

        if (IsDescendantOf(s, op.NewParentId, op.Id))
            throw new WriteApiException(
                "invalid_move",
                $"Новый родитель id={op.NewParentId} является потомком переносимого узла id={op.Id}.",
                "Выбери родителя вне поддерева переносимого узла.");

        if (op.NewParentId != Node.RootId && s.GetNode(op.NewParentId) is null)
            throw new WriteApiException(
                "parent_not_found",
                $"Новый родитель id={op.NewParentId} не найден.",
                "Сверь new_parent_id через get-map / get-nodes.");

        if (node.ParentId is not int oldParentId)
            throw new WriteApiException(
                "cannot_move_root",
                $"Узел id={op.Id} не имеет связи 'path' (root); перенос невозможен.");

        if (oldParentId == op.NewParentId)
            throw new WriteApiException(
                "no_effect",
                $"Узел id={op.Id} уже имеет path={op.NewParentId}.");

        var newSourceFile = node.SourceFile;
        if (op.NewParentId != Node.RootId)
        {
            var newParent = s.GetNode(op.NewParentId)!;
            newSourceFile = newParent.SourceFile;
        }

        var newOutRefs = CloneRefs(node.OutRefs);
        newOutRefs[Node.PathRefName] = new[] { op.NewParentId };

        var moved = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = node.Title,
            Text = node.Text,
            OutRefs = newOutRefs,
            SourceFile = newSourceFile,
        };
        s.Replace(moved);

        if (!string.Equals(newSourceFile, node.SourceFile, StringComparison.Ordinal))
            UpdateSubtreeSourceFile(s, op.Id, newSourceFile);

        s.MarkDocumentDirtyForNode(oldParentId);
        s.MarkDocumentDirtyForNode(op.NewParentId);

        return new WriteOpResult("move-node", new JsonObject
        {
            ["id"] = op.Id,
            ["new_parent_id"] = op.NewParentId,
        });
    }

    private static WriteOpResult ApplyCreateRef(WriteState s, CreateRefOp op)
    {
        if (string.Equals(op.Name, Node.PathRefName, StringComparison.Ordinal))
            throw new WriteApiException(
                "system_ref_name",
                "Связь 'path' управляется только структурными операциями create-node / move-node / delete-node.",
                "Для смены родителя используй move-node.");

        var src = s.GetNode(op.FromId)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел-источник id={op.FromId} не найден.",
                "Сверь from_id через get-map / get-nodes.");

        if (op.ToId != Node.RootId && s.GetNode(op.ToId) is null)
            throw new WriteApiException(
                "node_not_found",
                $"Узел-цель id={op.ToId} не найден.",
                "Сверь to_id через get-map / get-nodes.");

        var srcType = s.ResolveType(src.TypeName);
        if (!srcType.OutRefs.Any(rd => string.Equals(rd.Name, op.Name, StringComparison.Ordinal)))
            throw new WriteApiException(
                "unknown_ref",
                $"Тип '{src.TypeName}' не объявляет связь '{op.Name}'.",
                "Допустимые имена связей смотри в get-schema или describe-type. Новые имена объявляются ручной правкой Схемы.");

        var existing = src.OutRefs.TryGetValue(op.Name, out var current)
            ? current.ToList()
            : new List<int>();
        if (existing.Contains(op.ToId))
            throw new WriteApiException(
                "duplicate_ref",
                $"Узел id={op.FromId} уже имеет связь '{op.Name}' → id={op.ToId}.");
        existing.Add(op.ToId);

        var newOutRefs = CloneRefs(src.OutRefs);
        newOutRefs[op.Name] = existing;

        var updated = new Node
        {
            Id = src.Id,
            TypeName = src.TypeName,
            Title = src.Title,
            Text = src.Text,
            OutRefs = newOutRefs,
            SourceFile = src.SourceFile,
        };
        s.Replace(updated);
        s.MarkDocumentDirtyForNode(src.Id);

        return new WriteOpResult("create-ref", new JsonObject
        {
            ["from_id"] = op.FromId,
            ["name"] = op.Name,
            ["to_id"] = op.ToId,
        });
    }

    private static WriteOpResult ApplyDeleteRef(WriteState s, DeleteRefOp op)
    {
        if (string.Equals(op.Name, Node.PathRefName, StringComparison.Ordinal))
            throw new WriteApiException(
                "system_ref_name",
                "Связь 'path' управляется только структурными операциями.",
                "Для смены родителя используй move-node.");

        var src = s.GetNode(op.FromId)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел-источник id={op.FromId} не найден.",
                "Сверь from_id через get-map / get-nodes.");

        if (!src.OutRefs.TryGetValue(op.Name, out var existing) || !existing.Contains(op.ToId))
            throw new WriteApiException(
                "ref_not_found",
                $"У узла id={op.FromId} нет связи '{op.Name}' → id={op.ToId}.",
                "Сверь набор связей через get-refs --id=<from-id>.");

        var filtered = existing.Where(t => t != op.ToId).ToList();
        var newOutRefs = CloneRefs(src.OutRefs);
        if (filtered.Count == 0) newOutRefs.Remove(op.Name);
        else newOutRefs[op.Name] = filtered;

        var updated = new Node
        {
            Id = src.Id,
            TypeName = src.TypeName,
            Title = src.Title,
            Text = src.Text,
            OutRefs = newOutRefs,
            SourceFile = src.SourceFile,
        };
        s.Replace(updated);
        s.MarkDocumentDirtyForNode(src.Id);

        return new WriteOpResult("delete-ref", new JsonObject
        {
            ["from_id"] = op.FromId,
            ["name"] = op.Name,
            ["to_id"] = op.ToId,
        });
    }

    private static bool IsDescendantOf(WriteState s, int candidateId, int ancestorId)
    {
        if (candidateId == Node.RootId) return false;
        var current = s.GetNode(candidateId);
        var safety = 100_000;
        while (current is not null && current.ParentId is int pid && safety-- > 0)
        {
            if (pid == ancestorId) return true;
            if (pid == Node.RootId) return false;
            current = s.GetNode(pid);
        }
        return false;
    }

    private static void UpdateSubtreeSourceFile(WriteState s, int rootId, string newSourceFile)
    {
        var queue = new Queue<int>();
        foreach (var ch in s.GetChildren(rootId)) queue.Enqueue(ch.Id);
        var safety = 1_000_000;
        while (queue.Count > 0 && safety-- > 0)
        {
            var id = queue.Dequeue();
            var n = s.GetNode(id);
            if (n is null) continue;
            if (!string.Equals(n.SourceFile, newSourceFile, StringComparison.Ordinal))
            {
                var updated = new Node
                {
                    Id = n.Id,
                    TypeName = n.TypeName,
                    Title = n.Title,
                    Text = n.Text,
                    OutRefs = n.OutRefs,
                    SourceFile = newSourceFile,
                };
                s.Replace(updated);
            }
            foreach (var c in s.GetChildren(id)) queue.Enqueue(c.Id);
        }
    }

    private static Dictionary<string, IReadOnlyList<int>> CloneRefs(
        IReadOnlyDictionary<string, IReadOnlyList<int>> source)
    {
        var copy = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (k, v) in source) copy[k] = v;
        return copy;
    }
}
