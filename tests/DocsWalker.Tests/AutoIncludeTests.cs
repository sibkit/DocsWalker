using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение auto-include (#340): транзитивный обход non-tree required cross-refs
/// плюс шейп-вывод в read-командах. На реальной Схеме DocsWalker auto-include
/// активен только у связи rule.examples — её и используем как фикстуру.
/// </summary>
public class AutoIncludeTests
{
    private static (ReadApi Api, Graph Graph, SchemaDocument Schema) Build()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return (new ReadApi(loaded.Graph, schema), loaded.Graph, schema);
    }

    private static RefDef GetRule_ExamplesRefDef(SchemaDocument schema)
    {
        var rule = schema.Types.Single(t => t.Name == "rule");
        return rule.OutRefs.Single(r => r.Name == "examples");
    }

    private static Node FindFirstRuleWithExamples(Graph graph)
    {
        foreach (var n in graph.ById.Values.OrderBy(x => x.Id))
        {
            if (n.TypeName != "rule") continue;
            if (!n.OutRefs.TryGetValue("examples", out var ex)) continue;
            if (ex.Count > 0) return n;
        }
        throw new InvalidOperationException("В docs/ нет rule-узла со связью examples — фикстура сломана.");
    }

    [Fact]
    public void RefDef_IsAutoInclude_TrueOnlyWhenTreeNullAndRequired()
    {
        var (_, _, schema) = Build();
        var examples = GetRule_ExamplesRefDef(schema);
        Assert.True(examples.IsAutoInclude);
        Assert.Null(examples.Tree);
        Assert.True(examples.Required);

        // path-связь — tree-ref, не auto-include.
        var folder = schema.Types.Single(t => t.Name == "folder");
        var path = folder.OutRefs.Single(r => r.Name == "path");
        Assert.False(path.IsAutoInclude);
    }

    [Fact]
    public void CollectAutoIncludes_RuleSeed_PullsExamplesTargets()
    {
        var (api, graph, _) = Build();
        var rule = FindFirstRuleWithExamples(graph);
        var expectedTargets = rule.OutRefs["examples"];

        var pulled = api.CollectAutoIncludes(new[] { rule });

        // Все цели rule.examples должны быть подтянуты (минимум — все expectedTargets,
        // плюс возможные транзитивные auto-include от самих examples — но у statement
        // нет non-tree required связей, так что счёт = expectedTargets.Count).
        var pulledIds = pulled.Select(n => n.Id).ToHashSet();
        foreach (var tid in expectedTargets)
            Assert.Contains(tid, pulledIds);
        // Сам rule в результат не попадает (seed исключён).
        Assert.DoesNotContain(rule.Id, pulledIds);
    }

    [Fact]
    public void CollectAutoIncludes_NoSchema_ReturnsEmpty()
    {
        var (_, graph, _) = Build();
        var apiWithoutSchema = new ReadApi(graph);
        var rule = FindFirstRuleWithExamples(graph);

        var pulled = apiWithoutSchema.CollectAutoIncludes(new[] { rule });

        Assert.Empty(pulled);
    }

    [Fact]
    public void SubtreeToJson_GetByPath_AddsAutoIncludesField_WhenPresent()
    {
        var (api, graph, _) = Build();
        var rule = FindFirstRuleWithExamples(graph);
        var expectedAutoIds = rule.OutRefs["examples"].ToHashSet();

        // Получаем subtree, в котором rule — корень: нужно построить subtree от
        // самого rule. У rule в path-tree нет детей (statement-ы — это cross-refs,
        // не path-children), так что depth=0 даёт сам узел.
        var subtree = api.GetTree(rule.Id, Node.PathRefName, depth: 0);
        var autoIncludes = api.CollectAutoIncludes(subtree);
        var json = ReadApiJson.SubtreeToJsonWithAutoIncludes(
            subtree, fields: null, autoIncludes);

        Assert.Equal(rule.Id, (int)json["id"]!);
        var arr = (JsonArray)json["auto_includes"]!;
        Assert.NotEmpty(arr);
        foreach (JsonObject item in arr.Cast<JsonObject>())
            Assert.Contains((int)item["id"]!, expectedAutoIds);
    }

    [Fact]
    public void SubtreeToJson_GetSubtree_AddsAutoIncludesField_AlongsideRoot()
    {
        var (api, graph, _) = Build();
        var rule = FindFirstRuleWithExamples(graph);

        var subtree = api.GetTree(rule.Id, Node.PathRefName, depth: 0);
        var autoIncludes = api.CollectAutoIncludes(subtree);
        var json = ReadApiJson.SubtreeToJson(
            subtree, Node.PathRefName, fields: null, autoIncludes);

        Assert.Equal(Node.PathRefName, (string)json["tree"]!);
        Assert.NotNull(json["root"]);
        Assert.NotNull(json["auto_includes"]);
    }

    [Fact]
    public void SubtreeToJson_NoAutoIncludes_OmitsField_BackwardCompatible()
    {
        var (api, graph, _) = Build();
        // Берём узел без rule-типа (например, root id=0 или document) — у него
        // не должно быть auto-include-связей (auto-include только у rule.examples).
        var doc = graph.Documents.First();
        var subtree = api.GetTree(doc.Id, Node.PathRefName, depth: 0);
        var autoIncludes = api.CollectAutoIncludes(subtree);

        // Если у doc-узла есть транзитивные rule-узлы в subtree — auto-include может
        // быть. Здесь depth=0 → seed только doc → без rule в seed → пусто.
        Assert.Empty(autoIncludes);

        var withField = ReadApiJson.SubtreeToJsonWithAutoIncludes(
            subtree, fields: null, autoIncludes);
        var withoutField = ReadApiJson.SubtreeToJson(subtree);
        Assert.Equal(withoutField.ToJsonString(), withField.ToJsonString());
    }
}
