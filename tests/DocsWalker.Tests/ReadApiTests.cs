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
        return new ReadApi(loaded.Graph);
    }

    [Fact]
    public void ListDocuments_Returns_AllRootDocuments()
    {
        var api = BuildApi();
        var docs = api.ListDocuments();
        Assert.Equal(3, docs.Count);
        Assert.Contains(docs, d => d.Title == "DocsWalker" && d.Id == 1);
        Assert.Contains(docs, d => d.Title == "Правила оформления" && d.Id == 46);
        Assert.Contains(docs, d => d.Title == "Стек" && d.Id == 64);
    }

    [Fact]
    public void GetMap_BuildsTreeOfDocs_WithoutBlocks()
    {
        var api = BuildApi();
        var map = api.GetMap();
        Assert.Equal(3, map.Count);

        var docsWalker = map.Single(m => m.Title == "DocsWalker");
        Assert.Equal("document", docsWalker.TypeName);
        Assert.NotEmpty(docsWalker.Children);
        Assert.All(docsWalker.Children, c => Assert.Equal("section", c.TypeName));
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
        Assert.NotNull(nodes[2].Blocks);
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
    public void GetRefs_ForSection_Includes_ExplicitOut_And_DefaultRefs()
    {
        var api = BuildApi();
        // Section "Стек реализации" id=45: explicit out_refs ref→64 и ref→46.
        var set = api.GetRefs(45);
        Assert.Contains(set.Out, v => v.TypeName == "ref" && v.Origin == RefOrigin.Explicit && v.OtherId == 64);
        Assert.Contains(set.Out, v => v.TypeName == "ref" && v.Origin == RefOrigin.Explicit && v.OtherId == 46);
        // Системная path: 45 → 1 (родитель — DocsWalker).
        Assert.Contains(set.Out, v => v.TypeName == "path" && v.Origin == RefOrigin.System && v.OtherId == 1);
    }

    [Fact]
    public void GetRefs_FilterByOrigin_Explicit_DropsSystemAndDefault()
    {
        var api = BuildApi();
        var set = api.GetRefs(45, type: null, origin: RefOrigin.Explicit);
        Assert.All(set.Out, v => Assert.Equal(RefOrigin.Explicit, v.Origin));
        Assert.All(set.In, v => Assert.Equal(RefOrigin.Explicit, v.Origin));
    }

    [Fact]
    public void GetInRefs_ReturnsOnlyIncoming()
    {
        var api = BuildApi();
        // Документ Стек id=64 — на него ссылается section id=45 типом ref.
        var set = api.GetInRefs(64);
        Assert.Empty(set.Out);
        Assert.Contains(set.In, v => v.OtherId == 45 && v.TypeName == "ref" && v.Origin == RefOrigin.Explicit);
    }

    [Fact]
    public void Search_FindsSubstring_InDescriptionsAndBlocks()
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
        // Section id=8 "узел" — внутри section "Модель данных" (id=7) → DocsWalker.
        var path = api.FormatPath(8);
        Assert.StartsWith("DocsWalker/", path);
        Assert.EndsWith("узел", path);
    }

    [Fact]
    public void CheckIntegrity_OnRealDocs_Passes_NoErrors()
    {
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        var api = new ReadApi(loaded.Graph);
        var maxId = loaded.Graph.ById.Keys.Max();
        var result = api.CheckIntegrity(meta, schema, sequence: maxId);
        Assert.True(result.IsValid,
            "Реальные docs/ должны проходить check-integrity без ошибок:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Errors.Select(e => $"[{e.Code}] {e.Message}")));
    }
}
