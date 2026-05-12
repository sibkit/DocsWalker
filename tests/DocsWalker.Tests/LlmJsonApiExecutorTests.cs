using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiExecutorTests
{
    [Fact]
    public void Execute_QuerySuccess_ReturnsSuccessEnvelope()
    {
        var executor = BuildExecutor(out _);

        var response = executor.Execute(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": { "path": "TestDoc/Selectors/A_rule" }
                }
              ]
            }
            """);

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal("query", response["method"]!.GetValue<string>());
        Assert.Equal(123, response["base_revision"]!.GetValue<long>());
        Assert.Contains("query", response["summary"]!.GetValue<string>());

        var result = Assert.Single(response["results"]!.AsArray())!.AsObject();
        Assert.Equal(0, result["index"]!.GetValue<int>());
        Assert.Equal("select", result["op"]!.GetValue<string>());
        Assert.Equal(1, result["data"]!["count"]!.GetValue<int>());
        Assert.Equal(30, result["data"]!["nodes"]!.AsArray()[0]!["id"]!.GetValue<int>());
    }

    [Fact]
    public void Execute_InvalidJson_ReturnsInvalidJsonEnvelope()
    {
        var executor = BuildExecutor(out _);

        var response = executor.Execute("{ not json");

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Null(response["method"]);
        Assert.Equal("invalid_json", response["code"]!.GetValue<string>());
        Assert.False(response.ContainsKey("results"));
    }

    [Fact]
    public void Execute_ParseError_ReturnsErrorEnvelope()
    {
        var executor = BuildExecutor(out _);

        var response = executor.Execute(
            """
            {
              "method": "query"
            }
            """);

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("query", response["method"]!.GetValue<string>());
        Assert.Equal("missing_required_field", response["code"]!.GetValue<string>());
        Assert.Equal("$.ops", response["details"]!["path"]!.GetValue<string>());
        Assert.False(response.ContainsKey("results"));
    }

    [Fact]
    public void Execute_QueryValidationFailure_ReturnsTopLevelError()
    {
        var executor = BuildExecutor(out _);

        var response = executor.Execute(
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

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("query", response["method"]!.GetValue<string>());
        Assert.Equal("invalid_op", response["code"]!.GetValue<string>());
        Assert.Equal(0, response["details"]!["operation_index"]!.GetValue<int>());
        Assert.Equal("$.ops[0].op", response["details"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_HitValidationFailure_RemainsSuccessfulPreview()
    {
        var executor = BuildExecutor(out _);

        var response = executor.Execute(
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

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal("hit", response["method"]!.GetValue<string>());

        var result = Assert.Single(response["results"]!.AsArray())!.AsObject();
        var validation = result["data"]!["validation"]!.AsObject();
        Assert.False(validation["ok"]!.GetValue<bool>());
        Assert.Equal("count_mismatch", validation["code"]!.GetValue<string>());
    }

    [Fact]
    public void Execute_TxValidationFailure_ReturnsTopLevelErrorAndDoesNotApply()
    {
        var executor = BuildExecutor(out var capture);

        var response = executor.Execute(
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
                  "path": "TestDoc/Selectors/A_rule",
                  "set": { "text": "new" }
                }
              ]
            }
            """);

        Assert.Equal(0, capture.Calls);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("tx", response["method"]!.GetValue<string>());
        Assert.Equal("already_exists", response["code"]!.GetValue<string>());
        Assert.Equal(0, response["details"]!["operation_index"]!.GetValue<int>());
        Assert.Equal("$.ops[0].path", response["details"]!["path"]!.GetValue<string>());
        Assert.False(response.ContainsKey("results"));
    }

    [Fact]
    public void Execute_TxValidatorRejection_ReturnsValidationFailedEnvelope()
    {
        var executor = BuildExecutor(
            out var capture,
            new WriteValidationException(new[]
            {
                new ValidationError(
                    "missing_required_ref",
                    "Узел потерял обязательную связь.",
                    NodeId: 30,
                    Path: "TestDoc/Selectors/A_rule",
                    RefName: "examples"),
            }));

        var response = executor.Execute(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "update",
                  "id": 30,
                  "set": { "text": "updated" }
                }
              ]
            }
            """);

        Assert.Equal(1, capture.Calls);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("tx", response["method"]!.GetValue<string>());
        Assert.Equal("validation_failed", response["code"]!.GetValue<string>());

        var error = Assert.Single(response["details"]!["details"]!["errors"]!.AsArray())!.AsObject();
        Assert.Equal("missing_required_ref", error["code"]!.GetValue<string>());
        Assert.Equal(30, error["node_id"]!.GetValue<int>());
        Assert.Equal("TestDoc/Selectors/A_rule", error["path"]!.GetValue<string>());
        Assert.Equal("examples", error["ref"]!.GetValue<string>());
        Assert.False(response.ContainsKey("results"));
    }

    [Fact]
    public void Execute_TxApplyFailure_ReturnsTopLevelError()
    {
        var executor = BuildExecutor(
            out var capture,
            new WriteApiException("write_failed", "write failed", refName: "examples"));

        var response = executor.Execute(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "update",
                  "id": 30,
                  "set": { "text": "updated" }
                }
              ]
            }
            """);

        Assert.Equal(1, capture.Calls);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("tx", response["method"]!.GetValue<string>());
        Assert.Equal("write_failed", response["code"]!.GetValue<string>());
        Assert.Equal(0, response["details"]!["operation_index"]!.GetValue<int>());
        Assert.Equal("examples", response["details"]!["details"]!["ref"]!.GetValue<string>());
    }

    private static LlmJsonApiExecutor BuildExecutor(
        out CapturingWrite capture,
        Exception? applyException = null)
    {
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        capture = new CapturingWrite(applyException);
        return new LlmJsonApiExecutor(graph, schema, capture.Apply, () => 123);
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

        return new SchemaDocument(
            "Synthetic LLM envelope schema",
            trees,
            new List<TypeDefinition>
            {
                documentType,
                sectionType,
                categoryType,
                AtomType("rule", "section", new RefDef("examples", new[] { "example" }, null, Cardinality.Many, false, null)),
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
        Add(graph, 31, "rule", "B_rule", "second rule text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 10 },
            ["subsystem"] = new[] { 21 },
        });
        Add(graph, 40, "example", "A_example", "example text", new()
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
        private readonly Exception? _exception;

        public CapturingWrite(Exception? exception)
        {
            _exception = exception;
        }

        public int Calls { get; private set; }

        public WriteResult Apply(IReadOnlyList<WriteOp> ops)
        {
            Calls++;
            if (_exception is not null)
                throw _exception;

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
