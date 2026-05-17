using System.Text.Json.Nodes;
using DocsWalker.Core.Api;

namespace DocsWalker.Tests;

public class LlmJsonApiModelTests
{
    [Fact]
    public void Parse_QuerySelect_ReadsEnvelopeDefaultsAndInclude()
    {
        var request = LlmJsonApiParser.Parse(JsonNode.Parse(
            """
            {
              "method": "query",
              "defaults": {
                "path_parent": "DocsWalker-LLM JSON API",
                "coordinates": {
                  "type": "rule",
                  "subject": "api/read"
                }
              },
              "ops": [
                {
                  "op": "select",
                  "as": "rules",
                  "select": {
                    "path": "DocsWalker-LLM JSON API/**",
                    "coordinates": {
                      "type": "rule"
                    },
                    "expect": {
                      "count": 3
                    }
                  },
                  "include": ["text", "relations", "coordinates"],
                  "max_tokens": 12000
                }
              ]
            }
            """));

        Assert.Equal(LlmJsonApiMethod.Query, request.Method);
        Assert.Equal("DocsWalker-LLM JSON API", request.Defaults.PathParent);
        Assert.Equal("rule", request.Defaults.Coordinates.Type);
        Assert.Equal("api/read", request.Defaults.Coordinates.Get("subject"));

        var select = Assert.IsType<LlmSelectOperation>(request.Ops.Single());
        Assert.Equal("rules", select.Alias);
        Assert.Equal("DocsWalker-LLM JSON API/**", select.Select.Path);
        Assert.Equal("rule", select.Select.Coordinates.Type);
        Assert.NotNull(select.Select.Expect);
        Assert.Equal(new[] { "text", "relations", "coordinates" }, select.Include);
        Assert.Equal(12000, select.MaxTokens);
    }

    [Fact]
    public void Parse_QuerySelect_ReadsMatch()
    {
        var request = LlmJsonApiParser.Parse(JsonNode.Parse(
            """
            {
              "method": "query",
              "ops": [
                {
                  "op": "select",
                  "select": {
                    "path": "DocsWalker-LLM JSON API/**",
                    "coordinates": {
                      "type": "definition"
                    },
                    "match": {
                      "regex": "validation_failed",
                      "fields": ["title", "text"],
                      "case_sensitive": true
                    }
                  },
                  "max_tokens": 1000
                }
              ]
            }
            """));

        var select = Assert.IsType<LlmSelectOperation>(request.Ops.Single());
        Assert.Equal("DocsWalker-LLM JSON API/**", select.Select.Path);
        Assert.Equal("definition", select.Select.Coordinates.Type);
        Assert.Equal("validation_failed", select.Select.Match?.Regex);
        Assert.Equal(new[] { "title", "text" }, select.Select.Match?.Fields);
        Assert.True(select.Select.Match?.CaseSensitive);
        Assert.Equal(1000, select.MaxTokens);
    }

    [Fact]
    public void Parse_TxWriteOps_ReadsTargetsSetAndRelationPatches()
    {
        var request = LlmJsonApiParser.Parse(JsonNode.Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "create",
                  "as": "created",
                  "path": "DocsWalker-LLM JSON API/Example",
                  "set": {
                    "text": "body",
                    "coordinates": {
                      "type": "statement",
                      "subsystem": "kernel"
                    },
                    "relations": {
                      "examples": [42, "DocsWalker/Example"]
                    }
                  }
                },
                {
                  "op": "update",
                  "target": "$created",
                  "expected_count": 1,
                  "set": {
                    "relations": {
                      "examples": {
                        "add": [43]
                      },
                      "notes": {
                        "mode": "replace",
                        "targets": ["DocsWalker/Note"]
                      }
                    }
                  }
                },
                {
                  "op": "delete",
                  "ids": [50, 51],
                  "expected_count": 2
                }
              ]
            }
            """));

        Assert.Equal(LlmJsonApiMethod.Tx, request.Method);
        Assert.Equal(3, request.Ops.Count);

        var create = Assert.IsType<LlmCreateOperation>(request.Ops[0]);
        Assert.Equal("created", create.Alias);
        Assert.Equal("DocsWalker-LLM JSON API/Example", create.Path);
        Assert.Equal("body", create.Set.Text);
        Assert.Equal("statement", create.Set.Coordinates.Type);
        Assert.Equal("kernel", create.Set.Coordinates.Get("subsystem"));
        var createExamples = create.Set.Relations["examples"];
        Assert.Null(createExamples.Mode);
        Assert.Equal(42, createExamples.Targets[0].Id);
        Assert.Equal("DocsWalker/Example", createExamples.Targets[1].Path);

        var update = Assert.IsType<LlmUpdateOperation>(request.Ops[1]);
        Assert.Equal("created", update.Target.Alias);
        Assert.Equal(1, update.ExpectedCount);
        var addExamples = update.Set.Relations["examples"];
        Assert.Equal(LlmRelationPatchMode.Add, addExamples.Mode);
        Assert.Equal(43, addExamples.Targets.Single().Id);
        var replaceNotes = update.Set.Relations["notes"];
        Assert.Equal(LlmRelationPatchMode.Replace, replaceNotes.Mode);
        Assert.Equal("DocsWalker/Note", replaceNotes.Targets.Single().Path);

        var delete = Assert.IsType<LlmDeleteOperation>(request.Ops[2]);
        Assert.Equal(new[] { 50, 51 }, delete.Target.Ids);
        Assert.Equal(2, delete.ExpectedCount);
    }

    [Fact]
    public void Parse_LinkAndMove_ReadsSingleTargets()
    {
        var request = LlmJsonApiParser.Parse(JsonNode.Parse(
            """
            {
              "method": "tx",
              "ops": [
                {
                  "op": "move",
                  "path": "DocsWalker/Old",
                  "to": "DocsWalker/New"
                },
                {
                  "op": "link",
                  "from": "$created",
                  "name": "examples",
                  "to": {
                    "id": 42
                  }
                },
                {
                  "op": "unlink",
                  "from": {
                    "select": {
                      "path": "DocsWalker/New"
                    }
                  },
                  "relation": "examples",
                  "to": 42
                }
              ]
            }
            """));

        var move = Assert.IsType<LlmMoveOperation>(request.Ops[0]);
        Assert.Equal("DocsWalker/Old", move.Source.Path);
        Assert.Equal("DocsWalker/New", move.To);

        var link = Assert.IsType<LlmLinkOperation>(request.Ops[1]);
        Assert.Equal("created", link.From.Alias);
        Assert.Equal("examples", link.Name);
        Assert.Equal(42, link.To.Id);

        var unlink = Assert.IsType<LlmUnlinkOperation>(request.Ops[2]);
        Assert.Equal("DocsWalker/New", unlink.From.Select?.Path);
        Assert.Equal("examples", unlink.Name);
        Assert.Equal(42, unlink.To.Id);
    }

    [Fact]
    public void Parse_InvalidOpsShape_ReturnsPathInException()
    {
        var ex = Assert.Throws<LlmJsonApiParseException>(() =>
            LlmJsonApiParser.Parse(JsonNode.Parse(
            """
            {
              "method": "query",
              "ops": {}
            }
            """)));

        Assert.Equal("invalid_request", ex.Code);
        Assert.Equal("$.ops", ex.Path);
    }

    [Fact]
    public void Parse_SelectTypeField_ReturnsInvalidRequest()
    {
        var ex = Assert.Throws<LlmJsonApiParseException>(() =>
            LlmJsonApiParser.Parse(JsonNode.Parse(
                """
                {
                  "method": "query",
                  "ops": [
                    {
                      "op": "select",
                      "select": {
                        "path": "DocsWalker-LLM JSON API/**",
                        "type": "definition"
                      }
                    }
                  ]
                }
                """)));

        Assert.Equal("invalid_request", ex.Code);
        Assert.Equal("$.ops[0].select.type", ex.Path);
    }
}
