using System.Text.Json.Nodes;
using DocsWalker.Core.Schema;

namespace DocsWalker.Core.Api;

/// <summary>
/// Read-only executor метода scheme: возвращает машинный контракт Схемы без доступа к узлам графа.
/// </summary>
public sealed class LlmJsonApiSchemeExecutor
{
    private readonly SchemaDocument _schema;
    private readonly Dictionary<string, TypeDefinition> _typesByName;
    private readonly Dictionary<string, TreeDefinition> _treesByName;

    public LlmJsonApiSchemeExecutor(SchemaDocument schema)
    {
        _schema = schema;
        _typesByName = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _treesByName = schema.Trees.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<LlmOperationResult> Execute(LlmRequest request)
    {
        if (request.Method != LlmJsonApiMethod.Scheme)
            throw new LlmJsonApiResolveException(
                "invalid_method",
                "$.method",
                "LlmJsonApiSchemeExecutor принимает только method='scheme'.");

        var results = new List<LlmOperationResult>(request.Ops.Count);
        for (int i = 0; i < request.Ops.Count; i++)
        {
            var op = request.Ops[i];
            var jsonPath = $"$.ops[{i}]";
            try
            {
                results.Add(ExecuteOperation(op, i, jsonPath));
            }
            catch (LlmJsonApiResolveException ex)
            {
                results.Add(new LlmOperationResult(i, op.Op, op.Alias, BuildValidationFailureData(ex)));
            }
        }

        return results;
    }

    private LlmOperationResult ExecuteOperation(LlmOperation op, int index, string jsonPath) =>
        op switch
        {
            LlmSchemeGetOperation get => ExecuteGet(get, index, jsonPath),
            LlmSchemeDescribeTypeOperation describeType => ExecuteDescribeType(describeType, index, jsonPath),
            LlmSchemeDescribeTreeOperation describeTree => ExecuteDescribeTree(describeTree, index, jsonPath),
            _ => throw new LlmJsonApiResolveException(
                "invalid_op",
                $"{jsonPath}.op",
                "scheme в v1 принимает только op='get', op='describe_type' и op='describe_tree'."),
        };

    private LlmOperationResult ExecuteGet(LlmSchemeGetOperation op, int index, string jsonPath)
    {
        var include = SchemeInclude.Parse(op.Include, $"{jsonPath}.include");
        var types = ResolveTypes(op.TypeNames, $"{jsonPath}.type_names");
        var trees = ResolveTrees(op.TreeNames, $"{jsonPath}.tree_names");

        var data = new JsonObject
        {
            ["counts"] = new JsonObject
            {
                ["types_total"] = _schema.Types.Count,
                ["types_returned"] = types.Count,
                ["trees_total"] = _schema.Trees.Count,
                ["trees_returned"] = trees.Count,
            },
        };

        if (include.Description)
            data["description"] = _schema.Description;
        if (include.Trees)
            data["trees"] = BuildTrees(trees);
        if (include.Types)
            data["types"] = BuildTypes(types);

        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult ExecuteDescribeType(
        LlmSchemeDescribeTypeOperation op,
        int index,
        string jsonPath)
    {
        var type = ResolveType(op.Name, $"{jsonPath}.name");
        var data = BuildType(type);
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private LlmOperationResult ExecuteDescribeTree(
        LlmSchemeDescribeTreeOperation op,
        int index,
        string jsonPath)
    {
        var tree = ResolveTree(op.Name, $"{jsonPath}.name");
        var data = BuildTree(tree);
        data["referenced_by"] = BuildTreeReferences(tree.Name);
        return new LlmOperationResult(index, op.Op, op.Alias, data);
    }

    private IReadOnlyList<TypeDefinition> ResolveTypes(IReadOnlyList<string> names, string jsonPath)
    {
        if (names.Count == 0)
            return _schema.Types.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();

        var result = new List<TypeDefinition>(names.Count);
        for (int i = 0; i < names.Count; i++)
            result.Add(ResolveType(names[i], $"{jsonPath}[{i}]"));
        return result;
    }

    private IReadOnlyList<TreeDefinition> ResolveTrees(IReadOnlyList<string> names, string jsonPath)
    {
        if (names.Count == 0)
            return _schema.Trees.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();

        var result = new List<TreeDefinition>(names.Count);
        for (int i = 0; i < names.Count; i++)
            result.Add(ResolveTree(names[i], $"{jsonPath}[{i}]"));
        return result;
    }

    private TypeDefinition ResolveType(string name, string jsonPath) =>
        _typesByName.TryGetValue(name, out var type)
            ? type
            : throw new LlmJsonApiResolveException(
                "unknown_type",
                jsonPath,
                $"Тип '{name}' не найден в Схеме.",
                new JsonObject { ["name"] = name });

    private TreeDefinition ResolveTree(string name, string jsonPath) =>
        _treesByName.TryGetValue(name, out var tree)
            ? tree
            : throw new LlmJsonApiResolveException(
                "unknown_tree",
                jsonPath,
                $"Tree '{name}' не найден в Схеме.",
                new JsonObject { ["name"] = name });

    private static JsonArray BuildTypes(IEnumerable<TypeDefinition> types)
    {
        var array = new JsonArray();
        foreach (var type in types)
            array.Add((JsonNode)BuildType(type));
        return array;
    }

    private static JsonObject BuildType(TypeDefinition type)
    {
        var refs = new JsonArray();
        foreach (var rd in type.OutRefs.OrderBy(rd => rd.Name, StringComparer.Ordinal))
            refs.Add((JsonNode)BuildRef(rd));

        return new JsonObject
        {
            ["name"] = type.Name,
            ["description"] = type.Description,
            ["text_required"] = type.TextRequired,
            ["out_refs"] = refs,
        };
    }

    private static JsonObject BuildRef(RefDef rd)
    {
        var obj = new JsonObject
        {
            ["name"] = rd.Name,
            ["tree"] = rd.Tree,
            ["cardinality"] = rd.Tree is null ? FormatCardinality(rd.Cardinality) : null,
            ["required"] = rd.Tree is null ? rd.Required : null,
            ["target_types"] = BuildStringArray(rd.TargetTypes),
            ["description"] = rd.Description,
        };

        if (rd.Tree is not null)
            obj["unique_sibling_titles"] = rd.UniqueSiblingTitles;

        return obj;
    }

    private static JsonArray BuildTrees(IEnumerable<TreeDefinition> trees)
    {
        var array = new JsonArray();
        foreach (var tree in trees)
            array.Add((JsonNode)BuildTree(tree));
        return array;
    }

    private static JsonObject BuildTree(TreeDefinition tree) =>
        new()
        {
            ["name"] = tree.Name,
            ["description"] = tree.Description,
        };

    private JsonArray BuildTreeReferences(string treeName)
    {
        var array = new JsonArray();
        foreach (var type in _schema.Types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            foreach (var rd in type.OutRefs
                         .Where(rd => string.Equals(rd.Tree, treeName, StringComparison.Ordinal))
                         .OrderBy(rd => rd.Name, StringComparer.Ordinal))
            {
                array.Add((JsonNode)new JsonObject
                {
                    ["type"] = type.Name,
                    ["ref"] = rd.Name,
                });
            }
        }

        return array;
    }

    private static JsonArray BuildStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }

    private static string FormatCardinality(Cardinality cardinality) =>
        cardinality == Cardinality.One ? "one" : "many";

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

    private static JsonObject BuildValidationFailureData(LlmJsonApiResolveException ex) =>
        new()
        {
            ["validation"] = BuildValidation(false, ex.Code, ex.Message, ex.Path, ex.Details),
        };

    private sealed record SchemeInclude(bool Description, bool Trees, bool Types)
    {
        public static SchemeInclude Parse(IReadOnlyList<string> values, string jsonPath)
        {
            if (values.Count == 0)
                return new SchemeInclude(true, true, true);

            var set = values.ToHashSet(StringComparer.Ordinal);
            foreach (var value in set)
            {
                if (!Allowed.Contains(value))
                {
                    throw new LlmJsonApiResolveException(
                        "invalid_include",
                        jsonPath,
                        $"scheme.include содержит неизвестное поле '{value}'.",
                        new JsonObject { ["include"] = value });
                }
            }

            return new SchemeInclude(
                set.Contains("description"),
                set.Contains("trees"),
                set.Contains("types"));
        }

        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "description",
            "trees",
            "types",
        };
    }
}
