using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class LlmJsonApiPathResolverTests
{
    [Fact]
    public void ResolveSinglePath_ExactFullPath_ReturnsNode()
    {
        var resolver = BuildResolver();

        var node = resolver.ResolveSinglePath(
            "DocsWalker/Read API/Wildcard path",
            LlmRequestDefaults.Empty,
            "$.ops[0].path");

        Assert.Equal(3, node.Id);
        Assert.Equal("DocsWalker/Read API/Wildcard path", node.Path);
        Assert.Equal("Wildcard path", node.Node.Title);
    }

    [Fact]
    public void ResolveSinglePath_WithDefaults_CombinesRelativePath()
    {
        var resolver = BuildResolver();
        var defaults = new LlmRequestDefaults("DocsWalker/Read API", LlmCoordinates.Empty);

        var node = resolver.ResolveSinglePath("Exact path", defaults, "$.ops[0].path");

        Assert.Equal(4, node.Id);
        Assert.Equal("DocsWalker/Read API/Exact path", node.Path);
    }

    [Fact]
    public void ResolvePath_WithDefaultsAndFullPath_ThrowsAmbiguousPathScope()
    {
        var resolver = BuildResolver();
        var defaults = new LlmRequestDefaults("DocsWalker/Read API", LlmCoordinates.Empty);

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            resolver.ResolvePath("DocsWalker/Read API/Wildcard path", defaults, "$.ops[0].path"));

        Assert.Equal("ambiguous_path_scope", ex.Code);
        Assert.Equal("$.ops[0].path", ex.Path);
    }

    [Fact]
    public void ResolvePath_WildcardStar_MatchesOneSegmentOnly()
    {
        var resolver = BuildResolver();

        var nodes = resolver.ResolvePath(
            "DocsWalker/Read API/*",
            LlmRequestDefaults.Empty,
            "$.ops[0].select.path");

        Assert.Equal(new[] { 4, 3 }, nodes.Select(n => n.Id).OrderDescending());
        Assert.DoesNotContain(nodes, n => n.Id == 7);
    }

    [Fact]
    public void ResolvePath_WildcardDoubleStar_MatchesAnyDepth()
    {
        var resolver = BuildResolver();

        var nodes = resolver.ResolvePath(
            "DocsWalker/Read API/**",
            LlmRequestDefaults.Empty,
            "$.ops[0].select.path");

        Assert.Contains(nodes, n => n.Id == 2);
        Assert.Contains(nodes, n => n.Id == 3);
        Assert.Contains(nodes, n => n.Id == 4);
        Assert.Contains(nodes, n => n.Id == 7);
    }

    [Fact]
    public void ResolveSinglePath_WildcardMultiple_ThrowsAmbiguousSelector()
    {
        var resolver = BuildResolver();

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            resolver.ResolveSinglePath("DocsWalker/Read API/*", LlmRequestDefaults.Empty, "$.ops[0].path"));

        Assert.Equal("ambiguous_selector", ex.Code);
        Assert.Equal("$.ops[0].path", ex.Path);
    }

    [Fact]
    public void ResolvePath_NotFound_ThrowsNotFound()
    {
        var resolver = BuildResolver();

        var ex = Assert.Throws<LlmJsonApiResolveException>(() =>
            resolver.ResolvePath("DocsWalker/Missing", LlmRequestDefaults.Empty, "$.ops[0].path"));

        Assert.Equal("not_found", ex.Code);
        Assert.Equal("$.ops[0].path", ex.Path);
    }

    [Fact]
    public void ResolveExistingId_ReturnsFormattedPath()
    {
        var resolver = BuildResolver();

        var node = resolver.ResolveExistingId(7, "$.ops[0].id");

        Assert.Equal("DocsWalker/Read API/Wildcard path/Nested", node.Path);
    }

    private static LlmJsonApiPathResolver BuildResolver()
    {
        var graph = new GraphModel();
        graph.Add(MakeNode(1, "document", "DocsWalker", Node.RootId));
        graph.Add(MakeNode(2, "section", "Read API", 1));
        graph.Add(MakeNode(3, "rule", "Wildcard path", 2));
        graph.Add(MakeNode(4, "rule", "Exact path", 2));
        graph.Add(MakeNode(5, "document", "Stack", Node.RootId));
        graph.Add(MakeNode(6, "section", "Read API", 5));
        graph.Add(MakeNode(7, "example", "Nested", 3));
        return new LlmJsonApiPathResolver(graph);
    }

    private static Node MakeNode(int id, string typeName, string title, int parentId) =>
        new()
        {
            Id = id,
            TypeName = typeName,
            Title = title,
            Text = "",
            OutRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                [Node.PathRefName] = new[] { parentId },
            },
            SourceFile = $"node-{id}.yml",
        };
}
