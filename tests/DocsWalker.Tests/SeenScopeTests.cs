using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Sessions;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение seen-фильтрации (#344, #346) в сериализаторе read-ответов.
/// Использует <see cref="SeenScope.Create"/>, обходит ambient RequestContext —
/// тестируем именно код сериализации без серверной обвязки.
/// </summary>
public class SeenScopeTests
{
    private static (ReadApi Api, Graph) Build()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var loaded = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return (new ReadApi(loaded.Graph, schema), loaded.Graph);
    }

    [Fact]
    public void SubtreeToJson_TransitiveChildAlreadySeen_ReturnsPlaceholder()
    {
        var (api, _) = Build();
        var subtree = api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: 1);
        var firstChildId = subtree.Children[0].Node.Id;

        var sessions = new SessionState();
        var sid = Guid.NewGuid();
        // Делаем вид, что первый ребёнок уже выдан в этой сессии.
        sessions.MarkSeen(sid, new[] { firstChildId }, DateTime.UtcNow);

        var scope = SeenScope.Create(sessions, sid);
        var json = ReadApiJson.SubtreeToJson(subtree, fields: null, scope);

        // Корень subtree — прямой запрос → полный.
        Assert.Equal(2, (int)json["id"]!);
        Assert.NotNull(json["title"]);

        var children = (JsonArray)json["children"]!;
        var firstChild = (JsonObject)children.First(c => (int)c!["id"]! == firstChildId)!;

        // Транзитивный ребёнок в seen → placeholder без других полей.
        Assert.Equal(2, firstChild.Count);
        Assert.Equal(firstChildId, (int)firstChild["id"]!);
        Assert.True((bool)firstChild["seen"]!);
        Assert.Null(firstChild["title"]);
        Assert.Null(firstChild["text"]);
    }

    [Fact]
    public void SubtreeToJson_DirectRoot_NeverFiltered_EvenIfInSeenSet()
    {
        var (api, _) = Build();

        var sessions = new SessionState();
        var sid = Guid.NewGuid();
        // Корень subtree уже в seen — но spec гарантирует, что прямой id всегда полный (#346).
        sessions.MarkSeen(sid, new[] { 2 }, DateTime.UtcNow);

        var subtree = api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: 0);
        var scope = SeenScope.Create(sessions, sid);
        var json = ReadApiJson.SubtreeToJson(subtree, fields: null, scope);

        Assert.Equal(2, (int)json["id"]!);
        Assert.NotNull(json["title"]);
        Assert.Null(json["seen"]);
    }

    [Fact]
    public void NodesToJson_AllDirect_NeverFiltered_AndSeenAccumulates()
    {
        var (api, _) = Build();
        var sessions = new SessionState();
        var sid = Guid.NewGuid();

        var nodes = api.GetNodes(new[] { 1, 8 });
        var scope = SeenScope.Create(sessions, sid);
        var json = ReadApiJson.NodesToJson(nodes, scope);
        scope.Commit(DateTime.UtcNow);

        // Оба узла полные — get-nodes по прямым id не фильтрует.
        Assert.All(json, item =>
        {
            var obj = (JsonObject)item!;
            Assert.Null(obj["seen"]);
            Assert.NotNull(obj["title"]);
        });

        // После Commit оба id оказались в seen-set сессии.
        Assert.Contains(1, sessions.Sessions[sid].Ids);
        Assert.Contains(8, sessions.Sessions[sid].Ids);
    }

    [Fact]
    public void SubtreeToJson_Commit_AccumulatesAllVisitedIdsIntoSession()
    {
        var (api, _) = Build();
        var sessions = new SessionState();
        var sid = Guid.NewGuid();

        var subtree = api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: 1);
        var scope = SeenScope.Create(sessions, sid);
        ReadApiJson.SubtreeToJson(subtree, fields: null, scope);
        scope.Commit(DateTime.UtcNow);

        Assert.Contains(2, sessions.Sessions[sid].Ids);
        foreach (var c in subtree.Children)
            Assert.Contains(c.Node.Id, sessions.Sessions[sid].Ids);
    }

    [Fact]
    public void SubtreeToJson_NullScope_BackwardCompatibleOutput()
    {
        var (api, _) = Build();
        var subtree = api.GetSubtree(rootId: 2, tree: Node.PathRefName, depth: 1);

        var jsonNoScope = ReadApiJson.SubtreeToJson(subtree, fields: null, scope: null);
        var jsonOldOverload = ReadApiJson.SubtreeToJson(subtree);

        Assert.Equal(jsonOldOverload.ToJsonString(), jsonNoScope.ToJsonString());
    }
}
