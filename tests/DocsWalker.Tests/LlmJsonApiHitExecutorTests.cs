using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiHitExecutorTests
{
    [Fact]
    public void Execute_Select_ReturnsPreviewMetricsWithoutText()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "hit",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "coordinates": { "type": "rule" }
                  },
                  "max_tokens": 10000
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var data = result.Single().Data;
        Assert.Equal(2, data["count"]!.GetValue<int>());
        Assert.True(data["tokens"]!.GetValue<int>() > 0);
        Assert.True(data["subtree_tokens"]!.GetValue<int>() >= data["tokens"]!.GetValue<int>());
        Assert.True(data["within_budget"]!.GetValue<bool>());
        Assert.Equal(2, data["breakdown_by_type"]!["rule"]!.GetValue<int>());

        var samples = data["samples"]!.AsArray();
        Assert.Equal(2, samples.Count);
        var first = samples[0]!.AsObject();
        Assert.Equal(30, first["id"]!.GetValue<int>());
        Assert.Equal("rule", first["coordinates"]!["type"]!.GetValue<string>());
        Assert.False(first.ContainsKey("text"));
    }

    [Fact]
    public void Execute_SelectAliasThenUpdate_ReportsWouldChange()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "hit",
              "ops": [
                {
                  "op": "select",
                  "as": "rules",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "coordinates": { "type": "rule" }
                  }
                },
                {
                  "op": "update",
                  "target": "$rules",
                  "expected_count": 2,
                  "set": { "text": "updated" }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var data = result[1].Data;
        Assert.Equal(2, data["count"]!.GetValue<int>());
        Assert.True(data["validation"]!["ok"]!.GetValue<bool>());
        var wouldChange = data["would_change"]!.AsObject();
        Assert.Equal("update", wouldChange["op"]!.GetValue<string>());
        Assert.Equal(2, wouldChange["count"]!.GetValue<int>());
        Assert.Equal(new[] { 30, 31 }, wouldChange["ids"]!.AsArray().Select(id => id!.GetValue<int>()));
        Assert.True(wouldChange["set"]!["text"]!.GetValue<bool>());
    }

    [Fact]
    public void Execute_UpdateExpectedCountMismatch_ReturnsValidationFalse()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "hit",
              "ops": [
                {
                  "op": "update",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "coordinates": { "type": "rule" }
                  },
                  "expected_count": 3,
                  "set": { "text": "updated" }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var validation = result.Single().Data["validation"]!.AsObject();
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("count_mismatch", validation["code"]!.GetValue<string>());
        Assert.Equal(3, validation["details"]!["expected_count"]!.GetValue<int>());
        Assert.Equal(2, validation["details"]!["actual_count"]!.GetValue<int>());
    }

    [Fact]
    public void Execute_CreateAlias_CanBeUsedByLaterLinkPreview()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "hit",
              "defaults": {
                "coordinates": { "type": "statement" }
              },
              "ops": [
                {
                  "op": "create",
                  "as": "created",
                  "path": "TestDoc/Selectors/New_statement",
                  "set": { "text": "new" }
                },
                {
                  "op": "link",
                  "from": "$created",
                  "relation": "related",
                  "to": { "path": "TestDoc/Selectors/A_rule" }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var createData = result[0].Data;
        Assert.True(createData["validation"]!["ok"]!.GetValue<bool>());
        Assert.Equal(1, createData["count"]!.GetValue<int>());
        Assert.Equal("TestDoc/Selectors/New_statement", createData["would_change"]!["path"]!.GetValue<string>());

        var linkData = result[1].Data;
        Assert.True(linkData["validation"]!["ok"]!.GetValue<bool>());
        var wouldChange = linkData["would_change"]!.AsObject();
        Assert.Equal("link", wouldChange["op"]!.GetValue<string>());
        Assert.Equal("TestDoc/Selectors/New_statement", wouldChange["from_path"]!.GetValue<string>());
        Assert.Equal(30, wouldChange["to_id"]!.GetValue<int>());
    }

    [Fact]
    public void Execute_CreateExistingPath_ReturnsAlreadyExistsValidation()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "hit",
              "defaults": {
                "coordinates": { "type": "rule" }
              },
              "ops": [
                {
                  "op": "create",
                  "path": "TestDoc/Selectors/A_rule",
                  "set": { "text": "new" }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var validation = result.Single().Data["validation"]!.AsObject();
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("already_exists", validation["code"]!.GetValue<string>());
    }

    private static LlmJsonApiHitExecutor BuildExecutor()
    {
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        return new LlmJsonApiHitExecutor(graph, schema);
    }

    private static LlmRequest Parse(string json) =>
        LlmJsonApiParser.Parse(JsonNode.Parse(json));

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

        return new SchemaDocument(
            "Synthetic LLM hit schema",
            trees,
            new List<TypeDefinition>
            {
                documentType,
                sectionType,
                categoryType,
                AtomType("rule", "section"),
                AtomType("statement", "section"),
                AtomType("example", "rule"),
            });
    }

    private static TypeDefinition AtomType(string name, string parentType) =>
        new(
            name, null, TitleSource.InlineKey, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { parentType }, "path", Cardinality.One, true, null),
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

        Add(graph, 30, "rule", "A_rule", "rule text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 20 },
        });
        Add(graph, 31, "rule", "B_rule", "rule text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 21 },
        });
        Add(graph, 32, "statement", "S_statement", "statement text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 11 },
            ["subsystem"] = new[] { 20 },
        });
        Add(graph, 40, "example", "A_example", "example text", new()
        {
            ["path"] = new[] { 30 },
            ["subject"] = new[] { 10 },
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
}
