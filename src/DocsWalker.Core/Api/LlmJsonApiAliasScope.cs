using System.Text.Json.Nodes;

namespace DocsWalker.Core.Api;

public sealed record LlmAliasBinding(
    string Name,
    IReadOnlyList<LlmResolvedNode> Nodes,
    int OperationIndex,
    LlmOperationKind OperationKind);

/// <summary>
/// Хранит alias-ы LLM JSON API в порядке выполнения ops[].
/// Scope намеренно изменяемый: вызывающий код объявляет alias только после успешного
/// resolve/apply соответствующей операции, поэтому ссылки на будущие alias остаются unknown_alias.
/// </summary>
public sealed class LlmJsonApiAliasScope
{
    private readonly LlmJsonApiPathResolver _pathResolver;
    private readonly LlmJsonApiCoordinateResolver _coordinateResolver;
    private readonly Dictionary<string, LlmAliasBinding> _aliases = new(StringComparer.Ordinal);

    public LlmJsonApiAliasScope(
        LlmJsonApiPathResolver pathResolver,
        LlmJsonApiCoordinateResolver coordinateResolver)
    {
        _pathResolver = pathResolver;
        _coordinateResolver = coordinateResolver;
    }

    public IReadOnlyDictionary<string, LlmAliasBinding> Aliases => _aliases;

    public void DeclareSelectAlias(
        LlmSelectOperation operation,
        IReadOnlyList<LlmResolvedNode> nodes,
        int operationIndex,
        string jsonPath)
    {
        DeclareAlias(operation.Alias, nodes, operationIndex, operation.Kind, jsonPath);
    }

    public void DeclareCreateAlias(
        LlmCreateOperation operation,
        LlmResolvedNode node,
        int operationIndex,
        string jsonPath)
    {
        DeclareAlias(operation.Alias, new[] { node }, operationIndex, operation.Kind, jsonPath);
    }

    public void DeclareAlias(
        string? alias,
        IReadOnlyList<LlmResolvedNode> nodes,
        int operationIndex,
        LlmOperationKind operationKind,
        string jsonPath)
    {
        if (alias is null)
            return;
        if (string.IsNullOrWhiteSpace(alias))
            throw UnknownAlias(jsonPath, alias);

        _aliases[alias] = new LlmAliasBinding(
            alias,
            nodes.ToArray(),
            operationIndex,
            operationKind);
    }

    public LlmAliasBinding ResolveAlias(string alias, string jsonPath)
    {
        if (_aliases.TryGetValue(alias, out var binding))
            return binding;

        throw UnknownAlias(jsonPath, alias);
    }

    public IReadOnlyList<LlmResolvedNode> ResolveTarget(
        LlmTarget target,
        LlmRequestDefaults defaults,
        string jsonPath)
    {
        if (target.Alias is not null)
            return ResolveAlias(target.Alias, jsonPath).Nodes;

        if (target.Select is not null)
            return _coordinateResolver.ResolveSelector(target.Select, defaults, _pathResolver, jsonPath);

        if (target.Ids.Count > 0)
            return target.Ids
                .Select((id, index) => _pathResolver.ResolveExistingId(id, $"{jsonPath}.ids[{index}]"))
                .ToArray();

        if (target.Id is int id)
            return new[] { _pathResolver.ResolveExistingId(id, $"{jsonPath}.id") };

        if (target.Path is not null)
            return _pathResolver.ResolvePath(target.Path, defaults, jsonPath);

        throw new LlmJsonApiResolveException(
            "not_found",
            jsonPath,
            "Target должен содержать id, ids, path, target alias или selector.");
    }

    public LlmResolvedNode ResolveSingleTarget(
        LlmTarget target,
        LlmRequestDefaults defaults,
        string jsonPath)
    {
        var nodes = ResolveTarget(target, defaults, jsonPath);
        if (nodes.Count == 1)
            return nodes[0];

        throw new LlmJsonApiResolveException(
            "ambiguous_selector",
            jsonPath,
            $"Target нашел {nodes.Count} узлов, но операция требует одну цель.");
    }

    private static LlmJsonApiResolveException UnknownAlias(string jsonPath, string alias) =>
        new(
            "unknown_alias",
            jsonPath,
            $"Alias '${alias}' не объявлен в предыдущих ops[].",
            new JsonObject
            {
                ["alias"] = alias,
            });
}
