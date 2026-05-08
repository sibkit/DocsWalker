using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

public class ReadApiTests
{
    private static ReadApi BuildApi()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return new ReadApi(loaded.Graph, schema);
    }

    [Fact]
    public void GetMap_BuildsTreeOfDocs_WithTokenMetrics()
    {
        var api = BuildApi();
        var map = api.GetMap();
        Assert.Equal(3, map.Count);

        var docsWalker = map.Single(m => m.Title == "DocsWalker");
        Assert.Equal("document", docsWalker.TypeName);
        Assert.NotEmpty(docsWalker.Children);
        Assert.All(docsWalker.Children, c => Assert.Equal("section", c.TypeName));

        // Метрики: токены сами должны быть положительны, subtree_tokens >= tokens
        // (узел всегда часть собственного поддерева).
        Assert.True(docsWalker.Tokens > 0);
        Assert.True(docsWalker.SubtreeTokens >= docsWalker.Tokens);

        // SubtreeTokens = tokens(self) + Σ subtree_tokens(child) — checking by construction.
        var expected = docsWalker.Tokens + docsWalker.Children.Sum(c => c.SubtreeTokens);
        Assert.Equal(expected, docsWalker.SubtreeTokens);
    }

    [Fact]
    public void GetNodes_ByIds_Returns_FullNodes_InOrder()
    {
        var api = BuildApi();
        var nodes = api.GetNodes(new[] { 1, 8, 45 });
        Assert.Equal(3, nodes.Count);
        Assert.Equal(1, nodes[0].Id);
        Assert.Equal("document", nodes[0].TypeName);
        Assert.Equal(8, nodes[1].Id);
        Assert.Equal("definition", nodes[1].TypeName);
        Assert.Equal(45, nodes[2].Id);
        Assert.Equal("section", nodes[2].TypeName);
    }

    [Fact]
    public void GetNodes_MissingId_Throws()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() => api.GetNodes(new[] { 999_999 }));
        Assert.Equal("node_not_found", ex.Code);
    }

    [Fact]
    public void GetByPath_ResolvesDocumentTitle()
    {
        var api = BuildApi();
        var subtree = api.GetByPath("DocsWalker");
        Assert.Equal(1, subtree.Node.Id);
        Assert.Equal("document", subtree.Node.TypeName);
        Assert.NotEmpty(subtree.Children);
    }

    [Fact]
    public void GetByPath_ResolvesNestedSection()
    {
        var api = BuildApi();
        var subtree = api.GetByPath("DocsWalker/Стек реализации");
        Assert.Equal(45, subtree.Node.Id);
        Assert.Equal("section", subtree.Node.TypeName);
    }

    [Fact]
    public void GetByPath_UnknownPath_Throws()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() => api.GetByPath("DocsWalker/НетТакогоРаздела"));
        Assert.Equal("path_not_found", ex.Code);
    }

    [Fact]
    public void GetSubtree_DepthZero_ReturnsRootOnly()
    {
        var api = BuildApi();
        var subtree = api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: 0);
        Assert.Equal(2, subtree.Node.Id);
        Assert.Empty(subtree.Children);
        // SubtreeTokens === Tokens, потому что детей в результате нет.
        Assert.Equal(subtree.Tokens, subtree.SubtreeTokens);
    }

    [Fact]
    public void GetSubtree_DepthOne_ReturnsRootAndDirectChildrenOnly()
    {
        var api = BuildApi();
        var subtree = api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: 1);
        Assert.NotEmpty(subtree.Children);
        Assert.All(subtree.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void GetSubtree_NegativeDepth_Throws_InvalidParameter()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() => api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: -1));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    [Fact]
    public void GetSubtree_DefaultDepth_TraversesFullTree()
    {
        var api = BuildApi();
        var subtree = api.GetSubtree(rootId: 1);
        // У документа DocsWalker вложенные section'ы, у каждой — атомы. Хотя бы 2 уровня.
        Assert.NotEmpty(subtree.Children);
        Assert.Contains(subtree.Children, c => c.Children.Count > 0);
    }

    [Fact]
    public void GetRefs_ForSection_Includes_PathRef_AndExplicitOuts()
    {
        var api = BuildApi();
        // Section "Стек реализации" id=45 — родитель DocsWalker (id=1).
        var set = api.GetRefs(45);
        Assert.True(set.Out.TryGetValue(Node.PathRefName, out var pathTargets));
        Assert.Contains(1, pathTargets!);
    }

    [Fact]
    public void GetRefs_FilterByName_LimitsResultsByRefName()
    {
        var api = BuildApi();
        var set = api.GetRefs(45, name: Node.PathRefName);
        Assert.All(set.Out.Keys, n => Assert.Equal(Node.PathRefName, n));
        Assert.All(set.In.Keys, n => Assert.Equal(Node.PathRefName, n));
    }

    [Fact]
    public void GetInRefs_ReturnsOnlyIncoming()
    {
        var api = BuildApi();
        // У DocsWalker (id=1) есть path-children (sections); GetInRefs должен вернуть их.
        var map = api.GetInRefs(1);
        Assert.True(map.ContainsKey(Node.PathRefName));
    }

    [Fact]
    public void Search_FindsSubstring_InText()
    {
        var api = BuildApi();
        var hits = api.Search("DocsWalker");
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.NotEmpty(h.Fragments));
    }

    [Fact]
    public void Search_EmptyQuery_Throws()
    {
        var api = BuildApi();
        Assert.Throws<ReadApiException>(() => api.Search(""));
    }

    [Fact]
    public void FormatPath_BuildsHumanReadablePath()
    {
        var api = BuildApi();
        var path = api.FormatPath(8); // definition «узел» внутри section «Модель данных» внутри DocsWalker
        Assert.StartsWith("DocsWalker/", path);
        Assert.EndsWith("узел", path);
    }

    [Fact]
    public void CheckIntegrity_OnRealDocs_Passes_NoErrors()
    {
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        var api = new ReadApi(loaded.Graph, schema);
        var maxId = loaded.Graph.ById.Keys.Max();
        var result = api.CheckIntegrity(meta, schema, sequence: maxId);
        Assert.True(result.IsValid,
            "Реальные docs/ должны проходить check-integrity без ошибок:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Errors.Select(e => $"[{e.Code}] {e.Message}")));
    }
}
