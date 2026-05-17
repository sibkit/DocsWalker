using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiQueryExecutorTests
{
    [Fact]
    public void Execute_Select_ReturnsCompactNodesByDefault()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/A_rule"
                  }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var data = result.Single().Data;
        Assert.Equal(1, data["count"]!.GetValue<int>());
        Assert.False(data["truncated"]!.GetValue<bool>());

        var node = data["nodes"]!.AsArray()[0]!.AsObject();
        Assert.Equal(30, node["id"]!.GetValue<int>());
        Assert.Equal("TestDoc/Selectors/A_rule", node["path"]!.GetValue<string>());
        Assert.Equal("rule", node["type"]!.GetValue<string>());
        Assert.True(node["tokens"]!.GetValue<int>() > 0);
        Assert.False(node.ContainsKey("coordinates"));
        Assert.False(node.ContainsKey("title"));
        Assert.False(node.ContainsKey("text"));
        Assert.False(node.ContainsKey("relations"));
    }

    [Fact]
    public void Execute_SelectWithIncludes_ReturnsExpandedContext()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/A_rule",
                    "coordinates": { "type": "rule" }
                  },
                  "include": [
                    "text",
                    "relations",
                    "coordinates"
                  ]
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var node = result.Single().Data["nodes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("rule text", node["text"]!.GetValue<string>());
        Assert.Equal("api", node["coordinates"]!["subject"]!.GetValue<string>());
        Assert.Equal("mcp", node["coordinates"]!["subsystem"]!.GetValue<string>());

        var outRelations = node["relations"]!["out"]!.AsObject();
        Assert.False(outRelations.ContainsKey("path"));
        Assert.False(outRelations.ContainsKey("subject"));
        Assert.Equal(40, outRelations["examples"]!.AsArray()[0]!["id"]!.GetValue<int>());
    }

    [Fact]
    public void Execute_SelectWithTinyMaxTokens_TruncatesReturnedNodes()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "coordinates": { "type": "rule" }
                  },
                  "include": [ "text" ],
                  "max_tokens": 1
                }
              ]
            }
            """);

        var data = executor.Execute(request).Single().Data;

        Assert.Equal(2, data["count"]!.GetValue<int>());
        Assert.True(data["truncated"]!.GetValue<bool>());
        Assert.Equal(30, data["stopped_at"]!.GetValue<int>());
        Assert.Equal(2, data["omitted_count"]!.GetValue<int>());
        Assert.Empty(data["nodes"]!.AsArray());
        Assert.False(data.ContainsKey("returned"));
        Assert.False(data.ContainsKey("within_budget"));
        Assert.False(data.ContainsKey("tokens_budget"));
    }

    [Fact]
    public void Execute_SelectMatchRegex_FiltersByTitleAndText()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "match": {
                      "regex": "RULE"
                    }
                  }
                }
              ]
            }
            """);

        var data = executor.Execute(request).Single().Data;

        Assert.Equal(2, data["count"]!.GetValue<int>());
        Assert.False(data["truncated"]!.GetValue<bool>());

        var first = data["nodes"]!.AsArray()[0]!.AsObject();
        Assert.Equal(30, first["id"]!.GetValue<int>());
        Assert.Equal("TestDoc/Selectors/A_rule", first["path"]!.GetValue<string>());
        Assert.Equal("rule", first["type"]!.GetValue<string>());
        Assert.True(first["tokens"]!.GetValue<int>() > 0);
        Assert.False(first.ContainsKey("title"));
    }

    [Fact]
    public void Execute_SelectMatchCaseSensitive_ReturnsValidationFailureWhenNoNodesMatch()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "match": {
                      "regex": "RULE",
                      "case_sensitive": true
                    }
                  }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("not_found", validation["code"]!.GetValue<string>());
        Assert.Equal("$.ops[0].select", validation["path"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_SelectMatchRegex_FiltersByScopeAndField()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "TestDoc/Selectors/**",
                    "coordinates": { "type": "rule" },
                    "match": {
                      "regex": "second\\s+rule",
                      "fields": [ "text" ]
                    }
                  },
                  "include": [ "text" ]
                }
              ]
            }
            """);

        var node = executor.Execute(request).Single().Data["nodes"]!.AsArray().Single()!.AsObject();

        Assert.Equal(31, node["id"]!.GetValue<int>());
        Assert.Contains("second rule", node["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_SelectMatchInvalidRegex_ReturnsValidationFailure()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "match": {
                      "regex": "["
                    }
                  }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_match_regex", validation["code"]!.GetValue<string>());
        Assert.Equal("$.ops[0].select.match.regex", validation["path"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_NonSelect_ReturnsValidationFailure()
    {
        var executor = BuildExecutor();
        var request = Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "delete",
                  "id": 30
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_op", validation["code"]!.GetValue<string>());
        Assert.Equal("$.ops[0].op", validation["path"]!.GetValue<string>());
    }

    private static LlmJsonApiQueryExecutor BuildExecutor()
    {
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        return new LlmJsonApiQueryExecutor(graph, schema);
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
            "Synthetic LLM query schema",
            trees,
            new List<TypeDefinition>
            {
                documentType,
                sectionType,
                categoryType,
                AtomType("rule", "section", new RefDef("examples", new[] { "example" }, null, Cardinality.Many, false, null)),
                AtomType("statement", "section"),
                AtomType("example", "rule"),
            });
    }

    private static TypeDefinition AtomType(string name, string parentType, params RefDef[] extraRefs)
    {
        var refs = new List<RefDef>
        {
            new("path", new[] { parentType }, "path", Cardinality.One, true, null),
            new("subject", new[] { "category" }, "subject", Cardinality.One, true, null),
            new("subsystem", new[] { "category" }, "subsystem", Cardinality.One, true, null),
        };
        refs.AddRange(extraRefs);

        return new TypeDefinition(name, null, TitleSource.InlineKey, TextRequired: true, refs);
    }

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
            ["examples"] = new[] { 40 },
        });
        Add(graph, 31, "rule", "B_rule", "second rule text", new()
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
