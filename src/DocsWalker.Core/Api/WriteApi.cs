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
/// неподходящий тип узла, неверная форма body, попытка применить операцию вне допустимой
/// области (например, create_node под примитивный родитель). Содержательные нарушения,
/// обнаруженные валидатором, передаются через <see cref="WriteValidationException"/>.
/// </summary>
public sealed class WriteApiException : Exception
{
    public string Code { get; }

    public WriteApiException(string code, string message) : base(message)
    {
        Code = code;
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
/// Описание одной операции в составе транзакции. Перечисление имён
/// (<see cref="Type"/>) — стабильный контракт CLI/MCP.
/// </summary>
public abstract record WriteOp(string Type);

public sealed record CreateNodeOp(
    int ParentId,
    string TypeName,
    string? Title,
    string? Name,
    JsonObject? Body) : WriteOp("create-node");

public sealed record UpdateNodeOp(int Id, JsonObject Patch) : WriteOp("update-node");

public sealed record DeleteNodeOp(int Id) : WriteOp("delete-node");

public sealed record CreateRefOp(int FromId, string RefType, int ToId) : WriteOp("create-ref");

public sealed record DeleteRefOp(int FromId, string RefType, int ToId) : WriteOp("delete-ref");

public sealed record AddRefTypeOp(string Name, string Direction, string Description)
    : WriteOp("add-ref-type");

/// <summary>
/// Результат успешно применённой пачки операций. Идёт обратно к вызывающему через
/// CLI/transaction-handler. Для каждой операции — что произошло (id созданного узла,
/// id обновлённого/удалённого, и т. п.). Длина <see cref="OpResults"/> равна числу
/// операций во входной пачке.
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
    public MetaSchemaDocument MetaSchema { get; }

    public WriteContext(
        string docsRoot, string schemaPath, string sequencePath,
        MetaSchemaDocument metaSchema)
    {
        DocsRoot = docsRoot;
        SchemaPath = schemaPath;
        SequencePath = sequencePath;
        MetaSchema = metaSchema;
    }

    public static WriteContext FromRoot(string root)
    {
        var docsRoot = Path.Combine(root, "docs");
        var schemaPath = Path.Combine(docsRoot, "Схема.yml");
        var metaSchemaPath = Path.Combine(docsRoot, ".docswalker", "meta-schema.yml");
        var sequencePath = Path.Combine(docsRoot, ".docswalker", "sequence.txt");
        var meta = SchemaLoader.LoadMetaSchema(metaSchemaPath);
        return new WriteContext(docsRoot, schemaPath, sequencePath, meta);
    }
}

/// <summary>
/// Write-API DocsWalker. Все операции применяются на снимке состояния (граф + схема),
/// после чего весь снимок прогоняется через <see cref="Validator"/>; при успехе
/// затронутые YAML-файлы перезаписываются <see cref="AtomicWriter"/>'ом, в одной пачке
/// с новым значением sequence.txt. При ошибке (валидация / IO / конфликт типа) ничего
/// не записывается, ошибка возвращается структурированно.
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
        // непротиворечиво. Multi-process safety здесь не вводится (см. step-write-api-basics).
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
                throw new WriteApiException(ex.Code, $"Операция #{i} ({ops[i].Type}): {ex.Message}");
            }
        }

        var newGraph = state.BuildGraph();
        var newSchema = state.SchemaModified ? state.Schema : schema;

        var validator = new Validator(_ctx.MetaSchema, newSchema);
        var validation = validator.Validate(newGraph);
        if (!validation.IsValid)
            throw new WriteValidationException(validation.Errors);

        var targets = new List<AtomicWriteTarget>();

        if (state.SchemaModified)
            targets.Add(new AtomicWriteTarget(_ctx.SchemaPath, SchemaEmitter.EmitSchema(newSchema)));

        foreach (var rootId in state.AffectedDocumentIds)
        {
            var docNode = newGraph.GetById(rootId)
                ?? throw new WriteApiException(
                    "internal_inconsistency",
                    $"Документ id={rootId} помечен как изменённый, но отсутствует в новом графе.");
            var yaml = Emitter.EmitDocument(newGraph, newSchema, docNode);
            var absolutePath = Path.Combine(_ctx.DocsRoot, docNode.SourceFile.Replace('/', Path.DirectorySeparatorChar));
            targets.Add(new AtomicWriteTarget(absolutePath, yaml));
        }

        if (state.IdsConsumed > 0)
        {
            var newSequence = state.SequenceBase + state.IdsConsumed;
            var content = newSequence.ToString(CultureInfo.InvariantCulture) + "\n";
            targets.Add(new AtomicWriteTarget(_ctx.SequencePath, content));
        }

        if (targets.Count == 0)
            throw new WriteApiException(
                "no_effect",
                "Пачка операций не приводит ни к каким изменениям в файлах.");

        AtomicWriter.WriteAll(targets);

        return new WriteResult(opResults);
    }

    private static WriteOpResult ApplyOp(WriteState s, WriteOp op) => op switch
    {
        CreateNodeOp c => ApplyCreateNode(s, c),
        UpdateNodeOp u => ApplyUpdateNode(s, u),
        DeleteNodeOp d => ApplyDeleteNode(s, d),
        CreateRefOp cr => ApplyCreateRef(s, cr),
        DeleteRefOp dr => ApplyDeleteRef(s, dr),
        AddRefTypeOp ar => ApplyAddRefType(s, ar),
        _ => throw new WriteApiException(
            "unknown_op",
            $"Неизвестный тип операции '{op.Type}'."),
    };

    private static WriteOpResult ApplyCreateNode(WriteState s, CreateNodeOp op)
    {
        var parent = s.GetNode(op.ParentId)
            ?? throw new WriteApiException(
                "parent_not_found",
                $"Родительский узел id={op.ParentId} не найден.");

        var nodeType = s.ResolveNodeType(op.TypeName);

        // Поддерживаем только то, что прямо описано в Схеме: parent ∈ {document, section}.
        // Создание новых документов (с новым YAML-файлом) — отдельная задача, в этом шаге
        // не реализуется; LLM получит структурированную ошибку.
        if (string.Equals(parent.TypeName, "document", StringComparison.Ordinal))
            return CreateUnderDocument(s, parent, nodeType, op);
        if (string.Equals(parent.TypeName, "section", StringComparison.Ordinal))
            return CreateUnderSection(s, parent, nodeType, op);

        throw new WriteApiException(
            "invalid_parent",
            $"Узел типа '{parent.TypeName}' (id={parent.Id}) не может быть родителем create_node.");
    }

    private static WriteOpResult CreateUnderDocument(
        WriteState s, Node parent, NodeType nodeType, CreateNodeOp op)
    {
        if (!string.Equals(nodeType.Name, "section", StringComparison.Ordinal))
            throw new WriteApiException(
                "invalid_child_type",
                $"Под document можно создавать только section; запрошен тип '{nodeType.Name}'.");
        var title = RequireTitle(op);
        var id = s.ReserveId();

        var sectionBody = ParseSectionBody(op.Body);
        var blocks = BuildSectionBlocks(s.Schema, id, sectionBody);
        var explicitOutRefs = ExtractExplicitRefs(blocks);

        var newNode = new Node
        {
            Id = id,
            TypeName = "section",
            Title = title,
            ParentId = parent.Id,
            ParentBlockName = "content",
            SourceFile = parent.SourceFile,
            Blocks = blocks,
            ExplicitOutRefs = explicitOutRefs,
        };

        s.Add(newNode);
        s.MarkDocumentDirty(parent.Id);

        return new WriteOpResult("create-node", new JsonObject
        {
            ["id"] = id,
            ["type"] = nodeType.Name,
            ["title"] = title,
            ["parent_id"] = parent.Id,
        });
    }

    private static WriteOpResult CreateUnderSection(
        WriteState s, Node parent, NodeType nodeType, CreateNodeOp op)
    {
        var parentType = s.ResolveNodeType(parent.TypeName);
        var blockDef = ResolveChildBlock(parentType, nodeType.Name);
        var id = s.ReserveId();

        Node newNode;
        switch (nodeType.Name)
        {
            case "definition":
            case "example":
            {
                var title = RequireTitle(op);
                var inlineValue = ExtractInlineValue(op.Body, nodeType.Name);
                newNode = new Node
                {
                    Id = id,
                    TypeName = nodeType.Name,
                    Title = title,
                    ParentId = parent.Id,
                    ParentBlockName = blockDef.Name,
                    SourceFile = parent.SourceFile,
                    InlineValue = inlineValue,
                };
                break;
            }
            case "field":
            {
                var name = op.Name
                    ?? throw new WriteApiException(
                        "missing_parameter",
                        "Для типа 'field' требуется параметр 'name'.");
                var fields = BuildFieldNodeFields(id, name, op.Body);
                newNode = new Node
                {
                    Id = id,
                    TypeName = nodeType.Name,
                    Title = name,
                    ParentId = parent.Id,
                    ParentBlockName = blockDef.Name,
                    SourceFile = parent.SourceFile,
                    Fields = fields,
                };
                break;
            }
            default:
                throw new WriteApiException(
                    "invalid_child_type",
                    $"Создание узлов типа '{nodeType.Name}' под section на этом шаге не поддерживается.");
        }

        s.Add(newNode);

        // Обновляем у parent ChildrenBlock с этим именем — добавляем id в конец.
        var updatedParent = AddChildToParentBlock(parent, blockDef.Name, id);
        s.Replace(updatedParent);
        s.MarkDocumentDirtyForNode(parent.Id);

        return new WriteOpResult("create-node", new JsonObject
        {
            ["id"] = id,
            ["type"] = nodeType.Name,
            ["title"] = newNode.Title,
            ["parent_id"] = parent.Id,
        });
    }

    private static WriteOpResult ApplyUpdateNode(WriteState s, UpdateNodeOp op)
    {
        var node = s.GetNode(op.Id)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел id={op.Id} не найден.");

        var nodeType = s.ResolveNodeType(node.TypeName);
        var patch = op.Patch;
        var newTitle = node.Title;
        var newBlocks = node.Blocks;
        var newFields = node.Fields;
        var newInline = node.InlineValue;
        var newRefs = node.ExplicitOutRefs;

        if (patch.TryGetPropertyValue("title", out var titleNode) && titleNode is not null)
        {
            newTitle = ReadString(titleNode, "title");
        }
        if (patch.TryGetPropertyValue("name", out var nameNode) && nameNode is not null)
        {
            // Применимо только к field-узлам (title_source=field, title_field=name).
            if (nodeType.TitleSource != TitleSourceKind.Field || nodeType.TitleField != "name")
                throw new WriteApiException(
                    "patch_unsupported",
                    $"Узел типа '{nodeType.Name}': patch.name применим только к узлам с title_source=field/title_field=name.");
            var name = ReadString(nameNode, "name");
            newTitle = name;
            newFields = ReplaceFieldScalar(newFields ?? Array.Empty<FieldValue>(), "name", name);
        }
        if (patch.TryGetPropertyValue("value", out var valueNode) && valueNode is not null)
        {
            if (nodeType.Kind != TypeKind.SingleKeyMapping || nodeType.ValueType != "text")
                throw new WriteApiException(
                    "patch_unsupported",
                    $"Узел типа '{nodeType.Name}': patch.value применим только к single_key_mapping с value_type=text.");
            newInline = ReadString(valueNode, "value");
        }
        if (patch.TryGetPropertyValue("blocks", out var blocksNode) && blocksNode is JsonObject blocksObj)
        {
            if (nodeType.Kind != TypeKind.SingleKeyMapping || !string.Equals(nodeType.Name, "section", StringComparison.Ordinal))
                throw new WriteApiException(
                    "patch_unsupported",
                    $"Узел типа '{nodeType.Name}': patch.blocks применим только к section.");
            var sectionBody = ParseSectionBodyFromPatch(blocksObj);
            // ChildrenBlock-блоки оставляем как были (definitions/examples/fields управляются
            // отдельными операциями); патчим только text- и out_refs-блоки.
            newBlocks = MergeSectionBlocks(node.Blocks, s.Schema, op.Id, sectionBody);
            newRefs = ExtractExplicitRefs(newBlocks!);
        }
        if (patch.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is JsonObject fieldsObj)
        {
            if (nodeType.Kind != TypeKind.Mapping)
                throw new WriteApiException(
                    "patch_unsupported",
                    $"Узел типа '{nodeType.Name}': patch.fields применим только к mapping-узлам.");
            newFields = ApplyFieldsPatch(node.Fields ?? Array.Empty<FieldValue>(), nodeType, fieldsObj);
            if (nodeType.TitleSource == TitleSourceKind.Field && nodeType.TitleField is string tf)
            {
                var v = newFields.FirstOrDefault(f => f.Name == tf);
                if (v is not null && v.Scalar is not null) newTitle = v.Scalar;
            }
        }

        var updated = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = newTitle,
            ParentId = node.ParentId,
            ParentBlockName = node.ParentBlockName,
            SourceFile = node.SourceFile,
            Fields = newFields,
            Blocks = newBlocks,
            InlineValue = newInline,
            ExplicitOutRefs = newRefs,
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
                $"Узел id={op.Id} не найден.");
        if (node.ParentId is null)
            throw new WriteApiException(
                "delete_document_unsupported",
                $"Удаление документа id={op.Id} ('{node.Title}') в этой версии write-API не поддерживается.");

        // Запрет на входящие явные связи.
        var inExplicit = s.ListIncomingExplicitRefs(op.Id).ToList();
        if (inExplicit.Count > 0)
            throw new WriteApiException(
                "incoming_refs",
                $"Узел id={op.Id} имеет {inExplicit.Count} входящих явных связ(и/ей); удаление запрещено.");

        var parent = s.GetNode(node.ParentId.Value)
            ?? throw new WriteApiException(
                "parent_not_found",
                $"Родитель id={node.ParentId} удаляемого узла id={op.Id} не найден.");

        // Если ребёнок лежал в ChildrenBlock родителя — убираем его id оттуда.
        if (node.ParentBlockName is string blockName)
        {
            var updatedParent = RemoveChildFromParentBlock(parent, blockName, op.Id);
            if (!ReferenceEquals(updatedParent, parent))
                s.Replace(updatedParent);
        }
        s.Remove(op.Id);
        s.MarkDocumentDirtyForNode(parent.Id);

        return new WriteOpResult("delete-node", new JsonObject
        {
            ["id"] = op.Id,
        });
    }

    private static WriteOpResult ApplyCreateRef(WriteState s, CreateRefOp op)
    {
        var src = s.GetNode(op.FromId)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел-источник id={op.FromId} не найден.");
        if (s.GetNode(op.ToId) is null)
            throw new WriteApiException(
                "node_not_found",
                $"Узел-цель id={op.ToId} не найден.");

        var rt = s.ResolveRefType(op.RefType);
        if (rt.System)
            throw new WriteApiException(
                "system_ref_type",
                $"Тип связи '{op.RefType}' — системный, его нельзя создавать через create_ref.");

        var existing = src.ExplicitOutRefs ?? Array.Empty<Ref>();
        if (existing.Any(r => string.Equals(r.TypeName, op.RefType, StringComparison.Ordinal) && r.ToId == op.ToId))
            throw new WriteApiException(
                "duplicate_ref",
                $"Узел id={op.FromId} уже имеет связь '{op.RefType}' → id={op.ToId}.");

        var newRefs = existing.Concat(new[] { new Ref(op.FromId, op.RefType, op.ToId, RefOrigin.Explicit) }).ToArray();
        var updatedBlocks = ReplaceOutRefsBlock(src, newRefs);
        var updated = new Node
        {
            Id = src.Id,
            TypeName = src.TypeName,
            Title = src.Title,
            ParentId = src.ParentId,
            ParentBlockName = src.ParentBlockName,
            SourceFile = src.SourceFile,
            Fields = src.Fields,
            Blocks = updatedBlocks,
            InlineValue = src.InlineValue,
            ExplicitOutRefs = newRefs,
        };
        s.Replace(updated);
        s.MarkDocumentDirtyForNode(src.Id);

        return new WriteOpResult("create-ref", new JsonObject
        {
            ["from_id"] = op.FromId,
            ["type"] = op.RefType,
            ["to_id"] = op.ToId,
        });
    }

    private static WriteOpResult ApplyDeleteRef(WriteState s, DeleteRefOp op)
    {
        var src = s.GetNode(op.FromId)
            ?? throw new WriteApiException(
                "node_not_found",
                $"Узел-источник id={op.FromId} не найден.");

        var existing = src.ExplicitOutRefs ?? Array.Empty<Ref>();
        var filtered = existing
            .Where(r => !(string.Equals(r.TypeName, op.RefType, StringComparison.Ordinal) && r.ToId == op.ToId))
            .ToArray();
        if (filtered.Length == existing.Count)
            throw new WriteApiException(
                "ref_not_found",
                $"У узла id={op.FromId} нет связи '{op.RefType}' → id={op.ToId}.");

        var updatedBlocks = ReplaceOutRefsBlock(src, filtered);
        var updated = new Node
        {
            Id = src.Id,
            TypeName = src.TypeName,
            Title = src.Title,
            ParentId = src.ParentId,
            ParentBlockName = src.ParentBlockName,
            SourceFile = src.SourceFile,
            Fields = src.Fields,
            Blocks = updatedBlocks,
            InlineValue = src.InlineValue,
            ExplicitOutRefs = filtered,
        };
        s.Replace(updated);
        s.MarkDocumentDirtyForNode(src.Id);

        return new WriteOpResult("delete-ref", new JsonObject
        {
            ["from_id"] = op.FromId,
            ["type"] = op.RefType,
            ["to_id"] = op.ToId,
        });
    }

    private static WriteOpResult ApplyAddRefType(WriteState s, AddRefTypeOp op)
    {
        if (string.IsNullOrWhiteSpace(op.Name))
            throw new WriteApiException("invalid_name", "Имя ref_type не должно быть пустым.");
        if (op.Direction != "from_to")
            throw new WriteApiException(
                "invalid_direction",
                $"Для прикладного ref_type direction должен быть 'from_to', получено '{op.Direction}'.");

        // Запреты на коллизии с системными именами и default-блоками — фиксированный
        // список из docs/Правила оформления.yml/«Перекрёстные ссылки».
        var reserved = new[] { "path", "definitions", "examples", "fields", "content" };
        if (reserved.Contains(op.Name, StringComparer.Ordinal))
            throw new WriteApiException(
                "reserved_name",
                $"Имя '{op.Name}' зарезервировано (системный тип или default-блок) и не может использоваться для ref_type.");

        if (s.Schema.Types.Any(t => string.Equals(t.Name, op.Name, StringComparison.Ordinal)))
            throw new WriteApiException(
                "duplicate_type",
                $"Тип '{op.Name}' уже объявлен в Схеме.");

        var newRefType = new RefType(op.Name, RefDirection.FromTo, false, op.Description);
        var newTypes = s.Schema.Types.Concat(new TypeDefinition[] { newRefType }).ToList();
        s.Schema = new SchemaDocument(s.Schema.Description, newTypes);
        s.SchemaModified = true;

        return new WriteOpResult("add-ref-type", new JsonObject
        {
            ["name"] = op.Name,
            ["direction"] = op.Direction,
        });
    }

    private static IReadOnlyList<NodeBlock>? ReplaceOutRefsBlock(Node src, IReadOnlyList<Ref> refs)
    {
        // Блок out_refs может присутствовать у любого носителя блоков с blocks/of=reference;
        // в актуальной Схеме это section. Если у узла нет blocks — возвращаем null.
        if (src.Blocks is null)
        {
            if (refs.Count == 0) return null;
            return new NodeBlock[] { new OutRefsBlock("out_refs", refs) };
        }
        var existing = src.Blocks;
        var idx = -1;
        for (int i = 0; i < existing.Count; i++)
            if (existing[i] is OutRefsBlock) { idx = i; break; }

        var list = existing.ToList();
        if (refs.Count == 0)
        {
            if (idx >= 0) list.RemoveAt(idx);
        }
        else
        {
            var block = new OutRefsBlock("out_refs", refs);
            if (idx >= 0) list[idx] = block;
            else list.Add(block);
        }
        return list;
    }

    private static IReadOnlyList<Ref>? ExtractExplicitRefs(IReadOnlyList<NodeBlock>? blocks)
    {
        if (blocks is null) return null;
        foreach (var b in blocks)
            if (b is OutRefsBlock orb) return orb.Refs;
        return null;
    }

    private static Node AddChildToParentBlock(Node parent, string blockName, int childId)
    {
        var blocks = (parent.Blocks ?? Array.Empty<NodeBlock>()).ToList();
        int idx = blocks.FindIndex(b => b.Name == blockName);
        if (idx < 0)
        {
            blocks.Add(new ChildrenBlock(blockName, new[] { childId }));
        }
        else if (blocks[idx] is ChildrenBlock cb)
        {
            blocks[idx] = new ChildrenBlock(blockName, cb.ChildIds.Concat(new[] { childId }).ToList());
        }
        else
        {
            throw new WriteApiException(
                "invalid_parent_block",
                $"Блок '{blockName}' у родителя id={parent.Id} не является блоком дочерних узлов.");
        }
        return CloneWithBlocks(parent, blocks);
    }

    private static Node RemoveChildFromParentBlock(Node parent, string blockName, int childId)
    {
        if (parent.Blocks is null) return parent;
        var idx = -1;
        for (int i = 0; i < parent.Blocks.Count; i++)
            if (parent.Blocks[i].Name == blockName) { idx = i; break; }
        if (idx < 0) return parent;
        if (parent.Blocks[idx] is not ChildrenBlock cb) return parent;
        var filtered = cb.ChildIds.Where(id => id != childId).ToList();
        if (filtered.Count == cb.ChildIds.Count) return parent;
        var blocks = parent.Blocks.ToList();
        blocks[idx] = new ChildrenBlock(blockName, filtered);
        return CloneWithBlocks(parent, blocks);
    }

    private static Node CloneWithBlocks(Node node, IReadOnlyList<NodeBlock> blocks) => new()
    {
        Id = node.Id,
        TypeName = node.TypeName,
        Title = node.Title,
        ParentId = node.ParentId,
        ParentBlockName = node.ParentBlockName,
        SourceFile = node.SourceFile,
        Fields = node.Fields,
        Blocks = blocks,
        InlineValue = node.InlineValue,
        ExplicitOutRefs = node.ExplicitOutRefs,
    };

    private static BlockDefinition ResolveChildBlock(NodeType parentType, string childTypeName)
    {
        if (parentType.Blocks is null)
            throw new WriteApiException(
                "no_blocks",
                $"Тип '{parentType.Name}' не объявляет блоков детей.");
        var matches = parentType.Blocks.Where(b => string.Equals(b.Of, childTypeName, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            throw new WriteApiException(
                "invalid_child_type",
                $"У типа '{parentType.Name}' нет блока, принимающего детей типа '{childTypeName}'.");
        if (matches.Count > 1)
            throw new WriteApiException(
                "ambiguous_child_block",
                $"У типа '{parentType.Name}' несколько блоков для типа '{childTypeName}': {string.Join(", ", matches.Select(b => b.Name))}.");
        return matches[0];
    }

    private static IReadOnlyList<NodeBlock> BuildSectionBlocks(
        SchemaDocument schema, int sectionId, SectionBody body)
    {
        var sectionType = schema.Types.OfType<NodeType>().First(t => t.Name == "section");
        var blockDefs = (sectionType.Blocks ?? Array.Empty<BlockDefinition>())
            .ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);
        var blocks = new List<NodeBlock>();
        foreach (var kv in body.TextBlocks)
        {
            if (!blockDefs.TryGetValue(kv.Key, out var def) || def.Of != "text")
                throw new WriteApiException(
                    "unknown_block",
                    $"Блок '{kv.Key}' не объявлен у section с of=text.");
            blocks.Add(new TextBlock(kv.Key, kv.Value));
        }
        if (body.OutRefs is not null)
        {
            var refs = new List<Ref>();
            foreach (var (typeName, toId) in body.OutRefs)
                refs.Add(new Ref(sectionId, typeName, toId, RefOrigin.Explicit));
            blocks.Add(new OutRefsBlock("out_refs", refs));
        }
        return blocks;
    }

    private static IReadOnlyList<NodeBlock>? MergeSectionBlocks(
        IReadOnlyList<NodeBlock>? current, SchemaDocument schema, int sectionId, SectionBody body)
    {
        var list = (current ?? Array.Empty<NodeBlock>()).ToList();
        // Удаляем все text- и out_refs-блоки, заменяем на патч.
        list.RemoveAll(b => b is TextBlock || b is OutRefsBlock);
        var rebuilt = BuildSectionBlocks(schema, sectionId, body);
        list.AddRange(rebuilt);
        return list;
    }

    private static IReadOnlyList<FieldValue> ApplyFieldsPatch(
        IReadOnlyList<FieldValue> current, NodeType nodeType, JsonObject patch)
    {
        var byName = current.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal);
        foreach (var prop in patch)
        {
            if (string.Equals(prop.Key, "id", StringComparison.Ordinal))
                throw new WriteApiException(
                    "patch_id_immutable",
                    "Поле 'id' не подлежит изменению.");
            var def = nodeType.Fields?.FirstOrDefault(f => f.Name == prop.Key);
            if (def is null)
                throw new WriteApiException(
                    "unknown_field",
                    $"Тип '{nodeType.Name}' не имеет поля '{prop.Key}'.");
            byName[prop.Key] = JsonToFieldValue(prop.Key, def, prop.Value);
        }
        // Возвращаем в том же порядке, что и в типе.
        var ordered = new List<FieldValue>(byName.Count);
        if (nodeType.Fields is not null)
            foreach (var fd in nodeType.Fields)
                if (byName.TryGetValue(fd.Name, out var fv)) ordered.Add(fv);
        // Незнакомые имена не попадут (они отрезаны выше через unknown_field).
        return ordered;
    }

    private static FieldValue JsonToFieldValue(string name, FieldDefinition def, JsonNode? value)
    {
        if (value is null)
            throw new WriteApiException(
                "invalid_field_value",
                $"Поле '{name}': null недопустим.");
        if (def.Type == "list")
        {
            if (value is not JsonArray arr)
                throw new WriteApiException(
                    "invalid_field_value",
                    $"Поле '{name}': ожидался JSON-массив, получено {value.GetType().Name}.");
            var items = new List<string>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonValue jv || !jv.TryGetValue<string>(out var s))
                    throw new WriteApiException(
                        "invalid_field_value",
                        $"Поле '{name}'[{i}]: ожидалась строка.");
                items.Add(s);
            }
            return new FieldValue(name, null, items);
        }
        if (value is not JsonValue jv2)
            throw new WriteApiException(
                "invalid_field_value",
                $"Поле '{name}': ожидался скаляр.");
        if (jv2.TryGetValue<string>(out var sval)) return new FieldValue(name, sval, null);
        if (jv2.TryGetValue<bool>(out var bval)) return new FieldValue(name, bval ? "true" : "false", null);
        if (jv2.TryGetValue<int>(out var ival)) return new FieldValue(name, ival.ToString(CultureInfo.InvariantCulture), null);
        if (jv2.TryGetValue<long>(out var lval)) return new FieldValue(name, lval.ToString(CultureInfo.InvariantCulture), null);
        throw new WriteApiException(
            "invalid_field_value",
            $"Поле '{name}': значение '{value.ToJsonString()}' не приводится к скаляру.");
    }

    private static IReadOnlyList<FieldValue> BuildFieldNodeFields(int id, string name, JsonObject? body)
    {
        var fields = new List<FieldValue>
        {
            new("id", id.ToString(CultureInfo.InvariantCulture), null),
            new("name", name, null),
        };
        if (body is not null)
        {
            foreach (var prop in body)
            {
                if (string.Equals(prop.Key, "id", StringComparison.Ordinal) ||
                    string.Equals(prop.Key, "name", StringComparison.Ordinal))
                    continue;
                fields.Add(JsonToRawFieldValue(prop.Key, prop.Value));
            }
        }
        return fields;
    }

    private static FieldValue JsonToRawFieldValue(string name, JsonNode? value)
    {
        if (value is null)
            throw new WriteApiException(
                "invalid_field_value",
                $"Поле '{name}' в body: null недопустим.");
        if (value is JsonArray arr)
        {
            var items = new List<string>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonValue jv || !jv.TryGetValue<string>(out var s))
                    throw new WriteApiException(
                        "invalid_field_value",
                        $"Поле '{name}'[{i}]: ожидалась строка.");
                items.Add(s);
            }
            return new FieldValue(name, null, items);
        }
        if (value is JsonValue jv2)
        {
            if (jv2.TryGetValue<string>(out var sval)) return new FieldValue(name, sval, null);
            if (jv2.TryGetValue<bool>(out var bval)) return new FieldValue(name, bval ? "true" : "false", null);
            if (jv2.TryGetValue<int>(out var ival)) return new FieldValue(name, ival.ToString(CultureInfo.InvariantCulture), null);
            if (jv2.TryGetValue<long>(out var lval)) return new FieldValue(name, lval.ToString(CultureInfo.InvariantCulture), null);
        }
        throw new WriteApiException(
            "invalid_field_value",
            $"Поле '{name}' в body: тип не поддерживается.");
    }

    private static SectionBody ParseSectionBody(JsonObject? body)
    {
        var result = new SectionBody();
        if (body is null) return result;
        return ParseSectionBodyFromPatch(body);
    }

    private static SectionBody ParseSectionBodyFromPatch(JsonObject body)
    {
        var result = new SectionBody();
        foreach (var prop in body)
        {
            switch (prop.Key)
            {
                case "out_refs":
                    if (prop.Value is not JsonArray refsArr)
                        throw new WriteApiException(
                            "invalid_body",
                            "Блок 'out_refs' должен быть массивом объектов {type, to_id}.");
                    var refs = new List<(string Type, int ToId)>(refsArr.Count);
                    foreach (var item in refsArr)
                    {
                        if (item is not JsonObject obj)
                            throw new WriteApiException(
                                "invalid_body",
                                "Элемент out_refs должен быть объектом {type, to_id}.");
                        var type = obj["type"]?.GetValue<string>()
                            ?? throw new WriteApiException(
                                "invalid_body",
                                "Элемент out_refs без поля 'type'.");
                        var toId = obj["to_id"]?.GetValue<int>()
                            ?? throw new WriteApiException(
                                "invalid_body",
                                "Элемент out_refs без поля 'to_id'.");
                        refs.Add((type, toId));
                    }
                    result.OutRefs = refs;
                    break;

                default:
                    if (prop.Value is not JsonArray strArr)
                        throw new WriteApiException(
                            "invalid_body",
                            $"Блок '{prop.Key}' должен быть массивом строк.");
                    var items = new List<string>(strArr.Count);
                    for (int i = 0; i < strArr.Count; i++)
                    {
                        if (strArr[i] is not JsonValue jv || !jv.TryGetValue<string>(out var s))
                            throw new WriteApiException(
                                "invalid_body",
                                $"Элемент блока '{prop.Key}'[{i}] должен быть строкой.");
                        items.Add(s);
                    }
                    result.TextBlocks[prop.Key] = items;
                    break;
            }
        }
        return result;
    }

    private static string ExtractInlineValue(JsonObject? body, string typeName)
    {
        if (body is null)
            throw new WriteApiException(
                "missing_body",
                $"Для типа '{typeName}' требуется body с полем 'value' (текст определения/примера).");
        if (!body.TryGetPropertyValue("value", out var v) || v is not JsonValue jv || !jv.TryGetValue<string>(out var s))
            throw new WriteApiException(
                "missing_body",
                $"Для типа '{typeName}' body должен содержать строковое поле 'value'.");
        return s;
    }

    private static string RequireTitle(CreateNodeOp op)
    {
        if (string.IsNullOrEmpty(op.Title))
            throw new WriteApiException(
                "missing_parameter",
                $"Для типа '{op.TypeName}' требуется параметр 'title'.");
        return op.Title;
    }

    private static string ReadString(JsonNode node, string fieldName)
    {
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        throw new WriteApiException(
            "invalid_patch_value",
            $"patch.{fieldName}: ожидалась строка, получено {node.GetType().Name}.");
    }

    private static IReadOnlyList<FieldValue> ReplaceFieldScalar(
        IReadOnlyList<FieldValue> fields, string name, string newValue)
    {
        var list = fields.ToList();
        var idx = list.FindIndex(f => f.Name == name);
        if (idx < 0) list.Add(new FieldValue(name, newValue, null));
        else list[idx] = new FieldValue(name, newValue, null);
        return list;
    }

    /// <summary>
    /// Удобная форма body для секции: наборы текстовых элементов по именам блоков плюс
    /// список явных out_refs.
    /// </summary>
    private sealed class SectionBody
    {
        public Dictionary<string, IReadOnlyList<string>> TextBlocks { get; } =
            new(StringComparer.Ordinal);
        public IReadOnlyList<(string Type, int ToId)>? OutRefs { get; set; }
    }
}
