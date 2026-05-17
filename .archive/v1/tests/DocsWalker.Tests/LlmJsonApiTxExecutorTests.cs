using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiTxExecutorTests
{
    [Fact]
    public void Execute_Create_CompilesCreateNodeOpAndApplies()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "defaults": {
                "path_parent": "TestDoc/Selectors",
                "coordinates": {
                  "type": "rule",
                  "subject": "api",
                  "subsystem": "mcp"
                }
              },
              "ops": [
                {
                  "op": "create",
                  "path": "New_rule",
                  "set": {
                    "text": "new rule text",
                    "relations": {
                      "examples": [ 40 ]
                    }
                  }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        var op = Assert.IsType<CreateNodeOp>(Assert.Single(capture.Ops));
        Assert.Equal("rule", op.TypeName);
        Assert.Equal("New_rule", op.Title);
        Assert.Equal("new rule text", op.Text);
        Assert.Equal(new[] { 2 }, op.Refs["path"]);
        Assert.Equal(new[] { 10 }, op.Refs["subject"]);
        Assert.Equal(new[] { 20 }, op.Refs["subsystem"]);
        Assert.Equal(new[] { 40 }, op.Refs["examples"]);

        var data = result.Single().Data;
        Assert.False(data.ContainsKey("validation"));
        Assert.False(data.ContainsKey("applied"));
        Assert.False(data.ContainsKey("write_results"));
        Assert.False(data.ContainsKey("write_result"));
        Assert.Equal(50, data["node"]!["id"]!.GetValue<int>());
        Assert.Equal("TestDoc/Selectors/New_rule", data["node"]!["path"]!.GetValue<string>());
        Assert.Equal("rule", data["node"]!["type"]!.GetValue<string>());
        Assert.False(data["resolved"]!.AsObject().ContainsKey("title"));
    }

    [Fact]
    public void Execute_SelectAliasThenCreateRelation_ResolvesAliasIntoRefs()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "select",
                  "as": "sample",
                  "select": { "path": "TestDoc/Selectors/A_example" }
                },
                {
                  "op": "create",
                  "path": "TestDoc/Selectors/New_rule",
                  "set": {
                    "text": "new rule text",
                    "coordinates": {
                      "type": "rule",
                      "subject": "api",
                      "subsystem": "mcp"
                    },
                    "relations": {
                      "examples": [ "$sample" ]
                    }
                  }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.Equal(1, result[0].Data["count"]!.GetValue<int>());
        var op = Assert.IsType<CreateNodeOp>(Assert.Single(capture.Ops));
        Assert.Equal(new[] { 40 }, op.Refs["examples"]);
    }

    [Fact]
    public void Execute_CreateExistingPath_ReturnsAlreadyExistsAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "defaults": {
                "coordinates": {
                  "type": "rule",
                  "subject": "api",
                  "subsystem": "mcp"
                }
              },
              "ops": [
                {
                  "op": "create",
                  "path": "TestDoc/Selectors/A_example",
                  "set": {
                    "text": "new",
                    "relations": { "examples": [ 40 ] }
                  }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("already_exists", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_CreateMissingParent_ReturnsPathParentNotFoundAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "defaults": {
                "coordinates": {
                  "type": "rule",
                  "subject": "api",
                  "subsystem": "mcp"
                }
              },
              "ops": [
                {
                  "op": "create",
                  "path": "TestDoc/Missing/New_rule",
                  "set": {
                    "text": "new",
                    "relations": { "examples": [ 40 ] }
                  }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("path_parent_not_found", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_CreateMissingRequiredCoordinate_ReturnsMissingRequiredRefAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "defaults": {
                "coordinates": {
                  "type": "rule",
                  "subject": "api"
                }
              },
              "ops": [
                {
                  "op": "create",
                  "path": "TestDoc/Selectors/New_rule",
                  "set": {
                    "text": "new",
                    "relations": { "examples": [ 40 ] }
                  }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("missing_required_ref", validation["code"]!.GetValue<string>());
        Assert.Equal("$.ops[0].set.coordinates.subsystem", validation["path"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_CreateTreeRefInRelations_ReturnsUnknownRefAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "defaults": {
                "coordinates": {
                  "type": "rule",
                  "subject": "api",
                  "subsystem": "mcp"
                }
              },
              "ops": [
                {
                  "op": "create",
                  "path": "TestDoc/Selectors/New_rule",
                  "set": {
                    "text": "new",
                    "relations": {
                      "subject": [ 10 ],
                      "examples": [ 40 ]
                    }
                  }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("unknown_ref", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_UpdateTextCoordinateAndAddRelation_CompilesLowLevelOps()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "update",
                  "path": "TestDoc/Selectors/A_rule",
                  "set": {
                    "text": "updated rule text",
                    "coordinates": { "subsystem": "cli" },
                    "relations": {
                      "examples": { "add": [ 41 ] }
                    }
                  }
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        Assert.False(result.Single().Data.ContainsKey("applied"));
        Assert.Collection(
            capture.Ops,
            op =>
            {
                var update = Assert.IsType<UpdateNodeOp>(op);
                Assert.Equal(30, update.Id);
                Assert.Null(update.NewTitle);
                Assert.Equal("updated rule text", update.NewText);
            },
            op =>
            {
                var move = Assert.IsType<MoveNodeOp>(op);
                Assert.Equal(30, move.Id);
                Assert.Equal(21, move.NewParentId);
                Assert.Equal("subsystem", move.Tree);
            },
            op =>
            {
                var createRef = Assert.IsType<CreateRefOp>(op);
                Assert.Equal(30, createRef.FromId);
                Assert.Equal("examples", createRef.Name);
                Assert.Equal(41, createRef.ToId);
            });
    }

    [Fact]
    public void Execute_UpdateReplaceRelation_CompilesDeleteAndCreateRefOps()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "update",
                  "id": 30,
                  "set": {
                    "relations": {
                      "examples": { "replace": [ 41 ] }
                    }
                  }
                }
              ]
            }
            """);

        executor.Execute(request);

        Assert.Collection(
            capture.Ops,
            op =>
            {
                var deleteRef = Assert.IsType<DeleteRefOp>(op);
                Assert.Equal(30, deleteRef.FromId);
                Assert.Equal("examples", deleteRef.Name);
                Assert.Equal(40, deleteRef.ToId);
            },
            op =>
            {
                var createRef = Assert.IsType<CreateRefOp>(op);
                Assert.Equal(30, createRef.FromId);
                Assert.Equal("examples", createRef.Name);
                Assert.Equal(41, createRef.ToId);
            });
    }

    [Fact]
    public void Execute_UpdateExpectedCountMismatch_ReturnsValidationFailure()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "update",
                  "select": {
                    "path": "TestDoc/Selectors/*",
                    "coordinates": { "type": "rule" }
                  },
                  "expected_count": 2,
                  "set": { "text": "updated" }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("count_mismatch", validation["code"]!.GetValue<string>());
        Assert.Equal(2, validation["details"]!["expected_count"]!.GetValue<int>());
        Assert.Equal(1, validation["details"]!["actual_count"]!.GetValue<int>());
    }

    [Fact]
    public void Execute_UpdateIdsWithoutExpectedCount_ReturnsValidationFailure()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "update",
                  "ids": [ 30 ],
                  "set": { "text": "updated" }
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("count_mismatch", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_DeleteIdsWithExpectedCount_CompilesDeleteNodesOp()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "delete",
                  "ids": [ 41 ],
                  "expected_count": 1
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        var op = Assert.IsType<DeleteNodesOp>(Assert.Single(capture.Ops));
        Assert.Equal(new[] { 41 }, op.Ids);
    }

    [Fact]
    public void Execute_MoveToAnotherParent_CompilesMoveNodeOp()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "path": "TestDoc/Selectors/A_rule",
                  "to": "TestDoc/Other/A_rule"
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        Assert.True(result.Single().Data["resolved"]!["moved"]!.GetValue<bool>());
        Assert.False(result.Single().Data["resolved"]!["renamed"]!.GetValue<bool>());
        var op = Assert.IsType<MoveNodeOp>(Assert.Single(capture.Ops));
        Assert.Equal(30, op.Id);
        Assert.Equal(3, op.NewParentId);
        Assert.Equal("path", op.Tree);
    }

    [Fact]
    public void Execute_MoveRenameOnly_CompilesUpdateNodeOp()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "id": 30,
                  "to": "TestDoc/Selectors/Renamed_rule"
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        Assert.False(result.Single().Data["resolved"]!["moved"]!.GetValue<bool>());
        Assert.True(result.Single().Data["resolved"]!["renamed"]!.GetValue<bool>());
        var op = Assert.IsType<UpdateNodeOp>(Assert.Single(capture.Ops));
        Assert.Equal(30, op.Id);
        Assert.Equal("Renamed_rule", op.NewTitle);
        Assert.Null(op.NewText);
    }

    [Fact]
    public void Execute_MoveAndRename_CompilesMoveThenUpdateNodeOps()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "target": { "path": "TestDoc/Selectors/A_rule" },
                  "to": "TestDoc/Other/Renamed_rule"
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        Assert.Collection(
            capture.Ops,
            op =>
            {
                var move = Assert.IsType<MoveNodeOp>(op);
                Assert.Equal(30, move.Id);
                Assert.Equal(3, move.NewParentId);
            },
            op =>
            {
                var update = Assert.IsType<UpdateNodeOp>(op);
                Assert.Equal(30, update.Id);
                Assert.Equal("Renamed_rule", update.NewTitle);
                Assert.Null(update.NewText);
            });
    }

    [Fact]
    public void Execute_MoveToExistingPath_ReturnsAlreadyExistsAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "id": 30,
                  "to": "TestDoc/Selectors/A_example"
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("already_exists", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_MoveMissingParent_ReturnsPathParentNotFoundAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "id": 30,
                  "to": "TestDoc/Missing/A_rule"
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("path_parent_not_found", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_MoveWildcardTo_ReturnsAmbiguousSelectorAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "id": 30,
                  "to": "TestDoc/Other/*"
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("ambiguous_selector", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_MoveAmbiguousSource_ReturnsAmbiguousSelectorAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "path": "TestDoc/Selectors/*",
                  "to": "TestDoc/Other/A_rule"
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("ambiguous_selector", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_Link_CompilesCreateRefOp()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "link",
                  "from": "TestDoc/Selectors/A_rule",
                  "name": "examples",
                  "to": "TestDoc/Selectors/B_example"
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        Assert.False(result.Single().Data.ContainsKey("applied"));
        var op = Assert.IsType<CreateRefOp>(Assert.Single(capture.Ops));
        Assert.Equal(30, op.FromId);
        Assert.Equal("examples", op.Name);
        Assert.Equal(41, op.ToId);
        Assert.Equal(30, result.Single().Data["resolved"]!["from_id"]!.GetValue<int>());
        Assert.Equal(41, result.Single().Data["resolved"]!["to_id"]!.GetValue<int>());
    }

    [Fact]
    public void Execute_Unlink_CompilesDeleteRefOp()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "unlink",
                  "from": 30,
                  "relation": "examples",
                  "to": 40
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.False(result.Single().Data.ContainsKey("validation"));
        var op = Assert.IsType<DeleteRefOp>(Assert.Single(capture.Ops));
        Assert.Equal(30, op.FromId);
        Assert.Equal("examples", op.Name);
        Assert.Equal(40, op.ToId);
    }

    [Fact]
    public void Execute_SelectAliasesThenLink_ResolvesAliasesIntoCreateRefOp()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "select",
                  "as": "rule",
                  "select": { "path": "TestDoc/Selectors/A_rule" }
                },
                {
                  "op": "select",
                  "as": "sample",
                  "select": { "path": "TestDoc/Selectors/B_example" }
                },
                {
                  "op": "link",
                  "from": "$rule",
                  "name": "examples",
                  "to": "$sample"
                }
              ]
            }
            """);

        var result = executor.Execute(request);

        Assert.Equal(1, result[0].Data["count"]!.GetValue<int>());
        Assert.Equal(1, result[1].Data["count"]!.GetValue<int>());
        var op = Assert.IsType<CreateRefOp>(Assert.Single(capture.Ops));
        Assert.Equal(30, op.FromId);
        Assert.Equal(41, op.ToId);
    }

    [Fact]
    public void Execute_LinkTreeRef_ReturnsUnknownRefAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "link",
                  "from": "TestDoc/Selectors/A_rule",
                  "name": "subject",
                  "to": 10
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("unknown_ref", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_LinkAmbiguousFrom_ReturnsAmbiguousSelectorAndDoesNotApply()
    {
        var capture = new CapturingWrite();
        var executor = BuildExecutor(capture.Apply);
        var request = Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "link",
                  "from": { "path": "TestDoc/Selectors/*" },
                  "name": "examples",
                  "to": 41
                }
              ]
            }
            """);

        var validation = executor.Execute(request).Single().Data["validation"]!.AsObject();

        Assert.Equal(0, capture.Calls);
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("ambiguous_selector", validation["code"]!.GetValue<string>());
    }

    private static LlmJsonApiTxExecutor BuildExecutor(Func<IReadOnlyList<WriteOp>, WriteResult> apply)
    {
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        return new LlmJsonApiTxExecutor(graph, schema, apply);
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
            "Synthetic LLM tx schema",
            trees,
            new List<TypeDefinition>
            {
                documentType,
                sectionType,
                categoryType,
                AtomType("rule", "section", new RefDef("examples", new[] { "example" }, null, Cardinality.Many, true, null)),
                AtomType("example", "section"),
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
        Add(graph, 3, "section", "Other", "", new() { ["path"] = new[] { 1 } });

        Add(graph, 10, "category", "api", "", new()
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
        Add(graph, 40, "example", "A_example", "example text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 20 },
        });
        Add(graph, 41, "example", "B_example", "second example text", new()
        {
            ["path"] = new[] { 2 },
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

    private sealed class CapturingWrite
    {
        public int Calls { get; private set; }
        public IReadOnlyList<WriteOp> Ops { get; private set; } = Array.Empty<WriteOp>();

        public WriteResult Apply(IReadOnlyList<WriteOp> ops)
        {
            Calls++;
            Ops = ops.ToArray();
            return new WriteResult(
                ops.Select((op, index) => new WriteOpResult(
                        op.Type,
                        new JsonObject
                        {
                            ["id"] = 50 + index,
                            ["type"] = op is CreateNodeOp create ? create.TypeName : op.Type,
                            ["title"] = op is CreateNodeOp createNode ? createNode.Title : null,
                        }))
                    .ToArray(),
                Applied: true);
        }
    }
}
