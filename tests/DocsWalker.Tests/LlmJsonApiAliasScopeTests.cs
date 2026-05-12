using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiAliasScopeTests
{
    [Fact]
    public void ResolveTarget_AliasBeforeDeclaration_ThrowsUnknownAlias()
    {
        var (_, _, scope) = BuildScope();

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            scope.ResolveTarget(
                LlmTarget.Empty with { Alias = "later" },
                LlmRequestDefaults.Empty,
                "$.ops[0].target"));

        Assert.Equal("unknown_alias", ex.Code);
        Assert.Equal("$.ops[0].target", ex.Path);
        Assert.Equal("later", ex.Details?["alias"]?.GetValue<string>());
    }

    [Fact]
    public void DeclareSelectAlias_MakesAliasAvailableToLaterTargets()
    {
        var (pathResolver, coordinateResolver, scope) = BuildScope();
        var select = new LlmSelectOperation(
            new LlmSelector(
                "TestDoc/Selectors/*",
                Coordinates(("type", "rule")),
                null),
            Array.Empty<string>(),
            null,
            "rules");

        var selected = coordinateResolver.ResolveSelector(
            select.Select,
            LlmRequestDefaults.Empty,
            pathResolver,
            "$.ops[0].select");

        scope.DeclareSelectAlias(select, selected, 0, "$.ops[0].as");

        var resolved = scope.ResolveTarget(
            LlmTarget.Empty with { Alias = "rules" },
            LlmRequestDefaults.Empty,
            "$.ops[1].target");

        Assert.Equal(new[] { 30, 31 }, resolved.Select(n => n.Id));
        var binding = scope.ResolveAlias("rules", "$.ops[1].target");
        Assert.Equal(0, binding.OperationIndex);
        Assert.Equal(LlmOperationKind.Select, binding.OperationKind);
    }

    [Fact]
    public void DeclareCreateAlias_StoresSingleCreatedNode()
    {
        var (pathResolver, _, scope) = BuildScope();
        var create = new LlmCreateOperation(
            "TestDoc/Selectors/Created_statement",
            LlmNodeSet.Empty,
            "created");
        var createdNode = pathResolver.ResolveExistingId(32, "$.ops[0]");

        scope.DeclareCreateAlias(create, createdNode, 0, "$.ops[0].as");

        var resolved = scope.ResolveSingleTarget(
            LlmTarget.Empty with { Alias = "created" },
            LlmRequestDefaults.Empty,
            "$.ops[1].target");

        Assert.Equal(32, resolved.Id);
        Assert.Equal("TestDoc/Selectors/Created_statement", resolved.Path);
    }

    [Fact]
    public void ResolveSingleTarget_AliasSetWithManyNodes_ThrowsAmbiguousSelector()
    {
        var (pathResolver, coordinateResolver, scope) = BuildScope();
        var select = new LlmSelectOperation(
            new LlmSelector("TestDoc/Selectors/*", Coordinates(("type", "rule")), null),
            Array.Empty<string>(),
            null,
            "rules");
        var selected = coordinateResolver.ResolveSelector(
            select.Select,
            LlmRequestDefaults.Empty,
            pathResolver,
            "$.ops[0].select");
        scope.DeclareSelectAlias(select, selected, 0, "$.ops[0].as");

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            scope.ResolveSingleTarget(
                LlmTarget.Empty with { Alias = "rules" },
                LlmRequestDefaults.Empty,
                "$.ops[1].target"));

        Assert.Equal("ambiguous_selector", ex.Code);
        Assert.Equal("$.ops[1].target", ex.Path);
    }

    [Fact]
    public void ResolveTarget_DelegatesIdPathAndSelectorTargets()
    {
        var (_, _, scope) = BuildScope();

        var byId = scope.ResolveTarget(
            LlmTarget.Empty with { Id = 30 },
            LlmRequestDefaults.Empty,
            "$.ops[0].target");
        var byPath = scope.ResolveTarget(
            LlmTarget.Empty with { Path = "TestDoc/Selectors/B_rule" },
            LlmRequestDefaults.Empty,
            "$.ops[1].target");
        var bySelector = scope.ResolveTarget(
            LlmTarget.Empty with
            {
                Select = new LlmSelector(
                    "TestDoc/Selectors/*",
                    Coordinates(("type", "statement")),
                    null),
            },
            LlmRequestDefaults.Empty,
            "$.ops[2].target");

        Assert.Equal(new[] { 30 }, byId.Select(n => n.Id));
        Assert.Equal(new[] { 31 }, byPath.Select(n => n.Id));
        Assert.Equal(new[] { 32 }, bySelector.Select(n => n.Id));
    }

    private static (LlmJsonApiPathResolver PathResolver, LlmJsonApiCoordinateResolver CoordinateResolver, LlmJsonApiAliasScope Scope) BuildScope()
    {
        var schema = BuildSchema();
        var graph = BuildGraph();
        var pathResolver = new LlmJsonApiPathResolver(graph);
        var coordinateResolver = new LlmJsonApiCoordinateResolver(graph, schema);
        return (pathResolver, coordinateResolver, new LlmJsonApiAliasScope(pathResolver, coordinateResolver));
    }

    private static SchemaDocument BuildSchema()
    {
        var trees = new List<TreeDefinition>
        {
            new(TreeDefinition.PathTreeName, "Дерево хранения"),
        };

        return new SchemaDocument(
            "Synthetic LLM alias schema",
            trees,
            new List<TypeDefinition>
            {
                Type("document", "root"),
                Type("section", "document"),
                Type("rule", "section"),
                Type("statement", "section"),
            });
    }

    private static TypeDefinition Type(string name, string parentType) =>
        new(
            name,
            null,
            TitleSource.InlineKey,
            TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { parentType }, "path", Cardinality.One, true, null),
            });

    private static GraphModel BuildGraph()
    {
        var graph = new GraphModel();
        graph.Add(MakeNode(1, "document", "TestDoc", Node.RootId));
        graph.Add(MakeNode(2, "section", "Selectors", 1));
        graph.Add(MakeNode(30, "rule", "A_rule", 2));
        graph.Add(MakeNode(31, "rule", "B_rule", 2));
        graph.Add(MakeNode(32, "statement", "Created_statement", 2));
        return graph;
    }

    private static Node MakeNode(int id, string typeName, string title, int parentId) =>
        new()
        {
            Id = id,
            TypeName = typeName,
            Title = title,
            Text = "text",
            OutRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                [Node.PathRefName] = new[] { parentId },
            },
            SourceFile = $"node-{id}.yml",
        };

    private static LlmCoordinates Coordinates(params (string Name, string Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));
}
