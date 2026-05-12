using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiCoordinateResolverTests
{
    [Fact]
    public void FilterNodes_TypeCoordinate_UsesSchemaNodeType()
    {
        var (pathResolver, coordinateResolver) = BuildResolvers();
        var candidates = pathResolver.ResolvePath("TestDoc/Selectors/*", LlmRequestDefaults.Empty, "$.ops[0].select.path");

        var result = coordinateResolver.FilterNodes(
            candidates,
            Coordinates(("type", "rule")),
            LlmRequestDefaults.Empty,
            "$.ops[0].select.coordinates");

        Assert.Equal(new[] { 30, 31, 33 }, result.Select(n => n.Id).OrderBy(id => id));
    }

    [Fact]
    public void FilterNodes_ClassifierCoordinate_MatchesSubtree()
    {
        var (pathResolver, coordinateResolver) = BuildResolvers();
        var candidates = pathResolver.ResolvePath("TestDoc/Selectors/*", LlmRequestDefaults.Empty, "$.ops[0].select.path");

        var result = coordinateResolver.FilterNodes(
            candidates,
            Coordinates(("subject", "api")),
            LlmRequestDefaults.Empty,
            "$.ops[0].select.coordinates");

        Assert.Equal(new[] { 30, 31, 32 }, result.Select(n => n.Id).OrderBy(id => id));
    }

    [Fact]
    public void FilterNodes_DefaultCoordinates_MergeWithOperationCoordinates()
    {
        var (pathResolver, coordinateResolver) = BuildResolvers();
        var candidates = pathResolver.ResolvePath("TestDoc/Selectors/*", LlmRequestDefaults.Empty, "$.ops[0].select.path");
        var defaults = new LlmRequestDefaults(
            null,
            Coordinates(("type", "rule"), ("subject", "api")));

        var result = coordinateResolver.FilterNodes(
            candidates,
            Coordinates(("subsystem", "mcp")),
            defaults,
            "$.ops[0].select.coordinates");

        Assert.Equal(new[] { 30 }, result.Select(n => n.Id));
    }

    [Fact]
    public void FilterNodes_OperationCoordinate_OverridesDefaultType()
    {
        var (pathResolver, coordinateResolver) = BuildResolvers();
        var candidates = pathResolver.ResolvePath("TestDoc/Selectors/*", LlmRequestDefaults.Empty, "$.ops[0].select.path");
        var defaults = new LlmRequestDefaults(null, Coordinates(("type", "rule"), ("subject", "api")));

        var result = coordinateResolver.FilterNodes(
            candidates,
            Coordinates(("type", "statement")),
            defaults,
            "$.ops[0].select.coordinates");

        Assert.Equal(new[] { 32 }, result.Select(n => n.Id));
    }

    [Fact]
    public void ResolveSelector_CoordinatesOnly_UsesAllPathIndexedNodes()
    {
        var (pathResolver, coordinateResolver) = BuildResolvers();

        var result = coordinateResolver.ResolveSelector(
            new LlmSelector(null, Coordinates(("type", "statement"), ("subject", "api/read")), null),
            LlmRequestDefaults.Empty,
            pathResolver,
            "$.ops[0].select");

        Assert.Equal(new[] { 32 }, result.Select(n => n.Id));
        Assert.Equal("TestDoc/Selectors/S_api_read_mcp", result.Single().Path);
    }

    [Fact]
    public void Resolve_UnknownType_ThrowsUnknownCoordinate()
    {
        var (_, coordinateResolver) = BuildResolvers();

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            coordinateResolver.Resolve(
                Coordinates(("type", "missing")),
                LlmRequestDefaults.Empty,
                "$.ops[0].select.coordinates"));

        Assert.Equal("unknown_coordinate", ex.Code);
        Assert.Equal("$.ops[0].select.coordinates.type", ex.Path);
        Assert.DoesNotContain("candidates", ex.Details?.ToJsonString() ?? string.Empty);
    }

    [Fact]
    public void Resolve_UnknownClassifierPath_ThrowsUnknownCoordinate()
    {
        var (_, coordinateResolver) = BuildResolvers();

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            coordinateResolver.Resolve(
                Coordinates(("subject", "api/missing")),
                LlmRequestDefaults.Empty,
                "$.ops[0].select.coordinates"));

        Assert.Equal("unknown_coordinate", ex.Code);
        Assert.Equal("$.ops[0].select.coordinates.subject", ex.Path);
    }

    [Fact]
    public void Resolve_UnknownCoordinateName_ThrowsUnknownCoordinate()
    {
        var (_, coordinateResolver) = BuildResolvers();

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            coordinateResolver.Resolve(
                Coordinates(("unknown", "value")),
                LlmRequestDefaults.Empty,
                "$.ops[0].select.coordinates"));

        Assert.Equal("unknown_coordinate", ex.Code);
        Assert.Equal("$.ops[0].select.coordinates.unknown", ex.Path);
    }

    private static (LlmJsonApiPathResolver PathResolver, LlmJsonApiCoordinateResolver CoordinateResolver) BuildResolvers()
    {
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        return (
            new LlmJsonApiPathResolver(graph),
            new LlmJsonApiCoordinateResolver(graph, schema));
    }

    private static SchemaDocument BuildSchema()
    {
        var trees = new List<TreeDefinition>
        {
            new(TreeDefinition.PathTreeName, "Дерево хранения"),
            new("subject", "Предметный классификатор"),
            new("subsystem", "Подсистемный классификатор"),
        };

        var documentType = new TypeDefinition(
            "document", null, TitleSource.Filename, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "root" }, "path", Cardinality.One, true, null),
            });
        var sectionType = new TypeDefinition(
            "section", null, TitleSource.InlineKey, TextRequired: false,
            new List<RefDef>
            {
                new("path", new[] { "document" }, "path", Cardinality.One, true, null),
            });
        var categoryType = new TypeDefinition(
            "category", null, TitleSource.InlineKey, TextRequired: false,
            new List<RefDef>
            {
                new("path", new[] { "document" }, "path", Cardinality.One, true, null),
                new("subject_parent", new[] { "category", "root" }, "subject", Cardinality.One, true, null),
                new("subsystem_parent", new[] { "category", "root" }, "subsystem", Cardinality.One, true, null),
            });
        var ruleType = AtomType("rule");
        var statementType = AtomType("statement");

        return new SchemaDocument(
            "Synthetic LLM coordinate schema",
            trees,
            new List<TypeDefinition> { documentType, sectionType, categoryType, ruleType, statementType });
    }

    private static TypeDefinition AtomType(string name) =>
        new(
            name, null, TitleSource.InlineKey, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "section" }, "path", Cardinality.One, true, null),
                new("subject", new[] { "category" }, "subject", Cardinality.One, true, null),
                new("subsystem", new[] { "category" }, "subsystem", Cardinality.One, true, null),
            });

    private static GraphModel BuildGraph(SchemaDocument schema)
    {
        var graph = new GraphModel();
        graph.AttachSchema(schema);

        Add(graph, 1, "document", "TestDoc", "desc", new() { ["path"] = new[] { Node.RootId } });
        Add(graph, 2, "section", "Selectors", "", new() { ["path"] = new[] { 1 } });

        Add(graph, 10, "category", "api", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });
        Add(graph, 11, "category", "other", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });
        Add(graph, 12, "category", "read", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { 10 },
            ["subsystem_parent"] = new[] { Node.RootId },
        });
        Add(graph, 20, "category", "mcp", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });
        Add(graph, 21, "category", "cli", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });

        Add(graph, 30, "rule", "R_api_read_mcp", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 12 },
            ["subsystem"] = new[] { 20 },
        });
        Add(graph, 31, "rule", "R_api_cli", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 21 },
        });
        Add(graph, 32, "statement", "S_api_read_mcp", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 12 },
            ["subsystem"] = new[] { 20 },
        });
        Add(graph, 33, "rule", "R_other_mcp", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 11 },
            ["subsystem"] = new[] { 20 },
        });

        return graph;
    }

    private static void Add(
        GraphModel graph,
        int id,
        string typeName,
        string title,
        string text,
        Dictionary<string, int[]> refs)
    {
        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (name, targets) in refs)
            outRefs[name] = targets;
        graph.Add(new Node
        {
            Id = id,
            TypeName = typeName,
            Title = title,
            Text = text,
            OutRefs = outRefs,
            SourceFile = "test.yml",
        });
    }

    private static LlmCoordinates Coordinates(params (string Name, string Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));
}
