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

    /// <summary>
    /// Имя проблемной связи для ref-локализованных ошибок (`missing_required_ref`,
    /// `invalid_cardinality`, `ref_target_not_found`, `unknown_ref`,
    /// `invalid_path_target` и аналогичных). Используется CLI-слоем для trim'а
    /// embedded <c>describe_type</c> до одной записи в <c>out_refs</c>: LLM получает
    /// контракт только нужной связи без шума остальных. Для не-ref ошибок — null.
    /// </summary>
    public string? RefName { get; }

    public WriteApiException(string code, string message, string? hint = null, string? refName = null) : base(message)
    {
        Code = code;
        Hint = hint;
        RefName = refName;
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

/// <summary>
/// Удаление набора узлов одной операцией. Алгоритм:
///   1) path-замкнутость — все path-children каждого узла из набора тоже в наборе
///      (иначе <c>path_orphans_left</c>);
///   2) нет dangling cross-refs — ни одной входящей не-path-связи извне набора
///      (иначе <c>dangling_refs</c>);
///   3) атомарное удаление всех узлов.
/// Cascade ни по какому дереву не зашит — собирать удаляемое надо явно через
/// get_tree (любой scope) и redirect-refs/delete-ref для cross-refs.
/// </summary>
public sealed record DeleteNodesOp(IReadOnlyList<int> Ids) : WriteOp("delete-nodes");

/// <summary>
/// Массовая переподшивка входящих cross-refs одного источника или поддерева
/// path-источников на новый узел-цель либо разрыв связей.
///   - <see cref="FromIds"/> — набор узлов, чьи входящие cross-refs затрагиваются;
///   - <see cref="ToId"/> — куда перенаправлять (null при <see cref="Unlink"/>=true);
///   - <see cref="Name"/> — фильтр по имени связи (null = все имена кроме path);
///   - <see cref="Unlink"/> — true: удалить связи; false: переподшить на ToId.
/// CLI собирает этот набор из форм <c>--from</c>, <c>--from-subtree</c>,
/// <c>--unlink</c>, <c>--name</c>; ядро не различает «откуда взялся набор».
/// </summary>
public sealed record RedirectRefsOp(
    IReadOnlyList<int> FromIds,
    int? ToId,
    string? Name,
    bool Unlink) : WriteOp("redirect-refs");

/// <summary>
/// Перенос узла под нового родителя в указанном дереве (tree-scope).
/// <paramref name="Tree"/> = "path" (default) — реструктуризация хранилища с
/// каскадным rewrite SourceFile у потомков; для прочих scope'ов — атомарная
/// правка одного scope-ref'а узла без FS-операций.
/// </summary>
public sealed record MoveNodeOp(int Id, int NewParentId, string Tree = Node.PathRefName)
    : WriteOp("move-node");

public sealed record CreateRefOp(int FromId, string Name, int ToId) : WriteOp("create-ref");

public sealed record DeleteRefOp(int FromId, string Name, int ToId) : WriteOp("delete-ref");

/// <summary>
/// Результат одной операции. Поле <c>Type</c> совпадает с именем входной операции,
/// поле <c>Data</c> содержит данные, специфичные для команды.
/// </summary>
public sealed record WriteOpResult(string Type, JsonObject Data);

/// <summary>
/// Результат пачки write-операций. <see cref="Applied"/>=false означает dry-run:
/// pipeline отработал до валидатора включительно, но <see cref="Store.AtomicWriter"/>
/// и обновление sequence.txt пропущены — на FS ничего не изменилось. Поле передаётся
/// в success-конверт CLI как <c>applied</c> и позволяет LLM явно отличить «успешно
/// проверено» от «успешно записано».
/// </summary>
public sealed record WriteResult(
    IReadOnlyList<WriteOpResult> OpResults,
    bool Applied);

/// <summary>
/// Результат команды <c>update-schema</c>: серверная валидация новой Схемы прошла
/// успешно (или dry-run); атомарная замена <c>docs/Схема.yml</c> применена при
/// <see cref="Applied"/>=true. <see cref="TypesCount"/> и <see cref="TreesCount"/> —
/// сводка из применённой Схемы для подтверждения, что LLM получила ожидаемый снимок.
/// </summary>
public sealed record SchemaUpdateResult(
    bool Applied,
    int TypesCount,
    int TreesCount);

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

    /// <summary>
    /// Строит <see cref="WriteContext"/> от пути к storage-папке (= папке
    /// <c>docs/</c> графа). После stg-0010 step-03 kernel передаёт эту
    /// папку напрямую в <c>--storage-path=</c>; раньше CLI принимал
    /// project-folder и сам приклеивал <c>/docs</c>.
    /// </summary>
    public static WriteContext FromStoragePath(string storagePath)
    {
        var docsRoot = storagePath;
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
/// Изменение Схемы — через <see cref="UpdateSchema"/> (atomic-замена docs/Схема.yml
/// с серверной валидацией под meta-schema и текущий граф); прочие write-операции
/// Схему не трогают.
/// </summary>
public sealed class WriteApi
{
    /// <summary>
    /// Таймаут ожидания межпроцессного lock'а (см. <see cref="CrossProcessLock"/>).
    /// 30 секунд достаточно, чтобы пропустить чужую большую транзакцию, и не настолько
    /// долго, чтобы LLM решила, что процесс завис.
    /// </summary>
    public static readonly TimeSpan CrossProcessLockTimeout = TimeSpan.FromSeconds(30);

    private readonly WriteContext _ctx;
    private readonly object _processLock = new();

    public WriteApi(WriteContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Применяет пачку операций. <paramref name="dryRun"/>=true прогоняет весь pipeline
    /// (резервирование id → применение операций → сборка графа → валидация) ровно как
    /// при настоящей записи, но пропускает фазу <see cref="AtomicWriter.WriteAndApply"/>
    /// и обновление sequence.txt — файлы docs/ остаются неизменными. Возвращаемый
    /// <see cref="WriteResult"/> в обоих режимах одинаковой формы; режим различает
    /// поле <see cref="WriteResult.Applied"/>.
    /// </summary>
    public WriteResult Apply(IReadOnlyList<WriteOp> ops, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (ops.Count == 0)
            throw new WriteApiException(
                "empty_transaction",
                "Список операций пуст — пачка должна содержать хотя бы одну операцию.");

        // Двухуровневая сериализация:
        //   1) in-process lock — упорядочивает write-вызовы внутри одного процесса
        //      (общий sequence-счётчик, общий граф);
        //   2) cross-process lock на docs/.docswalker/.lock — упорядочивает между
        //      разными процессами DocsWalker над одним docs/. Lock берётся и при
        //      dry-run: read-снимок графа должен быть консистентным относительно
        //      чужих параллельных транзакций.
        lock (_processLock)
        {
            var lockPath = Path.Combine(_ctx.DocsRoot, ".docswalker", ".lock");
            try
            {
                using var fileLock = CrossProcessLock.Acquire(lockPath, CrossProcessLockTimeout);
                return ApplyLocked(ops, dryRun);
            }
            catch (CrossProcessLockTimeoutException ex)
            {
                throw new WriteApiException(
                    "lock_timeout",
                    $"Не удалось взять межпроцессный lock '{ex.LockPath}' за {ex.Timeout.TotalSeconds:0.#} секунд.",
                    "Другой процесс DocsWalker сейчас пишет в этот docs/. Подожди завершения параллельного процесса или прерви его.");
            }
        }
    }

    public WriteResult ApplyOne(WriteOp op, bool dryRun = false) => Apply(new[] { op }, dryRun);

    /// <summary>
    /// Atomic-замена <c>docs/Схема.yml</c>. Серверная валидация в порядке:
    /// (1) парсинг YAML; (2) <see cref="SchemaLoader.LoadSchemaFromString"/>;
    /// (3) текущий граф под новой Схемой через <see cref="Validator"/> — все
    /// существующие узлы должны остаться валидными. При ошибке Схема на диск
    /// не пишется. <paramref name="dryRun"/>=true — пропустить FS-фазу.
    /// </summary>
    public SchemaUpdateResult UpdateSchema(string yamlText, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
            throw new WriteApiException("invalid_yaml", "Параметр yaml_text пуст или содержит только пробелы.");

        lock (_processLock)
        {
            var lockPath = Path.Combine(_ctx.DocsRoot, ".docswalker", ".lock");
            try
            {
                using var fileLock = CrossProcessLock.Acquire(lockPath, CrossProcessLockTimeout);
                return UpdateSchemaLocked(yamlText, dryRun);
            }
            catch (CrossProcessLockTimeoutException ex)
            {
                throw new WriteApiException(
                    "lock_timeout",
                    $"Не удалось взять межпроцессный lock '{ex.LockPath}' за {ex.Timeout.TotalSeconds:0.#} секунд.",
                    "Другой процесс DocsWalker сейчас пишет в этот docs/. Подожди или прерви его.");
            }
        }
    }

    private SchemaUpdateResult UpdateSchemaLocked(string yamlText, bool dryRun)
    {
        SchemaDocument newSchema;
        try
        {
            newSchema = SchemaLoader.LoadSchemaFromString(yamlText, _ctx.SchemaPath);
        }
        catch (SchemaLoadException ex)
        {
            throw new WriteApiException(
                ex.Code,
                $"Новая Схема не соответствует meta-schema: {ex.Message}",
                "Проверь форму YAML — типы, out_refs, trees должны следовать meta-schema v6 (см. get-meta-schema).");
        }

        var oldSchema = SchemaLoader.LoadSchema(_ctx.SchemaPath);
        var loaded = DocumentLoader.Load(_ctx.DocsRoot, oldSchema);

        // Применяем новую Схему к существующему графу и валидируем — это ловит
        // несовместимые правки (убран тип, on котором стоят узлы; убрана требуемая
        // out_ref, оставив узлы без неё; и т. п.).
        loaded.Graph.AttachSchema(newSchema);
        var validator = new Validator(_ctx.MetaSchema, newSchema);
        var validation = validator.Validate(loaded.Graph);
        if (!validation.IsValid)
            throw new WriteValidationException(validation.Errors);

        if (!dryRun)
        {
            var target = new AtomicWriteTarget(_ctx.SchemaPath, yamlText);
            AtomicWriter.WriteAndApply(new[] { target }, Array.Empty<DocsWalker.Core.Store.FsOperation>());
        }

        return new SchemaUpdateResult(
            Applied: !dryRun,
            TypesCount: newSchema.Types.Count,
            TreesCount: newSchema.Trees.Count);
    }

    private WriteResult ApplyLocked(IReadOnlyList<WriteOp> ops, bool dryRun)
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
                throw new WriteApiException(ex.Code, $"Операция #{i} ({ops[i].Type}): {ex.Message}", ex.Hint, ex.RefName);
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

        // Dry-run останавливается ровно перед FS-фазой: валидатор уже подтвердил
        // целостность нового снимка, поэтому можно честно сказать «сработало бы».
        if (!dryRun)
            AtomicWriter.WriteAndApply(targets, state.FsOperations);

        return new WriteResult(opResults, Applied: !dryRun);
    }

    private static WriteOpResult ApplyOp(WriteState s, WriteOp op) => op switch
    {
        CreateNodeOp c => ApplyCreateNode(s, c),
        UpdateNodeOp u => ApplyUpdateNode(s, u),
        DeleteNodesOp d => ApplyDeleteNodes(s, d),
        MoveNodeOp m => ApplyMoveNode(s, m),
        CreateRefOp cr => ApplyCreateRef(s, cr),
        DeleteRefOp dr => ApplyDeleteRef(s, dr),
        RedirectRefsOp r => ApplyRedirectRefs(s, r),
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
                    "Передай --path=<parent_id> или укажи refs.path в JSON.",
                    refName: Node.PathRefName);
            if (pathTargets.Count > 1)
                throw new WriteApiException(
                    "invalid_cardinality",
                    $"Связь 'path' должна иметь ровно одну цель, передано {pathTargets.Count}.",
                    refName: Node.PathRefName);
            int parentId = pathTargets[0];
            if (parentId != Node.RootId && s.GetNode(parentId) is null)
                throw new WriteApiException(
                    "parent_not_found",
                    $"Родитель id={parentId} не найден.",
                    "Сверь parent_id с актуальным графом через get-nodes.");
            // Тип родителя должен входить в path_targets создаваемого узла (root — особое имя).
            var parentTypeName = parentId == Node.RootId
                ? Node.RootTypeName
                : s.GetNode(parentId)!.TypeName;
            if (!type.PathTargets.Any(p => string.Equals(p, parentTypeName, StringComparison.Ordinal)))
                throw new WriteApiException(
                    "invalid_path_target",
                    $"Тип '{type.Name}' не допускает родителя типа '{parentTypeName}'.",
                    $"Допустимые типы родителя: {string.Join(", ", type.PathTargets)}.",
                    refName: Node.PathRefName);
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
                        $"Связь '{rd.Name}' имеет cardinality=one; передано {targets.Count} целей.",
                        refName: rd.Name);
                foreach (var tid in targets)
                {
                    if (tid != Node.RootId && s.GetNode(tid) is null)
                        throw new WriteApiException(
                            "ref_target_not_found",
                            $"Связь '{rd.Name}': цель id={tid} не найдена.",
                            refName: rd.Name);
                }
                outRefs[rd.Name] = targets.ToArray();
            }
            else if (rd.Required)
            {
                throw new WriteApiException(
                    "missing_required_ref",
                    $"Для типа '{op.TypeName}' требуется значение связи '{rd.Name}'.",
                    $"Передай --{rd.Name}=<id[,id,...]>; допустимые типы цели: {string.Join(", ", rd.TargetTypes)}.",
                    refName: rd.Name);
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
                    "Допустимые имена связей смотри в get-schema или describe-type.",
                    refName: name);
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

        // Связь path двунаправленна по контракту схемы: помимо new.out_refs.path = parent
        // нужно обновить parent.out_refs[ref_name], где ref_name — имя path-child связи
        // в типе родителя, цели которой включают тип нового узла. Это обеспечивает
        // согласованность между «есть path-ребёнок» и «у родителя в out_refs он перечислен»,
        // на чём строится emitter (и от чего зависит загрузка YAML обратно).
        if (newNode.ParentId is int pid && pid != Node.RootId)
            AppendToParentPathChildRef(s, pid, type.Name, id);

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
    /// Добавляет id нового path-ребёнка <paramref name="newChildId"/> (тип
    /// <paramref name="newChildType"/>) в <c>out_refs[ref_name]</c> родителя
    /// <paramref name="parentId"/>. ref_name — имя единственной path-child связи
    /// в типе родителя, чьи target_types включают тип ребёнка. Если такой связи нет —
    /// no-op (тип ребёнка не вписывается в контракт типа родителя — это уже отвергнуто
    /// валидацией path_targets выше по стеку).
    /// </summary>
    private static void AppendToParentPathChildRef(WriteState s, int parentId, string newChildType, int newChildId)
    {
        var parent = s.GetNode(parentId);
        if (parent is null) return;
        var parentType = s.ResolveType(parent.TypeName);

        var refName = FindPathChildRefName(s, parentType, newChildType);
        if (refName is null) return;

        var existing = parent.OutRefs.TryGetValue(refName, out var current)
            ? current.ToList()
            : new List<int>();
        if (existing.Contains(newChildId)) return;
        existing.Add(newChildId);

        var newOutRefs = CloneRefs(parent.OutRefs);
        newOutRefs[refName] = existing;
        s.Replace(new Node
        {
            Id = parent.Id,
            TypeName = parent.TypeName,
            Title = parent.Title,
            Text = parent.Text,
            OutRefs = newOutRefs,
            SourceFile = parent.SourceFile,
        });
    }

    /// <summary>
    /// Удаляет id <paramref name="childId"/> из <c>out_refs[ref_name]</c> родителя
    /// <paramref name="parentId"/>. Используется при delete-nodes / move-node для зачистки
    /// path-child записей у бывшего родителя. Обратная пара
    /// <see cref="AppendToParentPathChildRef"/>.
    /// </summary>
    private static void RemoveFromParentPathChildRef(WriteState s, int parentId, string childType, int childId)
    {
        if (parentId == Node.RootId) return;
        var parent = s.GetNode(parentId);
        if (parent is null) return;
        var parentType = s.ResolveType(parent.TypeName);

        var refName = FindPathChildRefName(s, parentType, childType);
        if (refName is null) return;
        if (!parent.OutRefs.TryGetValue(refName, out var current) || !current.Contains(childId)) return;

        var filtered = current.Where(t => t != childId).ToList();
        var newOutRefs = CloneRefs(parent.OutRefs);
        if (filtered.Count == 0) newOutRefs.Remove(refName);
        else newOutRefs[refName] = filtered;

        s.Replace(new Node
        {
            Id = parent.Id,
            TypeName = parent.TypeName,
            Title = parent.Title,
            Text = parent.Text,
            OutRefs = newOutRefs,
            SourceFile = parent.SourceFile,
        });
    }

    /// <summary>
    /// Возвращает true, если связь <paramref name="refName"/> у типа
    /// <paramref name="sourceTypeName"/> объявлена как path-child (target.path_targets ⊇
    /// source.type). Используется в delete-nodes для игнорирования структурного
    /// зеркала path при проверке dangling cross-refs.
    /// </summary>
    private static bool IsPathChildRefName(WriteState s, string sourceTypeName, string refName)
    {
        var srcType = s.Schema.Types.FirstOrDefault(t => string.Equals(t.Name, sourceTypeName, StringComparison.Ordinal));
        if (srcType is null) return false;
        var rd = srcType.OutRefs.FirstOrDefault(r => string.Equals(r.Name, refName, StringComparison.Ordinal));
        if (rd is null) return false;
        foreach (var ttn in rd.TargetTypes)
        {
            var tt = s.Schema.Types.FirstOrDefault(t => string.Equals(t.Name, ttn, StringComparison.Ordinal));
            if (tt is null) continue;
            if (tt.PathTargets.Any(pt => string.Equals(pt, sourceTypeName, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Находит имя path-child связи в типе родителя, цели которой включают тип
    /// <paramref name="childType"/>. Path-child связь = такая, через которую цели становятся
    /// path-детьми источника (target.path_targets ⊇ parent.type). Каждый child-type у
    /// родителя проходит ровно через одну такую связь — иначе схема была бы неоднозначной.
    /// </summary>
    private static string? FindPathChildRefName(WriteState s, TypeDefinition parentType, string childType)
    {
        foreach (var rd in parentType.OutRefs)
        {
            if (string.Equals(rd.Name, Node.PathRefName, StringComparison.Ordinal)) continue;
            if (!rd.TargetTypes.Any(tt => string.Equals(tt, childType, StringComparison.Ordinal))) continue;
            // Дополнительно убедимся, что это path-child ref — иначе это cross-ref, а они
            // здесь не имеют отношения к path-детству.
            var ct = s.Schema.Types.FirstOrDefault(t => string.Equals(t.Name, childType, StringComparison.Ordinal));
            if (ct is null) continue;
            if (ct.PathTargets.Any(pt => string.Equals(pt, parentType.Name, StringComparison.Ordinal)))
                return rd.Name;
        }
        return null;
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
        if (op.Id == Node.RootId)
            throw new WriteApiException(
                "cannot_modify_root",
                "Корневой синглтон id=0 ('root') не может быть изменён (update-node).",
                "root — синглтон ядра DocsWalker, синтезируется на лету; запись в него запрещена.");

        var node = s.GetNode(op.Id)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел id={op.Id} не найден.",
                "Сверь id через get-nodes.");

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

        // R11: rename folder — переименование dirname + правка folders.yml +
        // cascade SourceFile у потомков. Идёт через отдельную ветку, потому
        // что folder не привязан к документу (свой SourceFile = folders.yml,
        // у потомков SourceFile содержит rel-цепочку каталога).
        if (string.Equals(node.TypeName, "folder", StringComparison.Ordinal))
        {
            return ApplyRenameFolder(s, op, node, newTitle);
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

    /// <summary>
    /// Переименование folder-узла: смена title → переименование каталога на FS,
    /// правка записи в folders.yml, cascade SourceFile у всех path-потомков
    /// (документы и узлы внутри них). Текст folder-а не меняется (у folder
    /// text всегда пуст).
    /// </summary>
    private static WriteOpResult ApplyRenameFolder(
        WriteState s, UpdateNodeOp op, Node node, string newTitle)
    {
        if (op.NewText is not null && !string.Equals(op.NewText, node.Text, StringComparison.Ordinal))
            throw new WriteApiException(
                "invalid_field",
                $"У folder id={op.Id} нельзя задать text — у folder контракт text_required=false и значение всегда пусто.",
                "Передавай только --title для переименования каталога.");

        if (op.NewTitle is null || string.Equals(op.NewTitle, node.Title, StringComparison.Ordinal))
            throw new WriteApiException(
                "no_effect",
                $"Update-node для folder id={op.Id} без смены title не вносит изменений.",
                "Передай --title=<new_name> для переименования каталога.");

        var parentId = node.ParentId
            ?? throw new WriteApiException(
                "internal_inconsistency",
                $"У folder id={op.Id} отсутствует связь path.");

        // Коллизия: уже есть folder с тем же title под этим parent (исключая сам узел).
        foreach (var sibling in s.GetChildren(parentId))
        {
            if (sibling.Id == node.Id) continue;
            if (string.Equals(sibling.TypeName, "folder", StringComparison.Ordinal)
                && string.Equals(sibling.Title, newTitle, StringComparison.Ordinal))
            {
                throw new WriteApiException(
                    "duplicate_folder_name",
                    $"Под parent id={parentId} уже существует folder с title '{newTitle}' (id={sibling.Id}).",
                    "Выбери другое имя.");
            }
        }

        var oldRel = BuildFolderRelativePath(s, node.Id);
        var parentRel = parentId == Node.RootId ? string.Empty : BuildFolderRelativePath(s, parentId);
        var newRel = parentRel.Length == 0 ? newTitle : parentRel + "/" + newTitle;
        var oldAbs = Path.Combine(s.DocsRoot, oldRel.Replace('/', Path.DirectorySeparatorChar));
        var newAbs = Path.Combine(s.DocsRoot, newRel.Replace('/', Path.DirectorySeparatorChar));

        if (!string.Equals(oldAbs, newAbs, StringComparison.Ordinal) && Directory.Exists(newAbs))
            throw new WriteApiException(
                "fs_collision",
                $"Целевой каталог '{newAbs}' уже существует на FS.",
                "Убери коллизию вручную или выбери другое имя.");

        var updated = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = newTitle,
            Text = node.Text,
            OutRefs = node.OutRefs,
            SourceFile = node.SourceFile,
        };
        s.Replace(updated);

        CascadeFolderSourceFile(s, node.Id, oldRel, newRel);

        s.AddFsOperation(new FsMoveDirectory(oldAbs, newAbs));
        s.MarkFoldersDirty();

        return new WriteOpResult("update-node", new JsonObject
        {
            ["id"] = node.Id,
            ["title"] = newTitle,
        });
    }

    private static WriteOpResult ApplyDeleteNodes(WriteState s, DeleteNodesOp op)
    {
        if (op.Ids.Count == 0)
            throw new WriteApiException(
                "empty_ids",
                "Список ids для delete-nodes пуст.",
                "Передай хотя бы один id; собрать набор удобно через get-tree.");

        // Дедуплицируем, сохраняя порядок появления (детерминированный для логов и JSON-результата).
        var ordered = new List<int>(op.Ids.Count);
        var set = new HashSet<int>();
        foreach (var id in op.Ids)
        {
            if (set.Add(id)) ordered.Add(id);
        }

        // 1) Все id существуют, root в наборе быть не может.
        var nodes = new List<Node>(ordered.Count);
        foreach (var id in ordered)
        {
            if (id == Node.RootId)
                throw new WriteApiException(
                    "cannot_modify_root",
                    "Корневой синглтон id=0 ('root') не может быть изменён (delete-nodes).",
                    "root — синглтон ядра DocsWalker, синтезируется на лету; запись в него запрещена.");
            var n = s.GetNode(id)
                ?? throw new WriteApiException(
                    "node_not_found",
                    $"Узел id={id} не найден.",
                    "Сверь id через get-nodes.");
            nodes.Add(n);
        }

        // 2) Удаление document-узлов в этой версии не поддерживается (отдельная команда).
        foreach (var n in nodes)
        {
            if (string.Equals(n.TypeName, "document", StringComparison.Ordinal))
                throw new WriteApiException(
                    "delete_document_unsupported",
                    $"Удаление документа id={n.Id} ('{n.Title}') в этой версии write-API не поддерживается.",
                    "Для удаления документа появится отдельная команда delete-document.");
        }

        // 3) Path-замкнутость: для каждого узла из набора все path-children — тоже в наборе.
        var orphans = new List<(int Parent, int Child)>();
        foreach (var n in nodes)
        {
            foreach (var child in s.GetChildren(n.Id))
            {
                if (!set.Contains(child.Id))
                    orphans.Add((n.Id, child.Id));
            }
        }
        if (orphans.Count > 0)
        {
            var detail = string.Join("; ",
                orphans.Take(20).Select(o => $"id={o.Parent}→child={o.Child}"));
            var more = orphans.Count > 20 ? $" (всего {orphans.Count})" : string.Empty;
            throw new WriteApiException(
                "path_orphans_left",
                $"После удаления остались бы path-сироты: {detail}{more}.",
                "Добавь отсутствующие id в --ids или ограничь набор до замкнутого по path.");
        }

        // 4) Dangling cross-refs: ни одной входящей не-path-связи извне набора.
        //    path-child refs (например, section.rules → rule) — структурное зеркало path,
        //    их игнорируем; реальные cross-refs (например, rule.examples → example) ловим.
        var dangling = new List<(int Source, string Name, int Target)>();
        foreach (var n in nodes)
        {
            foreach (var (sourceId, name) in s.ListIncomingRefs(n.Id, includePath: false))
            {
                if (set.Contains(sourceId)) continue;
                // Если это path-child ref у источника — пропускаем (структурное зеркало).
                var src = s.GetNode(sourceId);
                if (src is not null && IsPathChildRefName(s, src.TypeName, name)) continue;
                dangling.Add((sourceId, name, n.Id));
            }
        }
        if (dangling.Count > 0)
        {
            var detail = string.Join("; ",
                dangling.Take(20).Select(d => $"id={d.Source} ref '{d.Name}' → id={d.Target}"));
            var more = dangling.Count > 20 ? $" (всего {dangling.Count})" : string.Empty;
            throw new WriteApiException(
                "dangling_refs",
                $"На узлы в наборе ссылаются извне: {detail}{more}.",
                "Перенаправь связи через redirect-refs --to=<dst> или сними через delete-ref, затем повтори delete-nodes.");
        }

        // 5) Folder-узлы: deepest first — иначе FsDeleteEmptyDirectory упадёт на непустом
        //    верхнем каталоге. Глубину считаем по длине rel-пути на актуальном состоянии,
        //    до Remove'ов.
        var folderEntries = nodes
            .Where(n => string.Equals(n.TypeName, "folder", StringComparison.Ordinal))
            .Select(n => (Node: n, Rel: BuildFolderRelativePath(s, n.Id)))
            .OrderByDescending(t => t.Rel.Length)
            .ToList();

        // 6) Пометка dirty: для каждого не-folder — поднимаемся до document. Делаем до
        //    Remove'ов, иначе MarkDocumentDirtyForNode не найдёт узел.
        foreach (var n in nodes)
        {
            if (string.Equals(n.TypeName, "folder", StringComparison.Ordinal))
                continue;
            s.MarkDocumentDirtyForNode(n.Id);
        }

        // 7) Регистрация FS-операций для folder в порядке «сначала глубже».
        foreach (var (folder, rel) in folderEntries)
        {
            var absolutePath = Path.Combine(s.DocsRoot,
                rel.Replace('/', Path.DirectorySeparatorChar));
            s.AddFsOperation(new FsDeleteEmptyDirectory(absolutePath));
            s.MarkFoldersDirty();
        }

        // 8) Снятие записи из parent.out_refs[ref_name] для каждого удаляемого узла,
        //    чей parent не в самом наборе (parent в наборе — он тоже удаляется, чистить
        //    бесполезно). Делается до Remove'ов, иначе s.GetNode(parent) вернёт null.
        foreach (var n in nodes)
        {
            if (n.ParentId is int pid && pid != Node.RootId && !set.Contains(pid))
                RemoveFromParentPathChildRef(s, pid, n.TypeName, n.Id);
        }

        // 9) Атомарное удаление из state.
        foreach (var n in nodes)
            s.Remove(n.Id);

        return new WriteOpResult("delete-nodes", new JsonObject
        {
            ["ids"] = new JsonArray(ordered.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        });
    }

    /// <summary>
    /// Массовая переподшивка/разрыв входящих cross-refs для набора FromIds.
    /// Алгоритм:
    ///   1) Резолвим набор FromIds (set источников-целей переподшивки).
    ///   2) Перебираем все узлы графа; в out_refs каждого ищем ссылки на узлы из set
    ///      (по фильтру Name, если указан); пропускаем системную связь path.
    ///   3) Для каждой такой связи: при <see cref="RedirectRefsOp.Unlink"/>=true —
    ///      удаляем элементы из targets; иначе заменяем их на ToId с дедупликацией.
    ///   4) Самоссылки запрещены: если после переподшивки источник стал бы ссылаться сам
    ///      на себя, отвергаем (<c>self_ref</c>).
    ///   5) Cardinality=one: если после переподшивки в targets > 1 — отвергаем
    ///      (<c>invalid_cardinality</c>).
    /// Не правит сами узлы из FromIds (они становятся «изолированными» по cross-refs —
    /// типичный pre-step перед delete-nodes).
    /// </summary>
    private static WriteOpResult ApplyRedirectRefs(WriteState s, RedirectRefsOp op)
    {
        if (op.FromIds.Count == 0)
            throw new WriteApiException(
                "empty_from",
                "Набор FromIds для redirect-refs пуст.",
                "Передай --from=<id>, либо --from-subtree=<root_id>.");

        var fromSet = new HashSet<int>(op.FromIds);

        // Системная связь path — не переподшиваема (управляется move-node).
        if (op.Name is not null && string.Equals(op.Name, Node.PathRefName, StringComparison.Ordinal))
            throw new WriteApiException(
                "system_ref_name",
                "Связь 'path' нельзя переподшивать через redirect-refs.",
                "Используй move-node для смены родителя.");

        // Валидация ToId / Unlink: ровно одно из них.
        if (!op.Unlink)
        {
            if (op.ToId is not int toId)
                throw new WriteApiException(
                    "missing_target",
                    "Для redirect-refs без --unlink требуется --to=<dst_id>.");
            if (toId != Node.RootId && s.GetNode(toId) is null)
                throw new WriteApiException(
                    "node_not_found",
                    $"Узел-цель id={toId} не найден.",
                    "Сверь to_id через get-nodes.");
            if (fromSet.Contains(toId))
                throw new WriteApiException(
                    "self_redirect",
                    $"Узел-цель id={toId} входит в набор FromIds — переподшивка на сам себя запрещена.");
        }
        else
        {
            if (op.ToId is not null)
                throw new WriteApiException(
                    "conflicting_options",
                    "Опции --unlink и --to= взаимоисключающие.");
        }

        // Сначала проверяем наличие самих FromIds.
        foreach (var id in fromSet)
        {
            if (id != Node.RootId && s.GetNode(id) is null)
                throw new WriteApiException(
                    "node_not_found",
                    $"Узел из набора FromIds id={id} не найден.",
                    "Сверь from_id / from-subtree через get-nodes.");
        }

        // Собираем правки: для каждого узла-источника — новая копия OutRefs.
        var changes = new List<(int SourceId, string RefName, int RemovedCount)>();
        var nodesToReplace = new Dictionary<int, Node>();

        // Диагностика: считаем, сколько path-child связей было пропущено, чтобы при
        // no_effect показать LLM правильный путь решения (move-node вместо
        // redirect-refs). Без этого LLM путается: get-in-refs показывает связь
        // (она физическая в out_refs родителя), а redirect-refs тихо игнорирует
        // её под капотом и возвращает no_effect.
        int pathChildSkippedHits = 0;

        foreach (var srcNode in s.AllNodes.ToList())
        {
            // Не правим сами узлы из набора (они и так уйдут на удаление, либо будут изолированы).
            if (fromSet.Contains(srcNode.Id)) continue;

            Dictionary<string, IReadOnlyList<int>>? newOutRefs = null;

            foreach (var (refName, targets) in srcNode.OutRefs)
            {
                if (string.Equals(refName, Node.PathRefName, StringComparison.Ordinal))
                    continue;
                if (op.Name is not null && !string.Equals(refName, op.Name, StringComparison.Ordinal))
                    continue;
                // path-child refs — структурное зеркало path, ими управляет ядро через
                // create-node / delete-nodes / move-node. Свободно переподшивать их нельзя
                // (это ломает синхрон с path-child'ами), redirect-refs их пропускает —
                // но считаем «пропущенные хиты» для диагностики (см. no_effect ниже).
                if (IsPathChildRefName(s, srcNode.TypeName, refName))
                {
                    foreach (var t in targets)
                        if (fromSet.Contains(t)) pathChildSkippedHits++;
                    continue;
                }

                // Сколько целей этой связи попадает в FromIds?
                int hits = 0;
                foreach (var t in targets) if (fromSet.Contains(t)) hits++;
                if (hits == 0) continue;

                newOutRefs ??= CloneRefs(srcNode.OutRefs);

                if (op.Unlink)
                {
                    var filtered = targets.Where(t => !fromSet.Contains(t)).ToList();
                    if (filtered.Count == 0) newOutRefs.Remove(refName);
                    else newOutRefs[refName] = filtered;
                }
                else
                {
                    int toId = op.ToId!.Value;
                    var rebuilt = new List<int>(targets.Count);
                    var dedupSet = new HashSet<int>();
                    foreach (var t in targets)
                    {
                        var mapped = fromSet.Contains(t) ? toId : t;
                        if (dedupSet.Add(mapped)) rebuilt.Add(mapped);
                    }
                    // Cardinality=one: после переподшивки не должно быть > 1.
                    var srcType = s.ResolveType(srcNode.TypeName);
                    var rd = srcType.OutRefs.FirstOrDefault(r =>
                        string.Equals(r.Name, refName, StringComparison.Ordinal));
                    if (rd is not null && rd.Cardinality == Cardinality.One && rebuilt.Count > 1)
                        throw new WriteApiException(
                            "invalid_cardinality",
                            $"Узел id={srcNode.Id} связь '{refName}' имеет cardinality=one; после переподшивки получилось {rebuilt.Count} целей.",
                            "Сначала сними часть связей через delete-ref, затем повтори redirect-refs.");
                    newOutRefs[refName] = rebuilt;
                }

                changes.Add((srcNode.Id, refName, hits));
            }

            if (newOutRefs is not null)
            {
                nodesToReplace[srcNode.Id] = new Node
                {
                    Id = srcNode.Id,
                    TypeName = srcNode.TypeName,
                    Title = srcNode.Title,
                    Text = srcNode.Text,
                    OutRefs = newOutRefs,
                    SourceFile = srcNode.SourceFile,
                };
            }
        }

        if (nodesToReplace.Count == 0)
        {
            // Если все совпавшие связи — path-child refs, значит цели — это path-children
            // источников, и redirect-refs их трогать не должен (управление структурой
            // дерева — задача move-node). Подсказка должна вести именно к нему,
            // иначе LLM застревает: get-in-refs показывает связи, а redirect-refs
            // молча отказывается их менять.
            if (pathChildSkippedHits > 0)
                throw new WriteApiException(
                    "no_effect",
                    $"Все совпавшие входящие связи ({pathChildSkippedHits} шт.) — path-child refs (структурное зеркало path-дерева). redirect-refs их не трогает: смена parent в path-дереве — операция move-node.",
                    "Используй move-node --id=<child_id> --to=<new_parent_id> для переподшивки в path-дереве. redirect-refs предназначен только для cross-refs (вне tree-scopes).");
            throw new WriteApiException(
                "no_effect",
                "Ни одной cross-ref на узлы из FromIds не найдено — переподшивать нечего.",
                "Сверь набор источников через get-in-refs --id=<from_id>.");
        }

        foreach (var (id, updated) in nodesToReplace)
        {
            s.Replace(updated);
            s.MarkDocumentDirtyForNode(id);
        }

        var changesArr = new JsonArray();
        foreach (var c in changes)
        {
            changesArr.Add((JsonNode?)new JsonObject
            {
                ["source_id"] = c.SourceId,
                ["name"] = c.RefName,
                ["removed_count"] = c.RemovedCount,
            });
        }

        return new WriteOpResult("redirect-refs", new JsonObject
        {
            ["from_ids"] = new JsonArray(op.FromIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            ["to_id"] = op.ToId,
            ["unlink"] = op.Unlink,
            ["name"] = op.Name,
            ["changes"] = changesArr,
        });
    }

    private static WriteOpResult ApplyMoveNode(WriteState s, MoveNodeOp op)
    {
        if (op.Id == Node.RootId)
            throw new WriteApiException(
                "cannot_modify_root",
                "Корневой синглтон id=0 ('root') не может быть изменён (move-node).",
                "root — синглтон ядра DocsWalker, синтезируется на лету; запись в него запрещена.");

        var node = s.GetNode(op.Id)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел id={op.Id} не найден.",
                "Сверь id через get-nodes.");

        // Не-path scope: атомарная правка одного scope-ref'а; никаких FS-операций.
        if (!string.Equals(op.Tree, Node.PathRefName, StringComparison.Ordinal))
            return ApplyMoveNonPath(s, op, node);

        if (string.Equals(node.TypeName, "document", StringComparison.Ordinal))
            throw new WriteApiException(
                "cannot_move_document",
                $"Узел id={op.Id} ('{node.Title}') — документ; перенос документов не поддерживается.",
                "Документ нельзя перенести как узел; используй create-document/delete-document для смены файла.");

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
                "Сверь new_parent_id через get-nodes.");

        if (node.ParentId is not int oldParentId)
            throw new WriteApiException(
                "cannot_move_root",
                $"Узел id={op.Id} не имеет связи 'path' (root); перенос невозможен.");

        if (oldParentId == op.NewParentId)
            throw new WriteApiException(
                "no_effect",
                $"Узел id={op.Id} уже имеет path={op.NewParentId}.");

        // R11: перенос folder идёт по отдельной ветке — у folder нет SourceFile-документа,
        // и в дополнение к Replace требуется FS-перенос каталога и cascade SourceFile.
        if (string.Equals(node.TypeName, "folder", StringComparison.Ordinal))
        {
            return ApplyMoveFolder(s, op, node, oldParentId);
        }

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

        // Симметричная переподшивка path-child записей в out_refs обоих родителей.
        if (oldParentId != Node.RootId)
            RemoveFromParentPathChildRef(s, oldParentId, node.TypeName, node.Id);
        if (op.NewParentId != Node.RootId)
            AppendToParentPathChildRef(s, op.NewParentId, node.TypeName, node.Id);

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
        if (op.FromId == Node.RootId)
            throw new WriteApiException(
                "cannot_modify_root",
                "Корневой синглтон id=0 ('root') не может быть изменён (create-ref).",
                "root — синглтон ядра DocsWalker, синтезируется на лету; запись в него запрещена.");

        if (string.Equals(op.Name, Node.PathRefName, StringComparison.Ordinal))
            throw new WriteApiException(
                "system_ref_name",
                "Связь 'path' управляется только структурными операциями create-node / move-node / delete-node.",
                "Для смены родителя используй move-node.",
                refName: op.Name);

        var src = s.GetNode(op.FromId)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел-источник id={op.FromId} не найден.",
                "Сверь from_id через get-nodes.");

        if (op.ToId != Node.RootId && s.GetNode(op.ToId) is null)
            throw new WriteApiException(
                "node_not_found",
                $"Узел-цель id={op.ToId} не найден.",
                "Сверь to_id через get-nodes.");

        var srcType = s.ResolveType(src.TypeName);
        if (!srcType.OutRefs.Any(rd => string.Equals(rd.Name, op.Name, StringComparison.Ordinal)))
            throw new WriteApiException(
                "unknown_ref",
                $"Тип '{src.TypeName}' не объявляет связь '{op.Name}'.",
                "Допустимые имена связей смотри в get-schema или describe-type. Новые имена объявляются ручной правкой Схемы.",
                refName: op.Name);

        var existing = src.OutRefs.TryGetValue(op.Name, out var current)
            ? current.ToList()
            : new List<int>();
        if (existing.Contains(op.ToId))
            throw new WriteApiException(
                "duplicate_ref",
                $"Узел id={op.FromId} уже имеет связь '{op.Name}' → id={op.ToId}.",
                refName: op.Name);
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
        if (op.FromId == Node.RootId)
            throw new WriteApiException(
                "cannot_modify_root",
                "Корневой синглтон id=0 ('root') не может быть изменён (delete-ref).",
                "root — синглтон ядра DocsWalker, синтезируется на лету; запись в него запрещена.");

        if (string.Equals(op.Name, Node.PathRefName, StringComparison.Ordinal))
            throw new WriteApiException(
                "system_ref_name",
                "Связь 'path' управляется только структурными операциями.",
                "Для смены родителя используй move-node.",
                refName: op.Name);

        var src = s.GetNode(op.FromId)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел-источник id={op.FromId} не найден.",
                "Сверь from_id через get-nodes.");

        if (!src.OutRefs.TryGetValue(op.Name, out var existing) || !existing.Contains(op.ToId))
            throw new WriteApiException(
                "ref_not_found",
                $"У узла id={op.FromId} нет связи '{op.Name}' → id={op.ToId}.",
                "Сверь набор связей через get-refs --id=<from-id>.",
                refName: op.Name);

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

    /// <summary>
    /// Перенос узла в дереве (tree-scope), отличном от <c>path</c>: меняет один scope-ref
    /// узла без FS-операций и без каскадных правок SourceFile у потомков.
    /// Проверяет существование scope-ref'а в типе узла, target_types нового родителя,
    /// отсутствие цикла в scope (новый родитель не в субдереве переносимого узла по этому
    /// scope) и отличие нового родителя от текущего.
    /// </summary>
    private static WriteOpResult ApplyMoveNonPath(WriteState s, MoveNodeOp op, Node node)
    {
        var type = s.ResolveType(node.TypeName);
        // Найти scope-ref у типа.
        RefDef? scopeRef = null;
        foreach (var rd in type.OutRefs)
        {
            if (rd.Tree is null) continue;
            if (string.Equals(rd.Tree, op.Tree, StringComparison.Ordinal))
            {
                scopeRef = rd;
                break;
            }
        }
        if (scopeRef is null)
            throw new WriteApiException(
                "unknown_tree",
                $"Тип '{type.Name}' не объявляет связь в дереве '{op.Tree}'.",
                "Сверь набор tree-связей типа в get-schema; если дерево объявлено, у узла этого типа в нём нет родителя — узел не переносится.");

        if (op.NewParentId == op.Id)
            throw new WriteApiException(
                "invalid_move",
                $"Узел id={op.Id} нельзя сделать собственным родителем в дереве '{op.Tree}'.");

        if (op.NewParentId != Node.RootId && s.GetNode(op.NewParentId) is null)
            throw new WriteApiException(
                "parent_not_found",
                $"Новый родитель id={op.NewParentId} не найден.",
                "Сверь new_parent_id через get-nodes.");

        // Тип нового родителя должен входить в target_types scope-ref'а.
        var newParentTypeName = op.NewParentId == Node.RootId
            ? Node.RootTypeName
            : s.GetNode(op.NewParentId)!.TypeName;
        if (!scopeRef.TargetTypes.Any(t => string.Equals(t, newParentTypeName, StringComparison.Ordinal)))
            throw new WriteApiException(
                "invalid_target_type",
                $"Связь '{scopeRef.Name}' дерева '{op.Tree}' допускает родителей типов {{{string.Join(", ", scopeRef.TargetTypes)}}}; новый родитель id={op.NewParentId} имеет тип '{newParentTypeName}'.");

        // Текущее значение scope-ref'а (если задано) — для no_effect и снятия старого индекса.
        int? oldParentId = null;
        if (node.OutRefs.TryGetValue(scopeRef.Name, out var existing) && existing.Count > 0)
            oldParentId = existing[0];
        if (oldParentId == op.NewParentId)
            throw new WriteApiException(
                "no_effect",
                $"Узел id={op.Id} уже имеет связь '{scopeRef.Name}' = {op.NewParentId} в дереве '{op.Tree}'.");

        // Cycle pre-check: пройти вверх от нового родителя по scope-ref'ам этого дерева;
        // если встретим op.Id — цикл. Лимит — число узлов состояния (защита от lockstep).
        if (FormsCycleInScope(s, op.Tree, op.NewParentId, op.Id))
            throw new WriteApiException(
                "tree_cycle",
                $"Перенос id={op.Id} под id={op.NewParentId} в дереве '{op.Tree}' образует цикл.",
                "Выбери родителя вне субдерева переносимого узла в этом scope.");

        var newOutRefs = CloneRefs(node.OutRefs);
        newOutRefs[scopeRef.Name] = new[] { op.NewParentId };
        var moved = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = node.Title,
            Text = node.Text,
            OutRefs = newOutRefs,
            SourceFile = node.SourceFile,
        };
        s.Replace(moved);
        // scope-ref сериализуется в YAML того же документа, что и сам узел.
        s.MarkDocumentDirtyForNode(node.Id);

        return new WriteOpResult("move-node", new JsonObject
        {
            ["id"] = node.Id,
            ["new_parent_id"] = op.NewParentId,
            ["tree"] = op.Tree,
        });
    }

    /// <summary>
    /// Проверяет, образует ли назначение «<paramref name="movingId"/> → <paramref name="newParentId"/>»
    /// цикл в scope <paramref name="scope"/>: поднимаемся вверх по scope-ref'ам от
    /// <paramref name="newParentId"/>, ища <paramref name="movingId"/>. Возвращает true при
    /// обнаружении.
    /// </summary>
    private static bool FormsCycleInScope(WriteState s, string scope, int newParentId, int movingId)
    {
        var visited = new HashSet<int>();
        int? cursor = newParentId;
        var safety = 1_000_000;
        while (cursor is int cid && safety-- > 0)
        {
            if (cid == movingId) return true;
            if (cid == Node.RootId) return false;
            if (!visited.Add(cid)) return false; // существующий цикл — не наша забота, RefsCheck отловит
            var n = s.GetNode(cid);
            if (n is null) return false;
            cursor = ResolveScopeParent(s, n, scope);
        }
        return false;
    }

    private static int? ResolveScopeParent(WriteState s, Node node, string scope)
    {
        var t = s.ResolveType(node.TypeName);
        foreach (var rd in t.OutRefs)
        {
            if (rd.Tree is null) continue;
            if (!string.Equals(rd.Tree, scope, StringComparison.Ordinal)) continue;
            if (!node.OutRefs.TryGetValue(rd.Name, out var targets) || targets.Count == 0) return null;
            return targets[0];
        }
        return null;
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

    /// <summary>
    /// Перенос folder-узла под нового родителя: проверяет path_targets и
    /// коллизию имени, переписывает связь path, каскадно правит SourceFile у
    /// потомков и регистрирует <see cref="FsMoveDirectory"/>. Базовые проверки
    /// (cycle, parent_not_found, no_effect, cannot_move_root) выполнены
    /// вызывающим <see cref="ApplyMoveNode"/>.
    /// </summary>
    private static WriteOpResult ApplyMoveFolder(
        WriteState s, MoveNodeOp op, Node folder, int oldParentId)
    {
        var folderType = s.ResolveType("folder");
        var newParentTypeName = op.NewParentId == Node.RootId
            ? Node.RootTypeName
            : s.GetNode(op.NewParentId)!.TypeName;
        if (!folderType.PathTargets.Any(p => string.Equals(p, newParentTypeName, StringComparison.Ordinal)))
            throw new WriteApiException(
                "invalid_path_target",
                $"Folder можно перенести только под root или другой folder; новый родитель id={op.NewParentId} имеет тип '{newParentTypeName}'.",
                $"Допустимые типы родителя: {string.Join(", ", folderType.PathTargets)}.");

        // Коллизия: под новым parent уже есть folder с таким же title.
        foreach (var sibling in s.GetChildren(op.NewParentId))
        {
            if (sibling.Id == folder.Id) continue;
            if (string.Equals(sibling.TypeName, "folder", StringComparison.Ordinal)
                && string.Equals(sibling.Title, folder.Title, StringComparison.Ordinal))
            {
                throw new WriteApiException(
                    "duplicate_folder_name",
                    $"Под parent id={op.NewParentId} уже существует folder с title '{folder.Title}' (id={sibling.Id}).",
                    "Сначала переименуй один из folder, затем повтори перенос.");
            }
        }

        var oldRel = BuildFolderRelativePath(s, folder.Id);
        var newParentRel = op.NewParentId == Node.RootId
            ? string.Empty
            : BuildFolderRelativePath(s, op.NewParentId);
        var newRel = newParentRel.Length == 0 ? folder.Title : newParentRel + "/" + folder.Title;
        var oldAbs = Path.Combine(s.DocsRoot, oldRel.Replace('/', Path.DirectorySeparatorChar));
        var newAbs = Path.Combine(s.DocsRoot, newRel.Replace('/', Path.DirectorySeparatorChar));

        if (!string.Equals(oldAbs, newAbs, StringComparison.Ordinal) && Directory.Exists(newAbs))
            throw new WriteApiException(
                "fs_collision",
                $"Целевой каталог '{newAbs}' уже существует на FS.",
                "Убери коллизию вручную или перенеси folder в другое место.");

        var newOutRefs = CloneRefs(folder.OutRefs);
        newOutRefs[Node.PathRefName] = new[] { op.NewParentId };
        var moved = new Node
        {
            Id = folder.Id,
            TypeName = folder.TypeName,
            Title = folder.Title,
            Text = folder.Text,
            OutRefs = newOutRefs,
            SourceFile = folder.SourceFile,
        };
        s.Replace(moved);

        // Симметричная переподшивка path-child записей у обоих folder-родителей.
        if (oldParentId != Node.RootId)
            RemoveFromParentPathChildRef(s, oldParentId, folder.TypeName, folder.Id);
        if (op.NewParentId != Node.RootId)
            AppendToParentPathChildRef(s, op.NewParentId, folder.TypeName, folder.Id);

        CascadeFolderSourceFile(s, folder.Id, oldRel, newRel);

        s.AddFsOperation(new FsMoveDirectory(oldAbs, newAbs));
        s.MarkFoldersDirty();

        return new WriteOpResult("move-node", new JsonObject
        {
            ["id"] = folder.Id,
            ["new_parent_id"] = op.NewParentId,
        });
    }

    /// <summary>
    /// Префиксная замена SourceFile у всех path-потомков folder-узла после
    /// его rename/move. Узлы folder-типа (SourceFile = ".docswalker/folders.yml")
    /// под условие префикса не попадают и пропускаются автоматически.
    /// </summary>
    private static void CascadeFolderSourceFile(WriteState s, int folderId, string oldRel, string newRel)
    {
        if (string.Equals(oldRel, newRel, StringComparison.Ordinal)) return;

        var oldPrefix = oldRel + "/";
        var newPrefix = newRel + "/";

        var queue = new Queue<int>();
        foreach (var ch in s.GetChildren(folderId)) queue.Enqueue(ch.Id);
        var safety = 1_000_000;
        while (queue.Count > 0 && safety-- > 0)
        {
            var id = queue.Dequeue();
            var n = s.GetNode(id);
            if (n is null) continue;

            if (n.SourceFile.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                var newSourceFile = newPrefix + n.SourceFile.Substring(oldPrefix.Length);
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
