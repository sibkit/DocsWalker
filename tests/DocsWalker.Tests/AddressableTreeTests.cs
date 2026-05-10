using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

/// <summary>
/// Тесты на addressable-trees-семантику (stg-0010 step-02):
/// поле <see cref="RefDef.UniqueSiblingTitles"/>, опциональный
/// <see cref="SchemaDocument.DefaultAddressableTree"/>, lax-резолвинг
/// дефолтного дерева в <see cref="ReadApi.GetByPath"/>, errors
/// <c>tree_not_addressable</c> / <c>tree_required</c> / <c>unknown_tree_scope</c>.
///
/// Граф и Схема собираются в памяти, чтобы покрыть scenarios c
/// несколькими addressable trees, отсутствующими в реальном
/// <c>docs/</c> DocsWalker (где addressable только <c>path</c>).
/// </summary>
public class AddressableTreeTests
{
    /// <summary>
    /// Конструирует Схему с одним типом <c>thing</c>, у которого до трёх tree-связей:
    ///   - path (tree=path, addressable=<paramref name="addressablePath"/>);
    ///   - alpha (tree=alpha, addressable=<paramref name="includeAlpha"/> ∧
    ///     <paramref name="addressableAlpha"/>);
    ///   - beta (tree=beta, addressable=<paramref name="includeBeta"/> ∧
    ///     <paramref name="addressableBeta"/>).
    /// </summary>
    private static SchemaDocument BuildSchema(
        bool addressablePath = true,
        bool includeAlpha = false, bool addressableAlpha = false,
        bool includeBeta = false, bool addressableBeta = false,
        string? defaultAddressableTree = null)
    {
        var trees = new List<TreeDefinition>
        {
            new("path", null),
        };
        if (includeAlpha) trees.Add(new("alpha", null));
        if (includeBeta) trees.Add(new("beta", null));

        var rootType = new TypeDefinition(
            "root", null, TitleSource.Dirname, false, Array.Empty<RefDef>());

        var refs = new List<RefDef>
        {
            new(
                Name: "path",
                TargetTypes: new[] { "root", "thing" },
                Tree: "path",
                Cardinality: Cardinality.One,
                Required: true,
                Description: null,
                UniqueSiblingTitles: addressablePath),
        };
        if (includeAlpha)
            refs.Add(new(
                Name: "alpha",
                TargetTypes: new[] { "root", "thing" },
                Tree: "alpha",
                Cardinality: Cardinality.One,
                Required: true,
                Description: null,
                UniqueSiblingTitles: addressableAlpha));
        if (includeBeta)
            refs.Add(new(
                Name: "beta",
                TargetTypes: new[] { "root", "thing" },
                Tree: "beta",
                Cardinality: Cardinality.One,
                Required: true,
                Description: null,
                UniqueSiblingTitles: addressableBeta));

        var thing = new TypeDefinition(
            "thing", null, TitleSource.InlineKey, true, refs);

        return new SchemaDocument(
            "test schema",
            trees,
            new[] { rootType, thing },
            defaultAddressableTree);
    }

    private static (GraphModel Graph, ReadApi Api) BuildEmptyApi(SchemaDocument schema)
    {
        var graph = new GraphModel();
        graph.AttachSchema(schema);
        return (graph, new ReadApi(graph, schema));
    }

    private static Node MakeThing(int id, string title, int pathParent, int? alphaParent = null, int? betaParent = null)
    {
        var refs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["path"] = new[] { pathParent },
        };
        if (alphaParent is int a) refs["alpha"] = new[] { a };
        if (betaParent is int b) refs["beta"] = new[] { b };
        return new Node
        {
            Id = id,
            TypeName = "thing",
            Title = title,
            Text = "x",
            OutRefs = refs,
            SourceFile = $"thing-{id}.yml",
        };
    }

    [Fact]
    public void GetByPath_NullTree_SingleAddressable_AutoDefault_Resolves()
    {
        // Только path addressable; --tree= не задан → берётся path.
        var schema = BuildSchema(addressablePath: true);
        var (g, api) = BuildEmptyApi(schema);
        // Под root (id=0) кладём один thing с title="alpha", чтобы GetByPath нашёл его.
        // path-tree top-level в текущей реализации находится через GetDocumentByTitle (тип document),
        // которого у нас нет. Поэтому проверяем только успешный резолвинг tree-имени:
        // ошибка tree_required не должна выскочить.
        var ex = Record.Exception(() => api.GetByPath("nonexistent"));
        Assert.IsType<ReadApiException>(ex);
        Assert.Equal("path_not_found", ((ReadApiException)ex!).Code);
    }

    [Fact]
    public void GetByPath_NullTree_NoAddressable_TreeRequired()
    {
        // Path не addressable, других нет.
        var schema = BuildSchema(addressablePath: false);
        var (_, api) = BuildEmptyApi(schema);
        var ex = Assert.Throws<ReadApiException>(() => api.GetByPath("anything"));
        Assert.Equal("tree_required", ex.Code);
    }

    [Fact]
    public void GetByPath_NullTree_MultipleAddressable_TreeRequired()
    {
        // path и alpha — оба addressable; --tree= не задан, default не задан.
        var schema = BuildSchema(
            addressablePath: true,
            includeAlpha: true, addressableAlpha: true);
        var (_, api) = BuildEmptyApi(schema);
        var ex = Assert.Throws<ReadApiException>(() => api.GetByPath("anything"));
        Assert.Equal("tree_required", ex.Code);
    }

    [Fact]
    public void GetByPath_NullTree_DefaultAddressableTreeHonored()
    {
        // path и alpha — оба addressable; default_addressable_tree=alpha → tree_required не возникает.
        var schema = BuildSchema(
            addressablePath: true,
            includeAlpha: true, addressableAlpha: true,
            defaultAddressableTree: "alpha");
        var (g, api) = BuildEmptyApi(schema);
        // Узел с alpha-родителем=root и title="X".
        g.Add(MakeThing(id: 100, title: "X", pathParent: Node.RootId, alphaParent: Node.RootId));

        var subtree = api.GetByPath("X");
        Assert.Equal(100, subtree.Node.Id);
    }

    [Fact]
    public void GetByPath_NonAddressableTree_TreeNotAddressable()
    {
        // path addressable, beta non-addressable.
        var schema = BuildSchema(
            addressablePath: true,
            includeBeta: true, addressableBeta: false);
        var (_, api) = BuildEmptyApi(schema);
        var ex = Assert.Throws<ReadApiException>(() => api.GetByPath("anything", tree: "beta"));
        Assert.Equal("tree_not_addressable", ex.Code);
    }

    [Fact]
    public void GetByPath_UnknownTreeName_UnknownTreeScope()
    {
        var schema = BuildSchema(addressablePath: true);
        var (_, api) = BuildEmptyApi(schema);
        var ex = Assert.Throws<ReadApiException>(() => api.GetByPath("anything", tree: "nonexistent"));
        Assert.Equal("unknown_tree_scope", ex.Code);
    }

    [Fact]
    public void GetByPath_NestedAlphaTree_Resolves()
    {
        // Дерево alpha addressable; путь "X/Y": X — top-level (alpha-родитель=root), Y — child X в alpha.
        var schema = BuildSchema(
            addressablePath: false,
            includeAlpha: true, addressableAlpha: true);
        var (g, api) = BuildEmptyApi(schema);
        g.Add(MakeThing(id: 100, title: "X", pathParent: Node.RootId, alphaParent: Node.RootId));
        g.Add(MakeThing(id: 101, title: "Y", pathParent: Node.RootId, alphaParent: 100));

        var subtree = api.GetByPath("X/Y", tree: "alpha");
        Assert.Equal(101, subtree.Node.Id);
    }

    [Fact]
    public void GetByPath_PathTreeStillWorksAsBefore_OnRealSchema()
    {
        // Регрессия: реальный docs/ DocsWalker, default-резолвинг → path (единственный addressable).
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var docs = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        var api = new ReadApi(docs.Graph, schema);
        var subtree = api.GetByPath("DocsWalker");
        Assert.Equal("document", subtree.Node.TypeName);
    }
}
