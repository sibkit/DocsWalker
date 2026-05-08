using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение root-as-entry-point (stg-0006): синтез root-узла на лету в
/// <see cref="DocsWalker.Core.Graph.Graph.GetById"/>, доступность через read-команды,
/// единый код ошибки <c>cannot_modify_root</c> на write-операциях, целящих в id=0.
/// </summary>
public class RootSynthesisTests
{
    private static ReadApi BuildApi()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return new ReadApi(loaded.Graph, schema);
    }

    [Fact]
    public void GetById_RootId_ReturnsSyntheticRoot()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        var root = loaded.Graph.GetById(0);
        Assert.NotNull(root);
        Assert.Equal(0, root!.Id);
        Assert.Equal("root", root.TypeName);
        Assert.Equal("docs", root.Title);
        Assert.NotEmpty(root.Text);
        Assert.Empty(root.OutRefs);
    }

    [Fact]
    public void GetByType_RootTypeName_ReturnsSingletonRoot()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        var byType = loaded.Graph.GetByType("root");
        Assert.Single(byType);
        Assert.Equal(0, byType[0].Id);
    }

    [Fact]
    public void GetNodes_IncludingRoot_ReturnsRootInOrder()
    {
        var api = BuildApi();
        var nodes = api.GetNodes(new[] { 0, 1 });
        Assert.Equal(2, nodes.Count);
        Assert.Equal(0, nodes[0].Id);
        Assert.Equal("root", nodes[0].TypeName);
        Assert.Equal(1, nodes[1].Id);
        Assert.Equal("document", nodes[1].TypeName);
    }

    [Fact]
    public void GetInRefs_RootId_PathName_IncludesTopLevelDocsAndFolders()
    {
        var api = BuildApi();
        var inRefs = api.GetInRefs(0, "path");
        Assert.True(inRefs.ContainsKey("path"));
        var topLevel = inRefs["path"];
        Assert.NotEmpty(topLevel);
        // Документ id=1 («DocsWalker») — top-level, его path указывает на 0.
        Assert.Contains(1, topLevel);
    }

    [Fact]
    public void GetAncestors_FromAtom_IncludesRootAtEnd()
    {
        var api = BuildApi();
        // id=8 — definition «узел» в section «Модель данных» в document «DocsWalker».
        var ancestors = api.GetAncestors(8);
        Assert.NotEmpty(ancestors);
        var last = ancestors[^1];
        Assert.Equal(0, last.Id);
        Assert.Equal("root", last.TypeName);
    }

    [Fact]
    public void GetSubtree_FromRoot_IncludesAllTopLevelEntries()
    {
        var api = BuildApi();
        var subtree = api.GetSubtree(0);
        Assert.Equal(0, subtree.Node.Id);
        Assert.Equal("root", subtree.Node.TypeName);
        Assert.NotEmpty(subtree.Children);
        // На реальном docs/ — 3 top-level документа (DocsWalker, Стек, Правила оформления).
        Assert.Equal(3, subtree.Children.Count);
    }

    [Fact]
    public void UpdateNode_RootId_ThrowsCannotModifyRoot()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));
        var op = new UpdateNodeOp(Id: 0, NewTitle: "новый", NewText: null);
        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("cannot_modify_root", ex.Code);
    }

    [Fact]
    public void DeleteNodes_IncludingRoot_ThrowsCannotModifyRoot()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));
        var op = new DeleteNodesOp(new[] { 0 });
        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("cannot_modify_root", ex.Code);
    }

    [Fact]
    public void MoveNode_RootId_ThrowsCannotModifyRoot()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));
        var op = new MoveNodeOp(Id: 0, NewParentId: 1);
        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("cannot_modify_root", ex.Code);
    }

    [Fact]
    public void CreateRef_FromRootId_ThrowsCannotModifyRoot()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));
        var op = new CreateRefOp(FromId: 0, Name: "anything", ToId: 1);
        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("cannot_modify_root", ex.Code);
    }

    [Fact]
    public void DeleteRef_FromRootId_ThrowsCannotModifyRoot()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));
        var op = new DeleteRefOp(FromId: 0, Name: "anything", ToId: 1);
        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("cannot_modify_root", ex.Code);
    }
}
