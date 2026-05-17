using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

public sealed class LlmJsonApiTxExecutor
{
    private readonly GraphModel _graph;
    private readonly Func<IReadOnlyList<WriteOp>, WriteResult> _apply;
    private readonly LlmJsonApiPathResolver _pathResolver;
    private readonly LlmJsonApiCoordinateResolver _coordinateResolver;
    private readonly Dictionary<string, TypeDefinition> _typesByName;

    public LlmJsonApiTxExecutor(GraphModel graph, SchemaDocument schema, WriteApi writeApi)
        : this(graph, schema, ops => writeApi.Apply(ops))
    {
    }

    public LlmJsonApiTxExecutor(
        GraphModel graph,
        SchemaDocument schema,
        Func<IReadOnlyList<WriteOp>, WriteResult> apply)
    {
        _graph = graph;
        _apply = apply;
        _pathResolver = new LlmJsonApiPathResolver(graph);
        _coordinateResolver = new LlmJsonApiCoordinateResolver(graph, schema);
        _typesByName = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<LlmOperationResult> Execute(LlmRequest request)
    {
        if (request.Method != LlmJsonApiMethod.Tx)
            throw new LlmJsonApiResolveException(
                "invalid_method",
                "$.method",
                "LlmJsonApiTxExecutor принимает только method='tx'.");

        var aliases = new LlmJsonApiAliasScope(_pathResolver, _coordinateResolver);
        var pending = new PendingResult[request.Ops.Count];
        var writeOps = new List<WriteOp>();
        var writeIndexes = new List<int>();
        var reservedPaths = new HashSet<string>(StringComparer.Ordinal);
        var hasValidationFailure = false;

        for (int i = 0; i < request.Ops.Count; i++)
        {
            var op = request.Ops[i];
            var jsonPath = $"$.ops[{i}]";
            try
            {
                switch (op)
                {
                    case LlmSelectOperation select:
                        pending[i] = ExecuteSelect(select, request.Defaults, aliases, i, jsonPath);
                        break;

                    case LlmCreateOperation create:
                        {
                            var compiled = CompileCreate(create, request.Defaults, aliases, i, jsonPath, reservedPaths);
                            writeIndexes.Add(i);
                            pending[i] = BuildPendingCreate(i, create, compiled, writeOps.Count);
                            writeOps.Add(compiled.Op);
                            aliases.DeclareCreateAlias(
                                create,
                                CreatePreviewNode(create, i, compiled),
                                i,
                                $"{jsonPath}.as");
                            break;
                        }

                    case LlmUpdateOperation update:
                        {
                            var compiled = CompileUpdate(update, request.Defaults, aliases, jsonPath);
                            AddCompiledWrite(pending, writeOps, writeIndexes, i, update, compiled);
                            break;
                        }

                    case LlmDeleteOperation delete:
                        {
                            var compiled = CompileDelete(delete, request.Defaults, aliases, jsonPath);
                            AddCompiledWrite(pending, writeOps, writeIndexes, i, delete, compiled);
                            break;
                        }

                    case LlmMoveOperation move:
                        {
                            var compiled = CompileMove(move, request.Defaults, aliases, jsonPath, reservedPaths);
                            AddCompiledWrite(pending, writeOps, writeIndexes, i, move, compiled);
                            break;
                        }

                    case LlmLinkOperation link:
                        {
                            var compiled = CompileLink(link, request.Defaults, aliases, jsonPath);
                            AddCompiledWrite(pending, writeOps, writeIndexes, i, link, compiled);
                            break;
                        }

                    case LlmUnlinkOperation unlink:
                        {
                            var compiled = CompileUnlink(unlink, request.Defaults, aliases, jsonPath);
                            AddCompiledWrite(pending, writeOps, writeIndexes, i, unlink, compiled);
                            break;
                        }

                    default:
                        var failure = new LlmJsonApiResolveException(
                            "invalid_op",
                            $"{jsonPath}.op",
                            $"Операция '{op.Op}' пока не поддерживается tx executor-ом.");
                        pending[i] = new PendingResult(BuildValidationFailure(i, op, failure), Array.Empty<int>(), null);
                        hasValidationFailure = true;
                        break;
                }
            }
            catch (LlmJsonApiResolveException ex)
            {
                pending[i] = new PendingResult(BuildValidationFailure(i, op, ex), Array.Empty<int>(), null);
                hasValidationFailure = true;
            }
        }

        if (hasValidationFailure || writeOps.Count == 0)
            return pending.Select(p => p.Result).ToArray();

        try
        {
            var writeResult = _apply(writeOps);
            ApplyWriteResults(pending, writeResult);
        }
        catch (WriteApiException ex)
        {
            MarkApplyFailure(pending, writeIndexes, BuildValidation(
                false,
                ex.Code,
                ex.Message,
                path: null,
                details: ex.RefName is null ? null : new JsonObject { ["ref"] = ex.RefName }));
        }
        catch (WriteValidationException ex)
        {
            MarkApplyFailure(pending, writeIndexes, BuildValidation(
                false,
                "validation_failed",
                ex.Message,
                path: null,
                details: BuildValidationErrors(ex)));
        }

        return pending.Select(p => p.Result).ToArray();
    }

    private PendingResult ExecuteSelect(
        LlmSelectOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath)
    {
        var nodes = _coordinateResolver.ResolveSelector(op.Select, defaults, _pathResolver, $"{jsonPath}.select");
        aliases.DeclareSelectAlias(op, nodes, index, $"{jsonPath}.as");

        var data = new JsonObject
        {
            ["count"] = nodes.Count,
            ["ids"] = BuildIds(nodes),
        };
        return new PendingResult(new LlmOperationResult(index, op.Op, op.Alias, data), Array.Empty<int>(), null);
    }

    private static void AddCompiledWrite(
        PendingResult[] pending,
        List<WriteOp> writeOps,
        List<int> writeIndexes,
        int index,
        LlmOperation op,
        CompiledWrite compiled)
    {
        var resultIndexes = new List<int>(compiled.Ops.Count);
        foreach (var writeOp in compiled.Ops)
        {
            resultIndexes.Add(writeOps.Count);
            writeOps.Add(writeOp);
        }

        writeIndexes.Add(index);
        pending[index] = new PendingResult(
            new LlmOperationResult(index, op.Op, op.Alias, compiled.Data),
            resultIndexes,
            null);
    }

    private CompiledCreate CompileCreate(
        LlmCreateOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        int index,
        string jsonPath,
        HashSet<string> reservedPaths)
    {
        if (string.IsNullOrWhiteSpace(op.Path))
            throw new LlmJsonApiResolveException("not_found", $"{jsonPath}.path", "create.path должен быть задан.");

        var path = _pathResolver.NormalizePath(op.Path, defaults, $"{jsonPath}.path");
        if (path.HasWildcard)
            throw new LlmJsonApiResolveException(
                "ambiguous_selector",
                $"{jsonPath}.path",
                "create.path должен быть exact path без wildcard.");

        if (_pathResolver.AllNodes.Any(n => string.Equals(n.Path, path.Path, StringComparison.Ordinal)) ||
            !reservedPaths.Add(path.Path))
        {
            throw new LlmJsonApiResolveException(
                "already_exists",
                $"{jsonPath}.path",
                $"Path '{path.Path}' уже занят существующим узлом.");
        }

        var parent = ResolveParent(path.Path);
        if (parent is null)
            throw new LlmJsonApiResolveException(
                "path_parent_not_found",
                $"{jsonPath}.path",
                $"Parent path для '{path.Path}' не найден.");

        var coordinates = _coordinateResolver.Resolve(op.Set.Coordinates, defaults, $"{jsonPath}.set.coordinates");
        if (coordinates.Type is null)
        {
            throw new LlmJsonApiResolveException(
                "unknown_coordinate",
                $"{jsonPath}.set.coordinates.type",
                "coordinates.type обязателен для create.",
                new JsonObject
                {
                    ["coordinate"] = "type",
                    ["value"] = null,
                });
        }

        var type = coordinates.Type;
        var refs = BuildCreateRefs(type, parent, coordinates, op.Set.Relations, defaults, aliases, jsonPath);
        ValidateRequiredRefs(type, refs, jsonPath);

        var title = PathTitle(path.Path);
        return new CompiledCreate(
            new CreateNodeOp(type.Name, title, op.Set.Text, refs),
            path.Path,
            title,
            parent.Id,
            coordinates,
            refs);
    }

    private CompiledWrite CompileUpdate(
        LlmUpdateOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var nodes = ResolveWriteTargets(op.Target, op.ExpectedCount, defaults, aliases, jsonPath);
        var ops = new List<WriteOp>();

        if (op.Set.Text is not null)
        {
            foreach (var node in nodes)
                ops.Add(new UpdateNodeOp(node.Id, NewTitle: null, NewText: op.Set.Text));
        }

        var coordinates = _coordinateResolver.Resolve(op.Set.Coordinates, defaults, $"{jsonPath}.set.coordinates");
        AddCoordinateUpdates(ops, nodes, coordinates, jsonPath);
        AddRelationUpdates(ops, nodes, op.Set.Relations, defaults, aliases, jsonPath);

        if (ops.Count == 0)
            throw new LlmJsonApiResolveException(
                "no_effect",
                $"{jsonPath}.set",
                "update.set не приводит к изменениям.");

        return new CompiledWrite(ops, new JsonObject
        {
            ["resolved"] = new JsonObject
            {
                ["count"] = nodes.Count,
                ["ids"] = BuildIds(nodes),
                ["set"] = BuildSetSummary(op.Set),
            },
        });
    }

    private CompiledWrite CompileDelete(
        LlmDeleteOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var nodes = ResolveWriteTargets(op.Target, op.ExpectedCount, defaults, aliases, jsonPath);
        var ids = nodes.Select(node => node.Id).ToArray();

        return new CompiledWrite(new WriteOp[] { new DeleteNodesOp(ids) }, new JsonObject
        {
            ["resolved"] = new JsonObject
            {
                ["count"] = ids.Length,
                ["ids"] = BuildIds(nodes),
            },
        });
    }

    private CompiledWrite CompileMove(
        LlmMoveOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath,
        HashSet<string> reservedPaths)
    {
        var source = ResolveWriteTargets(op.Source, expectedCount: null, defaults, aliases, jsonPath).Single();
        var destination = _pathResolver.NormalizePath(op.To, defaults, $"{jsonPath}.to");
        if (destination.HasWildcard)
        {
            throw new LlmJsonApiResolveException(
                "ambiguous_selector",
                $"{jsonPath}.to",
                "move.to должен быть exact path без wildcard.");
        }

        var existing = _pathResolver.AllNodes
            .FirstOrDefault(n => string.Equals(n.Path, destination.Path, StringComparison.Ordinal));
        if (existing is not null && existing.Id != source.Id)
        {
            throw new LlmJsonApiResolveException(
                "already_exists",
                $"{jsonPath}.to",
                $"Path '{destination.Path}' уже занят существующим узлом.");
        }

        if (reservedPaths.Contains(destination.Path))
        {
            throw new LlmJsonApiResolveException(
                "already_exists",
                $"{jsonPath}.to",
                $"Path '{destination.Path}' уже занят предыдущей операцией tx.");
        }

        var parent = ResolveParent(destination.Path);
        if (parent is null)
        {
            throw new LlmJsonApiResolveException(
                "path_parent_not_found",
                $"{jsonPath}.to",
                $"Parent path для '{destination.Path}' не найден.");
        }

        var title = PathTitle(destination.Path);
        var parentChanged = source.Node.ParentId != parent.Id;
        var titleChanged = !string.Equals(source.Node.Title, title, StringComparison.Ordinal);
        if (!parentChanged && !titleChanged)
        {
            throw new LlmJsonApiResolveException(
                "no_effect",
                $"{jsonPath}.to",
                $"move.to совпадает с текущим path узла id={source.Id}.");
        }

        reservedPaths.Add(destination.Path);

        var ops = new List<WriteOp>();
        if (parentChanged)
            ops.Add(new MoveNodeOp(source.Id, parent.Id));
        if (titleChanged)
            ops.Add(new UpdateNodeOp(source.Id, NewTitle: title, NewText: null));

        return new CompiledWrite(ops, new JsonObject
        {
            ["resolved"] = new JsonObject
            {
                ["id"] = source.Id,
                ["from_path"] = source.Path,
                ["to_path"] = destination.Path,
                ["parent_id"] = parent.Id,
                ["moved"] = parentChanged,
                ["renamed"] = titleChanged,
            },
        });
    }

    private CompiledWrite CompileLink(
        LlmLinkOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var endpoints = ResolveLinkEndpoints(op.From, op.To, op.Name, defaults, aliases, jsonPath);
        return new CompiledWrite(new WriteOp[] { new CreateRefOp(endpoints.From.Id, op.Name, endpoints.To.Id) }, BuildLinkData(endpoints));
    }

    private CompiledWrite CompileUnlink(
        LlmUnlinkOperation op,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var endpoints = ResolveLinkEndpoints(op.From, op.To, op.Name, defaults, aliases, jsonPath);
        return new CompiledWrite(new WriteOp[] { new DeleteRefOp(endpoints.From.Id, op.Name, endpoints.To.Id) }, BuildLinkData(endpoints));
    }

    private LinkEndpoints ResolveLinkEndpoints(
        LlmTarget from,
        LlmTarget to,
        string name,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var fromNode = ResolveSingleWriteTarget(from, defaults, aliases, $"{jsonPath}.from");
        var toNode = ResolveSingleWriteTarget(to, defaults, aliases, $"{jsonPath}.to");
        var fromType = ResolveNodeType(fromNode.Node.TypeName);
        ResolveSemanticRef(fromType, name, $"{jsonPath}.name");
        return new LinkEndpoints(fromNode, toNode, name);
    }

    private IReadOnlyList<LlmResolvedNode> ResolveWriteTargets(
        LlmTarget target,
        int? expectedCount,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        ValidateTargetShape(target, jsonPath);
        var nodes = aliases.ResolveTarget(target, defaults, jsonPath);
        ValidateExpectedCount(target, nodes.Count, expectedCount, jsonPath);
        return nodes;
    }

    private LlmResolvedNode ResolveSingleWriteTarget(
        LlmTarget target,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        ValidateTargetShape(target, jsonPath);
        if (target.Ids.Count > 0)
        {
            throw new LlmJsonApiResolveException(
                "ambiguous_selector",
                $"{jsonPath}.ids",
                "Одиночная операция должна выбирать цель через id, path, target alias или select, а не ids.");
        }

        var nodes = aliases.ResolveTarget(target, defaults, jsonPath);
        if (nodes.Count == 1)
            return nodes[0];

        throw new LlmJsonApiResolveException(
            nodes.Count == 0 ? "not_found" : "ambiguous_selector",
            jsonPath,
            $"Одиночная операция должна выбрать ровно один узел, найдено {nodes.Count}.",
            new JsonObject { ["actual_count"] = nodes.Count });
    }

    private static void ValidateTargetShape(LlmTarget target, string jsonPath)
    {
        var forms = 0;
        if (target.Id.HasValue) forms++;
        if (target.Ids.Count > 0) forms++;
        if (target.Path is not null) forms++;
        if (target.Alias is not null) forms++;
        if (target.Select is not null) forms++;

        if (forms == 1)
            return;

        throw new LlmJsonApiResolveException(
            "ambiguous_selector",
            jsonPath,
            "Target должен задавать ровно один способ выбора: id, ids, path, target alias или select.",
            new JsonObject { ["target_forms"] = forms });
    }

    private static void ValidateExpectedCount(
        LlmTarget target,
        int actualCount,
        int? expectedCount,
        string jsonPath)
    {
        var uniqueIdsCount = target.Ids.Distinct().Count();
        if (target.Ids.Count > 0 && uniqueIdsCount != target.Ids.Count)
        {
            throw new LlmJsonApiResolveException(
                "count_mismatch",
                $"{jsonPath}.ids",
                "ids не должен содержать повторы.",
                new JsonObject
                {
                    ["actual_count"] = actualCount,
                    ["ids_count"] = target.Ids.Count,
                    ["unique_ids_count"] = uniqueIdsCount,
                });
        }

        if (target.Ids.Count > 0 && expectedCount is null)
        {
            throw new LlmJsonApiResolveException(
                "count_mismatch",
                $"{jsonPath}.expected_count",
                "Для ids в update/delete требуется expected_count.",
                new JsonObject
                {
                    ["actual_count"] = actualCount,
                    ["ids_count"] = target.Ids.Count,
                });
        }

        if (expectedCount is int expected)
        {
            if (actualCount == expected)
                return;

            throw new LlmJsonApiResolveException(
                "count_mismatch",
                $"{jsonPath}.expected_count",
                $"expected_count={expected}, фактически найдено {actualCount}.",
                new JsonObject
                {
                    ["expected_count"] = expected,
                    ["actual_count"] = actualCount,
                });
        }

        if (actualCount == 1)
            return;

        throw new LlmJsonApiResolveException(
            actualCount == 0 ? "not_found" : "ambiguous_selector",
            jsonPath,
            $"Операция без expected_count должна выбрать ровно один узел, найдено {actualCount}.",
            new JsonObject { ["actual_count"] = actualCount });
    }

    private void AddCoordinateUpdates(
        List<WriteOp> ops,
        IReadOnlyList<LlmResolvedNode> nodes,
        LlmResolvedCoordinates coordinates,
        string jsonPath)
    {
        if (coordinates.Type is not null)
        {
            foreach (var node in nodes)
            {
                if (!string.Equals(node.Node.TypeName, coordinates.Type.Name, StringComparison.Ordinal))
                {
                    throw new LlmJsonApiResolveException(
                        "unknown_coordinate",
                        $"{jsonPath}.set.coordinates.type",
                        "update не меняет structural coordinates.type текущего узла.",
                        new JsonObject
                        {
                            ["id"] = node.Id,
                            ["current_type"] = node.Node.TypeName,
                            ["requested_type"] = coordinates.Type.Name,
                        });
                }
            }
        }

        foreach (var coordinate in coordinates.Classifiers)
        {
            foreach (var node in nodes)
            {
                var type = ResolveNodeType(node.Node.TypeName);
                ResolveTreeRef(type, coordinate, $"{jsonPath}.set.coordinates.{coordinate.Name}");
                if (_graph.GetScopeParent(node.Id, coordinate.Name) == coordinate.Id)
                    continue;

                ops.Add(new MoveNodeOp(node.Id, coordinate.Id, coordinate.Name));
            }
        }
    }

    private void AddRelationUpdates(
        List<WriteOp> ops,
        IReadOnlyList<LlmResolvedNode> nodes,
        IReadOnlyDictionary<string, LlmRelationChange> relations,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        foreach (var (name, change) in relations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (change.Mode is null)
            {
                throw new LlmJsonApiResolveException(
                    "invalid_relation_patch",
                    $"{jsonPath}.set.relations.{name}",
                    "update.set.relations требует явный режим add, remove или replace.");
            }

            var targets = ResolveRelationTargets(
                    change.Targets,
                    defaults,
                    aliases,
                    $"{jsonPath}.set.relations.{name}")
                .Distinct()
                .ToArray();

            foreach (var node in nodes)
            {
                var type = ResolveNodeType(node.Node.TypeName);
                ResolveSemanticRef(type, name, $"{jsonPath}.set.relations.{name}");
                switch (change.Mode.Value)
                {
                    case LlmRelationPatchMode.Add:
                        foreach (var target in targets)
                            ops.Add(new CreateRefOp(node.Id, name, target));
                        break;

                    case LlmRelationPatchMode.Remove:
                        foreach (var target in targets)
                            ops.Add(new DeleteRefOp(node.Id, name, target));
                        break;

                    case LlmRelationPatchMode.Replace:
                        AddReplaceRelationUpdates(ops, node, name, targets);
                        break;
                }
            }
        }
    }

    private static void AddReplaceRelationUpdates(
        List<WriteOp> ops,
        LlmResolvedNode node,
        string name,
        IReadOnlyList<int> targetIds)
    {
        var existing = node.Node.OutRefs.TryGetValue(name, out var current)
            ? current.ToArray()
            : Array.Empty<int>();
        var desired = targetIds.ToHashSet();
        var existingSet = existing.ToHashSet();

        foreach (var oldTarget in existing.Where(id => !desired.Contains(id)))
            ops.Add(new DeleteRefOp(node.Id, name, oldTarget));
        foreach (var newTarget in targetIds.Where(id => !existingSet.Contains(id)))
            ops.Add(new CreateRefOp(node.Id, name, newTarget));
    }

    private Dictionary<string, IReadOnlyList<int>> BuildCreateRefs(
        TypeDefinition type,
        LlmResolvedNode parent,
        LlmResolvedCoordinates coordinates,
        IReadOnlyDictionary<string, LlmRelationChange> relations,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var refs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [Node.PathRefName] = new[] { parent.Id },
        };

        foreach (var coordinate in coordinates.Classifiers)
        {
            var refDef = ResolveTreeRef(type, coordinate, $"{jsonPath}.set.coordinates.{coordinate.Name}");
            refs[refDef.Name] = new[] { coordinate.Id };
        }

        foreach (var (name, change) in relations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var refDef = ResolveSemanticRef(type, name, $"{jsonPath}.set.relations.{name}");
            refs[refDef.Name] = ResolveRelationTargets(
                change.Targets,
                defaults,
                aliases,
                $"{jsonPath}.set.relations.{name}");
        }

        return refs;
    }

    private RefDef ResolveTreeRef(TypeDefinition type, LlmResolvedCoordinate coordinate, string jsonPath)
    {
        var matches = type.OutRefs
            .Where(rd => string.Equals(rd.Tree, coordinate.Name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
            return matches[0];

        if (matches.Length == 0)
        {
            throw new LlmJsonApiResolveException(
                "unknown_coordinate",
                jsonPath,
                $"Тип '{type.Name}' не принимает coordinate '{coordinate.Name}'.",
                new JsonObject
                {
                    ["coordinate"] = coordinate.Name,
                    ["value"] = coordinate.Value,
                    ["type"] = type.Name,
                });
        }

        throw new LlmJsonApiResolveException(
            "ambiguous_selector",
            jsonPath,
            $"Тип '{type.Name}' имеет несколько refs для classifier tree '{coordinate.Name}'.");
    }

    private static RefDef ResolveSemanticRef(TypeDefinition type, string name, string jsonPath)
    {
        if (string.Equals(name, Node.PathRefName, StringComparison.Ordinal))
            throw new LlmJsonApiResolveException(
                "unknown_ref",
                jsonPath,
                "path задается через create.path, а не через set.relations.path.");

        var refDef = type.OutRefs.FirstOrDefault(rd => string.Equals(rd.Name, name, StringComparison.Ordinal));
        if (refDef is null)
            throw new LlmJsonApiResolveException(
                "unknown_ref",
                jsonPath,
                $"Тип '{type.Name}' не объявляет relation '{name}'.");

        if (refDef.Tree is not null)
            throw new LlmJsonApiResolveException(
                "unknown_ref",
                jsonPath,
                $"Relation '{name}' является tree-coordinate ref; задавай ее через coordinates.");

        return refDef;
    }

    private static void ValidateRequiredRefs(
        TypeDefinition type,
        IReadOnlyDictionary<string, IReadOnlyList<int>> refs,
        string jsonPath)
    {
        foreach (var rd in type.OutRefs.Where(rd => rd.Required))
        {
            if (!refs.TryGetValue(rd.Name, out var targets) || targets.Count == 0)
            {
                var location = rd.Tree is null
                    ? $"{jsonPath}.set.relations.{rd.Name}"
                    : $"{jsonPath}.set.coordinates.{rd.Tree}";
                throw new LlmJsonApiResolveException(
                    "missing_required_ref",
                    location,
                    $"Для типа '{type.Name}' требуется значение связи '{rd.Name}'.",
                    new JsonObject
                    {
                        ["ref"] = rd.Name,
                        ["type"] = type.Name,
                    });
            }
        }
    }

    private IReadOnlyList<int> ResolveRelationTargets(
        IReadOnlyList<LlmTarget> targets,
        LlmRequestDefaults defaults,
        LlmJsonApiAliasScope aliases,
        string jsonPath)
    {
        var ids = new List<int>();
        for (int i = 0; i < targets.Count; i++)
        {
            var resolved = aliases.ResolveTarget(targets[i], defaults, $"{jsonPath}[{i}]");
            ids.AddRange(resolved.Select(n => n.Id));
        }
        return ids;
    }

    private LlmResolvedNode? ResolveParent(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            var root = _graph.GetById(Node.RootId)!;
            return new LlmResolvedNode(Node.RootId, string.Empty, root);
        }

        var parentPath = path[..lastSlash];
        return _pathResolver.AllNodes.FirstOrDefault(n => string.Equals(n.Path, parentPath, StringComparison.Ordinal));
    }

    private PendingResult BuildPendingCreate(
        int index,
        LlmCreateOperation op,
        CompiledCreate create,
        int writeResultIndex)
    {
        var data = new JsonObject
        {
            ["resolved"] = new JsonObject
            {
                ["path"] = create.Path,
                ["parent_id"] = create.ParentId,
                ["coordinates"] = BuildCoordinates(create.Coordinates),
                ["refs"] = BuildRefs(create.Refs),
            },
        };
        return new PendingResult(new LlmOperationResult(index, op.Op, op.Alias, data), new[] { writeResultIndex }, create);
    }

    private static void ApplyWriteResults(PendingResult[] pending, WriteResult writeResult)
    {
        foreach (var result in pending)
        {
            if (result.WriteResultIndexes.Count == 0)
                continue;

            foreach (var writeResultIndex in result.WriteResultIndexes)
            {
                if (writeResultIndex >= writeResult.OpResults.Count)
                    throw new WriteApiException(
                        "internal_inconsistency",
                        $"WriteApi вернул {writeResult.OpResults.Count} результатов для {writeResultIndex + 1} write-операций.");

            }

            if (result.Create is null)
                continue;

            var createWriteResult = writeResult.OpResults[result.WriteResultIndexes[0]];
            var nodeId = createWriteResult.Data["id"]?.GetValue<int>();
            if (nodeId is int id)
                result.Result.Data["node"] = BuildCreatedNode(id, result.Create);
        }
    }

    private static void MarkApplyFailure(
        PendingResult[] pending,
        IReadOnlyList<int> writeIndexes,
        JsonObject validation)
    {
        if (writeIndexes.Count == 0)
            return;

        var target = pending[writeIndexes[0]].Result.Data;
        target["validation"] = validation;
    }

    private static LlmResolvedNode CreatePreviewNode(LlmCreateOperation op, int index, CompiledCreate create)
    {
        var node = new Node
        {
            Id = -(index + 1),
            TypeName = create.Coordinates.Type!.Name,
            Title = create.Title,
            Text = op.Set.Text ?? string.Empty,
            OutRefs = create.Refs,
            SourceFile = "<tx-preview>",
        };
        return new LlmResolvedNode(node.Id, create.Path, node);
    }

    private static LlmOperationResult BuildValidationFailure(
        int index,
        LlmOperation op,
        LlmJsonApiResolveException ex) =>
        new(index, op.Op, op.Alias, new JsonObject
        {
            ["validation"] = BuildValidation(false, ex.Code, ex.Message, ex.Path, ex.Details),
        });

    private static JsonObject BuildValidation(
        bool ok,
        string? code = null,
        string? message = null,
        string? path = null,
        JsonObject? details = null)
    {
        var result = new JsonObject { ["ok"] = ok };
        if (code is not null)
            result["code"] = code;
        if (path is not null)
            result["path"] = path;
        if (details is not null)
            result["details"] = details.DeepClone();
        return result;
    }

    private static JsonObject BuildValidationErrors(WriteValidationException ex)
    {
        var errors = new JsonArray();
        foreach (var error in ex.Errors)
        {
            errors.Add((JsonNode)new JsonObject
            {
                ["code"] = error.Code,
                ["node_id"] = error.NodeId,
                ["path"] = error.Path,
                ["ref"] = error.RefName,
            });
        }

        return new JsonObject { ["errors"] = errors };
    }

    private static JsonObject BuildCreatedNode(int id, CompiledCreate create) =>
        new()
        {
            ["id"] = id,
            ["path"] = create.Path,
            ["type"] = create.Coordinates.Type!.Name,
        };

    private static JsonObject BuildCoordinates(LlmResolvedCoordinates coordinates)
    {
        var result = new JsonObject
        {
            ["type"] = coordinates.Type!.Name,
        };
        foreach (var coordinate in coordinates.Classifiers)
            result[coordinate.Name] = coordinate.Path;
        return result;
    }

    private static JsonObject BuildRefs(IReadOnlyDictionary<string, IReadOnlyList<int>> refs)
    {
        var result = new JsonObject();
        foreach (var (name, targets) in refs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var array = new JsonArray();
            foreach (var target in targets)
                array.Add((JsonNode?)JsonValue.Create(target));
            result[name] = array;
        }
        return result;
    }

    private static JsonArray BuildIds(IReadOnlyList<LlmResolvedNode> nodes)
    {
        var ids = new JsonArray();
        foreach (var node in nodes)
            ids.Add((JsonNode?)JsonValue.Create(node.Id));
        return ids;
    }

    private static string PathTitle(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }

    private TypeDefinition ResolveNodeType(string typeName) =>
        _typesByName.TryGetValue(typeName, out var type)
            ? type
            : throw new LlmJsonApiResolveException(
                "unknown_coordinate",
                "$.type",
                $"Тип '{typeName}' не найден в текущей Схеме.",
                new JsonObject { ["type"] = typeName });

    private static JsonObject BuildSetSummary(LlmNodeSet set)
    {
        var coordinates = new JsonArray();
        foreach (var name in set.Coordinates.Values.Keys.OrderBy(name => name, StringComparer.Ordinal))
            coordinates.Add((JsonNode?)JsonValue.Create(name));

        var relations = new JsonObject();
        foreach (var (name, change) in set.Relations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            relations[name] = change.Mode?.ToString().ToLowerInvariant() ?? "short";

        return new JsonObject
        {
            ["text"] = set.Text is not null,
            ["coordinates"] = coordinates,
            ["relations"] = relations,
        };
    }

    private static JsonObject BuildLinkData(LinkEndpoints endpoints) =>
        new()
        {
            ["resolved"] = new JsonObject
            {
                ["from_id"] = endpoints.From.Id,
                ["from_path"] = endpoints.From.Path,
                ["name"] = endpoints.Name,
                ["to_id"] = endpoints.To.Id,
                ["to_path"] = endpoints.To.Path,
            },
        };

    private sealed record LinkEndpoints(
        LlmResolvedNode From,
        LlmResolvedNode To,
        string Name);

    private sealed record PendingResult(
        LlmOperationResult Result,
        IReadOnlyList<int> WriteResultIndexes,
        CompiledCreate? Create);

    private sealed record CompiledWrite(
        IReadOnlyList<WriteOp> Ops,
        JsonObject Data);

    private sealed record CompiledCreate(
        CreateNodeOp Op,
        string Path,
        string Title,
        int ParentId,
        LlmResolvedCoordinates Coordinates,
        IReadOnlyDictionary<string, IReadOnlyList<int>> Refs);
}
