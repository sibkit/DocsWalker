using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

/// <summary>
/// Покрытие <see cref="ReadApi.Find"/> — структурный поиск по пересечению
/// multi-tree-фильтров. Synthetic schema: два classifier-дерева
/// (<c>subject</c> и <c>subsystem</c>) + 4 категории + 4 rule-узла
/// с разными комбинациями классификаторов.
/// </summary>
public class FindTests
{
    private static ReadApi BuildApi()
    {
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        return new ReadApi(graph, schema);
    }

    private static SchemaDocument BuildSchema()
    {
        var trees = new List<TreeDefinition>
        {
            new(TreeDefinition.PathTreeName, "Дерево хранилища"),
            new("subject", "Классификатор по предметной оси"),
            new("subsystem", "Классификатор по подсистеме"),
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
                new("path", new[] { "document", "section" }, "path", Cardinality.One, true, null),
            });
        var categoryType = new TypeDefinition(
            "category", null, TitleSource.InlineKey, TextRequired: false,
            new List<RefDef>
            {
                new("path", new[] { "document", "section", "category" }, "path", Cardinality.One, true, null),
                new("subject_parent", new[] { "category", "root" }, "subject", Cardinality.One, true, null),
                new("subsystem_parent", new[] { "category", "root" }, "subsystem", Cardinality.One, true, null),
            });
        var ruleType = new TypeDefinition(
            "rule", null, TitleSource.InlineKey, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "section" }, "path", Cardinality.One, true, null),
                new("subject", new[] { "category" }, "subject", Cardinality.One, true, null),
                new("subsystem", new[] { "category" }, "subsystem", Cardinality.One, true, null),
            });
        return new SchemaDocument(
            "Synthetic test schema",
            trees,
            new List<TypeDefinition> { documentType, sectionType, categoryType, ruleType });
    }

    private static GraphModel BuildGraph(SchemaDocument schema)
    {
        var g = new GraphModel();
        g.AttachSchema(schema);

        Add(g, 1, "document", "TestDoc", "desc", new() { ["path"] = new[] { Node.RootId } });
        Add(g, 2, "section", "S1", "", new() { ["path"] = new[] { 1 } });

        // Категории subject под root: api (id=10), other (id=11)
        Add(g, 10, "category", "api", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });
        Add(g, 11, "category", "other", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });

        // Категории subsystem под root: mcp (id=20), cli (id=21)
        Add(g, 20, "category", "mcp", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });
        Add(g, 21, "category", "cli", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
            ["subsystem_parent"] = new[] { Node.RootId },
        });

        // rule с subject=api, subsystem=mcp
        Add(g, 30, "rule", "R_api_mcp", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 20 },
        });
        // rule с subject=api, subsystem=cli
        Add(g, 31, "rule", "R_api_cli", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 21 },
        });
        // rule с subject=other, subsystem=mcp
        Add(g, 32, "rule", "R_other_mcp", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 11 },
            ["subsystem"] = new[] { 20 },
        });
        // rule с subject=other, subsystem=cli
        Add(g, 33, "rule", "R_other_cli", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 11 },
            ["subsystem"] = new[] { 21 },
        });
        return g;
    }

    private static void Add(
        GraphModel g, int id, string typeName, string title, string text,
        Dictionary<string, int[]> refs)
    {
        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (k, v) in refs) outRefs[k] = v;
        g.Add(new Node
        {
            Id = id,
            TypeName = typeName,
            Title = title,
            Text = text,
            OutRefs = outRefs,
            SourceFile = "test.yml",
        });
    }

    [Fact]
    public void Find_EmptyInTree_Throws_InvalidParameter()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() =>
            api.Find(Array.Empty<TreeFilter>()));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    [Fact]
    public void Find_SingleFilter_UnderApi_ReturnsAllRulesWithSubjectApi()
    {
        var api = BuildApi();
        var result = api.Find(new[] { new TreeFilter("subject", 10) });

        // Узлы с subject=api: rule 30, 31, и сам узел api (10) попадает,
        // т.к. CollectScopeDescendants включает корень.
        // category=api имеет subject_parent=root, не subject=api → не попадает.
        // Но category=10 имеет subject_parent=0, она не имеет subject-ref в subtree under=10.
        // Только rule 30, 31 имеют subject → 10.
        var ids = result.Select(n => n.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 30, 31 }, ids);
    }

    [Fact]
    public void Find_TwoFilters_Intersection()
    {
        var api = BuildApi();
        var result = api.Find(new[]
        {
            new TreeFilter("subject", 10),    // api
            new TreeFilter("subsystem", 20),  // mcp
        });
        var ids = result.Select(n => n.Id).ToArray();
        Assert.Equal(new[] { 30 }, ids);
    }

    [Fact]
    public void Find_TypeFilter_LimitsByType()
    {
        var api = BuildApi();
        var result = api.Find(
            new[] { new TreeFilter("subject", 10) },
            typeFilter: "rule");
        var ids = result.Select(n => n.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 30, 31 }, ids);
        Assert.All(result, n => Assert.Equal("rule", n.TypeName));
    }

    [Fact]
    public void Find_TypeFilter_NoMatch_ReturnsEmpty()
    {
        var api = BuildApi();
        var result = api.Find(
            new[] { new TreeFilter("subject", 10) },
            typeFilter: "section");
        Assert.Empty(result);
    }

    [Fact]
    public void Find_Limit_TruncatesResult()
    {
        var api = BuildApi();
        var result = api.Find(
            new[] { new TreeFilter("subject", 10) },
            limit: 1);
        Assert.Single(result);
        Assert.Equal(30, result[0].Id);
    }

    [Fact]
    public void Find_PathTreeName_Rejected()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() =>
            api.Find(new[] { new TreeFilter(Node.PathRefName, 1) }));
        Assert.Equal("invalid_parameter", ex.Code);
        Assert.Contains("path", ex.Message);
    }

    [Fact]
    public void Find_UnknownTree_Throws()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() =>
            api.Find(new[] { new TreeFilter("nonexistent", 1) }));
        // ValidateTree выдаёт unknown_tree.
        Assert.Contains("unknown", ex.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Find_UnderNotFound_Throws()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() =>
            api.Find(new[] { new TreeFilter("subject", 9999) }));
        Assert.Equal("node_not_found", ex.Code);
    }

    [Fact]
    public void FindToJson_Compact_ReturnsIdTypeTitleOnly()
    {
        var api = BuildApi();
        var result = api.Find(new[] { new TreeFilter("subject", 10) });
        var json = ReadApiJson.FindToJson(result, compact: true);
        Assert.NotEmpty(json);
        foreach (var item in json)
        {
            var obj = (System.Text.Json.Nodes.JsonObject)item!;
            Assert.True(obj.ContainsKey("id"));
            Assert.True(obj.ContainsKey("type"));
            Assert.True(obj.ContainsKey("title"));
            Assert.False(obj.ContainsKey("text"));
            Assert.False(obj.ContainsKey("out_refs"));
        }
    }

    [Fact]
    public void FindToJson_Full_IncludesTextAndOutRefs()
    {
        var api = BuildApi();
        var result = api.Find(new[] { new TreeFilter("subject", 10) });
        var json = ReadApiJson.FindToJson(result, compact: false);
        Assert.NotEmpty(json);
        foreach (var item in json)
        {
            var obj = (System.Text.Json.Nodes.JsonObject)item!;
            Assert.True(obj.ContainsKey("id"));
            Assert.True(obj.ContainsKey("type"));
            Assert.True(obj.ContainsKey("title"));
            Assert.True(obj.ContainsKey("text"));
            Assert.True(obj.ContainsKey("out_refs"));
        }
    }

}
