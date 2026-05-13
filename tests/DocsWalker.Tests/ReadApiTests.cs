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
    public void GetByPath_ResolvesDocumentTitle()
    {
        var api = BuildApi();
        var subtree = api.GetByPath("DocsWalker", tree: Node.PathRefName);
        Assert.Equal(1, subtree.Node.Id);
        Assert.Equal("document", subtree.Node.TypeName);
        Assert.NotEmpty(subtree.Children);
    }

    [Fact]
    public void GetByPath_ResolvesNestedSection()
    {
        var api = BuildApi();
        var subtree = api.GetByPath("DocsWalker/Стек реализации", tree: Node.PathRefName);
        Assert.Equal(45, subtree.Node.Id);
        Assert.Equal("section", subtree.Node.TypeName);
    }

    [Fact]
    public void GetByPath_DepthZero_ReturnsResolvedNodeOnly()
    {
        var api = BuildApi();
        var subtree = api.GetByPath("DocsWalker", tree: Node.PathRefName, depth: 0);
        Assert.Equal(1, subtree.Node.Id);
        Assert.Empty(subtree.Children);
    }

    [Fact]
    public void GetByPath_NegativeDepth_Throws_InvalidParameter()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() =>
            api.GetByPath("DocsWalker", tree: Node.PathRefName, depth: -1));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    [Fact]
    public void GetByPath_UnknownPath_Throws()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() => api.GetByPath("DocsWalker/НетТакогоРаздела", tree: Node.PathRefName));
        Assert.Equal("path_not_found", ex.Code);
    }

    [Fact]
    public void GetSubtree_DepthZero_ReturnsRootOnly()
    {
        var api = BuildApi();
        var subtree = api.GetTree(rootId: 2, tree: Node.PathRefName, depth: 0);
        Assert.Equal(2, subtree.Node.Id);
        Assert.Empty(subtree.Children);
        // SubtreeTokens === Tokens, потому что детей в результате нет.
        Assert.Equal(subtree.Tokens, subtree.SubtreeTokens);
    }

    [Fact]
    public void GetSubtree_DepthOne_ReturnsRootAndDirectChildrenOnly()
    {
        var api = BuildApi();
        var subtree = api.GetTree(rootId: 2, tree: Node.PathRefName, depth: 1);
        Assert.NotEmpty(subtree.Children);
        Assert.All(subtree.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void GetSubtree_NegativeDepth_Throws_InvalidParameter()
    {
        var api = BuildApi();
        var ex = Assert.Throws<ReadApiException>(() => api.GetTree(rootId: 2, tree: Node.PathRefName, depth: -1));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    [Fact]
    public void GetSubtree_DefaultDepth_TraversesFullTree()
    {
        var api = BuildApi();
        var subtree = api.GetTree(rootId: 1);
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

    [Fact]
    public void GetOverview_OnRealDocs_HasSaneAggregates()
    {
        var api = BuildApi();
        var o = api.GetOverview();

        Assert.True(o.TotalNodes > 0);
        Assert.True(o.MaxDepth >= 1);
        Assert.True(o.TotalTokens > 0);

        Assert.Contains(o.Trees, t => t.Name == Node.PathRefName);

        Assert.True(o.TypesCount > 0);
        Assert.NotEmpty(o.TopTypesByCount);
        Assert.True(o.TopTypesByCount.Count <= 5);
        // Сортировка по убыванию count.
        for (int i = 1; i < o.TopTypesByCount.Count; i++)
            Assert.True(o.TopTypesByCount[i - 1].Count >= o.TopTypesByCount[i].Count);

        Assert.Contains(o.RootChildren, rc => rc.Title == "DocsWalker" && rc.TypeName == "document");
        Assert.All(o.RootChildren, rc => Assert.True(rc.SubtreeTokens > 0));

        Assert.True(o.LargestNodes.Count <= 5);
        Assert.NotEmpty(o.LargestNodes);
        for (int i = 1; i < o.LargestNodes.Count; i++)
            Assert.True(o.LargestNodes[i - 1].Tokens >= o.LargestNodes[i].Tokens);

        Assert.True(o.MostConnectedNodes.Count <= 5);
        for (int i = 1; i < o.MostConnectedNodes.Count; i++)
            Assert.True(o.MostConnectedNodes[i - 1].RefsCount >= o.MostConnectedNodes[i].RefsCount);
    }

    [Fact]
    public void GetOverview_TopTypesByCount_ExcludesRootType()
    {
        var api = BuildApi();
        var o = api.GetOverview();
        Assert.DoesNotContain(o.TopTypesByCount, t => t.TypeName == Node.RootTypeName);
    }

    [Fact]
    public void GetOverview_MostConnected_ExcludesTreeRefs()
    {
        // Метрика связанности должна игнорировать path-ref (он есть у каждого узла кроме root),
        // иначе верх занимают узлы с многочисленными path-детьми. Проверка: число cross-refs
        // у листа меньше его суммарного in/out если бы считали path-ref.
        var api = BuildApi();
        var o = api.GetOverview();
        // Если бы tree-refs учитывались, root_children (документы) с десятками path-детей
        // обязательно бы попали в top. Документ DocsWalker — самый «жирный» по path-связям.
        // Проверяем, что в top связанности либо нет document'а DocsWalker, либо его refs_count
        // не равен числу его прямых path-детей.
        var doc = o.MostConnectedNodes.FirstOrDefault(m => m.Title == "DocsWalker");
        // Допустим, документ может попасть по cross-refs (например, examples к нему),
        // но refs_count явно меньше числа path-детей корпуса.
        Assert.True(doc is null || doc.RefsCount < 30,
            "DocsWalker в most_connected_nodes должен иметь меньше cross-refs, чем path-детей.");
    }
}
